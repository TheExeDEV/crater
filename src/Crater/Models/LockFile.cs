using System.Text.Json.Serialization;
using Crater.Core;
using Crater.Registry;
using Crater.Util;

namespace Crater.Models;

public sealed class LockFile
{
	public const string FileName = "crater.lock";

	[JsonPropertyName("lock_version")] public int LockVersion { get; set; } = 1;
	[JsonPropertyName("manifest_hash")] public string? ManifestHash { get; set; }
	[JsonPropertyName("packages")] public List<LockedPackage> Packages { get; set; } = [];

	public static LockFile Load(string path)
	{
		if (!File.Exists(path))
		{
			return new LockFile();
		}

		return Json.ReadFile<LockFile>(path);
	}

	public IEnumerable<LockedPackage> For(Realm realm) => Packages.Where(package => package.Realm == realm.Key());

	public Dictionary<string, string> PreferredVersions(Realm realm)
	{
		return For(realm)
			.Where(package => package.Source is null)
			.GroupBy(package => package.Name)
			.ToDictionary(group => group.Key, group => group.First().Version);
	}

	public Dictionary<string, string> LockedCommits()
	{
		Dictionary<string, string> commits = [];

		foreach (LockedPackage package in Packages)
		{
			if (package.Source is not null && package.Commit is not null)
			{
				commits[package.Source] = package.Commit;
			}
		}

		return commits;
	}

	public static LockFile From(IReadOnlyDictionary<Realm, ResolvedGraph> graphs, string projectDirectory)
	{
		LockFile lockFile = new();

		foreach (Realm realm in Realms.All)
		{
			if (!graphs.TryGetValue(realm, out ResolvedGraph? graph))
			{
				continue;
			}

			foreach (ResolvedPackage package in graph.All.OrderBy(package => package.Name, StringComparer.Ordinal))
			{
				lockFile.Packages.Add(LockedPackage.From(realm, graph, package, projectDirectory));
			}
		}

		return lockFile;
	}
}

public sealed class LockedPackage
{
	[JsonPropertyName("realm")] public string Realm { get; set; } = "";
	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("version")] public string Version { get; set; } = "";
	[JsonPropertyName("direct")] public bool Direct { get; set; }
	[JsonPropertyName("links")] public List<string>? Links { get; set; }
	[JsonPropertyName("registry")] public string? Registry { get; set; }
	[JsonPropertyName("source")] public string? Source { get; set; }
	[JsonPropertyName("commit")] public string? Commit { get; set; }
	[JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
	[JsonPropertyName("dependencies")] public List<string> Dependencies { get; set; } = [];

	[JsonIgnore] public string Id => $"{Name}@{Version}";

	[JsonIgnore] public string ShortName => Name[(Name.IndexOf('/') + 1)..];

	public static LockedPackage From(Realm realm, ResolvedGraph graph, ResolvedPackage package, string projectDirectory)
	{
		List<ResolvedDependency> rootEdges = graph.Roots.Where(edge => edge.Package == package).ToList();
		bool anyAliased = rootEdges.Any(edge => edge.IsAliased);

		LockedPackage locked = new()
		{
			Realm = realm.Key(),
			Name = package.Name,
			Version = package.Version.ToString(),
			Direct = rootEdges.Count > 0,
			Links = anyAliased ? rootEdges.Select(edge => edge.LinkName).ToList() : null,
			Sha256 = package.Release.Sha256,
			Dependencies = package.Dependencies
				.Select(edge => edge.LockEntry)
				.OrderBy(entry => entry, StringComparer.Ordinal)
				.ToList(),
		};

		switch (package.Registry)
		{
			case SourceRegistry source:
				locked.Source = source.SourceText;
				locked.Commit = source.Commit;
				break;

			case LocalRegistry local:
				locked.Registry = Path.GetRelativePath(projectDirectory, local.Location).Replace('\\', '/');
				break;

			default:
				locked.Registry = package.Registry.Location;
				break;
		}

		return locked;
	}
}
