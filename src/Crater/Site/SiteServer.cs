using System.Net;
using Crater.Util;

namespace Crater.Site;

public static class SiteServer
{
	static readonly Dictionary<string, string> ContentTypes = new(StringComparer.OrdinalIgnoreCase)
	{
		[".html"] = "text/html; charset=utf-8",
		[".js"] = "text/javascript; charset=utf-8",
		[".css"] = "text/css; charset=utf-8",
		[".json"] = "application/json; charset=utf-8",
		[".svg"] = "image/svg+xml",
		[".png"] = "image/png",
		[".ico"] = "image/x-icon",
	};

	public static async Task ServeAsync(string directory, int port)
	{
		string root = Path.GetFullPath(directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string prefix = $"http://localhost:{port}/";

		using HttpListener listener = new();
		listener.Prefixes.Add(prefix);

		try
		{
			listener.Start();
		}
		catch (HttpListenerException exception)
		{
			throw new CraterException($"Could not listen on {Fmt.File(prefix)}: {exception.Message}");
		}

		Out.Ok($"Serving {Fmt.File(directory)} at {Fmt.File(prefix)} (Ctrl+C to stop)");

		using CancellationTokenSource stop = new();

		Console.CancelKeyPress += (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			stop.Cancel();
			listener.Stop();
		};

		while (!stop.IsCancellationRequested)
		{
			HttpListenerContext context;

			try
			{
				context = await listener.GetContextAsync();
			}
			catch (Exception exception) when (exception is HttpListenerException or ObjectDisposedException)
			{
				break;
			}

			await RespondAsync(context, root);
		}
	}

	static async Task RespondAsync(HttpListenerContext context, string root)
	{
		HttpListenerResponse response = context.Response;

		try
		{
			string relative = Uri.UnescapeDataString(context.Request.Url?.AbsolutePath ?? "/").TrimStart('/');

			if (relative.Length == 0 || relative.EndsWith('/'))
			{
				relative += "index.html";
			}

			string path = Path.GetFullPath(Path.Combine(root, relative.Replace('/', Path.DirectorySeparatorChar)));
			bool allowed = path.StartsWith(root, StringComparison.OrdinalIgnoreCase) && File.Exists(path);

			if (!allowed)
			{
				response.StatusCode = 404;
				return;
			}

			byte[] body = await File.ReadAllBytesAsync(path);

			response.ContentType = ContentTypes.GetValueOrDefault(Path.GetExtension(path), "application/octet-stream");
			response.Headers["Cache-Control"] = "no-cache";
			response.ContentLength64 = body.Length;

			await response.OutputStream.WriteAsync(body);
		}
		finally
		{
			response.Close();
		}
	}
}
