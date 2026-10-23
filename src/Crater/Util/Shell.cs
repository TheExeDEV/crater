using System.ComponentModel;
using System.Diagnostics;

namespace Crater.Util;

public sealed record ShellResult(int ExitCode, string Output, string Error)
{
	public bool Success => ExitCode == 0;
}

public static class Shell
{
	public static bool Exists(string program)
	{
		try
		{
			ShellResult result = Capture(program, ["--version"], Environment.CurrentDirectory);
			return result.Success;
		}
		catch (CraterException)
		{
			return false;
		}
	}

	public static ShellResult Capture(string program, IEnumerable<string> arguments, string workingDirectory)
	{
		ProcessStartInfo info = new(program)
		{
			WorkingDirectory = workingDirectory,
			RedirectStandardOutput = true,
			RedirectStandardError = true,
			UseShellExecute = false,
		};

		foreach (string argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}

		try
		{
			using Process process = Process.Start(info)
				?? throw new CraterException($"Could not start {Fmt.Cmd(program)}");

			Task<string> output = process.StandardOutput.ReadToEndAsync();
			Task<string> error = process.StandardError.ReadToEndAsync();

			process.WaitForExit();

			return new ShellResult(process.ExitCode, output.Result.Trim(), error.Result.Trim());
		}
		catch (Win32Exception)
		{
			throw new CraterException($"{Fmt.Cmd(program)} is not installed or not on PATH.");
		}
	}

	public static string Run(string program, IEnumerable<string> arguments, string workingDirectory)
	{
		List<string> list = arguments.ToList();
		ShellResult result = Capture(program, list, workingDirectory);

		if (!result.Success)
		{
			string details = result.Error.Length > 0 ? result.Error : result.Output;
			throw new CraterException($"{Fmt.Cmd($"{program} {string.Join(' ', list)}")} failed: {details}");
		}

		return result.Output;
	}

	public static int RunInteractive(string commandLine, string workingDirectory, IReadOnlyDictionary<string, string>? environment = null)
	{
		ProcessStartInfo info = OperatingSystem.IsWindows()
			? new ProcessStartInfo("cmd.exe", $"/d /s /c \"{commandLine}\"")
			: new ProcessStartInfo("/bin/sh") { ArgumentList = { "-c", commandLine } };

		info.WorkingDirectory = workingDirectory;
		info.UseShellExecute = false;

		if (environment is not null)
		{
			foreach ((string key, string value) in environment)
			{
				info.Environment[key] = value;
			}
		}

		using Process process = Process.Start(info)
			?? throw new CraterException($"Could not start {Fmt.Cmd(commandLine)}");

		process.WaitForExit();

		return process.ExitCode;
	}
}
