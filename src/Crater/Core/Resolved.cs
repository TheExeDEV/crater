using Crater.Models;
using Crater.Registry;
using Crater.Versioning;

namespace Crater.Core;

public sealed class ResolvedPackage
{
	public required string Name { get; init; }
	public required RegistryRelease Release { get; init; }
	public required IRegistry Registry { get; init; }
	public List<ResolvedDependency> Dependencies { get; } = [];

	public SemVer Version => Release.SemVer;
	public Manifest Manifest => Release.Manifest;
	public string Scope => Name[..Name.IndexOf('/')];
	public string ShortName => Name[(Name.IndexOf('/') + 1)..];
	public string Id => $"{Name}@{Version}";
	public bool IsSource => Registry is SourceRegistry;

	public override string ToString() => Id;
}

public sealed record ResolvedDependency(string LinkName, ResolvedPackage Package)
{
	public bool IsAliased => LinkName != Package.ShortName;

	public string LockEntry => IsAliased ? $"{LinkName}:{Package.Id}" : Package.Id;

	public static (string? Alias, string Id) ParseLockEntry(string entry)
	{
		int colon = entry.IndexOf(':');

		if (colon < 0)
		{
			return (null, entry);
		}

		return (entry[..colon], entry[(colon + 1)..]);
	}
}

public sealed class ResolvedGraph
{
	public required Realm Realm { get; init; }
	public List<ResolvedDependency> Roots { get; init; } = [];
	public List<ResolvedPackage> All { get; init; } = [];
	public List<string> Notes { get; init; } = [];

	public static ResolvedGraph Empty(Realm realm) => new() { Realm = realm };
}
