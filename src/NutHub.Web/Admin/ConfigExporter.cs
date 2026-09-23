using System.Text.Json;
using System.Text.Json.Nodes;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Security;

namespace NutHub.Web.Admin;

/// <summary>
/// The configuration file without any secret: no password hash, no security stamp, no encrypted value, no header
/// value, no secret driver option. Safe to attach to a support request.
/// </summary>
internal static class ConfigExporter
{
    public static byte[] Export(NutHubConfig config, IDriverCatalog drivers, ISecretProtector secrets)
    {
        JsonObject root = JsonSerializer.SerializeToNode(config, NutHubJson.File)!.AsObject();

        Remove(root["nut"]?["tls"], "certificatePassword");
        Remove(root["web"], "certificatePassword");
        Remove(root["notifications"]?["email"], "password");
        foreach (JsonNode? hook in root["notifications"]?["webhooks"]?.AsArray() ?? [])
        {
            if (hook?["headers"] is JsonObject headers)
            {
                headers.Clear();
            }
        }

        if (root["ups"] is JsonArray upsList)
        {
            for (int i = 0; i < upsList.Count && i < config.Ups.Count; i++)
            {
                if (upsList[i]?["options"] is not JsonObject options)
                {
                    continue;
                }

                var secretKeys = new HashSet<string>(DriverCatalog.SecretKeys(drivers.Find(config.Ups[i].Driver)),
                                                     StringComparer.OrdinalIgnoreCase);
                foreach (var (key, _) in options.ToList())
                {
                    if (secretKeys.Contains(key))
                    {
                        options.Remove(key);
                    }
                }
            }
        }

        foreach (string list in new[] { "nutUsers", "webUsers" })
        {
            foreach (JsonNode? user in root[list]?.AsArray() ?? [])
            {
                Remove(user, "passwordHash");
                Remove(user, "securityStamp");
            }
        }

        RemoveProtectedValues(root, secrets);
        return JsonSerializer.SerializeToUtf8Bytes(root, NutHubJson.File);
    }

    private static void Remove(JsonNode? node, string property)
    {
        if (node is JsonObject obj)
        {
            obj.Remove(property);
        }
    }

    /// <summary>Catches secrets of drivers this build does not know, and any future secret field.</summary>
    private static void RemoveProtectedValues(JsonNode? node, ISecretProtector secrets)
    {
        switch (node)
        {
            case JsonObject obj:
                foreach (var (key, child) in obj.ToList())
                {
                    if (child is JsonValue value && value.TryGetValue(out string? text) && secrets.IsProtected(text))
                    {
                        obj.Remove(key);
                    }
                    else
                    {
                        RemoveProtectedValues(child, secrets);
                    }
                }

                break;
            case JsonArray array:
                for (int i = array.Count - 1; i >= 0; i--)
                {
                    if (array[i] is JsonValue value && value.TryGetValue(out string? text) && secrets.IsProtected(text))
                    {
                        array.RemoveAt(i);
                    }
                    else
                    {
                        RemoveProtectedValues(array[i], secrets);
                    }
                }

                break;
        }
    }
}
