using System.Text;

namespace Crater.Core;

public static class LuauLexer
{
	public static (string Kept, string Masked) Lex(string source)
	{
		StringBuilder kept = new(source.Length);
		StringBuilder masked = new(source.Length);
		int position = 0;

		while (position < source.Length)
		{
			char current = source[position];
			bool isComment = current == '-' && position + 1 < source.Length && source[position + 1] == '-';

			if (isComment)
			{
				int level = LongBracketLevel(source, position + 2);
				int end = level >= 0
					? LongBracketEnd(source, position + 2 + level + 2, level)
					: LineEnd(source, position);

				Blank(kept, masked, source, position, end);
				position = end;

				continue;
			}

			if (IsQuote(current))
			{
				int end = Math.Min(SkipQuoted(source, position) + 1, source.Length);
				int length = end - position;

				kept.Append(source, position, length);
				masked.Append(current).Append('x', Math.Max(0, length - 2));

				if (length >= 2)
				{
					masked.Append(source[end - 1]);
				}

				position = end;

				continue;
			}

			int stringLevel = current == '[' ? LongBracketLevel(source, position) : -1;

			if (stringLevel >= 0)
			{
				int end = LongBracketEnd(source, position + stringLevel + 2, stringLevel);

				Blank(kept, masked, source, position, end);
				position = end;

				continue;
			}

			kept.Append(current);
			masked.Append(current);
			position++;
		}

		return (kept.ToString(), masked.ToString());
	}

	public static bool IsQuote(char character) => character is '"' or '\'' or '`';

	public static int SkipQuoted(string text, int open)
	{
		char quote = text[open];

		for (int i = open + 1; i < text.Length; i++)
		{
			if (text[i] == '\\')
			{
				i++;
				continue;
			}

			if (text[i] == quote)
			{
				return i;
			}
		}

		return text.Length - 1;
	}

	static int LongBracketLevel(string text, int position)
	{
		if (position >= text.Length || text[position] != '[')
		{
			return -1;
		}

		int cursor = position + 1;

		while (cursor < text.Length && text[cursor] == '=')
		{
			cursor++;
		}

		bool closed = cursor < text.Length && text[cursor] == '[';

		return closed ? cursor - position - 1 : -1;
	}

	static int LongBracketEnd(string text, int start, int level)
	{
		string close = "]" + new string('=', level) + "]";
		int end = text.IndexOf(close, start, StringComparison.Ordinal);

		return end < 0 ? text.Length : end + close.Length;
	}

	static int LineEnd(string text, int start)
	{
		int end = text.IndexOf('\n', start);

		return end < 0 ? text.Length : end;
	}

	static void Blank(StringBuilder kept, StringBuilder masked, string source, int from, int to)
	{
		for (int i = from; i < to; i++)
		{
			char replacement = source[i] == '\n' ? '\n' : ' ';

			kept.Append(replacement);
			masked.Append(replacement);
		}
	}
}
