using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class AuditCommand
{
	const int FindingsPerPackage = 3;

	public enum Level
	{
		Problem,
		Warning,
		Note,
	}

	public sealed record Item(Level Level, string Package, string Text);

	public static async Task<int> RunAsync(Args args)
	{
		Project project = Common.LoadProject();
		List<Item> items = [];

		if (!File.Exists(project.LockPath))
		{
			if (!project.Manifest.AllDependencies().Any())
			{
				Out.Ok("No dependencies to check");
				return 0;
			}

			throw new CraterException($"Nothing is installed yet. Run {Fmt.Cmd("crater install")} first.");
		}

		LockFile lockFile = LockFile.Load(project.LockPath);

		if (!InstallState.IsCurrent(project, lockFile))
		{
			items.Add(new Item(Level.Warning, Manifest.FileName, $"the installed packages do not match {Manifest.FileName} any more, run {Fmt.Cmd("crater install")}"));
		}

		List<UpdateCandidate> plan;

		using (new Spinner("Checking the registries"))
		{
			plan = await UpdatePlanner.PlanAsync(project);
		}

		HashSet<string> allowedScripts = new(project.Manifest.ScriptsAllowedFrom, StringComparer.OrdinalIgnoreCase);

		foreach (UpdateCandidate candidate in plan.DistinctBy(candidate => candidate.Name))
		{
			items.AddRange(RegistryItems(candidate, allowedScripts));
		}

		List<InstalledPackage> installed = [];

		try
		{
			installed = InstalledPackages.Load(project).DistinctBy(package => package.Locked.Id).ToList();
		}
		catch (CraterException exception)
		{
			items.Add(new Item(Level.Warning, "packages", exception.Message));
		}

		using (new Spinner("Scanning the installed packages"))
		{
			foreach (InstalledPackage package in installed)
			{
				items.AddRange(FolderItems(package));
			}
		}

		return Print(items, args.Has("strict"));
	}

	static IEnumerable<Item> RegistryItems(UpdateCandidate candidate, HashSet<string> allowedScripts)
	{
		RegistryIndex? index = candidate.Index;
		string id = candidate.Installed is null ? candidate.Name : $"{candidate.Name}@{candidate.Installed}";
		RegistryRelease? release = index?.Versions.FirstOrDefault(item =>
			candidate.Installed is not null && SemVer.TryParse(candidate.Installed, out SemVer installed) && item.SemVer.Equals(installed));

		if (index?.Deprecated is string packageMessage)
		{
			yield return new Item(Level.Warning, id, $"the package is deprecated: {packageMessage}");
		}

		if (release?.Deprecated is string versionMessage)
		{
			yield return new Item(Level.Problem, id, $"this version is deprecated: {versionMessage}");
		}

		if (release?.Scripts is { Count: > 0 } scripts)
		{
			yield return allowedScripts.Contains(candidate.Name)
				? new Item(Level.Note, id, $"runs scripts in your game ({PackageScripts.Describe(scripts)}), allowed in {Manifest.FileName}")
				: new Item(Level.Warning, id, $"runs scripts in your game: {PackageScripts.Describe(scripts)}");
		}

		if (candidate.IsDirect && candidate.HasNewer)
		{
			yield return new Item(Level.Note, id, $"{candidate.Latest} is out, outside your requirement {candidate.Requirement}");
		}
		else if (candidate.CanUpdate && candidate.Installed is not null)
		{
			yield return new Item(Level.Note, id, $"{candidate.Wanted} is available, run {Fmt.Cmd($"crater update {candidate.Name}")}");
		}
	}

	static IEnumerable<Item> FolderItems(InstalledPackage package)
	{
		string id = package.Locked.Id;
		string license = ReportCommands.LicenseOf(package.Directory);

		if (license.Length == 0)
		{
			yield return new Item(Level.Warning, id, "has no license, you may not have the right to use it");
		}
		else if (ReportCommands.LicenseNote(license) is { Length: > 0 } note)
		{
			yield return new Item(Level.Warning, id, $"{license}: {Out.Plain(note)}");
		}

		List<Finding> findings = PackageScanner.ScanDirectory(package.Directory, id)
			.Where(finding => finding.Severity is not Severity.Info)
			.ToList();

		foreach (Finding finding in findings.Where(finding => finding.Severity is Severity.Blocked or Severity.Danger).Take(FindingsPerPackage))
		{
			yield return new Item(Level.Problem, id, $"{finding.Location}: {finding.Description}");
		}

		List<Finding> warnings = findings.Where(finding => finding.Severity == Severity.Warning).ToList();

		foreach (Finding finding in warnings.Take(FindingsPerPackage))
		{
			yield return new Item(Level.Warning, id, $"{finding.Location}: {finding.Description}");
		}

		if (warnings.Count > FindingsPerPackage)
		{
			yield return new Item(Level.Warning, id, $"and {warnings.Count - FindingsPerPackage} more, see {Fmt.Cmd($"crater registry scan {Path.GetRelativePath(Environment.CurrentDirectory, package.Directory)}")}");
		}
	}

	static int Print(List<Item> items, bool strict)
	{
		(Level Level, string Title)[] sections =
		[
			(Level.Problem, "Problems"),
			(Level.Warning, "Warnings"),
			(Level.Note, "Notes"),
		];

		foreach ((Level level, string title) in sections)
		{
			List<Item> group = items.Where(item => item.Level == level).ToList();

			if (group.Count == 0)
			{
				continue;
			}

			Out.Info("");
			Out.Heading($"{title} ({group.Count})");

			foreach (Item item in group)
			{
				string mark = level switch
				{
					Level.Problem => Fmt.Bad("✗"),
					Level.Warning => "[v]![/]",
					_ => "[c]·[/]",
				};

				Out.Info($"  {mark} {Fmt.Pkg(item.Package)}  {item.Text}");
			}
		}

		int problems = items.Count(item => item.Level == Level.Problem);
		int warningCount = items.Count(item => item.Level == Level.Warning);

		Out.Info("");

		if (problems == 0 && warningCount == 0)
		{
			Out.Ok("No problems found");
			return 0;
		}

		string summary = $"{Fmt.Plural(problems, "problem", "problems")}, {Fmt.Plural(warningCount, "warning", "warnings")}";

		if (problems > 0 || (strict && warningCount > 0))
		{
			Out.Error(summary);
			return 1;
		}

		Out.Warn(summary);

		return 0;
	}
}
