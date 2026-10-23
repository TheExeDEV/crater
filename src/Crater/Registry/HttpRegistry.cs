using System.Net;
using System.Net.Http.Headers;
using Crater.Util;

namespace Crater.Registry;

public sealed class HttpRegistry(string baseUrl) : IRegistry
{
	static readonly HttpClient Http = CreateClient();

	readonly Dictionary<string, RegistryIndex?> indexes = [];

	public string Location => baseUrl;

	public static HttpClient Client => Http;

	static HttpClient CreateClient()
	{
		HttpClient client = new() { Timeout = TimeSpan.FromSeconds(60) };
		client.DefaultRequestHeaders.UserAgent.ParseAdd($"crater/{BuildInfo.Version}");

		string? token = Environment.GetEnvironmentVariable("CRATER_REGISTRY_TOKEN");

		if (!string.IsNullOrWhiteSpace(token))
		{
			client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", token);
		}

		return client;
	}

	public async Task<RegistryIndex?> GetIndexAsync(string fullName)
	{
		if (indexes.TryGetValue(fullName, out RegistryIndex? cached))
		{
			return cached;
		}

		string url = $"{baseUrl}/{fullName}/{RegistryIndex.FileName}";
		string? text = await GetTextOrNullAsync(url);
		RegistryIndex? index = text is null ? null : Json.Parse<RegistryIndex>(text, url).Normalize(url);

		indexes[fullName] = index;

		return index;
	}

	public async Task<byte[]> DownloadAsync(string fullName, string version)
	{
		string url = $"{baseUrl}/{fullName}/{version}.zip";

		using HttpResponseMessage response = await SendAsync(url);
		EnsureSuccess(response, url);

		return await response.Content.ReadAsByteArrayAsync();
	}

	public async Task<Catalog?> GetCatalogAsync()
	{
		string url = $"{baseUrl}/{Catalog.FileName}";
		string? text = await GetTextOrNullAsync(url);

		if (text is not null)
		{
			return Json.Parse<Catalog>(text, url);
		}

		return await GetSiteCatalogAsync();
	}

	async Task<Catalog?> GetSiteCatalogAsync()
	{
		string infoUrl = $"{baseUrl}/{RegistryInfo.FileName}";
		string? infoText = await GetTextOrNullAsync(infoUrl);

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
		string? catalogText = await GetTextOrNullAsync(catalogUrl);

		return catalogText is null ? null : Json.Parse<Catalog>(catalogText, catalogUrl);
	}

	public Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		throw new CraterException(
			$"Publishing over HTTP is not supported. Clone the registry repository and run " +
			$"{Fmt.Cmd("crater publish --registry <clone> --pr")} to open a pull request.");
	}

	public static async Task<string?> GetTextOrNullAsync(string url)
	{
		using HttpResponseMessage response = await SendAsync(url);

		if (response.StatusCode == HttpStatusCode.NotFound)
		{
			return null;
		}

		EnsureSuccess(response, url);

		return await response.Content.ReadAsStringAsync();
	}

	public static async Task<HttpResponseMessage> SendAsync(string url)
	{
		const int attempts = 3;

		for (int attempt = 1; ; attempt++)
		{
			try
			{
				HttpResponseMessage response = await Http.GetAsync(url);
				bool retryable = (int)response.StatusCode is 502 or 503 or 504;

				if (!retryable || attempt == attempts)
				{
					return response;
				}

				response.Dispose();
			}
			catch (Exception exception) when (exception is HttpRequestException or TaskCanceledException)
			{
				if (attempt == attempts)
				{
					throw new CraterException($"Request to {Fmt.File(url)} failed after {attempts} attempts: {exception.Message}");
				}
			}

			await Task.Delay(TimeSpan.FromMilliseconds(500 * attempt * attempt));
		}
	}

	static void EnsureSuccess(HttpResponseMessage response, string url)
	{
		if (!response.IsSuccessStatusCode)
		{
			throw new CraterException($"{Fmt.File(url)}: HTTP {(int)response.StatusCode} {response.ReasonPhrase}");
		}
	}
}
