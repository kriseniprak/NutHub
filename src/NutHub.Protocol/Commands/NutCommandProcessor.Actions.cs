using Microsoft.Extensions.Logging;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Protocol.Parsing;
using NutHub.Protocol.Security;
using NutHub.Protocol.Tracking;

namespace NutHub.Protocol.Commands;

// Operations on the device: SET VAR / SET TRACKING (server/netset.c) and INSTCMD (server/netinstcmd.c).
internal sealed partial class NutCommandProcessor
{
    private ValueTask<NutReply> SetAsync(NutClientState client, ArraySegment<string> a, CancellationToken cancellationToken)
    {
        if (a.Count < 2)
        {
            return new(InvalidArgument);
        }

        string sub = NutText.AsciiUpper(a[0]);
        if (sub == "VAR")
        {
            // upsd ignores words after the value; NutHub refuses them, because "SET VAR ups ups.id My UPS" without
            // quotes would otherwise silently write "My".
            return a.Count == 4
                ? SetVariableAsync(client, a[1], a[2], a[3], cancellationToken)
                : new(InvalidArgument);
        }

        if (sub == "TRACKING")
        {
            // Needs USERNAME and PASSWORD (checked by the caller) but, as in upsd, no particular right.
            switch (NutText.AsciiUpper(a[1]))
            {
                case "ON":
                    client.Tracking = true;
                    return new(NutReply.Ok);
                case "OFF":
                    client.Tracking = false;
                    return new(NutReply.Ok);
                default:
                    return new(InvalidArgument);
            }
        }

        return new(InvalidArgument);
    }

    private async ValueTask<NutReply> SetVariableAsync(NutClientState client, string ups, string name, string value,
                                                       CancellationToken cancellationToken)
    {
        if (!TryFind(ups, requireData: true, out UpsUnit? unit, out _, out NutReply error))
        {
            return error;
        }

        if (!_authorizer.Authorize(client, NutRight.SetVariable, unit, null, $"SET VAR {unit.Name} {name}"))
        {
            return NutReply.Error(NutErrors.AccessDenied);
        }

        UpsSnapshot snapshot = unit.Snapshot;
        string? invalid = ValidateWrite(snapshot, name, value, out string? canonical);
        if (invalid is not null)
        {
            return NutReply.Error(invalid);
        }

        CommandOrigin origin = Origin(client);
        if (client.Tracking)
        {
            string id = _tracking.Create();
            StartTracked(id, token => unit.SetVariableAsync(canonical!, value, origin, token));
            return NutReply.Line("OK TRACKING " + id);
        }

        CommandResult result = await unit.SetVariableAsync(canonical!, value, origin, _serverStopping)
                                         .WaitAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NutReply.Ok : NutReply.Error(ErrorOf(result.Status, isSet: true));
    }

    /// <summary>
    /// The checks of upsd's set_var, in its order: the variable exists, is writable (READONLY), fits a STRING
    /// (TOO-LONG), is one of the ENUM values, lies in a RANGE (INVALID-VALUE); then NutHub's own check that a
    /// plain number is a number. Returns the NUT error, or null when the write can go to the driver.
    /// </summary>
    internal static string? ValidateWrite(UpsSnapshot snapshot, string name, string value, out string? canonical)
    {
        if (!SnapshotLookup.TryGetVariable(snapshot, name, out canonical, out _))
        {
            return NutErrors.VarNotSupported;
        }

        VariableInfo info = snapshot.GetInfo(canonical);
        if (!info.Writable)
        {
            return NutErrors.ReadOnly;
        }

        // MaxLength 0 means "not specified" in NutHub (upsd would refuse the write as a driver error).
        if (info.Type == VariableType.String && info.MaxLength > 0 && value.Length > info.MaxLength)
        {
            return NutErrors.TooLong;
        }

        if (info.EnumValues.Count > 0 && !info.EnumValues.Contains(value, StringComparer.Ordinal))
        {
            return NutErrors.InvalidValue;
        }

        if (info.Ranges.Count > 0 &&
            !(NutFormat.TryParseNumber(value, out double number) && info.Ranges.Any(r => r.Contains(number))))
        {
            return NutErrors.InvalidValue;
        }

        CommandResult? core = UpsUnit.CheckWritable(snapshot, canonical, value);
        return core is null ? null : ErrorOf(core.Status, isSet: true);
    }

    private async ValueTask<NutReply> InstantCommandAsync(NutClientState client, ArraySegment<string> a,
                                                          CancellationToken cancellationToken)
    {
        // upsd silently drops the parameter when more words follow it; running a command such as load.off.delay
        // without the delay the client asked for is worse than refusing the request.
        if (a.Count is < 2 or > 3)
        {
            return InvalidArgument;
        }

        if (!TryFind(a[0], requireData: true, out UpsUnit? unit, out UpsSnapshot? snapshot, out NutReply error))
        {
            return error;
        }

        string? command = SnapshotLookup.FindCommand(snapshot, a[1]);
        if (command is null)
        {
            return NutReply.Error(NutErrors.CmdNotSupported);
        }

        if (!_authorizer.Authorize(client, NutRight.InstantCommand, unit, command, $"INSTCMD {unit.Name} {command}"))
        {
            return NutReply.Error(NutErrors.AccessDenied);
        }

        string? parameter = a.Count == 3 ? a[2] : null;
        CommandOrigin origin = Origin(client);
        if (client.Tracking)
        {
            string id = _tracking.Create();
            StartTracked(id, token => unit.InstantCommandAsync(command, parameter, origin, token));
            return NutReply.Line("OK TRACKING " + id);
        }

        CommandResult result = await unit.InstantCommandAsync(command, parameter, origin, _serverStopping)
                                         .WaitAsync(cancellationToken).ConfigureAwait(false);
        return result.IsSuccess ? NutReply.Ok : NutReply.Error(ErrorOf(result.Status, isSet: false));
    }

    /// <summary>
    /// Runs a tracked operation in the background; its result is stored for GET TRACKING. It runs on the thread
    /// pool so that a driver that blocks cannot delay the "OK TRACKING" answer.
    /// </summary>
    private void StartTracked(string id, Func<CancellationToken, Task<CommandResult>> operation)
    {
        _ = Task.Run(async () =>
        {
            string result;
            try
            {
                result = TrackingStore.ResultOf(await operation(_serverStopping).ConfigureAwait(false));
            }
            catch (OperationCanceledException)
            {
                result = "ERR FAILED";
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "The tracked NUT operation {Id} failed.", id);
                result = "ERR FAILED";
            }

            _tracking.Complete(id, result);
        });
    }

    /// <summary>The NUT error word for a failed instant command (<paramref name="isSet"/> false) or write.</summary>
    internal static string ErrorOf(CommandStatus status, bool isSet) => status switch
    {
        CommandStatus.UnknownUps => NutErrors.UnknownUps,
        CommandStatus.NotSupported => isSet ? NutErrors.VarNotSupported : NutErrors.CmdNotSupported,
        CommandStatus.ReadOnly => NutErrors.ReadOnly,
        CommandStatus.InvalidValue => NutErrors.InvalidValue,
        CommandStatus.TooLong => NutErrors.TooLong,
        CommandStatus.InvalidArgument => NutErrors.InvalidArgument,
        CommandStatus.DriverNotConnected => NutErrors.DriverNotConnected,
        CommandStatus.AccessDenied => NutErrors.AccessDenied,
        _ => isSet ? NutErrors.SetFailed : NutErrors.InstCmdFailed,
    };
}
