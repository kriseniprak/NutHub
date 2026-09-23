using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.Common;

namespace NutHub.Drivers.Serial.ApcSmart;

/// <summary>Instant commands and variable writes of the APC Smart protocol (NUT drivers/apcsmart.c instcmd, setvar).</summary>
/// <remarks>
/// NUT's apcsmart driver requires the power commands to be sent twice within 3-15 s, as a guard for people typing
/// upscmd by hand. NutHub does not: its clients confirm before sending, and the host protection sends shutdown.return
/// once when it must power the UPS down.
/// </remarks>
internal sealed partial class ApcSmartProtocol
{
    /// <summary>Runs an instant command; <paramref name="parameter"/> is "cs" or "at:nnn" for shutdown.return.</summary>
    public async Task<CommandResult> InstantCommandAsync(string name, string? parameter, CancellationToken cancellationToken)
    {
        ApcCommandDef? definition = null;
        bool known = false;
        foreach (ApcCommandDef candidate in ApcSmartTables.Commands.Where(c => string.Equals(c.Name, name, StringComparison.OrdinalIgnoreCase)))
        {
            known = true;
            bool matches = candidate.ParameterPattern is null
                ? string.IsNullOrEmpty(parameter)
                : Regex.IsMatch(parameter ?? string.Empty, candidate.ParameterPattern, RegexOptions.None, TimeSpan.FromSeconds(1));
            if (matches)
            {
                definition = candidate;
                break;
            }
        }

        if (definition is null)
        {
            return known
                ? CommandResult.InvalidArgument(name == "shutdown.return"
                    ? "shutdown.return accepts no parameter, 'cs', or 'at:' followed by 1 to 3 digits."
                    : $"{name} takes no parameter.")
                : CommandResult.NotSupported($"The APC Smart protocol has no command '{name}'.");
        }

        if (!_commandsPresent.Contains(definition))
        {
            return CommandResult.NotSupported($"This APC UPS model does not support {name}.");
        }

        switch (definition.Name)
        {
            case "calibrate.start":
                return await CalibrationAsync(start: true, cancellationToken).ConfigureAwait(false);
            case "calibrate.stop":
                return await CalibrationAsync(start: false, cancellationToken).ConfigureAwait(false);
            case "load.on":
                return await LoadOnAsync(cancellationToken).ConfigureAwait(false);
            case "load.off":
                return await PowerOffNowAsync(cancellationToken).ConfigureAwait(false);
            case "shutdown.stayoff":
                return await PowerOffDelayedAsync(cancellationToken).ConfigureAwait(false);
            case "shutdown.return":
                if (string.IsNullOrEmpty(parameter))
                {
                    return await DefaultShutdownAsync(cancellationToken).ConfigureAwait(false);
                }

                return char.ToUpperInvariant(parameter[0]) == 'A'
                    ? await HardHibernateAsync(parameter[3..], cancellationToken).ConfigureAwait(false)
                    : await ForcedSoftHibernateAsync(cancellationToken).ConfigureAwait(false);
            default:
                return await SimpleCommandAsync(definition, cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>Writes an EEPROM variable: strings directly, the others by cycling through their values with '-'.</summary>
    public async Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        ApcVariable? variable = Find(name);
        if (variable is null)
        {
            return CommandResult.NotSupported($"This APC UPS has no variable {name}.");
        }

        if (!variable.Writable)
        {
            return new CommandResult(CommandStatus.ReadOnly, $"{name} cannot be changed on this APC UPS.");
        }

        if (variable.Is(ApcVarFlags.String))
        {
            return await SetStringAsync(variable, value, cancellationToken).ConfigureAwait(false);
        }

        return variable.IsEnum
            ? await SetEnumAsync(variable, value, cancellationToken).ConfigureAwait(false)
            : CommandResult.NotSupported($"{name} cannot be changed on this APC UPS.");
    }

    /// <summary>
    /// shutdown.return without parameter, following the shutdownType option (NUT sdtype, upsdrv_shutdown_simple); the
    /// default soft-hibernates on battery and hard-hibernates on line power, so the load is power-cycled either way.
    /// </summary>
    private async Task<CommandResult> DefaultShutdownAsync(CancellationToken cancellationToken)
    {
        // The status may have changed since the last poll; read it again when possible.
        await UpdateStatusAsync(cancellationToken).ConfigureAwait(false);
        switch (_settings.ShutdownType)
        {
            case 5:
                return await HardHibernateAsync(_settings.WakeUpDelay, cancellationToken).ConfigureAwait(false);
            case 4:
                return await ForcedSoftHibernateAsync(cancellationToken).ConfigureAwait(false);
            case 3:
                return await PowerOffDelayedAsync(cancellationToken).ConfigureAwait(false);
            case 2:
                return await PowerOffNowAsync(cancellationToken).ConfigureAwait(false);
            case 1:
                if ((Status & ApcStatusBits.OnBattery) != 0)
                {
                    CommandResult soft = await SoftHibernateAsync(cancellationToken).ConfigureAwait(false);
                    if (soft.IsSuccess)
                    {
                        return soft;
                    }
                }

                return await HardHibernateAsync(_settings.WakeUpDelay, cancellationToken).ConfigureAwait(false);
            default:
                return (Status & ApcStatusBits.Online) != 0
                    ? await HardHibernateAsync(_settings.WakeUpDelay, cancellationToken).ConfigureAwait(false)
                    : await SoftHibernateAsync(cancellationToken).ConfigureAwait(false);
        }
    }

    /// <summary>S: power off after the grace delay, back on when the mains returns; only works on battery.</summary>
    private async Task<CommandResult> SoftHibernateAsync(CancellationToken cancellationToken)
    {
        await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
        await _link.WriteAsync(ApcSmartTables.SoftDown, cancellationToken).ConfigureAwait(false);
        return await ShutdownAcknowledgedAsync(silenceIsSuccess: false, "S", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>The "CS" trick: simulate a power failure so that S also works on line power (NUT sdcmd_CS).</summary>
    private async Task<CommandResult> ForcedSoftHibernateAsync(CancellationToken cancellationToken)
    {
        if ((Status & ApcStatusBits.Online) != 0)
        {
            await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
            await _link.WriteAsync(ApcSmartTables.SimulatePowerFail, cancellationToken).ConfigureAwait(false);
            ApcLine reply = await _link.ReadAsync(ApcReadMode.TimeoutAllowed, ApcSmartLink.ShortTimeout, cancellationToken)
                                       .ConfigureAwait(false);
            if (!reply.Ok)
            {
                return CommandResult.Fail("The UPS did not accept the simulated power failure.");
            }

            await Task.Delay(_settings.CsDelay, _time, cancellationToken).ConfigureAwait(false);
        }

        return await SoftHibernateAsync(cancellationToken).ConfigureAwait(false);
    }

    /// <summary>
    /// @nnn: power off after the grace delay, back on when the mains returns plus nnn × 6 minutes; works on line power
    /// too. Some models take two digits: exactly two digits select that variant (NUT sdcmd_AT).
    /// </summary>
    private async Task<CommandResult> HardHibernateAsync(string wakeUpDelay, CancellationToken cancellationToken)
    {
        string digits = string.IsNullOrEmpty(wakeUpDelay) ? "000" : wakeUpDelay;
        int width = digits.Length == 2 ? 2 : 3;
        string code = ApcSmartTables.GraceDown + digits.PadLeft(width, '0');
        await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
        if (!await _link.WriteLongAsync(code, cancellationToken).ConfigureAwait(false))
        {
            return CommandResult.Fail($"The UPS refused {code}.");
        }

        CommandResult result = await ShutdownAcknowledgedAsync(silenceIsSuccess: false, code, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess || width == 3)
        {
            return result;
        }

        // The two-digit form did not work: abort the half-accepted sequence with something harmless.
        await _link.WriteAsync(ApcSmartTables.GoSmart, cancellationToken).ConfigureAwait(false);
        await _link.ReadAsync(ApcReadMode.TimeoutAllowed, ApcSmartLink.ShortTimeout, cancellationToken).ConfigureAwait(false);
        return CommandResult.Fail($"{code} with two digits does not work on this UPS; use three digits.");
    }

    /// <summary>K (sent twice): power off after the grace delay and stay off.</summary>
    private async Task<CommandResult> PowerOffDelayedAsync(CancellationToken cancellationToken)
    {
        await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
        if (!await _link.WriteRepeatedAsync(ApcSmartTables.Shutdown, cancellationToken).ConfigureAwait(false))
        {
            return CommandResult.Fail("The UPS refused the delayed power-off (K).");
        }

        return await ShutdownAcknowledgedAsync(silenceIsSuccess: false, "K", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Z (sent twice): power off now; the UPS does not answer.</summary>
    private async Task<CommandResult> PowerOffNowAsync(CancellationToken cancellationToken)
    {
        await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
        if (!await _link.WriteRepeatedAsync(ApcSmartTables.Off, cancellationToken).ConfigureAwait(false))
        {
            return CommandResult.Fail("The UPS refused the power-off (Z).");
        }

        return await ShutdownAcknowledgedAsync(silenceIsSuccess: true, "Z", cancellationToken).ConfigureAwait(false);
    }

    /// <summary>^N (sent twice): switch the load on; the UPS does not answer.</summary>
    private async Task<CommandResult> LoadOnAsync(CancellationToken cancellationToken)
    {
        await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
        return await _link.WriteRepeatedAsync(ApcSmartTables.On, cancellationToken).ConfigureAwait(false)
            ? CommandResult.Ok
            : CommandResult.Fail("The UPS refused to switch the load on.");
    }

    /// <summary>Shutdown commands answer "OK" (or '*' on older models); NUT sdok.</summary>
    private async Task<CommandResult> ShutdownAcknowledgedAsync(bool silenceIsSuccess, string what, CancellationToken cancellationToken)
    {
        ApcLine reply = await _link.ReadAsync(ApcReadMode.HandleAsterisk | ApcReadMode.TimeoutAllowed, ApcSmartLink.ShortTimeout,
                                              cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return CommandResult.Fail($"Reading the answer to {what} failed.");
        }

        if (reply.Text == "OK" || (silenceIsSuccess && reply.Text.Length == 0))
        {
            _logger.LogInformation("{Transport}: shutdown command {Command} accepted.", _link.Description, what);
            return CommandResult.Ok;
        }

        return CommandResult.Fail(reply.Text.Length == 0
            ? $"The UPS did not acknowledge {what} (S only works on battery)."
            : $"The UPS answered {what} with '{CText.Printable(reply.Text)}'.");
    }

    /// <summary>Runtime calibration: 'D' toggles it, so check the current state first (NUT do_cal).</summary>
    private async Task<CommandResult> CalibrationAsync(bool start, CancellationToken cancellationToken)
    {
        if (!await UpdateStatusAsync(cancellationToken).ConfigureAwait(false))
        {
            return CommandResult.Fail("The calibration state cannot be read from the UPS.");
        }

        bool running = (Status & ApcStatusBits.Calibrating) != 0;
        if (running == start)
        {
            return CommandResult.Fail(start ? "A runtime calibration is already in progress." : "No runtime calibration is in progress.");
        }

        await _link.WriteAsync('D', cancellationToken).ConfigureAwait(false);
        ApcLine reply = await _link.ReadAsync(ApcReadMode.AlertAware, null, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok || reply.Text.Length == 0 || reply.IsNotAvailable || reply.Text == "NO")
        {
            return CommandResult.Fail($"The UPS refused to {(start ? "start" : "stop")} the calibration" +
                                      (reply.Text.Length > 0 ? $" ('{CText.Printable(reply.Text)}')." : "."));
        }

        return CommandResult.Ok;
    }

    /// <summary>The other commands answer "OK" (NUT do_cmd); the bypass toggle answers BYP or INV instead.</summary>
    private async Task<CommandResult> SimpleCommandAsync(ApcCommandDef definition, CancellationToken cancellationToken)
    {
        await _link.FlushAsync(alertAware: true, cancellationToken).ConfigureAwait(false);
        if (definition.Repeat)
        {
            if (!await _link.WriteRepeatedAsync(definition.Command, cancellationToken).ConfigureAwait(false))
            {
                return CommandResult.Fail($"The UPS refused {definition.Name}.");
            }
        }
        else
        {
            await _link.WriteAsync(definition.Command, cancellationToken).ConfigureAwait(false);
        }

        ApcLine reply = await _link.ReadAsync(ApcReadMode.AlertAware, null, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok || reply.Text.Length == 0)
        {
            return CommandResult.Fail($"The UPS did not answer {definition.Name}.");
        }

        bool accepted = reply.Text == "OK" || (definition.Command == '^' && reply.Text is "BYP" or "INV");
        return accepted
            ? CommandResult.Ok
            : CommandResult.Fail($"The UPS answered {definition.Name} with '{CText.Printable(reply.Text)}'.");
    }

    /// <summary>
    /// Cycles an EEPROM variable with '-' until it shows the wanted value; gives up when it wraps around (NUT
    /// setvar_enum). The EEPROM is not touched when the value is already right.
    /// </summary>
    private async Task<CommandResult> SetEnumAsync(ApcVariable variable, string value, CancellationToken cancellationToken)
    {
        await _link.FlushAsync(alertAware: true, cancellationToken).ConfigureAwait(false);
        string? original = await ReadCurrentAsync(variable, cancellationToken).ConfigureAwait(false);
        if (original is null)
        {
            return CommandResult.Fail($"The UPS did not report the current value of {variable.Name}.");
        }

        if (original == value)
        {
            return CommandResult.Ok;
        }

        for (int i = 0; i < 32; i++)
        {
            await _link.WriteAsync(ApcSmartTables.NextValue, cancellationToken).ConfigureAwait(false);
            ApcLine step = await _link.ReadAsync(ApcReadMode.AlertAware, null, cancellationToken).ConfigureAwait(false);
            if (!step.Ok || step.Text != "OK")
            {
                return CommandResult.Fail(step.Text == "NO"
                    ? $"The UPS refused to change {variable.Name}."
                    : $"The UPS did not confirm the change of {variable.Name}.");
            }

            string? current = await ReadCurrentAsync(variable, cancellationToken).ConfigureAwait(false);
            if (current is null)
            {
                return CommandResult.Fail($"The UPS did not report the new value of {variable.Name}.");
            }

            if (current == value)
            {
                Values[variable.Name] = current;
                return CommandResult.Ok;
            }

            if (current == original)
            {
                return CommandResult.Invalid($"The UPS does not offer '{value}' for {variable.Name}.");
            }
        }

        return CommandResult.Fail($"Gave up changing {variable.Name} after 32 steps.");
    }

    /// <summary>Writes ups.id or battery.date: '-' then 8 characters, padded with CR (NUT setvar_string).</summary>
    private async Task<CommandResult> SetStringAsync(ApcVariable variable, string value, CancellationToken cancellationToken)
    {
        if (value.Length > ApcSmartValues.StringLength)
        {
            return new CommandResult(CommandStatus.TooLong, $"At most {ApcSmartValues.StringLength} characters.");
        }

        if (value.Any(c => c < ' ' || c > '~'))
        {
            return CommandResult.Invalid("Only printable ASCII characters can be stored in the UPS.");
        }

        await _link.FlushAsync(alertAware: true, cancellationToken).ConfigureAwait(false);
        string? current = await ReadCurrentAsync(variable, cancellationToken).ConfigureAwait(false);
        if (current is null)
        {
            return CommandResult.Fail($"The UPS did not report the current value of {variable.Name}.");
        }

        if (current == value)
        {
            return CommandResult.Ok;
        }

        string code = ApcSmartTables.NextValue + value + new string('\r', ApcSmartValues.StringLength - value.Length);
        if (!await _link.WriteLongAsync(code, cancellationToken).ConfigureAwait(false))
        {
            return CommandResult.Fail($"The UPS refused to change {variable.Name}.");
        }

        ApcLine reply = await _link.ReadAsync(ApcReadMode.AlertAware, null, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok || reply.Text != "OK")
        {
            return CommandResult.Fail($"The UPS did not accept the new value of {variable.Name}.");
        }

        await PollVariableAsync(variable, cancellationToken).ConfigureAwait(false);
        return CommandResult.Ok;
    }

    /// <summary>Reads the converted current value of a variable, or null.</summary>
    private async Task<string?> ReadCurrentAsync(ApcVariable variable, CancellationToken cancellationToken)
    {
        await _link.WriteAsync(variable.Command, cancellationToken).ConfigureAwait(false);
        ApcLine reply = await _link.ReadAsync(ApcReadMode.AlertAware, null, cancellationToken).ConfigureAwait(false);
        return reply.Ok && reply.Text.Length > 0 && !reply.IsNotAvailable
            ? ApcSmartValues.Convert(variable.Definition.Format, reply.Text)
            : null;
    }
}
