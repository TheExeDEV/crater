using Crater.Util;

namespace Crater.Registry;

public static class Registries
{
	const string GitHubPrefix = "github:";

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

			if (!Opened.TryGetValue(url, out IRegistry? remote))
			{
				remote = new HttpRegistry(url);
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
			throw new CraterException($"Invalid registry {Fmt.Val(location)}. Expected {Fmt.Cmd("github:Owner/Repo")} or {Fmt.Cmd("github:Owner/Repo@branch")}.");
		}

		return $"https://raw.githubusercontent.com/{parts[0]}/{parts[1]}/{branch}";
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
