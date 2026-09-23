using NutHub.Drivers.Serial.Megatec.Protocols;

namespace NutHub.Drivers.Serial.Megatec;

/// <summary>The Q* dialects NutHub implements, and the order in which "auto" tries them.</summary>
internal static class QxProtocols
{
    public const string Auto = "auto";

    /// <summary>
    /// Detection order, a subset of the subdriver_list of NUT drivers/nutdrv_qx.c: the dialects with a distinctive
    /// identification query first, the plain Q1 fallback last.
    /// </summary>
    public static IReadOnlyList<string> AutoOrder { get; } =
    [
        "voltronic", "voltronic-qs", "voltronic-qs-hex", "mustek", "megatec/old", "bestups", "megatec", "zinto", "q1",
    ];

    /// <summary>Every protocol name accepted by the "protocol" option.</summary>
    public static IReadOnlyList<string> Names { get; } = [.. AutoOrder];

    public static QxProtocol Create(string name, QxProtocolOptions options) => name switch
    {
        "megatec" => MegatecFamilyProtocol.Megatec(options),
        "megatec/old" => MegatecFamilyProtocol.MegatecOld(options),
        "mustek" => MegatecFamilyProtocol.Mustek(options),
        "zinto" => MegatecFamilyProtocol.Zinto(options),
        "q1" => MegatecFamilyProtocol.Q1(options),
        "bestups" => new BestUpsProtocol(options),
        "voltronic-qs" => new VoltronicQsProtocol(options),
        "voltronic-qs-hex" => new VoltronicQsHexProtocol(options),
        "voltronic" => new VoltronicProtocol(options),
        _ => throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown Q* protocol."),
    };
}
