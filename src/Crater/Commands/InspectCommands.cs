using System.Text.Json;
using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Registry;
using Crater.Tools;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class InspectCommands
{
	public static async Task OutdatedAsync(Args args)
	{
		Project project = Common.LoadProject();

		if (!project.Manifest.AllDependencies().Any())
		{
			Out.Info($"No dependencies in {Fmt.File(Manifest.FileName)}.");

			if (Out.Quiet)
			{
				Out.WriteJson(new JsonArray());
			}

			return;
		}

		List<UpdateCandidate> plan;

		using (new Spinner("Checking registry"))
		{
			plan = await UpdatePlanner.PlanAsync(project);
		}

		List<UpdateCandidate> shown = args.Has("all") ? plan : plan.Where(candidate => candidate.NeedsAttention).ToList();

		if (Out.Quiet)
		{
			Out.WriteJson(new JsonArray(shown.Select(candidate => (JsonNode)new JsonObject
			{
				["name"] = candidate.Name,
				["realm"] = candidate.Realm.Key(),
				["installed"] = candidate.Installed,
				["wanted"] = candidate.Wanted,
				["latest"] = candidate.Latest,
				["requirement"] = candidate.Requirement,
				["direct"] = candidate.IsDirect,
				["can_update"] = candidate.CanUpdate,
				["newer_outside_requirement"] = candidate.HasNewer,
				["deprecated"] = candidate.Index?.Deprecated,
			}).ToArray()));
			return;
		}

		if (shown.Count == 0)
		{
			Out.Ok("All dependencies are up to date");
			return;
		}

		List<IReadOnlyList<string>> rows = shown.Select(OutdatedRow).ToList();

		Table.Print(["Package", "Realm", "Installed", "Wanted", "Latest", "Requirement"], rows);
		Out.Info("");

		if (shown.Any(candidate => candidate.CanUpdate))
		{
			Out.Info($"Run {Fmt.Cmd("crater update")} to move to the {Fmt.Good("Wanted")} versions, or {Fmt.Cmd("crater update -i")} to pick.");
		}

		if (shown.Any(candidate => candidate.HasNewer))
		{
			Out.Info($"{Fmt.Val("Latest")} is outside the requirement in {Fmt.File(Manifest.FileName)}. {Fmt.Cmd("crater update -i")} can change it.");
		}

		if (shown.Any(candidate => candidate.Installed is null))
		{
			Out.Info($"Some packages are not installed yet. Run {Fmt.Cmd("crater install")}.");
		}

		foreach (UpdateCandidate candidate in shown.Where(candidate => candidate.IsDirect && candidate.Index is not null).DistinctBy(candidate => candidate.Name))
		{
			if (SemVer.TryParse(candidate.Installed ?? "", out SemVer from) && SemVer.TryParse(candidate.Latest ?? "", out SemVer to))
			{
				VersionNotices.PrintNotes(candidate.Name, Changelog.Between(candidate.Index!, from, to), 8);
			}
		}

		foreach (UpdateCandidate candidate in shown.Where(candidate => candidate.Index?.Deprecated is not null).DistinctBy(candidate => candidate.Name))
		{
			Out.Warn($"{Fmt.Pkg(candidate.Name)} is deprecated: {candidate.Index!.Deprecated}");
		}
	}

	static IReadOnlyList<string> OutdatedRow(UpdateCandidate candidate)
	{
		string installed = candidate.Installed is null ? Fmt.Cmd("-") : candidate.Installed;
		string wanted = candidate.Wanted ?? "?";
		string latest = candidate.Latest ?? "?";

		return
		[
			Fmt.Pkg(candidate.Name),
			candidate.Realm.Key(),
			installed,
			candidate.CanUpdate ? Fmt.Good(wanted) : wanted,
			candidate.HasNewer ? Fmt.Val(latest) : latest,
			candidate.Requirement,
		];
	}

	public static void List()
	{
		Project project = Common.LoadProject();
		LockFile lockFile = LockFile.Load(project.LockPath);

		if (Out.Quiet)
		{
			JsonObject realms = [];

			foreach (Realm realm in Realms.All)
			{
				realms[realm.Key()] = JsonSerializer.SerializeToNode(lockFile.For(realm).ToList(), Json.Options);
			}

			Out.WriteJson(realms);
			return;
		}

		if (lockFile.Packages.Count == 0)
		{
			Out.Info($"No packages installed. Run {Fmt.Cmd("crater install")}.");
			return;
		}

		foreach (Realm realm in Realms.All)
		{
			Dictionary<string, LockedPackage> packages = lockFile.For(realm).ToDictionary(package => package.Id);

			if (packages.Count == 0)
			{
				continue;
			}

			Out.Heading($"{project.Config.FolderFor(realm)}/ ({realm.ManifestField()})");

			HashSet<string> printed = [];

			foreach (LockedPackage root in packages.Values.Where(package => package.Direct))
			{
				foreach (string link in root.Links ?? [root.ShortName])
				{
					PrintNode(packages, root, link, 0, printed);
				}
			}
		}
	}

	static void PrintNode(Dictionary<string, LockedPackage> packages, LockedPackage package, string? link, int depth, HashSet<string> printed)
	{
		string? alias = link is not null && link != package.ShortName ? link : null;
		bool alreadyShown = !printed.Add(package.Id);
		string indent = Layout.TreeIndent(depth);

		if (alreadyShown && depth > 0)
		{
			string aliasPart = alias is null ? "" : $"[c]{alias}[/] = ";

			Out.Info($"{indent}{aliasPart}[p]{package.Name}[/]@[v]{package.Version}[/] [r](shared)[/]");
			return;
		}

		Out.TreeNode(indent, package.Name, package.Version, alias);

		foreach (string entry in package.Dependencies)
		{
			(string? childAlias, string id) = ResolvedDependency.ParseLockEntry(entry);

			if (packages.TryGetValue(id, out LockedPackage? child))
			{
				PrintNode(packages, child, childAlias, depth + 1, printed);
			}
		}
	}

	public static void Why(Args args)
	{
		string target = args.First?.ToLowerInvariant()
			?? throw new CraterException($"Usage: {Fmt.Cmd("crater why <scope/name>")}");

		Project project = Common.LoadProject();
		LockFile lockFile = LockFile.Load(project.LockPath);
		bool found = false;

		foreach (Realm realm in Realms.All)
		{
			Dictionary<string, LockedPackage> byName = lockFile.For(realm).ToDictionary(package => package.Name);

			if (!byName.ContainsKey(target))
			{
				continue;
			}

			found = true;
			List<List<string>> chains = FindChains(byName, target);

			Out.Heading($"{project.Config.FolderFor(realm)}/ ({realm.ManifestField()})");

			foreach (List<string> chain in chains.Take(20))
			{
				string path = string.Join(" → ", chain.Select(name => Fmt.Pkg($"{name}@{byName[name].Version}")));

				Out.Info($"  {Fmt.File(Manifest.FileName)} → {path}");
			}

			if (chains.Count > 20)
			{
				Out.Dim($"  and {chains.Count - 20} more");
			}
		}

		if (!found)
		{
			throw new CraterException($"{Fmt.Pkg(target)} is not installed.");
		}
	}

	static List<List<string>> FindChains(Dictionary<string, LockedPackage> byName, string target)
	{
		List<List<string>> chains = [];

		void Walk(string name, List<string> path)
		{
			if (path.Contains(name) || chains.Count > 100)
			{
				return;
			}

			List<string> next = [.. path, name];

			if (name == target)
			{
				chains.Add(next);
				return;
			}

			foreach (string entry in byName[name].Dependencies)
			{
				string id = ResolvedDependency.ParseLockEntry(entry).Id;
				string child = id[..id.LastIndexOf('@')];

				if (byName.ContainsKey(child))
				{
					Walk(child, next);
				}
			}
		}

		foreach (LockedPackage root in byName.Values.Where(package => package.Direct))
		{
			Walk(root.Name, []);
		}

		return chains;
	}

	public static async Task InfoAsync(Args args)
	{
		DependencySpec spec = ResolveSpec(args.First ?? throw new CraterException($"Usage: {Fmt.Cmd("crater info <scope/name>")}"));
		string name = spec.FullName;
		IRegistry registry = OpenRegistry(args, spec);
		RegistryIndex index = await registry.GetIndexAsync(name)
			?? throw new CraterException($"{Fmt.Pkg(name)} not found in {Fmt.File(registry.Location)}");

		RegistryRelease latest = index.Latest
			?? throw new CraterException($"{Fmt.Pkg(name)} has no published versions.");

		Manifest manifest = latest.Manifest;

		if (Out.Quiet)
		{
			Out.WriteJson(new JsonObject
			{
				["name"] = name,
				["latest"] = latest.Version,
				["registry"] = registry.Location,
				["deprecated"] = index.Deprecated,
				["owners"] = new JsonArray((index.Owners ?? []).Select(owner => (JsonNode?)owner).ToArray()),
				["versions"] = new JsonArray(index.Versions.OrderByDescending(release => release.SemVer).Select(release => (JsonNode)new JsonObject
				{
					["version"] = release.Version,
					["published_at"] = release.PublishedAt?.ToString("o"),
					["deprecated"] = release.Deprecated,
				}).ToArray()),
				["scripts"] = JsonSerializer.SerializeToNode(latest.Scripts, Json.Options),
				["notes"] = latest.Notes,
				["manifest"] = JsonSerializer.SerializeToNode(manifest, Json.Options),
			});
			return;
		}

		Out.Heading($"{name} {latest.Version}");

		if (index.Deprecated is not null)
		{
			Out.Warn($"Deprecated: {index.Deprecated}");
		}

		if (latest.Scripts is { Count: > 0 } scripts)
		{
			Out.Warn($"Runs scripts in your game: {PackageScripts.Describe(scripts)}");
		}

		if (manifest.Description.Length > 0)
		{
			Out.Info(manifest.Description);
		}

		Out.Info("");
		PrintField("install", Fmt.Cmd($"\"{(spec.RegistryName is null ? "" : spec.RegistryName + "#")}{name}^{latest.Version}\""));
		PrintField("realm", manifest.ImportTo);
		PrintField("license", manifest.License);
		PrintField("authors", string.Join(", ", manifest.Authors));
		PrintField("owners", string.Join(", ", index.Owners ?? []));
		PrintField("keywords", string.Join(", ", manifest.Keywords ?? []));
		PrintField("repository", manifest.Repository);
		PrintField("published", latest.PublishedAt?.ToString("yyyy-MM-dd") ?? "");
		PrintField("versions", string.Join(", ", index.Versions.OrderByDescending(release => release.SemVer).Take(10).Select(release => release.Version)));
		PrintField("type", manifest.IsInterface ? "interface" : manifest.IsMonorepository ? "monorepository" : "package");
		PrintField("implements", string.Join(", ", (manifest.RealizedInterfaces ?? []).Select(item => Fmt.Pkg(item))));
		PrintField("packages", string.Join(", ", (manifest.MonorepositoryElements ?? []).Select(pair => $"{pair.Key} {Fmt.Pkg(pair.Value)}")));

		foreach (Realm realm in new[] { Realm.Client, Realm.Server })
		{
			List<string> dependencies = manifest.DepsFor(realm);

			if (dependencies.Count > 0)
			{
				PrintField(realm.ManifestField(), string.Join(", ", dependencies.Select(dependency => Fmt.Pkg(dependency))));
			}
		}

		if (!string.IsNullOrWhiteSpace(latest.Notes))
		{
			Out.Info("");
			Out.Heading($"Changes in {latest.Version}");

			foreach (string line in latest.Notes.Split('\n'))
			{
				Out.Dim("  " + line.TrimEnd());
			}
		}
	}

	public static async Task VersionsAsync(Args args)
	{
		DependencySpec spec = ResolveSpec(args.First ?? throw new CraterException($"Usage: {Fmt.Cmd("crater versions <scope/name>")}"));
		string name = spec.FullName;
		IRegistry registry = OpenRegistry(args, spec);
		RegistryIndex index = await registry.GetIndexAsync(name)
			?? throw new CraterException($"{Fmt.Pkg(name)} not found in {Fmt.File(registry.Location)}");

		List<IReadOnlyList<string>> rows = index.Versions
			.OrderByDescending(release => release.SemVer)
			.Select(release => (IReadOnlyList<string>)
			[
				release.Deprecated is null ? Fmt.Val(release.Version) : $"{release.Version} {Fmt.Bad("deprecated")}",
				release.Manifest.ImportTo,
				release.PublishedAt?.ToString("yyyy-MM-dd") ?? "",
				Truncate(release.Manifest.Description, 60),
			])
			.ToList();

		Table.Print(["Version", "Realm", "Published", "Description"], rows);
	}

	static void PrintField(string label, string value)
	{
		if (Out.Plain(value).Length == 0)
		{
			return;
		}

		Out.Info($"  [v]{label.PadRight(14)}[/] {value}");
	}

	internal static IRegistry OpenRegistry(Args args, DependencySpec? spec = null)
	{
		string? explicitRegistry = args.Value("registry");
		Project? project = TryLoadProject();

		if (explicitRegistry is not null)
		{
			string location = NamedRegistries.LooksLikeName(explicitRegistry)
				? NamedRegistries.Location(explicitRegistry, project?.Manifest)
				: explicitRegistry;

			return Registries.Open(location, Environment.CurrentDirectory);
		}

		if (spec?.RegistryName is string named)
		{
			return Registries.Open(NamedRegistries.Location(named, project?.Manifest), project?.Directory ?? Environment.CurrentDirectory);
		}

		if (project is not null)
		{
			return project.RequireRegistry();
		}

		if (BuildInfo.DefaultRegistry.Length == 0)
		{
			throw new CraterException($"No registry configured. Pass {Fmt.Cmd("--registry <location>")} or run inside a project.");
		}

		return Registries.Open(BuildInfo.DefaultRegistry, Environment.CurrentDirectory);
	}

	internal static DependencySpec ResolveSpec(string raw)
	{
		Project? project = raw.Contains('/') ? null : TryLoadProject();

		foreach (string dependency in project is null ? [] : Realms.All.SelectMany(project.Manifest.DepsFor))
		{
			try
			{
				DependencySpec spec = DependencySpec.Parse(dependency);

				if (spec.LinkName.Equals(raw, StringComparison.OrdinalIgnoreCase))
				{
					return spec;
				}
			}
			catch (CraterException)
			{
			}
		}

		return DependencySpec.Parse(raw);
	}

	internal static Project? TryLoadProject()
	{
		try
		{
			return Common.LoadProject();
		}
		catch (CraterException)
		{
			return null;
		}
	}

	internal static string Truncate(string text, int length)
	{
		return text.Length <= length ? text : text[..(length - 1)] + "…";
	}
}
