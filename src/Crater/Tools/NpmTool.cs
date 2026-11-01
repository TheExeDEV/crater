using System.Text.Json;
using Crater.Util;

namespace Crater.Tools;

public static class NpmTool
{
	public static string Install(string name, ToolSpec spec, string target)
	{
		string directory = Path.GetDirectoryName(target)!;
		string[] arguments = ["install", "--prefix", directory, "--no-save", "--no-audit", "--no-fund", "--loglevel=error", $"{spec.NpmPackage}@{spec.Version}"];

		Directory.CreateDirectory(directory);

		ShellResult result;

		try
		{
			result = OperatingSystem.IsWindows()
				? Shell.Capture("cmd.exe", ["/d", "/c", "npm", .. arguments], directory)
				: Shell.Capture("npm", arguments, directory);
		}
		catch (CraterException)
		{
			throw new CraterException($"{Fmt.Val(name)} comes from npm, so it needs Node.js. Install it from {Fmt.File("https://nodejs.org")} and try again.");
		}

		if (!result.Success)
		{
			throw new CraterException($"npm could not install {Fmt.Val(spec.ToString())}: {(result.Error.Length > 0 ? result.Error : result.Output)}");
		}

		string script = BinScript(directory, spec.NpmPackage!, name);

		WriteLauncher(target, script);

		return target;
	}

	static string BinScript(string directory, string package, string name)
	{
		string packageDirectory = Path.Combine(directory, "node_modules", package);
		string manifest = Path.Combine(packageDirectory, "package.json");

		if (!File.Exists(manifest))
		{
			throw new CraterException($"npm did not install {Fmt.Val(package)}.");
		}

		using JsonDocument document = JsonDocument.Parse(File.ReadAllText(manifest));
		string? relative = null;

		if (document.RootElement.TryGetProperty("bin", out JsonElement bin))
		{
			relative = bin.ValueKind switch
			{
				JsonValueKind.String => bin.GetString(),
				JsonValueKind.Object => bin.EnumerateObject().FirstOrDefault(entry => entry.Name.Equals(name, StringComparison.OrdinalIgnoreCase)).Value.GetString()
					?? bin.EnumerateObject().Select(entry => entry.Value.GetString()).FirstOrDefault(),
				_ => null,
			};
		}

		return relative is null
			? throw new CraterException($"{Fmt.Val(package)} has no command to run.")
			: Path.GetFullPath(Path.Combine(packageDirectory, relative));
	}

	static void WriteLauncher(string target, string script)
	{
		if (OperatingSystem.IsWindows())
		{
			File.WriteAllText(target, $"@echo off\r\nnode \"{script}\" %*\r\nexit /b %ERRORLEVEL%\r\n");
			return;
		}

		File.WriteAllText(target, $"#!/bin/sh\nexec node \"{script}\" \"$@\"\n");
		File.SetUnixFileMode(target, File.GetUnixFileMode(target) | UnixFileMode.UserExecute | UnixFileMode.GroupExecute | UnixFileMode.OtherExecute);
	}
}
