using System.Net;
using NutHub.Core.Runtime;

namespace NutHub.Protocol.Commands;

/// <summary>
/// The protocol state of one connection (upsd's <c>nut_ctype_t</c>). Only the connection's own command loop reads
/// and writes it, so it needs no locking.
/// </summary>
internal sealed class NutClientState(NutSession session, IPAddress address)
{
    /// <summary>The entry in <see cref="NutSessionRegistry"/> that the web panel and the host protection see.</summary>
    public NutSession Session { get; } = session;

    /// <summary>The client address; IPv4 clients of a dual-mode socket are plain IPv4 here.</summary>
    public IPAddress Address { get; } = address;

    /// <summary>The address as written in logs, events and LIST CLIENT.</summary>
    public string AddressText => Session.Address;

    /// <summary>What USERNAME set; not verified until a privileged command needs it (as in upsd).</summary>
    public string? Username { get; set; }

    /// <summary>What PASSWORD set; kept only in memory, never logged.</summary>
    public string? Password { get; set; }

    /// <summary>Set once the credentials were verified, so the session registry shows the account.</summary>
    public bool CredentialsVerified { get; set; }

    /// <summary>The UPS this connection did LOGIN to.</summary>
    public string? LoginUps { get; set; }

    public bool Tls { get; set; }

    /// <summary>SET TRACKING ON: INSTCMD and SET VAR answer with a tracking id and run in the background.</summary>
    public bool Tracking { get; set; }
}
