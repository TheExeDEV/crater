using System.Collections.Concurrent;
using Crater.Util;

namespace Crater.Registry;

public sealed class HttpRegistry : IRegistry
{
	readonly string baseUrl;
	readonly ConcurrentDictionary<string, Lazy<Task<RegistryIndex?>>> indexes = new();
	readonly Lazy<Task<WallyRegistry?>> wally;

	public HttpRegistry(string baseUrl)
	{
		this.baseUrl = baseUrl;
		wally = new Lazy<Task<WallyRegistry?>>(() => WallyRegistry.DetectAsync(baseUrl));
	}

	public string Location => baseUrl;

	public bool IsWally => KnownWally() is not null;

	WallyRegistry? KnownWally()
	{
		return wally.IsValueCreated && wally.Value.IsCompletedSuccessfully ? wally.Value.Result : null;
	}

	public Task<RegistryIndex?> GetIndexAsync(string fullName)
	{
		return indexes.GetOrAdd(fullName, name => new Lazy<Task<RegistryIndex?>>(() => LoadIndexAsync(name))).Value;
	}

	async Task<RegistryIndex?> LoadIndexAsync(string fullName)
	{
		if (KnownWally() is WallyRegistry known)
		{
			return await known.GetIndexAsync(fullName);
		}

		string url = $"{baseUrl}/{fullName}/{RegistryIndex.FileName}";
		string? text = await Http.GetTextOrNullAsync(url);

		if (text is not null)
		{
			return Json.Parse<RegistryIndex>(text, url).Normalize(url);
		}

		return await wally.Value is WallyRegistry detected ? await detected.GetIndexAsync(fullName) : null;
	}

	public Task<byte[]> DownloadAsync(string fullName, string version)
	{
		if (KnownWally() is WallyRegistry known)
		{
			return known.DownloadAsync(fullName, version);
		}

		return Http.GetBytesAsync($"{baseUrl}/{fullName}/{version}.zip");
	}

	public async Task<Catalog?> GetCatalogAsync(string query)
	{
		string url = $"{baseUrl}/{Catalog.FileName}";
		string? text = await Http.GetTextOrNullAsync(url);

		if (text is not null)
		{
			return Json.Parse<Catalog>(text, url);
		}

		Catalog? site = await GetSiteCatalogAsync();

		if (site is not null)
		{
			return site;
		}

		return await wally.Value is WallyRegistry detected ? await detected.GetCatalogAsync(query) : null;
	}

	async Task<Catalog?> GetSiteCatalogAsync()
	{
		string infoUrl = $"{baseUrl}/{RegistryInfo.FileName}";
		string? infoText = await Http.GetTextOrNullAsync(infoUrl);

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

	public Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		throw new CraterException($"Run {Fmt.Cmd("crater publish")} to send packages to a GitHub registry.");
	}
}
