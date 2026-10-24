using System.Diagnostics;
using System.Text.Json;
using Crater.Util;

namespace Crater.Commands;

public static class DeviceLogin
{
	const string CodeUrl = "https://github.com/login/device/code";
	const string TokenUrl = "https://github.com/login/oauth/access_token";
	const string PublicScopes = "public_repo read:org";
	const string PrivateScopes = "repo read:org";

	static readonly Dictionary<string, string> AcceptJson = new() { ["Accept"] = "application/json" };

	public static async Task<Credentials> SignInAsync(string clientId, bool includePrivate, bool openBrowser)
	{
		JsonElement code = await PostAsync(CodeUrl, new()
		{
			["client_id"] = clientId,
			["scope"] = includePrivate ? PrivateScopes : PublicScopes,
		});

		if (code.TryGetProperty("error", out JsonElement codeError))
		{
			throw new CraterException(
				$"GitHub refused to start the sign-in ({codeError.GetString()}). This crater build has a wrong GitHub app id; update crater with {Fmt.Cmd("crater self-update")}.");
		}

		string deviceCode = code.GetProperty("device_code").GetString()!;
		string userCode = code.GetProperty("user_code").GetString()!;
		string verificationUrl = code.GetProperty("verification_uri").GetString()!;
		int interval = code.TryGetProperty("interval", out JsonElement seconds) ? seconds.GetInt32() : 5;
		DateTimeOffset expires = DateTimeOffset.UtcNow.AddSeconds(code.TryGetProperty("expires_in", out JsonElement lifetime) ? lifetime.GetInt32() : 900);

		Out.Step("Signing in to GitHub");
		Out.Info("");
		Out.Info($"  1. Open    {Fmt.File(verificationUrl)}");
		Out.Info($"  2. Enter   {Fmt.Val(userCode)}");
		Out.Info("  3. Confirm access for crater");
		Out.Info("");
		Out.Dim("  The link works in any browser, also on your phone or another computer.");
		Out.Info("");

		if (openBrowser)
		{
			OpenBrowser(verificationUrl);
		}

		using (new Spinner("Waiting for you to confirm on GitHub"))
		{
			while (DateTimeOffset.UtcNow < expires)
			{
				await Task.Delay(TimeSpan.FromSeconds(interval));

				JsonElement reply = await PostAsync(TokenUrl, new()
				{
					["client_id"] = clientId,
					["device_code"] = deviceCode,
					["grant_type"] = "urn:ietf:params:oauth:grant-type:device_code",
				});

				if (reply.TryGetProperty("access_token", out JsonElement token))
				{
					string scopes = reply.TryGetProperty("scope", out JsonElement granted) ? granted.GetString() ?? "" : "";

					return new Credentials { Token = token.GetString()!, Scopes = scopes };
				}

				string error = reply.TryGetProperty("error", out JsonElement value) ? value.GetString() ?? "" : "";

				switch (error)
				{
					case "authorization_pending":
						continue;

					case "slow_down":
						interval = reply.TryGetProperty("interval", out JsonElement slower) ? slower.GetInt32() : interval + 5;
						continue;

					case "access_denied":
						throw new CraterException("The sign-in was cancelled on GitHub.");

					case "expired_token":
						throw new CraterException($"The code expired. Run {Fmt.Cmd("crater login")} again.");

					default:
						throw new CraterException($"GitHub sign-in failed: {error}. {Describe(reply)}");
				}
			}
		}

		throw new CraterException($"The code expired. Run {Fmt.Cmd("crater login")} again.");
	}

	static async Task<JsonElement> PostAsync(string url, Dictionary<string, string> form)
	{
		Http.Reply reply = await Http.SendAsync(HttpMethod.Post, url, new FormUrlEncodedContent(form), AcceptJson);

		try
		{
			using JsonDocument document = JsonDocument.Parse(reply.Body);

			return document.RootElement.Clone();
		}
		catch (JsonException)
		{
			throw new CraterException($"GitHub answered {reply.Status} to the sign-in request.");
		}
	}

	static string Describe(JsonElement reply)
	{
		return reply.TryGetProperty("error_description", out JsonElement description) ? description.GetString() ?? "" : "";
	}

	public static bool CanOpenBrowser()
	{
		return OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			|| !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("DISPLAY"))
			|| !string.IsNullOrEmpty(Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));
	}

	static void OpenBrowser(string url)
	{
		try
		{
			ProcessStartInfo info = OperatingSystem.IsWindows()
				? new ProcessStartInfo(url) { UseShellExecute = true }
				: new ProcessStartInfo(OperatingSystem.IsMacOS() ? "open" : "xdg-open", url)
				{
					UseShellExecute = false,
					RedirectStandardOutput = true,
					RedirectStandardError = true,
				};

			Process.Start(info)?.Dispose();
		}
		catch (Exception)
		{
		}
	}
}
