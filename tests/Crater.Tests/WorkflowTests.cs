using System.Text.Json.Nodes;
using Crater.Core;
using CraterProject = Crater.Core.Project;
using Crater.Models;
using Crater.Registry;
using Crater.Versioning;

namespace Crater.Tests;

[Collection("crater process")]
public sealed class WorkflowTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-workflow-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public WorkflowTests()
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

	string CraterOutput(string directory, params string[] arguments)
	{
		TextWriter previous = Console.Out;
		StringWriter output = new();

		Console.SetOut(output);

		try
		{
			Crater(directory, arguments);
		}
		finally
		{
			Console.SetOut(previous);
		}

		return output.ToString();
	}

	string Project(string name, string[] dependencies, Dictionary<string, string> files)
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
		File.WriteAllText(manifestPath, manifest.ToJsonString());

		foreach ((string path, string content) in files)
		{
			string file = Path.Combine(directory, path);
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllText(file, content);
		}

		return directory;
	}

	void Publish(string name)
	{
		string directory = Project(name, [], new() { ["init.luau"] = "return {}\n" });

		Assert.Equal(0, Crater(directory, "publish", "-y"));
	}

	[Fact]
	public void UnchangedInstallDoesNotNeedTheRegistry()
	{
		Publish("t/alpha");

		string game = Project("t/game", ["t/alpha^1"], new() { ["init.luau"] = "return nil\n" });

		Assert.Equal(0, Crater(game, "install", "-y"));

		Directory.Move(Path.Combine(root, "registry"), Path.Combine(root, "registry-away"));

		Assert.Equal(0, Crater(game, "install", "-y"));

		Assert.True(InstallState.IsCurrent(CraterProject.Load(game), LockFile.Load(Path.Combine(game, LockFile.FileName))));

		File.WriteAllText(Path.Combine(game, "crater.json"), File.ReadAllText(Path.Combine(game, "crater.json")).Replace("t/alpha^1", "t/alpha^1.0.0"));

		Assert.False(InstallState.IsCurrent(CraterProject.Load(game), LockFile.Load(Path.Combine(game, LockFile.FileName))));
	}

	[Fact]
	public void AddingAPackageKeepsTheOthersInPlace()
	{
		Publish("t/alpha");
		Publish("t/beta");

		string game = Project("t/game", ["t/alpha^1"], new() { ["init.luau"] = "return nil\n" });

		Assert.Equal(0, Crater(game, "install", "-y"));

		string sentinel = Path.Combine(game, "Packages", "alpha", "SENTINEL");
		File.WriteAllText(sentinel, "");

		Assert.Equal(0, Crater(game, "add", "t/beta@1", "-y"));
		Assert.True(File.Exists(sentinel));
		Assert.True(Directory.Exists(Path.Combine(game, "Packages", "beta")));

		Assert.Equal(0, Crater(game, "remove", "beta", "-y"));
		Assert.True(File.Exists(sentinel));
		Assert.False(Directory.Exists(Path.Combine(game, "Packages", "beta")));
	}

	[Fact]
	public void UnusedFindsDeclaredButNotRequiredPackages()
	{
		Publish("t/alpha");
		Publish("t/beta");

		string game = Project("t/game", ["t/alpha^1", "t/beta^1"], new()
		{
			["init.luau"] = "return nil\n",
			["src/main.luau"] = "local Packages = game.ReplicatedStorage.Packages\nlocal alpha = require(Packages.alpha)\n-- require(Packages.beta)\nlocal x = require(Packages.gamma)\nreturn nil\n",
		});

		Assert.Equal(0, Crater(game, "install", "-y"));

		string output = CraterOutput(game, "unused");

		Assert.Contains("t/beta^1", output);
		Assert.DoesNotContain("t/alpha^1 in", output);
		Assert.Contains("Packages.gamma", output);
	}

	[Fact]
	public void PlanIsolatesClashingDependenciesButRejectsClashingRoots()
	{
		LocalRegistry registry = new(Path.Combine(root, "registry"));
		ResolvedPackage first = Package("a/util", registry);
		ResolvedPackage second = Package("b/util", registry);
		ResolvedPackage owner = Package("c/lib", registry);

		owner.Dependencies.Add(new ResolvedDependency("util", second));

		ResolvedGraph graph = new()
		{
			Realm = Realm.Client,
			Roots = [new ResolvedDependency("util", first), new ResolvedDependency("lib", owner)],
			All = [first, second, owner],
		};

		PackageLayout layout = PackageLayout.Plan(graph, "Packages");

		Assert.Equal("util", layout.Folders[first]);
		Assert.Equal("_Isolated/lib/lib", layout.Folders[owner]);
		Assert.Same(second, layout.PrivateLinks[owner]["util"]);
		Assert.Same(owner, layout.Aliases["lib"]);

		ResolvedGraph twoRoots = new()
		{
			Realm = Realm.Client,
			Roots = [new ResolvedDependency("util", first), new ResolvedDependency("util", second)],
			All = [first, second],
		};

		CraterException error = Assert.Throws<CraterException>(() => PackageLayout.Plan(twoRoots, "Packages"));

		Assert.Contains("util", error.Message);
	}

	[Fact]
	public async Task OfflineModeRefusesUncachedRequests()
	{
		Http.Offline = true;

		try
		{
			await Assert.ThrowsAsync<CraterException>(() => Http.GetCachedAsync("https://example.invalid/index.json"));
		}
		finally
		{
			Http.Offline = false;
		}
	}

	static ResolvedPackage Package(string name, IRegistry registry)
	{
		return new ResolvedPackage
		{
			Name = name,
			Registry = registry,
			Release = new RegistryRelease { Version = "1.0.0", SemVer = SemVer.Parse("1.0.0"), Manifest = new Manifest { Name = name } },
		};
	}
}

[Collection("crater process")]
public sealed class RegistryChainTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-chain-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public RegistryChainTests()
	{
		Directory.CreateDirectory(Path.Combine(root, "first"));
		Directory.CreateDirectory(Path.Combine(root, "second"));
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

	string Package(string name, string registry, string version, JsonNode registryValue, string[] dependencies)
	{
		string directory = Path.Combine(root, $"{registry}_{name.Replace('/', '_')}_{version}");
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--name", name, "-y"));

		string manifestPath = Path.Combine(directory, "crater.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["version"] = version;
		manifest["registry"] = registryValue;
		manifest["path_to_main"] = "init.luau";
		manifest["import_client"] = new JsonArray(dependencies.Select(item => (JsonNode)item).ToArray());
		File.WriteAllText(manifestPath, manifest.ToJsonString());
		File.WriteAllText(Path.Combine(directory, "init.luau"), $"return \"{registry}\"\n");

		return directory;
	}

	[Fact]
	public void FindsEachPackageInTheFirstRegistryThatHasIt()
	{
		Assert.Equal(0, Crater(Package("t/shared", "first", "1.0.0", "../first", []), "publish", "-y"));
		Assert.Equal(0, Crater(Package("t/shared", "second", "1.0.0", "../second", []), "publish", "-y"));
		Assert.Equal(0, Crater(Package("t/helper", "second", "1.0.0", "../second", []), "publish", "-y"));
		Assert.Equal(0, Crater(Package("t/tool", "second", "1.0.0", "../second", ["t/helper^1"]), "publish", "-y"));

		string game = Package("t/game", "game", "0.1.0", new JsonArray("../first", "../second"), ["t/shared^1", "t/tool^1"]);

		Assert.Equal(0, Crater(game, "install", "-y"));

		string packages = Path.Combine(game, "Packages");

		Assert.Contains("first", File.ReadAllText(Path.Combine(packages, "shared", "init.luau")));
		Assert.Contains("second", File.ReadAllText(Path.Combine(packages, "tool", "init.luau")));
		Assert.True(Directory.Exists(Path.Combine(packages, "helper")));

		string lockText = File.ReadAllText(Path.Combine(game, "crater.lock"));

		Assert.Contains("\"registry\": \"../first\"", lockText);
		Assert.Contains("\"registry\": \"../second\"", lockText);

		Assert.Equal(1, Crater(game, "add", "t/missing^1", "-y"));
		Assert.DoesNotContain("t/missing", File.ReadAllText(Path.Combine(game, "crater.json")));
	}
}

[Collection("crater process")]
public sealed class LuauRcTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-luaurc-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public LuauRcTests()
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

	string Project(string name, string[] dependencies)
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
		File.WriteAllText(manifestPath, manifest.ToJsonString());
		File.WriteAllText(Path.Combine(directory, "init.luau"), "return {}\n");

		return directory;
	}

	[Fact]
	public void WritesAliasesIntoACustomLuaurcAndPointsVsCodeAtIt()
	{
		Assert.Equal(0, Crater(Project("t/lib", []), "publish", "-y"));

		string game = Project("t/game", ["t/lib^1"]);
		string settingsPath = Path.Combine(game, "crater.settings.json");
		JsonObject settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();

		settings["luaurc_path"] = "src/config.luaurc";
		File.WriteAllText(settingsPath, settings.ToJsonString());
		Directory.CreateDirectory(Path.Combine(game, "src"));
		File.WriteAllText(Path.Combine(game, "src", "config.luaurc"), "{ \"languageMode\": \"strict\" }");

		Assert.Equal(0, Crater(game, "install", "-y"));

		JsonObject luaurc = JsonNode.Parse(File.ReadAllText(Path.Combine(game, "src", "config.luaurc")))!.AsObject();
		JsonObject vscode = JsonNode.Parse(File.ReadAllText(Path.Combine(game, ".vscode", "settings.json")))!.AsObject();

		Assert.Equal("strict", luaurc["languageMode"]!.GetValue<string>());
		Assert.Equal("../Packages", luaurc["aliases"]!["Packages"]!.GetValue<string>());
		Assert.Equal("src/config.luaurc", vscode["luau-lsp.server.baseLuaurc"]!.GetValue<string>());
		Assert.False(File.Exists(Path.Combine(game, ".luaurc")));
		Assert.Equal(0, Crater(game, "doctor"));
	}

	[Fact]
	public void WritesIntoTheLuaurcVsCodeAlreadyUses()
	{
		Assert.Equal(0, Crater(Project("t/lib", []), "publish", "-y"));

		string game = Project("t/game", ["t/lib^1"]);

		Directory.CreateDirectory(Path.Combine(game, ".vscode"));
		Directory.CreateDirectory(Path.Combine(game, "conf"));
		File.WriteAllText(Path.Combine(game, ".vscode", "settings.json"), "{ \"luau-lsp.server.baseLuaurc\": \"./conf/base.luaurc\" }");
		File.WriteAllText(Path.Combine(game, "conf", "base.luaurc"), "{ \"languageMode\": \"strict\", \"aliases\": { \"Mine\": \"./x\" } }");

		Assert.Equal(0, Crater(game, "install", "-y"));

		JsonObject luaurc = JsonNode.Parse(File.ReadAllText(Path.Combine(game, "conf", "base.luaurc")))!.AsObject();
		JsonObject vscode = JsonNode.Parse(File.ReadAllText(Path.Combine(game, ".vscode", "settings.json")))!.AsObject();

		Assert.Equal("../Packages", luaurc["aliases"]!["Packages"]!.GetValue<string>());
		Assert.Equal("./x", luaurc["aliases"]!["Mine"]!.GetValue<string>());
		Assert.Equal("strict", luaurc["languageMode"]!.GetValue<string>());
		Assert.Equal("./conf/base.luaurc", vscode["luau-lsp.server.baseLuaurc"]!.GetValue<string>());
		Assert.False(File.Exists(Path.Combine(game, ".luaurc")));
	}

	[Fact]
	public void UsesAliasNamesFromSettings()
	{
		Assert.Equal(0, Crater(Project("t/lib", []), "publish", "-y"));

		string game = Project("t/game", ["t/lib^1"]);
		string settingsPath = Path.Combine(game, "crater.settings.json");

		Assert.Equal(0, Crater(game, "install", "-y"));

		JsonObject settings = JsonNode.Parse(File.ReadAllText(settingsPath))!.AsObject();
		settings["import_client_alias"] = "@Shared";
		File.WriteAllText(settingsPath, settings.ToJsonString());

		Assert.Equal(0, Crater(game, "install", "-y"));

		JsonObject aliases = JsonNode.Parse(File.ReadAllText(Path.Combine(game, ".luaurc")))!["aliases"]!.AsObject();

		Assert.Equal("./Packages", aliases["Shared"]!.GetValue<string>());
		Assert.False(aliases.ContainsKey("Packages"));
		Assert.Equal(0, Crater(game, "doctor"));
	}

	[Fact]
	public void DefaultLuaurcStaysInTheProjectRoot()
	{
		Assert.Equal(0, Crater(Project("t/lib", []), "publish", "-y"));

		string game = Project("t/game", ["t/lib^1"]);

		Assert.Equal(0, Crater(game, "install", "-y"));

		JsonObject luaurc = JsonNode.Parse(File.ReadAllText(Path.Combine(game, ".luaurc")))!.AsObject();

		Assert.Equal("./Packages", luaurc["aliases"]!["Packages"]!.GetValue<string>());

		string vscodePath = Path.Combine(game, ".vscode", "settings.json");

		Assert.True(!File.Exists(vscodePath) || !File.ReadAllText(vscodePath).Contains("baseLuaurc"));
	}
}
