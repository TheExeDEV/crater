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

	public static bool IsCached(ResolvedPackage package)
	{
		if (package.Registry is SourceRegistry)
		{
			return true;
		}

		string expected = package.Release.Sha256.ToLowerInvariant();

		return ShaRegex().IsMatch(expected) && File.Exists(Path.Combine(Root, expected + ".zip"));
	}

	public static async Task<byte[]> GetAsync(ResolvedPackage package)
	{
		if (package.Registry is SourceRegistry)
		{
			return await package.Registry.DownloadAsync(package.Name, package.Release.Version);
		}

		string expected = package.Release.Sha256.ToLowerInvariant();
		bool hasSha = ShaRegex().IsMatch(expected);
		string? cachePath = hasSha ? Path.Combine(Root, expected + ".zip") : null;

		if (cachePath is not null && File.Exists(cachePath))
		{
			byte[] cached = await File.ReadAllBytesAsync(cachePath);

			if (Sha256(cached) == expected)
			{
				return cached;
			}

			File.Delete(cachePath);
		}

		byte[] data = await package.Registry.DownloadAsync(package.Name, package.Release.Version);

		if (!hasSha)
		{
			Out.Warn($"{Fmt.Pkg(package.Id)}: registry has no sha256, integrity not verified");
			return data;
		}

		string actual = Sha256(data);

		if (actual != expected)
		{
			throw new CraterException(
				$"{Fmt.Pkg(package.Id)}: checksum mismatch (expected {Fmt.Val(expected)}, got {Fmt.Val(actual)})");
		}

		Directory.CreateDirectory(Root);

		string temporary = $"{cachePath}.{Guid.NewGuid():N}.tmp";

		await File.WriteAllBytesAsync(temporary, data);
		File.Move(temporary, cachePath!, overwrite: true);

		return data;
	}
}
