using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.Json.Serialization.Metadata;
using NutHub.Core.Configuration;
using NutHub.Web.Api;

namespace NutHub.Web.Admin;

/// <summary>
/// Reads and writes configuration sections as JSON for the settings endpoints. Updates are merged onto the current
/// section (members absent from the body keep their value, nested objects merge), and certificate passwords follow
/// the secret rules: never returned, only <c>certificatePasswordSet</c>; on writes null keeps, "" clears.
/// </summary>
internal static class SectionJson
{
    private const string SecretName = "certificatePassword";
    private const string SecretFlag = "certificatePasswordSet";

    // The file conventions, except that null is refused where a value is required ("tls": null, "listen": null...),
    // which would otherwise reach the configuration store and fail there as an internal error.
    private static readonly JsonSerializerOptions Strict = new(NutHubJson.File) { RespectNullableAnnotations = true };

    /// <summary>The section as the API shows it.</summary>
    public static JsonObject View<T>(T section) where T : class
    {
        JsonObject node = JsonSerializer.SerializeToNode(section, ApiJson.Options)!.AsObject();
        HideSecrets(node);
        return node;
    }

    /// <summary>A new section: <paramref name="current"/> with the members of <paramref name="body"/> applied.</summary>
    public static T Merge<T>(T current, JsonElement body) where T : class
    {
        if (body.ValueKind != JsonValueKind.Object)
        {
            throw ApiException.BadRequest("The request body must be a JSON object.");
        }

        JsonObject node = JsonSerializer.SerializeToNode(current, NutHubJson.File)!.AsObject();
        MergeInto(node, body, NutHubJson.File.GetTypeInfo(typeof(T)), "");
        try
        {
            return node.Deserialize<T>(Strict) ?? throw ApiException.BadRequest("The request body is empty.");
        }
        catch (JsonException ex)
        {
            string field = string.IsNullOrEmpty(ex.Path) ? "body" : ex.Path.TrimStart('$', '.');
            throw ApiException.Validation(field.Length == 0 ? "body" : field, "This value has the wrong type.");
        }
    }

    private static void MergeInto(JsonObject target, JsonElement patch, JsonTypeInfo type, string path)
    {
        foreach (JsonProperty member in patch.EnumerateObject())
        {
            JsonPropertyInfo? property = type.Properties.FirstOrDefault(
                p => string.Equals(p.Name, member.Name, StringComparison.OrdinalIgnoreCase));
            if (property is null || property.Set is null)
            {
                continue; // Unknown or computed (certificatePasswordSet): ignored.
            }

            string name = property.Name;
            if (name == SecretName)
            {
                if (member.Value.ValueKind == JsonValueKind.Null)
                {
                    continue; // Keep the stored password.
                }

                if (member.Value.ValueKind == JsonValueKind.String && member.Value.GetString()!.Length == 0)
                {
                    target.Remove(name);
                    continue;
                }
            }

            JsonTypeInfo propertyType = NutHubJson.File.GetTypeInfo(property.PropertyType);
            if (member.Value.ValueKind == JsonValueKind.Object && propertyType.Kind == JsonTypeInfoKind.Object &&
                target[name] is JsonObject child)
            {
                MergeInto(child, member.Value, propertyType, path + name + ".");
                continue;
            }

            // No list of a section holds optional items; the null annotations only cover members.
            if (member.Value.ValueKind == JsonValueKind.Array)
            {
                int index = 0;
                foreach (JsonElement item in member.Value.EnumerateArray())
                {
                    if (item.ValueKind == JsonValueKind.Null)
                    {
                        throw ApiException.Validation($"{path}{name}[{index}]", "A value is required.");
                    }

                    index++;
                }
            }

            target[name] = JsonNode.Parse(member.Value.GetRawText());
        }
    }

    private static void HideSecrets(JsonNode? node)
    {
        switch (node)
        {
            case JsonObject obj:
                if (obj.ContainsKey(SecretName))
                {
                    bool set = obj[SecretName] is JsonValue v && v.TryGetValue(out string? s) && !string.IsNullOrEmpty(s);
                    obj.Remove(SecretName);
                    obj[SecretFlag] = set;
                }

                foreach (var (_, child) in obj.ToList())
                {
                    HideSecrets(child);
                }

                break;
            case JsonArray array:
                foreach (JsonNode? item in array)
                {
                    HideSecrets(item);
                }

                break;
        }
    }
}
