using System.IO.Compression;
using System.Text;
using System.Text.RegularExpressions;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Registry;

public enum Severity
{
	Info,
	Warning,
	Danger,
	Blocked,
}

public sealed record Finding(Severity Severity, string Package, string File, int Line, string Description, string Snippet)
{
	public string Location => Line > 0 ? $"{File}:{Line}" : File;
}

public static partial class PackageScanner
{
	const int MaxFindingsPerRule = 5;
	const int LongLine = 2000;

	sealed record Rule(Severity Severity, Regex Pattern, string Description, bool OnRawText = false, bool OnJoinedText = false);

	[GeneratedRegex(@"\brequire\s*\(\s*(?:0[xX][0-9a-fA-F]{4,}|\d{5,}|tonumber\s*\()")]
	private static partial Regex AssetRequire();

	[GeneratedRegex(@"\b([A-Za-z_]\w*)\s*=\s*(?:0[xX][0-9a-fA-F]{4,}|\d{5,})\s*(?:;|$)")]
	private static partial Regex NumberAssignment();

	[GeneratedRegex(@"\brequire\s*\(\s*([A-Za-z_]\w*)\s*\)")]
	private static partial Regex RequireOfName();

	[GeneratedRegex(@"([""'])\s*\.\.\s*([""'])")]
	private static partial Regex Concatenation();

	[GeneratedRegex(@"(?:\[\s*|\brawget\s*\([^,]*,\s*)([""'])(loadstring|load|require|getfenv|setfenv)\1")]
	private static partial Regex NamedLookup();

	[GeneratedRegex(@"\bloadstring\s*\(")]
	private static partial Regex Loadstring();

	[GeneratedRegex(@"\b(InsertService|LoadAsset|LoadAssetVersion|GetObjects)\b")]
	private static partial Regex AssetLoading();

	[GeneratedRegex(@"\b(getfenv|setfenv)\s*\(")]
	private static partial Regex FunctionEnvironment();

	[GeneratedRegex(@"\b(PromptPurchase|PromptProductPurchase|PromptGamePassPurchase|PromptPremiumPurchase|PromptBundlePurchase)\b")]
	private static partial Regex Purchase();

	[GeneratedRegex(@"string\.char\s*\((\s*\d+\s*,){8,}")]
	private static partial Regex CharCodes();

	[GeneratedRegex(@"(\\\d{1,3}|\\x[0-9a-fA-F]{2}){12,}")]
	private static partial Regex EscapedText();

	[GeneratedRegex(@"[A-Za-z0-9+/]{300,}={0,2}")]
	private static partial Regex EncodedBlob();

	[GeneratedRegex(@"\bHttpService\b")]
	private static partial Regex Http();

	[GeneratedRegex(@"https?://[^\s""'`)]+")]
	private static partial Regex Url();

	[GeneratedRegex(@"\bTeleportService\b")]
	private static partial Regex Teleport();

	[GeneratedRegex(@"\bIsStudio\s*\(")]
	private static partial Regex StudioCheck();

	static readonly Rule[] Rules =
	[
		new(Severity.Danger, AssetRequire(), "requires a Roblox asset by id, the most common backdoor"),
		new(Severity.Danger, Loadstring(), "runs code built from a string"),
		new(Severity.Danger, NamedLookup(), "looks up loadstring, require or getfenv by name, a way to hide them", OnJoinedText: true),
		new(Severity.Danger, AssetLoading(), "loads models from the Roblox website at runtime"),
		new(Severity.Warning, FunctionEnvironment(), "changes function environments, often used to hide code"),
		new(Severity.Warning, Purchase(), "prompts players to buy something"),
		new(Severity.Warning, CharCodes(), "builds text from character codes, a sign of obfuscation", OnRawText: true),
		new(Severity.Warning, EscapedText(), "long escaped string, a sign of obfuscation", OnRawText: true),
		new(Severity.Warning, EncodedBlob(), "long encoded data", OnRawText: true),
		new(Severity.Info, Http(), "uses HttpService (web requests)"),
		new(Severity.Info, Url(), "contains a web address", OnRawText: true),
		new(Severity.Info, Teleport(), "teleports players"),
		new(Severity.Info, StudioCheck(), "behaves differently in Studio, backdoors use this to hide"),
	];

	static readonly HashSet<string> ModelExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".rbxm", ".rbxmx", ".rbxl", ".rbxlx",
	};

	static readonly HashSet<string> LuauExtensions = new(StringComparer.OrdinalIgnoreCase)
	{
		".luau", ".lua",
	};

	public static List<Finding> ScanArchive(byte[] archive, string packageId)
	{
		List<Finding> findings = [];

		using ZipArchive zip = new(new MemoryStream(archive), ZipArchiveMode.Read);

		List<Packer.Pattern> allowed = AllowedFiles(zip.GetEntry(Manifest.FileName) is { } manifest ? Read(manifest) : null);

		foreach (ZipArchiveEntry entry in zip.Entries.OrderBy(item => item.FullName, StringComparer.Ordinal))
		{
			if (entry.FullName.EndsWith('/'))
			{
				continue;
			}

			using Stream stream = entry.Open();
			using MemoryStream buffer = new();

			stream.CopyTo(buffer);
			findings.AddRange(ScanFile(packageId, entry.FullName.Replace('\\', '/'), buffer.ToArray(), allowed));
		}

		return findings;
	}

	static string Read(ZipArchiveEntry entry)
	{
		using StreamReader reader = new(entry.Open());

		return reader.ReadToEnd();
	}

	static List<Packer.Pattern> AllowedFiles(string? manifestText)
	{
		if (manifestText is null)
		{
			return [];
		}

		try
		{
			return (Json.Parse<Manifest>(manifestText, Manifest.FileName).AllowedFiles)
				.Select(Packer.Pattern.Create)
				.ToList();
		}
		catch (CraterException)
		{
			return [];
		}
	}

	public static List<Finding> ScanDirectory(string directory, string packageId)
	{
		List<Finding> findings = [];
		string manifestPath = Path.Combine(directory, Manifest.FileName);
		List<Packer.Pattern> allowed = AllowedFiles(File.Exists(manifestPath) ? File.ReadAllText(manifestPath) : null);

		foreach (string path in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories).OrderBy(item => item, StringComparer.Ordinal))
		{
			string relative = Path.GetRelativePath(directory, path).Replace('\\', '/');
			bool skipped = relative.StartsWith(".git/") || relative.Contains("/_Index/") || relative.StartsWith("_Index/");

			if (!skipped)
			{
				findings.AddRange(ScanFile(packageId, relative, File.ReadAllBytes(path), allowed));
			}
		}

		return findings;
	}

	static IEnumerable<Finding> ScanFile(string packageId, string path, byte[] content, List<Packer.Pattern> allowed)
	{
		string extension = Path.GetExtension(path);
		ProgramKind kind = Executables.Classify(path, content);

		if (kind == ProgramKind.Binary)
		{
			yield return new Finding(Severity.Blocked, packageId, path, 0, "program or archive, packages must not ship programs or archives that can hide them", "");
			yield break;
		}

		if (kind == ProgramKind.Script)
		{
			bool isAllowed = allowed.Any(pattern => pattern.Excludes(path));

			yield return isAllowed
				? new Finding(Severity.Warning, packageId, path, 0, "script file the author kept with package_files.allow, read it before anyone runs it", "")
				: new Finding(Severity.Blocked, packageId, path, 0, "script file, the author must list it in package_files.allow to ship it", "");
			yield break;
		}

		if (ModelExtensions.Contains(extension))
		{
			yield return new Finding(Severity.Warning, packageId, path, 0, "Roblox model or place file, open it in Studio and review its scripts", "");
			yield break;
		}

		if (!LuauExtensions.Contains(extension))
		{
			yield break;
		}

		foreach (Finding finding in ScanLuau(packageId, path, Encoding.UTF8.GetString(content)))
		{
			yield return finding;
		}
	}

	static IEnumerable<Finding> ScanLuau(string packageId, string path, string source)
	{
		string[] rawLines = source.Replace("\r", "").Split('\n');
		string[] codeLines = LuauLexer.Lex(source.Replace("\r", "")).Kept.Split('\n');
		string[] joinedLines = codeLines.Select(line => Concatenation().Replace(line, "")).ToArray();

		foreach (Rule rule in Rules)
		{
			string[] lines = rule.OnJoinedText ? joinedLines : rule.OnRawText ? rawLines : codeLines;
			int count = 0;

			for (int index = 0; index < lines.Length && count < MaxFindingsPerRule; index++)
			{
				Match match = rule.Pattern.Match(lines[index]);

				if (!match.Success)
				{
					continue;
				}

				count++;

				yield return new Finding(rule.Severity, packageId, path, index + 1, rule.Description, Snippet(rawLines[index], match.Index));
			}
		}

		HashSet<string> assetIds = codeLines
			.SelectMany(line => NumberAssignment().Matches(line))
			.Select(match => match.Groups[1].Value)
			.ToHashSet(StringComparer.Ordinal);

		for (int index = 0, count = 0; index < codeLines.Length && count < MaxFindingsPerRule && assetIds.Count > 0; index++)
		{
			Match match = RequireOfName().Match(codeLines[index]);

			if (match.Success && assetIds.Contains(match.Groups[1].Value))
			{
				count++;

				yield return new Finding(Severity.Danger, packageId, path, index + 1, $"requires a Roblox asset by an id stored in {match.Groups[1].Value}, the most common backdoor", Snippet(rawLines[index], match.Index));
			}
		}

		for (int index = 0; index < rawLines.Length; index++)
		{
			if (rawLines[index].Length > LongLine)
			{
				yield return new Finding(Severity.Warning, packageId, path, index + 1, "very long line, possibly minified or obfuscated", Snippet(rawLines[index], 0));
				break;
			}
		}
	}

	static string Snippet(string line, int position)
	{
		const int width = 100;

		string trimmed = line.Trim();

		if (trimmed.Length <= width)
		{
			return trimmed;
		}

		int leading = line.Length - line.TrimStart().Length;
		int start = Math.Clamp(position - leading - 20, 0, trimmed.Length - width);
		string prefix = start > 0 ? "…" : "";
		string suffix = start + width < trimmed.Length ? "…" : "";

		return prefix + trimmed.Substring(start, width) + suffix;
	}
}
