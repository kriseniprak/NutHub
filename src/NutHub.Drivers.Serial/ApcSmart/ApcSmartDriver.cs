using Microsoft.Extensions.Logging;
using NutHub.Core.Model;
using NutHub.Drivers.Serial.Common;
using NutHub.Drivers.Serial.Transport;

namespace NutHub.Drivers.Serial.ApcSmart;

/// <summary>
/// An APC Smart-UPS on its serial port (NUT apcsmart). Every connection starts from scratch (smart mode, command set,
/// capabilities), because a UPS that was power-cycled or had its cable replugged may have fallen back to dumb mode and
/// the firmware may even have been replaced by another unit on the same port.
/// </summary>
internal sealed class ApcSmartDriver : SerialDriverBase
{
    private readonly ApcSmartSettings _settings;
    private ApcSmartProtocol? _protocol;

    public ApcSmartDriver(string upsName, ApcSmartSettings settings, ISerialTransport transport, TimeProvider time, ILogger logger)
        : base(upsName, transport, time, logger)
    {
        _settings = settings;
    }

    /// <summary>The protocol state of the current connection, for tests.</summary>
    internal ApcSmartProtocol? Protocol => _protocol;

    protected override async Task<string?> InitializeAsync(CancellationToken cancellationToken)
    {
        var protocol = new ApcSmartProtocol(Transport, _settings, Time, Logger);
        string? error = await protocol.InitializeAsync(cancellationToken).ConfigureAwait(false);
        if (error is null)
        {
            _protocol = protocol;
            Logger.LogInformation("{Ups}: APC {Model} (firmware {Firmware}) on {Transport}.", UpsName,
                                  protocol.Values.GetValueOrDefault("ups.model", "Smart-UPS"),
                                  protocol.Values.GetValueOrDefault("ups.firmware", "unknown"), Transport.Description);
        }

        return error;
    }

    protected override Task<PollResult> PollCoreAsync(bool recovering, CancellationToken cancellationToken) =>
        _protocol is { } protocol
            ? protocol.PollAsync(recovering, cancellationToken)
            : Task.FromResult(PollResult.Failed("The UPS is not identified."));

    protected override Task<CommandResult> InstantCommandCoreAsync(string command, string? parameter,
                                                                   CancellationToken cancellationToken) =>
        _protocol is { } protocol
            ? protocol.InstantCommandAsync(command, parameter, cancellationToken)
            : Task.FromResult(CommandResult.NotConnected());

    protected override Task<CommandResult> SetVariableCoreAsync(string name, string value, CancellationToken cancellationToken) =>
        _protocol is { } protocol
            ? protocol.SetVariableAsync(name, value, cancellationToken)
            : Task.FromResult(CommandResult.NotConnected());

    protected override void OnDisconnected() => _protocol = null;
}
