using System.Diagnostics;
using System.Text.Json;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Commands;

public static class CheckCommand
{
	const string RojoProject = "default.project.json";
	const string RobloxTypesUrl = "https://raw.githubusercontent.com/JohnnyMorganz/luau-lsp/main/scripts/globalTypes.PluginSecurity.d.luau";

	static readonly TimeSpan RobloxTypesMaxAge = TimeSpan.FromDays(7);

	public static async Task<int> RunAsync(Args args)
	{
		Project project = Common.LoadProject();
		string luauLsp = await ToolCommand.ExecutableAsync("luau-lsp");

		List<string> folders = project.Config.ConfiguredFolders().Select(pair => pair.Folder.TrimEnd('/')).ToList();
		List<string> targets = args.Positional.Count > 0 ? args.Positional.ToList() : SourceFolders(project, folders);
		string temporary = Path.Combine(Path.GetTempPath(), $"crater-check-{Guid.NewGuid():N}");

		Directory.CreateDirectory(temporary);

		try
		{
			List<string> arguments = ["analyze", "--platform=roblox", $"--definitions=@roblox={await RobloxTypesAsync()}"];

			string? sourcemap = await SourcemapAsync(project, temporary);

			if (sourcemap is not null)
			{
				arguments.Add($"--sourcemap={sourcemap}");
			}

			foreach ((string name, string file) in DefinitionFiles(project))
			{
				arguments.Add($"--definitions=@{name}={file}");
			}

			foreach (string folder in folders.Append(Patches.FolderName))
			{
				arguments.Add($"--ignore={folder}/**");
			}

			arguments.AddRange(targets);

			Out.Step($"Checking {string.Join(", ", targets)}");

			ProcessStartInfo info = new(luauLsp)
			{
				UseShellExecute = false,
				WorkingDirectory = project.Directory,
				RedirectStandardOutput = true,
				RedirectStandardError = true,
			};

			foreach (string argument in arguments)
			{
				info.ArgumentList.Add(argument);
			}

			using Process process = Process.Start(info) ?? throw new CraterException("Could not start luau-lsp.");

			process.OutputDataReceived += (_, line) => PrintLine(line.Data);
			process.ErrorDataReceived += (_, line) => PrintLine(line.Data);
			process.BeginOutputReadLine();
			process.BeginErrorReadLine();
			process.WaitForExit();

			if (process.ExitCode == 0)
			{
				Out.Ok("No type errors");
			}
			else
			{
				Out.Error("luau-lsp found problems");
			}

			return process.ExitCode == 0 ? 0 : 1;
		}
		finally
		{
			Directory.Delete(temporary, recursive: true);
		}
	}

	static void PrintLine(string? line)
	{
		if (line is null || line.StartsWith("[INFO]") || line.StartsWith("[WARN]"))
		{
			return;
		}

		lock (LiveLine.Sync)
		{
			Console.Out.WriteLine(line);
		}
	}

	static List<string> SourceFolders(Project project, List<string> packageFolders)
	{
		string projectFile = Path.Combine(project.Directory, RojoProject);
		List<string> paths = [];

		if (File.Exists(projectFile))
		{
			using JsonDocument document = JsonDocument.Parse(File.ReadAllText(projectFile));

			CollectPaths(document.RootElement, paths);
		}

		List<string> sources = paths
			.Select(path => path.Replace('\\', '/').TrimEnd('/'))
			.Where(path => !packageFolders.Contains(path, StringComparer.OrdinalIgnoreCase))
			.Where(path => Directory.Exists(Path.Combine(project.Directory, path)) || File.Exists(Path.Combine(project.Directory, path)))
			.Distinct()
			.ToList();

		if (sources.Count > 0)
		{
			return sources;
		}

		return Directory.Exists(Path.Combine(project.Directory, "src")) ? ["src"] : ["."];
	}

	static void CollectPaths(JsonElement element, List<string> paths)
	{
		if (element.ValueKind != JsonValueKind.Object)
		{
			return;
		}

		foreach (JsonProperty property in element.EnumerateObject())
		{
			if (property.Name == "$path" && property.Value.ValueKind == JsonValueKind.String)
			{
				paths.Add(property.Value.GetString()!);
			}
			else
			{
				CollectPaths(property.Value, paths);
			}
		}
	}

	static async Task<string?> SourcemapAsync(Project project, string temporary)
	{
		string existing = Path.Combine(project.Directory, "sourcemap.json");

		if (!File.Exists(Path.Combine(project.Directory, RojoProject)))
		{
			return File.Exists(existing) ? existing : null;
		}

		string output = Path.Combine(temporary, "sourcemap.json");

		try
		{
			string rojo = await ToolCommand.ExecutableAsync("rojo");
			ShellResult result = Shell.Capture(rojo, ["sourcemap", RojoProject, "--output", output], project.Directory);

			if (result.Success)
			{
				return output;
			}

			Out.Warn($"rojo could not build the sourcemap: {result.Error.Split('\n')[0]}");
		}
		catch (CraterException)
		{
			Out.Warn($"rojo is not installed, so requires through instances are not checked. Run {Fmt.Cmd("crater tool add rojo")}.");
		}

		return File.Exists(existing) ? existing : null;
	}

	static IEnumerable<(string Name, string File)> DefinitionFiles(Project project)
	{
		List<InstalledPackage> installed;

		try
		{
			installed = InstalledPackages.Load(project);
		}
		catch (CraterException)
		{
			yield break;
		}

		HashSet<string> seen = [];

		foreach (InstalledPackage package in installed)
		{
			string manifestPath = Path.Combine(package.Directory, Manifest.FileName);
			List<string> patterns = File.Exists(manifestPath) ? Json.ReadFile<Manifest>(manifestPath).DefinitionPatterns : [];
			List<string> files = patterns.Count > 0 ? DefinitionLookup.Resolve(package.Directory, patterns) : DefinitionLookup.AutoDetect(package.Directory);

			foreach (string file in files)
			{
				string full = Path.Combine(package.Directory, file);

				if (seen.Add(full))
				{
					string name = package.Locked.ShortName.Replace('-', '_') + (seen.Count > 1 ? "_" + seen.Count : "");

					yield return (name, full);
				}
			}
		}
	}

	static async Task<string> RobloxTypesAsync()
	{
		string path = Path.Combine(PackageCache.Root, "luau-lsp", "globalTypes.PluginSecurity.d.luau");

		if (File.Exists(path) && DateTime.UtcNow - File.GetLastWriteTimeUtc(path) < RobloxTypesMaxAge)
		{
			return path;
		}

		try
		{
			string text;

			using (new Spinner("Downloading Roblox types for luau-lsp"))
			{
				text = await Http.GetTextOrNullAsync(RobloxTypesUrl) ?? throw new CraterException("Roblox types are not available.");
			}

			Directory.CreateDirectory(Path.GetDirectoryName(path)!);
			await File.WriteAllTextAsync(path, text);
		}
		catch (CraterException) when (File.Exists(path))
		{
			Out.Warn("Could not refresh the Roblox types, using the saved copy.");
		}

		return path;
	}
}
