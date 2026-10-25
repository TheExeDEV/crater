using System.Text.RegularExpressions;
using Crater.Util;

namespace Crater.Tools;

public sealed partial record ToolSpec(string Owner, string Repo, string Version)
{
	static readonly Dictionary<string, string> KnownTools = new(StringComparer.OrdinalIgnoreCase)
	{
		["rojo"] = "rojo-rbx/rojo",
		["luau-lsp"] = "JohnnyMorganz/luau-lsp",
		["selene"] = "Kampfkarren/selene",
		["stylua"] = "JohnnyMorganz/StyLua",
		["darklua"] = "seaofvoices/darklua",
		["lune"] = "lune-org/lune",
		["pesde"] = "pesde-pkg/pesde",
		["luau"] = "luau-lang/luau",
		["asphalt"] = "jackTabsCode/asphalt",
	};

	public static IEnumerable<string> KnownNames => KnownTools.Keys;

	[GeneratedRegex(@"^(?<owner>[A-Za-z0-9_.\-]+)/(?<repo>[A-Za-z0-9_.\-]+)(?:@(?<version>[A-Za-z0-9_.\-+]+))?$")]
	private static partial Regex SpecRegex();

	public string Slug => $"{Owner}/{Repo}";

	public override string ToString() => $"{Slug}@{Version}";

	public static (string Name, string Repository, string? Version) ParseRequest(string text)
	{
		string body = text.Trim();
		string? version = null;
		int at = body.LastIndexOf('@');

		if (at > 0)
		{
			version = body[(at + 1)..].TrimStart('v');
			body = body[..at];
		}

		if (!body.Contains('/'))
		{
			string repository = KnownTools.GetValueOrDefault(body)
				?? throw new CraterException(
					$"Unknown tool {Fmt.Val(body)}. Use its GitHub repository, for example {Fmt.Cmd("crater tool add rojo-rbx/rojo")}. " +
					$"Known names: {string.Join(", ", KnownTools.Keys)}.");

			return (body.ToLowerInvariant(), repository, version);
		}

		return (body[(body.IndexOf('/') + 1)..].ToLowerInvariant(), body, version);
	}

	public static ToolSpec Parse(string name, string text)
	{
		Match match = SpecRegex().Match(text.Trim());

		if (!match.Success || !match.Groups["version"].Success)
		{
			throw new CraterException(
				$"Tool {Fmt.Val(name)} = {Fmt.Val(text)}: expected {Fmt.Cmd("owner/repo@version")}, for example {Fmt.Cmd("rojo-rbx/rojo@7.7.0")}.");
		}

		return new ToolSpec(match.Groups["owner"].Value, match.Groups["repo"].Value, match.Groups["version"].Value.TrimStart('v'));
	}
}
