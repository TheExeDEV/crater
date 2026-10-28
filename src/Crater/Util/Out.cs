using System.Text.Json.Nodes;
using System.Text.RegularExpressions;

namespace Crater.Util;

public sealed class CraterException(string message) : Exception(message);

public static class Fmt
{
	public static string Pkg(object value) => $"[p]{value}[/]";

	public static string File(object value) => $"[f]{value}[/]";

	public static string Cmd(object value) => $"[c]{value}[/]";

	public static string Val(object value) => $"[v]{value}[/]";

	public static string Good(object value) => $"[g]{value}[/]";

	public static string Bad(object value) => $"[r]{value}[/]";

	public static string Plural(int count, string singular, string plural) =>
		count == 1 ? $"1 {singular}" : $"{count} {plural}";
}

public static partial class Out
{
	static readonly bool UseColor = Environment.GetEnvironmentVariable("NO_COLOR") is null;

	[GeneratedRegex(@"\[(p|f|c|v|g|r)\](.*?)\[/\]")]
	private static partial Regex Markup();

	public static bool Quiet { get; set; }

	public static string Plain(string text) => Markup().Replace(text, "$2");

	public static void WriteJson(JsonNode? node) => Console.Out.WriteLine(Json.Serialize(node ?? new JsonArray()));

	public static void Info(string message) => WriteLine(Console.Out, "", null, message, null);

	public static void Step(string message) => WriteLine(Console.Out, "» ", ConsoleColor.Blue, message, null);

	public static void Ok(string message) => WriteLine(Console.Out, "✓ ", ConsoleColor.Green, message, ConsoleColor.Green);

	public static void Warn(string message) => WriteLine(Quiet ? Console.Error : Console.Out, "! ", ConsoleColor.Yellow, message, ConsoleColor.Yellow);

	public static void Error(string message) => WriteLine(Console.Error, "✗ ", ConsoleColor.Red, message, ConsoleColor.Red);

	public static void Dim(string message) => WriteLine(Console.Out, "", null, message, ConsoleColor.DarkGray);

	public static void Heading(string message) => WriteLine(Console.Out, "", null, message, ConsoleColor.White);

	public static void Inline(string message)
	{
		if (Quiet)
		{
			return;
		}

		lock (LiveLine.Sync)
		{
			Render(Console.Out, message, null);
		}
	}

	public static void Question(string message)
	{
		lock (LiveLine.Sync)
		{
			LiveLine.ClearUnlocked();
			Segment(Console.Out, "? ", ConsoleColor.Magenta);
			Render(Console.Out, message, null);
		}
	}

	public static void TreeNode(string indent, string name, string version, string? alias = null)
	{
		string aliasPart = alias is null ? "" : $"[c]{alias}[/] = ";

		WriteLine(Console.Out, "", null, $"{indent}{aliasPart}[p]{name}[/]@[v]{version}[/]", null);
	}

	internal static void RenderUnlocked(TextWriter writer, string message) => Render(writer, message, null);

	static void WriteLine(TextWriter writer, string prefix, ConsoleColor? prefixColor, string message, ConsoleColor? baseColor)
	{
		if (Quiet && writer == Console.Out)
		{
			return;
		}

		lock (LiveLine.Sync)
		{
			LiveLine.ClearUnlocked();

			Segment(writer, prefix, prefixColor);
			Render(writer, message, baseColor);
			writer.WriteLine();

			LiveLine.RedrawUnlocked();
		}
	}

	static void Render(TextWriter writer, string text, ConsoleColor? baseColor)
	{
		int position = 0;

		foreach (Match match in Markup().Matches(text))
		{
			Segment(writer, text[position..match.Index], baseColor);
			Segment(writer, match.Groups[2].Value, TagColor(match.Groups[1].Value));

			position = match.Index + match.Length;
		}

		Segment(writer, text[position..], baseColor);
	}

	static void Segment(TextWriter writer, string text, ConsoleColor? color)
	{
		if (text.Length == 0)
		{
			return;
		}

		if (!UseColor || color is null)
		{
			writer.Write(text);
			return;
		}

		ConsoleColor previous = Console.ForegroundColor;
		Console.ForegroundColor = color.Value;
		writer.Write(text);
		Console.ForegroundColor = previous;
	}

	static ConsoleColor TagColor(string tag) => tag switch
	{
		"p" => ConsoleColor.Cyan,
		"f" => ConsoleColor.Blue,
		"c" => ConsoleColor.Yellow,
		"g" => ConsoleColor.Green,
		"r" => ConsoleColor.Red,
		_ => ConsoleColor.Magenta,
	};
}

public sealed class Prompter(bool enabled)
{
	bool yesToAll;

	public bool Confirm(string action)
	{
		if (!enabled || yesToAll)
		{
			return true;
		}

		Out.Question(action);
		Out.Inline("[v] [Y/n/a][/] ");

		string? answer = Console.ReadLine();

		if (answer is null)
		{
			Console.WriteLine();
			Out.Warn($"No input, action skipped (use {Fmt.Cmd("-y")} to skip confirmations)");

			return false;
		}

		switch (answer.Trim().ToLowerInvariant())
		{
			case "" or "y" or "yes":
				return true;

			case "a" or "all":
				yesToAll = true;
				return true;

			default:
				return false;
		}
	}
}
