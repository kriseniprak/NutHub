using System.Collections.Concurrent;

namespace NutHub.Protocol.Server;

/// <summary>
/// Lets a message through once per key and interval. NUT clients retry every few seconds for as long as they are
/// misconfigured (wrong password, no TLS, not allowed): without a limit, one of them would fill the log.
/// </summary>
internal sealed class LogRateLimiter(TimeProvider time, TimeSpan interval)
{
    private const int MaxKeys = 1000;

    private readonly ConcurrentDictionary<string, DateTimeOffset> _last = new(StringComparer.Ordinal);

    /// <summary>True when a message for <paramref name="key"/> should be written now.</summary>
    public bool ShouldLog(string key)
    {
        DateTimeOffset now = time.GetUtcNow();
        if (_last.Count >= MaxKeys)
        {
            _last.Clear(); // crude but bounded: at worst a few messages are repeated early
        }

        if (_last.TryGetValue(key, out DateTimeOffset last) && now - last < interval)
        {
            return false;
        }

        _last[key] = now;
        return true;
    }
}
