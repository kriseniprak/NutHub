using Microsoft.Extensions.Logging;
using NutHub.Core.Model;

namespace NutHub.Core.Drivers;

/// <summary>The operating systems a driver works on.</summary>
[Flags]
public enum DriverPlatforms
{
    None = 0,
    Windows = 1,
    Linux = 2,
    MacOS = 4,
    All = Windows | Linux | MacOS,
}

/// <summary>
/// Describes a kind of driver (USB HID, SNMP, Megatec serial...) and creates driver instances for configured UPSes.
/// Register implementations as singletons of this interface; the driver manager and the web panel find them there.
/// </summary>
public interface IUpsDriverFactory
{
    /// <summary>Stable identifier stored in the configuration, lower case: "usbhid", "snmp", "megatec"...</summary>
    string Id { get; }

    /// <summary>Short name for the web panel: "USB HID Power Device".</summary>
    string DisplayName { get; }

    /// <summary>One or two sentences for the web panel: which devices this driver is for.</summary>
    string Description { get; }

    /// <summary>The operating systems on which this driver can work.</summary>
    DriverPlatforms Platforms { get; }

    /// <summary>The configuration options, in display order; the web panel builds its form from them.</summary>
    IReadOnlyList<DriverOption> Options { get; }

    /// <summary>Whether <see cref="DiscoverAsync"/> can find devices (USB enumeration, serial port list...).</summary>
    bool SupportsDiscovery { get; }

    /// <summary>
    /// Finds candidate devices. Each result carries the option values that select it. Must not throw for "nothing
    /// found"; return an empty list.
    /// </summary>
    Task<IReadOnlyList<DiscoveredDevice>> DiscoverAsync(CancellationToken cancellationToken);

    /// <summary>
    /// Creates a driver for one UPS. Validate the options here and throw <see cref="DriverConfigurationException"/>
    /// for invalid ones: the UPS is then marked failed until its configuration changes, instead of retrying.
    /// </summary>
    IUpsDriver Create(DriverCreateContext context);

    /// <summary>
    /// Checks the options the way <see cref="Create"/> does, without opening a device or a connection, so a value the
    /// driver would refuse is refused while it is being saved instead of leaving behind a UPS that never starts.
    /// Throws <see cref="DriverConfigurationException"/> naming the option at fault. The default accepts everything:
    /// a driver that does not override it is still checked when it starts.
    /// </summary>
    void ValidateOptions(DriverOptionReader read)
    {
    }
}

/// <summary>
/// A running driver for one UPS. The driver manager calls <see cref="RunAsync"/> once; commands and variable writes
/// may arrive concurrently from other threads while it runs.
/// </summary>
public interface IUpsDriver : IAsyncDisposable
{
    /// <summary>
    /// Connects to the device and publishes its data every poll interval until <paramref name="cancellationToken"/>
    /// is cancelled. Transient failures (device unplugged, timeout) must be handled inside: report them through
    /// <see cref="IDriverContext.ReportDisconnected"/> and keep retrying with a delay. Throwing ends this run; the
    /// manager then restarts the driver after a back-off (or waits for a configuration change for a
    /// <see cref="DriverConfigurationException"/>). Return only when cancelled.
    /// </summary>
    Task RunAsync(IDriverContext context, CancellationToken cancellationToken);

    /// <summary>
    /// Executes an instant command ("test.battery.start.quick", "load.off.delay" with a parameter...). Only called for
    /// commands the driver published in <see cref="DriverUpdate.Commands"/>.
    /// </summary>
    Task<CommandResult> InstantCommandAsync(string command, string? parameter, CancellationToken cancellationToken);

    /// <summary>
    /// Writes a variable. Only called for variables the driver published as writable, with a value already checked
    /// against their enum / range / maximum length.
    /// </summary>
    Task<CommandResult> SetVariableAsync(string name, string value, CancellationToken cancellationToken);
}

/// <summary>What a driver receives when it is created.</summary>
public sealed class DriverCreateContext
{
    public required string UpsName { get; init; }

    /// <summary>The configured options, with secrets already decrypted. Keys are case-insensitive.</summary>
    public required IReadOnlyDictionary<string, string> Options { get; init; }

    public required TimeSpan PollInterval { get; init; }

    public required ILoggerFactory LoggerFactory { get; init; }

    public required TimeProvider TimeProvider { get; init; }

    /// <summary>The application services, for drivers that need more than the above.</summary>
    public required IServiceProvider Services { get; init; }

    /// <summary>Typed access to <see cref="Options"/> with validation errors that name the option.</summary>
    public DriverOptionReader Read => new(Options);
}

/// <summary>The channel through which a running driver reports to NutHub.</summary>
public interface IDriverContext
{
    string UpsName { get; }

    ILogger Logger { get; }

    TimeProvider TimeProvider { get; }

    TimeSpan PollInterval { get; }

    /// <summary>The driver is trying to reach the device (initial connection or reconnection).</summary>
    void ReportConnecting(string? detail = null);

    /// <summary>
    /// The device stopped answering. The data becomes stale immediately (NUT clients get ERR DATA-STALE) and a
    /// "communication lost" event is raised. Keep retrying; the next <see cref="Publish"/> restores the state.
    /// </summary>
    void ReportDisconnected(string reason);

    /// <summary>
    /// Publishes a complete, fresh reading of the device and marks the driver connected. Call it every poll even
    /// when nothing changed: it is also the heartbeat that keeps the data from going stale.
    /// </summary>
    void Publish(DriverUpdate update);
}

/// <summary>A complete reading published by a driver.</summary>
public sealed class DriverUpdate
{
    /// <summary>
    /// All variables the device reports now (NUT names: "battery.charge", "ups.status"...), values formatted with
    /// <see cref="NutFormat"/>. Variables missing here disappear from the UPS. Do not set driver.* variables: NutHub
    /// adds driver.name, driver.version and driver.parameter.* itself (driver.version.data and driver.version.internal
    /// are the exceptions a driver may set).
    /// </summary>
    public required IReadOnlyDictionary<string, string> Variables { get; init; }

    /// <summary>Metadata of writable or typed variables; null keeps what was published before.</summary>
    public IReadOnlyDictionary<string, VariableInfo>? VariableInfo { get; init; }

    /// <summary>The supported instant commands; null keeps what was published before.</summary>
    public IReadOnlyCollection<string>? Commands { get; init; }
}

/// <summary>A device found by <see cref="IUpsDriverFactory.DiscoverAsync"/>.</summary>
/// <param name="Title">What the user sees first: "APC Back-UPS ES 700 (USB 051d:0002)".</param>
/// <param name="Detail">A second line: serial number, device path...</param>
/// <param name="Options">The option values that select this device, to pre-fill the form.</param>
/// <param name="SuggestedName">A UPS name suggestion, e.g. "backups".</param>
public sealed record DiscoveredDevice(
    string Title,
    string? Detail,
    IReadOnlyDictionary<string, string> Options,
    string? SuggestedName = null);

/// <summary>Thrown by a driver factory or driver for a configuration that cannot work; not retried.</summary>
public sealed class DriverConfigurationException(string message, string? optionKey = null) : Exception(message)
{
    /// <summary>The option at fault, when there is one.</summary>
    public string? OptionKey { get; } = optionKey;
}
