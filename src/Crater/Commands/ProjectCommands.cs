using System.Text.RegularExpressions;
using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static partial class ProjectCommands
{
	static readonly string[] ProjectTemplates = ["game", "library", "plugin"];

	public static void Init(Args args)
	{
		string directory = Environment.CurrentDirectory;
		string manifestPath = Path.Combine(directory, Manifest.FileName);
		string settingsPath = ProjectConfig.PathIn(directory);
		string? template = args.Value("template", "t")?.ToLowerInvariant();
		string name = PackageNameFor(directory, args.Value("name"));

		if (template is not null && !ProjectTemplates.Contains(template))
		{
			throw new CraterException($"Unknown template {Fmt.Val(template)}. Use {Fmt.Val("game")}, {Fmt.Val("library")} or {Fmt.Val("plugin")}.");
		}

		if (File.Exists(manifestPath))
		{
			Out.Warn($"{Fmt.File(Manifest.FileName)} already exists, skipped");
		}
		else
		{
			string registry = args.Value("registry") ?? ShortRegistry(BuildInfo.DefaultRegistry);
			JsonObject manifest = ManifestTemplate(name, registry);

			if (template is "game" or "plugin")
			{
				manifest["can_publish"] = false;
				manifest["path_to_main"] = "";
			}

			Json.WriteText(manifestPath, Json.Serialize(manifest));
			Out.Ok($"Created {Fmt.File(Manifest.FileName)} ({Fmt.Pkg(name)}, registry {Fmt.Val(registry)})");
		}


		if (File.Exists(settingsPath))
		{
			Out.Warn($"{Fmt.File(Path.GetFileName(settingsPath))} already exists, skipped");
		}
		else
		{
			Json.WriteText(settingsPath, Json.Serialize(SettingsTemplate()));
			Out.Ok($"Created {Fmt.File(ProjectConfig.FileName)}");
		}

		if (template is not null)
		{
			string shortName = name[(name.IndexOf('/') + 1)..];
			Dictionary<string, string> values = new()
			{
				["NAME"] = name,
				["SHORT_NAME"] = shortName,
				["MODULE_NAME"] = ModuleName(shortName),
				["REQUIRE_PATH"] = Regex.IsMatch(shortName, "^[A-Za-z_][A-Za-z0-9_]*$") ? $"Packages.{shortName}" : $"Packages[\"{shortName}\"]",
			};

			Templates.WriteTo($"init/{template}", directory, values, overwrite: false);
			Out.Ok($"Created a {template} project");
		}
	}

	public static string ShortRegistry(string location)
	{
		if (GitHubRepository.FromRegistry(location) is not GitHubRepository github)
		{
			return location;
		}

		return github.Branch == "main" ? $"https://github.com/{github.Slug}" : $"https://github.com/{github.Slug}/tree/{github.Branch}";
	}

	public static JsonObject ManifestTemplate(string name, string registry)
	{
		return new JsonObject
		{
			["name"] = name,
			["authors"] = new JsonArray(),
			["description"] = "",
			["license"] = "MIT",
			["version"] = "0.1.0",
			["keywords"] = new JsonArray(),
			["registry"] = registry,
			["repository"] = "",
			["can_publish"] = true,
			["import_to"] = "shared",
			["registries"] = new JsonObject(),
			["path_to_main"] = "src/init.luau",
			["icon"] = "",
			["path_to_definition_files"] = new JsonArray(),
			["import_client"] = new JsonArray(),
			["import_server"] = new JsonArray(),
			["import_develop"] = new JsonArray(),
			["overrides"] = new JsonArray(),
			["workspace"] = new JsonArray(),
			["scripts"] = new JsonObject(),
			["tools"] = new JsonObject(),
			["block_package_scripts"] = false,
			["allow_scripts_from"] = new JsonArray(),
			["exclude"] = new JsonArray(),
			["allow_files"] = new JsonArray(),
		};
	}

	public static JsonObject SettingsTemplate()
	{
		return new JsonObject
		{
			["import_client_path"] = ProjectConfig.SuggestedFolder(Realm.Client),
			["import_server_path"] = ProjectConfig.SuggestedFolder(Realm.Server),
			["import_develop_path"] = ProjectConfig.SuggestedFolder(Realm.Develop),
			["import_client_alias"] = "",
			["import_server_alias"] = "",
			["import_develop_alias"] = "",
			["on_install_update_luaurc"] = true,
			["luaurc_path"] = ".luaurc",
			["on_install_update_vscode_luau_definition_files"] = true,
			["on_install_should_i_ask_your_permission_to_do_any_action"] = true,
			["link_style"] = "roblox",
		};
	}

	static string ModuleName(string shortName)
	{
		string joined = string.Concat(shortName.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries).Select(word => char.ToUpperInvariant(word[0]) + word[1..]));

		return joined.Length > 0 && char.IsAsciiLetter(joined[0]) ? joined : "Module" + joined;
	}

		static string PackageNameFor(string directory, string? requested)
	{
		if (requested is not null)
		{
			if (!DependencySpec.IsValidPackageName(requested))
			{
				throw new CraterException($"Invalid package name {Fmt.Val(requested)}. Expected scope/name (a-z, 0-9, _, -).");
			}

			return requested;
		}

		string folder = new DirectoryInfo(directory).Name.ToLowerInvariant().Replace(' ', '-');
		string user = (GitHubAuth.Login() ?? Environment.UserName).ToLowerInvariant();
		string candidate = $"{user}/{folder}";

		if (DependencySpec.IsValidPackageName(candidate))
		{
			return candidate;
		}

		return DependencySpec.IsValidPackageName("scope/" + folder) ? "scope/" + folder : "scope/package";
	}

	public static void Bump(Args args)
	{
		Project project = Common.LoadProject();
		string part = (args.First ?? "patch").ToLowerInvariant();
		string current = project.Manifest.Version;

		if (!SemVer.TryParse(current, out SemVer version))
		{
			throw new CraterException($"Bad {Fmt.Val("version")} in {Fmt.File(Manifest.FileName)}: {SemVer.Explain(current)}.");
		}

		string next = version.Next(part).ToString();
		string text = File.ReadAllText(project.ManifestPath);
		Match field = VersionField().Match(text);

		if (!field.Success)
		{
			throw new CraterException($"{Fmt.File(Manifest.FileName)} has no {Fmt.Val("version")} field.");
		}

		File.WriteAllText(project.ManifestPath, text[..field.Groups["value"].Index] + next + text[(field.Groups["value"].Index + field.Groups["value"].Length)..]);
		Out.Ok($"{Fmt.Pkg(project.Manifest.Name)} {Fmt.Val(current)} → {Fmt.Val(next)}");
	}

	[GeneratedRegex(@"""version""\s*:\s*""(?<value>[^""]*)""")]
	private static partial Regex VersionField();

	public static int Run(Args args)
	{
		Project project = Common.LoadProject();
		Dictionary<string, string> scripts = project.Manifest.Scripts ?? [];

		if (args.First is null)
		{
			if (scripts.Count == 0)
			{
				Out.Info($"No scripts in {Fmt.File(Manifest.FileName)}. Add them under {Fmt.Val("\"scripts\"")}.");
				return 0;
			}

			Out.Heading("Scripts:");

			foreach ((string name, string command) in scripts.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			{
				Out.Info($"  {Fmt.Cmd(name.PadRight(16))} {command}");
			}

			return 0;
		}

		string scriptName = args.First;

		if (!scripts.TryGetValue(scriptName, out string? script))
		{
			throw new CraterException($"Script {Fmt.Cmd(scriptName)} not found in {Fmt.File(Manifest.FileName)}.");
		}

		IEnumerable<string> extra = args.Positional.Skip(1).Concat(args.Passthrough).Select(Quote);
		string commandLine = string.Join(' ', new[] { script }.Concat(extra));

		Dictionary<string, string> environment = new()
		{
			["CRATER_PROJECT_DIR"] = project.Directory,
			["CRATER_PACKAGE_NAME"] = project.Manifest.Name,
			["CRATER_PACKAGE_VERSION"] = project.Manifest.Version,
		};

		Out.Step(Fmt.Cmd(commandLine));

		return Shell.RunInteractive(commandLine, project.Directory, environment);
	}

	static string Quote(string argument)
	{
		bool needsQuotes = argument.Length == 0 || argument.Any(character => char.IsWhiteSpace(character) || character == '"');

		return needsQuotes ? "\"" + argument.Replace("\"", "\\\"") + "\"" : argument;
	}
}
