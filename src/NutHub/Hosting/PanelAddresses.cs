using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using NutHub.Core.Configuration;

namespace NutHub.Hosting;

/// <summary>The addresses to show at startup, so whoever started NutHub knows where the panel is.</summary>
internal static class PanelAddresses
{
    private const int MaxInterfaceAddresses = 4;

    /// <summary>The URLs the web panel should answer on, from its settings and this machine's addresses.</summary>
    public static IReadOnlyList<string> WebUrls(WebSettings web)
    {
        if (!web.Enabled)
        {
            return [];
        }

        IReadOnlyList<string> hosts = Hosts(web.BindAddress);
        var urls = new List<string>();
        if (web.HttpsEnabled)
        {
            urls.AddRange(hosts.Select(h => $"https://{h}{Port(web.HttpsPort, 443)}/"));
        }

        if (web.HttpEnabled)
        {
            urls.AddRange(hosts.Select(h => $"http://{h}{Port(web.HttpPort, 80)}/"));
        }

        return urls;
    }

    private static string Port(int port, int defaultPort) => port == defaultPort ? "" : ":" + port;

    private static IReadOnlyList<string> Hosts(string bindAddress)
    {
        if (bindAddress != "*" && IPAddress.TryParse(bindAddress, out IPAddress? bound) &&
            !bound.Equals(IPAddress.Any) && !bound.Equals(IPAddress.IPv6Any))
        {
            return [Format(bound)];
        }

        var hosts = new List<string> { "localhost" };
        try
        {
            string machine = Dns.GetHostName();
            if (!string.IsNullOrWhiteSpace(machine) && !string.Equals(machine, "localhost", StringComparison.OrdinalIgnoreCase))
            {
                hosts.Add(machine);
            }

            foreach (IPAddress address in InterfaceAddresses().Take(MaxInterfaceAddresses))
            {
                hosts.Add(Format(address));
            }
        }
        catch (Exception ex) when (ex is NetworkInformationException or SocketException or PlatformNotSupportedException)
        {
            // The addresses are a convenience; localhost is enough to go on.
        }

        return hosts;
    }

    private static IEnumerable<IPAddress> InterfaceAddresses() =>
        NetworkInterface.GetAllNetworkInterfaces()
            .Where(n => n.OperationalStatus == OperationalStatus.Up &&
                        n.NetworkInterfaceType is not (NetworkInterfaceType.Loopback or NetworkInterfaceType.Tunnel))
            .SelectMany(n => n.GetIPProperties().UnicastAddresses)
            .Select(a => a.Address)
            .Where(a => a.AddressFamily == AddressFamily.InterNetwork && !IPAddress.IsLoopback(a) &&
                        !a.ToString().StartsWith("169.254.", StringComparison.Ordinal))
            .Distinct();

    private static string Format(IPAddress address) =>
        address.AddressFamily == AddressFamily.InterNetworkV6 ? $"[{address}]" : address.ToString();
}
