using Microsoft.Extensions.Logging;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.Common;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.Megatec;

/// <summary>
/// A Megatec/Q1-family UPS (NUT nutdrv_qx, blazer_ser, blazer_usb). Finds the protocol (or uses the configured one),
/// then polls it; the protocol found is tried first after a reconnection, and delays written by clients survive it.
/// </summary>
internal sealed class MegatecDriver : SerialDriverBase
{
    private readonly MegatecSettings _settings;
    private readonly IQxLink _link;
    private readonly QxProtocolOptions _protocolOptions;
    private readonly Dictionary<string, string> _writtenDelays = new(StringComparer.Ordinal);
    private string? _detectedProtocol;
    private QxEngine? _engine;

    public MegatecDriver(string upsName, MegatecSettings settings, ISerialTransport transport, TimeProvider time, ILogger logger)
        : base(upsName, transport, time, logger)
    {
        _settings = settings;
        _link = new QxTransportLink(transport, settings.ReplyTimeout, logger);
        _protocolOptions = new QxProtocolOptions(settings.NoRating, settings.NoVendor, settings.IgnoreShutdownActive);
    }

    /// <summary>The protocol in use, once identified.</summary>
    internal string? ProtocolName => _engine?.Protocol.Name;

    internal QxEngine? Engine => _engine;

    protected override async Task<string?> InitializeAsync(CancellationToken cancellationToken)
    {
        bool auto = _settings.Protocol == QxProtocols.Auto;
        IEnumerable<string> candidates = auto
            ? _detectedProtocol is null
                ? QxProtocols.AutoOrder
                : QxProtocols.AutoOrder.Where(p => p != _detectedProtocol).Prepend(_detectedProtocol)
            : [_settings.Protocol];

        // NUT gives every subdriver MAXTRIES (3) attempts; when probing blindly two are enough and halve the time a
        // silent UPS takes to go through the whole list.
        int tries = auto && _detectedProtocol is null ? 2 : 3;
        foreach (string name in candidates)
        {
            for (int attempt = 1; attempt <= tries; attempt++)
            {
                var engine = new QxEngine(QxProtocols.Create(name, _protocolOptions), _link, _settings, Time, Logger);
                if (!await engine.ClaimAsync(cancellationToken).ConfigureAwait(false))
                {
                    continue;
                }

                string? error = await engine.InitializeAsync(cancellationToken).ConfigureAwait(false);
                if (error is not null)
                {
                    return error;
                }

                foreach (var (variable, value) in _writtenDelays)
                {
                    engine.SetVariable(variable, value);
                }

                if (auto && _detectedProtocol != name)
                {
                    Logger.LogInformation("{Ups}: the UPS on {Transport} speaks the '{Protocol}' protocol; set it explicitly " +
                                          "in the UPS options to skip the detection.", UpsName, Transport.Description, name);
                }

                _detectedProtocol = name;
                _engine = engine;
                return null;
            }
        }

        return auto
            ? $"No valid answer from a Megatec/Q1 UPS on {Transport.Description} in any known protocol. Check the cable " +
              "(a straight RS-232 cable, or the one supplied with the UPS), the port, and the baud rate (2400 for almost " +
              "all these UPSes); a UPS that is switched off does not answer either."
            : $"The UPS on {Transport.Description} does not answer in the '{_settings.Protocol}' protocol. Check the " +
              "cable and the baud rate, or set the protocol to automatic detection.";
    }

    protected override Task<PollResult> PollCoreAsync(bool recovering, CancellationToken cancellationToken) =>
        Engine is { } engine ? engine.PollAsync(cancellationToken) : Task.FromResult(PollResult.Failed("The UPS is not identified."));

    protected override Task<CommandResult> InstantCommandCoreAsync(string command, string? parameter,
                                                                   CancellationToken cancellationToken) =>
        Engine is { } engine
            ? engine.InstantCommandAsync(command, parameter, cancellationToken)
            : Task.FromResult(CommandResult.NotConnected());

    protected override Task<CommandResult> SetVariableCoreAsync(string name, string value, CancellationToken cancellationToken)
    {
        if (Engine is not { } engine)
        {
            return Task.FromResult(CommandResult.NotConnected());
        }

        CommandResult result = engine.SetVariable(name, value);
        if (result.IsSuccess)
        {
            _writtenDelays[name] = value;
        }

        return Task.FromResult(result);
    }

    protected override void OnDisconnected() => _engine = null;
}
