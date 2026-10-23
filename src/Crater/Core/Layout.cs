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
		if (!string.IsNullOrWhiteSpace(package.Manifest.PathToDefinitionFile))
		{
			return [Links.NormalizeRelative(package.Manifest.PathToDefinitionFile)];
		}

		string directory = project.Abs(content);

		if (!Directory.Exists(directory))
		{
			return [];
		}

		return Directory.EnumerateFiles(directory, "*.d.luau", SearchOption.AllDirectories)
			.Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
			.Where(path => !path.Split('/').Any(segment => segment.StartsWith('.') || segment == IndexFolder))
			.OrderBy(path => path, StringComparer.Ordinal)
			.Take(10)
			.ToList();
	}

	public static IEnumerable<string> ManagedPrefixes(Project project)
	{
		return project.Config.ConfiguredFolders().Select(pair => $"{pair.Folder}/{IndexFolder}/");
	}
}
