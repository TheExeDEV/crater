using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Registry;

public sealed partial class Rockspec
{
	public string Package { get; private init; } = "";
	public string Version { get; private init; } = "";
	public string? SourceUrl { get; private init; }
	public string? SourceDir { get; private init; }
	public string? SourceTag { get; private init; }
	public string? SourceBranch { get; private init; }
	public string Summary { get; private init; } = "";
	public string License { get; private init; } = "";
	public string Homepage { get; private init; } = "";
	public string BuildType { get; private init; } = "builtin";
	public List<string> Dependencies { get; private init; } = [];
	public Dictionary<string, string> Modules { get; private init; } = [];
	public List<string> NativeModules { get; private init; } = [];

	[GeneratedRegex(@"^\s*(?<name>[A-Za-z0-9_.\-]+)\s*(?<constraints>.*)$")]
	private static partial Regex DependencyRegex();

	[GeneratedRegex(@"(?<op>==|~=|>=|<=|~>|>|<|=)?\s*(?<version>[0-9][0-9A-Za-z.\-]*)")]
	private static partial Regex ConstraintRegex();

	public bool HasNativeSources { get; private init; }

	[GeneratedRegex(@"\b(sources|libraries)\s*=|[""'][^""'\n]+\.(c|cc|cpp|cxx|h)[""']")]
	private static partial Regex NativeHint();

	public bool IsPureLua => NativeModules.Count == 0 && !HasNativeSources && BuildType is "builtin" or "none" or "module";

	public static Rockspec Parse(string text)
	{
		JsonObject globals = LuaReader.Run(text);
		JsonObject? source = globals["source"] as JsonObject;
		JsonObject? description = globals["description"] as JsonObject;
		JsonObject? build = globals["build"] as JsonObject;
		Dictionary<string, string> modules = [];
		List<string> native = [];

		JsonObject? declared = build?["modules"] as JsonObject;

		if (Text(build?["type"]) == "none" && build?["install"] is JsonObject install)
		{
			declared = install["lua"] as JsonObject;
		}

		foreach ((string name, JsonNode? value) in declared ?? [])
		{
			string? path = Text(value);

			if (path is not null && (path.EndsWith(".lua", StringComparison.OrdinalIgnoreCase) || path.EndsWith(".luau", StringComparison.OrdinalIgnoreCase)))
			{
				modules[name.EndsWith(".init", StringComparison.Ordinal) ? name[..^".init".Length] : name] = path;
			}
			else
			{
				native.Add(name);
			}
		}

		return new Rockspec
		{
			Package = Text(globals["package"]) ?? "",
			Version = Text(globals["version"]) ?? "",
			SourceUrl = Text(source?["url"]),
			SourceDir = Text(source?["dir"]),
			SourceTag = Text(source?["tag"]),
			SourceBranch = Text(source?["branch"]),
			Summary = Text(description?["summary"])?.Trim() ?? "",
			License = Text(description?["license"])?.Trim() ?? "",
			Homepage = Text(description?["homepage"])?.Trim() ?? "",
			BuildType = Text(build?["type"]) ?? "builtin",
			Dependencies = LuaReader.List(globals["dependencies"]).Select(Text).OfType<string>().ToList(),
			Modules = modules,
			NativeModules = native,
			HasNativeSources = NativeHint().IsMatch(text),
		};
	}

	public static (string Name, string Requirement)? ParseDependency(string text)
	{
		Match match = DependencyRegex().Match(text);

		if (!match.Success)
		{
			return null;
		}

		return (match.Groups["name"].Value.ToLowerInvariant(), Requirement(match.Groups["constraints"].Value));
	}

	public static string Requirement(string constraints)
	{
		List<(string Op, string Version)> parts = constraints
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(part => ConstraintRegex().Match(part))
			.Where(match => match.Success && ToSemVer(match.Groups["version"].Value) is not null)
			.Select(match => (match.Groups["op"].Value, ToSemVer(match.Groups["version"].Value)!.ToString()))
			.ToList();

		if (parts.Count == 0)
		{
			return "^";
		}

		(string op, string version) = parts[0];

		if (parts.Count == 1)
		{
			return op switch
			{
				"==" or "=" or "" => "=" + version,
				">=" or ">" => "+" + version,
				"~>" => PessimisticRequirement(constraints, version),
				_ => "^",
			};
		}

		return WallyRegistry.Requirement(string.Join(", ", parts.Select(part => $"{part.Op.Replace("~>", ">=")}{part.Version}")));
	}

	static string PessimisticRequirement(string constraints, string version)
	{
		Match match = ConstraintRegex().Match(constraints);
		int given = match.Groups["version"].Value.Split('-')[0].Split('.').Length;

		return given >= 3 ? "~" + version : "^" + version;
	}

	public static SemVer? ToSemVer(string rockVersion)
	{
		string[] numbers = rockVersion.Split('-')[0].Split('.');

		if (numbers.Length is 0 or > 3 || numbers.Any(part => part.Length == 0 || !part.All(char.IsAsciiDigit)))
		{
			return null;
		}

		List<int> parsed = [];

		foreach (string part in numbers)
		{
			if (!int.TryParse(part, out int number))
			{
				return null;
			}

			parsed.Add(number);
		}

		string padded = string.Join('.', parsed.Concat(Enumerable.Repeat(0, 3 - parsed.Count)));

		return SemVer.TryParse(padded, out SemVer version) ? version : null;
	}

	public static int Revision(string rockVersion)
	{
		int dash = rockVersion.LastIndexOf('-');

		return dash >= 0 && int.TryParse(rockVersion[(dash + 1)..], out int revision) ? revision : 0;
	}

	static string? Text(JsonNode? node) => LuaReader.Text(node);
}
