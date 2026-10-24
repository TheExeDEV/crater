using System.Text;

namespace Crater.Util;

public sealed class TomlTable : Dictionary<string, object>
{
	public string? String(string key) => TryGetValue(key, out object? value) ? value as string : null;

	public bool? Bool(string key) => TryGetValue(key, out object? value) && value is bool flag ? flag : null;

	public List<string> Strings(string key)
	{
		if (TryGetValue(key, out object? value) && value is List<object> list)
		{
			return list.OfType<string>().ToList();
		}

		return [];
	}

	public TomlTable? Table(string key) => TryGetValue(key, out object? value) ? value as TomlTable : null;
}

public static class Toml
{
	public static TomlTable Parse(string text, string source)
	{
		TomlTable root = new();
		TomlTable current = root;
		int lineNumber = 0;

		foreach (string rawLine in text.Replace("\r", "").Split('\n'))
		{
			lineNumber++;

			string line = StripComment(rawLine).Trim();

			if (line.Length == 0)
			{
				continue;
			}

			try
			{
				if (line.StartsWith('['))
				{
					current = OpenSection(root, line);
					continue;
				}

				int equals = FindEquals(line);

				if (equals < 0)
				{
					throw new FormatException("expected key = value");
				}

				string key = UnquoteKey(line[..equals].Trim());
				int position = equals + 1;
				object value = ParseValue(line, ref position);

				current[key] = value;
			}
			catch (FormatException exception)
			{
				throw new CraterException($"{Fmt.File(source)} line {lineNumber}: {exception.Message}");
			}
		}

		return root;
	}

	static TomlTable OpenSection(TomlTable root, string line)
	{
		string name = line.Trim('[', ']').Trim();
		TomlTable table = root;

		foreach (string part in SplitKey(name))
		{
			string key = UnquoteKey(part.Trim());

			if (table.Table(key) is not TomlTable child)
			{
				child = new TomlTable();
				table[key] = child;
			}

			table = child;
		}

		return table;
	}

	static List<string> SplitKey(string name)
	{
		List<string> parts = [];
		StringBuilder current = new();
		char? quote = null;

		foreach (char character in name)
		{
			if (quote is null && character is '"' or '\'')
			{
				quote = character;
			}
			else if (quote == character)
			{
				quote = null;
			}
			else if (quote is null && character == '.')
			{
				parts.Add(current.ToString());
				current.Clear();
				continue;
			}

			current.Append(character);
		}

		parts.Add(current.ToString());

		return parts;
	}

	static object ParseValue(string text, ref int position)
	{
		SkipSpaces(text, ref position);

		if (position >= text.Length)
		{
			throw new FormatException("missing value");
		}

		char first = text[position];

		if (first is '"' or '\'')
		{
			return ParseString(text, ref position);
		}

		if (first == '[')
		{
			return ParseArray(text, ref position);
		}

		if (first == '{')
		{
			return ParseInlineTable(text, ref position);
		}

		int start = position;

		while (position < text.Length && text[position] is not (',' or ']' or '}'))
		{
			position++;
		}

		string bare = text[start..position].Trim();

		return bare switch
		{
			"true" => true,
			"false" => false,
			_ => bare,
		};
	}

	static string ParseString(string text, ref int position)
	{
		char quote = text[position];
		StringBuilder builder = new();

		position++;

		while (position < text.Length && text[position] != quote)
		{
			if (quote == '"' && text[position] == '\\' && position + 1 < text.Length)
			{
				position++;
				builder.Append(text[position] switch { 'n' => '\n', 't' => '\t', _ => text[position] });
			}
			else
			{
				builder.Append(text[position]);
			}

			position++;
		}

		if (position >= text.Length)
		{
			throw new FormatException("unterminated string");
		}

		position++;

		return builder.ToString();
	}

	static List<object> ParseArray(string text, ref int position)
	{
		List<object> items = [];

		position++;

		while (true)
		{
			SkipSpaces(text, ref position);

			if (position >= text.Length)
			{
				throw new FormatException("unterminated array (multi-line arrays are not supported)");
			}

			if (text[position] == ']')
			{
				position++;
				return items;
			}

			items.Add(ParseValue(text, ref position));
			SkipSpaces(text, ref position);

			if (position < text.Length && text[position] == ',')
			{
				position++;
			}
		}
	}

	static TomlTable ParseInlineTable(string text, ref int position)
	{
		TomlTable table = new();

		position++;

		while (true)
		{
			SkipSpaces(text, ref position);

			if (position >= text.Length)
			{
				throw new FormatException("unterminated inline table");
			}

			if (text[position] == '}')
			{
				position++;
				return table;
			}

			int equals = text.IndexOf('=', position);

			if (equals < 0)
			{
				throw new FormatException("expected key = value in inline table");
			}

			string key = UnquoteKey(text[position..equals].Trim());

			position = equals + 1;
			table[key] = ParseValue(text, ref position);

			SkipSpaces(text, ref position);

			if (position < text.Length && text[position] == ',')
			{
				position++;
			}
		}
	}

	static void SkipSpaces(string text, ref int position)
	{
		while (position < text.Length && char.IsWhiteSpace(text[position]))
		{
			position++;
		}
	}

	static int FindEquals(string line)
	{
		bool inQuotes = false;

		for (int i = 0; i < line.Length; i++)
		{
			if (line[i] == '"')
			{
				inQuotes = !inQuotes;
			}
			else if (line[i] == '=' && !inQuotes)
			{
				return i;
			}
		}

		return -1;
	}

	static string StripComment(string line)
	{
		char? quote = null;

		for (int i = 0; i < line.Length; i++)
		{
			char current = line[i];

			if (quote is null && current is '"' or '\'')
			{
				quote = current;
			}
			else if (quote == current)
			{
				quote = null;
			}
			else if (quote is null && current == '#')
			{
				return line[..i];
			}
		}

		return line;
	}

	static string UnquoteKey(string key)
	{
		bool quoted = key.Length >= 2 && key[0] is '"' or '\'' && key[^1] == key[0];

		return quoted ? key[1..^1] : key;
	}
}
