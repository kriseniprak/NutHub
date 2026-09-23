namespace NutHub.Drivers.Serial.Tests.Fakes;

/// <summary>
/// A clock for driver loops: every timer fires after a token real-time pause (so a poll loop cannot spin the CPU) and
/// moves the virtual clock forward by its full due time. A 30 s retry delay or a 1.3 s APC command repeat therefore
/// costs a few milliseconds, while the code under test still measures the intervals it expects.
/// </summary>
internal sealed class VirtualTimeProvider : TimeProvider
{
    private static readonly DateTimeOffset Epoch = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    private long _ticks;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override long GetTimestamp() => Interlocked.Read(ref _ticks);

    public override DateTimeOffset GetUtcNow() => Epoch.AddTicks(GetTimestamp());

    public void Advance(TimeSpan by) => Interlocked.Add(ref _ticks, by.Ticks);

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period) =>
        new VirtualTimer(this, callback, state, dueTime);

    private sealed class VirtualTimer : ITimer
    {
        private readonly VirtualTimeProvider _clock;
        private readonly TimerCallback _callback;
        private readonly object? _state;
        private int _generation;
        private volatile bool _disposed;

        public VirtualTimer(VirtualTimeProvider clock, TimerCallback callback, object? state, TimeSpan dueTime)
        {
            _clock = clock;
            _callback = callback;
            _state = state;
            Schedule(dueTime);
        }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            Schedule(dueTime);
            return !_disposed;
        }

        public void Dispose() => _disposed = true;

        public ValueTask DisposeAsync()
        {
            _disposed = true;
            return ValueTask.CompletedTask;
        }

        private void Schedule(TimeSpan dueTime)
        {
            int generation = Interlocked.Increment(ref _generation);
            if (dueTime == Timeout.InfiniteTimeSpan || _disposed)
            {
                return;
            }

            _ = Task.Delay(1).ContinueWith(_ =>
            {
                if (_disposed || generation != Volatile.Read(ref _generation))
                {
                    return;
                }

                _clock.Advance(dueTime);
                _callback(_state);
            }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        }
    }
}
