using System.Diagnostics;
using System.Text.RegularExpressions;
using Crater.Cli;
using Crater.Util;

namespace Crater.Commands;

public static partial class LoginCommands
{
	const string DeviceUrl = "https://github.com/login/device";

	public static int Login(Args args)
	{
		string gh = GitHubCli.Find() ?? InstallGitHubCli(args);
		string? current = GitHubCli.Login();

		if (current is not null && !args.Has("force"))
		{
			Out.Ok($"Already signed in to GitHub as {Fmt.Val(current)}. Run {Fmt.Cmd("crater login --force")} to switch accounts.");
			return 0;
		}

		bool hasBrowser = OperatingSystem.IsWindows() || OperatingSystem.IsMacOS()
			|| !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("DISPLAY"))
			|| !string.IsNullOrEmpty(System.Environment.GetEnvironmentVariable("WAYLAND_DISPLAY"));

		RunDeviceLogin(gh, hasBrowser);

		GitHubCli.Run(["auth", "setup-git", "--hostname", "github.com"]);

		string login = GitHubCli.RequireLogin();

		Out.Ok($"Signed in as {Fmt.Val(login)}. Publish packages named {Fmt.Pkg($"{login.ToLowerInvariant()}/<package>")} with {Fmt.Cmd("crater publish")}.");

		return 0;
	}

	static void RunDeviceLogin(string gh, bool hasBrowser)
	{
		ProcessStartInfo info = new(gh)
		{
			UseShellExecute = false,
			RedirectStandardInput = true,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
		};

		foreach (string argument in new[] { "auth", "login", "--hostname", "github.com", "--git-protocol", "https", "--web" })
		{
			info.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(info) ?? throw new CraterException("Could not start the GitHub CLI.");

		Task<string> output = process.StandardOutput.ReadToEndAsync();
		List<string> messages = [];
		string? code = null;

		while (code is null && process.StandardError.ReadLine() is string line)
		{
			string text = AnsiCodes().Replace(line, "").Trim();
			Match match = DeviceCode().Match(text);

			if (match.Success)
			{
				code = match.Value;
			}
			else if (text.Length > 0)
			{
				messages.Add(text);
			}
		}

		if (code is null)
		{
			process.WaitForExit();
			throw new CraterException($"GitHub sign-in did not start: {string.Join(" ", messages)}");
		}

		Out.Step("Signing in to GitHub");
		Out.Info("");
		Out.Info($"  1. Open    {Fmt.File(DeviceUrl)}");
		Out.Info($"  2. Enter   {Fmt.Val(code)}");
		Out.Info("  3. Confirm access");
		Out.Info("");
		Out.Dim("  The link works in any browser, also on your phone or another computer.");
		Out.Info("");

		if (hasBrowser)
		{
			OpenBrowser(DeviceUrl);
		}

		Task<string> rest = process.StandardError.ReadToEndAsync();

		using (new Spinner("Waiting for you to confirm on GitHub"))
		{
			process.WaitForExit();
		}

		if (process.ExitCode != 0)
		{
			string details = string.Join(" ", AnsiCodes().Replace(rest.Result + output.Result, "")
				.Split('\n', StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
				.Where(text => !text.Contains("Open this URL", StringComparison.OrdinalIgnoreCase)));

			throw new CraterException($"GitHub sign-in did not finish. {details}".Trim());
		}
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

	[GeneratedRegex(@"\x1b\[[0-9;]*[A-Za-z]")]
	private static partial Regex AnsiCodes();

	[GeneratedRegex(@"\b[A-Z0-9]{4}-[A-Z0-9]{4}\b")]
	private static partial Regex DeviceCode();

	public static int Logout()
	{
		string? gh = GitHubCli.Find();

		if (gh is null || GitHubCli.Login() is null)
		{
			Out.Info("You are not signed in.");
			return 0;
		}

		return Shell.RunInteractive($"\"{gh}\" auth logout --hostname github.com", System.Environment.CurrentDirectory);
	}

	public static int WhoAmI()
	{
		string? login = GitHubCli.Login();

		if (login is null)
		{
			Out.Info($"Not signed in. Run {Fmt.Cmd("crater login")}.");
			return 1;
		}

		Out.Info($"{Fmt.Val(login)} (packages go to {Fmt.Pkg(login.ToLowerInvariant() + "/")})");

		return 0;
	}

	static string InstallGitHubCli(Args args)
	{
		if (!OperatingSystem.IsWindows())
		{
			string hint = OperatingSystem.IsMacOS() ? "brew install gh" : "see https://github.com/cli/cli#installation";

			throw new CraterException($"crater signs in through the GitHub CLI. Install it ({Fmt.Cmd(hint)}) and run {Fmt.Cmd("crater login")} again.");
		}

		Prompter prompt = new(!args.Has("y", "yes"));

		if (!prompt.Confirm($"crater signs in through the GitHub CLI. Install it now with {Fmt.Cmd("winget")}"))
		{
			throw new CraterException($"Install the GitHub CLI ({Fmt.Cmd("winget install GitHub.cli")}) and run {Fmt.Cmd("crater login")} again.");
		}

		int exitCode = Shell.RunInteractive(
			"winget install --id GitHub.cli --exact --source winget --accept-package-agreements --accept-source-agreements",
			System.Environment.CurrentDirectory);

		GitHubCli.Forget();

		string? gh = GitHubCli.Find();

		if (exitCode != 0 || gh is null)
		{
			throw new CraterException($"The GitHub CLI could not be installed. Install it manually ({Fmt.Cmd("winget install GitHub.cli")}) and run {Fmt.Cmd("crater login")} again.");
		}

		return gh;
	}
}
