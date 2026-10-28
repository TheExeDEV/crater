using System.Diagnostics;
using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Tools;
using Crater.Util;

namespace Crater.Commands;

public static class ToolCommand
{
	static string GlobalFile => Path.Combine(ToolStore.Home, "tools", "global.json");

	public static async Task<int> RunAsync(Args args)
	{
		Args rest = args.Shift();

		switch (rest.Command)
		{
			case "add":
				await AddAsync(rest);
				return 0;

			case "remove" or "rm":
				Remove(rest);
				return 0;

			case "list" or "ls" or null:
				List();
				return 0;

			case "install":
				await InstallAllAsync(Common.LoadProject().Manifest, announce: true);
				return 0;

			case "known" or "available":
				await KnownAsync();
				return 0;

			default:
				throw new CraterException(
					$"Usage: {Fmt.Cmd("crater tool add <name>[@version] [--global]")}, {Fmt.Cmd("crater tool remove <name>")}, " +
					$"{Fmt.Cmd("crater tool list")}, {Fmt.Cmd("crater tool install")}, {Fmt.Cmd("crater tool known")}");
		}
	}

	public static async Task<int> ExecAsync(string name, string[] arguments)
	{
		(ToolSpec spec, string from) = Resolve(name);
		string executable = await ToolStore.InstallAsync(name, spec);

		ProcessStartInfo info = new(executable) { UseShellExecute = false };

		foreach (string argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(info) ?? throw new CraterException($"Could not start {name} ({from}).");

		process.WaitForExit();

		return process.ExitCode;
	}

	public static async Task InstallAllAsync(Manifest manifest, bool announce)
	{
		Dictionary<string, string> tools = manifest.Tools ?? [];

		if (tools.Count == 0)
		{
			if (announce)
			{
				Out.Info($"No tools in {Fmt.File(Manifest.FileName)}. Add one with {Fmt.Cmd("crater tool add rojo")}.");
			}

			return;
		}

		bool anyNew = false;

		foreach ((string name, string text) in tools)
		{
			ToolSpec spec = ToolSpec.Parse(name, text);
			bool installed = ToolStore.IsInstalled(name, spec);
			anyNew |= !installed;

			await ToolStore.InstallAsync(name, spec);
			ToolStore.WriteShim(name);

			if (!installed || announce)
			{
				Out.Ok($"{name} {Fmt.Val(spec.Version)}{(installed ? "" : " installed")}");
			}
		}

		if (announce || anyNew)
		{
			WarnIfShimsHidden(tools.Keys);
		}
	}

	static async Task AddAsync(Args args)
	{
		if (args.Positional.Count == 0)
		{
			throw new CraterException($"Usage: {Fmt.Cmd("crater tool add rojo luau-lsp")} or {Fmt.Cmd("crater tool add rojo-rbx/rojo@7.7.0")}");
		}

		bool global = args.Has("global", "g");
		Dictionary<string, string> added = [];

		ToolCatalog? catalog = args.Positional.Any(ToolSpec.NeedsCatalog) ? await ToolCatalog.LoadAsync() : null;

		foreach (string request in args.Positional)
		{
			(string name, string repository, string? version) = ToolSpec.ParseRequest(request, catalog);

			using (new Spinner($"Looking up {repository}"))
			{
				version ??= await ToolStore.LatestVersionAsync(repository);
			}

			ToolSpec spec = ToolSpec.Parse(name, $"{repository}@{version}");

			await ToolStore.InstallAsync(name, spec);
			ToolStore.WriteShim(name);
			added[name] = spec.ToString();

			Out.Ok($"Added {name} {Fmt.Val(spec.Version)} ({spec.Slug})");
		}

		if (global)
		{
			Dictionary<string, string> tools = ReadGlobal();

			foreach ((string name, string spec) in added)
			{
				tools[name] = spec;
			}

			Directory.CreateDirectory(Path.GetDirectoryName(GlobalFile)!);
			Json.WriteText(GlobalFile, Json.Serialize(tools));
		}
		else
		{
			Project project = Common.LoadProject();
			JsonObject manifest = Json.ReadObject(project.ManifestPath);
			JsonObject tools = manifest["tools"] as JsonObject ?? [];

			foreach ((string name, string spec) in added)
			{
				tools[name] = spec;
			}

			manifest["tools"] = tools;
			ManifestFile.Write(project.ManifestPath, manifest);
		}

		WarnIfShimsHidden(added.Keys);
	}

	static async Task KnownAsync()
	{
		ToolCatalog catalog = await ToolCatalog.LoadAsync();

		Table.Print(["Tool", "Repository", "Description"], catalog.Tools
			.OrderBy(pair => pair.Key, StringComparer.Ordinal)
			.Select(pair => (IReadOnlyList<string>)[Fmt.Cmd(pair.Key), pair.Value.Repository, pair.Value.Description ?? ""])
			.ToList());

		Out.Info("");
		Out.Dim($"  The list comes from {ToolCatalog.Url}. Any other GitHub release works too: {Fmt.Cmd("crater tool add owner/repo")}.");
	}

	static void Remove(Args args)
	{
		string name = args.First ?? throw new CraterException($"Usage: {Fmt.Cmd("crater tool remove <name>")}");

		if (args.Has("global", "g"))
		{
			Dictionary<string, string> tools = ReadGlobal();

			if (!tools.Remove(name))
			{
				throw new CraterException($"{Fmt.Val(name)} is not a global tool.");
			}

			Json.WriteText(GlobalFile, Json.Serialize(tools));
		}
		else
		{
			Project project = Common.LoadProject();
			JsonObject manifest = Json.ReadObject(project.ManifestPath);

			if (manifest["tools"] is not JsonObject tools || !tools.Remove(name))
			{
				throw new CraterException($"{Fmt.Val(name)} is not in {Fmt.File(Manifest.FileName)}.");
			}

			ManifestFile.Write(project.ManifestPath, manifest);
		}

		Out.Ok($"Removed {name}");
	}

	static void List()
	{
		Manifest? manifest = FindManifest(Environment.CurrentDirectory);
		Dictionary<string, string> project = manifest?.Tools ?? [];
		Dictionary<string, string> global = ReadGlobal();

		if (project.Count == 0 && global.Count == 0)
		{
			Out.Info($"No tools yet. Add one with {Fmt.Cmd("crater tool add rojo")}.");
			return;
		}

		List<IReadOnlyList<string>> rows = [];

		foreach ((string name, string text, string scope) in project.Select(pair => (pair.Key, pair.Value, "project"))
			.Concat(global.Where(pair => !project.ContainsKey(pair.Key)).Select(pair => (pair.Key, pair.Value, "global"))))
		{
			ToolSpec spec = ToolSpec.Parse(name, text);

			rows.Add([Fmt.Cmd(name), Fmt.Val(spec.Version), spec.Slug, scope, ToolStore.IsInstalled(name, spec) ? Fmt.Good("installed") : Fmt.Bad("missing")]);
		}

		Table.Print(["Tool", "Version", "Repository", "From", "State"], rows);
	}

	public static async Task<string> ExecutableAsync(string name)
	{
		try
		{
			(ToolSpec spec, _) = Resolve(name);

			return await ToolStore.InstallAsync(name, spec);
		}
		catch (CraterException) when (OnPath(name) && RunsFromPath(name))
		{
			return name;
		}
	}

	static bool RunsFromPath(string name)
	{
		try
		{
			return Shell.Capture(name, ["--version"], System.Environment.CurrentDirectory).Success;
		}
		catch (CraterException)
		{
			return false;
		}
	}

	static bool OnPath(string name)
	{
		string separator = OperatingSystem.IsWindows() ? ";" : ":";
		string[] candidates = OperatingSystem.IsWindows() ? [name + ".exe", name + ".cmd"] : [name];

		return (Environment.GetEnvironmentVariable("PATH") ?? "")
			.Split(separator, StringSplitOptions.RemoveEmptyEntries)
			.Any(directory => candidates.Any(candidate => File.Exists(Path.Combine(directory, candidate))));
	}

	static (ToolSpec Spec, string From) Resolve(string name)
	{
		Manifest? manifest = FindManifest(Environment.CurrentDirectory);

		if (manifest?.Tools?.TryGetValue(name, out string? pinned) == true)
		{
			return (ToolSpec.Parse(name, pinned), Manifest.FileName);
		}

		if (ReadGlobal().TryGetValue(name, out string? global))
		{
			return (ToolSpec.Parse(name, global), "global tools");
		}

		throw new CraterException(
			$"{Fmt.Val(name)} is not set up here. Run {Fmt.Cmd($"crater tool add {name}")} in the project, " +
			$"or {Fmt.Cmd($"crater tool add {name} --global")} for every folder.");
	}

	static Manifest? FindManifest(string start)
	{
		for (DirectoryInfo? current = new(start); current is not null; current = current.Parent)
		{
			string path = Path.Combine(current.FullName, Manifest.FileName);

			if (File.Exists(path))
			{
				return Json.ReadFile<Manifest>(path);
			}
		}

		return null;
	}

	static Dictionary<string, string> ReadGlobal()
	{
		return File.Exists(GlobalFile) ? Json.ReadFile<Dictionary<string, string>>(GlobalFile) : [];
	}

	static void WarnIfShimsHidden(IEnumerable<string> names)
	{
		string separator = OperatingSystem.IsWindows() ? ";" : ":";
		string[] path = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(separator, StringSplitOptions.RemoveEmptyEntries);
		string shims = Path.GetFullPath(ToolStore.ShimDirectory).TrimEnd(Path.DirectorySeparatorChar);
		int ownIndex = Array.FindIndex(path, entry => SamePath(entry, shims));

		if (ownIndex < 0)
		{
			Out.Warn($"{Fmt.File(ToolStore.ShimDirectory)} is not on PATH, so typing the tool name will not work. Reinstall crater or add that folder to PATH.");
			return;
		}

		foreach (string name in names)
		{
			for (int index = 0; index < ownIndex; index++)
			{
				string[] candidates = OperatingSystem.IsWindows() ? [name + ".exe", name + ".cmd", name + ".bat"] : [name];

				if (candidates.Any(candidate => File.Exists(Path.Combine(path[index], candidate))))
				{
					Out.Warn($"{Fmt.Cmd(name)} from {Fmt.File(path[index])} comes first on PATH (rokit or aftman?). Remove it there, or run {Fmt.Cmd($"crater tool run {name}")}.");
					break;
				}
			}
		}
	}

	static bool SamePath(string left, string right)
	{
		try
		{
			return Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar).Equals(right, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal);
		}
		catch (Exception exception) when (exception is ArgumentException or NotSupportedException)
		{
			return false;
		}
	}
}
