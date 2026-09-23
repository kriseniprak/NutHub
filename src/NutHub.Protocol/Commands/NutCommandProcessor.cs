using System.Diagnostics.CodeAnalysis;
using System.Globalization;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Protocol.Parsing;
using NutHub.Protocol.Security;
using NutHub.Protocol.Server;
using NutHub.Protocol.Tracking;

namespace NutHub.Protocol.Commands;

/// <summary>
/// Executes NUT protocol requests, reproducing upsd (server/netcmds.h and the net*.c handlers): the same answers,
/// error words and error precedence, so that upsmon, upsc, upsrw, upscmd and the other clients see no difference.
/// Deliberate differences are commented where they are made.
/// </summary>
/// <remarks>
/// One instance serves every connection; the per-connection state is in <see cref="NutClientState"/>.
/// Instant commands and variable writes run with the server's lifetime token, not the connection's, so that a
/// client disconnecting does not abort an operation half-way on the device.
/// </remarks>
internal sealed partial class NutCommandProcessor
{
    /// <summary>The HELP answer of upsd 2.8.3 and later (PRIMARY/MASTER and FSD are not listed there either).</summary>
    internal const string HelpText =
        "Commands: HELP VER PROTVER GET LIST SET INSTCMD LOGIN LOGOUT USERNAME PASSWORD STARTTLS";

    private static readonly NutReply InvalidArgument = NutReply.Error(NutErrors.InvalidArgument);

    private readonly IConfigStore _config;
    private readonly IUpsRegistry _registry;
    private readonly NutSessionRegistry _sessions;
    private readonly EventHub _hub;
    private readonly NutAuthorizer _authorizer;
    private readonly TrackingStore _tracking;
    private readonly NutTlsCertificates _tls;
    private readonly TimeProvider _time;
    private readonly LogRateLimiter _warnings;
    private readonly ILogger _logger;
    private readonly CancellationToken _serverStopping;

    public NutCommandProcessor(IConfigStore config, IUpsRegistry registry, NutSessionRegistry sessions, EventHub hub,
                               NutAuthorizer authorizer, TrackingStore tracking, NutTlsCertificates tls,
                               TimeProvider time, LogRateLimiter warnings, ILogger logger,
                               CancellationToken serverStopping)
    {
        _config = config;
        _registry = registry;
        _sessions = sessions;
        _hub = hub;
        _authorizer = authorizer;
        _tracking = tracking;
        _tls = tls;
        _time = time;
        _warnings = warnings;
        _logger = logger;
        _serverStopping = serverStopping;
    }

    /// <summary>
    /// Executes one request (its words, as split by <see cref="NutLineParser"/>) and returns the answer.
    /// </summary>
    /// <param name="cancellationToken">The connection's token: cancelled when the client goes away.</param>
    public ValueTask<NutReply> ExecuteAsync(NutClientState client, string[] words, CancellationToken cancellationToken)
    {
        // upsd answers an empty request (a bare line feed) with UNKNOWN-COMMAND too.
        if (words.Length == 0)
        {
            return new(NutReply.Error(NutErrors.UnknownCommand));
        }

        string command = NutText.AsciiUpper(words[0]);
        var args = new ArraySegment<string>(words, 1, words.Length - 1);
        if (_logger.IsEnabled(LogLevel.Trace))
        {
            _logger.LogTrace("NUT connection {Id}: {Request}", client.Session.Id,
                             command == "PASSWORD" ? "PASSWORD ****" : string.Join(' ', words));
        }

        switch (command)
        {
            case "VER":
                return new(args.Count != 0 ? InvalidArgument : NutReply.Line(VersionText()));
            case "NETVER":
            case "PROTVER":
                return new(args.Count != 0 ? InvalidArgument : NutReply.Line(NutHubInfo.ProtocolVersion));
            case "HELP":
                return new(args.Count != 0 ? InvalidArgument : NutReply.Line(HelpText));
            case "STARTTLS":
                return new(StartTls(client));
            case "GET":
                return new(Get(client, args));
            case "LIST":
                return new(List(args));
            case "USERNAME":
                return new(Username(client, args));
            case "PASSWORD":
                return new(Password(client, args));
            case "LOGOUT":
                return new(args.Count != 0 ? InvalidArgument : new NutReply("OK Goodbye\n", NutReplyAction.Close));
            case "LOGIN":
            case "PRIMARY":
            case "MASTER":
            case "FSD":
            case "SET":
            case "INSTCMD":
                break;
            default:
                return new(NutReply.Error(NutErrors.UnknownCommand));
        }

        // The commands flagged FLAG_USER in upsd need USERNAME and PASSWORD before anything else, even their
        // arguments, is looked at. The credentials themselves are verified later, by the command.
        if (client.Username is null)
        {
            return new(NutReply.Error(NutErrors.UsernameRequired));
        }

        if (client.Password is null)
        {
            return new(NutReply.Error(NutErrors.PasswordRequired));
        }

        return command switch
        {
            "LOGIN" => new(Login(client, args)),
            "PRIMARY" => new(Primary(client, args, "PRIMARY", "OK PRIMARY-GRANTED")),
            "MASTER" => new(Primary(client, args, "MASTER", "OK MASTER-GRANTED")),
            "FSD" => new(ForcedShutdown(client, args)),
            "SET" => SetAsync(client, args, cancellationToken),
            _ => InstantCommandAsync(client, args, cancellationToken),
        };
    }

    /// <summary>The VER answer: the configured text, or one naming NutHub and the protocol it speaks.</summary>
    private string VersionText()
    {
        string? custom = _config.Current.Nut.VersionString;
        string text = string.IsNullOrWhiteSpace(custom)
            ? $"{NutHubInfo.ProductName} {NutHubInfo.Version} (Network UPS Tools protocol {NutHubInfo.ProtocolVersion} compatible) - {NutHubInfo.ProjectUrl}"
            : custom.Trim();
        return SingleLine(text);
    }

    /// <summary>
    /// Finds a UPS by name (ignoring case, like upsd's get_ups_ptr). With <paramref name="requireData"/>, also
    /// refuses a UPS whose data cannot be served, like upsd's ups_available: DRIVER-NOT-CONNECTED when no driver
    /// runs, DATA-STALE when the driver has no fresh data.
    /// </summary>
    private bool TryFind(string name, bool requireData, [NotNullWhen(true)] out UpsUnit? unit,
                         [NotNullWhen(true)] out UpsSnapshot? snapshot, out NutReply error)
    {
        unit = _registry.Find(name);
        if (unit is null)
        {
            snapshot = null;
            error = NutReply.Error(NutErrors.UnknownUps);
            return false;
        }

        snapshot = unit.Snapshot;
        string? availability = requireData ? SnapshotLookup.AvailabilityError(snapshot) : null;
        if (availability is not null)
        {
            error = NutReply.Error(availability);
            unit = null;
            snapshot = null;
            return false;
        }

        error = default;
        return true;
    }

    private static CommandOrigin Origin(NutClientState client) => CommandOrigin.Nut(client.Username, client.AddressText);

    private static string Invariant(FormattableString text) => text.ToString(CultureInfo.InvariantCulture);

    private static string SingleLine(string text)
    {
        if (!text.Any(char.IsControl))
        {
            return text;
        }

        return new string(text.Select(c => char.IsControl(c) ? ' ' : c).ToArray());
    }
}
