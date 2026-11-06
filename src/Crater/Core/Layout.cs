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

	public static void IgnoreVendor(Project project, Prompter prompt)
	{
		string path = Path.Combine(project.Directory, ".gitignore");

		if (!File.Exists(path))
		{
			return;
		}

		string entry = project.Config.Vendor + "/";
		string text = File.ReadAllText(path);
		bool listed = text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim().TrimStart('/')).Any(line => line == entry || line == project.Config.Vendor);

		if (!listed && prompt.Confirm($"Keep vendored packages out of git ({entry} in .gitignore, {Fmt.File(LockFile.FileName)} pins them)"))
		{
			File.WriteAllText(path, text.TrimEnd('\r', '\n') + "\n" + entry + "\n");
		}
	}
}
