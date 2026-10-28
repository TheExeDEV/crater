using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Core;

public static class VersionNotices
{
	public static async Task PrintAsync(Project project, IEnumerable<ResolvedGraph> graphs)
	{
		List<ResolvedGraph> list = graphs.ToList();
		List<ResolvedPackage> packages = list
			.SelectMany(graph => graph.All)
			.Where(package => !package.IsSource)
			.DistinctBy(package => package.Id)
			.ToList();

		foreach (ResolvedPackage package in packages.Where(package => package.Release.PackageDeprecated is not null).DistinctBy(package => package.Name))
		{
			Out.Warn($"{Fmt.Pkg(package.Name)} is deprecated: {package.Release.PackageDeprecated}");
		}

		foreach (ResolvedPackage package in packages.Where(package => package.Release.Deprecated is not null))
		{
			Out.Warn($"{Fmt.Pkg(package.Id)} is deprecated: {package.Release.Deprecated}. Run {Fmt.Cmd($"crater update {package.Name}")} for a newer version.");
		}

		List<string> newer = await NewerVersionsAsync(project, list);

		if (newer.Count > 0)
		{
			Out.Info($"Newer versions exist outside the requirement in {Fmt.File(Manifest.FileName)}: {string.Join(", ", newer)}");
			Out.Dim($"  {Fmt.Cmd("crater update -i")} can move to them. To keep a version on purpose, pin it with {Fmt.Val("=")}, for example {Fmt.Val("scope/name=1.2.3")}.");
		}
	}

	public static async Task PrintChangesAsync(LockFile previous, IEnumerable<ResolvedGraph> graphs)
	{
		HashSet<string> shown = [];

		foreach (ResolvedGraph graph in graphs)
		{
			Dictionary<string, LockedPackage> locked = previous.For(graph.Realm).ToDictionary(package => package.Name);

			foreach (ResolvedPackage package in graph.Roots.Select(root => root.Package).Where(package => !package.IsSource))
			{
				bool upgraded = locked.TryGetValue(package.Name, out LockedPackage? old) && SemVer.TryParse(old.Version, out SemVer from) && package.Version > from;

				if (!upgraded || !shown.Add(package.Name) || await package.Registry.GetIndexAsync(package.Name) is not RegistryIndex index)
				{
					continue;
				}

				PrintNotes(package.Name, Changelog.Between(index, SemVer.Parse(old!.Version), package.Version), 12);
			}
		}
	}

	public static void PrintNotes(string name, List<RegistryRelease> releases, int maxLines)
	{
		if (releases.Count == 0)
		{
			return;
		}

		Out.Info("");
		Out.Heading($"What changed in {name}");

		int printed = 0;

		foreach (RegistryRelease release in releases)
		{
			string[] lines = release.Notes!.Split('\n').Where(line => line.Trim().Length > 0).ToArray();
			string date = release.PublishedAt is { } published ? $" ({published:yyyy-MM-dd})" : "";

			Out.Info($"  {Fmt.Val(release.Version)}{date}");

			foreach (string line in lines)
			{
				if (printed++ >= maxLines)
				{
					Out.Dim($"    ... more in {Fmt.Cmd($"crater changelog {name}")}");
					return;
				}

				Out.Dim("    " + line.Trim());
			}
		}
	}

	static async Task<List<string>> NewerVersionsAsync(Project project, List<ResolvedGraph> graphs)
	{
		List<string> newer = [];
		HashSet<string> seen = [];

		foreach (ResolvedGraph graph in graphs)
		{
			List<DependencySpec> specs = project.Manifest.DepsFor(graph.Realm).Select(raw => DependencySpec.Parse(raw)).ToList();

			foreach (ResolvedDependency root in graph.Roots)
			{
				ResolvedPackage package = root.Package;
				DependencySpec? spec = specs.FirstOrDefault(item => item.FullName == package.Name);

				if (spec is null || spec.Source is not null || spec.Req.Kind == ReqKind.Exact || package.IsSource || !seen.Add(package.Name))
				{
					continue;
				}

				RegistryIndex? index = await package.Registry.GetIndexAsync(package.Name);
				RegistryRelease? latest = index?.Latest;

				if (latest is not null && latest.SemVer > package.Version && !spec.Req.Matches(latest.SemVer))
				{
					newer.Add($"{Fmt.Pkg(package.Name)} {package.Version} → {Fmt.Val(latest.Version)}");
				}
			}
		}

		return newer;
	}
}
