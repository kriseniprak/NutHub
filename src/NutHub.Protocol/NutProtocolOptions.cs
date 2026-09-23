namespace NutHub.Protocol;

/// <summary>
/// Limits and timings of the NUT protocol server. They are not in the configuration file on purpose: they follow
/// upsd or protect the server, and only tests change them (by registering their own instance).
/// </summary>
internal sealed class NutProtocolOptions
{
    /// <summary>The longest request accepted, in bytes without the line terminator; longer ones close the connection.</summary>
    public int MaxLineLength { get; init; } = 1024;

    /// <summary>A connection that sends no complete command for this long is closed.</summary>
    public TimeSpan IdleTimeout { get; init; } = TimeSpan.FromMinutes(15);

    /// <summary>A client that does not read its answers for this long is disconnected, so it cannot pin the server.</summary>
    public TimeSpan WriteTimeout { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>The TLS handshake after "OK STARTTLS" must complete within this time.</summary>
    public TimeSpan TlsHandshakeTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>
    /// After a last answer (LOGOUT, request too long), how long unread input is drained before closing, so the
    /// client receives the answer instead of a connection reset.
    /// </summary>
    public TimeSpan CloseLinger { get; init; } = TimeSpan.FromSeconds(2);

    /// <summary>How often a listener that could not bind (port in use...) is tried again.</summary>
    public TimeSpan BindRetryInterval { get; init; } = TimeSpan.FromSeconds(30);

    /// <summary>Period of the housekeeping: idle connections, bind retries, expired tracking entries.</summary>
    public TimeSpan HousekeepingInterval { get; init; } = TimeSpan.FromSeconds(5);

    /// <summary>Failed password checks from one address that trigger the lockout...</summary>
    public int FailedPasswordLimit { get; init; } = 10;

    /// <summary>...within this window...</summary>
    public TimeSpan FailedPasswordWindow { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>...during which privileged commands from that address are refused without checking.</summary>
    public TimeSpan LockoutDuration { get; init; } = TimeSpan.FromMinutes(5);

    /// <summary>How long the result of a tracked command stays available (upsd tracking_delay).</summary>
    public TimeSpan TrackingRetention { get; init; } = TimeSpan.FromHours(1);

    /// <summary>At most this many tracked results are kept; the oldest go first.</summary>
    public int TrackingCapacity { get; init; } = 1000;

    /// <summary>On shutdown, how long to wait for the connections to finish closing.</summary>
    public TimeSpan ShutdownTimeout { get; init; } = TimeSpan.FromSeconds(5);
}
