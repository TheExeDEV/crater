namespace Crater.Util;

public static class GitHubAuth
{
	static string? token;
	static bool searched;

	public static string? Token()
	{
		if (searched)
		{
			return token;
		}

		searched = true;

		foreach (string variable in new[] { "CRATER_GITHUB_TOKEN", "GITHUB_TOKEN", "GH_TOKEN" })
		{
			string? value = Environment.GetEnvironmentVariable(variable);

			if (!string.IsNullOrWhiteSpace(value))
			{
				token = value.Trim();
				return token;
			}
		}

		if (GitHubCli.Find() is not null)
		{
			ShellResult result = GitHubCli.Capture(["auth", "token", "--hostname", "github.com"]);
			token = result.Success && result.Output.Length > 0 ? result.Output.Trim() : null;
		}

		return token;
	}

	public static Dictionary<string, string> Headers(string? accept = null)
	{
		Dictionary<string, string> headers = [];

		if (accept is not null)
		{
			headers["Accept"] = accept;
		}

		if (Token() is string value)
		{
			headers["Authorization"] = $"token {value}";
		}

		return headers;
	}
}
