using System.Text.Json;
using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Util;

namespace Crater.Integrations;

public static class Lunaut
{
	public const string DeclareFile = ".lunaut-declare";
	public const string Command = "lunaut";
	public const string SettingsPrefix = "lunaut.";
	public const string LuauLspPrefix = "luau-lsp.";
	public const int DirectiveLines = 32;
	public const string NameDirective = "show_module_name_message";
	public const string MessageDirective = "show_description_message";

	static readonly string[] ExtensionIds = ["theexe.lunaut-luau", "theexe.lunaut", "exitedb.lunaut"];
	static readonly string[] LuauLspExtensionIds = ["johnnymorganz.luau-lsp"];
	static readonly string[] EditorFolders = [".vscode", ".vscode-insiders", ".cursor", ".windsurf", ".vscode-oss"];
	const string ZedExtension = "lunaut";
	const string ObsoleteFile = ".obsolete";
	const string RecommendationsFile = "extensions.json";

	static readonly Lazy<bool> installed = new(() => Shell.Exists(Command) || HasEditorExtension(Home, ExtensionIds) || HasZedExtension());
	static readonly Lazy<bool> luauLspInstalled = new(() => HasEditorExtension(Home, LuauLspExtensionIds));

	static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

	public static bool IsInstalled => installed.Value;

	public static bool IsLuauLspInstalled => luauLspInstalled.Value;

	public static bool IsDeclared(Project project, JsonObject settings)
	{
		return File.Exists(Path.Combine(project.Directory, DeclareFile)) ||
			settings.Any(pair => pair.Key == Command || pair.Key.StartsWith(SettingsPrefix, StringComparison.Ordinal)) ||
			Recommends(project, ExtensionIds);
	}

	public static bool UsesLuauLsp(Project project, JsonObject settings, string managedKey)
	{
		return settings.Any(pair => pair.Key.StartsWith(LuauLspPrefix, StringComparison.Ordinal) && pair.Key != managedKey) ||
			Recommends(project, LuauLspExtensionIds);
	}

	public static bool HasEditorExtension(string home, IReadOnlyList<string> ids)
	{
		foreach (string editor in EditorFolders)
		{
			string extensions = Path.Combine(home, editor, "extensions");

			if (!Directory.Exists(extensions))
			{
				continue;
			}

			HashSet<string> removed = Obsolete(extensions);
			bool found = Directory.EnumerateDirectories(extensions)
				.Select(Path.GetFileName)
				.OfType<string>()
				.Any(folder => !removed.Contains(folder) && ids.Any(id => IsVersionOf(folder, id)));

			if (found)
			{
				return true;
			}
		}

		return false;
	}

	public static Dictionary<string, string> Directives(string file)
	{
		Dictionary<string, string> directives = new(StringComparer.Ordinal);

		if (!File.Exists(file))
		{
			return directives;
		}

		foreach (string raw in File.ReadLines(file).Take(DirectiveLines))
		{
			string line = raw.Trim();

			if (!line.StartsWith("--!", StringComparison.Ordinal))
			{
				continue;
			}

			string[] parts = line[3..].Split(' ', 2, StringSplitOptions.TrimEntries);

			if (parts[0].Length > 0)
			{
				directives.TryAdd(parts[0], parts.Length > 1 ? parts[1].Trim('"') : "");
			}
		}

		return directives;
	}

	static bool IsVersionOf(string folder, string id)
	{
		return folder.StartsWith(id + "-", StringComparison.OrdinalIgnoreCase) && folder.Length > id.Length + 1 && char.IsAsciiDigit(folder[id.Length + 1]);
	}

	static HashSet<string> Obsolete(string extensions)
	{
		string path = Path.Combine(extensions, ObsoleteFile);

		try
		{
			return File.Exists(path) && JsonNode.Parse(File.ReadAllText(path)) is JsonObject removed
				? removed.Select(pair => pair.Key).ToHashSet(StringComparer.OrdinalIgnoreCase)
				: [];
		}
		catch (JsonException)
		{
			return [];
		}
	}

	static bool HasZedExtension()
	{
		string data = OperatingSystem.IsWindows()
			? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Zed")
			: OperatingSystem.IsMacOS()
				? Path.Combine(Home, "Library", "Application Support", "Zed")
				: Path.Combine(Environment.GetEnvironmentVariable("XDG_DATA_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Home, ".local", "share"), "zed");

		return Directory.Exists(Path.Combine(data, "extensions", "installed", ZedExtension));
	}

	static bool Recommends(Project project, IReadOnlyList<string> ids)
	{
		string path = Path.Combine(project.Directory, ".vscode", RecommendationsFile);

		if (!File.Exists(path))
		{
			return false;
		}

		try
		{
			return Json.ReadObject(path)["recommendations"] is JsonArray list &&
				list.Any(item => item is JsonValue value && value.TryGetValue(out string? id) && ids.Contains(id, StringComparer.OrdinalIgnoreCase));
		}
		catch (CraterException)
		{
			return false;
		}
	}
}
