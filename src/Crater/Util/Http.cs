using System.Net;
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

		return client;
	}

	public static async Task<string?> GetTextOrNullAsync(string url, IReadOnlyDictionary<string, string>? headers = null)
	{
		byte[]? data = await GetBytesOrNullAsync(url, null, headers);

		return data is null ? null : System.Text.Encoding.UTF8.GetString(data);
	}

	public static bool Offline { get; set; } = Environment.GetEnvironmentVariable("CRATER_OFFLINE") is { Length: > 0 };

	sealed record Response(byte[]? Data, string? ETag, bool NotModified);

	public static async Task<byte[]?> GetBytesOrNullAsync(string url, Action<long, long?>? progress = null, IReadOnlyDictionary<string, string>? headers = null)
	{
		return (await SendWithRetriesAsync(url, progress, headers, null)).Data;
	}

	public static async Task<byte[]?> GetCachedAsync(string url, IReadOnlyDictionary<string, string>? headers = null)
	{
		string directory = Path.Combine(CraterPaths.Cache, "http");
		string key = Convert.ToHexStringLower(System.Security.Cryptography.SHA256.HashData(System.Text.Encoding.UTF8.GetBytes(url)));
		string bodyPath = Path.Combine(directory, key + ".body");
		string tagPath = Path.Combine(directory, key + ".etag");
		string missingPath = Path.Combine(directory, key + ".missing");
		bool hasBody = File.Exists(bodyPath);

		if (Offline)
		{
			if (hasBody)
			{
				return await File.ReadAllBytesAsync(bodyPath);
			}

			if (File.Exists(missingPath))
			{
				return null;
			}

			throw new CraterException($"{Fmt.File(url)} is not in the cache yet. Run the command once with internet, then {Fmt.Cmd("--offline")} works.");
		}

		string? etag = hasBody && File.Exists(tagPath) ? await File.ReadAllTextAsync(tagPath) : null;
		Response response = await SendWithRetriesAsync(url, null, headers, etag);

		Directory.CreateDirectory(directory);

		if (response.NotModified)
		{
			return await File.ReadAllBytesAsync(bodyPath);
		}

		if (response.Data is null)
		{
			File.Delete(bodyPath);
			File.Delete(tagPath);
			await File.WriteAllTextAsync(missingPath, "");
			return null;
		}

		await WriteAtomicallyAsync(bodyPath, response.Data);
		File.Delete(missingPath);

		if (response.ETag is not null)
		{
			await WriteAtomicallyAsync(tagPath, System.Text.Encoding.UTF8.GetBytes(response.ETag));
		}
		else
		{
			File.Delete(tagPath);
		}

		return response.Data;
	}

	static async Task WriteAtomicallyAsync(string path, byte[] data)
	{
		string temporary = $"{path}.{Guid.NewGuid():N}.tmp";

		await File.WriteAllBytesAsync(temporary, data);
		File.Move(temporary, path, overwrite: true);
	}

	static async Task<Response> SendWithRetriesAsync(string url, Action<long, long?>? progress, IReadOnlyDictionary<string, string>? headers, string? etag)
	{
		if (Offline)
		{
			throw new CraterException($"{Fmt.File(url)} needs the internet, but crater runs with {Fmt.Cmd("--offline")}.");
		}

		for (int attempt = 1; ; attempt++)
		{
			try
			{
				return await TryGetAsync(url, progress, headers, etag);
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

	static async Task<Response> TryGetAsync(string url, Action<long, long?>? progress, IReadOnlyDictionary<string, string>? headers, string? etag)
	{
		using CancellationTokenSource stall = new(StallTimeout);
		using HttpRequestMessage request = new(HttpMethod.Get, url);

		foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
		{
			request.Headers.TryAddWithoutValidation(name, value);
		}

		if (etag is not null)
		{
			request.Headers.TryAddWithoutValidation("If-None-Match", etag);
		}

		using HttpResponseMessage response = await Client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, stall.Token);

		if (response.StatusCode == HttpStatusCode.NotModified)
		{
			return new Response(null, etag, true);
		}

		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			return new Response(null, null, false);
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

		return new Response(buffer.ToArray(), response.Headers.ETag?.ToString(), false);
	}

	public sealed record Reply(int Status, byte[] Body);

	public static async Task PingAsync(string url)
	{
		if (Offline)
		{
			return;
		}

		try
		{
			using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(2));
			using HttpRequestMessage request = new(HttpMethod.Post, url);
			using HttpResponseMessage response = await Client.SendAsync(request, timeout.Token);
		}
		catch (Exception exception) when (exception is HttpRequestException or OperationCanceledException)
		{
		}
	}

	public static async Task<Reply> SendAsync(HttpMethod method, string url, HttpContent? content, IReadOnlyDictionary<string, string>? headers = null)
	{
		if (Offline)
		{
			throw new CraterException($"{Fmt.File(url)} needs the internet, but crater runs with {Fmt.Cmd("--offline")}.");
		}

		byte[]? payload = content is null ? null : await content.ReadAsByteArrayAsync();
		string? contentType = content?.Headers.ContentType?.ToString();

		for (int attempt = 1; ; attempt++)
		{
			try
			{
				using CancellationTokenSource timeout = new(TimeSpan.FromSeconds(60));
				using HttpRequestMessage request = new(method, url);

				foreach ((string name, string value) in headers ?? new Dictionary<string, string>())
				{
					request.Headers.TryAddWithoutValidation(name, value);
				}

				if (payload is not null)
				{
					request.Content = new ByteArrayContent(payload);
					request.Content.Headers.TryAddWithoutValidation("Content-Type", contentType ?? "application/json");
				}

				using HttpResponseMessage response = await Client.SendAsync(request, timeout.Token);

				if ((int)response.StatusCode is 502 or 503 or 504 && attempt < Attempts)
				{
					throw new HttpRequestException($"HTTP {(int)response.StatusCode}", null, response.StatusCode);
				}

				return new Reply((int)response.StatusCode, await response.Content.ReadAsByteArrayAsync(timeout.Token));
			}
			catch (Exception exception) when (IsTransient(exception) && attempt < Attempts)
			{
				await Task.Delay(TimeSpan.FromMilliseconds(600 * attempt));
			}
			catch (Exception exception) when (IsTransient(exception))
			{
				throw new CraterException($"Could not reach {Fmt.File(url)}: {Describe(exception)}. Check your internet connection and try again.");
			}
		}
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
