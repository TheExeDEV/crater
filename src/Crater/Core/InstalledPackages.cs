using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public sealed record InstalledPackage(LockedPackage Locked, Realm Realm, string Directory);

public static class InstalledPackages
{
	public static List<InstalledPackage> Load(Project project)
	{
		LockFile lockFile = LockFile.Load(project.LockPath);
		List<InstalledPackage> installed = [];

		if (lockFile.Packages.Count == 0)
		{
			throw new CraterException($"Nothing is installed. Run {Fmt.Cmd("crater install")} first.");
		}

		foreach (Realm realm in Realms.All)
		{
			string? folderName = project.Config.TryFolderFor(realm);

			if (folderName is null)
			{
				continue;
			}

			string folder = project.Abs(folderName);
			Dictionary<string, string> folders = PackageLayout.ReadMarker(folder);

			foreach (LockedPackage locked in lockFile.For(realm))
			{
				if (folders.TryGetValue(locked.Id, out string? name))
				{
					installed.Add(new InstalledPackage(locked, realm, Path.Combine(folder, name)));
				}
			}
		}

		if (installed.Count == 0)
		{
			throw new CraterException($"The package folders are missing or outdated. Run {Fmt.Cmd("crater install")} first.");
		}

		return installed;
	}

	public static InstalledPackage Find(Project project, string nameOrAlias)
	{
		string wanted = nameOrAlias.ToLowerInvariant();
		List<InstalledPackage> installed = Load(project);

		return installed.FirstOrDefault(package => package.Locked.Name == wanted)
			?? installed.FirstOrDefault(package => package.Locked.ShortName == wanted)
			?? installed.FirstOrDefault(package => (package.Locked.Links ?? []).Any(link => link.Equals(nameOrAlias, StringComparison.OrdinalIgnoreCase)))
			?? throw new CraterException($"{Fmt.Pkg(nameOrAlias)} is not installed in this project.");
	}
}
