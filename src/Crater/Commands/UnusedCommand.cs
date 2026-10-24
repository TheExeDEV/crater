using System.Text.RegularExpressions;
using Crater.Core;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static partial class UnusedCommand
{
	sealed record Use(string Name, string File, int Line);

	[GeneratedRegex(@"--\[(=*)\[.*?\]\1\]", RegexOptions.Singleline)]
	private static partial Regex LongComment();

	[GeneratedRegex(@"--[^\n]*")]
	private static partial Regex LineComment();

	public static int Run()
	{
		Project project = Common.LoadProject();
		List<string> files = ProjectSources.LuauFiles(project, ProjectSources.Folders(project));
		Dictionary<string, string> sources = files.ToDictionary(file => file, file => StripComments(File.ReadAllText(file)));
		int problems = 0;

		foreach ((Realm realm, string folder) in project.Config.ConfiguredFolders())
		{
			string folderName = Path.GetFileName(folder.TrimEnd('/'));
			List<DependencySpec> declared = project.Manifest.DepsFor(realm).Select(raw => DependencySpec.Parse(raw)).ToList();
			HashSet<string> installed = InstalledNames(project.Abs(folder));
			List<Use> uses = sources.SelectMany(pair => FindUses(pair.Value, folderName, pair.Key)).ToList();
			HashSet<string> used = uses.Select(use => use.Name).ToHashSet(StringComparer.OrdinalIgnoreCase);

			foreach (DependencySpec spec in declared.Where(spec => !used.Contains(spec.LinkName)))
			{
				Out.Warn($"{Fmt.Pkg(spec.ToString())} in {Fmt.Val(realm.ManifestField())} is never required in your code. Remove it with {Fmt.Cmd($"crater remove {spec.LinkName}")}.");
				problems++;
			}

			HashSet<string> declaredNames = declared.Select(spec => spec.LinkName).ToHashSet(StringComparer.OrdinalIgnoreCase);

			foreach (IGrouping<string, Use> group in uses.Where(use => !declaredNames.Contains(use.Name)).GroupBy(use => use.Name, StringComparer.OrdinalIgnoreCase))
			{
				Use first = group.First();
				string where = $"{Path.GetRelativePath(project.Directory, first.File).Replace('\\', '/')}:{first.Line}";

				string reason = installed.Contains(group.Key)
					? "comes only as a dependency of another package. Add it to crater.json so an update of that package cannot remove it"
					: "is not installed";

				Out.Warn($"{Fmt.File(where)} requires {Fmt.Val($"{folderName}.{group.Key}")}, which {reason}.");
				problems++;
			}
		}

		if (problems == 0)
		{
			Out.Ok($"Every dependency is used and every required package is declared ({Fmt.Plural(files.Count, "file", "files")} checked)");
		}
		else
		{
			Out.Info("");
			Out.Dim("  Only requires written as Packages.Name, Packages[\"Name\"], WaitForChild(\"Name\") or \"@Packages/Name\" are found.");
		}

		return 0;
	}

	static HashSet<string> InstalledNames(string folder)
	{
		HashSet<string> names = new(StringComparer.OrdinalIgnoreCase);

		if (!Directory.Exists(folder))
		{
			return names;
		}

		foreach (string entry in Directory.EnumerateFileSystemEntries(folder))
		{
			string name = Path.GetFileName(entry);

			if (!name.StartsWith('.'))
			{
				names.Add(name.EndsWith(".luau", StringComparison.OrdinalIgnoreCase) ? name[..^5] : name);
			}
		}

		return names;
	}

	static IEnumerable<Use> FindUses(string text, string folderName, string file)
	{
		string folder = Regex.Escape(folderName);
		List<string> roots = [folder];

		foreach (Match match in Regex.Matches(text, $@"local\s+([A-Za-z_]\w*)\s*=\s*[^\n]*?(?:\.{folder}|\[\s*[""']{folder}[""']\s*\]|WaitForChild\(\s*[""']{folder}[""'][^)]*\))\s*$", RegexOptions.Multiline))
		{
			roots.Add(Regex.Escape(match.Groups[1].Value));
		}

		string root = $"(?:{string.Join('|', roots.Distinct())})";
		string[] patterns =
		[
			$@"(?<![\w.]){root}\s*\.\s*(?<name>[A-Za-z_]\w*)",
			$@"\.{folder}\s*\.\s*(?<name>[A-Za-z_]\w*)",
			$@"{root}\s*\[\s*[""'](?<name>[^""']+)[""']\s*\]",
			$@"{root}\s*:\s*(?:WaitForChild|FindFirstChild)\(\s*[""'](?<name>[^""']+)[""']",
			$@"require\(\s*[""']@{folder}/(?<name>[^""'/]+)",
		];

		foreach (string pattern in patterns)
		{
			foreach (Match match in Regex.Matches(text, pattern))
			{
				string name = match.Groups["name"].Value;

				if (name is "Parent" or "Name" or "ClassName")
				{
					continue;
				}

				yield return new Use(name, file, text[..match.Index].Count(character => character == '\n') + 1);
			}
		}
	}

	static string StripComments(string text)
	{
		string withoutLong = LongComment().Replace(text, match => new string('\n', match.Value.Count(character => character == '\n')));

		return LineComment().Replace(withoutLong, "");
	}
}
