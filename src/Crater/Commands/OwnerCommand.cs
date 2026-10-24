using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using Crater.Cli;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static partial class OwnerCommand
{
	[GeneratedRegex("^[A-Za-z0-9](?:[A-Za-z0-9-]{0,38})$")]
	private static partial Regex LoginRegex();

	public static async Task<int> RunAsync(Args args)
	{
		Args rest = args.Shift();
		string usage = $"Usage: {Fmt.Cmd("crater owner add <scope/name> <github-login>")}, " +
			$"{Fmt.Cmd("crater owner remove <scope/name> <github-login>")} or {Fmt.Cmd("crater owner list <scope/name>")}";

		string name = (rest.First ?? throw new CraterException(usage)).ToLowerInvariant();

		if (!DependencySpec.IsValidPackageName(name))
		{
			throw new CraterException($"{Fmt.Val(name)} is not a package. Expected {Fmt.Val("scope/name")}.");
		}

		string location = DeprecateCommand.RegistryLocation(args, out string baseDirectory);

		switch (rest.Command)
		{
			case "list" or "ls":
				await ListAsync(name, location, baseDirectory);
				return 0;

			case "add" or "remove" or "rm":
				List<string> logins = rest.Positional.Skip(1).Select(login => login.Trim().TrimStart('@')).ToList();

				if (logins.Count == 0)
				{
					throw new CraterException(usage);
				}

				if (logins.FirstOrDefault(login => !LoginRegex().IsMatch(login)) is string invalid)
				{
					throw new CraterException($"{Fmt.Val(invalid)} is not a GitHub login.");
				}

				bool add = rest.Command == "add";

				await ChangeAsync(args, location, baseDirectory, name, index => Apply(index, name, logins, add));
				return 0;

			default:
				throw new CraterException(usage);
		}
	}

	public static string Apply(JsonObject index, string name, List<string> logins, bool add)
	{
		string scope = name[..name.IndexOf('/')];
		List<string> owners = (index["owners"] as JsonArray ?? [])
			.Select(node => node?.GetValue<string>() ?? "")
			.Where(owner => owner.Length > 0)
			.ToList();

		foreach (string login in logins)
		{
			if (add && login.Equals(scope, StringComparison.OrdinalIgnoreCase))
			{
				throw new CraterException($"{Fmt.Val(login)} already owns everything in {Fmt.Pkg(scope + "/")}.");
			}

			owners.RemoveAll(owner => owner.Equals(login, StringComparison.OrdinalIgnoreCase));

			if (add)
			{
				owners.Add(login.ToLowerInvariant());
			}
		}

		JsonObject reordered = [];

		foreach ((string key, JsonNode? value) in index.ToList())
		{
			index.Remove(key);

			if (key == "owners")
			{
				continue;
			}

			if (key == "versions" && owners.Count > 0)
			{
				reordered["owners"] = new JsonArray(owners.Order(StringComparer.Ordinal).Select(owner => (JsonNode)owner).ToArray());
			}

			reordered[key] = value;
		}

		foreach ((string key, JsonNode? value) in reordered.ToList())
		{
			reordered.Remove(key);
			index[key] = value;
		}

		string who = string.Join(", ", logins.Select(login => Fmt.Val(login)));

		return add ? $"{who} can now publish {Fmt.Pkg(name)}" : $"{who} can no longer publish {Fmt.Pkg(name)}";
	}

	static async Task ChangeAsync(Args args, string location, string baseDirectory, string name, Func<JsonObject, string> edit)
	{
		GitHubRepository? github = GitHubRepository.FromRegistry(Registries.Expand(location));

		if (github is not null)
		{
			await new GitHubPublisher(github, new Prompter(!args.Has("y", "yes")), args.Has("dry-run")).ChangeIndexAsync(name, edit, scopeOnly: true);
			return;
		}

		if (Registries.Open(location, baseDirectory) is not LocalRegistry local)
		{
			throw new CraterException($"{Fmt.File(location)} is not a GitHub or local registry, so crater cannot change it.");
		}

		Out.Ok(local.ChangeIndex(name, edit));
	}

	static async Task ListAsync(string name, string location, string baseDirectory)
	{
		RegistryIndex index = await Registries.Open(location, baseDirectory).GetIndexAsync(name)
			?? throw new CraterException($"{Fmt.Pkg(name)} is not in the registry {Fmt.File(location)}.");

		string scope = name[..name.IndexOf('/')];

		Out.Heading($"Who can publish {name}");
		Out.Info($"  {Fmt.Val(scope)} (the scope, or members of the {scope} organization)");

		foreach (string owner in index.Owners ?? [])
		{
			Out.Info($"  {Fmt.Val(owner)}");
		}

		if (index.Owners is not { Count: > 0 })
		{
			Out.Dim($"  Add someone with {Fmt.Cmd($"crater owner add {name} <github-login>")}.");
		}
	}
}
