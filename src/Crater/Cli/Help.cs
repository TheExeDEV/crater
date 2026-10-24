using Crater.Util;

namespace Crater.Cli;

public static class Help
{
	sealed record Entry(string Usage, string Description);

	static readonly (string Title, Entry[] Entries)[] Sections =
	[
		("Project", [
			new("init [--name scope/name]", "create crater.json and crater.settings.json"),
			new("import [wally.toml|pesde.toml]", "convert a Wally or pesde project"),
			new("bump [patch|minor|major]", "raise the version: 0.0.9 becomes 0.1.0"),
			new("run [script] [-- args]", "run a script from crater.json"),
			new("doctor", "check the project for problems"),
			new("check [paths...]", "type check the project with luau-lsp"),
			new("lint [paths...]", "find problems with selene"),
			new("fmt [--check] [paths...]", "format code with StyLua"),
			new("ci", "run check, lint and fmt --check together"),
			new("build [-o file] [--minify]", "build the place or model with Rojo, --minify uses darklua"),
		]),
		("Dependencies", [
			new("install [--workspace] [--locked]", "install dependencies, --locked fails if crater.lock is out of date"),
			new("add <spec...> [--client|--server|--dev]", "add dependencies and install"),
			new("remove <name...> [--client|--server|--dev]", "remove dependencies by scope/name or alias"),
			new("update [name...] [-i] [--workspace]", "update to the newest matching versions, -i to pick"),
			new("outdated [--all]", "show dependencies that have newer versions"),
			new("list", "show the installed package tree"),
			new("why <name>", "show why a package is installed"),
			new("patch [name] [--remove]", "keep your edits to an installed package"),
			new("unused", "find dependencies your code never requires"),
			new("size", "show how much code each package adds"),
			new("licenses", "list the licenses of installed packages"),
		]),
		("Registry", [
			new("login [--force]", "sign in with GitHub to publish packages"),
			new("logout", "sign out"),
			new("whoami", "show who you are signed in as"),
			new("search [query]", "search packages in the registry"),
			new("info <scope/name>", "show package details"),
			new("versions <scope/name>", "list published versions"),
			new("pack [-o file.zip]", "build the package archive"),
			new("publish [--dry-run]", "send the package to the registry for review"),
			new("registry init|check|catalog|site", "manage a registry repository"),
		]),
		("Crater", [
			new("self-update [--check]", "update crater itself"),
			new("tool add <name>[@version] [--global]", "pin a tool like rojo or luau-lsp to the project"),
			new("tool list|remove|install", "manage pinned tools"),
			new("cache [clean]", "show or clear the download cache"),
			new("version", "show version"),
		]),
	];

	static readonly Entry[] Flags =
	[
		new("-y, --yes", "do not ask for confirmation"),
		new("--offline", "use only the cache, no internet"),
		new("-h, --help", "show help"),
	];

	static readonly Entry[] Specs =
	[
		new("scope/name^", "latest version"),
		new("scope/name=1.2.3", "exactly 1.2.3"),
		new("scope/name+1.2.3", "1.2.3 or newer"),
		new("scope/name^1.2.3", "compatible (>=1.2.3 <2.0.0)"),
		new("scope/name@1.2.3", "the same, safe in the Windows terminal"),
		new("scope/name~1.2.3", "patch updates only (>=1.2.3 <1.3.0)"),
		new("Alias:scope/name^", "install under another link name"),
		new("scope/name@path:../lib", "use a local folder"),
		new("scope/name@git+https://host/repo.git#tag", "use a git repository"),
	];

	public static void Print()
	{
		Out.Heading($"crater {BuildInfo.Version}: package manager for Luau and Roblox");
		Out.Info("");
		Out.Info($"Usage: {Fmt.Cmd("crater <command>")} [arguments] [flags]");

		foreach ((string title, Entry[] entries) in Sections)
		{
			PrintSection(title, entries, Fmt.Cmd);
		}

		PrintSection("Flags", Flags, Fmt.Val);
		PrintSection("Dependency format", Specs, Fmt.Pkg);
	}

	static void PrintSection(string title, Entry[] entries, Func<object, string> format)
	{
		Out.Info("");
		Out.Heading(title + ":");

		foreach (Entry entry in entries)
		{
			Out.Info($"  {format(entry.Usage.PadRight(44))} {entry.Description}");
		}
	}
}
