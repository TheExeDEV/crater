using Crater.Tools;

namespace Crater.Tests;

public sealed class ToolTests
{
	static readonly Dictionary<string, string[]> Releases = new()
	{
		["rojo"] = ["rojo-7.7.1-linux-aarch64.zip", "rojo-7.7.1-linux-x86_64.zip", "rojo-7.7.1-macos-aarch64.zip", "rojo-7.7.1-macos-x86_64.zip", "rojo-7.7.1-windows-aarch64.zip", "rojo-7.7.1-windows-x86_64.zip", "Rojo.rbxm"],
		["luau-lsp"] = ["luau-lsp-linux-arm64.zip", "luau-lsp-linux-x86_64.zip", "luau-lsp-macos.zip", "luau-lsp-win64.zip", "Luau.rbxm"],
		["selene"] = ["selene-0.32.0-linux.zip", "selene-0.32.0-macos.zip", "selene-0.32.0-windows.zip", "selene-light-0.32.0-linux.zip", "selene-light-0.32.0-macos.zip", "selene-light-0.32.0-windows.zip"],
		["stylua"] = ["stylua-linux-aarch64-musl.zip", "stylua-linux-aarch64.zip", "stylua-linux-x86_64-musl.zip", "stylua-linux-x86_64.zip", "stylua-macos-aarch64.zip", "stylua-macos-x86_64.zip", "stylua-windows-x86_64.zip"],
		["darklua"] = ["darklua-linux-aarch64.zip", "darklua-linux-x86_64.zip", "darklua-macos-aarch64.zip", "darklua-macos-x86_64.zip", "darklua-windows-x86_64.zip"],
	};

	[Theory]
	[InlineData("rojo", "windows", false, "rojo-7.7.1-windows-x86_64.zip")]
	[InlineData("rojo", "windows", true, "rojo-7.7.1-windows-aarch64.zip")]
	[InlineData("rojo", "linux", false, "rojo-7.7.1-linux-x86_64.zip")]
	[InlineData("rojo", "linux", true, "rojo-7.7.1-linux-aarch64.zip")]
	[InlineData("rojo", "macos", true, "rojo-7.7.1-macos-aarch64.zip")]
	[InlineData("rojo", "macos", false, "rojo-7.7.1-macos-x86_64.zip")]
	[InlineData("luau-lsp", "windows", false, "luau-lsp-win64.zip")]
	[InlineData("luau-lsp", "windows", true, "luau-lsp-win64.zip")]
	[InlineData("luau-lsp", "linux", true, "luau-lsp-linux-arm64.zip")]
	[InlineData("luau-lsp", "macos", true, "luau-lsp-macos.zip")]
	[InlineData("selene", "windows", false, "selene-0.32.0-windows.zip")]
	[InlineData("selene", "macos", true, "selene-0.32.0-macos.zip")]
	[InlineData("stylua", "linux", false, "stylua-linux-x86_64.zip")]
	[InlineData("stylua", "linux", true, "stylua-linux-aarch64.zip")]
	[InlineData("stylua", "windows", true, "stylua-windows-x86_64.zip")]
	[InlineData("darklua", "macos", false, "darklua-macos-x86_64.zip")]
	public void PicksTheRightBuild(string tool, string system, bool arm, string expected)
	{
		(string Name, string Url)? chosen = ToolStore.PickAsset(Releases[tool].Select(name => (name, "")), system, arm);

		Assert.Equal(expected, chosen?.Name);
	}

	[Theory]
	[InlineData("rojo", "rojo", "rojo-rbx/rojo", null)]
	[InlineData("luau-lsp@1.70.1", "luau-lsp", "JohnnyMorganz/luau-lsp", "1.70.1")]
	[InlineData("JohnnyMorganz/StyLua@v2.6.0", "stylua", "JohnnyMorganz/StyLua", "2.6.0")]
	public void ParsesToolRequests(string request, string name, string repository, string? version)
	{
		Assert.Equal((name, repository, version), ToolSpec.ParseRequest(request, Catalog()));
	}

	[Fact]
	public void RejectsUnknownToolNames()
	{
		Assert.Throws<CraterException>(() => ToolSpec.ParseRequest("notatool", Catalog()));
		Assert.Throws<CraterException>(() => ToolSpec.Parse("rojo", "rojo-rbx/rojo"));
	}

	static ToolCatalog Catalog() => ToolCatalog.Parse(
		"""
		{ "tools": {
			"rojo": { "repository": "rojo-rbx/rojo" },
			"luau-lsp": { "repository": "JohnnyMorganz/luau-lsp" },
			"odd": { "repository": "someone/odd", "assets": { "windows-x64": "odd-*-pc.zip" } }
		} }
		""", "test");

	[Fact]
	public void TheToolListInTheRepositoryIsValid()
	{
		string path = Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", "..", "tools.json");
		ToolCatalog catalog = ToolCatalog.Parse(File.ReadAllText(path), path);

		Assert.Equal("rojo-rbx/rojo", catalog.Find("rojo")?.Repository);
		Assert.True(catalog.Tools.Count >= 10);
	}

	[Fact]
	public void CatalogPatternsChooseTheAsset()
	{
		(string, string)[] assets = [("odd-1.0-windows-x86_64.zip", "a"), ("odd-1.0-pc.zip", "b")];

		Assert.Equal("odd-1.0-pc.zip", ToolStore.PickAsset(assets, Catalog().Find("odd")!.AssetPattern("windows-x64"))?.Name);
		Assert.Throws<CraterException>(() => ToolCatalog.Parse("""{ "tools": { "bad": { "repository": "nope" } } }""", "test"));
	}
}
