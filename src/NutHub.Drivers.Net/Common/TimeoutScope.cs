namespace NutHub.Drivers.Net.Common;

/// <summary>
/// A cancellation token that fires when the caller cancels or when a timeout measured on the driver's
/// <see cref="TimeProvider"/> expires, and tells the two apart afterwards so a timeout can be reported as such
/// instead of as a cancellation.
/// </summary>
internal sealed class TimeoutScope : IDisposable
{
    private readonly CancellationTokenSource _timeout;
    private readonly CancellationTokenSource _linked;
    private readonly CancellationToken _outer;

    public TimeoutScope(TimeSpan timeout, TimeProvider time, CancellationToken cancellationToken)
    {
        _outer = cancellationToken;
        _timeout = new CancellationTokenSource(timeout, time);
        _linked = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken, _timeout.Token);
    }

    public CancellationToken Token => _linked.Token;

    /// <summary>True when the token fired because of the timeout and not because the caller cancelled.</summary>
    public bool TimedOut => _timeout.IsCancellationRequested && !_outer.IsCancellationRequested;

    public void Dispose()
    {
        _linked.Dispose();
        _timeout.Dispose();
    }
}
