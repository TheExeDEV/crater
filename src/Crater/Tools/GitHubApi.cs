using System.Text.Json;
using Crater.Util;

namespace Crater.Tools;

public static class GitHubApi
{
	public static async Task<JsonElement?> GetAsync(string path)
	{
		byte[]? data = await Http.GetBytesOrNullAsync($"https://api.github.com/{path}", null, GitHubAuth.Headers("application/vnd.github+json"));

		if (data is null)
		{
			return null;
		}

		using JsonDocument document = JsonDocument.Parse(data);

		return document.RootElement.Clone();
	}
}
