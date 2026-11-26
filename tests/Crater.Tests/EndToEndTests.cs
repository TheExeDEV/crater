using System.Text.Json.Nodes;
using LunautEditors = Crater.Integrations.Lunaut;
using CraterManifest = Crater.Models.Manifest;
using CraterJson = Crater.Util.Json;

namespace Crater.Tests;

[CollectionDefinition("crater process", DisableParallelization = true)]
public sealed class CraterProcessCollection;

[Collection("crater process")]
public sealed class EndToEndTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public EndToEndTests()
	{
		Directory.CreateDirectory(Path.Combine(root, "registry"));
		Environment.SetEnvironmentVariable("CRATER_CACHE", Path.Combine(root, "cache"));
		Environment.SetEnvironmentVariable("NO_COLOR", "1");
	}

	public void Dispose()
	{
		Environment.CurrentDirectory = previousDirectory;
		Directory.Delete(root, recursive: true);
	}

	int Crater(string directory, params string[] arguments)
	{
		Environment.CurrentDirectory = directory;

		return Program.Main(arguments).GetAwaiter().GetResult();
	}

	string NewPackage(string name, string version, string pathToMain, string[] clientDependencies, Dictionary<string, string> files)
	{
		string directory = Path.Combine(root, name.Replace('/', '_'));
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--name", name, "-y"));

		string manifestPath = Path.Combine(directory, "crater.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["version"] = version;
		manifest["registry"] = "../registry";
		manifest["path_to_main"] = pathToMain;
		manifest["import_client"] = new JsonArray(clientDependencies.Select(item => (JsonNode)item).ToArray());
		File.WriteAllText(manifestPath, manifest.ToJsonString());

		foreach ((string path, string content) in files)
		{
			string file = Path.Combine(directory, path);
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllText(file, content);
		}

		return directory;
	}

	[Fact]
	public void PublishesAndInstallsIntoAFlatFolder()
	{
		string janitor = NewPackage("t/janitor", "1.0.0", "src/init.luau", [], new()
		{
			["src/init.luau"] = "export type Janitor = { count: number }\nreturn {}\n",
			["src/janitor.d.luau"] = "declare JANITOR: number\n",
		});

		string signal = NewPackage("t/signal", "1.0.0", "init.luau", ["Janitor:t/janitor^1"], new()
		{
			["init.luau"] = "local Janitor = require(script.Parent.Janitor)\nreturn {}\n",
		});

		string util = NewPackage("t/util", "1.0.0", "", [], new()
		{
			["a.luau"] = "return 1\n",
			["b.luau"] = "return 2\n",
		});

		Assert.Equal(0, Crater(janitor, "publish", "-y"));
		Assert.Equal(0, Crater(signal, "publish", "-y"));
		Assert.Equal(0, Crater(util, "publish", "-y"));
		Assert.Equal(1, Crater(janitor, "publish", "-y"));

		string game = NewPackage("t/game", "0.1.0", "src/init.luau", ["t/signal^1", "t/janitor@1.0.0", "t/util^1"], []);

		Assert.Equal(0, Crater(game, "install", "-y"));

		string packages = Path.Combine(game, "Packages");

		Assert.True(File.Exists(Path.Combine(packages, ".crater")));
		Assert.False(Directory.Exists(Path.Combine(packages, "_Index")));
		Assert.True(File.Exists(Path.Combine(packages, "signal", "init.luau")));
		Assert.Contains("script[\"src\"]", File.ReadAllText(Path.Combine(packages, "janitor", "init.luau")));
		Assert.Contains("export type Janitor", File.ReadAllText(Path.Combine(packages, "Janitor.luau")));
		Assert.Contains("loaders", File.ReadAllText(Path.Combine(packages, "util", "init.luau")));

		JsonObject settings = JsonNode.Parse(File.ReadAllText(Path.Combine(game, ".vscode", "settings.json")))!.AsObject();
		JsonObject definitions = settings["luau-lsp.types.definitionFiles"]!.AsObject();

		Assert.Equal("Packages/janitor/src/janitor.d.luau", definitions["@janitor@1.0.0"]!.GetValue<string>());

		Assert.Equal(0, Crater(game, "install", "--locked", "-y"));
		Assert.Equal(0, Crater(game, "doctor"));
	}

	[Fact]
	public void WritesLunautDefinitionsWithPackagePriorities()
	{
		foreach ((string name, int priority) in new[] { ("t/base", 100), ("t/extra", 10) })
		{
			string package = NewPackage(name, "1.0.0", "init.luau", [], new()
			{
				["init.luau"] = "return {}\n",
				[$"{name[2..]}.d.luau"] = $"declare {name[2..].ToUpperInvariant()}: number\n",
			});

			string manifestPath = Path.Combine(package, "crater.json");
			JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

			manifest["definition_priority"] = priority;
			manifest["definition_show_as_module"] = priority == 100;
			File.WriteAllText(manifestPath, manifest.ToJsonString());

			Assert.Equal(0, Crater(package, "publish", "-y"));
		}

		string game = NewPackage("t/game", "0.1.0", "src/init.luau", ["t/extra^1", "t/base^1"], []);
		string settingsPath = Path.Combine(game, ".vscode", "settings.json");

		Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
		File.WriteAllText(settingsPath, "{ \"lunaut.platform\": \"roblox\", \"luau-lsp.types.definitionFiles\": { \"@own\": \"types/own.d.luau\" } }");

		Assert.Equal(0, Crater(game, "install", "-y"));

		JsonObject settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
		List<JsonObject> lunaut = settings["lunaut.definitions"]!.AsArray().Select(item => item!.AsObject()).ToList();

		Assert.Equal(["types/own.d.luau", "Packages/base/base.d.luau", "Packages/extra/extra.d.luau"], lunaut.Select(entry => entry["path"]!.GetValue<string>()));
		Assert.Equal([100, 10], lunaut.Skip(1).Select(entry => entry["priority"]!.GetValue<int>()));
		Assert.Null(lunaut[0]["priority"]);
		Assert.Equal(["@own", "@base", "@extra"], lunaut.Select(entry => entry["name"]!.GetValue<string>()));
		Assert.True(lunaut[1]["show_as_module"]!.GetValue<bool>());
		Assert.Null(lunaut[2]["show_as_module"]);
		Assert.Equal(["@own"], settings["luau-lsp.types.definitionFiles"]!.AsObject().Select(pair => pair.Key));

		File.WriteAllText(Path.Combine(game, "crater.settings.json"), File.ReadAllText(Path.Combine(game, "crater.settings.json")).TrimEnd().TrimEnd('}') + ", \"language_server\": \"lunaut\" }");
		Assert.Equal(0, Crater(game, "install", "-y"));

		settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();

		Assert.Equal(["@own"], settings["luau-lsp.types.definitionFiles"]!.AsObject().Select(pair => pair.Key));
		Assert.Equal(3, settings["lunaut.definitions"]!.AsArray().Count);
	}

	sealed class OldManifest
	{
		[System.Text.Json.Serialization.JsonPropertyName("path_to_definition_files")] public List<string> DefinitionFiles { get; set; } = [];
	}

	[Fact]
	public void DefinitionFilesTakeObjectsAndStayReadableForOlderCrater()
	{
		const string text = """
			{
			  "name": "t/core",
			  "path_to_definition_files": [
			    { "name": "funny_core_types", "path": "src/core.d.luau", "priority": 100, "show_as_module": true },
			    "src/extra.d.luau"
			  ]
			}
			""";

		CraterManifest manifest = CraterJson.Parse<CraterManifest>(text, "crater.json");

		Assert.Equal(["src/core.d.luau", "src/extra.d.luau"], manifest.DefinitionPatterns);
		Assert.Equal(100, manifest.DefinitionFiles[0].Priority);

		string index = CraterJson.Serialize(manifest);
		OldManifest old = System.Text.Json.JsonSerializer.Deserialize<OldManifest>(index)!;

		Assert.Equal(["src/core.d.luau", "src/extra.d.luau"], old.DefinitionFiles);

		CraterManifest again = CraterJson.Parse<CraterManifest>(index, "index.json");

		Assert.Equal("funny_core_types", again.DefinitionFiles[0].Name);
		Assert.True(again.DefinitionFiles[0].ShowAsModule);
		Assert.Null(again.DefinitionFiles[1].Priority);
	}

	[Fact]
	public void PerFileDefinitionOptionsReachLunautThroughALiftedPackage()
	{
		string core = NewPackage("t/core", "1.0.0", "src/init.luau", [], new()
		{
			["default.project.json"] = "{ \"name\": \"core\", \"tree\": { \"$path\": \"src\" } }",
			["src/init.luau"] = "return {}\n",
			["src/core.d.luau"] = "declare CORE: number\n",
			["src/extra.d.luau"] = "declare EXTRA: number\n",
			["src/named.d.luau"] = "--!strict\n--!show_module_name_message \"fc_named\"\nexport type Row = { id: number }\n",
		});

		string manifestPath = Path.Combine(core, "crater.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["definition_priority"] = 5;
		manifest["path_to_definition_files"] = JsonNode.Parse("""
			[
			  { "name": "funny_core_types", "path": "src/core.d.luau", "priority": 100, "show_as_module": true, "message": "Core types" },
			  "src/extra.d.luau",
			  "src/named.d.luau"
			]
			""");
		File.WriteAllText(manifestPath, manifest.ToJsonString());

		Assert.Equal(0, Crater(core, "publish", "-y"));

		string game = NewPackage("t/game", "0.1.0", "src/init.luau", ["t/core^1"], []);
		string settingsPath = Path.Combine(game, ".vscode", "settings.json");

		Directory.CreateDirectory(Path.GetDirectoryName(settingsPath)!);
		File.WriteAllText(settingsPath, "{ \"lunaut.platform\": \"roblox\" }");

		Assert.Equal(0, Crater(game, "install", "-y"));

		List<JsonObject> lunaut = JsonNode.Parse(File.ReadAllText(settingsPath))!["lunaut.definitions"]!.AsArray().Select(item => item!.AsObject()).ToList();
		JsonObject types = lunaut.Single(entry => entry["name"]?.GetValue<string>() == "@funny_core_types");
		JsonObject extra = lunaut.Single(entry => entry["path"]!.GetValue<string>().EndsWith("extra.d.luau", StringComparison.Ordinal));

		Assert.Equal(100, types["priority"]!.GetValue<int>());
		Assert.True(types["show_as_module"]!.GetValue<bool>());
		Assert.Equal(5, extra["priority"]!.GetValue<int>());
		Assert.Null(extra["show_as_module"]);
		Assert.Equal("Core types", types["message"]!.GetValue<string>());
		Assert.Null(lunaut.Single(entry => entry["path"]!.GetValue<string>().EndsWith("named.d.luau", StringComparison.Ordinal))["name"]);
	}

	[Fact]
	public void FindsTheLunautExtensionInEveryEditor()
	{
		string home = Path.Combine(root, "home");
		string extensions = Path.Combine(home, ".cursor", "extensions");
		string[] lunaut = ["theexe.lunaut-luau"];

		Directory.CreateDirectory(Path.Combine(extensions, "theexe.lunaut-luautools-1.0.0"));
		Assert.False(LunautEditors.HasEditorExtension(home, lunaut));

		Directory.CreateDirectory(Path.Combine(extensions, "theexe.lunaut-luau-0.0.9-win32-x64"));
		Assert.True(LunautEditors.HasEditorExtension(home, lunaut));

		File.WriteAllText(Path.Combine(extensions, ".obsolete"), "{ \"theexe.lunaut-luau-0.0.9-win32-x64\": true }");
		Assert.False(LunautEditors.HasEditorExtension(home, lunaut));
	}

	[Fact]
	public void LockedInstallFailsWhenLockIsOutdated()
	{
		string lib = NewPackage("t/lib", "1.0.0", "init.luau", [], new() { ["init.luau"] = "return {}\n" });
		Assert.Equal(0, Crater(lib, "publish", "-y"));

		string game = NewPackage("t/game", "0.1.0", "src/init.luau", [], []);

		Assert.Equal(1, Crater(game, "install", "--locked", "-y"));
		Assert.Equal(0, Crater(game, "add", "t/lib@1", "-y"));
		Assert.Equal(0, Crater(game, "install", "--locked", "-y"));
	}

	[Fact]
	public void BumpEditsOnlyTheVersion()
	{
		string lib = NewPackage("t/bumped", "0.0.9", "init.luau", [], new() { ["init.luau"] = "return {}\n" });
		string before = File.ReadAllText(Path.Combine(lib, "crater.json"));

		Assert.Equal(0, Crater(lib, "bump"));

		string after = File.ReadAllText(Path.Combine(lib, "crater.json"));

		Assert.Equal(before.Replace("\"0.0.9\"", "\"0.1.0\""), after);
	}
}

[Collection("crater process")]
public sealed class PatchTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-patch-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public PatchTests()
	{
		Directory.CreateDirectory(Path.Combine(root, "registry"));
		Environment.SetEnvironmentVariable("CRATER_CACHE", Path.Combine(root, "cache"));
		Environment.SetEnvironmentVariable("NO_COLOR", "1");
	}

	public void Dispose()
	{
		Environment.CurrentDirectory = previousDirectory;
		Directory.Delete(root, recursive: true);
	}

	int Crater(string directory, params string[] arguments)
	{
		Environment.CurrentDirectory = directory;

		return Program.Main(arguments).GetAwaiter().GetResult();
	}

	string Project(string name, string[] dependencies, Dictionary<string, string> files)
	{
		string directory = Path.Combine(root, name.Replace('/', '_'));
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--name", name, "-y"));

		string manifestPath = Path.Combine(directory, "crater.json");
		System.Text.Json.Nodes.JsonObject manifest = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["version"] = "1.0.0";
		manifest["license"] = name == "t/lib" ? "GPL-3.0" : "MIT";
		manifest["registry"] = "../registry";
		manifest["path_to_main"] = "init.luau";
		manifest["import_client"] = new System.Text.Json.Nodes.JsonArray(dependencies.Select(item => (System.Text.Json.Nodes.JsonNode)item).ToArray());
		File.WriteAllText(manifestPath, manifest.ToJsonString());

		foreach ((string path, string content) in files)
		{
			File.WriteAllText(Path.Combine(directory, path), content);
		}

		return directory;
	}

	[Fact]
	public void SavesAndReappliesPatches()
	{
		string lib = Project("t/lib", [], new() { ["init.luau"] = "return 1\n", ["extra.luau"] = "return 2\n" });
		Assert.Equal(0, Crater(lib, "publish", "-y"));

		string game = Project("t/game", ["t/lib^1"], new() { ["init.luau"] = "return nil\n" });
		Assert.Equal(0, Crater(game, "install", "-y"));

		string installed = Path.Combine(game, "Packages", "lib");

		File.WriteAllText(Path.Combine(installed, "init.luau"), "return 42\n");
		File.Delete(Path.Combine(installed, "extra.luau"));

		Assert.Equal(0, Crater(game, "patch", "lib"));

		string patch = Path.Combine(game, "patches", "t_lib@1.0.0");

		Assert.Equal("return 42\n", File.ReadAllText(Path.Combine(patch, "init.luau")));
		Assert.Equal("extra.luau", File.ReadAllText(Path.Combine(patch, ".deleted")).Trim());

		Directory.Delete(Path.Combine(game, "Packages"), recursive: true);
		Assert.Equal(0, Crater(game, "install", "-y"));

		Assert.Equal("return 42\n", File.ReadAllText(Path.Combine(installed, "init.luau")));
		Assert.False(File.Exists(Path.Combine(installed, "extra.luau")));

		Assert.Equal(0, Crater(game, "size"));
		Assert.Equal(0, Crater(game, "licenses"));

		Assert.Equal(0, Crater(game, "patch", "lib", "--remove"));
		Assert.Equal(0, Crater(game, "install", "-y"));
		Assert.Equal("return 1\n", File.ReadAllText(Path.Combine(installed, "init.luau")));
	}
}
