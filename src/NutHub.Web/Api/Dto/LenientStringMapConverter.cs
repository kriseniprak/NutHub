using System.Buffers;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NutHub.Web.Api.Dto;

/// <summary>
/// Reads a string map whose values may also be sent as JSON numbers or booleans (a form serialises
/// <c>"port": 161</c> as easily as <c>"port": "161"</c>); driver options and overrides are stored as strings anyway.
/// </summary>
internal sealed class LenientStringMapConverter : JsonConverter<Dictionary<string, string?>>
{
    public override Dictionary<string, string?>? Read(ref Utf8JsonReader reader, Type typeToConvert,
                                                     JsonSerializerOptions options)
    {
        if (reader.TokenType == JsonTokenType.Null)
        {
            return null;
        }

        if (reader.TokenType != JsonTokenType.StartObject)
        {
            throw new JsonException("Expected an object of strings.");
        }

        var result = new Dictionary<string, string?>(StringComparer.Ordinal);
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            string key = reader.GetString() ?? throw new JsonException("Expected a property name.");
            reader.Read();
            result[key] = reader.TokenType switch
            {
                JsonTokenType.String => reader.GetString(),
                JsonTokenType.Number => System.Text.Encoding.UTF8.GetString(
                    reader.HasValueSequence ? reader.ValueSequence.ToArray() : reader.ValueSpan),
                JsonTokenType.True => "true",
                JsonTokenType.False => "false",
                JsonTokenType.Null => null,
                _ => throw new JsonException($"The value of '{key}' must be a string."),
            };
        }

        return result;
    }

    public override void Write(Utf8JsonWriter writer, Dictionary<string, string?> value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        foreach (var (key, v) in value)
        {
            writer.WriteString(key, v);
        }

        writer.WriteEndObject();
    }
}
