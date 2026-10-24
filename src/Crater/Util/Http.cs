using System.Net;
using System.Net.Http.Headers;
using System.Net.Sockets;

namespace Crater.Util;

public static class Http
{
	const int Attempts = 3;

	static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(8);
	static readonly TimeSpan StallTimeout = TimeSpan.FromSeconds(20);
	static readonly HttpClient Client = Create();

	static HttpClient Create()
	{
		SocketsHttpHandler handler = new()
		{
			ConnectTimeout = ConnectTimeout,
			ConnectCallback = ConnectAsync,
			AutomaticDecompression = DecompressionMethods.All,
			PooledConnectionLifetime = TimeSpan.FromMinutes(5),
		};

		HttpClient client = new(handler) { Timeout = Timeout.InfiniteTimeSpan };
		client.DefaultRequestHeaders.UserAgent.ParseAdd($"crater/{BuildInfo.Version}");

		string? token = Environment.GetEnvironmentVariable("CRATER_REGISTRY_TOKEN");

		if (!string.IsNullOrWhiteSpace(token))
		{
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}

		return client;
	}

	public static async Task<string?> GetTextOrNullAsync(string url)
	{
		byte[]? data = await GetBytesOrNullAsync(url);

		return data is null ? null : System.Text.Encoding.UTF8.GetString(data);
	}

	public static async Task<byte[]> GetBytesAsync(string url)
	{
		return await GetBytesOrNullAsync(url) ?? throw new CraterException($"{Fmt.File(url)}: HTTP 404 Not Found");
	}

	public static async Task<byte[]?> GetBytesOrNullAsync(string url, Action<long, long?>? progress = null, IReadOnlyDictionary<string, string>? headers = null)
	{
		for (int attempt = 1; ; attempt++)
		{
			try
			{
				return await TryGetAsync(url, progress, headers);
			}
			catch (Exception exception) when (IsTransient(exception) && attempt < Attempts)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt));
			}
			catch (Exception exception) when (IsTransient(exception))
			{
				throw new CraterException($"Could not download {Fmt.File(url)}: {Describe(exception)}. Check your internet connection and try again.");
			}
		}
	}

	static async Task<byte[]?> TryGetAsync(string url, Action<long, long?>? progress, IReadOnlyDictionary<string, string>? headers)
	{
		using CancellationTokenSource stall = new(StallTimeout);
		using HttpRequestMessage request = new(HttpMethod.Get, url);

		foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
		{
			request.Headers.TryAddWithoutValidation(name, value);
		}

		using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);

		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			return null;
		}

		if ((int)response.StatusCode is 429 or 500 or 502 or 503 or 504)
		{
			throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
		}

		if (!response.IsSuccessStatusCode)
		{
			throw new CraterException($"{Fmt.File(url)}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
		}

		long? total = response.Content.Headers.ContentLength;
		await using Stream stream = await response.Content.ReadAsStreamAsync(stall.Token);
		using MemoryStream buffer = total is > 0 and < int.MaxValue ? new MemoryStream((int)total) : new MemoryStream();
		byte[] chunk = new byte[81920];

		while (true)
		{
			stall.CancelAfter(StallTimeout);

			int read = await stream.ReadAsync(chunk, stall.Token);

			if (read == 0)
			{
				break;
			}

			buffer.Write(chunk, 0, read);
			progress?.Invoke(buffer.Length, total);
		}

		return buffer.ToArray();
	}

	static bool IsTransient(Exception exception)
	{
		return exception is HttpRequestException or TaskCanceledException or OperationCanceledException or IOException or SocketException;
	}

	static string Describe(Exception exception)
	{
		return exception switch
		{
			TaskCanceledException or OperationCanceledException => $"no data for {StallTimeout.TotalSeconds:0}s",
			HttpRequestException { StatusCode: not null } http => $"HTTP {(int)http.StatusCode!}",
			_ => exception.InnerException?.Message ?? exception.Message,
		};
	}

	static async ValueTask<Stream> ConnectAsync(SocketsHttpConnectionContext context, CancellationToken token)
	{
		IPAddress[] addresses = await Dns.GetHostAddressesAsync(context.DnsEndPoint.Host, token);
		IEnumerable<IPAddress> ordered = addresses
			.OrderBy(address => address.AddressFamily == AddressFamily.InterNetwork ? 0 : 1);

		Exception? last = null;

		foreach (IPAddress address in ordered)
		{
			Socket socket = new(address.AddressFamily, SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };

			try
			{
				using CancellationTokenSource attempt = CancellationTokenSource.CreateLinkedTokenSource(token);
				attempt.CancelAfter(TimeSpan.FromSeconds(4));

				await socket.ConnectAsync(new IPEndPoint(address, context.DnsEndPoint.Port), attempt.Token);

				return new NetworkStream(socket, ownsSocket: true);
			}
			catch (Exception exception) when (exception is SocketException or OperationCanceledException)
			{
				socket.Dispose();
				last = exception;
				token.ThrowIfCancellationRequested();
			}
		}

		throw new HttpRequestException($"Could not connect to {context.DnsEndPoint.Host}", last);
	}
}
