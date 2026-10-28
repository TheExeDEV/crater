using System.Text.Json;
using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Registry;
using Crater.Tools;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class SearchCommand
{
	static readonly string[] Kinds = ["package", "interface", "tool"];
	static readonly string[] Sorts = ["relevance", "name", "updated"];
	const int Shown = 50;

	public static async Task SearchAsync(Args args)
	{
		List<string> words = [.. args.Positional];
		string? kind = args.Value("type")?.ToLowerInvariant();
		string sort = args.Value("sort")?.ToLowerInvariant() ?? "relevance";

		if (kind is null && words.Count > 0 && Kinds.Contains(words[^1].ToLowerInvariant()))
		{
			kind = words[^1].ToLowerInvariant();
			words.RemoveAt(words.Count - 1);
		}

		if (kind is not null && !Kinds.Contains(kind))
		{
			throw new CraterException($"Unknown type {Fmt.Val(kind)}. Use {Fmt.Val("package")}, {Fmt.Val("interface")} or {Fmt.Val("tool")}.");
		}

		if (!Sorts.Contains(sort))
		{
			throw new CraterException($"Unknown sort {Fmt.Val(sort)}. Use {Fmt.Val("relevance")}, {Fmt.Val("name")} or {Fmt.Val("updated")}.");
		}

		string query = string.Join(' ', words);
		List<string> tags = CatalogEntry.ParseTags(args.Value("tag", "tags"));
		string? implements = args.Value("implements")?.Trim().ToLowerInvariant();

		if (kind == "tool")
		{
			if (tags.Count > 0 || implements is not null)
			{
				throw new CraterException($"Tools have no tags or interfaces. Use {Fmt.Cmd("--tag")} and {Fmt.Cmd("--implements")} with packages.");
			}

			await SearchToolsAsync(query);
			return;
		}

		if (implements is not null && !DependencySpec.IsValidPackageName(implements))
		{
			throw new CraterException($"{Fmt.Val(implements)} is not an interface name. Expected author/name.");
		}

		List<CatalogEntry> found = (await CatalogAsync(args, query))
			.Where(entry => kind is null || (kind == "interface") == (entry.Interface == true))
			.ToList();
		List<CatalogEntry> results = Sort(found
			.Where(entry => entry.HasTags(tags) && (implements is null || entry.Implements(implements)))
			.ToList(), sort);

		if (Out.Quiet)
		{
			Out.WriteJson(JsonSerializer.SerializeToNode(results, Json.Options));
			return;
		}

		if (results.Count == 0)
		{
			Out.Info($"No {(kind is null ? "packages" : kind + "s")} match {Description(query, tags, implements)}.");

			if (tags.Count > 0 && found.Count > 0 && found.All(entry => entry.Keywords.Count == 0))
			{
				Out.Dim("  this registry does not list package tags");
			}

			return;
		}

		PrintPackages(results);
	}

	public static async Task TagsAsync(Args args)
	{
		string query = string.Join(' ', args.Positional).Trim().ToLowerInvariant();
		List<(string Tag, int Count)> tags = (await CatalogAsync(args, ""))
			.SelectMany(entry => entry.Keywords.Select(keyword => keyword.Trim().ToLowerInvariant()).Distinct())
			.Where(tag => tag.Length > 0 && tag.Contains(query))
			.GroupBy(tag => tag)
			.Select(group => (group.Key, group.Count()))
			.OrderByDescending(pair => pair.Item2)
			.ThenBy(pair => pair.Key, StringComparer.Ordinal)
			.ToList();

		if (Out.Quiet)
		{
			Out.WriteJson(new JsonArray(tags.Select(pair => (JsonNode)new JsonObject { ["tag"] = pair.Tag, ["packages"] = pair.Count }).ToArray()));
			return;
		}

		if (tags.Count == 0)
		{
			Out.Info(query.Length == 0 ? "No packages in this registry have tags." : $"No tags match {Fmt.Val(query)}.");
			return;
		}

		Table.Print(["Tag", "Packages"], tags.Take(Shown).Select(pair => (IReadOnlyList<string>)[Fmt.Val(pair.Tag), pair.Count.ToString()]).ToList());

		if (tags.Count > Shown)
		{
			Out.Dim($"  and {tags.Count - Shown} more, add a word to narrow them down");
		}

		Out.Info("");
		Out.Info($"Find packages with a tag: {Fmt.Cmd($"crater search --tag {tags[0].Tag}")}");
	}

	public static async Task FindInterfaceAsync(Args args)
	{
		string name = (args.First ?? throw new CraterException($"Usage: {Fmt.Cmd("crater find-interface <author/name>")}")).ToLowerInvariant();

		if (!DependencySpec.IsValidPackageName(name))
		{
			throw new CraterException($"{Fmt.Val(name)} is not an interface name. Expected author/name.");
		}

		List<CatalogEntry> catalog = await CatalogAsync(args, "");
		CatalogEntry? declared = catalog.FirstOrDefault(entry => entry.Name == name);
		List<CatalogEntry> implementations = catalog
			.Where(entry => entry.Implements(name))
			.ToList();

		if (declared is null)
		{
			Out.Warn($"{Fmt.Pkg(name)} is not in this registry.");
		}
		else if (declared.Interface != true)
		{
			Out.Warn($"{Fmt.Pkg(name)} is a package, not an interface.");
		}

		if (implementations.Count == 0)
		{
			Out.Info($"No packages implement {Fmt.Pkg(name)} yet.");
			return;
		}

		Out.Heading($"{Fmt.Plural(implementations.Count, "package implements", "packages implement")} {name}");
		PrintPackages(implementations);
	}

	static List<CatalogEntry> Sort(List<CatalogEntry> entries, string sort)
	{
		return sort switch
		{
			"name" => entries.OrderBy(entry => entry.Name, StringComparer.Ordinal).ToList(),
			"updated" => entries.OrderByDescending(entry => entry.UpdatedAt ?? DateTimeOffset.MinValue).ThenBy(entry => entry.Name, StringComparer.Ordinal).ToList(),
			_ => entries,
		};
	}

	static string Description(string query, List<string> tags, string? implements)
	{
		List<string> parts = [];

		if (query.Length > 0)
		{
			parts.Add(Fmt.Val(query));
		}

		if (tags.Count > 0)
		{
			parts.Add($"{(tags.Count == 1 ? "tag" : "tags")} {string.Join(", ", tags.Select(tag => Fmt.Val(tag)))}");
		}

		if (implements is not null)
		{
			parts.Add($"implementing {Fmt.Pkg(implements)}");
		}

		return parts.Count == 0 ? Fmt.Val("") : string.Join(", ", parts);
	}

	static async Task<List<CatalogEntry>> CatalogAsync(Args args, string query)
	{
		IRegistry registry = InspectCommands.OpenRegistry(args);
		Catalog? catalog;

		using (new Spinner("Searching"))
		{
			catalog = await registry.GetCatalogAsync(query);
		}

		if (catalog is null)
		{
			throw new CraterException(
				$"Registry {Fmt.File(registry.Location)} has no {Fmt.File(Catalog.FileName)}. " +
				$"Its maintainer can create it with {Fmt.Cmd("crater registry catalog")}.");
		}

		return catalog.Search(query);
	}

	static async Task SearchToolsAsync(string query)
	{
		ToolCatalog tools;

		using (new Spinner("Searching tools"))
		{
			tools = await ToolCatalog.LoadAsync();
		}

		List<KeyValuePair<string, ToolEntry>> found = tools.Tools
			.Where(pair => query.Length == 0
				|| pair.Key.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| pair.Value.Repository.Contains(query, StringComparison.OrdinalIgnoreCase)
				|| (pair.Value.Description ?? "").Contains(query, StringComparison.OrdinalIgnoreCase))
			.OrderBy(pair => pair.Key, StringComparer.Ordinal)
			.ToList();

		if (Out.Quiet)
		{
			Out.WriteJson(new JsonArray(found.Select(pair => (JsonNode)new JsonObject
			{
				["name"] = pair.Key,
				["repository"] = pair.Value.Repository,
				["description"] = pair.Value.Description ?? "",
			}).ToArray()));
			return;
		}

		if (found.Count == 0)
		{
			Out.Info($"No tools match {Fmt.Val(query)}. Any GitHub release works: {Fmt.Cmd("crater tool add owner/repo")}.");
			return;
		}

		Table.Print(["Tool", "Repository", "Description"], found.Select(pair => (IReadOnlyList<string>)
		[
			Fmt.Pkg(pair.Key),
			pair.Value.Repository,
			InspectCommands.Truncate(pair.Value.Description ?? "", 60),
		]).ToList());
	}

	static void PrintPackages(List<CatalogEntry> results)
	{
		bool withTags = results.Any(entry => entry.Keywords.Count > 0);
		List<string> headers = withTags ? ["Package", "Latest", "Type", "Realm", "Tags", "Description"] : ["Package", "Latest", "Type", "Realm", "Description"];
		List<IReadOnlyList<string>> rows = results.Take(Shown).Select(entry =>
		{
			List<string> row = [Fmt.Pkg(entry.Name), Fmt.Val(entry.Latest), Kind(entry), entry.ImportTo];

			if (withTags)
			{
				row.Add(InspectCommands.Truncate(string.Join(", ", entry.Keywords.Select(keyword => keyword.ToLowerInvariant())), 28));
			}

			row.Add(InspectCommands.Truncate(entry.Description, withTags ? 48 : 60));

			return (IReadOnlyList<string>)row;
		}).ToList();

		Table.Print(headers, rows);

		if (results.Count > Shown)
		{
			Out.Dim($"  and {results.Count - Shown} more, refine the query");
		}
	}

	static string Kind(CatalogEntry entry)
	{
		if (entry.Interface == true)
		{
			return "interface";
		}

		return entry.Monorepository == true ? "monorepository" : "package";
	}
}
