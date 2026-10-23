using System.Text;
using Crater.Util;

namespace Crater.Registry;

public static class ScanReport
{
	public static void Print(IReadOnlyList<Finding> findings)
	{
		foreach (Finding finding in Ordered(findings))
		{
			Out.Info($"  {Label(finding.Severity)} {Fmt.Pkg(finding.Package)} {Fmt.File(finding.Location)}  {finding.Description}");

			if (finding.Snippet.Length > 0)
			{
				Out.Dim($"      {finding.Snippet}");
			}
		}
	}

	public static void PrintSummaryLine(IReadOnlyList<Finding> findings, int scanned)
	{
		string versions = Fmt.Plural(scanned, "new version", "new versions");

		if (findings.Count == 0)
		{
			Out.Ok($"Scanned {versions}, nothing suspicious found");
			return;
		}

		int serious = findings.Count(finding => finding.Severity >= Severity.Danger);
		int warnings = findings.Count(finding => finding.Severity == Severity.Warning);
		int notes = findings.Count(finding => finding.Severity == Severity.Info);

		Out.Warn($"Scanned {versions}: {serious} dangerous, {warnings} suspicious, {notes} informational. Review them before merging.");
	}

	public static void WriteGitHubSummary(IReadOnlyList<Finding> findings, int scanned)
	{
		string? path = System.Environment.GetEnvironmentVariable("GITHUB_STEP_SUMMARY");

		if (string.IsNullOrEmpty(path))
		{
			return;
		}

		StringBuilder markdown = new();

		markdown.AppendLine("## Package scan");
		markdown.AppendLine();

		if (scanned == 0)
		{
			markdown.AppendLine("No new package versions in this pull request.");
		}
		else if (findings.Count == 0)
		{
			markdown.AppendLine($"Scanned {scanned} new version(s), nothing suspicious found. Still read the code before merging.");
		}
		else
		{
			markdown.AppendLine($"Scanned {scanned} new version(s). The scanner only points at code worth reading; the decision is the reviewer's.");
			markdown.AppendLine();
			markdown.AppendLine("| Level | Package | File | Finding | Code |");
			markdown.AppendLine("|---|---|---|---|---|");

			foreach (Finding finding in Ordered(findings))
			{
				string code = finding.Snippet.Length > 0 ? $"`{Escape(finding.Snippet).Replace("`", "'")}`" : "";

				markdown.AppendLine($"| {finding.Severity.ToString().ToLowerInvariant()} | {Escape(finding.Package)} | {Escape(finding.Location)} | {Escape(finding.Description)} | {code} |");
			}
		}

		markdown.AppendLine();

		File.AppendAllText(path, markdown.ToString());
	}

	static IEnumerable<Finding> Ordered(IEnumerable<Finding> findings)
	{
		return findings
			.OrderByDescending(finding => finding.Severity)
			.ThenBy(finding => finding.Package, StringComparer.Ordinal)
			.ThenBy(finding => finding.File, StringComparer.Ordinal)
			.ThenBy(finding => finding.Line);
	}

	static string Label(Severity severity) => severity switch
	{
		Severity.Blocked => "[r]blocked[/]",
		Severity.Danger => "[r]danger [/]",
		Severity.Warning => "[c]warning[/]",
		_ => "[v]info   [/]",
	};

	static string Escape(string text)
	{
		return text.Replace("|", "\\|").Replace("\n", " ");
	}
}
