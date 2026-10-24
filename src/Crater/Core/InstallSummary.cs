using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Core;

public static class InstallSummary
{
	enum ChangeKind
	{
		Added,
		Removed,
		Upgraded,
		Downgraded,
		Changed,
	}

	sealed record Change(ChangeKind Kind, string Realm, string Name, string? From, string? To);

	public static void Print(LockFile previous, LockFile next, TimeSpan elapsed)
	{
		List<Change> changes = Compare(previous, next);
		int total = next.Packages.Count;
		string time = $"{elapsed.TotalSeconds:0.0}s";

		if (changes.Count == 0)
		{
			Out.Ok($"No changes, {Fmt.Plural(total, "package", "packages")} ready in {time}");
			return;
		}

		Out.Info("");

		foreach (Change change in changes)
		{
			Out.Info(Describe(change));
		}

		Out.Info("");
		Out.Ok($"{Fmt.Plural(changes.Count, "change", "changes")}, {Fmt.Plural(total, "package", "packages")} installed in {time}");
	}

	static List<Change> Compare(LockFile previous, LockFile next)
	{
		Dictionary<(string, string), LockedPackage> before = previous.Packages.ToDictionary(package => (package.Realm, package.Name));
		Dictionary<(string, string), LockedPackage> after = next.Packages.ToDictionary(package => (package.Realm, package.Name));
		List<Change> changes = [];

		foreach (((string realm, string name), LockedPackage package) in after)
		{
			if (!before.TryGetValue((realm, name), out LockedPackage? old))
			{
				changes.Add(new Change(ChangeKind.Added, realm, name, null, package.Version));
				continue;
			}

			if (old.Version != package.Version)
			{
				changes.Add(new Change(VersionDirection(old.Version, package.Version), realm, name, old.Version, package.Version));
				continue;
			}

			if (old.Sha256 != package.Sha256)
			{
				changes.Add(new Change(ChangeKind.Changed, realm, name, old.Version, package.Version));
			}
		}

		foreach (((string realm, string name), LockedPackage package) in before)
		{
			if (!after.ContainsKey((realm, name)))
			{
				changes.Add(new Change(ChangeKind.Removed, realm, name, package.Version, null));
			}
		}

		return changes
			.OrderBy(change => Array.IndexOf(Realms.All.Select(realm => realm.Key()).ToArray(), change.Realm))
			.ThenBy(change => change.Name, StringComparer.Ordinal)
			.ToList();
	}

	static ChangeKind VersionDirection(string from, string to)
	{
		bool parsed = SemVer.TryParse(from, out SemVer old) & SemVer.TryParse(to, out SemVer current);

		if (parsed && current < old)
		{
			return ChangeKind.Downgraded;
		}

		return ChangeKind.Upgraded;
	}

	static string Describe(Change change)
	{
		string realm = $"[v]{change.Realm}[/]";

		return change.Kind switch
		{
			ChangeKind.Added => $"  [g]+[/] [p]{change.Name}[/] {change.To}  {realm}",
			ChangeKind.Removed => $"  [r]-[/] [p]{change.Name}[/] {change.From}  {realm}",
			ChangeKind.Upgraded => $"  [g]↑[/] [p]{change.Name}[/] {change.From} → [g]{change.To}[/]  {realm}",
			ChangeKind.Downgraded => $"  [c]↓[/] [p]{change.Name}[/] {change.From} → [c]{change.To}[/]  {realm}",
			_ => $"  [c]~[/] [p]{change.Name}[/] {change.To} (contents changed)  {realm}",
		};
	}
}
