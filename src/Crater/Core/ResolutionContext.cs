using Crater.Models;
using Crater.Registry;
using Crater.Versioning;

namespace Crater.Core;

public sealed class ResolutionContext
{
	readonly Project project;
	readonly LockFile lockFile;
	readonly ISet<string>? update;
	readonly IRegistry? defaultRegistry;
	readonly SourceSet sources;
	readonly Dictionary<string, DependencySpec> overrides;
	readonly Resolver resolver = new();

	public ResolutionContext(Project project, LockFile lockFile, ISet<string>? update)
	{
		this.project = project;
		this.lockFile = lockFile;
		this.update = update;

		defaultRegistry = project.OpenRegistry();
		sources = new SourceSet(project, lockFile, update);
		overrides = project.Manifest.ParseOverrides().ToDictionary(spec => spec.FullName);
	}

	public IRegistry? DefaultRegistry => defaultRegistry;

	public static async Task<Dictionary<Realm, ResolvedGraph>> ResolveAllAsync(Project project, LockFile lockFile, ISet<string>? update)
	{
		ResolutionContext context = new(project, lockFile, update);
		Dictionary<Realm, ResolvedGraph> graphs = [];

		foreach (Realm realm in Realms.All)
		{
			graphs[realm] = await context.ResolveRealmAsync(realm);
		}

		return graphs;
	}

	public async Task<ResolvedGraph> ResolveRealmAsync(Realm realm)
	{
		List<DependencySpec> roots = project.Manifest.DepsFor(realm)
			.Select(raw => DependencySpec.Parse(raw, $"{realm.ManifestField()} in {Manifest.FileName}"))
			.ToList();

		if (roots.Count == 0)
		{
			return ResolvedGraph.Empty(realm);
		}

		project.Config.FolderFor(realm);

		ResolveRequest request = new()
		{
			Realm = realm,
			Roots = roots,
			DefaultRegistry = defaultRegistry,
			Preferred = PreferredVersions(realm),
			Overrides = overrides,
			Pinned = sources.For,
		};

		return await resolver.ResolveAsync(request);
	}

	public IRegistry? RegistryFor(string fullName) => sources.For(fullName) ?? defaultRegistry;

	Dictionary<string, string> PreferredVersions(Realm realm)
	{
		if (update is { Count: 0 })
		{
			return [];
		}

		return lockFile.PreferredVersions(realm)
			.Where(pair => update is null || !update.Contains(pair.Key))
			.ToDictionary();
	}
}
