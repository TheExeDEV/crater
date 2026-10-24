using System.Diagnostics;
using Crater.Integrations;
using Crater.Models;
using Crater.Registry;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Core;

public sealed class Installer(Project project, Prompter prompt, bool locked = false)
{
	const int ParallelDownloads = 6;

	public async Task InstallAsync(ISet<string>? update)
	{
		Stopwatch watch = Stopwatch.StartNew();
		LockFile oldLock = LockFile.Load(project.LockPath);

		if (update is null && InstallState.IsCurrent(project, oldLock))
		{
			InstallSummary.Print(oldLock, oldLock, watch.Elapsed);
			return;
		}

		Dictionary<Realm, ResolvedGraph> graphs;

		using (new Spinner("Resolving dependencies"))
		{
			graphs = await ResolutionContext.ResolveAllAsync(project, oldLock, update);
		}

		foreach (string note in graphs.Values.SelectMany(graph => graph.Notes))
		{
			Out.Warn(note);
		}

		RejectRepublished(graphs, oldLock);

		LockFile newLock = LockFile.From(graphs, project.Directory);
		newLock.ManifestHash = InstallState.ManifestHash(project);

		if (locked && (!File.Exists(project.LockPath) || Json.Serialize(newLock.Packages) != Json.Serialize(oldLock.Packages)))
		{
			throw new CraterException(
				$"{Fmt.File(LockFile.FileName)} does not match {Fmt.File(Manifest.FileName)}. Run {Fmt.Cmd("crater install")} and commit {Fmt.File(LockFile.FileName)}.");
		}

		FolderBuilder builder = new(project, prompt);
		List<ResolvedGraph> stale = graphs.Values.Where(graph => !builder.IsUpToDate(graph, oldLock)).ToList();
		Dictionary<ResolvedPackage, byte[]> archives = await DownloadAsync(stale.SelectMany(builder.PackagesToExtract).ToList());

		foreach (ResolvedGraph graph in stale)
		{
			builder.Build(graph, archives);
		}

		if (project.Config.OnInstallUpdateLuaurc)
		{
			LuauRc.Update(project, graphs, prompt);
		}

		if (project.Config.OnInstallUpdateVscodeLuauDefinitionFiles || (project.Config.OnInstallUpdateLuaurc && LuauRc.IsCustom(project)))
		{
			VsCodeSettings.Update(project, graphs, prompt);
		}

		if (stale.Count > 0)
		{
			RojoSourcemap.Refresh(project);
		}

		WriteLock(newLock);
		InstallSummary.Print(oldLock, newLock, watch.Elapsed);
	}

	static void RejectRepublished(Dictionary<Realm, ResolvedGraph> graphs, LockFile oldLock)
	{
		foreach ((Realm realm, ResolvedGraph graph) in graphs)
		{
			foreach (ResolvedPackage package in graph.All.Where(package => !package.IsSource))
			{
				LockedPackage? locked = oldLock.For(realm).FirstOrDefault(
					item => item.Name == package.Name && item.Version == package.Version.ToString());

				bool republished = locked is not null &&
					locked.Sha256.Length > 0 &&
					!locked.Sha256.Equals(package.Release.Sha256, StringComparison.OrdinalIgnoreCase);

				if (republished)
				{
					throw new CraterException(
						$"{Fmt.Pkg(package.Id)}: registry sha256 differs from {Fmt.File(LockFile.FileName)}. " +
						$"The version was republished. If this is expected, run {Fmt.Cmd($"crater update {package.Name}")}.");
				}
			}
		}
	}

	static async Task<Dictionary<ResolvedPackage, byte[]>> DownloadAsync(List<ResolvedPackage> packages)
	{
		List<IGrouping<string, ResolvedPackage>> groups = packages
			.GroupBy(package => $"{package.Registry.Location}|{package.Id}|{package.Release.Sha256}")
			.ToList();

		int missing = groups.Count(group => !PackageCache.IsCached(group.First()));
		Dictionary<ResolvedPackage, byte[]> archives = [];
		object gate = new();

		using (ProgressBar progress = new(missing, "downloading"))
		using (SemaphoreSlim slots = new(ParallelDownloads))
		{
			IEnumerable<Task> downloads = groups.Select(async group =>
			{
				ResolvedPackage first = group.First();
				bool wasCached = PackageCache.IsCached(first);

				await slots.WaitAsync();

				try
				{
					byte[] data = await PackageCache.GetAsync(first);

					lock (gate)
					{
						foreach (ResolvedPackage package in group)
						{
							archives[package] = data;
						}
					}

					if (!wasCached)
					{
						progress.Advance(first.Id);
					}
				}
				finally
				{
					slots.Release();
				}
			});

			await Task.WhenAll(downloads);
		}

		return archives;
	}

	void WriteLock(LockFile newLock)
	{
		bool lockExists = File.Exists(project.LockPath);

		if (!lockExists && newLock.Packages.Count == 0)
		{
			return;
		}

		string newText = Json.Serialize(newLock);
		string? oldText = lockExists ? File.ReadAllText(project.LockPath).TrimEnd() : null;

		if (newText == oldText)
		{
			return;
		}

		if (prompt.Confirm($"Write {Fmt.File(LockFile.FileName)}"))
		{
			Json.WriteText(project.LockPath, newText);
		}
	}
}
