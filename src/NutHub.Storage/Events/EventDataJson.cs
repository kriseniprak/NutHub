using System.Buffers;
using System.Text;
using System.Text.Json;

namespace NutHub.Storage.Events;

/// <summary>
/// The <c>data</c> column of an event: its details dictionary as a flat JSON object of strings. Written and read by
/// hand (no reflection) so it keeps working if the application is ever trimmed.
/// </summary>
internal static class EventDataJson
{
    public static string? Serialize(IReadOnlyDictionary<string, string>? data)
    {
        if (data is null)
        {
            return null;
        }

        var buffer = new ArrayBufferWriter<byte>();
        using (var writer = new Utf8JsonWriter(buffer))
        {
            writer.WriteStartObject();
            foreach ((string key, string value) in data)
            {
                writer.WriteString(key, value);
            }

            writer.WriteEndObject();
        }

        return Encoding.UTF8.GetString(buffer.WrittenSpan);
    }

    /// <summary>Null for a missing or unreadable value: a damaged detail must not hide the event itself.</summary>
    public static IReadOnlyDictionary<string, string>? Deserialize(string? json)
    {
        if (string.IsNullOrEmpty(json))
        {
            return null;
        }

        try
        {
            using JsonDocument document = JsonDocument.Parse(json);
            if (document.RootElement.ValueKind != JsonValueKind.Object)
            {
                return null;
            }

            var result = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (JsonProperty property in document.RootElement.EnumerateObject())
            {
                result[property.Name] = property.Value.ValueKind == JsonValueKind.String
                    ? property.Value.GetString()!
                    : property.Value.GetRawText();
            }

            return result;
        }
        catch (JsonException)
        {
            return null;
        }
    }
}
