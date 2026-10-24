using System.Security.Cryptography;
using System.Text.RegularExpressions;
using Crater.Registry;
using Crater.Util;

namespace Crater.Core;

public static partial class PackageCache
{
	public static string Root
	{
		get
		{
			string? custom = Environment.GetEnvironmentVariable("CRATER_CACHE");

			if (!string.IsNullOrWhiteSpace(custom))
			{
				return custom;
			}

			string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

			return Path.Combine(local, "crater", "cache");
		}
	}

	[GeneratedRegex("^[0-9a-f]{64}$")]
	private static partial Regex ShaRegex();

	public static string Sha256(byte[] data) => Convert.ToHexStringLower(SHA256.HashData(data));

	static string UnverifiedPath(ResolvedPackage package)
	{
		string source = Sha256(System.Text.Encoding.UTF8.GetBytes(package.Registry.Location))[..12];

		return Path.Combine(Root, "unverified", $"{source}_{package.Name.Replace('/', '_')}@{package.Release.Version}.zip");
	}

	public static bool IsCached(ResolvedPackage package)
	{
		if (package.Registry is SourceRegistry)
		{
			return true;
		}

		string expected = package.Release.Sha256.ToLowerInvariant();

		return File.Exists(ShaRegex().IsMatch(expected) ? Path.Combine(Root, expected + ".zip") : UnverifiedPath(package));
	}

	public static async Task<byte[]> GetAsync(ResolvedPackage package)
	{
		if (package.Registry is SourceRegistry)
		{
			return await package.Registry.DownloadAsync(package.Name, package.Release.Version);
		}

		string expected = package.Release.Sha256.ToLowerInvariant();
		bool hasSha = ShaRegex().IsMatch(expected);
		string cachePath = hasSha ? Path.Combine(Root, expected + ".zip") : UnverifiedPath(package);

		if (File.Exists(cachePath))
		{
			byte[] cached = await File.ReadAllBytesAsync(cachePath);

			if (!hasSha || Sha256(cached) == expected)
			{
				return cached;
			}

			File.Delete(cachePath);
		}

		byte[] data = await package.Registry.DownloadAsync(package.Name, package.Release.Version);

		if (!hasSha && package.Registry is not HttpRegistry { IsWally: true })
		{
			Out.Warn($"{Fmt.Pkg(package.Id)}: registry has no sha256, integrity not verified");
		}

		string actual = Sha256(data);

		if (hasSha && actual != expected)
		{
			throw new CraterException(
				$"{Fmt.Pkg(package.Id)}: checksum mismatch (expected {Fmt.Val(expected)}, got {Fmt.Val(actual)})");
		}

		Directory.CreateDirectory(Path.GetDirectoryName(cachePath)!);

		string temporary = $"{cachePath}.{Guid.NewGuid():N}.tmp";

		await File.WriteAllBytesAsync(temporary, data);
		File.Move(temporary, cachePath, overwrite: true);

		return data;
	}
}
