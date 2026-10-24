using System.Text.Json.Nodes;

namespace Crater.Tests;

[Collection("crater process")]
public sealed class IsolationAndScriptTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-isolation-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public IsolationAndScriptTests()
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

	string Package(string name, string[] dependencies, Dictionary<string, string> files, Action<JsonObject>? edit = null)
	{
		string directory = Path.Combine(root, name.Replace('/', '_'));
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--name", name, "-y"));

		string manifestPath = Path.Combine(directory, "crater.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["version"] = "1.0.0";
		manifest["registry"] = "../registry";
		manifest["path_to_main"] = "init.luau";
		manifest["import_client"] = new JsonArray(dependencies.Select(item => (JsonNode)item).ToArray());
		edit?.Invoke(manifest);
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
	public void IsolatesAPackageWhoseDependencyNameIsTaken()
	{
		Assert.Equal(0, Crater(Package("t/promise", [], new() { ["init.luau"] = "return \"evaera\"\n" }), "publish", "-y"));
		Assert.Equal(0, Crater(Package("t/typed-promise", [], new() { ["init.luau"] = "return \"typed\"\n" }), "publish", "-y"));
		Assert.Equal(0, Crater(Package("t/janitor", ["Promise:t/typed-promise^1"], new() { ["init.luau"] = "return require(script.Parent.Promise)\n" }), "publish", "-y"));

		string game = Package("t/game", ["Promise:t/promise^1", "t/janitor^1"], new() { ["init.luau"] = "return nil\n" });

		Assert.Equal(0, Crater(game, "install", "-y"));

		string packages = Path.Combine(game, "Packages");

		Assert.Contains("evaera", File.ReadAllText(Path.Combine(packages, "Promise", "init.luau")));
		Assert.True(File.Exists(Path.Combine(packages, "_Isolated", "janitor", "janitor", "init.luau")));
		Assert.Contains("[\"typed-promise\"]", File.ReadAllText(Path.Combine(packages, "_Isolated", "janitor", "Promise.luau")));
		Assert.Contains("[\"_Isolated\"][\"janitor\"][\"janitor\"]", File.ReadAllText(Path.Combine(packages, "janitor.luau")));
		Assert.Equal(0, Crater(game, "doctor"));
	}

	[Fact]
	public void WarnsAboutScriptsAndBlocksThemWhenAsked()
	{
		string lib = Package("t/boot", [], new() { ["init.luau"] = "return {}\n", ["Boot.server.luau"] = "print(1)\n" });

		Assert.Equal(0, Crater(lib, "publish", "-y"));
		Assert.Contains("Boot.server.luau", File.ReadAllText(Path.Combine(root, "registry", "t", "boot", "index.json")));

		string game = Package("t/game", [], new() { ["init.luau"] = "return nil\n" }, manifest => manifest["block_package_scripts"] = true);

		Assert.Equal(1, Crater(game, "add", "t/boot@1", "-y"));
		Assert.DoesNotContain("t/boot", File.ReadAllText(Path.Combine(game, "crater.json")));

		JsonObject manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(game, "crater.json")))!.AsObject();
		manifest["allow_scripts_from"] = new JsonArray("t/boot");
		File.WriteAllText(Path.Combine(game, "crater.json"), manifest.ToJsonString());

		Assert.Equal(0, Crater(game, "add", "t/boot@1", "-y"));
	}
}
