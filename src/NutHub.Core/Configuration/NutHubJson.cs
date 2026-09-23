using System.Text.Encodings.Web;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace NutHub.Core.Configuration;

/// <summary>The JSON conventions of NutHub: camelCase names, enums as camelCase strings.</summary>
public static class NutHubJson
{
    /// <summary>For the configuration file: indented, nulls omitted.</summary>
    public static JsonSerializerOptions File { get; } = CreateFileOptions();

    /// <summary>For the web API and anything compact.</summary>
    public static JsonSerializerOptions Compact { get; } = Create(indented: false);

    public static JsonSerializerOptions Create(bool indented)
    {
        var options = new JsonSerializerOptions(JsonSerializerDefaults.Web)
        {
            WriteIndented = indented,
            DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
            ReadCommentHandling = JsonCommentHandling.Skip,
            AllowTrailingCommas = true,
        };
        options.Converters.Add(new JsonStringEnumConverter(JsonNamingPolicy.CamelCase));
        return options;
    }

    private static JsonSerializerOptions CreateFileOptions()
    {
        // A file read by people: keep '+', '<', '&'... as they are instead of escape sequences.
        var options = Create(indented: true);
        options.Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping;
        return options;
    }

    /// <summary>A deep copy through JSON.</summary>
    public static T Clone<T>(T value) =>
        JsonSerializer.Deserialize<T>(JsonSerializer.SerializeToUtf8Bytes(value, File), File)!;
}
