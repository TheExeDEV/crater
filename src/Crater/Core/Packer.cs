using System.IO.Compression;
using System.Text.RegularExpressions;
using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public static class Packer
{
	static readonly string[] DefaultExclude =
	[
		".git/",
		".github/",
		".vs/",
		".vscode/",
		".idea/",
		"node_modules/",
		"*.zip",
		".crater-staging/",
		LockFile.FileName,
		CraterDeclare.FileName,
		ProjectConfig.FileName,
		ProjectConfig.LegacyFileName,
	];

	static readonly DateTimeOffset FixedTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

	static readonly string[] RootDocuments = ["README*", "LICENSE*", "LICENCE*", "COPYING*", "CHANGELOG*"];

	public static string Root(Project project)
	{
		string? configured = project.Manifest.PackageRoot;

		if (string.IsNullOrWhiteSpace(configured))
		{
			return project.Directory;
		}

		string root = Path.GetFullPath(Path.Combine(project.Directory, Links.NormalizeRelative(configured)));

		if (!Directory.Exists(root))
		{
			string build = project.Manifest.Scripts?.ContainsKey(PrepackScript) == true
				? $"{Fmt.Cmd($"crater run {PrepackScript}")} did not create it"
				: $"build it first, or add a {Fmt.Val($"\"{PrepackScript}\"")} script that does";

			throw new CraterException($"package_root = {Fmt.File(configured)} does not exist: {build}.");
		}

		return root;
	}

	public const string PrepackScript = "prepack";

	public static byte[] Pack(Project project, out List<string> files)
	{
		string root = Root(project);
		bool built = root != project.Directory;
		List<Pattern> patterns = DefaultExclude
			.Concat(project.Config.ConfiguredFolders().Select(pair => pair.Folder + "/"))
			.Concat(project.Config.VendorFolders().Select(pair => pair.Folder + "/"))
			.Concat(project.Manifest.ExcludedFiles)
			.Select(Pattern.Create)
			.ToList();

		List<string> collected = [];
		IgnoreRules rules = built ? new IgnoreRules() : new IgnoreRules().With(root, root);

		CollectFiles(root, root, rules, collected, gitignore: !built);

		Dictionary<string, string> sources = collected
			.Where(relative => !patterns.Any(pattern => pattern.Excludes(relative)))
			.Where(relative => !built || relative != Manifest.FileName)
			.ToDictionary(relative => relative, relative => Path.Combine(root, relative), StringComparer.Ordinal);

		if (built)
		{
			AddRootDocuments(project, sources);
		}

		List<Pattern> allowed = project.Manifest.AllowedFiles.Select(Pattern.Create).ToList();
		List<(string Path, ProgramKind Kind)> programs = sources
			.Select(pair => (Path: pair.Key, Kind: Executables.Classify(pair.Value, pair.Key)))
			.Where(item => item.Kind == ProgramKind.Binary || (item.Kind == ProgramKind.Script && !allowed.Any(pattern => pattern.Excludes(item.Path))))
			.ToList();

		if (programs.Count > 0)
		{
			foreach ((string path, ProgramKind _) in programs)
			{
				sources.Remove(path);
			}

			string shown = string.Join(", ", programs.Take(5).Select(item => Fmt.File(item.Path)));
			string more = programs.Count > 5 ? $" and {programs.Count - 5} more" : "";

			Out.Warn($"Left out {Fmt.Plural(programs.Count, "executable file or archive", "executable files or archives")}, packages do not ship programs: {shown}{more}");

			if (programs.Any(item => item.Kind == ProgramKind.Script))
			{
				Out.Dim($"  To ship a script on purpose, list it in {Fmt.Val("\"package_files\"")} in {Fmt.File(Manifest.FileName)}, for example {Fmt.Val("\"package_files\": { \"allow\": [\"scripts/setup.sh\"] }")}. Programs cannot be allowed.");
			}
		}

		if (built)
		{
			sources[Manifest.FileName] = project.ManifestPath;
		}

		if (!sources.ContainsKey(Manifest.FileName))
		{
			throw new CraterException($"{Fmt.File(Manifest.FileName)} is excluded from the archive by the exclude patterns.");
		}

		List<string> own = sources.Keys.OrderBy(relative => relative, StringComparer.Ordinal).ToList();

		RequirePath(root, own, project.Manifest.PathToMain, "path_to_main");
		RequireIcon(project, root, own);
		RequireDefinitions(project, root, own);
		DocLanguage.Require(own.Select(relative => (relative, sources[relative])));
		RequireImplementationEntry(project, own);

		AddVendored(project, sources);

		files = sources.Keys.OrderBy(relative => relative, StringComparer.Ordinal).ToList();

		using MemoryStream stream = new();

		using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (string relative in files)
			{
				ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
				entry.LastWriteTime = FixedTime;

				using Stream destination = entry.Open();
				using FileStream source = File.OpenRead(sources[relative]);

				source.CopyTo(destination);
			}
		}

		return stream.ToArray();
	}

	public static List<LockedPackage> Vendored(Project project)
	{
		if (!project.Manifest.PacksVendored)
		{
			return [];
		}

		LockFile lockFile = LockFile.Load(project.LockPath);

		return Manifest.PackedVendorRealms.SelectMany(lockFile.VendorFor).ToList();
	}

	static void AddVendored(Project project, Dictionary<string, string> sources)
	{
		if (!project.Manifest.PacksVendored)
		{
			return;
		}

		LockFile lockFile = LockFile.Load(project.LockPath);
		string install = $"Run {Fmt.Cmd("crater install")} in {Fmt.File(project.Directory)} first";

		foreach (Realm realm in Manifest.PackedVendorRealms)
		{
			List<string> wanted = project.Manifest.AllVendored()
				.Where(item => item.Realm == realm)
				.Select(item => item.Spec.FullName.ToLowerInvariant())
				.Distinct()
				.Order(StringComparer.Ordinal)
				.ToList();

			List<LockedPackage> locked = lockFile.VendorFor(realm).ToList();
			List<string> direct = locked.Where(package => package.Direct).Select(package => package.Name.ToLowerInvariant()).Distinct().Order(StringComparer.Ordinal).ToList();

			if (!wanted.SequenceEqual(direct))
			{
				throw new CraterException($"{Fmt.Val(Manifest.VendorField(realm))} does not match {Fmt.File(LockFile.FileName)}. {install}.");
			}

			if (locked.Count == 0)
			{
				continue;
			}

			string folderName = project.Config.VendorFolder(realm);
			string folder = project.Abs(folderName);
			HashSet<string> installed = PackageLayout.ReadMarker(folder).Keys.Where(key => !key.StartsWith('#')).ToHashSet();

			if (!installed.SetEquals(locked.Select(package => package.Id)) || !File.Exists(Path.Combine(folder, PackageLayout.VendorMarkerFile)))
			{
				throw new CraterException($"{Fmt.File(folderName)} does not hold the vendored versions from {Fmt.File(LockFile.FileName)}. {install}.");
			}

			foreach (string file in Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories))
			{
				string relative = Path.GetRelativePath(folder, file).Replace('\\', '/');

				if (relative != PackageLayout.MarkerFile)
				{
					sources[$"{folderName}/{relative}"] = file;
				}
			}
		}
	}

	static void AddRootDocuments(Project project, Dictionary<string, string> sources)
	{
		List<Pattern> documents = RootDocuments.Select(Pattern.Create).ToList();

		foreach (string file in Directory.EnumerateFiles(project.Directory))
		{
			string name = Path.GetFileName(file);

			if (documents.Any(pattern => pattern.Excludes(name)) && !sources.Keys.Any(key => key.Equals(name, StringComparison.OrdinalIgnoreCase)))
			{
				sources[name] = file;
			}
		}
	}

	static void CollectFiles(string root, string directory, IgnoreRules rules, List<string> files, bool gitignore)
	{
		foreach (string file in Directory.EnumerateFiles(directory))
		{
			string relative = Path.GetRelativePath(root, file).Replace('\\', '/');

			if (!IgnoreRules.FileNames.Contains(Path.GetFileName(file)) && !rules.IsIgnored(relative, isDirectory: false))
			{
				files.Add(relative);
			}
		}

		foreach (string child in Directory.EnumerateDirectories(directory))
		{
			string relative = Path.GetRelativePath(root, child).Replace('\\', '/');

			if (!rules.IsIgnored(relative, isDirectory: true))
			{
				CollectFiles(root, child, gitignore ? rules.With(root, child) : rules, files, gitignore);
			}
		}
	}

	public static void Extract(byte[] archiveData, string targetDirectory, string packageId)
	{
		using ZipArchive archive = new(new MemoryStream(archiveData), ZipArchiveMode.Read);

		Extract(archive, targetDirectory, packageId, null);
	}

	public static void Extract(ZipArchive archive, string targetDirectory, string packageId, Func<string, bool>? include)
	{
		string root = Path.GetFullPath(targetDirectory) + Path.DirectorySeparatorChar;

		Directory.CreateDirectory(root);

		foreach (ZipArchiveEntry entry in archive.Entries)
		{
			string destination = Path.GetFullPath(Path.Combine(root, entry.FullName));

			if (!destination.StartsWith(root, StringComparison.OrdinalIgnoreCase))
			{
				throw new CraterException($"{Fmt.Pkg(packageId)}: archive contains an unsafe path {Fmt.File(entry.FullName)}");
			}

			bool isDirectory = entry.FullName.EndsWith('/') || entry.FullName.EndsWith('\\');

			if (isDirectory)
			{
				if (include is null)
				{
					Directory.CreateDirectory(destination);
				}

				continue;
			}

			if (include is not null && !include(entry.FullName.Replace('\\', '/')))
			{
				continue;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(destination)!);
			entry.ExtractToFile(destination, overwrite: true);
		}
	}

	public static List<string> ListEntries(byte[] archiveData)
	{
		using ZipArchive archive = new(new MemoryStream(archiveData), ZipArchiveMode.Read);

		return archive.Entries.Select(entry => entry.FullName.Replace('\\', '/')).ToList();
	}

	public static string? ReadText(byte[] archiveData, string entryName, int maxBytes = 512 * 1024)
	{
		byte[]? data = ReadBytes(archiveData, entryName, maxBytes);

		return data is null ? null : new StreamReader(new MemoryStream(data)).ReadToEnd();
	}

	public static byte[]? ReadBytes(byte[] archiveData, string entryName, int maxBytes)
	{
		using ZipArchive archive = new(new MemoryStream(archiveData), ZipArchiveMode.Read);

		string wanted = Links.NormalizeRelative(entryName);
		ZipArchiveEntry? entry = archive.Entries.FirstOrDefault(
			item => item.FullName.Replace('\\', '/').Equals(wanted, StringComparison.OrdinalIgnoreCase));

		if (entry is null || entry.Length > maxBytes)
		{
			return null;
		}

		using Stream stream = entry.Open();
		using MemoryStream buffer = new();

		stream.CopyTo(buffer);

		return buffer.ToArray();
	}

	public static readonly string[] IconExtensions = [".png", ".jpg", ".jpeg", ".webp", ".gif", ".svg"];

	public const int MaxIconBytes = 256 * 1024;

	static void RequireIcon(Project project, string root, List<string> files)
	{
		string? icon = project.Manifest.Icon;

		if (string.IsNullOrWhiteSpace(icon))
		{
			return;
		}

		string relative = Links.NormalizeRelative(icon);

		if (!IconExtensions.Contains(Path.GetExtension(relative), StringComparer.OrdinalIgnoreCase))
		{
			throw new CraterException($"icon = {Fmt.File(icon)}: use a .png, .jpg, .webp, .gif or .svg image.");
		}

		if (!files.Contains(relative))
		{
			throw new CraterException($"icon = {Fmt.File(icon)}: file not found or excluded from the archive.");
		}

		long size = new FileInfo(Path.Combine(root, relative)).Length;

		if (size > MaxIconBytes)
		{
			throw new CraterException($"icon = {Fmt.File(icon)} is {size / 1024} KB. Keep it under {MaxIconBytes / 1024} KB, 256x256 pixels is plenty.");
		}

		if (relative.EndsWith(".svg", StringComparison.OrdinalIgnoreCase) && !SafeSvg.IsSafe(File.ReadAllBytes(Path.Combine(root, relative))))
		{
			throw new CraterException($"icon = {Fmt.File(icon)} contains scripts, event handlers or links to other files. Use a plain SVG or a PNG.");
		}
	}

	static void RequireDefinitions(Project project, string root, List<string> files)
	{
		List<string> packed = files.Where(file => file.EndsWith(DefinitionLookup.Extension, StringComparison.OrdinalIgnoreCase)).ToList();

		foreach (string pattern in project.Manifest.DefinitionPatterns)
		{
			List<string> matched = DefinitionLookup.Match(root, packed, pattern)
				.Where(match => packed.Contains(match, StringComparer.OrdinalIgnoreCase))
				.ToList();

			if (matched.Count == 0)
			{
				throw new CraterException(
					$"path_to_definition_files entry {Fmt.File(pattern)} matches no {DefinitionLookup.Extension} file in the archive.");
			}
		}
	}

	static void RequireImplementationEntry(Project project, List<string> files)
	{
		List<string> interfaces = project.Manifest.RealizedInterfaces ?? [];

		if (interfaces.Count == 0)
		{
			return;
		}

		string main = Links.NormalizeRelative(project.Manifest.PathToMain);
		bool hasEntry = Manifest.EntryFileNames.Any(name =>
			main.Length == 0 ? files.Contains(name) : main == name || main.EndsWith("/" + name) || files.Contains($"{main}/{name}"));

		if (!hasEntry)
		{
			throw new CraterException(
				$"This package implements {string.Join(", ", interfaces.Select(name => Fmt.Pkg(name)))}, so it needs an entry file: set {Fmt.Val("path_to_main")} to an {Fmt.File("init.luau")} or {Fmt.File("main.luau")}, or a folder with one. Implementations cannot be collections of modules.");
		}
	}

	static void RequirePath(string root, List<string> files, string path, string field)
	{
		if (string.IsNullOrWhiteSpace(path))
		{
			return;
		}

		string relative = Links.NormalizeRelative(path);

		if (files.Contains(relative))
		{
			return;
		}

		bool isFolder = Directory.Exists(Path.Combine(root, relative));

		if (isFolder && files.Any(file => file.StartsWith(relative + "/")))
		{
			return;
		}

		throw new CraterException($"{field} = {Fmt.File(path)}: file not found or excluded from the archive.");
	}

	public sealed class Pattern
	{
		readonly Regex regex;
		readonly bool directoryOnly;
		readonly bool anchored;

		Pattern(Regex regex, bool directoryOnly, bool anchored)
		{
			this.regex = regex;
			this.directoryOnly = directoryOnly;
			this.anchored = anchored;
		}

		public static Pattern Create(string glob)
		{
			string text = glob.Trim().Replace('\\', '/');
			bool directoryOnly = text.EndsWith('/');

			text = text.Trim('/');

			if (text.StartsWith("./"))
			{
				text = text[2..];
			}

			string body = Regex.Escape(text)
				.Replace(@"\*\*", "\u0001")
				.Replace(@"\*", "[^/]*")
				.Replace(@"\?", "[^/]")
				.Replace("\u0001", ".*");

			Regex regex = new("^" + body + "$", RegexOptions.IgnoreCase);

			return new Pattern(regex, directoryOnly, text.Contains('/'));
		}

		public bool Excludes(string relative)
		{
			string[] segments = relative.Split('/');

			for (int i = 0; i < segments.Length; i++)
			{
				bool isDirectory = i < segments.Length - 1;

				if (directoryOnly && !isDirectory)
				{
					continue;
				}

				string candidate = anchored ? string.Join('/', segments[..(i + 1)]) : segments[i];

				if (regex.IsMatch(candidate))
				{
					return true;
				}
			}

			return false;
		}
	}
}
