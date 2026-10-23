using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public sealed class Workspace
{
	public required string Root { get; init; }
	public required Dictionary<string, string> Members { get; init; }

	public static Workspace? Find(string projectDirectory, Manifest projectManifest)
	{
		if (projectManifest.Workspace is { Count: > 0 })
		{
			return FromRoot(projectDirectory, projectManifest);
		}

		DirectoryInfo? parent = new DirectoryInfo(projectDirectory).Parent;

		while (parent is not null)
		{
			string manifestPath = Path.Combine(parent.FullName, Manifest.FileName);

			if (File.Exists(manifestPath))
			{
				Manifest manifest = Manifest.Load(manifestPath);

				if (manifest.Workspace is { Count: > 0 })
				{
					Workspace workspace = FromRoot(parent.FullName, manifest);
					bool isMember = workspace.Members.Values.Any(member => SamePath(member, projectDirectory));

					return isMember ? workspace : null;
				}
			}

			parent = parent.Parent;
		}

		return null;
	}

	static Workspace FromRoot(string root, Manifest manifest)
	{
		Dictionary<string, string> members = [];

		foreach (string pattern in manifest.Workspace ?? [])
		{
			foreach (string directory in Expand(root, pattern))
			{
				string memberManifestPath = Path.Combine(directory, Manifest.FileName);

				if (!File.Exists(memberManifestPath))
				{
					continue;
				}

				string name = Manifest.Load(memberManifestPath).Name;

				if (members.TryGetValue(name, out string? existing) && !SamePath(existing, directory))
				{
					throw new CraterException(
						$"Workspace members {Fmt.File(existing)} and {Fmt.File(directory)} are both named {Fmt.Pkg(name)}.");
				}

				members[name] = directory;
			}
		}

		return new Workspace { Root = root, Members = members };
	}

	static IEnumerable<string> Expand(string root, string pattern)
	{
		string normalized = pattern.Trim().Replace('\\', '/').TrimEnd('/');

		if (normalized.EndsWith("/*"))
		{
			string parent = Path.GetFullPath(Path.Combine(root, normalized[..^2]));

			if (!Directory.Exists(parent))
			{
				return [];
			}

			return Directory.EnumerateDirectories(parent).OrderBy(path => path, StringComparer.Ordinal);
		}

		string exact = Path.GetFullPath(Path.Combine(root, normalized));

		return Directory.Exists(exact) ? [exact] : [];
	}

	public string? MemberDirectory(string fullName)
	{
		return Members.TryGetValue(fullName, out string? directory) ? directory : null;
	}

	public IEnumerable<string> AllProjectDirectories()
	{
		yield return Root;

		foreach (string member in Members.Values.OrderBy(path => path, StringComparer.Ordinal))
		{
			if (!SamePath(member, Root))
			{
				yield return member;
			}
		}
	}

	static bool SamePath(string left, string right)
	{
		string a = Path.GetFullPath(left).TrimEnd(Path.DirectorySeparatorChar);
		string b = Path.GetFullPath(right).TrimEnd(Path.DirectorySeparatorChar);

		return a.Equals(b, StringComparison.OrdinalIgnoreCase);
	}
}
