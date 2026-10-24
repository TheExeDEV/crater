using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace Crater.Util;

public static class GitHubApi
{
	const string Root = "https://api.github.com/";

	static string? login;

	public sealed record Result(int Status, JsonElement? Json)
	{
		public bool Success => Status is >= 200 and < 300;

		public string Message => Json is { ValueKind: JsonValueKind.Object } json && json.TryGetProperty("message", out JsonElement message)
			? message.GetString() ?? $"HTTP {Status}"
			: $"HTTP {Status}";
	}

	public static async Task<JsonElement?> GetAsync(string path)
	{
		Result result = await SendAsync(HttpMethod.Get, path);

		if (result.Status == 404)
		{
			return null;
		}

		return result.Success ? result.Json : throw new CraterException($"GitHub request failed ({path}): {result.Message}");
	}

	public static async Task<Result> SendAsync(HttpMethod method, string path, JsonNode? body = null)
	{
		HttpContent? content = body is null ? null : new StringContent(body.ToJsonString(), Encoding.UTF8, "application/json");
		Http.Reply reply = await Http.SendAsync(method, Root + path, content, GitHubAuth.Headers("application/vnd.github+json"));
		JsonElement? json = null;

		if (reply.Body.Length > 0)
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(reply.Body);
				json = document.RootElement.Clone();
			}
			catch (JsonException)
			{
			}
		}

		return new Result(reply.Status, json);
	}

	public static async Task<string> RequireLoginAsync()
	{
		if (login is not null)
		{
			return login;
		}

		if (GitHubAuth.Token() is null)
		{
			throw new CraterException($"You are not signed in to GitHub. Run {Fmt.Cmd("crater login")}.");
		}

		Result result = await SendAsync(HttpMethod.Get, "user");

		if (result.Status == 401)
		{
			throw new CraterException($"Your GitHub sign-in ({GitHubAuth.Source()}) has expired or was revoked. Run {Fmt.Cmd("crater login")} again.");
		}

		login = result.Success ? result.Json!.Value.GetProperty("login").GetString() : null;

		return login ?? throw new CraterException($"Could not read your GitHub account: {result.Message}");
	}

	public static async Task<bool> HasPermissionAsync(string repository, string permission)
	{
		Result result = await SendAsync(HttpMethod.Get, $"repos/{repository}");

		return result.Success &&
			result.Json!.Value.TryGetProperty("permissions", out JsonElement permissions) &&
			permissions.TryGetProperty(permission, out JsonElement value) &&
			value.GetBoolean();
	}

	public static async Task<string?> ParentOfAsync(string repository)
	{
		Result result = await SendAsync(HttpMethod.Get, $"repos/{repository}");

		if (!result.Success)
		{
			return null;
		}

		return result.Json!.Value.TryGetProperty("parent", out JsonElement parent)
			? parent.GetProperty("full_name").GetString() ?? ""
			: "";
	}

	public static async Task<string> RequireAsync(HttpMethod method, string path, JsonNode? body, string property)
	{
		Result result = await SendAsync(method, path, body);

		if (!result.Success || result.Json is not { ValueKind: JsonValueKind.Object } json)
		{
			throw new CraterException($"GitHub request failed ({path}): {result.Message}");
		}

		return Read(json, property);
	}

	static string Read(JsonElement json, string path)
	{
		JsonElement current = json;

		foreach (string part in path.Split('.'))
		{
			current = current.GetProperty(part);
		}

		return current.GetString() ?? "";
	}
}
