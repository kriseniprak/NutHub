using System.Globalization;
using System.Text;

namespace NutHub.Drivers.Hid.Descriptors;

/// <summary>
/// The chain of usages from the top-level collection down to a report field, e.g.
/// UPS (0x00840004) / PowerSummary (0x00840024) / RemainingCapacity (0x00850066). Every node is a 32-bit
/// usage (page in the high half); a node 0x00FFnnnn is the index of a collection whose type is 0x80 or above,
/// written "[n]" (NUT drivers/libhid.c path_to_string / string_to_path).
/// </summary>
internal readonly struct HidPath : IEquatable<HidPath>
{
    /// <summary>The high half of index nodes.</summary>
    public const uint IndexPage = 0x00FF0000;

    private readonly uint[]? _nodes;

    public HidPath(params uint[] nodes) => _nodes = nodes;

    public HidPath(ReadOnlySpan<uint> nodes) => _nodes = nodes.ToArray();

    public ReadOnlySpan<uint> Nodes => _nodes;

    public int Length => _nodes?.Length ?? 0;

    public uint this[int index] => _nodes![index];

    /// <summary>The usage of the field itself (0 for an empty path).</summary>
    public uint Last => Length == 0 ? 0 : _nodes![^1];

    public static bool IsIndexNode(uint node) => (node & 0xFFFF0000) == IndexPage;

    /// <summary>
    /// Parses "UPS.PowerSummary.RemainingCapacity", "UPS.Flow.[4].ConfigVoltage" or "UPS.ff860024". Unlike NUT,
    /// which silently drops a component it cannot read (and so may match a shorter, wrong path), an unknown
    /// component makes the whole path invalid.
    /// </summary>
    public static bool TryParse(string? text, HidUsageTable usages, out HidPath path)
    {
        ArgumentNullException.ThrowIfNull(usages);
        path = default;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        string[] parts = text.Split('.');
        var nodes = new uint[parts.Length];
        for (int i = 0; i < parts.Length; i++)
        {
            string token = parts[i];
            if (token.Length == 0)
            {
                return false;
            }

            if (usages.TryGetCode(token, out uint code))
            {
                nodes[i] = code;
            }
            else if (token.All(char.IsAsciiHexDigit) && token.Length <= 8)
            {
                nodes[i] = uint.Parse(token, NumberStyles.HexNumber, CultureInfo.InvariantCulture);
            }
            else if (token.Length > 2 && token[0] == '[' && token[^1] == ']' &&
                     ushort.TryParse(token.AsSpan(1, token.Length - 2), NumberStyles.None, CultureInfo.InvariantCulture,
                                     out ushort index))
            {
                nodes[i] = IndexPage | index;
            }
            else
            {
                return false;
            }
        }

        path = new HidPath(nodes);
        return true;
    }

    /// <summary>Formats the path with usage names where known, "[n]" for indexes and 8 hex digits otherwise.</summary>
    public string ToString(HidUsageTable? usages)
    {
        var sb = new StringBuilder();
        for (int i = 0; i < Length; i++)
        {
            if (i > 0)
            {
                sb.Append('.');
            }

            uint node = _nodes![i];
            string? name = usages?.GetName(node);
            if (name is not null)
            {
                sb.Append(name);
            }
            else if (IsIndexNode(node))
            {
                sb.Append('[').Append((node & 0xFFFF).ToString(CultureInfo.InvariantCulture)).Append(']');
            }
            else
            {
                sb.Append(node.ToString("x8", CultureInfo.InvariantCulture));
            }
        }

        return sb.ToString();
    }

    public override string ToString() => ToString(HidUsageTable.Standard);

    /// <summary>Whether this path begins with all the nodes of <paramref name="prefix"/>.</summary>
    public bool StartsWith(HidPath prefix) => prefix.Length <= Length && Nodes[..prefix.Length].SequenceEqual(prefix.Nodes);

    public bool Equals(HidPath other) => Nodes.SequenceEqual(other.Nodes);

    public override bool Equals(object? obj) => obj is HidPath other && Equals(other);

    public override int GetHashCode()
    {
        var hash = new HashCode();
        foreach (uint node in Nodes)
        {
            hash.Add(node);
        }

        return hash.ToHashCode();
    }

    public static bool operator ==(HidPath left, HidPath right) => left.Equals(right);

    public static bool operator !=(HidPath left, HidPath right) => !left.Equals(right);
}
