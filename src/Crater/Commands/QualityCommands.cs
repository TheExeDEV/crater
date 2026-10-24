using System.Diagnostics;
using Crater.Cli;
using Crater.Core;
using Crater.Util;

namespace Crater.Commands;

public static class QualityCommands
{
	const string SeleneConfig = "selene.toml";

	public static async Task<int> LintAsync(Args args)
	{
		Project project = Common.LoadProject();
		string config = Path.Combine(project.Directory, SeleneConfig);

		if (!File.Exists(config))
		{
			await File.WriteAllTextAsync(config, "std = \"roblox\"\n");
			Out.Info($"Created {Fmt.File(SeleneConfig)} with the Roblox standard library. Edit it to change selene's rules.");
		}

		List<string> targets = args.Positional.Count > 0 ? args.Positional.ToList() : ProjectSources.Folders(project);

		Out.Step($"Linting {string.Join(", ", targets)}");

		List<string> arguments = args.Has("strict") ? [.. targets] : ["--allow-warnings", .. targets];
		int exitCode = await RunToolAsync(project, "selene", arguments);

		Report(exitCode == 0, "No lint problems", "selene found problems");

		return exitCode == 0 ? 0 : 1;
	}

	public static async Task<int> FormatAsync(Args args)
	{
		Project project = Common.LoadProject();
		bool check = args.Has("check");
		List<string> targets = args.Positional.Count > 0 ? args.Positional.ToList() : ProjectSources.Folders(project);
		List<string> arguments = check ? ["--check", .. targets] : [.. targets];

		Out.Step($"{(check ? "Checking the formatting of" : "Formatting")} {string.Join(", ", targets)}");

		int exitCode = await RunToolAsync(project, "stylua", arguments);

		Report(exitCode == 0, check ? "Formatting is clean" : "Formatted", check ? $"Some files are not formatted. Run {Fmt.Cmd("crater fmt")}." : "stylua failed");

		return exitCode == 0 ? 0 : 1;
	}

	public static async Task<int> CiAsync(Args args)
	{
		List<(string Name, Func<Task<int>> Run)> steps =
		[
			("types", () => CheckCommand.RunAsync(Args.Parse(["check"]))),
			("lint", () => LintAsync(Args.Parse(["lint"]))),
			("format", () => FormatAsync(Args.Parse(["fmt", "--check"]))),
		];

		List<(string Name, bool Passed)> results = [];

		foreach ((string name, Func<Task<int>> run) in steps)
		{
			bool passed;

			try
			{
				passed = await run() == 0;
			}
			catch (CraterException exception)
			{
				Out.Error(exception.Message);
				passed = false;
			}

			results.Add((name, passed));
			Out.Info("");
		}

		Table.Print(["Step", "Result"], results.Select(result => (IReadOnlyList<string>)[result.Name, result.Passed ? Fmt.Good("passed") : Fmt.Bad("failed")]).ToList());

		return results.All(result => result.Passed) ? 0 : 1;
	}

	static void Report(bool passed, string success, string failure)
	{
		if (passed)
		{
			Out.Ok(success);
		}
		else
		{
			Out.Error(failure);
		}
	}

	static async Task<int> RunToolAsync(Project project, string tool, IEnumerable<string> arguments)
	{
		string executable = await ToolCommand.ExecutableAsync(tool);
		ProcessStartInfo info = new(executable) { UseShellExecute = false, WorkingDirectory = project.Directory };

		foreach (string argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(info) ?? throw new CraterException($"Could not start {tool}.");

		process.WaitForExit();

		return process.ExitCode;
	}
}
