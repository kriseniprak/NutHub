using System.Net;

namespace NutHub.Core.Security;

/// <summary>
/// An allow-list of client networks in CIDR notation. An empty list allows everybody. IPv4 clients seen through a
/// dual-mode IPv6 socket (::ffff:192.168.1.5) are matched as IPv4.
/// </summary>
public sealed class AddressFilter
{
    public static readonly AddressFilter AllowAll = new([]);

    private readonly IPNetwork[] _networks;

    public AddressFilter(IEnumerable<IPNetwork> networks)
    {
        _networks = networks.ToArray();
    }

    public bool IsEmpty => _networks.Length == 0;

    /// <summary>Parses entries such as "192.168.1.0/24", "10.0.0.5", "fe80::/10"; throws on an invalid one.</summary>
    public static AddressFilter Parse(IEnumerable<string>? entries)
    {
        var list = new List<IPNetwork>();
        foreach (string entry in entries ?? [])
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (!TryParseNetwork(entry, out IPNetwork network))
            {
                throw new FormatException($"'{entry}' is not a valid address or network (CIDR).");
            }

            list.Add(network);
        }

        return new AddressFilter(list);
    }

    public static bool TryParseNetwork(string entry, out IPNetwork network)
    {
        entry = entry.Trim();
        if (IPNetwork.TryParse(entry, out network))
        {
            return true;
        }

        if (IPAddress.TryParse(entry, out IPAddress? address))
        {
            if (address.IsIPv4MappedToIPv6)
            {
                address = address.MapToIPv4();
            }

            network = new IPNetwork(address, address.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork ? 32 : 128);
            return true;
        }

        return false;
    }

    public bool IsAllowed(IPAddress? address)
    {
        if (_networks.Length == 0)
        {
            return true;
        }

        if (address is null)
        {
            return false;
        }

        if (address.IsIPv4MappedToIPv6)
        {
            address = address.MapToIPv4();
        }

        foreach (IPNetwork network in _networks)
        {
            if (network.BaseAddress.AddressFamily == address.AddressFamily && network.Contains(address))
            {
                return true;
            }
        }

        return false;
    }
}
