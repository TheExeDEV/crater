using System.Text.Json.Serialization;

namespace Crater.Util;

public sealed class Credentials
{
	[JsonPropertyName("login")] public string Login { get; set; } = "";
	[JsonPropertyName("token")] public string Token { get; set; } = "";
	[JsonPropertyName("scopes")] public string Scopes { get; set; } = "";

	static string Directory
	{
		get
		{
			string? custom = Environment.GetEnvironmentVariable("CRATER_CONFIG_DIR");

			if (!string.IsNullOrWhiteSpace(custom))
			{
				return custom;
			}

			return OperatingSystem.IsWindows()
				? Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "crater")
				: Path.Combine(Environment.GetEnvironmentVariable("XDG_CONFIG_HOME") is { Length: > 0 } xdg ? xdg : Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile), ".config"), "crater");
		}
	}

	public static string FilePath => Path.Combine(Directory, "credentials.json");

	public bool CanReadPrivate => Scopes.Split([',', ' '], StringSplitOptions.RemoveEmptyEntries).Contains("repo");

	public static Credentials? Load()
	{
		try
		{
			return File.Exists(FilePath) ? Json.ReadFile<Credentials>(FilePath) : null;
		}
		catch (Exception exception) when (exception is IOException or System.Text.Json.JsonException or CraterException)
		{
			return null;
		}
	}

	public void Save()
	{
		System.IO.Directory.CreateDirectory(Directory);

		string temporary = $"{FilePath}.{Guid.NewGuid():N}.tmp";

		File.WriteAllText(temporary, "");

		if (!OperatingSystem.IsWindows())
		{
			File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite);
		}

		File.WriteAllText(temporary, Json.Serialize(this));
		File.Move(temporary, FilePath, overwrite: true);
	}

	public static bool Delete()
	{
		if (!File.Exists(FilePath))
		{
			return false;
		}

		File.Delete(FilePath);

		return true;
	}
}
