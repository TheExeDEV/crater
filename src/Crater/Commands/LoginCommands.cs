using Crater.Cli;
using Crater.Util;

namespace Crater.Commands;

public static class LoginCommands
{
	public static int Login(Args args)
	{
		string gh = GitHubCli.Find() ?? InstallGitHubCli(args);
		string? current = GitHubCli.Login();

		if (current is not null && !args.Has("force"))
		{
			Out.Ok($"Already signed in to GitHub as {Fmt.Val(current)}. Run {Fmt.Cmd("crater login --force")} to switch accounts.");
			return 0;
		}

		Out.Step("Signing in to GitHub. A browser window opens, confirm the code shown below.");

		int exitCode = Shell.RunInteractive($"\"{gh}\" auth login --hostname github.com --git-protocol https --web", System.Environment.CurrentDirectory);

		if (exitCode != 0)
		{
			throw new CraterException("GitHub sign-in did not finish.");
		}

		GitHubCli.Run(["auth", "setup-git", "--hostname", "github.com"]);

		string login = GitHubCli.RequireLogin();

		Out.Ok($"Signed in as {Fmt.Val(login)}. Publish packages named {Fmt.Pkg($"{login.ToLowerInvariant()}/<package>")} with {Fmt.Cmd("crater publish")}.");

		return 0;
	}

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
