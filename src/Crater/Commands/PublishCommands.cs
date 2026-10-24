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

		IRegistry registry = project.OpenPublishRegistry() ?? project.RequireRegistry();
		GitHubRepository? github = GitHubRepository.FromRegistry(Registries.Expand(project.RegistryLocation!));

		if (github is null && registry is not LocalRegistry)
		{
			throw new CraterException(
				$"{Fmt.File(registry.Location)} is not a GitHub registry, so crater cannot send packages to it. " +
				$"Use a registry like {Fmt.Cmd("\"registry\": \"https://github.com/Owner/crater-registry\"")} in {Fmt.File(Manifest.FileName)}.");
		}

		byte[] archive = Packer.Pack(project, out List<string> files);

		DescribeEntry(project);
		ScanBeforePublish(archive, $"{manifest.Name}@{manifest.Version}");

		RegistryRelease release = new()
		{
			Version = SemVer.Parse(manifest.Version).ToString(),
			Sha256 = PackageCache.Sha256(archive),
			PublishedAt = DateTimeOffset.UtcNow,
			Manifest = Json.ReadFile<Manifest>(project.ManifestPath),
		};

		string id = Fmt.Pkg($"{manifest.Name}@{release.Version}");
		Prompter prompt = Common.PrompterFor(project, args);

		if (github is not null)
		{
			await new GitHubPublisher(github, prompt, args.Has("dry-run")).PublishAsync(release, archive, files.Count);
			return;
		}

		if (args.Has("dry-run"))
		{
			Out.Ok($"Dry run: {id} is ready, nothing was written.");
			return;
		}

		if (!prompt.Confirm($"Publish {id} ({Fmt.Plural(files.Count, "file", "files")}) to {Fmt.File(registry.Location)}"))
		{
			throw new CraterException("Cancelled.");
		}

		await registry.PublishAsync(release, archive);
		Out.Ok($"Published {id} to {Fmt.File(registry.Location)}");
	}

	static void ScanBeforePublish(byte[] archive, string packageId)
	{
		List<Finding> findings = PackageScanner.ScanArchive(archive, packageId);

		if (findings.Count == 0)
		{
			return;
		}

		Out.Warn("The scanner found code that registry reviewers will look at:");
		ScanReport.Print(findings);

		Finding? blocked = findings.FirstOrDefault(finding => finding.Severity == Severity.Blocked);

		if (blocked is not null)
		{
			throw new CraterException($"{Fmt.File(blocked.File)} is an {blocked.Description}. Remove it or add it to {Fmt.Val("exclude")}.");
		}
	}

	static void DescribeEntry(Project project)
	{
		string rojoProject = Path.Combine(project.Directory, PackageEntry.RojoProject);

		if (File.Exists(rojoProject) && File.ReadAllText(rojoProject).Contains("\"DataModel\""))
		{
			Out.Warn(
				$"{Fmt.File(PackageEntry.RojoProject)} describes a whole game, not this package, and is ignored when installed. " +
				$"Add it to {Fmt.Val("exclude")} in {Fmt.File(Manifest.FileName)}.");
		}

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
