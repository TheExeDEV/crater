using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crater.Models;

public sealed class DefinitionFile
{
	[JsonPropertyName("path")] public string Path { get; set; } = "";
	[JsonPropertyName("name")] public string? Name { get; set; }
	[JsonPropertyName("priority")] public int? Priority { get; set; }
	[JsonPropertyName("show_as_module")] public bool? ShowAsModule { get; set; }
	[JsonPropertyName("message")] public string? Message { get; set; }

	[JsonIgnore] public bool HasOptions => Name is not null || Priority is not null || ShowAsModule is not null || Message is not null;

	public void Take(DefinitionFile options)
	{
		Name ??= options.Name;
		Priority ??= options.Priority;
		ShowAsModule ??= options.ShowAsModule;
		Message ??= options.Message;
	}
}

public sealed class DefinitionFilesConverter : JsonConverter<List<DefinitionFile>>
{
	public override List<DefinitionFile> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		if (reader.TokenType == JsonTokenType.String)
		{
			return [new DefinitionFile { Path = reader.GetString()! }];
		}

		if (reader.TokenType != JsonTokenType.StartArray)
		{
			throw new JsonException("path_to_definition_files must be a list of paths or of { \"path\": ... } entries");
		}

		List<DefinitionFile> files = [];

		while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
		{
			if (reader.TokenType == JsonTokenType.String)
			{
				files.Add(new DefinitionFile { Path = reader.GetString()! });
				continue;
			}

			DefinitionFile entry = JsonSerializer.Deserialize<DefinitionFile>(ref reader, options)
				?? throw new JsonException("path_to_definition_files has an empty entry");

			if (string.IsNullOrWhiteSpace(entry.Path))
			{
				throw new JsonException("every entry of path_to_definition_files needs a \"path\"");
			}

			files.Add(entry);
		}

		return files;
	}

	public override void Write(Utf8JsonWriter writer, List<DefinitionFile> value, JsonSerializerOptions options)
	{
		writer.WriteStartArray();

		foreach (DefinitionFile file in value)
		{
			writer.WriteStringValue(file.Path);
		}

		writer.WriteEndArray();
	}
}
