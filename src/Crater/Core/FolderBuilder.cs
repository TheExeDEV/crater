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

		PackageLayout layout = PackageLayout.Plan(graph, folderName);

		bool packagesPresent = layout.Folders.Values.All(name => Directory.Exists(Path.Combine(folder, name)));
		bool aliasesPresent = layout.Aliases.Keys.All(name => File.Exists(Path.Combine(folder, name + ".luau")));

		return packagesPresent && aliasesPresent;
	}

	public void Build(ResolvedGraph graph, IReadOnlyDictionary<ResolvedPackage, byte[]> archives)
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

		string verb = Directory.Exists(folder) ? "Rebuild" : "Create";
		string list = string.Join(", ", graph.All.Select(package => Fmt.Pkg(package.Id)));
		string count = Fmt.Plural(graph.All.Count, "package", "packages");

		if (!prompt.Confirm($"{verb} folder {Fmt.File(folderName)} ({count}: {list})"))
		{
			throw new CraterException("Installation cancelled.");
		}

		if (Directory.Exists(folder))
		{
			Directory.Delete(folder, recursive: true);
		}

		Directory.CreateDirectory(folder);

		Dictionary<ResolvedPackage, Extracted> extracted = ExtractPackages(graph, archives, folder, layout);

		WriteAliases(layout, extracted, folder);
		layout.WriteMarker(folder);
		Report(graph, extracted);
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
		IReadOnlyDictionary<ResolvedPackage, byte[]> archives,
		string folder,
		PackageLayout layout)
	{
		Dictionary<ResolvedPackage, Extracted> extracted = [];
		string style = project.Config.LinkStyle;

		foreach (ResolvedPackage package in graph.All)
		{
			string content = Path.Combine(folder, layout.Folders[package]);

			Directory.CreateDirectory(content);
			Packer.Extract(archives[package], content, package.Id);

			RenameRojoProject(content, layout.Folders[package]);

			PackageEntry entry = PackageEntry.Analyze(content, package.Manifest.PathToMain);
			List<TypeExport> types = entry.MainFile is null ? [] : TypeExports.Parse(File.ReadAllText(entry.MainFile));

			if (Links.NeedsInit(entry) && !PackageEntry.HasRojoProject(content))
			{
				WriteInit(package, content, Links.PackageInit(package, style, entry, types));
			}

			extracted[package] = new Extracted(entry, types);
		}

		return extracted;
	}

	static void RenameRojoProject(string content, string name)
	{
		string path = Path.Combine(content, PackageEntry.RojoProject);

		if (!File.Exists(path))
		{
			return;
		}

		try
		{
			JsonObject project = Json.ReadObject(path);

			project["name"] = name;
			Json.WriteText(path, Json.Serialize(project));
		}
		catch (Exception exception) when (exception is System.Text.Json.JsonException or CraterException)
		{
			Out.Warn($"{PackageEntry.RojoProject} in {Fmt.File(name)} could not be read, Rojo may name it differently");
		}
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
	}

	static void Report(ResolvedGraph graph, Dictionary<ResolvedPackage, Extracted> extracted)
	{
		foreach (ResolvedPackage package in graph.All)
		{
			Extracted item = extracted[package];

			if (item.Entry.Kind == EntryKind.Missing)
			{
				Out.Warn(
					$"{Fmt.Pkg(package.Id)} has no main module and no modules to expose " +
					$"(path_to_main = {Fmt.File(package.Manifest.PathToMain.Length == 0 ? "\"\"" : package.Manifest.PathToMain)}), require will fail");
			}

			foreach (TypeExport type in item.Types.Where(type => type.Approximated))
			{
				Out.Dim($"  {package.Id}: default of type {type.Name} uses names not visible outside the module, re-exported as any");
			}
		}
	}
}
