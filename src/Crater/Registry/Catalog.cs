using System.Text.Json.Serialization;
using Crater.Util;

namespace Crater.Registry;

public sealed class Catalog
{
	public const string FileName = "packages.json";

	[JsonPropertyName("generated_at")] public DateTimeOffset GeneratedAt { get; set; }
	[JsonPropertyName("packages")] public List<CatalogEntry> Packages { get; set; } = [];

	public static Catalog Build(string registryRoot)
	{
		List<CatalogEntry> entries = [];

		foreach (string indexPath in FindIndexFiles(registryRoot))
		{
			RegistryIndex index = Json.ReadFile<RegistryIndex>(indexPath).Normalize(indexPath);
			RegistryRelease? latest = index.Latest;

			if (latest is null)
			{
				continue;
			}

			entries.Add(CatalogEntry.From(index, latest));
		}

		DateTimeOffset newest = entries
			.Select(entry => entry.UpdatedAt ?? DateTimeOffset.UnixEpoch)
			.DefaultIfEmpty(DateTimeOffset.UnixEpoch)
			.Max();

		return new Catalog
		{
			GeneratedAt = newest,
			Packages = entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList(),
		};
	}

	public static IEnumerable<string> FindIndexFiles(string registryRoot)
	{
		if (!Directory.Exists(registryRoot))
		{
			yield break;
		}

		foreach (string scopeDirectory in Directory.EnumerateDirectories(registryRoot).OrderBy(path => path, StringComparer.Ordinal))
		{
			if (Path.GetFileName(scopeDirectory).StartsWith('.'))
			{
				continue;
			}

			foreach (string packageDirectory in Directory.EnumerateDirectories(scopeDirectory).OrderBy(path => path, StringComparer.Ordinal))
			{
				string indexPath = Path.Combine(packageDirectory, RegistryIndex.FileName);

				if (File.Exists(indexPath))
				{
					yield return indexPath;
				}
			}
		}
	}

	public List<CatalogEntry> Search(string query)
	{
		string needle = query.Trim().ToLowerInvariant();

		if (needle.Length == 0)
		{
			return Packages.ToList();
		}

		return Packages
			.Select(entry => (Entry: entry, Score: entry.Score(needle)))
			.Where(pair => pair.Score > 0)
			.OrderByDescending(pair => pair.Score)
			.ThenBy(pair => pair.Entry.Name, StringComparer.Ordinal)
			.Select(pair => pair.Entry)
			.ToList();
	}
}

public sealed class CatalogEntry
{
	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("description")] public string Description { get; set; } = "";
	[JsonPropertyName("latest")] public string Latest { get; set; } = "";
	[JsonPropertyName("versions")] public int Versions { get; set; }
	[JsonPropertyName("license")] public string License { get; set; } = "";
	[JsonPropertyName("authors")] public List<string> Authors { get; set; } = [];
	[JsonPropertyName("keywords")] public List<string> Keywords { get; set; } = [];
	[JsonPropertyName("import_to")] public string ImportTo { get; set; } = "shared";
	[JsonPropertyName("repository")] public string Repository { get; set; } = "";
	[JsonPropertyName("updated_at")] public DateTimeOffset? UpdatedAt { get; set; }

	public static CatalogEntry From(RegistryIndex index, RegistryRelease latest)
	{
		return new CatalogEntry
		{
			Name = index.Name,
			Description = latest.Manifest.Description,
			Latest = latest.Version,
			Versions = index.Versions.Count,
			License = latest.Manifest.License,
			Authors = latest.Manifest.Authors,
			Keywords = latest.Manifest.Keywords ?? [],
			ImportTo = latest.Manifest.ImportTo,
			Repository = latest.Manifest.Repository,
			UpdatedAt = index.Versions.Max(release => release.PublishedAt),
		};
	}

	public int Score(string needle)
	{
		string shortName = Name[(Name.IndexOf('/') + 1)..];
		int score = 0;

		if (shortName == needle || Name == needle)
		{
			score += 100;
		}
		else if (shortName.StartsWith(needle))
		{
			score += 60;
		}
		else if (Name.Contains(needle))
		{
			score += 40;
		}

		if (Keywords.Any(keyword => keyword.Equals(needle, StringComparison.OrdinalIgnoreCase)))
		{
			score += 30;
		}

		if (Description.Contains(needle, StringComparison.OrdinalIgnoreCase))
		{
			score += 10;
		}

		return score;
	}
}

public sealed class RegistryInfo
{
	public const string FileName = "registry.json";

	[JsonPropertyName("name")] public string Name { get; set; } = "Crater Registry";
	[JsonPropertyName("description")] public string Description { get; set; } = "";
	[JsonPropertyName("url")] public string Url { get; set; } = "";
	[JsonPropertyName("site_url")] public string SiteUrl { get; set; } = "";
	[JsonPropertyName("repository")] public string Repository { get; set; } = "";
	[JsonPropertyName("maintainers")] public List<string> Maintainers { get; set; } = [];

	public static RegistryInfo Load(string registryRoot)
	{
		string path = Path.Combine(registryRoot, FileName);

		if (!File.Exists(path))
		{
			return new RegistryInfo();
		}

		return Json.ReadFile<RegistryInfo>(path);
	}

	public bool IsMaintainer(string user)
	{
		return Maintainers.Any(maintainer => maintainer.Equals(user, StringComparison.OrdinalIgnoreCase));
	}
}
