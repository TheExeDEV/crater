using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Registry;
using Crater.Util;

namespace Crater.Commands;

public static class PatchCommand
{
	public static async Task RunAsync(Args args)
	{
		Project project = Common.LoadProject();

		if (args.First is null)
		{
			List(project);
			return;
		}

		InstalledPackage package = InstalledPackages.Find(project, args.First);
		string id = package.Locked.Id;
		string directory = Patches.DirectoryFor(project, id);
		string shown = $"{Patches.FolderName}/{Path.GetFileName(directory)}";

		if (args.Has("remove"))
		{
			if (!Directory.Exists(directory))
			{
				throw new CraterException($"{Fmt.Pkg(id)} has no patch.");
			}

			Directory.Delete(directory, recursive: true);
			Out.Ok($"Removed {Fmt.File(shown)}. Run {Fmt.Cmd("crater install")} to get the original package back.");
			return;
		}

		if (package.Locked.Source is not null)
		{
			throw new CraterException($"{Fmt.Pkg(id)} comes from {Fmt.Val(package.Locked.Source)}. Change it there instead of patching it.");
		}

		string original = Path.Combine(Path.GetTempPath(), $"crater-patch-{Guid.NewGuid():N}");

		try
		{
			byte[] archive;

			using (new Spinner($"Getting the original {id}"))
			{
				archive = await OriginalArchiveAsync(project, package.Locked);
			}

			Packer.Extract(archive, original, id);
			PackageFiles.Prepare(id, original, Path.GetFileName(package.Directory), PackageFiles.ManifestIn(original), quiet: true);

			PatchChanges changes = Patches.Compare(original, package.Directory);

			if (changes.IsEmpty)
			{
				if (Directory.Exists(directory))
				{
					Directory.Delete(directory, recursive: true);
					Out.Ok($"{Fmt.Pkg(id)} is the same as the original again, removed {Fmt.File(shown)}");
					return;
				}

				string folder = Path.GetRelativePath(project.Directory, package.Directory).Replace('\\', '/');

				Out.Info($"No changes in {Fmt.File(folder)}. Edit files there first, then run {Fmt.Cmd($"crater patch {args.First}")} again.");
				return;
			}

			Patches.Write(directory, package.Directory, changes);

			foreach (string file in changes.Changed)
			{
				Out.Dim($"  changed {file}");
			}

			foreach (string file in changes.Deleted)
			{
				Out.Dim($"  deleted {file}");
			}

			Out.Ok($"Saved your changes to {Fmt.Pkg(id)} in {Fmt.File(shown)}. Commit that folder; {Fmt.Cmd("crater install")} applies it everywhere.");
		}
		finally
		{
			if (Directory.Exists(original))
			{
				Directory.Delete(original, recursive: true);
			}
		}
	}

	static void List(Project project)
	{
		string root = Patches.Root(project);
		List<string> patches = Directory.Exists(root) ? Directory.EnumerateDirectories(root).Select(Path.GetFileName).OfType<string>().Order().ToList() : [];

		if (patches.Count == 0)
		{
			Out.Info($"No patches. Edit a package in your package folder and run {Fmt.Cmd("crater patch <name>")} to keep the change.");
			return;
		}

		HashSet<string> installed = LockFile.Load(project.LockPath).Packages.Select(package => package.Id.Replace('/', '_')).ToHashSet();

		foreach (string patch in patches)
		{
			string state = installed.Contains(patch) ? Fmt.Good("applied") : Fmt.Bad("not installed");

			Out.Info($"  {Fmt.File($"{Patches.FolderName}/{patch}")}  {state}");
		}
	}

	static async Task<byte[]> OriginalArchiveAsync(Project project, LockedPackage locked)
	{
		string cached = Path.Combine(PackageCache.Root, locked.Sha256.ToLowerInvariant() + ".zip");

		if (locked.Sha256.Length == 64 && File.Exists(cached))
		{
			return await File.ReadAllBytesAsync(cached);
		}

		if (locked.Registry is null)
		{
			throw new CraterException($"crater does not know where {Fmt.Pkg(locked.Id)} came from. Run {Fmt.Cmd("crater install")} first.");
		}

		IRegistry registry = Registries.Open(locked.Registry, project.Directory);

		await registry.GetIndexAsync(locked.Name);

		return await registry.DownloadAsync(locked.Name, locked.Version);
	}
}
