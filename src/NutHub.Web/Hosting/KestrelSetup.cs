using System.Net;
using System.Security.Cryptography.X509Certificates;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Server.Kestrel.Core;

namespace NutHub.Web.Hosting;

/// <summary>The listeners of one panel instance, from its <see cref="PanelBinding"/>.</summary>
internal static class KestrelSetup
{
    /// <summary>Largest accepted request body: the biggest legitimate one is a NUT import of a few kilobytes.</summary>
    public const long MaxRequestBodyBytes = 1024 * 1024;

    public static void Configure(IWebHostBuilder web, PanelBinding binding, X509Certificate2? certificate)
    {
        web.ConfigureKestrel(kestrel =>
        {
            kestrel.AddServerHeader = false;
            kestrel.Limits.MaxRequestBodySize = MaxRequestBodyBytes;
            if (binding.HttpEnabled)
            {
                Listen(kestrel, binding.BindAddress, binding.HttpPort, null);
            }

            if (binding.HttpsEnabled && certificate is not null)
            {
                Listen(kestrel, binding.BindAddress, binding.HttpsPort, listen => listen.UseHttps(certificate));
            }
        });
    }

    private static void Listen(KestrelServerOptions kestrel, string address, int port, Action<ListenOptions>? configure)
    {
        Action<ListenOptions> apply = configure ?? (_ => { });
        if (address == "*")
        {
            // IPv6 dual-mode where available, IPv4 otherwise.
            kestrel.ListenAnyIP(port, apply);
        }
        else
        {
            kestrel.Listen(IPAddress.Parse(address), port, apply);
        }
    }
}
