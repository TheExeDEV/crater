using Crater.Registry;

namespace Crater.Tests;

public sealed class RegistryTests
{
	[Theory]
	[InlineData("https://github.com/TheExeDEV/crater-registry", "https://raw.githubusercontent.com/TheExeDEV/crater-registry/main")]
	[InlineData("https://github.com/TheExeDEV/crater-registry/", "https://raw.githubusercontent.com/TheExeDEV/crater-registry/main")]
	[InlineData("https://github.com/TheExeDEV/crater-registry.git", "https://raw.githubusercontent.com/TheExeDEV/crater-registry/main")]
	[InlineData("https://github.com/TheExeDEV/crater-registry/tree/dev", "https://raw.githubusercontent.com/TheExeDEV/crater-registry/dev")]
	[InlineData("github:TheExeDEV/crater-registry", "https://raw.githubusercontent.com/TheExeDEV/crater-registry/main")]
	[InlineData("github:TheExeDEV/crater-registry@dev", "https://raw.githubusercontent.com/TheExeDEV/crater-registry/dev")]
	[InlineData("../registry", "../registry")]
	public void ExpandsRegistryLocations(string location, string expected)
	{
		Assert.Equal(expected, Registries.Expand(location));
	}

	[Fact]
	public void RejectsGitHubUrlWithoutRepository()
	{
		Assert.Throws<CraterException>(() => Registries.Expand("https://github.com/TheExeDEV"));
	}

	[Theory]
	[InlineData(">=1.0.0, <2.0.0", "^1.0.0")]
	[InlineData(">=0.2.3, <0.3.0", "^0.2.3")]
	[InlineData(">=1.2.0, <1.3.0", "~1.2.0")]
	[InlineData(">=1.0.0", "+1.0.0")]
	[InlineData("^1.2", "^1.2.0")]
	[InlineData("=1.2.3", "=1.2.3")]
	[InlineData("1.2.3", "^1.2.3")]
	[InlineData("*", "^")]
	public void ConvertsWallyRequirements(string wally, string expected)
	{
		Assert.Equal(expected, WallyRegistry.Requirement(wally));
	}

	[Fact]
	public void KnowsBuiltInRegistryNames()
	{
		Assert.Equal(NamedRegistries.WallyIndex, NamedRegistries.Location("wally", null));
		Assert.True(NamedRegistries.IsBuiltin("crater"));
		Assert.Throws<CraterException>(() => NamedRegistries.Location("nope", null));
	}
}
