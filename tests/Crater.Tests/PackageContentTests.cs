using Crater.Core;
using Crater.Models;

namespace Crater.Tests;

public sealed class PackageContentTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-content-test-{Guid.NewGuid():N}");

	public PackageContentTests()
	{
		Directory.CreateDirectory(root);
	}

	public void Dispose()
	{
		Directory.Delete(root, recursive: true);
	}

	void Write(params string[] files)
	{
		foreach (string file in files)
		{
			string path = Path.Combine(root, file);
			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllText(path, "return {}\n");
		}
	}

	[Theory]
	[InlineData(new[] { "src/init.luau", "tests/init.luau" }, "promise", "src")]
	[InlineData(new[] { "lib/Promise.lua", "lib/Util.lua" }, "promise", "lib/Promise")]
	[InlineData(new[] { "src/a.luau", "src/b.luau" }, "util", "src")]
	[InlineData(new[] { "deep/inner/init.luau", "examples/init.luau" }, "thing", "deep/inner")]
	[InlineData(new[] { "Janitor.lua", "other/init.lua" }, "janitor", "Janitor")]
	[InlineData(new[] { "packages/typed-promise/init.luau", "packages/other/init.luau" }, "typed_promise", "packages/typed-promise")]
	public void FindsTheEntryOfForeignPackages(string[] files, string name, string expected)
	{
		Write(files);

		Assert.Equal(expected, EntryDiscovery.Find(root, $"someone/{name}"));
	}

	[Fact]
	public void KeepsOnlyTheProjectPathLicenseAndManifest()
	{
		Write("lib/init.lua", "lib/Sub/init.lua", "docs/intro.md", "modules/testez/init.lua", "runTests.server.lua");
		File.WriteAllText(Path.Combine(root, "default.project.json"), "{\"name\":\"promise\",\"tree\":{\"$path\":\"lib\"}}");
		File.WriteAllText(Path.Combine(root, "LICENSE"), "MIT");
		File.WriteAllText(Path.Combine(root, "README.md"), "readme");
		File.WriteAllText(Path.Combine(root, "wally.toml"), "[package]");
		File.WriteAllText(Path.Combine(root, "aftman.toml"), "[tools]");

		PackageFiles.Prepare("evaera/promise@4.0.0", root, "Promise", new Manifest(), quiet: true);

		string[] left = Directory.EnumerateFileSystemEntries(root).Select(Path.GetFileName).OfType<string>().Order(StringComparer.Ordinal).ToArray();

		Assert.Equal(["LICENSE", "default.project.json", "lib", "wally.toml"], left);
		Assert.True(File.Exists(Path.Combine(root, "lib", "Sub", "init.lua")));
		Assert.Empty(PackageScripts.InDirectory(root));
	}

	[Fact]
	public void RemovesClutterButKeepsCodeWithoutAProjectFile()
	{
		Write("class/Signal.luau", "class/Signal.spec.luau", "tests/x.luau", "core.d.luau", "Boot.server.luau");
		File.WriteAllText(Path.Combine(root, "CHANGELOG.md"), "x");
		File.WriteAllText(Path.Combine(root, "LICENSE.md"), "MIT");
		File.WriteAllText(Path.Combine(root, "selene.toml"), "std = \"roblox\"");

		PackageFiles.Prepare("me/core@1.0.0", root, "core", new Manifest(), quiet: true);

		Assert.True(File.Exists(Path.Combine(root, "class", "Signal.luau")));
		Assert.False(File.Exists(Path.Combine(root, "class", "Signal.spec.luau")));
		Assert.False(Directory.Exists(Path.Combine(root, "tests")));
		Assert.False(File.Exists(Path.Combine(root, "CHANGELOG.md")));
		Assert.False(File.Exists(Path.Combine(root, "selene.toml")));
		Assert.True(File.Exists(Path.Combine(root, "LICENSE.md")));
		Assert.True(File.Exists(Path.Combine(root, "core.d.luau")));
		Assert.Equal(["Boot.server.luau"], PackageScripts.InDirectory(root));
	}

	[Theory]
	[InlineData("src/Boot.server.luau", true)]
	[InlineData("Client.client.lua", true)]
	[InlineData("assets/Model.rbxm", true)]
	[InlineData("src/Thing.local.luau", true)]
	[InlineData("src/init.luau", false)]
	[InlineData("src/server.luau", false)]
	public void RecognizesScripts(string path, bool isScript)
	{
		Assert.Equal(isScript, PackageScripts.IsScript(path));
	}
}
