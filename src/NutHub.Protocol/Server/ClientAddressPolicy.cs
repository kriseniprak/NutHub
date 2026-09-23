using System.Net;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Core.Security;

namespace NutHub.Protocol.Server;

/// <summary>
/// Which client addresses may use the NUT server (<see cref="NutServerSettings.AllowedNetworks"/>). The parsed
/// filter is cached per configuration snapshot, since it is consulted on every connection and every command.
/// </summary>
internal sealed class ClientAddressPolicy(ILogger logger)
{
    private static readonly List<string> NoEntries = [];

    private Cache? _cache;

    public bool IsAllowed(NutServerSettings settings, IPAddress address)
    {
        List<string> entries = settings.AllowedNetworks ?? NoEntries;
        Cache cache = Volatile.Read(ref _cache) is { } c && ReferenceEquals(c.Source, entries)
            ? c
            : Build(entries);
        return !cache.DenyAll && cache.Filter.IsAllowed(address);
    }

    private Cache Build(List<string> entries)
    {
        var networks = new List<IPNetwork>();
        int invalid = 0;
        foreach (string entry in entries)
        {
            if (string.IsNullOrWhiteSpace(entry))
            {
                continue;
            }

            if (AddressFilter.TryParseNetwork(entry, out IPNetwork network))
            {
                networks.Add(network);
            }
            else
            {
                invalid++;
                logger.LogError("Ignoring '{Entry}' in the NUT allowed networks: not an address or a network.", entry);
            }
        }

        // A list made only of invalid entries must not turn into "everybody allowed": the administrator meant to
        // restrict access.
        var cache = new Cache(entries, new AddressFilter(networks), DenyAll: networks.Count == 0 && invalid > 0);
        Volatile.Write(ref _cache, cache);
        return cache;
    }

    private sealed record Cache(List<string> Source, AddressFilter Filter, bool DenyAll);
}
