using Crater.Models;

namespace Crater.Core;

public sealed record LockChange(string Name, string Good, string Bad);

public static class Bisector
{
	public static List<LockChange> Changes(LockFile good, LockFile bad)
	{
		Dictionary<string, string> before = Versions(good);
		Dictionary<string, string> after = Versions(bad);

		return after
			.Where(pair => before.TryGetValue(pair.Key, out string? version) && version != pair.Value)
			.Select(pair => new LockChange(pair.Key, before[pair.Key], pair.Value))
			.OrderBy(change => change.Name, StringComparer.Ordinal)
			.ToList();
	}

	static Dictionary<string, string> Versions(LockFile lockFile)
	{
		return lockFile.Everything
			.GroupBy(package => package.Name)
			.ToDictionary(group => group.Key, group => string.Join(", ", group.Select(package => package.Version).Distinct().Order(StringComparer.Ordinal)));
	}

	public static LockFile Mix(LockFile good, LockFile bad, IReadOnlyCollection<string> reverted)
	{
		return new LockFile
		{
			Packages = bad.Packages.Select(package => Pick(package, good.Packages, reverted)).ToList(),
			Vendor = bad.Vendor?.Select(package => Pick(package, good.Vendor ?? [], reverted)).ToList(),
		};
	}

	static LockedPackage Pick(LockedPackage current, List<LockedPackage> good, IReadOnlyCollection<string> reverted)
	{
		if (!reverted.Contains(current.Name))
		{
			return current;
		}

		return good.FirstOrDefault(package => package.Realm == current.Realm && package.Name == current.Name)
			?? good.FirstOrDefault(package => package.Name == current.Name)
			?? current;
	}

	public static async Task<string?> FindAsync(IReadOnlyList<string> candidates, Func<IReadOnlyCollection<string>, Task<bool>> passes, Action<int, int, int>? step = null)
	{
		List<string> suspects = [.. candidates];
		List<string> innocent = [];
		int total = (int)Math.Ceiling(Math.Log2(Math.Max(1, suspects.Count)));
		int number = 0;

		while (suspects.Count > 1)
		{
			List<string> half = suspects.Take(suspects.Count / 2).ToList();

			step?.Invoke(++number, Math.Max(total, number), half.Count);

			if (await passes([.. innocent, .. half]))
			{
				innocent.AddRange(half);
				suspects = suspects.Skip(half.Count).ToList();
			}
			else
			{
				suspects = half;
			}
		}

		return suspects.FirstOrDefault();
	}
}
