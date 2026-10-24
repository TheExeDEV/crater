using System.Text.Json;
using Crater.Cli;
using Crater.Core;
using Crater.Util;

namespace Crater.Commands;

public static class BuildCommand
{
	static readonly string[] SkippedFolders = [".git", ".vscode", ".idea", "node_modules", "build"];

	const string MinifyConfig = """
		{
			"generator": "dense",
			"rules": [
				"remove_comments",
				"remove_spaces",
				"remove_types",
				"compute_expression",
				"remove_empty_do",
				"remove_unused_if_branch",
				"remove_unused_while",
				"filter_after_early_return"
			]
		}
		""";

	public static async Task<int> RunAsync(Args args)
	{
		Project project = Common.LoadProject();
		string projectFile = Path.Combine(project.Directory, ProjectSources.RojoProject);

		if (!File.Exists(projectFile))
		{
			throw new CraterException($"{Fmt.File(ProjectSources.RojoProject)} not found. crater build uses Rojo to build the place or model.");
		}

		string output = Path.GetFullPath(args.Value("o", "output") ?? DefaultOutput(project, projectFile), project.Directory);
		string rojo = await ToolCommand.ExecutableAsync("rojo");

		Directory.CreateDirectory(Path.GetDirectoryName(output)!);

		if (!args.Has("minify"))
		{
			Build(rojo, project.Directory, output);
			return 0;
		}

		string darklua = await ToolCommand.ExecutableAsync("darklua");
		string staging = Path.Combine(Path.GetTempPath(), $"crater-build-{Guid.NewGuid():N}");

		try
		{
			using (new Spinner("Copying the project"))
			{
				Copy(project.Directory, staging, Path.GetDirectoryName(output)!);
			}

			string config = Path.Combine(staging, ".crater-minify.json");

			await File.WriteAllTextAsync(config, MinifyConfig);

			using (new Spinner("Minifying with darklua"))
			{
				foreach (string folder in MappedPaths(projectFile).Where(path => Directory.Exists(Path.Combine(staging, path))))
				{
					Minify(darklua, staging, folder, config);
				}
			}

			Build(rojo, staging, output);
		}
		finally
		{
			if (Directory.Exists(staging))
			{
				Directory.Delete(staging, recursive: true);
			}
		}

		return 0;
	}

	static void Build(string rojo, string directory, string output)
	{
		ShellResult result;

		using (new Spinner("Building with Rojo"))
		{
			result = Shell.Capture(rojo, ["build", ProjectSources.RojoProject, "--output", output], directory);
		}

		if (!result.Success)
		{
			throw new CraterException($"Rojo could not build: {(result.Error.Length > 0 ? result.Error : result.Output)}");
		}

		Out.Ok($"Built {Fmt.File(output)} ({new FileInfo(output).Length / 1024.0:0.0} KB)");
	}

	static void Minify(string darklua, string staging, string folder, string config)
	{
		string target = Path.Combine(staging, folder);
		HashSet<string> before = Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).ToHashSet(StringComparer.OrdinalIgnoreCase);
		ShellResult result = Shell.Capture(darklua, ["process", folder, folder, "--config", config], staging);

		if (!result.Success)
		{
			throw new CraterException($"darklua could not process {Fmt.File(folder)}: {(result.Error.Length > 0 ? result.Error : result.Output)}");
		}

		foreach (string created in Directory.EnumerateFiles(target, "*", SearchOption.AllDirectories).Where(file => !before.Contains(file)).ToList())
		{
			File.Delete(created);
		}
	}

	static void Copy(string source, string destination, string outputFolder)
	{
		Directory.CreateDirectory(destination);

		foreach (string file in Directory.EnumerateFiles(source))
		{
			File.Copy(file, Path.Combine(destination, Path.GetFileName(file)));
		}

		foreach (string directory in Directory.EnumerateDirectories(source))
		{
			string name = Path.GetFileName(directory);
			bool isOutput = Path.GetFullPath(directory).Equals(Path.GetFullPath(outputFolder), StringComparison.OrdinalIgnoreCase);

			if (SkippedFolders.Contains(name, StringComparer.OrdinalIgnoreCase) || isOutput)
			{
				continue;
			}

			Copy(directory, Path.Combine(destination, name), outputFolder);
		}
	}

	static string DefaultOutput(Project project, string projectFile)
	{
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(projectFile));
		JsonElement root = document.RootElement;
		string name = root.TryGetProperty("name", out JsonElement value) && value.ValueKind == JsonValueKind.String
			? value.GetString()!
			: new DirectoryInfo(project.Directory).Name;
		bool isPlace = root.TryGetProperty("tree", out JsonElement tree) &&
			tree.TryGetProperty("$className", out JsonElement className) &&
			className.GetString() == "DataModel";

		return Path.Combine("build", name + (isPlace ? ".rbxl" : ".rbxm"));
	}

	static List<string> MappedPaths(string projectFile)
	{
		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(projectFile));
		List<string> paths = [];

		void Collect(JsonElement element)
		{
			if (element.ValueKind != JsonValueKind.Object)
			{
				return;
			}

			foreach (JsonProperty property in element.EnumerateObject())
			{
				if (property.Name == "$path" && property.Value.ValueKind == JsonValueKind.String)
				{
					paths.Add(property.Value.GetString()!.Replace('\\', '/').TrimEnd('/'));
				}
				else
				{
					Collect(property.Value);
				}
			}
		}

		Collect(document.RootElement);

		return paths.Distinct().ToList();
	}
}
