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

		if (root[Key] is JsonObject map)
		{
			List<string> stale = map.Where(pair => IsManaged(pair.Value)).Select(pair => pair.Key).ToList();

			foreach (string key in stale)
			{
				map.Remove(key);
			}

			foreach ((ResolvedPackage package, string file) in definitions)
			{
				string key = map.ContainsKey(package.Name) ? package.Id : package.Name;
				map[key] = file;
			}
		}
		else
		{
			JsonArray list = root[Key] as JsonArray ?? [];

			for (int i = list.Count - 1; i >= 0; i--)
			{
				if (IsManaged(list[i]))
				{
					list.RemoveAt(i);
				}
			}

			foreach ((_, string file) in definitions)
			{
				list.Add(file);
			}

			root[Key] = list;
		}

		if (root[Key] is JsonArray { Count: 0 } or JsonObject { Count: 0 })
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
}
