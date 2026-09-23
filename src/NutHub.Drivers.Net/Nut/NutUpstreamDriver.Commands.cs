using Microsoft.Extensions.Logging;
using NutHub.Core.Model;
using NutHub.Drivers.Net.Common;

namespace NutHub.Drivers.Net.Nut;

/// <summary>Forwarding of instant commands and variable writes to the upstream server.</summary>
internal sealed partial class NutUpstreamDriver
{
    /// <summary>How often GET TRACKING asks whether a tracked command has completed.</summary>
    internal TimeSpan TrackingPollInterval { get; init; } = TimeSpan.FromMilliseconds(250);

    /// <summary>
    /// How long to wait for a tracked command to complete. upsd forwards the command to its driver at once, but
    /// the driver runs it between two polls of a possibly slow device.
    /// </summary>
    internal TimeSpan TrackingTimeout { get; init; } = TimeSpan.FromSeconds(15);

    public Task<CommandResult> InstantCommandAsync(string command, string? parameter, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (!IsWord(command))
        {
            return Task.FromResult(CommandResult.InvalidArgument($"'{command}' is not a valid command name."));
        }

        string? argument = string.IsNullOrWhiteSpace(parameter) ? null : parameter.Trim();
        if (argument is not null && !NutLine.IsTransmittable(argument))
        {
            return Task.FromResult(CommandResult.InvalidArgument("The parameter contains control characters."));
        }

        string line = argument is null
            ? NutLine.Build("INSTCMD", _settings.RemoteUps, command)
            : NutLine.Build("INSTCMD", _settings.RemoteUps, command, argument);
        return ForwardAsync(line, $"command {command}", cancellationToken);
    }

    public Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        if (!IsWord(name))
        {
            return Task.FromResult(CommandResult.InvalidArgument($"'{name}' is not a valid variable name."));
        }

        if (!NutLine.IsTransmittable(value))
        {
            return Task.FromResult(CommandResult.Invalid("The value contains control characters."));
        }

        string line = NutLine.Build("SET VAR", _settings.RemoteUps, name) + " " + NutLine.Quote(value, always: true);
        return ForwardAsync(line, $"write of {name}", cancellationToken);
    }

    private async Task<CommandResult> ForwardAsync(string line, string what, CancellationToken cancellationToken)
    {
        if (!_settings.HasCredentials)
        {
            return new CommandResult(CommandStatus.AccessDenied,
                                     $"The NUT server {Upstream} needs a username and a password for this; set them " +
                                     "in the options of this UPS.");
        }

        NutUpstreamSession? session = CurrentSession;
        if (session is null || session.Connection.IsBroken)
        {
            return CommandResult.NotConnected($"Not connected to the NUT server {Upstream}.");
        }

        if (!session.Authenticated)
        {
            return new CommandResult(CommandStatus.AccessDenied,
                                     $"The NUT server {Upstream} refused the username or password " +
                                     $"({session.AuthenticationError ?? "unknown error"}).");
        }

        List<string> answer;
        try
        {
            answer = await session.Connection.RequestAsync(line, cancellationToken).ConfigureAwait(false);
        }
        catch (NutErrorException ex)
        {
            return NutErrorCodes.ToCommandResult(ex, Upstream);
        }
        catch (Exception ex) when (NetworkErrors.IsTransient(ex))
        {
            // The request may or may not have reached the server; say so rather than guess.
            return CommandResult.Fail($"The connection to the NUT server {Upstream} failed during the {what}: " +
                                      $"{NetworkErrors.Describe(ex)}. It may or may not have been executed.");
        }

        if (answer is ["OK", "TRACKING", var id, ..] && session.Tracking)
        {
            return await WaitForTrackingAsync(session, id, what, cancellationToken).ConfigureAwait(false);
        }

        if (answer is ["OK", ..])
        {
            return CommandResult.Ok;
        }

        _logger.LogWarning("{Ups}: unexpected answer from {Upstream} to the {What}: {Answer}", _upsName, Upstream, what,
                           string.Join(' ', answer));
        return CommandResult.Fail($"Unexpected answer from the NUT server {Upstream}: '{string.Join(' ', answer)}'.");
    }

    /// <summary>
    /// Follows a tracked command (NUT 2.8 TRACKING) until the upstream driver reports its outcome. The connection
    /// is released between the GET TRACKING requests so polling goes on meanwhile.
    /// </summary>
    private async Task<CommandResult> WaitForTrackingAsync(NutUpstreamSession session, string id, string what,
                                                           CancellationToken cancellationToken)
    {
        DateTimeOffset deadline = _time.GetUtcNow() + TrackingTimeout;
        while (_time.GetUtcNow() < deadline)
        {
            await Task.Delay(TrackingPollInterval, _time, cancellationToken).ConfigureAwait(false);
            List<string> answer;
            try
            {
                answer = await session.Connection.RequestAsync(NutLine.Build("GET TRACKING", id), cancellationToken)
                    .ConfigureAwait(false);
            }
            catch (NutErrorException ex)
            {
                return NutErrorCodes.ToCommandResult(ex, Upstream);
            }
            catch (Exception ex) when (NetworkErrors.IsTransient(ex))
            {
                return new CommandResult(CommandStatus.Success,
                                         $"The NUT server {Upstream} accepted the {what}, but the connection failed " +
                                         $"before it reported the outcome ({NetworkErrors.Describe(ex)}).");
            }

            switch (answer[0].ToUpperInvariant())
            {
                case "SUCCESS":
                    return CommandResult.Ok;
                case "PENDING":
                    continue;
                default:
                    return new CommandResult(CommandStatus.Success,
                                             $"The NUT server {Upstream} accepted the {what} and answered " +
                                             $"'{string.Join(' ', answer)}' about its outcome.");
            }
        }

        return new CommandResult(CommandStatus.Success,
                                 $"The NUT server {Upstream} accepted the {what}, but its driver did not report the " +
                                 $"outcome within {TrackingTimeout.TotalSeconds:0} s.");
    }

    /// <summary>A command or variable name that can be sent as one protocol word.</summary>
    private static bool IsWord(string text) =>
        text.Length is > 0 and <= 128 && text.All(c => !char.IsWhiteSpace(c) && !char.IsControl(c) && c is not ('"' or '\\' or '#' or '='));
}
