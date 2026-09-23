using System.Text.RegularExpressions;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.Common;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.ApcSmart;

/// <summary>
/// The APC Smart protocol for one connection: entering smart mode, learning what the UPS supports (command set 'a',
/// firmware tables for older models, capability string ^Z), and polling. A port of NUT drivers/apcsmart.c (driver
/// version 3.40); instant commands and variable writes are in ApcSmartProtocol.Commands.cs.
/// </summary>
internal sealed partial class ApcSmartProtocol
{
    private static readonly TimeSpan FullRefreshInterval = TimeSpan.FromHours(1);

    private readonly ApcSmartLink _link;
    private readonly ApcSmartSettings _settings;
    private readonly TimeProvider _time;
    private readonly ILogger _logger;
    private readonly List<ApcVariable> _variables = ApcSmartTables.Variables.Select(d => new ApcVariable(d)).ToList();
    private readonly HashSet<ApcDualVariableDef> _dualPresent = [];
    private readonly HashSet<ApcCommandDef> _commandsPresent = [];
    private long _lastFullRefresh;

    public ApcSmartProtocol(ISerialTransport transport, ApcSmartSettings settings, TimeProvider time, ILogger logger)
    {
        _settings = settings;
        _time = time;
        _logger = logger;
        _link = new ApcSmartLink(transport, time, settings.ReplyTimeout, OnAlert, logger);
    }

    /// <summary>Variables in NUT naming, as last read.</summary>
    public Dictionary<string, string> Values { get; } = new(StringComparer.Ordinal);

    /// <summary>The 'Q' status register, kept up to date by the alerts between polls.</summary>
    public int Status { get; private set; }

    public IReadOnlyCollection<string> Commands =>
        _commandsPresent.Select(c => c.Name).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToList();

    internal IReadOnlyList<ApcVariable> Variables => _variables;

    /// <summary>Puts the UPS in smart mode and learns its variables and commands. Returns null or the problem.</summary>
    public async Task<string?> InitializeAsync(CancellationToken cancellationToken)
    {
        if (!await SmartModeAsync(5, cancellationToken).ConfigureAwait(false))
        {
            return $"No APC Smart UPS answers on {_link.Description}. Check the cable (APC 940-0024C or a clone: an " +
                   "ordinary serial cable does not work and can even switch the UPS off), the port, and that the UPS " +
                   "speaks the Smart protocol (models with an RJ45 serial port use the Microlink protocol instead).";
        }

        if (!await ReadBaseInfoAsync(cancellationToken).ConfigureAwait(false))
        {
            return $"The APC UPS on {_link.Description} stopped answering while its capabilities were being read.";
        }

        Values["ups.mfr"] = "APC";
        _lastFullRefresh = _time.GetTimestamp();
        return null;
    }

    /// <summary>One poll: the status register, then the variables (all of them once an hour).</summary>
    public async Task<PollResult> PollAsync(bool recovering, CancellationToken cancellationToken)
    {
        // After a failure the UPS may have fallen back to dumb mode (power cycle, cable replugged): nudge it.
        if (recovering && !await SmartModeAsync(1, cancellationToken).ConfigureAwait(false))
        {
            return PollResult.Failed($"The APC UPS on {_link.Description} does not answer. Check the cable and that the UPS is on.");
        }

        if (!await UpdateStatusAsync(cancellationToken).ConfigureAwait(false))
        {
            return PollResult.Failed($"The APC UPS on {_link.Description} did not answer the status query.");
        }

        bool all = _time.GetElapsedTime(_lastFullRefresh) >= FullRefreshInterval;
        if (all)
        {
            _lastFullRefresh = _time.GetTimestamp();
        }

        if (!await UpdateInfoAsync(all, cancellationToken).ConfigureAwait(false))
        {
            return PollResult.Failed($"The APC UPS on {_link.Description} stopped answering during the poll.");
        }

        return PollResult.Ok(BuildUpdate());
    }

    internal DriverUpdate BuildUpdate()
    {
        var vars = new Dictionary<string, string>(Values, StringComparer.Ordinal)
        {
            ["ups.status"] = ApcSmartValues.FormatStatus(Status),
        };

        var info = new Dictionary<string, VariableInfo>(StringComparer.Ordinal);
        foreach (ApcVariable variable in _variables.Where(v => v.Present && v.Writable))
        {
            if (variable.Is(ApcVarFlags.String))
            {
                info[variable.Name] = VariableInfo.WritableString(ApcSmartValues.StringLength);
            }
            else if (variable.IsEnum)
            {
                info[variable.Name] = VariableInfo.WritableEnum([.. variable.EnumValues.Distinct(StringComparer.Ordinal)]);
            }
        }

        return new DriverUpdate { Variables = vars, VariableInfo = info, Commands = Commands };
    }

    private void OnAlert(char alert)
    {
        int updated = ApcSmartValues.ApplyAlert(Status, alert);
        if (updated != Status)
        {
            _logger.LogDebug("{Transport}: alert '{Alert}', status {Old:X2} -> {New:X2}.", _link.Description, alert, Status, updated);
            Status = updated;
        }
    }

    /// <summary>Sends 'Y' until the UPS answers "SM", escaping out of any half-typed command in between (NUT smartmode).</summary>
    private async Task<bool> SmartModeAsync(int attempts, CancellationToken cancellationToken)
    {
        for (int i = 0; i < attempts; i++)
        {
            await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
            await _link.WriteAsync(ApcSmartTables.GoSmart, cancellationToken).ConfigureAwait(false);
            ApcLine reply = await _link.ReadAsync(ApcReadMode.TimeoutAllowed, ApcSmartLink.ShortTimeout, cancellationToken)
                                       .ConfigureAwait(false);
            if (reply.Ok && reply.Text == "SM")
            {
                return true;
            }

            if (!reply.Ok)
            {
                await Task.Delay(TimeSpan.FromSeconds(1), _time, cancellationToken).ConfigureAwait(false);
            }

            await _link.WriteAsync('\u001B', cancellationToken).ConfigureAwait(false);
            await _link.ReadAsync(ApcReadMode.TimeoutAllowed, ApcSmartLink.ShortTimeout, cancellationToken).ConfigureAwait(false);
        }

        return false;
    }

    /// <summary>
    /// Learns the supported variables and commands: from the firmware tables for old models that need it, else from
    /// the command set 'a' (with the capability string when available), else a basic guess (NUT getbaseinfo).
    /// </summary>
    private async Task<bool> ReadBaseInfoAsync(CancellationToken cancellationToken)
    {
        int firmware = await FirmwareTableLookupAsync(cancellationToken).ConfigureAwait(false);
        if (firmware == 1)
        {
            return true;
        }

        await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
        await _link.WriteAsync(ApcSmartTables.CommandSet, cancellationToken).ConfigureAwait(false);
        ApcLine reply = await _link.ReadAsync(ApcReadMode.CommandSet | ApcReadMode.TimeoutAllowed, null, cancellationToken)
                                   .ConfigureAwait(false);
        if (!reply.Ok)
        {
            return false;
        }

        string set = reply.Text;
        if (set.Length == 0 || reply.IsNotAvailable || !Regex.IsMatch(set, ApcSmartTables.CommandSetPattern, RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            _logger.LogInformation("{Transport}: very old or unknown APC model; support will be limited.", _link.Description);
            await OldModelSetupAsync(cancellationToken).ConfigureAwait(false);
            return true;
        }

        // "version.alerts.commands[.extensions]"
        string[] sections = set.Split('.', 4);
        string commands = sections[2];
        foreach (char command in commands)
        {
            await VerifyCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }

        await DeprecateVariablesAsync(cancellationToken).ConfigureAwait(false);
        if (sections.Length > 3 && sections[3].Length > 0)
        {
            ParseDualCommands(sections[3]);
        }

        if (commands.Contains(ApcSmartTables.Capabilities, StringComparison.Ordinal))
        {
            await ReadCapabilitiesAsync(quirkyFirmware: firmware == 2, cancellationToken).ConfigureAwait(false);
        }

        return true;
    }

    /// <summary>
    /// Old models do not answer 'a': recognise them by firmware revision ('V', or 'b' when 'V' is not supported) and use
    /// the command list NUT keeps for them. Returns 1 when matched, 2 for a firmware known to overflow its capability
    /// string, 0 otherwise (NUT firmware_table_lookup).
    /// </summary>
    private async Task<int> FirmwareTableLookupAsync(CancellationToken cancellationToken)
    {
        await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
        await _link.WriteAsync(ApcSmartTables.FirmwareOld, cancellationToken).ConfigureAwait(false);
        ApcLine reply = await _link.ReadAsync(ApcReadMode.TimeoutAllowed, null, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok)
        {
            return 0;
        }

        if (reply.Text.Length == 0 || reply.IsNotAvailable)
        {
            await _link.WriteAsync(ApcSmartTables.FirmwareNew, cancellationToken).ConfigureAwait(false);
            reply = await _link.ReadAsync(ApcReadMode.TimeoutAllowed, null, cancellationToken).ConfigureAwait(false);
            if (!reply.Ok || reply.Text.Length == 0)
            {
                return 0;
            }
        }

        string firmware = reply.Text;
        if (firmware == "451.2.I")
        {
            return 2;
        }

        if (Regex.IsMatch(firmware, "^[a-fA-F0-9]{2}$", RegexOptions.None, TimeSpan.FromSeconds(1)))
        {
            // Some old APC 600 return their voltage through 'b'; they have their own table entry.
            firmware = "set\u0001";
        }

        if (!ApcSmartTables.Compatibility.TryGetValue(firmware, out string? commands))
        {
            return 0;
        }

        Values["ups.model"] = firmware[0] is '0' or '5' ? "Matrix-UPS" : "Smart-UPS";
        foreach (char command in commands)
        {
            await VerifyCommandAsync(command, cancellationToken).ConfigureAwait(false);
        }

        await DeprecateVariablesAsync(cancellationToken).ConfigureAwait(false);
        return 1;
    }

    /// <summary>Checks a command character of the command set: a variable to read, or an instant command (NUT protocol_verify).</summary>
    private async Task VerifyCommandAsync(char command, CancellationToken cancellationToken)
    {
        if (ApcSmartTables.Unrecognised.Contains(command, StringComparison.Ordinal))
        {
            return;
        }

        bool found = false;
        foreach (ApcVariable variable in _variables.Where(v => v.Command == command))
        {
            found = true;
            await VerifyVariableAsync(variable, cancellationToken).ConfigureAwait(false);
        }

        if (found)
        {
            return;
        }

        foreach (ApcCommandDef definition in ApcSmartTables.Commands.Where(c => c.Command == command))
        {
            found = true;
            _commandsPresent.Add(definition);
        }

        if (!found)
        {
            _logger.LogDebug("{Transport}: command character 0x{Code:X2} not used.", _link.Description, (int)command);
        }
    }

    /// <summary>Reads a variable once to see if the UPS really supports it (NUT var_verify).</summary>
    private async Task VerifyVariableAsync(ApcVariable variable, CancellationToken cancellationToken)
    {
        if (variable.Is(ApcVarFlags.Multi))
        {
            // Resolved by DeprecateVariablesAsync once every candidate is known.
            variable.Present = true;
            return;
        }

        string? value = await PreReadAsync(variable.Command, cancellationToken).ConfigureAwait(false);
        if (value is null || !ApcSmartValues.Matches(variable.Definition.Pattern, value))
        {
            _logger.LogDebug("{Transport}: {Variable} not supported.", _link.Description, variable.Name);
            return;
        }

        variable.Present = true;
        variable.Writable |= variable.Is(ApcVarFlags.String);
        ApcSmartValues.Store(variable, value, Values);
    }

    /// <summary>
    /// For the NUT variables several commands can provide (firmware from 'b' or 'V') and the commands that mean
    /// different things on different models ('T': temperature or uptime), keeps the first candidate whose reply has the
    /// expected shape (NUT deprecate_vars).
    /// </summary>
    private async Task DeprecateVariablesAsync(CancellationToken cancellationToken)
    {
        for (int i = 0; i < _variables.Count; i++)
        {
            ApcVariable variable = _variables[i];
            if (!variable.Is(ApcVarFlags.Multi) || !variable.Present)
            {
                continue;
            }

            string? value = await PreReadAsync(variable.Command, cancellationToken).ConfigureAwait(false);
            if (value is null || !ApcSmartValues.Matches(variable.Definition.Pattern, value))
            {
                variable.Present = false;
                continue;
            }

            for (int j = i + 1; j < _variables.Count; j++)
            {
                if (_variables[j].Name == variable.Name || _variables[j].Command == variable.Command)
                {
                    _variables[j].Present = false;
                }
            }

            variable.Writable |= variable.Is(ApcVarFlags.String);
            ApcSmartValues.Store(variable, value, Values);
        }
    }

    /// <summary>Models that answer neither 'a' nor a known firmware: try the basic variables one by one (NUT oldapcsetup).</summary>
    private async Task OldModelSetupAsync(CancellationToken cancellationToken)
    {
        string[] basics =
        [
            "ups.temperature", "ups.load", "input.voltage", "output.voltage", "battery.charge", "battery.voltage",
            "ups.model", "ups.serial", "ups.firmware", "output.current",
        ];
        foreach (string name in basics)
        {
            foreach (ApcVariable variable in _variables.Where(v => v.Name == name))
            {
                await VerifyVariableAsync(variable, cancellationToken).ConfigureAwait(false);
            }
        }

        await DeprecateVariablesAsync(cancellationToken).ConfigureAwait(false);
        if (Find("output.current") is not null)
        {
            Values["ups.model"] = "Matrix-UPS";
        }
        else if (Find("ups.model") is null)
        {
            Values["ups.model"] = "Smart-UPS";
        }
    }

    /// <summary>
    /// Parses the capability string of ^Z: "#" then groups of command, locale, count, width and the values, e.g.
    /// "#uD43132135138129": 'u' has 4 values of 3 characters for locale D. Matrix-UPS repeat "##" before every group.
    /// Listed variables become writable with those values (NUT apc_getcaps).
    /// </summary>
    private async Task ReadCapabilitiesAsync(bool quirkyFirmware, CancellationToken cancellationToken)
    {
        string? firmware = Values.GetValueOrDefault("ups.firmware");
        char locale = string.IsNullOrEmpty(firmware) ? '\0' : firmware[^1];

        await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
        await _link.WriteAsync(ApcSmartTables.Capabilities, cancellationToken).ConfigureAwait(false);
        ApcLine reply = await _link.ReadAsync(ApcReadMode.CapabilityCheck | ApcReadMode.TimeoutAllowed, null, cancellationToken)
                                   .ConfigureAwait(false);
        if (!reply.Ok || reply.Text.Length == 0 || reply.IsNotAvailable)
        {
            _logger.LogWarning("{Transport}: the APC UPS announced capabilities but does not report them.", _link.Description);
            return;
        }

        ParseCapabilities(reply.Text, locale, quirkyFirmware);
    }

    internal void ParseCapabilities(string text, char locale, bool quirkyFirmware)
    {
        if (text[0] != '#')
        {
            _logger.LogWarning("{Transport}: unknown capability string start '{Start}'.", _link.Description, text[0]);
            return;
        }

        bool matrix = text.Length > 1 && text[1] == '#';
        int position = matrix ? 0 : 1;
        while (position < text.Length)
        {
            if (matrix)
            {
                position += 2;
            }

            if (position + 4 > text.Length)
            {
                if (!quirkyFirmware && position < text.Length)
                {
                    _logger.LogWarning("{Transport}: the capability string is truncated.", _link.Description);
                }

                return;
            }

            char command = text[position];
            char entryLocale = text[position + 1];
            int count = text[position + 2] >= '0' && text[position + 3] >= '0' ? text[position + 2] - '0' : 0;
            int width = count > 0 ? text[position + 3] - '0' : 0;
            int entries = position + 4;

            ApcVariable? variable = _variables.FirstOrDefault(v => v.Present && v.Command == command);
            bool valid = variable is not null && (entryLocale == locale || entryLocale == '4') && !variable.Is(ApcVarFlags.Pack);
            if (valid)
            {
                variable!.Writable = true;
                variable.EnumValues.Clear();
            }

            for (int i = 0; i < count; i++)
            {
                if (entries + width > text.Length)
                {
                    if (!quirkyFirmware)
                    {
                        _logger.LogWarning("{Transport}: the capability string is truncated.", _link.Description);
                    }

                    return;
                }

                if (valid)
                {
                    variable!.EnumValues.Add(ApcSmartValues.Convert(variable.Definition.Format, text.Substring(entries, width)));
                }

                entries += width;
            }

            position = entries;
        }
    }

    /// <summary>
    /// The optional fourth section of 'a' lists two-byte commands as "prefix:subcommands" (SPM models). When the input
    /// frequency has its own two-byte command, 'F' is the output frequency.
    /// </summary>
    internal void ParseDualCommands(string extension)
    {
        byte prefix = 0;
        for (int i = 0; i < extension.Length; i++)
        {
            byte b = (byte)extension[i];
            if (i + 1 < extension.Length && extension[i + 1] == ':')
            {
                prefix = b;
                continue;
            }

            if (b == (byte)':' || prefix == 0)
            {
                continue;
            }

            foreach (ApcDualVariableDef dual in ApcSmartTables.DualVariables.Where(d => d.Prefix == prefix && d.Sub == b))
            {
                _dualPresent.Add(dual);
            }
        }

        if (_dualPresent.Any(d => d.Name == "input.frequency") &&
            _variables.FirstOrDefault(v => v.Command == 'F') is { } frequency)
        {
            frequency.Name = "output.frequency";
            Values.Remove("input.frequency");
        }
    }

    private async Task<string?> PreReadAsync(char command, CancellationToken cancellationToken)
    {
        await _link.FlushAsync(alertAware: false, cancellationToken).ConfigureAwait(false);
        await _link.WriteAsync(command, cancellationToken).ConfigureAwait(false);
        ApcLine reply = await _link.ReadAsync(ApcReadMode.TimeoutAllowed, null, cancellationToken).ConfigureAwait(false);
        return reply.Ok && reply.Text.Length > 0 && !reply.IsNotAvailable ? reply.Text : null;
    }

    private async Task<bool> UpdateStatusAsync(CancellationToken cancellationToken)
    {
        await _link.FlushAsync(alertAware: true, cancellationToken).ConfigureAwait(false);
        await _link.WriteAsync(ApcSmartTables.Status, cancellationToken).ConfigureAwait(false);
        ApcLine reply = await _link.ReadAsync(ApcReadMode.AlertAware, null, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok || !ApcSmartValues.TryParseStatus(reply.Text, out int status))
        {
            if (reply.Ok)
            {
                _logger.LogDebug("{Transport}: invalid status register '{Reply}'.", _link.Description, CText.Printable(reply.Text));
            }

            return false;
        }

        Status = status & 0xFF;
        return true;
    }

    private async Task<bool> UpdateInfoAsync(bool all, CancellationToken cancellationToken)
    {
        foreach (ApcVariable variable in _variables)
        {
            if ((all || variable.Is(ApcVarFlags.Poll)) && !await PollVariableAsync(variable, cancellationToken).ConfigureAwait(false))
            {
                return false;
            }
        }

        foreach (ApcDualVariableDef dual in _dualPresent)
        {
            await _link.FlushAsync(alertAware: true, cancellationToken).ConfigureAwait(false);
            await _link.WriteAsync([dual.Prefix, dual.Sub], cancellationToken).ConfigureAwait(false);
            ApcLine reply = await _link.ReadAsync(ApcReadMode.AlertAware, null, cancellationToken).ConfigureAwait(false);
            if (!reply.Ok || reply.Text.Length == 0)
            {
                return false;
            }

            if (!reply.IsNotAvailable)
            {
                Values[dual.Name] = ApcSmartValues.Convert(dual.Format, reply.Text);
            }
        }

        return true;
    }

    /// <summary>Reads one supported variable; one the UPS now answers NA to is dropped (NUT poll_data).</summary>
    private async Task<bool> PollVariableAsync(ApcVariable variable, CancellationToken cancellationToken)
    {
        if (!variable.Present)
        {
            return true;
        }

        await _link.FlushAsync(alertAware: true, cancellationToken).ConfigureAwait(false);
        await _link.WriteAsync(variable.Command, cancellationToken).ConfigureAwait(false);
        ApcLine reply = await _link.ReadAsync(ApcReadMode.AlertAware, null, cancellationToken).ConfigureAwait(false);
        if (!reply.Ok || reply.Text.Length == 0)
        {
            return false;
        }

        if (reply.IsNotAvailable)
        {
            _logger.LogWarning("{Transport}: {Variable} is no longer supported by the UPS; removed.", _link.Description, variable.Name);
            variable.Present = false;
            ApcSmartValues.Remove(variable, Values);
        }
        else
        {
            ApcSmartValues.Store(variable, reply.Text, Values);
        }

        return true;
    }

    private ApcVariable? Find(string name) => _variables.FirstOrDefault(v => v.Present && v.Name == name);
}
