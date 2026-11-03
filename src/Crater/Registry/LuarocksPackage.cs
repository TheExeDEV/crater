using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;

namespace Crater.Registry;

public sealed partial record LuarocksPackage(byte[] Archive, List<string> Missing, bool UsesLua51Modules)
{
	static readonly string[] KeptFiles = ["license", "licence", "copying", "readme", "changelog", "changes"];
	static readonly string[] Libraries = ["debug", "string", "table", "math", "os", "coroutine", "utf8", "bit32"];

	[GeneratedRegex(@"\brequire\s*(?:\(\s*(?<quote>[""'])(?<name>[A-Za-z0-9_.\-]+)\k<quote>\s*\)|(?<bare>[""'])(?<barename>[A-Za-z0-9_.\-]+)\k<bare>)")]
	private static partial Regex RequireCall();

	[GeneratedRegex(@"^\s*module\s*\(", RegexOptions.Multiline)]
	private static partial Regex ModuleCall();

	public static LuarocksPackage Build(Dictionary<string, byte[]> files, Dictionary<string, string> modules, Dictionary<string, (string Dependency, string Path)> dependencyModules)
	{
		Dictionary<string, string> own = LuarocksRegistry.Layout(modules.Keys);
		SortedSet<string> missing = new(StringComparer.Ordinal);
		bool usesModule = false;
		using MemoryStream stream = new();

		using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach ((string module, string source) in modules.OrderBy(pair => pair.Key, StringComparer.Ordinal))
			{
				if (!files.TryGetValue(source, out byte[]? data))
				{
					continue;
				}

				string text = Encoding.UTF8.GetString(data);
				string target = own[module];

				usesModule |= ModuleCall().IsMatch(text);
				Add(archive, target, Rewrite(text, target, own, dependencyModules, missing));
			}

			foreach ((string path, byte[] data) in files.Where(pair => !pair.Key.Contains('/') && KeptFiles.Any(name => pair.Key.StartsWith(name, StringComparison.OrdinalIgnoreCase))))
			{
				Add(archive, path, data);
			}
		}

		return new LuarocksPackage(stream.ToArray(), [.. missing], usesModule);
	}

	public static string Rewrite(string text, string from, Dictionary<string, string> own, Dictionary<string, (string Dependency, string Path)> dependencyModules, ISet<string> missing)
	{
		return RequireCall().Replace(text, match =>
		{
			string name = match.Groups["name"].Success ? match.Groups["name"].Value : match.Groups["barename"].Value;

			if (Libraries.Contains(name) && !own.ContainsKey(name))
			{
				return $"({name})";
			}

			if (own.TryGetValue(name, out string? path))
			{
				return $"require({Climb(from, 0)}{Index(Segments(path))})";
			}

			if (dependencyModules.TryGetValue(name, out (string Dependency, string Path) target))
			{
				return $"require({Climb(from, 1)}{Index([target.Dependency, .. Segments(target.Path)])})";
			}

			missing.Add(name);

			return match.Value;
		});
	}

	static string Climb(string from, int extra)
	{
		return "script" + string.Concat(Enumerable.Repeat(".Parent", Segments(from).Count + extra));
	}

	static List<string> Segments(string path)
	{
		List<string> segments = path[..^".lua".Length].Split('/').ToList();

		if (segments.Count > 1 && segments[^1] == "init")
		{
			segments.RemoveAt(segments.Count - 1);
		}

		return segments;
	}

	static string Index(IEnumerable<string> segments)
	{
		return string.Concat(segments.Select(segment => $"[\"{segment.Replace("\\", "\\\\").Replace("\"", "\\\"")}\"]"));
	}

	static void Add(ZipArchive archive, string path, string text) => Add(archive, path, Encoding.UTF8.GetBytes(text));

	static void Add(ZipArchive archive, string path, byte[] data)
	{
		ZipArchiveEntry entry = archive.CreateEntry(path);

		entry.LastWriteTime = new DateTimeOffset(2000, 1, 1, 0, 0, 0, TimeSpan.Zero);

		using Stream output = entry.Open();

		output.Write(data);
	}
}
