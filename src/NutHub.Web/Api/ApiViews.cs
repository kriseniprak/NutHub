using System.Text.RegularExpressions;
using NutHub.Core;
using NutHub.Core.Abstractions;
using NutHub.Core.Catalog;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Web.Api.Dto;

namespace NutHub.Web.Api;

/// <summary>Builds the read models of the API from the live runtime state.</summary>
internal sealed partial class ApiViews(
    IUpsRegistry registry,
    IDriverCatalog drivers,
    NutSessionRegistry sessions,
    IConfigStore config,
    IHostProtectionService hostProtection,
    INutServerStatus nutServer,
    TimeProvider time)
{
    public OverviewDto Overview() => new(ServerInfo(), registry.Units.Select(Summary).ToList());

    public ServerInfoDto ServerInfo()
    {
        NutHubConfig current = config.Current;
        DateTimeOffset now = time.GetUtcNow();
        return new ServerInfoDto(
            current.Server.Name,
            current.Server.Location,
            NutHubInfo.Version,
            NutHubInfo.StartedAt,
            (long)Math.Max(0, (now - NutHubInfo.StartedAt).TotalSeconds),
            now,
            new NutStatusDto(nutServer.Enabled, nutServer.Endpoints, nutServer.LastError, nutServer.TlsAvailable,
                             sessions.Sessions.Count),
            HostProtection());
    }

    /// <summary>The command host protection runs when none is configured.</summary>
    public string DefaultShutdownCommand => hostProtection.DefaultShutdownCommand;

    public HostProtectionDto HostProtection()
    {
        HostProtectionStatus status = hostProtection.GetStatus();
        return new HostProtectionDto(status.Enabled, status.State, status.Reason, status.ShutdownAt, status.DryRun,
                                     status.CriticalUps, config.Current.HostProtection.Ups);
    }

    public UpsSummaryDto Summary(UpsUnit unit)
    {
        UpsSnapshot s = unit.Snapshot;
        UpsConfig cfg = unit.Config;
        IUpsDriverFactory? factory = drivers.Find(s.DriverId);
        return new UpsSummaryDto(
            s.Name,
            s.Description,
            s.DriverId,
            factory?.DisplayName ?? s.DriverId,
            cfg.Enabled,
            s.DriverState,
            s.DriverMessage,
            s.Availability,
            s.StatusText,
            UpsStatus.SplitTokens(s.StatusText),
            s.ForcedShutdown,
            SeverityOf(s, cfg.Enabled),
            s.Get("ups.mfr") ?? s.Get("device.mfr"),
            s.Get("ups.model") ?? s.Get("device.model"),
            s.Get("ups.serial") ?? s.Get("device.serial"),
            new BatteryDto(Number(s, "battery.charge"), Number(s, "battery.runtime"), Number(s, "battery.voltage"),
                           Number(s, "battery.charge.low"), Number(s, "battery.runtime.low")),
            Number(s, "ups.load"),
            Number(s, "input.voltage"),
            Number(s, "output.voltage"),
            Number(s, "input.frequency"),
            Number(s, "ups.temperature") ?? Number(s, "battery.temperature"),
            Number(s, "ups.power"),
            Number(s, "ups.power.nominal"),
            Number(s, "ups.realpower"),
            Number(s, "ups.realpower.nominal"),
            s.LastUpdate,
            sessions.GetLoginCount(s.Name),
            s.Sequence);
    }

    public UpsDetailDto Detail(UpsUnit unit)
    {
        UpsSnapshot s = unit.Snapshot;
        UpsConfig cfg = unit.Config;
        var variables = s.Variables
            .Select(v =>
            {
                VariableInfo info = s.GetInfo(v.Key);
                return new VariableDto(v.Key, v.Value, NutCatalog.DescribeVariable(v.Key), info.Writable, info.Type,
                                       info.MaxLength, info.EnumValues,
                                       info.Ranges.Select(r => new ValueRangeDto(r.Min, r.Max)).ToList(),
                                       cfg.Overrides.ContainsKey(v.Key));
            })
            .ToList();
        var commands = s.Commands
            .Select(c => new CommandDto(c, NutCatalog.DescribeCommand(c), IsDangerous(c)))
            .ToList();
        return new UpsDetailDto(Summary(unit), variables, commands,
                                sessions.GetLogins(s.Name).Select(Client).ToList(), cfg.PollIntervalSeconds);
    }

    public static NutClientDto Client(NutSession session) =>
        new(session.Id, session.Address, session.Remote.Port, session.Username, session.Tls, session.LoginUps,
            session.Primary, session.ConnectedAt, session.LastActivity, session.Commands);

    public ClientsSummaryDto ClientsSummary()
    {
        var byUps = new SortedDictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        IReadOnlyList<NutSession> all = sessions.Sessions;
        foreach (NutSession session in all)
        {
            if (session.LoginUps is { } ups)
            {
                string name = registry.Find(ups)?.Name ?? ups;
                byUps[name] = byUps.GetValueOrDefault(name) + 1;
            }
        }

        return new ClientsSummaryDto(all.Count, byUps);
    }

    public static EventDto Event(UpsEvent e) =>
        new(e.Id, e.Timestamp, e.Ups, e.Type, e.Severity, e.Category, e.Message, e.Actor,
            e.Data ?? new Dictionary<string, string>());

    /// <summary>The colour class of a UPS; the first matching rule of docs/API.md wins.</summary>
    public static UpsSeverity SeverityOf(UpsSnapshot s, bool enabled)
    {
        if (!enabled || s.DriverState == DriverState.Disabled || s.Availability == DataAvailability.DriverNotConnected)
        {
            return UpsSeverity.Offline;
        }

        if (s.Has(UpsStatusFlags.ForcedShutdown) || s.ForcedShutdown ||
            (s.Has(UpsStatusFlags.OnBattery) && s.Has(UpsStatusFlags.LowBattery)) || s.Has(UpsStatusFlags.Off))
        {
            return UpsSeverity.Critical;
        }

        const UpsStatusFlags warning = UpsStatusFlags.OnBattery | UpsStatusFlags.ReplaceBattery |
                                       UpsStatusFlags.Overload | UpsStatusFlags.Bypass | UpsStatusFlags.Alarm;
        if ((s.Status & warning) != 0 || s.Availability == DataAvailability.Stale)
        {
            return UpsSeverity.Warning;
        }

        const UpsStatusFlags info = UpsStatusFlags.Calibrating | UpsStatusFlags.Test | UpsStatusFlags.Trim |
                                    UpsStatusFlags.Boost;
        return (s.Status & info) != 0 ? UpsSeverity.Info : UpsSeverity.Ok;
    }

    /// <summary>
    /// Commands that can cut the power of the load or change the battery state; the panel asks for confirmation.
    /// </summary>
    public static bool IsDangerous(string command) =>
        !string.Equals(command, "shutdown.stop", StringComparison.OrdinalIgnoreCase) && DangerousRegex().IsMatch(command);

    // load.off*, shutdown.*, calibrate.start, test.failure.start, bypass.start, reset.*, outlet.*.off*,
    // outlet.*.shutdown*
    [GeneratedRegex(@"^(load\.off.*|shutdown\..*|calibrate\.start|test\.failure\.start|bypass\.start|reset\..*|outlet\..+\.off.*|outlet\..+\.shutdown.*)$",
                    RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex DangerousRegex();

    private static double? Number(UpsSnapshot s, string name)
    {
        double? value = s.GetNumber(name);
        return value is { } v && double.IsFinite(v) ? v : null;
    }
}
