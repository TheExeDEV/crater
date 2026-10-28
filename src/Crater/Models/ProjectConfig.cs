using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Crater.Util;

namespace Crater.Models;

public sealed class ProjectConfig
{
	public const string FileName = "crater.settings.json";
	public const string LegacyFileName = "crater.config.json";

	[JsonPropertyName("import_client_path")] public string ImportClientPath { get; set; } = "";
	[JsonPropertyName("import_server_path")] public string ImportServerPath { get; set; } = "";
	[JsonPropertyName("import_develop_path")] public string ImportDevelopPath { get; set; } = "";

	[JsonPropertyName("import_client_alias")] public string ImportClientAlias { get; set; } = "";
	[JsonPropertyName("import_server_alias")] public string ImportServerAlias { get; set; } = "";
	[JsonPropertyName("import_develop_alias")] public string ImportDevelopAlias { get; set; } = "";

	[JsonPropertyName("on_install_update_luaurc")] public bool OnInstallUpdateLuaurc { get; set; } = true;
	[JsonPropertyName("luaurc_path")] public string LuaurcPath { get; set; } = ".luaurc";
	[JsonPropertyName("on_install_update_vscode_luau_definition_files")] public bool OnInstallUpdateVscodeLuauDefinitionFiles { get; set; } = true;
	[JsonPropertyName("on_install_should_i_ask_your_permission_to_do_any_action")] public bool OnInstallShouldIAskYourPermissionToDoAnyAction { get; set; } = true;

	[JsonPropertyName("link_style")] public string LinkStyle { get; set; } = "roblox";

	[JsonPropertyName("self_update_repo")] public string SelfUpdateRepo { get; set; } = "";

	public static string GlobalDirectory =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "crater");

	public static string GlobalPath => Existing(Path.Combine(GlobalDirectory, "settings.json"), Path.Combine(GlobalDirectory, "config.json"));

	public static string PathIn(string projectDirectory)
	{
		return Existing(Path.Combine(projectDirectory, FileName), Path.Combine(projectDirectory, LegacyFileName));
	}

	static string Existing(string current, string legacy)
	{
		return !File.Exists(current) && File.Exists(legacy) ? legacy : current;
	}

	public static string FolderField(Realm realm) => $"{realm.Key()}.path";

	public static string AliasField(Realm realm) => $"{realm.Key()}.alias";

	public static void Normalize(JsonObject settings)
	{
		foreach (Realm realm in Realms.All)
		{
			string key = realm.Key();
			JsonNode? node = settings[key] ?? (realm == Realm.Develop ? settings["dev"] : null);

			if (node is JsonObject folder)
			{
				if (folder["path"] is JsonNode path)
				{
					settings[$"import_{key}_path"] = path.DeepClone();
				}

				if (folder["alias"] is JsonNode alias)
				{
					settings[$"import_{key}_alias"] = alias.DeepClone();
				}
			}
			else if (node is JsonValue value && value.TryGetValue(out string? text))
			{
				settings[$"import_{key}_path"] = text;
			}

			settings.Remove(key);
		}

		settings.Remove("dev");

		if (settings["luaurc"] is JsonValue luaurc)
		{
			if (luaurc.TryGetValue(out bool enabled))
			{
				settings["on_install_update_luaurc"] = enabled;
			}
			else if (luaurc.TryGetValue(out string? path))
			{
				settings["on_install_update_luaurc"] = true;
				settings["luaurc_path"] = path;
			}
		}

		settings.Remove("luaurc");
		Rename(settings, "vscode_definitions", "on_install_update_vscode_luau_definition_files");
		Rename(settings, "confirm", "on_install_should_i_ask_your_permission_to_do_any_action");
	}

	static void Rename(JsonObject settings, string current, string internalName)
	{
		if (settings[current] is JsonNode value)
		{
			settings[internalName] = value.DeepClone();
		}

		settings.Remove(current);
	}

	public string AliasFor(Realm realm, string folder)
	{
		string configured = realm switch
		{
			Realm.Client => ImportClientAlias,
			Realm.Server => ImportServerAlias,
			_ => ImportDevelopAlias,
		};

		configured = configured.Trim().TrimStart('@');

		if (configured.Length > 0)
		{
			return configured;
		}

		string trimmed = folder.TrimEnd('/');

		return trimmed[(trimmed.LastIndexOf('/') + 1)..];
	}

	public string? TryFolderFor(Realm realm)
	{
		string folder = realm switch
		{
			Realm.Client => ImportClientPath,
			Realm.Server => ImportServerPath,
			_ => ImportDevelopPath,
		};

		return folder.Length == 0 ? null : folder;
	}

	public string FolderFor(Realm realm)
	{
		string? folder = TryFolderFor(realm);

		if (folder is null)
		{
			throw new CraterException(
				$"{Fmt.Val(realm.ManifestField())} has dependencies, but {Fmt.Val(FolderField(realm))} is not set. " +
				$"Add it to {Fmt.File(FileName)}, for example {Fmt.Cmd($"\"{realm.Key()}\": {{ \"path\": \"{SuggestedFolder(realm)}\" }}")}.");
		}

		return folder;
	}

	public IEnumerable<(Realm Realm, string Folder)> ConfiguredFolders()
	{
		foreach (Realm realm in Realms.All)
		{
			string? folder = TryFolderFor(realm);

			if (folder is not null)
			{
				yield return (realm, folder);
			}
		}
	}

	public static string SuggestedFolder(Realm realm) => realm switch
	{
		Realm.Client => "Packages",
		Realm.Server => "ServerPackages",
		_ => "DevPackages",
	};

	public static ProjectConfig Load(string projectDirectory)
	{
		JsonObject merged = new();
		string[] sources = [GlobalPath, PathIn(projectDirectory)];

		foreach (string path in sources)
		{
			if (!File.Exists(path))
			{
				continue;
			}

			JsonObject settings = Json.ReadObject(path);

			Normalize(settings);

			if (settings.ContainsKey("registry"))
			{
				Out.Warn($"{Fmt.File(path)}: {Fmt.Val("registry")} is ignored here, it now lives in {Fmt.File(Manifest.FileName)}");
			}

			foreach ((string key, JsonNode? value) in settings)
			{
				merged[key] = value?.DeepClone();
			}
		}

		ProjectConfig config = Json.Parse<ProjectConfig>(Json.Serialize(merged), FileName);
		config.Validate();

		return config;
	}

	public static ProjectConfig LoadGlobal()
	{
		if (!File.Exists(GlobalPath))
		{
			return new ProjectConfig();
		}

		JsonObject settings = Json.ReadObject(GlobalPath);

		Normalize(settings);

		return Json.Parse<ProjectConfig>(Json.Serialize(settings), GlobalPath);
	}

	void Validate()
	{
		if (LinkStyle is not ("roblox" or "string"))
		{
			throw new CraterException($"link_style = {Fmt.Val(LinkStyle)}: expected roblox or string.");
		}

		ImportClientPath = NormalizeFolder(ImportClientPath);
		ImportServerPath = NormalizeFolder(ImportServerPath);
		ImportDevelopPath = NormalizeFolder(ImportDevelopPath);

		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		foreach ((Realm realm, string folder) in ConfiguredFolders())
		{
			bool outside = folder == "." || folder.Split('/').Contains("..") || Path.IsPathRooted(folder);

			if (outside)
			{
				throw new CraterException($"{FolderField(realm)} = {Fmt.Val(folder)}: must be a relative path inside the project.");
			}

			if (!seen.Add(folder))
			{
				throw new CraterException($"{FolderField(realm)} = {Fmt.Val(folder)} is already used by another realm.");
			}
		}

		HashSet<string> aliases = new(StringComparer.OrdinalIgnoreCase);

		foreach ((Realm realm, string folder) in ConfiguredFolders())
		{
			string alias = AliasFor(realm, folder);

			if (alias.Any(character => char.IsWhiteSpace(character) || character is '/' or '\\' or '"'))
			{
				throw new CraterException($"{AliasField(realm)} = {Fmt.Val(alias)}: use letters, digits, - or _ only.");
			}

			if (!aliases.Add(alias))
			{
				throw new CraterException($"{AliasField(realm)} = {Fmt.Val(alias)} is already used by another realm.");
			}
		}
	}

	static string NormalizeFolder(string path)
	{
		string result = path.Trim().Replace('\\', '/');

		while (result.StartsWith("./"))
		{
			result = result[2..];
		}

		return result.Trim('/');
	}
}
