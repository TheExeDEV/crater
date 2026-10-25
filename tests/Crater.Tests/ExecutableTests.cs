using System.IO.Compression;
using System.Text;
using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Registry;

namespace Crater.Tests;

[Collection("crater process")]
public sealed class ExecutableTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-exec-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public ExecutableTests()
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

	string Package(string[] allowFiles, Dictionary<string, byte[]> files)
	{
		string directory = Path.Combine(root, "lib");
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--name", "t/lib", "-y").ExitCode);

		string manifestPath = Path.Combine(directory, "crater.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["version"] = "1.0.0";
		manifest["registry"] = "../registry";
		manifest["path_to_main"] = "init.luau";
		manifest["allow_files"] = new JsonArray(allowFiles.Select(item => (JsonNode)item).ToArray());
		File.WriteAllText(manifestPath, manifest.ToJsonString());
		File.WriteAllText(Path.Combine(directory, "init.luau"), "return {}\n");

		foreach ((string path, byte[] content) in files)
		{
			string file = Path.Combine(directory, path);
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllBytes(file, content);
		}

		return directory;
	}

	static byte[] Text(string text) => Encoding.UTF8.GetBytes(text);

	List<string> Packed(string directory)
	{
		string output = Path.Combine(root, "out.zip");

		Assert.Equal(0, Crater(directory, "pack", "-o", output).ExitCode);

		using ZipArchive zip = ZipFile.OpenRead(output);

		return zip.Entries.Select(entry => entry.FullName).Order(StringComparer.Ordinal).ToList();
	}

	[Theory]
	[InlineData("level.gpp", "level data", ProgramKind.None)]
	[InlineData("native/thing.cpp", "int main() { return 0; }", ProgramKind.None)]
	[InlineData("scripts/setup.sh", "#!/bin/sh\necho hi\n", ProgramKind.Script)]
	[InlineData("build.bat", "@echo off\n", ProgramKind.Script)]
	[InlineData("tools/run", "#!/usr/bin/env lune\n", ProgramKind.Script)]
	[InlineData("tools/notes", "just some notes", ProgramKind.None)]
	public void ClassifiesTextFiles(string path, string content, ProgramKind expected)
	{
		Assert.Equal(expected, Executables.Classify(path, Text(content)));
	}

	[Theory]
	[InlineData("level.gpp", new byte[] { 0x4D, 0x5A, 0x90, 0x00 })]
	[InlineData("setup.sh", new byte[] { 0x7F, 0x45, 0x4C, 0x46 })]
	[InlineData("setup.sh", new byte[] { 0x23, 0x21, 0x00, 0x01 })]
	[InlineData("module.luau.exe", new byte[] { 0x72 })]
	[InlineData("thing.wasm", new byte[] { 0x00, 0x61, 0x73, 0x6D })]
	public void DisguisedProgramsAreStillBinaries(string path, byte[] content)
	{
		Assert.Equal(ProgramKind.Binary, Executables.Classify(path, content));
	}

	[Fact]
	public void AnyNonProgramFileIsPublished()
	{
		string lib = Package([], new()
		{
			["data/level.gpp"] = Text("level data"),
			["native/thing.cpp"] = Text("int main() { return 0; }"),
			["assets/icon.png"] = [0x89, 0x50, 0x4E, 0x47],
			["notes.weird-extension"] = Text("hello"),
		});

		Assert.Equal(["assets/icon.png", "crater.json", "data/level.gpp", "init.luau", "native/thing.cpp", "notes.weird-extension"], Packed(lib));
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);
	}

	[Fact]
	public void AllowFilesKeepsScriptsButNeverPrograms()
	{
		string lib = Package(["scripts/*.sh", "*.exe", "bin/", "fake.gpp"], new()
		{
			["scripts/setup.sh"] = Text("#!/bin/sh\necho hi\n"),
			["other.sh"] = Text("#!/bin/sh\necho no\n"),
			["tool.exe"] = [0x4D, 0x5A, 0x90, 0x00],
			["bin/rojo"] = [0x7F, 0x45, 0x4C, 0x46],
			["fake.gpp"] = [0x4D, 0x5A, 0x90, 0x00],
			["scripts/sneaky.sh"] = [0x7F, 0x45, 0x4C, 0x46],
		});

		(int _, string output) = Crater(lib, "pack", "-o", Path.Combine(root, "check.zip"));

		Assert.Contains("allow_files", output);
		Assert.Equal(["crater.json", "init.luau", "scripts/setup.sh"], Packed(lib));
		Assert.Equal(0, Crater(lib, "publish", "-y").ExitCode);
	}

	[Fact]
	public void RegistryCheckBlocksProgramsInAHandMadeArchive()
	{
		using MemoryStream stream = new();

		using (ZipArchive zip = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			Add(zip, "crater.json", Text("{\"name\":\"t/evil\",\"version\":\"1.0.0\",\"allow_files\":[\"*\"]}"));
			Add(zip, "init.luau", Text("return {}"));
			Add(zip, "setup.sh", Text("#!/bin/sh\necho allowed\n"));
			Add(zip, "payload.gpp", [0x4D, 0x5A, 0x90, 0x00]);
			Add(zip, "run.sh", [0x7F, 0x45, 0x4C, 0x46]);
		}

		List<Finding> findings = PackageScanner.ScanArchive(stream.ToArray(), "t/evil@1.0.0");

		Assert.Contains(findings, finding => finding.File == "payload.gpp" && finding.Severity == Severity.Blocked);
		Assert.Contains(findings, finding => finding.File == "run.sh" && finding.Severity == Severity.Blocked);
		Assert.Contains(findings, finding => finding.File == "setup.sh" && finding.Severity == Severity.Warning);

		using MemoryStream noAllow = new();

		using (ZipArchive zip = new(noAllow, ZipArchiveMode.Create, leaveOpen: true))
		{
			Add(zip, "crater.json", Text("{\"name\":\"t/evil\",\"version\":\"1.0.0\"}"));
			Add(zip, "setup.sh", Text("#!/bin/sh\necho not allowed\n"));
		}

		Assert.Contains(PackageScanner.ScanArchive(noAllow.ToArray(), "t/evil@1.0.0"), finding => finding.File == "setup.sh" && finding.Severity == Severity.Blocked);
	}

	static void Add(ZipArchive zip, string path, byte[] content)
	{
		using Stream entry = zip.CreateEntry(path).Open();

		entry.Write(content);
	}
}
