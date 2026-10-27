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

	[JsonPropertyName("registry")]
	[JsonConverter(typeof(StringOrListConverter))]
	public List<string> Registry { get; set; } = [];
	[JsonPropertyName("repository")] public string Repository { get; set; } = "";
	[JsonPropertyName("repository_registry")] public string? LegacyRegistry { get; set; }

	[JsonIgnore] public List<string> RegistryLocations => Registry.Count > 0 ? Registry : LegacyRegistry is { Length: > 0 } legacy ? [legacy] : [];

	[JsonIgnore] public string RegistryLocation => RegistryLocations.FirstOrDefault() ?? "";

	[JsonPropertyName("can_publish")] public bool CanPublish { get; set; }
	[JsonPropertyName("registries")] public Dictionary<string, string>? Registries { get; set; }
	[JsonPropertyName("import_to")] public string ImportTo { get; set; } = "shared";

	[JsonPropertyName("path_to_main")] public string PathToMain { get; set; } = "";
	[JsonPropertyName("icon")] public string? Icon { get; set; }
	[JsonPropertyName("path_to_definition_files")] public List<string> DefinitionFiles { get; set; } = [];
	[JsonPropertyName("path_to_definition_file")] public string? LegacyDefinitionFile { get; set; }

	[JsonIgnore]
	public List<string> DefinitionPatterns
	{
		get
		{
			IEnumerable<string> legacy = string.IsNullOrWhiteSpace(LegacyDefinitionFile) ? [] : [LegacyDefinitionFile];

			return DefinitionFiles
				.Concat(legacy)
				.Where(entry => !string.IsNullOrWhiteSpace(entry))
				.Select(entry => entry.Trim())
				.Distinct(StringComparer.OrdinalIgnoreCase)
				.ToList();
		}
	}

	[JsonPropertyName("import_client")] public List<string> ImportClient { get; set; } = [];
	[JsonPropertyName("import_server")] public List<string> ImportServer { get; set; } = [];
	[JsonPropertyName("import_develop")] public List<string> ImportDevelop { get; set; } = [];

	[JsonPropertyName("overrides")] public List<string>? Overrides { get; set; }
	[JsonPropertyName("workspace")] public List<string>? Workspace { get; set; }
	[JsonPropertyName("scripts")] public Dictionary<string, string>? Scripts { get; set; }
	[JsonPropertyName("tools")] public Dictionary<string, string>? Tools { get; set; }
	[JsonPropertyName("block_package_scripts")] public bool? BlockPackageScripts { get; set; }
	[JsonPropertyName("allow_scripts_from")] public List<string>? AllowScriptsFrom { get; set; }
	[JsonPropertyName("exclude")] public List<string>? Exclude { get; set; }
	[JsonPropertyName("allow_files")] public List<string>? AllowFiles { get; set; }
	[JsonPropertyName("monorepository")] public bool? Monorepository { get; set; }
	[JsonPropertyName("monorepository-elements")] public Dictionary<string, string>? MonorepositoryElements { get; set; }
	[JsonPropertyName("interface")] public bool? Interface { get; set; }
	[JsonPropertyName("realized_interfaces")] public List<string>? RealizedInterfaces { get; set; }

	[JsonIgnore] public bool IsMonorepository => Monorepository == true && MonorepositoryElements is { Count: > 0 };

	[JsonIgnore] public bool IsInterface => Interface == true;

	public static readonly string[] EntryFileNames = ["init.luau", "main.luau"];

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

		foreach ((string element, string raw) in MonorepositoryElements ?? [])
		{
			DependencySpec.Parse(raw, $"monorepository-elements.{element} in {FileName}");
		}

		if (Monorepository == true && MonorepositoryElements is not { Count: > 0 })
		{
			throw new CraterException($"{Fmt.Val("monorepository")} is true, but {Fmt.Val("monorepository-elements")} lists no packages.");
		}

		string? badInterface = (RealizedInterfaces ?? []).FirstOrDefault(name => !DependencySpec.IsValidPackageName(name.ToLowerInvariant()));

		if (badInterface is not null)
		{
			throw new CraterException($"realized_interfaces: {Fmt.Val(badInterface)} is not an interface name. Expected author/name.");
		}

		if (IsInterface && RealizedInterfaces is { Count: > 0 })
		{
			throw new CraterException($"An interface cannot implement other interfaces. Remove {Fmt.Val("realized_interfaces")} or set {Fmt.Val("interface")} to false.");
		}
	}

	public void ValidateForPublish()
	{
		if (!DependencySpec.IsValidPackageName(Name))
		{
			throw new CraterException($"Invalid package name {Fmt.Val(Name)}. Expected scope/name (a-z, 0-9, _, -).");
		}

		if (!SemVer.TryParse(Version, out _))
		{
			throw new CraterException($"Bad {Fmt.Val("version")} in {Fmt.File(FileName)}: {SemVer.Explain(Version)}.");
		}

		ValidateCommon();

		DependencySpec? custom = AllDependencies().FirstOrDefault(spec => spec.RegistryName is string named && !Crater.Registry.NamedRegistries.IsBuiltin(named));

		if (custom is not null)
		{
			throw new CraterException(
				$"{Fmt.Val(custom)} uses your own registry name {Fmt.Val(custom.RegistryName!)}. People who install this package do not have it (and may have no access to a private registry); " +
				$"published packages can only use {Fmt.Val("crater#")}, {Fmt.Val("wally#")}, {Fmt.Val("pesde#")}, {Fmt.Val("forest#")} or {Fmt.Val("ember#")}.");
		}

		DependencySpec? local = AllDependencies().FirstOrDefault(spec => spec.Source is not null);

		if (local is not null)
		{
			throw new CraterException(
				$"{Fmt.Val(local)} uses a path or git source. Published packages can only depend on registry packages.");
		}
	}
}
