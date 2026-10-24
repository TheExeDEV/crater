using System.Diagnostics;
using System.Text;
using Crater.Cli;
using Crater.Core;
using Crater.Util;

namespace Crater.Commands;

public static class TestCommand
{
	static readonly string[] TestSuffixes = [".test.luau", ".spec.luau", ".test.lua", ".spec.lua"];
	static readonly string[] TestFolders = ["tests", "test", "spec", "specs"];

	public static async Task<int> RunAsync(Args args)
	{
		Project project = Common.LoadProject();

		if (project.Manifest.Scripts?.ContainsKey("test") == true && !args.Has("lune"))
		{
			Out.Step($"Running the {Fmt.Val("test")} script from {Fmt.File("crater.json")}");

			return ProjectCommands.Run(Args.Parse(["run", "test", "--", .. args.Passthrough]));
		}

		List<string> files = FindTests(project, args.First);

		if (files.Count == 0)
		{
			Out.Info($"No tests found. Name test files {Fmt.File("*.test.luau")} or {Fmt.File("*.spec.luau")}, or put them in {Fmt.File("tests/")}.");
			return 0;
		}

		string lune = await ToolCommand.ExecutableAsync("lune");
		List<(string File, bool Passed, TimeSpan Time)> results = [];

		foreach (string file in files)
		{
			string relative = Path.GetRelativePath(project.Directory, file).Replace('\\', '/');
			Stopwatch watch = Stopwatch.StartNew();
			ShellResult result = Run(lune, relative, project.Directory);

			watch.Stop();
			results.Add((relative, result.Success, watch.Elapsed));

			if (result.Success)
			{
				Out.Ok($"{relative} {Fmt.Val($"{watch.Elapsed.TotalSeconds:0.00}s")}");
				continue;
			}

			Out.Error(relative);

			foreach (string line in (result.Error.Length > 0 ? result.Error : result.Output).Split('\n').Take(20))
			{
				Out.Dim("    " + line.TrimEnd());
			}
		}

		int failed = results.Count(result => !result.Passed);

		Out.Info("");

		if (failed == 0)
		{
			Out.Ok($"{Fmt.Plural(results.Count, "test file", "test files")} passed in {results.Sum(result => result.Time.TotalSeconds):0.00}s");
			return 0;
		}

		Out.Error($"{failed} of {Fmt.Plural(results.Count, "test file", "test files")} failed");

		return 1;
	}

	static List<string> FindTests(Project project, string? filter)
	{
		IEnumerable<string> folders = ProjectSources.Folders(project)
			.Concat(TestFolders.Where(folder => Directory.Exists(Path.Combine(project.Directory, folder))));

		return ProjectSources.LuauFiles(project, folders)
			.Where(file => IsTest(project, file))
			.Where(file => filter is null || Path.GetRelativePath(project.Directory, file).Replace('\\', '/').Contains(filter, StringComparison.OrdinalIgnoreCase))
			.Distinct()
			.ToList();
	}

	static bool IsTest(Project project, string file)
	{
		string relative = Path.GetRelativePath(project.Directory, file).Replace('\\', '/');
		string name = Path.GetFileName(relative).ToLowerInvariant();

		return TestSuffixes.Any(name.EndsWith) || TestFolders.Contains(relative.Split('/')[0], StringComparer.OrdinalIgnoreCase);
	}

	static ShellResult Run(string lune, string file, string directory)
	{
		ProcessStartInfo info = new(lune)
		{
			WorkingDirectory = directory,
			UseShellExecute = false,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			StandardOutputEncoding = Encoding.UTF8,
			StandardErrorEncoding = Encoding.UTF8,
		};

		info.ArgumentList.Add("run");
		info.ArgumentList.Add(file);

		using Process process = Process.Start(info) ?? throw new CraterException("Could not start lune.");

		Task<string> output = process.StandardOutput.ReadToEndAsync();
		Task<string> error = process.StandardError.ReadToEndAsync();

		process.WaitForExit();

		return new ShellResult(process.ExitCode, output.Result.Trim(), error.Result.Trim());
	}
}
