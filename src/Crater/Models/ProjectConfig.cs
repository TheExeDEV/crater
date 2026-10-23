using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using Crater.Util;

namespace Crater.Models;

public sealed class ProjectConfig
{
	public const string FileName = "crater.config.json";

	[JsonPropertyName("registry")] public string Registry { get; set; } = "";

	[JsonPropertyName("import_client_path")] public string ImportClientPath { get; set; } = "";
	[JsonPropertyName("import_server_path")] public string ImportServerPath { get; set; } = "";
	[JsonPropertyName("import_develop_path")] public string ImportDevelopPath { get; set; } = "";

	[JsonPropertyName("on_install_update_luaurc")] public bool OnInstallUpdateLuaurc { get; set; } = true;
	[JsonPropertyName("on_install_update_vscode_luau_definition_files")] public bool OnInstallUpdateVscodeLuauDefinitionFiles { get; set; } = true;
	[JsonPropertyName("on_install_should_i_ask_your_permission_to_do_any_action")] public bool OnInstallShouldIAskYourPermissionToDoAnyAction { get; set; } = true;

	[JsonPropertyName("link_style")] public string LinkStyle { get; set; } = "roblox";

	[JsonPropertyName("self_update_repo")] public string SelfUpdateRepo { get; set; } = "";

	public static string GlobalDirectory =>
		Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "crater");

	public static string GlobalPath => Path.Combine(GlobalDirectory, "config.json");

	public static string FolderField(Realm realm) => $"import_{realm.Key()}_path";

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
				$"Add it to {Fmt.File(FileName)}, for example {Fmt.Cmd($"\"{FolderField(realm)}\": \"{SuggestedFolder(realm)}\"")}.");
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
		string[] sources = [GlobalPath, Path.Combine(projectDirectory, FileName)];

		foreach (string path in sources)
		{
			if (!File.Exists(path))
			{
				continue;
			}

			foreach ((string key, JsonNode? value) in Json.ReadObject(path))
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

		return Json.ReadFile<ProjectConfig>(GlobalPath);
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
