using NutHub.Core.Configuration;

namespace NutHub.Web.Hosting;

/// <summary>
/// How one instance of the panel listens. Fixed for the lifetime of that instance: any change of these settings
/// starts a new instance.
/// </summary>
internal sealed record PanelBinding(
    string BindAddress,
    bool HttpEnabled,
    int HttpPort,
    bool HttpsEnabled,
    int HttpsPort,
    bool RedirectHttpToHttps)
{
    public static PanelBinding From(WebSettings web) =>
        new(web.BindAddress, web.HttpEnabled, web.HttpPort, web.HttpsEnabled, web.HttpsPort,
            web.RedirectHttpToHttps && web.HttpEnabled && web.HttpsEnabled);

    /// <summary>HTTP only, for when the certificate cannot be loaded: the panel stays reachable to fix it.</summary>
    public PanelBinding WithoutHttps() => this with { HttpsEnabled = false, RedirectHttpToHttps = false };

    /// <summary>Whether a configuration change needs a new listener (as opposed to settings read per request).</summary>
    public static bool RequiresRestart(WebSettings previous, WebSettings current)
    {
        if (previous.Enabled != current.Enabled)
        {
            return true;
        }

        if (!current.Enabled)
        {
            return false;
        }

        // The certificate only matters while HTTPS is on (turning HTTPS on is a change of its own).
        return !string.Equals(previous.BindAddress, current.BindAddress, StringComparison.OrdinalIgnoreCase) ||
               previous.HttpEnabled != current.HttpEnabled ||
               previous.HttpPort != current.HttpPort ||
               previous.HttpsEnabled != current.HttpsEnabled ||
               previous.HttpsPort != current.HttpsPort ||
               previous.RedirectHttpToHttps != current.RedirectHttpToHttps ||
               (current.HttpsEnabled &&
                (!string.Equals(previous.CertificatePath, current.CertificatePath, StringComparison.Ordinal) ||
                 !string.Equals(previous.CertificatePassword, current.CertificatePassword, StringComparison.Ordinal)));
    }

    public override string ToString()
    {
        var parts = new List<string>(2);
        if (HttpEnabled)
        {
            parts.Add($"http://{Host}:{HttpPort}");
        }

        if (HttpsEnabled)
        {
            parts.Add($"https://{Host}:{HttpsPort}");
        }

        return parts.Count == 0 ? "(no listener)" : string.Join(", ", parts);
    }

    private string Host => BindAddress == "*" ? "*" : BindAddress.Contains(':') ? $"[{BindAddress}]" : BindAddress;
}
