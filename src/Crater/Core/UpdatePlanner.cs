using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Core;

public sealed record UpdateCandidate(
	Realm Realm,
	string Name,
	string? Installed,
	string? Wanted,
	string? Latest,
	IReadOnlyList<DependencySpec> Direct,
	IReadOnlyList<string> Parents)
{
	public bool IsDirect => Direct.Count > 0;

	public bool IsSource => Direct.Any(spec => spec.Source is not null);

	public bool CanUpdate => Wanted is not null && Installed != Wanted;

	public bool HasNewer
	{
		get
		{
			if (Latest is null || Wanted is null || Latest == Wanted)
			{
				return false;
			}

			bool parsed = SemVer.TryParse(Latest, out SemVer latest) & SemVer.TryParse(Wanted, out SemVer wanted);

			return parsed && latest > wanted;
		}
	}

	public bool NeedsAttention => CanUpdate || HasNewer || Installed is null;

	public string Requirement
	{
		get
		{
			if (Direct.Count > 0)
			{
				return string.Join(", ", Direct.Select(spec => spec.Source is null
					? (spec.Alias is null ? "" : spec.Alias + ":") + spec.Req
					: "@" + spec.Source.Text));
			}

			return "via " + string.Join(", ", Parents);
		}
	}
}

public static class UpdatePlanner
{
	public static async Task<List<UpdateCandidate>> PlanAsync(Project project)
	{
		LockFile lockFile = LockFile.Load(project.LockPath);
		ResolutionContext context = new(project, lockFile, new HashSet<string>());
		List<UpdateCandidate> candidates = [];

		foreach (Realm realm in Realms.All)
		{
			List<DependencySpec> specs = project.Manifest.DepsFor(realm).Select(raw => DependencySpec.Parse(raw)).ToList();

			if (specs.Count == 0)
			{
				continue;
			}

			ResolvedGraph? wanted = await TryResolveAsync(context, realm);

			candidates.AddRange(await PlanRealmAsync(context, realm, specs, lockFile, wanted));
		}

		return candidates;
	}

	static async Task<ResolvedGraph?> TryResolveAsync(ResolutionContext context, Realm realm)
	{
		try
		{
			return await context.ResolveRealmAsync(realm);
		}
		catch (CraterException exception)
		{
			Out.Warn($"[{realm.Key()}] Wanted versions are unknown, the newest versions do not resolve:\n{exception.Message}");
			return null;
		}
	}

	static async Task<List<UpdateCandidate>> PlanRealmAsync(
		ResolutionContext context,
		Realm realm,
		List<DependencySpec> specs,
		LockFile lockFile,
		ResolvedGraph? wanted)
	{
		Dictionary<string, LockedPackage> locked = lockFile.For(realm).ToDictionary(package => package.Name);
		Dictionary<string, ResolvedPackage> nodes = wanted?.All.ToDictionary(node => node.Name) ?? [];
		Dictionary<string, SortedSet<string>> parents = CollectParents(locked.Values, nodes.Values);

		IEnumerable<string> names = locked.Keys
			.Concat(nodes.Keys)
			.Concat(specs.Select(spec => spec.FullName))
			.Distinct()
			.OrderBy(name => name, StringComparer.Ordinal);

		List<UpdateCandidate> result = [];

		foreach (string name in names)
		{
			List<DependencySpec> direct = specs.Where(spec => spec.FullName == name).ToList();
			bool stillUsed = direct.Count > 0 || nodes.ContainsKey(name);

			if (!stillUsed)
			{
				continue;
			}

			nodes.TryGetValue(name, out ResolvedPackage? node);

			string? latest = await LatestVersionAsync(context, name, node);
			string? installed = locked.TryGetValue(name, out LockedPackage? lockedPackage) ? lockedPackage.Version : null;
			List<string> parentNames = parents.TryGetValue(name, out SortedSet<string>? set) ? set.ToList() : [];

			result.Add(new UpdateCandidate(realm, name, installed, node?.Version.ToString(), latest, direct, parentNames));
		}

		return result;
	}

	static async Task<string?> LatestVersionAsync(ResolutionContext context, string name, ResolvedPackage? node)
	{
		IRegistry? registry = node?.Registry ?? context.RegistryFor(name);

		if (registry is null)
		{
			return null;
		}

		RegistryIndex? index = await registry.GetIndexAsync(name);

		return index?.Latest?.Version;
	}

	static Dictionary<string, SortedSet<string>> CollectParents(IEnumerable<LockedPackage> locked, IEnumerable<ResolvedPackage> nodes)
	{
		Dictionary<string, SortedSet<string>> parents = [];

		void Add(string child, string parent)
		{
			if (!parents.TryGetValue(child, out SortedSet<string>? set))
			{
				set = new SortedSet<string>(StringComparer.Ordinal);
				parents[child] = set;
			}

			set.Add(parent);
		}

		foreach (ResolvedPackage node in nodes)
		{
			foreach (ResolvedDependency edge in node.Dependencies)
			{
				Add(edge.Package.Name, node.Name);
			}
		}

		foreach (LockedPackage package in locked)
		{
			foreach (string entry in package.Dependencies)
			{
				string id = ResolvedDependency.ParseLockEntry(entry).Id;

				Add(id[..id.LastIndexOf('@')], package.Name);
			}
		}

		return parents;
	}
}
