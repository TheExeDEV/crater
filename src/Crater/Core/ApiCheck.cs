using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Core;

public sealed record ApiVerdict(bool Breaks, bool Compatible, bool AddsInPatch, SemVer Suggested);

public static class ApiCheck
{
	const int ShownChanges = 20;

	public static async Task RunAsync(IRegistry registry, Manifest manifest, byte[] archive, bool allowBreaking)
	{
		SemVer next = SemVer.Parse(manifest.Version);
		RegistryIndex? index;

		try
		{
			index = await registry.GetIndexAsync(manifest.Name);
		}
		catch (CraterException exception)
		{
			Out.Warn($"Could not compare the API with the last version: {exception.Message}");
			return;
		}

		RegistryRelease? previous = index?.Versions
			.Where(release => SemVer.TryParse(release.Version, out SemVer version) && version < next)
			.OrderByDescending(release => SemVer.Parse(release.Version))
			.FirstOrDefault();

		if (previous is null)
		{
			return;
		}

		byte[] old;

		try
		{
			old = await registry.DownloadAsync(manifest.Name, previous.Version);
		}
		catch (CraterException exception)
		{
			Out.Warn($"Could not download {Fmt.Pkg($"{manifest.Name}@{previous.Version}")} to compare the API: {exception.Message}");
			return;
		}

		PublicApi before = Read(old, previous.Manifest.PathToMain, manifest.Name, previous.Version);
		PublicApi after = Read(archive, manifest.PathToMain, manifest.Name, manifest.Version);

		if (before.Opaque || after.Opaque)
		{
			Out.Dim($"  API: crater cannot read what the module returns, so it was not compared with {previous.Version}");
			return;
		}

		List<ApiChange> changes = ApiDiff.Compare(before, after);

		if (changes.Count == 0)
		{
			Out.Dim($"  API: the same as {previous.Version}");
			return;
		}

		Out.Info($"  API compared with {previous.Version}:");

		foreach (ApiChange change in changes.OrderByDescending(change => change.Breaking).Take(ShownChanges))
		{
			Out.Info($"    {(change.Breaking ? "-" : "+")} {change.Text}");
		}

		if (changes.Count > ShownChanges)
		{
			Out.Dim($"    and {changes.Count - ShownChanges} more");
		}

		ApiVerdict verdict = Judge(SemVer.Parse(previous.Version), next, changes);

		if (verdict.Breaks && verdict.Compatible)
		{
			string message =
				$"{manifest.Version} breaks code written for {previous.Version}, but people who asked for {Fmt.Val($"^{previous.Version}")} would get it automatically. " +
				$"Publish it as {Fmt.Val(verdict.Suggested.ToString())}, keep the old API, or pass {Fmt.Cmd("--allow-breaking")} if crater got it wrong.";

			if (!allowBreaking)
			{
				throw new CraterException(message);
			}

			Out.Warn(message);
		}

		if (verdict.AddsInPatch)
		{
			Out.Warn($"{manifest.Version} adds to the API in a patch release. {Fmt.Val($"{next.Major}.{next.Minor + 1}.0")} would tell people there is something new.");
		}
	}

	public static ApiVerdict Judge(SemVer previous, SemVer next, List<ApiChange> changes)
	{
		bool breaks = changes.Any(change => change.Breaking);
		bool compatible = VersionReq.Parse('^', previous.ToString()).Matches(next);
		bool addsInPatch = !breaks && changes.Count > 0 && previous.Major > 0 && next.Major == previous.Major && next.Minor == previous.Minor;

		SemVer suggested = previous.Major > 0
			? new SemVer(previous.Major + 1, 0, 0)
			: previous.Minor > 0
				? new SemVer(0, previous.Minor + 1, 0)
				: new SemVer(0, 0, previous.Patch + 1);

		return new ApiVerdict(breaks, compatible, addsInPatch, suggested);
	}

	static PublicApi Read(byte[] archive, string pathToMain, string name, string version)
	{
		string directory = Path.Combine(Path.GetTempPath(), $"crater-api-{Guid.NewGuid():N}");

		try
		{
			Packer.Extract(archive, directory, $"{name}@{version}");

			return PublicApi.Read(directory, pathToMain, name);
		}
		finally
		{
			if (Directory.Exists(directory))
			{
				Directory.Delete(directory, recursive: true);
			}
		}
	}
}
