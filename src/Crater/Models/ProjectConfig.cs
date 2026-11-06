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

	[JsonPropertyName("darklua")] public bool Darklua { get; set; } = true;

	[JsonPropertyName("sync")] public string Sync { get; set; } = "auto";

	[JsonPropertyName("self_update_repo")] public string SelfUpdateRepo { get; set; } = "";

	[JsonPropertyName("vendor_client_path")] public string VendorClientPath { get; set; } = "";
	[JsonPropertyName("vendor_server_path")] public string VendorServerPath { get; set; } = "";
	[JsonPropertyName("vendor_develop_path")] public string VendorDevelopPath { get; set; } = "";

	[JsonPropertyName("vendor_client_alias")] public string VendorClientAlias { get; set; } = "";
	[JsonPropertyName("vendor_server_alias")] public string VendorServerAlias { get; set; } = "";
	[JsonPropertyName("vendor_develop_alias")] public string VendorDevelopAlias { get; set; } = "";

	public const string DefaultVendor = "_vendor";

	public static string DefaultVendorFolder(Realm realm) => $"{DefaultVendor}/{realm.Key()}";

	public static string VendorFolderField(Realm realm) => $"vendor.{realm.Key()}.path";

	public static string VendorAliasField(Realm realm) => $"vendor.{realm.Key()}.alias";

	public string VendorFolder(Realm realm)
	{
		string folder = realm switch
		{
			Realm.Client => VendorClientPath,
			Realm.Server => VendorServerPath,
			_ => VendorDevelopPath,
		};

		return folder.Length == 0 ? DefaultVendorFolder(realm) : folder;
	}

	public string VendorAlias(Realm realm)
	{
		string configured = realm switch
		{
			Realm.Client => VendorClientAlias,
			Realm.Server => VendorServerAlias,
			_ => VendorDevelopAlias,
		};

		return LastSegmentUnless(configured, VendorFolder(realm));
	}

	public IEnumerable<(Realm Realm, string Folder)> VendorFolders() => Realms.All.Select(realm => (realm, VendorFolder(realm)));

	public IEnumerable<(Realm Realm, string Folder, string Alias, bool Vendor)> AliasedFolders()
	{
		foreach ((Realm realm, string folder) in ConfiguredFolders())
		{
			yield return (realm, folder, AliasFor(realm, folder), false);
		}

		foreach ((Realm realm, string folder) in VendorFolders())
		{
			yield return (realm, folder, VendorAlias(realm), true);
		}
	}

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
		NormalizeVendor(settings);

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

	static void NormalizeVendor(JsonObject settings)
	{
		JsonNode? vendor = settings["vendor"];

		settings.Remove("vendor");

		if (vendor is JsonValue value && value.TryGetValue(out string? root))
		{
			foreach (Realm realm in Realms.All)
			{
				settings[$"vendor_{realm.Key()}_path"] = $"{root.Trim().TrimEnd('/', '\\')}/{realm.Key()}";
			}

			return;
		}

		if (vendor is not JsonObject folders)
		{
			return;
		}

		foreach (Realm realm in Realms.All)
		{
			string key = realm.Key();

			if (folders[key] is JsonObject folder)
			{
				if (folder["path"] is JsonNode path)
				{
					settings[$"vendor_{key}_path"] = path.DeepClone();
				}

				if (folder["alias"] is JsonNode alias)
				{
					settings[$"vendor_{key}_alias"] = alias.DeepClone();
				}
			}
			else if (folders[key] is JsonValue text && text.TryGetValue(out string? path))
			{
				settings[$"vendor_{key}_path"] = path;
			}
		}
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

		return LastSegmentUnless(configured, folder);
	}

	static string LastSegmentUnless(string configured, string folder)
	{
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

		if (Sync is not ("auto" or "rojo" or "azul"))
		{
			throw new CraterException($"sync = {Fmt.Val(Sync)}: expected auto, rojo or azul.");
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

		VendorClientPath = NormalizeFolder(VendorClientPath);
		VendorServerPath = NormalizeFolder(VendorServerPath);
		VendorDevelopPath = NormalizeFolder(VendorDevelopPath);

		List<(string Field, string Folder)> taken = ConfiguredFolders().Select(pair => (FolderField(pair.Realm), pair.Folder)).ToList();

		foreach ((Realm realm, string folder) in VendorFolders())
		{
			string field = VendorFolderField(realm);
			bool outside = folder == "." || folder.Split('/').Contains("..") || Path.IsPathRooted(folder);

			if (outside)
			{
				throw new CraterException($"{field} = {Fmt.Val(folder)}: must be a relative path inside the project, for example {Fmt.Val(DefaultVendorFolder(realm))}.");
			}

			(string? other, _) = taken.FirstOrDefault(item => Overlaps(item.Folder, folder));

			if (other is not null)
			{
				throw new CraterException($"{field} = {Fmt.Val(folder)} overlaps {other}. Every package folder needs its own place.");
			}

			taken.Add((field, folder));
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

		foreach ((Realm realm, string _) in VendorFolders())
		{
			string alias = VendorAlias(realm);

			if (alias.Any(character => char.IsWhiteSpace(character) || character is '/' or '\\' or '"'))
			{
				throw new CraterException($"{VendorAliasField(realm)} = {Fmt.Val(alias)}: use letters, digits, - or _ only.");
			}

			if (!aliases.Add(alias))
			{
				throw new CraterException($"{VendorAliasField(realm)} = {Fmt.Val(alias)} is already used by another folder. Set a different alias.");
			}
		}
	}

	static bool Overlaps(string first, string second)
	{
		return first.Equals(second, StringComparison.OrdinalIgnoreCase) ||
			first.StartsWith(second + "/", StringComparison.OrdinalIgnoreCase) ||
			second.StartsWith(first + "/", StringComparison.OrdinalIgnoreCase);
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
