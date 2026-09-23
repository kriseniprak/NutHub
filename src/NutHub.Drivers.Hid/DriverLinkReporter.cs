using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;

namespace NutHub.Drivers.Hid;

/// <summary>
/// Reports connection problems once per distinct message: the core logs every report and raises events, and a UPS
/// that stays unplugged for hours must flood neither. Before the first connection a problem is a "connecting"
/// detail (the UPS was never there); afterwards it is a disconnection, which makes the data stale at once.
/// </summary>
internal sealed class DriverLinkReporter(IDriverContext context, ILogger logger, string upsName)
{
    private bool _everConnected;
    private string? _lastProblem;

    /// <summary>Call after each successful publish that follows a connection.</summary>
    public void Connected()
    {
        _everConnected = true;
        _lastProblem = null;
    }

    public void Problem(string message)
    {
        ArgumentNullException.ThrowIfNull(message);
        if (message == _lastProblem)
        {
            return;
        }

        _lastProblem = message;
        if (_everConnected)
        {
            context.ReportDisconnected(message);
        }
        else
        {
            logger.LogWarning("{Ups}: {Message}", upsName, message);
            context.ReportConnecting(message);
        }
    }
}
