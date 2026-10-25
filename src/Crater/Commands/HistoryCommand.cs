using System.Text.Json;
using Crater.Cli;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class HistoryCommand
{
	const int DefaultLimit = 30;

	public sealed record Change(DateTimeOffset Date, string Who, string What);

	public static async Task<int> RunAsync(Args args)
	{
		string? name = args.First?.ToLowerInvariant();

		if (name is not null && !DependencySpec.IsValidPackageName(name))
		{
			throw new CraterException($"{Fmt.Val(name)} is not a package. Expected {Fmt.Val("scope/name")}, or nothing for the whole registry.");
		}

		int limit = int.TryParse(args.Value("limit", "n"), out int parsed) && parsed > 0 ? parsed : DefaultLimit;
		string location = DeprecateCommand.RegistryLocation(args, out string baseDirectory);
		string path = name is null ? "" : $"{name}/{RegistryIndex.FileName}";
		GitHubRepository? github = GitHubRepository.FromRegistry(Registries.Expand(location));
		List<Change> changes;

		using (new Spinner("Reading the registry history"))
		{
			changes = github is not null
				? await FromGitHubAsync(github, path, limit)
				: FromLocal(Registries.Open(location, baseDirectory), path, name, limit);
		}

		if (changes.Count == 0)
		{
			Out.Info(name is null ? "The registry has no history yet." : $"{Fmt.Pkg(name)} has no history in {Fmt.File(location)}.");
			return 0;
		}

		Out.Heading(name is null ? $"Latest changes in {Registries.Expand(location)}" : $"History of {name}");

		List<IReadOnlyList<string>> rows = changes
			.Select(change => (IReadOnlyList<string>)[change.Date.ToString("yyyy-MM-dd HH:mm"), Fmt.Val(change.Who), change.What])
			.ToList();

		Table.Print(["When", "Who", "What"], rows);

		return 0;
	}

	static async Task<List<Change>> FromGitHubAsync(GitHubRepository registry, string path, int limit)
	{
		string query = $"repos/{registry.Slug}/commits?sha={Uri.EscapeDataString(registry.Branch)}&per_page={Math.Min(100, limit * 2)}" +
			(path.Length > 0 ? $"&path={Uri.EscapeDataString(path)}" : "");
		JsonElement? commits = await GitHubApi.GetAsync(query);

		if (commits is not { ValueKind: JsonValueKind.Array } list)
		{
			throw new CraterException($"GitHub did not return the history of {Fmt.Val(registry.Slug)}.");
		}

		return Parse(list).Take(limit).ToList();
	}

	public static IEnumerable<Change> Parse(JsonElement commits)
	{
		foreach (JsonElement item in commits.EnumerateArray())
		{
			JsonElement commit = item.GetProperty("commit");
			string message = commit.GetProperty("message").GetString() ?? "";
			string title = message.Split('\n')[0].Trim();

			if (title.StartsWith("Merge pull request", StringComparison.Ordinal) || title.StartsWith("Merge branch", StringComparison.Ordinal))
			{
				continue;
			}

			string who = item.TryGetProperty("author", out JsonElement author) && author.ValueKind == JsonValueKind.Object
				? author.GetProperty("login").GetString() ?? ""
				: commit.GetProperty("author").GetProperty("name").GetString() ?? "";
			DateTimeOffset date = commit.GetProperty("author").GetProperty("date").GetDateTimeOffset();

			yield return new Change(date, who, title);
		}
	}

	static List<Change> FromLocal(IRegistry registry, string path, string? name, int limit)
	{
		if (registry is not LocalRegistry local)
		{
			throw new CraterException($"{Fmt.File(registry.Location)} keeps no history crater can read.");
		}

		if (local.IsGitRepository)
		{
			List<string> arguments = ["log", $"-{limit}", "--no-merges", "--format=%aI%x09%an%x09%s"];

			if (path.Length > 0)
			{
				arguments.AddRange(["--", path]);
			}

			return Shell.Run("git", arguments, local.Location)
				.Split('\n', StringSplitOptions.RemoveEmptyEntries)
				.Select(line => line.Split('\t', 3))
				.Where(parts => parts.Length == 3 && DateTimeOffset.TryParse(parts[0], out _))
				.Select(parts => new Change(DateTimeOffset.Parse(parts[0]), parts[1], parts[2]))
				.ToList();
		}

		if (name is null)
		{
			throw new CraterException($"{Fmt.File(local.Location)} is not a git repository, so only package histories are known. Run {Fmt.Cmd("crater history scope/name")}.");
		}

		RegistryIndex? index = local.GetIndexAsync(name).GetAwaiter().GetResult();

		return (index?.Versions ?? [])
			.Where(release => release.PublishedAt is not null)
			.OrderByDescending(release => release.PublishedAt)
			.Take(limit)
			.Select(release => new Change(release.PublishedAt!.Value, "", $"Publish {name}@{release.Version}"))
			.ToList();
	}
}
