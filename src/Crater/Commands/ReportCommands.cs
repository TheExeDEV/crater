using System.Text.Json;
using System.Text.RegularExpressions;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Commands;

public static partial class ReportCommands
{
	static readonly string[] LuauExtensions = [".luau", ".lua"];

	[GeneratedRegex(@"^\s*license\s*=\s*""(?<value>[^""]*)""", RegexOptions.Multiline)]
	private static partial Regex WallyLicense();

	public static void Size()
	{
		Project project = Common.LoadProject();
		List<InstalledPackage> installed = InstalledPackages.Load(project);
		Dictionary<(Realm, string), (int Modules, long Bytes)> own = installed.ToDictionary(
			package => (package.Realm, package.Locked.Id),
			package => Measure(package.Directory));

		List<(InstalledPackage Package, int Modules, long Bytes, long Total)> rows = installed
			.Select(package =>
			{
				(int modules, long bytes) = own[(package.Realm, package.Locked.Id)];
				long total = Closure(package, installed).Sum(item => own[(item.Realm, item.Locked.Id)].Bytes);

				return (package, modules, bytes, total);
			})
			.OrderByDescending(row => row.total)
			.ToList();

		Table.Print(
			["Package", "Realm", "Modules", "Size", "With dependencies"],
			rows.Select(row => (IReadOnlyList<string>)
			[
				Fmt.Pkg(row.Package.Locked.Id),
				row.Package.Realm.Key(),
				row.Modules.ToString(),
				Kilobytes(row.Bytes),
				row.Total == row.Bytes ? Kilobytes(row.Total) : Fmt.Val(Kilobytes(row.Total)),
			]).ToList());

		long everything = own.Values.Sum(item => item.Bytes);

		Out.Info("");
		Out.Info($"Luau code of all packages: {Fmt.Val(Kilobytes(everything))} in {Fmt.Plural(own.Values.Sum(item => item.Modules), "module", "modules")}");
	}

	public static void Licenses()
	{
		Project project = Common.LoadProject();
		List<InstalledPackage> installed = InstalledPackages.Load(project)
			.GroupBy(package => package.Locked.Id)
			.Select(group => group.First())
			.ToList();

		List<(InstalledPackage Package, string License)> rows = installed
			.Select(package => (package, LicenseOf(package.Directory)))
			.OrderBy(row => row.Item2, StringComparer.OrdinalIgnoreCase)
			.ThenBy(row => row.package.Locked.Name, StringComparer.Ordinal)
			.ToList();

		Table.Print(
			["Package", "License", "Note"],
			rows.Select(row => (IReadOnlyList<string>)
			[
				Fmt.Pkg(row.Package.Locked.Id),
				row.License.Length == 0 ? Fmt.Bad("unknown") : Fmt.Val(row.License),
				LicenseNote(row.License),
			]).ToList());

		Out.Info("");

		foreach (IGrouping<string, (InstalledPackage Package, string License)> group in rows.GroupBy(row => row.License.Length == 0 ? "unknown" : row.License))
		{
			Out.Info($"  {Fmt.Val(group.Key)}: {Fmt.Plural(group.Count(), "package", "packages")}");
		}

		if (rows.Any(row => LicenseNote(row.License).Length > 0))
		{
			Out.Info("");
			Out.Dim("  The notes are a hint, not legal advice. Read the license text of the flagged packages.");
		}
	}

	public static string LicenseOf(string directory)
	{
		if (PackageMeta.Read(directory) is { License.Length: > 0 } meta)
		{
			return meta.License;
		}

		string manifest = Path.Combine(directory, Manifest.FileName);

		if (File.Exists(manifest))
		{
			try
			{
				using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest), Json.DocumentOptions);

				if (document.RootElement.TryGetProperty("license", out JsonElement license) && license.ValueKind == JsonValueKind.String)
				{
					return license.GetString()!.Trim();
				}
			}
			catch (JsonException)
			{
			}
		}

		string wally = Path.Combine(directory, "wally.toml");

		if (File.Exists(wally))
		{
			Match match = WallyLicense().Match(File.ReadAllText(wally));

			if (match.Success)
			{
				return match.Groups["value"].Value.Trim();
			}
		}

		return "";
	}

	public static string LicenseNote(string license)
	{
		string upper = license.ToUpperInvariant();

		if (upper.Length == 0)
		{
			return "no license: you may not have the right to use it";
		}

		if (upper.Contains("AGPL") || (upper.Contains("GPL") && !upper.Contains("LGPL")))
		{
			return Fmt.Bad("copyleft: code that uses it must be shared under the same license");
		}

		if (upper.Contains("LGPL") || upper.Contains("MPL") || upper.Contains("EPL"))
		{
			return "changes to this package's own files must stay open";
		}

		return "";
	}

	static IEnumerable<InstalledPackage> Closure(InstalledPackage start, List<InstalledPackage> installed)
	{
		HashSet<string> seen = [];
		Stack<InstalledPackage> pending = new([start]);

		while (pending.TryPop(out InstalledPackage? package))
		{
			if (!seen.Add(package.Locked.Id))
			{
				continue;
			}

			yield return package;

			foreach (string entry in package.Locked.Dependencies)
			{
				string id = entry.Contains(':') ? entry[(entry.IndexOf(':') + 1)..] : entry;
				InstalledPackage? dependency = installed.FirstOrDefault(item => item.Realm == start.Realm && item.Locked.Id == id);

				if (dependency is not null)
				{
					pending.Push(dependency);
				}
			}
		}
	}

	static (int Modules, long Bytes) Measure(string directory)
	{
		List<FileInfo> files = Directory.Exists(directory)
			? Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)
				.Where(path => LuauExtensions.Contains(Path.GetExtension(path), StringComparer.OrdinalIgnoreCase))
				.Select(path => new FileInfo(path))
				.ToList()
			: [];

		return (files.Count, files.Sum(file => file.Length));
	}

	static string Kilobytes(long bytes) => $"{bytes / 1024.0:0.0} KB";
}
