namespace NutHub.Drivers.Hid.Descriptors;

/// <summary>
/// Parses HID report descriptors the way NUT's drivers/hidparser.c does, so that field paths, offsets and
/// limits match what the NUT subdriver tables were written against:
/// <list type="bullet">
/// <item>a collection adds the first pending usage to the path (0 when there is none) and, when its type is
/// 0x80 or more, an index node "[type &amp; 0x7F]";</item>
/// <item>each field of a main item takes the next pending usage; fields left without one (padding, or a count
/// larger than the usages given) advance the offset but are not stored;</item>
/// <item>local items are reset after every main item;</item>
/// <item>a logical maximum below the logical minimum is re-read as unsigned (firmware encoding mistake).</item>
/// </list>
/// Beyond NUT it understands Push/Pop and Usage Minimum/Maximum, and it never reads past the buffer: a
/// malformed descriptor ends the parse with a warning, keeping the fields found so far.
/// </summary>
internal static class HidReportDescriptorParser
{
    /// <summary>Report descriptors are at most a few kilobytes; anything larger is not a UPS.</summary>
    public const int MaxDescriptorLength = 64 * 1024;

    private const int MaxPathDepth = 32;
    private const int MaxFields = 16384;
    private const int MaxUsageRange = 1024;
    private const int MaxGlobalStack = 16;

    public static HidReportDescriptor Parse(ReadOnlySpan<byte> descriptor)
    {
        if (descriptor.Length > MaxDescriptorLength)
        {
            throw new InvalidDataException(
                $"The report descriptor is {descriptor.Length} bytes long; at most {MaxDescriptorLength} are accepted.");
        }

        var state = new ParserState();
        int pos = 0;
        while (pos < descriptor.Length)
        {
            byte prefix = descriptor[pos++];
            if (prefix == 0xFE)
            {
                // Long item: data size, long item tag, data. Nothing a UPS uses; skip it.
                if (descriptor.Length - pos < 2)
                {
                    state.Warn($"Truncated long item at offset {pos - 1}.");
                    break;
                }

                int longSize = descriptor[pos];
                pos += 2 + longSize;
                continue;
            }

            int size = (prefix & 0x03) == 3 ? 4 : prefix & 0x03;
            if (descriptor.Length - pos < size)
            {
                state.Warn($"Truncated item 0x{prefix:x2} at offset {pos - 1}.");
                break;
            }

            uint value = 0;
            for (int i = 0; i < size; i++)
            {
                value |= (uint)descriptor[pos + i] << (8 * i);
            }

            pos += size;
            if (!state.Apply((byte)(prefix & 0xFC), value, size))
            {
                break;
            }
        }

        return state.Build();
    }

    /// <summary>Sign-extends an item value according to its encoded size (NUT FormatValue).</summary>
    private static long Signed(uint value, int size) => size switch
    {
        1 => (sbyte)(byte)value,
        2 => (short)(ushort)value,
        4 => (int)value,
        _ => value,
    };

    private struct Globals
    {
        public ushort UsagePage;
        public long LogicalMinimum;
        public long LogicalMaximum;
        public bool LogicalMaximumAssumed;
        public long PhysicalMinimum;
        public long PhysicalMaximum;
        public bool HasPhysicalMinimum;
        public bool HasPhysicalMaximum;
        public sbyte UnitExponent;
        public uint Unit;
        public int ReportSize;
        public byte ReportId;
        public int ReportCount;
    }

    private sealed class ParserState
    {
        private readonly List<HidField> _fields = [];
        private readonly Dictionary<(HidReportKind, byte), int> _reportBits = [];
        private readonly List<uint> _path = [];
        private readonly Stack<int> _collectionNodes = new();
        private readonly Stack<Globals> _globalStack = new();
        private readonly Queue<uint> _usages = new();
        private readonly List<uint> _applications = [];
        private readonly List<string> _warnings = [];
        private Globals _g;
        private uint? _usageMinimum;
        private bool _usesReportIds;

        public void Warn(string message) => _warnings.Add(message);

        /// <summary>Applies one short item; false stops the parse.</summary>
        public bool Apply(byte tag, uint value, int size)
        {
            switch (tag)
            {
                // Global items
                case 0x04:
                    _g.UsagePage = (ushort)value;
                    break;
                case 0x14:
                    _g.LogicalMinimum = Signed(value, size);
                    break;
                case 0x24:
                    _g.LogicalMaximum = Signed(value, size);
                    _g.LogicalMaximumAssumed = false;
                    if (_g.LogicalMaximum < _g.LogicalMinimum)
                    {
                        // NUT hidparser.c: a firmware wrote e.g. 0..65535 as 0..-1; take the bits as unsigned.
                        _g.LogicalMaximum = value;
                        _g.LogicalMaximumAssumed = true;
                    }

                    break;
                case 0x34:
                    _g.PhysicalMinimum = Signed(value, size);
                    _g.HasPhysicalMinimum = true;
                    break;
                case 0x44:
                    _g.PhysicalMaximum = Signed(value, size);
                    _g.HasPhysicalMaximum = true;
                    break;
                case 0x54:
                    // A 4-bit two's complement nibble per the spec, but some firmwares write a full signed byte.
                    sbyte exponent = unchecked((sbyte)(byte)value);
                    _g.UnitExponent = exponent > 7 ? unchecked((sbyte)(exponent | unchecked((sbyte)0xF0))) : exponent;
                    break;
                case 0x64:
                    _g.Unit = value;
                    break;
                case 0x74:
                    _g.ReportSize = (int)Math.Min(value, 1024u);
                    break;
                case 0x84:
                    _g.ReportId = (byte)value;
                    _usesReportIds = true;
                    break;
                case 0x94:
                    _g.ReportCount = (int)Math.Min(value, 65535u);
                    break;
                case 0xA4:
                    if (_globalStack.Count >= MaxGlobalStack)
                    {
                        Warn("Too many nested Push items.");
                        return false;
                    }

                    _globalStack.Push(_g);
                    break;
                case 0xB4:
                    if (_globalStack.Count == 0)
                    {
                        Warn("Pop item without a matching Push.");
                        return false;
                    }

                    _g = _globalStack.Pop();
                    break;

                // Local items
                case 0x08:
                    _usages.Enqueue(Usage(value, size));
                    break;
                case 0x18:
                    _usageMinimum = Usage(value, size);
                    break;
                case 0x28:
                    if (_usageMinimum is uint min)
                    {
                        uint max = Usage(value, size);
                        if (max >= min && max - min < MaxUsageRange)
                        {
                            for (uint u = min; u <= max; u++)
                            {
                                _usages.Enqueue(u);
                            }
                        }
                        else
                        {
                            Warn($"Ignored usage range 0x{min:x8}..0x{max:x8}.");
                        }

                        _usageMinimum = null;
                    }

                    break;

                // Main items
                case 0xA0:
                    return BeginCollection(value);
                case 0xC0:
                    return EndCollection();
                case 0x80:
                case 0x90:
                case 0xB0:
                    return AddFields((HidReportKind)tag, value);
            }

            return true;
        }

        private uint Usage(uint value, int size) => size == 4 ? value : ((uint)_g.UsagePage << 16) | (value & 0xFFFF);

        private bool BeginCollection(uint type)
        {
            uint usage = _usages.Count > 0 ? _usages.Dequeue() : 0;
            int nodes = (type & 0xFF) >= 0x80 ? 2 : 1;
            if (_path.Count + nodes > MaxPathDepth)
            {
                Warn("Collections nested too deeply.");
                return false;
            }

            if (_path.Count == 0 && (type & 0xFF) == 0x01)
            {
                _applications.Add(usage);
            }

            _path.Add(usage);
            if (nodes == 2)
            {
                _path.Add(HidPath.IndexPage | (type & 0x7F));
            }

            _collectionNodes.Push(nodes);
            ResetLocals();
            return true;
        }

        private bool EndCollection()
        {
            if (_collectionNodes.Count == 0)
            {
                Warn("End Collection without a matching Collection.");
                return false;
            }

            _path.RemoveRange(_path.Count - _collectionNodes.Peek(), _collectionNodes.Pop());
            ResetLocals();
            return true;
        }

        private bool AddFields(HidReportKind kind, uint attributes)
        {
            if (_path.Count + 1 > MaxPathDepth)
            {
                Warn("Collections nested too deeply.");
                return false;
            }

            var key = (kind, _g.ReportId);
            int offset = _reportBits.GetValueOrDefault(key);
            for (int i = 0; i < _g.ReportCount; i++)
            {
                uint usage = _usages.Count > 0 ? _usages.Dequeue() : 0;
                if (usage != 0 && _fields.Count < MaxFields)
                {
                    _fields.Add(new HidField
                    {
                        Path = new HidPath([.. _path, usage]),
                        ReportId = _g.ReportId,
                        Kind = kind,
                        BitOffset = offset,
                        BitSize = _g.ReportSize,
                        Attributes = (int)(attributes & 0x1FF),
                        Unit = _g.Unit,
                        UnitExponent = _g.UnitExponent,
                        LogicalMinimum = _g.LogicalMinimum,
                        LogicalMaximum = _g.LogicalMaximum,
                        LogicalMaximumAssumed = _g.LogicalMaximumAssumed,
                        PhysicalMinimum = _g.PhysicalMinimum,
                        PhysicalMaximum = _g.PhysicalMaximum,
                        HasPhysicalMinimum = _g.HasPhysicalMinimum,
                        HasPhysicalMaximum = _g.HasPhysicalMaximum,
                        Index = _fields.Count,
                    });
                }
                else if (usage != 0)
                {
                    Warn("Too many fields; the rest of the descriptor is ignored.");
                    return false;
                }

                offset += _g.ReportSize;
            }

            _reportBits[key] = offset;
            ResetLocals();
            return true;
        }

        private void ResetLocals()
        {
            _usages.Clear();
            _usageMinimum = null;
        }

        public HidReportDescriptor Build()
        {
            if (_collectionNodes.Count > 0 && _warnings.Count == 0)
            {
                Warn("The descriptor ends inside a collection.");
            }

            return new HidReportDescriptor(_fields, _reportBits, _applications, _usesReportIds, _warnings);
        }
    }
}
