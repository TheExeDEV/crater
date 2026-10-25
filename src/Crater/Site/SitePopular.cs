using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Site;

public sealed class PopularEntry
{
	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("description")] public string Description { get; set; } = "";
	[JsonPropertyName("latest")] public string Latest { get; set; } = "";
	[JsonPropertyName("license")] public string? License { get; set; }
	[JsonPropertyName("dependents")] public int? Dependents { get; set; }
	[JsonPropertyName("downloads")] public long? Downloads { get; set; }
	[JsonPropertyName("weekly_downloads")] public long? WeeklyDownloads { get; set; }
	[JsonPropertyName("likes")] public long? Likes { get; set; }
	[JsonPropertyName("url")] public string? Url { get; set; }
	[JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }
}

public sealed class PopularPackages
{
	public const string FileName = "popular.json";

	[JsonPropertyName("generated_at")] public DateTimeOffset GeneratedAt { get; set; }
	[JsonPropertyName("crater")] public List<PopularEntry> Crater { get; set; } = [];
	[JsonPropertyName("forest")] public List<PopularEntry> Forest { get; set; } = [];
}

public static partial class SitePopular
{
	const int Count = 8;
	const string ForestSite = "https://forest.dev/";

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
		List<PopularEntry> crater = catalog.Packages
			.Where(entry => entry.Deprecated is null)
			.OrderByDescending(entry => dependents.GetValueOrDefault(entry.Name)?.Count ?? 0)
			.ThenByDescending(entry => entry.Versions)
			.ThenByDescending(entry => entry.UpdatedAt)
			.Take(Count)
			.Select(entry => new PopularEntry
			{
				Name = entry.Name,
				Description = entry.Description,
				Latest = entry.Latest,
				License = entry.License,
				Dependents = dependents.GetValueOrDefault(entry.Name)?.Count ?? 0,
				UpdatedAt = entry.UpdatedAt,
			})
			.ToList();

		return new PopularPackages
		{
			GeneratedAt = DateTimeOffset.UtcNow,
			Crater = crater,
			Forest = ForestAsync().GetAwaiter().GetResult(),
		};
	}

	static async Task<List<PopularEntry>> ForestAsync()
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
				.Take(Count)
				.ToList();

			PopularEntry?[] entries = await Task.WhenAll(names.Select(item => ForestEntryAsync(item.Scope, item.Name)));

			return entries.OfType<PopularEntry>().ToList();
		}
		catch (CraterException exception)
		{
			Out.Dim($"  Forest packages left out of the site: {exception.Message}");
			return [];
		}
	}

	static async Task<PopularEntry?> ForestEntryAsync(string scope, string name)
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

			return new PopularEntry
			{
				Name = $"{scope}/{name}".ToLowerInvariant(),
				Description = String(root, "description") ?? "",
				Latest = String(root, "latest") ?? String(root, "version") ?? "",
				License = String(root, "license"),
				Downloads = Number(root, "downloads"),
				WeeklyDownloads = Number(root, "weeklyDownloads"),
				Likes = Number(root, "likes"),
				Url = $"{ForestSite}p/roblox/{scope}/{name}",
			};
		}
		catch (JsonException)
		{
			return null;
		}
	}

	static string? String(JsonElement root, string property)
	{
		return root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.String ? value.GetString() : null;
	}

	static long? Number(JsonElement root, string property)
	{
		return root.TryGetProperty(property, out JsonElement value) && value.ValueKind == JsonValueKind.Number ? value.GetInt64() : null;
	}
}
