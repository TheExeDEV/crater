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
		[JsonPropertyName("icon")] public string? Icon { get; set; }
	}

	static readonly string[] ReadmeNames = ["README.md", "readme.md", "Readme.md", "README"];

	static readonly string[] TextExtensions = [".html", ".js", ".css", ".svg", ".json", ".txt", ".xml", ""];

	public static int Build(string registryRoot, string output, string craterRepository, string? frontend = null)
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
			["ASSET_VERSION"] = frontend is null ? "" : AssetVersion(frontend),
		};

		if (frontend is not null)
		{
			CopyFrontend(Path.GetFullPath(frontend), target, values);
		}

		string data = Path.Combine(target, "data");

		Dictionary<string, SortedSet<string>> dependents = SitePopular.Dependents(root);

		foreach (CatalogEntry entry in catalog.Packages)
		{
			entry.Dependents = dependents.GetValueOrDefault(entry.Name)?.Count ?? 0;
		}

		Dictionary<string, string> icons = WriteIcons(root, target);

		foreach (CatalogEntry entry in catalog.Packages)
		{
			entry.Icon = icons.GetValueOrDefault(entry.Name);
		}

		Json.WriteText(Path.Combine(data, RegistryInfo.FileName), Json.Serialize(info));
		Json.WriteText(Path.Combine(data, Originals.FileName), Json.Serialize(new Originals { Packages = Originals.Load(root).Normalized() }));
		Json.WriteText(Path.Combine(data, Catalog.FileName), Json.Serialize(catalog));
		PopularPackages popular = SitePopular.Build(catalog, dependents);

		Json.WriteText(Path.Combine(data, PopularPackages.FileName), Json.Serialize(popular));

		foreach (PopularEntry entry in popular.Packages.Where(entry => entry.Source == "forest" && entry.Detail is not null))
		{
			Json.WriteText(Path.Combine(data, "forest", entry.Name.Replace('/', Path.DirectorySeparatorChar) + ".json"), Json.Serialize(entry.Detail!));
		}

		foreach (string indexPath in Catalog.FindIndexFiles(root))
		{
			PackagePage page = BuildPage(indexPath);
			page.UsedBy = dependents.GetValueOrDefault(page.Name)?.ToList() ?? [];
			page.Icon = icons.GetValueOrDefault(page.Name);
			string pagePath = Path.Combine(data, "packages", page.Name.Replace('/', Path.DirectorySeparatorChar) + ".json");

			Json.WriteText(pagePath, Json.Serialize(page));
		}

		if (!Http.Offline && Environment.GetEnvironmentVariable("CRATER_SITE_NO_EXTERNAL") is not { Length: > 0 })
		{
			Catalog? ember = new EmberRegistry(EmberRegistry.RawIndex).ListAsync("", 500).GetAwaiter().GetResult();

			if (ember is not null)
			{
				Json.WriteText(Path.Combine(data, "ember.json"), Json.Serialize(ember));
			}
		}

		List<RecentRelease> recent = SiteFeed.Recent(root, icons);

		Json.WriteText(Path.Combine(data, SiteFeed.RecentFile), Json.Serialize(recent));
		File.WriteAllText(Path.Combine(target, SiteFeed.FeedFile), SiteFeed.Rss(info, recent));

		return catalog.Packages.Count;
	}

	static Dictionary<string, string> WriteIcons(string root, string target)
	{
		Dictionary<string, string> icons = new(StringComparer.Ordinal);

		foreach (string indexPath in Catalog.FindIndexFiles(root))
		{
			RegistryIndex index = Json.ReadFile<RegistryIndex>(indexPath).Normalize(indexPath);
			RegistryRelease? latest = index.Latest;
			string? icon = latest?.Manifest.Icon;
			string extension = Path.GetExtension(icon ?? "").ToLowerInvariant();
			string archivePath = Path.Combine(Path.GetDirectoryName(indexPath)!, (latest?.Version ?? "") + ".zip");

			if (icon is null || !Packer.IconExtensions.Contains(extension) || !File.Exists(archivePath))
			{
				continue;
			}

			byte[]? data = Packer.ReadBytes(File.ReadAllBytes(archivePath), icon, Packer.MaxIconBytes);

			if (data is null || (extension == ".svg" && !SafeSvg.IsSafe(data)))
			{
				continue;
			}

			string relative = $"data/icons/{index.Name}{extension}";
			string path = Path.Combine(target, relative.Replace('/', Path.DirectorySeparatorChar));

			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			File.WriteAllBytes(path, data);
			icons[index.Name] = relative;
		}

		return icons;
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

	static void CopyFrontend(string frontend, string target, IReadOnlyDictionary<string, string> values)
	{
		if (!Directory.Exists(frontend))
		{
			throw new CraterException($"Frontend folder {Fmt.File(frontend)} not found.");
		}

		foreach (string file in Directory.EnumerateFiles(frontend, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(frontend, file);
			string destination = Path.Combine(target, relative);

			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);

			if (TextExtensions.Contains(Path.GetExtension(file).ToLowerInvariant()))
			{
				File.WriteAllText(destination, Templates.Fill(File.ReadAllText(file), values));
			}
			else
			{
				File.Copy(file, destination, overwrite: true);
			}
		}
	}

	static string AssetVersion(string frontend)
	{
		StringBuilder content = new();

		foreach (string file in Directory.EnumerateFiles(frontend, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal))
		{
			content.Append(Path.GetRelativePath(frontend, file)).Append('\n').Append(File.ReadAllText(file));
		}

		return PackageCache.Sha256(Encoding.UTF8.GetBytes(content.ToString()))[..10];
	}

	static string Html(string text)
	{
		return System.Net.WebUtility.HtmlEncode(text);
	}
}
