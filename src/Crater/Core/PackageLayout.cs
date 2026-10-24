using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using Crater.Util;

namespace Crater.Core;

public sealed class PackageLayout
{
	public const string MarkerFile = ".crater";
	public const string IsolatedFolder = "_Isolated";

	public Dictionary<ResolvedPackage, string> Folders { get; } = [];
	public Dictionary<string, ResolvedPackage> Aliases { get; } = new(StringComparer.Ordinal);
	public Dictionary<ResolvedPackage, Dictionary<string, ResolvedPackage>> PrivateLinks { get; } = [];

	readonly Dictionary<string, ResolvedPackage> names = new(StringComparer.Ordinal);
	readonly Dictionary<string, ResolvedPackage> folderNames = new(StringComparer.OrdinalIgnoreCase);
	readonly Dictionary<string, ResolvedPackage> aliasNames = new(StringComparer.OrdinalIgnoreCase);
	readonly string folderName;

	PackageLayout(string folderName)
	{
		this.folderName = folderName;
	}

	public bool HasIsolated => PrivateLinks.Count > 0;

	static readonly ConditionalWeakTable<ResolvedGraph, ConcurrentDictionary<string, PackageLayout>> Planned = new();

	public static PackageLayout Plan(ResolvedGraph graph, string folderName)
	{
		ConcurrentDictionary<string, PackageLayout> byFolder = Planned.GetValue(graph, _ => new ConcurrentDictionary<string, PackageLayout>());

		if (byFolder.TryGetValue(folderName, out PackageLayout? known))
		{
			return known;
		}

		PackageLayout layout = Build(graph, folderName);

		byFolder[folderName] = layout;

		return layout;
	}

	static PackageLayout Build(ResolvedGraph graph, string folderName)
	{
		PackageLayout layout = new(folderName);
		Dictionary<ResolvedPackage, List<string>> incoming = [];

		foreach (ResolvedDependency edge in graph.All.SelectMany(owner => owner.Dependencies))
		{
			if (!incoming.TryGetValue(edge.Package, out List<string>? names))
			{
				names = [];
				incoming[edge.Package] = names;
			}

			names.Add(edge.LinkName);
		}

		foreach (ResolvedDependency root in graph.Roots)
		{
			if (!layout.TryClaim(root.LinkName, root.Package, out ResolvedPackage? taken))
			{
				throw layout.Clash(root.LinkName, root.Package, taken!);
			}
		}

		foreach (ResolvedPackage package in graph.All.Where(package => !layout.Folders.ContainsKey(package)))
		{
			IEnumerable<string> wanted = incoming.GetValueOrDefault(package, [])
				.Append(package.ShortName)
				.Append($"{package.Scope}_{package.ShortName}");

			string name = wanted.FirstOrDefault(layout.IsFreeFolder) ?? layout.NumberedFolder($"{package.Scope}_{package.ShortName}");

			layout.TryClaim(name, package, out _);
		}

		List<ResolvedPackage> isolated = [];

		foreach (ResolvedPackage owner in graph.All)
		{
			bool clashes = false;

			foreach (ResolvedDependency edge in owner.Dependencies)
			{
				if (!layout.TryClaim(edge.LinkName, edge.Package, out _))
				{
					clashes = true;
				}
			}

			if (clashes)
			{
				isolated.Add(owner);
			}
		}

		foreach (ResolvedPackage owner in isolated)
		{
			layout.Isolate(owner);
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

	public void WriteMarker(string folder, string patchFingerprint, string linkStyle)
	{
		Dictionary<string, string> entries = Folders.ToDictionary(pair => pair.Key.Id, pair => pair.Value);

		entries[FolderBuilder.StyleKey] = linkStyle;

		if (patchFingerprint.Length > 0)
		{
			entries[Patches.MarkerKey] = patchFingerprint;
		}

		Json.WriteText(Path.Combine(folder, MarkerFile), Json.Serialize(entries));
	}

	public static string PrivateDirectory(string top) => $"{IsolatedFolder}/{top}";

	bool TryClaim(string name, ResolvedPackage package, out ResolvedPackage? taken)
	{
		if (names.TryGetValue(name, out taken))
		{
			return taken == package;
		}

		bool isAlias = Folders.ContainsKey(package);
		Dictionary<string, ResolvedPackage> files = isAlias ? aliasNames : folderNames;

		if (files.TryGetValue(name, out taken))
		{
			return false;
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

		return true;
	}

	bool IsFreeFolder(string name) => !names.ContainsKey(name) && !folderNames.ContainsKey(name);

	string NumberedFolder(string name)
	{
		for (int index = 2; ; index++)
		{
			string candidate = $"{name}_{index}";

			if (IsFreeFolder(candidate))
			{
				return candidate;
			}
		}
	}

	void Isolate(ResolvedPackage owner)
	{
		string top = Folders[owner];

		folderNames.Remove(top);
		aliasNames[top] = owner;
		Aliases[top] = owner;
		Folders[owner] = $"{PrivateDirectory(top)}/{top}";

		PrivateLinks[owner] = owner.Dependencies.ToDictionary(edge => edge.LinkName, edge => edge.Package, StringComparer.Ordinal);
	}

	CraterException Clash(string name, ResolvedPackage wanted, ResolvedPackage taken)
	{
		return new CraterException(
			$"Your project lists two packages under the name {Fmt.Val(name)} in {Fmt.File(folderName)}: {Fmt.Pkg(taken.Id)} and {Fmt.Pkg(wanted.Id)}. " +
			$"Give one of them another name, for example {Fmt.Cmd($"\"{name}2:{wanted.Name}^\"")}.");
	}
}
