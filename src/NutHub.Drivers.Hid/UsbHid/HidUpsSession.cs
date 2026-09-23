using System.Globalization;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Drivers.Hid.Descriptors;
using NutHub.Drivers.Hid.Transport;
using NutHub.Drivers.Hid.UsbHid.Subdrivers;

namespace NutHub.Drivers.Hid.UsbHid;

/// <summary>
/// One connected HID UPS: reads the device through the subdriver's mapping table and keeps the NUT variables,
/// status bits, writable variables and commands, like NUT's usbhid-ups (drivers/usbhid-ups.c: hid_ups_walk,
/// ups_infoval_set, instcmd, setvar, HIDGetEvents). Not thread-safe: the driver calls it on the device thread only.
/// </summary>
internal sealed class HidUpsSession : IHidConversionContext
{
    /// <summary>Semi-static values (limits, settings) are re-read this often even without writes.</summary>
    private static readonly TimeSpan SemiStaticRefresh = TimeSpan.FromSeconds(60);

    /// <summary>NUT waits this long between the two halves of shutdown.return, for Tripp Lite firmwares.</summary>
    private static readonly TimeSpan CompositeCommandPause = TimeSpan.FromMilliseconds(125);

    private readonly IHidConnection _device;
    private readonly HidReportDescriptor _descriptor;
    private readonly UsbHidSubdriver _subdriver;
    private readonly ILogger _logger;
    private readonly UpsStatusComposer _composer;
    private readonly IReadOnlyList<HidMapping> _mappings;
    private readonly HidField?[] _fields;
    private readonly Dictionary<string, string> _vars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, VariableInfo> _info = new(StringComparer.Ordinal);
    private readonly List<string> _commands = [];
    private readonly Dictionary<byte, byte[]> _featureReports = [];
    private readonly Dictionary<byte, byte[]> _inputReports = [];
    private readonly List<string> _walkAlarms = [];
    private readonly List<string> _buzzwords = [];
    private Dictionary<byte, byte[]?>? _walkReports;
    private int _walkRequested;
    private int _walkAnswered;
    private UpsStatusBits _bits;
    private ComposedStatus _status = new([], null, [], []);
    private string? _alarm;
    private bool _dataChanged = true;
    private bool _changed;
    private bool _initialized;
    private DateTimeOffset _lastSemiStaticRead;
    private HidField? _currentField;

    public HidUpsSession(IHidConnection device, HidReportDescriptor descriptor, UsbHidSubdriver subdriver,
                         UsbHidSettings settings, TimeProvider time, ILogger logger)
    {
        _device = device ?? throw new ArgumentNullException(nameof(device));
        _descriptor = descriptor ?? throw new ArgumentNullException(nameof(descriptor));
        _subdriver = subdriver ?? throw new ArgumentNullException(nameof(subdriver));
        Settings = settings ?? throw new ArgumentNullException(nameof(settings));
        TimeProvider = time ?? throw new ArgumentNullException(nameof(time));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
        _mappings = subdriver.Mappings;
        _fields = new HidField?[_mappings.Count];
        _composer = new UpsStatusComposer(time, logger, settings.OnlineDischargeOnBattery, settings.OnlineDischargeCalibration,
                                          subdriver.Quirks.LowReplaceDelaySeconds);
    }

    public HidDeviceInfo Device => _device.Device;

    public UsbHidSubdriver Subdriver => _subdriver;

    public ILogger Logger => _logger;

    public TimeProvider TimeProvider { get; }

    public UsbHidSettings Settings { get; }

    public UpsStatusBits StatusBits => _bits;

    public HidField? CurrentField => _currentField;

    /// <summary>Whether input reports should be read: the device has them and the subdriver does not forbid it.</summary>
    public bool UsesInterruptPipe =>
        _device.MaxInputReportLength > 0 &&
        (_subdriver.Quirks.UseInterruptPipe || _subdriver.Quirks.InterruptOnly) &&
        _descriptor.GetReportIds(HidReportKind.Input).Any();

    /// <summary>"APC Back-UPS ES 700 (USB 051d:0002)", for log messages.</summary>
    public string Description =>
        $"{_vars.GetValueOrDefault("ups.mfr") ?? Device.Manufacturer} {_vars.GetValueOrDefault("ups.model") ?? Device.Product} (USB {Device.UsbId})".Trim();

    /// <summary>
    /// Identifies the device and reads everything once (NUT callback + upsdrv_initinfo): repairs the descriptor,
    /// resolves the mapping table against it (the first entry found for a variable wins), finds the supported
    /// commands and the writable variables.
    /// </summary>
    public void Initialize()
    {
        if (_subdriver.FixReportDescriptor(Device, _descriptor))
        {
            _logger.LogDebug("{Ups}: repaired known defects of the report descriptor.", Device.UsbId);
        }

        BeginWalk();
        try
        {
            SetIfPresent("ups.mfr", _subdriver.FormatManufacturer(Device, this));
            SetIfPresent("ups.model", _subdriver.FormatModel(Device, this));
            SetIfPresent("ups.serial", _subdriver.FormatSerial(Device, this));
        }
        finally
        {
            EndWalk(checkAnswers: false);
        }

        _vars["ups.vendorid"] = Device.VendorId.ToString("x4", CultureInfo.InvariantCulture);
        _vars["ups.productid"] = Device.ProductId.ToString("x4", CultureInfo.InvariantCulture);
        _vars["driver.version.data"] = _subdriver.Version;

        Walk(initial: true);

        if (_bits == UpsStatusBits.None)
        {
            _logger.LogWarning(
                "{Ups}: the device reported no status flag; the '{Subdriver}' subdriver may not suit it (try another " +
                "value of the 'subdriver' option).", Description, _subdriver.Id);
        }

        if (Settings.OnDelay is int onDelay && _vars.ContainsKey("ups.delay.start"))
        {
            _vars["ups.delay.start"] = onDelay.ToString(CultureInfo.InvariantCulture);
        }

        if (Settings.OffDelay is int offDelay && _vars.ContainsKey("ups.delay.shutdown"))
        {
            _vars["ups.delay.shutdown"] = offDelay.ToString(CultureInfo.InvariantCulture);
        }

        // Commands made of several HID writes, offered when their parts answered (NUT upsdrv_initinfo).
        bool offDelayCommand = _commands.Contains("load.off.delay", StringComparer.Ordinal);
        bool onDelayCommand = _commands.Contains("load.on.delay", StringComparer.Ordinal);
        if (offDelayCommand)
        {
            AddCommand("load.off");
        }

        if (onDelayCommand)
        {
            AddCommand("load.on");
        }

        if (offDelayCommand && onDelayCommand)
        {
            AddCommand("shutdown.return");
            AddCommand("shutdown.stayoff");
        }

        ComposeStatus(full: true);
        _initialized = true;
    }

    /// <summary>
    /// Reads the device (one request per report) and recomputes the status. Throws
    /// <see cref="HidDeviceLostException"/> when the device answered none of the requests.
    /// </summary>
    public void Update()
    {
        if (!_initialized)
        {
            throw new InvalidOperationException("The session is not initialised.");
        }

        Walk(initial: false);
        ComposeStatus(full: true);
    }

    /// <summary>
    /// Applies an input report from the interrupt pipe (NUT HIDGetEvents): each input field updates the variable
    /// mapped to the feature field of the same path. Returns true when a variable or the status changed, so the
    /// driver can publish at once instead of waiting for the next poll.
    /// </summary>
    public bool ProcessInputReport(ReadOnlySpan<byte> report)
    {
        if (report.IsEmpty || !_initialized)
        {
            return false;
        }

        byte reportId = _descriptor.UsesReportIds ? report[0] : (byte)0;
        byte[] data = report.ToArray();
        if (!_descriptor.UsesReportIds)
        {
            data[0] = 0;
        }

        _inputReports[reportId] = data;
        _changed = false;
        UpsStatusBits before = _bits;
        IReadOnlyList<string> tokensBefore = _status.Tokens;
        bool interruptOnly = _subdriver.Quirks.InterruptOnly;
        foreach (HidField field in _descriptor.GetFields(HidReportKind.Input, reportId))
        {
            double value = HidValueCodec.GetValue(data, field);
            HidField target = interruptOnly ? field : _descriptor.Find(field.Path, HidReportKind.Feature) ?? field;
            int index = FindMappingByField(target);
            if (index >= 0)
            {
                SetInfoValue(_mappings[index], field, value);
            }
        }

        if (_bits == before && !_changed)
        {
            return false;
        }

        ComposeStatus(full: false);
        return _changed || !_status.Tokens.SequenceEqual(tokensBefore);
    }

    /// <summary>Executes an instant command (NUT instcmd), including the composite load.* and shutdown.* ones.</summary>
    public CommandResult ExecuteCommand(string command, string? parameter)
    {
        ArgumentNullException.ThrowIfNull(command);
        if (string.Equals(command, "beeper.off", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteCommand("beeper.disable", null);
        }

        if (string.Equals(command, "beeper.on", StringComparison.OrdinalIgnoreCase))
        {
            return ExecuteCommand("beeper.enable", null);
        }

        int index = FindMapping(command, command: true);
        if (index < 0)
        {
            return ExecuteComposite(command);
        }

        HidMapping mapping = _mappings[index];
        string? text = parameter ?? mapping.Default;
        if (text is null && mapping.Has(HidMapFlags.ParamRequired))
        {
            return CommandResult.InvalidArgument($"The command '{command}' needs a value.");
        }

        double value;
        if (mapping.Lookup is not null)
        {
            if (mapping.Lookup.ToHid(text, this) is not double converted)
            {
                return CommandResult.InvalidArgument($"The command '{command}' does not accept '{text}'.");
            }

            value = converted;
        }
        else if (text is null)
        {
            value = 0;
        }
        else if (!TryParseNumber(text, out value))
        {
            return CommandResult.InvalidArgument($"The command '{command}' needs a number, not '{text}'.");
        }

        _logger.LogInformation("{Ups}: running '{Command}' (writing {Value} to {Path}).", Description, command,
                               value.ToString(CultureInfo.InvariantCulture), mapping.Path);
        return Write(_fields[index]!, value);
    }

    /// <summary>Writes a variable (NUT setvar): driver-kept variables in memory, the others to the device.</summary>
    public CommandResult WriteVariable(string name, string value)
    {
        ArgumentNullException.ThrowIfNull(name);
        ArgumentNullException.ThrowIfNull(value);
        int index = FindMapping(name, command: false);
        if (index < 0)
        {
            return CommandResult.NotSupported($"The variable '{name}' is not supported by this UPS.");
        }

        HidMapping mapping = _mappings[index];
        if (!mapping.IsWritable)
        {
            return new CommandResult(CommandStatus.ReadOnly, $"The variable '{name}' is read-only.");
        }

        if (mapping.IsAbsent)
        {
            if (!TryParseNumber(value, out _))
            {
                return CommandResult.Invalid($"'{value}' is not a number.");
            }

            _vars[mapping.Name] = value.Trim();
            return CommandResult.Ok;
        }

        double hid;
        if (mapping.Lookup is not null)
        {
            if (mapping.Lookup.ToHid(value, this) is not double converted)
            {
                return CommandResult.Invalid($"'{value}' is not an accepted value of {name}.");
            }

            hid = converted;
        }
        else if (!TryParseNumber(value, out hid))
        {
            return CommandResult.Invalid($"'{value}' is not a number.");
        }

        return Write(_fields[index]!, hid);
    }

    /// <summary>The complete current reading, in the form the NutHub core expects.</summary>
    public DriverUpdate BuildUpdate()
    {
        var vars = new Dictionary<string, string>(_vars.Count + 4, StringComparer.Ordinal);
        foreach (var (name, value) in _vars)
        {
            if (NutFormat.IsValidVariableName(name))
            {
                vars[name] = value;
            }
        }

        string status = string.Join(' ', _status.Tokens);
        if (_alarm is not null)
        {
            vars["ups.alarm"] = _alarm;
            status = status.Length == 0 ? "ALARM" : "ALARM " + status;
        }

        vars["ups.status"] = status;
        if (_status.TransferReason is not null)
        {
            vars["input.transfer.reason"] = _status.TransferReason;
        }

        var buzzwords = _status.Buzzwords.Concat(_buzzwords).Distinct(StringComparer.Ordinal).ToList();
        if (buzzwords.Count > 0)
        {
            vars["experimental.ups.mode.buzzwords"] = string.Join(' ', buzzwords);
        }

        return new DriverUpdate
        {
            Variables = vars,
            VariableInfo = new Dictionary<string, VariableInfo>(_info, StringComparer.Ordinal),
            Commands = _commands.ToArray(),
        };
    }

    // ----- IHidConversionContext -------------------------------------------------------------------------------

    public string? GetVariable(string name) => _vars.GetValueOrDefault(name);

    void IHidConversionContext.SetVariable(string name, string value) => SetVar(name, value);

    public void RemoveVariable(string name) => _changed |= _vars.Remove(name);

    public void AddBuzzword(string word)
    {
        if (!_buzzwords.Contains(word))
        {
            _buzzwords.Add(word);
        }
    }

    public string? GetIndexedString(int index)
    {
        try
        {
            return _device.GetIndexedString(index);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException && ex is not HidDeviceLostException)
        {
            return null;
        }
    }

    public bool TryReadValue(string path, out double value)
    {
        value = 0;
        HidField? field = Resolve(path);
        return field is not null && TryRead(field, out value);
    }

    public string? ReadItemString(string path) =>
        TryReadValue(path, out double index) ? GetIndexedString((int)HidValueCodec.Truncate(index)) : null;

    public ReadOnlySpan<byte> GetReportBuffer(HidReportKind kind, byte reportId)
    {
        Dictionary<byte, byte[]> reports = kind == HidReportKind.Input ? _inputReports : _featureReports;
        return reports.TryGetValue(reportId, out byte[]? data) ? data : [];
    }

    // ----- Walk ------------------------------------------------------------------------------------------------

    private void Walk(bool initial)
    {
        DateTimeOffset now = TimeProvider.GetUtcNow();
        bool pollOnly = !UsesInterruptPipe;
        bool readStatic = initial || pollOnly;
        bool readSemiStatic = initial || pollOnly || _dataChanged || now - _lastSemiStaticRead >= SemiStaticRefresh;
        _walkAlarms.Clear();
        _buzzwords.Clear();
        BeginWalk();
        try
        {
            for (int i = 0; i < _mappings.Count; i++)
            {
                HidMapping mapping = _mappings[i];
                HidField? field;
                if (initial)
                {
                    field = Resolve(mapping.Path);
                    if (field is null)
                    {
                        continue;
                    }

                    if (mapping.IsAbsent)
                    {
                        _fields[i] = field;
                        if (!_vars.ContainsKey(mapping.Name))
                        {
                            _vars[mapping.Name] = mapping.Default ?? "";
                            SetInfo(mapping);
                        }

                        continue;
                    }

                    // The first entry the device has wins; alarms, status flags and commands may repeat.
                    if (!mapping.IsAlarm && !mapping.IsCommand && _vars.ContainsKey(mapping.Name))
                    {
                        continue;
                    }

                    _fields[i] = field;
                }
                else
                {
                    field = _fields[i];
                    if (field is null || mapping.IsAbsent || mapping.IsCommand ||
                        (mapping.Has(HidMapFlags.Static) && !readStatic) ||
                        (mapping.Has(HidMapFlags.SemiStatic) && !readSemiStatic))
                    {
                        continue;
                    }
                }

                // Tripp Lite SU3000LCD2UHV: report 0x54 hangs the firmware (NUT hid_ups_walk).
                if (Device.VendorId == 0x09ae && Device.ProductId == 0x1330 && field.ReportId == 0x54)
                {
                    continue;
                }

                if (!TryRead(field, out double value))
                {
                    continue;
                }

                if (mapping.IsCommand)
                {
                    AddCommand(mapping.Name);
                    continue;
                }

                if (SetInfoValue(mapping, field, value) == 1 && initial)
                {
                    SetInfo(mapping, field);
                }
            }
        }
        finally
        {
            EndWalk(checkAnswers: !initial && !_subdriver.Quirks.InterruptOnly);
        }

        if (readSemiStatic)
        {
            _lastSemiStaticRead = now;
        }

        _dataChanged = false;
    }

    private void BeginWalk()
    {
        _walkReports = [];
        _walkRequested = 0;
        _walkAnswered = 0;
    }

    private void EndWalk(bool checkAnswers)
    {
        _walkReports = null;
        if (checkAnswers && _walkRequested > 0 && _walkAnswered == 0)
        {
            throw new HidDeviceLostException(
                $"The UPS answered none of the {_walkRequested} report requests; it is probably disconnected.");
        }
    }

    /// <summary>Converts and stores one value (NUT ups_infoval_set): -1 no valid value, 0 status/alarm, 1 variable.</summary>
    private int SetInfoValue(HidMapping mapping, HidField field, double value)
    {
        _currentField = field;
        try
        {
            string? text = null;
            if (mapping.Lookup is not null)
            {
                text = mapping.Lookup.ToNut(value, this);
                if (text is null)
                {
                    return -1;
                }
            }

            if (text is not null)
            {
                if (mapping.IsStatusFlag)
                {
                    _bits = UpsStatusBitNames.Apply(_bits, text);
                    return 0;
                }

                if (mapping.IsAlarm)
                {
                    _walkAlarms.Add(text);
                    return 0;
                }

                SetVar(mapping.Name, text);
                return 1;
            }

            text = CFormat.FormatDouble(mapping.Default, value);
            if (text is null)
            {
                return -1;
            }

            SetVar(mapping.Name, text);
            return 1;
        }
        catch (Exception ex) when (ex is not HidDeviceLostException and not UnauthorizedAccessException)
        {
            _logger.LogWarning(ex, "{Ups}: converting {Path} for {Variable} failed.", Device.UsbId, mapping.Path, mapping.Name);
            return -1;
        }
        finally
        {
            _currentField = null;
        }
    }

    /// <summary>
    /// Publishes the type information of a variable (NUT dstate_setflags / setaux / addenum). Unlike NUT, a variable
    /// whose field the descriptor declares constant is published read-only, since the write would be refused.
    /// </summary>
    private void SetInfo(HidMapping mapping, HidField? field = null)
    {
        if (mapping.IsWritable && !(field?.IsReadOnlyConstant ?? false))
        {
            IReadOnlyList<string> values = mapping.Has(HidMapFlags.Enumerated) && mapping.Lookup is not null
                ? mapping.Lookup.GetEnumValues(this)
                : [];
            _info[mapping.Name] = values.Count > 0 ? VariableInfo.WritableEnum([.. values])
                : mapping.Has(HidMapFlags.Str) ? VariableInfo.WritableString(Math.Max(mapping.Length, 0))
                : VariableInfo.WritableNumber();
        }
        else if (mapping.Has(HidMapFlags.Str))
        {
            _info[mapping.Name] = new VariableInfo { Type = VariableType.String, MaxLength = Math.Max(mapping.Length, 0) };
        }
    }

    private void ComposeStatus(bool full)
    {
        _status = _composer.Compose(_bits, _vars.GetValueOrDefault("battery.charge"), Description);
        if (full)
        {
            // ups.alarm is rebuilt on full readings only, like NUT (alarm_init / alarm_commit).
            List<string> alarms = [.. _walkAlarms, .. _status.Alarms];
            _alarm = alarms.Count > 0 ? string.Join(' ', alarms) : null;
        }
    }

    // ----- Device access ---------------------------------------------------------------------------------------

    private HidField? Resolve(string? path)
    {
        if (path is null)
        {
            return null;
        }

        if (!HidPath.TryParse(path, _subdriver.Usages, out HidPath parsed))
        {
            _logger.LogDebug("{Ups}: cannot parse the HID path '{Path}'.", Device.UsbId, path);
            return null;
        }

        return _descriptor.Find(parsed, _subdriver.Quirks.InterruptOnly ? HidReportKind.Input : HidReportKind.Feature);
    }

    private bool TryRead(HidField field, out double value)
    {
        value = 0;
        byte[]? report = GetReport(field);
        if (report is null)
        {
            return false;
        }

        value = HidValueCodec.GetValue(report, field);
        return true;
    }

    private byte[]? GetReport(HidField field)
    {
        if (field.Kind == HidReportKind.Input)
        {
            // Interrupt-only devices: values come from the last input report of that id, if one arrived.
            return _inputReports.GetValueOrDefault(field.ReportId);
        }

        byte id = field.ReportId;
        if (_walkReports is not null && _walkReports.TryGetValue(id, out byte[]? cached))
        {
            return cached;
        }

        byte[]? report = null;
        _walkRequested++;
        try
        {
            report = _device.GetFeature(id, ReadLength(id));

            // Windows pads to the longest report of the collection; keep what the descriptor declares, so vendor
            // conversions that decode whole reports see the same bytes on every platform.
            int declared = 1 + _descriptor.GetReportLength(HidReportKind.Feature, id);
            if (declared > 1 && report.Length > declared)
            {
                report = report[..declared];
            }

            _featureReports[id] = report;
            _walkAnswered++;
        }
        catch (Exception ex) when (ex is IOException or TimeoutException && ex is not HidDeviceLostException)
        {
            // Some firmwares fail single reports now and then; skip them rather than reconnect (NUT does the same).
            _logger.LogDebug("{Ups}: reading report 0x{Report:x2} failed: {Message}", Device.UsbId, id, ex.Message);
        }

        if (_walkReports is not null)
        {
            _walkReports[id] = report;
        }

        return report;
    }

    private int ReadLength(byte reportId)
    {
        int length = 1 + _descriptor.GetReportLength(HidReportKind.Feature, reportId);
        return _subdriver.Quirks.LargeReportBuffer ? Math.Max(length, 64) : length;
    }

    /// <summary>
    /// Writes a physical value into its feature report: read-modify-write, so the other fields of the report keep
    /// the values the device has now (NUT HIDSetDataValue).
    /// </summary>
    private CommandResult Write(HidField field, double physical)
    {
        if (field.Kind != HidReportKind.Feature || field.IsReadOnlyConstant)
        {
            return CommandResult.Fail($"The HID field {field.Path.ToString(_subdriver.Usages)} cannot be written.");
        }

        byte id = field.ReportId;
        int length = 1 + _descriptor.GetReportLength(HidReportKind.Feature, id);
        byte[] report;
        try
        {
            report = _device.GetFeature(id, ReadLength(id));
        }
        catch (Exception ex) when (ex is IOException or TimeoutException && ex is not HidDeviceLostException)
        {
            _logger.LogDebug("{Ups}: reading report 0x{Report:x2} before writing it failed: {Message}", Device.UsbId, id, ex.Message);
            report = _featureReports.GetValueOrDefault(id) ?? new byte[length];
        }

        var buffer = new byte[length];
        report.AsSpan(0, Math.Min(report.Length, length)).CopyTo(buffer);
        buffer[0] = id;
        HidValueCodec.SetLogical(buffer, field, HidValueCodec.ToLogical(field, physical));
        try
        {
            _device.SetFeature(buffer);
        }
        catch (Exception ex) when (ex is IOException or TimeoutException && ex is not HidDeviceLostException)
        {
            return CommandResult.Fail($"The UPS did not accept the request: {ex.Message}");
        }

        _featureReports.Remove(id);
        _dataChanged = true;
        return CommandResult.Ok;
    }

    private CommandResult ExecuteComposite(string command)
    {
        switch (command.ToLowerInvariant())
        {
            case "load.on":
                return FindMapping("load.on.delay", command: true) >= 0
                    ? ExecuteCommand("load.on.delay", "0")
                    : CommandResult.NotSupported();
            case "load.off":
                return FindMapping("load.off.delay", command: true) >= 0
                    ? ExecuteCommand("load.off.delay", "0")
                    : CommandResult.NotSupported();
            case "shutdown.return":
            case "shutdown.stayoff":
            {
                bool stayOff = command.Equals("shutdown.stayoff", StringComparison.OrdinalIgnoreCase);
                if (FindMapping("load.on.delay", command: true) < 0 || FindMapping("load.off.delay", command: true) < 0)
                {
                    return CommandResult.NotSupported();
                }

                if (_vars.ContainsKey("ups.start.auto"))
                {
                    _ = WriteVariable("ups.start.auto", stayOff ? "no" : "yes");
                }

                // Arm the restart first (or cancel it with -1), then the shutdown.
                CommandResult result = ExecuteCommand("load.on.delay", stayOff ? "-1" : _vars.GetValueOrDefault("ups.delay.start"));
                if (!result.IsSuccess)
                {
                    return result;
                }

                Thread.Sleep(CompositeCommandPause);
                return ExecuteCommand("load.off.delay", _vars.GetValueOrDefault("ups.delay.shutdown"));
            }

            default:
                return CommandResult.NotSupported($"The UPS does not support the command '{command}'.");
        }
    }

    // ----- Helpers ---------------------------------------------------------------------------------------------

    /// <summary>The first table entry for a name whose path the device has (NUT find_nut_info).</summary>
    private int FindMapping(string name, bool command)
    {
        for (int i = 0; i < _mappings.Count; i++)
        {
            if (_fields[i] is not null && _mappings[i].IsCommand == command &&
                string.Equals(_mappings[i].Name, name, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>The entry reading a given field (NUT find_hid_info), ignoring commands and driver-kept values.</summary>
    private int FindMappingByField(HidField field)
    {
        for (int i = 0; i < _mappings.Count; i++)
        {
            if (ReferenceEquals(_fields[i], field) && !_mappings[i].IsAbsent && !_mappings[i].IsCommand &&
                !_mappings[i].IsAlarm)
            {
                return i;
            }
        }

        return -1;
    }

    private void AddCommand(string name)
    {
        if (!_commands.Contains(name, StringComparer.Ordinal))
        {
            _commands.Add(name);
        }
    }

    private void SetVar(string name, string value)
    {
        if (!_vars.TryGetValue(name, out string? old) || old != value)
        {
            _vars[name] = value;
            _changed = true;
        }
    }

    private void SetIfPresent(string name, string? value)
    {
        if (!string.IsNullOrWhiteSpace(value))
        {
            _vars[name] = value.Trim();
        }
    }

    private static bool TryParseNumber(string text, out double value) =>
        double.TryParse(text.Trim(), NumberStyles.Float, CultureInfo.InvariantCulture, out value) &&
        double.IsFinite(value);
}
