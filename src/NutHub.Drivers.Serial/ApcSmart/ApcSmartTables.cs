namespace NutHub.Drivers.Serial.ApcSmart;

/// <summary>Variable flags of NUT drivers/apcsmart_tabs.h.</summary>
[Flags]
internal enum ApcVarFlags
{
    None = 0,

    /// <summary>Read on every poll (APC_POLL); the others are read at start-up and hourly.</summary>
    Poll = 1,

    /// <summary>A free-text EEPROM value of up to 8 characters, writable (APC_STRING).</summary>
    String = 2,

    /// <summary>Several APC commands feed this NUT variable, or one command several variables (APC_MULTI).</summary>
    Multi = 4,

    /// <summary>A comma-separated value spread over ambient.1.*, ambient.2.*... (APC_PACK).</summary>
    Pack = 8,
}

/// <summary>How an APC value becomes a NUT value (APC_F_* in NUT drivers/apcsmart_tabs.h).</summary>
internal enum ApcFormat
{
    Leave,
    Percent,
    Volt,
    Amp,
    Celsius,
    Hex,
    Dec,
    Seconds,
    Minutes,
    Hours,
    Reason,
}

/// <summary>An APC Smart variable: the command character that reads it and how to convert the reply.</summary>
internal sealed record ApcVariableDef(string Name, char Command, ApcVarFlags Flags, ApcFormat Format, string? Pattern = null);

/// <summary>A two-byte command variable of the newer SPM-style models.</summary>
internal sealed record ApcDualVariableDef(string Name, byte Prefix, byte Sub, ApcFormat Format);

/// <summary>An APC instant command.</summary>
/// <param name="ParameterPattern">Regular expression the parameter must match; null when no parameter is allowed.</param>
/// <param name="Repeat">Sent twice 1.3 s apart, as the UPS requires for the power commands.</param>
internal sealed record ApcCommandDef(string Name, string? ParameterPattern, char Command, bool Repeat);

/// <summary>
/// The tables of NUT drivers/apcsmart_tabs.c (table version 3.2): variables, instant commands, the command sets of old
/// firmware that cannot report them, and the dual-byte variables.
/// </summary>
internal static class ApcSmartTables
{
    public const char Status = 'Q';
    public const char GoSmart = 'Y';
    public const char GoDumb = 'R';
    public const char CommandSet = 'a';
    public const char Capabilities = '\u001A';
    public const char NextValue = '-';
    public const char FirmwareOld = 'V';
    public const char FirmwareNew = 'b';
    public const char GraceDown = '@';
    public const char SoftDown = 'S';
    public const char Shutdown = 'K';
    public const char Off = 'Z';
    public const char On = '\u000E';
    public const char SimulatePowerFail = 'U';

    /// <summary>Command characters the driver uses internally or ignores (APC_UNR_CMDS).</summary>
    public const string Unrecognised = "\u001A\u007F~')-+8QRYayz";

    /// <summary>The format of a command set reply to 'a': "version.alerts.commands[.extensions]".</summary>
    public const string CommandSetPattern = @"^[0-9]\.[^.]*\.[^.]+(\.[^.]+)?$";

    /// <summary>Variables, in order of preference for the MULTI ones.</summary>
    public static IReadOnlyList<ApcVariableDef> Variables { get; } =
    [
        new("ups.temperature", 'C', ApcVarFlags.Poll, ApcFormat.Celsius),
        new("ups.load", 'P', ApcVarFlags.Poll, ApcFormat.Percent),
        new("ups.test.interval", 'E', ApcVarFlags.None, ApcFormat.Hours),
        new("ups.test.result", 'X', ApcVarFlags.Poll, ApcFormat.Leave),
        new("ups.delay.start", 'r', ApcVarFlags.None, ApcFormat.Seconds),
        new("ups.delay.shutdown", 'p', ApcVarFlags.None, ApcFormat.Seconds),
        new("ups.id", 'c', ApcVarFlags.String, ApcFormat.Leave),
        new("ups.contacts", 'i', ApcVarFlags.Poll, ApcFormat.Hex),
        new("ups.display.language", '\u000C', ApcVarFlags.None, ApcFormat.Leave),
        new("input.voltage", 'L', ApcVarFlags.Poll, ApcFormat.Volt),
        new("input.frequency", 'F', ApcVarFlags.Poll, ApcFormat.Dec),
        new("input.sensitivity", 's', ApcVarFlags.None, ApcFormat.Leave),
        new("input.quality", '9', ApcVarFlags.Poll, ApcFormat.Hex),
        new("input.transfer.low", 'l', ApcVarFlags.None, ApcFormat.Volt),
        new("input.transfer.high", 'u', ApcVarFlags.None, ApcFormat.Volt),
        new("input.transfer.reason", 'G', ApcVarFlags.Poll, ApcFormat.Reason),
        new("input.voltage.maximum", 'M', ApcVarFlags.Poll, ApcFormat.Volt),
        new("input.voltage.minimum", 'N', ApcVarFlags.Poll, ApcFormat.Volt),
        new("output.current", '/', ApcVarFlags.Poll, ApcFormat.Amp),
        new("output.voltage", 'O', ApcVarFlags.Poll, ApcFormat.Volt),
        new("output.voltage.nominal", 'o', ApcVarFlags.None, ApcFormat.Volt),
        new("ambient.humidity", 'h', ApcVarFlags.Poll, ApcFormat.Percent),
        new("ambient.0.humidity", 'H', ApcVarFlags.Poll | ApcVarFlags.Pack, ApcFormat.Percent),
        new("ambient.0.humidity.high", '{', ApcVarFlags.Poll | ApcVarFlags.Pack, ApcFormat.Percent),
        new("ambient.0.humidity.low", '}', ApcVarFlags.Poll | ApcVarFlags.Pack, ApcFormat.Percent),
        new("ambient.temperature", 't', ApcVarFlags.Poll, ApcFormat.Celsius),
        new("ambient.0.temperature", 'T', ApcVarFlags.Multi | ApcVarFlags.Poll | ApcVarFlags.Pack, ApcFormat.Celsius, @"^[0-9]{2}\.[0-9]{2}$"),
        new("ambient.0.temperature.high", '[', ApcVarFlags.Poll | ApcVarFlags.Pack, ApcFormat.Celsius),
        new("ambient.0.temperature.low", ']', ApcVarFlags.Poll | ApcVarFlags.Pack, ApcFormat.Celsius),
        new("battery.date", 'x', ApcVarFlags.String, ApcFormat.Leave),
        new("battery.charge", 'f', ApcVarFlags.Poll, ApcFormat.Percent),
        new("battery.charge.restart", 'e', ApcVarFlags.None, ApcFormat.Percent),
        new("battery.voltage", 'B', ApcVarFlags.Poll, ApcFormat.Volt),
        new("battery.voltage.nominal", 'g', ApcVarFlags.None, ApcFormat.Leave),
        new("battery.runtime", 'j', ApcVarFlags.Poll, ApcFormat.Minutes),
        new("battery.runtime.low", 'q', ApcVarFlags.None, ApcFormat.Minutes),
        new("battery.packs", '>', ApcVarFlags.None, ApcFormat.Dec),
        new("battery.packs.bad", '<', ApcVarFlags.None, ApcFormat.Dec),
        new("battery.alarm.threshold", 'k', ApcVarFlags.None, ApcFormat.Leave),
        new("device.uptime", 'T', ApcVarFlags.Multi | ApcVarFlags.Poll, ApcFormat.Hours, @"^[0-9]{3}\.[0-9]{1}$"),
        new("ups.serial", 'n', ApcVarFlags.None, ApcFormat.Leave),
        new("ups.mfr.date", 'm', ApcVarFlags.None, ApcFormat.Leave),
        new("ups.model", '\u0001', ApcVarFlags.None, ApcFormat.Leave),
        new("ups.firmware.aux", 'v', ApcVarFlags.None, ApcFormat.Leave),
        new("ups.firmware", 'b', ApcVarFlags.Multi, ApcFormat.Leave, @"^[A-Za-z0-9]+\.[A-Za-z0-9]+\.[A-Za-z0-9]+$"),
        new("ups.firmware", 'V', ApcVarFlags.Multi, ApcFormat.Leave),
    ];

    /// <summary>Dual-byte variables announced after the command list of 'a' as "prefix:subcommands".</summary>
    public static IReadOnlyList<ApcDualVariableDef> DualVariables { get; } =
    [
        new("input.frequency", 0x9F, 0xD3, ApcFormat.Dec),
        new("battery.current", 0x9F, 0xD4, ApcFormat.Amp),
    ];

    /// <summary>Instant commands; shutdown.return exists twice, told apart by the parameter.</summary>
    public static IReadOnlyList<ApcCommandDef> Commands { get; } =
    [
        new("shutdown.return", "^[Aa][Tt]:[0-9]{1,3}$", GraceDown, Repeat: false),
        new("shutdown.return", "^([Cc][Ss]|)$", SoftDown, Repeat: false),
        new("shutdown.stayoff", null, Shutdown, Repeat: true),
        new("load.off", null, Off, Repeat: true),
        new("load.on", null, On, Repeat: true),
        new("calibrate.start", null, 'D', Repeat: false),
        new("calibrate.stop", null, 'D', Repeat: false),
        new("test.panel.start", null, 'A', Repeat: false),
        new("test.failure.start", null, SimulatePowerFail, Repeat: false),
        new("test.battery.start", null, 'W', Repeat: false),
        new("test.battery.stop", null, 'W', Repeat: false),
        new("bypass.start", null, '^', Repeat: false),
        new("bypass.stop", null, '^', Repeat: false),
    ];

    /// <summary>Firmware revisions of UPSes that do not answer 'a', with their command characters.</summary>
    public static IReadOnlyDictionary<string, string> Compatibility { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        // Matrix-UPS
        ["0XI"] = "@789ABCDEFGKLMNOPQRSTUVWXYZcefgjklmnopqrsuwxz/<>\\^\u000C\u0016",
        ["0XM"] = "@789ABCDEFGKLMNOPQRSTUVWXYZcefgjklmnopqrsuwxz/<>\\^\u000C\u0016",
        ["0ZI"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz/<>",
        ["5UI"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz/<>",
        ["5ZM"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz/<>",

        // APC600
        ["6QD"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",
        ["6QI"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",
        ["6TD"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",
        ["6TI"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",

        // Smart-UPS 900
        ["7QD"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",
        ["7QI"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",
        ["7TD"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",
        ["7TI"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",

        // Smart-UPS 600I, 900I, 2000I, 1250I
        ["6JI"] = "@789ABCFGKLMNOPQSTUVWXYZfg",
        ["7II"] = "@79ABCEFGKLMNOPQRSUVWXYZcfg",
        ["9II"] = "@79ABCEFGKLMNOPQRSUVWXYZcfg",
        ["9GI"] = "@79ABCEFGKLMNOPQRSUVWXYZcfg",
        ["8II"] = "@79ABCEFGKLMNOPQRSUVWXYZcfg",
        ["8GI"] = "@79ABCEFGKLMNOPQRSUVWXYZfg",

        // Smart-UPS 1250
        ["8QD"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",
        ["8QI"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",
        ["8TD"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",
        ["8TI"] = "@79ABCDEFGKLMNOPQRSUVWXYZcefgjklmnopqrsuxz",

        // CS 350
        ["5.4.D"] = "@\u0001ABPQRSUYbdfgjmnx9",

        // Old APC 600 models that return a voltage above 255 V through 'b' (matched as a fake key).
        ["set\u0001"] = "@789ABCFGKLMNOPQRSUVWXYZ",
    };

    /// <summary>The text for the transfer reason letter of 'G'.</summary>
    public static string TransferReason(string value) => value.Length == 0 ? value : value[0] switch
    {
        'R' => "unacceptable utility voltage rate of change",
        'H' => "high utility voltage",
        'L' => "low utility voltage",
        'T' => "line voltage notch or spike",
        'O' => "no transfers yet since turnon",
        'S' => "simulated power failure or UPS test",
        _ => value,
    };
}
