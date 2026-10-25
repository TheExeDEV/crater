using System.Text.RegularExpressions;
using Crater.Util;

namespace Crater.Tools;

public sealed partial record ToolSpec(string Owner, string Repo, string Version)
{
	[GeneratedRegex(@"^(?<owner>[A-Za-z0-9_.\-]+)/(?<repo>[A-Za-z0-9_.\-]+)(?:@(?<version>[A-Za-z0-9_.\-+]+))?$")]
	private static partial Regex SpecRegex();

	public string Slug => $"{Owner}/{Repo}";

	public override string ToString() => $"{Slug}@{Version}";

	public static bool NeedsCatalog(string text) => !text.Contains('/');

	public static (string Name, string Repository, string? Version) ParseRequest(string text, ToolCatalog? catalog)
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
			string repository = catalog?.Find(body)?.Repository
				?? throw new CraterException(
					$"Unknown tool {Fmt.Val(body)}. Use its GitHub repository, for example {Fmt.Cmd("crater tool add rojo-rbx/rojo")}. " +
					$"Known names: {string.Join(", ", catalog?.Tools.Keys.Order(StringComparer.Ordinal) ?? Enumerable.Empty<string>())}.");

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
