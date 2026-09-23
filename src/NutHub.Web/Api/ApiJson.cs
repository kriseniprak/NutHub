using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;
using NutHub.Core.Configuration;

namespace NutHub.Web.Api;

/// <summary>
/// The JSON conventions of the web API: those of <see cref="NutHubJson.Compact"/> (camelCase, enums as camelCase
/// strings), except that nulls are written, because the panel distinguishes "unknown" (null) from "absent", and
/// timestamps are always UTC with a "Z" and milliseconds.
/// </summary>
internal static class ApiJson
{
    public static JsonSerializerOptions Options { get; } = Create();

    /// <summary>Copies the API conventions into the options ASP.NET Core uses for request and response bodies.</summary>
    public static void Apply(JsonSerializerOptions target)
    {
        target.PropertyNamingPolicy = Options.PropertyNamingPolicy;
        target.DictionaryKeyPolicy = null;
        target.PropertyNameCaseInsensitive = true;
        target.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        target.ReadCommentHandling = JsonCommentHandling.Skip;
        target.AllowTrailingCommas = true;
        target.NumberHandling = JsonNumberHandling.Strict;
        target.Converters.Clear();
        foreach (JsonConverter converter in Options.Converters)
        {
            target.Converters.Add(converter);
        }
    }

    private static JsonSerializerOptions Create()
    {
        var options = NutHubJson.Create(indented: false);
        options.DefaultIgnoreCondition = JsonIgnoreCondition.Never;
        options.NumberHandling = JsonNumberHandling.Strict;
        // Dictionary keys are data (variable names, option keys, header names): never rename them.
        options.DictionaryKeyPolicy = null;
        options.Converters.Insert(0, new UtcTimestampConverter());
        options.MakeReadOnly(populateMissingResolver: true);
        return options;
    }
}

/// <summary>Writes "2026-09-21T14:03:12.345Z"; reads any ISO 8601 form.</summary>
internal sealed class UtcTimestampConverter : JsonConverter<DateTimeOffset>
{
    public const string Format = "yyyy-MM-dd'T'HH:mm:ss.fff'Z'";

    public override DateTimeOffset Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        string? text = reader.GetString();
        if (text is null || !DateTimeOffset.TryParse(text, CultureInfo.InvariantCulture,
                                                     DateTimeStyles.AssumeUniversal | DateTimeStyles.AdjustToUniversal,
                                                     out DateTimeOffset value))
        {
            throw new JsonException("Expected an ISO 8601 timestamp.");
        }

        return value;
    }

    public override void Write(Utf8JsonWriter writer, DateTimeOffset value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.UtcDateTime.ToString(Format, CultureInfo.InvariantCulture));
}
