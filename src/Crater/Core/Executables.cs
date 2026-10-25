namespace Crater.Core;

public enum ProgramKind
{
	None,
	Script,
	Binary,
}

public static class Executables
{
	const int HeaderSize = 8192;

	static readonly HashSet<string> BinaryExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".exe", ".dll", ".msi", ".msix", ".appx", ".scr", ".com", ".lnk", ".sys",
		".so", ".run", ".bin", ".appimage", ".deb", ".rpm", ".elf",
		".dylib", ".pkg", ".dmg",
		".jar", ".apk", ".wasm", ".node",
	};

	static readonly HashSet<string> ScriptExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".bat", ".cmd", ".ps1", ".vbs", ".sh", ".bash", ".zsh", ".command",
	};

	static readonly byte[][] Magic =
	[
		[0x4D, 0x5A],
		[0x7F, 0x45, 0x4C, 0x46],
		[0xFE, 0xED, 0xFA, 0xCE],
		[0xFE, 0xED, 0xFA, 0xCF],
		[0xCE, 0xFA, 0xED, 0xFE],
		[0xCF, 0xFA, 0xED, 0xFE],
		[0xCA, 0xFE, 0xBA, 0xBE],
		[0x00, 0x61, 0x73, 0x6D],
	];

	static readonly HashSet<string> TextExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".luau", ".lua", ".json", ".toml", ".md", ".txt", ".yml", ".yaml",
	};

	public static bool IsExecutable(string path, ReadOnlySpan<byte> content) => Classify(path, content) != ProgramKind.None;

	public static ProgramKind Classify(string path, ReadOnlySpan<byte> content)
	{
		string normalized = path.Replace('\\', '/');

		if (normalized.Split('/')[..^1].Any(segment => segment.EndsWith(".app", StringComparison.OrdinalIgnoreCase)))
		{
			return ProgramKind.Binary;
		}

		string extension = Path.GetExtension(normalized);

		if (BinaryExtensions.Contains(extension))
		{
			return ProgramKind.Binary;
		}

		foreach (byte[] magic in Magic)
		{
			if (content.StartsWith(magic))
			{
				return magic.Length == 2 && TextExtensions.Contains(extension) ? ProgramKind.None : ProgramKind.Binary;
			}
		}

		bool script = ScriptExtensions.Contains(extension) || (extension.Length == 0 && content.StartsWith("#!"u8));

		if (!script)
		{
			return ProgramKind.None;
		}

		return content.Contains((byte)0) ? ProgramKind.Binary : ProgramKind.Script;
	}

	public static ProgramKind Classify(string fullPath, string relative)
	{
		byte[] header = new byte[HeaderSize];
		int read;

		using (FileStream stream = File.OpenRead(fullPath))
		{
			read = stream.ReadAtLeast(header, header.Length, throwOnEndOfStream: false);
		}

		return Classify(relative, header.AsSpan(0, read));
	}
}
