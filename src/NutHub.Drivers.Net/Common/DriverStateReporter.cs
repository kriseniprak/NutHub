using NutHub.Core.Drivers;

namespace NutHub.Drivers.Net.Common;

/// <summary>
/// Forwards connection state changes to the <see cref="IDriverContext"/> only when they change. The context logs a
/// warning and rebuilds the snapshot on every report, so a server that stays unreachable for an hour must not
/// produce one warning per retry.
/// </summary>
internal sealed class DriverStateReporter(IDriverContext context)
{
    private readonly object _lock = new();
    private string? _lastReason;
    private bool _connected;

    /// <summary>Whether the last report was a successful publish.</summary>
    public bool IsConnected
    {
        get
        {
            lock (_lock)
            {
                return _connected;
            }
        }
    }

    public void Connecting(string detail)
    {
        lock (_lock)
        {
            _connected = false;
            _lastReason = null;
        }

        context.ReportConnecting(detail);
    }

    /// <summary>Reports a problem; repeated reports of the same reason are dropped.</summary>
    public void Disconnected(string reason)
    {
        lock (_lock)
        {
            if (!_connected && string.Equals(_lastReason, reason, StringComparison.Ordinal))
            {
                return;
            }

            _connected = false;
            _lastReason = reason;
        }

        context.ReportDisconnected(reason);
    }

    public void Publish(DriverUpdate update)
    {
        lock (_lock)
        {
            _connected = true;
            _lastReason = null;
        }

        context.Publish(update);
    }
}
