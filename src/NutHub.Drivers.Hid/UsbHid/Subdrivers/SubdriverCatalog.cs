using NutHub.Drivers.Hid.Transport;

namespace NutHub.Drivers.Hid.UsbHid.Subdrivers;

/// <summary>The available subdrivers and the choice of one for a device (NUT subdriver_list and callback()).</summary>
internal static class SubdriverCatalog
{
    /// <summary>In NUT's claim order: the first subdriver that claims a device gets it.</summary>
    private static readonly (string Id, string Label, Func<UsbHidSubdriver> Create)[] Entries =
    [
        ("mge", "Eaton / MGE / Powerware / Dell / HP", () => new MgeSubdriver()),
        ("apc", "APC", () => new ApcSubdriver()),
        ("arduino", "Arduino", () => new ArduinoSubdriver()),
        ("belkin", "Belkin / Liebert PSI", () => new BelkinSubdriver()),
        ("cps", "CyberPower / Cyber Energy", () => new CyberPowerSubdriver()),
        ("delta", "Delta", () => new DeltaSubdriver()),
        ("ecoflow", "EcoFlow", () => new EcoFlowSubdriver()),
        ("ever", "Ever", () => new EverSubdriver()),
        ("idowell", "iDowell / GoldenMate", () => new IDowellSubdriver()),
        ("legrand", "Legrand", () => new LegrandSubdriver()),
        ("liebert", "Liebert (Phoenixtec)", () => new LiebertSubdriver()),
        ("openups", "openUPS", () => new OpenUpsSubdriver()),
        ("powercom", "PowerCOM", () => new PowercomSubdriver()),
        ("powervar", "Powervar", () => new PowervarSubdriver()),
        ("salicru", "Salicru", () => new SalicruSubdriver()),
        ("tripplite", "Tripp Lite / HP T-series", () => new TrippLiteSubdriver()),
        ("generic", "Generic (standard HID Power Device usages only)", () => new GenericSubdriver()),
    ];

    /// <summary>Ids and labels for the "subdriver" option, "auto" excluded.</summary>
    public static IEnumerable<(string Id, string Label)> Choices => Entries.Select(e => (e.Id, e.Label));

    public static IReadOnlyList<string> Ids { get; } = Entries.Select(e => e.Id).ToArray();

    public static UsbHidSubdriver? Create(string id)
    {
        foreach (var entry in Entries)
        {
            if (string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return entry.Create();
            }
        }

        return null;
    }

    /// <summary>
    /// Picks the subdriver for a device: the configured one, else the first that claims it, else the generic one
    /// when the device declares itself a Power Device. Null means the device is not a UPS this driver can read.
    /// </summary>
    public static UsbHidSubdriver? Select(HidDeviceInfo device, bool isPowerDevice, string configured, bool productIdGiven)
    {
        ArgumentNullException.ThrowIfNull(device);
        if (!string.Equals(configured, "auto", StringComparison.OrdinalIgnoreCase))
        {
            return Create(configured);
        }

        foreach (var entry in Entries)
        {
            UsbHidSubdriver candidate = entry.Create();
            if (candidate.Claim(device, productIdGiven))
            {
                return candidate;
            }
        }

        return isPowerDevice ? new GenericSubdriver() : null;
    }

    /// <summary>Whether some subdriver knows this exact vendor and product (used when the descriptor is unreadable).</summary>
    public static bool IsKnownDevice(HidDeviceInfo device)
    {
        ArgumentNullException.ThrowIfNull(device);
        return KnownIds.Value.Contains((device.VendorId, device.ProductId));
    }

    private static readonly Lazy<HashSet<(int VendorId, int ProductId)>> KnownIds = new(() =>
        Entries.SelectMany(e => e.Create().SupportedDevices).Select(d => (d.VendorId, d.ProductId)).ToHashSet());
}
