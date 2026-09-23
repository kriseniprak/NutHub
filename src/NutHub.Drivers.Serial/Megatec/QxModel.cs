using NutHub.Core.Model;

namespace NutHub.Drivers.Serial.Megatec;

/// <summary>The item flags of NUT drivers/nutdrv_qx.h, those that matter for polling.</summary>
[Flags]
internal enum QxFlags
{
    None = 0,

    /// <summary>Read once when the UPS is identified (QX_FLAG_STATIC).</summary>
    Static = 1,

    /// <summary>Read at start-up and again after an instant command or variable write (QX_FLAG_SEMI_STATIC).</summary>
    SemiStatic = 2,

    /// <summary>Essential status: a failure fails the whole poll (QX_FLAG_QUICK_POLL).</summary>
    QuickPoll = 4,

    /// <summary>Trim spaces and '#' around the value (QX_FLAG_TRIM).</summary>
    Trim = 8,

    /// <summary>Not queried: unsupported by this UPS or waiting to be enabled by another reply (QX_FLAG_SKIP).</summary>
    Skip = 16,

    /// <summary>Internal value with no NUT variable (QX_FLAG_NONUT).</summary>
    NoNut = 32,
}

/// <summary>
/// How a raw field becomes a NUT value: a string (NUT "%s"), or a number printed with a fixed count of decimals
/// (NUT "%.1f", "%.0f"...).
/// </summary>
internal readonly record struct QxFormat(int Decimals)
{
    public static readonly QxFormat Text = new(-1);

    public bool IsText => Decimals < 0;

    public static QxFormat Fixed(int decimals) => new(decimals);

    public string Apply(double value) => NutFormat.Fixed(value, Math.Max(Decimals, 0));
}

/// <summary>
/// Turns the raw text of a field into its NUT value (NUT item_t.preprocess). Returns false when the value is invalid.
/// For "ups.status" items the value is a status change ("OL", "!OL", "LB"...), for "ups.alarm" an alarm text, and an
/// empty value means "nothing to report".
/// </summary>
internal delegate bool QxValueProcessor(QxField field, string raw, QxState state, out string value);

/// <summary>
/// Rewrites a whole reply before the fields are cut out of it (NUT item_t.preprocess_answer), e.g. to decode binary
/// data or normalise field widths. Returns null when the reply is not valid.
/// </summary>
internal delegate string? QxAnswerProcessor(string answer);

/// <summary>
/// Builds the text of an instant command (NUT item_t.preprocess for commands). Returns false with an error when the
/// parameter or the current settings cannot be expressed.
/// </summary>
internal delegate bool QxCommandFormatter(QxCommand command, string? parameter, QxState state, out string text, out string error);

/// <summary>
/// One value read from the UPS: which command to send, how to validate the reply and where the value sits in it. A
/// port of NUT's item_t for data items; see drivers/nutdrv_qx.h.
/// </summary>
internal sealed class QxField
{
    public QxField(string name, string command, int minLength, char leading, int from, int to, QxFormat format,
                   QxFlags flags = QxFlags.None, QxValueProcessor? process = null, QxAnswerProcessor? answer = null)
    {
        Name = name;
        Command = command;
        MinLength = minLength;
        Leading = leading;
        From = from;
        To = to;
        Format = format;
        Flags = flags;
        Process = process;
        Answer = answer;
    }

    /// <summary>The NUT variable, "ups.status" / "ups.alarm" for status and alarm bits, or an internal name.</summary>
    public string Name { get; }

    /// <summary>The query sent to the UPS, with its carriage return: "Q1\r".</summary>
    public string Command { get; }

    /// <summary>Minimum length of a valid reply, carriage return included; 0 when any length is fine.</summary>
    public int MinLength { get; }

    /// <summary>The first character of a valid reply, or '\0' when not checked.</summary>
    public char Leading { get; }

    /// <summary>Position of the first character of the value in the reply.</summary>
    public int From { get; }

    /// <summary>Position of the last character of the value, or 0 for "up to the carriage return".</summary>
    public int To { get; }

    public QxFormat Format { get; }

    public QxFlags Flags { get; set; }

    public QxValueProcessor? Process { get; }

    public QxAnswerProcessor? Answer { get; }

    public bool Skip
    {
        get => (Flags & QxFlags.Skip) != 0;
        set => Flags = value ? Flags | QxFlags.Skip : Flags & ~QxFlags.Skip;
    }

    public bool Has(QxFlags flag) => (Flags & flag) != 0;

    public bool IsStatus => Name == "ups.status";

    public bool IsAlarm => Name == "ups.alarm";

    public override string ToString() => $"{Name} [{Command.TrimEnd('\r')} {From}-{To}]";
}

/// <summary>An instant command (a NUT item_t with QX_FLAG_CMD) and how to check the UPS's reply to it.</summary>
internal sealed class QxCommand
{
    public QxCommand(string name, string command, int minLength = 0, char leading = '\0', int from = 0, int to = 0,
                     QxCommandFormatter? format = null, QxFlags flags = QxFlags.None)
    {
        Name = name;
        Command = command;
        MinLength = minLength;
        Leading = leading;
        From = from;
        To = to;
        Format = format;
        Flags = flags;
    }

    public string Name { get; }

    /// <summary>The command, possibly a template ("S%s\r") that <see cref="Format"/> fills in.</summary>
    public string Command { get; }

    public int MinLength { get; }

    public char Leading { get; }

    public int From { get; }

    public int To { get; }

    public QxCommandFormatter? Format { get; }

    public QxFlags Flags { get; set; }

    public bool Skip
    {
        get => (Flags & QxFlags.Skip) != 0;
        set => Flags = value ? Flags | QxFlags.Skip : Flags & ~QxFlags.Skip;
    }
}

/// <summary>What the user may configure for a protocol, beyond the connection.</summary>
internal sealed record QxProtocolOptions(bool NoRating = false, bool NoVendor = false, bool IgnoreShutdownActive = false);
