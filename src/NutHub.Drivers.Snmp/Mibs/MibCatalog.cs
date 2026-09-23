namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// The supported MIBs, in NUT's detection order (drivers/snmp-ups.c, mib2nut[]): vendor MIBs first, so a card that
/// implements both its vendor MIB and RFC 1628 is read through the richer vendor MIB; "ietf" last as the fallback.
/// PDU and ATS mappings of NUT are not included: they are not UPSes.
/// </summary>
internal static class MibCatalog
{
    public const string Auto = "auto";

    public static IReadOnlyList<MibDefinition> All { get; } =
    [
        ApcMib.Definition,
        CyberPowerMib.Definition,
        DeltaUpsMib.Definition,
        EatonPowerwareMib.Definition,
        HuaweiMib.Definition,
        MgeMib.Definition,
        NetvisionMib.Definition,
        XppcMib.Definition,
        IetfMib.TrippLite,
        IetfMib.Definition,
    ];

    /// <summary>Finds a MIB by name or alias, ignoring case; null when unknown.</summary>
    public static MibDefinition? Find(string name) => All.FirstOrDefault(m => m.Matches(name));
}
