using System.Formats.Tar;
using System.IO.Compression;
using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Registry;
using Crater.Util;

namespace Crater.Tests;

[Collection("crater process")]
public sealed class RegistryFeatureTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-feature-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public RegistryFeatureTests()
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

	(int ExitCode, string Output) Crater(string directory, params string[] arguments)
	{
		TextWriter previous = Console.Out;
		StringWriter output = new();

		Environment.CurrentDirectory = directory;
		Console.SetOut(output);

		try
		{
			return (Program.Main(arguments).GetAwaiter().GetResult(), output.ToString());
		}
		finally
		{
			Console.SetOut(previous);
		}
	}

	string Project(string name, string[] dependencies, Action<JsonObject>? edit = null)
	{
		string directory = Path.Combine(root, name.Replace('/', '_'));
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--name", name, "-y").ExitCode);

		string manifestPath = Path.Combine(directory, "crater.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["version"] = "1.0.0";
		manifest["registry"] = "../registry";
		manifest["path_to_main"] = "init.luau";
		manifest["import_client"] = new JsonArray(dependencies.Select(item => (JsonNode)item).ToArray());
		edit?.Invoke(manifest);
		File.WriteAllText(manifestPath, manifest.ToJsonString());
		File.WriteAllText(Path.Combine(directory, "init.luau"), "return {}\n");

		return directory;
	}

	void SetVersion(string directory, string version)
	{
		string path = Path.Combine(directory, "crater.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

		manifest["version"] = version;
		File.WriteAllText(path, manifest.ToJsonString());
	}

	RegistryIndex Index(string name) => Json.ReadFile<RegistryIndex>(Path.Combine(root, "registry", name, "index.json"));

	[Fact]
	public void MonorepositoryAddsItsPackagesWhereTheySay()
	{
		Assert.Equal(0, Crater(Project("t/a", []), "publish", "-y").ExitCode);
		Assert.Equal(0, Crater(Project("t/b", [], manifest => manifest["import_to"] = "server"), "publish", "-y").ExitCode);
		Assert.Equal(0, Crater(Project("t/kit", [], manifest =>
		{
			manifest["monorepository-elements"] = new JsonObject { ["Alpha"] = "t/a^1", ["b"] = "t/b^1" };
		}), "publish", "-y").ExitCode);

		string game = Project("t/game", []);

		Assert.Equal(0, Crater(game, "add", "t/kit", "-y").ExitCode);

		JsonObject manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(game, "crater.json")))!.AsObject();

		Assert.Equal(["Alpha:t/a^1"], manifest["import_client"]!.AsArray().Select(item => item!.GetValue<string>()));
		Assert.Equal(["t/b^1"], manifest["import_server"]!.AsArray().Select(item => item!.GetValue<string>()));
		Assert.True(Directory.Exists(Path.Combine(game, "Packages", "Alpha")) || File.Exists(Path.Combine(game, "Packages", "Alpha.luau")));
	}

	[Fact]
	public void InterfacesAndTheirImplementationsAreFound()
	{
		Assert.Equal(0, Crater(Project("t/logger-api", [], manifest => manifest["interface"] = true), "publish", "-y").ExitCode);
		Assert.Equal(0, Crater(Project("t/logger", [], manifest => manifest["realized_interfaces"] = new JsonArray("t/logger-api")), "publish", "-y").ExitCode);

		string library = Project("t/bad-logger", [], manifest =>
		{
			manifest["realized_interfaces"] = new JsonArray("t/logger-api");
			manifest["path_to_main"] = "";
		});

		File.Delete(Path.Combine(library, "init.luau"));
		Directory.CreateDirectory(Path.Combine(library, "lib"));
		File.WriteAllText(Path.Combine(library, "lib", "a.luau"), "return {}\n");
		Assert.Equal(1, Crater(library, "pack", "-o", Path.Combine(root, "bad.zip")).ExitCode);

		string game = Project("t/game", []);
		(int exitCode, string output) = Crater(game, "find-interface", "t/logger-api");

		Assert.Equal(0, exitCode);
		Assert.Contains("t/logger ", output);
		Assert.DoesNotContain("t/bad-logger", output);
		Assert.Contains("t/logger-api", Crater(game, "search", "logger", "interface").Output);
		Assert.DoesNotContain("t/logger-api", Crater(game, "search", "logger", "package").Output);
	}

	[Fact]
	public void DeprecatedVersionsAreSkippedAndMarked()
	{
		string lib = Project("t/lib", []);

		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);
		SetVersion(lib, "1.1.0");
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);

		Assert.Equal(0, Crater(lib, "deprecate", "t/lib@1.1.0", "has a leak", "--registry", "../registry", "-y").ExitCode);
		Assert.Equal("has a leak", Index("t/lib").Versions.Single(release => release.Version == "1.1.0").Deprecated);

		string game = Project("t/game", ["t/lib^1"]);
		(int exitCode, string output) = Crater(game, "install", "-y");

		Assert.Equal(0, exitCode);
		Assert.Contains("\"version\": \"1.0.0\"", File.ReadAllText(Path.Combine(game, "crater.lock")));
		Assert.DoesNotContain("deprecated", output);

		Assert.Equal(0, Crater(lib, "deprecate", "t/lib", "use t/other", "--registry", "../registry", "-y").ExitCode);
		Assert.Equal("use t/other", Index("t/lib").Deprecated);
		Assert.Contains("\"deprecated\": \"use t/other\"", File.ReadAllText(Path.Combine(root, "registry", "packages.json")));

		File.Delete(Path.Combine(game, "crater.lock"));
		Assert.Contains("t/lib is deprecated: use t/other", Crater(game, "install", "-y").Output);

		Assert.Equal(0, Crater(lib, "deprecate", "t/lib", "--undo", "--registry", "../registry", "-y").ExitCode);
		Assert.Null(Index("t/lib").Deprecated);

		Crater.Registry.RegistryChecker checker = new(Path.Combine(root, "registry"));
		checker.CheckContents();
		Assert.Empty(checker.Errors);
	}

	[Fact]
	public void PublishStoresTheChangelogAndUpdateShowsIt()
	{
		string lib = Project("t/notes", []);

		File.WriteAllText(Path.Combine(lib, "CHANGELOG.md"), "# Changelog\n\n## [2.0.0] - 2026-10-24\n\n### Breaking\n\n- Renamed `run` to `start`\n\n## 1.0.0\n\n- First release\n");
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);
		SetVersion(lib, "2.0.0");
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);

		Assert.Equal("- First release", Index("t/notes").Versions.Single(release => release.Version == "1.0.0").Notes);
		Assert.Equal("### Breaking\n\n- Renamed `run` to `start`", Index("t/notes").Versions.Single(release => release.Version == "2.0.0").Notes);

		string game = Project("t/game", ["t/notes^1"]);

		Assert.Contains("t/notes 1.0.0 → 2.0.0", Crater(game, "install", "-y").Output);

		File.WriteAllText(Path.Combine(game, "crater.json"), File.ReadAllText(Path.Combine(game, "crater.json")).Replace("t/notes^1", "t/notes=1.0.0"));
		Assert.DoesNotContain("Newer versions exist", Crater(game, "install", "-y").Output);

		File.WriteAllText(Path.Combine(game, "crater.json"), File.ReadAllText(Path.Combine(game, "crater.json")).Replace("t/notes=1.0.0", "t/notes^2"));

		string output = Crater(game, "install", "-y").Output;

		Assert.Contains("What changed in t/notes", output);
		Assert.Contains("Renamed `run` to `start`", output);
	}

	[Fact]
	public void PackageIconIsCheckedAndShownOnTheSite()
	{
		byte[] png = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A, 1, 2, 3];
		string lib = Project("t/pretty", [], manifest => manifest["icon"] = "assets/icon.png");

		Assert.Equal(1, Crater(lib, "pack", "-o", Path.Combine(root, "pretty.zip")).ExitCode);

		Directory.CreateDirectory(Path.Combine(lib, "assets"));
		File.WriteAllBytes(Path.Combine(lib, "assets", "icon.png"), new byte[300 * 1024]);
		Assert.Equal(1, Crater(lib, "pack", "-o", Path.Combine(root, "pretty.zip")).ExitCode);

		File.WriteAllBytes(Path.Combine(lib, "assets", "icon.png"), png);
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);

		string site = Path.Combine(root, "site");
		Environment.SetEnvironmentVariable("CRATER_SITE_NO_EXTERNAL", "1");

		try
		{
			global::Crater.Site.SiteBuilder.Build(Path.Combine(root, "registry"), site, "TheExeDEV/crater");
		}
		finally
		{
			Environment.SetEnvironmentVariable("CRATER_SITE_NO_EXTERNAL", null);
		}

		Assert.Equal(png, File.ReadAllBytes(Path.Combine(site, "data", "icons", "t", "pretty.png")));
		Assert.Contains("\"icon\": \"data/icons/t/pretty.png\"", File.ReadAllText(Path.Combine(site, "data", "packages", "t", "pretty.json")));
		Assert.Contains("\"icon\": \"data/icons/t/pretty.png\"", File.ReadAllText(Path.Combine(site, "data", "packages.json")));
		Assert.Contains("<title>t/pretty 1.0.0</title>", File.ReadAllText(Path.Combine(site, "feed.xml")));
		Assert.Contains("\"first\": true", File.ReadAllText(Path.Combine(site, "data", "recent.json")));
	}

	[Fact]
	public void IconMustBeAnImage()
	{
		string lib = Project("t/odd", [], manifest => manifest["icon"] = "icon.txt");

		File.WriteAllText(Path.Combine(lib, "icon.txt"), "not an image");
		Assert.Equal(1, Crater(lib, "pack", "-o", Path.Combine(root, "odd.zip")).ExitCode);
		Assert.False(File.Exists(Path.Combine(root, "odd.zip")));
	}

	[Theory]
	[InlineData("## 1.2.0\n\n- a\n\n## 1.1.0\n\n- b", "1.2.0", "- a")]
	[InlineData("## [1.2.0] - 2026-01-01\n- a\n### Fixed\n- c\n## [1.1.0]\n- b", "1.2.0", "- a\n### Fixed\n- c")]
	[InlineData("# v1.2.0\ntext\n# v1.1.0", "1.2.0", "text")]
	[InlineData("## 1.2.0\n\n- a", "1.3.0", null)]
	[InlineData("## 1.2.10\n- x\n## 1.2.1\n- y", "1.2.1", "- y")]
	public void ReadsTheChangelogSectionOfAVersion(string markdown, string version, string? expected)
	{
		Assert.Equal(expected, Changelog.Section(markdown, version));
	}

	[Fact]
	public void GraphDrawsTheLockedTree()
	{
		string lib = Project("t/base", []);
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);

		string top = Project("t/top", ["Base:t/base^1"]);
		Assert.Equal(0, Crater(top, "publish", "-y").ExitCode);

		string game = Project("t/game", ["t/top^1"]);
		Assert.Equal(0, Crater(game, "install", "-y").ExitCode);

		string graph = Crater(game, "graph").Output;

		Assert.StartsWith("graph LR", graph);
		Assert.Contains("root[\"t/game\"]", graph);
		Assert.Contains("client_0[\"t/top<br/>1.0.0\"]", graph);
		Assert.Contains("root --> client_0", graph);
		Assert.Contains("client_0 -->|Base| client_1", graph);

		Assert.Equal(0, Crater(game, "graph", "-o", "deps.md").ExitCode);
		Assert.StartsWith("```mermaid\ngraph LR", File.ReadAllText(Path.Combine(game, "deps.md")));
	}

	[Theory]
	[InlineData("game", "src/shared/Greeting.luau", false)]
	[InlineData("library", "src/init.luau", true)]
	[InlineData("plugin", "src/init.server.luau", false)]
	public void InitCreatesAProjectFromATemplate(string template, string file, bool publishable)
	{
		string directory = Path.Combine(root, template);
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--template", template, "--name", "me/my-thing", "-y").ExitCode);
		Assert.True(File.Exists(Path.Combine(directory, file)));
		Assert.True(File.Exists(Path.Combine(directory, "default.project.json")));
		Assert.DoesNotContain("{{", File.ReadAllText(Path.Combine(directory, "default.project.json")));
		Assert.Equal(publishable, JsonNode.Parse(File.ReadAllText(Path.Combine(directory, "crater.json")))!["can_publish"]!.GetValue<bool>());

		if (publishable)
		{
			Assert.Contains("Packages[\"my-thing\"]", File.ReadAllText(Path.Combine(directory, "README.md")));
			Assert.Equal(0, Crater(directory, "pack", "-o", Path.Combine(root, "thing.zip")).ExitCode);
		}

		Assert.Equal(1, Crater(Path.Combine(root, "registry"), "init", "--template", "website").ExitCode);
	}

	[Fact]
	public void ReadsPesdeIndexFiles()
	{
		const string index = """
			[meta]
			deprecated = "use other/thing"

			["0.1.0 luau"]
			published_at = "2025-02-04T22:10:37Z"

			["0.1.0 luau".target]
			environment = "luau"
			lib = "lib.luau"

			["0.1.0 roblox"]
			published_at = "2025-02-04T22:14:53Z"
			description = "Parses things"
			license = "MIT"

			["0.1.0 roblox".target]
			environment = "roblox"
			lib = "src/lib.luau"
			build_files = ["src"]

			["0.1.0 roblox".dependencies]
			types = [{ name = "alicesaidhi/conch_types", version = "^0.1.0", index = "https://github.com/daimond113/pesde-index", target = "roblox" }, "standard"]
			Promise = [{ wally = "evaera/promise", version = "^4.0.0", index = "https://github.com/UpliftGames/wally-index" }, "peer"]
			test = [{ name = "someone/tester", version = "^1.0.0", index = "https://github.com/pesde-pkg/index" }, "dev"]

			["0.2.0+1 roblox_server"]
			published_at = "2025-03-01T00:00:00Z"
			yanked = true

			["0.2.0+1 roblox_server".target]
			environment = "roblox_server"
			lib = "init.luau"
			""";

		PesdeRegistry registry = new("https://example.invalid/index", "https://example.invalid", "{PACKAGE}");
		RegistryIndex parsed = registry.ParseIndex("alicesaidhi/conch_ast", index, "test");

		Assert.Equal("use other/thing", parsed.Deprecated);
		Assert.Equal(["0.1.0", "0.2.0"], parsed.Versions.Select(release => release.Version).Order().ToList());

		RegistryRelease first = parsed.Versions.Single(release => release.Version == "0.1.0");

		Assert.Equal("src/lib.luau", first.Manifest.PathToMain);
		Assert.Equal("shared", first.Manifest.ImportTo);
		Assert.Equal(["types:alicesaidhi/conch_types^0.1.0", "Promise:wally#evaera/promise^4.0.0"], first.Manifest.ImportClient);

		RegistryRelease second = parsed.Versions.Single(release => release.Version == "0.2.0");

		Assert.Equal("server", second.Manifest.ImportTo);
		Assert.NotNull(second.Deprecated);
	}

	[Fact]
	public void ConvertsPesdeArchivesToZip()
	{
		using MemoryStream tarGz = new();

		using (GZipStream gzip = new(tarGz, CompressionLevel.Fastest, leaveOpen: true))
		using (TarWriter tar = new(gzip))
		{
			foreach ((string name, string text) in new[] { ("pesde.toml", "name = \"a/b\""), ("src/lib.luau", "return 1") })
			{
				PaxTarEntry entry = new(TarEntryType.RegularFile, name) { DataStream = new MemoryStream(System.Text.Encoding.UTF8.GetBytes(text)) };
				tar.WriteEntry(entry);
			}
		}

		using ZipArchive zip = new(new MemoryStream(PesdeRegistry.ToZip(tarGz.ToArray())));

		Assert.Equal(["pesde.toml", "src/lib.luau"], zip.Entries.Select(entry => entry.FullName).Order().ToList());
	}
}
