using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Integrations;

public static class VsCodeSettings
{
	public const string Key = "luau-lsp.types.definitionFiles";

	public static string PathIn(Project project) => Path.Combine(project.Directory, ".vscode", "settings.json");

	public static void Update(Project project, IReadOnlyDictionary<Realm, ResolvedGraph> graphs, Prompter prompt)
	{
		string path = PathIn(project);
		List<(ResolvedPackage Package, string Path)> definitions = Layout.DefinitionFiles(project, graphs).ToList();

		if (!File.Exists(path) && definitions.Count == 0)
		{
			return;
		}

		JsonObject root = Json.ReadObject(path);
		string before = Json.Serialize(root);
		List<string> prefixes = Layout.ManagedPrefixes(project).ToList();

		bool IsManaged(JsonNode? node)
		{
			return node is JsonValue value &&
				value.TryGetValue(out string? text) &&
				prefixes.Any(prefix => text.StartsWith(prefix));
		}

		JsonObject map = AsObject(root[Key]);

		foreach (string key in map.Where(pair => IsManaged(pair.Value)).Select(pair => pair.Key).ToList())
		{
			map.Remove(key);
		}

		foreach ((ResolvedPackage package, string file) in definitions)
		{
			map[UniqueKey(map, package, file)] = file;
		}

		root[Key] = map;

		if (map.Count == 0)
		{
			root.Remove(Key);
		}

		string after = Json.Serialize(root);

		if (after == before)
		{
			return;
		}

		string shown = Fmt.File(".vscode/settings.json");
		string count = Fmt.Plural(definitions.Count, "file", "files");

		if (prompt.Confirm($"Update {Fmt.Val(Key)} in {shown} ({count}){LuauRc.CommentsNote(path)}"))
		{
			Json.WriteText(path, after);
		}
	}

	static JsonObject AsObject(JsonNode? node)
	{
		if (node is JsonObject existing)
		{
			return existing;
		}

		JsonObject map = [];

		if (node is JsonArray list)
		{
			foreach (string file in list.OfType<JsonValue>().Select(value => value.ToString()))
			{
				map[FreeKey(map, "@" + Stem(file))] = file;
			}
		}

		return map;
	}

	static string UniqueKey(JsonObject map, ResolvedPackage package, string file)
	{
		string name = package.Name[(package.Name.IndexOf('/') + 1)..].Replace('-', '_');
		string key = "@" + name;

		if (map.ContainsKey(key))
		{
			key = "@" + package.Name.Replace('/', '_').Replace('-', '_');
		}

		if (map.ContainsKey(key))
		{
			key = $"{key}_{Stem(file)}";
		}

		return FreeKey(map, key);
	}

	static string FreeKey(JsonObject map, string key)
	{
		string candidate = key;

		for (int index = 2; map.ContainsKey(candidate); index++)
		{
			candidate = $"{key}_{index}";
		}

		return candidate;
	}

	static string Stem(string file)
	{
		string name = Path.GetFileName(file);
		int dot = name.IndexOf('.');

		return (dot > 0 ? name[..dot] : name).Replace('-', '_');
	}
}

