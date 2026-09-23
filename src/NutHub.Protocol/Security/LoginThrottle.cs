using System.Net;
using System.Net.Sockets;
using Microsoft.Extensions.Logging;

namespace NutHub.Protocol.Security;

/// <summary>
/// Slows down password guessing: after <see cref="NutProtocolOptions.FailedPasswordLimit"/> failed password checks
/// from one client within <see cref="NutProtocolOptions.FailedPasswordWindow"/>, every privileged command from it is
/// refused without checking for <see cref="NutProtocolOptions.LockoutDuration"/>. Refusing without checking also
/// keeps a flood of guesses from burning CPU on key derivations.
/// </summary>
/// <remarks>
/// IPv6 clients are grouped by /64 prefix, since a single host can pick any address of its prefix. The table is
/// bounded; when it is full of active entries new addresses are not tracked (and a warning is logged once).
/// </remarks>
internal sealed class LoginThrottle
{
    private const int MaxEntries = 10_000;

    private readonly TimeProvider _time;
    private readonly NutProtocolOptions _options;
    private readonly ILogger _logger;
    private readonly object _lock = new();
    private readonly Dictionary<IPAddress, Entry> _entries = [];
    private bool _fullWarned;

    public LoginThrottle(TimeProvider time, NutProtocolOptions options, ILogger logger)
    {
        _time = time;
        _options = options;
        _logger = logger;
    }

    /// <summary>Whether privileged commands from this address are currently refused.</summary>
    public bool IsLockedOut(IPAddress address)
    {
        IPAddress key = KeyOf(address);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out Entry? entry) || entry.LockedUntil is not { } until)
            {
                return false;
            }

            if (now < until)
            {
                return true;
            }

            entry.LockedUntil = null;
            entry.Failures.Clear();
            return false;
        }
    }

    /// <summary>Records a failed password check; may start a lockout.</summary>
    public void RecordFailure(IPAddress address)
    {
        IPAddress key = KeyOf(address);
        DateTimeOffset now = _time.GetUtcNow();
        lock (_lock)
        {
            if (!_entries.TryGetValue(key, out Entry? entry))
            {
                if (_entries.Count >= MaxEntries)
                {
                    RemoveExpired(now);
                    if (_entries.Count >= MaxEntries)
                    {
                        if (!_fullWarned)
                        {
                            _fullWarned = true;
                            _logger.LogWarning(
                                "Failed NUT logins come from more than {Count} addresses; new addresses are not throttled until older entries expire.",
                                MaxEntries);
                        }

                        return;
                    }
                }

                entry = new Entry();
                _entries[key] = entry;
            }

            Prune(entry, now);
            entry.Failures.Enqueue(now);
            if (entry.LockedUntil is null && entry.Failures.Count >= _options.FailedPasswordLimit)
            {
                entry.LockedUntil = now + _options.LockoutDuration;
                entry.Failures.Clear();
                _logger.LogWarning(
                    "{Count} failed NUT password checks from {Address} within {Window} min: its privileged commands are refused for {Lockout} min.",
                    _options.FailedPasswordLimit, Describe(address, key), _options.FailedPasswordWindow.TotalMinutes,
                    _options.LockoutDuration.TotalMinutes);
            }
        }
    }

    /// <summary>Forgets addresses that have neither recent failures nor an active lockout.</summary>
    public void Cleanup()
    {
        lock (_lock)
        {
            RemoveExpired(_time.GetUtcNow());
            if (_entries.Count < MaxEntries / 2)
            {
                _fullWarned = false;
            }
        }
    }

    internal int TrackedAddresses
    {
        get
        {
            lock (_lock)
            {
                return _entries.Count;
            }
        }
    }

    private void RemoveExpired(DateTimeOffset now)
    {
        foreach (var (key, entry) in _entries.ToList())
        {
            Prune(entry, now);
            if (entry.LockedUntil is { } until && now >= until)
            {
                entry.LockedUntil = null;
            }

            if (entry.LockedUntil is null && entry.Failures.Count == 0)
            {
                _entries.Remove(key);
            }
        }
    }

    private void Prune(Entry entry, DateTimeOffset now)
    {
        while (entry.Failures.TryPeek(out DateTimeOffset oldest) && now - oldest > _options.FailedPasswordWindow)
        {
            entry.Failures.Dequeue();
        }
    }

    private static IPAddress KeyOf(IPAddress address)
    {
        if (address.IsIPv4MappedToIPv6)
        {
            return address.MapToIPv4();
        }

        if (address.AddressFamily != AddressFamily.InterNetworkV6)
        {
            return address;
        }

        byte[] bytes = address.GetAddressBytes();
        Array.Clear(bytes, 8, 8);
        return new IPAddress(bytes);
    }

    private static string Describe(IPAddress address, IPAddress key) =>
        key.AddressFamily == AddressFamily.InterNetworkV6 ? $"{address} (prefix {key}/64)" : address.ToString();

    private sealed class Entry
    {
        public Queue<DateTimeOffset> Failures { get; } = new();

        public DateTimeOffset? LockedUntil { get; set; }
    }
}
