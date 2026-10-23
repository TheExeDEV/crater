using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public sealed class FolderBuilder(Project project, Prompter prompt)
{
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
			return !Directory.Exists(Path.Combine(folder, Layout.IndexFolder));
		}

		bool packagesPresent = graph.All.All(
			package => Directory.Exists(Path.Combine(folder, Layout.IndexFolder, package.IndexDirectory, package.ShortName)));

		bool linksPresent = graph.Roots.All(
			edge => File.Exists(Path.Combine(folder, edge.LinkName + ".luau")));

		return packagesPresent && linksPresent;
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

		string index = Path.Combine(folder, Layout.IndexFolder);
		Directory.CreateDirectory(index);

		Dictionary<ResolvedPackage, Extracted> extracted = ExtractPackages(graph, archives, index);

		WriteLinks(graph, extracted, folder, index);
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
		bool managed = Directory.Exists(Path.Combine(folder, Layout.IndexFolder));

		if (!managed)
		{
			return;
		}

		if (prompt.Confirm($"Delete folder {Fmt.File(folderName)} ({realm.ManifestField()} is now empty)"))
		{
			Directory.Delete(folder, recursive: true);
		}
	}

	void EnsureManaged(Realm realm, string folderName, string folder)
	{
		if (!Directory.Exists(folder))
		{
			return;
		}

		bool managed = Directory.Exists(Path.Combine(folder, Layout.IndexFolder));
		bool empty = !Directory.EnumerateFileSystemEntries(folder).Any();

		if (managed || empty)
		{
			return;
		}

		throw new CraterException(
			$"Folder {Fmt.File(folderName)} exists, is not empty and is not managed by crater (no {Layout.IndexFolder}). " +
			$"Rename it or set a different {ProjectConfig.FolderField(realm)} in {Fmt.File(ProjectConfig.FileName)}.");
	}

	static Dictionary<ResolvedPackage, Extracted> ExtractPackages(
		ResolvedGraph graph,
		IReadOnlyDictionary<ResolvedPackage, byte[]> archives,
		string index)
	{
		Dictionary<ResolvedPackage, Extracted> extracted = [];

		foreach (ResolvedPackage package in graph.All)
		{
			string content = Path.Combine(index, package.IndexDirectory, package.ShortName);

			Directory.CreateDirectory(content);
			Packer.Extract(archives[package], content, package.Id);

			PackageEntry entry = PackageEntry.Analyze(content, package.Manifest.PathToMain);
			List<TypeExport> types = entry.MainFile is null ? [] : TypeExports.Parse(File.ReadAllText(entry.MainFile));

			extracted[package] = new Extracted(entry, types);
		}

		return extracted;
	}

	void WriteLinks(ResolvedGraph graph, Dictionary<ResolvedPackage, Extracted> extracted, string folder, string index)
	{
		string style = project.Config.LinkStyle;

		foreach (ResolvedPackage package in graph.All)
		{
			foreach (ResolvedDependency edge in package.Dependencies)
			{
				Extracted target = extracted[edge.Package];
				string path = Path.Combine(index, package.IndexDirectory, edge.LinkName + ".luau");

				File.WriteAllText(path, Links.Nested(edge.Package, style, target.Entry, target.Types));
			}
		}

		foreach (ResolvedDependency edge in graph.Roots)
		{
			Extracted target = extracted[edge.Package];
			string path = Path.Combine(folder, edge.LinkName + ".luau");

			File.WriteAllText(path, Links.TopLevel(edge.Package, style, target.Entry, target.Types));
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
