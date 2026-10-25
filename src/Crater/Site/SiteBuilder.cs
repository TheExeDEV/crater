using System.Text;
using System.Text.Json.Serialization;
using Crater.Core;
using Crater.Registry;
using Crater.Util;

namespace Crater.Site;

public static class SiteBuilder
{
	sealed class PackagePage
	{
		[JsonPropertyName("name")] public string Name { get; set; } = "";
		[JsonPropertyName("latest")] public string Latest { get; set; } = "";
		[JsonPropertyName("deprecated")] public string? Deprecated { get; set; }
		[JsonPropertyName("owners")] public List<string>? Owners { get; set; }
		[JsonPropertyName("versions")] public List<RegistryRelease> Versions { get; set; } = [];
		[JsonPropertyName("readme")] public string? Readme { get; set; }
		[JsonPropertyName("used_by")] public List<string> UsedBy { get; set; } = [];
	}

	static readonly string[] ReadmeNames = ["README.md", "readme.md", "Readme.md", "README"];

	public static int Build(string registryRoot, string output, string craterRepository)
	{
		string root = Path.GetFullPath(registryRoot);
		string target = Path.GetFullPath(output);

		EnsureSafeOutput(root, target);

		RegistryInfo info = RegistryInfo.Load(root);
		Catalog catalog = Catalog.Build(root);

		if (Directory.Exists(target))
		{
			Directory.Delete(target, recursive: true);
		}

		string repositoryUrl = info.Repository.Length > 0
			? $"https://github.com/{info.Repository}"
			: $"https://github.com/{craterRepository}";

		Dictionary<string, string> values = new()
		{
			["NAME"] = Html(info.Name),
			["DESCRIPTION"] = Html(info.Description),
			["CRATER_REPO"] = Html(craterRepository),
			["REPO_URL"] = Html(repositoryUrl),
			["YEAR"] = DateTime.UtcNow.Year.ToString(),
			["ASSET_VERSION"] = AssetVersion(),
		};

		Templates.WriteTo("site", target, values, overwrite: true);

		string data = Path.Combine(target, "data");

		Dictionary<string, SortedSet<string>> dependents = SitePopular.Dependents(root);

		foreach (CatalogEntry entry in catalog.Packages)
		{
			entry.Dependents = dependents.GetValueOrDefault(entry.Name)?.Count ?? 0;
		}

		Json.WriteText(Path.Combine(data, RegistryInfo.FileName), Json.Serialize(info));
		Json.WriteText(Path.Combine(data, Catalog.FileName), Json.Serialize(catalog));
		PopularPackages popular = SitePopular.Build(catalog, dependents);

		Json.WriteText(Path.Combine(data, PopularPackages.FileName), Json.Serialize(popular));

		foreach (PopularEntry entry in popular.Forest.Where(entry => entry.Detail is not null))
		{
			Json.WriteText(Path.Combine(data, "forest", entry.Name.Replace('/', Path.DirectorySeparatorChar) + ".json"), Json.Serialize(entry.Detail!));
		}

		foreach (string indexPath in Catalog.FindIndexFiles(root))
		{
			PackagePage page = BuildPage(indexPath);
			page.UsedBy = dependents.GetValueOrDefault(page.Name)?.ToList() ?? [];
			string pagePath = Path.Combine(data, "packages", page.Name.Replace('/', Path.DirectorySeparatorChar) + ".json");

			Json.WriteText(pagePath, Json.Serialize(page));
		}

		File.WriteAllText(Path.Combine(target, ".nojekyll"), "");

		return catalog.Packages.Count;
	}

	static PackagePage BuildPage(string indexPath)
	{
		RegistryIndex index = Json.ReadFile<RegistryIndex>(indexPath).Normalize(indexPath);
		RegistryRelease? latest = index.Latest;
		string? readme = null;

		if (latest is not null)
		{
			string archivePath = Path.Combine(Path.GetDirectoryName(indexPath)!, latest.Version + ".zip");

			if (File.Exists(archivePath))
			{
				byte[] archive = File.ReadAllBytes(archivePath);

				readme = ReadmeNames.Select(name => Packer.ReadText(archive, name)).FirstOrDefault(text => text is not null);
			}
		}

		return new PackagePage
		{
			Name = index.Name,
			Latest = latest?.Version ?? "",
			Deprecated = index.Deprecated,
			Owners = index.Owners,
			Versions = index.Versions.OrderByDescending(release => release.SemVer).ToList(),
			Readme = readme,
		};
	}

	static void EnsureSafeOutput(string root, string target)
	{
		string normalizedRoot = root.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;
		string normalizedTarget = target.TrimEnd(Path.DirectorySeparatorChar) + Path.DirectorySeparatorChar;

		bool isRootOrAbove = normalizedRoot.StartsWith(normalizedTarget, StringComparison.OrdinalIgnoreCase);

		if (isRootOrAbove)
		{
			throw new CraterException($"Output {Fmt.File(target)} must not be the registry folder or above it.");
		}

		bool insidePackages = File.Exists(Path.Combine(target, RegistryIndex.FileName));

		if (insidePackages)
		{
			throw new CraterException($"Output {Fmt.File(target)} looks like a package folder.");
		}
	}

	static string AssetVersion()
	{
		StringBuilder content = new();

		foreach ((string path, string text) in Templates.Under("site"))
		{
			content.Append(path).Append('\n').Append(text);
		}

		return PackageCache.Sha256(Encoding.UTF8.GetBytes(content.ToString()))[..10];
	}

	static string Html(string text)
	{
		return System.Net.WebUtility.HtmlEncode(text);
	}
}
