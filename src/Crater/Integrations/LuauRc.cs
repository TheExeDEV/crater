using System.Text.Json;
using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Integrations;

public static class LuauRc
{
	public const string FileName = ".luaurc";

	public static string RelativePath(Project project)
	{
		if (FromVsCode(project) is string existing)
		{
			return existing;
		}

		string configured = Clean(project.Config.LuaurcPath);

		return configured.Length == 0 ? FileName : configured;
	}

	public static string? FromVsCode(Project project)
	{
		string settings = VsCodeSettings.PathIn(project);

		if (!File.Exists(settings))
		{
			return null;
		}

		try
		{
			string? value = Json.ReadObject(settings)[VsCodeSettings.BaseLuaurcKey]?.GetValue<string>();

			if (string.IsNullOrWhiteSpace(value))
			{
				return null;
			}

			string path = Clean(value.Replace("${workspaceFolder}", "").Replace("${workspaceRoot}", ""));

			if (Path.IsPathRooted(path))
			{
				string relative = Path.GetRelativePath(project.Directory, path).Replace('\\', '/');

				return relative.StartsWith("..") ? path : relative;
			}

			return path.Length == 0 ? null : path;
		}
		catch (Exception exception) when (exception is CraterException or InvalidOperationException or JsonException)
		{
			return null;
		}
	}

	static string Clean(string path)
	{
		string cleaned = path.Trim().Replace('\\', '/');

		if (cleaned.StartsWith('/') && cleaned.Length > 2 && cleaned[2] == ':')
		{
			cleaned = cleaned[1..];
		}

		while (cleaned.StartsWith("./"))
		{
			cleaned = cleaned[2..];
		}

		return cleaned;
	}

	public static string PathIn(Project project) => Path.GetFullPath(Path.Combine(project.Directory, RelativePath(project)));

	public static bool IsCustom(Project project) => !RelativePath(project).Equals(FileName, StringComparison.Ordinal);

	public static string AliasValue(Project project, string folder)
	{
		string luaurcDirectory = Path.GetDirectoryName(PathIn(project))!;
		string relative = Path.GetRelativePath(luaurcDirectory, project.Abs(folder)).Replace('\\', '/');

		return relative.StartsWith("..") ? relative : "./" + relative;
	}

	public static void Update(Project project, IReadOnlyDictionary<Realm, ResolvedGraph> graphs, Prompter prompt)
	{
		string path = PathIn(project);
		string shown = RelativePath(project);
		string configured = Clean(project.Config.LuaurcPath);

		if (FromVsCode(project) is not null && configured.Length > 0 && configured != FileName && configured != shown)
		{
			Out.Dim($"  {VsCodeSettings.BaseLuaurcKey} in .vscode/settings.json points to {shown}, so the aliases go there instead of luaurc ({configured})");
		}
		JsonObject root = Json.ReadObject(path);
		string before = Json.Serialize(root);

		if (root["aliases"] is not JsonObject aliases)
		{
			aliases = new JsonObject();
			root["aliases"] = aliases;
		}

		foreach ((Realm realm, string folder) in project.Config.ConfiguredFolders())
		{
			string alias = project.Config.AliasFor(realm, folder);
			string value = AliasValue(project, folder);
			List<string> renamed = aliases
				.Where(pair => pair.Key != alias && pair.Value?.GetValueKind() == JsonValueKind.String && (string?)pair.Value == value)
				.Select(pair => pair.Key)
				.ToList();

			foreach (string old in renamed)
			{
				aliases.Remove(old);
			}

			bool installed = graphs.TryGetValue(realm, out ResolvedGraph? graph) && graph.All.Count > 0;

			if (installed)
			{
				aliases[alias] = value;
				continue;
			}

			bool pointsHere = aliases[alias]?.GetValueKind() == JsonValueKind.String && (string?)aliases[alias] == value;

			if (pointsHere)
			{
				aliases.Remove(alias);
			}
		}

		if (aliases.Count == 0)
		{
			root.Remove("aliases");
		}

		string after = Json.Serialize(root);
		bool nothingToWrite = after == before || (!File.Exists(path) && root.Count == 0);

		if (nothingToWrite)
		{
			return;
		}

		string action = File.Exists(path)
			? $"Update aliases in {Fmt.File(shown)}{CommentsNote(path)}"
			: $"Create {Fmt.File(shown)} with package folder aliases";

		if (prompt.Confirm(action))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			Json.WriteText(path, after);
		}
	}

	public static string AliasFor(string folder)
	{
		return folder.TrimEnd('/')[(folder.TrimEnd('/').LastIndexOf('/') + 1)..];
	}

	internal static string CommentsNote(string path)
	{
		if (!File.Exists(path))
		{
			return "";
		}

		string text = File.ReadAllText(path);
		bool hasComments = text.Contains("//") || text.Contains("/*");

		return hasComments ? " (comments in the file will be lost)" : "";
	}
}
