using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Site;

public sealed class PopularEntry
{
	[JsonPropertyName("source")] public string Source { get; set; } = "crater";
	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("description")] public string Description { get; set; } = "";
	[JsonPropertyName("latest")] public string Latest { get; set; } = "";
	[JsonPropertyName("license")] public string? License { get; set; }
	[JsonPropertyName("dependents")] public int? Dependents { get; set; }
	[JsonPropertyName("downloads")] public long? Downloads { get; set; }
	[JsonPropertyName("weekly_downloads")] public long? WeeklyDownloads { get; set; }
	[JsonPropertyName("likes")] public long? Likes { get; set; }
	[JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }
	[JsonPropertyName("icon")] public string? Icon { get; set; }
	[JsonIgnore] public JsonObject? Detail { get; set; }
}

public sealed class PopularPackages
{
	public const string FileName = "popular.json";

	[JsonPropertyName("generated_at")] public DateTimeOffset GeneratedAt { get; set; }
	[JsonPropertyName("packages")] public List<PopularEntry> Packages { get; set; } = [];
}

public static partial class SitePopular
{
	const int Count = 8;
	const string ForestSite = "https://forest.dev/";
	const string WallyApi = "https://api.wally.run/v1";

	static readonly string[] SourceOrder = ["crater", "wally", "pesde", "forest"];

	[GeneratedRegex(@"/p/roblox/([A-Za-z0-9_\-]+)/([A-Za-z0-9_\-]+)")]
	private static partial Regex ForestLink();

	public static Dictionary<string, SortedSet<string>> Dependents(string registryRoot)
	{
		Dictionary<string, SortedSet<string>> dependents = new(StringComparer.Ordinal);

		foreach (string indexPath in Catalog.FindIndexFiles(registryRoot))
		{
			RegistryIndex index = Json.ReadFile<RegistryIndex>(indexPath).Normalize(indexPath);
			Manifest? manifest = index.Latest?.Manifest;

			if (manifest is null)
			{
				continue;
			}

			foreach (string raw in manifest.ImportClient.Concat(manifest.ImportServer).Distinct())
			{
				DependencySpec spec;

				try
				{
					spec = DependencySpec.Parse(raw);
				}
				catch (CraterException)
				{
					continue;
				}

				if (spec.RegistryName is not null || spec.Source is not null || spec.FullName == index.Name)
				{
					continue;
				}

				if (!dependents.TryGetValue(spec.FullName, out SortedSet<string>? set))
				{
					set = new SortedSet<string>(StringComparer.Ordinal);
					dependents[spec.FullName] = set;
				}

				set.Add(index.Name);
			}
		}

		return dependents;
	}

	public static PopularPackages Build(Catalog catalog, Dictionary<string, SortedSet<string>> dependents)
	{
		IEnumerable<PopularEntry> crater = catalog.Packages
			.Where(entry => entry.Deprecated is null && dependents.GetValueOrDefault(entry.Name)?.Count > 0)
			.Select(entry => new PopularEntry
			{
				Source = "crater",
				Name = entry.Name,
				Description = entry.Description,
				Latest = entry.Latest,
				License = entry.License,
				Dependents = dependents[entry.Name].Count,
				UpdatedAt = entry.UpdatedAt,
				Icon = entry.Icon,
			});

		List<PopularEntry> external = ExternalAsync().GetAwaiter().GetResult();

		return new PopularPackages
		{
			GeneratedAt = DateTimeOffset.UtcNow,
			Packages = Combine(crater.Concat(external)),
		};
	}

	public static List<PopularEntry> Combine(IEnumerable<PopularEntry> entries)
	{
		return entries
			.GroupBy(entry => entry.Name.ToLowerInvariant())
			.Select(group => group.OrderBy(entry => Array.IndexOf(SourceOrder, entry.Source)).First())
			.OrderByDescending(entry => entry.Source == "crater" ? 1 : 0)
			.ThenByDescending(entry => entry.Dependents ?? 0)
			.ThenByDescending(entry => entry.WeeklyDownloads ?? entry.Downloads ?? 0)
			.Take(Count)
			.ToList();
	}

	static async Task<List<PopularEntry>> ExternalAsync()
	{
		if (Http.Offline || Environment.GetEnvironmentVariable("CRATER_SITE_NO_EXTERNAL") is { Length: > 0 })
		{
			return [];
		}

		try
		{
			string? html = await Http.GetTextOrNullAsync(ForestSite);

			if (html is null)
			{
				return [];
			}

			List<(string Scope, string Name)> names = ForestLink().Matches(html)
				.Select(match => (match.Groups[1].Value, match.Groups[2].Value))
				.Distinct()
				.Take(Count * 2)
				.ToList();

			PopularEntry?[] entries = await Task.WhenAll(names.Select(item => ExternalEntryAsync(item.Scope, item.Name)));

			return entries.OfType<PopularEntry>().ToList();
		}
		catch (CraterException exception)
		{
			Out.Dim($"  Popular packages from other registries left out of the site: {exception.Message}");
			return [];
		}
	}

	static async Task<PopularEntry?> ExternalEntryAsync(string scope, string name)
	{
		string url = $"{ForestRegistry.DefaultApi}/ai/package/roblox/{Uri.EscapeDataString(scope)}/{Uri.EscapeDataString(name)}";
		string? text = await Http.GetTextOrNullAsync(url);

		if (text is null)
		{
			return null;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			JsonElement root = document.RootElement;

			PopularEntry entry = new()
			{
				Source = "forest",
				Name = $"{scope}/{name}".ToLowerInvariant(),
				Description = String(root, "description") ?? "",
				Latest = String(root, "latest") ?? String(root, "version") ?? "",
				License = String(root, "license"),
				Downloads = Number(root, "downloads"),
				WeeklyDownloads = Number(root, "weeklyDownloads"),
				Likes = Number(root, "likes"),
				Icon = String(root, "imageUrl") is string image && image.StartsWith("https://", StringComparison.Ordinal) ? image : null,
			};

			if (await MirroredFromWallyAsync(scope, name, entry.Latest) && await WallyEntryAsync(entry) is PopularEntry wally)
			{
				return wally;
			}

			entry.Detail = new JsonObject
			{
				["name"] = entry.Name,
				["description"] = entry.Description,
				["latest"] = entry.Latest,
				["license"] = entry.License,
				["downloads"] = entry.Downloads,
				["weekly_downloads"] = entry.WeeklyDownloads,
				["likes"] = entry.Likes,
				["icon"] = entry.Icon,
				["readme"] = String(root, "readme"),
				["versions"] = root.TryGetProperty("versions", out JsonElement versions) ? JsonNode.Parse(versions.GetRawText()) : null,
				["dependencies"] = root.TryGetProperty("dependencies", out JsonElement dependencies) ? JsonNode.Parse(dependencies.GetRawText()) : null,
			};

			return entry;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	static async Task<bool> MirroredFromWallyAsync(string scope, string name, string version)
	{
		string url = $"{ForestRegistry.DefaultApi}/v1/package/{Uri.EscapeDataString(scope)}/roblox/{Uri.EscapeDataString(name)}/{Uri.EscapeDataString(version)}";
		string? text = await Http.GetTextOrNullAsync(url);

		if (text is null)
		{
			return false;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(text);

			return String(document.RootElement, "mirroredFrom") == "wally";
		}
		catch (JsonException)
		{
			return false;
		}
	}

	static async Task<PopularEntry?> WallyEntryAsync(PopularEntry forest)
	{
		string? text = await Http.GetTextOrNullAsync($"{WallyApi}/package-metadata/{forest.Name}");

		if (text is null)
		{
			return null;
		}

		try
		{
			using JsonDocument document = JsonDocument.Parse(text);
			List<JsonElement> packages = document.RootElement.GetProperty("versions").EnumerateArray()
				.Select(version => version.GetProperty("package"))
				.ToList();
			JsonElement? newest = packages
				.Where(package => SemVer.TryParse(String(package, "version"), out _))
				.OrderByDescending(package => SemVer.Parse(String(package, "version")!))
				.Cast<JsonElement?>()
				.FirstOrDefault();

			if (newest is not JsonElement package)
			{
				return null;
			}

			return new PopularEntry
			{
				Source = "wally",
				Name = forest.Name,
				Description = String(package, "description") ?? "",
				Latest = String(package, "version") ?? forest.Latest,
				License = String(package, "license"),
				Downloads = forest.Downloads,
				WeeklyDownloads = forest.WeeklyDownloads,
			};
		}
		catch (Exception exception) when (exception is JsonException or KeyNotFoundException or InvalidOperationException)
		{
			return null;
		}
	}

	static string? String(JsonElement root, string property)
	{
		return root.ValueKind == JsonValueKind.Object && root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
	}

	static long? Number(JsonElement root, string property)
	{
		return root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
	}
}
