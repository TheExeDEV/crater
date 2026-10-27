using Crater.Registry;

namespace Crater.Tests;

public sealed class EmberTests
{
	const string Index = """
		["0.1.0 shared"]
		description = "Cleanup container"
		download = "https://cdn.luaupm.com/chief/bin/0.1.0.tar.gz"
		name = "chief/bin"
		published_at = "2026-07-26T05:11:58Z"
		repository = "https://github.com/ryancundiff/chief"
		target = "shared"
		type = "roblox"
		version = "0.1.0"

		["0.1.0 shared".dependencies.core]
		name = "chief/core"
		version = "^0.2"

		["0.1.0 shared".dependencies.Promise]
		name = "evaera/promise"
		version = "^4.0.0"
		index = "https://github.com/UpliftGames/wally-index"

		["0.2.0 lune"]
		download = "https://cdn.luaupm.com/chief/bin/0.2.0.tar.gz"
		target = "lune"
		type = "lune"
		version = "0.2.0"
		""";

	[Fact]
	public void ReadsRobloxVersionsAndDependencies()
	{
		RegistryIndex index = new EmberRegistry(EmberRegistry.RawIndex).ParseIndex("chief/bin", Index, "test");
		RegistryRelease release = Assert.Single(index.Versions);

		Assert.Equal("0.1.0", release.Version);
		Assert.Equal("Cleanup container", release.Manifest.Description);
		Assert.Contains("chief/core^0.2.0", release.Manifest.ImportClient);
		Assert.Contains("Promise:wally#evaera/promise^4.0.0", release.Manifest.ImportClient);
	}

	[Fact]
	public void FindsTheEntryInTheEmberManifest()
	{
		Assert.Equal("out/lpm", EmberRegistry.MainFromManifest("[target]\nenvironment = \"shared\"\nmain = \"out/lpm\"\n", "lpm.toml"));
		Assert.True(EmberRegistry.IsEmber(Registries.Expand(EmberRegistry.DefaultIndex)));
	}
}
