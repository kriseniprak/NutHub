namespace NutHub.Drivers.Serial.Megatec.Protocols;

/// <summary>
/// The original Megatec protocol and its close relatives, which differ only in the status query and in whether the
/// rating (F) and vendor (I, FW?) queries exist. Ported from NUT drivers/nutdrv_qx_megatec.c, nutdrv_qx_megatec-old.c,
/// nutdrv_qx_mustek.c, nutdrv_qx_zinto.c and nutdrv_qx_q1.c.
/// </summary>
internal sealed class MegatecFamilyProtocol : QxProtocol
{
    private readonly string _vendorCommand;
    private readonly bool _requiresVendor;
    private readonly bool _vendorOptions;

    private MegatecFamilyProtocol(QxProtocolOptions options, string name, string version, string statusCommand,
                                  bool rating, string? vendorCommand, bool requiresVendor)
        : base(options)
    {
        Name = name;
        Version = version;
        _vendorCommand = vendorCommand ?? string.Empty;
        _requiresVendor = requiresVendor;
        _vendorOptions = vendorCommand is not null;

        var fields = BlazerCommon.Q1StatusFields(statusCommand, bypassBit: BlazerCommon.StatusBits);
        if (rating)
        {
            fields.AddRange(BlazerCommon.RatingFields());
        }

        if (vendorCommand is not null)
        {
            fields.AddRange(BlazerCommon.VendorFields(vendorCommand));
        }

        Fields = fields;
        Commands = BlazerCommon.MegatecCommands();
    }

    public override string Name { get; }

    public override string Version { get; }

    public override IReadOnlyList<QxField> Fields { get; }

    public override IReadOnlyList<QxCommand> Commands { get; }

    public override IReadOnlyList<string> ClaimFields =>
        _requiresVendor && !Options.NoVendor ? ["input.voltage", "ups.firmware"] : ["input.voltage"];

    /// <summary>
    /// A blank firmware field still proves the UPS understood the vendor query; NUT master accepts it too (the
    /// "ups.firmware" item of the megatec table is flagged QX_FLAG_ABSENT for that reason, NUT issue #3436).
    /// </summary>
    public override bool ClaimAcceptsBlank(QxField field) => _vendorCommand.Length > 0 && field.Name == "ups.firmware";

    protected override bool HonoursVendorOptions => _vendorOptions;

    /// <summary>megatec: Q1 status, F ratings, I vendor; claimed on Q1 plus I.</summary>
    public static MegatecFamilyProtocol Megatec(QxProtocolOptions options) =>
        new(options, "megatec", "Megatec 0.10", "Q1\r", rating: true, vendorCommand: "I\r", requiresVendor: true);

    /// <summary>megatec/old: the same with the older D status query; claimed on D alone.</summary>
    public static MegatecFamilyProtocol MegatecOld(QxProtocolOptions options) =>
        new(options, "megatec/old", "Megatec/old 0.08", "D\r", rating: true, vendorCommand: "I\r", requiresVendor: false);

    /// <summary>mustek: QS status query; claimed on QS alone.</summary>
    public static MegatecFamilyProtocol Mustek(QxProtocolOptions options) =>
        new(options, "mustek", "Mustek 0.08", "QS\r", rating: true, vendorCommand: "I\r", requiresVendor: false);

    /// <summary>zinto: Q1 status, vendor information through FW?; claimed on Q1 plus FW?.</summary>
    public static MegatecFamilyProtocol Zinto(QxProtocolOptions options) =>
        new(options, "zinto", "Zinto 0.07", "Q1\r", rating: true, vendorCommand: "FW?\r", requiresVendor: true);

    /// <summary>q1: the bare Q1 status, the fallback for anything that answers Q1.</summary>
    public static MegatecFamilyProtocol Q1(QxProtocolOptions options) =>
        new(options, "q1", "Q1 0.08", "Q1\r", rating: false, vendorCommand: null, requiresVendor: false);
}
