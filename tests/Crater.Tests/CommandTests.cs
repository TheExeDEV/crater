using System.Text.Json.Nodes;
using Crater.Commands;

namespace Crater.Tests;

[Collection("crater process")]
public sealed class CommandTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-command-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public CommandTests()
	{
		Directory.CreateDirectory(root);
		Environment.SetEnvironmentVariable("CRATER_CACHE", Path.Combine(root, "cache"));
		Environment.SetEnvironmentVariable("NO_COLOR", "1");
	}

	public void Dispose()
	{
		Environment.CurrentDirectory = previousDirectory;
		Directory.Delete(root, recursive: true);
	}

	(int ExitCode, string Output) Crater(params string[] arguments)
	{
		TextWriter previous = Console.Out;
		StringWriter output = new();

		Environment.CurrentDirectory = root;
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

	string[] Complete(params string[] arguments)
	{
		return Crater(["__complete", .. arguments]).Output.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
	}

	void Project(Action<JsonObject> edit)
	{
		Assert.Equal(0, Crater("init", "--name", "t/game", "-y").ExitCode);

		string path = Path.Combine(root, "crater.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

		edit(manifest);
		File.WriteAllText(path, manifest.ToJsonString());
	}

	[Fact]
	public void CompletesCommandsFlagsAndDependencies()
	{
		Project(manifest =>
		{
			manifest["import_client"] = new JsonArray("Promise:evaera/promise^4");
			manifest["scripts"] = new JsonObject { ["serve"] = "rojo serve" };
		});

		Assert.Equal(["info", "init", "install"], Complete("c:in"));
		Assert.Contains("--workspace", Complete("c:--", "install"));
		Assert.Equal(["Promise", "evaera/promise"], Complete("c:", "remove"));
		Assert.Equal(["Promise", "evaera/promise"], Complete("c:", "rm", "--dev"));
		Assert.Equal(["serve"], Complete("c:", "run"));
		Assert.Equal(["add", "install", "known", "list", "remove", "run"], Complete("c:", "tool"));

		Directory.CreateDirectory(Path.Combine(root, "cache"));
		File.WriteAllText(Path.Combine(root, "cache", "tools.json"), "{\"tools\":{\"rojo\":{\"repository\":\"rojo-rbx/rojo\"}}}");
		Assert.Contains("rojo", Complete("c:ro", "tool", "add"));
		Assert.Empty(Complete("c:", "version"));
	}

	[Fact]
	public void InitWritesEveryField()
	{
		Assert.Equal(0, Crater("init", "--name", "t/game", "-y").ExitCode);

		JsonObject manifest = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "crater.json")))!.AsObject();
		JsonObject settings = JsonNode.Parse(File.ReadAllText(Path.Combine(root, "crater.settings.json")))!.AsObject();

		foreach (string key in new[] { "$schema", "registries", "overrides", "workspace", "tools", "package_scripts", "package_files", "monorepository-elements", "interface", "realized_interfaces" })
		{
			Assert.True(manifest.ContainsKey(key), key);
		}

		Assert.False(manifest.ContainsKey("icon"));
		Assert.False(manifest.ContainsKey("block_package_scripts"));
		Assert.Equal("crater", manifest["registry"]!.GetValue<string>());
		Assert.True(settings.ContainsKey("link_style"));
		Assert.Equal("Packages", settings["client"]!["path"]!.GetValue<string>());
		Assert.False(settings.ContainsKey("import_client_path"));
		Assert.Equal(0, Crater("install", "-y").ExitCode);
	}

	[Fact]
	public void LabelsAndGroupsSurviveAdd()
	{
		Assert.Equal(0, Crater("init", "--name", "t/game", "-y").ExitCode);

		string path = Path.Combine(root, "crater.json");
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

		manifest["scripts"] = new JsonObject { ["build"] = "rojo build" };
		global::Crater.Models.ManifestFile.Write(path, manifest);

		string text = File.ReadAllText(path);

		Assert.Contains("\n\n  \"import_client\": [],", text);
		Assert.DoesNotContain("//", text.Replace("https://", ""));
		Assert.Contains("\"scripts\": { \"build\": \"rojo build\" },", text);
		Assert.Equal(text.TrimEnd('\n'), global::Crater.Models.ManifestFile.Format(JsonNode.Parse(text)!.AsObject()));

		global::Crater.Models.Manifest parsed = global::Crater.Models.Manifest.Load(path);

		Assert.True(parsed.Extra is null || !parsed.Extra.Keys.Any(key => key.StartsWith("//")));
	}

	[Fact]
	public void MergedFieldsAndOldFieldsMeanTheSame()
	{
		global::Crater.Models.Manifest merged = global::Crater.Util.Json.Parse<global::Crater.Models.Manifest>(
			"{ \"registry\": [\"team\", \"wally\"], \"registries\": { \"team\": \"https://github.com/Studio/team\" }, \"package_scripts\": { \"block\": true, \"allow\": [\"a/b\"] }, \"package_files\": { \"exclude\": [\"tests/\"], \"allow\": [\"run.sh\"] } }", "test");
		global::Crater.Models.Manifest old = global::Crater.Util.Json.Parse<global::Crater.Models.Manifest>(
			"{ \"registry\": \"../registry\", \"block_package_scripts\": true, \"allow_scripts_from\": [\"a/b\"], \"exclude\": [\"tests/\"], \"allow_files\": [\"run.sh\"] }", "test");

		Assert.Equal(["https://github.com/Studio/team", "https://github.com/UpliftGames/wally-index"], merged.RegistryLocations);
		Assert.Equal(["../registry"], old.RegistryLocations);

		foreach (global::Crater.Models.Manifest manifest in new[] { merged, old })
		{
			Assert.True(manifest.ScriptsBlocked);
			Assert.Equal(["a/b"], manifest.ScriptsAllowedFrom);
			Assert.Equal(["tests/"], manifest.ExcludedFiles);
			Assert.Equal(["run.sh"], manifest.AllowedFiles);
		}
	}

	[Fact]
	public void SettingsReadNewAndOldNames()
	{
		Assert.Equal(0, Crater("init", "--name", "t/game", "-y").ExitCode);

		string path = Path.Combine(root, "crater.settings.json");

		File.WriteAllText(path, "{ \"client\": { \"path\": \"Shared/Packages\", \"alias\": \"Pkg\" }, \"develop\": \"Dev\", \"luaurc\": false, \"vscode_definitions\": false, \"confirm\": false }");

		global::Crater.Models.ProjectConfig config = global::Crater.Models.ProjectConfig.Load(root);

		Assert.Equal("Shared/Packages", config.TryFolderFor(global::Crater.Models.Realm.Client));
		Assert.Equal("Pkg", config.AliasFor(global::Crater.Models.Realm.Client, "Shared/Packages"));
		Assert.Equal("Dev", config.TryFolderFor(global::Crater.Models.Realm.Develop));
		Assert.False(config.OnInstallUpdateLuaurc);
		Assert.False(config.OnInstallUpdateVscodeLuauDefinitionFiles);
		Assert.False(config.OnInstallShouldIAskYourPermissionToDoAnyAction);

		File.WriteAllText(path, "{ \"import_client_path\": \"OldPackages\", \"luaurc_path\": \"src/.luaurc\", \"on_install_should_i_ask_your_permission_to_do_any_action\": false }");
		config = global::Crater.Models.ProjectConfig.Load(root);

		Assert.Equal("OldPackages", config.TryFolderFor(global::Crater.Models.Realm.Client));
		Assert.Equal("src/.luaurc", config.LuaurcPath);
		Assert.False(config.OnInstallShouldIAskYourPermissionToDoAnyAction);
	}

	[Fact]
	public void DeclareFileIsWrittenCheckedAndRepaired()
	{
		Assert.Equal(0, Crater("init", "--name", "t/game", "-y").ExitCode);

		string path = Path.Combine(root, ".crater-declare");
		JsonObject declared = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

		Assert.Equal("crater.json", declared["path_to_package_info"]!.GetValue<string>());
		Assert.Equal("crater.settings.json", declared["path_to_crater_settings"]!.GetValue<string>());
		Assert.Equal(3, declared["api_version"]!.GetValue<int>());
		Assert.Equal(3, declared["features"]!["package_rules"]!.GetValue<int>());
		Assert.Equal("https://github.com/UpliftGames/wally-index", declared["registries"]!["wally"]!.GetValue<string>());
		Assert.False(declared["defaults"]!["package_scripts"]!["block"]!.GetValue<bool>());
		Assert.Matches(declared["dependency"]!["pattern"]!.GetValue<string>(), "Promise:wally#evaera/promise^4");

		if (OperatingSystem.IsWindows())
		{
			Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.Hidden));
		}

		Assert.Null(global::Crater.Core.CraterDeclare.Problem(root));

		File.SetAttributes(path, FileAttributes.Normal);
		File.WriteAllText(path, "{\"path_to_crater_settings\": \"gone.json\", \"path_to_package_info\": \"crater.json\"}");
		Assert.Contains("gone.json", global::Crater.Core.CraterDeclare.Problem(root));

		Assert.Equal(0, Crater("list").ExitCode);
		Assert.Null(global::Crater.Core.CraterDeclare.Problem(root));

		File.Delete(path);
		Assert.Equal(0, Crater("list").ExitCode);
		Assert.True(File.Exists(path));
	}

	[Fact]
	public void AuditNeedsAnInstall()
	{
		Project(_ => { });
		Assert.Equal(0, Crater("audit", "--strict").ExitCode);

		Project(manifest => manifest["import_client"] = new JsonArray("Promise:evaera/promise^4"));

		Assert.Equal(1, Crater("audit").ExitCode);
	}

	[Fact]
	public void CompletionBlockIsReplacedNotAddedTwice()
	{
		string profile = Path.Combine(root, "profile.ps1");

		File.WriteAllText(profile, "Set-Alias g git\n");

		CompletionCommand.WriteBlock(profile, Crater("completion", "powershell").Output);
		CompletionCommand.WriteBlock(profile, Crater("completion", "powershell").Output);

		string text = File.ReadAllText(profile);

		Assert.StartsWith("Set-Alias g git", text);
		Assert.Single(text.Split("Register-ArgumentCompleter")[1..]);
		Assert.Equal(1, Crater("completion", "tcsh").ExitCode);
	}
}
