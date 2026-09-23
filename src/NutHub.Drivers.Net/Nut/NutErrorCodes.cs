using NutHub.Core.Model;

namespace NutHub.Drivers.Net.Nut;

/// <summary>
/// Translates the error codes of an upstream NUT server (docs/net-protocol.txt, "Error responses") into NutHub
/// results and readable explanations.
/// </summary>
internal static class NutErrorCodes
{
    public const string DataStale = "DATA-STALE";
    public const string DriverNotConnected = "DRIVER-NOT-CONNECTED";
    public const string UnknownUps = "UNKNOWN-UPS";
    public const string AccessDenied = "ACCESS-DENIED";

    /// <summary>
    /// The result of a command or variable write the upstream server refused. UNKNOWN-UPS becomes "not connected"
    /// rather than "unknown UPS": the UPS exists here, it is its upstream source that is missing, and a NUT client
    /// of NutHub must not be told the UPS it is talking to does not exist.
    /// </summary>
    public static CommandResult ToCommandResult(NutErrorException error, string upstream)
    {
        string suffix = $" (upstream {upstream}: ERR {error.Code})";
        return error.Code switch
        {
            UnknownUps => new CommandResult(CommandStatus.DriverNotConnected,
                                            "The upstream server does not know this UPS" + suffix),
            "CMD-NOT-SUPPORTED" => CommandResult.NotSupported("The upstream UPS does not support this command" + suffix),
            "VAR-NOT-SUPPORTED" => CommandResult.NotSupported("The upstream UPS does not have this variable" + suffix),
            "READONLY" => new CommandResult(CommandStatus.ReadOnly, "The variable is read-only upstream" + suffix),
            "INVALID-VALUE" => CommandResult.Invalid("The upstream server rejected the value" + suffix),
            "TOO-LONG" => new CommandResult(CommandStatus.TooLong, "The value is too long for the upstream server" + suffix),
            "INVALID-ARGUMENT" => CommandResult.InvalidArgument("The upstream server rejected the argument" + suffix),
            DriverNotConnected or DataStale => CommandResult.NotConnected(
                "The upstream driver is not connected to the UPS or its data is stale" + suffix),
            AccessDenied or "USERNAME-REQUIRED" or "PASSWORD-REQUIRED" or "INVALID-USERNAME" or "INVALID-PASSWORD" =>
                new CommandResult(CommandStatus.AccessDenied,
                                  "The upstream server refused the credentials; check the username, the password and " +
                                  "the actions and instcmds granted in its upsd.users" + suffix),
            "INSTCMD-FAILED" or "SET-FAILED" or "FAILED" or "UNKNOWN" =>
                CommandResult.Fail("The upstream UPS did not execute the request" + suffix),
            _ => CommandResult.Fail("The upstream server refused the request" + suffix),
        };
    }

    /// <summary>Why the data of the UPS is unavailable when LIST VAR fails with <paramref name="error"/>.</summary>
    public static string DescribeReadFailure(NutErrorException error, string upsName, string upstream) =>
        error.Code switch
        {
            DataStale => $"The upstream server {upstream} reports stale data for '{upsName}' (ERR DATA-STALE).",
            DriverNotConnected =>
                $"The driver of '{upsName}' on {upstream} is not connected to the UPS (ERR DRIVER-NOT-CONNECTED).",
            UnknownUps => $"The upstream server {upstream} has no UPS named '{upsName}' (ERR UNKNOWN-UPS).",
            AccessDenied => $"The upstream server {upstream} does not allow this machine to read '{upsName}' (ERR ACCESS-DENIED).",
            _ => $"The upstream server {upstream} refused to list the variables of '{upsName}' (ERR {error.Code}).",
        };
}
