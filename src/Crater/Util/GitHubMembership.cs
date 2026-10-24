using System.Collections.Concurrent;

namespace Crater.Util;

public static class GitHubMembership
{
	static readonly ConcurrentDictionary<string, bool> Known = new(StringComparer.OrdinalIgnoreCase);

	public static string PeopleUrl(string organization) => $"https://github.com/orgs/{organization}/people";

	public static bool IsPublicMember(string organization, string login)
	{
		return Known.GetOrAdd($"{organization}/{login}", _ => Check(organization, login));
	}

	static bool Check(string organization, string login)
	{
		Dictionary<string, string> headers = new() { ["Accept"] = "application/vnd.github+json" };
		string? token = Environment.GetEnvironmentVariable("GITHUB_TOKEN");

		if (!string.IsNullOrWhiteSpace(token))
		{
			headers["Authorization"] = $"Bearer {token}";
		}

		string url = $"https://api.github.com/orgs/{organization}/public_members/{login}";

		return Http.GetBytesOrNullAsync(url, null, headers).GetAwaiter().GetResult() is not null;
	}
}
