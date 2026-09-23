using System.Net;
using System.Net.Sockets;

namespace NutHub.Web.Auth;

/// <summary>
/// Counts failed sign-ins per client address and per user name over a sliding window. Past the limit, further
/// attempts are refused without checking the password, which makes guessing impractical. Lives outside the web
/// application so that restarting the panel (new port...) does not reset the counters.
/// </summary>
internal sealed class LoginRateLimiter(TimeProvider time)
{
    public const int MaxFailures = 10;

    public static readonly TimeSpan Window = TimeSpan.FromMinutes(5);

    // Bounds memory when many addresses or names fail: expired entries are dropped past this many keys.
    private const int PruneThreshold = 4096;

    private const int MaxUserKeyLength = 256;

    private readonly Dictionary<string, Queue<DateTimeOffset>> _failures = new(StringComparer.Ordinal);

    // Attempts whose password is still being checked, per key: they count against the limit until they end.
    private readonly Dictionary<string, int> _pending = new(StringComparer.Ordinal);
    private readonly object _lock = new();

    /// <summary>How long the caller must wait before trying again; null when an attempt is allowed now.</summary>
    public TimeSpan? GetRetryAfter(string? address, string? userName)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_lock)
        {
            return Longest(RetryAfter(AddressKey(address), now), RetryAfter(UserKey(userName), now));
        }
    }

    /// <summary>
    /// Checks the limits and, when they allow an attempt, counts it at once (null is returned): the password check
    /// takes a while, and requests sent in parallel must not all get past the limit before the first one fails.
    /// Every allowed attempt must be closed with <see cref="EndAttempt"/>.
    /// </summary>
    public TimeSpan? TryBeginAttempt(string? address, string? userName)
    {
        DateTimeOffset now = time.GetUtcNow();
        string addressKey = AddressKey(address), userKey = UserKey(userName);
        lock (_lock)
        {
            TimeSpan? wait = Longest(RetryAfter(addressKey, now), RetryAfter(userKey, now));
            if (wait is null)
            {
                _pending[addressKey] = _pending.GetValueOrDefault(addressKey) + 1;
                _pending[userKey] = _pending.GetValueOrDefault(userKey) + 1;
            }

            return wait;
        }
    }

    /// <summary>Ends an attempt started by <see cref="TryBeginAttempt"/>; a failed one is recorded.</summary>
    public void EndAttempt(string? address, string? userName, bool failed)
    {
        lock (_lock)
        {
            Release(AddressKey(address));
            Release(UserKey(userName));
            if (failed)
            {
                RecordFailure(address, userName);
            }
        }
    }

    public void RecordFailure(string? address, string? userName)
    {
        DateTimeOffset now = time.GetUtcNow();
        lock (_lock)
        {
            if (_failures.Count > PruneThreshold)
            {
                Prune(now);
            }

            Add(AddressKey(address), now);
            Add(UserKey(userName), now);
        }
    }

    /// <summary>A successful sign-in clears the failures of that account (not those of the address).</summary>
    public void RecordSuccess(string userName)
    {
        lock (_lock)
        {
            _failures.Remove(UserKey(userName));
        }
    }

    // IPv6 clients are grouped by /64 prefix (like the NUT login throttle): a host can pick any address of its prefix.
    private static string AddressKey(string? address)
    {
        if (IPAddress.TryParse(address, out IPAddress? ip) && ip.AddressFamily == AddressFamily.InterNetworkV6 &&
            !ip.IsIPv4MappedToIPv6)
        {
            byte[] bytes = ip.GetAddressBytes();
            Array.Clear(bytes, 8, 8);
            return $"a:{new IPAddress(bytes)}/64";
        }

        return "a:" + (address ?? "unknown");
    }

    // Account names are short: a longer typed name only needs a bounded key, not a copy of a 1 MB request body.
    private static string UserKey(string? userName)
    {
        string name = (userName ?? "").Trim();
        return "u:" + (name.Length > MaxUserKeyLength ? name[..MaxUserKeyLength] : name).ToLowerInvariant();
    }

    private static TimeSpan? Longest(TimeSpan? a, TimeSpan? b) =>
        a is null ? b : b is null ? a : TimeSpan.FromTicks(Math.Max(a.Value.Ticks, b.Value.Ticks));

    private TimeSpan? RetryAfter(string key, DateTimeOffset now)
    {
        int pending = _pending.GetValueOrDefault(key);
        if (_failures.TryGetValue(key, out Queue<DateTimeOffset>? queue))
        {
            Expire(queue, now);
        }

        int failures = queue?.Count ?? 0;
        if (failures + pending < MaxFailures)
        {
            return null;
        }

        if (failures < MaxFailures)
        {
            // Full only with the attempts still being checked, which end within moments.
            return TimeSpan.FromSeconds(1);
        }

        // The attempt that frees a slot is the oldest one leaving the window.
        TimeSpan wait = queue!.Peek() + Window - now;
        return wait > TimeSpan.Zero ? wait : TimeSpan.FromSeconds(1);
    }

    private void Release(string key)
    {
        int left = _pending.GetValueOrDefault(key) - 1;
        if (left > 0)
        {
            _pending[key] = left;
        }
        else
        {
            _pending.Remove(key);
        }
    }

    private void Add(string key, DateTimeOffset now)
    {
        if (!_failures.TryGetValue(key, out Queue<DateTimeOffset>? queue))
        {
            queue = new Queue<DateTimeOffset>();
            _failures[key] = queue;
        }

        Expire(queue, now);
        queue.Enqueue(now);
        // Never keep more than needed to decide: the newest attempts matter.
        while (queue.Count > MaxFailures)
        {
            queue.Dequeue();
        }
    }

    private static void Expire(Queue<DateTimeOffset> queue, DateTimeOffset now)
    {
        while (queue.Count > 0 && queue.Peek() + Window <= now)
        {
            queue.Dequeue();
        }
    }

    private void Prune(DateTimeOffset now)
    {
        foreach (string key in _failures.Keys.ToList())
        {
            Queue<DateTimeOffset> queue = _failures[key];
            Expire(queue, now);
            if (queue.Count == 0)
            {
                _failures.Remove(key);
            }
        }
    }
}
