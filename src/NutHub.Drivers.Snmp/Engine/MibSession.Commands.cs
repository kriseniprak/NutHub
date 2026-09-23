using System.Globalization;
using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging;
using NutHub.Core.Model;
using NutHub.Drivers.Snmp.Mibs;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp.Engine;

/// <summary>Instant commands and variable writes, as SNMP SETs (snmp-ups' su_setOID()).</summary>
internal sealed partial class MibSession
{
    /// <summary>Runs an instant command. <paramref name="parameter"/> is the delay of the delayed commands.</summary>
    public async Task<CommandResult> ExecuteCommandAsync(string name, string? parameter, CancellationToken cancellationToken)
    {
        string? argument = string.IsNullOrWhiteSpace(parameter) ? null : parameter.Trim();
        if (_commands.TryGetValue(name, out MibEntry? entry))
        {
            return await ExecuteSingleAsync(entry, argument, cancellationToken).ConfigureAwait(false);
        }

        if (_composites.TryGetValue(name, out MibComposite? composite))
        {
            return await ExecuteCompositeAsync(composite, argument, cancellationToken).ConfigureAwait(false);
        }

        return CommandResult.NotSupported($"The device does not support the command {name}.");
    }

    /// <summary>Writes a variable: a driver-side setting, or an SNMP object.</summary>
    public async Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken)
    {
        if (_localSettings.ContainsKey(name))
        {
            if (IsNumericSetting(name) &&
                !(long.TryParse(value.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out long seconds) && seconds <= 86400))
            {
                return CommandResult.Invalid($"{name} must be a whole number of seconds between 0 and 86400.");
            }

            _localSettings[name] = value.Trim();
            return CommandResult.Ok;
        }

        if (!_writables.TryGetValue(name, out MibEntry? entry))
        {
            return new CommandResult(CommandStatus.ReadOnly, $"{name} cannot be written on this device.");
        }

        ISnmpData data;
        try
        {
            data = SnmpSetValues.Build(entry, value, _types.TryGetValue(entry.Oid!, out SnmpType type) ? type : null);
        }
        catch (FormatException ex)
        {
            return CommandResult.Invalid(ex.Message);
        }

        CommandResult result = await WriteAsync(entry.Oid!, data, name, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation("{Target}: {Name} set to {Value}.", _client.Target, name, value);
        }

        return result;
    }

    private async Task<CommandResult> ExecuteSingleAsync(MibEntry entry, string? argument, CancellationToken cancellationToken)
    {
        string? value;
        if (entry.Default is null)
        {
            // A command that writes the caller's value: the delay of load.off.delay / load.on.delay.
            value = argument ?? DefaultDelay(entry.Name);
            if (value is null)
            {
                return CommandResult.InvalidArgument($"{entry.Name} needs a delay in seconds.");
            }

            if (!long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out _))
            {
                return CommandResult.InvalidArgument($"The delay of {entry.Name} must be a whole number of seconds.");
            }
        }
        else
        {
            // The value is fixed by the MIB ("2" = turnUpsOff): a parameter would write something else to a control
            // object, which snmp-ups allows but nobody means.
            if (argument is not null)
            {
                return CommandResult.InvalidArgument($"{entry.Name} takes no parameter.");
            }

            value = entry.Default;
        }

        ISnmpData data;
        try
        {
            data = SnmpSetValues.Build(entry, value, _types.TryGetValue(entry.Oid!, out SnmpType type) ? type : null);
        }
        catch (FormatException ex)
        {
            return CommandResult.InvalidArgument(ex.Message);
        }

        CommandResult result = await WriteAsync(entry.Oid!, data, entry.Name, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            _logger.LogInformation("{Target}: command {Command} sent ({Oid} = {Value}).",
                                   _client.Target, entry.Name, entry.Oid, data);
        }

        return result;
    }

    private async Task<CommandResult> ExecuteCompositeAsync(MibComposite composite, string? argument,
                                                            CancellationToken cancellationToken)
    {
        foreach (MibStep step in composite.Steps)
        {
            CommandResult result;
            if (step.CommandName is { } command)
            {
                if (!_commands.TryGetValue(command, out MibEntry? entry))
                {
                    return CommandResult.Fail($"{composite.Name}: the device no longer offers {command}.");
                }

                // The caller's parameter is the delay of the delayed step; fixed commands take none.
                result = await ExecuteSingleAsync(entry, entry.Default is null ? argument : null, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                result = await SetVariableAsync(step.VariableName!, step.Value!, cancellationToken).ConfigureAwait(false);
            }

            if (!result.IsSuccess)
            {
                return result with { Message = $"{composite.Name} stopped at step {step}: {result.Message ?? result.Status.ToString()}" };
            }
        }

        return CommandResult.Ok;
    }

    /// <summary>The delay of a delayed command given without parameter: ups.delay.shutdown or ups.delay.start.</summary>
    private string? DefaultDelay(string command)
    {
        string? variable = command switch
        {
            "load.on.delay" => "ups.delay.start",
            _ when command.EndsWith(".delay", StringComparison.Ordinal) || command.StartsWith("shutdown.", StringComparison.Ordinal)
                => "ups.delay.shutdown",
            _ => null,
        };

        if (variable is null)
        {
            return null;
        }

        return _localSettings.TryGetValue(variable, out string? setting) ? setting : _lastVariables.GetValueOrDefault(variable);
    }

    private async Task<CommandResult> WriteAsync(string oid, ISnmpData data, string what, CancellationToken cancellationToken)
    {
        try
        {
            await _client.SetAsync(oid, data, cancellationToken).ConfigureAwait(false);
            _types[oid] = data.TypeCode;
            return CommandResult.Ok;
        }
        catch (SnmpErrorStatusException ex)
        {
            string hint = ex.Status switch
            {
                ErrorCode.NoAccess or ErrorCode.NotWritable or ErrorCode.ReadOnly or ErrorCode.AuthorizationError =>
                    _settings.Version == SnmpProtocolVersion.V3
                        ? $" Check that user '{_settings.SecurityName}' has write access on the card."
                        : " Check that the write community has write access on the card.",

                // SNMPv1 agents answer noSuchName to a SET with a read-only community.
                ErrorCode.NoSuchName when _settings.Version == SnmpProtocolVersion.V1 =>
                    " Check that the write community has write access on the card.",
                ErrorCode.WrongType or ErrorCode.WrongValue or ErrorCode.BadValue or ErrorCode.WrongLength or
                    ErrorCode.WrongEncoding or ErrorCode.InconsistentValue => " The device does not accept this value.",
                _ => "",
            };
            _logger.LogWarning("{Target}: {What} refused: {Status}.", _client.Target, what, ex.Status);
            return CommandResult.Fail($"The device refused {what} ({ex.Status}).{hint}");
        }
    }
}
