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

		Dictionary<ResolvedPackage, List<TypeExport>> types = ExtractPackages(graph, archives, index);

		WriteLinks(graph, types, folder, index);
		ReportTypes(graph, types);
	}

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

	static Dictionary<ResolvedPackage, List<TypeExport>> ExtractPackages(
		ResolvedGraph graph,
		IReadOnlyDictionary<ResolvedPackage, byte[]> archives,
		string index)
	{
		Dictionary<ResolvedPackage, List<TypeExport>> types = [];

		foreach (ResolvedPackage package in graph.All)
		{
			string content = Path.Combine(index, package.IndexDirectory, package.ShortName);

			Directory.CreateDirectory(content);
			Packer.Extract(archives[package], content, package.Id);

			if (TypeExports.FindMainFile(content, package.Manifest.PathToMain) is null)
			{
				Out.Warn(
					$"{Fmt.Pkg(package.Id)}: main module not found (path_to_main = {Fmt.File(package.Manifest.PathToMain)}), " +
					"require may fail");
			}

			types[package] = TypeExports.FromPackage(content, package.Manifest.PathToMain);
		}

		return types;
	}

	void WriteLinks(ResolvedGraph graph, Dictionary<ResolvedPackage, List<TypeExport>> types, string folder, string index)
	{
		string style = project.Config.LinkStyle;

		foreach (ResolvedPackage package in graph.All)
		{
			foreach (ResolvedDependency edge in package.Dependencies)
			{
				string path = Path.Combine(index, package.IndexDirectory, edge.LinkName + ".luau");

				File.WriteAllText(path, Links.Nested(edge.Package, style, types[edge.Package]));
			}
		}

		foreach (ResolvedDependency edge in graph.Roots)
		{
			string path = Path.Combine(folder, edge.LinkName + ".luau");

			File.WriteAllText(path, Links.TopLevel(edge.Package, style, types[edge.Package]));
		}
	}

	static void ReportTypes(ResolvedGraph graph, Dictionary<ResolvedPackage, List<TypeExport>> types)
	{
		foreach (ResolvedPackage package in graph.All)
		{
			foreach (TypeExport type in types[package].Where(type => type.Approximated))
			{
				Out.Dim($"  {package.Id}: default of type {type.Name} uses names not visible outside the module, re-exported as any");
			}
		}
	}
}
