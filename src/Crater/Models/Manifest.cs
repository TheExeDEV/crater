using System.Text.Json;
using System.Text.Json.Serialization;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Models;

public sealed class Manifest
{
	public const string FileName = "crater.json";

	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("authors")] public List<string> Authors { get; set; } = [];
	[JsonPropertyName("description")] public string Description { get; set; } = "";
	[JsonPropertyName("license")] public string License { get; set; } = "";
	[JsonPropertyName("version")] public string Version { get; set; } = "0.1.0";
	[JsonPropertyName("keywords")] public List<string>? Keywords { get; set; }

	[JsonPropertyName("registry")] public string Registry { get; set; } = "";
	[JsonPropertyName("repository")] public string Repository { get; set; } = "";
	[JsonPropertyName("repository_registry")] public string? LegacyRegistry { get; set; }

	[JsonIgnore] public string RegistryLocation => Registry.Length > 0 ? Registry : LegacyRegistry ?? "";

	[JsonPropertyName("can_publish")] public bool CanPublish { get; set; }
	[JsonPropertyName("import_to")] public string ImportTo { get; set; } = "shared";

	[JsonPropertyName("path_to_main")] public string PathToMain { get; set; } = "";
	[JsonPropertyName("path_to_definition_file")] public string PathToDefinitionFile { get; set; } = "";

	[JsonPropertyName("import_client")] public List<string> ImportClient { get; set; } = [];
	[JsonPropertyName("import_server")] public List<string> ImportServer { get; set; } = [];
	[JsonPropertyName("import_develop")] public List<string> ImportDevelop { get; set; } = [];

	[JsonPropertyName("overrides")] public List<string>? Overrides { get; set; }
	[JsonPropertyName("workspace")] public List<string>? Workspace { get; set; }
	[JsonPropertyName("scripts")] public Dictionary<string, string>? Scripts { get; set; }
	[JsonPropertyName("exclude")] public List<string>? Exclude { get; set; }

	[JsonExtensionData] public Dictionary<string, JsonElement>? Extra { get; set; }

	public List<string> DepsFor(Realm realm) => realm switch
	{
		Realm.Client => ImportClient,
		Realm.Server => ImportServer,
		_ => ImportDevelop,
	};

	public IEnumerable<string> TransitiveDepsFor(Realm realm) => realm switch
	{
		Realm.Client => ImportClient,
		Realm.Server => ImportServer,
		_ => ImportClient.Concat(ImportServer),
	};

	public IEnumerable<DependencySpec> AllDependencies()
	{
		foreach (Realm realm in Realms.All)
		{
			foreach (string raw in DepsFor(realm))
			{
				yield return DependencySpec.Parse(raw, $"{realm.ManifestField()} in {FileName}");
			}
		}
	}

	public List<DependencySpec> ParseOverrides()
	{
		List<DependencySpec> overrides = (Overrides ?? [])
			.Select(raw => DependencySpec.Parse(raw, $"overrides in {FileName}"))
			.ToList();

		DependencySpec? aliased = overrides.FirstOrDefault(spec => spec.Alias is not null);

		if (aliased is not null)
		{
			throw new CraterException($"Override {Fmt.Val(aliased)} cannot have an alias. Aliases belong in the import lists.");
		}

		return overrides;
	}

	public static Manifest Load(string path)
	{
		if (!File.Exists(path))
		{
			throw new CraterException($"{Fmt.File(path)} not found. Run {Fmt.Cmd("crater init")} to create it.");
		}

		return Json.ReadFile<Manifest>(path);
	}

	public void ValidateCommon()
	{
		if (ImportTo is not ("client" or "server" or "shared"))
		{
			throw new CraterException($"import_to = {Fmt.Val(ImportTo)}: expected client, server or shared.");
		}

		foreach (DependencySpec _ in AllDependencies())
		{
		}

		ParseOverrides();
	}

	public void ValidateForPublish()
	{
		if (!DependencySpec.IsValidPackageName(Name))
		{
			throw new CraterException($"Invalid package name {Fmt.Val(Name)}. Expected scope/name (a-z, 0-9, _, -).");
		}

		if (!SemVer.TryParse(Version, out _))
		{
			throw new CraterException($"Invalid version {Fmt.Val(Version)}. Expected MAJOR.MINOR.PATCH, for example 1.0.0.");
		}

		ValidateCommon();

		DependencySpec? local = AllDependencies().FirstOrDefault(spec => spec.Source is not null);

		if (local is not null)
		{
			throw new CraterException(
				$"{Fmt.Val(local)} uses a path or git source. Published packages can only depend on registry packages.");
		}
	}
}
