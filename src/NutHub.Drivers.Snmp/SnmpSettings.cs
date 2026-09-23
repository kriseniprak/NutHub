namespace NutHub.Drivers.Snmp;

/// <summary>The SNMP protocol version spoken to the agent.</summary>
internal enum SnmpProtocolVersion
{
    V1,
    V2c,
    V3,
}

/// <summary>SNMPv3 security level (RFC 3414).</summary>
internal enum SnmpSecurityLevel
{
    NoAuthNoPriv,
    AuthNoPriv,
    AuthPriv,
}

/// <summary>SNMPv3 authentication protocols SharpSnmpLib implements (no SHA-224 there).</summary>
internal enum SnmpAuthProtocol
{
    Md5,
    Sha1,
    Sha256,
    Sha384,
    Sha512,
}

/// <summary>SNMPv3 privacy protocols SharpSnmpLib implements.</summary>
internal enum SnmpPrivProtocol
{
    Des,
    Aes128,
    Aes192,
    Aes256,
}

/// <summary>
/// The validated configuration of one SNMP UPS, built from the driver options by
/// <see cref="SnmpDriverFactory"/>.
/// </summary>
internal sealed record SnmpSettings
{
    public required string Host { get; init; }

    public int Port { get; init; } = 161;

    public SnmpProtocolVersion Version { get; init; } = SnmpProtocolVersion.V2c;

    public string Community { get; init; } = "public";

    /// <summary>Community for SETs (commands, variable writes); many cards keep a separate "private" one.</summary>
    public string WriteCommunity { get; init; } = "public";

    public string SecurityName { get; init; } = "";

    public SnmpSecurityLevel SecurityLevel { get; init; } = SnmpSecurityLevel.NoAuthNoPriv;

    public SnmpAuthProtocol AuthProtocol { get; init; } = SnmpAuthProtocol.Md5;

    public string AuthPassword { get; init; } = "";

    public SnmpPrivProtocol PrivProtocol { get; init; } = SnmpPrivProtocol.Des;

    public string PrivPassword { get; init; } = "";

    /// <summary>"auto" or the name of one MIB of <see cref="Mibs.MibCatalog"/>.</summary>
    public string Mib { get; init; } = Mibs.MibCatalog.Auto;

    /// <summary>How long to wait for each answer before sending the request again.</summary>
    public TimeSpan Timeout { get; init; } = TimeSpan.FromSeconds(1);

    /// <summary>How many times a request is sent again after a timeout.</summary>
    public int Retries { get; init; } = 3;

    /// <summary>Largest number of objects asked in one GET; lowered automatically when the agent says tooBig.</summary>
    public int MaxObjectsPerRequest { get; init; } = 16;

    /// <summary>
    /// Waits between reconnection attempts; the last one repeats. Short at first so a rebooting card is picked up
    /// quickly, then spaced out so an unplugged card does not flood the network.
    /// </summary>
    public IReadOnlyList<TimeSpan> ReconnectDelays { get; init; } =
    [
        TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(5), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30),
    ];

    /// <summary>How often (in polls) the semi-static objects (dates, settings) are read again.</summary>
    public int SemiStaticEvery { get; init; } = 10;

    /// <summary>"192.168.1.20:161", for messages.</summary>
    public string Target => Host.Contains(':', StringComparison.Ordinal) ? $"[{Host}]:{Port}" : $"{Host}:{Port}";

    public string VersionText => Version switch
    {
        SnmpProtocolVersion.V1 => "v1",
        SnmpProtocolVersion.V2c => "v2c",
        _ => "v3",
    };
}
