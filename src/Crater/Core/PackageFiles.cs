using System.Text.Json.Nodes;
using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public static class PackageFiles
{
	public const string PesdeLinkFolder = "roblox_packages";

	static readonly string[] LicensePrefixes = ["license", "licence", "copying", "notice", "unlicense"];
	static readonly string[] Manifests = ["crater.json", "wally.toml", "pesde.toml", "ember.toml", "lpm.toml"];
	static readonly string[] DocumentPrefixes = ["readme", "changelog", "changes", "history", "contributing", "code_of_conduct", "security", "authors"];

	static readonly HashSet<string> ToolFiles = new(StringComparer.OrdinalIgnoreCase)
	{
		"aftman.toml", "foreman.toml", "rokit.toml", "selene.toml", "stylua.toml", ".styluaignore", "moonwave.toml",
		"testez.toml", "testez.yml", ".gitignore", ".gitattributes", ".editorconfig", ".luaurc", "package.json",
		"package-lock.json", "pnpm-lock.yaml", "wally.lock", "pesde.lock", "crater.lock", "sourcemap.json",
	};

	static readonly HashSet<string> DevFolders = new(StringComparer.OrdinalIgnoreCase)
	{
		"docs", "doc", "documentation", "test", "tests", "__tests__", "spec", "specs", "example", "examples",
		"bench", "benches", "benchmark", "benchmarks", "node_modules", "demo", "demos", ".github", ".vscode", ".idea", ".git",
	};

	public static void ExtractPrepared(byte[] archive, string content, string id, string folderName, Manifest manifest, bool quiet)
	{
		using (System.IO.Compression.ZipArchive zip = new(new MemoryStream(archive), System.IO.Compression.ZipArchiveMode.Read))
		{
			Packer.Extract(zip, content, id, ExtractFilter(zip));
		}

		bool fromPesde = File.Exists(Path.Combine(content, "pesde.toml"));

		RemovePrograms(content, manifest, id);
		Prepare(id, content, folderName, manifest, quiet);

		if (fromPesde)
		{
			WritePesdeLinks(content, manifest);
		}
	}

	static void RemovePrograms(string content, Manifest manifest, string id)
	{
		Manifest own = ManifestIn(content);
		List<Packer.Pattern> allowed = own.AllowedFiles.Select(Packer.Pattern.Create).ToList();
		List<string> removed = [];

		foreach (string file in Directory.EnumerateFiles(content, "*", SearchOption.AllDirectories))
		{
			string relative = Path.GetRelativePath(content, file).Replace('\\', '/');
			ProgramKind kind = Executables.Classify(file, relative);

			if (kind == ProgramKind.Binary || (kind == ProgramKind.Script && !allowed.Any(pattern => pattern.Excludes(relative))))
			{
				File.Delete(file);
				removed.Add(relative);
			}
		}

		if (removed.Count > 0)
		{
			Out.Warn($"{Fmt.Pkg(id)} contained {Fmt.Plural(removed.Count, "program or archive", "programs or archives")}, crater did not install them: {string.Join(", ", removed.Take(5).Select(path => Fmt.File(path)))}");
		}
	}

	static void WritePesdeLinks(string content, Manifest manifest)
	{
		List<string> aliases = manifest.ImportClient
			.Concat(manifest.ImportServer)
			.Select(raw => Versioning.DependencySpec.Parse(raw).LinkName)
			.Distinct(StringComparer.OrdinalIgnoreCase)
			.ToList();

		string folder = Path.Combine(content, PesdeLinkFolder);

		foreach (string alias in aliases)
		{
			Directory.CreateDirectory(folder);
			File.WriteAllText(Path.Combine(folder, alias + ".luau"), Links.PrivateLink(alias, "roblox", []));
		}
	}

	public static void NameRojoProject(string content, string name)
	{
		string path = Path.Combine(content, PackageEntry.RojoProject);

		if (!File.Exists(path))
		{
			return;
		}

		JsonObject project = Json.ReadObject(path);

		project["name"] = name;
		Json.WriteText(path, Json.Serialize(project));
	}

	static Func<string, bool> ExtractFilter(System.IO.Compression.ZipArchive zip)
	{
		List<string> paths = zip.Entries
			.Select(entry => entry.FullName.Replace('\\', '/'))
			.Where(path => !path.EndsWith('/'))
			.ToList();

		return NeededFilter(paths, ProjectTop(zip, paths));
	}

	public static Func<string, bool> NeededFilter(List<string> paths, string? codeTop)
	{
		HashSet<string> definitionTops = paths
			.Where(path => path.EndsWith(".d.luau", StringComparison.OrdinalIgnoreCase) && path.Contains('/'))
			.Select(path => path.Split('/')[0])
			.ToHashSet(StringComparer.OrdinalIgnoreCase);

		return path =>
		{
			string[] segments = path.Split('/');
			string name = segments[^1];
			bool atRoot = segments.Length == 1;

			if (name.EndsWith(".d.luau", StringComparison.OrdinalIgnoreCase) || (!atRoot && definitionTops.Contains(segments[0])))
			{
				return true;
			}

			if (atRoot && (IsLicense(name) || Manifests.Contains(name, StringComparer.OrdinalIgnoreCase) || name.Equals(PackageEntry.RojoProject, StringComparison.OrdinalIgnoreCase)))
			{
				return true;
			}

			if (codeTop is not null && !segments[0].Equals(codeTop, StringComparison.OrdinalIgnoreCase))
			{
				return false;
			}

			int skipped = codeTop is null ? 0 : 1;
			string[] folders = segments.Length > skipped ? segments[skipped..^1] : [];

			if (folders.Any(DevFolders.Contains))
			{
				return false;
			}

			if ((IsDocument(name) && !IsLicense(name)) || IsTest(name))
			{
				return false;
			}

			return !(atRoot && (ToolFiles.Contains(name) || IsExtraProject(name)));
		};
	}

	static string? EmberTop(System.IO.Compression.ZipArchive zip, List<string> paths)
	{
		System.IO.Compression.ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(item => Crater.Registry.EmberRegistry.ManifestFiles.Contains(item.FullName));

		if (entry is null)
		{
			return null;
		}

		using StreamReader reader = new(entry.Open());
		string normalized = Links.NormalizeRelative(Crater.Registry.EmberRegistry.MainFromManifest(reader.ReadToEnd(), entry.FullName) ?? "");
		bool exists = normalized.Contains('/') && paths.Any(path => path.StartsWith(normalized + "/", StringComparison.Ordinal) || path.StartsWith(normalized + ".", StringComparison.Ordinal));

		return exists ? normalized.Split('/')[0] : null;
	}

	static string? ProjectTop(System.IO.Compression.ZipArchive zip, List<string> paths)
	{
		System.IO.Compression.ZipArchiveEntry? entry = zip.Entries.FirstOrDefault(item => item.FullName == PackageEntry.RojoProject);

		if (entry is null)
		{
			return EmberTop(zip, paths);
		}

		try
		{
			using StreamReader reader = new(entry.Open());
			JsonObject? project = JsonNode.Parse(reader.ReadToEnd()) as JsonObject;
			JsonObject? tree = project?["tree"] as JsonObject;
			string? target = tree?["$path"]?.GetValue<string>();

			if (tree?["$className"]?.GetValue<string>() == "DataModel" || target is null)
			{
				return null;
			}

			string normalized = Links.NormalizeRelative(target);
			string top = normalized.Split('/')[0];
			bool exists = normalized.Length > 0 && paths.Any(path => path == normalized || path.StartsWith(normalized + "/", StringComparison.Ordinal));

			return exists ? top : null;
		}
		catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException)
		{
			return null;
		}
	}

	public static void Prepare(string id, string content, string folderName, Manifest manifest, bool quiet)
	{
		PrepareRojoProject(id, content, folderName, quiet);

		List<string> definitions = Definitions(content, manifest).ToList();

		Trim(content, manifest, id);

		string? lifted = LiftProjectPath(content);

		if (lifted is not null)
		{
			definitions = definitions
				.Select(path => path.StartsWith(lifted + "/", StringComparison.Ordinal) ? path[(lifted.Length + 1)..] : path)
				.ToList();
		}

		PackageMeta.Capture(content, manifest, definitions.Where(path => File.Exists(Path.Combine(content, path))).ToList());
	}

	static string? LiftProjectPath(string content)
	{
		if (!PackageEntry.HasRojoProject(content) || ProjectPath(content) is not string target || target.Length == 0)
		{
			return null;
		}

		string source = Path.Combine(content, target);

		if (File.Exists(source))
		{
			string init = "init" + Path.GetExtension(source);

			if (File.Exists(Path.Combine(content, init)))
			{
				return null;
			}

			File.Move(source, Path.Combine(content, init));
			File.Delete(Path.Combine(content, PackageEntry.RojoProject));
			RemoveEmptyParents(content, Path.GetDirectoryName(source)!);

			return null;
		}

		if (!Directory.Exists(source))
		{
			return null;
		}

		string top = target.Split('/')[0];
		List<string> entries = Directory.EnumerateFileSystemEntries(source).ToList();
		bool clashes = entries.Any(entry =>
		{
			string name = Path.GetFileName(entry);
			string destination = Path.Combine(content, name);

			return !name.Equals(top, StringComparison.OrdinalIgnoreCase) && (File.Exists(destination) || Directory.Exists(destination));
		});

		if (clashes)
		{
			return null;
		}

		string staging = Path.Combine(content, $".crater-lift-{Guid.NewGuid():N}");

		Directory.Move(source, staging);
		RemoveEmptyParents(content, Path.GetDirectoryName(source)!);

		foreach (string entry in Directory.EnumerateFileSystemEntries(staging))
		{
			string destination = Path.Combine(content, Path.GetFileName(entry));

			if (Directory.Exists(entry))
			{
				Directory.Move(entry, destination);
			}
			else
			{
				File.Move(entry, destination);
			}
		}

		Directory.Delete(staging);
		File.Delete(Path.Combine(content, PackageEntry.RojoProject));

		return target;
	}

	static void RemoveEmptyParents(string root, string directory)
	{
		string full = Path.GetFullPath(directory);
		string stop = Path.GetFullPath(root);

		while (!full.Equals(stop, StringComparison.OrdinalIgnoreCase) && Directory.Exists(full) && !Directory.EnumerateFileSystemEntries(full).Any())
		{
			Directory.Delete(full);
			full = Path.GetDirectoryName(full)!;
		}
	}

	public static Manifest ManifestIn(string content)
	{
		string path = Path.Combine(content, Manifest.FileName);

		try
		{
			return File.Exists(path) ? Json.ReadFile<Manifest>(path) : new Manifest();
		}
		catch (CraterException)
		{
			return new Manifest();
		}
	}

	static void Trim(string content, Manifest manifest, string id)
	{
		HashSet<string> keep = new(StringComparer.OrdinalIgnoreCase);
		string? onlyCode = CodeRoot(content, manifest, id);

		foreach (string definition in Definitions(content, manifest))
		{
			keep.Add(definition.Split('/')[0]);
		}

		if (onlyCode is not null)
		{
			keep.Add(onlyCode);
			keep.Add(PackageEntry.RojoProject);

			foreach (string entry in Directory.EnumerateFileSystemEntries(content))
			{
				string name = Path.GetFileName(entry);

				if (!keep.Contains(name) && !IsLicense(name) && !Manifests.Contains(name, StringComparer.OrdinalIgnoreCase))
				{
					Delete(entry);
				}
			}

			string code = Path.Combine(content, onlyCode);

			if (Directory.Exists(code))
			{
				RemoveClutter(content, code, keep);
			}

			return;
		}

		RemoveClutter(content, content, keep);
	}

	static string? CodeRoot(string content, Manifest manifest, string id)
	{
		if (PackageEntry.HasRojoProject(content) && ProjectPath(content) is string projectPath)
		{
			return projectPath.Split('/')[0];
		}

		PackageEntry entry = PackageEntry.Analyze(content, manifest.PathToMain, id[..id.LastIndexOf('@')]);

		if (entry.Kind == EntryKind.Missing || entry.Segments.Count == 0)
		{
			return null;
		}

		string top = entry.Segments[0];

		return Directory.Exists(Path.Combine(content, top)) ? top : null;
	}

	static string? ProjectPath(string content)
	{
		try
		{
			JsonObject project = Json.ReadObject(Path.Combine(content, PackageEntry.RojoProject));
			string? path = project["tree"]?["$path"]?.GetValue<string>();

			return path is null ? null : Links.NormalizeRelative(path);
		}
		catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or CraterException)
		{
			return null;
		}
	}

	static IEnumerable<string> Definitions(string content, Manifest manifest)
	{
		List<string> patterns = manifest.DefinitionPatterns;

		try
		{
			return patterns.Count > 0 ? DefinitionLookup.Resolve(content, patterns) : DefinitionLookup.AutoDetect(content);
		}
		catch (CraterException)
		{
			return [];
		}
	}

	static void RemoveClutter(string root, string directory, HashSet<string> keep)
	{
		bool atRoot = directory == root;

		foreach (string child in Directory.EnumerateDirectories(directory))
		{
			string name = Path.GetFileName(child);

			if (atRoot && keep.Contains(name))
			{
				continue;
			}

			if (DevFolders.Contains(name))
			{
				Delete(child);
				continue;
			}

			RemoveClutter(root, child, keep);
		}

		foreach (string file in Directory.EnumerateFiles(directory))
		{
			string name = Path.GetFileName(file);

			if ((atRoot && keep.Contains(name)) || IsLicense(name))
			{
				continue;
			}

			if (IsDocument(name) || IsTest(name) || (atRoot && (ToolFiles.Contains(name) || IsExtraProject(name))))
			{
				File.Delete(file);
			}
		}
	}

	static bool IsLicense(string name)
	{
		string lower = name.ToLowerInvariant();

		return LicensePrefixes.Any(lower.StartsWith);
	}

	static bool IsDocument(string name)
	{
		string lower = name.ToLowerInvariant();

		return DocumentPrefixes.Any(lower.StartsWith) || lower.EndsWith(".md") || lower.EndsWith(".mdx");
	}

	static bool IsTest(string name)
	{
		string lower = name.ToLowerInvariant();

		return lower.EndsWith(".spec.luau") || lower.EndsWith(".spec.lua") || lower.EndsWith(".test.luau") || lower.EndsWith(".test.lua")
			|| lower.EndsWith(".story.luau") || lower.EndsWith(".story.lua") || lower.StartsWith("jest.config");
	}

	static bool IsExtraProject(string name)
	{
		return name.EndsWith(".project.json", StringComparison.OrdinalIgnoreCase) && !name.Equals(PackageEntry.RojoProject, StringComparison.OrdinalIgnoreCase);
	}

	static void Delete(string path)
	{
		if (Directory.Exists(path))
		{
			Directory.Delete(path, recursive: true);
		}
		else if (File.Exists(path))
		{
			File.Delete(path);
		}
	}

	static void PrepareRojoProject(string id, string content, string name, bool quiet)
	{
		string path = Path.Combine(content, PackageEntry.RojoProject);

		if (!File.Exists(path))
		{
			return;
		}

		try
		{
			JsonObject project = Json.ReadObject(path);
			JsonObject? tree = project["tree"] as JsonObject;
			string? className = tree?["$className"]?.GetValue<string>();
			string? target = tree?["$path"]?.GetValue<string>();
			bool targetExists = target is not null && (Directory.Exists(Path.Combine(content, target)) || File.Exists(Path.Combine(content, target)));

			if (className == "DataModel" || !targetExists)
			{
				File.Delete(path);
				Note(quiet, $"  {id}: ignored its {PackageEntry.RojoProject}, it does not describe the package");
				return;
			}

			project["name"] = name;
			Json.WriteText(path, Json.Serialize(project));
		}
		catch (Exception exception) when (exception is System.Text.Json.JsonException or InvalidOperationException or CraterException)
		{
			File.Delete(path);
			Note(quiet, $"  {id}: ignored its {PackageEntry.RojoProject}, it could not be read");
		}
	}

	static void Note(bool quiet, string text)
	{
		if (!quiet)
		{
			Out.Dim(text);
		}
	}
}
