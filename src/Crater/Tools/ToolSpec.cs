using System.Text.RegularExpressions;
using Crater.Util;

namespace Crater.Tools;

public sealed partial record ToolSpec(string Owner, string Repo, string Version, string? NpmPackage = null)
{
	public const string NpmPrefix = "npm:";

	static readonly Dictionary<string, string> NpmTools = new(StringComparer.OrdinalIgnoreCase)
	{
		["azul"] = "azul-sync",
	};

	[GeneratedRegex(@"^(?<owner>[A-Za-z0-9_.\-]+)/(?<repo>[A-Za-z0-9_.\-]+)(?:@(?<version>[A-Za-z0-9_.\-+]+))?$")]
	private static partial Regex SpecRegex();

	[GeneratedRegex(@"^(@[a-z0-9_.~\-]+/)?[a-z0-9_.~\-]+$")]
	private static partial Regex NpmPackageRegex();

	public bool IsNpm => NpmPackage is not null;

	public string Slug => IsNpm ? NpmPrefix + NpmPackage : $"{Owner}/{Repo}";

	public override string ToString() => $"{Slug}@{Version}";

	public static bool NeedsCatalog(string text) => !text.Contains('/') && !text.StartsWith(NpmPrefix, StringComparison.Ordinal) && !NpmTools.ContainsKey(SplitVersion(text).Body);

	public static (string Name, string Repository, string? Version) ParseRequest(string text, ToolCatalog? catalog)
	{
		(string body, string? version) = SplitVersion(text.Trim());

		if (body.StartsWith(NpmPrefix, StringComparison.Ordinal))
		{
			string package = body[NpmPrefix.Length..];

			return (package[(package.LastIndexOf('/') + 1)..].ToLowerInvariant(), body, version);
		}

		if (NpmTools.TryGetValue(body, out string? known))
		{
			return (body.ToLowerInvariant(), NpmPrefix + known, version);
		}

		if (!body.Contains('/'))
		{
			string repository = catalog?.Find(body)?.Repository
				?? throw new CraterException(
					$"Unknown tool {Fmt.Val(body)}. Use its GitHub repository, for example {Fmt.Cmd("crater tool add rojo-rbx/rojo")}. " +
					$"Known names: {string.Join(", ", (catalog?.Tools.Keys ?? Enumerable.Empty<string>()).Concat(NpmTools.Keys).Order(StringComparer.Ordinal))}.");

			return (body.ToLowerInvariant(), repository, version);
		}

		return (body[(body.IndexOf('/') + 1)..].ToLowerInvariant(), body, version);
	}

	public static ToolSpec Parse(string name, string text)
	{
		string trimmed = text.Trim();

		if (trimmed.StartsWith(NpmPrefix, StringComparison.Ordinal))
		{
			(string body, string? version) = SplitVersion(trimmed);
			string package = body[NpmPrefix.Length..];

			if (version is null || !NpmPackageRegex().IsMatch(package))
			{
				throw new CraterException(
					$"Tool {Fmt.Val(name)} = {Fmt.Val(text)}: expected {Fmt.Cmd("npm:package@version")}, for example {Fmt.Cmd("npm:azul-sync@2.4.0")}.");
			}

			return new ToolSpec("npm", package, version, package);
		}

		Match match = SpecRegex().Match(trimmed);

		if (!match.Success || !match.Groups["version"].Success)
		{
			throw new CraterException(
				$"Tool {Fmt.Val(name)} = {Fmt.Val(text)}: expected {Fmt.Cmd("owner/repo@version")}, for example {Fmt.Cmd("rojo-rbx/rojo@7.7.0")}.");
		}

		return new ToolSpec(match.Groups["owner"].Value, match.Groups["repo"].Value, match.Groups["version"].Value.TrimStart('v'));
	}

	static (string Body, string? Version) SplitVersion(string text)
	{
		int at = text.LastIndexOf('@');

		if (at <= 0 || text[at - 1] is ':' or '/')
		{
			return (text, null);
		}

		return (text[..at], text[(at + 1)..].TrimStart('v'));
	}
}
