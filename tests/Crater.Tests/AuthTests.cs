namespace Crater.Tests;

[Collection("crater process")]
public sealed class AuthTests : IDisposable
{
	readonly string root = Path.Combine(Path.GetTempPath(), $"crater-auth-test-{Guid.NewGuid():N}");
	readonly Dictionary<string, string?> saved = [];

	public AuthTests()
	{
		foreach (string variable in new[] { "CRATER_CONFIG_DIR", "CRATER_GITHUB_TOKEN", "GITHUB_TOKEN", "GH_TOKEN" })
		{
			saved[variable] = Environment.GetEnvironmentVariable(variable);
			Environment.SetEnvironmentVariable(variable, null);
		}

		Environment.SetEnvironmentVariable("CRATER_CONFIG_DIR", root);
		GitHubAuth.Forget();
	}

	public void Dispose()
	{
		foreach ((string variable, string? value) in saved)
		{
			Environment.SetEnvironmentVariable(variable, value);
		}

		GitHubAuth.Forget();

		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}
	}

	[Fact]
	public void SavedSignInIsUsedForRequests()
	{
		new Credentials { Login = "someone", Token = "token-from-login", Scopes = "repo,read:org" }.Save();
		GitHubAuth.Forget();

		Assert.Equal("token-from-login", GitHubAuth.Token());
		Assert.Equal("crater login", GitHubAuth.Source());
		Assert.Equal("someone", GitHubAuth.Login());
		Assert.Equal("token token-from-login", GitHubAuth.Headers()["Authorization"]);
		Assert.True(Credentials.Load()!.CanReadPrivate);
	}

	[Fact]
	public void EnvironmentTokenWinsOverSavedSignIn()
	{
		new Credentials { Login = "someone", Token = "token-from-login", Scopes = "public_repo" }.Save();
		Environment.SetEnvironmentVariable("CRATER_GITHUB_TOKEN", "token-from-ci");
		GitHubAuth.Forget();

		Assert.Equal("token-from-ci", GitHubAuth.Token());
		Assert.Equal("CRATER_GITHUB_TOKEN", GitHubAuth.Source());
		Assert.False(Credentials.Load()!.CanReadPrivate);
	}

	[Fact]
	public void LogoutRemovesTheSavedSignIn()
	{
		new Credentials { Login = "someone", Token = "secret" }.Save();

		Assert.True(File.Exists(Credentials.FilePath));
		Assert.True(Credentials.Delete());
		Assert.False(File.Exists(Credentials.FilePath));
		Assert.Null(Credentials.Load());
	}
}
