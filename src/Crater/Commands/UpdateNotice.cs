using System.Text.Json.Serialization;
using Crater.Core;
using Crater.Util;
using Crater.Versioning;

namespace Crater.Commands;

public static class UpdateNotice
{
	static readonly TimeSpan CheckEvery = TimeSpan.FromHours(24);
	static readonly TimeSpan WaitAtMost = TimeSpan.FromSeconds(2.5);

	sealed class State
	{
		[JsonPropertyName("checked_at")] public DateTimeOffset CheckedAt { get; set; }
		[JsonPropertyName("latest")] public string Latest { get; set; } = "";
	}

	static string StatePath => Path.Combine(PackageCache.Root, "update-check.json");

	public static async Task ShowAsync(string? command)
	{
		bool skipped = command is null or "self-update" or "version" ||
			Console.IsOutputRedirected ||
			Environment.GetEnvironmentVariable("CI") is not null ||
			Environment.GetEnvironmentVariable("CRATER_NO_UPDATE_CHECK") is not null ||
			BuildInfo.UpdateRepo.Length == 0;

		if (skipped || !SemVer.TryParse(BuildInfo.Version, out SemVer current))
		{
			return;
		}

		try
		{
			State state = File.Exists(StatePath) ? Json.ReadFile<State>(StatePath) : new State();

			if (DateTimeOffset.UtcNow - state.CheckedAt > CheckEvery)
			{
				Task<string> lookup = SelfUpdateCommand.LatestVersionAsync();
				bool finished = await Task.WhenAny(lookup, Task.Delay(WaitAtMost)) == lookup && lookup.IsCompletedSuccessfully;

				state.CheckedAt = finished ? DateTimeOffset.UtcNow : DateTimeOffset.UtcNow - CheckEvery + TimeSpan.FromHours(1);
				state.Latest = finished ? lookup.Result : state.Latest;

				Directory.CreateDirectory(PackageCache.Root);
				Json.WriteText(StatePath, Json.Serialize(state));
			}

			if (SemVer.TryParse(state.Latest, out SemVer latest) && latest > current)
			{
				Out.Info("");
				Out.Info($"crater {Fmt.Good(latest)} is available (you have {Fmt.Val(current)}). Run {Fmt.Cmd("crater self-update")}.");
			}
		}
		catch (Exception exception) when (exception is CraterException or IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
		{
		}
	}
}
