using System.Text.Json.Nodes;

namespace Crater.Registry;

public sealed record RegistrySource(string Name, string Label, string Icon, string Homepage, string? PackageUrl, bool LiveSearch);

public interface IListedRegistry
{
	Task<Catalog?> ListAllAsync();
}

public static class RegistrySources
{
	public static IReadOnlyList<RegistrySource> All { get; } =
	[
		new("wally", "Wally", "assets/sources/wally.svg", "https://wally.run", "https://wally.run/package/{scope}/{name}", true),
		new("pesde", "pesde", "https://avatars.githubusercontent.com/u/189457840?s=64&v=4", "https://pesde.dev", null, true),
		new("forest", "Forest", "https://avatars.githubusercontent.com/u/214608400?s=64&v=4", "https://forest.dev", null, false),
		new("ember", "Ember", "https://github.com/ember-luau.png?size=64", "https://emberpm.dev", null, false),
		new("luarocks", "LuaRocks", "https://github.com/luarocks.png?size=64", "https://luarocks.org", "https://luarocks.org/search?q={name}", false),
	];

	public static JsonObject Describe(RegistrySource source, string? catalog)
	{
		return new JsonObject
		{
			["name"] = source.Name,
			["label"] = source.Label,
			["prefix"] = source.Name + "#",
			["icon"] = source.Icon,
			["homepage"] = source.Homepage,
			["package_url"] = source.PackageUrl,
			["live_search"] = source.LiveSearch,
			["catalog"] = catalog,
		};
	}
}
