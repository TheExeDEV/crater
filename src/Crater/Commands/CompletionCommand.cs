using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Tools;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class CompletionCommand
{
	const string Marker = "# crater completion";

	static readonly string[] Commands =
	[
		"init", "import", "bump", "run", "dev", "doctor",
		"install", "update", "add", "remove", "outdated", "list", "graph", "why", "patch", "unused", "size", "licenses",
		"search", "info", "versions", "pack", "publish", "deprecate", "login", "logout", "whoami", "registry", "tool",
		"cache", "completion", "self-update", "version",
	];

	static readonly string[] CommonFlags = ["--yes", "--offline", "--help"];

	static readonly Dictionary<string, string[]> Flags = new()
	{
		["init"] = ["--name", "--registry", "--template"],
		["import"] = ["--force", "--registry"],
		["install"] = ["--workspace", "--locked"],
		["update"] = ["--interactive", "--workspace"],
		["add"] = ["--client", "--server", "--dev"],
		["remove"] = ["--client", "--server", "--dev"],
		["outdated"] = ["--all"],
		["publish"] = ["--dry-run", "--workspace"],
		["deprecate"] = ["--undo", "--registry", "--dry-run"],
		["login"] = ["--private", "--force"],
		["dev"] = ["--no-serve"],
		["graph"] = ["--output", "--client", "--server", "--dev", "--depth"],
		["search"] = ["--registry"],
		["info"] = ["--registry"],
		["versions"] = ["--registry"],
		["patch"] = ["--remove"],
		["tool"] = ["--global"],
		["self-update"] = ["--check"],
		["completion"] = ["--print"],
	};

	static readonly Dictionary<string, string[]> Subcommands = new()
	{
		["tool"] = ["add", "list", "remove", "install", "run"],
		["registry"] = ["init", "check", "catalog", "scan", "site"],
		["cache"] = ["clean"],
		["bump"] = ["patch", "minor", "major"],
		["completion"] = ["install", "powershell", "bash", "zsh", "fish"],
	};

	static readonly Dictionary<string, string> Aliases = new()
	{
		["i"] = "install",
		["up"] = "update",
		["rm"] = "remove",
		["ls"] = "list",
	};

	public static int Complete(string[] argv)
	{
		string current = argv.Length > 1 && argv[1].StartsWith("c:") ? argv[1][2..] : "";
		List<string> previous = argv.Skip(2).ToList();

		foreach (string candidate in Candidates(previous, current).Distinct().Where(item => item.StartsWith(current, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal))
		{
			Console.Out.WriteLine(candidate);
		}

		return 0;
	}

	static IEnumerable<string> Candidates(List<string> previous, string current)
	{
		if (previous.LastOrDefault() is "--template" or "-t")
		{
			return ["game", "library", "plugin"];
		}

		List<string> words = previous.Where(word => !word.StartsWith('-')).ToList();

		if (words.Count == 0)
		{
			return current.StartsWith('-') ? CommonFlags.Append("--version") : Commands;
		}

		string command = Aliases.GetValueOrDefault(words[0], words[0]);

		if (current.StartsWith('-'))
		{
			return Flags.GetValueOrDefault(command, []).Concat(CommonFlags);
		}

		if (words.Count == 1 && Subcommands.TryGetValue(command, out string[]? subcommands))
		{
			return subcommands;
		}

		string? subcommand = words.Count > 1 ? words[1] : null;

		return (command, subcommand) switch
		{
			("remove" or "update" or "why" or "outdated", _) => Dependencies(),
			("patch" or "info" or "versions", _) => Dependencies(),
			("run", null) => Scripts(),
			("tool", "add") => ToolSpec.KnownNames,
			("tool", "remove" or "run") => Tools(),
			("import", null) => ["wally.toml", "pesde.toml"],
			_ => [],
		};
	}

	static IEnumerable<string> Dependencies()
	{
		Manifest? manifest = TryManifest();

		if (manifest is null)
		{
			return [];
		}

		List<string> names = [];

		foreach (string raw in Realms.All.SelectMany(manifest.DepsFor))
		{
			try
			{
				DependencySpec spec = DependencySpec.Parse(raw);

				names.Add(spec.FullName);
				names.Add(spec.LinkName);
			}
			catch (CraterException)
			{
			}
		}

		return names;
	}

	static IEnumerable<string> Scripts() => TryManifest()?.Scripts?.Keys ?? Enumerable.Empty<string>();

	static IEnumerable<string> Tools() => TryManifest()?.Tools?.Keys ?? Enumerable.Empty<string>();

	static Manifest? TryManifest()
	{
		try
		{
			return Project.Load(Environment.CurrentDirectory).Manifest;
		}
		catch (CraterException)
		{
			return null;
		}
	}

	public static int Run(Args args)
	{
		string? shell = args.First;

		if (shell is null or "install")
		{
			return Install(args.Positional.Count > 1 ? args.Positional[1] : DetectShell());
		}

		Console.Out.Write(Script(shell));

		return 0;
	}

	static int Install(string shell)
	{
		string script = Script(shell);
		List<string> files = shell switch
		{
			"powershell" => PowerShellProfiles(),
			"bash" => [Path.Combine(Home, ".bashrc")],
			"zsh" => [Path.Combine(Home, ".zshrc")],
			_ => [Path.Combine(Home, ".config", "fish", "completions", "crater.fish")],
		};

		foreach (string file in files)
		{
			if (shell == "fish")
			{
				Directory.CreateDirectory(Path.GetDirectoryName(file)!);
				File.WriteAllText(file, script);
			}
			else
			{
				WriteBlock(file, script);
			}

			Out.Ok($"Tab completion added to {Fmt.File(file)}");
		}

		Out.Info("Open a new terminal, type crater and press Tab.");

		return 0;
	}

	public static void WriteBlock(string file, string script)
	{
		Directory.CreateDirectory(Path.GetDirectoryName(file)!);

		string existing = File.Exists(file) ? File.ReadAllText(file) : "";
		int start = existing.IndexOf(Marker, StringComparison.Ordinal);

		if (start >= 0)
		{
			string endMarker = Marker + " end";
			int end = existing.IndexOf(endMarker, start, StringComparison.Ordinal);

			existing = existing[..start] + (end >= 0 ? existing[(end + endMarker.Length)..] : "");
		}

		existing = existing.Trim('\r', '\n', ' ');

		File.WriteAllText(file, existing + (existing.Length > 0 ? "\n\n" : "") + script);
	}

	static string Home => Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);

	static List<string> PowerShellProfiles()
	{
		string documents = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);

		return
		[
			Path.Combine(documents, "PowerShell", "Microsoft.PowerShell_profile.ps1"),
			Path.Combine(documents, "WindowsPowerShell", "Microsoft.PowerShell_profile.ps1"),
		];
	}

	static string DetectShell()
	{
		if (OperatingSystem.IsWindows())
		{
			return "powershell";
		}

		string name = Path.GetFileName(Environment.GetEnvironmentVariable("SHELL") ?? "bash");

		return name is "zsh" or "fish" ? name : "bash";
	}

	static string Script(string shell)
	{
		return shell switch
		{
			"powershell" => $$"""
				{{Marker}}
				Register-ArgumentCompleter -Native -CommandName crater -ScriptBlock {
				    param($wordToComplete, $commandAst, $cursorPosition)
				    $previous = @($commandAst.CommandElements | Select-Object -Skip 1 | Where-Object { $_.Extent.EndOffset -lt $cursorPosition } | ForEach-Object { $_.ToString() })
				    crater __complete "c:$wordToComplete" @previous 2>$null | ForEach-Object {
				        [System.Management.Automation.CompletionResult]::new($_, $_, 'ParameterValue', $_)
				    }
				}
				{{Marker}} end

				""",
			"bash" => $$"""
				{{Marker}}
				_crater() {
				    local IFS=$'\n'
				    COMPREPLY=($(crater __complete "c:${COMP_WORDS[COMP_CWORD]}" "${COMP_WORDS[@]:1:COMP_CWORD-1}" 2>/dev/null))
				}
				complete -o default -F _crater crater
				{{Marker}} end

				""",
			"zsh" => $$"""
				{{Marker}}
				_crater() {
				    local -a items
				    items=("${(@f)$(crater __complete "c:${words[CURRENT]}" "${(@)words[2,CURRENT-1]}" 2>/dev/null)}")
				    compadd -- $items
				}
				compdef _crater crater
				{{Marker}} end

				""",
			"fish" => $$"""
				{{Marker}}
				complete -c crater -f -a '(crater __complete "c:"(commandline -ct) (commandline -opc)[2..-1] 2>/dev/null)'

				""",
			_ => throw new CraterException($"Unknown shell {Fmt.Val(shell)}. Use powershell, bash, zsh or fish."),
		};
	}
}
