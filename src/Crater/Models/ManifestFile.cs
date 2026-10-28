using System.Text;
using System.Text.Json.Nodes;
using Crater.Util;

namespace Crater.Models;

public static class ManifestFile
{
	const int InlineWidth = 90;
	const string Indent = "  ";

	public const string LabelPrefix = "//";

	static readonly string[][] Groups =
	[
		["$schema"],
		["name", "version", "description", "authors", "license", "keywords", "repository"],
		["registry", "registries", "can_publish", "import_to"],
		["path_to_main", "icon", "path_to_definition_files", "path_to_definition_file", "package_files", "exclude", "allow_files"],
		["import_client", "import_server", "import_develop", "overrides"],
		["workspace", "scripts", "tools", "package_scripts", "block_package_scripts", "allow_scripts_from"],
		["monorepository-elements", "interface", "realized_interfaces"],
	];

	public static bool IsLabel(string key) => key.StartsWith(LabelPrefix, StringComparison.Ordinal);

	public static void Write(string path, JsonObject manifest)
	{
		Json.WriteText(path, Format(manifest));
	}

	public static string Format(JsonObject manifest)
	{
		HashSet<string> known = Groups.SelectMany(group => group).ToHashSet(StringComparer.Ordinal);
		List<string> others = manifest.Select(pair => pair.Key).Where(key => !known.Contains(key) && !IsLabel(key)).ToList();
		List<List<string>> groups = Groups
			.Select(group => group.Where(manifest.ContainsKey).ToList())
			.Append(others)
			.Where(keys => keys.Count > 0)
			.ToList();

		StringBuilder text = new("{\n");

		for (int g = 0; g < groups.Count; g++)
		{
			List<string> keys = groups[g];

			for (int k = 0; k < keys.Count; k++)
			{
				bool last = g == groups.Count - 1 && k == keys.Count - 1;

				text.Append(Indent).Append(Quote(keys[k])).Append(": ").Append(Value(manifest[keys[k]], Indent)).Append(last ? "\n" : ",\n");
			}

			if (g < groups.Count - 1)
			{
				text.Append('\n');
			}
		}

		return text.Append('}').ToString();
	}

	static string Value(JsonNode? node, string indent)
	{
		string inline = Inline(node);

		if (inline.Length + indent.Length <= InlineWidth || node is not (JsonArray or JsonObject))
		{
			return inline;
		}

		string inner = indent + Indent;
		IEnumerable<string> lines = node switch
		{
			JsonArray array => array.Select(item => inner + Value(item, inner)),
			JsonObject obj => obj.Select(pair => $"{inner}{Quote(pair.Key)}: {Value(pair.Value, inner)}"),
			_ => [],
		};

		(string open, string close) = node is JsonArray ? ("[", "]") : ("{", "}");

		return $"{open}\n{string.Join(",\n", lines)}\n{indent}{close}";
	}

	static string Inline(JsonNode? node)
	{
		return node switch
		{
			null => "null",
			JsonArray { Count: 0 } => "[]",
			JsonObject { Count: 0 } => "{}",
			JsonArray array => "[" + string.Join(", ", array.Select(Inline)) + "]",
			JsonObject obj => "{ " + string.Join(", ", obj.Select(pair => $"{Quote(pair.Key)}: {Inline(pair.Value)}")) + " }",
			_ => node.ToJsonString(Json.Options),
		};
	}

	static string Quote(string key) => JsonValue.Create(key).ToJsonString(Json.Options);
}
