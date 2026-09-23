namespace NutHub.Services.Notifications;

/// <summary>An auto-reset wake-up for a loop that otherwise sleeps until a deadline (measured with a TimeProvider).</summary>
internal sealed class AsyncSignal
{
    private readonly object _lock = new();
    private TaskCompletionSource _tcs = new(TaskCreationOptions.RunContinuationsAsynchronously);

    public void Set()
    {
        lock (_lock)
        {
            _tcs.TrySetResult();
        }
    }

    /// <summary>Waits until <see cref="Set"/> is called or <paramref name="timeout"/> elapses, then resets.</summary>
    public async Task WaitAsync(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        Task signal;
        lock (_lock)
        {
            signal = _tcs.Task;
        }

        if (!signal.IsCompleted && timeout != TimeSpan.Zero)
        {
            using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            Task delay = timeout == Timeout.InfiniteTimeSpan
                ? Task.Delay(Timeout.Infinite, cts.Token)
                : Task.Delay(timeout, time, cts.Token);
            await Task.WhenAny(signal, delay).ConfigureAwait(false);
            await cts.CancelAsync().ConfigureAwait(false);
        }

        cancellationToken.ThrowIfCancellationRequested();
        lock (_lock)
        {
            if (_tcs.Task.IsCompleted)
            {
                _tcs = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }
    }
}
