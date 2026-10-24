using Crater.Core;

namespace Crater.Tests;

public sealed class IgnoreRulesTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-ignore-test-{Guid.NewGuid():N}");

	public IgnoreRulesTests()
	{
		Directory.CreateDirectory(root);
	}

	public void Dispose()
	{
		Directory.Delete(root, recursive: true);
	}

	IgnoreRules Rules(string gitignore, string craterignore = "")
	{
		File.WriteAllText(Path.Combine(root, ".gitignore"), gitignore);
		File.WriteAllText(Path.Combine(root, ".craterignore"), craterignore);

		return new IgnoreRules().With(root, root);
	}

	[Theory]
	[InlineData("*.log", "debug.log", false, true)]
	[InlineData("*.log", "src/deep/debug.log", false, true)]
	[InlineData("*.log", "src/init.luau", false, false)]
	[InlineData("/build", "build", true, true)]
	[InlineData("/build", "src/build", true, false)]
	[InlineData("build/", "src/build", true, true)]
	[InlineData("build/", "build", false, false)]
	[InlineData("docs/*.md", "docs/a.md", false, true)]
	[InlineData("docs/*.md", "docs/deep/a.md", false, false)]
	[InlineData("**/tests", "a/b/tests", true, true)]
	[InlineData("src/**/*.spec.luau", "src/a/b/x.spec.luau", false, true)]
	[InlineData("src/**/*.spec.luau", "src/x.spec.luau", false, true)]
	[InlineData("assets/**", "assets/img/a.png", false, true)]
	[InlineData("file?.luau", "file1.luau", false, true)]
	[InlineData("file[0-9].luau", "file7.luau", false, true)]
	[InlineData("file[!0-9].luau", "file7.luau", false, false)]
	[InlineData("# just a comment", "# just a comment", false, false)]
	public void MatchesLikeGit(string pattern, string path, bool isDirectory, bool ignored)
	{
		Assert.Equal(ignored, Rules(pattern + "\n").IsIgnored(path, isDirectory));
	}

	[Fact]
	public void LaterNegationReincludesAndCraterignoreWins()
	{
		IgnoreRules rules = Rules("*.log\n!keep.log\nbuild/\n", "!build/\nsecret.luau\n");

		Assert.True(rules.IsIgnored("other.log", false));
		Assert.False(rules.IsIgnored("keep.log", false));
		Assert.False(rules.IsIgnored("build", true));
		Assert.True(rules.IsIgnored("src/secret.luau", false));
	}

	[Fact]
	public void NestedFilesApplyOnlyBelowTheirFolder()
	{
		Directory.CreateDirectory(Path.Combine(root, "src"));
		File.WriteAllText(Path.Combine(root, "src", ".gitignore"), "*.luau\n!init.luau\n");

		IgnoreRules nested = Rules("").With(root, Path.Combine(root, "src"));

		Assert.True(nested.IsIgnored("src/util.luau", false));
		Assert.False(nested.IsIgnored("src/init.luau", false));
		Assert.False(nested.IsIgnored("other.luau", false));
	}
}
