using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Models;
using Crater.Util;
using CraterProject = Crater.Core.Project;

namespace Crater.Tests;

[Collection("crater process")]
public sealed class VendorTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-vendor-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public VendorTests()
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

	(int Code, string Errors) CraterErrors(string directory, params string[] arguments)
	{
		TextWriter previous = Console.Error;
		StringWriter errors = new();

		Console.SetError(errors);

		try
		{
			return (Crater(directory, arguments), errors.ToString());
		}
		finally
		{
			Console.SetError(previous);
		}
	}

	string Project(string folder, string name, string version, Action<JsonObject> configure, Dictionary<string, string>? files = null)
	{
		string directory = Path.Combine(root, folder);
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--name", name, "-y"));

		string manifestPath = Path.Combine(directory, Manifest.FileName);
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["version"] = version;
		manifest["registry"] = "../registry";
		manifest["path_to_main"] = "init.luau";
		configure(manifest);
		File.WriteAllText(manifestPath, manifest.ToJsonString());

		foreach ((string path, string content) in files ?? new() { ["init.luau"] = "return {}\n" })
		{
			string file = Path.Combine(directory, path);
			Directory.CreateDirectory(Path.GetDirectoryName(file)!);
			File.WriteAllText(file, content);
		}

		return directory;
	}

	static JsonArray List(params string[] items) => new(items.Select(item => (JsonNode)item).ToArray());

	static Action<JsonObject> Imports(params string[] dependencies) => manifest => manifest["import_client"] = List(dependencies);

	static Action<JsonObject> Vendors(params string[] dependencies) => manifest => manifest["vendor_packages"] = new JsonObject { ["client"] = List(dependencies) };

	void Publish(string name, string version, params string[] dependencies)
	{
		Dictionary<string, string> files = new()
		{
			["init.luau"] = $"return \"{name}@{version}\"\n",
			["LICENSE"] = $"MIT License for {name}\n",
		};

		string directory = Project($"{name.Replace('/', '_')}-{version}", name, version, Imports(dependencies), files);

		Assert.Equal(0, Crater(directory, "publish", "-y"));
	}

	static List<string> Installed(string folder)
	{
		return PackageLayout.ReadMarker(folder).Keys.Where(key => !key.StartsWith('#')).Order(StringComparer.Ordinal).ToList();
	}

	List<string> Pack(string directory)
	{
		string output = Path.Combine(root, $"{Guid.NewGuid():N}.zip");

		Assert.Equal(0, Crater(directory, "pack", "-o", output, "-y"));

		return Packer.ListEntries(File.ReadAllBytes(output));
	}

	[Fact]
	public void VendoredPackagesInstallIntoTheVendorFolderAndTheirOwnLockSection()
	{
		Publish("t/promise", "1.0.0");

		string library = Project("lib", "t/lib", "1.0.0", Vendors("t/promise^1"));

		Assert.Equal(0, Crater(library, "install", "-y"));

		Assert.Equal(new List<string> { "t/promise@1.0.0" }, Installed(Path.Combine(library, "_vendor", "client")));
		Assert.False(Directory.Exists(Path.Combine(library, "Packages")));

		LockFile lockFile = LockFile.Load(Path.Combine(library, LockFile.FileName));

		Assert.Empty(lockFile.Packages);
		Assert.Equal("t/promise@1.0.0", Assert.Single(lockFile.Vendor!).Id);
		Assert.True(InstallState.IsCurrent(CraterProject.Load(library), lockFile));

		List<string> entries = Pack(library);

		Assert.Contains(entries, entry => entry.StartsWith("_vendor/client/", StringComparison.Ordinal) && entry.EndsWith("/LICENSE", StringComparison.Ordinal));
		Assert.Contains(entries, entry => entry.StartsWith("_vendor/client/", StringComparison.Ordinal) && entry.EndsWith("/init.luau", StringComparison.Ordinal));
		Assert.DoesNotContain(entries, entry => entry.EndsWith("/" + PackageLayout.MarkerFile, StringComparison.Ordinal));
	}

	[Fact]
	public void ConsumersDoNotInstallVendoredPackagesAndMayUseAnotherVersion()
	{
		Publish("t/promise", "1.0.0");
		Publish("t/promise", "2.0.0");

		string library = Project("lib", "t/lib", "1.0.0", Vendors("t/promise^1"));

		Assert.Equal(0, Crater(library, "install", "-y"));
		Assert.Equal(0, Crater(library, "publish", "-y"));

		string game = Project("game", "t/game", "1.0.0", Imports("t/lib^1", "t/promise^2"));

		Assert.Equal(0, Crater(game, "install", "-y"));

		LockFile lockFile = LockFile.Load(Path.Combine(game, LockFile.FileName));

		Assert.Equal(new List<string> { "t/lib@1.0.0", "t/promise@2.0.0" }, lockFile.Packages.Select(package => package.Id).Order(StringComparer.Ordinal).ToList());
		Assert.Null(lockFile.Vendor);

		string vendored = Path.Combine(game, "Packages", "lib", "_vendor", "client");
		string copy = Directory.EnumerateFiles(vendored, "init.luau", SearchOption.AllDirectories).Single();

		Assert.Contains("t/promise@1.0.0", File.ReadAllText(copy));
		Assert.False(Directory.Exists(Path.Combine(game, "_vendor")));
	}

	[Fact]
	public void TransitiveDependenciesAreVendoredToo()
	{
		Publish("t/promise", "1.0.0");
		Publish("t/signal", "1.0.0", "t/promise^1");

		string library = Project("lib", "t/lib", "1.0.0", Vendors("t/signal^1"));

		Assert.Equal(0, Crater(library, "install", "-y"));

		Assert.Equal(new List<string> { "t/promise@1.0.0", "t/signal@1.0.0" }, Installed(Path.Combine(library, "_vendor", "client")));

		List<string> licenses = Pack(library).Where(entry => entry.StartsWith("_vendor/client/", StringComparison.Ordinal) && entry.EndsWith("/LICENSE", StringComparison.Ordinal)).ToList();

		Assert.Equal(2, licenses.Count);
	}

	[Fact]
	public void ConflictingTransitiveVersionsAreAClearError()
	{
		Publish("t/promise", "1.0.0");
		Publish("t/promise", "2.0.0");
		Publish("t/a", "1.0.0", "t/promise^1");
		Publish("t/b", "1.0.0", "t/promise^2");

		string library = Project("lib", "t/lib", "1.0.0", Vendors("t/a^1", "t/b^1"));

		(int code, string errors) = CraterErrors(library, "install", "-y");

		Assert.NotEqual(0, code);
		Assert.Contains("vendor_packages.client", errors);
		Assert.Contains("t/promise", errors);
	}

	[Fact]
	public void ProjectsWithoutTheFieldKeepTheirLockFormat()
	{
		Publish("t/alpha", "1.0.0");

		string game = Project("game", "t/game", "1.0.0", Imports("t/alpha^1"));

		Assert.Equal(0, Crater(game, "install", "-y"));

		string text = File.ReadAllText(Path.Combine(game, LockFile.FileName));

		Assert.DoesNotContain("\"vendor\"", text);
		Assert.False(Directory.Exists(Path.Combine(game, "_vendor")));

		LockFile oldLock = Json.Parse<LockFile>("{\"lock_version\": 1, \"packages\": []}", LockFile.FileName);

		Assert.Null(oldLock.Vendor);
		Assert.Empty(oldLock.VendorFor(Realm.Client));
	}

	[Fact]
	public void TheSameDependencyCannotBeVendoredAndImported()
	{
		Publish("t/promise", "1.0.0");

		string library = Project("lib", "t/lib", "1.0.0", manifest =>
		{
			Imports("t/promise^1")(manifest);
			Vendors("t/promise^1")(manifest);
		});

		(int code, string errors) = CraterErrors(library, "install", "-y");

		Assert.NotEqual(0, code);
		Assert.Contains("vendored", errors);
	}

	[Fact]
	public void SharedIsNotARealm()
	{
		string library = Project("lib", "t/lib", "1.0.0", manifest => manifest["vendor_packages"] = new JsonObject { ["shared"] = List("t/promise^1") });

		(int code, string errors) = CraterErrors(library, "install", "-y");

		Assert.NotEqual(0, code);
		Assert.Contains("vendor_packages.client", errors);
	}

	[Fact]
	public void AFailedPrepackScriptStopsPacking()
	{
		string library = Project("lib", "t/lib", "1.0.0", manifest => manifest["scripts"] = new JsonObject { ["prepack"] = "exit 3" });
		string output = Path.Combine(root, "lib.zip");

		(int code, string errors) = CraterErrors(library, "pack", "-o", output, "-y");

		Assert.NotEqual(0, code);
		Assert.Contains("prepack", errors);
		Assert.False(File.Exists(output));
	}

	[Fact]
	public void PackageRootPacksTheBuiltFolder()
	{
		string library = Project("lib", "t/lib", "1.0.0", manifest => manifest["package_root"] = "bin/core", new()
		{
			["src/init.luau"] = "return {}\n",
			["bin/core/init.luau"] = "return {}\n",
			["LICENSE"] = "MIT\n",
			[".gitignore"] = "bin/\n",
		});

		List<string> entries = Pack(library);

		Assert.Contains("init.luau", entries);
		Assert.Contains(Manifest.FileName, entries);
		Assert.Contains("LICENSE", entries);
		Assert.DoesNotContain(entries, entry => entry.StartsWith("src/", StringComparison.Ordinal));
	}
}
