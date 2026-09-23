using Microsoft.Extensions.Logging;
using NutHub.Drivers.Hid.Descriptors;

namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>
/// What conversion functions and subdriver hooks may use while the session reads the device: the variables known
/// so far (NUT's dstate), the raw status bits, the field being converted and the device itself. Everything runs
/// on the device thread, so no locking is needed.
/// </summary>
internal interface IHidConversionContext
{
    ILogger Logger { get; }

    /// <summary>Local time, for the few conversions NUT does with localtime().</summary>
    TimeProvider TimeProvider { get; }

    UsbHidSettings Settings { get; }

    /// <summary>The status bits collected so far (NUT ups_status).</summary>
    UpsStatusBits StatusBits { get; }

    /// <summary>The field whose value is being converted, or null outside a conversion.</summary>
    HidField? CurrentField { get; }

    string? GetVariable(string name);

    void SetVariable(string name, string value);

    void RemoveVariable(string name);

    /// <summary>Adds a token to experimental.ups.mode.buzzwords for this update.</summary>
    void AddBuzzword(string word);

    /// <summary>A USB string by index (NUT HIDGetIndexString); null when unavailable.</summary>
    string? GetIndexedString(int index);

    /// <summary>Reads the physical value at a HID path (NUT HIDGetItemValue).</summary>
    bool TryReadValue(string path, out double value);

    /// <summary>Reads a string-index item and resolves the string (NUT HIDGetItemString).</summary>
    string? ReadItemString(string path);

    /// <summary>The last buffer read for a report (report id first), or empty.</summary>
    ReadOnlySpan<byte> GetReportBuffer(HidReportKind kind, byte reportId);
}
