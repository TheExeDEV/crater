using System.IO.Compression;
using Crater.Core;
using Crater.Models;
using Crater.Registry;

namespace Crater.Tests;

public sealed class OptimizationTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-optimization-test-{Guid.NewGuid():N}");

	public OptimizationTests()
	{
		Directory.CreateDirectory(root);
	}

	public void Dispose()
	{
		Directory.Delete(root, recursive: true);
	}

	static byte[] Zip(Dictionary<string, string> files)
	{
		using MemoryStream stream = new();

		using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach ((string path, string content) in files)
			{
				using StreamWriter writer = new(archive.CreateEntry(path).Open());
				writer.Write(content);
			}
		}

		return stream.ToArray();
	}

	static Dictionary<string, string> Snapshot(string directory)
	{
		return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
			.ToDictionary(file => Path.GetRelativePath(directory, file).Replace('\\', '/'), File.ReadAllText);
	}

	public static TheoryData<string> Packages => new()
	{
		"project",
		"generic",
		"crater",
	};

	static Dictionary<string, string> Sample(string kind) => kind switch
	{
		"project" => new()
		{
			["default.project.json"] = "{\"name\":\"promise\",\"tree\":{\"$path\":\"lib\"}}",
			["lib/init.lua"] = "return {}",
			["lib/Util.lua"] = "return 1",
			["lib/init.spec.lua"] = "test",
			["lib/docs/notes.md"] = "notes",
			["modules/testez/init.lua"] = "return {}",
			["docs/intro.md"] = "docs",
			["README.md"] = "readme",
			["LICENSE"] = "MIT",
			["wally.toml"] = "[package]\nlicense = \"MIT\"\n",
			["aftman.toml"] = "[tools]",
			["types/promise.d.luau"] = "declare x: number",
		},
		"generic" => new()
		{
			["init.lua"] = "return {}",
			["Server.server.lua"] = "print(1)",
			["tests/a.lua"] = "test",
			["src/thing.spec.luau"] = "test",
			["CHANGELOG.md"] = "log",
			["LICENSE.md"] = "MIT",
			["selene.toml"] = "std = \"roblox\"",
			["wally.toml"] = "[package]",
		},
		_ => new()
		{
			["crater.json"] = "{\"name\":\"me/lib\",\"version\":\"1.0.0\",\"path_to_main\":\"src/init.luau\",\"license\":\"MIT\"}",
			["src/init.luau"] = "return {}",
			["src/inner/x.luau"] = "return 2",
			["examples/demo.luau"] = "demo",
			["README.md"] = "readme",
		},
	};

	[Theory]
	[MemberData(nameof(Packages))]
	public void FilteredExtractionMatchesFullExtraction(string kind)
	{
		byte[] archive = Zip(Sample(kind));
		Manifest manifest = new() { Name = "me/lib", Version = "1.0.0", License = "MIT" };
		string full = Path.Combine(root, "full");
		string filtered = Path.Combine(root, "filtered");

		Packer.Extract(archive, full, "me/lib@1.0.0");
		PackageFiles.Prepare("me/lib@1.0.0", full, "lib", manifest, quiet: true);
		PackageFiles.ExtractPrepared(archive, filtered, "me/lib@1.0.0", "lib", manifest, quiet: true);

		Assert.Equal(Snapshot(full), Snapshot(filtered));
	}

	[Fact]
	public void DiffShowsChangedLinesWithContext()
	{
		byte[] before = Zip(new() { ["init.luau"] = "local M = {}\nfunction M.a() end\nreturn M\n", ["old.luau"] = "x" });
		byte[] after = Zip(new() { ["init.luau"] = "local M = {}\nfunction M.a() end\nfunction M.b() end\nreturn M\n", ["new.luau"] = "y" });

		List<FileChange> changes = PackageDiff.Compare(after, before);

		Assert.Equal(ChangeKind.Changed, changes.Single(change => change.Path == "init.luau").Kind);
		Assert.Equal(ChangeKind.Added, changes.Single(change => change.Path == "new.luau").Kind);
		Assert.Equal(ChangeKind.Removed, changes.Single(change => change.Path == "old.luau").Kind);

		List<string> diff = PackageDiff.UnifiedDiff("a\nb\nc\n", "a\nB\nc\n");

		Assert.Equal([" a", "-b", "+B", " c"], diff);

		string markdown = PackageDiff.Markdown("me/lib@1.1.0", "1.0.0", changes, 100_000);

		Assert.Contains("compared with 1.0.0", markdown);
		Assert.Contains("+function M.b() end", markdown);
	}
}
