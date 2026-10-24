namespace Crater.Util;

public static class CraterPaths
{
	public static string Cache
	{
		get
		{
			string? custom = Environment.GetEnvironmentVariable("CRATER_CACHE");

			if (!string.IsNullOrWhiteSpace(custom))
			{
				return custom;
			}

			string local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);

			return Path.Combine(local, "crater", "cache");
		}
	}
}
