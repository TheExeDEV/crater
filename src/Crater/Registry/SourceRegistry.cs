using System.Security.Cryptography;
using System.Text;
using Crater.Core;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Registry;

public sealed class SourceRegistry : IRegistry
{
	readonly RegistryIndex index;
	readonly byte[] archive;

	SourceRegistry(string sourceText, string directory, string? commit, RegistryIndex index, byte[] archive)
	{
		SourceText = sourceText;
		Directory = directory;
		Commit = commit;

		this.index = index;
		this.archive = archive;
	}

	public string SourceText { get; }
	public string Directory { get; }
	public string? Commit { get; }

	public string Location => SourceText;

	public static SourceRegistry Open(string fullName, DependencySource source, string baseDirectory, string? lockedCommit)
	{
		if (source.Kind == SourceKind.Path)
		{
			string directory = Path.GetFullPath(Path.Combine(baseDirectory, source.Location));

			return FromDirectory(fullName, source.Text, directory, null);
		}

		(string checkout, string commit) = GitCheckout.Fetch(source, lockedCommit);

		return FromDirectory(fullName, source.Text, checkout, commit);
	}

	public static SourceRegistry FromDirectory(string fullName, string sourceText, string directory, string? commit)
	{
		if (!System.IO.Directory.Exists(directory))
		{
			throw new CraterException($"{Fmt.Pkg(fullName)}: folder {Fmt.File(directory)} from {Fmt.Val(sourceText)} does not exist.");
		}

		Project project = Project.LoadExact(directory);

		if (project.Manifest.Name != fullName)
		{
			throw new CraterException(
				$"{Fmt.Val(sourceText)} contains {Fmt.Pkg(project.Manifest.Name)}, but the dependency is named {Fmt.Pkg(fullName)}.");
		}

		SemVer version = SemVer.TryParse(project.Manifest.Version, out SemVer parsed) ? parsed : new SemVer(0, 0, 0);
		byte[] packed = Packer.Pack(project, out _);

		RegistryRelease release = new()
		{
			Version = version.ToString(),
			Sha256 = PackageCache.Sha256(packed),
			Manifest = project.Manifest,
			SemVer = version,
		};

		RegistryIndex index = new() { Name = fullName, Versions = [release] };

		return new SourceRegistry(sourceText, directory, commit, index, packed);
	}

	public Task<RegistryIndex?> GetIndexAsync(string fullName)
	{
		return Task.FromResult(fullName == index.Name ? index : null);
	}

	public Task<byte[]> DownloadAsync(string fullName, string version) => Task.FromResult(archive);

	public Task<Catalog?> GetCatalogAsync(string query) => Task.FromResult<Catalog?>(null);

	public Task PublishAsync(RegistryRelease release, byte[] archive)
	{
		throw new CraterException($"Cannot publish into {Fmt.Val(SourceText)}.");
	}
}

public static class GitCheckout
{
	public static (string Directory, string Commit) Fetch(DependencySource source, string? lockedCommit)
	{
		string key = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(source.Text)))[..16];
		string directory = Path.Combine(PackageCache.Root, "git", key);

		if (!Directory.Exists(Path.Combine(directory, ".git")))
		{
			Directory.CreateDirectory(directory);
			Shell.Run("git", ["init", "--quiet"], directory);
			Shell.Run("git", ["remote", "add", "origin", source.Location], directory);
		}

		string target = lockedCommit ?? source.Ref ?? "HEAD";
		ShellResult fetched = Shell.Capture("git", ["fetch", "--quiet", "--depth", "1", "origin", target], directory);

		if (!fetched.Success && lockedCommit is not null)
		{
			Out.Warn($"Locked commit {Fmt.Val(lockedCommit[..Math.Min(12, lockedCommit.Length)])} of {Fmt.Val(source.Text)} is gone, using {Fmt.Val(source.Ref ?? "HEAD")}");
			fetched = Shell.Capture("git", ["fetch", "--quiet", "--depth", "1", "origin", source.Ref ?? "HEAD"], directory);
		}

		if (!fetched.Success)
		{
			throw new CraterException($"Could not fetch {Fmt.Val(source.Text)}: {fetched.Error}");
		}

		Shell.Run("git", ["checkout", "--quiet", "--force", "--detach", "FETCH_HEAD"], directory);
		Shell.Run("git", ["clean", "--quiet", "-fdx"], directory);

		string commit = Shell.Run("git", ["rev-parse", "HEAD"], directory);

		return (directory, commit);
	}
}
