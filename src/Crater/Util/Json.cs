using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization;

namespace Crater.Util;

public static class Json
{
	public static readonly JsonSerializerOptions Options = new()
	{
		ReadCommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
		WriteIndented = true,
		NewLine = "\n",
		PropertyNameCaseInsensitive = true,
		DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
		Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
	};

	public static readonly JsonDocumentOptions DocumentOptions = new()
	{
		CommentHandling = JsonCommentHandling.Skip,
		AllowTrailingCommas = true,
	};

	public static T ReadFile<T>(string path) => Parse<T>(File.ReadAllText(path), path);

	public static T Parse<T>(string json, string source)
	{
		try
		{
			T? value = JsonSerializer.Deserialize<T>(json, Options);

			if (value is null)
			{
				throw new CraterException($"{Fmt.File(source)}: empty JSON");
			}

			return value;
		}
		catch (JsonException exception)
		{
			throw new CraterException($"{Fmt.File(source)}: invalid JSON: {exception.Message}");
		}
	}

	public static string Serialize<T>(T value) => JsonSerializer.Serialize(value, Options);

	public static string Serialize(JsonNode node) => node.ToJsonString(Options);

	public static JsonObject ReadObject(string path)
	{
		if (!File.Exists(path))
		{
			return new JsonObject();
		}

		string text = File.ReadAllText(path);

		if (string.IsNullOrWhiteSpace(text))
		{
			return new JsonObject();
		}

		try
		{
			if (JsonNode.Parse(text, documentOptions: DocumentOptions) is JsonObject result)
			{
				return result;
			}

			throw new CraterException($"{Fmt.File(path)}: expected a JSON object");
		}
		catch (JsonException exception)
		{
			throw new CraterException($"{Fmt.File(path)}: invalid JSON: {exception.Message}");
		}
	}

	public static void WriteText(string path, string text)
	{
		string? directory = Path.GetDirectoryName(path);

		if (!string.IsNullOrEmpty(directory))
		{
			Directory.CreateDirectory(directory);
		}

		File.WriteAllText(path, text.TrimEnd() + "\n", new UTF8Encoding(false));
	}
}
