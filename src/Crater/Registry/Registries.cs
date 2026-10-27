using Crater.Util;

namespace Crater.Registry;

public static class Registries
{
	const string GitHubPrefix = "github:";
	const string GitHubWebPrefix = "https://github.com/";

	static readonly Dictionary<string, IRegistry> Opened = new(StringComparer.OrdinalIgnoreCase);

	public static IRegistry Open(string location, string baseDirectory)
	{
		string expanded = Expand(location);

		if (expanded.Length == 0)
		{
			throw new CraterException("No registry configured.");
		}

		if (IsHttp(expanded))
		{
			string url = expanded.TrimEnd('/');

			if (ForestRegistry.IsForest(url))
			{
				url = ForestRegistry.DefaultApi;
			}

			if (!Opened.TryGetValue(url, out IRegistry? remote))
			{
				remote = ForestRegistry.IsForest(url) ? new ForestRegistry(url) : EmberRegistry.IsEmber(url) ? new EmberRegistry(url) : new HttpRegistry(url);
				Opened[url] = remote;
			}

			return remote;
		}

		string path = expanded.StartsWith("file://", StringComparison.OrdinalIgnoreCase)
			? new Uri(expanded).LocalPath
			: expanded;

		string fullPath = Path.GetFullPath(Path.Combine(baseDirectory, path));

		if (!Opened.TryGetValue(fullPath, out IRegistry? local))
		{
			local = new LocalRegistry(fullPath);
			Opened[fullPath] = local;
		}

		return local;
	}

	public static string Expand(string location)
	{
		string trimmed = location.Trim();

		if (trimmed.StartsWith(GitHubWebPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return ExpandWebUrl(location, trimmed[GitHubWebPrefix.Length..]);
		}

		if (!trimmed.StartsWith(GitHubPrefix, StringComparison.OrdinalIgnoreCase))
		{
			return trimmed;
		}

		string body = trimmed[GitHubPrefix.Length..].Trim('/');
		string branch = "main";
		int at = body.IndexOf('@');

		if (at >= 0)
		{
			branch = body[(at + 1)..];
			body = body[..at];
		}

		string[] parts = body.Split('/');

		if (parts.Length != 2 || parts.Any(part => part.Length == 0) || branch.Length == 0)
		{
			throw InvalidLocation(location);
		}

		return RawUrl(parts[0], parts[1], branch);
	}

	static string ExpandWebUrl(string location, string path)
	{
		string[] parts = path.Trim('/').Split('/');

		if (parts.Length < 2 || parts[0].Length == 0 || parts[1].Length == 0)
		{
			throw InvalidLocation(location);
		}

		string repository = parts[1].EndsWith(".git", StringComparison.OrdinalIgnoreCase) ? parts[1][..^4] : parts[1];

		if (parts.Length == 2)
		{
			return RawUrl(parts[0], repository, "main");
		}

		if (parts.Length >= 4 && parts[2] == "tree")
		{
			return RawUrl(parts[0], repository, string.Join('/', parts[3..]));
		}

		throw InvalidLocation(location);
	}

	static string RawUrl(string owner, string repository, string branch)
	{
		return $"https://raw.githubusercontent.com/{owner}/{repository}/{branch}";
	}

	static CraterException InvalidLocation(string location)
	{
		return new CraterException(
			$"Invalid registry {Fmt.Val(location)}. Expected {Fmt.Cmd("https://github.com/Owner/Repo")} " +
			$"or {Fmt.Cmd("https://github.com/Owner/Repo/tree/branch")}.");
	}


	public static bool IsAbsolute(string location)
	{
		string expanded = Expand(location);

		return IsHttp(expanded) ||
			expanded.StartsWith("file://", StringComparison.OrdinalIgnoreCase) ||
			Path.IsPathRooted(expanded);
	}

	static bool IsHttp(string location)
	{
		return location.StartsWith("http://", StringComparison.OrdinalIgnoreCase) ||
			location.StartsWith("https://", StringComparison.OrdinalIgnoreCase);
	}

	internal static (string Scope, string Name) Split(string fullName)
	{
		int slash = fullName.IndexOf('/');

		return (fullName[..slash], fullName[(slash + 1)..]);
	}
}
