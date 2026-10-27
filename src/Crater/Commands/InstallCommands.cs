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
			await ToolCommand.InstallAllAsync(project.Manifest, announce: false);
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
		bool browse = args.Has("packages-list", "l");

		if (args.Positional.Count == 0 && !browse)
		{
			throw new CraterException(
				$"Usage: {Fmt.Cmd("crater add <scope/name^> [--client|--server|--dev]")}, or {Fmt.Cmd("crater add --packages-list [search]")} to pick from a list");
		}

		Project project = Common.LoadProject();
		Prompter prompt = Common.PrompterFor(project, args);
		Realm realm = Common.RealmFlag(args) ?? DefaultRealm(project);
		bool interactive = Terminal.CanPrompt && !args.Has("y", "yes");
		List<DependencySpec> specs = [];

		if (browse)
		{
			DependencySpec picked = await AddPicker.PickAsync(project, string.Join(' ', args.Positional)) ?? throw new CraterException("Cancelled.");

			specs.Add(picked);
		}
		else
		{
			foreach (string raw in args.Positional)
			{
				specs.Add(await AddPicker.ChooseRegistryAsync(project, DependencySpec.Parse(raw), interactive));
			}
		}
		List<MonorepositoryPicker.Choice> choices = [];

		foreach (DependencySpec spec in specs)
		{
			List<MonorepositoryPicker.Choice>? expanded = await MonorepositoryPicker.ExpandAsync(project, spec, Common.RealmFlag(args), realm, interactive);

			choices.AddRange(expanded ?? [new MonorepositoryPicker.Choice(spec, realm)]);
		}

		JsonObject manifest = Json.ReadObject(project.ManifestPath);

		foreach (MonorepositoryPicker.Choice choice in choices)
		{
			JsonArray list = ManifestEditor.ListFor(manifest, choice.Realm.ManifestField());

			ManifestEditor.RemoveWhere(list, existing => existing.LinkName.Equals(choice.Spec.LinkName, StringComparison.OrdinalIgnoreCase));
			list.Add(choice.Spec.ToString());
		}

		string names = string.Join(", ", choices
			.GroupBy(choice => choice.Realm)
			.Select(group => $"{string.Join(", ", group.Select(choice => Fmt.Pkg(choice.Spec)))} to {Fmt.Val(group.Key.ManifestField())}"));

		if (!prompt.Confirm($"Add {names} in {Fmt.File(Manifest.FileName)}"))
		{
			throw new CraterException("Cancelled.");
		}

		HashSet<string> update = choices.Select(choice => choice.Spec.FullName).ToHashSet();

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

		List<DependencySpec> declared = project.Manifest.AllDependencies().ToList();
		List<string> names = args.Positional.Select(name => ByShortName(declared, name.Trim())).ToList();
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
			string wanted = ByShortName(specs, argument);
			List<string> matched = specs.Where(spec => spec.Matches(wanted)).Select(spec => spec.FullName).ToList();

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

	static string ByShortName(List<DependencySpec> specs, string name)
	{
		if (specs.Any(spec => spec.Matches(name)))
		{
			return name;
		}

		List<string> found = specs
			.Where(spec => spec.Name.Equals(name, StringComparison.OrdinalIgnoreCase))
			.Select(spec => spec.FullName)
			.Distinct()
			.ToList();

		if (found.Count > 1)
		{
			throw new CraterException($"{Fmt.Pkg(name)} matches {string.Join(", ", found.Select(item => Fmt.Pkg(item)))}. Use the full name.");
		}

		return found.Count == 1 ? found[0] : name;
	}

	static Realm DefaultRealm(Project project)
	{
		return project.Manifest.ImportTo == "server" ? Realm.Server : Realm.Client;
	}
}
