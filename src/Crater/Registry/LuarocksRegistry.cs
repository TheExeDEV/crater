using System.Collections.Concurrent;
using System.Formats.Tar;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Registry;

public sealed class LuarocksRegistry(string url) : IRegistry, IListedRegistry
{
	public const string DefaultUrl = "https://luarocks.org";
	public const string Scope = "rocks";

	sealed record Rock(string Name, string RockVersion, string[] Arches, Rockspec Spec);

	readonly ConcurrentDictionary<string, Lazy<Task<RegistryIndex?>>> indexes = new();
	readonly ConcurrentDictionary<string, Rock> rocks = new();
	readonly SemaphoreSlim requests = new(8);
	Lazy<Task<Dictionary<string, Dictionary<string, string[]>>>>? repository;

	public string Location => url;

	public static bool IsLuarocks(string location)
	{
		return location.TrimEnd('/').Equals(DefaultUrl, StringComparison.OrdinalIgnoreCase);
	}

	public Task<RegistryIndex?> GetIndexAsync(string fullName)
	{
		return indexes.GetOrAdd(fullName.ToLowerInvariant(), name => new Lazy<Task<RegistryIndex?>>(() => LoadIndexAsync(name))).Value;
	}

	async Task<RegistryIndex?> LoadIndexAsync(string fullName)
	{
		int slash = fullName.IndexOf('/');

		if (slash < 0 || fullName[..slash] != Scope)
		{
			return null;
		}

		string name = fullName[(slash + 1)..];
		Dictionary<string, Dictionary<string, string[]>> all = await RepositoryAsync();

		if (!all.TryGetValue(name, out Dictionary<string, string[]>? versions))
		{
			return null;
		}

		List<(SemVer Version, string RockVersion, string[] Arches)> chosen = versions
			.Where(pair => pair.Value.Contains("rockspec") && Rockspec.ToSemVer(pair.Key) is not null)
			.GroupBy(pair => Rockspec.ToSemVer(pair.Key)!.ToString())
			.Select(group => group.MaxBy(pair => Rockspec.Revision(pair.Key)))
			.Select(pair => (Rockspec.ToSemVer(pair.Key)!, pair.Key, pair.Value))
			.ToList();

		RegistryIndex index = new() { Name = fullName };
		Rock?[] loaded = await Task.WhenAll(chosen.Select(item => LoadRockAsync(name, item.RockVersion, item.Arches)));

		foreach (Rock rock in loaded.OfType<Rock>())
		{
			SemVer version = Rockspec.ToSemVer(rock.RockVersion)!;

			rocks[$"{fullName}@{version}"] = rock;
			index.Versions.Add(ToRelease(fullName, version.ToString(), rock));
		}

		return index.Versions.Count == 0 ? null : index.Normalize($"{url}/modules/{name}");
	}

	async Task<Rock?> LoadRockAsync(string name, string rockVersion, string[] arches)
	{
		byte[]? data = await ImmutableAsync($"{url}/{name}-{rockVersion}.rockspec");

		if (data is null)
		{
			return null;
		}

		try
		{
			return new Rock(name, rockVersion, arches, Rockspec.Parse(Encoding.UTF8.GetString(data)));
		}
		catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException or OverflowException)
		{
			return null;
		}
	}

	static RegistryRelease ToRelease(string fullName, string version, Rock rock)
	{
		List<string> dependencies = rock.Spec.Dependencies
			.Select(Rockspec.ParseDependency)
			.OfType<(string Name, string Requirement)>()
			.Where(dependency => dependency.Name != "lua")
			.Select(dependency => $"{Scope}/{dependency.Name}{dependency.Requirement}")
			.ToList();

		return new RegistryRelease
		{
			Version = version,
			Manifest = new Manifest
			{
				Name = fullName,
				Version = version,
				Description = rock.Spec.Summary,
				License = rock.Spec.License,
				Repository = rock.Spec.Homepage,
				ImportTo = "shared",
				PathToMain = MainModule(rock.Name, rock.Spec.Modules.Keys.ToList()),
				ImportClient = dependencies,
				ImportServer = dependencies,
			},
		};
	}

	public static Dictionary<string, string> Layout(IReadOnlyCollection<string> modules)
	{
		return modules.ToDictionary(module => module, module =>
		{
			string path = module.Replace('.', '/');

			return modules.Any(other => other.StartsWith(module + ".", StringComparison.Ordinal)) ? path + "/init.lua" : path + ".lua";
		});
	}

	public static string MainModule(string rockName, List<string> modules)
	{
		string? main = modules.FirstOrDefault(module => module.Equals(rockName, StringComparison.OrdinalIgnoreCase))
			?? modules.FirstOrDefault(module => rockName.EndsWith("-" + module, StringComparison.OrdinalIgnoreCase) || rockName.StartsWith(module + "-", StringComparison.OrdinalIgnoreCase))
			?? (modules.Count == 1 ? modules[0] : null);

		if (main is null)
		{
			return "";
		}

		Dictionary<string, string> layout = Layout(modules);
		string path = layout[main];
		string top = path.Split('/')[0];

		return path.Contains('/') && layout.Values.Any(other => !other.StartsWith(top + "/", StringComparison.Ordinal)) ? "" : path;
	}

	public async Task<byte[]> DownloadAsync(string fullName, string version)
	{
		string key = $"{fullName.ToLowerInvariant()}@{version}";

		if (!rocks.TryGetValue(key, out Rock? rock))
		{
			await GetIndexAsync(fullName);

			if (!rocks.TryGetValue(key, out rock))
			{
				throw new CraterException($"{Fmt.Pkg($"{fullName}@{version}")} is not on LuaRocks.");
			}
		}

		string id = Fmt.Pkg($"{fullName}@{version}");

		if (!rock.Spec.IsPureLua)
		{
			string reason = rock.Spec.NativeModules.Count > 0 ? $"its modules {string.Join(", ", rock.Spec.NativeModules)} are written in C"
				: rock.Spec.BuildType is "builtin" or "none" or "module" ? "it compiles C sources"
				: $"it is built with {rock.Spec.BuildType}";

			throw new CraterException($"{id} cannot run in Roblox: {reason}, and Roblox runs only Luau. Only rocks written in plain Lua can be installed.");
		}

		Dictionary<string, byte[]> files = await SourceFilesAsync(rock);
		Dictionary<string, string> modules = rock.Spec.Modules.Count > 0 ? rock.Spec.Modules : DetectModules(files);
		Dictionary<string, (string Dependency, string Path)> dependencyModules = await DependencyModulesAsync(rock);
		LuarocksPackage package = LuarocksPackage.Build(files, modules, dependencyModules);

		if (package.Missing.Count > 0)
		{
			string what = package.Missing.Count == 1 ? "which is not a plain Lua module crater can install, so that require fails" : "which are not plain Lua modules crater can install, so those requires fail";

			Out.Warn($"{id} requires {string.Join(", ", package.Missing.Select(module => Fmt.Val(module)))}, {what} in Roblox");
		}

		if (package.UsesLua51Modules)
		{
			Out.Warn($"{id} uses the Lua 5.1 {Fmt.Val("module()")} function, which Luau does not have");
		}

		return package.Archive;
	}

	async Task<Dictionary<string, (string Dependency, string Path)>> DependencyModulesAsync(Rock rock)
	{
		Dictionary<string, (string Dependency, string Path)> modules = [];

		foreach ((string name, _) in rock.Spec.Dependencies.Select(Rockspec.ParseDependency).OfType<(string Name, string Requirement)>().Where(dependency => dependency.Name != "lua"))
		{
			RegistryIndex? index = await GetIndexAsync($"{Scope}/{name}");
			RegistryRelease? latest = index?.Latest;

			if (latest is null || !rocks.TryGetValue($"{Scope}/{name}@{latest.Version}", out Rock? dependency))
			{
				continue;
			}

			foreach ((string module, string path) in Layout(dependency.Spec.Modules.Keys))
			{
				modules.TryAdd(module, (name, path));
			}
		}

		return modules;
	}

	async Task<Dictionary<string, byte[]>> SourceFilesAsync(Rock rock)
	{
		string fileName = $"{rock.Name}-{rock.RockVersion}";
		Dictionary<string, byte[]> files;

		if (rock.Arches.Contains("src") && await ImmutableAsync($"{url}/{fileName}.src.rock") is byte[] sourceRock)
		{
			files = FromSourceRock(sourceRock, fileName + ".rockspec");
		}
		else
		{
			string archiveUrl = SourceArchiveUrl(rock.Spec)
				?? throw new CraterException($"{Fmt.Pkg($"{Scope}/{rock.Name}")}: crater cannot download its source ({rock.Spec.SourceUrl ?? "no source url"}).");
			byte[] archive = await ImmutableAsync(archiveUrl)
				?? throw new CraterException($"{Fmt.Pkg($"{Scope}/{rock.Name}")}: could not download {Fmt.File(archiveUrl)}.");

			files = Unpack(archiveUrl, archive);
		}

		return StripBase(files, rock.Spec.SourceDir);
	}

	static Dictionary<string, byte[]> FromSourceRock(byte[] data, string rockspecName)
	{
		Dictionary<string, byte[]> entries = FromZip(data);
		string? archive = entries.Keys.FirstOrDefault(name => !name.Contains('/') && name != rockspecName && IsArchive(name));

		if (archive is not null)
		{
			return Unpack(archive, entries[archive]);
		}

		return entries.Where(pair => pair.Key != rockspecName).ToDictionary();
	}

	static string? SourceArchiveUrl(Rockspec spec)
	{
		string? source = spec.SourceUrl;

		if (source is null)
		{
			return null;
		}

		if (source.StartsWith("https://", StringComparison.OrdinalIgnoreCase) && IsArchive(source))
		{
			return source;
		}

		string trimmed = System.Text.RegularExpressions.Regex.Replace(source, @"^(git\+https|git\+ssh|git|https)://", "");

		if (!trimmed.StartsWith("github.com/", StringComparison.OrdinalIgnoreCase))
		{
			return null;
		}

		string repository = trimmed["github.com/".Length..].TrimEnd('/');

		repository = repository.EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? repository[..^4] : repository;

		return $"https://github.com/{repository}/archive/{spec.SourceTag ?? spec.SourceBranch ?? "HEAD"}.tar.gz";
	}

	static bool IsArchive(string name)
	{
		string lower = name.ToLowerInvariant();

		return lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") || lower.EndsWith(".zip");
	}

	static Dictionary<string, byte[]> Unpack(string name, byte[] data)
	{
		return name.EndsWith(".zip", StringComparison.OrdinalIgnoreCase) ? FromZip(data) : FromTarGz(data);
	}

	static Dictionary<string, byte[]> FromZip(byte[] data)
	{
		using ZipArchive archive = new(new MemoryStream(data), ZipArchiveMode.Read);
		Dictionary<string, byte[]> files = [];

		foreach (ZipArchiveEntry entry in archive.Entries.Where(entry => !entry.FullName.EndsWith('/')))
		{
			using Stream stream = entry.Open();
			using MemoryStream buffer = new();

			stream.CopyTo(buffer);
			files[entry.FullName.Replace('\\', '/')] = buffer.ToArray();
		}

		return files;
	}

	static Dictionary<string, byte[]> FromTarGz(byte[] data)
	{
		using GZipStream gzip = new(new MemoryStream(data), CompressionMode.Decompress);
		using TarReader reader = new(gzip);
		Dictionary<string, byte[]> files = [];

		while (reader.GetNextEntry() is TarEntry entry)
		{
			if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
			{
				continue;
			}

			using MemoryStream buffer = new();

			entry.DataStream.CopyTo(buffer);
			files[entry.Name.Replace('\\', '/').TrimStart('.', '/')] = buffer.ToArray();
		}

		return files;
	}

	static Dictionary<string, byte[]> StripBase(Dictionary<string, byte[]> files, string? sourceDir)
	{
		string? prefix = sourceDir is not null && files.Keys.Any(name => name.StartsWith(sourceDir.Trim('/') + "/", StringComparison.Ordinal))
			? sourceDir.Trim('/') + "/"
			: null;

		if (prefix is null)
		{
			List<string> tops = files.Keys.Select(name => name.Contains('/') ? name[..name.IndexOf('/')] : "").Distinct().ToList();

			prefix = tops.Count == 1 && tops[0].Length > 0 ? tops[0] + "/" : null;
		}

		return prefix is null
			? files
			: files.Where(pair => pair.Key.StartsWith(prefix, StringComparison.Ordinal)).ToDictionary(pair => pair.Key[prefix.Length..], pair => pair.Value);
	}

	static Dictionary<string, string> DetectModules(Dictionary<string, byte[]> files)
	{
		string[] skipped = ["spec/", "test/", "tests/", "examples/", "example/", "doc/", "docs/", "bench/"];

		foreach (string root in new[] { "lua/", "src/", "" })
		{
			Dictionary<string, string> modules = files.Keys
				.Where(name => name.StartsWith(root, StringComparison.Ordinal) && name.EndsWith(".lua", StringComparison.OrdinalIgnoreCase))
				.Where(name => !skipped.Any(dir => name[root.Length..].StartsWith(dir, StringComparison.OrdinalIgnoreCase)) && !name.EndsWith(".rockspec", StringComparison.OrdinalIgnoreCase))
				.ToDictionary(name => name[root.Length..^4].Replace('/', '.'), name => name);

			if (modules.Count > 0)
			{
				return modules;
			}
		}

		return [];
	}

	public async Task<Catalog?> GetCatalogAsync(string query)
	{
		string needle = query.Trim().ToLowerInvariant();
		List<string> names = (await RepositoryAsync()).Keys
			.Where(name => needle.Length == 0 || name.Contains(needle, StringComparison.Ordinal))
			.OrderBy(name => name == needle ? 0 : name.StartsWith(needle, StringComparison.Ordinal) ? 1 : 2)
			.ThenBy(name => name, StringComparer.Ordinal)
			.Take(30)
			.ToList();

		RegistryIndex?[] loaded = await Task.WhenAll(names.Select(name => GetIndexAsync($"{Scope}/{name}")));

		return new Catalog
		{
			Packages = loaded.OfType<RegistryIndex>()
				.Where(index => index.Latest is not null)
				.Select(index => CatalogEntry.From(index, index.Latest!))
				.ToList(),
			GeneratedAt = DateTimeOffset.UtcNow,
		};
	}

	public async Task<Catalog?> ListAllAsync()
	{
		List<CatalogEntry> packages = [];

		foreach ((string name, Dictionary<string, string[]> versions) in (await RepositoryAsync()).OrderBy(pair => pair.Key, StringComparer.Ordinal))
		{
			string? latest = versions.Keys
				.Where(version => versions[version].Contains("rockspec") && Rockspec.ToSemVer(version) is not null)
				.OrderByDescending(version => Rockspec.ToSemVer(version)!)
				.ThenByDescending(Rockspec.Revision)
				.FirstOrDefault();

			if (latest is null)
			{
				continue;
			}

			packages.Add(new CatalogEntry
			{
				Name = $"{Scope}/{name}",
				Latest = Rockspec.ToSemVer(latest)!.ToString(),
				Description = CachedSummary(name, latest),
			});
		}

		return new Catalog { Packages = packages, GeneratedAt = DateTimeOffset.UtcNow };
	}

	string CachedSummary(string name, string rockVersion)
	{
		string path = CachePath($"{url}/{name}-{rockVersion}.rockspec");

		try
		{
			return File.Exists(path) ? Rockspec.Parse(File.ReadAllText(path)).Summary : "";
		}
		catch (Exception exception) when (exception is FormatException or ArgumentException or InvalidOperationException or OverflowException)
		{
			return "";
		}
	}

	public Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		throw new CraterException("crater cannot publish to LuaRocks. Publish there with luarocks upload.");
	}

	Task<Dictionary<string, Dictionary<string, string[]>>> RepositoryAsync()
	{
		repository ??= new Lazy<Task<Dictionary<string, Dictionary<string, string[]>>>>(LoadRepositoryAsync);

		return repository.Value;
	}

	async Task<Dictionary<string, Dictionary<string, string[]>>> LoadRepositoryAsync()
	{
		byte[] data = await Http.GetCachedAsync($"{url}/manifest-5.1.json")
			?? throw new CraterException("LuaRocks is not available right now.");

		using JsonDocument document = JsonDocument.Parse(data);
		Dictionary<string, Dictionary<string, string[]>> result = new(StringComparer.OrdinalIgnoreCase);

		foreach (JsonProperty package in document.RootElement.GetProperty("repository").EnumerateObject())
		{
			result[package.Name.ToLowerInvariant()] = package.Value.EnumerateObject().ToDictionary(
				version => version.Name,
				version => version.Value.EnumerateArray().Select(item => item.GetProperty("arch").GetString() ?? "").ToArray());
		}

		return result;
	}

	static string CachePath(string address)
	{
		return Path.Combine(CraterPaths.Cache, "luarocks", Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(address))));
	}

	async Task<byte[]?> ImmutableAsync(string address)
	{
		string path = CachePath(address);

		if (File.Exists(path))
		{
			return await File.ReadAllBytesAsync(path);
		}

		await requests.WaitAsync();

		try
		{
			byte[]? data = await Http.GetBytesOrNullAsync(address);

			if (data is not null)
			{
				Directory.CreateDirectory(Path.GetDirectoryName(path)!);
				await File.WriteAllBytesAsync(path, data);
			}

			return data;
		}
		finally
		{
			requests.Release();
		}
	}
}
