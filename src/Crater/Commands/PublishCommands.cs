using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class PublishCommands
{
	public static void Pack(Args args)
	{
		Project project = Common.LoadProject();

		project.Manifest.ValidateForPublish();

		byte[] archive = Packer.Pack(project, out List<string> files);
		string defaultName = $"{project.Manifest.Name.Replace('/', '_')}@{project.Manifest.Version}.zip";

		DescribeEntry(project);
		string output = args.Value("o", "output") ?? Path.Combine(project.Directory, defaultName);

		File.WriteAllBytes(output, archive);

		foreach (string file in files)
		{
			Out.Dim("  " + file);
		}

		string size = $"{archive.Length / 1024.0:0.0} KB";

		Out.Ok($"{Fmt.File(output)} ({Fmt.Plural(files.Count, "file", "files")}, {size}, sha256 {Fmt.Val(PackageCache.Sha256(archive))})");
	}

	public static async Task PublishAsync(Args args)
	{
		Project current = Common.LoadProject();
		List<Project> projects = args.Has("workspace", "w") ? WorkspacePublishOrder(current) : [current];

		foreach (Project project in projects)
		{
			Common.Heading(project, projects.Count);
			await PublishOneAsync(project, args);
		}
	}

	static async Task PublishOneAsync(Project project, Args args)
	{
		Manifest manifest = project.Manifest;

		if (!manifest.CanPublish)
		{
			throw new CraterException($"Publishing is disabled: {Fmt.File(Manifest.FileName)} has {Fmt.Val("\"can_publish\": false")}.");
		}

		manifest.ValidateForPublish();

		IRegistry registry = args.Value("registry") is string location
			? Registries.Open(location, Environment.CurrentDirectory)
			: project.RequireRegistry();

		byte[] archive = Packer.Pack(project, out List<string> files);

		DescribeEntry(project);

		RegistryRelease release = new()
		{
			Version = SemVer.Parse(manifest.Version).ToString(),
			Sha256 = PackageCache.Sha256(archive),
			PublishedAt = DateTimeOffset.UtcNow,
			Manifest = Json.ReadFile<Manifest>(project.ManifestPath),
		};

		string id = Fmt.Pkg($"{manifest.Name}@{release.Version}");
		Prompter prompt = Common.PrompterFor(project, args);

		if (args.Has("pr"))
		{
			if (registry is not LocalRegistry local)
			{
				throw new CraterException(
					$"{Fmt.Cmd("--pr")} needs a local clone of the registry repository. Pass {Fmt.Cmd("--registry <path to clone>")}.");
			}

			await new PullRequestPublisher(local, prompt, args.Has("push")).PublishAsync(release, archive, files.Count);
			return;
		}

		if (!prompt.Confirm($"Publish {id} ({Fmt.Plural(files.Count, "file", "files")}) to {Fmt.File(registry.Location)}"))
		{
			throw new CraterException("Cancelled.");
		}

		await registry.PublishAsync(release, archive);
		Out.Ok($"Published {id} to {Fmt.File(registry.Location)}");
	}

	static void DescribeEntry(Project project)
	{
		PackageEntry entry = PackageEntry.Analyze(project.Directory, project.Manifest.PathToMain);

		switch (entry.Kind)
		{
			case EntryKind.Module:
				Out.Dim($"  entry: {Path.GetRelativePath(project.Directory, entry.MainFile!).Replace('\\', '/')}");
				break;

			case EntryKind.Collection:
				string names = string.Join(", ", entry.Members.Take(8).Select(member => member.Name));
				string more = entry.Members.Count > 8 ? ", ..." : "";

				Out.Dim($"  entry: collection of {Fmt.Plural(entry.Members.Count, "module", "modules")} ({names}{more})");
				break;

			default:
				Out.Warn(
					"No main module and no modules to expose. Set path_to_main, add init.luau, " +
					"or put .luau modules in the package root so users can require it.");
				break;
		}
	}

	static List<Project> WorkspacePublishOrder(Project current)
	{
		if (current.Workspace is null)
		{
			throw new CraterException($"{Fmt.File(current.ManifestPath)} is not part of a workspace.");
		}

		List<Project> members = current.Workspace.Members.Values
			.Select(Project.LoadExact)
			.Where(project => project.Manifest.CanPublish)
			.ToList();

		Dictionary<string, Project> byName = members.ToDictionary(project => project.Manifest.Name);
		List<Project> ordered = [];
		HashSet<string> visited = [];

		void Visit(Project project)
		{
			if (!visited.Add(project.Manifest.Name))
			{
				return;
			}

			foreach (DependencySpec dependency in project.Manifest.AllDependencies())
			{
				if (byName.TryGetValue(dependency.FullName, out Project? member))
				{
					Visit(member);
				}
			}

			ordered.Add(project);
		}

		foreach (Project member in members.OrderBy(project => project.Manifest.Name, StringComparer.Ordinal))
		{
			Visit(member);
		}

		return ordered;
	}
}

public sealed class PullRequestPublisher(LocalRegistry registry, Prompter prompt, bool push)
{
	public async Task PublishAsync(RegistryRelease release, byte[] archive, int fileCount)
	{
		string root = registry.Location;
		string name = release.Manifest.Name;
		string id = $"{name}@{release.Version}";

		if (!registry.IsGitRepository)
		{
			throw new CraterException($"{Fmt.File(root)} is not a git repository. Clone the registry repository first.");
		}

		string status = Shell.Run("git", ["status", "--porcelain"], root);

		if (status.Length > 0)
		{
			throw new CraterException($"{Fmt.File(root)} has uncommitted changes. Commit or stash them first.");
		}

		string baseBranch = Shell.Run("git", ["rev-parse", "--abbrev-ref", "HEAD"], root);
		string branch = $"publish/{name.Replace('/', '-')}-{release.Version}";

		if (!prompt.Confirm($"Create branch {Fmt.Val(branch)} in {Fmt.File(root)} and commit {Fmt.Pkg(id)} ({Fmt.Plural(fileCount, "file", "files")})"))
		{
			throw new CraterException("Cancelled.");
		}

		Shell.Run("git", ["checkout", "--quiet", "-b", branch], root);

		try
		{
			await registry.PublishAsync(release, archive);

			string packagePath = Path.GetRelativePath(root, registry.PackageDirectory(name)).Replace('\\', '/');

			Shell.Run("git", ["add", "--", packagePath], root);
			Shell.Run("git", ["commit", "--quiet", "-m", $"Publish {id}"], root);
		}
		catch
		{
			Shell.Capture("git", ["checkout", "--quiet", baseBranch], root);
			Shell.Capture("git", ["branch", "--quiet", "-D", branch], root);

			throw;
		}

		Out.Ok($"Committed {Fmt.Pkg(id)} on branch {Fmt.Val(branch)}");

		bool pushed = push || prompt.Confirm($"Push {Fmt.Val(branch)} to {Fmt.Val("origin")}");

		if (pushed)
		{
			Shell.Run("git", ["push", "--quiet", "--set-upstream", "origin", branch], root);
			Out.Ok($"Pushed {Fmt.Val(branch)}");

			OpenPullRequest(root, branch, baseBranch, id);
		}
		else
		{
			Out.Info($"Push it later with {Fmt.Cmd($"git -C \"{root}\" push -u origin {branch}")} and open a pull request.");
		}

		Shell.Run("git", ["checkout", "--quiet", baseBranch], root);

		Out.Dim($"  The registry check accepts packages only inside the folder named after your GitHub login ({Fmt.Val(release.Manifest.Name.Split('/')[0])}).");
	}

	void OpenPullRequest(string root, string branch, string baseBranch, string id)
	{
		if (Shell.Exists("gh") && prompt.Confirm($"Open a pull request with {Fmt.Cmd("gh")}"))
		{
			string url = Shell.Run("gh", ["pr", "create", "--title", $"Publish {id}", "--body", $"Publishes `{id}`.", "--head", branch, "--base", baseBranch], root);

			Out.Ok($"Pull request: {Fmt.File(url)}");
			return;
		}

		string remote = Shell.Capture("git", ["remote", "get-url", "origin"], root).Output;
		string? compare = GitHubCompareUrl(remote, baseBranch, branch);

		if (compare is not null)
		{
			Out.Info($"Open the pull request here: {Fmt.File(compare)}");
		}
		else
		{
			Out.Info($"Open a pull request from {Fmt.Val(branch)} into {Fmt.Val(baseBranch)}.");
		}
	}

	static string? GitHubCompareUrl(string remote, string baseBranch, string branch)
	{
		string trimmed = remote.Trim();

		if (trimmed.StartsWith("git@github.com:"))
		{
			trimmed = "https://github.com/" + trimmed["git@github.com:".Length..];
		}

		if (!trimmed.StartsWith("https://github.com/"))
		{
			return null;
		}

		if (trimmed.EndsWith(".git"))
		{
			trimmed = trimmed[..^4];
		}

		return $"{trimmed}/compare/{baseBranch}...{branch}?expand=1";
	}
}
