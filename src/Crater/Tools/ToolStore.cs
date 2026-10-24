using System.Formats.Tar;
using System.IO.Compression;
using System.Runtime.InteropServices;
using System.Text.Json;
using Crater.Util;

namespace Crater.Tools;

public static class ToolStore
{
	static readonly string[] SkippedExtensions = [".sha256", ".sig", ".asc", ".pdb", ".vsix", ".txt", ".json", ".deb", ".rpm", ".msi", ".pkg", ".dmg"];

	public static string Home
	{
		get
		{
			string? custom = Environment.GetEnvironmentVariable("CRATER_HOME");

			if (!string.IsNullOrWhiteSpace(custom))
			{
				return custom;
			}

			return OperatingSystem.IsWindows()
				? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "crater")
				: Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".crater");
		}
	}

	public static string ShimDirectory => Path.Combine(Home, "bin");

	static string ToolsDirectory => Path.Combine(Home, "tools");

	static string ExecutableName(string name) => OperatingSystem.IsWindows() ? name + ".exe" : name;

	public static string ExecutablePath(string name, ToolSpec spec)
	{
		return Path.Combine(ToolsDirectory, spec.Owner.ToLowerInvariant(), spec.Repo.ToLowerInvariant(), spec.Version, ExecutableName(name));
	}

	public static bool IsInstalled(string name, ToolSpec spec) => File.Exists(ExecutablePath(name, spec));

	public static async Task<string> LatestVersionAsync(string repository)
	{
		JsonElement release = await GitHubApi.GetAsync($"repos/{repository}/releases/latest")
			?? throw new CraterException($"{Fmt.Val(repository)} has no releases on GitHub.");

		return release.GetProperty("tag_name").GetString()!.TrimStart('v');
	}

	public static async Task<string> InstallAsync(string name, ToolSpec spec)
	{
		string target = ExecutablePath(name, spec);

		if (File.Exists(target))
		{
			return target;
		}

		using Spinner spinner = new($"Installing {name} {spec.Version}");

		JsonElement release = await GitHubApi.GetAsync($"repos/{spec.Slug}/releases/tags/v{spec.Version}")
			?? await GitHubApi.GetAsync($"repos/{spec.Slug}/releases/tags/{spec.Version}")
			?? throw new CraterException($"{Fmt.Val(spec.Slug)} has no release {Fmt.Val(spec.Version)}.");

		List<(string Name, string Url)> assets = release.GetProperty("assets").EnumerateArray()
			.Select(asset => (asset.GetProperty("name").GetString()!, asset.GetProperty("browser_download_url").GetString()!))
			.ToList();

		(string Name, string Url) chosen = PickAsset(assets)
			?? throw new CraterException(
				$"{Fmt.Val(spec.ToString())} has no build for {RuntimeInformation.OSDescription} {RuntimeInformation.OSArchitecture}. " +
				$"Assets: {string.Join(", ", assets.Select(asset => asset.Name))}");

		byte[] data = await Http.GetBytesOrNullAsync(chosen.Url, (done, total) => spinner.Text = $"Installing {name} {spec.Version} ({done / 1048576.0:0.0} MB)")
			?? throw new CraterException($"Could not download {Fmt.File(chosen.Url)}");

		byte[] executable = ExtractExecutable(chosen.Name, data, name, spec.Repo)
			?? throw new CraterException($"{Fmt.File(chosen.Name)} does not contain a {Fmt.Val(name)} executable.");

		Directory.CreateDirectory(Path.GetDirectoryName(target)!);

		string temporary = $"{target}.{Guid.NewGuid():N}.tmp";

		await File.WriteAllBytesAsync(temporary, executable);

		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute | UnixFileMode.GroupRead | UnixFileMode.GroupExecute | UnixFileMode.OtherRead | UnixFileMode.OtherExecute);
		}

		File.Move(temporary, target, overwrite: true);

		return target;
	}

	public static (string Name, string Url)? PickAsset(IEnumerable<(string Name, string Url)> assets)
	{
		string system = OperatingSystem.IsWindows() ? "windows" : OperatingSystem.IsMacOS() ? "macos" : "linux";
		bool arm = RuntimeInformation.OSArchitecture == Architecture.Arm64;

		return PickAsset(assets, system, arm);
	}

	public static (string Name, string Url)? PickAsset(IEnumerable<(string Name, string Url)> assets, string system, bool arm)
	{
		string[] systemWords = system switch
		{
			"windows" => ["windows", "win64", "win32", "win", "msvc"],
			"macos" => ["macos", "darwin", "osx", "apple", "mac"],
			_ => ["linux", "gnu", "musl"],
		};

		string[] otherSystems = system switch
		{
			"windows" => ["linux", "macos", "darwin", "osx", "apple"],
			"macos" => ["windows", "win64", "win32", "linux"],
			_ => ["windows", "win64", "win32", "macos", "darwin", "osx", "apple"],
		};

		int best = int.MinValue;
		(string Name, string Url)? chosen = null;

		foreach ((string name, string url) in assets)
		{
			string lower = name.ToLowerInvariant();
			string[] tokens = lower.Split(['-', '_', '.', ' '], StringSplitOptions.RemoveEmptyEntries);

			if (SkippedExtensions.Any(lower.EndsWith) || tokens.Any(otherSystems.Contains) || !tokens.Any(systemWords.Contains))
			{
				continue;
			}

			bool isArm = lower.Contains("aarch64") || lower.Contains("arm64");
			bool isX64 = lower.Contains("x86_64") || lower.Contains("x64") || lower.Contains("amd64") || tokens.Contains("win64");
			int score = 0;

			score += arm ? (isArm ? 20 : isX64 && system is "macos" or "windows" ? 5 : isX64 ? -100 : 0) : (isX64 ? 20 : isArm ? -100 : 0);
			score += lower.EndsWith(".zip") ? 3 : lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") ? 2 : 0;
			score -= tokens.Contains("musl") || tokens.Contains("light") ? 1 : 0;

			if (score > best)
			{
				best = score;
				chosen = (name, url);
			}
		}

		return best < -50 ? null : chosen;
	}

	static byte[]? ExtractExecutable(string assetName, byte[] data, string name, string repo)
	{
		string lower = assetName.ToLowerInvariant();
		List<(string Path, byte[] Data)> files = lower.EndsWith(".zip") ? FromZip(data)
			: lower.EndsWith(".tar.gz") || lower.EndsWith(".tgz") ? FromTar(data)
			: [(assetName, data)];

		string[] wanted = [name.ToLowerInvariant(), repo.ToLowerInvariant()];

		bool IsExecutable((string Path, byte[] Data) file)
		{
			string fileName = System.IO.Path.GetFileName(file.Path).ToLowerInvariant();

			return OperatingSystem.IsWindows() ? fileName.EndsWith(".exe") : !fileName.Contains('.') || fileName == lower;
		}

		List<(string Path, byte[] Data)> executables = files.Where(IsExecutable).ToList();

		(string Path, byte[] Data) match = executables.FirstOrDefault(file =>
			wanted.Contains(System.IO.Path.GetFileNameWithoutExtension(file.Path).ToLowerInvariant()));

		if (match.Data is not null)
		{
			return match.Data;
		}

		return executables.Count == 1 ? executables[0].Data : null;
	}

	static List<(string Path, byte[] Data)> FromZip(byte[] data)
	{
		using ZipArchive archive = new(new MemoryStream(data), ZipArchiveMode.Read);

		return archive.Entries
			.Where(entry => !entry.FullName.EndsWith('/'))
			.Select(entry =>
			{
				using Stream stream = entry.Open();
				using MemoryStream buffer = new();
				stream.CopyTo(buffer);

				return (entry.FullName, buffer.ToArray());
			})
			.ToList();
	}

	static List<(string Path, byte[] Data)> FromTar(byte[] data)
	{
		using GZipStream gzip = new(new MemoryStream(data), CompressionMode.Decompress);
		using TarReader reader = new(gzip);
		List<(string Path, byte[] Data)> files = [];

		while (reader.GetNextEntry() is TarEntry entry)
		{
			if (entry.EntryType is not (TarEntryType.RegularFile or TarEntryType.V7RegularFile) || entry.DataStream is null)
			{
				continue;
			}

			using MemoryStream buffer = new();
			entry.DataStream.CopyTo(buffer);
			files.Add((entry.Name, buffer.ToArray()));
		}

		return files;
	}

	public static void WriteShim(string name)
	{
		string crater = Environment.ProcessPath ?? "crater";
		bool runsOnDotnet = Path.GetFileNameWithoutExtension(crater).Equals("dotnet", StringComparison.OrdinalIgnoreCase);
		string command = runsOnDotnet ? "crater" : crater;

		Directory.CreateDirectory(ShimDirectory);

		string unix = Path.Combine(ShimDirectory, name);

		File.WriteAllText(unix, $"#!/bin/sh\nexec \"{command.Replace('\\', '/')}\" tool run {name} \"$@\"\n");

		if (OperatingSystem.IsWindows())
		{
			File.WriteAllText(Path.Combine(ShimDirectory, name + ".cmd"), $"@echo off\r\n\"{command}\" tool run {name} %*\r\nexit /b %ERRORLEVEL%\r\n");
		}
		else
		{
			File.SetUnixFileMode(unix, File.GetUnixFileMode(unix) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
		}
	}
}
