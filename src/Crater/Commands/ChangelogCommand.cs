using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class ChangelogCommand
{
	public static async Task RunAsync(Args args)
	{
		DependencySpec spec = InspectCommands.ResolveSpec(args.First ?? throw new CraterException($"Usage: {Fmt.Cmd("crater changelog <scope/name> [version]")}"));
		string name = spec.FullName;
		string? wanted = args.Positional.Count > 1 ? args.Positional[1].TrimStart('v') : null;
		IRegistry registry = InspectCommands.OpenRegistry(args, spec);
		RegistryIndex index = await registry.GetIndexAsync(name)
			?? throw new CraterException($"{Fmt.Pkg(name)} not found in {Fmt.File(registry.Location)}");
		HashSet<string> installed = InstalledVersions(name);

		if (wanted is not null && !index.Versions.Any(release => release.Version == wanted))
		{
			throw new CraterException($"{Fmt.Pkg(name)} has no version {Fmt.Val(wanted)}. See {Fmt.Cmd($"crater versions {name}")}.");
		}

		List<RegistryRelease> releases = index.Versions
			.Where(release => wanted is null ? !string.IsNullOrWhiteSpace(release.Notes) : release.Version == wanted)
			.OrderByDescending(release => release.SemVer)
			.ToList();

		if (Out.Quiet)
		{
			Out.WriteJson(new JsonArray(releases.Select(release => (JsonNode)new JsonObject
			{
				["version"] = release.Version,
				["published_at"] = release.PublishedAt?.ToString("o"),
				["installed"] = installed.Contains(release.Version),
				["notes"] = release.Notes,
			}).ToArray()));
			return;
		}

		if (releases.All(release => string.IsNullOrWhiteSpace(release.Notes)))
		{
			Out.Info(wanted is null
				? $"{Fmt.Pkg(name)} has no changelog. Authors add one with a {Fmt.File("CHANGELOG.md")} that has a {Fmt.Val("## 1.2.3")} section for each version."
				: $"{Fmt.Pkg($"{name}@{wanted}")} was published without notes.");
			return;
		}

		Out.Heading($"Changelog of {name}");

		foreach (RegistryRelease release in releases)
		{
			string date = release.PublishedAt is { } published ? $" ({published:yyyy-MM-dd})" : "";
			string mark = installed.Contains(release.Version) ? $" {Fmt.Good("installed")}" : "";

			Out.Info("");
			Out.Info($"  {Fmt.Val(release.Version)}{date}{mark}");

			foreach (string line in (release.Notes ?? "").Split('\n'))
			{
				Out.Dim("    " + line.TrimEnd());
			}
		}
	}

	static HashSet<string> InstalledVersions(string name)
	{
		Project? project = InspectCommands.TryLoadProject();

		if (project is null || !File.Exists(project.LockPath))
		{
			return [];
		}

		return LockFile.Load(project.LockPath).Packages
			.Where(package => package.Name == name)
			.Select(package => package.Version)
			.ToHashSet();
	}
}
