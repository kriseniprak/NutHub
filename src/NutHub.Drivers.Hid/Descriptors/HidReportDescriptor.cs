namespace NutHub.Drivers.Hid.Descriptors;

/// <summary>A parsed report descriptor: the data fields of every report and the size of each report.</summary>
internal sealed class HidReportDescriptor
{
    private readonly List<HidField> _fields;
    private readonly Dictionary<(HidReportKind Kind, byte Id), int> _reportBits;

    internal HidReportDescriptor(List<HidField> fields, Dictionary<(HidReportKind, byte), int> reportBits,
                                 IReadOnlyList<uint> applicationUsages, bool usesReportIds, IReadOnlyList<string> warnings)
    {
        _fields = fields;
        _reportBits = reportBits;
        ApplicationUsages = applicationUsages;
        UsesReportIds = usesReportIds;
        Warnings = warnings;
    }

    /// <summary>The fields in descriptor order; only fields with a usage (NUT ignores padding and continuations).</summary>
    public IReadOnlyList<HidField> Fields => _fields;

    /// <summary>The usages of the top-level application collections (UPS is 0x00840004).</summary>
    public IReadOnlyList<uint> ApplicationUsages { get; }

    public bool UsesReportIds { get; }

    /// <summary>Problems met while parsing; the fields parsed before them are kept, as NUT does.</summary>
    public IReadOnlyList<string> Warnings { get; }

    /// <summary>
    /// Whether a top-level collection belongs to the Power Device (0x84) or Battery System (0x85) page, which
    /// is how a UPS identifies itself whatever its vendor.
    /// </summary>
    public bool IsPowerDevice => ApplicationUsages.Any(u => (u >> 16) is 0x84 or 0x85);

    /// <summary>The report ids used by reports of the given kind, in ascending order.</summary>
    public IEnumerable<byte> GetReportIds(HidReportKind kind) =>
        _reportBits.Keys.Where(k => k.Kind == kind).Select(k => k.Id).Order();

    /// <summary>The length in bytes of a report, without the report id byte (0 when unknown).</summary>
    public int GetReportLength(HidReportKind kind, byte reportId) =>
        _reportBits.TryGetValue((kind, reportId), out int bits) ? (bits + 7) / 8 : 0;

    /// <summary>
    /// Finds the field at <paramref name="path"/>: the first exact match in descriptor order, else the first
    /// field whose path starts with it (NUT FindObject_with_Path compares only the requested nodes).
    /// </summary>
    public HidField? Find(HidPath path, HidReportKind kind)
    {
        if (path.Length == 0)
        {
            return null;
        }

        HidField? prefixMatch = null;
        foreach (HidField field in _fields)
        {
            if (field.Kind != kind)
            {
                continue;
            }

            if (field.Path == path)
            {
                return field;
            }

            if (prefixMatch is null && field.Path.StartsWith(path))
            {
                prefixMatch = field;
            }
        }

        return prefixMatch;
    }

    /// <summary>The first field (of any kind) in report <paramref name="reportId"/> whose last usage is <paramref name="usage"/> (NUT FindObject_with_ID_Node).</summary>
    public HidField? FindByReportAndUsage(byte reportId, uint usage) =>
        _fields.FirstOrDefault(f => f.ReportId == reportId && f.Path.Length > 0 && f.Path.Last == usage);

    /// <summary>The fields of one report, in descriptor order.</summary>
    public IEnumerable<HidField> GetFields(HidReportKind kind, byte reportId) =>
        _fields.Where(f => f.Kind == kind && f.ReportId == reportId);
}
