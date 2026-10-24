using System.Security.Cryptography;
using System.Text;
using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public static class InstallState
{
	public static string ManifestHash(Project project)
	{
		Manifest manifest = project.Manifest;
		StringBuilder builder = new();

		builder.Append("crater ").Append(BuildInfo.Version).Append('\n');
		builder.Append("registry ").Append(string.Join(' ', manifest.RegistryLocations)).Append('\n');
		builder.Append("style ").Append(project.Config.LinkStyle).Append('\n');

		foreach ((string name, string location) in (manifest.Registries ?? []).OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			builder.Append("named ").Append(name).Append('=').Append(location).Append('\n');
		}

		foreach (Realm realm in Realms.All)
		{
			builder.Append("folder ").Append(realm.Key()).Append('=').Append(project.Config.TryFolderFor(realm) ?? "").Append('\n');

			foreach (string dependency in manifest.DepsFor(realm))
			{
				builder.Append("dependency ").Append(realm.Key()).Append(' ').Append(dependency).Append('\n');
			}
		}

		builder.Append("scripts ").Append(manifest.BlockPackageScripts).Append(' ').Append(string.Join(',', manifest.AllowScriptsFrom ?? [])).Append('\n');

		foreach (string item in manifest.Overrides ?? [])
		{
			builder.Append("override ").Append(item).Append('\n');
		}

		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
	}

	public static bool IsCurrent(Project project, LockFile lockFile)
	{
		bool hasMovingSources = lockFile.Packages.Any(package => package.Source is not null) || project.Workspace is not null;

		if (hasMovingSources || lockFile.ManifestHash != ManifestHash(project))
		{
			return false;
		}

		foreach (Realm realm in Realms.All)
		{
			List<string> ids = lockFile.For(realm).Select(package => package.Id).ToList();
			string? folderName = project.Config.TryFolderFor(realm);

			if (folderName is null)
			{
				if (ids.Count > 0)
				{
					return false;
				}

				continue;
			}

			string folder = project.Abs(folderName);

			if (ids.Count == 0)
			{
				if (PackageLayout.IsManaged(folder))
				{
					return false;
				}

				continue;
			}

			Dictionary<string, string> marker = PackageLayout.ReadMarker(folder);
			string patches = marker.GetValueOrDefault(Patches.MarkerKey, "");
			HashSet<string> installed = marker.Keys.Where(key => !key.StartsWith('#')).ToHashSet();

			if (!installed.SetEquals(ids) || patches != Patches.Fingerprint(project, ids))
			{
				return false;
			}

			if (marker.Where(pair => !pair.Key.StartsWith('#')).Any(pair => !Directory.Exists(Path.Combine(folder, pair.Value))))
			{
				return false;
			}
		}

		return true;
	}
}
