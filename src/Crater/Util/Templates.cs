using System.Reflection;

namespace Crater.Util;

public static class Templates
{
	const string Root = "Templates/";

	public static IEnumerable<(string Path, string Content)> Under(string folder, IReadOnlyDictionary<string, string>? values = null)
	{
		Assembly assembly = typeof(Templates).Assembly;
		string prefix = Root + folder.TrimEnd('/') + "/";

		foreach (string resource in assembly.GetManifestResourceNames().OrderBy(name => name, StringComparer.Ordinal))
		{
			string normalized = resource.Replace('\\', '/');

			if (!normalized.StartsWith(prefix, StringComparison.Ordinal))
			{
				continue;
			}

			using Stream stream = assembly.GetManifestResourceStream(resource)!;
			using StreamReader reader = new(stream);

			yield return (normalized[prefix.Length..], Fill(reader.ReadToEnd(), values));
		}
	}

	public static string Fill(string text, IReadOnlyDictionary<string, string>? values)
	{
		if (values is null)
		{
			return text;
		}

		string result = text;

		foreach ((string key, string value) in values)
		{
			result = result.Replace("{{" + key + "}}", value);
		}

		return result;
	}

	public static void WriteTo(string folder, string target, IReadOnlyDictionary<string, string>? values, bool overwrite)
	{
		foreach ((string path, string content) in Under(folder, values))
		{
			string destination = Path.Combine(target, path.Replace('/', Path.DirectorySeparatorChar));

			if (File.Exists(destination) && !overwrite)
			{
				Out.Dim($"  kept {path}");
				continue;
			}

			Json.WriteText(destination, content);
			Out.Dim($"  wrote {path}");
		}
	}
}
