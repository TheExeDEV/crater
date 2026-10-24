using System.Collections.Concurrent;
using Crater.Util;

namespace Crater.Registry;

public sealed class RegistryChain(IReadOnlyList<IRegistry> members) : IRegistry
{
	readonly ConcurrentDictionary<string, Lazy<Task<(IRegistry Registry, RegistryIndex Index)?>>> found = new();

	public IReadOnlyList<IRegistry> Members => members;

	public string Location => string.Join(", ", members.Select(member => member.Location));

	public async Task<RegistryIndex?> GetIndexAsync(string fullName)
	{
		return (await FindAsync(fullName))?.Index;
	}

	public async Task<IRegistry?> OwnerOfAsync(string fullName)
	{
		return (await FindAsync(fullName))?.Registry;
	}

	Task<(IRegistry Registry, RegistryIndex Index)?> FindAsync(string fullName)
	{
		return found.GetOrAdd(fullName, name => new Lazy<Task<(IRegistry, RegistryIndex)?>>(() => SearchAsync(name))).Value;
	}

	async Task<(IRegistry Registry, RegistryIndex Index)?> SearchAsync(string fullName)
	{
		foreach (IRegistry member in members)
		{
			if (await member.GetIndexAsync(fullName) is RegistryIndex index)
			{
				return (member, index);
			}
		}

		return null;
	}

	public async Task<byte[]> DownloadAsync(string fullName, string version)
	{
		IRegistry owner = await OwnerOfAsync(fullName)
			?? throw new CraterException($"{Fmt.Pkg(fullName)} is not in any of the registries {Fmt.File(Location)}");

		return await owner.DownloadAsync(fullName, version);
	}

	public async Task<Catalog?> GetCatalogAsync(string query)
	{
		List<CatalogEntry> packages = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);
		CraterException? firstError = null;
		bool any = false;

		foreach (IRegistry member in members)
		{
			Catalog? catalog;

			try
			{
				catalog = await member.GetCatalogAsync(query);
			}
			catch (CraterException exception)
			{
				firstError ??= exception;
				continue;
			}

			if (catalog is null)
			{
				continue;
			}

			any = true;

			foreach (CatalogEntry entry in catalog.Packages.Where(entry => seen.Add(entry.Name)))
			{
				packages.Add(entry);
			}
		}

		if (!any && firstError is not null)
		{
			throw firstError;
		}

		return any ? new Catalog { Packages = packages, GeneratedAt = DateTimeOffset.UtcNow } : null;
	}

	public Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		return members[0].PublishAsync(release, archive);
	}
}
