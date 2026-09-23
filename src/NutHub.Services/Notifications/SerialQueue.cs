namespace NutHub.Services.Notifications;

/// <summary>
/// Runs work items one after the other per key (one webhook, one command hook), in arrival order, without blocking
/// the caller: a slow webhook must not delay the others, and "on line" must never overtake "on battery".
/// </summary>
internal sealed class SerialQueue
{
    private readonly Dictionary<string, Lane> _lanes = new(StringComparer.Ordinal);
    private readonly object _lock = new();
    private readonly int _maxPending;

    public SerialQueue(int maxPendingPerKey = 100)
    {
        _maxPending = maxPendingPerKey;
    }

    /// <summary>Queues <paramref name="work"/> after the work already queued for <paramref name="key"/>.</summary>
    /// <param name="urgent">Queued even when the lane is full (the machine may be about to lose power).</param>
    /// <returns>False when the lane is full (the target has been failing for a long time).</returns>
    public bool TryEnqueue(string key, Func<Task> work, bool urgent = false)
    {
        lock (_lock)
        {
            if (!_lanes.TryGetValue(key, out Lane? lane))
            {
                lane = new Lane();
                _lanes[key] = lane;
            }

            if (lane.Pending >= _maxPending && !urgent)
            {
                return false;
            }

            lane.Pending++;
            lane.Tail = lane.Tail.ContinueWith(_ => RunAsync(key, lane, work), CancellationToken.None,
                                               TaskContinuationOptions.None, TaskScheduler.Default).Unwrap();
            return true;
        }
    }

    /// <summary>Completes when everything queued so far has run.</summary>
    public Task WhenIdleAsync()
    {
        lock (_lock)
        {
            return Task.WhenAll(_lanes.Values.Select(l => l.Tail));
        }
    }

    private async Task RunAsync(string key, Lane lane, Func<Task> work)
    {
        try
        {
            await work().ConfigureAwait(false);
        }
        catch
        {
            // Work items log their own failures; a failure must not stop the lane.
        }
        finally
        {
            lock (_lock)
            {
                lane.Pending--;
                if (lane.Pending == 0 && _lanes.TryGetValue(key, out Lane? current) && current == lane)
                {
                    _lanes.Remove(key);
                }
            }
        }
    }

    private sealed class Lane
    {
        public Task Tail { get; set; } = Task.CompletedTask;

        public int Pending { get; set; }
    }
}
