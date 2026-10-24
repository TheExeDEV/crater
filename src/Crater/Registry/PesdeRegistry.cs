using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Registry;

public sealed class PesdeRegistry(string indexUrl, string apiUrl, string downloadTemplate) : IRegistry
{
	public const string ConfigFile = "config.toml";

	static readonly string[] Targets = ["roblox", "roblox_server"];
	static readonly string[] OwnIndexes = ["https://github.com/pesde-pkg/index", "https://github.com/daimond113/pesde-index"];
	static readonly Dictionary<string, string> ArchiveHeaders = new() { ["Accept"] = "application/octet-stream" };

	readonly ConcurrentDictionary<string, Lazy<Task<RegistryIndex?>>> indexes = new();
	readonly ConcurrentDictionary<string, (string Version, string Target)> releases = new();

	public string Location => indexUrl;

	public static async Task<PesdeRegistry?> DetectAsync(string indexUrl)
	{
		byte[]? data = await Http.GetCachedAsync($"{indexUrl}/{ConfigFile}");

		if (data is null)
		{
			return null;
		}

		TomlTable config;

		try
		{
			config = Toml.Parse(Encoding.UTF8.GetString(data), $"{indexUrl}/{ConfigFile}");
		}
		catch (CraterException)
		{
			return null;
		}

		string? api = config.String("api")?.TrimEnd('/');

		if (string.IsNullOrWhiteSpace(api))
		{
			return null;
		}

		string download = config.String("download") ?? "{API_URL}/v1/packages/{PACKAGE}/{PACKAGE_VERSION}/{PACKAGE_TARGET}/archive";

		return new PesdeRegistry(indexUrl, api, download.Replace("{API_URL}", api));
	}

	public Task<RegistryIndex?> GetIndexAsync(string fullName)
	{
		return indexes.GetOrAdd(fullName.ToLowerInvariant(), name => new Lazy<Task<RegistryIndex?>>(() => LoadIndexAsync(name))).Value;
	}

	async Task<RegistryIndex?> LoadIndexAsync(string fullName)
	{
		string url = $"{indexUrl}/{fullName}";
		byte[]? data = await Http.GetCachedAsync(url);

		return data is null ? null : ParseIndex(fullName, Encoding.UTF8.GetString(data), url);
	}

	public RegistryIndex ParseIndex(string fullName, string content, string url)
	{
		TomlTable file = Toml.Parse(content, url);
		RegistryIndex index = new() { Name = fullName };
		string? deprecated = file.Table("meta")?.String("deprecated");

		if (!string.IsNullOrWhiteSpace(deprecated))
		{
			index.Deprecated = deprecated;
		}

		Dictionary<string, (SemVer Version, string Text, string Target, TomlTable Entry)> chosen = [];

		foreach ((string key, object value) in file)
		{
			int space = key.LastIndexOf(' ');

			if (value is not TomlTable entry || space < 0)
			{
				continue;
			}

			string text = key[..space];
			string target = key[(space + 1)..];

			if (!Targets.Contains(target) || !SemVer.TryParse(text, out SemVer version))
			{
				continue;
			}

			string normalized = version.ToString();
			bool better = !chosen.TryGetValue(normalized, out var existing) ||
				Array.IndexOf(Targets, target) < Array.IndexOf(Targets, existing.Target) ||
				(target == existing.Target && string.CompareOrdinal(Published(entry), Published(existing.Entry)) > 0);

			if (better)
			{
				chosen[normalized] = (version, text, target, entry);
			}
		}

		foreach ((string normalized, (SemVer _, string text, string target, TomlTable entry)) in chosen)
		{
			releases[$"{fullName}@{normalized}"] = (text, target);
			releases[$"{fullName}@{text}"] = (text, target);
			index.Versions.Add(ToRelease(fullName, normalized, target, entry));
		}

		return index.Normalize(url);
	}

	static string Published(TomlTable entry) => entry.String("published_at") ?? "";

	RegistryRelease ToRelease(string fullName, string version, string target, TomlTable entry)
	{
		List<string> dependencies = Dependencies(entry.Table("dependencies"));
		bool server = target == "roblox_server";

		return new RegistryRelease
		{
			Version = version,
			PublishedAt = DateTimeOffset.TryParse(Published(entry), out DateTimeOffset published) ? published : null,
			Deprecated = entry.Bool("yanked") == true ? "yanked by its author" : null,
			Manifest = new Manifest
			{
				Name = fullName,
				Version = version,
				Description = entry.String("description") ?? "",
				License = entry.String("license") ?? "",
				Authors = entry.Strings("authors"),
				Repository = entry.String("repository") ?? "",
				ImportTo = server ? "server" : "shared",
				PathToMain = entry.Table("target")?.String("lib") ?? "",
				ImportClient = server ? [] : dependencies,
				ImportServer = dependencies,
			},
		};
	}

	static List<string> Dependencies(TomlTable? table)
	{
		List<string> specs = [];

		foreach ((string alias, object value) in table ?? [])
		{
			if (value is not List<object> { Count: 2 } pair || pair[0] is not TomlTable spec || pair[1] is not string kind || kind == "dev")
			{
				continue;
			}

			string? converted = Convert(alias, spec);

			if (converted is not null)
			{
				specs.Add(converted);
			}
		}

		return specs;
	}

	static string? Convert(string alias, TomlTable spec)
	{
		string version = WallyRegistry.Requirement(spec.String("version") ?? "*");
		string index = (spec.String("index") ?? "").TrimEnd('/');

		if (spec.String("wally") is string wally)
		{
			string wallyName = wally.Contains(':') ? wally[(wally.IndexOf(':') + 1)..] : wally;

			return Spec(alias, "wally#", wallyName.ToLowerInvariant(), version);
		}

		if (spec.String("name") is string name && (index.Length == 0 || OwnIndexes.Contains(index, StringComparer.OrdinalIgnoreCase)))
		{
			return Spec(alias, "", name.ToLowerInvariant(), version);
		}

		return null;
	}

	static string Spec(string alias, string registry, string name, string version)
	{
		string shortName = name[(name.IndexOf('/') + 1)..];
		string prefix = alias == shortName ? "" : alias + ":";

		return $"{prefix}{registry}{name}{version}";
	}

	public async Task<byte[]> DownloadAsync(string fullName, string version)
	{
		if (!releases.TryGetValue($"{fullName.ToLowerInvariant()}@{version}", out (string Version, string Target) release))
		{
			await GetIndexAsync(fullName);

			if (!releases.TryGetValue($"{fullName.ToLowerInvariant()}@{version}", out release))
			{
				throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")} has no Roblox build on pesde.");
			}
		}

		string url = downloadTemplate
			.Replace("{PACKAGE}", Uri.EscapeDataString(fullName.ToLowerInvariant()))
			.Replace("{PACKAGE_VERSION}", Uri.EscapeDataString(release.Version))
			.Replace("{PACKAGE_TARGET}", Uri.EscapeDataString(release.Target));

		byte[] archive = await Http.GetBytesOrNullAsync(url, null, ArchiveHeaders)
			?? throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")} is not available on pesde.");

		try
		{
			return ToZip(archive);
		}
		catch (Exception exception) when (exception is InvalidDataException or FormatException or EndOfStreamException)
		{
			throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")}: pesde sent an archive crater cannot read ({exception.Message})");
		}
	}

	public static byte[] ToZip(byte[] tarball)
	{
		using MemoryStream output = new();
		bool gzipped = tarball.Length > 2 && tarball[0] == 0x1f && tarball[1] == 0x8b;
		Stream input = gzipped ? new GZipStream(new MemoryStream(tarball), CompressionMode.Decompress) : new MemoryStream(tarball);

		using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
		using (input)
		using (TarReader tar = new(input))
		{
			while (tar.GetNextEntry() is TarEntry entry)
			{
				string path = entry.Name.Replace('\\', '/').TrimStart('.', '/');

				if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null || path.Length == 0 || path.Split('/').Contains(".."))
				{
					continue;
				}

				using Stream target = zip.CreateEntry(path, CompressionLevel.Fastest).Open();

				entry.DataStream.CopyTo(target);
			}
		}

		return output.ToArray();
	}

	public async Task<Catalog?> GetCatalogAsync(string query)
	{
		string url = $"{apiUrl}/v1/search?query={Uri.EscapeDataString(query)}";
		string text = await Http.GetTextOrNullAsync(url) ?? throw new CraterException("pesde search is not available right now.");
		SearchResult result = JsonSerializer.Deserialize<SearchResult>(text) ?? new SearchResult();

		List<CatalogEntry> entries = result.Data
			.Where(item => item.Targets.Any(target => Targets.Contains(target.Kind)))
			.Select(item => new CatalogEntry
			{
				Name = item.Name,
				Description = item.Description ?? "",
				Latest = SemVer.TryParse(item.Version, out SemVer parsed) ? parsed.ToString() : item.Version,
				License = item.License ?? "",
				Authors = item.Authors ?? [],
				Repository = item.Repository ?? "",
			})
			.ToList();

		return new Catalog { Packages = entries };
	}

	public Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		throw new CraterException("crater cannot publish to pesde. Publish there with pesde publish.");
	}

	sealed class SearchResult
	{
		[JsonPropertyName("data")] public List<SearchItem> Data { get; set; } = [];
	}

	sealed class SearchItem
	{
		[JsonPropertyName("name")] public string Name { get; set; } = "";
		[JsonPropertyName("version")] public string Version { get; set; } = "";
		[JsonPropertyName("description")] public string? Description { get; set; }
		[JsonPropertyName("license")] public string? License { get; set; }
		[JsonPropertyName("authors")] public List<string>? Authors { get; set; }
		[JsonPropertyName("repository")] public string? Repository { get; set; }
		[JsonPropertyName("targets")] public List<SearchTarget> Targets { get; set; } = [];
	}

	sealed class SearchTarget
	{
		[JsonPropertyName("kind")] public string Kind { get; set; } = "";
	}
}
