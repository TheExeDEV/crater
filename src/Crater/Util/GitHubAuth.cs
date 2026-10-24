namespace Crater.Util;

public static class GitHubAuth
{
	static string? token;
	static string? source;
	static bool searched;

	public static string? Token()
	{
		Search();

		return token;
	}

	public static string Source()
	{
		Search();

		return source ?? "nothing";
	}

	public static string? Login()
	{
		if (Credentials.Load() is { Login.Length: > 0 } credentials && Environment.GetEnvironmentVariable("CRATER_GITHUB_TOKEN") is null)
		{
			return credentials.Login;
		}

		return GitHubCli.Login();
	}

	public static void Forget()
	{
		searched = false;
		token = null;
		source = null;
	}

	static void Search()
	{
		if (searched)
		{
			return;
		}

		searched = true;

		foreach (string variable in new[] { "CRATER_GITHUB_TOKEN", "GITHUB_TOKEN", "GH_TOKEN" })
		{
			string? value = Environment.GetEnvironmentVariable(variable);

			if (!string.IsNullOrWhiteSpace(value))
			{
				token = value.Trim();
				source = variable;
				return;
			}
		}

		if (Credentials.Load() is { Token.Length: > 0 } credentials)
		{
			token = credentials.Token;
			source = "crater login";
			return;
		}

		if (GitHubCli.Find() is not null)
		{
			ShellResult result = GitHubCli.Capture(["auth", "token", "--hostname", "github.com"]);

			if (result.Success && result.Output.Length > 0)
			{
				token = result.Output.Trim();
				source = "GitHub CLI";
			}
		}
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
