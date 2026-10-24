using System.Text.RegularExpressions;
using Crater.Core;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Registry;

public sealed partial class RegistryChecker(string root)
{
	static readonly HashSet<string> AllowedRootEntries = new(StringComparer.OrdinalIgnoreCase)
	{
		".git", ".github", ".gitignore", ".gitattributes", "_site",
		"README.md", "CONTRIBUTING.md", "LICENSE", "LICENSE.md",
		RegistryInfo.FileName, Catalog.FileName,
	};

	[GeneratedRegex("^[a-z0-9_-]+$")]
	private static partial Regex SegmentRegex();

	public List<string> Errors { get; } = [];
	public List<string> Warnings { get; } = [];
	public int PackageCount { get; private set; }
	public int VersionCount { get; private set; }
	public List<Finding> Findings { get; } = [];
	public int ScannedVersions { get; private set; }
	public List<string> Reviews { get; } = [];

	public void CheckContents()
	{
		foreach (string entry in Directory.EnumerateFileSystemEntries(root).OrderBy(path => path, StringComparer.Ordinal))
		{
			string name = Path.GetFileName(entry);

			if (AllowedRootEntries.Contains(name))
			{
				continue;
			}

			if (File.Exists(entry))
			{
				Warnings.Add($"Unexpected file {Fmt.File(name)} in the registry root");
				continue;
			}

			CheckScope(entry, name);
		}

		string catalogPath = Path.Combine(root, Catalog.FileName);

		if (File.Exists(catalogPath))
		{
			Try(() => Json.ReadFile<Catalog>(catalogPath), Catalog.FileName);
		}
	}

	void CheckScope(string directory, string scope)
	{
		if (!SegmentRegex().IsMatch(scope))
		{
			Errors.Add($"Folder {Fmt.File(scope)} is not a valid scope (use the GitHub login in lowercase: a-z, 0-9, _, -)");
			return;
		}

		foreach (string packageDirectory in Directory.EnumerateDirectories(directory).OrderBy(path => path, StringComparer.Ordinal))
		{
			CheckPackage(packageDirectory, $"{scope}/{Path.GetFileName(packageDirectory)}");
		}

		foreach (string file in Directory.EnumerateFiles(directory))
		{
			Errors.Add($"Unexpected file {Fmt.File(Relative(file))}, packages must be in {Fmt.File($"{scope}/<package>/")}");
		}
	}

	void CheckPackage(string directory, string fullName)
	{
		if (!DependencySpec.IsValidPackageName(fullName))
		{
			Errors.Add($"{Fmt.File(fullName)} is not a valid package name");
			return;
		}

		string indexPath = Path.Combine(directory, RegistryIndex.FileName);

		if (!File.Exists(indexPath))
		{
			Errors.Add($"{Fmt.File(Relative(indexPath))} is missing");
			return;
		}

		RegistryIndex? index = Try(() => Json.ReadFile<RegistryIndex>(indexPath), Relative(indexPath));

		if (index is null)
		{
			return;
		}

		PackageCount++;

		if (index.Name != fullName)
		{
			Errors.Add($"{Fmt.File(Relative(indexPath))}: name is {Fmt.Val(index.Name)}, expected {Fmt.Val(fullName)}");
		}

		HashSet<string> expectedFiles = new(StringComparer.Ordinal) { RegistryIndex.FileName };
		HashSet<string> seenVersions = [];

		foreach (RegistryRelease release in index.Versions)
		{
			string label = $"{fullName}@{release.Version}";

			if (!SemVer.TryParse(release.Version, out SemVer version))
			{
				Errors.Add($"{Fmt.Pkg(label)}: invalid version");
				continue;
			}

			if (!seenVersions.Add(version.ToString()))
			{
				Errors.Add($"{Fmt.Pkg(label)}: listed twice");
				continue;
			}

			VersionCount++;
			expectedFiles.Add(release.Version + ".zip");
			CheckRelease(directory, fullName, release, label);
		}

		foreach (string file in Directory.EnumerateFileSystemEntries(directory))
		{
			if (!expectedFiles.Contains(Path.GetFileName(file)))
			{
				Errors.Add($"Unexpected {Fmt.File(Relative(file))}, it is not listed in {Fmt.File(RegistryIndex.FileName)}");
			}
		}
	}

	void CheckRelease(string directory, string fullName, RegistryRelease release, string label)
	{
		string archivePath = Path.Combine(directory, release.Version + ".zip");

		if (!File.Exists(archivePath))
		{
			Errors.Add($"{Fmt.Pkg(label)}: archive {Fmt.File(Relative(archivePath))} is missing");
			return;
		}

		byte[] archive = File.ReadAllBytes(archivePath);
		string sha = PackageCache.Sha256(archive);

		if (!sha.Equals(release.Sha256, StringComparison.OrdinalIgnoreCase))
		{
			Errors.Add($"{Fmt.Pkg(label)}: sha256 is {Fmt.Val(sha)}, {Fmt.File(RegistryIndex.FileName)} says {Fmt.Val(release.Sha256)}");
		}

		string? manifestText = Try(() => Packer.ReadText(archive, Manifest.FileName), $"{label} archive");

		if (manifestText is null)
		{
			Errors.Add($"{Fmt.Pkg(label)}: archive has no {Fmt.File(Manifest.FileName)}");
			return;
		}

		Manifest? packed = Try(() => Json.Parse<Manifest>(manifestText, $"{label}/{Manifest.FileName}"), label);

		if (packed is null)
		{
			return;
		}

		if (packed.Name != fullName || packed.Version != release.Version)
		{
			Errors.Add($"{Fmt.Pkg(label)}: archive contains {Fmt.Pkg($"{packed.Name}@{packed.Version}")}");
		}

		if (!SameDependencies(packed, release.Manifest))
		{
			Errors.Add($"{Fmt.Pkg(label)}: dependencies in {Fmt.File(RegistryIndex.FileName)} differ from the archive");
		}

		try
		{
			packed.ValidateForPublish();
		}
		catch (CraterException exception)
		{
			Errors.Add($"{Fmt.Pkg(label)}: {exception.Message}");
		}
	}

	static bool SameDependencies(Manifest left, Manifest right)
	{
		foreach (Realm realm in Realms.All)
		{
			if (!left.DepsFor(realm).SequenceEqual(right.DepsFor(realm)))
			{
				return false;
			}
		}

		return left.ImportTo == right.ImportTo && left.PathToMain == right.PathToMain;
	}

	public void CheckChanges(string baseRef, string? author)
	{
		RegistryInfo baseInfo = BaseRegistryInfo(baseRef);
		bool isMaintainer = author is not null && baseInfo.IsMaintainer(author);
		string? ownScope = author?.ToLowerInvariant();

		string diff = Shell.Run("git", ["diff", "--name-status", "--no-renames", $"{baseRef}...HEAD"], root);

		foreach (string line in diff.Split('\n', StringSplitOptions.RemoveEmptyEntries))
		{
			string[] parts = line.Split('\t');

			if (parts.Length < 2)
			{
				continue;
			}

			char status = parts[0][0];
			string path = parts[1].Replace('\\', '/');
			string[] segments = path.Split('/');
			bool isPackageFile = segments.Length == 3 && !segments[0].StartsWith('.');

			if (!isPackageFile)
			{
				if (!isMaintainer)
				{
					Errors.Add($"Only maintainers can change {Fmt.File(path)}");
				}

				continue;
			}

			bool ownFolder = ownScope is null || isMaintainer || segments[0] == ownScope || GitHubMembership.IsPublicMember(segments[0], author!);

			if (!ownFolder)
			{
				Errors.Add(
					$"{Fmt.File(path)} is outside your folder {Fmt.File(ownScope + "/")}. " +
					$"To publish for the organization {Fmt.Val(segments[0])}, make your membership public at {Fmt.File(GitHubMembership.PeopleUrl(segments[0]))}");
			}

			if (!isMaintainer)
			{
				CheckImmutable(baseRef, status, path, segments[2]);
			}

			if (status == 'A' && segments[2].EndsWith(".zip", StringComparison.OrdinalIgnoreCase))
			{
				ScanNewVersion(path, segments);
			}
		}
	}

	void ScanNewVersion(string path, string[] segments)
	{
		string archivePath = Path.Combine(root, path);

		if (!File.Exists(archivePath))
		{
			return;
		}

		string packageId = $"{segments[0]}/{segments[1]}@{Path.GetFileNameWithoutExtension(segments[2])}";
		List<Finding> found = Try(() => PackageScanner.ScanArchive(File.ReadAllBytes(archivePath), packageId), path) ?? [];

		ScannedVersions++;
		Findings.AddRange(found);

		foreach (Finding blocked in found.Where(finding => finding.Severity == Severity.Blocked))
		{
			Errors.Add($"{Fmt.Pkg(packageId)}: {Fmt.File(blocked.File)} is an {blocked.Description}");
		}

		CheckScriptsList(archivePath, segments, packageId);
		DescribeChanges(archivePath, segments, packageId);
	}

	void DescribeChanges(string archivePath, string[] segments, string packageId)
	{
		string packageDirectory = Path.Combine(root, segments[0], segments[1]);
		SemVer current = SemVer.Parse(Path.GetFileNameWithoutExtension(segments[2]));

		string? previous = Directory.EnumerateFiles(packageDirectory, "*.zip")
			.Select(Path.GetFileNameWithoutExtension)
			.OfType<string>()
			.Where(version => SemVer.TryParse(version, out SemVer parsed) && parsed < current)
			.MaxBy(SemVer.Parse);

		byte[]? previousArchive = previous is null ? null : File.ReadAllBytes(Path.Combine(packageDirectory, previous + ".zip"));
		List<FileChange>? changes = Try(() => PackageDiff.Compare(File.ReadAllBytes(archivePath), previousArchive), archivePath);

		if (changes is not null)
		{
			int used = Reviews.Sum(review => review.Length);

			Reviews.Add(PackageDiff.Markdown(packageId, previous, changes, Math.Max(20_000, 600_000 - used)));
		}
	}

	void CheckScriptsList(string archivePath, string[] segments, string packageId)
	{
		string indexPath = Path.Combine(root, segments[0], segments[1], RegistryIndex.FileName);
		string version = Path.GetFileNameWithoutExtension(segments[2]);
		RegistryRelease? release = Try(() => Json.ReadFile<RegistryIndex>(indexPath), indexPath)?.Versions.FirstOrDefault(item => item.Version == version);

		if (release is null)
		{
			return;
		}

		List<string>? actual = Try(() => Crater.Core.PackageScripts.InArchive(File.ReadAllBytes(archivePath), release.Manifest, packageId), archivePath);

		if (actual is null)
		{
			return;
		}

		if (release.Scripts is null)
		{
			Errors.Add($"{Fmt.Pkg(packageId)}: index.json has no \"scripts\" list. Publish it with a current crater ({Fmt.Cmd("crater self-update")}).");
			return;
		}

		if (!release.Scripts.Order(StringComparer.Ordinal).SequenceEqual(actual.Order(StringComparer.Ordinal)))
		{
			string listed = release.Scripts.Count == 0 ? "none" : string.Join(", ", release.Scripts);
			string found = actual.Count == 0 ? "none" : string.Join(", ", actual);

			Errors.Add($"{Fmt.Pkg(packageId)}: index.json lists scripts {Fmt.Val(listed)}, but the archive has {Fmt.Val(found)}");
		}

		if (actual.Count > 0)
		{
			Out.Warn($"{Fmt.Pkg(packageId)} runs scripts in the games that install it: {string.Join(", ", actual)}");
		}
	}

	void CheckImmutable(string baseRef, char status, string path, string fileName)
	{
		bool isArchive = fileName.EndsWith(".zip", StringComparison.OrdinalIgnoreCase);

		if (isArchive && status is 'M' or 'D')
		{
			Errors.Add($"{Fmt.File(path)}: published archives cannot be changed or deleted");
			return;
		}

		if (fileName != RegistryIndex.FileName)
		{
			return;
		}

		if (status == 'D')
		{
			Errors.Add($"{Fmt.File(path)}: a package index cannot be deleted");
			return;
		}

		if (status != 'M')
		{
			return;
		}

		ShellResult before = Shell.Capture("git", ["show", $"{baseRef}:{path}"], root);

		if (!before.Success)
		{
			return;
		}

		RegistryIndex oldIndex = Json.Parse<RegistryIndex>(before.Output, $"{baseRef}:{path}");
		RegistryIndex newIndex = Json.ReadFile<RegistryIndex>(Path.Combine(root, path));
		Dictionary<string, RegistryRelease> current = newIndex.Versions
			.GroupBy(release => release.Version)
			.ToDictionary(group => group.Key, group => group.First());

		foreach (RegistryRelease old in oldIndex.Versions)
		{
			if (!current.TryGetValue(old.Version, out RegistryRelease? now))
			{
				Errors.Add($"{Fmt.File(path)}: version {Fmt.Val(old.Version)} was removed");
				continue;
			}

			bool changed = now.Sha256 != old.Sha256 || Json.Serialize(now.Manifest) != Json.Serialize(old.Manifest);

			if (changed)
			{
				Errors.Add($"{Fmt.File(path)}: version {Fmt.Val(old.Version)} was modified, publish a new version instead");
			}
		}
	}

	RegistryInfo BaseRegistryInfo(string baseRef)
	{
		ShellResult result = Shell.Capture("git", ["show", $"{baseRef}:{RegistryInfo.FileName}"], root);

		if (!result.Success)
		{
			return new RegistryInfo();
		}

		return Json.Parse<RegistryInfo>(result.Output, $"{baseRef}:{RegistryInfo.FileName}");
	}

	T? Try<T>(Func<T?> action, string label) where T : class
	{
		try
		{
			return action();
		}
		catch (Exception exception) when (exception is CraterException or InvalidDataException or IOException)
		{
			Errors.Add($"{Fmt.File(label)}: {exception.Message}");
			return null;
		}
	}

	string Relative(string path) => Path.GetRelativePath(root, path).Replace('\\', '/');
}
