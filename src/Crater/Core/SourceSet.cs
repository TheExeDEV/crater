using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Core;

public sealed class SourceSet
{
	readonly Project project;
	readonly IReadOnlyDictionary<string, string> lockedCommits;
	readonly ISet<string>? update;
	readonly Dictionary<string, DependencySource> explicitSources = [];
	readonly Dictionary<string, IRegistry?> opened = [];

	public SourceSet(Project project, LockFile lockFile, ISet<string>? update)
	{
		this.project = project;
		this.update = update;
		lockedCommits = lockFile.LockedCommits();

		IEnumerable<DependencySpec> declared = project.Manifest.AllDependencies().Concat(project.Manifest.ParseOverrides());

		foreach (DependencySpec spec in declared)
		{
			if (spec.Source is null)
			{
				continue;
			}

			if (explicitSources.TryGetValue(spec.FullName, out DependencySource? existing) && existing.Text != spec.Source.Text)
			{
				throw new CraterException(
					$"{Fmt.Pkg(spec.FullName)} is declared with two sources: {Fmt.Val(existing.Text)} and {Fmt.Val(spec.Source.Text)}.");
			}

			explicitSources[spec.FullName] = spec.Source;
		}
	}

	public bool UsesGit => explicitSources.Values.Any(source => source.Kind == SourceKind.Git);

	public IRegistry? For(string fullName)
	{
		if (opened.TryGetValue(fullName, out IRegistry? cached))
		{
			return cached;
		}

		IRegistry? registry = Open(fullName);
		opened[fullName] = registry;

		return registry;
	}

	IRegistry? Open(string fullName)
	{
		if (explicitSources.TryGetValue(fullName, out DependencySource? source))
		{
			bool refresh = update is not null && (update.Count == 0 || update.Contains(fullName));
			string? commit = !refresh && lockedCommits.TryGetValue(source.Text, out string? locked) ? locked : null;

			return SourceRegistry.Open(fullName, source, project.Directory, commit);
		}

		string? memberDirectory = project.Workspace?.MemberDirectory(fullName);

		if (memberDirectory is null || fullName == project.Manifest.Name)
		{
			return null;
		}

		string sourceText = "path:" + project.Relative(memberDirectory);

		return SourceRegistry.FromDirectory(fullName, sourceText, memberDirectory, null);
	}
}
