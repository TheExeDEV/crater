namespace Crater.Registry;

public interface IRegistry
{
	string Location { get; }

	Task<RegistryIndex?> GetIndexAsync(string fullName);

	Task<byte[]> DownloadAsync(string fullName, string version);

	Task<Catalog?> GetCatalogAsync(string query);

	Task PublishAsync(RegistryRelease release, byte[] archive);
}
