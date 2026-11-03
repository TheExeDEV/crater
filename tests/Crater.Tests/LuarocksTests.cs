using Crater.Registry;

namespace Crater.Tests;

public sealed class LuarocksTests
{
	[Fact]
	public void ReadsASimpleRockspec()
	{
		Rockspec spec = Rockspec.Parse(
			"""
			package = "inspect"
			version = "3.1.3-0"
			source = {
			  url = "https://github.com/kikito/inspect.lua/archive/v3.1.3.tar.gz",
			  dir = "inspect.lua-3.1.3"
			}
			description = {
			  summary = "Lua table visualizer, ideal for debugging",
			  detailed = [[
			    inspect will print out your lua tables nicely.
			  ]],
			  license = "MIT <http://opensource.org/licenses/MIT>"
			}
			dependencies = {
			  "lua >= 5.1"
			}
			build = {
			  type = "builtin",
			  modules = {
			    inspect = "inspect.lua"
			  }
			}
			""");

		Assert.Equal("inspect", spec.Package);
		Assert.Equal("inspect.lua-3.1.3", spec.SourceDir);
		Assert.Equal("Lua table visualizer, ideal for debugging", spec.Summary);
		Assert.Equal(["lua >= 5.1"], spec.Dependencies);
		Assert.Equal("inspect.lua", spec.Modules["inspect"]);
		Assert.True(spec.IsPureLua);
	}

	[Fact]
	public void ReadsLocalsConcatenationAndComments()
	{
		Rockspec spec = Rockspec.Parse(
			"""
			local package_name = "penlight"
			local package_version = "1.14.0"
			local rockspec_revision = "3"
			local github_account_name = "lunarmodules"
			local github_repo_name = package_name
			local git_checkout = package_version == "dev" and "master" or package_version

			-- the package
			package = package_name
			version = package_version .. "-" .. rockspec_revision
			source = {
			  url = "git+https://github.com/"..github_account_name.."/"..github_repo_name..".git",
			  branch = git_checkout
			}
			dependencies = { "lua >= 5.1", "luafilesystem" }
			build = {
			  type = "builtin",
			  modules = {
			    ["pl.utils"] = "lua/pl/utils.lua",
			    ["pl.class"] = "lua/pl/class.lua",
			  },
			  copy_directories = {"docs", "tests"}
			}
			""");

		Assert.Equal("penlight", spec.Package);
		Assert.Equal("1.14.0-3", spec.Version);
		Assert.Equal("git+https://github.com/lunarmodules/penlight.git", spec.SourceUrl);
		Assert.Equal(["lua >= 5.1", "luafilesystem"], spec.Dependencies);
		Assert.Equal("lua/pl/utils.lua", spec.Modules["pl.utils"]);
		Assert.Equal(2, spec.Modules.Count);
	}

	[Fact]
	public void SpotsNativeCode()
	{
		Rockspec spec = Rockspec.Parse(
			"""
			package = "luasocket"
			version = "3.1.0-1"
			build = {
			  type = "builtin",
			  modules = {
			    ["socket.core"] = { sources = { "src/luasocket.c", "src/timeout.c" } },
			    ["socket.http"] = "src/http.lua",
			  }
			}
			""");

		Assert.False(spec.IsPureLua);
		Assert.Equal(["socket.core"], spec.NativeModules);
		Assert.False(Rockspec.Parse("package = 'lpeg'\nbuild = { type = 'make' }").IsPureLua);

		Rockspec hidden = Rockspec.Parse(
			"""
			local function make_plat(plat)
			  local modules = { ["socket.core"] = { sources = { "src/luasocket.c" } } }
			  return { modules = modules }
			end
			build = { type = "builtin", platforms = { unix = make_plat("unix") } }
			""");

		Assert.False(hidden.IsPureLua);
	}

	[Fact]
	public void TreatsInitModulesAsTheirFolder()
	{
		Rockspec spec = Rockspec.Parse(
			"""
			build = { type = "builtin", modules = {
			  ["luassert.matchers.init"] = "src/matchers/init.lua",
			  ["luassert.matchers.core"] = "src/matchers/core.lua",
			} }
			""");

		Assert.Equal("src/matchers/init.lua", spec.Modules["luassert.matchers"]);
		Assert.Equal("luassert/matchers/init.lua", LuarocksRegistry.Layout(spec.Modules.Keys)["luassert.matchers"]);
	}

	[Fact]
	public void LaysOutModulesForRoblox()
	{
		Dictionary<string, string> layout = LuarocksRegistry.Layout(["lustache", "lustache.context", "lustache.renderer", "helper"]);

		Assert.Equal("lustache/init.lua", layout["lustache"]);
		Assert.Equal("lustache/context.lua", layout["lustache.context"]);
		Assert.Equal("helper.lua", layout["helper"]);
		Assert.Equal("lustache/init.lua", LuarocksRegistry.MainModule("lustache", ["lustache", "lustache.context"]));
		Assert.Equal("", LuarocksRegistry.MainModule("lustache", ["lustache", "lustache.context", "helper"]));
		Assert.Equal("cjson.lua", LuarocksRegistry.MainModule("lua-cjson", ["cjson"]));
		Assert.Equal("inspect.lua", LuarocksRegistry.MainModule("inspect", ["inspect"]));
		Assert.Equal("", LuarocksRegistry.MainModule("penlight", ["pl.utils", "pl.class"]));
	}

	[Fact]
	public void RewritesRequiresToInstancePaths()
	{
		Dictionary<string, string> own = LuarocksRegistry.Layout(["lustache", "lustache.scanner"]);
		Dictionary<string, (string Dependency, string Path)> dependencies = new() { ["class"] = ("middleclass", "class.lua") };
		HashSet<string> missing = [];

		string text = LuarocksPackage.Rewrite(
			"""
			local Scanner = require("lustache.scanner")
			local Scanner2 = require 'lustache.scanner'
			local class = require("class")
			local meta = require "debug".getmetatable
			local socket = require("socket")
			local dynamic = require(name)
			""",
			"lustache/init.lua", own, dependencies, missing);

		Assert.Contains("local Scanner = require(script.Parent[\"lustache\"][\"scanner\"])", text);
		Assert.Contains("local Scanner2 = require(script.Parent[\"lustache\"][\"scanner\"])", text);
		Assert.Contains("local class = require(script.Parent.Parent[\"middleclass\"][\"class\"])", text);
		Assert.Contains("local meta = (debug).getmetatable", text);
		Assert.Contains("require(\"socket\")", text);
		Assert.Contains("require(name)", text);
		Assert.Equal(["socket"], missing);

		string nested = LuarocksPackage.Rewrite("return require('lustache')", "lustache/scanner.lua", own, dependencies, missing);

		Assert.Equal("return require(script.Parent.Parent[\"lustache\"])", nested);
	}

	[Theory]
	[InlineData("3.1.3-0", "3.1.3")]
	[InlineData("2.8-1", "2.8.0")]
	[InlineData("0.30-2", "0.30.0")]
	[InlineData("1-0", "1.0.0")]
	[InlineData("scm-1", null)]
	[InlineData("1.2.3.4-1", null)]
	public void MapsRockVersions(string rock, string? semver)
	{
		Assert.Equal(semver, Rockspec.ToSemVer(rock)?.ToString());
	}

	[Theory]
	[InlineData("penlight >= 1.0", "penlight", "+1.0.0")]
	[InlineData("penlight >= 1.5, < 2.0", "penlight", "^1.5.0")]
	[InlineData("dkjson ~> 2.5", "dkjson", "^2.5.0")]
	[InlineData("lume ~> 2.3.1", "lume", "~2.3.1")]
	[InlineData("serpent == 0.30", "serpent", "=0.30.0")]
	[InlineData("inspect", "inspect", "^")]
	public void MapsDependencies(string text, string name, string requirement)
	{
		Assert.Equal((name, requirement), Rockspec.ParseDependency(text));
	}
}
