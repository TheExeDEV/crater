using Crater.Core;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class AddPicker
{
	const int MaxResults = 25;
	const int MaxVersions = 20;

	sealed record Source(string Label, string? Name, IRegistry Registry, bool InProject);

	public static async Task<DependencySpec?> PickAsync(Project project, string? query)
	{
		List<Source> sources = Sources(project);

		while (true)
		{
			string? text = string.IsNullOrWhiteSpace(query) ? Select.Ask("Search packages:") : query;

			query = null;

			if (string.IsNullOrWhiteSpace(text))
			{
				return null;
			}

			List<(Source Source, CatalogEntry Entry)> results;

			using (new Spinner($"Searching {string.Join(", ", sources.Select(source => source.Label))}"))
			{
				results = await SearchAsync(sources, text);
			}

			if (results.Count == 0)
			{
				Out.Info($"Nothing found for {Fmt.Val(text)}. Try another word.");
				continue;
			}

			List<Select.Item> items = results
				.Select(result => new Select.Item(
					$"[p]{result.Entry.Name}[/] [v]{result.Entry.Latest}[/] {Fmt.Cmd(result.Source.Label)}",
					false,
					Truncate(result.Entry.Description, 50)))
				.ToList();

			int? picked = Select.One($"Packages matching {Fmt.Val(text)}", items);

			if (picked is null)
			{
				continue;
			}

			(Source source, CatalogEntry entry) = results[picked.Value];
			string? version = await PickVersionAsync(source, entry.Name);

			if (version is null)
			{
				continue;
			}

			string prefix = await PrefixAsync(sources, source, entry.Name);

			return DependencySpec.Parse($"{prefix}{entry.Name}^{version}");
		}
	}

	public static async Task<DependencySpec> ChooseRegistryAsync(Project project, DependencySpec spec, bool interactive)
	{
		if (spec.RegistryName is not null || spec.Source is not null)
		{
			return spec;
		}

		List<Source> members = Sources(project).Where(source => source.InProject).ToList();

		if (members.Count < 2)
		{
			return spec;
		}

		List<(Source Source, RegistryIndex Index)> found = [];

		using (new Spinner($"Looking up {spec.FullName}"))
		{
			foreach (Source member in members)
			{
				if (await TryIndexAsync(member.Registry, spec.FullName) is RegistryIndex index && index.Latest is not null)
				{
					found.Add((member, index));
				}
			}
		}

		if (found.Count < 2)
		{
			return spec;
		}

		List<(Source Source, RegistryIndex Index)> others = found.Skip(1).ToList();

		if (!interactive)
		{
			string named = others.FirstOrDefault(item => item.Source.Name is not null).Source?.Name ?? "";
			string hint = named.Length > 0 ? $" Write {Fmt.Cmd($"{named}#{spec.FullName}")} to take it from {named}." : "";

			Out.Info($"{Fmt.Pkg(spec.FullName)} is also in {string.Join(", ", others.Select(item => item.Source.Label))}; using {found[0].Source.Label}.{hint}");
			return spec;
		}

		List<Select.Item> items = found
			.Select(item => new Select.Item(
				$"{Fmt.Cmd(item.Source.Label)} [v]{item.Index.Latest!.Version}[/]",
				false,
				Truncate(item.Index.Latest.Manifest.Description, 50)))
			.ToList();

		int? picked = Select.One($"{Fmt.Pkg(spec.FullName)} is in several registries, which one?", items);

		if (picked is null)
		{
			throw new CraterException("Cancelled.");
		}

		if (picked.Value == 0)
		{
			return spec;
		}

		Source chosen = found[picked.Value].Source;

		if (chosen.Name is null)
		{
			throw new CraterException(
				$"{chosen.Label} has no name to point at. Give it one in {Fmt.File("crater.json")}: {Fmt.Cmd($"\"registries\": {{ \"mine\": \"{chosen.Label}\" }}")}, then add {Fmt.Cmd($"mine#{spec.FullName}")}.");
		}

		return DependencySpec.Parse($"{(spec.Alias is null ? "" : spec.Alias + ":")}{chosen.Name}#{spec.FullName}{spec.Req}");
	}

	static List<Source> Sources(Project project)
	{
		List<Source> sources = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		foreach (string location in project.RegistryLocations)
		{
			string? name = NamedRegistries.NameOf(location, project.Manifest);

			if (seen.Add(Registries.Expand(location).TrimEnd('/')))
			{
				sources.Add(new Source(name ?? ProjectCommands.ShortRegistry(location), name, Registries.Open(location, project.Directory), true));
			}
		}

		foreach (string name in NamedRegistries.BuiltinNames)
		{
			string location = NamedRegistries.Location(name, null);

			if (seen.Add(Registries.Expand(location).TrimEnd('/')))
			{
				sources.Add(new Source(name, name, Registries.Open(location, project.Directory), false));
			}
		}

		return sources;
	}

	static async Task<List<(Source Source, CatalogEntry Entry)>> SearchAsync(List<Source> sources, string query)
	{
		Task<List<CatalogEntry>>[] searches = sources.Select(async source =>
		{
			try
			{
				Catalog? catalog = await source.Registry.GetCatalogAsync(query);

				if (catalog is null)
				{
					return [];
				}

				List<CatalogEntry> matched = catalog.Search(query);
				bool searchedByServer = source.Registry is HttpRegistry { IsForeign: true };

				return matched.Count > 0 || !searchedByServer ? matched : catalog.Packages;
			}
			catch (CraterException)
			{
				return [];
			}
		}).ToArray();

		List<CatalogEntry>[] found = await Task.WhenAll(searches);
		List<(Source, CatalogEntry)> results = [];
		int share = Math.Max(5, MaxResults / Math.Max(1, found.Count(list => list.Count > 0)));

		for (int index = 0; index < sources.Count; index++)
		{
			results.AddRange(found[index].Take(share).Select(entry => (sources[index], entry)));
		}

		return results.Take(MaxResults).ToList();
	}

	static async Task<string?> PickVersionAsync(Source source, string name)
	{
		RegistryIndex? index = await TryIndexAsync(source.Registry, name);

		if (index?.Latest is not RegistryRelease latest)
		{
			Out.Warn($"{Fmt.Pkg(name)} has no versions in {source.Label}.");
			return null;
		}

		List<RegistryRelease> versions = index.Versions.OrderByDescending(release => release.SemVer).Take(MaxVersions).ToList();
		List<Select.Item> items = versions
			.Select(release => new Select.Item(
				release.Deprecated is null ? $"[v]{release.Version}[/]" : $"{release.Version} {Fmt.Bad("deprecated")}",
				false,
				release == latest ? "latest" : release.PublishedAt?.ToString("yyyy-MM-dd")))
			.ToList();

		int? picked = Select.One($"Version of {Fmt.Pkg(name)}", items, versions.IndexOf(latest));

		return picked is null ? null : versions[picked.Value].Version;
	}

	static async Task<string> PrefixAsync(List<Source> sources, Source chosen, string name)
	{
		foreach (Source member in sources.Where(source => source.InProject))
		{
			if (await TryIndexAsync(member.Registry, name) is not null)
			{
				if (member == chosen)
				{
					return "";
				}

				break;
			}
		}

		if (chosen.Name is null)
		{
			Out.Warn($"{chosen.Label} has no name, so {Fmt.Pkg(name)} is added without one and comes from the first registry that has it.");
			return "";
		}

		return chosen.Name + "#";
	}

	static async Task<RegistryIndex?> TryIndexAsync(IRegistry registry, string name)
	{
		try
		{
			return await registry.GetIndexAsync(name);
		}
		catch (CraterException)
		{
			return null;
		}
	}

	static string? Truncate(string text, int length)
	{
		if (string.IsNullOrWhiteSpace(text))
		{
			return null;
		}

		string line = text.ReplaceLineEndings(" ");

		return line.Length <= length ? $"[f]{line}[/]" : $"[f]{line[..(length - 1)]}…[/]";
	}
}
