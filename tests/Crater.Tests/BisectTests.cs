using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Models;

namespace Crater.Tests;

[Collection("crater process")]
public sealed class BisectTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-bisect-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public BisectTests()
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

	(int Code, string Output) CraterAll(string directory, params string[] arguments)
	{
		TextWriter output = Console.Out;
		TextWriter error = Console.Error;
		StringWriter both = new();

		Console.SetOut(both);
		Console.SetError(both);

		try
		{
			return (Crater(directory, arguments), both.ToString());
		}
		finally
		{
			Console.SetOut(output);
			Console.SetError(error);
		}
	}

	string Project(string folder, string name, string version, string[] dependencies)
	{
		string directory = Path.Combine(root, folder);
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--name", name, "-y"));

		string manifestPath = Path.Combine(directory, Manifest.FileName);
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["version"] = version;
		manifest["registry"] = "../registry";
		manifest["path_to_main"] = "init.luau";
		manifest["import_client"] = new JsonArray(dependencies.Select(item => (JsonNode)item).ToArray());
		File.WriteAllText(manifestPath, manifest.ToJsonString());
		File.WriteAllText(Path.Combine(directory, "init.luau"), $"return \"{name}@{version}\"\n");

		return directory;
	}

	void Publish(string name, string version)
	{
		Assert.Equal(0, Crater(Project($"{name.Replace('/', '_')}-{version}", name, version, []), "publish", "-y"));
	}

	[Fact]
	public async Task FindsTheOnlyFailingUpdateInFewSteps()
	{
		List<string> names = Enumerable.Range(0, 30).Select(index => $"t/p{index:00}").ToList();
		List<int> sizes = [];

		string? culprit = await Bisector.FindAsync(names, applied => Task.FromResult(!applied.Contains("t/p17")), (_, _, size) => sizes.Add(size));

		Assert.Equal("t/p17", culprit);
		Assert.True(sizes.Count <= 5);
	}

	[Fact]
	public void MixesTheGoodVersionsIntoTheCurrentLock()
	{
		LockFile good = new() { Packages = [new LockedPackage { Realm = "client", Name = "t/a", Version = "1.0.0" }, new LockedPackage { Realm = "client", Name = "t/b", Version = "1.0.0" }] };
		LockFile bad = new() { Packages = [new LockedPackage { Realm = "client", Name = "t/a", Version = "1.1.0" }, new LockedPackage { Realm = "client", Name = "t/b", Version = "2.0.0" }, new LockedPackage { Realm = "client", Name = "t/c", Version = "1.0.0" }] };

		List<LockChange> changes = Bisector.Changes(good, bad);

		Assert.Equal(["t/a", "t/b"], changes.Select(change => change.Name));

		LockFile mixed = Bisector.Mix(good, bad, ["t/b"]);

		Assert.Equal(["t/a@1.1.0", "t/b@1.0.0", "t/c@1.0.0"], mixed.Packages.Select(package => package.Id));
	}

	[Fact]
	public void BisectNamesThePackageThatBreaksTheTestAndPutsTheLockBack()
	{
		Publish("t/alpha", "1.0.0");
		Publish("t/beta", "1.0.0");

		string game = Project("game", "t/game", "1.0.0", ["t/alpha^1", "t/beta^1"]);

		Assert.Equal(0, Crater(game, "install", "-y"));

		string goodLock = Path.Combine(root, "good.lock");
		File.Copy(Path.Combine(game, LockFile.FileName), goodLock);

		Publish("t/alpha", "1.1.0");
		Publish("t/beta", "1.1.0");

		Assert.Equal(0, Crater(game, "update", "-y"));

		string badLock = File.ReadAllText(Path.Combine(game, LockFile.FileName));
		string test = "process.exit(require('fs').readFileSync('Packages/beta/init.luau','utf8').includes('1.1.0')?1:0)";

		(int code, string output) = CraterAll(game, "bisect", "--good", goodLock, "-y", "--", "node", "-e", test);

		Assert.True(code == 0, output);
		Assert.Contains("t/beta 1.0.0 → 1.1.0 breaks", output);
		Assert.DoesNotContain("t/alpha 1.0.0 → 1.1.0 breaks", output);
		Assert.Equal(badLock, File.ReadAllText(Path.Combine(game, LockFile.FileName)));
		Assert.Contains("1.1.0", File.ReadAllText(Path.Combine(game, "Packages", "beta", "init.luau")));
	}
}
