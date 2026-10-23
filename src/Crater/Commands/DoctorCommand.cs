using System.Text.Json.Nodes;
using Crater.Core;
using Crater.Integrations;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class DoctorCommand
{
	sealed class Report
	{
		public int Errors { get; private set; }
		public int Warnings { get; private set; }

		public void Pass(string message) => Out.Ok(message);

		public void Warn(string message)
		{
			Warnings++;
			Out.Warn(message);
		}

		public void Fail(string message)
		{
			Errors++;
			Out.Error(message);
		}
	}

	public static async Task<int> RunAsync()
	{
		Report report = new();
		Project project;

		try
		{
			project = Common.LoadProject();
			report.Pass($"{Fmt.File(Manifest.FileName)} and {Fmt.File(ProjectConfig.FileName)} are valid");
		}
		catch (CraterException exception)
		{
			report.Fail(exception.Message);
			return 1;
		}

		List<DependencySpec> specs = project.Manifest.AllDependencies().ToList();
		LockFile lockFile = LockFile.Load(project.LockPath);

		await CheckRegistryAsync(project, specs, report);
		CheckGit(specs, report);
		CheckLock(project, lockFile, report);
		CheckFolders(project, lockFile, report);
		CheckLuauRc(project, lockFile, report);
		CheckVsCode(project, report);
		CheckRojo(project, lockFile, report);
		CheckWorkspace(project, report);

		Out.Info("");

		if (report.Errors > 0)
		{
			Out.Error($"{Fmt.Plural(report.Errors, "problem", "problems")}, {Fmt.Plural(report.Warnings, "warning", "warnings")}");
			return 1;
		}

		Out.Ok(report.Warnings == 0 ? "No problems found" : $"No problems, {Fmt.Plural(report.Warnings, "warning", "warnings")}");

		return 0;
	}

	static async Task CheckRegistryAsync(Project project, List<DependencySpec> specs, Report report)
	{
		DependencySpec? sample = specs.FirstOrDefault(spec => spec.Source is null);

		if (sample is null)
		{
			return;
		}

		IRegistry? registry = project.OpenRegistry();

		if (registry is null)
		{
			report.Fail($"Dependencies need a registry, but none is configured. Set {Fmt.Val("registry")} in {Fmt.File(Manifest.FileName)}.");
			return;
		}

		try
		{
			RegistryIndex? index = await registry.GetIndexAsync(sample.FullName);

			if (index is null)
			{
				report.Warn($"Registry {Fmt.File(registry.Location)} is reachable, but {Fmt.Pkg(sample.FullName)} is not in it");
				return;
			}

			report.Pass($"Registry {Fmt.File(registry.Location)} is reachable");
		}
		catch (CraterException exception)
		{
			report.Fail($"Registry {Fmt.File(registry.Location)} is not reachable: {exception.Message}");
		}
	}

	static void CheckGit(List<DependencySpec> specs, Report report)
	{
		bool needsGit = specs.Any(spec => spec.Source?.Kind == SourceKind.Git);

		if (!needsGit)
		{
			return;
		}

		if (Shell.Exists("git"))
		{
			report.Pass($"{Fmt.Cmd("git")} is installed (needed for git dependencies)");
		}
		else
		{
			report.Fail($"Git dependencies need {Fmt.Cmd("git")}, but it is not installed");
		}
	}

	static void CheckLock(Project project, LockFile lockFile, Report report)
	{
		bool hasDependencies = Realms.All.Any(realm => project.Manifest.DepsFor(realm).Count > 0);

		if (!hasDependencies)
		{
			return;
		}

		if (!File.Exists(project.LockPath))
		{
			report.Fail($"{Fmt.File(LockFile.FileName)} is missing. Run {Fmt.Cmd("crater install")}.");
			return;
		}

		int problems = 0;

		foreach (Realm realm in Realms.All)
		{
			Dictionary<string, LockedPackage> locked = lockFile.For(realm).ToDictionary(package => package.Name);
			List<DependencySpec> specs = project.Manifest.DepsFor(realm).Select(raw => DependencySpec.Parse(raw)).ToList();
			HashSet<string> overridden = project.Manifest.ParseOverrides().Select(spec => spec.FullName).ToHashSet();

			foreach (DependencySpec spec in specs)
			{
				if (!locked.TryGetValue(spec.FullName, out LockedPackage? package) || !package.Direct)
				{
					report.Fail($"{Fmt.Pkg(spec)} ({realm.Key()}) is not in {Fmt.File(LockFile.FileName)}. Run {Fmt.Cmd("crater install")}.");
					problems++;
					continue;
				}

				bool matches = spec.Source is not null ||
					overridden.Contains(spec.FullName) ||
					(SemVer.TryParse(package.Version, out SemVer version) && spec.Req.Matches(version));

				if (!matches)
				{
					report.Fail($"{Fmt.Pkg(spec)} ({realm.Key()}) is locked at {Fmt.Val(package.Version)}. Run {Fmt.Cmd("crater install")}.");
					problems++;
				}
			}

			HashSet<string> declared = specs.Select(spec => spec.FullName).ToHashSet();

			foreach (LockedPackage package in locked.Values.Where(package => package.Direct && !declared.Contains(package.Name)))
			{
				report.Warn($"{Fmt.Pkg(package.Name)} ({realm.Key()}) is locked but no longer in {Fmt.File(Manifest.FileName)}. Run {Fmt.Cmd("crater install")}.");
				problems++;
			}
		}

		if (problems == 0)
		{
			report.Pass($"{Fmt.File(LockFile.FileName)} matches {Fmt.File(Manifest.FileName)}");
		}
	}

	static void CheckFolders(Project project, LockFile lockFile, Report report)
	{
		foreach (Realm realm in Realms.All)
		{
			List<LockedPackage> packages = lockFile.For(realm).ToList();

			if (packages.Count == 0)
			{
				continue;
			}

			string? folderName = project.Config.TryFolderFor(realm);

			if (folderName is null)
			{
				report.Fail($"{Fmt.Val(ProjectConfig.FolderField(realm))} is not set in {Fmt.File(ProjectConfig.FileName)}");
				continue;
			}

			string folder = project.Abs(folderName);
			List<string> missing = [];

			foreach (LockedPackage package in packages)
			{
				string packageDirectory = Path.Combine(folder, Layout.IndexFolder, $"{package.Name.Replace('/', '_')}@{package.Version}", package.ShortName);

				if (!Directory.Exists(packageDirectory))
				{
					missing.Add(package.Id);
				}

				if (!package.Direct)
				{
					continue;
				}

				foreach (string link in package.Links ?? [package.ShortName])
				{
					if (!File.Exists(Path.Combine(folder, link + ".luau")))
					{
						missing.Add($"{link}.luau");
					}
				}
			}

			if (missing.Count == 0)
			{
				report.Pass($"{Fmt.File(folderName + "/")} has {Fmt.Plural(packages.Count, "package", "packages")}");
			}
			else
			{
				report.Fail($"{Fmt.File(folderName + "/")} is missing {string.Join(", ", missing.Take(5))}. Run {Fmt.Cmd("crater install")}.");
			}
		}
	}

	static void CheckLuauRc(Project project, LockFile lockFile, Report report)
	{
		if (!project.Config.OnInstallUpdateLuaurc)
		{
			return;
		}

		JsonObject root = Json.ReadObject(Path.Combine(project.Directory, LuauRc.FileName));
		JsonObject? aliases = root["aliases"] as JsonObject;

		foreach ((Realm realm, string folder) in project.Config.ConfiguredFolders())
		{
			if (!lockFile.For(realm).Any())
			{
				continue;
			}

			string alias = LuauRc.AliasFor(folder);
			string? value = aliases?[alias]?.GetValue<string>();

			if (value == "./" + folder)
			{
				report.Pass($"{Fmt.File(LuauRc.FileName)} alias {Fmt.Val("@" + alias)} points to {Fmt.File(folder)}");
			}
			else
			{
				report.Warn($"{Fmt.File(LuauRc.FileName)} has no alias {Fmt.Val("@" + alias)} for {Fmt.File(folder)}. Run {Fmt.Cmd("crater install")}.");
			}
		}
	}

	static void CheckVsCode(Project project, Report report)
	{
		string path = VsCodeSettings.PathIn(project);

		if (!project.Config.OnInstallUpdateVscodeLuauDefinitionFiles || !File.Exists(path))
		{
			return;
		}

		JsonNode? definitions = Json.ReadObject(path)[VsCodeSettings.Key];
		List<string> files = definitions switch
		{
			JsonArray list => list.Select(item => item?.GetValue<string>() ?? "").ToList(),
			JsonObject map => map.Select(pair => pair.Value?.GetValue<string>() ?? "").ToList(),
			_ => [],
		};

		List<string> prefixes = Layout.ManagedPrefixes(project).ToList();
		List<string> broken = files
			.Where(file => prefixes.Any(prefix => file.StartsWith(prefix)))
			.Where(file => !File.Exists(Path.Combine(project.Directory, file)))
			.ToList();

		if (broken.Count == 0)
		{
			report.Pass($"VS Code definition files exist");
		}
		else
		{
			report.Warn($"VS Code points to missing definition files: {string.Join(", ", broken.Take(3))}. Run {Fmt.Cmd("crater install")}.");
		}
	}

	static void CheckRojo(Project project, LockFile lockFile, Report report)
	{
		string path = Path.Combine(project.Directory, "default.project.json");

		if (!File.Exists(path))
		{
			return;
		}

		string text = File.ReadAllText(path).Replace('\\', '/');

		foreach ((Realm realm, string folder) in project.Config.ConfiguredFolders())
		{
			if (!lockFile.For(realm).Any())
			{
				continue;
			}

			bool mapped = text.Contains($"\"{folder}\"") || text.Contains($"\"./{folder}\"");

			if (mapped)
			{
				report.Pass($"Rojo maps {Fmt.File(folder)}");
			}
			else
			{
				report.Warn($"{Fmt.File("default.project.json")} does not map {Fmt.File(folder)} with {Fmt.Val("$path")}, Roblox will not see these packages");
			}
		}
	}

	static void CheckWorkspace(Project project, Report report)
	{
		if (project.Workspace is null)
		{
			return;
		}

		report.Pass($"Workspace with {Fmt.Plural(project.Workspace.Members.Count, "member", "members")} at {Fmt.File(project.Workspace.Root)}");
	}
}
