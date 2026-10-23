using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class Common
{
	public static Project LoadProject() => Project.Load(Environment.CurrentDirectory);

	public static Prompter PrompterFor(Project project, Args args)
	{
		bool ask = project.Config.OnInstallShouldIAskYourPermissionToDoAnyAction && !args.Has("y", "yes");

		return new Prompter(ask);
	}

	public static Realm? RealmFlag(Args args)
	{
		List<Realm> realms = [];

		if (args.Has("client"))
		{
			realms.Add(Realm.Client);
		}

		if (args.Has("server"))
		{
			realms.Add(Realm.Server);
		}

		if (args.Has("dev", "develop"))
		{
			realms.Add(Realm.Develop);
		}

		if (realms.Count > 1)
		{
			throw new CraterException($"Use only one of {Fmt.Cmd("--client")}, {Fmt.Cmd("--server")}, {Fmt.Cmd("--dev")}.");
		}

		return realms.Count == 1 ? realms[0] : null;
	}

	public static List<Project> TargetProjects(Args args)
	{
		Project project = LoadProject();

		if (!args.Has("workspace", "w"))
		{
			return [project];
		}

		if (project.Workspace is null)
		{
			throw new CraterException($"{Fmt.File(project.ManifestPath)} is not part of a workspace.");
		}

		return project.Workspace.AllProjectDirectories().Select(Project.LoadExact).ToList();
	}

	public static void Heading(Project project, int total)
	{
		if (total > 1)
		{
			Out.Info("");
			Out.Heading($"{project.Manifest.Name} ({Fmt.File(project.Directory)})");
		}
	}
}

public static class ManifestEditor
{
	public static JsonArray ListFor(JsonObject manifest, string field)
	{
		if (manifest[field] is JsonArray existing)
		{
			return existing;
		}

		JsonArray created = [];
		manifest[field] = created;

		return created;
	}

	public static int RemoveWhere(JsonArray list, Func<DependencySpec, bool> match)
	{
		int removed = 0;

		for (int i = list.Count - 1; i >= 0; i--)
		{
			if (list[i] is not JsonValue value || !value.TryGetValue(out string? text))
			{
				continue;
			}

			if (match(DependencySpec.Parse(text)))
			{
				list.RemoveAt(i);
				removed++;
			}
		}

		return removed;
	}

	public static int ReplaceWhere(JsonArray list, Func<DependencySpec, DependencySpec?> replace)
	{
		int replaced = 0;

		for (int i = 0; i < list.Count; i++)
		{
			if (list[i] is not JsonValue value || !value.TryGetValue(out string? text))
			{
				continue;
			}

			DependencySpec? updated = replace(DependencySpec.Parse(text));

			if (updated is not null)
			{
				list[i] = updated.ToString();
				replaced++;
			}
		}

		return replaced;
	}

	public static async Task SaveAndInstallAsync(Project project, JsonObject manifest, Func<Project, Task> install)
	{
		string original = File.ReadAllText(project.ManifestPath);

		Json.WriteText(project.ManifestPath, Json.Serialize(manifest));

		try
		{
			await install(Project.LoadExact(project.Directory));
		}
		catch
		{
			File.WriteAllText(project.ManifestPath, original);
			Out.Warn($"{Fmt.File(Manifest.FileName)} was restored to its previous state");

			throw;
		}
	}
}
