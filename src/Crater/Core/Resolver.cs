using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Core;

public sealed class ResolveRequest
{
	public required Realm Realm { get; init; }
	public required IReadOnlyList<DependencySpec> Roots { get; init; }
	public IRegistry? DefaultRegistry { get; init; }
	public IReadOnlyDictionary<string, string> Preferred { get; init; } = new Dictionary<string, string>();
	public IReadOnlyDictionary<string, DependencySpec> Overrides { get; init; } = new Dictionary<string, DependencySpec>();
	public Func<string, IRegistry?> Pinned { get; init; } = _ => null;
	public Func<string, IRegistry> Named { get; init; } = name => throw new CraterException($"Unknown registry {Fmt.Val(name)}");
}

public sealed class Resolver
{
	const string RootOwner = "crater.json";
	const string OverrideOwner = "overrides";
	const int MaxSteps = 20_000;

	sealed record Constraint(VersionReq Req, string From);

	public async Task<ResolvedGraph> ResolveAsync(ResolveRequest request)
	{
		Session session = new(request);

		CheckLinkNames(request.Roots, RootOwner, null);

		foreach (DependencySpec spec in request.Roots)
		{
			session.AddConstraint(spec, RootOwner, request.DefaultRegistry);
		}

		int steps = 0;

		while (session.Queue.TryDequeue(out string? name))
		{
			steps++;

			if (steps > MaxSteps)
			{
				throw new CraterException(
					$"[{request.Realm.Key()}] Could not resolve dependencies: too many iterations (cyclic version conflicts).");
			}

			await session.ResolveAsync(name);
		}

		return session.BuildGraph();
	}

	sealed class Session(ResolveRequest request)
	{
		public readonly Queue<string> Queue = new();

		readonly Dictionary<string, List<Constraint>> constraints = [];
		readonly Dictionary<string, IRegistry?> edgeRegistries = [];
		readonly Dictionary<string, RegistryRelease> selected = [];
		readonly Dictionary<string, IRegistry> selectedRegistries = [];
		readonly List<(string Name, Constraint Constraint)> overridden = [];
		readonly HashSet<string> appliedOverrides = [];

		public void AddConstraint(DependencySpec spec, string owner, IRegistry? registry)
		{
			if (spec.RegistryName is string named)
			{
				registry = request.Named(named);
			}

			edgeRegistries.TryAdd(spec.FullName, registry);

			bool isOverridden = request.Overrides.TryGetValue(spec.FullName, out DependencySpec? forced);

			if (isOverridden && owner != OverrideOwner)
			{
				overridden.Add((spec.FullName, new Constraint(spec.Req, owner)));

				if (appliedOverrides.Add(spec.FullName))
				{
					AddConstraint(forced!, OverrideOwner, registry);
				}

				return;
			}

			if (!constraints.TryGetValue(spec.FullName, out List<Constraint>? list))
			{
				list = [];
				constraints[spec.FullName] = list;
			}

			list.Add(new Constraint(spec.Req, owner));
			Queue.Enqueue(spec.FullName);
			Prefetch(spec.FullName);
		}

		void Prefetch(string name)
		{
			IRegistry? registry = request.Pinned(name) ?? edgeRegistries.GetValueOrDefault(name);

			if (registry is HttpRegistry or RegistryChain)
			{
				_ = registry.GetIndexAsync(name).ContinueWith(task => task.Exception, TaskContinuationOptions.OnlyOnFaulted);
			}
		}

		public async Task ResolveAsync(string name)
		{
			List<Constraint> requirements = constraints[name];

			if (selected.TryGetValue(name, out RegistryRelease? current) && requirements.All(item => item.Req.Matches(current.SemVer)))
			{
				return;
			}

			IRegistry registry = RegistryFor(name, requirements);
			RegistryIndex? index = await registry.GetIndexAsync(name);

			if (index is null)
			{
				string owners = string.Join(", ", requirements.Select(item => item.From).Distinct());
				string where = registry is RegistryChain ? "any of the registries" : "registry";

				throw new CraterException(
					$"Package {Fmt.Pkg(name)} not found in {where} {Fmt.File(registry.Location)} (required by {owners})");
			}

			if (registry is RegistryChain chain)
			{
				registry = await chain.OwnerOfAsync(name) ?? registry;
			}

			List<RegistryRelease> candidates = index.Versions
				.Where(release => requirements.All(item => item.Req.Matches(release.SemVer)))
				.ToList();

			if (candidates.Count == 0)
			{
				throw await NoMatchingVersionAsync(name, requirements, index);
			}

			RegistryRelease pick = Pick(name, candidates);

			CheckRealm(request.Realm, name, pick);

			selected[name] = pick;
			selectedRegistries[name] = registry;

			foreach (List<Constraint> list in constraints.Values)
			{
				list.RemoveAll(item => item.From == name);
			}

			IRegistry? dependencyRegistry = DependencyRegistry(pick.Manifest, registry);
			List<DependencySpec> dependencies = DependenciesOf(pick.Manifest, request.Realm, $"dependency of {name}@{pick.Version}");

			CheckLinkNames(dependencies, $"{name}@{pick.Version}", name);

			foreach (DependencySpec dependency in dependencies)
			{
				AddConstraint(dependency, name, dependencyRegistry);
			}
		}

		RegistryRelease Pick(string name, List<RegistryRelease> candidates)
		{
			if (request.Preferred.TryGetValue(name, out string? lockedVersion))
			{
				RegistryRelease? locked = candidates.FirstOrDefault(release => release.Version == lockedVersion);

				if (locked is not null)
				{
					return locked;
				}
			}

			return candidates.Where(release => release.Deprecated is null).MaxBy(release => release.SemVer)
				?? candidates.MaxBy(release => release.SemVer)!;
		}

		IRegistry RegistryFor(string name, List<Constraint> requirements)
		{
			IRegistry? pinned = request.Pinned(name);

			if (pinned is not null)
			{
				return pinned;
			}

			if (edgeRegistries.TryGetValue(name, out IRegistry? registry) && registry is not null)
			{
				return registry;
			}

			string owners = string.Join(", ", requirements.Select(item => item.From).Distinct());

			throw new CraterException(
				$"{Fmt.Pkg(name)} (required by {owners}) needs a registry, but none is configured. " +
				$"Set {Fmt.Val("registry")} in {Fmt.File(Manifest.FileName)}.");
		}

		IRegistry? DependencyRegistry(Manifest manifest, IRegistry packageRegistry)
		{
			if (packageRegistry is SourceRegistry source)
			{
				if (string.IsNullOrWhiteSpace(manifest.RegistryLocation))
				{
					return request.DefaultRegistry;
				}

				return Registries.Open(manifest.RegistryLocation, source.Directory);
			}

			if (Registries.IsAbsolute(manifest.RegistryLocation))
			{
				return Registries.Open(manifest.RegistryLocation, Environment.CurrentDirectory);
			}

			return packageRegistry;
		}

		async Task<CraterException> NoMatchingVersionAsync(string name, List<Constraint> requirements, RegistryIndex index)
		{
			string details = string.Join("\n", requirements.Select(item => $"    {Fmt.Pkg(item.From)} requires {Fmt.Val(item.Req.Describe())}"));
			string available = index.Versions.Count == 0 ? "none" : string.Join(", ", index.Versions.Select(release => release.Version));
			string message = $"[{request.Realm.Key()}] No version of {Fmt.Pkg(name)} satisfies all requirements:\n{details}\n" +
				$"  Available versions: {Fmt.Val(available)}";

			List<string> fixes = await UpdatesThatFixAsync(name, requirements, index);

			if (fixes.Count > 0)
			{
				message += "\n" + string.Join("\n", fixes);
			}

			RegistryRelease? latest = index.Latest;

			if (latest is not null && !request.Overrides.ContainsKey(name))
			{
				string lead = fixes.Count > 0 ? "Or, to force one version for every package" : "To use one version for every package";

				message += $"\n  {lead}, add {Fmt.Cmd($"\"overrides\": [\"{name}^{latest.Version}\"]")} " +
					$"to {Fmt.File(Manifest.FileName)}.";
			}

			return new CraterException(message);
		}

		async Task<List<string>> UpdatesThatFixAsync(string name, List<Constraint> requirements, RegistryIndex index)
		{
			foreach (RegistryRelease target in index.Versions.Where(release => release.Deprecated is null).OrderByDescending(release => release.SemVer))
			{
				List<string> fixes = [];

				foreach (Constraint constraint in requirements.Where(item => !item.Req.Matches(target.SemVer)))
				{
					string? fix = await FixForAsync(constraint, name, target);

					if (fix is null)
					{
						fixes.Clear();
						break;
					}

					fixes.Add(fix);
				}

				if (fixes.Count > 0)
				{
					return fixes;
				}
			}

			return [];
		}

		async Task<string?> FixForAsync(Constraint constraint, string name, RegistryRelease target)
		{
			if (constraint.From == OverrideOwner)
			{
				return null;
			}

			if (constraint.From == RootOwner)
			{
				return $"  Your {Fmt.File(Manifest.FileName)} asks for {Fmt.Val(constraint.Req.Describe())}: {Fmt.Cmd($"crater update -i {name}")} can move it to {Fmt.Val(target.Version)}.";
			}

			string? parentVersion = await NewerAcceptingAsync(constraint.From, name, target);

			return parentVersion is null
				? null
				: $"  {Fmt.Pkg($"{constraint.From}@{parentVersion}")} accepts {Fmt.Pkg($"{name}@{target.Version}")}: run {Fmt.Cmd($"crater update -i {constraint.From}")}.";
		}

		async Task<string?> NewerAcceptingAsync(string parent, string name, RegistryRelease target)
		{
			if (!selected.TryGetValue(parent, out RegistryRelease? current) || !selectedRegistries.TryGetValue(parent, out IRegistry? registry))
			{
				return null;
			}

			RegistryIndex? index;

			try
			{
				index = await registry.GetIndexAsync(parent);
			}
			catch (CraterException)
			{
				return null;
			}

			return index?.Versions
				.Where(release => release.SemVer > current.SemVer && release.Deprecated is null)
				.OrderBy(release => release.SemVer)
				.FirstOrDefault(release => Accepts(release, parent, name, target))
				?.Version;
		}

		bool Accepts(RegistryRelease release, string parent, string name, RegistryRelease target)
		{
			try
			{
				return DependenciesOf(release.Manifest, request.Realm, $"dependency of {parent}@{release.Version}")
					.Any(spec => spec.FullName == name && spec.Req.Matches(target.SemVer));
			}
			catch (CraterException)
			{
				return false;
			}
		}

		public ResolvedGraph BuildGraph()
		{
			Dictionary<string, ResolvedPackage> nodes = [];

			ResolvedPackage Node(string name)
			{
				if (nodes.TryGetValue(name, out ResolvedPackage? existing))
				{
					return existing;
				}

				ResolvedPackage node = new()
				{
					Name = name,
					Release = selected[name],
					Registry = selectedRegistries[name],
				};

				nodes[name] = node;

				IEnumerable<DependencySpec> dependencies = DependenciesOf(node.Manifest, request.Realm)
					.DistinctBy(spec => spec.LinkName, StringComparer.OrdinalIgnoreCase);

				foreach (DependencySpec dependency in dependencies)
				{
					node.Dependencies.Add(new ResolvedDependency(dependency.LinkName, Node(dependency.FullName)));
				}

				return node;
			}

			List<ResolvedDependency> roots = request.Roots
				.DistinctBy(spec => spec.LinkName, StringComparer.OrdinalIgnoreCase)
				.Select(spec => new ResolvedDependency(spec.LinkName, Node(spec.FullName)))
				.ToList();

			Validate(nodes);

			return new ResolvedGraph
			{
				Realm = request.Realm,
				Roots = roots,
				All = nodes.Values.OrderBy(node => node.Name, StringComparer.Ordinal).ToList(),
				Notes = OverrideNotes(nodes),
			};
		}

		void Validate(Dictionary<string, ResolvedPackage> nodes)
		{
			foreach (DependencySpec spec in request.Roots)
			{
				SemVer chosen = nodes[spec.FullName].Version;

				if (!request.Overrides.ContainsKey(spec.FullName) && !spec.Req.Matches(chosen))
				{
					throw new CraterException(
						$"[{request.Realm.Key()}] {Fmt.Pkg(spec.FullName)}: selected {Fmt.Val(chosen)}, " +
						$"but crater.json requires {Fmt.Val(spec.Req.Describe())}");
				}
			}

			foreach (ResolvedPackage node in nodes.Values)
			{
				foreach (DependencySpec spec in DependenciesOf(node.Manifest, request.Realm))
				{
					SemVer chosen = nodes[spec.FullName].Version;

					if (!request.Overrides.ContainsKey(spec.FullName) && !spec.Req.Matches(chosen))
					{
						throw new CraterException(
							$"[{request.Realm.Key()}] Conflict: {Fmt.Pkg(node.Id)} requires {Fmt.Pkg(spec.FullName)} " +
							$"{Fmt.Val(spec.Req.Describe())}, selected {Fmt.Val(chosen)}");
					}
				}
			}
		}

		List<string> OverrideNotes(Dictionary<string, ResolvedPackage> nodes)
		{
			List<string> notes = [];

			foreach ((string name, Constraint constraint) in overridden)
			{
				if (!nodes.TryGetValue(name, out ResolvedPackage? node) || constraint.Req.Matches(node.Version))
				{
					continue;
				}

				notes.Add(
					$"{Fmt.Pkg(constraint.From)} wants {Fmt.Pkg(name)} {Fmt.Val(constraint.Req.Describe())}, " +
					$"using {Fmt.Val(node.Version)} from overrides");
			}

			return notes.Distinct().ToList();
		}
	}

	static List<DependencySpec> DependenciesOf(Manifest manifest, Realm realm, string? where = null)
	{
		return manifest.TransitiveDepsFor(realm).Distinct().Select(raw => DependencySpec.Parse(raw, where)).ToList();
	}

	static void CheckRealm(Realm realm, string name, RegistryRelease release)
	{
		string importTo = release.Manifest.ImportTo;
		string id = Fmt.Pkg($"{name}@{release.Version}");

		if (realm == Realm.Client && importTo == "server")
		{
			throw new CraterException($"{id} is server-only (import_to = server) but is listed in import_client.");
		}

		if (realm == Realm.Server && importTo == "client")
		{
			throw new CraterException($"{id} is client-only (import_to = client) but is listed in import_server.");
		}
	}

	static void CheckLinkNames(IEnumerable<DependencySpec> specs, string owner, string? ownerName)
	{
		string? ownerShortName = ownerName?[(ownerName.IndexOf('/') + 1)..];
		Dictionary<string, string> byLinkName = new(StringComparer.OrdinalIgnoreCase);

		foreach (DependencySpec spec in specs)
		{
			if (ownerName is not null && spec.FullName == ownerName)
			{
				throw new CraterException($"{Fmt.Pkg(owner)} depends on itself.");
			}

			if (ownerShortName is not null && spec.LinkName.Equals(ownerShortName, StringComparison.OrdinalIgnoreCase))
			{
				throw new CraterException(
					$"{Fmt.Pkg(owner)}: dependency {Fmt.Pkg(spec.FullName)} would be linked as {Fmt.Val(spec.LinkName)}, " +
					$"the same name as the package itself. Give it an alias, for example {Fmt.Cmd(SuggestAlias(spec))}.");
			}

			if (byLinkName.TryGetValue(spec.LinkName, out string? other) && other != spec.FullName)
			{
				throw new CraterException(
					$"{Fmt.Pkg(owner)}: dependencies {Fmt.Pkg(other)} and {Fmt.Pkg(spec.FullName)} would both be linked as " +
					$"{Fmt.Val(spec.LinkName)}. Give one of them an alias, for example {Fmt.Cmd(SuggestAlias(spec))}.");
			}

			byLinkName[spec.LinkName] = spec.FullName;
		}
	}

	static string SuggestAlias(DependencySpec spec)
	{
		string alias = Pascal(spec.Scope) + Pascal(spec.Name);

		if (alias.Length == 0 || !char.IsAsciiLetter(alias[0]))
		{
			alias = "Pkg" + alias;
		}

		string suffix = spec.Source is null ? spec.Req.ToString() : "@" + spec.Source.Text;

		return $"{alias}:{spec.FullName}{suffix}";
	}

	static string Pascal(string text)
	{
		IEnumerable<string> words = text.Split(['-', '_'], StringSplitOptions.RemoveEmptyEntries);

		return string.Concat(words.Select(word => char.ToUpperInvariant(word[0]) + word[1..]));
	}
}
