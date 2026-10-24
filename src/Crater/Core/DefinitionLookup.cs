using System.Text.RegularExpressions;

namespace Crater.Core;

public static class DefinitionLookup
{
	public const string Extension = ".d.luau";
	const int AutoLimit = 10;

	public static List<string> Resolve(string directory, IReadOnlyList<string> patterns)
	{
		List<string> all = AllDefinitionFiles(directory);
		List<string> result = [];

		foreach (string pattern in patterns)
		{
			foreach (string match in Match(directory, all, pattern))
			{
				if (!result.Contains(match, StringComparer.OrdinalIgnoreCase))
				{
					result.Add(match);
				}
			}
		}

		return result;
	}

	public static List<string> Match(string directory, IReadOnlyList<string> all, string pattern)
	{
		string normalized = Links.NormalizeRelative(pattern);

		if (normalized.Contains('*') || normalized.Contains('?'))
		{
			Regex regex = Glob(normalized);

			return all.Where(path => regex.IsMatch(path)).ToList();
		}

		string full = Path.Combine(directory, normalized);

		if (File.Exists(full))
		{
			return [normalized];
		}

		if (Directory.Exists(full))
		{
			string prefix = normalized.Length == 0 ? "" : normalized + "/";

			return all.Where(path => path.StartsWith(prefix, StringComparison.OrdinalIgnoreCase)).ToList();
		}

		return [];
	}

	public static List<string> AutoDetect(string directory)
	{
		return AllDefinitionFiles(directory).Take(AutoLimit).ToList();
	}

	static List<string> AllDefinitionFiles(string directory)
	{
		if (!Directory.Exists(directory))
		{
			return [];
		}

		return Directory.EnumerateFiles(directory, "*" + Extension, SearchOption.AllDirectories)
			.Select(path => Path.GetRelativePath(directory, path).Replace('\\', '/'))
			.Where(path => !path.Split('/').Any(segment => segment.StartsWith('.') || segment == Layout.LegacyIndexFolder))
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToList();
	}

	static Regex Glob(string pattern)
	{
		string body = Regex.Escape(pattern)
			.Replace(@"\*\*/", "\u0001")
			.Replace(@"\*\*", "\u0002")
			.Replace(@"\*", "[^/]*")
			.Replace(@"\?", "[^/]")
			.Replace("\u0001", "(.*/)?")
			.Replace("\u0002", ".*");

		return new Regex("^" + body + "$", RegexOptions.IgnoreCase);
	}
}
