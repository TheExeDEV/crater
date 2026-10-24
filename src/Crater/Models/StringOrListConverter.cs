using System.Text.Json;
using System.Text.Json.Serialization;

namespace Crater.Models;

public sealed class StringOrListConverter : JsonConverter<List<string>>
{
	public override List<string> Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
	{
		switch (reader.TokenType)
		{
			case JsonTokenType.Null:
				return [];

			case JsonTokenType.String:
				string single = reader.GetString() ?? "";

				return single.Length == 0 ? [] : [single];

			case JsonTokenType.StartArray:
				List<string> items = [];

				while (reader.Read() && reader.TokenType != JsonTokenType.EndArray)
				{
					if (reader.TokenType != JsonTokenType.String)
					{
						throw new JsonException("expected a list of strings");
					}

					string item = reader.GetString() ?? "";

					if (item.Length > 0)
					{
						items.Add(item);
					}
				}

				return items;

			default:
				throw new JsonException("expected a string or a list of strings");
		}
	}

	public override void Write(Utf8JsonWriter writer, List<string> value, JsonSerializerOptions options)
	{
		if (value.Count == 1)
		{
			writer.WriteStringValue(value[0]);
			return;
		}

		writer.WriteStartArray();

		foreach (string item in value)
		{
			writer.WriteStringValue(item);
		}

		writer.WriteEndArray();
	}
}
