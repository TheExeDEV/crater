using System.Text.RegularExpressions;
using Crater.Registry;
using Crater.Versioning;

namespace Crater.Core;

public static partial class Changelog
{
	public static readonly string[] FileNames = ["CHANGELOG.md", "changelog.md", "Changelog.md", "CHANGES.md", "HISTORY.md"];

	const int MaxLength = 4000;

	[GeneratedRegex(@"^(?<level>#{1,4})\s+\[?v?(?<version>\d+\.\d+\.\d+(?:-[0-9A-Za-z.\-]+)?)\]?(?=[\s\]\-(]|$)")]
	private static partial Regex VersionHeading();

	[GeneratedRegex(@"^(?<level>#{1,4})\s")]
	private static partial Regex AnyHeading();

	public static string? FindFile(string directory)
	{
		return FileNames.Select(name => Path.Combine(directory, name)).FirstOrDefault(File.Exists);
	}

	public static string? NotesFor(string directory, string version)
	{
		string? path = FindFile(directory);

		return path is null ? null : Section(File.ReadAllText(path), version);
	}

	public static string? Section(string markdown, string version)
	{
		if (!SemVer.TryParse(version, out SemVer wanted))
		{
			return null;
		}

		string[] lines = markdown.Replace("\r\n", "\n").Split('\n');
		int start = -1;
		int level = 0;

		for (int index = 0; index < lines.Length; index++)
		{
			Match match = VersionHeading().Match(lines[index].Trim());

			if (match.Success && SemVer.TryParse(match.Groups["version"].Value, out SemVer found) && found.Equals(wanted))
			{
				start = index + 1;
				level = match.Groups["level"].Length;
				break;
			}
		}

		if (start < 0)
		{
			return null;
		}

		List<string> section = [];

		for (int index = start; index < lines.Length; index++)
		{
			Match heading = AnyHeading().Match(lines[index].Trim());

			if (heading.Success && heading.Groups["level"].Length <= level)
			{
				break;
			}

			section.Add(lines[index].TrimEnd());
		}

		string text = string.Join('\n', section).Trim('\n', ' ');

		if (text.Length == 0)
		{
			return null;
		}

		return text.Length <= MaxLength ? text : text[..MaxLength].TrimEnd() + "\n...";
	}

	public static List<RegistryRelease> Between(RegistryIndex index, SemVer from, SemVer to)
	{
		return index.Versions
			.Where(release => release.SemVer > from && release.SemVer <= to && !string.IsNullOrWhiteSpace(release.Notes))
			.OrderByDescending(release => release.SemVer)
			.ToList();
	}
}
