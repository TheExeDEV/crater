using Crater.Models;
using Crater.Registry;
using Crater.Util;

namespace Crater.Core;

public sealed class Project
{
	public required string Directory { get; init; }
	public required Manifest Manifest { get; init; }
	public required ProjectConfig Config { get; init; }
	public Workspace? Workspace { get; init; }

	public string ManifestPath => Path.Combine(Directory, Manifest.FileName);
	public string LockPath => Path.Combine(Directory, LockFile.FileName);

	public static Project Load(string startDirectory)
	{
		DirectoryInfo? current = new(startDirectory);

		while (current is not null && !File.Exists(Path.Combine(current.FullName, Manifest.FileName)))
		{
			current = current.Parent;
		}

		if (current is null)
		{
			throw new CraterException(
				$"{Fmt.File(Manifest.FileName)} not found in {Fmt.File(startDirectory)} or any parent folder. " +
				$"Run {Fmt.Cmd("crater init")} to create a project.");
		}

		return LoadExact(current.FullName);
	}

	public static Project LoadExact(string directory)
	{
		string fullPath = Path.GetFullPath(directory);
		Manifest manifest = Manifest.Load(Path.Combine(fullPath, Manifest.FileName));

		manifest.ValidateCommon();

		return new Project
		{
			Directory = fullPath,
			Manifest = manifest,
			Config = ProjectConfig.Load(fullPath),
			Workspace = Workspace.Find(fullPath, manifest),
		};
	}

	public string? RegistryLocation
	{
		get
		{
			string[] candidates = [Manifest.RegistryLocation, BuildInfo.DefaultRegistry];

			return candidates.FirstOrDefault(candidate => !string.IsNullOrWhiteSpace(candidate));
		}
	}

	public IRegistry? OpenRegistry()
	{
		string? location = RegistryLocation;

		return location is null ? null : Registries.Open(location, Directory);
	}

	public IRegistry RequireRegistry()
	{
		IRegistry? registry = OpenRegistry();

		if (registry is null)
		{
			throw new CraterException(
				$"No registry configured. Set {Fmt.Val("registry")} in {Fmt.File(Manifest.FileName)}, " +
				$"for example {Fmt.Cmd("\"registry\": \"github:Owner/crater-registry\"")}.");
		}

		return registry;
	}

	public string Abs(string relative)
	{
		string full = Path.GetFullPath(Path.Combine(Directory, relative));
		string root = Path.GetFullPath(Directory).TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

		if (!full.StartsWith(root, StringComparison.OrdinalIgnoreCase))
		{
			throw new CraterException($"Path {Fmt.File(relative)} points outside the project.");
		}

		return full;
	}

	public string Relative(string absolute)
	{
		return Path.GetRelativePath(Directory, absolute).Replace('\\', '/');
	}
}
