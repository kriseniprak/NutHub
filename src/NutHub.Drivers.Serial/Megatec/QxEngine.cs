using System.Globalization;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.Common;

namespace NutHub.Drivers.Serial.Megatec;

/// <summary>
/// Runs one Q* protocol table against a UPS: identification (claim), the first complete reading, the polls, instant
/// commands and the server-side delay variables. A port of the generic part of NUT drivers/nutdrv_qx.c (qx_ups_walk,
/// qx_process, qx_process_answer, ups_infoval_set, instcmd, setvar).
/// </summary>
internal sealed class QxEngine
{
    private const string BeeperToggle = "beeper.toggle";
    private const string BeeperEnable = "beeper.enable";
    private const string BeeperDisable = "beeper.disable";
    private const string BeeperStatusName = "ups.beeper.status";

    private readonly IQxLink _link;
    private readonly MegatecSettings _settings;
    private readonly ILogger _logger;
    private readonly BatteryEstimator _battery;

    public QxEngine(QxProtocol protocol, IQxLink link, MegatecSettings settings, TimeProvider time, ILogger logger)
    {
        Protocol = protocol;
        _link = link;
        _settings = settings;
        _logger = logger;
        _battery = new BatteryEstimator(settings, time, logger);
        State = new QxState { BatteryVoltageReportsOnePack = settings.BatteryVoltageReportsOnePack };
    }

    public QxProtocol Protocol { get; }

    public QxState State { get; }

    /// <summary>
    /// The instant commands the UPS supports with this protocol, plus beeper.enable / beeper.disable when the protocol
    /// only has a toggle but the UPS reports the beeper state (the NUT subdrivers offer just the toggle there).
    /// </summary>
    public IReadOnlyList<string> Commands
    {
        get
        {
            List<string> names = Protocol.Commands.Where(c => !c.Skip).Select(c => c.Name).Distinct(StringComparer.Ordinal).ToList();
            if (names.Contains(BeeperToggle) && BeeperEnabled() is not null)
            {
                foreach (string emulated in new[] { BeeperEnable, BeeperDisable })
                {
                    if (!names.Contains(emulated))
                    {
                        names.Add(emulated);
                    }
                }
            }

            return names;
        }
    }

    /// <summary>The writable variables: the two delays the driver keeps for the shutdown commands.</summary>
    public IReadOnlyDictionary<string, VariableInfo> VariableInfo => new Dictionary<string, VariableInfo>(StringComparer.Ordinal)
    {
        ["ups.delay.start"] = Core.Model.VariableInfo.WritableRange(Protocol.OnDelayRange.Min, Protocol.OnDelayRange.Max),
        ["ups.delay.shutdown"] = Core.Model.VariableInfo.WritableRange(Protocol.OffDelayRange.Min, Protocol.OffDelayRange.Max),
    };

    /// <summary>
    /// Whether the UPS answers the identifying queries of this protocol (NUT subdriver claim functions). Values read
    /// while claiming are kept, as NUT keeps them in dstate.
    /// </summary>
    public async Task<bool> ClaimAsync(CancellationToken cancellationToken)
    {
        Protocol.ApplyOptions();
        foreach (string name in Protocol.ClaimFields)
        {
            QxField? field = Protocol.FindField(name);
            if (field is null)
            {
                return false;
            }

            FieldOutcome outcome = await ProcessFieldAsync(field, cache: null, cancellationToken).ConfigureAwait(false);
            bool accepted = outcome == FieldOutcome.ValueSet ||
                            (outcome == FieldOutcome.Blank && Protocol.ClaimAcceptsBlank(field));
            if (!accepted)
            {
                State.Values.Clear();
                State.Internal.Clear();
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// The first complete reading (NUT QX_WALKMODE_INIT): every item is tried once; items the UPS does not answer are
    /// skipped from then on. Returns null, or why the UPS cannot be used.
    /// </summary>
    public async Task<string?> InitializeAsync(CancellationToken cancellationToken)
    {
        State.BeginWalk();
        var cache = new Dictionary<string, QxAnswer>(StringComparer.Ordinal);
        foreach (QxField field in Protocol.Fields)
        {
            if (field.Skip)
            {
                continue;
            }

            // Several queries may provide the same variable: the first one that works wins.
            if (!field.IsStatus && !field.IsAlarm && !field.Has(QxFlags.NoNut) && State.Values.ContainsKey(field.Name))
            {
                continue;
            }

            FieldOutcome outcome = await ProcessFieldAsync(field, cache, cancellationToken).ConfigureAwait(false);
            if (outcome == FieldOutcome.QueryFailed)
            {
                if (field.Has(QxFlags.QuickPoll))
                {
                    return StatusFailure(field);
                }

                _logger.LogDebug("{Protocol}: {Field} not supported by this UPS; skipped.", Protocol.Name, field);
                field.Skip = true;
            }
            else if (outcome == FieldOutcome.Invalid && field.Has(QxFlags.QuickPoll))
            {
                return StatusFailure(field);
            }
        }

        State.Values["ups.delay.start"] = InitialDelay(_settings.OnDelay, Protocol.OnDelayRange, Protocol.NormalizeOnDelay, "onDelay");
        State.Values["ups.delay.shutdown"] = InitialDelay(_settings.OffDelay, Protocol.OffDelayRange, Protocol.NormalizeOffDelay, "offDelay");
        _battery.Initialize(State);
        return null;
    }

    /// <summary>One poll (NUT QX_WALKMODE_FULL_UPDATE): an update, or why there is none.</summary>
    public async Task<PollResult> PollAsync(CancellationToken cancellationToken)
    {
        State.BeginWalk();
        bool chargeReported = false;
        bool runtimeReported = false;
        var cache = new Dictionary<string, QxAnswer>(StringComparer.Ordinal);
        foreach (QxField field in Protocol.Fields)
        {
            if (field.Skip || field.Has(QxFlags.Static) || (field.Has(QxFlags.SemiStatic) && !State.DataChanged))
            {
                continue;
            }

            FieldOutcome outcome = await ProcessFieldAsync(field, cache, cancellationToken).ConfigureAwait(false);
            if (outcome is FieldOutcome.QueryFailed or FieldOutcome.Invalid && field.Has(QxFlags.QuickPoll))
            {
                return PollResult.Failed(StatusFailure(field));
            }

            if (outcome == FieldOutcome.ValueSet)
            {
                chargeReported |= field.Name == "battery.charge";
                runtimeReported |= field.Name == "battery.runtime";
            }
        }

        State.DataChanged = false;
        _battery.Update(State, chargeReported, runtimeReported);
        return PollResult.Ok(BuildUpdate());
    }

    /// <summary>Sends an instant command and checks the reply (NUT instcmd).</summary>
    public async Task<CommandResult> InstantCommandAsync(string name, string? parameter, CancellationToken cancellationToken)
    {
        QxCommand? command = Protocol.FindCommand(name);
        if (command is null && Protocol.FindCommand(BeeperToggle) is not null &&
            (string.Equals(name, BeeperEnable, StringComparison.OrdinalIgnoreCase) ||
             string.Equals(name, BeeperDisable, StringComparison.OrdinalIgnoreCase)))
        {
            return await SetBeeperAsync(string.Equals(name, BeeperEnable, StringComparison.OrdinalIgnoreCase), parameter,
                                        cancellationToken).ConfigureAwait(false);
        }

        if (command is null)
        {
            return CommandResult.NotSupported($"The {Protocol.Name} protocol has no command '{name}'.");
        }

        string text = command.Command;
        if (command.Format is not null)
        {
            if (!command.Format(command, parameter, State, out text, out string error))
            {
                return CommandResult.InvalidArgument(error);
            }
        }
        else if (!string.IsNullOrEmpty(parameter))
        {
            return CommandResult.InvalidArgument($"The command '{name}' takes no parameter.");
        }

        QxAnswer answer = await _link.ExchangeAsync(text, cancellationToken).ConfigureAwait(false);
        string reply = CString(answer.Text);
        if (Protocol.Rejected is { } rejected && string.Equals(answer.Text, rejected, StringComparison.OrdinalIgnoreCase))
        {
            return CommandResult.Fail($"The UPS refused '{CText.Printable(text)}'.");
        }

        if (command.MinLength > 0 && answer.Text.Length < command.MinLength)
        {
            return CommandResult.Fail(answer.IsEmpty
                ? $"The UPS did not acknowledge '{CText.Printable(text)}'."
                : $"The UPS answered '{CText.Printable(text)}' with '{CText.Printable(answer.Text)}'.");
        }

        if (command.Leading != '\0' && (reply.Length == 0 || reply[0] != command.Leading))
        {
            return CommandResult.Fail($"The UPS answered '{CText.Printable(text)}' with '{CText.Printable(answer.Text)}'.");
        }

        // A reply is either the protocol's acknowledgement or a refusal (Megatec UPSes echo commands they do not know);
        // silence means the command was taken.
        string value = Extract(reply, command.From, command.To);
        if (value.Length > 0 && !(Protocol.Accepted is { } accepted && string.Equals(value, accepted, StringComparison.OrdinalIgnoreCase)))
        {
            return CommandResult.Fail($"The UPS refused '{CText.Printable(text)}' (reply '{CText.Printable(answer.Text)}').");
        }

        State.DataChanged = true;
        return CommandResult.Ok;
    }

    /// <summary>
    /// beeper.enable / beeper.disable through the toggle: nothing is sent when the beeper is already in the wanted
    /// state. The state is updated at once, so a second request before the next poll does not toggle it back.
    /// </summary>
    private async Task<CommandResult> SetBeeperAsync(bool enable, string? parameter, CancellationToken cancellationToken)
    {
        if (!string.IsNullOrEmpty(parameter))
        {
            return CommandResult.InvalidArgument("The beeper commands take no parameter.");
        }

        if (BeeperEnabled() is not { } enabled)
        {
            return CommandResult.NotSupported("The UPS does not report the beeper state, so only beeper.toggle can be used.");
        }

        if (enabled == enable)
        {
            return CommandResult.Ok;
        }

        CommandResult result = await InstantCommandAsync(BeeperToggle, null, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            State.Values[BeeperStatusName] = enable ? "enabled" : "disabled";
        }

        return result;
    }

    /// <summary>The beeper state from ups.beeper.status: null when unknown.</summary>
    private bool? BeeperEnabled() => State.Get(BeeperStatusName) switch
    {
        "enabled" => true,
        "disabled" or "muted" => false,
        _ => null,
    };

    /// <summary>
    /// Writes ups.delay.start / ups.delay.shutdown: kept by the driver and used by the shutdown commands, rounded to what
    /// the protocol can express (NUT setvar with blazer_process_setvar).
    /// </summary>
    public CommandResult SetVariable(string name, string value)
    {
        (int Min, int Max) range;
        Func<int, int> normalize;
        switch (name)
        {
            case "ups.delay.start":
                range = Protocol.OnDelayRange;
                normalize = Protocol.NormalizeOnDelay;
                break;
            case "ups.delay.shutdown":
                range = Protocol.OffDelayRange;
                normalize = Protocol.NormalizeOffDelay;
                break;
            default:
                return CommandResult.NotSupported($"{name} cannot be written with the {Protocol.Name} protocol.");
        }

        if (!int.TryParse(value.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out int seconds) ||
            seconds < range.Min || seconds > range.Max)
        {
            return CommandResult.Invalid($"{name} must be a whole number of seconds between {range.Min} and {range.Max}.");
        }

        State.Values[name] = normalize(seconds).ToString(CultureInfo.InvariantCulture);
        return CommandResult.Ok;
    }

    /// <summary>The complete set of variables for the current state.</summary>
    internal DriverUpdate BuildUpdate()
    {
        var vars = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in State.Values)
        {
            string name = key switch
            {
                "device.mfr" => "ups.mfr",
                "device.model" => "ups.model",
                "device.serial" => "ups.serial",
                _ => key,
            };
            vars[name] = value;
        }

        IReadOnlyList<string> alarms = State.EffectiveAlarms();
        if (alarms.Count > 0)
        {
            vars["ups.alarm"] = string.Join(' ', alarms);
        }

        vars["ups.status"] = State.FormatStatus(alarms.Count > 0);
        vars["driver.version.data"] = Protocol.Version;
        return new DriverUpdate
        {
            Variables = vars,
            VariableInfo = VariableInfo,
            Commands = Commands,
        };
    }

    private string InitialDelay(int requested, (int Min, int Max) range, Func<int, int> normalize, string option)
    {
        int clamped = Math.Clamp(requested, range.Min, range.Max);
        if (clamped != requested)
        {
            _logger.LogWarning("{Option} = {Value} is outside {Min}-{Max} for the {Protocol} protocol; using {Used}.",
                               option, requested, range.Min, range.Max, Protocol.Name, clamped);
        }

        return normalize(clamped).ToString(CultureInfo.InvariantCulture);
    }

    private string StatusFailure(QxField field) =>
        $"The UPS did not give a valid answer to the status query {CText.Printable(field.Command)} " +
        $"({Protocol.Name} protocol). Check the cable, the protocol and the baud rate.";

    /// <summary>Query (or reuse the reply of the same query in this walk), validate, extract and store one field.</summary>
    private async Task<FieldOutcome> ProcessFieldAsync(QxField field, Dictionary<string, QxAnswer>? cache,
                                                       CancellationToken cancellationToken)
    {
        if (cache is null || !cache.TryGetValue(field.Command, out QxAnswer answer))
        {
            answer = await _link.ExchangeAsync(field.Command, cancellationToken).ConfigureAwait(false);
            if (field.Answer is not null && !answer.IsEmpty)
            {
                answer = field.Answer(answer.Text) is { } processed ? new QxAnswer(processed) : QxAnswer.None;
            }

            // One failed query is not repeated for the other fields of the same reply in this walk.
            cache?.TryAdd(field.Command, answer);
        }

        string text = answer.Text;
        if (Protocol.Rejected is { } rejected && string.Equals(text, rejected, StringComparison.OrdinalIgnoreCase))
        {
            return FieldOutcome.QueryFailed;
        }

        if (field.MinLength > 0 && text.Length < field.MinLength)
        {
            return FieldOutcome.QueryFailed;
        }

        string reply = CString(text);
        if (field.Leading != '\0' && (reply.Length == 0 || reply[0] != field.Leading))
        {
            return FieldOutcome.QueryFailed;
        }

        string raw = Extract(reply, field.From, field.To);
        string value;
        if (field.Process is not null)
        {
            if (!field.Process(field, raw, State, out value))
            {
                _logger.LogDebug("{Protocol}: invalid value '{Raw}' for {Field}.", Protocol.Name, CText.Printable(raw), field);
                return FieldOutcome.Invalid;
            }

            if (field.IsStatus)
            {
                if (value.Length > 0)
                {
                    State.UpdateStatus(value);
                }

                return FieldOutcome.StatusSet;
            }

            if (field.IsAlarm)
            {
                if (value.Length > 0)
                {
                    State.AddAlarm(value);
                }

                return FieldOutcome.StatusSet;
            }
        }
        else
        {
            value = field.Has(QxFlags.Trim) ? CText.TrimChars(raw, "# ") : raw;
            if (!field.Format.IsText)
            {
                if (!CText.IsNumericField(value))
                {
                    _logger.LogDebug("{Protocol}: non-numeric value '{Raw}' for {Field}.", Protocol.Name, CText.Printable(raw), field);
                    return FieldOutcome.Invalid;
                }

                value = field.Format.Apply(CText.StrToD(value));
            }
        }

        if (field.Has(QxFlags.NoNut))
        {
            State.Internal[field.Name] = value;
            return FieldOutcome.ValueSet;
        }

        if (value.Length == 0)
        {
            return FieldOutcome.Blank;
        }

        State.Values[field.Name] = value;
        return FieldOutcome.ValueSet;
    }

    /// <summary>The reply as C code sees it: up to the first NUL.</summary>
    private static string CString(string text)
    {
        int nul = text.IndexOf('\0');
        return nul >= 0 ? text[..nul] : text;
    }

    /// <summary>The characters From..To of the reply, or From..end of line when To is 0 (NUT qx_process_answer).</summary>
    internal static string Extract(string reply, int from, int to)
    {
        if (from >= reply.Length)
        {
            return string.Empty;
        }

        if (to > 0)
        {
            int length = Math.Min(to - from + 1, reply.Length - from);
            return length > 0 ? reply.Substring(from, length) : string.Empty;
        }

        int end = reply.IndexOf('\r');
        if (end < 0)
        {
            end = reply.Length;
        }

        return end > from ? reply[from..end] : string.Empty;
    }

    private enum FieldOutcome
    {
        /// <summary>No reply, a refusal, a short reply or the wrong first character.</summary>
        QueryFailed,

        /// <summary>A well-formed reply whose value does not parse.</summary>
        Invalid,

        /// <summary>A well-formed reply with an empty value.</summary>
        Blank,

        ValueSet,

        StatusSet,
    }
}
