using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class ImportCommand
{
	sealed record Imported(JsonObject Manifest, List<string> Warnings);

	public static void Run(Args args)
	{
		string directory = Environment.CurrentDirectory;
		string source = args.First ?? DetectSource(directory);
		string sourcePath = Path.GetFullPath(Path.Combine(directory, source));
		string projectDirectory = Path.GetDirectoryName(sourcePath)!;
		string manifestPath = Path.Combine(projectDirectory, Manifest.FileName);
		string settingsPath = ProjectConfig.PathIn(projectDirectory);

		if (!File.Exists(sourcePath))
		{
			throw new CraterException($"{Fmt.File(source)} not found.");
		}

		if (File.Exists(manifestPath) && !args.Has("force"))
		{
			throw new CraterException($"{Fmt.File(Path.GetRelativePath(directory, manifestPath))} already exists. Pass {Fmt.Cmd("--force")} to overwrite it.");
		}

		TomlTable toml = Toml.Parse(File.ReadAllText(sourcePath), source);
		bool isPesde = Path.GetFileName(sourcePath).Equals("pesde.toml", StringComparison.OrdinalIgnoreCase);
		Imported imported = isPesde ? FromPesde(toml, projectDirectory) : FromWally(toml, projectDirectory);
		JsonNode registry = RegistryFor(args, toml, isPesde);

		imported.Manifest["registry"] = registry;

		ManifestFile.Write(manifestPath, imported.Manifest);
		Out.Ok($"Created {Fmt.File(Path.GetRelativePath(directory, manifestPath))} from {Fmt.File(source)}");

		if (!File.Exists(settingsPath))
		{
			Json.WriteText(settingsPath, Json.Serialize(ProjectCommands.SettingsTemplate()));
			Out.Ok($"Created {Fmt.File(ProjectConfig.FileName)}");
		}

		foreach (string warning in imported.Warnings)
		{
			Out.Warn(warning);
		}

		Out.Info("");
		Out.Info($"Run {Fmt.Cmd("crater install")} in {Fmt.File(Path.GetRelativePath(directory, projectDirectory) is "." ? "this folder" : Path.GetRelativePath(directory, projectDirectory))}.");
	}

	static JsonNode RegistryFor(Args args, TomlTable toml, bool isPesde)
	{
		if (args.Value("registry") is string chosen)
		{
			return chosen;
		}

		string main = ProjectCommands.ShortRegistry(BuildInfo.DefaultRegistry);

		if (isPesde)
		{
			return main;
		}

		string? declared = toml.Table("package")?.String("registry");
		string wally = declared is { Length: > 0 } ? declared.TrimEnd('/') : NamedRegistries.WallyIndex;

		return new JsonArray(main, wally);
	}

	static string DetectSource(string directory)
	{
		foreach (string candidate in new[] { "wally.toml", "pesde.toml" })
		{
			if (File.Exists(Path.Combine(directory, candidate)))
			{
				return candidate;
			}
		}

		throw new CraterException($"No {Fmt.File("wally.toml")} or {Fmt.File("pesde.toml")} here. Pass the file name explicitly.");
	}

	static Imported FromWally(TomlTable toml, string directory)
	{
		List<string> warnings = [];
		TomlTable package = toml.Table("package") ?? new TomlTable();
		JsonObject manifest = BaseManifest(package.String("name"), package, directory);

		manifest["can_publish"] = !(package.Bool("private") ?? false);
		manifest["import_to"] = package.String("realm") switch
		{
			"server" => "server",
			_ => "shared",
		};

		(string Section, Realm Realm)[] sections =
		[
			("dependencies", Realm.Client),
			("server-dependencies", Realm.Server),
			("dev-dependencies", Realm.Develop),
		];

		foreach ((string section, Realm realm) in sections)
		{
			JsonArray list = [];

			foreach ((string alias, object value) in toml.Table(section) ?? new TomlTable())
			{
				string? spec = value is string text ? WallySpec(alias, text, warnings) : null;

				if (spec is not null)
				{
					list.Add(spec);
				}
			}

			manifest[realm.ManifestField()] = list;
		}

		return new Imported(manifest, warnings);
	}

	static Imported FromPesde(TomlTable toml, string directory)
	{
		List<string> warnings = [];
		JsonObject manifest = BaseManifest(toml.String("name"), toml, directory);

		manifest["can_publish"] = !(toml.Bool("private") ?? false);
		manifest["import_to"] = "shared";

		if (toml.Table("target")?.String("lib") is string library)
		{
			manifest["path_to_main"] = library;
		}

		(string Section, Realm Realm)[] sections =
		[
			("dependencies", Realm.Client),
			("peer_dependencies", Realm.Client),
			("dev_dependencies", Realm.Develop),
		];

		foreach ((string section, Realm realm) in sections)
		{
			JsonArray list = ManifestEditor.ListFor(manifest, realm.ManifestField());

			foreach ((string alias, object value) in toml.Table(section) ?? new TomlTable())
			{
				string? spec = value is TomlTable table ? PesdeSpec(alias, table, warnings) : null;

				if (spec is not null)
				{
					list.Add(spec);
				}
			}
		}

		warnings.Add($"pesde has no server dependency list, all dependencies went to {Fmt.Val("import_client")}. Move server-only ones to {Fmt.Val("import_server")}.");

		return new Imported(manifest, warnings);
	}

	static JsonObject BaseManifest(string? name, TomlTable package, string directory)
	{
		string fullName = (name ?? "scope/package").ToLowerInvariant();
		JsonObject manifest = ProjectCommands.ManifestTemplate(fullName, BuildInfo.DefaultRegistry);

		manifest["version"] = package.String("version") ?? "0.1.0";
		manifest["description"] = package.String("description") ?? "";
		manifest["license"] = package.String("license") ?? "";
		manifest["repository"] = package.String("repository") ?? "";
		manifest["authors"] = new JsonArray(package.Strings("authors").Select(author => (JsonNode)author).ToArray());
		manifest["path_to_main"] = RojoMainPath(directory) ?? "src/init.luau";

		List<string> exclude = package.Strings("exclude");

		if (exclude.Count > 0)
		{
			manifest["exclude"] = new JsonArray(exclude.Select(item => (JsonNode)item).ToArray());
		}

		return manifest;
	}

	static string? RojoMainPath(string directory)
	{
		string path = Path.Combine(directory, "default.project.json");

		if (!File.Exists(path))
		{
			return null;
		}

		JsonObject project = Json.ReadObject(path);

		return project["tree"]?["$path"]?.GetValue<string>();
	}

	static string? WallySpec(string alias, string text, List<string> warnings)
	{
		int at = text.IndexOf('@');

		if (at < 0)
		{
			warnings.Add($"Skipped {Fmt.Val(alias)} = {Fmt.Val(text)}: expected scope/name@version.");
			return null;
		}

		string name = text[..at].ToLowerInvariant();
		string requirement = ConvertRequirement(text[(at + 1)..], $"{alias} = \"{text}\"", warnings);

		return Compose(alias, name, requirement);
	}

	static string? PesdeSpec(string alias, TomlTable table, List<string> warnings)
	{
		string? name = table.String("name") ?? table.String("workspace") ?? table.String("wally");
		string version = table.String("version") ?? "*";

		if (table.String("repo") is not null)
		{
			warnings.Add($"Skipped git dependency {Fmt.Val(alias)}: add it by hand as {Fmt.Cmd("scope/name@git+<url>#<rev>")}.");
			return null;
		}

		if (name is null)
		{
			warnings.Add($"Skipped {Fmt.Val(alias)}: no package name.");
			return null;
		}

		int hash = name.IndexOf('#');
		string fullName = (hash >= 0 ? name[(hash + 1)..] : name).ToLowerInvariant();
		string requirement = ConvertRequirement(version, alias, warnings);
		string registry = table.String("wally") is not null ? "wally#" : table.String("workspace") is not null ? "" : "pesde#";

		return Compose(alias, fullName, requirement, registry);
	}

	static string? Compose(string alias, string fullName, string requirement, string registry = "")
	{
		string shortName = fullName[(fullName.IndexOf('/') + 1)..];
		string prefix = alias == shortName ? "" : alias + ":";
		string spec = prefix + registry + fullName + requirement;

		DependencySpec.Parse(spec);

		return spec;
	}

	static string ConvertRequirement(string requirement, string context, List<string> warnings)
	{
		string text = requirement.Trim();

		if (text is "" or "*")
		{
			return "^";
		}

		if (text.Contains(','))
		{
			string first = text.Split(',')[0].Trim();

			warnings.Add($"{Fmt.Val(context)}: ranges are not supported, kept only {Fmt.Val(first)}.");
			text = first;
		}

		if (text.StartsWith(">="))
		{
			return "+" + text[2..].Trim();
		}

		if (text[0] is '^' or '~')
		{
			return text[0] + text[1..].Trim();
		}

		if (text[0] == '=')
		{
			return "=" + PadVersion(text[1..].Trim());
		}

		if (text[0] is '<' or '>')
		{
			warnings.Add($"{Fmt.Val(context)}: {Fmt.Val(text)} has no crater equivalent, using the latest version.");
			return "^";
		}

		return "^" + text;
	}

	static string PadVersion(string version)
	{
		int dots = version.Count(character => character == '.');

		return version + string.Concat(Enumerable.Repeat(".0", Math.Max(0, 2 - dots)));
	}
}
