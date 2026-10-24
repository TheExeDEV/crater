using Crater.Models;

namespace Crater.Core;

public static class PackageScripts
{
	static readonly string[] ScriptSuffixes =
	[
		".server.luau", ".server.lua", ".client.luau", ".client.lua",
		".legacy.luau", ".legacy.lua", ".local.luau", ".local.lua",
		".plugin.luau", ".plugin.lua",
	];

	static readonly string[] ModelExtensions = [".rbxm", ".rbxmx"];

	public static List<string> InArchive(byte[] archive, Manifest manifest, string id)
	{
		string staging = Path.Combine(Path.GetTempPath(), $"crater-scripts-{Guid.NewGuid():N}");

		try
		{
			Packer.Extract(archive, staging, id);
			PackageFiles.Prepare(id, staging, id[(id.IndexOf('/') + 1)..id.LastIndexOf('@')], manifest, quiet: true);

			return InDirectory(staging);
		}
		finally
		{
			if (Directory.Exists(staging))
			{
				Directory.Delete(staging, recursive: true);
			}
		}
	}

	public static List<string> InDirectory(string directory)
	{
		return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
			.Select(file => Path.GetRelativePath(directory, file).Replace('\\', '/'))
			.Where(IsScript)
			.OrderBy(path => path, StringComparer.Ordinal)
			.ToList();
	}

	public static bool IsScript(string path)
	{
		string lower = path.ToLowerInvariant();

		return ScriptSuffixes.Any(lower.EndsWith) || ModelExtensions.Any(lower.EndsWith);
	}

	public static string Describe(IReadOnlyList<string> scripts)
	{
		string shown = string.Join(", ", scripts.Take(4));

		return scripts.Count > 4 ? $"{shown} and {scripts.Count - 4} more" : shown;
	}
}
