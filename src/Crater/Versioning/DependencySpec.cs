using System.Text.RegularExpressions;
using Crater.Util;

namespace Crater.Versioning;

public enum SourceKind
{
	Path,
	Git,
}

public sealed record DependencySource(SourceKind Kind, string Location, string? Ref, string Text)
{
	public static DependencySource Parse(string text, string context)
	{
		if (text.StartsWith("path:", StringComparison.OrdinalIgnoreCase))
		{
			string location = text["path:".Length..].Trim();

			if (location.Length == 0)
			{
				throw new CraterException($"Empty path in {Fmt.Val(text)}{context}");
			}

			return new DependencySource(SourceKind.Path, location, null, text);
		}

		if (text.StartsWith("git+", StringComparison.OrdinalIgnoreCase))
		{
			string body = text["git+".Length..];
			int hash = body.LastIndexOf('#');

			string url = hash >= 0 ? body[..hash] : body;
			string? reference = hash >= 0 ? body[(hash + 1)..] : null;

			if (url.Length == 0 || reference is { Length: 0 })
			{
				throw new CraterException($"Invalid git source {Fmt.Val(text)}{context}. Expected {Fmt.Cmd("git+https://host/repo.git#tag")}");
			}

			return new DependencySource(SourceKind.Git, url, reference, text);
		}

		throw new CraterException(
			$"Unknown source {Fmt.Val(text)}{context}. Expected {Fmt.Cmd("path:../folder")} or {Fmt.Cmd("git+https://host/repo.git#tag")}");
	}

	public override string ToString() => Text;
}

public sealed partial class DependencySpec
{
	public string? Alias { get; }
	public string? RegistryName { get; }
	public string Scope { get; }
	public string Name { get; }
	public VersionReq Req { get; }
	public DependencySource? Source { get; }

	public string FullName => $"{Scope}/{Name}";
	public string LinkName => Alias ?? Name;

	DependencySpec(string? alias, string? registryName, string scope, string name, VersionReq req, DependencySource? source)
	{
		Alias = alias;
		RegistryName = registryName;
		Scope = scope;
		Name = name;
		Req = req;
		Source = source;
	}

	[GeneratedRegex(
		@"^(?:(?<alias>[A-Za-z][A-Za-z0-9_\-]*):)?(?:(?<registry>[A-Za-z][A-Za-z0-9_\-]*)#)?(?<scope>[a-z0-9_\-]+)/(?<name>[a-z0-9_\-]+)(?:(?<op>[=+^~])(?<ver>\S*)|@(?<source>.+))?$",
		RegexOptions.IgnoreCase)]
	private static partial Regex SpecRegex();

	[GeneratedRegex(@"^[a-z0-9_\-]+/[a-z0-9_\-]+$")]
	private static partial Regex PackageNameRegex();

	[GeneratedRegex(@"^(?<head>[^@\s]+/[A-Za-z0-9_\-]+)@(?<op>[=+^~]?)(?<ver>\d\S*)$")]
	private static partial Regex AtVersionRegex();

	[GeneratedRegex(@"^(?<name>(?:[A-Za-z][A-Za-z0-9_\-]*#)?[A-Za-z0-9_\-]+/[A-Za-z0-9_\-]*?[A-Za-z_\-])(?<ver>\d+(\.\d+){1,2})$")]
	private static partial Regex EatenCaretRegex();

	public static bool IsValidPackageName(string name)
	{
		return PackageNameRegex().IsMatch(name) && !name.Split('/')[1].StartsWith('_');
	}

	public static DependencySpec Parse(string raw, string? where = null)
	{
		string context = where is null ? "" : $" ({where})";
		string text = raw.Trim();
		Match atVersion = AtVersionRegex().Match(text);

		if (atVersion.Success)
		{
			string op = atVersion.Groups["op"].Value;

			text = atVersion.Groups["head"].Value + (op.Length == 0 ? "^" : op) + atVersion.Groups["ver"].Value;
		}

		Match match = SpecRegex().Match(text);

		if (!match.Success && EatenCaretRegex().Match(text) is { Success: true } eaten)
		{
			string fixedSpec = $"{eaten.Groups["name"].Value}@{eaten.Groups["ver"].Value}";

			throw new CraterException(
				$"Got {Fmt.Val(raw)}: the Windows terminal removed the {Fmt.Val("^")} sign. Write {Fmt.Cmd(fixedSpec)} instead, it means the same.");
		}

		if (!match.Success)
		{
			throw new CraterException(
				$"Invalid dependency {Fmt.Val(raw)}{context}. Expected " +
				$"{Fmt.Cmd("scope/name^")}, {Fmt.Cmd("scope/name=1.2.3")}, {Fmt.Cmd("scope/name+1.2.3")}, " +
				$"{Fmt.Cmd("scope/name@1.2.3")}, {Fmt.Cmd("scope/name~1.2.3")} or {Fmt.Cmd("scope/name@path:../folder")}, " +
				$"optionally with an alias: {Fmt.Cmd("Alias:scope/name^")}");
		}

		string? alias = match.Groups["alias"].Success ? match.Groups["alias"].Value : null;
		string? registryName = match.Groups["registry"].Success ? match.Groups["registry"].Value.ToLowerInvariant() : null;
		string scope = match.Groups["scope"].Value.ToLowerInvariant();
		string name = match.Groups["name"].Value.ToLowerInvariant();

		if (name.StartsWith('_'))
		{
			throw new CraterException($"Package name cannot start with '_': {Fmt.Val(raw)}");
		}

		if (match.Groups["source"].Success)
		{
			DependencySource source = DependencySource.Parse(match.Groups["source"].Value.Trim(), context);

			return new DependencySpec(alias, registryName, scope, name, VersionReq.Latest, source);
		}

		char? operatorSymbol = match.Groups["op"].Success ? match.Groups["op"].Value[0] : null;

		try
		{
			VersionReq req = VersionReq.Parse(operatorSymbol, match.Groups["ver"].Value);

			return new DependencySpec(alias, registryName, scope, name, req, null);
		}
		catch (FormatException exception)
		{
			throw new CraterException($"Invalid dependency {Fmt.Val(raw)}{context}: {exception.Message}");
		}
	}

	public DependencySpec WithReq(VersionReq req) => new(Alias, RegistryName, Scope, Name, req, Source);

	public DependencySpec WithNames(string? alias, string? registryName) => new(alias, registryName, Scope, Name, Req, Source);

	public bool Matches(string nameOrAlias)
	{
		if (FullName == nameOrAlias.ToLowerInvariant())
		{
			return true;
		}

		return Alias is not null && Alias.Equals(nameOrAlias, StringComparison.OrdinalIgnoreCase);
	}

	public override string ToString()
	{
		string prefix = (Alias is null ? "" : Alias + ":") + (RegistryName is null ? "" : RegistryName.ToLowerInvariant() + "#");
		string suffix = Source is null ? Req.ToString() : "@" + Source.Text;

		return prefix + FullName + suffix;
	}
}
