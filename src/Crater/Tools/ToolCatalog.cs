using System.Text;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Crater.Util;

namespace Crater.Tools;

public sealed class ToolEntry
{
	[JsonPropertyName("repository")] public string Repository { get; set; } = "";
	[JsonPropertyName("description")] public string? Description { get; set; }
	[JsonPropertyName("executable")] public string? Executable { get; set; }
	[JsonPropertyName("assets")] public Dictionary<string, string>? Assets { get; set; }

	public string? AssetPattern(string platform) => Assets?.GetValueOrDefault(platform);
}

public sealed partial class ToolCatalog
{
	public const string FileName = "tools.json";

	[GeneratedRegex(@"^[A-Za-z0-9_.\-]+/[A-Za-z0-9_.\-]+$")]
	private static partial Regex RepositoryRegex();

	static Task<ToolCatalog>? loading;

	[JsonPropertyName("tools")] public Dictionary<string, ToolEntry> Tools { get; set; } = new(StringComparer.OrdinalIgnoreCase);

	public static string Url
	{
		get
		{
			string? custom = Environment.GetEnvironmentVariable("CRATER_TOOLS_URL");

			if (!string.IsNullOrWhiteSpace(custom))
			{
				return custom.Trim();
			}

			string repository = BuildInfo.UpdateRepo.Length > 0 ? BuildInfo.UpdateRepo : "TheExeDEV/crater";

			return $"https://raw.githubusercontent.com/{repository}/main/{FileName}";
		}
	}

	static string CachePath => Path.Combine(CraterPaths.Cache, FileName);

	public static Task<ToolCatalog> LoadAsync()
	{
		return loading ??= FetchAsync();
	}

	static async Task<ToolCatalog> FetchAsync()
	{
		string url = Url;
		string? text;

		if (url.StartsWith("http://", StringComparison.OrdinalIgnoreCase) || url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			byte[]? data;

			try
			{
				data = await Http.GetCachedAsync(url);
			}
			catch (CraterException)
			{
				data = null;
			}

			text = data is null ? null : Encoding.UTF8.GetString(data);
		}
		else
		{
			string path = url.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ? new Uri(url).LocalPath : url;

			text = File.Exists(path) ? await File.ReadAllTextAsync(path) : null;
		}

		if (text is null)
		{
			return Cached() ?? throw new CraterException(
				$"Could not load the list of known tools from {Fmt.File(url)}. Name the GitHub repository instead, for example {Fmt.Cmd("crater tool add rojo-rbx/rojo")}.");
		}

		ToolCatalog catalog = Parse(text, url);

		try
		{
			Directory.CreateDirectory(Path.GetDirectoryName(CachePath)!);
			await File.WriteAllTextAsync(CachePath, text);
		}
		catch (IOException)
		{
		}

		return catalog;
	}

	public static ToolCatalog Parse(string text, string source)
	{
		ToolCatalog catalog = Json.Parse<ToolCatalog>(text, source);

		catalog.Tools = new Dictionary<string, ToolEntry>(catalog.Tools, StringComparer.OrdinalIgnoreCase);

		List<string> invalid = catalog.Tools
			.Where(pair => !RepositoryRegex().IsMatch(pair.Value.Repository ?? ""))
			.Select(pair => pair.Key)
			.ToList();

		if (invalid.Count > 0)
		{
			throw new CraterException($"{Fmt.File(source)}: {string.Join(", ", invalid)} need a {Fmt.Val("repository")} like {Fmt.Val("owner/repo")}.");
		}

		return catalog;
	}

	public static ToolCatalog? Cached()
	{
		try
		{
			return File.Exists(CachePath) ? Parse(File.ReadAllText(CachePath), CachePath) : null;
		}
		catch (CraterException)
		{
			return null;
		}
	}

	public ToolEntry? Find(string name) => Tools.GetValueOrDefault(name);

	public ToolEntry? ForRepository(string slug)
	{
		return Tools.Values.FirstOrDefault(entry => entry.Repository.Equals(slug, StringComparison.OrdinalIgnoreCase));
	}
}
