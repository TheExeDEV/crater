using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public static class ScriptPolicy
{
	public static async Task CheckAsync(
		Project project,
		IEnumerable<ResolvedPackage> packages,
		IReadOnlyDictionary<ResolvedPackage, byte[]> archives,
		LockFile oldLock)
	{
		Manifest manifest = project.Manifest;
		bool block = manifest.BlockPackageScripts == true;
		HashSet<string> allowed = new(manifest.AllowScriptsFrom ?? [], StringComparer.OrdinalIgnoreCase);
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
				byte[]? archive = archives.TryGetValue(package, out byte[]? downloaded)
					? downloaded
					: block ? await PackageCache.GetAsync(package) : null;

				scripts = archive is null ? null : PackageScripts.InArchive(archive, package.Manifest, package.Id);
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
			$"These packages run scripts in your game, and {Fmt.Val("\"block_package_scripts\": true")} is set in {Fmt.File(Manifest.FileName)}:\n{list}\n" +
			$"  Remove them, or trust one with {Fmt.Cmd($"\"allow_scripts_from\": [\"{first}\"]")}.");
	}
}
