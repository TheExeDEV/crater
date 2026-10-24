using System.Diagnostics;
using Crater.Cli;
using Crater.Core;
using Crater.Models;
using Crater.Util;

namespace Crater.Commands;

public static class DevCommand
{
	static readonly TimeSpan Debounce = TimeSpan.FromMilliseconds(400);

	public static async Task<int> RunAsync(Args args)
	{
		Project project = Common.LoadProject();
		List<Process> children = [];
		List<(string Executable, string[] Arguments)> commands = [];
		using CancellationTokenSource stop = new();

		Console.CancelKeyPress += (_, eventArgs) =>
		{
			eventArgs.Cancel = true;
			stop.Cancel();
		};

		await InstallAsync(project.Directory);

		try
		{
			if (!args.Has("no-serve") && File.Exists(Path.Combine(project.Directory, ProjectSources.RojoProject)))
			{
				string rojo = await ToolCommand.ExecutableAsync("rojo");

				commands.Add((rojo, ["serve", ProjectSources.RojoProject, .. args.Passthrough]));

				if (File.Exists(Path.Combine(project.Directory, "sourcemap.json")))
				{
					commands.Add((rojo, ["sourcemap", ProjectSources.RojoProject, "--output", "sourcemap.json", "--watch"]));
				}

				children.AddRange(commands.Select(command => Start(command.Executable, project.Directory, command.Arguments)));
			}

			using FileSystemWatcher watcher = new(project.Directory)
			{
				NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
				IncludeSubdirectories = false,
			};

			watcher.Filters.Add(Manifest.FileName);
			watcher.Filters.Add(ProjectConfig.FileName);

			SemaphoreSlim changed = new(0);

			watcher.Changed += (_, _) => changed.Release();
			watcher.Created += (_, _) => changed.Release();
			watcher.Renamed += (_, _) => changed.Release();
			watcher.EnableRaisingEvents = true;

			Out.Info("");
			Out.Ok($"Watching {Fmt.File(Manifest.FileName)} and {Fmt.File(ProjectConfig.FileName)}. Press {Fmt.Cmd("Ctrl+C")} to stop.");

			while (!stop.IsCancellationRequested)
			{
				try
				{
					await changed.WaitAsync(stop.Token);
					await Task.Delay(Debounce, stop.Token);

					while (changed.CurrentCount > 0)
					{
						await changed.WaitAsync(stop.Token);
					}
				}
				catch (OperationCanceledException)
				{
					break;
				}

				Out.Info("");
				Out.Step("Dependencies changed, installing");
				await InstallAsync(project.Directory);

				for (int index = 0; index < children.Count; index++)
				{
					if (children[index].HasExited)
					{
						(string executable, string[] arguments) = commands[index];

						children[index].Dispose();
						children[index] = Start(executable, project.Directory, arguments);
						Out.Dim($"  restarted rojo {arguments[0]}, reconnect the Rojo plugin in Studio if it shows disconnected");
					}
				}
			}
		}
		finally
		{
			foreach (Process child in children)
			{
				if (!child.HasExited)
				{
					child.Kill(entireProcessTree: true);
				}

				child.Dispose();
			}
		}

		Out.Info("");
		Out.Ok("Stopped");

		return 0;
	}

	static async Task InstallAsync(string directory)
	{
		try
		{
			Project project = Project.LoadExact(directory);

			await new Installer(project, new Prompter(false)).InstallAsync(update: null);
			await ToolCommand.InstallAllAsync(project.Manifest, announce: false);
		}
		catch (CraterException exception)
		{
			LiveLine.Set(null);
			Out.Error(exception.Message);
			Out.Dim("  Fix it and save the file again.");
		}
	}

	static Process Start(string executable, string directory, IEnumerable<string> arguments)
	{
		ProcessStartInfo info = new(executable) { WorkingDirectory = directory, UseShellExecute = false };

		foreach (string argument in arguments)
		{
			info.ArgumentList.Add(argument);
		}

		return Process.Start(info) ?? throw new CraterException($"Could not start {Path.GetFileNameWithoutExtension(executable)}.");
	}
}
