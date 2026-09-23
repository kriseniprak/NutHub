using System.Diagnostics;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Hid.Tests.Support;

/// <summary>Records what a driver reports, and lets a test wait for a condition on it.</summary>
internal sealed class RecordingDriverContext(TimeSpan pollInterval) : IDriverContext
{
    private readonly object _lock = new();
    private readonly List<string> _events = [];
    private readonly List<DriverUpdate> _updates = [];

    public string UpsName => "test";

    public ILogger Logger => NullLogger.Instance;

    public TimeProvider TimeProvider => TimeProvider.System;

    public TimeSpan PollInterval { get; } = pollInterval;

    /// <summary>"connecting: ...", "disconnected: ...", "publish" in order.</summary>
    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_lock)
            {
                return [.. _events];
            }
        }
    }

    public IReadOnlyList<DriverUpdate> Updates
    {
        get
        {
            lock (_lock)
            {
                return [.. _updates];
            }
        }
    }

    public DriverUpdate? Last
    {
        get
        {
            lock (_lock)
            {
                return _updates.Count == 0 ? null : _updates[^1];
            }
        }
    }

    public void ReportConnecting(string? detail = null) => Add("connecting: " + detail);

    public void ReportDisconnected(string reason) => Add("disconnected: " + reason);

    public void Publish(DriverUpdate update)
    {
        lock (_lock)
        {
            _events.Add("publish");
            _updates.Add(update);
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds; fails the test after <paramref name="seconds"/>.</summary>
    public async Task WaitUntilAsync(Func<RecordingDriverContext, bool> condition, string what, double seconds = 10)
    {
        var watch = Stopwatch.StartNew();
        while (!condition(this))
        {
            if (watch.Elapsed.TotalSeconds > seconds)
            {
                throw new TimeoutException($"Timed out waiting for {what}. Events: {string.Join(" | ", Events)}");
            }

            await Task.Delay(10);
        }
    }

    private void Add(string text)
    {
        lock (_lock)
        {
            _events.Add(text);
        }
    }
}
