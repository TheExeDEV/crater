using System.Text;
using System.Text.Json.Nodes;
using Crater.Util;

namespace Crater.Models;

public static class ManifestFile
{
	const int InlineWidth = 90;
	const string Indent = "  ";

	public const string LabelPrefix = "//";

	sealed record Group(string? Label, string Note, string[] Keys);

	static readonly Group[] Groups =
	[
		new(null, "", ["$schema"]),
		new("// package", "what this package is", ["name", "version", "description", "authors", "license", "keywords", "repository"]),
		new("// registries", "where packages come from and where this one is published", ["registry", "registries", "can_publish", "import_to"]),
		new("// files", "the entry module and what goes into the published package", ["path_to_main", "icon", "path_to_definition_files", "path_to_definition_file", "package_files", "exclude", "allow_files"]),
		new("// dependencies", "packages this project uses", ["import_client", "import_server", "import_develop", "overrides"]),
		new("// project", "workspace, scripts, pinned tools and packages that may run scripts", ["workspace", "scripts", "tools", "package_scripts", "block_package_scripts", "allow_scripts_from"]),
		new("// kind", "monorepository packages and interfaces", ["monorepository-elements", "interface", "realized_interfaces"]),
	];

	public static bool IsLabel(string key) => key.StartsWith(LabelPrefix, StringComparison.Ordinal);

	public static void Write(string path, JsonObject manifest)
	{
		Json.WriteText(path, Format(manifest));
	}

	public static string Format(JsonObject manifest)
	{
		HashSet<string> known = Groups.SelectMany(group => group.Keys).ToHashSet(StringComparer.Ordinal);
		List<string> others = manifest.Select(pair => pair.Key).Where(key => !known.Contains(key) && !IsLabel(key)).ToList();
		List<(Group Group, List<string> Keys)> groups = Groups
			.Select(group => (group, group.Keys.Where(manifest.ContainsKey).ToList()))
			.Append((new Group(null, "", []), others))
			.Where(item => item.Item2.Count > 0)
			.ToList();

		List<List<string>> lines = [];

		foreach ((Group group, List<string> keys) in groups)
		{
			List<string> block = [];

			if (group.Label is not null)
			{
				block.Add($"{Indent}{Quote(group.Label)}: {Quote(group.Note)}");
			}

			block.AddRange(keys.Select(key => $"{Indent}{Quote(key)}: {Value(manifest[key], Indent)}"));
			lines.Add(block);
		}

		StringBuilder text = new("{\n");

		for (int g = 0; g < lines.Count; g++)
		{
			for (int l = 0; l < lines[g].Count; l++)
			{
				bool last = g == lines.Count - 1 && l == lines[g].Count - 1;

				text.Append(lines[g][l]).Append(last ? "\n" : ",\n");
			}

			if (g < lines.Count - 1)
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
