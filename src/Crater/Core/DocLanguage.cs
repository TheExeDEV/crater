using System.Text.RegularExpressions;
using Crater.Util;

namespace Crater.Core;

public static partial class DocLanguage
{
	const int MinForeignLetters = 4;
	const double ForeignLetterShare = 0.01;
	const int MinForeignWords = 4;
	const double ForeignWordShare = 0.05;

	const string NotAllowed = "is not in English or Russian";

	static readonly HashSet<string> ForeignWords =
	[
		"el", "los", "las", "para", "una", "por", "pero", "como", "del", "puede", "usar", "este", "esta",
		"und", "der", "das", "nicht", "ist", "eine", "auch", "sich", "wird", "werden", "oder", "wenn", "kann",
		"les", "des", "est", "pour", "avec", "une", "dans", "sont", "pas", "nous", "vous", "qui", "cette",
		"uma", "isso", "pelo", "mais", "il", "che", "della", "sono", "anche", "questo", "essere", "gli",
		"het", "een", "niet", "voor", "zijn", "wordt", "ook", "worden",
	];

	public static bool IsDocument(string path)
	{
		string name = Path.GetFileName(path).ToLowerInvariant();
		string extension = Path.GetExtension(name);

		if (name.StartsWith("license") || name.StartsWith("licence"))
		{
			return false;
		}

		return extension is ".md" or ".markdown"
			|| (extension is "" or ".txt" && (name.StartsWith("readme") || name.StartsWith("changelog")));
	}

	public static string? Problem(string text)
	{
		string prose = Prose(text);
		int letters = 0;
		int foreignCount = 0;

		foreach (char character in prose)
		{
			if (!char.IsLetter(character))
			{
				continue;
			}

			letters++;

			if (!IsAllowedLetter(character))
			{
				foreignCount++;
			}
		}

		if (foreignCount >= MinForeignLetters && foreignCount >= letters * ForeignLetterShare)
		{
			return NotAllowed;
		}

		List<string> words = WordPattern().Matches(prose.ToLowerInvariant()).Select(match => match.Value).ToList();
		int hits = words.Count(ForeignWords.Contains);

		return hits >= MinForeignWords && hits >= words.Count * ForeignWordShare ? NotAllowed : null;
	}

	public static void Require(string root, IEnumerable<string> files)
	{
		List<string> problems = [];

		foreach (string relative in files.Where(IsDocument))
		{
			string path = Path.Combine(root, relative);

			if (new FileInfo(path).Length > 2 * 1024 * 1024)
			{
				continue;
			}

			if (Problem(File.ReadAllText(path)) is string problem)
			{
				problems.Add($"{Fmt.File(relative)} {problem}");
			}
		}

		if (problems.Count > 0)
		{
			throw new CraterException(
				$"Docs and changelogs can only be in English or Russian. Change the language of these files and pack again:\n  {string.Join("\n  ", problems)}");
		}
	}

	static bool IsAllowedLetter(char character)
	{
		return character is >= 'a' and <= 'z' or >= 'A' and <= 'Z' or >= 'а' and <= 'я' or >= 'А' and <= 'Я' or 'ё' or 'Ё';
	}

	static string Prose(string text)
	{
		string result = FencePattern().Replace(text, " ");

		result = InlineCodePattern().Replace(result, " ");
		result = LinkTargetPattern().Replace(result, "]");
		result = UrlPattern().Replace(result, " ");
		result = TagPattern().Replace(result, " ");

		return result;
	}

	[GeneratedRegex(@"(```|~~~)[\s\S]*?(\1|$)")]
	private static partial Regex FencePattern();

	[GeneratedRegex(@"`[^`\n]*`")]
	private static partial Regex InlineCodePattern();

	[GeneratedRegex(@"\]\([^)]*\)")]
	private static partial Regex LinkTargetPattern();

	[GeneratedRegex(@"\b(https?|ftp)://\S+|\b[\w.-]+\.(com|org|net|io|dev|gg|app)\S*")]
	private static partial Regex UrlPattern();

	[GeneratedRegex(@"<[^>\n]+>")]
	private static partial Regex TagPattern();

	[GeneratedRegex(@"\p{L}+")]
	private static partial Regex WordPattern();
}
