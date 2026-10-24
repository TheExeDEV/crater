using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class InstallCommands
{
	public static async Task InstallAsync(Args args)
	{
		List<Project> projects = Common.TargetProjects(args);

		foreach (Project project in projects)
		{
			Common.Heading(project, projects.Count);

			await new Installer(project, Common.PrompterFor(project, args), args.Has("locked")).InstallAsync(update: null);
		}
	}

	public static async Task UpdateAsync(Args args)
	{
		if (args.Has("i", "interactive"))
		{
			await InteractiveUpdate.RunAsync(Common.LoadProject(), args);
			return;
		}

		List<Project> projects = Common.TargetProjects(args);

		foreach (Project project in projects)
		{
			Common.Heading(project, projects.Count);

			HashSet<string> names = ResolveNames(project, args.Positional, allowUnknown: projects.Count > 1);

			await new Installer(project, Common.PrompterFor(project, args)).InstallAsync(names);
		}
	}

	public static async Task AddAsync(Args args)
	{
		if (args.Positional.Count == 0)
		{
			throw new CraterException($"Usage: {Fmt.Cmd("crater add <scope/name^> [--client|--server|--dev]")}");
		}

		Project project = Common.LoadProject();
		Prompter prompt = Common.PrompterFor(project, args);
		Realm realm = Common.RealmFlag(args) ?? DefaultRealm(project);

		List<DependencySpec> specs = args.Positional.Select(raw => DependencySpec.Parse(raw)).ToList();
		JsonObject manifest = Json.ReadObject(project.ManifestPath);
		JsonArray list = ManifestEditor.ListFor(manifest, realm.ManifestField());

		foreach (DependencySpec spec in specs)
		{
			ManifestEditor.RemoveWhere(list, existing => existing.LinkName.Equals(spec.LinkName, StringComparison.OrdinalIgnoreCase));
		}

		foreach (DependencySpec spec in specs)
		{
			list.Add(spec.ToString());
		}

		string names = string.Join(", ", specs.Select(spec => Fmt.Pkg(spec)));

		if (!prompt.Confirm($"Add {names} to {Fmt.Val(realm.ManifestField())} in {Fmt.File(Manifest.FileName)}"))
		{
			throw new CraterException("Cancelled.");
		}

		HashSet<string> update = specs.Select(spec => spec.FullName).ToHashSet();

		await ManifestEditor.SaveAndInstallAsync(project, manifest, updated => new Installer(updated, prompt).InstallAsync(update));
	}

	public static async Task RemoveAsync(Args args)
	{
		if (args.Positional.Count == 0)
		{
			throw new CraterException($"Usage: {Fmt.Cmd("crater remove <scope/name|alias> [--client|--server|--dev]")}");
		}

		Project project = Common.LoadProject();
		Prompter prompt = Common.PrompterFor(project, args);
		Realm? only = Common.RealmFlag(args);

		List<string> names = args.Positional.Select(name => name.Trim()).ToList();
		string shown = string.Join(", ", names.Select(name => Fmt.Pkg(name)));
		JsonObject manifest = Json.ReadObject(project.ManifestPath);
		int removed = 0;

		foreach (Realm realm in Realms.All.Where(realm => only is null || realm == only))
		{
			if (manifest[realm.ManifestField()] is not JsonArray list)
			{
				continue;
			}

			foreach (string name in names)
			{
				removed += ManifestEditor.RemoveWhere(list, spec => spec.Matches(name));
			}
		}

		if (removed == 0)
		{
			throw new CraterException($"{shown} not found in {Fmt.File(Manifest.FileName)}.");
		}

		if (!prompt.Confirm($"Remove {shown} from {Fmt.File(Manifest.FileName)}"))
		{
			throw new CraterException("Cancelled.");
		}

		await ManifestEditor.SaveAndInstallAsync(project, manifest, updated => new Installer(updated, prompt).InstallAsync(update: null));
	}

	public static HashSet<string> ResolveNames(Project project, IEnumerable<string> arguments, bool allowUnknown)
	{
		List<DependencySpec> specs = project.Manifest.AllDependencies().ToList();
		HashSet<string> names = [];

		foreach (string argument in arguments)
		{
			List<string> matched = specs.Where(spec => spec.Matches(argument)).Select(spec => spec.FullName).ToList();

			if (matched.Count > 0)
			{
				names.UnionWith(matched);
				continue;
			}

			if (!argument.Contains('/') && !allowUnknown)
			{
				throw new CraterException($"{Fmt.Pkg(argument)} is not a dependency name or alias in {Fmt.File(Manifest.FileName)}.");
			}

			names.Add(argument.ToLowerInvariant());
		}

		return names;
	}

	static Realm DefaultRealm(Project project)
	{
		return project.Manifest.ImportTo == "server" ? Realm.Server : Realm.Client;
	}
}
