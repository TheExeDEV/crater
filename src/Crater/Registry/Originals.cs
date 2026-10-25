using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Crater.Util;

namespace Crater.Registry;

public sealed partial class Originals
{
	public const string FileName = "originals.json";

	[GeneratedRegex(@"^(?:(crater|wally|pesde|forest)#)?[a-z0-9_\-]+/[a-z0-9_\-]+$")]
	private static partial Regex EntryRegex();

	[JsonPropertyName("packages")] public List<string> Packages { get; set; } = [];

	public static Originals Load(string registryRoot)
	{
		string path = Path.Combine(registryRoot, FileName);

		return File.Exists(path) ? Json.ReadFile<Originals>(path) : new Originals();
	}

	public List<string> Invalid()
	{
		return Packages.Where(entry => !EntryRegex().IsMatch(entry.Trim().ToLowerInvariant())).ToList();
	}

	public List<string> Normalized()
	{
		return Packages
			.Select(entry => entry.Trim().ToLowerInvariant())
			.Where(entry => EntryRegex().IsMatch(entry))
			.Distinct()
			.Order(StringComparer.Ordinal)
			.ToList();
	}
}
