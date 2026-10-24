using System.Text.Json.Nodes;
using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public sealed class FolderBuilder(Project project, Prompter prompt)
{
	static readonly string[] InitFiles = ["init.luau", "init.lua"];

	public bool IsUpToDate(ResolvedGraph graph, LockFile oldLock)
	{
		List<LockedPackage> current = graph.All
			.Select(package => LockedPackage.From(graph.Realm, graph, package, project.Directory))
			.ToList();

		List<LockedPackage> previous = oldLock.For(graph.Realm).ToList();

		if (Json.Serialize(current) != Json.Serialize(previous))
		{
			return false;
		}

		string? folderName = project.Config.TryFolderFor(graph.Realm);

		if (folderName is null)
		{
			return graph.All.Count == 0;
		}

		string folder = project.Abs(folderName);

		if (graph.All.Count == 0)
		{
			return !PackageLayout.IsManaged(folder);
		}

		if (!File.Exists(Path.Combine(folder, PackageLayout.MarkerFile)))
		{
			return false;
		}

		string patches = Patches.Fingerprint(project, graph.All);

		if (PackageLayout.ReadMarker(folder).GetValueOrDefault(Patches.MarkerKey, "") != patches)
		{
			return false;
		}

		PackageLayout layout = PackageLayout.Plan(graph, folderName);

		bool packagesPresent = layout.Folders.Values.All(name => Directory.Exists(Path.Combine(folder, name)));
		bool aliasesPresent = layout.Aliases.Keys.All(name => File.Exists(Path.Combine(folder, name + ".luau")));

		return packagesPresent && aliasesPresent;
	}

	public const string StyleKey = "#style";

	public HashSet<ResolvedPackage> PackagesToExtract(ResolvedGraph graph)
	{
		string? folderName = project.Config.TryFolderFor(graph.Realm);

		if (folderName is null || graph.All.Count == 0)
		{
			return [.. graph.All];
		}

		string folder = project.Abs(folderName);
		PackageLayout layout = PackageLayout.Plan(graph, folderName);
		Dictionary<string, string> previous = PackageLayout.ReadMarker(folder);
		bool hadIsolated = previous.Values.Any(value => value.StartsWith(PackageLayout.IsolatedFolder + "/", StringComparison.Ordinal));
		bool reusable = File.Exists(Path.Combine(folder, PackageLayout.MarkerFile)) &&
			!layout.HasIsolated && !hadIsolated &&
			!Directory.Exists(Path.Combine(folder, Layout.LegacyIndexFolder)) &&
			previous.GetValueOrDefault(StyleKey) == project.Config.LinkStyle &&
			previous.GetValueOrDefault(Patches.MarkerKey, "") == Patches.Fingerprint(project, graph.All);

		if (!reusable)
		{
			return [.. graph.All];
		}

		return graph.All
			.Where(package => previous.GetValueOrDefault(package.Id) != layout.Folders[package] || !Directory.Exists(Path.Combine(folder, layout.Folders[package])))
			.ToHashSet();
	}

	public void Build(ResolvedGraph graph, IReadOnlyDictionary<ResolvedPackage, string> prepared)
	{
		if (graph.All.Count == 0)
		{
			RemoveEmpty(graph.Realm);
			return;
		}

		string folderName = project.Config.FolderFor(graph.Realm);
		string folder = project.Abs(folderName);
		PackageLayout layout = PackageLayout.Plan(graph, folderName);

		EnsureManaged(graph.Realm, folderName, folder);

		HashSet<ResolvedPackage> changed = PackagesToExtract(graph);
		bool full = changed.Count == graph.All.Count;
		HashSet<string> keptFolders = graph.All.Where(package => !changed.Contains(package)).Select(package => layout.Folders[package]).ToHashSet(OperatingSystem.IsLinux() ? StringComparer.Ordinal : StringComparer.OrdinalIgnoreCase);
		int removed = PackageLayout.ReadMarker(folder).Keys.Count(key => !key.StartsWith('#') && graph.All.All(package => package.Id != key));

		string action = !Directory.Exists(folder) ? "Create" : full ? "Rebuild" : "Update";
		string list = string.Join(", ", changed.Select(package => Fmt.Pkg(package.Id)));
		string summary = full
			? $"{Fmt.Plural(graph.All.Count, "package", "packages")}: {list}"
			: $"{(changed.Count > 0 ? $"add {list}, " : "")}{(removed > 0 ? $"remove {removed}, " : "")}keep {keptFolders.Count}";

		if (!prompt.Confirm($"{action} folder {Fmt.File(folderName)} ({summary})"))
		{
			throw new CraterException("Installation cancelled.");
		}

		if (full && Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}

		Directory.CreateDirectory(folder);

		foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
		{
			string name = Path.GetFileName(entry);

			if (name == PackageLayout.MarkerFile || keptFolders.Contains(name))
			{
				continue;
			}

			if (Directory.Exists(entry))
			{
				Directory.Delete(entry, recursive: true);
			}
			else
			{
				File.Delete(entry);
			}
		}

		Dictionary<ResolvedPackage, Extracted> extracted = ExtractPackages(graph, prepared, folder, layout, changed);

		WriteAliases(layout, extracted, folder);
		layout.WriteMarker(folder, Patches.Fingerprint(project, graph.All), project.Config.LinkStyle);
		Report(graph.All.Where(changed.Contains), extracted, layout, folder);
	}


	sealed record Extracted(PackageEntry Entry, List<TypeExport> Types);

	void RemoveEmpty(Realm realm)
	{
		string? folderName = project.Config.TryFolderFor(realm);

		if (folderName is null)
		{
			return;
		}

		string folder = project.Abs(folderName);

		if (!PackageLayout.IsManaged(folder))
		{
			return;
		}

		if (prompt.Confirm($"Delete folder {Fmt.File(folderName)} ({realm.ManifestField()} is now empty)"))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	static void EnsureManaged(Realm realm, string folderName, string folder)
	{
		if (!Directory.Exists(folder))
		{
			return;
		}

		bool empty = !Directory.EnumerateFileSystemEntries(folder).Any();

		if (PackageLayout.IsManaged(folder) || empty)
		{
			return;
		}

		throw new CraterException(
			$"Folder {Fmt.File(folderName)} exists, is not empty and was not created by crater. " +
			$"Rename it or set a different {ProjectConfig.FolderField(realm)} in {Fmt.File(ProjectConfig.FileName)}.");
	}

	Dictionary<ResolvedPackage, Extracted> ExtractPackages(
		ResolvedGraph graph,
		IReadOnlyDictionary<ResolvedPackage, string> prepared,
		string folder,
		PackageLayout layout,
		HashSet<ResolvedPackage> changed)
	{
		Dictionary<ResolvedPackage, Extracted> extracted = [];
		string style = project.Config.LinkStyle;

		foreach (ResolvedPackage package in graph.All)
		{
			string content = Path.Combine(folder, layout.Folders[package]);

			if (!changed.Contains(package))
			{
				PackageEntry kept = PackageEntry.Analyze(content, package.Manifest.PathToMain, package.Name);

				extracted[package] = new Extracted(kept, kept.MainFile is null ? [] : TypeExports.Parse(File.ReadAllText(kept.MainFile)));
				continue;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(content)!);
			Directory.Move(prepared[package], content);
			PackageFiles.NameRojoProject(content, Path.GetFileName(layout.Folders[package]));
			Patches.Apply(project, package, content);

			PackageEntry entry = PackageEntry.Analyze(content, package.Manifest.PathToMain, package.Name);
			List<TypeExport> types = entry.MainFile is null ? [] : TypeExports.Parse(File.ReadAllText(entry.MainFile));

			if (Links.NeedsInit(entry) && !PackageEntry.HasRojoProject(content))
			{
				WriteInit(package, content, Links.PackageInit(package, style, entry, types));
			}

			extracted[package] = new Extracted(entry, types);
		}

		return extracted;
	}


	static void WriteInit(ResolvedPackage package, string content, string text)
	{
		string? existing = InitFiles.FirstOrDefault(name => File.Exists(Path.Combine(content, name)));

		if (existing is not null)
		{
			Out.Warn($"{Fmt.Pkg(package.Id)} has its own {existing} that is not its main module, require uses that file");
			return;
		}

		File.WriteAllText(Path.Combine(content, "init.luau"), text);
	}

	void WriteAliases(PackageLayout layout, Dictionary<ResolvedPackage, Extracted> extracted, string folder)
	{
		string style = project.Config.LinkStyle;

		foreach ((string alias, ResolvedPackage package) in layout.Aliases)
		{
			Extracted target = extracted[package];
			List<TypeExport> types = target.Entry.Kind == EntryKind.Module ? target.Types : [];

			File.WriteAllText(Path.Combine(folder, alias + ".luau"), Links.Alias(layout.Folders[package], style, types));
		}

		foreach ((ResolvedPackage owner, Dictionary<string, ResolvedPackage> links) in layout.PrivateLinks)
		{
			string top = layout.Aliases.First(pair => pair.Value == owner).Key;
			string directory = Path.Combine(folder, PackageLayout.IsolatedFolder, top);

			Directory.CreateDirectory(directory);

			foreach ((string link, ResolvedPackage package) in links)
			{
				Extracted target = extracted[package];
				List<TypeExport> types = target.Entry.Kind == EntryKind.Module ? target.Types : [];

				File.WriteAllText(Path.Combine(directory, link + ".luau"), Links.PrivateLink(layout.Folders[package], style, types));
			}

			Out.Dim($"  {owner.Id}: its dependencies clash with other packages by name, so it gets its own links in {PackageLayout.IsolatedFolder}/{top}");
		}
	}

	static void Report(IEnumerable<ResolvedPackage> packages, Dictionary<ResolvedPackage, Extracted> extracted, PackageLayout layout, string folder)
	{
		foreach (ResolvedPackage package in packages)
		{
			Extracted item = extracted[package];

			if (item.Entry.Kind == EntryKind.Missing)
			{
				string name = layout.Folders[package];
				string? example = ExampleModule(Path.Combine(folder, name));
				string hint = example is null
					? "it has no Luau modules"
					: $"require its modules by path, for example {Fmt.Cmd($"require(Packages[\"{name}\"]{example})")}";

				Out.Info($"  {Fmt.Pkg(package.Id)} has no main module: {hint}");
			}

			foreach (TypeExport type in item.Types.Where(type => type.Approximated))
			{
				Out.Dim($"  {package.Id}: default of type {type.Name} uses names not visible outside the module, re-exported as any");
			}
		}
	}

	static string? ExampleModule(string content)
	{
		string? file = Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories)
			.Where(path => Path.GetExtension(path) is ".luau" or ".lua")
			.Where(path => !Path.GetFileNameWithoutExtension(path).Contains('.'))
			.Select(path => Path.GetRelativePath(content, path).Replace('\\', '/'))
			.OrderBy(path => path.Count(character => character == '/'))
			.ThenBy(path => path, StringComparer.Ordinal)
			.FirstOrDefault(path => path.Contains('/'));

		if (file is null)
		{
			return null;
		}

		List<string> segments = Links.MainSegments(file);

		return string.Concat(segments.Select(segment => $"[\"{segment}\"]"));
	}
}

