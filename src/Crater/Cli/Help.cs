using Crater.Util;

namespace Crater.Cli;

public static class Help
{
	public static void Print()
	{
		CommandList list = CommandList.Current;

		Out.Heading($"crater {BuildInfo.Version}: project manager for Luau and Roblox");
		Out.Info("");
		Out.Info($"Usage: {Fmt.Cmd("crater <command>")} [arguments] [flags]");

		foreach (CommandSection section in list.Sections)
		{
			PrintSection(section.Title, section.Commands.SelectMany(command => command.Help), Fmt.Cmd);
		}

		PrintSection("Flags", list.Flags.Select(flag => new[] { flag.Flag, flag.Description }), Fmt.Val);
		PrintSection("Dependency format", list.DependencyFormats, Fmt.Pkg);
	}

	static void PrintSection(string title, IEnumerable<string[]> entries, Func<object, string> format)
	{
		Out.Info("");
		Out.Heading(title + ":");

		foreach (string[] entry in entries)
		{
			Out.Info($"  {format(entry[0].PadRight(44))} {entry[1]}");
		}
	}
}
