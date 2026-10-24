using Crater.Core;
using Crater.Util;

namespace Crater.Integrations;

public static class RojoSourcemap
{
	const string SourcemapFile = "sourcemap.json";
	const string ProjectFile = "default.project.json";

	public static void Refresh(Project project)
	{
		string sourcemap = Path.Combine(project.Directory, SourcemapFile);

		if (!File.Exists(sourcemap) || !File.Exists(Path.Combine(project.Directory, ProjectFile)))
		{
			return;
		}

		try
		{
			ShellResult result = Shell.Capture("rojo", ["sourcemap", ProjectFile, "--output", SourcemapFile], project.Directory);

			if (result.Success)
			{
				Out.Dim($"  updated {SourcemapFile}");
				return;
			}

			Out.Dim($"  {SourcemapFile} was not updated: {result.Error.Split('\n')[0]}");
		}
		catch (CraterException)
		{
			Out.Dim($"  {SourcemapFile} was not updated: rojo is not installed");
		}
	}
}
