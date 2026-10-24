using System.Text.Json.Nodes;

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

		Assert.Equal("Packages/janitor/src/janitor.d.luau", definitions["@janitor"]!.GetValue<string>());

		Assert.Equal(0, Crater(game, "install", "--locked", "-y"));
		Assert.Equal(0, Crater(game, "doctor"));
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
