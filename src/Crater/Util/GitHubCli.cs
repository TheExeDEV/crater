namespace Crater.Util;

public static class GitHubCli
{
	static string? located;
	static bool searched;

	public static string? Find()
	{
		if (searched)
		{
			return located;
		}

		searched = true;

		List<string> candidates = ["gh"];

		if (OperatingSystem.IsWindows())
		{
			candidates.Add(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.ProgramFiles), "GitHub CLI", "gh.exe"));
			candidates.Add(Path.Combine(System.Environment.GetFolderPath(System.Environment.SpecialFolder.LocalApplicationData), "Programs", "GitHub CLI", "gh.exe"));
		}

		foreach (string candidate in candidates)
		{
			try
			{
				if (Shell.Capture(candidate, ["--version"], System.Environment.CurrentDirectory).Success)
				{
					located = candidate;
					break;
				}
			}
			catch (CraterException)
			{
			}
		}

		return located;
	}

	public static string Require()
	{
		return Find() ?? throw new CraterException($"GitHub CLI is not installed. Run {Fmt.Cmd("crater login")} first.");
	}

	public static void Forget()
	{
		searched = false;
		located = null;
	}

	public static ShellResult Capture(IEnumerable<string> arguments, string? directory = null)
	{
		return Shell.Capture(Require(), arguments, directory ?? System.Environment.CurrentDirectory);
	}

	public static string Run(IEnumerable<string> arguments, string? directory = null)
	{
		return Shell.Run(Require(), arguments, directory ?? System.Environment.CurrentDirectory);
	}

	public static string? Login()
	{
		if (Find() is null)
		{
			return null;
		}

		ShellResult saved = Capture(["config", "get", "-h", "github.com", "user"]);

		if (saved.Success && saved.Output.Length > 0 && Capture(["auth", "token", "-h", "github.com"]).Success)
		{
			return saved.Output.Trim();
		}

		ShellResult result = Capture(["api", "user", "--jq", ".login"]);

		return result.Success && result.Output.Length > 0 ? result.Output : null;
	}

	public static string RequireLogin()
	{
		return Login() ?? throw new CraterException($"You are not signed in to GitHub. Run {Fmt.Cmd("crater login")}.");
	}

	public static bool HasPermission(string repository, string permission)
	{
		ShellResult result = Api([$"repos/{repository}", "--jq", $".permissions.{permission}"]);

		return result.Success && result.Output == "true";
	}

	public static string? ParentOf(string repository)
	{
		ShellResult result = Api([$"repos/{repository}", "--jq", ".parent.full_name // \"\""]);

		if (!result.Success)
		{
			return null;
		}

		return result.Output.Length > 0 ? result.Output : "";
	}

	public static ShellResult Api(IEnumerable<string> arguments)
	{
		List<string> command = ["api", .. arguments];
		ShellResult result = Capture(command);

		for (int attempt = 1; attempt < 4 && !result.Success && !result.Error.Contains("HTTP 4"); attempt++)
		{
			Thread.Sleep(1000 * attempt);
			result = Capture(command);
		}

		if (!result.Success && !result.Error.Contains("HTTP 4"))
		{
			throw new CraterException($"Could not reach GitHub: {result.Error}");
		}

		return result;
	}
}

public sealed record GitHubRepository(string Owner, string Name, string Branch)
{
	const string RawPrefix = "https://raw.githubusercontent.com/";

	public string Slug => $"{Owner}/{Name}";
	public string CloneUrl => $"https://github.com/{Slug}.git";

	public static GitHubRepository? FromRegistry(string expandedLocation)
	{
		if (!expandedLocation.StartsWith(RawPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		string[] parts = expandedLocation[RawPrefix.Length..].Trim('/').Split('/');

		if (parts.Length < 3 || parts.Take(3).Any(part => part.Length == 0))
		{
			return null;
		}

		return new GitHubRepository(parts[0], parts[1], string.Join('/', parts[2..]));
	}
}
