using System.Text.Json;
using Crater.Util;

namespace Crater.Tools;

public static class GitHubApi
{
	static string? token;
	static bool searched;

	public static async Task<JsonElement?> GetAsync(string path)
	{
		Dictionary<string, string> headers = new() { ["Accept"] = "application/vnd.github+json" };

		if (Token() is string value)
		{
			headers["Authorization"] = $"Bearer {value}";
		}

		byte[]? data = await Http.GetBytesOrNullAsync($"https://api.github.com/{path}", null, headers);

		if (data is null)
		{
			return null;
		}

		using JsonDocument document = JsonDocument.Parse(data);

		return document.RootElement.Clone();
	}

	static string? Token()
	{
		if (searched)
		{
			return token;
		}

		searched = true;
		token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

		if (string.IsNullOrWhiteSpace(token) && GitHubCli.Find() is not null)
		{
			ShellResult result = GitHubCli.Capture(["auth", "token", "--hostname", "github.com"]);
			token = result.Success && result.Output.Length > 0 ? result.Output.Trim() : null;
		}

		return string.IsNullOrWhiteSpace(token) ? null : token;
	}
}
