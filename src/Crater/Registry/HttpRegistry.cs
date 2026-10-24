using System.Collections.Concurrent;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Crater.Util;

namespace Crater.Registry;

public sealed partial class HttpRegistry : IRegistry
{
	enum Access
	{
		Public,
		Private,
	}

	readonly string baseUrl;
	readonly GitHubRepository? github;
	readonly ConcurrentDictionary<string, Lazy<Task<RegistryIndex?>>> indexes = new();
	readonly Lazy<Task<WallyRegistry?>> wally;
	readonly Lazy<Task<PesdeRegistry?>> pesde;
	readonly Lazy<Task<Access>> access;

	[GeneratedRegex(@"^[^./][^/]*/[^/]+/index\.json$")]
	private static partial Regex IndexPath();

	public HttpRegistry(string baseUrl)
	{
		this.baseUrl = baseUrl;
		github = GitHubRepository.FromRegistry(baseUrl);
		wally = new Lazy<Task<WallyRegistry?>>(() => WallyRegistry.DetectAsync(baseUrl));
		pesde = new Lazy<Task<PesdeRegistry?>>(() => PesdeRegistry.DetectAsync(baseUrl));
		access = new Lazy<Task<Access>>(ProbeAccessAsync);
	}

	public string Location => baseUrl;

	public bool IsWally => KnownWally() is not null;

	public bool IsForeign => IsWally || KnownPesde() is not null;

	public bool IsPrivate => access.IsValueCreated && access.Value.IsCompletedSuccessfully && access.Value.Result == Access.Private;

	WallyRegistry? KnownWally()
	{
		return wally.IsValueCreated && wally.Value.IsCompletedSuccessfully ? wally.Value.Result : null;
	}

	PesdeRegistry? KnownPesde()
	{
		return pesde.IsValueCreated && pesde.Value.IsCompletedSuccessfully ? pesde.Value.Result : null;
	}

	async Task<IRegistry?> ForeignAsync()
	{
		if (IsPrivate)
		{
			return null;
		}

		return (IRegistry?)await wally.Value ?? await pesde.Value;
	}

	public Task<RegistryIndex?> GetIndexAsync(string fullName)
	{
		return indexes.GetOrAdd(fullName, name => new Lazy<Task<RegistryIndex?>>(() => LoadIndexAsync(name))).Value;
	}

	async Task<RegistryIndex?> LoadIndexAsync(string fullName)
	{
		if (((IRegistry?)KnownWally() ?? KnownPesde()) is IRegistry known)
		{
			return await known.GetIndexAsync(fullName);
		}

		string url = $"{baseUrl}/{fullName}/{RegistryIndex.FileName}";
		string? text = await TextAsync(url);

		if (text is not null)
		{
			return Json.Parse<RegistryIndex>(text, url).Normalize(url);
		}

		return await ForeignAsync() is IRegistry detected ? await detected.GetIndexAsync(fullName) : null;
	}

	public async Task<byte[]> DownloadAsync(string fullName, string version)
	{
		if (((IRegistry?)KnownWally() ?? KnownPesde()) is IRegistry known)
		{
			return await known.DownloadAsync(fullName, version);
		}

		string url = $"{baseUrl}/{fullName}/{version}.zip";

		return await BytesAsync(url) ?? throw new CraterException($"{Fmt.File(url)}: HTTP 404 Not Found");
	}

	public async Task<Catalog?> GetCatalogAsync(string query)
	{
		string? text = await TextAsync($"{baseUrl}/{Catalog.FileName}");

		if (text is not null)
		{
			return Json.Parse<Catalog>(text, $"{baseUrl}/{Catalog.FileName}");
		}

		if (!IsPrivate && await GetSiteCatalogAsync() is Catalog site)
		{
			return site;
		}

		if (github is not null && await GetTreeCatalogAsync(github) is Catalog tree)
		{
			return tree;
		}

		return await ForeignAsync() is IRegistry detected ? await detected.GetCatalogAsync(query) : null;
	}

	async Task<Catalog?> GetSiteCatalogAsync()
	{
		string infoUrl = $"{baseUrl}/{RegistryInfo.FileName}";
		string? infoText = await TextAsync(infoUrl);

		if (infoText is null)
		{
			return null;
		}

		RegistryInfo info = Json.Parse<RegistryInfo>(infoText, infoUrl);

		if (string.IsNullOrWhiteSpace(info.SiteUrl))
		{
			return null;
		}

		string catalogUrl = $"{info.SiteUrl.TrimEnd('/')}/data/{Catalog.FileName}";
		string? catalogText = await Http.GetTextOrNullAsync(catalogUrl);

		return catalogText is null ? null : Json.Parse<Catalog>(catalogText, catalogUrl);
	}

	async Task<Catalog?> GetTreeCatalogAsync(GitHubRepository repository)
	{
		JsonElement? tree = await GitHubApi.GetAsync($"repos/{repository.Slug}/git/trees/{repository.Branch}?recursive=1");

		if (tree is null || !tree.Value.TryGetProperty("tree", out JsonElement entries))
		{
			return null;
		}

		List<string> paths = entries.EnumerateArray()
			.Select(entry => entry.GetProperty("path").GetString()!)
			.Where(path => IndexPath().IsMatch(path))
			.ToList();

		if (paths.Count == 0)
		{
			return null;
		}

		RegistryIndex?[] loaded = await Task.WhenAll(paths.Select(async path =>
		{
			string url = $"{baseUrl}/{path}";
			string? text = await TextAsync(url);

			return text is null ? null : Json.Parse<RegistryIndex>(text, url).Normalize(url);
		}));

		List<CatalogEntry> packages = loaded
			.OfType<RegistryIndex>()
			.Where(index => index.Latest is not null)
			.Select(index => CatalogEntry.From(index, index.Latest!))
			.ToList();

		return new Catalog { Packages = packages, GeneratedAt = DateTimeOffset.UtcNow };
	}

	public Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		throw new CraterException($"Run {Fmt.Cmd("crater publish")} to send packages to a GitHub registry.");
	}

	async Task<string?> TextAsync(string url)
	{
		byte[]? data = await BytesAsync(url, cached: true);

		return data is null ? null : Encoding.UTF8.GetString(data);
	}

	async Task<byte[]?> BytesAsync(string url, bool cached = false)
	{
		Task<byte[]?> Fetch(IReadOnlyDictionary<string, string>? headers)
		{
			return cached ? Http.GetCachedAsync(url, headers) : Http.GetBytesOrNullAsync(url, null, headers);
		}

		if (IsPrivate)
		{
			return await Fetch(GitHubAuth.Headers());
		}

		byte[]? data = await Fetch(OwnHeaders());

		if (data is not null || github is null)
		{
			return data;
		}

		return await access.Value == Access.Private ? await Fetch(GitHubAuth.Headers()) : null;
	}

	static Dictionary<string, string>? OwnHeaders()
	{
		string? token = Environment.GetEnvironmentVariable("CRATER_REGISTRY_TOKEN");

		return string.IsNullOrWhiteSpace(token) ? null : new Dictionary<string, string> { ["Authorization"] = $"Bearer {token.Trim()}" };
	}

	async Task<Access> ProbeAccessAsync()
	{
		string[] markers = [RegistryInfo.FileName, WallyRegistry.ConfigFile, PesdeRegistry.ConfigFile];

		foreach (string marker in markers)
		{
			if (await Http.GetCachedAsync($"{baseUrl}/{marker}") is not null)
			{
				return Access.Public;
			}
		}

		string slug = github!.Slug;

		if (GitHubAuth.Token() is null)
		{
			throw new CraterException(
				$"The registry {Fmt.Val(slug)} is private or does not exist. If it is private, run {Fmt.Cmd("crater login")} with a GitHub account that has access to it.");
		}

		foreach (string marker in markers)
		{
			if (await Http.GetCachedAsync($"{baseUrl}/{marker}", GitHubAuth.Headers()) is not null)
			{
				return Access.Private;
			}
		}

		if (GitHubAuth.Source() == "crater login" && Credentials.Load() is { CanReadPrivate: false })
		{
			throw new CraterException(
				$"The registry {Fmt.Val(slug)} is private, and your crater sign-in covers only public repositories. Run {Fmt.Cmd("crater login --private")} and try again.");
		}

		string who = GitHubAuth.Login() is string login ? $"Your GitHub account {Fmt.Val(login)}" : "Your GitHub token";

		throw new CraterException(
			$"{who} has no access to the registry {Fmt.Val(slug)}, or it does not exist. Ask its owner to add you on GitHub " +
			$"(Settings, Collaborators) and run the command again.");
	}
}
