using Crater.Core;
using Crater.Models;
using Crater.Registry;
using Crater.Util;

namespace Crater.Commands;

public sealed class GitHubPublisher(GitHubRepository registry, Prompter prompt, bool dryRun)
{
	public async Task PublishAsync(RegistryRelease release, byte[] archive, int fileCount)
	{
		string login = GitHubCli.RequireLogin();
		string name = release.Manifest.Name;
		string id = $"{name}@{release.Version}";

		CheckScope(name, login);

		if (!prompt.Confirm($"Send {Fmt.Pkg(id)} ({Fmt.Plural(fileCount, "file", "files")}) to {Fmt.Val(registry.Slug)} for review"))
		{
			throw new CraterException("Cancelled.");
		}

		bool isOwner = registry.Owner.Equals(login, StringComparison.OrdinalIgnoreCase) || GitHubCli.HasPermission(registry.Slug, "push");
		string copy = isOwner || dryRun ? registry.Slug : EnsureCopy(login);
		string directory = PrepareWorkingCopy(copy);
		string branch = $"publish/{name.Replace('/', '-')}-{release.Version}";

		using (new Spinner($"Packing {id}"))
		{
			Shell.Run("git", ["checkout", "--quiet", "-B", branch], directory);

			LocalRegistry local = new(directory);

			await local.PublishAsync(release, archive);

			string packagePath = Path.GetRelativePath(directory, local.PackageDirectory(name)).Replace('\\', '/');

			Shell.Run("git", ["add", "--", packagePath], directory);
			Shell.Run("git", ["commit", "--quiet", "-m", $"Publish {id}"], directory);
		}

		if (dryRun)
		{
			Out.Ok($"Dry run: {Fmt.Pkg(id)} is ready, nothing was sent.");
			return;
		}

		string url;

		using (new Spinner($"Sending {id}"))
		{
			Shell.Run("git", ["push", "--quiet", "--force", "origin", branch], directory);
			url = OpenReview(branch, isOwner ? branch : $"{login}:{branch}", id);
		}

		Out.Ok($"Sent {Fmt.Pkg(id)} for review: {Fmt.File(url)}");
		Out.Info("The registry owner checks it and adds it. GitHub notifies you when it is accepted.");
	}

	void CheckScope(string name, string login)
	{
		string scope = name[..name.IndexOf('/')];

		if (scope.Equals(login, StringComparison.OrdinalIgnoreCase) || registry.Owner.Equals(login, StringComparison.OrdinalIgnoreCase) || GitHubCli.HasPermission(registry.Slug, "admin"))
		{
			return;
		}

		string expected = login.ToLowerInvariant() + name[name.IndexOf('/')..];

		throw new CraterException(
			$"You are signed in as {Fmt.Val(login)}, so the package name must start with {Fmt.Pkg(login.ToLowerInvariant() + "/")}. " +
			$"In {Fmt.File(Manifest.FileName)} set {Fmt.Val($"\"name\": \"{expected}\"")}.");
	}

	string EnsureCopy(string login)
	{
		string copy = $"{login}/{registry.Name}";
		string? parent = GitHubCli.ParentOf(copy);

		if (parent is not null && parent.Equals(registry.Slug, StringComparison.OrdinalIgnoreCase))
		{
			return copy;
		}

		if (parent is not null)
		{
			throw new CraterException(
				$"Your GitHub account already has a repository named {Fmt.Val(copy)}. crater needs that name for publishing. Rename yours on GitHub and try again.");
		}

		using (new Spinner("Setting up publishing for your GitHub account (only the first time)"))
		{
			GitHubCli.Run(["repo", "fork", registry.Slug, "--clone=false", "--default-branch-only"]);

			for (int attempt = 0; attempt < 30; attempt++)
			{
				if (GitHubCli.ParentOf(copy) is { Length: > 0 })
				{
					return copy;
				}

				Thread.Sleep(2000);
			}
		}

		throw new CraterException($"GitHub is still setting up your account. Run {Fmt.Cmd("crater publish")} again in a minute.");
	}

	string PrepareWorkingCopy(string copy)
	{
		string directory = Path.Combine(PackageCache.Root, "publish", registry.Slug.Replace('/', '_'));

		using (new Spinner($"Downloading {registry.Slug}"))
		{
			if (!Directory.Exists(Path.Combine(directory, ".git")))
			{
				Directory.CreateDirectory(directory);
				Shell.Run("git", ["init", "--quiet"], directory);
			}

			SetRemote(directory, "origin", $"https://github.com/{copy}.git");
			SetRemote(directory, "upstream", registry.CloneUrl);

			Shell.Run("git", ["fetch", "--quiet", "upstream", registry.Branch], directory);
			Shell.Run("git", ["checkout", "--quiet", "--force", "-B", registry.Branch, $"upstream/{registry.Branch}"], directory);
			Shell.Run("git", ["clean", "--quiet", "-fdx"], directory);
		}

		EnsureIdentity(directory);

		return directory;
	}

	string OpenReview(string branch, string head, string id)
	{
		ShellResult created = GitHubCli.Capture(
			["pr", "create", "--repo", registry.Slug, "--base", registry.Branch, "--head", head, "--title", $"Publish {id}", "--body", $"Publishes `{id}`."]);

		if (created.Success)
		{
			return created.Output.Trim();
		}

		ShellResult existing = GitHubCli.Capture(["pr", "view", head, "--repo", registry.Slug, "--json", "url", "--jq", ".url"]);

		if (existing.Success && existing.Output.Length > 0)
		{
			return existing.Output.Trim();
		}

		throw new CraterException($"GitHub did not accept the package: {created.Error}");
	}

	static void SetRemote(string directory, string name, string url)
	{
		ShellResult existing = Shell.Capture("git", ["remote", "get-url", name], directory);

		if (!existing.Success)
		{
			Shell.Run("git", ["remote", "add", name, url], directory);
		}
		else if (existing.Output != url)
		{
			Shell.Run("git", ["remote", "set-url", name, url], directory);
		}
	}

	static void EnsureIdentity(string directory)
	{
		ShellResult account = GitHubCli.Api(["user", "--jq", "(.id|tostring) + \" \" + .login"]);

		if (!account.Success)
		{
			return;
		}

		string[] parts = account.Output.Split(' ', 2);

		Shell.Run("git", ["config", "user.name", parts[1]], directory);
		Shell.Run("git", ["config", "user.email", $"{parts[0]}+{parts[1]}@users.noreply.github.com"], directory);
	}
}
