using System.Text.Json;

namespace Crater.Core;

public enum EntryKind
{
	Module,
	Collection,
	Missing,
}

public sealed record CollectionMember(string Name);

public sealed record PackageEntry(EntryKind Kind, List<string> Segments, List<CollectionMember> Members, string? MainFile)
{
	static readonly string[] LuauExtensions = [".luau", ".lua"];
	static readonly string[] InitFiles = ["init.luau", "init.lua"];

	public const string RojoProject = "default.project.json";

	public static bool HasRojoProject(string contentDirectory) => File.Exists(Path.Combine(contentDirectory, RojoProject));

	public static PackageEntry Analyze(string contentDirectory, string pathToMain, string? packageName = null)
	{
		if (pathToMain.Length == 0 && ProjectMain(contentDirectory) is string projectMain)
		{
			pathToMain = projectMain;
		}

		PackageEntry declared = AnalyzePath(contentDirectory, pathToMain);

		if (declared.Kind == EntryKind.Module)
		{
			return declared;
		}

		PackageEntry atRoot = AnalyzePath(contentDirectory, "");
		bool isCraterPackage = File.Exists(Path.Combine(contentDirectory, "crater.json")) || PackageMeta.Read(contentDirectory)?.IsCrater == true;
		bool declaredMissing = Links.NormalizeRelative(pathToMain).Length > 0 && declared.Kind == EntryKind.Missing;

		if (atRoot.Kind == EntryKind.Module && (declaredMissing || !isCraterPackage))
		{
			return atRoot;
		}

		if (isCraterPackage && !declaredMissing)
		{
			return declared;
		}

		if (EntryDiscovery.Find(contentDirectory, packageName ?? "") is string discovered)
		{
			PackageEntry found = AnalyzePath(contentDirectory, discovered);

			if (found.Kind != EntryKind.Missing)
			{
				return found;
			}
		}

		return declaredMissing ? atRoot : declared;
	}

	static PackageEntry AnalyzePath(string contentDirectory, string pathToMain)
	{
		string? mainFile = TypeExports.FindMainFile(contentDirectory, pathToMain);
		List<string> segments = Links.MainSegments(pathToMain);

		if (mainFile is not null)
		{
			return new PackageEntry(EntryKind.Module, segments, [], mainFile);
		}

		string relative = Links.NormalizeRelative(pathToMain);
		string folder = relative.Length == 0 ? contentDirectory : Path.Combine(contentDirectory, relative);

		if (!Directory.Exists(folder))
		{
			return new PackageEntry(EntryKind.Missing, segments, [], null);
		}

		List<CollectionMember> members = CollectMembers(folder);
		EntryKind kind = members.Count > 0 ? EntryKind.Collection : EntryKind.Missing;

		return new PackageEntry(kind, segments, members, null);
	}

	static string? ProjectMain(string contentDirectory)
	{
		if (!HasRojoProject(contentDirectory))
		{
			return EmberMain(contentDirectory);
		}

		try
		{
			using JsonDocument project = JsonDocument.Parse(File.ReadAllText(Path.Combine(contentDirectory, RojoProject)));

			return project.RootElement.TryGetProperty("tree", out JsonElement tree) &&
				tree.TryGetProperty("$path", out JsonElement path) &&
				path.ValueKind == JsonValueKind.String
				? path.GetString()
				: null;
		}
		catch (JsonException)
		{
			return null;
		}
	}

	static string? EmberMain(string contentDirectory)
	{
		foreach (string name in Crater.Registry.EmberRegistry.ManifestFiles)
		{
			string path = Path.Combine(contentDirectory, name);

			if (File.Exists(path))
			{
				return Crater.Registry.EmberRegistry.MainFromManifest(File.ReadAllText(path), name);
			}
		}

		return null;
	}

	static List<CollectionMember> CollectMembers(string folder)
	{
		List<CollectionMember> members = [];
		HashSet<string> seen = new(StringComparer.OrdinalIgnoreCase);

		foreach (string file in Directory.EnumerateFiles(folder).OrderBy(path => path, StringComparer.Ordinal))
		{
			string name = Path.GetFileName(file);
			string? extension = LuauExtensions.FirstOrDefault(item => name.EndsWith(item, StringComparison.OrdinalIgnoreCase));

			if (extension is null)
			{
				continue;
			}

			string stem = name[..^extension.Length];
			bool isScriptOrTest = stem.Contains('.');

			if (isScriptOrTest || stem.Length == 0 || seen.Contains(stem))
			{
				continue;
			}

			seen.Add(stem);
			members.Add(new CollectionMember(stem));
		}

		foreach (string directory in Directory.EnumerateDirectories(folder).OrderBy(path => path, StringComparer.Ordinal))
		{
			string name = Path.GetFileName(directory);
			bool hidden = name.StartsWith('.') || name.StartsWith('_');
			bool requirable = InitFiles.Any(init => File.Exists(Path.Combine(directory, init)));

			if (hidden || !requirable || seen.Contains(name))
			{
				continue;
			}

			seen.Add(name);
			members.Add(new CollectionMember(name));
		}

		return members;
	}
}
