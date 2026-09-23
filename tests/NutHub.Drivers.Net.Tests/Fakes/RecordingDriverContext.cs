using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Net.Tests.Fakes;

/// <summary>What a driver reported, in order.</summary>
public sealed record DriverReport(string Kind, string? Text, DriverUpdate? Update)
{
    public bool IsPublish => Kind == "publish";

    public bool IsDisconnect => Kind == "disconnected";

    public string Var(string name) => Update!.Variables[name];
}

/// <summary>An <see cref="IDriverContext"/> that records every report and lets a test wait for one.</summary>
public sealed class RecordingDriverContext : IDriverContext
{
    private readonly object _lock = new();
    private readonly List<DriverReport> _reports = [];
    private readonly List<(Func<DriverReport, bool> Predicate, int From, TaskCompletionSource<DriverReport> Done)> _waiters = [];

    public string UpsName { get; init; } = "test";

    public ILogger Logger { get; init; } = NullLogger.Instance;

    public TimeProvider TimeProvider { get; init; } = TimeProvider.System;

    public TimeSpan PollInterval { get; init; } = TimeSpan.FromMilliseconds(100);

    public IReadOnlyList<DriverReport> Reports
    {
        get
        {
            lock (_lock)
            {
                return _reports.ToArray();
            }
        }
    }

    /// <summary>The position of the next report, to wait only for reports made after this point.</summary>
    public int Mark()
    {
        lock (_lock)
        {
            return _reports.Count;
        }
    }

    public void ReportConnecting(string? detail = null) => Add(new DriverReport("connecting", detail, null));

    public void ReportDisconnected(string reason) => Add(new DriverReport("disconnected", reason, null));

    public void Publish(DriverUpdate update) => Add(new DriverReport("publish", null, update));

    /// <summary>Waits for a report at or after <paramref name="from"/> that matches.</summary>
    public Task<DriverReport> WaitAsync(Func<DriverReport, bool> predicate, int from = 0, double timeoutSeconds = 10)
    {
        var done = new TaskCompletionSource<DriverReport>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            for (int i = from; i < _reports.Count; i++)
            {
                if (predicate(_reports[i]))
                {
                    return Task.FromResult(_reports[i]);
                }
            }

            _waiters.Add((predicate, from, done));
        }

        return done.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds));
    }

    public Task<DriverReport> WaitForPublishAsync(Func<DriverReport, bool>? predicate = null, int from = 0) =>
        WaitAsync(r => r.IsPublish && (predicate?.Invoke(r) ?? true), from);

    public Task<DriverReport> WaitForDisconnectAsync(string? containing = null, int from = 0) =>
        WaitAsync(r => r.IsDisconnect && (containing is null || r.Text!.Contains(containing, StringComparison.Ordinal)), from);

    private void Add(DriverReport report)
    {
        var matched = new List<TaskCompletionSource<DriverReport>>();
        lock (_lock)
        {
            _reports.Add(report);
            int index = _reports.Count - 1;
            for (int i = _waiters.Count - 1; i >= 0; i--)
            {
                if (index >= _waiters[i].From && _waiters[i].Predicate(report))
                {
                    matched.Add(_waiters[i].Done);
                    _waiters.RemoveAt(i);
                }
            }
        }

        foreach (var done in matched)
        {
            done.TrySetResult(report);
        }
    }
}
