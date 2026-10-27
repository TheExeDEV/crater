using System.Text.Json;
using System.Text.Json.Serialization;
using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public sealed class CraterDeclare
{
	public const string FileName = ".crater-declare";
	public const int CurrentApiVersion = 1;

	public static readonly string[] Features = ["dependencies", "tools", "scripts", "workspaces", "monorepository", "interfaces"];

	[JsonPropertyName("path_to_crater_settings")] public string PathToCraterSettings { get; set; } = "";
	[JsonPropertyName("path_to_package_info")] public string PathToPackageInfo { get; set; } = "";
	[JsonPropertyName("api_version")] public int ApiVersion { get; set; }
	[JsonPropertyName("features")] public List<string> FeatureList { get; set; } = [];

	public static string PathIn(string directory) => Path.Combine(directory, FileName);

	public static CraterDeclare For(string directory)
	{
		string settings = File.Exists(ProjectConfig.PathIn(directory)) ? Path.GetFileName(ProjectConfig.PathIn(directory)) : ProjectConfig.FileName;

		return new CraterDeclare
		{
			PathToCraterSettings = settings,
			PathToPackageInfo = Manifest.FileName,
			ApiVersion = CurrentApiVersion,
			FeatureList = [.. Features],
		};
	}

	public static void Write(string directory)
	{
		string path = PathIn(directory);

		if (File.Exists(path))
		{
			File.SetAttributes(path, File.GetAttributes(path) & ~FileAttributes.Hidden & ~FileAttributes.ReadOnly);
		}

		Json.WriteText(path, Json.Serialize(For(directory)));

		if (OperatingSystem.IsWindows())
		{
			File.SetAttributes(path, File.GetAttributes(path) | FileAttributes.Hidden);
		}
	}

	public static string? Problem(string directory)
	{
		string path = PathIn(directory);

		if (!File.Exists(path))
		{
			return $"{Fmt.File(FileName)} is missing";
		}

		CraterDeclare? declared;

		try
		{
			declared = JsonSerializer.Deserialize<CraterDeclare>(File.ReadAllText(path));
		}
		catch (JsonException exception)
		{
			return $"{Fmt.File(FileName)} is not valid JSON ({exception.Message})";
		}

		if (declared is null)
		{
			return $"{Fmt.File(FileName)} is empty";
		}

		CraterDeclare expected = For(directory);

		if (!SamePath(directory, declared.PathToPackageInfo, expected.PathToPackageInfo))
		{
			return $"{Fmt.File(FileName)}: path_to_package_info is {Fmt.Val(Shown(declared.PathToPackageInfo))}, but the package info is {Fmt.File(expected.PathToPackageInfo)}";
		}

		if (!SamePath(directory, declared.PathToCraterSettings, expected.PathToCraterSettings))
		{
			return $"{Fmt.File(FileName)}: path_to_crater_settings is {Fmt.Val(Shown(declared.PathToCraterSettings))}, but the settings are {Fmt.File(expected.PathToCraterSettings)}";
		}

		if (declared.ApiVersion != CurrentApiVersion || !declared.FeatureList.Order().SequenceEqual(expected.FeatureList.Order()))
		{
			return $"{Fmt.File(FileName)} describes an older crater API";
		}

		return null;
	}

	public static void Ensure(string directory, bool quiet)
	{
		string? problem = Problem(directory);

		if (problem is null)
		{
			return;
		}

		bool existed = File.Exists(PathIn(directory));

		try
		{
			Write(directory);
		}
		catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
		{
			Out.Warn($"{problem}, and crater could not rewrite it: {exception.Message}");
			return;
		}

		if (existed && !quiet)
		{
			Out.Warn($"{problem}. Fixed it.");
		}
	}

	static bool SamePath(string directory, string declared, string expected)
	{
		if (string.IsNullOrWhiteSpace(declared))
		{
			return false;
		}

		string full = Path.GetFullPath(Path.Combine(directory, declared));

		return File.Exists(full) && full.Equals(Path.GetFullPath(Path.Combine(directory, expected)), StringComparison.OrdinalIgnoreCase);
	}

	static string Shown(string value) => string.IsNullOrWhiteSpace(value) ? "empty" : value;
}
