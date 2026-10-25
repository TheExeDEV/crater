using System.Globalization;
using System.Text.Json.Serialization;
using System.Xml.Linq;
using Crater.Registry;
using Crater.Util;

namespace Crater.Site;

public sealed class RecentRelease
{
	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("version")] public string Version { get; set; } = "";
	[JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
	[JsonPropertyName("description")] public string Description { get; set; } = "";
	[JsonPropertyName("notes")] public string? Notes { get; set; }
	[JsonPropertyName("first")] public bool First { get; set; }
	[JsonPropertyName("icon")] public string? Icon { get; set; }
}

public static class SiteFeed
{
	public const string RecentFile = "recent.json";
	public const string FeedFile = "feed.xml";
	public const int Limit = 50;

	public static List<RecentRelease> Recent(string registryRoot, IReadOnlyDictionary<string, string> icons)
	{
		List<RecentRelease> releases = [];

		foreach (string indexPath in Catalog.FindIndexFiles(registryRoot))
		{
			RegistryIndex index = Json.ReadFile<RegistryIndex>(indexPath).Normalize(indexPath);
			RegistryRelease? first = index.Versions.Where(release => release.PublishedAt is not null).MinBy(release => release.PublishedAt);

			foreach (RegistryRelease release in index.Versions.Where(release => release.PublishedAt is not null))
			{
				releases.Add(new RecentRelease
				{
					Name = index.Name,
					Version = release.Version,
					PublishedAt = release.PublishedAt,
					Description = release.Manifest.Description,
					Notes = release.Notes,
					First = release == first,
					Icon = icons.GetValueOrDefault(index.Name),
				});
			}
		}

		return releases
			.OrderByDescending(release => release.PublishedAt)
			.ThenBy(release => release.Name, StringComparer.Ordinal)
			.Take(Limit)
			.ToList();
	}

	public static string Rss(RegistryInfo info, IEnumerable<RecentRelease> releases)
	{
		string site = info.SiteUrl.Length > 0 ? info.SiteUrl.TrimEnd('/') + "/" : "";

		XElement channel = new("channel",
			new XElement("title", $"{info.Name}: new packages"),
			new XElement("link", site),
			new XElement("description", info.Description.Length > 0 ? info.Description : $"New packages and versions in {info.Name}"),
			new XElement("language", "en"));

		foreach (RecentRelease release in releases)
		{
			string link = $"{site}#/pkg/{release.Name}/{release.Version}";
			string kind = release.First ? "New package" : "New version";
			string body = string.Join("\n\n", new[] { release.Description, release.Notes ?? "" }.Where(text => text.Length > 0));

			channel.Add(new XElement("item",
				new XElement("title", $"{release.Name} {release.Version}"),
				new XElement("link", link),
				new XElement("guid", new XAttribute("isPermaLink", "false"), $"{release.Name}@{release.Version}"),
				new XElement("category", kind),
				new XElement("pubDate", release.PublishedAt!.Value.ToUniversalTime().ToString("r", CultureInfo.InvariantCulture)),
				new XElement("description", body.Length > 0 ? body : kind)));
		}

		XDocument document = new(new XDeclaration("1.0", "utf-8", null), new XElement("rss", new XAttribute("version", "2.0"), channel));

		return document.Declaration + "\n" + document.Root;
	}
}
