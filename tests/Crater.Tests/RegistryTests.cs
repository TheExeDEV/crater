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

public sealed class RegistryListTests
{
	[Theory]
	[InlineData("{\"registry\": \"https://github.com/A/r\"}", new[] { "https://github.com/A/r" })]
	[InlineData("{\"registry\": [\"https://github.com/A/r\", \"https://github.com/B/s\"]}", new[] { "https://github.com/A/r", "https://github.com/B/s" })]
	[InlineData("{\"registry\": \"\"}", new string[0])]
	[InlineData("{}", new string[0])]
	public void ReadsOneOrManyRegistries(string json, string[] expected)
	{
		Crater.Models.Manifest manifest = System.Text.Json.JsonSerializer.Deserialize<Crater.Models.Manifest>(json, Json.Options)!;

		Assert.Equal(expected, manifest.RegistryLocations);
		Assert.Equal(expected.FirstOrDefault() ?? "", manifest.RegistryLocation);
	}

	[Fact]
	public void WritesASingleRegistryAsAString()
	{
		Crater.Models.Manifest single = new() { Registry = ["https://github.com/A/r"] };
		Crater.Models.Manifest many = new() { Registry = ["https://github.com/A/r", "https://github.com/B/s"] };

		Assert.Contains("\"registry\": \"https://github.com/A/r\"", Json.Serialize(single));
		Assert.Contains("\"registry\": [", Json.Serialize(many));
	}
}
