using System.Text.Json;
using System.Text.Json.Nodes;
using Crater.Models;
using Crater.Registry;
using Crater.Util;

namespace Crater.Commands;

public sealed class GitHubPublisher(GitHubRepository registry, Prompter prompt, bool dryRun)
{
	sealed record PackageFile(string Path, byte[]? Content);

	public async Task PublishAsync(RegistryRelease release, byte[] archive, int fileCount)
	{
		string login = await GitHubApi.RequireLoginAsync();
		string name = release.Manifest.Name;
		string id = $"{name}@{release.Version}";

		await CheckScopeAsync(name, login);

		if (!prompt.Confirm($"Send {Fmt.Pkg(id)} ({Fmt.Plural(fileCount, "file", "files")}) to {Fmt.Val(registry.Slug)} for review"))
		{
			throw new CraterException("Cancelled.");
		}

		string head;
		List<PackageFile> files;

		using (new Spinner($"Preparing {id}"))
		{
			head = await GitHubApi.RequireAsync(HttpMethod.Get, $"repos/{registry.Slug}/git/ref/heads/{registry.Branch}", null, "object.sha");
			files = await BuildFilesAsync(release, archive, head);
		}

		if (dryRun)
		{
			Out.Ok($"Dry run: {Fmt.Pkg(id)} is ready, nothing was sent.");
			return;
		}

		bool isOwner = registry.Owner.Equals(login, StringComparison.OrdinalIgnoreCase) || await GitHubApi.HasPermissionAsync(registry.Slug, "push");
		string target = isOwner ? registry.Slug : await EnsureCopyAsync(login);
		string branch = $"publish/{name.Replace('/', '-')}-{release.Version}";
		string url;

		using (new Spinner($"Sending {id}"))
		{
			if (!isOwner)
			{
				await GitHubApi.SendAsync(HttpMethod.Post, $"repos/{target}/merge-upstream", new JsonObject { ["branch"] = registry.Branch });
			}

			string commit = await CreateCommitAsync(target, head, files, $"Publish {id}");

			await SetBranchAsync(target, branch, commit);
			url = await OpenReviewAsync(isOwner ? branch : $"{login}:{branch}", id);
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
			string sourceDirectory = Path.Combine(packageDirectory, LocalRegistry.SourceFolder);
			string sourcePath = $"{packagePath}/{LocalRegistry.SourceFolder}";

			List<PackageFile> files =
			[
				new PackageFile($"{packagePath}/{zip}", await File.ReadAllBytesAsync(Path.Combine(packageDirectory, zip))),
				new PackageFile($"{packagePath}/{RegistryIndex.FileName}", await File.ReadAllBytesAsync(Path.Combine(packageDirectory, RegistryIndex.FileName))),
			];

			foreach (string file in Directory.EnumerateFiles(sourceDirectory, "*", SearchOption.AllDirectories))
			{
				files.Add(new PackageFile($"{sourcePath}/{Path.GetRelativePath(sourceDirectory, file).Replace('\\', '/')}", await File.ReadAllBytesAsync(file)));
			}

			HashSet<string> written = files.Select(file => file.Path).ToHashSet(StringComparer.Ordinal);

			foreach (string old in await ExistingFilesAsync(head, sourcePath + "/"))
			{
				if (!written.Contains(old))
				{
					files.Add(new PackageFile(old, null));
				}
			}

			return files;
		}
		finally
		{
			if (Directory.Exists(staging))
			{
				Directory.Delete(staging, recursive: true);
			}
		}
	}

	static async Task<string> CreateCommitAsync(string target, string head, List<PackageFile> files, string message)
	{
		string baseTree = await GitHubApi.RequireAsync(HttpMethod.Get, $"repos/{target}/git/commits/{head}", null, "tree.sha");
		JsonArray entries = [];

		foreach (PackageFile file in files)
		{
			JsonObject entry = new() { ["path"] = file.Path, ["mode"] = "100644", ["type"] = "blob" };

			if (file.Content is null)
			{
				entry["sha"] = null;
			}
			else if (AsText(file.Content) is string text)
			{
				entry["content"] = text;
			}
			else
			{
				JsonObject blob = new() { ["content"] = Convert.ToBase64String(file.Content), ["encoding"] = "base64" };

				entry["sha"] = await GitHubApi.RequireAsync(HttpMethod.Post, $"repos/{target}/git/blobs", blob, "sha");
			}

			entries.Add(entry);
		}

		string tree = await GitHubApi.RequireAsync(HttpMethod.Post, $"repos/{target}/git/trees", new JsonObject { ["base_tree"] = baseTree, ["tree"] = entries }, "sha");
		JsonObject commit = new() { ["message"] = message, ["tree"] = tree, ["parents"] = new JsonArray(head) };

		return await GitHubApi.RequireAsync(HttpMethod.Post, $"repos/{target}/git/commits", commit, "sha");
	}

	static string? AsText(byte[] content)
	{
		if (content.Contains((byte)0))
		{
			return null;
		}

		try
		{
			string text = new System.Text.UTF8Encoding(false, throwOnInvalidBytes: true).GetString(content);

			return System.Text.Encoding.UTF8.GetByteCount(text) == content.Length && !text.StartsWith('﻿') ? text : null;
		}
		catch (System.Text.DecoderFallbackException)
		{
			return null;
		}
	}

	async Task<List<string>> ExistingFilesAsync(string head, string prefix)
	{
		JsonElement? tree = await GitHubApi.GetAsync($"repos/{registry.Slug}/git/trees/{head}?recursive=1");

		if (tree is not { } found || !found.TryGetProperty("tree", out JsonElement items))
		{
			return [];
		}

		return items.EnumerateArray()
			.Where(item => item.GetProperty("type").GetString() == "blob")
			.Select(item => item.GetProperty("path").GetString()!)
			.Where(path => path.StartsWith(prefix, StringComparison.Ordinal))
			.ToList();
	}

	static async Task SetBranchAsync(string target, string branch, string commit)
	{
		GitHubApi.Result updated = await GitHubApi.SendAsync(HttpMethod.Patch, $"repos/{target}/git/refs/heads/{branch}", new JsonObject { ["sha"] = commit, ["force"] = true });

		if (!updated.Success)
		{
			await GitHubApi.RequireAsync(HttpMethod.Post, $"repos/{target}/git/refs", new JsonObject { ["ref"] = $"refs/heads/{branch}", ["sha"] = commit }, "ref");
		}
	}

	async Task CheckScopeAsync(string name, string login)
	{
		string scope = name[..name.IndexOf('/')];

		if (scope.Equals(login, StringComparison.OrdinalIgnoreCase) || registry.Owner.Equals(login, StringComparison.OrdinalIgnoreCase) || await GitHubApi.HasPermissionAsync(registry.Slug, "admin"))
		{
			return;
		}

		if (GitHubMembership.IsPublicMember(scope, login))
		{
			return;
		}

		if ((await GitHubApi.SendAsync(HttpMethod.Get, $"orgs/{scope}/members/{login}")).Success)
		{
			await MakeMembershipPublicAsync(scope, login);
			return;
		}

		string expected = login.ToLowerInvariant() + name[name.IndexOf('/')..];

		throw new CraterException(
			$"You are signed in as {Fmt.Val(login)}, so the package name must start with {Fmt.Pkg(login.ToLowerInvariant() + "/")} " +
			$"or with the name of a GitHub organization you are a member of. In {Fmt.File(Manifest.FileName)} set {Fmt.Val($"\"name\": \"{expected}\"")}.");
	}

	async Task MakeMembershipPublicAsync(string organization, string login)
	{
		Out.Info($"You are a member of {Fmt.Val(organization)}, but your membership is hidden, so the registry check cannot see it.");

		if (!prompt.Confirm($"Make your membership in {Fmt.Val(organization)} public"))
		{
			throw new CraterException($"Make it public at {Fmt.File(GitHubMembership.PeopleUrl(organization))} and run {Fmt.Cmd("crater publish")} again.");
		}

		GitHubApi.Result result = await GitHubApi.SendAsync(HttpMethod.Put, $"orgs/{organization}/public_members/{login}");

		if (!result.Success)
		{
			throw new CraterException(
				$"GitHub did not allow crater to change it. Open {Fmt.File(GitHubMembership.PeopleUrl(organization))}, " +
				$"find yourself, set the visibility to Public and run {Fmt.Cmd("crater publish")} again.");
		}

		Out.Ok($"Your membership in {Fmt.Val(organization)} is public now");
	}

	async Task<string> EnsureCopyAsync(string login)
	{
		string copy = $"{login}/{registry.Name}";
		string? parent = await GitHubApi.ParentOfAsync(copy);

		if (parent is not null && parent.Equals(registry.Slug, StringComparison.OrdinalIgnoreCase))
		{
			return copy;
		}

		if (parent is not null)
		{
			throw new CraterException(
				$"Your GitHub account already has a repository named {Fmt.Val(copy)}. crater needs that name for publishing. Rename yours on GitHub and try again.");
		}

		GitHubApi.Result fork = await GitHubApi.SendAsync(HttpMethod.Post, $"repos/{registry.Slug}/forks", new JsonObject { ["default_branch_only"] = true });

		if (!fork.Success)
		{
			throw new CraterException($"GitHub could not set up publishing for your account: {fork.Message}");
		}

		for (int attempt = 0; attempt < 30; attempt++)
		{
			if (await GitHubApi.ParentOfAsync(copy) is { Length: > 0 })
			{
				return copy;
			}

			await Task.Delay(2000);
		}

		throw new CraterException($"GitHub is still setting up your account. Run {Fmt.Cmd("crater publish")} again in a minute.");
	}

	async Task<string> OpenReviewAsync(string head, string id)
	{
		JsonObject request = new() { ["title"] = $"Publish {id}", ["head"] = head, ["base"] = registry.Branch, ["body"] = $"Publishes `{id}`." };
		GitHubApi.Result created = await GitHubApi.SendAsync(HttpMethod.Post, $"repos/{registry.Slug}/pulls", request);

		if (created.Success && created.Json is { } json)
		{
			return json.GetProperty("html_url").GetString()!;
		}

		string qualified = head.Contains(':') ? head : $"{registry.Owner}:{head}";
		GitHubApi.Result open = await GitHubApi.SendAsync(HttpMethod.Get, $"repos/{registry.Slug}/pulls?state=open&head={Uri.EscapeDataString(qualified)}");

		if (open.Success && open.Json is { ValueKind: JsonValueKind.Array } list && list.GetArrayLength() > 0)
		{
			return list[0].GetProperty("html_url").GetString()!;
		}

		throw new CraterException($"GitHub did not accept the package: {created.Message}");
	}
}
