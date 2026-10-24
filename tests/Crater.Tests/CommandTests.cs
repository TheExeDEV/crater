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
		Assert.Equal(["add", "install", "list", "remove", "run"], Complete("c:", "tool"));
		Assert.Contains("rojo", Complete("c:ro", "tool", "add"));
		Assert.Empty(Complete("c:", "version"));
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

	[Fact]
	public void TestUsesTheTestScriptOrSaysThereAreNoTests()
	{
		Project(_ => { });

		(int exitCode, string output) = Crater("test");

		Assert.Equal(0, exitCode);
		Assert.Contains("No tests found", output);

		Project(manifest => manifest["scripts"] = new JsonObject { ["test"] = "exit 3" });

		Assert.Equal(3, Crater("test").ExitCode);
	}
}
