using System.Text.Json.Nodes;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class InteractiveUpdate
{
	sealed record Choice(UpdateCandidate Candidate, bool ToLatest);

	public static async Task RunAsync(Project project, Args args)
	{
		List<UpdateCandidate> plan;

		using (new Spinner("Checking for updates"))
		{
			plan = await UpdatePlanner.PlanAsync(project);
		}

		List<Choice> choices = BuildChoices(plan);

		if (choices.Count == 0)
		{
			Out.Ok("All dependencies are up to date");
			return;
		}

		List<Select.Item> items = choices.Select(ToItem).ToList();
		List<int>? picked = Select.Many("Select packages to update", items);

		if (picked is null || picked.Count == 0)
		{
			Out.Info("Nothing selected.");
			return;
		}

		List<Choice> selected = picked.Select(index => choices[index]).ToList();
		JsonObject manifest = Json.ReadObject(project.ManifestPath);
		int rewritten = RewriteRequirements(manifest, selected.Where(choice => choice.ToLatest));

		HashSet<string> update = selected.Select(choice => choice.Candidate.Name).ToHashSet();
		Prompter prompt = Common.PrompterFor(project, args);

		if (rewritten == 0)
		{
			await new Installer(project, prompt).InstallAsync(update);
			return;
		}

		if (!prompt.Confirm($"Update {Fmt.Plural(rewritten, "requirement", "requirements")} in {Fmt.File(Manifest.FileName)}"))
		{
			throw new CraterException("Cancelled.");
		}

		await ManifestEditor.SaveAndInstallAsync(project, manifest, updated => new Installer(updated, prompt).InstallAsync(update));
	}

	static List<Choice> BuildChoices(List<UpdateCandidate> plan)
	{
		List<Choice> choices = [];

		foreach (UpdateCandidate candidate in plan.Where(candidate => !candidate.IsSource))
		{
			if (candidate.CanUpdate && candidate.Installed is not null)
			{
				choices.Add(new Choice(candidate, ToLatest: false));
			}

			if (candidate.HasNewer && candidate.IsDirect)
			{
				choices.Add(new Choice(candidate, ToLatest: true));
			}
		}

		return choices;
	}

	static Select.Item ToItem(Choice choice)
	{
		UpdateCandidate candidate = choice.Candidate;
		string from = candidate.Installed ?? "-";
		string realm = $"[v]{candidate.Realm.Key()}[/]";

		if (!choice.ToLatest)
		{
			return new Select.Item($"[p]{candidate.Name}[/] {from} → [g]{candidate.Wanted}[/]", true, realm);
		}

		DependencySpec spec = candidate.Direct[0];
		VersionReq next = VersionReq.SameKind(spec.Req, SemVer.Parse(candidate.Latest!));
		string hint = $"{realm} [c]changes {spec.Req} to {next}[/]";

		return new Select.Item($"[p]{candidate.Name}[/] {from} → [c]{candidate.Latest}[/]", false, hint);
	}

	static int RewriteRequirements(JsonObject manifest, IEnumerable<Choice> latest)
	{
		int rewritten = 0;

		foreach (Choice choice in latest)
		{
			UpdateCandidate candidate = choice.Candidate;
			JsonArray list = ManifestEditor.ListFor(manifest, candidate.Realm.ManifestField());
			SemVer target = SemVer.Parse(candidate.Latest!);

			rewritten += ManifestEditor.ReplaceWhere(list, spec =>
			{
				bool isTarget = spec.FullName == candidate.Name && spec.Source is null && !spec.Req.Matches(target);

				return isTarget ? spec.WithReq(VersionReq.SameKind(spec.Req, target)) : null;
			});
		}

		return rewritten;
	}
}
