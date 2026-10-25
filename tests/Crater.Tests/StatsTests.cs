using System.Collections.Concurrent;
using System.IO.Compression;
using System.Net;
using System.Net.Sockets;
using System.Text;
using Crater.Registry;

namespace Crater.Tests;

[Collection("crater process")]
public sealed class StatsTests : IDisposable
{
	readonly string cache = Path.Combine(Path.GetTempPath(), $"crater-stats-test-{Guid.NewGuid():N}");
	readonly HttpListener listener = new();
	readonly ConcurrentQueue<string> reports = new();
	readonly string baseUrl;

	public StatsTests()
	{
		Environment.SetEnvironmentVariable("CRATER_CACHE", cache);

		TcpListener probe = new(IPAddress.Loopback, 0);
		probe.Start();
		int port = ((IPEndPoint)probe.LocalEndpoint).Port;
		probe.Stop();

		baseUrl = $"http://localhost:{port}";
		listener.Prefixes.Add(baseUrl + "/");
		listener.Start();
		_ = ServeAsync();
	}

	public void Dispose()
	{
		listener.Close();
		Environment.SetEnvironmentVariable("CRATER_NO_STATS", null);

		if (Directory.Exists(cache))
		{
			Directory.Delete(cache, recursive: true);
		}
	}

	async Task ServeAsync()
	{
		while (listener.IsListening)
		{
			HttpListenerContext context;

			try
			{
				context = await listener.GetContextAsync();
			}
			catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
			{
				return;
			}

			string path = context.Request.Url!.AbsolutePath;
			byte[] body = [];

			if (context.Request.HttpMethod == "POST")
			{
				reports.Enqueue(path);
				context.Response.StatusCode = 204;
			}
			else if (path == "/registry.json")
			{
				body = Encoding.UTF8.GetBytes($"{{\"name\":\"Test\",\"stats_url\":\"{baseUrl}/stats\"}}");
			}
			else if (path.EndsWith(".zip"))
			{
				body = Archive();
			}
			else
			{
				context.Response.StatusCode = 404;
			}

			await context.Response.OutputStream.WriteAsync(body);
			context.Response.Close();
		}
	}

	static byte[] Archive()
	{
		using MemoryStream stream = new();

		using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			using StreamWriter writer = new(zip.CreateEntry("init.luau").Open());
			writer.Write("return {}");
		}

		return stream.ToArray();
	}

	[Fact]
	public async Task DownloadsAreReportedUnlessTurnedOff()
	{
		await new HttpRegistry(baseUrl).DownloadAsync("t/lib", "1.0.0");

		Assert.Contains("/stats/download/t/lib/1.0.0", reports);

		Environment.SetEnvironmentVariable("CRATER_NO_STATS", "1");
		await new HttpRegistry(baseUrl).DownloadAsync("t/lib", "1.1.0");

		Assert.DoesNotContain("/stats/download/t/lib/1.1.0", reports);
	}
}
