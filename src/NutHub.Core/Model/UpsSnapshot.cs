using System.Collections.Immutable;

namespace NutHub.Core.Model;

/// <summary>
/// An immutable, consistent view of one UPS at a point in time. Every consumer (NUT protocol, web panel,
/// history, notifications) reads snapshots; the registry replaces the snapshot atomically whenever anything changes.
/// </summary>
public sealed class UpsSnapshot
{
    public static readonly ImmutableSortedDictionary<string, string> NoVariables =
        ImmutableSortedDictionary.Create<string, string>(StringComparer.Ordinal);

    public static readonly ImmutableDictionary<string, VariableInfo> NoVariableInfo =
        ImmutableDictionary.Create<string, VariableInfo>(StringComparer.Ordinal);

    /// <summary>The UPS name, as used in the NUT protocol ("ups@host").</summary>
    public required string Name { get; init; }

    /// <summary>The description ("desc" in ups.conf); null when not set.</summary>
    public string? Description { get; init; }

    /// <summary>The id of the driver factory, e.g. "usbhid".</summary>
    public required string DriverId { get; init; }

    public DriverState DriverState { get; init; }

    /// <summary>A human-readable detail about <see cref="DriverState"/> (last error, device path...).</summary>
    public string? DriverMessage { get; init; }

    /// <summary>Whether the data can be served, see <see cref="DataAvailability"/>.</summary>
    public DataAvailability Availability { get; init; } = DataAvailability.DriverNotConnected;

    /// <summary>True when a forced shutdown was set on this UPS by a client or an operator.</summary>
    public bool ForcedShutdown { get; init; }

    /// <summary>The flags of the effective "ups.status" (including FSD and synthesized LB).</summary>
    public UpsStatusFlags Status { get; init; }

    /// <summary>
    /// All variables, including the driver.* and device.* ones added by NutHub. "ups.status" holds the effective
    /// status, i.e. with "FSD" in front when a forced shutdown is set.
    /// </summary>
    public ImmutableSortedDictionary<string, string> Variables { get; init; } = NoVariables;

    /// <summary>Metadata of the variables that are writable or typed; the others are read-only numbers.</summary>
    public ImmutableDictionary<string, VariableInfo> VariableInfo { get; init; } = NoVariableInfo;

    /// <summary>The instant commands supported by the driver, sorted.</summary>
    public ImmutableArray<string> Commands { get; init; } = [];

    /// <summary>Incremented on every change of this UPS; lets consumers skip snapshots they already handled.</summary>
    public long Sequence { get; init; }

    /// <summary>When the driver last delivered data (even unchanged data); null if it never did.</summary>
    public DateTimeOffset? LastUpdate { get; init; }

    /// <summary>When this snapshot was built.</summary>
    public DateTimeOffset Timestamp { get; init; }

    public bool IsAvailable => Availability == DataAvailability.Available;

    public string? Get(string name) => Variables.TryGetValue(name, out string? value) ? value : null;

    public double? GetNumber(string name) => NutFormat.ParseNumberOrNull(Get(name));

    public VariableInfo GetInfo(string name) =>
        VariableInfo.TryGetValue(name, out VariableInfo? info) ? info : Model.VariableInfo.ReadOnly;

    public bool HasCommand(string command) => Commands.Contains(command, StringComparer.OrdinalIgnoreCase);

    public bool Has(UpsStatusFlags flag) => (Status & flag) == flag;

    public string StatusText => Get("ups.status") ?? string.Empty;
}
