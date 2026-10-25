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
		ProjectConfig.FileName,
		ProjectConfig.LegacyFileName,
	];

	static readonly DateTimeOffset FixedTime = new(1980, 1, 1, 0, 0, 0, TimeSpan.Zero);

	public static byte[] Pack(Project project, out List<string> files)
	{
		List<Pattern> patterns = DefaultExclude
			.Concat(project.Config.ConfiguredFolders().Select(pair => pair.Folder + "/"))
			.Concat(project.Manifest.Exclude ?? [])
			.Select(Pattern.Create)
			.ToList();

		List<string> collected = [];

		CollectFiles(project.Directory, project.Directory, new IgnoreRules().With(project.Directory, project.Directory), collected);

		files = collected
			.Where(relative => !patterns.Any(pattern => pattern.Excludes(relative)))
			.OrderBy(relative => relative, StringComparer.Ordinal)
			.ToList();

		List<Pattern> allowed = (project.Manifest.AllowFiles ?? []).Select(Pattern.Create).ToList();
		List<(string Path, ProgramKind Kind)> programs = files
			.Select(relative => (Path: relative, Kind: Executables.Classify(Path.Combine(project.Directory, relative), relative)))
			.Where(item => item.Kind == ProgramKind.Binary || (item.Kind == ProgramKind.Script && !allowed.Any(pattern => pattern.Excludes(item.Path))))
			.ToList();

		if (programs.Count > 0)
		{
			files = files.Except(programs.Select(item => item.Path)).ToList();

			string shown = string.Join(", ", programs.Take(5).Select(item => Fmt.File(item.Path)));
			string more = programs.Count > 5 ? $" and {programs.Count - 5} more" : "";

			Out.Warn($"Left out {Fmt.Plural(programs.Count, "executable file or archive", "executable files or archives")}, packages do not ship programs: {shown}{more}");

			if (programs.Any(item => item.Kind == ProgramKind.Script))
			{
				Out.Dim($"  To ship a script on purpose, list it in {Fmt.Val("\"allow_files\"")} in {Fmt.File(Manifest.FileName)}, for example {Fmt.Val("\"allow_files\": [\"scripts/setup.sh\"]")}. Programs cannot be allowed.");
			}
		}

		if (!files.Contains(Manifest.FileName))
		{
			throw new CraterException($"{Fmt.File(Manifest.FileName)} is excluded from the archive by the exclude patterns.");
		}

		RequirePath(project, files, project.Manifest.PathToMain, "path_to_main");
		RequireIcon(project, files);
		RequireDefinitions(project, files);
		DocLanguage.Require(project.Directory, files);

		using MemoryStream stream = new();

		using (ZipArchive archive = new(stream, ZipArchiveMode.Create, leaveOpen: true))
		{
			foreach (string relative in files)
			{
				ZipArchiveEntry entry = archive.CreateEntry(relative, CompressionLevel.Optimal);
				entry.LastWriteTime = FixedTime;

				using Stream destination = entry.Open();
				using FileStream source = File.OpenRead(Path.Combine(project.Directory, relative));

				source.CopyTo(destination);
			}
		}

		return stream.ToArray();
	}

	static void CollectFiles(string root, string directory, IgnoreRules rules, List<string> files)
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
				CollectFiles(root, child, rules.With(root, child), files);
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

	static void RequireIcon(Project project, List<string> files)
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

		long size = new FileInfo(Path.Combine(project.Directory, relative)).Length;

		if (size > MaxIconBytes)
		{
			throw new CraterException($"icon = {Fmt.File(icon)} is {size / 1024} KB. Keep it under {MaxIconBytes / 1024} KB, 256x256 pixels is plenty.");
		}
	}

	static void RequireDefinitions(Project project, List<string> files)
	{
		List<string> packed = files.Where(file => file.EndsWith(DefinitionLookup.Extension, StringComparison.OrdinalIgnoreCase)).ToList();

		foreach (string pattern in project.Manifest.DefinitionPatterns)
		{
			List<string> matched = DefinitionLookup.Match(project.Directory, packed, pattern)
				.Where(match => packed.Contains(match, StringComparer.OrdinalIgnoreCase))
				.ToList();

			if (matched.Count == 0)
			{
				throw new CraterException(
					$"path_to_definition_files entry {Fmt.File(pattern)} matches no {DefinitionLookup.Extension} file in the archive.");
			}
		}
	}

	static void RequirePath(Project project, List<string> files, string path, string field)
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

		bool isFolder = Directory.Exists(Path.Combine(project.Directory, relative));

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
