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

		if (locked && oldLock.HadConflicts)
		{
			throw new CraterException($"{Fmt.File(LockFile.FileName)} has merge conflicts. Run {Fmt.Cmd("crater install")} and commit the result.");
		}

		if (update is null && InstallState.IsCurrent(project, oldLock))
		{
			InstallSummary.Print(oldLock, oldLock, watch.Elapsed);
			AfterInstall(project, prompt);
			return;
		}

		Dictionary<Realm, ResolvedGraph> graphs;
		Dictionary<Realm, ResolvedGraph> vendor;

		using (new Spinner("Resolving dependencies"))
		{
			(graphs, vendor) = await ResolutionContext.ResolveWithVendorAsync(project, oldLock, update);
		}

		List<ResolvedGraph> everything = graphs.Values.Concat(vendor.Values).ToList();

		foreach (string note in everything.SelectMany(graph => graph.Notes))
		{
			Out.Warn(note);
		}

		RejectRepublished(everything, oldLock);
		await VersionNotices.PrintAsync(project, graphs.Values);

		LockFile newLock = LockFile.From(graphs, vendor, project.Directory);
		newLock.ManifestHash = InstallState.ManifestHash(project);

		bool lockDiffers = Json.Serialize(newLock.Packages) != Json.Serialize(oldLock.Packages) ||
			Json.Serialize(newLock.Vendor ?? []) != Json.Serialize(oldLock.Vendor ?? []);

		if (locked && (!File.Exists(project.LockPath) || lockDiffers))
		{
			throw new CraterException(
				$"{Fmt.File(LockFile.FileName)} does not match {Fmt.File(Manifest.FileName)}. Run {Fmt.Cmd("crater install")} and commit {Fmt.File(LockFile.FileName)}.");
		}

		FolderBuilder builder = new(project, prompt);
		List<ResolvedGraph> stale = everything.Where(graph => !builder.IsUpToDate(graph, oldLock)).ToList();
		Dictionary<ResolvedPackage, byte[]> archives = await DownloadAsync(stale.SelectMany(builder.PackagesToExtract).ToList());
		string staging = Path.Combine(project.Directory, ".crater-staging");

		try
		{
			Dictionary<ResolvedPackage, string> prepared = Stage(archives, staging);

			archives.Clear();

			await ScriptPolicy.CheckAsync(project, everything.SelectMany(graph => graph.All), prepared, oldLock);

			foreach (ResolvedGraph graph in stale)
			{
				builder.Build(graph, prepared);
			}
		}
		finally
		{
			if (Directory.Exists(staging))
			{
				Directory.Delete(staging, recursive: true);
			}
		}

		if (project.Config.OnInstallUpdateLuaurc)
		{
			LuauRc.Update(project, everything, prompt);
		}

		if (project.Config.OnInstallUpdateVscodeLuauDefinitionFiles || (project.Config.OnInstallUpdateLuaurc && LuauRc.IsCustom(project)))
		{
			VsCodeSettings.Update(project, graphs, prompt);
		}

		if (stale.Count > 0)
		{
			RojoSourcemap.Refresh(project);
		}

		Layout.IgnoreVendor(project, vendor.Values.Where(graph => graph.All.Count > 0).Select(graph => graph.VendorFolder!).ToList(), prompt);

		WriteLock(newLock);
		InstallSummary.Print(oldLock, newLock, watch.Elapsed);
		PrintVendored(vendor);
		await VersionNotices.PrintChangesAsync(oldLock, graphs.Values);
		WarnHiddenDefinitions(project, graphs, oldLock);
		AfterInstall(project, prompt);
	}

	static void WarnHiddenDefinitions(Project project, IReadOnlyDictionary<Realm, ResolvedGraph> graphs, LockFile oldLock)
	{
		if (project.Config.OnInstallUpdateVscodeLuauDefinitionFiles)
		{
			return;
		}

		HashSet<string> before = oldLock.Packages.Select(package => package.Id).ToHashSet();
		List<string> packages = Layout.DefinitionFiles(project, graphs)
			.Select(item => item.Package.Id)
			.Where(id => !before.Contains(id))
			.Distinct()
			.Order(StringComparer.Ordinal)
			.ToList();

		if (packages.Count == 0)
		{
			return;
		}

		Out.Warn(
			$"{string.Join(", ", packages.Select(id => Fmt.Pkg(id)))} {(packages.Count == 1 ? "comes" : "come")} with type definitions ({Fmt.File(".d.luau")}), " +
			$"but {Fmt.Val("vscode_definitions")} is off in {Fmt.File(ProjectConfig.FileName)}, so luau-lsp will not see them. Set {Fmt.Val("\"vscode_definitions\": true")} to add them.");
	}

	static void AfterInstall(Project project, Prompter prompt)
	{
		Darklua.Update(project, prompt);

		if (!Azul.IsUsed(project))
		{
			return;
		}

		Azul.UpdateGitignore(project, prompt);

		if (Azul.NeedsPush(project))
		{
			Out.Dim($"  Studio does not have these packages yet: {Fmt.Cmd("crater push")} sends them, {Fmt.Cmd("crater dev")} does it by itself");
		}
	}

	static Dictionary<ResolvedPackage, string> Stage(Dictionary<ResolvedPackage, byte[]> archives, string staging)
	{
		if (Directory.Exists(staging))
		{
			Directory.Delete(staging, recursive: true);
		}

		Dictionary<ResolvedPackage, string> prepared = [];
		int index = 0;

		foreach ((ResolvedPackage package, byte[] archive) in archives)
		{
			string directory = Path.Combine(staging, (index++).ToString());

			PackageFiles.ExtractPrepared(archive, directory, package.Id, package.ShortName, package.Manifest, quiet: false);
			prepared[package] = directory;
		}

		return prepared;
	}

	static void PrintVendored(Dictionary<Realm, ResolvedGraph> vendor)
	{
		foreach (ResolvedGraph graph in vendor.Values.Where(graph => graph.All.Count > 0))
		{
			string list = string.Join(", ", graph.All.OrderBy(package => package.Name, StringComparer.Ordinal).Select(package => Fmt.Pkg(package.Id)));

			Out.Dim($"  vendored ({graph.Realm.Key()}, in {graph.VendorFolder}): {list}");
		}
	}

	static void RejectRepublished(IEnumerable<ResolvedGraph> graphs, LockFile oldLock)
	{
		foreach (ResolvedGraph graph in graphs)
		{
			foreach (ResolvedPackage package in graph.All.Where(package => !package.IsSource))
			{
				LockedPackage? locked = oldLock.ForGraph(graph).FirstOrDefault(
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

		if (!lockExists && !newLock.Everything.Any())
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
