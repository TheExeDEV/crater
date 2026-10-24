using Crater.Versioning;

namespace Crater.Tests;

public sealed class VersioningTests
{
	[Theory]
	[InlineData("0.0.8", "patch", "0.0.9")]
	[InlineData("0.0.9", "patch", "0.1.0")]
	[InlineData("0.9.9", "patch", "1.0.0")]
	[InlineData("0.1.4", "minor", "0.2.0")]
	[InlineData("0.9.3", "minor", "1.0.0")]
	[InlineData("1.2.3", "major", "2.0.0")]
	[InlineData("1.0.0-rc.1", "patch", "1.0.0")]
	public void BumpRollsOverAfterNine(string current, string part, string expected)
	{
		Assert.Equal(expected, SemVer.Parse(current).Next(part).ToString());
	}

	[Fact]
	public void BumpRejectsUnknownPart()
	{
		Assert.Throws<CraterException>(() => SemVer.Parse("1.0.0").Next("huge"));
	}

	[Theory]
	[InlineData("1.0", "write '1.0.0'")]
	[InlineData("abc", "is not a version")]
	public void ExplainsBadVersions(string text, string expected)
	{
		Assert.Contains(expected, SemVer.Explain(text));
	}

	[Theory]
	[InlineData("a/b^1.2.3", "1.2.3", true)]
	[InlineData("a/b^1.2.3", "1.9.0", true)]
	[InlineData("a/b^1.2.3", "2.0.0", false)]
	[InlineData("a/b^0.2.3", "0.3.0", false)]
	[InlineData("a/b~1.2.3", "1.2.9", true)]
	[InlineData("a/b~1.2.3", "1.3.0", false)]
	[InlineData("a/b=1.2.3", "1.2.4", false)]
	[InlineData("a/b+1.2.3", "5.0.0", true)]
	[InlineData("a/b@1.2.3", "1.5.0", true)]
	[InlineData("a/b@1.2.3", "2.0.0", false)]
	[InlineData("a/b@=1.2.3", "1.2.4", false)]
	public void RequirementsMatchVersions(string spec, string version, bool matches)
	{
		Assert.Equal(matches, DependencySpec.Parse(spec).Req.Matches(SemVer.Parse(version)));
	}

	[Fact]
	public void ParsesAliasRegistryAndVersion()
	{
		DependencySpec spec = DependencySpec.Parse("Knit:wally#sleitnick/knit^1");

		Assert.Equal("Knit", spec.Alias);
		Assert.Equal("wally", spec.RegistryName);
		Assert.Equal("sleitnick/knit", spec.FullName);
		Assert.Equal("Knit", spec.LinkName);
		Assert.Equal("Knit:wally#sleitnick/knit^1", spec.ToString());
	}

	[Fact]
	public void AtVersionIsStoredAsCaret()
	{
		Assert.Equal("ifmnb/funny-core^0.1.0", DependencySpec.Parse("ifmnb/funny-core@0.1.0").ToString());
	}

	[Fact]
	public void ExplainsCaretEatenByWindowsTerminal()
	{
		CraterException error = Assert.Throws<CraterException>(() => DependencySpec.Parse("ifmnb/funny-core0.1.0"));

		Assert.Contains("ifmnb/funny-core@0.1.0", error.Message);
	}

	[Fact]
	public void PathSourcesStillWork()
	{
		DependencySpec spec = DependencySpec.Parse("me/lib@path:../lib");

		Assert.NotNull(spec.Source);
		Assert.Equal(SourceKind.Path, spec.Source.Kind);
	}
}
