namespace NutHub.Core.Model;

/// <summary>The outcome of an instant command or of a variable write.</summary>
public enum CommandStatus
{
    Success,

    /// <summary>No UPS with that name (NUT: UNKNOWN-UPS).</summary>
    UnknownUps,

    /// <summary>The UPS does not have that command / variable (NUT: CMD-NOT-SUPPORTED / VAR-NOT-SUPPORTED).</summary>
    NotSupported,

    /// <summary>The variable exists but cannot be written (NUT: READONLY).</summary>
    ReadOnly,

    /// <summary>The value is not accepted: not in the enum, out of range, not a number (NUT: INVALID-VALUE).</summary>
    InvalidValue,

    /// <summary>The string value exceeds the maximum length (NUT: TOO-LONG).</summary>
    TooLong,

    /// <summary>A missing or malformed argument (NUT: INVALID-ARGUMENT).</summary>
    InvalidArgument,

    /// <summary>The driver is not running or not connected to the device (NUT: DRIVER-NOT-CONNECTED).</summary>
    DriverNotConnected,

    /// <summary>The caller lacks the permission (NUT: ACCESS-DENIED).</summary>
    AccessDenied,

    /// <summary>The device refused or did not answer (NUT: INSTCMD-FAILED / SET-FAILED).</summary>
    Failed,
}

/// <summary>The outcome of an instant command or of a variable write, with an optional detail message.</summary>
public sealed record CommandResult(CommandStatus Status, string? Message = null)
{
    public static readonly CommandResult Ok = new(CommandStatus.Success);

    public bool IsSuccess => Status == CommandStatus.Success;

    public static CommandResult Fail(string message) => new(CommandStatus.Failed, message);

    public static CommandResult NotSupported(string? message = null) => new(CommandStatus.NotSupported, message);

    public static CommandResult Invalid(string message) => new(CommandStatus.InvalidValue, message);

    public static CommandResult InvalidArgument(string message) => new(CommandStatus.InvalidArgument, message);

    public static CommandResult NotConnected(string? message = null) =>
        new(CommandStatus.DriverNotConnected, message);

    public override string ToString() => Message is null ? Status.ToString() : $"{Status}: {Message}";
}

/// <summary>Who asked for a command, a variable write or a configuration change; recorded in the event log.</summary>
public sealed record CommandOrigin(string Source, string? User = null, string? Address = null)
{
    public static readonly CommandOrigin System = new("system");

    public static CommandOrigin Web(string? user, string? address) => new("web", user, address);

    public static CommandOrigin Nut(string? user, string? address) => new("nut", user, address);

    /// <summary>"web:admin@192.168.1.10", "nut:upsmon@10.0.0.5", "system", "host-protection".</summary>
    public override string ToString()
    {
        if (User is null && Address is null)
        {
            return Source;
        }

        string who = User ?? "anonymous";
        return Address is null ? $"{Source}:{who}" : $"{Source}:{who}@{Address}";
    }
}
