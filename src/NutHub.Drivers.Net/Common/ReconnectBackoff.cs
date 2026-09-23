namespace NutHub.Drivers.Net.Common;

/// <summary>
/// The waits between reconnection attempts to a network server. Short at first, because a restarted upsd or
/// apcupsd is usually back within seconds, then capped at a value low enough that a UPS coming back is noticed
/// quickly: a machine protected through this UPS depends on it.
/// </summary>
internal sealed class ReconnectBackoff
{
    /// <summary>The default schedule; the last value repeats.</summary>
    public static readonly IReadOnlyList<TimeSpan> DefaultDelays =
    [
        TimeSpan.FromSeconds(2),
        TimeSpan.FromSeconds(5),
        TimeSpan.FromSeconds(10),
        TimeSpan.FromSeconds(20),
        TimeSpan.FromSeconds(30),
    ];

    private readonly IReadOnlyList<TimeSpan> _delays;
    private int _failures;

    public ReconnectBackoff(IReadOnlyList<TimeSpan>? delays = null)
    {
        _delays = delays is { Count: > 0 } ? delays : DefaultDelays;
    }

    /// <summary>Consecutive failures since the last success.</summary>
    public int Failures => _failures;

    /// <summary>Records a failure and returns how long to wait before the next attempt.</summary>
    public TimeSpan NextDelay()
    {
        TimeSpan delay = _delays[Math.Min(_failures, _delays.Count - 1)];
        if (_failures < int.MaxValue)
        {
            _failures++;
        }

        return delay;
    }

    public void Reset() => _failures = 0;
}
