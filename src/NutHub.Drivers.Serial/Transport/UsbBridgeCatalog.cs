namespace NutHub.Drivers.Serial.Transport;

/// <summary>
/// How a USB-to-serial HID bridge frames the serial bytes; one per NUT nutdrv_qx USB "subdriver" that can be driven
/// through the operating system's HID driver (SET_REPORT output reports and interrupt input reports).
/// </summary>
internal enum UsbBridgeKind
{
    /// <summary>NUT "cypress": 8-byte output reports, replies read 8 bytes at a time until a carriage return.</summary>
    Cypress,

    /// <summary>NUT "phoenix": like cypress, but the bridge's stale output must be drained before each command.</summary>
    Phoenix,

    /// <summary>NUT "ippon": 8-byte output reports, the whole reply comes in one 64-byte input report.</summary>
    Ippon,

    /// <summary>NUT "sgs": each 8-byte report carries a length byte followed by up to 7 data bytes.</summary>
    Sgs,
}

/// <summary>A known USB id of a Megatec/Q1 USB bridge.</summary>
/// <param name="Kind">The framing, or null when NutHub cannot drive this bridge (see <paramref name="UnsupportedReason"/>).</param>
/// <param name="DrainQuirk">Drain stale input reports before each command (NUT's cypress_drain_quirk).</param>
internal sealed record UsbBridgeInfo(
    int VendorId,
    int ProductId,
    UsbBridgeKind? Kind,
    string Devices,
    bool DrainQuirk = false,
    string? UnsupportedReason = null)
{
    public bool IsSupported => Kind is not null;

    public string Id => $"{VendorId:x4}:{ProductId:x4}";
}

/// <summary>
/// The USB ids of Megatec/Q1 UPS bridges. Ported from the qx_usb_id table of NUT drivers/nutdrv_qx.c (and
/// drivers/blazer_usb.c); the bridges that need USB string-descriptor tricks or vendor control transfers are listed so
/// the user gets a clear explanation instead of silence.
/// </summary>
internal static class UsbBridgeCatalog
{
    private const string NeedsRawUsb =
        "this bridge needs raw USB control transfers or string-descriptor tricks that are not available through the " +
        "operating system's HID driver; use NUT's nutdrv_qx driver for it, or a serial cable";

    public static IReadOnlyList<UsbBridgeInfo> All { get; } =
    [
        new(0x05b8, 0x0000, UsbBridgeKind.Cypress, "Agiler UPS"),
        new(0x0665, 0x5161, UsbBridgeKind.Cypress, "Belkin F6C1200-UNV, Voltronic Power and many OEM UPSes", DrainQuirk: true),
        new(0x06da, 0x0002, UsbBridgeKind.Cypress, "Online Yunto YQ450 (not Masterguard A, which uses the phoenixtec protocol)"),
        new(0x06da, 0x0003, UsbBridgeKind.Ippon, "Mustek PowerMust, Ippon"),
        new(0x06da, 0x0004, UsbBridgeKind.Cypress, "Phoenixtec Innova 3/1 T"),
        new(0x06da, 0x0005, UsbBridgeKind.Cypress, "Phoenixtec Innova RT"),
        new(0x06da, 0x0201, UsbBridgeKind.Cypress, "Phoenixtec Innova T"),
        new(0x06da, 0x0601, UsbBridgeKind.Phoenix, "Online Zinto A"),
        new(0x0f03, 0x0001, UsbBridgeKind.Cypress, "Unitek Alpha 1200Sx"),
        new(0x14f0, 0x00c9, UsbBridgeKind.Phoenix, "GE EP series"),
        new(0x0483, 0x0035, UsbBridgeKind.Sgs, "TS Shara"),
        new(0x0001, 0x0000, null, "Krauler, Fideltronik/MEC (fabula), Hunnox, Fuji, SNR", UnsupportedReason: NeedsRawUsb),
        new(0xffff, 0x0000, null, "Ablerex 625L", UnsupportedReason: NeedsRawUsb),
        new(0x1cb0, 0x0035, null, "Legrand Daker DK (krauler)", UnsupportedReason: NeedsRawUsb),
        new(0x0925, 0x1234, null, "Armac and other Richcomm-based UPSes", UnsupportedReason: NeedsRawUsb),
        new(0x0590, 0x00b7, null, "OMRON BN150T", UnsupportedReason: "the OMRON protocol is not implemented"),
        new(0x1a86, 0x7523, null, "CH340 USB-serial converter (Ippon Innova TAE...)",
            UnsupportedReason: "it is a plain USB-serial converter: use the 'serial' transport with the COM/ttyUSB port it creates"),
    ];

    /// <summary>The entry for an exact vendor/product pair, or null.</summary>
    public static UsbBridgeInfo? Find(int vendorId, int productId) =>
        All.FirstOrDefault(b => b.VendorId == vendorId && b.ProductId == productId);

    /// <summary>Whether a vendor id appears in the table at all (a "possibly supported" device, in NUT's words).</summary>
    public static bool IsKnownVendor(int vendorId) => All.Any(b => b.VendorId == vendorId);

    public static UsbBridgeKind? ParseKind(string? text) => text?.ToLowerInvariant() switch
    {
        null or "" or "auto" => null,
        "cypress" => UsbBridgeKind.Cypress,
        "phoenix" => UsbBridgeKind.Phoenix,
        "ippon" => UsbBridgeKind.Ippon,
        "sgs" => UsbBridgeKind.Sgs,
        _ => throw new ArgumentException($"Unknown USB bridge type '{text}'.", nameof(text)),
    };
}
