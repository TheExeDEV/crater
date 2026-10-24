using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Crater.Cli;
using Crater.Core;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static partial class ReviewCommand
{
	const int TerminalLinesPerFile = 120;

	[GeneratedRegex(@"^(?<scope>[^/]+)/(?<name>[^/]+)/(?<version>[^/]+)\.zip$")]
	private static partial Regex ArchivePath();

	[GeneratedRegex(@"/pull/(?<number>\d+)")]
	private static partial Regex PullUrl();

	public static async Task<int> RunAsync(Args args)
	{
		string request = args.First ?? throw new CraterException($"Usage: {Fmt.Cmd("crater review <pull request number or link>")}");
		GitHubRepository registry = RegistryRepository(args, request);
		int number = PullUrl().Match(request) is { Success: true } match ? int.Parse(match.Groups["number"].Value) : int.TryParse(request.TrimStart('#'), out int parsed) ? parsed : throw new CraterException($"{Fmt.Val(request)} is not a pull request number or link.");

		await RequireMaintainerAsync(registry);

		JsonElement pull = await GitHubApi.GetAsync($"repos/{registry.Slug}/pulls/{number}")
			?? throw new CraterException($"Pull request #{number} not found in {Fmt.Val(registry.Slug)}.");

		string headRepository = pull.GetProperty("head").GetProperty("repo").GetProperty("full_name").GetString()!;
		string headSha = pull.GetProperty("head").GetProperty("sha").GetString()!;
		string baseBranch = pull.GetProperty("base").GetProperty("ref").GetString()!;

		Out.Heading($"#{number} {pull.GetProperty("title").GetString()}");
		Out.Info($"by {Fmt.Val(pull.GetProperty("user").GetProperty("login").GetString()!)}, {pull.GetProperty("state").GetString()}, {Fmt.File(pull.GetProperty("html_url").GetString()!)}");

		List<(string Path, string Status)> files = await ChangedFilesAsync(registry, number);
		List<string> archives = files.Where(file => file.Status == "added" && ArchivePath().IsMatch(file.Path)).Select(file => file.Path).ToList();
		string root = Path.Combine(CraterPaths.Cache, "review", registry.Slug.Replace('/', '_'), $"pr-{number}");

		if (Directory.Exists(root))
		{
			Directory.Delete(root, recursive: true);
		}

		Directory.CreateDirectory(root);

		foreach ((string path, string status) in files.Where(file => !ArchivePath().IsMatch(file.Path) && !file.Path.EndsWith("index.json", StringComparison.Ordinal)))
		{
			Out.Warn($"Also changes {Fmt.File(path)} ({status})");
		}

		if (archives.Count == 0)
		{
			Out.Info("No new package versions in this pull request.");
			return 0;
		}

		foreach (string archivePath in archives)
		{
			await ReviewArchiveAsync(registry, headRepository, headSha, baseBranch, archivePath, root);
		}

		Out.Info("");
		Out.Ok($"Unpacked to {Fmt.File(root)}");
		Out.Dim("  Each version has new/ (this pull request), old/ (the previous version) and changes.diff.");

		if (args.Has("open"))
		{
			Open(root);
		}
		else
		{
			Out.Dim($"  Add {Fmt.Cmd("--open")} to open the folder in VS Code.");
		}

		return 0;
	}

	static async Task RequireMaintainerAsync(GitHubRepository registry)
	{
		string login = await GitHubApi.RequireLoginAsync();
		bool isMaintainer = registry.Owner.Equals(login, StringComparison.OrdinalIgnoreCase)
			|| await GitHubApi.HasPermissionAsync(registry.Slug, "push");

		if (!isMaintainer)
		{
			throw new CraterException(
				$"{Fmt.Cmd("crater review")} is for the maintainers of {Fmt.Val(registry.Slug)}, and {Fmt.Val(login)} cannot merge there.");
		}
	}

	static GitHubRepository RegistryRepository(Args args, string request)
	{
		Match link = Regex.Match(request, @"github\.com/(?<owner>[^/]+)/(?<repo>[^/]+)/pull/");

		if (link.Success)
		{
			return new GitHubRepository(link.Groups["owner"].Value, link.Groups["repo"].Value, "main");
		}

		string location = args.Value("registry")
			?? TryProjectRegistry()
			?? BuildInfo.DefaultRegistry;

		return GitHubRepository.FromRegistry(Registries.Expand(location))
			?? throw new CraterException($"{Fmt.Val(location)} is not a GitHub registry. Pass a pull request link instead.");
	}

	static string? TryProjectRegistry()
	{
		try
		{
			return Common.LoadProject().RegistryLocation;
		}
		catch (CraterException)
		{
			return null;
		}
	}

	static async Task<List<(string Path, string Status)>> ChangedFilesAsync(GitHubRepository registry, int number)
	{
		List<(string, string)> files = [];

		for (int page = 1; page <= 30; page++)
		{
			JsonElement? batch = await GitHubApi.GetAsync($"repos/{registry.Slug}/pulls/{number}/files?per_page=100&page={page}");

			if (batch is not { ValueKind: JsonValueKind.Array } list || list.GetArrayLength() == 0)
			{
				break;
			}

			files.AddRange(list.EnumerateArray().Select(file => (file.GetProperty("filename").GetString()!, file.GetProperty("status").GetString()!)));
		}

		return files;
	}

	static async Task ReviewArchiveAsync(GitHubRepository registry, string headRepository, string headSha, string baseBranch, string archivePath, string root)
	{
		Match parts = ArchivePath().Match(archivePath);
		string name = $"{parts.Groups["scope"].Value}/{parts.Groups["name"].Value}";
		string version = parts.Groups["version"].Value;
		string id = $"{name}@{version}";

		Out.Info("");
		Out.Heading(id);

		byte[] archive = await DownloadAsync($"https://raw.githubusercontent.com/{headRepository}/{headSha}/{archivePath}");
		string? previous = await PreviousVersionAsync(registry, baseBranch, name, version);
		byte[]? previousArchive = previous is null ? null : await DownloadAsync($"{registry.RawBase(baseBranch)}/{name}/{previous}.zip");

		string directory = Path.Combine(root, id.Replace('/', '_'));

		Packer.Extract(archive, Path.Combine(directory, "new"), id);

		if (previousArchive is not null)
		{
			Packer.Extract(previousArchive, Path.Combine(directory, "old"), $"{name}@{previous}");
		}

		List<Finding> findings = PackageScanner.ScanArchive(archive, id);
		List<string> scripts = PackageScripts.InArchive(archive, PackageFiles.ManifestIn(Path.Combine(directory, "new")), id);
		List<FileChange> changes = PackageDiff.Compare(archive, previousArchive);

		Out.Info(previous is null ? "First version of this package." : $"Compared with {Fmt.Val(previous)}.");

		if (scripts.Count > 0)
		{
			Out.Warn($"Runs scripts in the games that install it: {PackageScripts.Describe(scripts)}");
		}

		if (findings.Count > 0)
		{
			ScanReport.Print(findings);
		}
		else
		{
			Out.Ok("The scanner found nothing suspicious. Still read the code.");
		}

		PrintChanges(changes, showCode: previous is not null);
		await File.WriteAllTextAsync(Path.Combine(directory, "changes.diff"), FullDiff(changes, previous ?? "nothing", version));
	}

	static async Task<string?> PreviousVersionAsync(GitHubRepository registry, string baseBranch, string name, string version)
	{
		string? text = await Http.GetTextOrNullAsync($"{registry.RawBase(baseBranch)}/{name}/{RegistryIndex.FileName}", GitHubAuth.Headers());

		if (text is null || !SemVer.TryParse(version, out SemVer current))
		{
			return null;
		}

		return Json.Parse<RegistryIndex>(text, RegistryIndex.FileName).Normalize(name).Versions
			.Where(release => release.SemVer < current)
			.MaxBy(release => release.SemVer)?.Version;
	}

	static async Task<byte[]> DownloadAsync(string url)
	{
		return await Http.GetBytesOrNullAsync(url, null, GitHubAuth.Headers())
			?? throw new CraterException($"Could not download {Fmt.File(url)}.");
	}

	static void PrintChanges(List<FileChange> changes, bool showCode)
	{
		foreach (FileChange change in changes.Where(change => change.Kind != ChangeKind.Same))
		{
			string mark = change.Kind switch
			{
				ChangeKind.Added => Fmt.Good("added  "),
				ChangeKind.Removed => Fmt.Bad("removed"),
				_ => Fmt.Val("changed"),
			};

			Out.Info($"  {mark} {Fmt.File(change.Path)}");
		}

		if (!showCode)
		{
			Out.Dim("  First version: read the code in new/.");
			return;
		}

		foreach (FileChange change in changes.Where(change => change.Kind is ChangeKind.Added or ChangeKind.Changed && change.NewText is not null))
		{
			List<string> lines = PackageDiff.UnifiedDiff(change.OldText ?? "", change.NewText!);

			Out.Info("");
			Out.Info(Fmt.File(change.Path));

			foreach (string line in lines.Take(TerminalLinesPerFile))
			{
				Out.Info(line.StartsWith('+') ? Fmt.Good(Escape(line)) : line.StartsWith('-') ? Fmt.Bad(Escape(line)) : Escape(line));
			}

			if (lines.Count > TerminalLinesPerFile)
			{
				Out.Dim($"  ... {lines.Count - TerminalLinesPerFile} more lines in changes.diff");
			}
		}
	}

	[GeneratedRegex(@"\[(?=(?:p|f|c|v|g|r|/)\])")]
	private static partial Regex MarkupStart();

	static string Escape(string line) => MarkupStart().Replace(line, "[​");

	static string FullDiff(List<FileChange> changes, string previous, string version)
	{
		StringBuilder builder = new();

		foreach (FileChange change in changes.Where(change => change.Kind != ChangeKind.Same))
		{
			builder.AppendLine($"--- {(change.Kind == ChangeKind.Added ? "/dev/null" : $"old/{change.Path}")} ({previous})");
			builder.AppendLine($"+++ {(change.Kind == ChangeKind.Removed ? "/dev/null" : $"new/{change.Path}")} ({version})");

			if (change.OldText is null && change.NewText is null)
			{
				builder.AppendLine("binary file");
				continue;
			}

			foreach (string line in PackageDiff.UnifiedDiff(change.OldText ?? "", change.NewText ?? ""))
			{
				builder.AppendLine(line);
			}
		}

		return builder.ToString();
	}

	static void Open(string folder)
	{
		try
		{
			ProcessStartInfo info = OperatingSystem.IsWindows()
				? new ProcessStartInfo("cmd.exe", $"/d /c code \"{folder}\"") { UseShellExecute = false, CreateNoWindow = true }
				: new ProcessStartInfo("code", folder) { UseShellExecute = false };

			Process.Start(info)?.Dispose();
		}
		catch (Exception)
		{
			Out.Info($"Open {Fmt.File(folder)} yourself.");
		}
	}
}
