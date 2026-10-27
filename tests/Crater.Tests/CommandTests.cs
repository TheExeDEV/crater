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

		foreach (string key in new[] { "$schema", "registries", "overrides", "workspace", "tools", "block_package_scripts", "allow_scripts_from", "exclude", "allow_files", "monorepository", "monorepository-elements", "interface", "realized_interfaces" })
		{
			Assert.True(manifest.ContainsKey(key), key);
		}

		Assert.False(manifest.ContainsKey("icon"));
		Assert.True(settings.ContainsKey("link_style"));
		Assert.Equal(0, Crater("install", "-y").ExitCode);
	}

	[Fact]
	public void DeclareFileIsWrittenCheckedAndRepaired()
	{
		Assert.Equal(0, Crater("init", "--name", "t/game", "-y").ExitCode);

		string path = Path.Combine(root, ".crater-declare");
		JsonObject declared = JsonNode.Parse(File.ReadAllText(path))!.AsObject();

		Assert.Equal("crater.json", declared["path_to_package_info"]!.GetValue<string>());
		Assert.Equal("crater.settings.json", declared["path_to_crater_settings"]!.GetValue<string>());
		Assert.Equal(1, declared["api_version"]!.GetValue<int>());

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
