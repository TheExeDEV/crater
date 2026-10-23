using System.Text.RegularExpressions;

namespace Crater.Core;

public sealed record TypeExport(string Name, string Parameters, string Arguments, bool Approximated = false);

public static partial class TypeExports
{
	[GeneratedRegex(@"(?<![\w.])export\s+type\s+(?<name>[A-Za-z_]\w*)\s*")]
	private static partial Regex ExportRegex();

	[GeneratedRegex(@"(?<![\w.])(?<export>export\s+)?type\s+(?<name>[A-Za-z_]\w*)\s*[<=]")]
	private static partial Regex DeclarationRegex();

	[GeneratedRegex(@"[A-Za-z_]\w*")]
	private static partial Regex Identifier();

	[GeneratedRegex(@"\s+")]
	private static partial Regex Whitespace();

	public static string? FindMainFile(string contentDirectory, string pathToMain)
	{
		string relative = Links.NormalizeRelative(pathToMain);
		string basePath = relative.Length == 0 ? contentDirectory : Path.Combine(contentDirectory, relative);

		if (relative.Length > 0 && File.Exists(basePath))
		{
			return basePath;
		}

		if (Directory.Exists(basePath))
		{
			foreach (string init in new[] { "init.luau", "init.lua" })
			{
				string candidate = Path.Combine(basePath, init);

				if (File.Exists(candidate))
				{
					return candidate;
				}
			}
		}

		if (relative.Length == 0)
		{
			return null;
		}

		foreach (string extension in new[] { ".luau", ".lua" })
		{
			if (File.Exists(basePath + extension))
			{
				return basePath + extension;
			}
		}

		return null;
	}

	public static List<TypeExport> Parse(string source)
	{
		(string kept, string code) = LuauLexer.Lex(source);
		(HashSet<string> exported, HashSet<string> local) = CollectDeclarations(code);

		List<TypeExport> result = [];
		HashSet<string> seen = [];

		foreach (Match match in ExportRegex().Matches(code))
		{
			string name = match.Groups["name"].Value;

			if (name == "function")
			{
				continue;
			}

			TypeExport? export = ParseDeclaration(name, match.Index + match.Length, kept, code, local);

			if (export is not null && seen.Add(name))
			{
				result.Add(export);
			}
		}

		return result;
	}

	static (HashSet<string> Exported, HashSet<string> Local) CollectDeclarations(string code)
	{
		HashSet<string> exported = [];
		HashSet<string> local = [];

		foreach (Match declaration in DeclarationRegex().Matches(code))
		{
			string name = declaration.Groups["name"].Value;

			if (declaration.Groups["export"].Success)
			{
				exported.Add(name);
			}
			else
			{
				local.Add(name);
			}
		}

		local.ExceptWith(exported);
		local.Remove("function");

		return (exported, local);
	}

	static TypeExport? ParseDeclaration(string name, int position, string kept, string code, HashSet<string> local)
	{
		string parameters = "";
		string arguments = "";
		bool approximated = false;

		if (position < code.Length && code[position] == '<')
		{
			int end = MatchingAngle(code, position);

			if (end < 0)
			{
				return null;
			}

			string inner = Whitespace().Replace(kept[(position + 1)..end], " ").Trim();
			List<string> items = SplitTopLevel(inner).Select(item => item.Trim()).Where(item => item.Length > 0).ToList();

			if (items.Count == 0)
			{
				return null;
			}

			List<string> names = items.Select(ParameterName).ToList();
			HashSet<string> generics = names.Select(item => item.TrimEnd('.')).ToHashSet();
			List<string> rewritten = [];

			foreach (string item in items)
			{
				(string rewrittenItem, bool changed) = RewriteDefault(item, local, generics);

				rewritten.Add(rewrittenItem);
				approximated |= changed;
			}

			parameters = $"<{string.Join(", ", rewritten)}>";
			arguments = $"<{string.Join(", ", names)}>";
			position = end + 1;

			while (position < code.Length && char.IsWhiteSpace(code[position]))
			{
				position++;
			}
		}

		if (position >= code.Length || code[position] != '=')
		{
			return null;
		}

		return new TypeExport(name, parameters, arguments, approximated);
	}

	static (string Item, bool Approximated) RewriteDefault(string item, HashSet<string> local, HashSet<string> generics)
	{
		int equals = item.IndexOf('=');

		if (equals < 0)
		{
			return (item, false);
		}

		string parameter = item[..equals].Trim();
		string value = item[(equals + 1)..].Trim();

		if (IsPortableDefault(value, local, generics))
		{
			return ($"{parameter} = {value}", false);
		}

		string fallback = parameter.EndsWith("...") ? "...any" : "any";

		return ($"{parameter} = {fallback}", true);
	}

	static string ParameterName(string parameter)
	{
		int equals = parameter.IndexOf('=');
		string name = equals >= 0 ? parameter[..equals] : parameter;

		return name.Trim();
	}

	static bool IsPortableDefault(string type, HashSet<string> local, HashSet<string> generics)
	{
		int position = 0;

		while (position < type.Length)
		{
			if (LuauLexer.IsQuote(type[position]))
			{
				position = LuauLexer.SkipQuoted(type, position) + 1;
				continue;
			}

			Match match = Identifier().Match(type, position);
			bool startsWord = position == 0 || !(char.IsLetterOrDigit(type[position - 1]) || type[position - 1] == '_');

			if (!match.Success || match.Index != position || !startsWord)
			{
				position++;
				continue;
			}

			string word = match.Value;
			int next = match.Index + match.Length;

			while (next < type.Length && type[next] == ' ')
			{
				next++;
			}

			char follower = next < type.Length ? type[next] : '\0';
			bool isPropertyName = follower == ':';

			if (!isPropertyName && !generics.Contains(word))
			{
				bool isPack = next + 1 < type.Length && type[next + 1] == '.';
				bool isModuleAccess = follower == '.' && !isPack;

				if (isModuleAccess || word == "typeof" || local.Contains(word))
				{
					return false;
				}
			}

			position = match.Index + match.Length;
		}

		return true;
	}

	static int MatchingAngle(string code, int open)
	{
		int depth = 0;

		for (int i = open; i < code.Length; i++)
		{
			char current = code[i];

			if (LuauLexer.IsQuote(current))
			{
				i = LuauLexer.SkipQuoted(code, i);
				continue;
			}

			bool isArrow = current == '>' && i > 0 && code[i - 1] == '-';

			if (isArrow)
			{
				continue;
			}

			if (current is '<' or '(' or '{' or '[')
			{
				depth++;
			}
			else if (current is '>' or ')' or '}' or ']')
			{
				depth--;

				if (depth == 0)
				{
					return current == '>' ? i : -1;
				}
			}
			else if (current == '=' && depth == 0)
			{
				return -1;
			}
		}

		return -1;
	}

	static List<string> SplitTopLevel(string text)
	{
		List<string> parts = [];
		int depth = 0;
		int start = 0;

		for (int i = 0; i < text.Length; i++)
		{
			char current = text[i];

			if (LuauLexer.IsQuote(current))
			{
				i = LuauLexer.SkipQuoted(text, i);
				continue;
			}

			bool isArrow = current == '>' && i > 0 && text[i - 1] == '-';

			if (isArrow)
			{
				continue;
			}

			if (current is '<' or '(' or '{' or '[')
			{
				depth++;
			}
			else if (current is '>' or ')' or '}' or ']')
			{
				depth--;
			}
			else if (current == ',' && depth == 0)
			{
				parts.Add(text[start..i]);
				start = i + 1;
			}
		}

		parts.Add(text[start..]);

		return parts;
	}
}
