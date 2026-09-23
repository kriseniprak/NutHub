namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>
/// How a mapping entry behaves: the NUT variable flags (ST_FLAG_*) and the usbhid-ups flags (HU_FLAG_*,
/// HU_TYPE_CMD) of NUT's hid_info_t, in one set.
/// </summary>
[Flags]
internal enum HidMapFlags
{
    None = 0,

    /// <summary>ST_FLAG_RW: the variable can be written.</summary>
    Rw = 1 << 0,

    /// <summary>ST_FLAG_STRING: a string variable; the entry's length is its maximum length.</summary>
    Str = 1 << 1,

    /// <summary>ST_FLAG_NUMBER: explicitly numeric.</summary>
    Num = 1 << 2,

    /// <summary>HU_FLAG_STATIC: read once when connecting.</summary>
    Static = 1 << 3,

    /// <summary>HU_FLAG_SEMI_STATIC: read when connecting, after writes and commands, and from time to time.</summary>
    SemiStatic = 1 << 4,

    /// <summary>
    /// HU_FLAG_ABSENT: a variable kept by the driver (not read from the device) with the entry's default value;
    /// it exists only when the device has the HID path.
    /// </summary>
    Absent = 1 << 5,

    /// <summary>HU_FLAG_QUICK_POLL: status information NUT polls often.</summary>
    QuickPoll = 1 << 6,

    /// <summary>HU_TYPE_CMD: an instant command that writes the entry's default (or the parameter) to the path.</summary>
    Cmd = 1 << 7,

    /// <summary>HU_FLAG_ENUM: the lookup table lists the accepted values of a writable variable.</summary>
    Enumerated = 1 << 8,

    /// <summary>HU_FLAG_PARAM_REQUIRED: the command or write needs a value.</summary>
    ParamRequired = 1 << 9,
}

/// <summary>
/// One line of a subdriver table: which HID path feeds which NUT variable, status flag or instant command, and how
/// the value is converted (NUT hid_info_t).
/// </summary>
/// <param name="Name">
/// The NUT variable or command; "BOOL" for status flags (the lookup gives the internal flag name, e.g. "online",
/// "!dischrg"); a name starting with "ups.alarm" adds the lookup's text to ups.alarm.
/// </param>
/// <param name="Length">Maximum length of a string variable.</param>
/// <param name="Path">The HID path, e.g. "UPS.PowerSummary.RemainingCapacity".</param>
/// <param name="Default">
/// A printf format for the value ("%.0f") for variables without lookup; the value of a driver-kept variable
/// (<see cref="HidMapFlags.Absent"/>); the value written by a command.
/// </param>
/// <param name="Flags">Behaviour flags.</param>
/// <param name="Lookup">Conversion between HID values and NUT text, or null for plain numbers.</param>
internal sealed record HidMapping(string Name, int Length, string? Path, string? Default, HidMapFlags Flags, NutLookup? Lookup)
{
    public bool IsCommand => (Flags & HidMapFlags.Cmd) != 0;

    public bool IsAbsent => (Flags & HidMapFlags.Absent) != 0;

    public bool IsWritable => (Flags & HidMapFlags.Rw) != 0;

    /// <summary>NUT compares the first four characters only.</summary>
    public bool IsStatusFlag => Name.StartsWith("BOOL", StringComparison.Ordinal);

    public bool IsAlarm => Name.StartsWith("ups.alarm", StringComparison.Ordinal);

    public bool Has(HidMapFlags flag) => (Flags & flag) != 0;
}
