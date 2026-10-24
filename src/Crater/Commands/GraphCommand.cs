using System.Text;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Commands;

public static class GraphCommand
{
	public static int Run(Args args)
	{
		Project project = Common.LoadProject();
		LockFile lockFile = LockFile.Load(project.LockPath);

		if (lockFile.Packages.Count == 0)
		{
			Out.Info($"No packages installed. Run {Fmt.Cmd("crater install")} first.");
			return 0;
		}

		Realm? only = Common.RealmFlag(args);
		int depth = int.TryParse(args.Value("depth"), out int parsed) && parsed > 0 ? parsed : int.MaxValue;
		string mermaid = Mermaid(project, lockFile, only, depth);
		string? output = args.Value("o", "output");

		if (output is null)
		{
			Console.Out.Write(mermaid);
			return 0;
		}

		bool markdown = output.EndsWith(".md", StringComparison.OrdinalIgnoreCase);

		File.WriteAllText(Path.GetFullPath(output), markdown ? $"```mermaid\n{mermaid}```\n" : mermaid);
		Out.Ok($"Wrote {Fmt.File(output)}{(markdown ? ", GitHub shows it as a diagram" : "")}");

		return 0;
	}

	public static string Mermaid(Project project, LockFile lockFile, Realm? only, int depth)
	{
		StringBuilder text = new();

		text.Append("graph LR\n");
		text.Append($"  root[\"{Escape(project.Manifest.Name)}\"]\n");

		foreach (Realm realm in Realms.All.Where(realm => only is null || realm == only))
		{
			Dictionary<string, LockedPackage> packages = lockFile.For(realm).ToDictionary(package => package.Id);

			if (packages.Count == 0)
			{
				continue;
			}

			string key = realm.Key();
			List<LockedPackage> nodes = [];
			List<string> edges = [];
			HashSet<string> seen = [];

			string NodeId(LockedPackage package) => $"{key}_{nodes.IndexOf(package)}";

			void Add(LockedPackage package)
			{
				if (seen.Add(package.Id))
				{
					nodes.Add(package);
				}
			}

			List<LockedPackage> direct = packages.Values.Where(package => package.Direct).OrderBy(package => package.Name, StringComparer.Ordinal).ToList();
			Queue<(LockedPackage Package, int Level)> queue = new();
			HashSet<string> expanded = [];

			foreach (LockedPackage package in direct)
			{
				Add(package);

				foreach (string link in package.Links ?? [package.ShortName])
				{
					edges.Add($"  root -->{Label(link, package)} {NodeId(package)}");
				}

				queue.Enqueue((package, 1));
			}

			while (queue.TryDequeue(out (LockedPackage Package, int Level) item))
			{
				if (item.Level >= depth || !expanded.Add(item.Package.Id))
				{
					continue;
				}

				foreach (string entry in item.Package.Dependencies)
				{
					(string? alias, string id) = ResolvedDependency.ParseLockEntry(entry);

					if (packages.TryGetValue(id, out LockedPackage? child))
					{
						Add(child);
						edges.Add($"  {NodeId(item.Package)} -->{Label(alias, child)} {NodeId(child)}");
						queue.Enqueue((child, item.Level + 1));
					}
				}
			}

			text.Append($"  subgraph {key}[\"{Escape(project.Config.FolderFor(realm))}\"]\n");

			foreach (LockedPackage package in nodes)
			{
				text.Append($"    {NodeId(package)}[\"{Escape(package.Name)}<br/>{Escape(package.Version)}\"]\n");
			}

			text.Append("  end\n");

			foreach (string edge in edges.Distinct())
			{
				text.Append(edge).Append('\n');
			}
		}

		return text.ToString();
	}

	static string Label(string? alias, LockedPackage package)
	{
		return alias is null || alias == package.ShortName ? "" : $"|{Escape(alias)}|";
	}

	static string Escape(string text)
	{
		return text.Replace("\"", "#quot;");
	}
}
