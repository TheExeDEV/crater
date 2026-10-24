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

	[JsonIgnore] public bool HadConflicts { get; private set; }

	public static LockFile Load(string path)
	{
		if (!File.Exists(path))
		{
			return new LockFile();
		}

		string text = File.ReadAllText(path);

		if (!HasConflictMarkers(text))
		{
			return Json.Parse<LockFile>(text, path);
		}

		Out.Warn($"{Fmt.File(FileName)} has git merge conflicts. crater keeps the versions from both sides and rebuilds it from {Fmt.File(Manifest.FileName)}.");

		return Merge(text, path);
	}

	public static bool HasConflictMarkers(string text)
	{
		return text.Replace("\r\n", "\n").Split('\n').Any(line => line.StartsWith("<<<<<<<", StringComparison.Ordinal));
	}

	static LockFile Merge(string text, string path)
	{
		(string ours, string theirs) = Sides(text);
		List<LockedPackage> packages = [];

		foreach (string side in new[] { ours, theirs })
		{
			try
			{
				packages.AddRange(Json.Parse<LockFile>(side, path).Packages);
			}
			catch (CraterException)
			{
			}
		}

		return new LockFile
		{
			Packages = packages.DistinctBy(package => (package.Realm, package.Name)).ToList(),
			HadConflicts = true,
		};
	}

	static (string Ours, string Theirs) Sides(string text)
	{
		System.Text.StringBuilder ours = new();
		System.Text.StringBuilder theirs = new();
		int state = 0;

		foreach (string line in text.Replace("\r\n", "\n").Split('\n'))
		{
			if (line.StartsWith("<<<<<<<", StringComparison.Ordinal))
			{
				state = 1;
				continue;
			}

			if (line.StartsWith("|||||||", StringComparison.Ordinal) && state == 1)
			{
				state = 3;
				continue;
			}

			if (line.StartsWith("=======", StringComparison.Ordinal) && state is 1 or 3)
			{
				state = 2;
				continue;
			}

			if (line.StartsWith(">>>>>>>", StringComparison.Ordinal) && state == 2)
			{
				state = 0;
				continue;
			}

			if (state is 0 or 1)
			{
				ours.Append(line).Append('\n');
			}

			if (state is 0 or 2)
			{
				theirs.Append(line).Append('\n');
			}
		}

		return (ours.ToString(), theirs.ToString());
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
