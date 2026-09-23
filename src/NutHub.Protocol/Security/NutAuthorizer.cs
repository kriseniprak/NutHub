using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Protocol.Commands;
using NutHub.Protocol.Server;

namespace NutHub.Protocol.Security;

/// <summary>The rights a privileged NUT command needs (the upsd.users "upsmon", "actions" and "instcmds").</summary>
internal enum NutRight
{
    /// <summary>LOGIN: "upsmon secondary" or "upsmon primary".</summary>
    Login,

    /// <summary>PRIMARY / MASTER: "upsmon primary".</summary>
    Primary,

    /// <summary>FSD: "upsmon primary" or the FSD action.</summary>
    ForcedShutdown,

    /// <summary>SET VAR: the SET action.</summary>
    SetVariable,

    /// <summary>INSTCMD: the command, or ALL, in the instant commands.</summary>
    InstantCommand,
}

/// <summary>
/// Checks the credentials a client gave with USERNAME / PASSWORD against the NUT accounts, at the moment a
/// privileged command needs them, as upsd does (user.c). Account names are case-sensitive; action and command
/// names are not. On top of upsd, an account can be limited to some UPSes and failed checks are throttled.
/// </summary>
internal sealed class NutAuthorizer
{
    private readonly IConfigStore _config;
    private readonly IPasswordHasher _hasher;
    private readonly NutSessionRegistry _sessions;
    private readonly LoginThrottle _throttle;
    private readonly LogRateLimiter _warnings;
    private readonly ILogger _logger;
    private readonly Lazy<string> _dummyHash;

    public NutAuthorizer(IConfigStore config, IPasswordHasher hasher, NutSessionRegistry sessions,
                         LoginThrottle throttle, LogRateLimiter warnings, ILogger logger)
    {
        _config = config;
        _hasher = hasher;
        _sessions = sessions;
        _throttle = throttle;
        _warnings = warnings;
        _logger = logger;

        // Verifying against a throwaway hash when the account does not exist costs the same time as a real check,
        // so response times do not reveal which account names exist.
        _dummyHash = new Lazy<string>(() => hasher.Hash(Guid.NewGuid().ToString("N")));
    }

    /// <summary>
    /// Whether the client may run <paramref name="request"/>. Logs the reason of a refusal (never the password).
    /// The caller answers ERR ACCESS-DENIED when this returns false.
    /// </summary>
    /// <param name="client">The connection; USERNAME and PASSWORD must have been given.</param>
    /// <param name="right">The right needed.</param>
    /// <param name="ups">The UPS the command concerns.</param>
    /// <param name="command">For <see cref="NutRight.InstantCommand"/>, the instant command.</param>
    /// <param name="request">What was asked, for the log: "INSTCMD rack1 load.off".</param>
    public bool Authorize(NutClientState client, NutRight right, UpsUnit ups, string? command, string request)
    {
        string username = client.Username ?? "";
        string password = client.Password ?? "";

        if (_throttle.IsLockedOut(client.Address))
        {
            return Deny(client, request, "too many failed password checks from this address");
        }

        NutUserConfig? user = FindUser(username);
        bool passwordOk = _hasher.Verify(password, user?.PasswordHash ?? _dummyHash.Value);
        if (user is null || !passwordOk)
        {
            _throttle.RecordFailure(client.Address);
            return Deny(client, request, user is null ? "unknown account" : "wrong password");
        }

        if (!client.CredentialsVerified)
        {
            client.CredentialsVerified = true;
            _sessions.SetUser(client.Session, user.Name);
        }

        if (!HasRight(user, right, command))
        {
            return Deny(client, request, $"the account lacks the right ({Describe(right)})");
        }

        if (user.AllowedUps.Count > 0 &&
            !user.AllowedUps.Any(u => string.Equals(u, ups.Name, StringComparison.OrdinalIgnoreCase)))
        {
            return Deny(client, request, $"the account may not use the UPS {ups.Name}");
        }

        return true;
    }

    internal static bool HasRight(NutUserConfig user, NutRight right, string? command) => right switch
    {
        NutRight.Login => user.Monitor is NutMonitorRole.Secondary or NutMonitorRole.Primary,
        NutRight.Primary => user.Monitor == NutMonitorRole.Primary,
        NutRight.ForcedShutdown => user.Monitor == NutMonitorRole.Primary || HasAction(user, "FSD"),
        NutRight.SetVariable => HasAction(user, "SET"),
        NutRight.InstantCommand => command is not null &&
                                   user.InstantCommands.Any(c => string.Equals(c, "ALL", StringComparison.OrdinalIgnoreCase) ||
                                                                 string.Equals(c, command, StringComparison.OrdinalIgnoreCase)),
        _ => false,
    };

    private NutUserConfig? FindUser(string username)
    {
        foreach (NutUserConfig user in _config.Current.NutUsers)
        {
            if (string.Equals(user.Name, username, StringComparison.Ordinal))
            {
                return user;
            }
        }

        return null;
    }

    private static bool HasAction(NutUserConfig user, string action) =>
        user.Actions.Any(a => string.Equals(a, action, StringComparison.OrdinalIgnoreCase));

    private static string Describe(NutRight right) => right switch
    {
        NutRight.Login => "upsmon secondary or primary",
        NutRight.Primary => "upsmon primary",
        NutRight.ForcedShutdown => "upsmon primary or the FSD action",
        NutRight.SetVariable => "the SET action",
        _ => "the instant command",
    };

    /// <summary>
    /// Logs a refusal as a warning, once a minute per client, account, request and reason (a misconfigured upsmon
    /// retries every few seconds); the repeats go to the debug level.
    /// </summary>
    private bool Deny(NutClientState client, string request, string reason)
    {
        LogLevel level = _warnings.ShouldLog($"deny|{client.AddressText}|{client.Username}|{request}|{reason}")
            ? LogLevel.Warning
            : LogLevel.Debug;
        _logger.Log(level, "NUT client {Address} (account '{User}') was refused {Request}: {Reason}.",
                    client.AddressText, client.Username, request, reason);
        return false;
    }
}
