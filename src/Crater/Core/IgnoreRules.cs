using System.Text;
using System.Text.RegularExpressions;

namespace Crater.Core;

public sealed class IgnoreRules
{
	public static readonly string[] FileNames = [".gitignore", ".craterignore"];

	sealed record Rule(Regex Pattern, string Base, bool Negated, bool DirectoryOnly);

	readonly List<Rule> rules = [];

	public IgnoreRules With(string root, string directory)
	{
		IgnoreRules combined = new();
		combined.rules.AddRange(rules);

		string relativeBase = Path.GetRelativePath(root, directory).Replace('\\', '/');
		string basePath = relativeBase == "." ? "" : relativeBase;

		foreach (string name in FileNames)
		{
			string file = Path.Combine(directory, name);

			if (File.Exists(file))
			{
				combined.rules.AddRange(File.ReadAllLines(file).Select(line => Parse(line, basePath)).OfType<Rule>());
			}
		}

		return combined;
	}

	public bool IsIgnored(string relativePath, bool isDirectory)
	{
		bool ignored = false;

		foreach (Rule rule in rules)
		{
			if (rule.DirectoryOnly && !isDirectory)
			{
				continue;
			}

			string? candidate = rule.Base.Length == 0
				? relativePath
				: relativePath.StartsWith(rule.Base + "/", StringComparison.Ordinal) ? relativePath[(rule.Base.Length + 1)..] : null;

			if (candidate is not null && rule.Pattern.IsMatch(candidate))
			{
				ignored = !rule.Negated;
			}
		}

		return ignored;
	}

	static Rule? Parse(string line, string basePath)
	{
		string text = TrimTrailingSpaces(line);

		if (text.Length == 0 || text.StartsWith('#'))
		{
			return null;
		}

		bool negated = false;

		if (text.StartsWith('!'))
		{
			negated = true;
			text = text[1..];
		}
		else if (text.StartsWith("\\!") || text.StartsWith("\\#"))
		{
			text = text[1..];
		}

		bool directoryOnly = text.EndsWith('/');
		text = text.TrimEnd('/');

		if (text.Length == 0)
		{
			return null;
		}

		bool anchored = text.Contains('/');
		text = text.TrimStart('/');

		string body = Translate(text);
		string pattern = anchored ? $"^{body}$" : $"^(?:.*/)?{body}$";

		return new Rule(new Regex(pattern, RegexOptions.CultureInvariant), basePath, negated, directoryOnly);
	}

	static string Translate(string glob)
	{
		StringBuilder builder = new();

		for (int index = 0; index < glob.Length; index++)
		{
			char character = glob[index];

			if (character == '*' && index + 1 < glob.Length && glob[index + 1] == '*')
			{
				bool startsSegment = index == 0 || glob[index - 1] == '/';
				bool slashAfter = index + 2 < glob.Length && glob[index + 2] == '/';

				if (startsSegment && slashAfter)
				{
					builder.Append("(?:.*/)?");
					index += 2;
				}
				else
				{
					builder.Append(".*");
					index += 1;
				}

				continue;
			}

			switch (character)
			{
				case '*':
					builder.Append("[^/]*");
					break;

				case '?':
					builder.Append("[^/]");
					break;

				case '[':
					int close = glob.IndexOf(']', index + 1);

					if (close < 0)
					{
						builder.Append("\\[");
						break;
					}

					string set = glob[(index + 1)..close];

					builder.Append('[').Append(set.StartsWith('!') ? "^" + Regex.Escape(set[1..]).Replace("\\-", "-") : Regex.Escape(set).Replace("\\-", "-")).Append(']');
					index = close;
					break;

				case '\\' when index + 1 < glob.Length:
					builder.Append(Regex.Escape(glob[index + 1].ToString()));
					index++;
					break;

				default:
					builder.Append(Regex.Escape(character.ToString()));
					break;
			}
		}

		return builder.ToString();
	}

	static string TrimTrailingSpaces(string line)
	{
		int end = line.Length;

		while (end > 0 && line[end - 1] is ' ' or '\t' or '\r' && !(end > 1 && line[end - 2] == '\\'))
		{
			end--;
		}

		return line[..end];
	}
}
