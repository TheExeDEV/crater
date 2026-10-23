using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class ProjectCommands
{
	public static void Init(Args args)
	{
		string directory = Environment.CurrentDirectory;
		string manifestPath = Path.Combine(directory, Manifest.FileName);
		string configPath = Path.Combine(directory, ProjectConfig.FileName);

		if (File.Exists(manifestPath))
		{
			Out.Warn($"{Fmt.File(Manifest.FileName)} already exists, skipped");
		}
		else
		{
			string name = PackageNameFor(directory, args.Value("name"));

			Json.WriteText(manifestPath, Json.Serialize(ManifestTemplate(name)));
			Out.Ok($"Created {Fmt.File(Manifest.FileName)} ({Fmt.Pkg(name)})");
		}

		if (File.Exists(configPath))
		{
			Out.Warn($"{Fmt.File(ProjectConfig.FileName)} already exists, skipped");
		}
		else
		{
			Json.WriteText(configPath, Json.Serialize(ConfigTemplate(args.Value("registry") ?? "")));
			Out.Ok($"Created {Fmt.File(ProjectConfig.FileName)}");
		}
	}

	public static JsonObject ManifestTemplate(string name)
	{
		return new JsonObject
		{
			["name"] = name,
			["authors"] = new JsonArray(),
			["description"] = "",
			["license"] = "MIT",
			["version"] = "0.1.0",
			["keywords"] = new JsonArray(),
			["repository"] = "",
			["repository_registry"] = "",
			["can_publish"] = false,
			["import_to"] = "shared",
			["path_to_main"] = "src/init.luau",
			["path_to_definition_file"] = "",
			["import_client"] = new JsonArray(),
			["import_server"] = new JsonArray(),
			["import_develop"] = new JsonArray(),
			["scripts"] = new JsonObject(),
		};
	}

	public static JsonObject ConfigTemplate(string registry)
	{
		return new JsonObject
		{
			["registry"] = registry,
			["import_client_path"] = ProjectConfig.SuggestedFolder(Realm.Client),
			["import_server_path"] = ProjectConfig.SuggestedFolder(Realm.Server),
			["import_develop_path"] = ProjectConfig.SuggestedFolder(Realm.Develop),
			["on_install_update_luaurc"] = true,
			["on_install_update_vscode_luau_definition_files"] = true,
			["on_install_should_i_ask_your_permission_to_do_any_action"] = true,
			["link_style"] = "roblox",
		};
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
		string user = Environment.UserName.ToLowerInvariant();
		string candidate = $"{user}/{folder}";

		if (DependencySpec.IsValidPackageName(candidate))
		{
			return candidate;
		}

		return DependencySpec.IsValidPackageName("scope/" + folder) ? "scope/" + folder : "scope/package";
	}

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
