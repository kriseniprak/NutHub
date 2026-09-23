using System.Security.Cryptography.X509Certificates;

namespace NutHub.Protocol.Commands;

/// <summary>What the connection must do after sending a reply.</summary>
internal enum NutReplyAction
{
    None,

    /// <summary>Close the connection gracefully (LOGOUT).</summary>
    Close,

    /// <summary>Switch the connection to TLS with <see cref="NutReply.Certificate"/> (STARTTLS).</summary>
    StartTls,
}

/// <summary>The answer to one request: complete lines, each ending with "\n".</summary>
internal readonly record struct NutReply(string Text, NutReplyAction Action = NutReplyAction.None,
                                         X509Certificate2? Certificate = null)
{
    public static readonly NutReply Ok = new("OK\n");

    public static NutReply Line(string line) => new(line + "\n");

    public static NutReply Error(string code) => new("ERR " + code + "\n");
}

/// <summary>The error words of the NUT protocol (<c>ERR &lt;word&gt;</c>), as defined by upsd's neterr.h.</summary>
internal static class NutErrors
{
    public const string AccessDenied = "ACCESS-DENIED";
    public const string UnknownUps = "UNKNOWN-UPS";
    public const string VarNotSupported = "VAR-NOT-SUPPORTED";
    public const string CmdNotSupported = "CMD-NOT-SUPPORTED";
    public const string InvalidArgument = "INVALID-ARGUMENT";
    public const string InstCmdFailed = "INSTCMD-FAILED";
    public const string SetFailed = "SET-FAILED";
    public const string ReadOnly = "READONLY";
    public const string TooLong = "TOO-LONG";
    public const string FeatureNotSupported = "FEATURE-NOT-SUPPORTED";
    public const string FeatureNotConfigured = "FEATURE-NOT-CONFIGURED";
    public const string AlreadySslMode = "ALREADY-SSL-MODE";
    public const string DriverNotConnected = "DRIVER-NOT-CONNECTED";
    public const string DataStale = "DATA-STALE";
    public const string AlreadyLoggedIn = "ALREADY-LOGGED-IN";
    public const string InvalidPassword = "INVALID-PASSWORD";
    public const string AlreadySetPassword = "ALREADY-SET-PASSWORD";
    public const string InvalidUsername = "INVALID-USERNAME";
    public const string AlreadySetUsername = "ALREADY-SET-USERNAME";
    public const string UsernameRequired = "USERNAME-REQUIRED";
    public const string PasswordRequired = "PASSWORD-REQUIRED";
    public const string UnknownCommand = "UNKNOWN-COMMAND";
    public const string InvalidValue = "INVALID-VALUE";
}
