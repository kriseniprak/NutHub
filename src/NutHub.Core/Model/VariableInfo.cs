namespace NutHub.Core.Model;

/// <summary>The value type of a variable, in the sense of the NUT "GET TYPE" command.</summary>
public enum VariableType
{
    /// <summary>A number (integer or float with a dot as decimal separator). The NUT default.</summary>
    Number,

    /// <summary>Free text, limited to <see cref="VariableInfo.MaxLength"/> characters.</summary>
    String,
}

/// <summary>One allowed interval of a numeric variable (NUT "LIST RANGE").</summary>
public readonly record struct ValueRange(string Min, string Max)
{
    public bool Contains(double value) =>
        double.TryParse(Min, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double min) &&
        double.TryParse(Max, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out double max) &&
        value >= min && value <= max;
}

/// <summary>
/// Metadata of a UPS variable: whether it can be written and which values it accepts. Variables without explicit
/// metadata are read-only numbers, exactly like in upsd.
/// </summary>
public sealed record VariableInfo
{
    public static readonly VariableInfo ReadOnly = new();

    /// <summary>Whether SET VAR is allowed ("RW").</summary>
    public bool Writable { get; init; }

    public VariableType Type { get; init; } = VariableType.Number;

    /// <summary>Maximum length of a <see cref="VariableType.String"/> value; 0 when unspecified.</summary>
    public int MaxLength { get; init; }

    /// <summary>The accepted values of an enumerated variable ("ENUM"); empty when not enumerated.</summary>
    public IReadOnlyList<string> EnumValues { get; init; } = [];

    /// <summary>The accepted intervals of a ranged variable ("RANGE"); empty when not ranged.</summary>
    public IReadOnlyList<ValueRange> Ranges { get; init; } = [];

    public static VariableInfo WritableNumber() => new() { Writable = true };

    public static VariableInfo WritableString(int maxLength) =>
        new() { Writable = true, Type = VariableType.String, MaxLength = maxLength };

    public static VariableInfo WritableEnum(params string[] values) =>
        new() { Writable = true, EnumValues = values };

    public static VariableInfo WritableRange(double min, double max) =>
        new()
        {
            Writable = true,
            Ranges = [new ValueRange(NutFormat.Number(min), NutFormat.Number(max))],
        };

    /// <summary>
    /// The words upsd prints after "TYPE ups var": RW, ENUM, RANGE, STRING:n, or NUMBER when none of ENUM, RANGE
    /// and STRING apply.
    /// </summary>
    public string ToNutTypeWords()
    {
        var words = new List<string>(3);
        if (Writable)
        {
            words.Add("RW");
        }

        if (EnumValues.Count > 0)
        {
            words.Add("ENUM");
        }

        if (Ranges.Count > 0)
        {
            words.Add("RANGE");
        }

        if (Type == VariableType.String)
        {
            words.Add($"STRING:{MaxLength}");
        }
        else if (EnumValues.Count == 0 && Ranges.Count == 0)
        {
            words.Add("NUMBER");
        }

        return string.Join(' ', words);
    }

    public bool Equals(VariableInfo? other) =>
        other is not null &&
        Writable == other.Writable &&
        Type == other.Type &&
        MaxLength == other.MaxLength &&
        EnumValues.SequenceEqual(other.EnumValues) &&
        Ranges.SequenceEqual(other.Ranges);

    public override int GetHashCode() => HashCode.Combine(Writable, Type, MaxLength, EnumValues.Count, Ranges.Count);
}
