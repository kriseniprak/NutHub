namespace NutHub.Drivers.Hid.Descriptors;

/// <summary>The three kinds of HID reports; the values are the main item tags of the report descriptor.</summary>
internal enum HidReportKind : byte
{
    Input = 0x80,
    Output = 0x90,
    Feature = 0xB0,
}

/// <summary>
/// One data field of a report, located by its report id, bit offset and size, with the scaling the descriptor
/// declares (the equivalent of NUT's HIDData_t). The limits are settable because some UPS firmwares declare
/// them wrongly and the subdrivers repair them after parsing (NUT's fix_report_desc).
/// </summary>
internal sealed class HidField
{
    public required HidPath Path { get; init; }

    public required byte ReportId { get; init; }

    public required HidReportKind Kind { get; init; }

    /// <summary>Offset in bits from the first data byte (after the report id byte).</summary>
    public required int BitOffset { get; init; }

    public required int BitSize { get; init; }

    /// <summary>The main item flags: bit 0 constant, bit 1 variable, bit 2 relative, bit 7 volatile...</summary>
    public int Attributes { get; init; }

    /// <summary>The HID unit system and dimensions (0x00F0D121 is volt, 0x0000D121 watt/VA...).</summary>
    public uint Unit { get; set; }

    public sbyte UnitExponent { get; set; }

    public long LogicalMinimum { get; set; }

    public long LogicalMaximum { get; set; }

    /// <summary>
    /// True when the declared logical maximum read as a negative number smaller than the minimum and was
    /// reinterpreted as unsigned (a common encoding mistake in UPS firmwares).
    /// </summary>
    public bool LogicalMaximumAssumed { get; set; }

    public long PhysicalMinimum { get; set; }

    public long PhysicalMaximum { get; set; }

    public bool HasPhysicalMinimum { get; set; }

    public bool HasPhysicalMaximum { get; set; }

    /// <summary>Position of the field in the descriptor, for stable ordering.</summary>
    public int Index { get; init; }

    /// <summary>A constant field cannot be written (NUT only refuses writes when the attributes are exactly 1).</summary>
    public bool IsReadOnlyConstant => Attributes == 1;

    public override string ToString() =>
        $"{Path} {Kind} id=0x{ReportId:x2} offset={BitOffset} size={BitSize} logical={LogicalMinimum}..{LogicalMaximum}";
}
