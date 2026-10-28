using System.Text.Json.Serialization;
using Crater.Util;

namespace Crater.Cli;

public sealed class CommandList
{
	const string ResourceName = "Cli/commands.json";

	[JsonPropertyName("flags")] public List<FlagHelp> Flags { get; set; } = [];
	[JsonPropertyName("sections")] public List<CommandSection> Sections { get; set; } = [];
	[JsonPropertyName("dependency_formats")] public List<string[]> DependencyFormats { get; set; } = [];

	public static CommandList Current { get; } = Load();

	public IEnumerable<CommandInfo> Commands => Sections.SelectMany(section => section.Commands);

	public HashSet<string> OptionsWithValue { get; private set; } = [];

	public CommandInfo? Find(string name)
	{
		return Commands.FirstOrDefault(command => command.Name == name || command.Aliases.Contains(name));
	}

	public string Canonical(string name) => Find(name)?.Name ?? name;

	public static CommandList Parse(string json, string source)
	{
		CommandList list = Json.Parse<CommandList>(json, source);

		list.OptionsWithValue = list.Commands
			.SelectMany(command => command.ParsedFlags)
			.Where(flag => flag.Value is not null)
			.SelectMany(flag => flag.Names)
			.ToHashSet();

		return list;
	}

	static CommandList Load()
	{
		using Stream stream = typeof(CommandList).Assembly.GetManifestResourceStream(ResourceName)!;
		using StreamReader reader = new(stream);

		return Parse(reader.ReadToEnd(), ResourceName);
	}
}

public sealed class CommandSection
{
	[JsonPropertyName("title")] public string Title { get; set; } = "";
	[JsonPropertyName("commands")] public List<CommandInfo> Commands { get; set; } = [];
}

public sealed class CommandInfo
{
	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("aliases")] public List<string> Aliases { get; set; } = [];
	[JsonPropertyName("flags")] public List<string> Flags { get; set; } = [];
	[JsonPropertyName("subcommands")] public List<string> Subcommands { get; set; } = [];
	[JsonPropertyName("complete")] public List<string> Complete { get; set; } = [];
	[JsonPropertyName("complete_subcommands")] public Dictionary<string, List<string>> CompleteSubcommands { get; set; } = [];
	[JsonPropertyName("help")] public List<string[]> Help { get; set; } = [];

	[JsonIgnore] public List<FlagInfo> ParsedFlags => field ??= Flags.Select(FlagInfo.Parse).ToList();

	public FlagInfo? FindFlag(string word)
	{
		string name = word.TrimStart('-');

		return ParsedFlags.FirstOrDefault(flag => flag.Names.Contains(name));
	}
}

public sealed class FlagHelp
{
	[JsonPropertyName("flag")] public string Flag { get; set; } = "";
	[JsonPropertyName("description")] public string Description { get; set; } = "";
}

public sealed record FlagInfo(List<string> Names, string? Value)
{
	public static FlagInfo Parse(string text)
	{
		int open = text.IndexOf('<');
		string? value = open >= 0 ? text[(open + 1)..text.LastIndexOf('>')] : null;
		List<string> names = (open >= 0 ? text[..open] : text)
			.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
			.Select(name => name.TrimStart('-'))
			.ToList();

		return new FlagInfo(names, value);
	}

	public string LongName => "--" + (Names.FirstOrDefault(name => name.Length > 1) ?? Names[0]);

	public string[] Choices => Value?.Contains('|') == true ? Value.Split('|') : [];
}
