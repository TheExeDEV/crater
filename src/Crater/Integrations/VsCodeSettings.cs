using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Integrations;

public static class VsCodeSettings
{
	public const string Key = "luau-lsp.types.definitionFiles";

	public static string PathIn(Project project) => Path.Combine(project.Directory, ".vscode", "settings.json");

	public const string BaseLuaurcKey = "luau-lsp.server.baseLuaurc";

	public static void Update(Project project, IReadOnlyDictionary<Realm, ResolvedGraph> graphs, Prompter prompt)
	{
		string path = PathIn(project);
		bool writeDefinitions = project.Config.OnInstallUpdateVscodeLuauDefinitionFiles;
		bool writeBaseLuaurc = project.Config.OnInstallUpdateLuaurc && LuauRc.IsCustom(project) && LuauRc.FromVsCode(project) is null;
		List<(ResolvedPackage Package, string Name, string Path)> definitions = writeDefinitions ? Layout.DefinitionFiles(project, graphs).ToList() : [];

		if (!File.Exists(path) && definitions.Count == 0 && !writeBaseLuaurc)
		{
			return;
		}

		JsonObject root = Json.ReadObject(path);
		string before = Json.Serialize(root);

		if (writeBaseLuaurc)
		{
			root[BaseLuaurcKey] = LuauRc.RelativePath(project);
		}

		if (!writeDefinitions)
		{
			Save(path, root, before, prompt, $"Set {Fmt.Val(BaseLuaurcKey)} to {Fmt.File(LuauRc.RelativePath(project))}");
			return;
		}
		List<string> prefixes = Layout.ManagedPrefixes(project).ToList();

		bool IsManaged(JsonNode? node)
		{
			return node is JsonValue value &&
				value.TryGetValue(out string? text) &&
				prefixes.Any(prefix => text.StartsWith(prefix));
		}

		(bool luauLsp, bool lunaut) = Targets(project, root);
		JsonObject map = AsObject(root[Key]);

		foreach (string key in map.Where(pair => IsManaged(pair.Value)).Select(pair => pair.Key).ToList())
		{
			map.Remove(key);
		}

		JsonObject own = map.DeepClone().AsObject();
		List<LunautEntry> entries = [];

		foreach ((ResolvedPackage package, string name, string file) in definitions)
		{
			map[UniqueKey(map, package, name, file)] = file;
			entries.Add(new LunautEntry(name, file, package.Manifest.DefinitionPriority, package.Manifest.DefinitionShowAsModule == true));
		}

		if (lunaut)
		{
			UpdateLunaut(root, own, entries, IsManaged);
		}

		JsonObject written = luauLsp ? map : own;

		if (written.Count > 0 && (luauLsp || root[Key] is not null))
		{
			root[Key] = written;
		}
		else
		{
			root.Remove(Key);
		}

		string count = Fmt.Plural(definitions.Count, "file", "files");
		string keys = string.Join(" and ", new[] { luauLsp ? Key : null, lunaut ? LunautKey : null }.OfType<string>().Select(key => Fmt.Val(key)));
		string extra = writeBaseLuaurc ? $" and {Fmt.Val(BaseLuaurcKey)}" : "";

		Save(path, root, before, prompt, $"Update {keys} ({count}){extra}");
	}

	public const string LunautKey = "lunaut.definitions";

	static (bool LuauLsp, bool Lunaut) Targets(Project project, JsonObject root)
	{
		return project.Config.LanguageServer switch
		{
			"luau-lsp" => (true, false),
			"lunaut" => (false, true),
			"both" => (true, true),
			_ => (true, UsesLunaut(root)),
		};
	}

	static bool UsesLunaut(JsonObject root)
	{
		return root.Any(pair => pair.Key == "lunaut" || pair.Key.StartsWith("lunaut.", StringComparison.Ordinal)) || Shell.Exists("lunaut");
	}

	sealed record LunautEntry(string Package, string File, int? Priority, bool ShowAsModule);

	static void UpdateLunaut(JsonObject root, JsonObject luauLspMap, List<LunautEntry> entries, Func<JsonNode?, bool> isManaged)
	{
		JsonArray list = root[LunautKey] switch
		{
			JsonArray existing => existing,
			JsonObject named => new JsonArray(named.Select(pair => (JsonNode?)new JsonObject { ["name"] = pair.Key, ["path"] = pair.Value?.DeepClone() }).ToArray()),
			_ => new JsonArray(luauLspMap.Select(pair => (JsonNode?)new JsonObject { ["name"] = pair.Key, ["path"] = pair.Value?.DeepClone() }).ToArray()),
		};

		foreach (JsonNode? item in list.Where(item => isManaged(item is JsonObject entry ? entry["path"] : item)).ToList())
		{
			list.Remove(item);
		}

		HashSet<string> taken = list.OfType<JsonObject>()
			.Select(item => item["name"] is JsonValue value && value.TryGetValue(out string? name) ? name : "")
			.Append("@roblox")
			.ToHashSet(StringComparer.Ordinal);
		HashSet<string> several = entries.GroupBy(entry => entry.Package).Where(group => group.Count() > 1).Select(group => group.Key).ToHashSet();

		foreach (LunautEntry item in entries.OrderByDescending(entry => entry.Priority ?? 0).ThenBy(entry => entry.Package, StringComparer.Ordinal).ThenBy(entry => entry.File, StringComparer.Ordinal))
		{
			string module = Identifier(several.Contains(item.Package) ? $"{item.Package}_{Stem(item.File)}" : item.Package);
			string name = FreeName(taken, "@" + module);
			JsonObject entry = new() { ["path"] = item.File, ["name"] = name };

			taken.Add(name);

			if (item.Priority is int value)
			{
				entry["priority"] = value;
			}

			if (item.ShowAsModule)
			{
				entry["show_as_module"] = true;
			}

			list.Add(entry);
		}

		if (list.Count == 0)
		{
			root.Remove(LunautKey);
			return;
		}

		root[LunautKey] = list;
	}

	static string Identifier(string text)
	{
		string result = new(text.Select(character => char.IsAsciiLetterOrDigit(character) || character == '_' ? character : '_').ToArray());

		return result.Length == 0 || char.IsAsciiDigit(result[0]) ? "_" + result : result;
	}

	static string FreeName(HashSet<string> taken, string name)
	{
		string candidate = name;

		for (int index = 2; taken.Contains(candidate); index++)
		{
			candidate = $"{name}_{index}";
		}

		return candidate;
	}

	static void Save(string path, JsonObject root, string before, Prompter prompt, string action)
	{
		string after = Json.Serialize(root);

		if (after == before)
		{
			return;
		}

		if (prompt.Confirm($"{action} in {Fmt.File(".vscode/settings.json")}{LuauRc.CommentsNote(path)}"))
		{
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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

	static string UniqueKey(JsonObject map, ResolvedPackage package, string name, string file)
	{
		string key = $"@{name}@{package.Version}";

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

