using Microsoft.AspNetCore.Http;
using NutHub.Core.Configuration;
using NutHub.Web.Api;
using NutHub.Web.Auth;

namespace NutHub.Web.Admin;

/// <summary>
/// Every configuration write of the API goes through here: the change is recorded with the web user and address,
/// and a failed validation becomes <c>400 validation</c> with the field paths of the request body.
/// </summary>
internal sealed class ConfigWriter(IConfigStore store)
{
    public NutHubConfig Current => store.Current;

    /// <param name="context">The request, for the origin of the change.</param>
    /// <param name="mutate">Works on a private copy; may throw <see cref="ApiException"/> to cancel the change.</param>
    /// <param name="description">For the log and the event.</param>
    /// <param name="mapField">Translates configuration paths ("ups[3].name") into body paths ("name").</param>
    public async Task<NutHubConfig> UpdateAsync(HttpContext context, Action<NutHubConfig> mutate, string description,
                                                Func<string, string>? mapField = null)
    {
        try
        {
            return await store.UpdateAsync(mutate, WebIdentity.Origin(context), description, context.RequestAborted)
                .ConfigureAwait(false);
        }
        catch (ConfigValidationException ex)
        {
            var fields = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var (key, message) in ex.Errors)
            {
                fields.TryAdd(mapField?.Invoke(key) ?? key, message);
            }

            throw ApiException.Validation(fields);
        }
    }
}

/// <summary>Translations of configuration field paths into the paths of a request body.</summary>
internal static class FieldMaps
{
    /// <summary>"web.httpPort" becomes "httpPort" for a body that is the <c>web</c> section.</summary>
    public static Func<string, string> Section(string prefix) => key =>
        key.StartsWith(prefix + ".", StringComparison.Ordinal) ? key[(prefix.Length + 1)..] : key;

    /// <summary>"ups[3].lowBattery.chargePercent" becomes "lowBattery.chargePercent" for the body of ups[3].</summary>
    public static Func<string, string> Item(string list, int index) => key =>
    {
        string prefix = $"{list}[{index}]";
        if (key == prefix)
        {
            return "name";
        }

        return key.StartsWith(prefix + ".", StringComparison.Ordinal) ? key[(prefix.Length + 1)..] : key;
    };
}
