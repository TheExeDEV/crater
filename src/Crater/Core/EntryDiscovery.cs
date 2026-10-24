namespace Crater.Core;

public static class EntryDiscovery
{
	const int MaxDepth = 4;

	static readonly string[] SourceFolders = ["src", "lib", "source", "Source"];
	static readonly string[] LuauExtensions = [".luau", ".lua"];
	static readonly string[] InitFiles = ["init.luau", "init.lua"];

	public static readonly HashSet<string> DevFolders = new(StringComparer.OrdinalIgnoreCase)
	{
		"test", "tests", "__tests__", "spec", "specs", "example", "examples", "docs", "doc", "documentation",
		"bench", "benches", "benchmark", "benchmarks", "node_modules", "modules", "vendor", "dev", "scripts", "tools",
		"stories", "storybook", "demo", "demos", "assets", "build", "dist", "out",
	};

	public static string? Find(string root, string packageName)
	{
		string wanted = Normalize(packageName.Contains('/') ? packageName[(packageName.IndexOf('/') + 1)..] : packageName);

		if (FromSourceFolder(root, wanted) is string source)
		{
			return source;
		}

		return Nearest(root, wanted);
	}

	static string? FromSourceFolder(string root, string wanted)
	{
		foreach (string name in SourceFolders)
		{
			string folder = Path.Combine(root, name);

			if (!Directory.Exists(folder))
			{
				continue;
			}

			string actual = Path.GetFileName(Directory.EnumerateDirectories(root).First(path => Path.GetFileName(path).Equals(name, StringComparison.OrdinalIgnoreCase)));

			if (HasInit(folder))
			{
				return actual;
			}

			if (NamedModule(folder, wanted) is string named)
			{
				return $"{actual}/{named}";
			}

			if (Directory.EnumerateFiles(folder, "*", SearchOption.AllDirectories).Any(IsLuau))
			{
				return actual;
			}
		}

		return null;
	}

	static string? Nearest(string root, string wanted)
	{
		List<string> level = [root];

		for (int depth = 0; depth <= MaxDepth && level.Count > 0; depth++)
		{
			string? anyInit = null;
			List<string> next = [];

			foreach (string directory in level)
			{
				if (wanted.Length > 0 && NamedModule(directory, wanted) is string named)
				{
					return Relative(root, Path.Combine(directory, named));
				}

				foreach (string child in Children(directory))
				{
					anyInit ??= HasInit(child) ? child : null;
					next.Add(child);
				}
			}

			if (anyInit is not null)
			{
				return Relative(root, anyInit);
			}

			level = next;
		}

		return null;
	}

	static string? NamedModule(string directory, string wanted)
	{
		if (wanted.Length == 0)
		{
			return null;
		}

		foreach (string file in Directory.EnumerateFiles(directory).Where(IsLuau).OrderBy(path => path, StringComparer.Ordinal))
		{
			string stem = Path.GetFileNameWithoutExtension(file);

			if (!stem.Contains('.') && Normalize(stem) == wanted)
			{
				return stem;
			}
		}

		foreach (string child in Children(directory))
		{
			if (Normalize(Path.GetFileName(child)) == wanted && HasInit(child))
			{
				return Path.GetFileName(child);
			}
		}

		return null;
	}

	static IEnumerable<string> Children(string directory)
	{
		return Directory.EnumerateDirectories(directory)
			.Where(child =>
			{
				string name = Path.GetFileName(child);

				return !name.StartsWith('.') && !name.StartsWith('_') && !DevFolders.Contains(name);
			})
			.OrderBy(child => SourceFolders.Contains(Path.GetFileName(child), StringComparer.OrdinalIgnoreCase) ? 0 : 1)
			.ThenBy(child => child, StringComparer.Ordinal);
	}

	static bool HasInit(string directory) => InitFiles.Any(init => File.Exists(Path.Combine(directory, init)));

	static bool IsLuau(string file) => LuauExtensions.Contains(Path.GetExtension(file), StringComparer.OrdinalIgnoreCase);

	static string Relative(string root, string path) => Path.GetRelativePath(root, path).Replace('\\', '/');

	static string Normalize(string name) => name.Replace("-", "").Replace("_", "").ToLowerInvariant();
}
