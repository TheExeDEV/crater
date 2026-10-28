using Crater.Models;
using Crater.Util;

namespace Crater.Registry;

public static class NamedRegistries
{
	public const string WallyIndex = "https://github.com/UpliftGames/wally-index";
	public const string PesdeIndex = "https://github.com/pesde-pkg/index";
	public const string ForestApi = ForestRegistry.DefaultApi;

	static Dictionary<string, string> Builtin => new(StringComparer.OrdinalIgnoreCase)
	{
		["wally"] = WallyIndex,
		["pesde"] = PesdeIndex,
		["forest"] = ForestApi,
		["ember"] = EmberRegistry.DefaultIndex,
		["crater"] = BuildInfo.DefaultRegistry,
	};

	public static bool IsBuiltin(string name) => Builtin.ContainsKey(name);

	public static IEnumerable<string> BuiltinNames => ["crater", "wally", "pesde", "forest", "ember"];

	public static string? NameOf(string location, Manifest? manifest)
	{
		string wanted = Registries.Expand(location).TrimEnd('/');

		foreach ((string name, string candidate) in (manifest?.Registries ?? []).Concat(Builtin))
		{
			try
			{
				if (Registries.Expand(candidate).TrimEnd('/').Equals(wanted, StringComparison.OrdinalIgnoreCase))
				{
					return name;
				}
			}
			catch (CraterException)
			{
			}
		}

		return null;
	}

	public static bool IsKnownName(string value, Manifest? manifest)
	{
		return LooksLikeName(value) && (Builtin.ContainsKey(value) || manifest?.Registries?.Keys.Any(key => key.Equals(value, StringComparison.OrdinalIgnoreCase)) == true);
	}

	public static bool LooksLikeName(string value)
	{
		return value.Length > 0 && value.All(character => char.IsAsciiLetterOrDigit(character) || character is '_' or '-');
	}

	public static string Location(string name, Manifest? manifest)
	{
		if (manifest?.Registries is { } custom)
		{
			string? own = custom.FirstOrDefault(pair => pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase)).Value;

			if (own is not null)
			{
				return own;
			}
		}

		if (Builtin.TryGetValue(name, out string? location))
		{
			return location;
		}

		throw new CraterException(
			$"Unknown registry {Fmt.Val(name)}. Built in: {Fmt.Val("crater")}, {Fmt.Val("wally")}, {Fmt.Val("pesde")}, {Fmt.Val("forest")}, {Fmt.Val("ember")}. " +
			$"Add your own in {Fmt.File(Manifest.FileName)}: {Fmt.Cmd($"\"registries\": {{ \"{name}\": \"https://github.com/Owner/Repo\" }}")}");
	}
}
