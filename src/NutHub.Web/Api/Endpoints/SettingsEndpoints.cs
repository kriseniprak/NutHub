using System.Net;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NutHub.Core.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Security;
using NutHub.Web.Admin;
using NutHub.Web.Api.Dto;
using NutHub.Web.Auth;
using NutHub.Web.Hosting;

namespace NutHub.Web.Api.Endpoints;

/// <summary>Server, NUT, web and history settings, and the host protection settings (admin).</summary>
internal static class SettingsEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/settings", (ConfigWriter writer) => AllSettings(writer.Current));
        admin.MapPut("/settings/server", (JsonElement body, HttpContext context, ConfigWriter writer) =>
            UpdateSectionAsync(context, writer, body, "server", c => c.Server, (c, s) => c.Server = s));
        admin.MapPut("/settings/nut", (JsonElement body, HttpContext context, ConfigWriter writer) =>
            UpdateSectionAsync(context, writer, body, "nut", c => c.Nut, (c, s) => c.Nut = s));
        admin.MapPut("/settings/history", (JsonElement body, HttpContext context, ConfigWriter writer) =>
            UpdateSectionAsync(context, writer, body, "history", c => c.History, (c, s) => c.History = s));
        admin.MapPut("/settings/web", UpdateWebAsync);
        admin.MapGet("/host-protection", (ConfigWriter writer, ApiViews views) => HostProtectionView(writer.Current, views));
        admin.MapPut("/host-protection", UpdateHostProtectionAsync);
    }

    public static JsonObject AllSettings(NutHubConfig config) => new()
    {
        ["server"] = SectionJson.View(config.Server),
        ["nut"] = SectionJson.View(config.Nut),
        ["web"] = SectionJson.View(config.Web),
        ["history"] = SectionJson.View(config.History),
    };

    private static async Task<JsonObject> UpdateSectionAsync<T>(HttpContext context, ConfigWriter writer, JsonElement body,
                                                                string section, Func<NutHubConfig, T> get,
                                                                Action<NutHubConfig, T> set)
        where T : class
    {
        NutHubConfig saved = await writer.UpdateAsync(context, config => set(config, SectionJson.Merge(get(config), body)),
                                                      $"{section} settings changed", FieldMaps.Section(section))
            .ConfigureAwait(false);
        return new JsonObject { ["settings"] = SectionJson.View(get(saved)) };
    }

    private static async Task<JsonObject> UpdateWebAsync(JsonElement body, HttpContext context, ConfigWriter writer)
    {
        WebSettings before = writer.Current.Web;
        NutHubConfig saved = await writer.UpdateAsync(context, config =>
        {
            WebSettings merged = SectionJson.Merge(config.Web, body);
            ThrowIfLocksOutCaller(context, merged);
            config.Web = merged;
        }, "web settings changed", FieldMaps.Section("web")).ConfigureAwait(false);

        return new JsonObject
        {
            ["settings"] = SectionJson.View(saved.Web),
            ["notice"] = Notice(context, before, saved.Web),
        };
    }

    /// <summary>Refuses an address filter that would shut out the administrator making the change.</summary>
    private static void ThrowIfLocksOutCaller(HttpContext context, WebSettings web)
    {
        AddressFilter filter;
        try
        {
            filter = AddressFilter.Parse(web.AllowedNetworks);
        }
        catch (FormatException)
        {
            return; // The configuration validator names the bad entry.
        }

        IPAddress? caller = WebIdentity.ClientAddress(context);
        if (caller is not null && !filter.IsAllowed(caller))
        {
            throw ApiException.Validation("allowedNetworks",
                                          $"Your own address {caller} would no longer be allowed; add it to the list.");
        }
    }

    /// <summary>Where the browser finds the panel after a change of its listeners; null when nothing moves.</summary>
    internal static string? Notice(HttpContext context, WebSettings before, WebSettings after)
    {
        if (!PanelBinding.RequiresRestart(before, after))
        {
            return null;
        }

        if (!after.Enabled)
        {
            return "The web panel is now disabled; enable it again in the configuration file.";
        }

        bool redirect = after.RedirectHttpToHttps && after.HttpEnabled && after.HttpsEnabled;
        bool useHttps = context.Request.IsHttps
            ? after.HttpsEnabled
            : !after.HttpEnabled || redirect;
        if (!after.HttpEnabled && !after.HttpsEnabled)
        {
            return "The web panel no longer listens on any port.";
        }

        string scheme = useHttps ? "https" : "http";
        int port = useHttps ? after.HttpsPort : after.HttpPort;
        string host = after.BindAddress is "*" or "0.0.0.0" or "::" ? context.Request.Host.Host : after.BindAddress;
        if (string.IsNullOrEmpty(host))
        {
            host = "localhost";
        }

        if (host.Contains(':') && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }

        string target = $"{scheme}://{host}:{port}/";
        int currentPort = context.Request.Host.Port ?? (context.Request.IsHttps ? 443 : 80);
        string current = $"{context.Request.Scheme}://{context.Request.Host.Host}:{currentPort}/";
        return string.Equals(target, current, StringComparison.OrdinalIgnoreCase)
            ? "The panel restarts with the new settings; reload the page in a few seconds."
            : $"The panel moves to {target}";
    }

    private static HostProtectionAdminDto HostProtectionView(NutHubConfig config, ApiViews views) =>
        new(config.HostProtection, views.HostProtection(), views.DefaultShutdownCommand);

    private static async Task<HostProtectionAdminDto> UpdateHostProtectionAsync(JsonElement body, HttpContext context,
                                                                                 ConfigWriter writer, ApiViews views)
    {
        NutHubConfig saved = await writer.UpdateAsync(
            context,
            config => config.HostProtection = SectionJson.Merge(config.HostProtection, body),
            "host protection settings changed",
            FieldMaps.Section("hostProtection")).ConfigureAwait(false);
        return HostProtectionView(saved, views);
    }
}
