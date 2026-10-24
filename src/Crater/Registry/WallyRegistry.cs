using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Registry;

public sealed class WallyRegistry(string indexUrl, string apiUrl) : IRegistry
{
	public const string ConfigFile = "config.json";

	static readonly Dictionary<string, string> ApiHeaders = new() { ["Wally-Version"] = "0.3.2" };

	readonly ConcurrentDictionary<string, Lazy<Task<RegistryIndex?>>> indexes = new();

	public string Location => indexUrl;

	public static async Task<WallyRegistry?> DetectAsync(string indexUrl)
	{
		string? text = await Http.GetTextOrNullAsync($"{indexUrl}/{ConfigFile}");

		if (text is null)
		{
			return null;
		}

		WallyConfig? config = JsonSerializer.Deserialize<WallyConfig>(text);

		return string.IsNullOrWhiteSpace(config?.Api) ? null : new WallyRegistry(indexUrl, config.Api.TrimEnd('/'));
	}

	public Task<RegistryIndex?> GetIndexAsync(string fullName)
	{
		return indexes.GetOrAdd(fullName.ToLowerInvariant(), name => new Lazy<Task<RegistryIndex?>>(() => LoadIndexAsync(name))).Value;
	}

	async Task<RegistryIndex?> LoadIndexAsync(string fullName)
	{
		string url = $"{indexUrl}/{fullName}";
		string? text = await Http.GetTextOrNullAsync(url);

		if (text is null)
		{
			return null;
		}

		RegistryIndex index = new() { Name = fullName };

		foreach (string line in text.Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
		{
			WallyManifest? entry = JsonSerializer.Deserialize<WallyManifest>(line);

			if (entry?.Package is not null)
			{
				index.Versions.Add(new RegistryRelease { Version = entry.Package.Version, Manifest = ToManifest(entry) });
			}
		}

		return index.Normalize(url);
	}

	public async Task<byte[]> DownloadAsync(string fullName, string version)
	{
		string url = $"{apiUrl}/v1/package-contents/{fullName.ToLowerInvariant()}/{version}";

		return await Http.GetBytesOrNullAsync(url, null, ApiHeaders)
			?? throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")} is not available on Wally.");
	}

	public async Task<Catalog?> GetCatalogAsync(string query)
	{
		if (string.IsNullOrWhiteSpace(query))
		{
			throw new CraterException($"Wally can only search by a word. Try {Fmt.Cmd("crater search promise")}.");
		}

		string url = $"{apiUrl}/v1/package-search?query={Uri.EscapeDataString(query)}";
		string text = await Http.GetTextOrNullAsync(url) ?? "[]";
		List<WallySearchResult> results = JsonSerializer.Deserialize<List<WallySearchResult>>(text) ?? [];

		List<CatalogEntry> entries = results.Select(result => new CatalogEntry
		{
			Name = $"{result.Scope}/{result.Name}",
			Description = result.Description ?? "",
			Latest = result.Versions.FirstOrDefault() ?? "",
			Versions = result.Versions.Count,
		}).ToList();

		return new Catalog { Packages = entries };
	}

	public Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		throw new CraterException("crater cannot publish to Wally. Publish there with wally publish.");
	}

	static Manifest ToManifest(WallyManifest entry)
	{
		WallyPackage package = entry.Package!;
		List<string> shared = Specs(entry.Dependencies);
		List<string> server = Specs(entry.ServerDependencies);

		return new Manifest
		{
			Name = package.Name.ToLowerInvariant(),
			Version = package.Version,
			Description = package.Description ?? "",
			License = package.License ?? "",
			Authors = package.Authors ?? [],
			ImportTo = package.Realm == "server" ? "server" : "shared",
			ImportClient = shared,
			ImportServer = [.. shared, .. server],
		};
	}

	static List<string> Specs(Dictionary<string, string>? dependencies)
	{
		List<string> specs = [];

		foreach ((string alias, string value) in dependencies ?? [])
		{
			int at = value.IndexOf('@');

			if (at <= 0)
			{
				continue;
			}

			string name = value[..at].ToLowerInvariant();
			string shortName = name[(name.IndexOf('/') + 1)..];
			string prefix = alias == shortName ? "" : alias + ":";

			specs.Add($"{prefix}{name}{Requirement(value[(at + 1)..])}");
		}

		return specs;
	}

	public static string Requirement(string wally)
	{
		List<string> parts = wally.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries).ToList();

		if (parts.Count == 1 && parts[0] is "*" or "")
		{
			return "^";
		}

		if (parts.Count == 1 && parts[0][0] is '^' or '~' or '=')
		{
			return parts[0][0] + Full(parts[0][1..]);
		}

		string? lower = parts.FirstOrDefault(part => part.StartsWith(">="))?[2..].Trim();
		string? upper = parts.FirstOrDefault(part => part.StartsWith('<') && !part.StartsWith("<="))?[1..].Trim();

		if (lower is null)
		{
			return parts.Count == 1 && SemVer.TryParse(parts[0], out _, out _) ? "^" + Full(parts[0]) : "^";
		}

		if (!SemVer.TryParse(lower, out SemVer from, out _))
		{
			return "^";
		}

		if (upper is not null && SemVer.TryParse(upper, out SemVer to, out _))
		{
			if (to.Equals(CaretUpper(from)))
			{
				return "^" + from;
			}

			if (to.Equals(new SemVer(from.Major, from.Minor + 1, 0)))
			{
				return "~" + from;
			}
		}

		return "+" + from;
	}

	static string Full(string version)
	{
		return SemVer.TryParse(version.Trim(), out SemVer parsed, out _) ? parsed.ToString() : version.Trim();
	}

	static SemVer CaretUpper(SemVer version)
	{
		if (version.Major > 0)
		{
			return new SemVer(version.Major + 1, 0, 0);
		}

		return version.Minor > 0 ? new SemVer(0, version.Minor + 1, 0) : new SemVer(0, 0, version.Patch + 1);
	}

	sealed class WallyConfig
	{
		[JsonPropertyName("api")] public string? Api { get; set; }
	}

	sealed class WallyManifest
	{
		[JsonPropertyName("package")] public WallyPackage? Package { get; set; }
		[JsonPropertyName("dependencies")] public Dictionary<string, string>? Dependencies { get; set; }
		[JsonPropertyName("server-dependencies")] public Dictionary<string, string>? ServerDependencies { get; set; }
	}

	sealed class WallyPackage
	{
		[JsonPropertyName("name")] public string Name { get; set; } = "";
		[JsonPropertyName("version")] public string Version { get; set; } = "";
		[JsonPropertyName("realm")] public string? Realm { get; set; }
		[JsonPropertyName("description")] public string? Description { get; set; }
		[JsonPropertyName("license")] public string? License { get; set; }
		[JsonPropertyName("authors")] public List<string>? Authors { get; set; }
	}

	sealed class WallySearchResult
	{
		[JsonPropertyName("scope")] public string Scope { get; set; } = "";
		[JsonPropertyName("name")] public string Name { get; set; } = "";
		[JsonPropertyName("description")] public string? Description { get; set; }
		[JsonPropertyName("versions")] public List<string> Versions { get; set; } = [];
	}
}
