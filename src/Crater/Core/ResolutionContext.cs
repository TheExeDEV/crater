using Crater.Models;
using Crater.Registry;
using Crater.Util;
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
			Named = name => Registries.Open(NamedRegistries.Location(name, project.Manifest), project.Directory),
		};

		return await resolver.ResolveAsync(request);
	}

	public static async Task<(Dictionary<Realm, ResolvedGraph> Graphs, Dictionary<Realm, ResolvedGraph> Vendor)> ResolveWithVendorAsync(Project project, LockFile lockFile, ISet<string>? update)
	{
		ResolutionContext context = new(project, lockFile, update);
		Dictionary<Realm, ResolvedGraph> graphs = [];
		Dictionary<Realm, ResolvedGraph> vendor = [];

		foreach (Realm realm in Realms.All)
		{
			graphs[realm] = await context.ResolveRealmAsync(realm);
		}

		foreach (Realm realm in Manifest.VendorRealms)
		{
			vendor[realm] = await context.ResolveVendorAsync(realm);
		}

		return (graphs, vendor);
	}

	public async Task<ResolvedGraph> ResolveVendorAsync(Realm realm)
	{
		string folder = project.Config.VendorFolder(realm);
		List<DependencySpec> roots = project.Manifest.VendorFor(realm)
			.Select(raw => DependencySpec.Parse(raw, $"{Manifest.VendorField(realm)} in {Manifest.FileName}"))
			.ToList();

		if (roots.Count == 0)
		{
			return ResolvedGraph.Empty(realm).AsVendor(folder);
		}

		ResolveRequest request = new()
		{
			Realm = realm,
			Roots = roots,
			DefaultRegistry = defaultRegistry,
			Preferred = Unlocked(lockFile.PreferredVendorVersions(realm)),
			Overrides = overrides,
			Pinned = sources.For,
			Named = name => Registries.Open(NamedRegistries.Location(name, project.Manifest), project.Directory),
		};

		try
		{
			ResolvedGraph graph = await resolver.ResolveAsync(request);

			return graph.AsVendor(folder);
		}
		catch (CraterException exception)
		{
			throw new CraterException($"{Manifest.VendorField(realm)}: {exception.Message}");
		}
	}

	public IRegistry? RegistryFor(string fullName) => sources.For(fullName) ?? defaultRegistry;

	Dictionary<string, string> PreferredVersions(Realm realm) => Unlocked(lockFile.PreferredVersions(realm));

	Dictionary<string, string> Unlocked(Dictionary<string, string> preferred)
	{
		if (update is { Count: 0 })
		{
			return [];
		}

		return preferred
			.Where(pair => update is null || !update.Contains(pair.Key))
			.ToDictionary();
	}
}
