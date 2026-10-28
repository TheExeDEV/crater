using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public static class ScriptPolicy
{
	public static async Task CheckAsync(
		Project project,
		IEnumerable<ResolvedPackage> packages,
		IReadOnlyDictionary<ResolvedPackage, string> prepared,
		LockFile oldLock)
	{
		Dictionary<string, string> preparedById = prepared.GroupBy(pair => pair.Key.Id).ToDictionary(group => group.Key, group => group.First().Value);
		Manifest manifest = project.Manifest;
		bool block = manifest.ScriptsBlocked;
		HashSet<string> allowed = new(manifest.ScriptsAllowedFrom, StringComparer.OrdinalIgnoreCase);
		HashSet<string> known = oldLock.Packages.Select(package => package.Id).ToHashSet();
		List<(ResolvedPackage Package, List<string> Scripts)> blocked = [];

		foreach (ResolvedPackage package in packages.DistinctBy(package => package.Id))
		{
			bool isNew = !known.Contains(package.Id);

			if (package.IsSource || (!isNew && !block))
			{
				continue;
			}

			List<string>? scripts = package.Release.Scripts;

			if (scripts is null)
			{
				if (preparedById.TryGetValue(package.Id, out string? directory))
				{
					scripts = PackageScripts.InDirectory(directory);
				}
				else if (block)
				{
					scripts = PackageScripts.InArchive(await PackageCache.GetAsync(package), package.Manifest, package.Id);
				}
			}

			if (scripts is null || scripts.Count == 0)
			{
				continue;
			}

			if (block && !allowed.Contains(package.Name))
			{
				blocked.Add((package, scripts));
				continue;
			}

			if (isNew)
			{
				Out.Warn($"{Fmt.Pkg(package.Id)} runs scripts in your game: {PackageScripts.Describe(scripts)}");
			}
		}

		if (blocked.Count == 0)
		{
			return;
		}

		string list = string.Join("\n", blocked.Select(item => $"    {Fmt.Pkg(item.Package.Id)}: {PackageScripts.Describe(item.Scripts)}"));
		string first = blocked[0].Package.Name;

		throw new CraterException(
			$"These packages run scripts in your game, and {Fmt.Val("\"package_scripts\": { \"block\": true }")} is set in {Fmt.File(Manifest.FileName)}:\n{list}\n" +
			$"  Remove them, or trust one with {Fmt.Cmd($"\"package_scripts\": {{ \"block\": true, \"allow\": [\"{first}\"] }}")}.");
	}
}
