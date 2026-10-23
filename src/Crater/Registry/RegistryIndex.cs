using System.Text.Json.Serialization;
using Crater.Models;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Registry;

public sealed class RegistryIndex
{
	public const string FileName = "index.json";

	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("versions")] public List<RegistryRelease> Versions { get; set; } = [];

	public RegistryRelease? Latest =>
		Versions.Where(release => !release.SemVer.IsPrerelease).MaxBy(release => release.SemVer)
		?? Versions.MaxBy(release => release.SemVer);

	public RegistryIndex Normalize(string source)
	{
		List<RegistryRelease> valid = [];

		foreach (RegistryRelease release in Versions)
		{
			if (!SemVer.TryParse(release.Version, out SemVer version))
			{
				Out.Warn($"{Fmt.File(source)}: skipped entry with invalid version {Fmt.Val(release.Version)}");
				continue;
			}

			release.SemVer = version;
			release.Manifest ??= new Manifest();

			if (string.IsNullOrEmpty(release.Manifest.Name))
			{
				release.Manifest.Name = Name;
			}

			valid.Add(release);
		}

		Versions = valid;

		return this;
	}
}

public sealed class RegistryRelease
{
	[JsonPropertyName("version")] public string Version { get; set; } = "";
	[JsonPropertyName("sha256")] public string Sha256 { get; set; } = "";
	[JsonPropertyName("published_at")] public DateTimeOffset? PublishedAt { get; set; }
	[JsonPropertyName("manifest")] public Manifest Manifest { get; set; } = new();

	[JsonIgnore] public SemVer SemVer { get; set; } = new(0, 0, 0);
}
