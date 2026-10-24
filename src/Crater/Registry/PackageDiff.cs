using System.IO.Compression;
using System.Text;

namespace Crater.Registry;

public enum ChangeKind
{
	Added,
	Removed,
	Changed,
	Same,
}

public sealed record FileChange(string Path, ChangeKind Kind, long OldSize, long NewSize, string? OldText, string? NewText);

public static class PackageDiff
{
	const int MaxLinesPerFile = 400;
	const int MaxDiffCells = 4_000_000;

	static readonly string[] TextExtensions =
	[
		".luau", ".lua", ".json", ".toml", ".md", ".txt", ".yml", ".yaml", ".csv", ".gitignore", ".luaurc", ".d.luau",
	];

	public static Dictionary<string, byte[]> Read(byte[] archive)
	{
		using ZipArchive zip = new(new MemoryStream(archive), ZipArchiveMode.Read);
		Dictionary<string, byte[]> files = new(StringComparer.Ordinal);

		foreach (ZipArchiveEntry entry in zip.Entries.Where(entry => !entry.FullName.EndsWith('/')))
		{
			using Stream stream = entry.Open();
			using MemoryStream buffer = new();
			stream.CopyTo(buffer);
			files[entry.FullName.Replace('\\', '/')] = buffer.ToArray();
		}

		return files;
	}

	public static List<FileChange> Compare(byte[] newArchive, byte[]? oldArchive)
	{
		Dictionary<string, byte[]> current = Read(newArchive);
		Dictionary<string, byte[]> previous = oldArchive is null ? [] : Read(oldArchive);

		return current.Keys.Union(previous.Keys)
			.Order(StringComparer.Ordinal)
			.Select(path =>
			{
				byte[]? before = previous.GetValueOrDefault(path);
				byte[]? after = current.GetValueOrDefault(path);
				ChangeKind kind = before is null ? ChangeKind.Added
					: after is null ? ChangeKind.Removed
					: before.AsSpan().SequenceEqual(after) ? ChangeKind.Same
					: ChangeKind.Changed;

				return new FileChange(path, kind, before?.Length ?? 0, after?.Length ?? 0, Text(path, before), Text(path, after));
			})
			.ToList();
	}

	public static string Markdown(string id, string? previousVersion, List<FileChange> changes, int budget)
	{
		StringBuilder markdown = new();
		string against = previousVersion is null ? "first version" : $"compared with {previousVersion}";

		markdown.AppendLine($"### {id} ({against})");
		markdown.AppendLine();
		markdown.AppendLine("| File | Change | Size |");
		markdown.AppendLine("|---|---|---|");

		foreach (FileChange change in changes.Where(change => change.Kind != ChangeKind.Same))
		{
			markdown.AppendLine($"| `{change.Path}` | {change.Kind.ToString().ToLowerInvariant()} | {Size(change)} |");
		}

		int unchanged = changes.Count(change => change.Kind == ChangeKind.Same);

		if (unchanged > 0)
		{
			markdown.AppendLine($"| {unchanged} more | unchanged | |");
		}

		markdown.AppendLine();

		foreach (FileChange change in changes.Where(change => change.Kind is ChangeKind.Added or ChangeKind.Changed))
		{
			if (markdown.Length > budget)
			{
				markdown.AppendLine("_The rest is cut to keep this summary small. The Files changed tab of the pull request has every file under `source/`._");
				break;
			}

			if (change.NewText is null)
			{
				continue;
			}

			List<string> lines = UnifiedDiff(change.OldText ?? "", change.NewText);
			int added = lines.Count(line => line.StartsWith('+'));
			int removed = lines.Count(line => line.StartsWith('-'));

			markdown.AppendLine($"<details><summary><code>{change.Path}</code> +{added} -{removed}</summary>");
			markdown.AppendLine();
			markdown.AppendLine("```diff");

			foreach (string line in lines.Take(MaxLinesPerFile))
			{
				markdown.AppendLine(line.Replace("```", "` ` `"));
			}

			if (lines.Count > MaxLinesPerFile)
			{
				markdown.AppendLine($"... {lines.Count - MaxLinesPerFile} more lines");
			}

			markdown.AppendLine("```");
			markdown.AppendLine();
			markdown.AppendLine("</details>");
			markdown.AppendLine();
		}

		return markdown.ToString();
	}

	public static List<string> UnifiedDiff(string oldText, string newText)
	{
		string[] before = Lines(oldText);
		string[] after = Lines(newText);

		if (before.Length == 0)
		{
			return after.Select(line => "+" + line).ToList();
		}

		if ((long)before.Length * after.Length > MaxDiffCells)
		{
			return [.. before.Select(line => "-" + line), .. after.Select(line => "+" + line)];
		}

		int[,] common = new int[before.Length + 1, after.Length + 1];

		for (int i = before.Length - 1; i >= 0; i--)
		{
			for (int j = after.Length - 1; j >= 0; j--)
			{
				common[i, j] = before[i] == after[j] ? common[i + 1, j + 1] + 1 : Math.Max(common[i + 1, j], common[i, j + 1]);
			}
		}

		List<string> script = [];
		int x = 0;
		int y = 0;

		while (x < before.Length || y < after.Length)
		{
			if (x < before.Length && y < after.Length && before[x] == after[y])
			{
				script.Add(" " + before[x]);
				x++;
				y++;
			}
			else if (x < before.Length && (y == after.Length || common[x + 1, y] >= common[x, y + 1]))
			{
				script.Add("-" + before[x]);
				x++;
			}
			else
			{
				script.Add("+" + after[y]);
				y++;
			}
		}

		return Hunks(script, 3);
	}

	static List<string> Hunks(List<string> script, int context)
	{
		List<string> result = [];
		bool[] keep = new bool[script.Count];

		for (int index = 0; index < script.Count; index++)
		{
			if (script[index][0] == ' ')
			{
				continue;
			}

			for (int near = Math.Max(0, index - context); near <= Math.Min(script.Count - 1, index + context); near++)
			{
				keep[near] = true;
			}
		}

		bool skipping = false;

		for (int index = 0; index < script.Count; index++)
		{
			if (keep[index])
			{
				result.Add(script[index]);
				skipping = false;
			}
			else if (!skipping)
			{
				result.Add("@@");
				skipping = true;
			}
		}

		return result;
	}

	static string[] Lines(string text)
	{
		return text.Length == 0 ? [] : text.Replace("\r\n", "\n").TrimEnd('\n').Split('\n');
	}

	static string? Text(string path, byte[]? data)
	{
		if (data is null || !TextExtensions.Any(extension => path.EndsWith(extension, StringComparison.OrdinalIgnoreCase)) && Path.HasExtension(path))
		{
			return null;
		}

		if (data.Take(8000).Any(value => value == 0))
		{
			return null;
		}

		return Encoding.UTF8.GetString(data);
	}

	static string Size(FileChange change)
	{
		static string Format(long bytes) => bytes < 1024 ? $"{bytes} B" : $"{bytes / 1024.0:0.0} KB";

		return change.Kind switch
		{
			ChangeKind.Added => Format(change.NewSize),
			ChangeKind.Removed => Format(change.OldSize),
			_ => $"{Format(change.OldSize)} → {Format(change.NewSize)}",
		};
	}
}
