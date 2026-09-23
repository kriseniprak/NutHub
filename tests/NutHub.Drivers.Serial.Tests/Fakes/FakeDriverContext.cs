using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Serial.Tests.Fakes;

/// <summary>Records what a driver reports and lets a test wait for a condition on it.</summary>
internal sealed class FakeDriverContext : IDriverContext
{
    private readonly object _sync = new();
    private readonly List<string> _events = [];
    private readonly List<(Func<bool> Condition, TaskCompletionSource Done)> _waiters = [];
    private DriverUpdate? _last;
    private int _publishCount;

    public FakeDriverContext(TimeProvider time, TimeSpan? pollInterval = null)
    {
        TimeProvider = time;
        PollInterval = pollInterval ?? TimeSpan.FromSeconds(2);
    }

    public string UpsName => "test";

    public ILogger Logger => NullLogger.Instance;

    public TimeProvider TimeProvider { get; }

    public TimeSpan PollInterval { get; }

    public DriverUpdate? Last
    {
        get
        {
            lock (_sync)
            {
                return _last;
            }
        }
    }

    public int PublishCount => Volatile.Read(ref _publishCount);

    /// <summary>"connecting: ...", "disconnected: ...", "publish", in order (consecutive publishes collapsed).</summary>
    public IReadOnlyList<string> Events
    {
        get
        {
            lock (_sync)
            {
                return [.. _events];
            }
        }
    }

    public string? Status => Last?.Variables.GetValueOrDefault("ups.status");

    public void ReportConnecting(string? detail = null) => Record("connecting: " + detail);

    public void ReportDisconnected(string reason) => Record("disconnected: " + reason);

    public void Publish(DriverUpdate update)
    {
        lock (_sync)
        {
            _last = update;
            _publishCount++;
            if (_events.Count == 0 || _events[^1] != "publish")
            {
                _events.Add("publish");
            }

            Check();
        }
    }

    /// <summary>Waits until <paramref name="condition"/> holds, checked after every report; fails after 10 s.</summary>
    public async Task WaitForAsync(Func<FakeDriverContext, bool> condition, string what)
    {
        var done = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_sync)
        {
            if (condition(this))
            {
                return;
            }

            _waiters.Add((() => condition(this), done));
        }

        Task finished = await Task.WhenAny(done.Task, Task.Delay(TimeSpan.FromSeconds(10))).ConfigureAwait(false);
        Assert.True(finished == done.Task, $"Timed out waiting for {what}. Events: {string.Join(" | ", Events)}");
    }

    private void Record(string text)
    {
        lock (_sync)
        {
            _events.Add(text);
            Check();
        }
    }

    private void Check()
    {
        for (int i = _waiters.Count - 1; i >= 0; i--)
        {
            if (_waiters[i].Condition())
            {
                _waiters[i].Done.TrySetResult();
                _waiters.RemoveAt(i);
            }
        }
    }
}
