using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Integrations;

public static partial class Darklua
{
	public const string ConfigFile = ".darklua.json";
	public const string BuildProject = "build.project.json";
	public const string Output = "dist";
	public const string Sourcemap = "sourcemap.json";
	const string BuildScript = "build";

	[GeneratedRegex(@"\brequire\s*\(?\s*[""'`]@[A-Za-z_]")]
	private static partial Regex AliasRequire();

	public static bool IsSetUp(Project project)
	{
		return File.Exists(Path.Combine(project.Directory, ConfigFile)) && File.Exists(Path.Combine(project.Directory, BuildProject));
	}

	public static bool UsesAliases(Project project)
	{
		return ProjectSources.LuauFiles(project, SourceFolders(project)).Any(file => AliasRequire().IsMatch(File.ReadAllText(file)));
	}

	public static List<string> SourceFolders(Project project)
	{
		return File.Exists(Path.Combine(project.Directory, ProjectSources.RojoProject))
			? ProjectSources.Folders(project).Where(folder => folder != "." && !folder.StartsWith(Output + "/", StringComparison.Ordinal)).ToList()
			: [];
	}

	public static List<string> Roots(Project project)
	{
		return SourceFolders(project).Select(folder => folder.Split('/')[0]).Distinct(StringComparer.Ordinal).ToList();
	}

	public static void Update(Project project, Prompter prompt)
	{
		if (!project.Config.Darklua || SourceFolders(project).Count == 0 || !UsesAliases(project))
		{
			return;
		}

		try
		{
			Apply(project, prompt);
		}
		catch (CraterException exception)
		{
			Out.Dim($"  darklua was not set up: {exception.Message}");
		}
	}

	static void Apply(Project project, Prompter prompt)
	{
		List<(string Path, string Text)> changes = [];

		AddChange(changes, project, ConfigFile, Json.Serialize(Config(project)));
		AddChange(changes, project, BuildProject, Json.Serialize(BuildProjectFor(project)));

		string manifestPath = Path.Combine(project.Directory, Manifest.FileName);
		JsonObject manifest = Json.ReadObject(manifestPath);
		bool addScript = manifest["scripts"] is not JsonObject scripts || !scripts.ContainsKey(BuildScript);
		string? gitignore = GitignoreWithBuild(project);

		if (changes.Count == 0 && !addScript && gitignore is null)
		{
			return;
		}

		List<string> names = changes.Select(change => change.Path).ToList();

		if (addScript)
		{
			names.Add($"the {Fmt.Cmd(BuildScript)} script");
		}

		if (!prompt.Confirm($"Your code requires through aliases, which Roblox cannot read. Set up darklua to turn them into instance paths ({string.Join(", ", names)})"))
		{
			return;
		}

		foreach ((string path, string text) in changes)
		{
			Json.WriteText(Path.Combine(project.Directory, path), text);
		}

		if (addScript)
		{
			if (manifest["scripts"] is not JsonObject existing)
			{
				existing = [];
				manifest["scripts"] = existing;
			}

			existing[BuildScript] = BuildCommand(project);
			ManifestFile.Write(manifestPath, manifest);
		}

		if (gitignore is not null)
		{
			File.WriteAllText(Path.Combine(project.Directory, ".gitignore"), gitignore);
		}

		Out.Dim($"  darklua: {Fmt.Cmd($"crater run {BuildScript}")} builds the place, {Fmt.Cmd("crater dev")} serves the converted code");

		if (project.Manifest.Tools?.ContainsKey("darklua") != true)
		{
			Out.Dim($"  pin darklua for everyone on the project with {Fmt.Cmd("crater tool add darklua")}");
		}
	}

	public static JsonObject Config(Project project)
	{
		string path = Path.Combine(project.Directory, ConfigFile);
		JsonObject config = File.Exists(path) ? Json.ReadObject(path) : [];

		if (config["rules"] is not JsonArray rules)
		{
			rules = [];
			config["rules"] = rules;
		}

		JsonObject aliases = [];

		foreach ((Realm realm, string folder) in project.Config.ConfiguredFolders())
		{
			aliases["@" + project.Config.AliasFor(realm, folder)] = "./" + folder;
		}

		JsonObject rule = new()
		{
			["rule"] = "convert_require",
			["current"] = new JsonObject { ["name"] = "luau", ["aliases"] = aliases },
			["target"] = new JsonObject { ["name"] = "roblox", ["rojo_sourcemap"] = "./" + Sourcemap, ["indexing_style"] = "find_first_child" },
		};

		int index = rules.ToList().FindIndex(item => item is JsonObject existing && existing["rule"]?.GetValue<string>() == "convert_require");

		if (index >= 0)
		{
			rules[index] = rule;
		}
		else
		{
			rules.Add(rule);
		}

		return config;
	}

	public static JsonObject BuildProjectFor(Project project)
	{
		JsonObject source = Json.ReadObject(Path.Combine(project.Directory, ProjectSources.RojoProject));
		HashSet<string> folders = SourceFolders(project).ToHashSet(StringComparer.OrdinalIgnoreCase);

		Redirect(source, folders);

		return source;
	}

	public static List<(string Executable, string[] Arguments)> WatchCommands(Project project, string darklua)
	{
		return Roots(project)
			.Select(root => (darklua, new[] { "process", root, $"{Output}/{root}", "--config", ConfigFile, "--watch" }))
			.ToList();
	}

	static string BuildCommand(Project project)
	{
		IEnumerable<string> steps = Roots(project)
			.Select(root => $"darklua process {root} {Output}/{root} --config {ConfigFile}")
			.Prepend($"rojo sourcemap {ProjectSources.RojoProject} --output {Sourcemap}")
			.Append($"rojo build {BuildProject} --output build.rbxl");

		return string.Join(" && ", steps);
	}

	static void Redirect(JsonNode? node, HashSet<string> folders)
	{
		if (node is not JsonObject element)
		{
			return;
		}

		foreach ((string key, JsonNode? value) in element.ToList())
		{
			if (key == "$path" && value is JsonValue path && path.TryGetValue(out string? text) && folders.Contains(text.Replace('\\', '/').TrimEnd('/')))
			{
				element[key] = $"{Output}/{text.Replace('\\', '/').TrimEnd('/')}";
			}
			else
			{
				Redirect(value, folders);
			}
		}
	}

	static void AddChange(List<(string Path, string Text)> changes, Project project, string name, string text)
	{
		string path = Path.Combine(project.Directory, name);

		if (!File.Exists(path) || Json.Serialize(Json.ReadObject(path)) != text)
		{
			changes.Add((name, text));
		}
	}

	static string? GitignoreWithBuild(Project project)
	{
		string path = Path.Combine(project.Directory, ".gitignore");

		if (!File.Exists(path))
		{
			return null;
		}

		string text = File.ReadAllText(path);
		List<string> lines = text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim()).ToList();
		List<string> missing = new[] { Output + "/", BuildProject }.Where(entry => !lines.Contains(entry) && !lines.Contains("/" + entry)).ToList();

		if (missing.Count == 0)
		{
			return null;
		}

		return text.TrimEnd('\r', '\n') + "\n" + string.Join("\n", missing) + "\n";
	}
}
