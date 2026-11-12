using System.Text.RegularExpressions;

namespace Crater.Core;

public enum ApiKind
{
	Function,
	Value,
	Type,
	Module,
}

public sealed record ApiParameter(string Name, bool Required);

public sealed record ApiMember(string Key, ApiKind Kind, List<ApiParameter> Parameters, int RequiredTypeParameters);

public sealed partial class PublicApi
{
	public Dictionary<string, ApiMember> Members { get; } = new(StringComparer.Ordinal);
	public bool Opaque { get; private set; }

	sealed record Token(string Text, int Index, int Blocks, int Brackets);

	static readonly HashSet<string> BlockOpeners = ["function", "do", "repeat", "if"];
	static readonly HashSet<string> BlockClosers = ["end", "until"];
	static readonly HashSet<string> ExpressionStarts = ["=", "(", ",", "{", "[", "return", "and", "or", "not", ".", "+", "-", "*", "/", "%", "^", "<", ">", "~", "#"];

	[GeneratedRegex(@"[A-Za-z_]\w*|\d[\w.]*|\S")]
	private static partial Regex TokenRegex();

	[GeneratedRegex(@"(^|\|)\s*nil\s*($|\|)")]
	private static partial Regex NilUnion();

	public static PublicApi Read(string contentDirectory, string pathToMain, string packageName)
	{
		PublicApi api = new();
		PackageEntry entry = PackageEntry.Analyze(contentDirectory, pathToMain, packageName);

		switch (entry.Kind)
		{
			case EntryKind.Module:
				api.ReadModule(File.ReadAllText(entry.MainFile!), "");
				break;

			case EntryKind.Collection:
				string relative = Links.NormalizeRelative(pathToMain);
				string folder = relative.Length == 0 || !Directory.Exists(Path.Combine(contentDirectory, relative)) ? contentDirectory : Path.Combine(contentDirectory, relative);

				foreach (CollectionMember member in entry.Members)
				{
					api.Add(new ApiMember(member.Name, ApiKind.Module, [], 0));

					if (TypeExports.FindMainFile(folder, member.Name) is string file)
					{
						api.ReadModule(File.ReadAllText(file), member.Name + ".");
					}
				}

				break;

			default:
				api.Opaque = true;
				break;
		}

		return api;
	}

	public static PublicApi FromSource(string source)
	{
		PublicApi api = new();

		api.ReadModule(source, "");

		return api;
	}

	void Add(ApiMember member)
	{
		Members.TryAdd(member.Key, member);
	}

	void ReadModule(string source, string prefix)
	{
		foreach (TypeExport type in TypeExports.Parse(source))
		{
			int required = type.Parameters.Trim('<', '>').Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).Count(item => !item.Contains('='));

			Add(new ApiMember($"{prefix}type {type.Name}", ApiKind.Type, [], required));
		}

		string code = LuauLexer.Lex(source).Masked;
		List<Token> tokens = Tokenize(code);
		Dictionary<string, List<ApiParameter>> localFunctions = LocalFunctions(code, tokens);
		int returned = tokens.FindLastIndex(token => token.Text == "return" && token.Blocks == 0 && token.Brackets == 0);

		if (returned < 0 || returned + 1 >= tokens.Count)
		{
			Opaque = prefix.Length == 0 || Opaque;
			return;
		}

		Token first = tokens[returned + 1];

		if (first.Text == "function")
		{
			Add(new ApiMember(Callable(prefix), ApiKind.Function, Parameters(code, tokens, returned + 2), 0));
			return;
		}

		if (first.Text == "{")
		{
			ReadConstructor(code, tokens, returned + 1, localFunctions, prefix);
			return;
		}

		if (first.Text == "setmetatable" && Next(tokens, returned + 2) == "{")
		{
			ReadConstructor(code, tokens, returned + 3, localFunctions, prefix);
			return;
		}

		if (!IsWord(first.Text) || Next(tokens, returned + 2) is "(" or "." or ":" or "[")
		{
			Opaque = prefix.Length == 0 || Opaque;
			return;
		}

		string table = first.Text;

		if (localFunctions.TryGetValue(table, out List<ApiParameter>? callable))
		{
			Add(new ApiMember(Callable(prefix), ApiKind.Function, callable, 0));
			return;
		}

		ReadTable(code, tokens, table, localFunctions, prefix);
	}

	static string Callable(string prefix) => prefix.Length == 0 ? "()" : prefix.TrimEnd('.') + "()";

	void ReadTable(string code, List<Token> tokens, string table, Dictionary<string, List<ApiParameter>> localFunctions, string prefix)
	{
		for (int i = 0; i < tokens.Count; i++)
		{
			Token token = tokens[i];

			if (token.Blocks != 0 || token.Brackets != 0)
			{
				continue;
			}

			if (token.Text == "local" && Next(tokens, i + 1) == table && Next(tokens, i + 2) == "=")
			{
				int value = i + 3;

				if (Next(tokens, value) == "setmetatable" && Next(tokens, value + 1) == "(")
				{
					value += 2;
				}

				if (Next(tokens, value) == "{")
				{
					ReadConstructor(code, tokens, value, localFunctions, prefix);
				}

				continue;
			}

			if (token.Text == "function" && Next(tokens, i + 1) == table && Next(tokens, i + 2) is "." or ":" && IsWord(Next(tokens, i + 3)) && Next(tokens, i + 4) is "(" or "<")
			{
				AddField(prefix, tokens[i + 3].Text, ApiKind.Function, Parameters(code, tokens, i + 4));
				continue;
			}

			bool statementStart = i == 0 || tokens[i - 1].Text is not ("." or ":");

			if (statementStart && token.Text == table && Next(tokens, i + 1) == "." && IsWord(Next(tokens, i + 2)) && Next(tokens, i + 3) == "=" && Next(tokens, i + 4) != "=")
			{
				(ApiKind kind, List<ApiParameter> parameters) = Value(code, tokens, i + 4, localFunctions);

				AddField(prefix, tokens[i + 2].Text, kind, parameters);
			}
		}
	}

	void ReadConstructor(string code, List<Token> tokens, int open, Dictionary<string, List<ApiParameter>> localFunctions, string prefix)
	{
		Token brace = tokens[open];

		for (int i = open + 1; i < tokens.Count; i++)
		{
			Token token = tokens[i];

			if (token.Brackets <= brace.Brackets)
			{
				return;
			}

			bool atTop = token.Brackets == brace.Brackets + 1 && token.Blocks == brace.Blocks;
			bool entryStart = tokens[i - 1].Text is "{" or "," or ";";

			if (atTop && entryStart && IsWord(token.Text) && Next(tokens, i + 1) == "=" && Next(tokens, i + 2) != "=")
			{
				(ApiKind kind, List<ApiParameter> parameters) = Value(code, tokens, i + 2, localFunctions);

				AddField(prefix, token.Text, kind, parameters);
			}
		}
	}

	void AddField(string prefix, string name, ApiKind kind, List<ApiParameter> parameters)
	{
		if (!name.StartsWith('_'))
		{
			Add(new ApiMember(prefix + name, kind, parameters, 0));
		}
	}

	static (ApiKind Kind, List<ApiParameter> Parameters) Value(string code, List<Token> tokens, int index, Dictionary<string, List<ApiParameter>> localFunctions)
	{
		string? value = Next(tokens, index);

		if (value == "function")
		{
			return (ApiKind.Function, Parameters(code, tokens, index + 1));
		}

		bool plainName = value is not null && localFunctions.ContainsKey(value) && Next(tokens, index + 1) is not ("(" or "." or ":" or "[");

		return plainName ? (ApiKind.Function, localFunctions[value!]) : (ApiKind.Value, []);
	}

	static Dictionary<string, List<ApiParameter>> LocalFunctions(string code, List<Token> tokens)
	{
		Dictionary<string, List<ApiParameter>> functions = new(StringComparer.Ordinal);

		for (int i = 0; i + 2 < tokens.Count; i++)
		{
			if (tokens[i].Text != "local" || tokens[i].Blocks != 0)
			{
				continue;
			}

			if (tokens[i + 1].Text == "function" && IsWord(tokens[i + 2].Text))
			{
				functions[tokens[i + 2].Text] = Parameters(code, tokens, i + 3);
			}
			else if (IsWord(tokens[i + 1].Text) && Next(tokens, i + 2) == "=" && Next(tokens, i + 3) == "function")
			{
				functions[tokens[i + 1].Text] = Parameters(code, tokens, i + 4);
			}
		}

		return functions;
	}

	static List<ApiParameter> Parameters(string code, List<Token> tokens, int index)
	{
		while (index < tokens.Count && tokens[index].Text != "(")
		{
			index++;
		}

		if (index >= tokens.Count)
		{
			return [];
		}

		int start = tokens[index].Index + 1;
		int depth = 0;
		int end = start;

		for (; end < code.Length; end++)
		{
			char character = code[end];

			if (character is '(' or '{' or '[' or '<')
			{
				depth++;
			}
			else if (character is ')' or '}' or ']' or '>' && !(character == '>' && end > 0 && code[end - 1] == '-'))
			{
				if (depth == 0)
				{
					break;
				}

				depth--;
			}
		}

		return SplitTopLevel(code[start..Math.Min(end, code.Length)])
			.Select(item => item.Trim())
			.Where(item => item.Length > 0)
			.Select(Parameter)
			.ToList();
	}

	static ApiParameter Parameter(string text)
	{
		if (text.StartsWith("...", StringComparison.Ordinal))
		{
			return new ApiParameter("...", false);
		}

		int colon = text.IndexOf(':');

		if (colon < 0)
		{
			return new ApiParameter(text, false);
		}

		string name = text[..colon].Trim();
		string type = text[(colon + 1)..].Trim();
		bool optional = type.EndsWith('?') || NilUnion().IsMatch(type) || type is "any" or "unknown";

		return new ApiParameter(name, !optional);
	}

	static List<string> SplitTopLevel(string text)
	{
		List<string> items = [];
		int depth = 0;
		int start = 0;

		for (int i = 0; i < text.Length; i++)
		{
			char character = text[i];

			if (character is '(' or '{' or '[' or '<')
			{
				depth++;
			}
			else if (character is ')' or '}' or ']' || (character == '>' && (i == 0 || text[i - 1] != '-')))
			{
				depth--;
			}
			else if (character == ',' && depth == 0)
			{
				items.Add(text[start..i]);
				start = i + 1;
			}
		}

		items.Add(text[start..]);

		return items;
	}

	static List<Token> Tokenize(string code)
	{
		List<Token> tokens = [];
		int blocks = 0;
		int brackets = 0;
		string previous = "";

		foreach (Match match in TokenRegex().Matches(code))
		{
			string text = match.Value;

			if (BlockClosers.Contains(text))
			{
				blocks = Math.Max(0, blocks - 1);
			}
			else if (text is ")" or "}" or "]")
			{
				brackets = Math.Max(0, brackets - 1);
			}

			tokens.Add(new Token(text, match.Index, blocks, brackets));

			bool expressionIf = text == "if" && ExpressionStarts.Contains(previous);

			if (BlockOpeners.Contains(text) && !expressionIf && previous != ".")
			{
				blocks++;
			}
			else if (text is "(" or "{" or "[")
			{
				brackets++;
			}

			previous = text;
		}

		return tokens;
	}

	static string? Next(List<Token> tokens, int index) => index < tokens.Count ? tokens[index].Text : null;

	static bool IsWord(string? text) => text is { Length: > 0 } && (char.IsLetter(text[0]) || text[0] == '_');
}
