using Crater.Models;

namespace Crater.Core;

public static class Layout
{
	public const string IndexFolder = "_Index";

	public static string TreeIndent(int depth)
	{
		return depth == 0 ? "  " : new string(' ', depth * 2) + "└ ";
	}

	public static IEnumerable<(ResolvedPackage Package, string Path)> DefinitionFiles(
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

			foreach (ResolvedPackage package in graph.All)
			{
				string content = $"{folder}/{IndexFolder}/{package.IndexDirectory}/{package.ShortName}";

				foreach (string definition in DefinitionsOf(project, package, content))
				{
					yield return (package, $"{content}/{definition}");
				}
			}
		}
	}

	static IEnumerable<string> DefinitionsOf(Project project, ResolvedPackage package, string content)
	{
		string directory = project.Abs(content);
		List<string> patterns = package.Manifest.DefinitionPatterns;

		return patterns.Count > 0
			? DefinitionLookup.Resolve(directory, patterns)
			: DefinitionLookup.AutoDetect(directory);
	}

	public static IEnumerable<string> ManagedPrefixes(Project project)
	{
		return project.Config.ConfiguredFolders().Select(pair => $"{pair.Folder}/{IndexFolder}/");
	}
}
