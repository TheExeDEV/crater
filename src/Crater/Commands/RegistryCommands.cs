using Crater.Cli;
using Crater.Registry;
using Crater.Site;
using Crater.Util;

namespace Crater.Commands;

public static class RegistryCommands
{
	public static async Task<int> RunAsync(Args args)
	{
		Args sub = args.Shift();

		switch (sub.Command)
		{
			case "init":
				Init(sub);
				return 0;

			case "check":
				return Check(sub);

			case "catalog":
				WriteCatalog(sub);
				return 0;

			case "scan":
				return Scan(sub);

			case "site":
				await SiteAsync(sub);
				return 0;

			default:
				PrintUsage();

				return sub.Command is null ? 0 : 1;
		}
	}

	static void PrintUsage()
	{
		(string Usage, string Description)[] entries =
		[
			("init <folder> --repo Owner/Repo", "create a registry repository from the template"),
			("check [folder] [--base ref] [--author login]", "validate packages and pull request changes"),
			("catalog [folder]", "regenerate packages.json"),
			("scan <package.zip|folder>", "look for suspicious code in a package"),
			("site [folder] [-o out] [--serve] [--port n]", "build and preview the package website"),
		];

		Out.Info($"Usage: {Fmt.Cmd("crater registry <init|check|catalog|scan|site>")}");
		Out.Info("");

		foreach ((string usage, string description) in entries)
		{
			Out.Info($"  {Fmt.Cmd(usage.PadRight(46))} {description}");
		}
	}

	static void Init(Args args)
	{
		string target = Path.GetFullPath(args.First ?? throw new CraterException($"Usage: {Fmt.Cmd("crater registry init <folder> --repo Owner/Repo")}"));
		string repository = args.Value("repo") ?? throw new CraterException($"Pass the GitHub repository with {Fmt.Cmd("--repo Owner/Repo")}.");
		string[] parts = repository.Split('/');

		if (parts.Length != 2 || parts.Any(part => part.Length == 0))
		{
			throw new CraterException($"{Fmt.Val(repository)} is not Owner/Repo.");
		}

		string owner = parts[0];
		string name = parts[1];
		string branch = args.Value("branch") ?? "main";
		string craterRepository = args.Value("crater-repo") ?? (BuildInfo.UpdateRepo.Length > 0 ? BuildInfo.UpdateRepo : "Owner/crater");
		string siteUrl = $"https://{owner.ToLowerInvariant()}.github.io/{name}/";

		Dictionary<string, string> values = new()
		{
			["NAME"] = args.Value("name") ?? "Crater Registry",
			["OWNER"] = owner,
			["REPO"] = name,
			["BRANCH"] = branch,
			["CRATER_REPO"] = craterRepository,
			["SITE_URL"] = siteUrl,
			["YEAR"] = DateTime.UtcNow.Year.ToString(),
		};

		Directory.CreateDirectory(target);
		Out.Step($"Writing registry template to {Fmt.File(target)}");
		Templates.WriteTo("registry", target, values, overwrite: args.Has("force"));

		string infoPath = Path.Combine(target, RegistryInfo.FileName);

		if (!File.Exists(infoPath) || args.Has("force"))
		{
			RegistryInfo info = new()
			{
				Name = values["NAME"],
				Description = "",
				Url = $"https://raw.githubusercontent.com/{owner}/{name}/{branch}",
				SiteUrl = siteUrl,
				Repository = repository,
				Maintainers = [args.Value("maintainer") ?? owner],
			};

			Json.WriteText(infoPath, Json.Serialize(info));
			Out.Dim($"  wrote {RegistryInfo.FileName}");
		}

		new LocalRegistry(target).WriteCatalog();
		Out.Dim($"  wrote {Catalog.FileName}");

		Out.Ok("Registry template is ready");
		Out.Info("");
		Out.Info("Next steps:");
		Out.Info($"  1. Create the GitHub repository {Fmt.Val(repository)} and push this folder to {Fmt.Val(branch)}.");
		Out.Info($"  2. In Settings → Pages, set Source to {Fmt.Val("GitHub Actions")}.");
		Out.Info($"  3. Make sure {Fmt.Val(craterRepository)} is the public crater repository the workflows build from.");
		Out.Info($"  4. Users set {Fmt.Cmd($"\"registry\": \"github:{repository}\"")} in their crater.json.");
	}

	static int Check(Args args)
	{
		string root = Path.GetFullPath(args.First ?? ".");
		RegistryChecker checker = new(root);
		string? baseRef = args.Value("base");

		using (new Spinner("Checking registry"))
		{
			checker.CheckContents();

			if (baseRef is not null)
			{
				checker.CheckChanges(baseRef, args.Value("author"));
			}
		}

		if (baseRef is not null)
		{
			ScanReport.Print(checker.Findings);
			ScanReport.PrintSummaryLine(checker.Findings, checker.ScannedVersions);
			ScanReport.WriteGitHubSummary(checker.Findings, checker.ScannedVersions);
		}

		foreach (string warning in checker.Warnings)
		{
			Out.Warn(warning);
		}

		foreach (string error in checker.Errors)
		{
			Out.Error(error);
		}

		string summary = $"{Fmt.Plural(checker.PackageCount, "package", "packages")}, {Fmt.Plural(checker.VersionCount, "version", "versions")}";

		if (checker.Errors.Count > 0)
		{
			Out.Error($"{Fmt.Plural(checker.Errors.Count, "problem", "problems")} in {summary}");
			return 1;
		}

		Out.Ok($"Registry is valid ({summary})");

		return 0;
	}

	static int Scan(Args args)
	{
		string target = Path.GetFullPath(args.First ?? throw new CraterException($"Usage: {Fmt.Cmd("crater registry scan <package.zip|folder>")}"));
		string label = Path.GetFileNameWithoutExtension(target);
		List<Finding> findings;

		if (File.Exists(target))
		{
			findings = PackageScanner.ScanArchive(File.ReadAllBytes(target), label);
		}
		else if (Directory.Exists(target))
		{
			findings = PackageScanner.ScanDirectory(target, label);
		}
		else
		{
			throw new CraterException($"{Fmt.File(target)} not found.");
		}

		ScanReport.Print(findings);
		ScanReport.PrintSummaryLine(findings, 1);

		return findings.Any(finding => finding.Severity == Severity.Blocked) ? 1 : 0;
	}

	static void WriteCatalog(Args args)
	{
		string root = Path.GetFullPath(args.First ?? ".");
		LocalRegistry registry = new(root);

		registry.WriteCatalog();

		Catalog catalog = Json.ReadFile<Catalog>(Path.Combine(root, Catalog.FileName));

		Out.Ok($"Wrote {Fmt.File(Catalog.FileName)} with {Fmt.Plural(catalog.Packages.Count, "package", "packages")}");
	}

	static async Task SiteAsync(Args args)
	{
		string root = Path.GetFullPath(args.First ?? ".");
		string output = Path.GetFullPath(args.Value("o", "output") ?? Path.Combine(root, "_site"));
		string craterRepository = args.Value("crater-repo") ?? (BuildInfo.UpdateRepo.Length > 0 ? BuildInfo.UpdateRepo : "Owner/crater");

		int count = SiteBuilder.Build(root, output, craterRepository);

		Out.Ok($"Built the site with {Fmt.Plural(count, "package", "packages")} in {Fmt.File(output)}");

		if (args.Has("serve"))
		{
			int port = int.TryParse(args.Value("port"), out int parsed) ? parsed : 8080;

			await SiteServer.ServeAsync(output, port);
		}
	}
}
