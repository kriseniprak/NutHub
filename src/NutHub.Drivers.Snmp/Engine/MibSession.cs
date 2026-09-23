using Lextm.SharpSnmpLib;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Snmp.Mibs;
using NutHub.Drivers.Snmp.Transport;

namespace NutHub.Drivers.Snmp.Engine;

/// <summary>
/// One connection to an agent through one mapping table: what the device answered when connecting (which
/// objects exist, how many phases, which commands and writable objects are available) and the readings built from
/// it every poll. The equivalent of snmp-ups' snmp_ups_walk() in init and update modes.
/// </summary>
/// <remarks>
/// <para>
/// Several entries may give the same NUT variable (a high-resolution object and a coarse one): the first entry, in
/// table order, with a valid value wins. This is what NUT obtains with SU_FLAG_UNIQUE and the order of its tables.
/// </para>
/// <para>
/// Objects the agent does not answer when connecting are not asked in every poll; they are tried again with the
/// semi-static objects every <see cref="SnmpSettings.SemiStaticEvery"/> polls (sensors plugged in later, cards that
/// fill their MIB slowly after a reboot). NUT disables them until the driver restarts.
/// </para>
/// Not thread-safe: the driver serialises polls, commands and writes.
/// </remarks>
internal sealed partial class MibSession
{
    private readonly SnmpClient _client;
    private readonly SnmpSettings _settings;
    private readonly ILogger _logger;

    private readonly Dictionary<string, ISnmpData> _staticValues = new(StringComparer.Ordinal);
    private readonly Dictionary<string, ISnmpData> _slowValues = new(StringComparer.Ordinal);
    private readonly HashSet<string> _unanswered = new(StringComparer.Ordinal);
    private readonly Dictionary<string, SnmpType> _types = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _localSettings = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MibEntry> _commands = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MibComposite> _composites = new(StringComparer.Ordinal);
    private readonly Dictionary<string, MibEntry> _writables = new(StringComparer.Ordinal);

    private IReadOnlyList<MibEntry> _entries = [];
    private IReadOnlyDictionary<string, ISnmpData>? _initialValues;
    private IReadOnlyDictionary<string, string> _lastVariables = new Dictionary<string, string>();
    private long _polls;

    public MibSession(SnmpClient client, MibDefinition mib, SnmpSettings settings, ILogger logger)
    {
        _client = client;
        Mib = mib;
        _settings = settings;
        _logger = logger;
    }

    public MibDefinition Mib { get; }

    /// <summary>Phases of the input, the output and the bypass as found when connecting (1 when not reported).</summary>
    public (int Input, int Output, int Bypass) Phases { get; private set; } = (1, 1, 1);

    /// <summary>The instant commands the device offers (single SETs and composites).</summary>
    public IReadOnlyCollection<string> Commands => _commands.Keys.Concat(_composites.Keys).Distinct().Order(StringComparer.Ordinal).ToArray();

    /// <summary>"ietf MIB 1.55", for driver.version.data.</summary>
    public string DataVersion => $"{Mib.Name} MIB {Mib.Version}";

    /// <summary>
    /// Reads every object of the table once: finds the phases, the objects that exist, the commands and writable
    /// objects available. The values read are also those of the first poll.
    /// </summary>
    public async Task InitializeAsync(CancellationToken cancellationToken)
    {
        var oids = new List<string>();
        foreach (MibEntry entry in Mib.Entries)
        {
            if (!entry.IsAbsent)
            {
                oids.Add(entry.Oid!);
            }

            if (entry.FahrenheitUnitOid is not null)
            {
                oids.Add(Mib_Normalize(entry.FahrenheitUnitOid));
            }
        }

        if (Mib.AlarmTable is { } alarms)
        {
            oids.Add(alarms.CountOid);
        }

        IReadOnlyDictionary<string, ISnmpData> values = await _client.GetAsync(oids, cancellationToken).ConfigureAwait(false);
        foreach (var (oid, data) in values)
        {
            _types[oid] = data.TypeCode;
        }

        Phases = (PhasesOf("input.phases", values), PhasesOf("output.phases", values), PhasesOf("input.bypass.phases", values));
        _entries = Mib.Entries.Where(AppliesToPhases).ToArray();

        foreach (MibEntry entry in _entries)
        {
            if (entry.IsAbsent)
            {
                if (entry.Writable && entry.Default is not null)
                {
                    _localSettings.TryAdd(entry.Name, entry.Default);
                }

                continue;
            }

            string oid = entry.Oid!;
            if (!values.TryGetValue(oid, out ISnmpData? data))
            {
                _unanswered.Add(oid);
                continue;
            }

            if (entry.Has(MibFlags.Static))
            {
                _staticValues[oid] = data;
            }
            else if (entry.Has(MibFlags.SemiStatic))
            {
                _slowValues[oid] = data;
            }

            if (entry.Kind == MibEntryKind.Command)
            {
                _commands.TryAdd(entry.Name, entry);
            }
            else if (entry.Writable)
            {
                _writables.TryAdd(entry.Name, entry);
            }
        }

        foreach (MibComposite composite in Mib.Composites)
        {
            if (!_commands.ContainsKey(composite.Name) && composite.Steps.All(IsAvailable))
            {
                _composites[composite.Name] = composite;
            }
        }

        _initialValues = values;
        _logger.LogDebug(
            "{Target}: {Mib} MIB, {Answered} of {Asked} objects answered, phases in/out/bypass {In}/{Out}/{Bypass}, " +
            "{Commands} commands.",
            _client.Target, Mib.Name, values.Count, oids.Distinct().Count(), Phases.Input, Phases.Output, Phases.Bypass,
            _commands.Count + _composites.Count);
    }

    /// <summary>Reads the device and builds a complete update.</summary>
    public async Task<DriverUpdate> PollAsync(CancellationToken cancellationToken)
    {
        IReadOnlyDictionary<string, ISnmpData> values;
        if (_initialValues is not null)
        {
            values = _initialValues;
            _initialValues = null;
        }
        else
        {
            _polls++;
            bool fullRound = _polls % Math.Max(1, _settings.SemiStaticEvery) == 0;
            List<string> oids = OidsToRead(fullRound);
            values = await _client.GetAsync(oids, cancellationToken).ConfigureAwait(false);
            Remember(oids, values, fullRound);
        }

        var variables = new Dictionary<string, string>(StringComparer.Ordinal);
        var tokens = new List<string>();
        var alarms = new List<string>();
        foreach (MibEntry entry in _entries)
        {
            switch (entry.Kind)
            {
                case MibEntryKind.Command:
                    break;
                case MibEntryKind.Status:
                    if (Value(entry, values) is { } statusData)
                    {
                        tokens.AddRange(MibValueMapper.StatusTokens(entry, statusData));
                    }

                    break;
                case MibEntryKind.Alarm:
                    if (Value(entry, values) is { } alarm && MibValueMapper.AlarmMessage(entry, alarm) is { } message)
                    {
                        alarms.Add(message);
                    }

                    break;
                default:
                    if (!variables.ContainsKey(entry.Name) && Resolve(entry, values) is { Length: > 0 } text)
                    {
                        variables[entry.Name] = text;
                    }

                    break;
            }
        }

        await ReadAlarmTableAsync(values, tokens, alarms, cancellationToken).ConfigureAwait(false);

        // The power source first ("OB LB", not "LB OB"), whatever the table order; NUT clients do not care, people do.
        List<string> status = tokens.Distinct(StringComparer.Ordinal).OrderBy(t => t is "OL" or "OB" ? 0 : 1).ToList();
        List<string> activeAlarms = alarms.Distinct(StringComparer.Ordinal).ToList();
        if (activeAlarms.Count > 0)
        {
            variables["ups.alarm"] = string.Join(' ', activeAlarms);
            status.Insert(0, "ALARM");
        }

        if (status.Count > 0)
        {
            variables["ups.status"] = string.Join(' ', status);
        }

        variables["driver.version.data"] = DataVersion;
        _lastVariables = variables;

        return new DriverUpdate
        {
            Variables = variables,
            VariableInfo = BuildVariableInfo(variables),
            Commands = Commands,
        };
    }

    private List<string> OidsToRead(bool fullRound)
    {
        var oids = new List<string>();
        foreach (MibEntry entry in _entries)
        {
            if (entry.IsAbsent || entry.Kind == MibEntryKind.Command)
            {
                continue;
            }

            string oid = entry.Oid!;
            bool unanswered = _unanswered.Contains(oid);
            bool slow = entry.Has(MibFlags.Static) || entry.Has(MibFlags.SemiStatic);
            if (fullRound ? (unanswered || !entry.Has(MibFlags.Static)) : (!unanswered && !slow))
            {
                oids.Add(oid);
                if (entry.FahrenheitUnitOid is not null)
                {
                    oids.Add(Mib_Normalize(entry.FahrenheitUnitOid));
                }
            }
        }

        if (Mib.AlarmTable is { } alarms)
        {
            oids.Add(alarms.CountOid);
        }

        return oids;
    }

    private void Remember(List<string> asked, IReadOnlyDictionary<string, ISnmpData> values, bool fullRound)
    {
        foreach (var (oid, data) in values)
        {
            _types[oid] = data.TypeCode;
            if (_unanswered.Remove(oid))
            {
                _logger.LogDebug("{Target}: {Oid} answers now.", _client.Target, oid);
            }
        }

        foreach (MibEntry entry in _entries)
        {
            if (entry.IsAbsent || entry.Kind == MibEntryKind.Command || !values.TryGetValue(entry.Oid!, out ISnmpData? data))
            {
                continue;
            }

            if (entry.Has(MibFlags.Static))
            {
                _staticValues[entry.Oid!] = data;
            }
            else if (entry.Has(MibFlags.SemiStatic))
            {
                _slowValues[entry.Oid!] = data;
            }

            if (entry.Writable)
            {
                _writables.TryAdd(entry.Name, entry);
            }
        }

        if (fullRound)
        {
            foreach (string oid in asked)
            {
                if (!values.ContainsKey(oid) && _entries.Any(e => e.Oid == oid && e.Kind != MibEntryKind.Command))
                {
                    _unanswered.Add(oid);
                    _slowValues.Remove(oid);
                }
            }
        }
    }

    /// <summary>The value of an entry in this poll, or the cached one for static and semi-static entries.</summary>
    private ISnmpData? Value(MibEntry entry, IReadOnlyDictionary<string, ISnmpData> values)
    {
        if (entry.IsAbsent)
        {
            return null;
        }

        string oid = entry.Oid!;
        if (values.TryGetValue(oid, out ISnmpData? data))
        {
            return data;
        }

        if (entry.Has(MibFlags.Static) && _staticValues.TryGetValue(oid, out data))
        {
            return data;
        }

        return entry.Has(MibFlags.SemiStatic) && _slowValues.TryGetValue(oid, out data) ? data : null;
    }

    private string? Resolve(MibEntry entry, IReadOnlyDictionary<string, ISnmpData> values)
    {
        if (entry.IsAbsent)
        {
            return entry.Writable && _localSettings.TryGetValue(entry.Name, out string? setting) ? setting : entry.Default;
        }

        ISnmpData? unit = entry.FahrenheitUnitOid is null
            ? null
            : values.GetValueOrDefault(Mib_Normalize(entry.FahrenheitUnitOid));
        string? text = Value(entry, values) is { } data ? MibValueMapper.ToNutValue(entry, data, unit) : null;
        return text ?? (entry.Has(MibFlags.Static) ? entry.Default : null);
    }

    private async Task ReadAlarmTableAsync(IReadOnlyDictionary<string, ISnmpData> values, List<string> tokens,
                                           List<string> alarms, CancellationToken cancellationToken)
    {
        if (Mib.AlarmTable is not { } table || !values.TryGetValue(table.CountOid, out ISnmpData? countData) ||
            !SnmpValues.TryGetInteger(countData, out long count) || count <= 0)
        {
            return;
        }

        IReadOnlyList<Variable> rows;
        try
        {
            rows = await _client.WalkAsync(table.DescriptionColumnOid, (int)Math.Min(count, table.MaxRows), cancellationToken)
                .ConfigureAwait(false);
        }
        catch (SnmpErrorStatusException ex)
        {
            _logger.LogDebug("{Target}: cannot read the alarm table: {Message}", _client.Target, ex.Message);
            return;
        }

        foreach (Variable row in rows)
        {
            if (row.Data is not ObjectIdentifier id)
            {
                continue;
            }

            string alarmOid = SnmpValues.Key(id);
            MibAlarm? known = table.Alarms.FirstOrDefault(a => Mib_Normalize(a.Oid) == alarmOid);
            if (known is null)
            {
                _logger.LogDebug("{Target}: unknown alarm {Oid}.", _client.Target, alarmOid);
                continue;
            }

            if (known.StatusToken is not null)
            {
                tokens.AddRange(known.StatusToken.Split(' ', StringSplitOptions.RemoveEmptyEntries));
            }

            if (known.Message is not null)
            {
                alarms.Add(known.Message);
            }
        }
    }

    private int PhasesOf(string name, IReadOnlyDictionary<string, ISnmpData> values)
    {
        foreach (MibEntry entry in Mib.Entries)
        {
            if (entry.Name == name && !entry.IsAbsent && values.TryGetValue(entry.Oid!, out ISnmpData? data) &&
                SnmpValues.TryGetInteger(data, out long phases) && phases is >= 1 and <= 3)
            {
                return (int)phases;
            }
        }

        return 1;
    }

    private bool AppliesToPhases(MibEntry entry) =>
        Applies(entry, MibFlags.Input1, MibFlags.Input3, Phases.Input) &&
        Applies(entry, MibFlags.Output1, MibFlags.Output3, Phases.Output) &&
        Applies(entry, MibFlags.Bypass1, MibFlags.Bypass3, Phases.Bypass);

    private static bool Applies(MibEntry entry, MibFlags single, MibFlags three, int phases)
    {
        bool one = (entry.Flags & single) != 0;
        bool many = (entry.Flags & three) != 0;
        return (!one && !many) || (one && phases == 1) || (many && phases == 3);
    }

    private bool IsAvailable(MibStep step) =>
        step.CommandName is { } command
            ? _commands.ContainsKey(command)
            : step.VariableName is { } variable && (_writables.ContainsKey(variable) || _localSettings.ContainsKey(variable));

    private Dictionary<string, VariableInfo> BuildVariableInfo(Dictionary<string, string> variables)
    {
        var info = new Dictionary<string, VariableInfo>(StringComparer.Ordinal);
        foreach (var (name, _) in _localSettings)
        {
            if (variables.ContainsKey(name))
            {
                info[name] = IsNumericSetting(name) ? VariableInfo.WritableNumber() : VariableInfo.WritableString(SettingEntry(name)?.MaxLength ?? 32);
            }
        }

        foreach (var (name, entry) in _writables)
        {
            if (!variables.ContainsKey(name) || info.ContainsKey(name))
            {
                continue;
            }

            info[name] = entry.Lookup is not null
                ? VariableInfo.WritableEnum([.. entry.Lookup.Texts])
                : entry.Kind == MibEntryKind.Number
                    ? VariableInfo.WritableNumber()
                    : VariableInfo.WritableString(entry.MaxLength > 0 ? entry.MaxLength : 64);
        }

        return info;
    }

    private MibEntry? SettingEntry(string name) => _entries.FirstOrDefault(e => e.Name == name && e.IsAbsent && e.Writable);

    private bool IsNumericSetting(string name) => NutFormat.TryParseNumber(SettingEntry(name)?.Default, out _);

    private static string Mib_Normalize(string oid) => Mibs.Mib.Normalize(oid);
}
