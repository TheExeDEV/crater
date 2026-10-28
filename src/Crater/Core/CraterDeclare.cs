using System.Text.Json;
using System.Text.Json.Nodes;
using Crater.Commands;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Core;

public static class CraterDeclare
{
	public const string FileName = ".crater-declare";
	public const int CurrentApiVersion = 3;

	const string PackageInfoKey = "path_to_package_info";
	const string SettingsKey = "path_to_crater_settings";

	static readonly (string Name, int Version)[] Features =
	[
		("dependencies", 1),
		("tools", 1),
		("scripts", 1),
		("workspaces", 1),
		("interfaces", 1),
		("monorepository", 2),
		("settings", 2),
		("package_rules", 3),
		("registry_names", 3),
	];

	public static string PathIn(string directory) => Path.Combine(directory, FileName);

	public static JsonObject For(string directory)
	{
		string settings = File.Exists(ProjectConfig.PathIn(directory)) ? Path.GetFileName(ProjectConfig.PathIn(directory)) : ProjectConfig.FileName;

		return new JsonObject
		{
			[SettingsKey] = settings,
			[PackageInfoKey] = Manifest.FileName,
			["api_version"] = CurrentApiVersion,
			["features"] = Object(Features.Select(feature => (feature.Name, (JsonNode?)feature.Version))),
			["schemas"] = new JsonObject
			{
				["package_info"] = ProjectCommands.SchemaUrl("crater.schema.json"),
				["crater_settings"] = ProjectCommands.SchemaUrl("crater.settings.schema.json"),
			},
			["registries"] = Object(NamedRegistries.BuiltinNames.Select(name => (name, (JsonNode?)NamedRegistries.Location(name, null)))),
			["realms"] = Object(Realms.All.Select(realm => (realm.ManifestField(), (JsonNode?)realm.Key()))),
			["dependency"] = new JsonObject
			{
				["pattern"] = DependencySpec.Pattern,
				["operators"] = new JsonObject
				{
					[""] = "the newest version, the same as ^ alone",
					["^"] = "compatible: ^1.2.3 is >=1.2.3 <2.0.0, ^0.2.3 is >=0.2.3 <0.3.0",
					["~"] = "patch updates only: ~1.2.3 is >=1.2.3 <1.3.0",
					["="] = "exactly this version",
					["+"] = "this version or newer: +1.2.3 is >=1.2.3",
					["@"] = "@1.2.3 is ^1.2.3; @=1.2.3, @~1.2.3, @+1.2.3 and @^1.2.3 use that operator",
				},
				["sources"] = new JsonObject
				{
					["@path:"] = "a local folder, relative to the package info",
					["@git+"] = "a git repository; #ref after the URL picks a tag, branch or commit",
				},
				["registry"] = "a dependency without registry# comes from the registry field of the package info",
			},
			["defaults"] = Defaults(),
		};
	}

	static JsonObject Defaults()
	{
		JsonArray Empty() => [];

		return new JsonObject
		{
			["authors"] = Empty(),
			["description"] = "",
			["license"] = "",
			["keywords"] = Empty(),
			["repository"] = "",
			["registry"] = "crater",
			["registries"] = new JsonObject(),
			["can_publish"] = false,
			["import_to"] = "shared",
			["path_to_main"] = "",
			["icon"] = "",
			["path_to_definition_files"] = Empty(),
			["package_files"] = new JsonObject { ["exclude"] = Empty(), ["allow"] = Empty() },
			["import_client"] = Empty(),
			["import_server"] = Empty(),
			["import_develop"] = Empty(),
			["overrides"] = Empty(),
			["workspace"] = Empty(),
			["scripts"] = new JsonObject(),
			["tools"] = new JsonObject(),
			["package_scripts"] = new JsonObject { ["block"] = false, ["allow"] = Empty() },
			["monorepository-elements"] = new JsonObject(),
			["interface"] = false,
			["realized_interfaces"] = Empty(),
			["block_package_scripts"] = false,
			["allow_scripts_from"] = Empty(),
			["exclude"] = Empty(),
			["allow_files"] = Empty(),
		};
	}

	static JsonObject Object(IEnumerable<(string Key, JsonNode? Value)> pairs)
	{
		JsonObject result = new();

		foreach ((string key, JsonNode? value) in pairs)
		{
			result[key] = value;
		}

		return result;
	}

	public static void Write(string directory)
	{
		string path = PathIn(directory);

		if (File.Exists(path))
		{
			File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden & ~FileAttributes.ReadOnly);
		}

		Json.WriteText(path, Json.Serialize(For(directory)));

		if (OperatingSystem.IsWindows())
		{
			File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
		}
	}

	public static string? Problem(string directory)
	{
		string path = PathIn(directory);

		if (!File.Exists(path))
		{
			return $"{Fmt.File(FileName)} is missing";
		}

		JsonObject? declared;

		try
		{
			declared = JsonNode.Parse(File.ReadAllText(path)) as JsonObject;
		}
		catch (JsonException exception)
		{
			return $"{Fmt.File(FileName)} is not valid JSON ({exception.Message})";
		}

		if (declared is null)
		{
			return $"{Fmt.File(FileName)} is empty";
		}

		JsonObject expected = For(directory);

		foreach ((string key, string label) in new[] { (PackageInfoKey, "package info"), (SettingsKey, "settings") })
		{
			string value = Text(declared[key]);

			if (!SamePath(directory, value, Text(expected[key])))
			{
				return $"{Fmt.File(FileName)}: {key} is {Fmt.Val(value.Length == 0 ? "empty" : value)}, but the {label} is {Fmt.File(Text(expected[key]))}";
			}
		}

		declared[PackageInfoKey] = expected[PackageInfoKey]!.DeepClone();
		declared[SettingsKey] = expected[SettingsKey]!.DeepClone();

		if (Text(declared["api_version"]) != CurrentApiVersion.ToString())
		{
			return $"{Fmt.File(FileName)} describes an older crater API";
		}

		return JsonNode.DeepEquals(declared, expected) ? null : $"{Fmt.File(FileName)} is out of date";
	}

	public static void Ensure(string directory, bool quiet)
	{
		string? problem = Problem(directory);

		if (problem is null)
		{
			return;
		}

		bool existed = File.Exists(PathIn(directory));

		try
		{
			Write(directory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Out.Warn($"{problem}, and crater could not rewrite it: {exception.Message}");
			return;
		}

		if (existed && !quiet)
		{
			Out.Warn($"{problem}. Fixed it.");
		}
	}

	static string Text(JsonNode? node)
	{
		return node is JsonValue value ? value.ToString() : "";
	}

	static bool SamePath(string directory, string declared, string expected)
	{
		if (string.IsNullOrWhiteSpace(declared))
		{
			return false;
		}

		string full = Path.GetFullPath(Path.Combine(directory, declared));

		return File.Exists(full) && full.Equals(Path.GetFullPath(Path.Combine(directory, expected)), StringComparison.OrdinalIgnoreCase);
	}
}
