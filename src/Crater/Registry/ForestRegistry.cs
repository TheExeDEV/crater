using System.Collections.Concurrent;
using System.IO.Compression;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Registry;

public sealed class ForestRegistry(string apiUrl) : IRegistry
{
	public const string DefaultApi = "https://api.forest.dev";
	const string Platform = "roblox";
	const int MaxVersions = 60;
	const int ParallelRequests = 8;

	readonly ConcurrentDictionary<string, Lazy<Task<RegistryIndex?>>> indexes = new();

	public string Location => apiUrl;

	public static bool IsForest(string url)
	{
		return Uri.TryCreate(url, UriKind.Absolute, out Uri? uri) && uri.Host is "api.forest.dev" or "forest.dev" or "www.forest.dev";
	}

	string PackageUrl(string fullName)
	{
		(string scope, string name) = Registries.Split(fullName.ToLowerInvariant());

		return $"{apiUrl}/v1/package/{Uri.EscapeDataString(scope)}/{Platform}/{Uri.EscapeDataString(name)}";
	}

	public Task<RegistryIndex?> GetIndexAsync(string fullName)
	{
		return indexes.GetOrAdd(fullName.ToLowerInvariant(), name => new Lazy<Task<RegistryIndex?>>(() => LoadIndexAsync(name))).Value;
	}

	async Task<RegistryIndex?> LoadIndexAsync(string fullName)
	{
		string url = PackageUrl(fullName);
		string? text = await TextAsync(url);

		if (text is null)
		{
			return null;
		}

		Overview overview = Parse<Overview>(text, url);
		List<string> versions = overview.Versions
			.Select(item => item.Version)
			.Where(version => SemVer.TryParse(version, out _))
			.OrderByDescending(SemVer.Parse)
			.Take(MaxVersions)
			.ToList();

		using SemaphoreSlim slots = new(ParallelRequests);

		RegistryRelease?[] releases = await Task.WhenAll(versions.Select(async version =>
		{
			await slots.WaitAsync();

			try
			{
				return await LoadReleaseAsync(fullName, version);
			}
			finally
			{
				slots.Release();
			}
		}));

		RegistryIndex index = new() { Name = fullName, Versions = releases.OfType<RegistryRelease>().ToList() };

		return index.Normalize(url);
	}

	async Task<RegistryRelease?> LoadReleaseAsync(string fullName, string version)
	{
		string url = $"{PackageUrl(fullName)}/{Uri.EscapeDataString(version)}";
		string? text = await TextAsync(url);

		if (text is null)
		{
			return null;
		}

		VersionInfo info = Parse<VersionInfo>(text, url);
		List<string> dependencies = (info.Dependencies ?? [])
			.Select(pair => Spec(pair.Key, pair.Value))
			.OfType<string>()
			.ToList();

		return new RegistryRelease
		{
			Version = version,
			Deprecated = info.Archived ? "archived by its author on Forest" : null,
			Manifest = new Manifest
			{
				Name = fullName,
				Version = version,
				Description = info.Description ?? "",
				License = info.License ?? "",
				ImportTo = "shared",
				PathToMain = info.ArchiveRoot ?? "",
				ImportClient = dependencies,
				ImportServer = dependencies,
			},
		};
	}

	static string? Spec(string name, Dependency dependency)
	{
		string fullName = name.ToLowerInvariant();

		if (!DependencySpec.IsValidPackageName(fullName))
		{
			return null;
		}

		string shortName = fullName[(fullName.IndexOf('/') + 1)..];
		string alias = string.IsNullOrWhiteSpace(dependency.Alias) || dependency.Alias == shortName ? "" : dependency.Alias + ":";

		return $"{alias}{fullName}{WallyRegistry.Requirement(dependency.Version ?? "*")}";
	}

	public async Task<byte[]> DownloadAsync(string fullName, string version)
	{
		string baseUrl = $"{PackageUrl(fullName)}/{Uri.EscapeDataString(version)}";
		string listing = await TextAsync($"{baseUrl}/files")
			?? throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")} is not available on Forest.");

		List<FileEntry> files = await NeededFilesAsync(fullName, version, Parse<FileList>(listing, baseUrl).Files);
		using SemaphoreSlim slots = new(ParallelRequests);

		(string Path, byte[] Content)[] downloaded = await Task.WhenAll(files.Select(async file =>
		{
			await slots.WaitAsync();

			try
			{
				string url = $"{baseUrl}/file?path={Uri.EscapeDataString(file.Path)}";
				string text = await Http.GetTextOrNullAsync(url)
					?? throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")}: Forest did not send {Fmt.File(file.Path)}.");
				FileContent content = Parse<FileContent>(text, url);
				byte[] data = content.Encoding == "base64" ? Convert.FromBase64String(content.Content ?? "") : Encoding.UTF8.GetBytes(content.Content ?? "");

				return (file.Target, data);
			}
			finally
			{
				slots.Release();
			}
		}));

		using MemoryStream output = new();

		using (ZipArchive zip = new(output, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach ((string path, byte[] content) in downloaded.OrderBy(item => item.Path, StringComparer.Ordinal))
			{
				string normalized = path.Replace('\\', '/').TrimStart('/');

				if (normalized.Length == 0 || normalized.Split('/').Contains(".."))
				{
					continue;
				}

				using Stream entry = zip.CreateEntry(normalized, CompressionLevel.Fastest).Open();

				entry.Write(content);
			}
		}

		return output.ToArray();
	}

	async Task<List<FileEntry>> NeededFilesAsync(string fullName, string version, List<FileEntry> files)
	{
		RegistryIndex? index = await GetIndexAsync(fullName);
		string main = Crater.Core.Links.NormalizeRelative(index?.Versions.FirstOrDefault(release => release.Version == version)?.Manifest.PathToMain ?? "");
		List<string> paths = files.Select(file => file.Path.Replace('\\', '/').TrimStart('/')).ToList();
		string? codeTop = main.Contains('/') ? main.Split('/')[0] : null;
		Func<string, bool> needed = Crater.Core.PackageFiles.NeededFilter(paths, codeTop);

		List<FileEntry> kept = files
			.Where(file =>
			{
				string path = file.Path.Replace('\\', '/').TrimStart('/');

				return needed(path) && Crater.Core.Executables.Classify(path, ReadOnlySpan<byte>.Empty) != Crater.Core.ProgramKind.Binary;
			})
			.ToList();

		foreach (FileEntry file in kept)
		{
			file.Target = file.Path.Replace('\\', '/').TrimStart('/');
		}

		return UnwrapMirror(kept);
	}

	public static List<(string Path, string Target)> UnwrapMirror(List<string> paths)
	{
		List<FileEntry> files = paths.Select(path => new FileEntry { Path = path, Target = path }).ToList();

		return UnwrapMirror(files).Select(file => (file.Path, file.Target)).ToList();
	}

	static List<FileEntry> UnwrapMirror(List<FileEntry> files)
	{
		string? top = files
			.Select(file => file.Target.Split('/'))
			.Where(segments => segments.Length > 2 && segments[0] == segments[1])
			.Select(segments => segments[0])
			.FirstOrDefault();

		if (top is null || !files.Any(file => file.Target is var target && (target == $"{top}/init.lua" || target == $"{top}/init.luau")))
		{
			return files;
		}

		string inner = $"{top}/{top}/";

		return files
			.Where(file => !file.Target.Contains('/') || file.Target.StartsWith(inner, StringComparison.Ordinal))
			.Select(file =>
			{
				file.Target = file.Target.StartsWith(inner, StringComparison.Ordinal) ? file.Target[inner.Length..] : file.Target;
				return file;
			})
			.ToList();
	}

	public async Task<Catalog?> GetCatalogAsync(string query)
	{
		string url = $"{apiUrl}/v1/search/packages?q={Uri.EscapeDataString(query)}&platform={Platform}";
		string text = await Http.GetTextOrNullAsync(url) ?? throw new CraterException("Forest search is not available right now.");

		List<CatalogEntry> entries = Parse<SearchResult>(text, url).Packages
			.Where(item => item.Owner.Length > 0 && item.Name.Length > 0)
			.Select(item => new CatalogEntry
			{
				Name = $"{item.Owner}/{item.Name}".ToLowerInvariant(),
				Description = item.Description ?? "",
				Latest = item.Version,
				License = item.License ?? "",
			})
			.ToList();

		return new Catalog { Packages = entries };
	}

	public Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		throw new CraterException("crater cannot publish to Forest. Publish there with forest publish.");
	}

	static async Task<string?> TextAsync(string url)
	{
		byte[]? data = await Http.GetCachedAsync(url);

		return data is null ? null : Encoding.UTF8.GetString(data);
	}

	static T Parse<T>(string text, string source) where T : new()
	{
		try
		{
			return JsonSerializer.Deserialize<T>(text) ?? new T();
		}
		catch (JsonException exception)
		{
			throw new CraterException($"{Fmt.File(source)}: Forest sent data crater cannot read ({exception.Message})");
		}
	}

	sealed class Overview
	{
		[JsonPropertyName("versions")] public List<VersionItem> Versions { get; set; } = [];
	}

	sealed class VersionItem
	{
		[JsonPropertyName("version")] public string Version { get; set; } = "";
	}

	sealed class VersionInfo
	{
		[JsonPropertyName("description")] public string? Description { get; set; }
		[JsonPropertyName("license")] public string? License { get; set; }
		[JsonPropertyName("archiveRoot")] public string? ArchiveRoot { get; set; }
		[JsonPropertyName("archived")] public bool Archived { get; set; }
		[JsonPropertyName("dependencies")] public Dictionary<string, Dependency>? Dependencies { get; set; }
	}

	sealed class Dependency
	{
		[JsonPropertyName("version")] public string? Version { get; set; }
		[JsonPropertyName("alias")] public string? Alias { get; set; }
	}

	sealed class FileList
	{
		[JsonPropertyName("files")] public List<FileEntry> Files { get; set; } = [];
	}

	sealed class FileEntry
	{
		[JsonPropertyName("path")] public string Path { get; set; } = "";
		[JsonIgnore] public string Target { get; set; } = "";
	}

	sealed class FileContent
	{
		[JsonPropertyName("content")] public string? Content { get; set; }
		[JsonPropertyName("encoding")] public string? Encoding { get; set; }
	}

	sealed class SearchResult
	{
		[JsonPropertyName("packages")] public List<SearchItem> Packages { get; set; } = [];
	}

	sealed class SearchItem
	{
		[JsonPropertyName("name")] public string Name { get; set; } = "";
		[JsonPropertyName("owner")] public string Owner { get; set; } = "";
		[JsonPropertyName("version")] public string Version { get; set; } = "";
		[JsonPropertyName("description")] public string? Description { get; set; }
		[JsonPropertyName("license")] public string? License { get; set; }
	}
}
