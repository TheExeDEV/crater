using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public static class Layout
{
	public const string LegacyIndexFolder = "_Index";

	public static string TreeIndent(int depth)
	{
		return depth == 0 ? "  " : new string(' ', depth * 2) + "└ ";
	}

	public static IEnumerable<(ResolvedPackage Package, string Name, string Path)> DefinitionFiles(
		Project project,
		IReadOnlyDictionary<Realm, ResolvedGraph> graphs)
	{
		foreach (ResolvedGraph graph in graphs.Values)
		{
			if (graph.All.Count == 0)
			{
				continue;
			}

			string folder = project.Config.FolderFor(graph.Realm);
			PackageLayout layout = PackageLayout.Plan(graph, folder);

			foreach (ResolvedPackage package in graph.All)
			{
				string content = $"{folder}/{layout.Folders[package]}";

				foreach (string definition in DefinitionsOf(project, package, content))
				{
					yield return (package, Path.GetFileName(layout.Folders[package]), $"{content}/{definition}");
				}
			}
		}
	}

	static IEnumerable<string> DefinitionsOf(Project project, ResolvedPackage package, string content)
	{
		string directory = project.Abs(content);

		if (PackageMeta.Read(directory) is PackageMeta meta)
		{
			return meta.DefinitionFiles.Where(file => File.Exists(Path.Combine(directory, file)));
		}

		List<string> patterns = package.Manifest.DefinitionPatterns;

		return patterns.Count > 0
			? DefinitionLookup.Resolve(directory, patterns)
			: DefinitionLookup.AutoDetect(directory);
	}

	public static IEnumerable<string> ManagedPrefixes(Project project)
	{
		return project.Config.ConfiguredFolders().Select(pair => $"{pair.Folder}/");
	}

	public static void IgnoreVendor(Project project, List<string> folders, Prompter prompt)
	{
		string path = Path.Combine(project.Directory, ".gitignore");

		if (folders.Count == 0 || !File.Exists(path))
		{
			return;
		}

		string text = File.ReadAllText(path);
		HashSet<string> lines = text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim().Trim('/')).ToHashSet();
		List<string> missing = folders
			.Where(folder => !Ancestors(folder).Any(lines.Contains))
			.Select(folder => folder + "/")
			.ToList();

		if (missing.Count > 0 && prompt.Confirm($"Keep vendored packages out of git ({string.Join(", ", missing)} in .gitignore, {Fmt.File(LockFile.FileName)} pins them)"))
		{
			File.WriteAllText(path, text.TrimEnd('\r', '\n') + "\n" + string.Join("\n", missing) + "\n");
		}
	}

	static IEnumerable<string> Ancestors(string folder)
	{
		string[] segments = folder.Split('/');

		for (int count = 1; count <= segments.Length; count++)
		{
			yield return string.Join('/', segments[..count]);
		}
	}
}
