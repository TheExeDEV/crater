using Crater.Util;

namespace Crater.Core;

public sealed class PackageLayout
{
	public const string MarkerFile = ".crater";

	public Dictionary<ResolvedPackage, string> Folders { get; } = [];
	public Dictionary<string, ResolvedPackage> Aliases { get; } = new(StringComparer.Ordinal);

	readonly Dictionary<string, ResolvedPackage> names = new(StringComparer.Ordinal);
	readonly Dictionary<string, ResolvedPackage> folderNames = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, ResolvedPackage> aliasNames = new(StringComparer.OrdinalIgnoreCase);
	readonly string folderName;

	PackageLayout(string folderName)
	{
		this.folderName = folderName;
	}

	public static PackageLayout Plan(ResolvedGraph graph, string folderName)
	{
		PackageLayout layout = new(folderName);

		foreach (ResolvedDependency root in graph.Roots)
		{
			layout.Place(root.Package, root.LinkName, null);
		}

		foreach (ResolvedPackage package in graph.All.Where(package => !layout.Folders.ContainsKey(package)))
		{
			string? wanted = graph.All
				.SelectMany(owner => owner.Dependencies.Select(edge => (owner, edge)))
				.Where(pair => pair.edge.Package == package)
				.Select(pair => pair.edge.LinkName)
				.FirstOrDefault(name => !layout.names.ContainsKey(name) && !layout.folderNames.ContainsKey(name));

			layout.Place(package, wanted ?? package.ShortName, null);
		}

		foreach (ResolvedPackage owner in graph.All)
		{
			foreach (ResolvedDependency edge in owner.Dependencies)
			{
				layout.Place(edge.Package, edge.LinkName, owner);
			}
		}

		return layout;
	}

	public static bool IsManaged(string folder)
	{
		return File.Exists(Path.Combine(folder, MarkerFile)) || Directory.Exists(Path.Combine(folder, Layout.LegacyIndexFolder));
	}

	public static Dictionary<string, string> ReadMarker(string folder)
	{
		string path = Path.Combine(folder, MarkerFile);

		return File.Exists(path) ? Json.ReadFile<Dictionary<string, string>>(path) : [];
	}

	public void WriteMarker(string folder)
	{
		Dictionary<string, string> entries = Folders.ToDictionary(pair => pair.Key.Id, pair => pair.Value);

		Json.WriteText(Path.Combine(folder, MarkerFile), Json.Serialize(entries));
	}

	void Place(ResolvedPackage package, string name, ResolvedPackage? owner)
	{
		if (names.TryGetValue(name, out ResolvedPackage? taken))
		{
			if (taken == package)
			{
				return;
			}

			throw Clash(name, package, taken, owner);
		}

		bool isAlias = Folders.ContainsKey(package);
		Dictionary<string, ResolvedPackage> files = isAlias ? aliasNames : folderNames;

		if (files.TryGetValue(name, out ResolvedPackage? sameOnDisk))
		{
			throw Clash(name, package, sameOnDisk, owner);
		}

		names[name] = package;
		files[name] = package;

		if (isAlias)
		{
			Aliases[name] = package;
		}
		else
		{
			Folders[package] = name;
		}
	}

	CraterException Clash(string name, ResolvedPackage wanted, ResolvedPackage taken, ResolvedPackage? owner)
	{
		string who = owner is null ? "your project" : Fmt.Pkg(owner.Id);

		return new CraterException(
			$"Two packages need the name {Fmt.Val(name)} in {Fmt.File(folderName)}: {Fmt.Pkg(taken.Id)} and {Fmt.Pkg(wanted.Id)} (used by {who}). " +
			$"If one of them is your own dependency, add it under another name, for example {Fmt.Cmd($"\"{name}2:{taken.Name}^\"")}.");
	}
}
