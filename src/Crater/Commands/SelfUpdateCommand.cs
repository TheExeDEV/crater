using System.Runtime.InteropServices;
using System.Text.Json.Serialization;
using Crater.Cli;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class SelfUpdateCommand
{
	sealed class Release
	{
		[JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
		[JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
		[JsonPropertyName("assets")] public List<Asset> Assets { get; set; } = [];
	}

	sealed class Asset
	{
		[JsonPropertyName("name")] public string Name { get; set; } = "";
		[JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
	}

	public static async Task<string> LatestVersionAsync()
	{
		return (await LatestReleaseAsync(UpdateRepository())).TagName;
	}

	public static async Task RunAsync(Args args)
	{
		string repository = UpdateRepository();
		Release release;

		using (new Spinner("Checking for a new crater version"))
		{
			release = await LatestReleaseAsync(repository);
		}

		SemVer current = SemVer.Parse(BuildInfo.Version);

		if (!SemVer.TryParse(release.TagName, out SemVer latest))
		{
			throw new CraterException($"Latest release tag {Fmt.Val(release.TagName)} of {Fmt.Val(repository)} is not a version.");
		}

		if (latest <= current)
		{
			Out.Ok($"crater {Fmt.Val(current)} is up to date");
			return;
		}

		Out.Info($"New version {Fmt.Good(latest)} is available (current {Fmt.Val(current)}): {Fmt.File(release.HtmlUrl)}");

		if (args.Has("check"))
		{
			return;
		}

		string? executable = Environment.ProcessPath;
		bool runsOnDotnet = executable is null || Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase);

		if (runsOnDotnet)
		{
			Out.Info($"crater runs through {Fmt.Cmd("dotnet")}. Update it with {Fmt.Cmd("dotnet tool update -g crater")} or download the release.");
			return;
		}

		string assetName = AssetName();
		Asset asset = release.Assets.FirstOrDefault(item => item.Name.Equals(assetName, StringComparison.OrdinalIgnoreCase))
			?? throw new CraterException($"Release {Fmt.Val(release.TagName)} has no {Fmt.File(assetName)} for this platform.");

		Prompter prompt = new(!args.Has("y", "yes"));

		if (!prompt.Confirm($"Replace {Fmt.File(executable!)} with crater {Fmt.Val(latest)}"))
		{
			throw new CraterException("Cancelled.");
		}

		await ReplaceExecutableAsync(executable!, asset.DownloadUrl);

		Out.Ok($"Updated crater to {Fmt.Val(latest)}");
	}

	public static void CleanupPreviousVersion()
	{
		string? executable = Environment.ProcessPath;

		if (executable is null)
		{
			return;
		}

		string previous = executable + ".old";

		try
		{
			if (File.Exists(previous))
			{
				File.Delete(previous);
			}
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
		}
	}

	static string UpdateRepository()
	{
		string? fromEnvironment = Environment.GetEnvironmentVariable("CRATER_UPDATE_REPO");
		string[] candidates = [fromEnvironment ?? "", ProjectConfig.LoadGlobal().SelfUpdateRepo, BuildInfo.UpdateRepo];
		string? repository = candidates.FirstOrDefault(candidate => candidate.Length > 0);

		if (repository is null)
		{
			throw new CraterException(
				$"No update source configured. Set {Fmt.Val("self_update_repo")} (for example {Fmt.Val("Owner/crater")}) " +
				$"in {Fmt.File(ProjectConfig.GlobalPath)}, or build crater with {Fmt.Cmd("-p:CraterUpdateRepo=Owner/crater")}.");
		}

		return repository;
	}

	static async Task<Release> LatestReleaseAsync(string repository)
	{
		string url = $"https://api.github.com/repos/{repository}/releases/latest";
		string? text = await Http.GetTextOrNullAsync(url);

		if (text is null)
		{
			throw new CraterException($"{Fmt.Val(repository)} has no published releases.");
		}

		return Json.Parse<Release>(text, url);
	}

	static async Task ReplaceExecutableAsync(string executable, string downloadUrl)
	{
		string downloaded = executable + ".new";
		string previous = executable + ".old";

		using (Spinner spinner = new("Downloading"))
		{
			byte[] data = await Http.GetBytesOrNullAsync(downloadUrl, (done, total) => spinner.Text = $"Downloading {Megabytes(done)}{(total is long size ? " / " + Megabytes(size) : "")}")
				?? throw new CraterException($"Download failed: {Fmt.File(downloadUrl)} not found");

			await File.WriteAllBytesAsync(downloaded, data);
		}

		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(downloaded, File.GetUnixFileMode(executable));
		}

		File.Move(executable, previous, overwrite: true);
		File.Move(downloaded, executable);
	}

	static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";

	static string AssetName()
	{
		string system = OperatingSystem.IsWindows() ? "win" : OperatingSystem.IsMacOS() ? "osx" : "linux";
		string architecture = RuntimeInformation.OSArchitecture == Architecture.Arm64 ? "arm64" : "x64";
		string extension = OperatingSystem.IsWindows() ? ".exe" : "";

		return $"crater-{system}-{architecture}{extension}";
	}
}
