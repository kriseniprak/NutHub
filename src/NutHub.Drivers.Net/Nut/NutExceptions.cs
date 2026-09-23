namespace NutHub.Drivers.Net.Nut;

/// <summary>
/// The server answered a request with "ERR &lt;code&gt; [extra]". The exchange completed normally, so the connection
/// stays usable.
/// </summary>
internal sealed class NutErrorException(string code, string? extra = null)
    : Exception(extra is null ? $"ERR {code}" : $"ERR {code} {extra}")
{
    /// <summary>The error code, upper case: "DATA-STALE", "ACCESS-DENIED"...</summary>
    public string Code { get; } = code;

    public string? Extra { get; } = extra;
}

/// <summary>
/// The server sent something that does not fit the protocol (an unexpected line, a list without its end, an
/// oversized line). The connection is out of step and must be closed.
/// </summary>
internal sealed class NutProtocolException(string message) : IOException(message);
