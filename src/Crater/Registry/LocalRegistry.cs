using Crater.Util;
using Crater.Versioning;

namespace Crater.Registry;

public sealed class LocalRegistry(string root) : IRegistry
{
	readonly Dictionary<string, RegistryIndex?> indexes = [];

	public const string SourceFolder = "source";

	public string Location => root;

	public bool IsGitRepository => Directory.Exists(Path.Combine(root, ".git"));

	public string PackageDirectory(string fullName)
	{
		(string scope, string name) = Registries.Split(fullName);

		return Path.Combine(root, scope, name);
	}

	public Task<RegistryIndex?> GetIndexAsync(string fullName)
	{
		if (indexes.TryGetValue(fullName, out RegistryIndex? cached))
		{
			return Task.FromResult(cached);
		}

		string path = Path.Combine(PackageDirectory(fullName), RegistryIndex.FileName);
		RegistryIndex? index = File.Exists(path) ? Json.ReadFile<RegistryIndex>(path).Normalize(path) : null;

		indexes[fullName] = index;

		return Task.FromResult(index);
	}

	public async Task<byte[]> DownloadAsync(string fullName, string version)
	{
		string path = Path.Combine(PackageDirectory(fullName), version + ".zip");

		if (!File.Exists(path))
		{
			throw new CraterException($"Archive {Fmt.File(path)} is missing from registry {Fmt.File(root)}");
		}

		return await File.ReadAllBytesAsync(path);
	}

	public Task<Catalog?> GetCatalogAsync(string query)
	{
		string path = Path.Combine(root, Catalog.FileName);
		Catalog catalog = File.Exists(path) ? Json.ReadFile<Catalog>(path) : Catalog.Build(root);

		return Task.FromResult<Catalog?>(catalog);
	}

	public async Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		string fullName = release.Manifest.Name;
		string directory = PackageDirectory(fullName);
		string indexPath = Path.Combine(directory, RegistryIndex.FileName);

		RegistryIndex index = File.Exists(indexPath)
			? Json.ReadFile<RegistryIndex>(indexPath)
			: new RegistryIndex { Name = fullName };

		if (index.Versions.Any(existing => existing.Version == release.Version))
		{
			throw new CraterException(
				$"{Fmt.Pkg($"{fullName}@{release.Version}")} is already published. Bump the version in crater.json.");
		}

		Directory.CreateDirectory(directory);
		await File.WriteAllBytesAsync(Path.Combine(directory, release.Version + ".zip"), archive);

		index.Versions.Add(release);
		index.Versions = index.Versions.OrderBy(VersionOf).ToList();

		Json.WriteText(indexPath, Json.Serialize(index));
		indexes.Remove(fullName);

		string source = Path.Combine(directory, SourceFolder);

		if (Directory.Exists(source))
		{
			Directory.Delete(source, recursive: true);
		}

		Crater.Core.Packer.Extract(archive, source, $"{fullName}@{release.Version}");

		if (!IsGitRepository)
		{
			WriteCatalog();
		}
	}

	public void WriteCatalog()
	{
		Json.WriteText(Path.Combine(root, Catalog.FileName), Json.Serialize(Catalog.Build(root)));
	}

	static SemVer VersionOf(RegistryRelease release)
	{
		return SemVer.TryParse(release.Version, out SemVer version) ? version : new SemVer(0, 0, 0);
	}
}
