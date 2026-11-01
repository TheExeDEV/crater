using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Integrations;

public static class Azul
{
	public const string SyncDirectory = "sync";

	public static string ToolFor(ProjectConfig config, string directory)
	{
		return config.Sync switch
		{
			"rojo" or "azul" => config.Sync,
			_ when File.Exists(Path.Combine(directory, ProjectSources.RojoProject)) => "rojo",
			_ when Directory.Exists(Path.Combine(directory, SyncDirectory)) => "azul",
			_ => "none",
		};
	}

	public static bool IsUsed(Project project) => ToolFor(project.Config, project.Directory) == "azul";

	public static string Destination(Realm realm, string folder)
	{
		string service = realm == Realm.Server ? "ServerStorage" : "ReplicatedStorage";

		return $"{service}.{LuauRc.AliasFor(folder)}";
	}

	public static List<(string Folder, string Destination)> Pushes(Project project)
	{
		return project.Config.ConfiguredFolders()
			.Where(pair => Directory.Exists(project.Abs(pair.Folder)))
			.Select(pair => (pair.Folder, Destination(pair.Realm, pair.Folder)))
			.ToList();
	}

	public static List<string> MirrorFolders(Project project)
	{
		return project.Config.ConfiguredFolders()
			.Select(pair => $"{SyncDirectory}/{Destination(pair.Realm, pair.Folder).Replace('.', '/')}")
			.ToList();
	}

	public static JsonObject Declare(ProjectConfig config, string directory)
	{
		string tool = ToolFor(config, directory);
		JsonObject declared = new() { ["tool"] = tool };

		if (tool == "rojo")
		{
			declared["project"] = ProjectSources.RojoProject;
		}
		else if (tool == "azul")
		{
			JsonObject packages = [];

			foreach ((Realm realm, string folder) in config.ConfiguredFolders())
			{
				packages[folder] = Destination(realm, folder);
			}

			declared["directory"] = SyncDirectory;
			declared["packages"] = packages;
		}

		return declared;
	}

	public static bool NeedsPush(Project project)
	{
		string marker = MarkerPath(project);
		string pushed = File.Exists(marker) ? File.ReadAllText(marker) : "";

		return Pushes(project).Count > 0 && pushed != LockHash(project);
	}

	public static int Push(Project project, string azul)
	{
		List<(string Folder, string Destination)> pushes = Pushes(project);

		if (pushes.Count == 0)
		{
			Out.Info($"Nothing to push: the package folders are empty. Run {Fmt.Cmd("crater install")} first.");
			return 0;
		}

		Out.Step($"Sending packages to Studio. Open the place and connect the Azul plugin if it is not connected yet.");

		foreach ((string folder, string destination) in pushes)
		{
			Out.Info($"  {Fmt.File(folder)} -> {Fmt.Val(destination)}");

			int exitCode = Run(azul, project.Directory, ["push", "-s", folder, "-d", destination, "--destructive", "--rojo", "--no-warn"]);

			if (exitCode != 0)
			{
				return exitCode;
			}
		}

		Directory.CreateDirectory(Path.GetDirectoryName(MarkerPath(project))!);
		File.WriteAllText(MarkerPath(project), LockHash(project));
		Out.Ok($"Packages are in Studio");

		return 0;
	}

	public static void UpdateGitignore(Project project, Prompter prompt)
	{
		string path = Path.Combine(project.Directory, ".gitignore");

		if (!File.Exists(path))
		{
			return;
		}

		string text = File.ReadAllText(path);
		List<string> lines = text.Replace("\r\n", "\n").Split('\n').Select(line => line.Trim().TrimStart('/')).ToList();
		List<string> missing = MirrorFolders(project).Select(folder => folder + "/").Where(entry => !lines.Contains(entry)).ToList();

		if (missing.Count > 0 && prompt.Confirm($"Keep the packages Azul copies back from Studio out of git ({string.Join(", ", missing)} in .gitignore)"))
		{
			File.WriteAllText(path, text.TrimEnd('\r', '\n') + "\n" + string.Join("\n", missing) + "\n");
		}
	}

	static int Run(string executable, string directory, IEnumerable<string> arguments)
	{
		ProcessStartInfo info = new(executable) { WorkingDirectory = directory, UseShellExecute = false };

		foreach (string argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}

		using Process process = Process.Start(info) ?? throw new CraterException($"Could not start {Fmt.Cmd("azul")}.");

		process.WaitForExit();

		return process.ExitCode;
	}

	static string MarkerPath(Project project)
	{
		string key = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(project.Directory).ToLowerInvariant())))[..16];

		return Path.Combine(CraterPaths.Cache, "azul", key + ".pushed");
	}

	static string LockHash(Project project)
	{
		if (!File.Exists(project.LockPath))
		{
			return "";
		}

		string packages = string.Join("\n", LockFile.Load(project.LockPath).Packages
			.Select(package => $"{package.Realm} {package.Name} {package.Version} {package.Sha256}")
			.Order(StringComparer.Ordinal));

		return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(packages)));
	}
}
