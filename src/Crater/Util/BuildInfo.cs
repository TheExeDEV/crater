using System.Reflection;

namespace Crater.Util;

public static class BuildInfo
{
	public static string Version =>
		typeof(BuildInfo).Assembly.GetName().Version?.ToString(3) ?? "0.0.0";

	public static string DefaultRegistry => Metadata("DefaultRegistry");

	public static string UpdateRepo => Metadata("UpdateRepo");

	public static string GitHubClientId =>
		Environment.GetEnvironmentVariable("CRATER_GITHUB_CLIENT_ID") is { Length: > 0 } custom ? custom : Metadata("GitHubClientId");

	static string Metadata(string key)
	{
		AssemblyMetadataAttribute? attribute = typeof(BuildInfo).Assembly
			.GetCustomAttributes<AssemblyMetadataAttribute>()
			.FirstOrDefault(item => item.Key == key);

		return attribute?.Value ?? "";
	}
}
