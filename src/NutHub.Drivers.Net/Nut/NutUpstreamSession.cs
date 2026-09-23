using NutHub.Core.Model;

namespace NutHub.Drivers.Net.Nut;

/// <summary>
/// One established connection to the upstream server and what was learnt on it. Replaced as a whole on
/// reconnection, because a restarted server may have a different version, UPS configuration or user rights.
/// </summary>
internal sealed class NutUpstreamSession(NutClientConnection connection, string? serverVersion)
{
    private IReadOnlyDictionary<string, VariableInfo> _variableInfo = new Dictionary<string, VariableInfo>();
    private IReadOnlyCollection<string> _commands = [];

    public NutClientConnection Connection { get; } = connection;

    /// <summary>The answer to VER, without the web address upsd appends; null when the server did not say.</summary>
    public string? ServerVersion { get; } = serverVersion;

    /// <summary>Whether USERNAME and PASSWORD were accepted (upsd checks them only when they are used).</summary>
    public bool Authenticated { get; init; }

    /// <summary>Why USERNAME / PASSWORD failed, for the answer to a command; null when they did not.</summary>
    public string? AuthenticationError { get; init; }

    /// <summary>Whether SET TRACKING ON was accepted, so INSTCMD and SET VAR answer with a tracking id.</summary>
    public bool Tracking { get; init; }

    /// <summary>When LIST RW and LIST CMD are due again.</summary>
    public DateTimeOffset NextMetadataRefresh { get; set; } = DateTimeOffset.MinValue;

    /// <summary>The metadata of the writable variables; replaced atomically on each refresh.</summary>
    public IReadOnlyDictionary<string, VariableInfo> VariableInfo
    {
        get => Volatile.Read(ref _variableInfo);
        set => Volatile.Write(ref _variableInfo, value);
    }

    /// <summary>The instant commands of the upstream UPS; replaced atomically on each refresh.</summary>
    public IReadOnlyCollection<string> Commands
    {
        get => Volatile.Read(ref _commands);
        set => Volatile.Write(ref _commands, value);
    }

    /// <summary>Whether a metadata refresh has completed at least once on this connection.</summary>
    public bool HasMetadata { get; set; }
}
