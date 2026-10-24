using System.Text.Json;
using Crater.Models;

namespace Crater.Core;

public static class ProjectSources
{
	public const string RojoProject = "default.project.json";

	static readonly string[] LuauExtensions = [".luau", ".lua"];

	public static List<string> Folders(Project project)
	{
		List<string> packageFolders = project.Config.ConfiguredFolders().Select(pair => pair.Folder.TrimEnd('/')).ToList();
		string projectFile = Path.Combine(project.Directory, RojoProject);
		List<string> paths = [];

		if (File.Exists(projectFile))
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(File.ReadAllText(projectFile));

				CollectPaths(document.RootElement, paths);
			}
			catch (JsonException)
			{
			}
		}

		List<string> sources = paths
			.Select(path => path.Replace('\\', '/').TrimEnd('/'))
			.Where(path => !packageFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
			.Where(path => Directory.Exists(Path.Combine(project.Directory, path)) || File.Exists(Path.Combine(project.Directory, path)))
			.Distinct()
			.ToList();

		if (sources.Count > 0)
		{
			return sources;
		}

		return Directory.Exists(Path.Combine(project.Directory, "src")) ? ["src"] : ["."];
	}

	public static List<string> LuauFiles(Project project, IEnumerable<string> targets)
	{
		HashSet<string> skipped = project.Config.ConfiguredFolders()
			.Select(pair => Path.GetFullPath(project.Abs(pair.Folder)))
			.Append(Path.GetFullPath(Patches.Root(project)))
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		List<string> files = [];

		foreach (string target in targets)
		{
			string full = Path.GetFullPath(Path.Combine(project.Directory, target));

			if (File.Exists(full))
			{
				files.Add(full);
				continue;
			}

			if (Directory.Exists(full))
			{
				Walk(full, skipped, files);
			}
		}

		return files.Distinct().OrderBy(file => file, StringComparer.Ordinal).ToList();
	}

	static void Walk(string directory, HashSet<string> skipped, List<string> files)
	{
		if (skipped.Contains(directory) || Path.GetFileName(directory).StartsWith('.'))
		{
			return;
		}

		files.AddRange(Directory.EnumerateFiles(directory).Where(file => LuauExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase)));

		foreach (string child in Directory.EnumerateDirectories(directory))
		{
			Walk(child, skipped, files);
		}
	}

	static void CollectPaths(JsonElement element, List<string> paths)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (property.Name == "$path" && property.Value.ValueKind == JsonValueKind.String)
			{
				paths.Add(property.Value.GetString()!);
			}
			else
			{
				CollectPaths(property.Value, paths);
			}
		}
	}
}
