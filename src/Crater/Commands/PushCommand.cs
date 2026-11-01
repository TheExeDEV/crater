using Crater.Core;
using Crater.Integrations;
using Crater.Util;

namespace Crater.Commands;

public static class PushCommand
{
	public static async Task<int> RunAsync()
	{
		Project project = Common.LoadProject();

		if (!Azul.IsUsed(project))
		{
			throw new CraterException(
				$"{Fmt.Cmd("crater push")} sends packages to Studio through Azul, and this project does not use it. " +
				$"Set {Fmt.Val("\"sync\": \"azul\"")} in {Fmt.File("crater.settings.json")}, or with Rojo just run {Fmt.Cmd("crater dev")}.");
		}

		return Azul.Push(project, await ToolCommand.ExecutableAsync("azul"));
	}
}
