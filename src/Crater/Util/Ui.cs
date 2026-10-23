using System.Diagnostics;

namespace Crater.Util;

public static class Terminal
{
	public static bool CanAnimate =>
		!Console.IsOutputRedirected &&
		Environment.GetEnvironmentVariable("CI") is null &&
		Environment.GetEnvironmentVariable("NO_COLOR") is null;

	public static bool CanPrompt => !Console.IsInputRedirected && !Console.IsOutputRedirected;

	public static int Width
	{
		get
		{
			if (Console.IsOutputRedirected)
			{
				return 100;
			}

			try
			{
				return Math.Max(40, Console.WindowWidth);
			}
			catch (IOException)
			{
				return 100;
			}
		}
	}
}

public static class LiveLine
{
	public static readonly object Sync = new();

	static string? current;
	static bool drawn;

	public static void Set(string? text)
	{
		lock (Sync)
		{
			ClearUnlocked();

			current = text;

			RedrawUnlocked();
		}
	}

	internal static void ClearUnlocked()
	{
		if (current is null || !drawn)
		{
			return;
		}

		int length = Math.Min(Terminal.Width - 1, Out.Plain(current).Length + 2);

		Console.Out.Write("\r" + new string(' ', length) + "\r");
		drawn = false;
	}

	internal static void RedrawUnlocked()
	{
		if (current is null)
		{
			return;
		}

		Out.RenderUnlocked(Console.Out, Fit(current));
		drawn = true;
	}

	static string Fit(string text)
	{
		string plain = Out.Plain(text);
		int max = Terminal.Width - 2;

		if (plain.Length <= max)
		{
			return text;
		}

		return plain[..(max - 1)] + "…";
	}
}

public sealed class Spinner : IDisposable
{
	static readonly string[] Frames = ["⠋", "⠙", "⠹", "⠸", "⠼", "⠴", "⠦", "⠧", "⠇", "⠏"];

	readonly Timer? timer;
	readonly Stopwatch watch = Stopwatch.StartNew();
	readonly string text;
	int frame;

	public Spinner(string text)
	{
		this.text = text;

		if (!Terminal.CanAnimate)
		{
			Out.Step(text);
			return;
		}

		timer = new Timer(_ => Tick(), null, 0, 80);
	}

	public TimeSpan Elapsed => watch.Elapsed;

	void Tick()
	{
		string symbol = Frames[frame % Frames.Length];
		frame++;

		LiveLine.Set($"[c]{symbol}[/] {text} [v]{watch.Elapsed.TotalSeconds:0.0}s[/]");
	}

	public void Dispose()
	{
		if (timer is null)
		{
			return;
		}

		timer.Dispose();
		LiveLine.Set(null);
	}
}

public sealed class ProgressBar(int total, string label) : IDisposable
{
	const int BarWidth = 24;

	int done;

	public void Advance(string item)
	{
		int count = Interlocked.Increment(ref done);

		if (!Terminal.CanAnimate)
		{
			Out.Dim($"  [{count}/{total}] {label} {item}");
			return;
		}

		int filled = total == 0 ? BarWidth : (int)Math.Round(BarWidth * (double)count / total);
		string bar = new string('█', filled) + new string('░', BarWidth - filled);

		LiveLine.Set($"[g]{bar}[/] {count}/{total} {label} [p]{item}[/]");
	}

	public void Dispose()
	{
		if (Terminal.CanAnimate)
		{
			LiveLine.Set(null);
		}
	}
}

public static class Select
{
	public sealed record Item(string Label, bool Selected, string? Hint = null);

	public static List<int>? Many(string title, IReadOnlyList<Item> items)
	{
		if (!Terminal.CanPrompt)
		{
			throw new CraterException("Interactive selection needs a terminal. Pass package names instead.");
		}

		bool[] selected = items.Select(item => item.Selected).ToArray();
		int cursor = 0;

		Out.Question(title);
		Console.WriteLine();
		Out.Dim("  ↑/↓ move, space toggle, a all, enter confirm, esc cancel");

		for (int i = 0; i < items.Count; i++)
		{
			Console.WriteLine();
		}

		int top = Math.Max(0, Console.CursorTop - items.Count);

		SetCursorVisible(false);

		try
		{
			Render(items, selected, cursor, top);

			while (true)
			{
				ConsoleKeyInfo key = Console.ReadKey(intercept: true);

				switch (key.Key)
				{
					case ConsoleKey.UpArrow or ConsoleKey.K:
						cursor = (cursor - 1 + items.Count) % items.Count;
						break;

					case ConsoleKey.DownArrow or ConsoleKey.J:
						cursor = (cursor + 1) % items.Count;
						break;

					case ConsoleKey.Spacebar:
						selected[cursor] = !selected[cursor];
						break;

					case ConsoleKey.A:
						bool selectAll = !selected.All(value => value);

						Array.Fill(selected, selectAll);
						break;

					case ConsoleKey.Enter:
						return Enumerable.Range(0, items.Count).Where(i => selected[i]).ToList();

					case ConsoleKey.Escape or ConsoleKey.Q:
						return null;
				}

				Render(items, selected, cursor, top);
			}
		}
		finally
		{
			SetCursorVisible(true);
		}
	}

	static void Render(IReadOnlyList<Item> items, bool[] selected, int cursor, int top)
	{
		Console.SetCursorPosition(0, top);

		for (int i = 0; i < items.Count; i++)
		{
			string pointer = i == cursor ? "[c]›[/]" : " ";
			string box = selected[i] ? "[g]◉[/]" : "○";
			string hint = items[i].Hint is null ? "" : "  " + items[i].Hint;
			string line = $"{pointer} {box} {items[i].Label}{hint}";

			int padding = Math.Max(0, Terminal.Width - 1 - Out.Plain(line).Length);

			Out.Inline(line + new string(' ', padding));
			Console.WriteLine();
		}
	}

	static void SetCursorVisible(bool visible)
	{
		try
		{
			Console.CursorVisible = visible;
		}
		catch (Exception exception) when (exception is IOException or PlatformNotSupportedException)
		{
		}
	}
}

public static class Table
{
	public static void Print(IReadOnlyList<string> headers, IReadOnlyList<IReadOnlyList<string>> rows)
	{
		int[] widths = new int[headers.Count];

		for (int column = 0; column < headers.Count; column++)
		{
			widths[column] = headers[column].Length;

			foreach (IReadOnlyList<string> row in rows)
			{
				widths[column] = Math.Max(widths[column], Out.Plain(row[column]).Length);
			}
		}

		Out.Heading(JoinRow(headers, widths));

		foreach (IReadOnlyList<string> row in rows)
		{
			Out.Info(JoinRow(row, widths));
		}
	}

	static string JoinRow(IReadOnlyList<string> cells, int[] widths)
	{
		List<string> padded = [];

		for (int column = 0; column < cells.Count; column++)
		{
			string cell = cells[column];
			int padding = widths[column] - Out.Plain(cell).Length;

			padded.Add(column == cells.Count - 1 ? cell : cell + new string(' ', padding));
		}

		return string.Join("  ", padded);
	}
}
