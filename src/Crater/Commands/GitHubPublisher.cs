using System.Text.Json.Nodes;
using Crater.Models;
using Crater.Registry;
using Crater.Util;

namespace Crater.Commands;

public sealed class GitHubPublisher(GitHubRepository registry, Prompter prompt, bool dryRun)
{
	sealed record PackageFile(string Path, byte[] Content);

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

		string head;
		List<PackageFile> files;

		using (new Spinner($"Preparing {id}"))
		{
			head = Read($"repos/{registry.Slug}/git/ref/heads/{registry.Branch}", ".object.sha");
			files = await BuildFilesAsync(release, archive, head);
		}

		if (dryRun)
		{
			Out.Ok($"Dry run: {Fmt.Pkg(id)} is ready, nothing was sent.");
			return;
		}

		bool isOwner = registry.Owner.Equals(login, StringComparison.OrdinalIgnoreCase) || GitHubCli.HasPermission(registry.Slug, "push");
		string target = isOwner ? registry.Slug : EnsureCopy(login);
		string branch = $"publish/{name.Replace('/', '-')}-{release.Version}";
		string url;

		using (new Spinner($"Sending {id}"))
		{
			if (!isOwner)
			{
				GitHubCli.Send("POST", $"repos/{target}/merge-upstream", new JsonObject { ["branch"] = registry.Branch }, ".message");
			}

			string commit = CreateCommit(target, head, files, $"Publish {id}");

			SetBranch(target, branch, commit);
			url = OpenReview(isOwner ? branch : $"{login}:{branch}", id);
		}

		Out.Ok($"Sent {Fmt.Pkg(id)} for review: {Fmt.File(url)}");
		Out.Info("The registry owner checks it and adds it. GitHub notifies you when it is accepted.");
	}

	async Task<List<PackageFile>> BuildFilesAsync(RegistryRelease release, byte[] archive, string head)
	{
		string name = release.Manifest.Name;
		string staging = Path.Combine(Path.GetTempPath(), $"crater-publish-{Guid.NewGuid():N}");

		try
		{
			LocalRegistry local = new(staging);
			string packageDirectory = local.PackageDirectory(name);
			string packagePath = Path.GetRelativePath(staging, packageDirectory).Replace('\\', '/');
			string? existing = await Http.GetTextOrNullAsync($"{registry.RawBase(head)}/{packagePath}/{RegistryIndex.FileName}", GitHubAuth.Headers());

			Directory.CreateDirectory(packageDirectory);

			if (existing is not null)
			{
				await File.WriteAllTextAsync(Path.Combine(packageDirectory, RegistryIndex.FileName), existing);
			}

			await local.PublishAsync(release, archive);

			string zip = $"{release.Version}.zip";

			return
			[
				new PackageFile($"{packagePath}/{zip}", await File.ReadAllBytesAsync(Path.Combine(packageDirectory, zip))),
				new PackageFile($"{packagePath}/{RegistryIndex.FileName}", await File.ReadAllBytesAsync(Path.Combine(packageDirectory, RegistryIndex.FileName))),
			];
		}
		finally
		{
			if (Directory.Exists(staging))
			{
				Directory.Delete(staging, recursive: true);
			}
		}
	}

	static string CreateCommit(string target, string head, List<PackageFile> files, string message)
	{
		string baseTree = Read($"repos/{target}/git/commits/{head}", ".tree.sha");
		JsonArray entries = [];

		foreach (PackageFile file in files)
		{
			JsonObject blob = new() { ["content"] = Convert.ToBase64String(file.Content), ["encoding"] = "base64" };
			string sha = Write("POST", $"repos/{target}/git/blobs", blob, ".sha");

			entries.Add(new JsonObject { ["path"] = file.Path, ["mode"] = "100644", ["type"] = "blob", ["sha"] = sha });
		}

		string tree = Write("POST", $"repos/{target}/git/trees", new JsonObject { ["base_tree"] = baseTree, ["tree"] = entries }, ".sha");
		JsonObject commit = new() { ["message"] = message, ["tree"] = tree, ["parents"] = new JsonArray(head) };

		return Write("POST", $"repos/{target}/git/commits", commit, ".sha");
	}

	static void SetBranch(string target, string branch, string commit)
	{
		ShellResult updated = GitHubCli.Send("PATCH", $"repos/{target}/git/refs/heads/{branch}", new JsonObject { ["sha"] = commit, ["force"] = true }, ".ref");

		if (!updated.Success)
		{
			Write("POST", $"repos/{target}/git/refs", new JsonObject { ["ref"] = $"refs/heads/{branch}", ["sha"] = commit }, ".ref");
		}
	}

	void CheckScope(string name, string login)
	{
		string scope = name[..name.IndexOf('/')];

		if (scope.Equals(login, StringComparison.OrdinalIgnoreCase) || registry.Owner.Equals(login, StringComparison.OrdinalIgnoreCase) || GitHubCli.HasPermission(registry.Slug, "admin"))
		{
			return;
		}

		if (GitHubMembership.IsPublicMember(scope, login))
		{
			return;
		}

		if (GitHubCli.Api([$"orgs/{scope}/members/{login}"]).Success)
		{
			MakeMembershipPublic(scope, login);
			return;
		}

		string expected = login.ToLowerInvariant() + name[name.IndexOf('/')..];

		throw new CraterException(
			$"You are signed in as {Fmt.Val(login)}, so the package name must start with {Fmt.Pkg(login.ToLowerInvariant() + "/")} " +
			$"or with the name of a GitHub organization you are a member of. In {Fmt.File(Manifest.FileName)} set {Fmt.Val($"\"name\": \"{expected}\"")}.");
	}

	void MakeMembershipPublic(string organization, string login)
	{
		Out.Info($"You are a member of {Fmt.Val(organization)}, but your membership is hidden, so the registry check cannot see it.");

		if (!prompt.Confirm($"Make your membership in {Fmt.Val(organization)} public"))
		{
			throw new CraterException($"Make it public at {Fmt.File(GitHubMembership.PeopleUrl(organization))} and run {Fmt.Cmd("crater publish")} again.");
		}

		ShellResult result = GitHubCli.Api(["-X", "PUT", $"orgs/{organization}/public_members/{login}"]);

		if (!result.Success)
		{
			throw new CraterException(
				$"GitHub did not allow crater to change it. Open {Fmt.File(GitHubMembership.PeopleUrl(organization))}, " +
				$"find yourself, set the visibility to Public and run {Fmt.Cmd("crater publish")} again.");
		}

		Out.Ok($"Your membership in {Fmt.Val(organization)} is public now");
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

		GitHubCli.Run(["repo", "fork", registry.Slug, "--clone=false", "--default-branch-only"]);

		for (int attempt = 0; attempt < 30; attempt++)
		{
			if (GitHubCli.ParentOf(copy) is { Length: > 0 })
			{
				return copy;
			}

			Thread.Sleep(2000);
		}

		throw new CraterException($"GitHub is still setting up your account. Run {Fmt.Cmd("crater publish")} again in a minute.");
	}

	string OpenReview(string head, string id)
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

	static string Read(string endpoint, string jq)
	{
		ShellResult result = GitHubCli.Api([endpoint, "--jq", jq]);

		return result.Success && result.Output.Length > 0
			? result.Output.Trim()
			: throw new CraterException($"GitHub request failed ({endpoint}): {result.Error}");
	}

	static string Write(string method, string endpoint, JsonNode body, string jq)
	{
		ShellResult result = GitHubCli.Send(method, endpoint, body, jq);

		return result.Success && result.Output.Length > 0
			? result.Output.Trim()
			: throw new CraterException($"GitHub request failed ({endpoint}): {result.Error}");
	}
}
