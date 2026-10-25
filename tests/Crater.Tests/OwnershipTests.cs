using System.Text.Json.Nodes;
using Crater.Models;
using Crater.Registry;
using Crater.Util;

namespace Crater.Tests;

[Collection("crater process")]
public sealed class OwnershipTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-owner-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;
	readonly bool previousOffline = Http.Offline;

	public OwnershipTests()
	{
		Directory.CreateDirectory(root);
		Environment.SetEnvironmentVariable("CRATER_CACHE", Path.Combine(root, "cache"));
		Environment.SetEnvironmentVariable("NO_COLOR", "1");
		Http.Offline = true;
	}

	public void Dispose()
	{
		Http.Offline = previousOffline;
		Environment.CurrentDirectory = previousDirectory;

		foreach (string file in Directory.EnumerateFiles(root, "*", SearchOption.AllDirectories))
		{
			File.SetAttributes(file, FileAttributes.Normal);
		}

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

	string Project(string name, string registry, string[] dependencies, Dictionary<string, string>? files = null, string main = "init.luau")
	{
		string directory = Path.Combine(root, name.Replace('/', '_'));
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--name", name, "-y").ExitCode);

		string manifestPath = Path.Combine(directory, "crater.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["version"] = "1.0.0";
		manifest["registry"] = registry;
		manifest["path_to_main"] = main;
		manifest["import_client"] = new JsonArray(dependencies.Select(item => (JsonNode)item).ToArray());
		File.WriteAllText(manifestPath, manifest.ToJsonString());

		foreach ((string path, string content) in files ?? new() { ["init.luau"] = "return {}\n" })
		{
			string file = Path.Combine(directory, path);
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllText(file, content);
		}

		return directory;
	}

	static void Git(string directory, params string[] arguments)
	{
		Shell.Run("git", ["-c", "user.name=test", "-c", "user.email=test@example.com", "-c", "commit.gpgsign=false", .. arguments], directory);
	}

	static void Commit(string registry, string message)
	{
		Git(registry, "add", "-A");
		Git(registry, "commit", "-q", "-m", message);
	}

	List<string> Check(string registry, string author)
	{
		RegistryChecker checker = new(registry);

		checker.CheckChanges("main", author);

		return checker.Errors.Select(Out.Plain).ToList();
	}

	[Fact]
	public void OnlyTheScopeOwnerCanChangeOwnersAndCoOwnersCanPublish()
	{
		string registry = Path.Combine(root, "registry");
		Directory.CreateDirectory(registry);
		Git(registry, "init", "-q", "-b", "main");

		string lib = Project("alice/lib", "../registry", []);

		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);
		Commit(registry, "alice/lib 1.0.0");

		Git(registry, "checkout", "-q", "-b", "mallory-takes-over");
		Assert.Equal(0, Crater(lib, "owner", "add", "alice/lib", "mallory", "-y").ExitCode);
		Commit(registry, "mallory adds herself");

		List<string> takeover = Check(registry, "mallory");

		Assert.Contains(takeover, error => error.Contains("only alice can change the owners"));
		Assert.Contains(takeover, error => error.Contains("outside your folder"));

		Git(registry, "checkout", "-q", "main");
		Git(registry, "checkout", "-q", "-b", "alice-adds-bob");
		Assert.Equal(0, Crater(lib, "owner", "add", "alice/lib", "bob", "-y").ExitCode);
		Commit(registry, "alice adds bob");

		Assert.Empty(Check(registry, "alice"));
		Assert.Equal(["bob"], Json.ReadFile<RegistryIndex>(Path.Combine(registry, "alice", "lib", "index.json")).Owners);

		Git(registry, "checkout", "-q", "main");
		Git(registry, "merge", "-q", "alice-adds-bob");

		Git(registry, "checkout", "-q", "-b", "bob-releases");
		File.WriteAllText(Path.Combine(lib, "crater.json"), File.ReadAllText(Path.Combine(lib, "crater.json")).Replace("\"1.0.0\"", "\"1.1.0\""));
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);
		Commit(registry, "bob publishes 1.1.0");

		Assert.Empty(Check(registry, "bob"));
		Assert.Contains(Check(registry, "mallory"), error => error.Contains("outside your folder"));

		Git(registry, "checkout", "-q", "main");
		Git(registry, "checkout", "-q", "-b", "bob-adds-carol");
		Assert.Equal(0, Crater(lib, "owner", "add", "alice/lib", "carol", "-y").ExitCode);
		Commit(registry, "bob adds carol");

		Assert.Contains(Check(registry, "bob"), error => error.Contains("only alice can change the owners"));

		Git(registry, "checkout", "-q", "main");
		Git(registry, "checkout", "-q", "-b", "bob-removes-himself-and-adds-mallory");
		Assert.Equal(0, Crater(lib, "owner", "remove", "alice/lib", "bob", "-y").ExitCode);
		Commit(registry, "bob edits owners");

		Assert.Contains(Check(registry, "bob"), error => error.Contains("only alice can change the owners"));

		Git(registry, "checkout", "-q", "main");
		Git(registry, "checkout", "-q", "-b", "bob-other-package");

		string other = Project("alice/other", "../registry", []);

		Assert.Equal(0, Crater(other, "publish", "-y").ExitCode);
		Commit(registry, "bob publishes another alice package");

		Assert.Contains(Check(registry, "bob"), error => error.Contains("outside your folder"));
		Assert.Empty(Check(registry, "alice"));

		Git(registry, "checkout", "-q", "main");
		Git(registry, "checkout", "-q", "-b", "old-crater-publish");
		File.WriteAllText(Path.Combine(lib, "crater.json"), File.ReadAllText(Path.Combine(lib, "crater.json")).Replace("\"1.1.0\"", "\"1.2.0\""));
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);

		string indexPath = Path.Combine(registry, "alice", "lib", "index.json");
		JsonObject stripped = JsonNode.Parse(File.ReadAllText(indexPath))!.AsObject();

		stripped.Remove("owners");
		File.WriteAllText(indexPath, stripped.ToJsonString());
		Commit(registry, "publish with an old crater");

		Assert.Contains(Check(registry, "alice"), error => error.Contains("Update crater"));
	}

	[Fact]
	public void OwnerCommandValidatesInput()
	{
		string registry = Path.Combine(root, "registry");
		Directory.CreateDirectory(registry);

		string lib = Project("alice/lib", "../registry", []);

		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);
		Assert.Equal(1, Crater(lib, "owner", "add", "alice/lib", "alice", "-y").ExitCode);
		Assert.Equal(1, Crater(lib, "owner", "add", "alice/lib", "not a login", "-y").ExitCode);
		Assert.Equal(1, Crater(lib, "owner", "add", "alice/missing", "bob", "-y").ExitCode);
		Assert.Equal(0, Crater(lib, "owner", "add", "alice/lib", "Bob", "carol", "-y").ExitCode);
		Assert.Equal(["bob", "carol"], Json.ReadFile<RegistryIndex>(Path.Combine(registry, "alice", "lib", "index.json")).Owners);

		string listed = Crater(lib, "owner", "list", "alice/lib").Output;

		Assert.Contains("bob", listed);
		Assert.Contains("carol", listed);

		Assert.Equal(0, Crater(lib, "owner", "remove", "alice/lib", "bob", "carol", "-y").ExitCode);
		Assert.Null(Json.ReadFile<RegistryIndex>(Path.Combine(registry, "alice", "lib", "index.json")).Owners);
	}

	[Theory]
	[InlineData("tools/rojo.exe", new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, true)]
	[InlineData("bin/rojo", new byte[] { 0x7F, 0x45, 0x4C, 0x46 }, true)]
	[InlineData("bin/rojo-macos", new byte[] { 0xCF, 0xFA, 0xED, 0xFE }, true)]
	[InlineData("lib/native.so", new byte[] { 0x00 }, true)]
	[InlineData("lib/native.dylib", new byte[] { 0x00 }, true)]
	[InlineData("hidden.png", new byte[] { 0x4D, 0x5A, 0x90, 0x00 }, true)]
	[InlineData("scripts/setup", new byte[] { 0x23, 0x21, 0x2F, 0x62 }, true)]
	[InlineData("Tool.app/Contents/Info.plist", new byte[] { 0x3C }, true)]
	[InlineData("src/init.luau", new byte[] { 0x4D, 0x5A, 0x20, 0x3D }, false)]
	[InlineData("README.md", new byte[] { 0x4D, 0x5A, 0x20, 0x69 }, false)]
	[InlineData("assets/icon.png", new byte[] { 0x89, 0x50, 0x4E, 0x47 }, false)]
	public void RecognizesExecutablesOfEverySystem(string path, byte[] header, bool expected)
	{
		Assert.Equal(expected, global::Crater.Core.Executables.IsExecutable(path, header));
	}

	[Fact]
	public void PackLeavesOutExecutables()
	{
		string registry = Path.Combine(root, "registry");
		Directory.CreateDirectory(registry);

		string lib = Project("t/tools", "../registry", []);

		File.WriteAllBytes(Path.Combine(lib, "rojo.exe"), [0x4D, 0x5A, 0x90, 0x00]);
		Directory.CreateDirectory(Path.Combine(lib, "bin"));
		File.WriteAllBytes(Path.Combine(lib, "bin", "rojo"), [0x7F, 0x45, 0x4C, 0x46, 0x02]);
		File.WriteAllText(Path.Combine(lib, "build.sh"), "#!/bin/sh\necho hi\n");

		(int exitCode, string output) = Crater(lib, "pack", "-o", Path.Combine(root, "tools.zip"));

		Assert.Equal(0, exitCode);
		Assert.Contains("Left out 3 executable files", output);

		using System.IO.Compression.ZipArchive zip = System.IO.Compression.ZipFile.OpenRead(Path.Combine(root, "tools.zip"));

		Assert.Equal(["crater.json", "init.luau"], zip.Entries.Select(entry => entry.FullName).Order().ToList());
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);
	}

	[Fact]
	public void MergesAConflictedLockFile()
	{
		const string conflicted = """
			{
			  "lock_version": 1,
			  "packages": [
			<<<<<<< HEAD
			    { "realm": "client", "name": "t/a", "version": "1.0.0", "sha256": "" }
			||||||| base
			=======
			    { "realm": "client", "name": "t/b", "version": "2.0.0", "sha256": "" }
			>>>>>>> branch
			  ]
			}
			""";

		string path = Path.Combine(root, "crater.lock");
		File.WriteAllText(path, conflicted);

		LockFile merged = LockFile.Load(path);

		Assert.True(merged.HadConflicts);
		Assert.Equal(["t/a@1.0.0", "t/b@2.0.0"], merged.Packages.Select(package => package.Id).Order().ToList());
	}

	[Fact]
	public void InstallRebuildsAConflictedLockAndLockedRefusesIt()
	{
		string registry = Path.Combine(root, "registry");
		Directory.CreateDirectory(registry);

		Assert.Equal(0, Crater(Project("t/a", "../registry", []), "publish", "-y").ExitCode);
		Assert.Equal(0, Crater(Project("t/b", "../registry", []), "publish", "-y").ExitCode);

		string game = Project("t/game", "../registry", ["t/a^1"]);
		string lockPath = Path.Combine(game, "crater.lock");

		Assert.Equal(0, Crater(game, "install", "-y").ExitCode);

		string ours = File.ReadAllText(lockPath);

		File.WriteAllText(Path.Combine(game, "crater.json"), File.ReadAllText(Path.Combine(game, "crater.json")).Replace("[\"t/a^1\"]", "[\"t/a^1\",\"t/b^1\"]"));
		Assert.Equal(0, Crater(game, "install", "-y").ExitCode);

		string theirs = File.ReadAllText(lockPath);

		File.WriteAllText(lockPath, $"<<<<<<< HEAD\n{ours}=======\n{theirs}>>>>>>> other\n");

		Assert.Equal(1, Crater(game, "install", "--locked", "-y").ExitCode);

		(int exitCode, string output) = Crater(game, "install", "-y");

		Assert.Equal(0, exitCode);
		Assert.Contains("merge conflicts", output);
		Assert.False(LockFile.HasConflictMarkers(File.ReadAllText(lockPath)));
		Assert.Equal(["t/a@1.0.0", "t/b@1.0.0"], LockFile.Load(lockPath).Packages.Select(package => package.Id).Order().ToList());
	}

	[Fact]
	public void PesdeLinksReExportDependencyTypes()
	{
		string registry = Path.Combine(root, "registry");
		Directory.CreateDirectory(registry);

		string dep = Project("t/dep", "../registry", [], new() { ["init.luau"] = "export type Point = { x: number }\nreturn {}\n" });

		Assert.Equal(0, Crater(dep, "publish", "-y").ExitCode);

		string pesde = Project("t/pes", "../registry", ["t/dep^1"], new()
		{
			["pesde.toml"] = "name = \"t/pes\"\nversion = \"1.0.0\"\n",
			["src/lib.luau"] = "local dep = require(\"../roblox_packages/dep\")\nexport type P = dep.Point\nreturn {}\n",
		}, main: "src/lib.luau");

		Assert.Equal(0, Crater(pesde, "publish", "-y").ExitCode);

		string game = Project("t/game", "../registry", ["t/pes^1"]);

		Assert.Equal(0, Crater(game, "install", "-y").ExitCode);

		string link = File.ReadAllText(Path.Combine(game, "Packages", "pes", "roblox_packages", "dep.luau"));

		Assert.Contains("script.Parent.Parent.Parent[\"dep\"]", link);
		Assert.Contains("export type Point = MODULE.Point", link);
	}

	[Fact]
	public void AddSaysWhenAPackageIsInSeveralRegistries()
	{
		Directory.CreateDirectory(Path.Combine(root, "first"));
		Directory.CreateDirectory(Path.Combine(root, "second"));

		string lib = Project("t/lib", "../first", []);

		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);
		File.WriteAllText(Path.Combine(lib, "crater.json"), File.ReadAllText(Path.Combine(lib, "crater.json")).Replace("../first", "../second"));
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);

		string game = Project("t/game", "../first", []);
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(game, "crater.json")))!.AsObject();

		manifest["registry"] = new JsonArray("../first", "../second");
		manifest["registries"] = new JsonObject { ["backup"] = "../second" };
		File.WriteAllText(Path.Combine(game, "crater.json"), manifest.ToJsonString());

		(int exitCode, string output) = Crater(game, "add", "t/lib", "-y");

		Assert.Equal(0, exitCode);
		Assert.Contains("t/lib is also in backup", output);
		Assert.Contains("backup#t/lib", output);
	}
}
