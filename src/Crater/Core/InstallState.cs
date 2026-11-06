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
		builder.Append("luaurc ").Append(project.Config.LuaurcPath).Append(' ').Append(project.Config.OnInstallUpdateLuaurc).Append(' ').Append(project.Config.OnInstallUpdateVscodeLuauDefinitionFiles).Append('\n');

		foreach ((string name, string location) in (manifest.Registries ?? []).OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			builder.Append("named ").Append(name).Append('=').Append(location).Append('\n');
		}

		foreach (Realm realm in Realms.All)
		{
			string folder = project.Config.TryFolderFor(realm) ?? "";

			builder.Append("folder ").Append(realm.Key()).Append('=').Append(folder).Append('\n');
			builder.Append("alias ").Append(realm.Key()).Append('=').Append(folder.Length > 0 ? project.Config.AliasFor(realm, folder) : "").Append('\n');

			foreach (string dependency in manifest.DepsFor(realm))
			{
				builder.Append("dependency ").Append(realm.Key()).Append(' ').Append(dependency).Append('\n');
			}
		}

		foreach ((Realm realm, string folder) in project.Config.VendorFolders())
		{
			builder.Append("vendor ").Append(realm.Key()).Append('=').Append(folder).Append(' ').Append(project.Config.VendorAlias(realm)).Append('\n');
		}

		foreach (Realm realm in Realms.All)
		{
			foreach (string dependency in manifest.VendorFor(realm))
			{
				builder.Append("vendored ").Append(realm.Key()).Append(' ').Append(dependency).Append('\n');
			}
		}

		builder.Append("scripts ").Append(manifest.ScriptsBlocked).Append(' ').Append(string.Join(',', manifest.ScriptsAllowedFrom)).Append('\n');

		foreach (string item in manifest.Overrides ?? [])
		{
			builder.Append("override ").Append(item).Append('\n');
		}

		return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(builder.ToString())));
	}

	public static bool IsCurrent(Project project, LockFile lockFile)
	{
		bool hasMovingSources = lockFile.Everything.Any(package => package.Source is not null) || project.Workspace is not null;

		if (hasMovingSources || lockFile.ManifestHash != ManifestHash(project))
		{
			return false;
		}

		foreach (Realm realm in Realms.All)
		{
			if (!FolderMatches(project, project.Config.TryFolderFor(realm), lockFile.For(realm).Select(package => package.Id).ToList()))
			{
				return false;
			}
		}

		foreach (Realm realm in Realms.All)
		{
			if (!FolderMatches(project, project.Config.VendorFolder(realm), lockFile.VendorFor(realm).Select(package => package.Id).ToList()))
			{
				return false;
			}
		}

		return true;
	}

	static bool FolderMatches(Project project, string? folderName, List<string> ids)
	{
		if (folderName is null)
		{
			return ids.Count == 0;
		}

		string folder = project.Abs(folderName);

		if (ids.Count == 0)
		{
			return !PackageLayout.IsManaged(folder);
		}

		Dictionary<string, string> marker = PackageLayout.ReadMarker(folder);
		string patches = marker.GetValueOrDefault(Patches.MarkerKey, "");
		HashSet<string> installed = marker.Keys.Where(key => !key.StartsWith('#')).ToHashSet();

		if (!installed.SetEquals(ids) || patches != Patches.Fingerprint(project, ids))
		{
			return false;
		}

		return marker.Where(pair => !pair.Key.StartsWith('#')).All(pair => Directory.Exists(Path.Combine(folder, pair.Value)));
	}
}
