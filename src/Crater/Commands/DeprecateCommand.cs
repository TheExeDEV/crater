using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class DeprecateCommand
{
	public static async Task<int> RunAsync(Args args)
	{
		string target = args.First ?? throw new CraterException(
			$"Usage: {Fmt.Cmd("crater deprecate scope/name \"use other/package instead\"")}, " +
			$"{Fmt.Cmd("crater deprecate scope/name@1.2.0 \"breaks on mobile, use 1.2.1\"")} or {Fmt.Cmd("crater deprecate scope/name --undo")}");

		(string name, string? version) = ParseTarget(target);
		bool undo = args.Has("undo");
		string? message = undo ? null : string.Join(' ', args.Positional.Skip(1)).Trim() is { Length: > 0 } given ? given : DefaultMessage(version);
		string location = RegistryLocation(args, out string baseDirectory);
		GitHubRepository? github = GitHubRepository.FromRegistry(Registries.Expand(location));
		Func<JsonObject, string> edit = index => Apply(index, name, version, message);

		if (github is not null)
		{
			Prompter prompt = new(!args.Has("y", "yes"));

			await new GitHubPublisher(github, prompt, args.Has("dry-run")).ChangeIndexAsync(name, edit);
			return 0;
		}

		if (Registries.Open(location, baseDirectory) is not LocalRegistry local)
		{
			throw new CraterException($"{Fmt.File(location)} is not a GitHub or local registry, so crater cannot change it.");
		}

		Out.Ok(local.ChangeIndex(name, edit));

		return 0;
	}

	public static string Apply(JsonObject index, string name, string? version, string? message)
	{
		if (version is null)
		{
			JsonObject reordered = [];

			foreach ((string key, JsonNode? value) in index.ToList())
			{
				index.Remove(key);

				if (key == "deprecated")
				{
					continue;
				}

				reordered[key] = value;

				if (key == "name" && message is not null)
				{
					reordered["deprecated"] = message;
				}
			}

			foreach ((string key, JsonNode? value) in reordered.ToList())
			{
				reordered.Remove(key);
				index[key] = value;
			}

			return message is null ? $"{Fmt.Pkg(name)} is no longer deprecated" : $"{Fmt.Pkg(name)} is deprecated: {message}";
		}

		JsonObject release = (index["versions"] as JsonArray ?? [])
			.OfType<JsonObject>()
			.FirstOrDefault(item => item["version"]?.GetValue<string>() == version)
			?? throw new CraterException($"{Fmt.Pkg($"{name}@{version}")} is not published.");

		release.Remove("deprecated");

		if (message is not null)
		{
			release["deprecated"] = message;
		}

		string id = Fmt.Pkg($"{name}@{version}");

		return message is null ? $"{id} is no longer deprecated" : $"{id} is deprecated: {message}";
	}

	static (string Name, string? Version) ParseTarget(string target)
	{
		int at = target.IndexOf('@');
		string name = (at < 0 ? target : target[..at]).ToLowerInvariant();
		string? version = at < 0 ? null : target[(at + 1)..];

		if (!DependencySpec.IsValidPackageName(name))
		{
			throw new CraterException($"{Fmt.Val(target)} is not a package. Expected {Fmt.Val("scope/name")} or {Fmt.Val("scope/name@1.2.3")}.");
		}

		if (version is not null)
		{
			if (!SemVer.TryParse(version, out SemVer parsed, out int parts) || parts != 3)
			{
				throw new CraterException($"{Fmt.Val(version)} is not a full version. Expected for example {Fmt.Val($"{name}@1.2.3")}.");
			}

			version = parsed.ToString();
		}

		return (name, version);
	}

	static string DefaultMessage(string? version)
	{
		return version is null ? "this package is no longer maintained" : "do not use this version";
	}

	public static string RegistryLocation(Args args, out string baseDirectory)
	{
		Project? project = null;

		try
		{
			project = Project.Load(Environment.CurrentDirectory);
		}
		catch (CraterException)
		{
		}

		baseDirectory = project?.Directory ?? Environment.CurrentDirectory;

		if (args.Value("registry") is string explicitRegistry)
		{
			return NamedRegistries.LooksLikeName(explicitRegistry) ? NamedRegistries.Location(explicitRegistry, project?.Manifest) : explicitRegistry;
		}

		return project?.RegistryLocation ?? BuildInfo.DefaultRegistry;
	}
}
