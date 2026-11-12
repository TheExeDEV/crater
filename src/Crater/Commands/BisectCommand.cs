using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Commands;

public static class BisectCommand
{
	public static async Task<int> RunAsync(Args args)
	{
		Project project = Common.LoadProject();
		string test = TestCommand(project, args);
		string goodSource = args.Value("good")
			?? throw new CraterException($"Say which state worked: {Fmt.Cmd("crater bisect --good <commit|lock file> -- <test command>")}, for example {Fmt.Cmd("--good HEAD~1")}.");

		if (!File.Exists(project.LockPath))
		{
			throw new CraterException($"There is no {Fmt.File(LockFile.FileName)} to compare. Run {Fmt.Cmd("crater install")} first.");
		}

		string original = File.ReadAllText(project.LockPath);
		LockFile bad = LockFile.Load(project.LockPath);
		LockFile good = LoadGood(project, goodSource);
		List<LockChange> changes = Bisector.Changes(good, bad);

		if (changes.Count == 0)
		{
			Out.Info($"No package versions changed between {Fmt.Val(goodSource)} and the current {Fmt.File(LockFile.FileName)}.");
			return 0;
		}

		Out.Heading($"{Fmt.Plural(changes.Count, "package", "packages")} changed since {goodSource}:");

		foreach (LockChange change in changes)
		{
			Out.Info($"  {Fmt.Pkg(change.Name)} {change.Good} → {change.Bad}");
		}

		int runs = (int)Math.Ceiling(Math.Log2(changes.Count)) + 3;

		if (!Common.PrompterFor(project, args).Confirm($"Reinstall packages and run {Fmt.Cmd(test)} about {runs} times; {Fmt.File(LockFile.FileName)} and the package folders are put back at the end"))
		{
			return 1;
		}

		Session session = new(project, good, bad, test, changes);

		try
		{
			return await session.RunAsync(goodSource);
		}
		finally
		{
			Out.Step($"Putting back {LockFile.FileName}");
			File.WriteAllText(project.LockPath, original);
			await Session.InstallQuietlyAsync(project);
		}
	}

	static string TestCommand(Project project, Args args)
	{
		if (args.Value("script") is string script)
		{
			return project.Manifest.Scripts?.GetValueOrDefault(script)
				?? throw new CraterException($"Script {Fmt.Cmd(script)} not found in {Fmt.File(Manifest.FileName)}.");
		}

		if (args.Passthrough.Count > 0)
		{
			return string.Join(' ', args.Passthrough.Select(Quote));
		}

		throw new CraterException(
			$"Give the test that tells good from bad: {Fmt.Cmd("--script test")} runs a script from {Fmt.File(Manifest.FileName)}, " +
			$"or put a command after {Fmt.Cmd("--")}. It must exit with 0 when everything works.");
	}

	static string Quote(string argument)
	{
		bool plain = argument.Length > 0 && argument.All(character => char.IsLetterOrDigit(character) || character is '_' or '-' or '.' or '/' or ':' or '=' or '@' or ',' or '+' or '%');

		return plain ? argument : "\"" + argument.Replace("\"", "\\\"") + "\"";
	}

	static LockFile LoadGood(Project project, string source)
	{
		if (File.Exists(source))
		{
			return LockFile.Load(Path.GetFullPath(source));
		}

		ShellResult result = Shell.Capture("git", ["show", $"{source}:./{LockFile.FileName}"], project.Directory);

		if (!result.Success)
		{
			throw new CraterException($"Could not read {Fmt.File(LockFile.FileName)} at {Fmt.Val(source)}: {(result.Error.Length > 0 ? result.Error : "not a file or git revision")}.");
		}

		return Json.Parse<LockFile>(result.Output, $"{source}:{LockFile.FileName}");
	}

	sealed class Session(Project project, LockFile good, LockFile bad, string test, List<LockChange> changes)
	{
		readonly HashSet<string> stuck = [];

		public async Task<int> RunAsync(string goodSource)
		{
			List<string> names = changes.Select(change => change.Name).ToList();

			Out.Step("Checking the current versions");

			if (await PassesAsync(names))
			{
				Out.Ok($"{Fmt.Cmd(test)} passes with the current {Fmt.File(LockFile.FileName)}, there is nothing to look for.");
				return 0;
			}

			Out.Step($"Checking the versions from {goodSource}");

			if (!await PassesAsync([]))
			{
				Out.Error($"{Fmt.Cmd(test)} also fails with the versions from {Fmt.Val(goodSource)}, so a package update is not the cause.");
				WarnStuck();
				return 1;
			}

			string culprit = (await Bisector.FindAsync(names, PassesAsync, (number, total, size) =>
				Out.Step($"Step {number} of about {total}: trying {Fmt.Plural(size, "update", "updates")}")))!;

			LockChange found = changes.First(change => change.Name == culprit);

			Out.Step("Making sure it is the only one");

			bool alone = await PassesAsync(names.Where(name => name != culprit).ToList());

			Console.WriteLine();
			Out.Error($"{Fmt.Pkg(found.Name)} {found.Good} → {found.Bad} breaks {Fmt.Cmd(test)}");

			if (!alone)
			{
				Out.Warn("Other updates break it too: with this one undone the test still fails. Fix this one and run bisect again.");
			}

			Out.Dim($"  What changed: {Fmt.Cmd($"crater changelog {found.Name}")}");
			Out.Dim($"  Stay on the old version for now: {Fmt.Val($"\"overrides\": [\"{found.Name}@={found.Good.Split(", ")[0]}\"]")} in {Fmt.File(Manifest.FileName)}");
			WarnStuck();

			return 0;
		}

		async Task<bool> PassesAsync(IReadOnlyCollection<string> applied)
		{
			HashSet<string> reverted = changes.Select(change => change.Name).Where(name => !applied.Contains(name)).ToHashSet();
			LockFile mixed = Bisector.Mix(good, bad, reverted);

			Json.WriteText(project.LockPath, Json.Serialize(mixed));
			await InstallQuietlyAsync(project);
			NoteStuck(reverted);

			return Shell.RunInteractive(test, project.Directory, ProjectCommands.ScriptEnvironment(project)) == 0;
		}

		void NoteStuck(HashSet<string> reverted)
		{
			LockFile installed = LockFile.Load(project.LockPath);

			foreach (LockChange change in changes.Where(change => reverted.Contains(change.Name)))
			{
				bool back = installed.Everything.Any(package => package.Name == change.Name && change.Good.Split(", ").Contains(package.Version));

				if (!back)
				{
					stuck.Add(change.Name);
				}
			}
		}

		void WarnStuck()
		{
			if (stuck.Count > 0)
			{
				Out.Warn($"{string.Join(", ", stuck.Order(StringComparer.Ordinal).Select(name => Fmt.Pkg(name)))} could not go back to the old version, {Fmt.File(Manifest.FileName)} no longer allows it. The answer may be less exact.");
			}
		}

		public static async Task InstallQuietlyAsync(Project project)
		{
			bool quiet = Out.Quiet;

			Out.Quiet = true;

			try
			{
				await new Installer(project, new Prompter(false)).InstallAsync(update: null);
			}
			finally
			{
				Out.Quiet = quiet;
			}
		}
	}
}
