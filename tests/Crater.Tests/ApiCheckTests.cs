using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Models;
using Crater.Versioning;

namespace Crater.Tests;

[Collection("crater process")]
public sealed class ApiCheckTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-api-test-{Guid.NewGuid():N}");
	readonly string previousDirectory = Environment.CurrentDirectory;

	public ApiCheckTests()
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

	const string Module = """
		--!strict
		export type Options = { retries: number }
		export type Result<T, E = string> = { value: T?, error: E? }

		local function helper(a: number, b: string?)
			return a
		end

		local Signal = {}
		Signal.__index = Signal
		Signal.Version = "1.0.0"
		Signal.connect = helper

		function Signal.new(name: string, options: Options?)
			local self = setmetatable({}, Signal)
			local mode = if name == "" then "anonymous" else "named"
			function self.inner() end
			return self
		end

		function Signal:fire(...: any)
			for _, listener in self.listeners do
				listener(...)
			end
		end

		function Signal:_private() end

		return Signal
		""";

	[Fact]
	public void ReadsTheFunctionsFieldsAndTypesOfAModule()
	{
		PublicApi api = PublicApi.FromSource(Module);

		Assert.False(api.Opaque);
		Assert.Equal(["Version", "connect", "fire", "new", "type Options", "type Result"], api.Members.Keys.Order(StringComparer.Ordinal));
		Assert.Equal(ApiKind.Value, api.Members["Version"].Kind);
		Assert.Equal(ApiKind.Function, api.Members["connect"].Kind);
		Assert.Equal([true, false], api.Members["new"].Parameters.Select(parameter => parameter.Required));
		Assert.Equal(1, api.Members["type Result"].RequiredTypeParameters);
	}

	[Fact]
	public void ReadsATableReturnedDirectly()
	{
		PublicApi api = PublicApi.FromSource("local function sum(a: number, b: number) return a + b end\nreturn { sum = sum, zero = 0, double = function(x: number) return x * 2 end }\n");

		Assert.Equal(["double", "sum", "zero"], api.Members.Keys.Order(StringComparer.Ordinal));
		Assert.Equal(2, api.Members["sum"].Parameters.Count);
	}

	[Fact]
	public void FindsBreakingChanges()
	{
		PublicApi before = PublicApi.FromSource("local M = {}\nfunction M.a(x: number) end\nfunction M.b() end\nfunction M.c(x: number, y: number?) end\nreturn M\n");
		PublicApi after = PublicApi.FromSource("local M = {}\nfunction M.a(x: number, y: string) end\nfunction M.c(x: number, y: number) end\nfunction M.d() end\nM.e = 1\nreturn M\n");

		List<ApiChange> changes = ApiDiff.Compare(before, after);

		Assert.Contains(changes, change => change.Breaking && change.Text == "removed b()");
		Assert.Contains(changes, change => change.Breaking && change.Text == "a() has a new required parameter y");
		Assert.Contains(changes, change => change.Breaking && change.Text == "parameter y of c() is now required");
		Assert.Contains(changes, change => !change.Breaking && change.Text == "added d()");
		Assert.Contains(changes, change => !change.Breaking && change.Text == "added e");
	}

	[Fact]
	public void OptionalAdditionsAreNotBreaking()
	{
		PublicApi before = PublicApi.FromSource("local M = {}\nfunction M.a(x: number) end\nreturn M\n");
		PublicApi after = PublicApi.FromSource("local M = {}\nfunction M.a(x: number, y: string?, z) end\nreturn M\n");

		Assert.Empty(ApiDiff.Compare(before, after));
	}

	[Theory]
	[InlineData("1.2.0", "1.3.0", true, "2.0.0")]
	[InlineData("1.2.0", "2.0.0", false, "2.0.0")]
	[InlineData("0.2.3", "0.2.4", true, "0.3.0")]
	[InlineData("0.2.3", "0.3.0", false, "0.3.0")]
	[InlineData("0.0.3", "0.0.4", false, "0.0.4")]
	public void KnowsWhichVersionsMayBreak(string previous, string next, bool compatible, string suggested)
	{
		ApiVerdict verdict = ApiCheck.Judge(SemVer.Parse(previous), SemVer.Parse(next), [new ApiChange("removed a()", true)]);

		Assert.Equal(compatible, verdict.Compatible);
		Assert.Equal(suggested, verdict.Suggested.ToString());
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

	string Library(string version, string source)
	{
		string directory = Path.Combine(root, $"lib-{version}");
		Directory.CreateDirectory(directory);

		Assert.Equal(0, Crater(directory, "init", "--name", "t/lib", "-y"));

		string manifestPath = Path.Combine(directory, Manifest.FileName);
		JsonObject manifest = JsonNode.Parse(File.ReadAllText(manifestPath))!.AsObject();

		manifest["version"] = version;
		manifest["registry"] = "../registry";
		manifest["path_to_main"] = "init.luau";
		File.WriteAllText(manifestPath, manifest.ToJsonString());
		File.WriteAllText(Path.Combine(directory, "init.luau"), source);

		return directory;
	}

	[Fact]
	public void PublishStopsABreakingMinorRelease()
	{
		Assert.Equal(0, Crater(Library("1.0.0", "local M = {}\nfunction M.a() end\nfunction M.b() end\nreturn M\n"), "publish", "-y"));

		string breaking = Library("1.1.0", "local M = {}\nfunction M.a() end\nreturn M\n");

		(int code, string errors) = CraterErrors(breaking, "publish", "-y");

		Assert.NotEqual(0, code);
		Assert.Contains("Publish it as 2.0.0", errors);

		Assert.Equal(0, Crater(breaking, "publish", "-y", "--allow-breaking"));
		Assert.Equal(0, Crater(Library("2.0.0", "local M = {}\nreturn M\n"), "publish", "-y"));
	}
}
