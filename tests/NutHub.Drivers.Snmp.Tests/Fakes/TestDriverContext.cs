using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Snmp.Tests.Fakes;

/// <summary>Records what a driver reports, and lets tests wait for a condition on it.</summary>
internal sealed class TestDriverContext(TimeSpan pollInterval) : IDriverContext
{
    private readonly object _lock = new();
    private readonly List<DriverUpdate> _updates = [];
    private readonly List<string> _disconnections = [];
    private readonly SemaphoreSlim _changed = new(0);

    public string UpsName => "test";

    public ILogger Logger => NullLogger.Instance;

    public TimeProvider TimeProvider => TimeProvider.System;

    public TimeSpan PollInterval { get; } = pollInterval;

    public DriverUpdate? LastUpdate
    {
        get
        {
            lock (_lock)
            {
                return _updates.Count == 0 ? null : _updates[^1];
            }
        }
    }

    public int UpdateCount
    {
        get
        {
            lock (_lock)
            {
                return _updates.Count;
            }
        }
    }

    public IReadOnlyList<string> Disconnections
    {
        get
        {
            lock (_lock)
            {
                return _disconnections.ToArray();
            }
        }
    }

    public void ReportConnecting(string? detail = null)
    {
    }

    public void ReportDisconnected(string reason)
    {
        lock (_lock)
        {
            _disconnections.Add(reason);
        }

        _changed.Release();
    }

    public void Publish(DriverUpdate update)
    {
        lock (_lock)
        {
            _updates.Add(update);
        }

        _changed.Release();
    }

    /// <summary>Waits until <paramref name="condition"/> holds, failing the test after <paramref name="timeout"/>.</summary>
    public async Task WaitForAsync(Func<TestDriverContext, bool> condition, string what, TimeSpan? timeout = null)
    {
        DateTime deadline = DateTime.UtcNow + (timeout ?? TimeSpan.FromSeconds(10));
        while (!condition(this))
        {
            TimeSpan left = deadline - DateTime.UtcNow;
            if (left <= TimeSpan.Zero)
            {
                throw new TimeoutException($"Timed out waiting for {what}. Disconnections: {string.Join(" | ", Disconnections)}");
            }

            await _changed.WaitAsync(left < TimeSpan.FromMilliseconds(200) ? left : TimeSpan.FromMilliseconds(200));
        }
    }

    /// <summary>Waits for an update published after this call that satisfies <paramref name="condition"/>.</summary>
    public async Task<DriverUpdate> NextUpdateAsync(Func<DriverUpdate, bool> condition, string what, TimeSpan? timeout = null)
    {
        int after = UpdateCount;
        DriverUpdate? found = null;
        await WaitForAsync(c =>
        {
            lock (c._lock)
            {
                for (int i = after; i < c._updates.Count; i++)
                {
                    if (condition(c._updates[i]))
                    {
                        found = c._updates[i];
                        return true;
                    }
                }

                return false;
            }
        }, what, timeout);
        return found!;
    }
}
