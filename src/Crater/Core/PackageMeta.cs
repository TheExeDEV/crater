using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Crater.Models;
using Crater.Util;

namespace Crater.Core;

public sealed partial class PackageMeta
{
	public const string FileName = ".crater-package";

	static readonly string[] Manifests = ["crater.json", "wally.toml", "wally_bundle.toml", "wally.lock", "pesde.toml", "pesde.lock"];

	[JsonPropertyName("name")] public string Name { get; set; } = "";
	[JsonPropertyName("version")] public string Version { get; set; } = "";
	[JsonPropertyName("description")] public string Description { get; set; } = "";
	[JsonPropertyName("license")] public string License { get; set; } = "";
	[JsonPropertyName("source")] public string Source { get; set; } = "external";
	[JsonPropertyName("import_client")] public List<string> ImportClient { get; set; } = [];
	[JsonPropertyName("import_server")] public List<string> ImportServer { get; set; } = [];
	[JsonPropertyName("definition_files")] public List<string> DefinitionFiles { get; set; } = [];

	[JsonIgnore] public bool IsCrater => Source == "crater";

	[GeneratedRegex(@"^\s*license\s*=\s*""(?<value>[^""]*)""", RegexOptions.Multiline)]
	private static partial Regex TomlLicense();

	public static PackageMeta? Read(string directory)
	{
		string path = Path.Combine(directory, FileName);

		try
		{
			return File.Exists(path) ? Json.ReadFile<PackageMeta>(path) : null;
		}
		catch (CraterException)
		{
			return null;
		}
	}

	public static void Capture(string directory, Manifest manifest, List<string> definitionFiles)
	{
		PackageMeta meta = new()
		{
			Name = manifest.Name,
			Version = manifest.Version,
			Description = manifest.Description,
			License = manifest.License,
			Source = File.Exists(Path.Combine(directory, "crater.json")) ? "crater" : "external",
			ImportClient = manifest.ImportClient,
			ImportServer = manifest.ImportServer.Except(manifest.ImportClient).ToList(),
			DefinitionFiles = definitionFiles,
		};

		if (meta.License.Length == 0)
		{
			foreach (string toml in new[] { "wally.toml", "pesde.toml" })
			{
				string path = Path.Combine(directory, toml);

				if (File.Exists(path) && TomlLicense().Match(File.ReadAllText(path)) is { Success: true } match)
				{
					meta.License = match.Groups["value"].Value.Trim();
					break;
				}
			}
		}

		foreach (string name in Manifests)
		{
			string path = Path.Combine(directory, name);

			if (File.Exists(path))
			{
				File.Delete(path);
			}
		}

		Json.WriteText(Path.Combine(directory, FileName), Json.Serialize(meta));
	}
}
