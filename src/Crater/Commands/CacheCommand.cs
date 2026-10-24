using Crater.Cli;
using Crater.Core;
using Crater.Util;

namespace Crater.Commands;

public static class CacheCommand
{
	public static int Run(Args args)
	{
		string root = PackageCache.Root;
		long size = SizeOf(root);

		switch (args.First)
		{
			case null:
				Out.Info($"{Fmt.File(root)} ({Megabytes(size)})");
				return 0;

			case "clean":
				if (Directory.Exists(root))
				{
					Directory.Delete(root, recursive: true);
				}

				Out.Ok($"Cleaned the download cache, freed {Megabytes(size)}");
				return 0;

			default:
				throw new CraterException($"Usage: {Fmt.Cmd("crater cache")} or {Fmt.Cmd("crater cache clean")}");
		}
	}

	static long SizeOf(string directory)
	{
		if (!Directory.Exists(directory))
		{
			return 0;
		}

		return Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).Sum(file => new FileInfo(file).Length);
	}

	static string Megabytes(long bytes) => $"{bytes / 1048576.0:0.0} MB";
}
