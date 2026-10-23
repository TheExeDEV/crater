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
				if (string.IsNullOrWhiteSpace(package.Manifest.PathToDefinitionFile))
				{
					continue;
				}

				string definition = Links.NormalizeRelative(package.Manifest.PathToDefinitionFile);

				yield return (package, $"{folder}/{IndexFolder}/{package.IndexDirectory}/{package.ShortName}/{definition}");
			}
		}
	}

	public static IEnumerable<string> ManagedPrefixes(Project project)
	{
		return project.Config.ConfiguredFolders().Select(pair => $"{pair.Folder}/{IndexFolder}/");
	}
}
