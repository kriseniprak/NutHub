using System.Diagnostics;
using System.Runtime.InteropServices;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Logging;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Web.Admin;
using NutHub.Web.Api.Dto;

namespace NutHub.Web.Api.Endpoints;

/// <summary>NUT clients, logs, system information and configuration export (admin).</summary>
internal static class ServerEndpoints
{
    public const int MaxLogEntries = 2000;

    public static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/clients", (NutSessionRegistry sessions) => sessions.Sessions.Select(ApiViews.Client).ToList());
        admin.MapDelete("/clients/{id:long}", (long id, NutSessionRegistry sessions) =>
            sessions.Disconnect(id)
                ? Results.NoContent()
                : throw ApiException.NotFound($"There is no NUT client with id {id}."));
        admin.MapGet("/logs", Logs);
        admin.MapGet("/system", System);
        admin.MapGet("/config/export", (IConfigStore config, IDriverCatalog drivers, ISecretProtector secrets) =>
            Results.File(ConfigExporter.Export(config.Current, drivers, secrets), "application/json", "nuthub-config.json"));
    }

    private static List<LogEntryDto> Logs(InMemoryLogSink sink, string? minLevel, int? limit, string? search)
    {
        LogLevel level = LogLevel.Information;
        if (!string.IsNullOrWhiteSpace(minLevel) &&
            (!Enum.TryParse(minLevel, ignoreCase: true, out level) || level == LogLevel.None || int.TryParse(minLevel, out _)))
        {
            throw ApiException.Validation("minLevel", "Use trace, debug, information, warning, error or critical.");
        }

        int count = Math.Clamp(limit ?? 500, 1, MaxLogEntries);
        return sink.Get(level, count, string.IsNullOrWhiteSpace(search) ? null : search.Trim())
            .Select(e => new LogEntryDto(e.Id, e.Timestamp, e.Level, e.Category, e.Message, e.Exception))
            .ToList();
    }

    private static SystemInfoDto System(NutHubPaths paths, IDriverCatalog drivers, TimeProvider time)
    {
        DateTimeOffset now = time.GetUtcNow();
        using Process process = Process.GetCurrentProcess();
        return new SystemInfoDto(
            NutHubInfo.Version,
            RuntimeInformation.OSDescription,
            RuntimeInformation.OSArchitecture.ToString().ToLowerInvariant(),
            RuntimeInformation.FrameworkDescription,
            Environment.ProcessId,
            Environment.MachineName,
            NutHubInfo.IsService,
            NutHubInfo.StartedAt,
            (long)Math.Max(0, (now - NutHubInfo.StartedAt).TotalSeconds),
            process.WorkingSet64,
            paths.DataDirectory,
            paths.ConfigFile,
            paths.DatabaseFile,
            drivers.All.Select(d => new DriverSupportDto(d.Id, drivers.IsSupportedHere(d))).ToList());
    }
}
