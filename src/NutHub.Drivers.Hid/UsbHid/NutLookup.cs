namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>Converts a HID value to NUT text; null means "no valid value" (the variable is left unchanged).</summary>
internal delegate string? HidToNut(double value, IHidConversionContext context);

/// <summary>Converts NUT text to the HID value to write; null means the text is not acceptable.</summary>
internal delegate double? NutToHid(string? value, IHidConversionContext context);

/// <summary>
/// A value translation between the device and NUT (NUT info_lkp_t): a table of HID value / NUT text pairs,
/// optionally overridden in each direction by a conversion function. As in NUT, a function replaces the table in
/// its direction, while the table still lists the values offered for enumerated writable variables.
/// </summary>
internal sealed class NutLookup
{
    private readonly (long HidValue, string NutValue)[] _entries;
    private readonly HidToNut? _toNut;
    private readonly NutToHid? _toHid;

    private NutLookup((long, string)[] entries, HidToNut? toNut, NutToHid? toHid)
    {
        _entries = entries;
        _toNut = toNut;
        _toHid = toHid;
    }

    public IReadOnlyList<(long HidValue, string NutValue)> Entries => _entries;

    public static NutLookup Table(IReadOnlyList<(long HidValue, string NutValue)> entries) =>
        new([.. entries], null, null);

    public static NutLookup Create(IReadOnlyList<(long HidValue, string NutValue)> entries, HidToNut? toNut, NutToHid? toHid) =>
        new([.. entries], toNut, toHid);

    public static NutLookup Function(HidToNut toNut, NutToHid? toHid = null) => new([], toNut, toHid);

    /// <summary>The NUT text for a HID value (NUT hu_find_infoval).</summary>
    public string? ToNut(double value, IHidConversionContext context)
    {
        if (_toNut is not null)
        {
            return _toNut(value, context);
        }

        long key = Descriptors.HidValueCodec.Truncate(value);
        foreach (var (hid, nut) in _entries)
        {
            if (hid == key)
            {
                return nut;
            }
        }

        return null;
    }

    /// <summary>The HID value for NUT text (NUT hu_find_valinfo), or null when the text is not accepted.</summary>
    public double? ToHid(string? value, IHidConversionContext context)
    {
        if (_toHid is not null)
        {
            return _toHid(value, context);
        }

        foreach (var (hid, nut) in _entries)
        {
            if (string.Equals(nut, value, StringComparison.Ordinal))
            {
                return hid;
            }
        }

        return null;
    }

    /// <summary>
    /// The values offered for an enumerated variable: the table entries the device side accepts (NUT only adds an
    /// enum value when the reverse conversion of its HID value succeeds).
    /// </summary>
    public IReadOnlyList<string> GetEnumValues(IHidConversionContext context)
    {
        var values = new List<string>();
        foreach (var (hid, nut) in _entries)
        {
            if (ToNut(hid, context) is not null && !values.Contains(nut))
            {
                values.Add(nut);
            }
        }

        return values;
    }
}
