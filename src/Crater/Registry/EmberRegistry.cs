using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Registry;

public sealed class EmberRegistry(string indexUrl) : IRegistry
{
	public const string DefaultIndex = "https://github.com/ember-luau/index";
	public const string RawIndex = "https://raw.githubusercontent.com/ember-luau/index/main";

	public static readonly string[] ManifestFiles = ["ember.toml", "lpm.toml"];

	static readonly string[] Targets = ["roblox", "shared", "roblox_server", "server"];
	static readonly string[] ServerTargets = ["roblox_server", "server"];
	static readonly string[] SkippedFiles = ["owners.toml", "config.toml"];

	readonly ConcurrentDictionary<string, Lazy<Task<RegistryIndex?>>> indexes = new();
	readonly ConcurrentDictionary<string, string> downloads = new();

	public string Location => indexUrl;

	public static bool IsEmber(string url)
	{
		return url.TrimEnd('/').Equals(RawIndex, StringComparison.OrdinalIgnoreCase);
	}

	public static string? MainFromManifest(string text, string source)
	{
		try
		{
			TomlTable target = Toml.Parse(text, source).Table("target") ?? [];

			return target.String("main") ?? target.String("lib");
		}
		catch (CraterException)
		{
			return null;
		}
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
		Dictionary<string, (string Target, TomlTable Entry)> chosen = [];

		foreach ((string key, object value) in file)
		{
			int space = key.LastIndexOf(' ');

			if (value is not TomlTable entry || space < 0 || !SemVer.TryParse(key[..space], out SemVer version))
			{
				continue;
			}

			string target = key[(space + 1)..];

			if (!Targets.Contains(target) && entry.String("type") != "roblox")
			{
				continue;
			}

			string normalized = version.ToString();
			bool better = !chosen.TryGetValue(normalized, out var existing) || Rank(target) < Rank(existing.Target);

			if (better)
			{
				chosen[normalized] = (target, entry);
			}
		}

		foreach ((string version, (string target, TomlTable entry)) in chosen)
		{
			if (entry.String("download") is string download)
			{
				downloads[$"{fullName}@{version}"] = download;
			}

			index.Versions.Add(ToRelease(fullName, version, target, entry));
		}

		return index.Normalize(url);
	}

	static int Rank(string target)
	{
		int position = Array.IndexOf(Targets, target);

		return position < 0 ? Targets.Length : position;
	}

	static RegistryRelease ToRelease(string fullName, string version, string target, TomlTable entry)
	{
		List<string> dependencies = Dependencies(entry.Table("dependencies"));
		bool server = ServerTargets.Contains(target);

		return new RegistryRelease
		{
			Version = version,
			PublishedAt = DateTimeOffset.TryParse(entry.String("published_at"), out DateTimeOffset published) ? published : null,
			Deprecated = entry.Bool("yanked") == true ? "yanked by its author" : entry.String("deprecated"),
			Manifest = new Manifest
			{
				Name = fullName,
				Version = version,
				Description = entry.String("description") ?? "",
				License = entry.String("license") ?? "",
				Authors = entry.Strings("authors"),
				Repository = entry.String("repository") ?? "",
				ImportTo = server ? "server" : "shared",
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
			if (value is not TomlTable spec || spec.String("name") is not string name)
			{
				continue;
			}

			string? registry = RegistryPrefix(spec.String("index"));

			if (registry is null)
			{
				continue;
			}

			string lower = name.ToLowerInvariant();
			string shortName = lower[(lower.IndexOf('/') + 1)..];
			string prefix = alias == shortName || alias.Contains('/') ? "" : alias + ":";

			specs.Add($"{prefix}{registry}{lower}{WallyRegistry.Requirement(spec.String("version") ?? "*")}");
		}

		return specs;
	}

	static string? RegistryPrefix(string? index)
	{
		if (string.IsNullOrWhiteSpace(index) || index.Equals("default", StringComparison.OrdinalIgnoreCase))
		{
			return "";
		}

		string value = index.TrimEnd('/');

		if (value.Equals("wally", StringComparison.OrdinalIgnoreCase) || value.Contains("UpliftGames/wally-index", StringComparison.OrdinalIgnoreCase))
		{
			return "wally#";
		}

		if (value.Equals("pesde", StringComparison.OrdinalIgnoreCase) || value.Contains("pesde", StringComparison.OrdinalIgnoreCase))
		{
			return "pesde#";
		}

		if (value.Equals("forest", StringComparison.OrdinalIgnoreCase) || ForestRegistry.IsForest(value))
		{
			return "forest#";
		}

		return value.Contains("ember-luau/index", StringComparison.OrdinalIgnoreCase) ? "" : null;
	}

	public async Task<byte[]> DownloadAsync(string fullName, string version)
	{
		string key = $"{fullName.ToLowerInvariant()}@{version}";

		if (!downloads.TryGetValue(key, out string? url))
		{
			await GetIndexAsync(fullName);

			if (!downloads.TryGetValue(key, out url))
			{
				throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")} has no Roblox build on Ember.");
			}
		}

		if (!url.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
		{
			throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")}: Ember lists an archive that is not on https.");
		}

		byte[] archive = await Http.GetBytesOrNullAsync(url)
			?? throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")} is not available on Ember.");

		try
		{
			return PesdeRegistry.ToZip(archive);
		}
		catch (Exception exception) when (exception is InvalidDataException or FormatException or EndOfStreamException)
		{
			throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")}: Ember sent an archive crater cannot read ({exception.Message})");
		}
	}

	public async Task<Catalog?> GetCatalogAsync(string query)
	{
		return await ListAsync(query, 40);
	}

	public async Task<Catalog?> ListAsync(string query, int limit)
	{
		JsonElement? tree = await GitHubApi.GetAsync("repos/ember-luau/index/git/trees/main?recursive=1");

		if (tree is null || !tree.Value.TryGetProperty("tree", out JsonElement entries))
		{
			return null;
		}

		string needle = query.Trim().ToLowerInvariant();
		List<string> names = entries.EnumerateArray()
			.Where(entry => entry.GetProperty("type").GetString() == "blob")
			.Select(entry => entry.GetProperty("path").GetString()!)
			.Where(path => path.Count(character => character == '/') == 1 && !path.StartsWith('.') && !SkippedFiles.Contains(path[(path.IndexOf('/') + 1)..]))
			.Where(path => needle.Length == 0 || path.Contains(needle, StringComparison.OrdinalIgnoreCase))
			.Take(limit)
			.ToList();

		RegistryIndex?[] loaded = await Task.WhenAll(names.Select(GetIndexAsync));

		List<CatalogEntry> packages = loaded
			.OfType<RegistryIndex>()
			.Where(index => index.Latest is not null)
			.Select(index => CatalogEntry.From(index, index.Latest!))
			.ToList();

		return new Catalog { Packages = packages, GeneratedAt = DateTimeOffset.UtcNow };
	}

	public Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		throw new CraterException("crater cannot publish to Ember. Publish there with embr publish.");
	}
}
