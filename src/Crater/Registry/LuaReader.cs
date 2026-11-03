using System.Globalization;
using System.Text;
using System.Text.Json.Nodes;

namespace Crater.Registry;

public sealed class LuaReader
{
	enum TokenKind
	{
		Name,
		String,
		Number,
		Symbol,
		End,
	}

	sealed record Token(TokenKind Kind, string Text, int Line);

	readonly List<Token> tokens;
	readonly JsonObject globals = [];
	readonly Dictionary<string, JsonNode?> locals = [];
	int position;

	LuaReader(string text)
	{
		tokens = Tokenize(text);
	}

	public static JsonObject Run(string text)
	{
		LuaReader reader = new(text);

		reader.Chunk();

		return reader.globals;
	}

	public static List<JsonNode?> List(JsonNode? node)
	{
		if (node is not JsonObject table)
		{
			return [];
		}

		List<JsonNode?> items = [];

		for (int index = 1; table.TryGetPropertyValue(index.ToString(CultureInfo.InvariantCulture), out JsonNode? item); index++)
		{
			items.Add(item);
		}

		return items;
	}

	public static string? Text(JsonNode? node)
	{
		return node is JsonValue value && value.TryGetValue(out string? text) ? text : null;
	}

	Token Current => tokens[position];

	void Chunk()
	{
		while (Current.Kind != TokenKind.End)
		{
			int start = position;
			int line = Current.Line;

			try
			{
				Statement();
			}
			catch (FormatException)
			{
				position = start;
				SkipStatement(line);
			}

			Accept(";");
		}
	}

	void Statement()
	{
		bool local = Accept("local");
		List<List<string>> targets = [Target()];

		while (Accept(","))
		{
			targets.Add(Target());
		}

		if (!Accept("="))
		{
			if (local)
			{
				foreach (List<string> target in targets)
				{
					locals[target[0]] = null;
				}

				return;
			}

			throw new FormatException();
		}

		List<JsonNode?> values = [Expression()];

		while (Accept(","))
		{
			values.Add(Expression());
		}

		for (int index = 0; index < targets.Count; index++)
		{
			Assign(targets[index], index < values.Count ? values[index] : null, local);
		}
	}

	List<string> Target()
	{
		List<string> path = [Name()];

		while (true)
		{
			if (Accept("."))
			{
				path.Add(Name());
			}
			else if (Accept("["))
			{
				path.Add(Key(Expression()));
				Expect("]");
			}
			else
			{
				return path;
			}
		}
	}

	void Assign(List<string> target, JsonNode? value, bool local)
	{
		if (target.Count == 1)
		{
			if (local || locals.ContainsKey(target[0]))
			{
				locals[target[0]] = value;
			}
			else
			{
				globals[target[0]] = value;
			}

			return;
		}

		if (Lookup(target[0]) is not JsonObject table)
		{
			throw new FormatException();
		}

		for (int index = 1; index < target.Count - 1; index++)
		{
			if (table[target[index]] is not JsonObject inner)
			{
				inner = [];
				table[target[index]] = inner;
			}

			table = inner;
		}

		table[target[^1]] = value;
	}

	JsonNode? Lookup(string name)
	{
		if (locals.TryGetValue(name, out JsonNode? local))
		{
			return local;
		}

		return globals[name];
	}

	JsonNode? Expression()
	{
		JsonNode? left = Primary();

		while (Accept(".."))
		{
			JsonNode? right = Primary();

			left = JsonValue.Create(Concat(left) + Concat(right));
		}

		return left;
	}

	static string Concat(JsonNode? node)
	{
		return node switch
		{
			JsonValue value when value.TryGetValue(out string? text) => text,
			JsonValue value when value.TryGetValue(out double number) => number.ToString(CultureInfo.InvariantCulture),
			_ => throw new FormatException(),
		};
	}

	JsonNode? Primary()
	{
		Token token = Current;

		switch (token.Kind)
		{
			case TokenKind.String:
				position++;
				return JsonValue.Create(token.Text);

			case TokenKind.Number:
				position++;
				return JsonValue.Create(double.Parse(token.Text, CultureInfo.InvariantCulture));
		}

		if (Accept("{"))
		{
			return Table();
		}

		if (Accept("("))
		{
			JsonNode? inner = Expression();

			Expect(")");

			return inner;
		}

		if (Accept("true"))
		{
			return JsonValue.Create(true);
		}

		if (Accept("false"))
		{
			return JsonValue.Create(false);
		}

		if (Accept("nil"))
		{
			return null;
		}

		JsonNode? node = Lookup(Name())?.DeepClone();

		while (true)
		{
			if (Accept("."))
			{
				node = (node as JsonObject)?[Name()]?.DeepClone();
			}
			else if (Accept("["))
			{
				string key = Key(Expression());

				Expect("]");
				node = (node as JsonObject)?[key]?.DeepClone();
			}
			else if (Current.Text is "(" or ":" || Current.Kind == TokenKind.String || Current.Text == "{")
			{
				throw new FormatException();
			}
			else
			{
				return node;
			}
		}
	}

	JsonObject Table()
	{
		JsonObject table = [];
		int next = 1;

		while (!Accept("}"))
		{
			if (Current.Text == "[")
			{
				position++;
				string key = Key(Expression());

				Expect("]");
				Expect("=");
				table[key] = Expression();
			}
			else if (Current.Kind == TokenKind.Name && tokens[position + 1].Text == "=" && !IsKeyword(Current.Text))
			{
				string key = Name();

				Expect("=");
				table[key] = Expression();
			}
			else
			{
				table[(next++).ToString(CultureInfo.InvariantCulture)] = Expression();
			}

			if (!Accept(",") && !Accept(";"))
			{
				Expect("}");
				break;
			}
		}

		return table;
	}

	static string Key(JsonNode? node)
	{
		return node switch
		{
			JsonValue value when value.TryGetValue(out string? text) => text,
			JsonValue value when value.TryGetValue(out double number) => number.ToString(CultureInfo.InvariantCulture),
			_ => throw new FormatException(),
		};
	}

	string Name()
	{
		if (Current.Kind != TokenKind.Name || IsKeyword(Current.Text))
		{
			throw new FormatException();
		}

		return tokens[position++].Text;
	}

	bool Accept(string symbol)
	{
		if (Current.Kind is TokenKind.Symbol or TokenKind.Name && Current.Text == symbol)
		{
			position++;
			return true;
		}

		return false;
	}

	void Expect(string symbol)
	{
		if (!Accept(symbol))
		{
			throw new FormatException();
		}
	}

	void SkipStatement(int line)
	{
		int depth = 0;

		while (Current.Kind != TokenKind.End)
		{
			Token token = tokens[position];

			if (depth == 0 && token.Line > line && token.Kind == TokenKind.Name && !IsKeyword(token.Text))
			{
				return;
			}

			depth += token.Text switch
			{
				"{" or "(" or "[" => 1,
				"}" or ")" or "]" => -1,
				_ => 0,
			};

			if (depth < 0)
			{
				depth = 0;
			}

			position++;
		}
	}

	static bool IsKeyword(string text)
	{
		return text is "and" or "break" or "do" or "else" or "elseif" or "end" or "false" or "for" or "function" or "goto" or "if" or "in"
			or "local" or "nil" or "not" or "or" or "repeat" or "return" or "then" or "true" or "until" or "while";
	}

	static List<Token> Tokenize(string text)
	{
		List<Token> result = [];
		int index = 0;
		int line = 1;

		while (index < text.Length)
		{
			char character = text[index];

			if (character == '\n')
			{
				line++;
				index++;
				continue;
			}

			if (char.IsWhiteSpace(character))
			{
				index++;
				continue;
			}

			if (character == '-' && index + 1 < text.Length && text[index + 1] == '-')
			{
				index += 2;

				if (LongBracket(text, index) is int level)
				{
					index = SkipLong(text, index, level, ref line, out _);
				}
				else
				{
					while (index < text.Length && text[index] != '\n')
					{
						index++;
					}
				}

				continue;
			}

			if (character == '[' && LongBracket(text, index) is int stringLevel)
			{
				int startLine = line;

				index = SkipLong(text, index, stringLevel, ref line, out string content);
				result.Add(new Token(TokenKind.String, content.StartsWith('\n') ? content[1..] : content, startLine));
				continue;
			}

			if (character is '"' or '\'')
			{
				result.Add(new Token(TokenKind.String, QuotedString(text, ref index, character), line));
				continue;
			}

			if (char.IsDigit(character) || (character == '.' && index + 1 < text.Length && char.IsDigit(text[index + 1])))
			{
				int start = index;

				while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '.'))
				{
					index++;
				}

				string number = text[start..index];

				result.Add(new Token(TokenKind.Number, number.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
					? long.Parse(number[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture).ToString(CultureInfo.InvariantCulture)
					: number, line));
				continue;
			}

			if (char.IsLetter(character) || character == '_')
			{
				int start = index;

				while (index < text.Length && (char.IsLetterOrDigit(text[index]) || text[index] == '_'))
				{
					index++;
				}

				result.Add(new Token(TokenKind.Name, text[start..index], line));
				continue;
			}

			string symbol = index + 2 < text.Length && text.Substring(index, 3) == "..." ? "..."
				: index + 1 < text.Length && text.Substring(index, 2) is ".." or "==" or "~=" or "<=" or ">=" or "::" ? text.Substring(index, 2)
				: character.ToString();

			result.Add(new Token(TokenKind.Symbol, symbol, line));
			index += symbol.Length;
		}

		result.Add(new Token(TokenKind.End, "", line));

		return result;
	}

	static int? LongBracket(string text, int index)
	{
		if (index >= text.Length || text[index] != '[')
		{
			return null;
		}

		int level = 0;
		int cursor = index + 1;

		while (cursor < text.Length && text[cursor] == '=')
		{
			level++;
			cursor++;
		}

		return cursor < text.Length && text[cursor] == '[' ? level : null;
	}

	static int SkipLong(string text, int index, int level, ref int line, out string content)
	{
		int start = index + level + 2;
		string close = "]" + new string('=', level) + "]";
		int end = text.IndexOf(close, start, StringComparison.Ordinal);

		if (end < 0)
		{
			end = text.Length;
		}

		content = text[start..end];
		line += content.Count(character => character == '\n');

		return Math.Min(text.Length, end + close.Length);
	}

	static string QuotedString(string text, ref int index, char quote)
	{
		StringBuilder builder = new();

		index++;

		while (index < text.Length && text[index] != quote)
		{
			char character = text[index];

			if (character != '\\' || index + 1 >= text.Length)
			{
				builder.Append(character);
				index++;
				continue;
			}

			char escaped = text[index + 1];
			index += 2;

			switch (escaped)
			{
				case 'n': builder.Append('\n'); break;
				case 't': builder.Append('\t'); break;
				case 'r': builder.Append('\r'); break;
				case '\n': builder.Append('\n'); break;
				default:
					if (char.IsDigit(escaped))
					{
						int start = index - 1;

						while (index < text.Length && index - start < 3 && char.IsDigit(text[index]))
						{
							index++;
						}

						builder.Append((char)int.Parse(text[start..index], CultureInfo.InvariantCulture));
					}
					else
					{
						builder.Append(escaped);
					}

					break;
			}
		}

		index++;

		return builder.ToString();
	}
}
