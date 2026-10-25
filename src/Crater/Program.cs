using System.Text;
using Crater.Cli;
using Crater.Commands;
using Crater.Util;

namespace Crater;

public static class Program
{
	public static async Task<int> Main(string[] argv)
	{
		Console.OutputEncoding = new UTF8Encoding(false);
		Console.InputEncoding = new UTF8Encoding(false);

		Console.CancelKeyPress += (_, _) => Terminal.Restore();

		if (argv.Length > 0 && argv[0] == "__complete")
		{
			return CompletionCommand.Complete(argv);
		}

		SelfUpdateCommand.CleanupPreviousVersion();

		if (argv.Length >= 3 && argv[0] == "tool" && argv[1] == "run")
		{
			try
			{
				return await ToolCommand.ExecAsync(argv[2], argv[3..]);
			}
			catch (CraterException exception)
			{
				LiveLine.Set(null);
				Out.Error(exception.Message);

				return 1;
			}
		}

		Args args = Args.Parse(argv);

		if (args.Has("offline"))
		{
			Http.Offline = true;
		}

		if (args.Command is null || args.Command == "help" || args.Has("h", "help"))
		{
			Help.Print();
			return args.Command is null && !args.Has("h", "help") ? 1 : 0;
		}

		try
		{
			int exitCode = await DispatchAsync(args);

			await UpdateNotice.ShowAsync(args.Command);

			return exitCode;
		}
		catch (CraterException exception)
		{
			LiveLine.Set(null);
			Out.Error(exception.Message);

			return 1;
		}
		catch (Exception exception) when (Environment.GetEnvironmentVariable("CRATER_DEBUG") is null)
		{
			LiveLine.Set(null);
			Out.Error($"Internal error: {exception.Message} (set {Fmt.Cmd("CRATER_DEBUG=1")} for details)");

			return 2;
		}
	}

	static async Task<int> DispatchAsync(Args args)
	{
		switch (args.Command)
		{
			case "init":
				ProjectCommands.Init(args);
				return 0;

			case "import":
				ImportCommand.Run(args);
				return 0;

			case "bump":
				ProjectCommands.Bump(args);
				return 0;

			case "run":
				return ProjectCommands.Run(args);

			case "dev":
				return await DevCommand.RunAsync(args);

			case "doctor":
				return await DoctorCommand.RunAsync();

			case "install" or "i":
				await InstallCommands.InstallAsync(args);
				return 0;

			case "update" or "up":
				await InstallCommands.UpdateAsync(args);
				return 0;

			case "add":
				await InstallCommands.AddAsync(args);
				return 0;

			case "remove" or "rm":
				await InstallCommands.RemoveAsync(args);
				return 0;

			case "outdated":
				await InspectCommands.OutdatedAsync(args);
				return 0;

			case "list" or "ls":
				InspectCommands.List();
				return 0;

			case "why":
				InspectCommands.Why(args);
				return 0;

			case "patch":
				await PatchCommand.RunAsync(args);
				return 0;

			case "unused":
				return UnusedCommand.Run();

			case "size":
				ReportCommands.Size();
				return 0;

			case "licenses":
				ReportCommands.Licenses();
				return 0;

			case "search":
				await InspectCommands.SearchAsync(args);
				return 0;

			case "info":
				await InspectCommands.InfoAsync(args);
				return 0;

			case "versions":
				await InspectCommands.VersionsAsync(args);
				return 0;

			case "pack":
				PublishCommands.Pack(args);
				return 0;

			case "publish":
				await PublishCommands.PublishAsync(args);
				return 0;

			case "login":
				return await LoginCommands.LoginAsync(args);

			case "logout":
				return LoginCommands.Logout();

			case "whoami":
				return LoginCommands.WhoAmI();

			case "registry":
				return await RegistryCommands.RunAsync(args);

			case "tool":
				return await ToolCommand.RunAsync(args);

			case "cache":
				return CacheCommand.Run(args);

			case "graph":
				return GraphCommand.Run(args);

			case "audit":
				return await AuditCommand.RunAsync(args);

			case "history":
				return await HistoryCommand.RunAsync(args);

			case "owner":
				return await OwnerCommand.RunAsync(args);

			case "deprecate":
				return await DeprecateCommand.RunAsync(args);

			case "completion":
				return CompletionCommand.Run(args);

			case "self-update":
				await SelfUpdateCommand.RunAsync(args);
				return 0;

			case "version":
				Out.Info($"crater {Fmt.Val(BuildInfo.Version)}");
				return 0;

			default:
				Out.Error($"Unknown command {Fmt.Cmd(args.Command!)}. See {Fmt.Cmd("crater --help")}.");
				return 1;
		}
	}
}
