namespace NutHub.Drivers.Snmp.Mibs;

/// <summary>
/// Behaviour flags of a mapping entry. They are the equivalent of NUT's <c>SU_FLAG_*</c>, <c>SU_INPUT_*</c>,
/// <c>SU_OUTPUT_*</c> and <c>SU_BYPASS_*</c> bits (drivers/snmp-ups.h), so mapping tables can be ported line by line.
/// </summary>
[Flags]
internal enum MibFlags
{
    None = 0,

    /// <summary>Read once when connecting (identity, ratings): SU_FLAG_STATIC.</summary>
    Static = 1 << 0,

    /// <summary>
    /// The device has no such object: the entry only supplies <see cref="MibEntry.Default"/> (SU_FLAG_ABSENT).
    /// A writable absent entry is a driver-side setting (the default delays of the delayed commands).
    /// </summary>
    Absent = 1 << 1,

    /// <summary>A negative value means "not available" (SU_FLAG_NEGINVALID).</summary>
    NegativeInvalid = 1 << 2,

    /// <summary>A zero value means "not available" (SU_FLAG_ZEROINVALID).</summary>
    ZeroInvalid = 1 << 3,

    /// <summary>The text "N/A" means "not available" (SU_FLAG_NAINVALID).</summary>
    NotAvailableInvalid = 1 << 4,

    /// <summary>
    /// The first entry of this name that answers wins and disables the other providers of the name
    /// (SU_FLAG_UNIQUE): used to prefer a high-resolution object over a coarse one.
    /// </summary>
    Unique = 1 << 5,

    /// <summary>Refreshed only every few polls (SU_FLAG_SEMI_STATIC): dates and settings that rarely change.</summary>
    SemiStatic = 1 << 6,

    /// <summary>Only when the UPS has one input phase (SU_INPUT_1).</summary>
    Input1 = 1 << 7,

    /// <summary>Only when the UPS has three input phases (SU_INPUT_3).</summary>
    Input3 = 1 << 8,

    /// <summary>Only when the UPS has one output phase (SU_OUTPUT_1).</summary>
    Output1 = 1 << 9,

    /// <summary>Only when the UPS has three output phases (SU_OUTPUT_3).</summary>
    Output3 = 1 << 10,

    /// <summary>Only when the bypass has one phase (SU_BYPASS_1).</summary>
    Bypass1 = 1 << 11,

    /// <summary>Only when the bypass has three phases (SU_BYPASS_3).</summary>
    Bypass3 = 1 << 12,
}

/// <summary>What an entry produces.</summary>
internal enum MibEntryKind
{
    /// <summary>A number: the raw value times <see cref="MibEntry.Multiplier"/>, or a lookup text.</summary>
    Number,

    /// <summary>Text: an OCTET STRING, or the lookup text / decimal form of a numeric object.</summary>
    Text,

    /// <summary>Contributes tokens to "ups.status" through its lookup (NUT's "ups.status" entries).</summary>
    Status,

    /// <summary>Contributes a message to "ups.alarm" through its lookup.</summary>
    Alarm,

    /// <summary>An instant command, executed as an SNMP SET.</summary>
    Command,
}

/// <summary>The SNMP type used when writing a value.</summary>
internal enum SnmpSetType
{
    /// <summary>
    /// The type the agent reported when the object was last read; if it was never read, INTEGER for numbers
    /// and OCTET STRING for text. Following the agent avoids the wrongType errors a fixed guess would cause.
    /// </summary>
    Auto,

    /// <summary>INTEGER (NUT SU_TYPE_INT).</summary>
    Integer,

    /// <summary>OCTET STRING.</summary>
    OctetString,

    /// <summary>TimeTicks: the value is in seconds and is sent in hundredths (NUT SU_TYPE_TIME).</summary>
    TimeTicks,

    /// <summary>OBJECT IDENTIFIER (RFC 1628 upsTestId).</summary>
    ObjectIdentifier,

    /// <summary>Gauge32 / Unsigned32.</summary>
    Gauge,
}

/// <summary>A value transformation NUT implements with lookup functions.</summary>
internal enum MibConverter
{
    None,

    /// <summary>"MM/DD/YYYY" to "YYYY-MM-DD" (NUT su_usdate_to_isodate_info).</summary>
    UsDateToIso,
}

/// <summary>
/// A lookup between SNMP integers and NUT texts (NUT <c>info_lkp_t</c>). The first matching pair wins in both
/// directions, as in NUT, where some tables list a value twice.
/// </summary>
internal sealed class MibLookup
{
    private readonly (long Value, string Text)[] _pairs;

    public MibLookup(params (long Value, string Text)[] pairs)
    {
        _pairs = pairs;
    }

    public IReadOnlyList<(long Value, string Text)> Pairs => _pairs;

    public string? Find(long value)
    {
        foreach (var (v, text) in _pairs)
        {
            if (v == value)
            {
                return text;
            }
        }

        return null;
    }

    public long? FindValue(string text)
    {
        foreach (var (v, t) in _pairs)
        {
            if (string.Equals(t, text, StringComparison.OrdinalIgnoreCase))
            {
                return v;
            }
        }

        return null;
    }

    /// <summary>The distinct, non-empty texts: the accepted values of a writable enumerated variable.</summary>
    public IReadOnlyList<string> Texts =>
        _pairs.Select(p => p.Text).Where(t => t.Length > 0).Distinct(StringComparer.Ordinal).ToArray();
}

/// <summary>
/// One line of a mapping table: which NUT variable, status token or command an SNMP object gives, and how
/// (the equivalent of NUT's <c>snmp_info_t</c>).
/// </summary>
internal sealed record MibEntry
{
    /// <summary>The NUT variable ("battery.charge"), "ups.status", "ups.alarm" or the command name.</summary>
    public required string Name { get; init; }

    public required MibEntryKind Kind { get; init; }

    /// <summary>Numeric OID without the leading dot; null for <see cref="MibFlags.Absent"/> entries.</summary>
    public string? Oid { get; init; }

    /// <summary>Factor applied to numbers read (and divided out of numbers written).</summary>
    public double Multiplier { get; init; } = 1;

    /// <summary>
    /// The value of an absent entry, or of a static entry the device does not answer. For commands, the value
    /// written when the caller gives no parameter; null means the command needs a parameter.
    /// </summary>
    public string? Default { get; init; }

    public MibFlags Flags { get; init; }

    public MibLookup? Lookup { get; init; }

    /// <summary>Whether NUT clients may write the variable (SET VAR).</summary>
    public bool Writable { get; init; }

    /// <summary>Maximum length of a writable text variable; 0 when not limited.</summary>
    public int MaxLength { get; init; }

    public SnmpSetType SetType { get; init; }

    public MibConverter Converter { get; init; }

    /// <summary>
    /// For "ambient.temperature" style entries: an OID whose value 2 means the reading is in Fahrenheit and must be
    /// converted to Celsius (APC integrated environment monitor, see NUT drivers/apc-iem-mib.h).
    /// </summary>
    public string? FahrenheitUnitOid { get; init; }

    public bool Has(MibFlags flag) => (Flags & flag) == flag;

    public bool IsAbsent => Has(MibFlags.Absent) || Oid is null;

    public override string ToString() => Oid is null ? Name : $"{Name} <- {Oid}";
}

/// <summary>
/// A vendor alarm table walked each poll when its counter is non zero: every row points (with an OID value) at a
/// well-known alarm, which may add a status token and an alarm message (NUT <c>alarms_info_t</c>, used by the
/// Eaton XUPS-MIB).
/// </summary>
internal sealed record MibAlarmTable
{
    /// <summary>The number of active alarms (xupsAlarms.0).</summary>
    public required string CountOid { get; init; }

    /// <summary>The column holding each alarm's OID (xupsAlarmDescr).</summary>
    public required string DescriptionColumnOid { get; init; }

    public required IReadOnlyList<MibAlarm> Alarms { get; init; }

    /// <summary>Upper bound of rows read in one poll, against misbehaving agents.</summary>
    public int MaxRows { get; init; } = 64;
}

/// <summary>A well-known alarm of a <see cref="MibAlarmTable"/>.</summary>
internal sealed record MibAlarm(string Oid, string? StatusToken, string? Message);

/// <summary>
/// An instant command made of several steps (other commands and variable writes), run in order; the first failing
/// step stops it. It is offered only when every step is available on the device.
/// </summary>
internal sealed record MibComposite(string Name, IReadOnlyList<MibStep> Steps);

/// <summary>A step of a <see cref="MibComposite"/>.</summary>
internal sealed record MibStep
{
    /// <summary>The command to run, or null for a variable write.</summary>
    public string? CommandName { get; private init; }

    /// <summary>The variable to write, or null for a command.</summary>
    public string? VariableName { get; private init; }

    /// <summary>The value written by a variable step.</summary>
    public string? Value { get; private init; }

    /// <summary>Runs a command (a delayed command takes its delay from ups.delay.shutdown / ups.delay.start).</summary>
    public static MibStep Command(string name) => new() { CommandName = name };

    public static MibStep Write(string variable, string value) => new() { VariableName = variable, Value = value };

    public override string ToString() => CommandName ?? $"{VariableName}={Value}";
}

/// <summary>
/// A complete mapping for one family of network cards (NUT <c>mib2nut_info_t</c> with its tables).
/// </summary>
internal sealed record MibDefinition
{
    /// <summary>The value of the "mibs" option, as in NUT ("ietf", "apcc", "mge"...).</summary>
    public required string Name { get; init; }

    /// <summary>Other accepted spellings of <see cref="Name"/> (older NUT names).</summary>
    public IReadOnlyList<string> Aliases { get; init; } = [];

    /// <summary>For the option list of the web panel: "APC PowerNet (apcc)".</summary>
    public required string DisplayName { get; init; }

    /// <summary>The version of the NUT mapping this table follows.</summary>
    public required string Version { get; init; }

    /// <summary>
    /// sysObjectID values (or prefixes) announcing this MIB. A match is confirmed by reading
    /// <see cref="ProbeOid"/> before the MIB is chosen.
    /// </summary>
    public IReadOnlyList<string> SysObjectIds { get; init; } = [];

    /// <summary>
    /// The object that proves the agent implements this MIB; by default the OID of "ups.model" (as NUT's
    /// match_model_OID does).
    /// </summary>
    public string? ProbeOid { get; init; }

    public required IReadOnlyList<MibEntry> Entries { get; init; }

    public MibAlarmTable? AlarmTable { get; init; }

    /// <summary>
    /// Commands made of several steps, offered when every step is available on the device and no single-object
    /// command of the same name exists.
    /// </summary>
    public IReadOnlyList<MibComposite> Composites { get; init; } = [];

    /// <summary>Whether auto-detection may pick this MIB by probing when the sysObjectID says nothing.</summary>
    public bool Probeable { get; init; } = true;

    public string EffectiveProbeOid =>
        ProbeOid
        ?? Entries.FirstOrDefault(e => e.Name == "ups.model" && !e.IsAbsent)?.Oid
        ?? Entries.First(e => !e.IsAbsent && e.Kind != MibEntryKind.Command).Oid!;

    public bool Matches(string name) =>
        string.Equals(Name, name, StringComparison.OrdinalIgnoreCase) ||
        Aliases.Any(a => string.Equals(a, name, StringComparison.OrdinalIgnoreCase));

    public override string ToString() => $"{Name} {Version}";
}

/// <summary>
/// Compact constructors for mapping tables, so each vendor file reads like its NUT counterpart. OIDs may be given
/// with or without the leading dot.
/// </summary>
internal static class Mib
{
    public static string Normalize(string oid) => oid.Trim().TrimStart('.');

    /// <summary>A number: raw value times <paramref name="multiplier"/> (or its lookup text).</summary>
    public static MibEntry Num(string name, string oid, double multiplier = 1, MibFlags flags = MibFlags.None,
                               MibLookup? lookup = null) =>
        new() { Name = name, Kind = MibEntryKind.Number, Oid = Normalize(oid), Multiplier = multiplier, Flags = flags, Lookup = lookup };

    /// <summary>Text: the string value, or the lookup text of a numeric value.</summary>
    public static MibEntry Text(string name, string oid, MibFlags flags = MibFlags.None, MibLookup? lookup = null,
                                string? dfl = null, MibConverter converter = MibConverter.None) =>
        new()
        {
            Name = name, Kind = MibEntryKind.Text, Oid = Normalize(oid), Flags = flags, Lookup = lookup, Default = dfl,
            Converter = converter,
        };

    /// <summary>A fixed value the device does not report (SU_FLAG_ABSENT with a default).</summary>
    public static MibEntry Fixed(string name, string value) =>
        new() { Name = name, Kind = MibEntryKind.Text, Default = value, Flags = MibFlags.Absent | MibFlags.Static };

    /// <summary>A driver-side setting with a default, writable by clients but never sent to the device.</summary>
    public static MibEntry Setting(string name, string value, int maxLength = 6) =>
        new()
        {
            Name = name, Kind = MibEntryKind.Text, Default = value, Flags = MibFlags.Absent, Writable = true,
            MaxLength = maxLength,
        };

    /// <summary>Status tokens from a lookup ("OL", "OB LB"...).</summary>
    public static MibEntry Status(string oid, MibLookup lookup, MibFlags flags = MibFlags.None) =>
        new() { Name = "ups.status", Kind = MibEntryKind.Status, Oid = Normalize(oid), Lookup = lookup, Flags = flags };

    /// <summary>An alarm message from a lookup.</summary>
    public static MibEntry Alarm(string name, string oid, MibLookup lookup) =>
        new() { Name = name, Kind = MibEntryKind.Alarm, Oid = Normalize(oid), Lookup = lookup };

    /// <summary>A writable number.</summary>
    public static MibEntry RwNum(string name, string oid, double multiplier = 1, MibFlags flags = MibFlags.None,
                                 SnmpSetType setType = SnmpSetType.Auto, MibLookup? lookup = null) =>
        new()
        {
            Name = name, Kind = MibEntryKind.Number, Oid = Normalize(oid), Multiplier = multiplier, Flags = flags,
            Writable = true, SetType = setType, Lookup = lookup,
        };

    /// <summary>A writable text (or enumerated) variable.</summary>
    public static MibEntry RwText(string name, string oid, int maxLength, MibFlags flags = MibFlags.None,
                                  SnmpSetType setType = SnmpSetType.Auto, MibLookup? lookup = null,
                                  MibConverter converter = MibConverter.None) =>
        new()
        {
            Name = name, Kind = MibEntryKind.Text, Oid = Normalize(oid), MaxLength = maxLength, Flags = flags,
            Writable = true, SetType = setType, Lookup = lookup, Converter = converter,
        };

    /// <summary>
    /// An instant command writing <paramref name="value"/> (a parameter given by the caller replaces it). A null
    /// value makes the parameter mandatory.
    /// </summary>
    public static MibEntry Cmd(string name, string oid, string? value, SnmpSetType setType = SnmpSetType.Auto) =>
        new() { Name = name, Kind = MibEntryKind.Command, Oid = Normalize(oid), Default = value, SetType = setType };

    public static MibLookup Lookup(params (long Value, string Text)[] pairs) => new(pairs);

    /// <summary>The standard SNMPv2-MIB objects every NUT mapping starts with.</summary>
    public static IEnumerable<MibEntry> SystemGroup()
    {
        // sysDescr is read-only in SNMPv2-MIB; NUT marks it writable but agents refuse the write.
        yield return Text("device.description", "1.3.6.1.2.1.1.1.0");
        yield return RwText("device.contact", "1.3.6.1.2.1.1.4.0", 128, setType: SnmpSetType.OctetString);
        yield return RwText("device.location", "1.3.6.1.2.1.1.6.0", 128, setType: SnmpSetType.OctetString);
    }
}
