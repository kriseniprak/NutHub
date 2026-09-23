using System.Net;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Logging;
using NutHub.Core.Configuration;
using NutHub.Core.Security;
using NutHub.Web.Api;
using NutHub.Web.Auth;
using NutHub.Web.Hosting;

namespace NutHub.Web.Middleware;

/// <summary>
/// Refuses clients outside <c>web.allowedNetworks</c> before anything else runs (static files included), so a
/// restricted panel reveals nothing to other networks.
/// </summary>
internal sealed class NetworkAclMiddleware(RequestDelegate next, IConfigStore config, ILogger<NetworkAclMiddleware> logger)
{
    private FilterCache? _cache;
    private long _lastWarning = -60_000;

    public Task InvokeAsync(HttpContext context)
    {
        AddressFilter filter = GetFilter(config.Current.Web.AllowedNetworks);
        IPAddress? address = WebIdentity.ClientAddress(context);
        if (filter.IsAllowed(address))
        {
            return next(context);
        }

        // At most one warning a minute: a scanner must not flood the log.
        long now = Environment.TickCount64;
        long last = Interlocked.Read(ref _lastWarning);
        if (now - last >= 60_000 && Interlocked.CompareExchange(ref _lastWarning, now, last) == last)
        {
            logger.LogWarning("Refused a web request from {Address}: not in the allowed networks.", address);
        }

        return ApiErrorWriter.WriteAsync(context, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden,
                                         "Your address is not allowed to use this panel.");
    }

    // Configuration snapshots are immutable, so the parsed filter is valid as long as the list instance is the same.
    private AddressFilter GetFilter(List<string> networks)
    {
        FilterCache? cache = Volatile.Read(ref _cache);
        if (cache is not null && ReferenceEquals(cache.Source, networks))
        {
            return cache.Filter;
        }

        AddressFilter filter;
        try
        {
            filter = AddressFilter.Parse(networks);
        }
        catch (FormatException ex)
        {
            // The validator prevents this; should a hand edit slip through, fail closed rather than open.
            logger.LogError(ex, "web.allowedNetworks is invalid; only this machine may use the panel.");
            filter = AddressFilter.Parse(["127.0.0.1", "::1"]);
        }

        Volatile.Write(ref _cache, new FilterCache(networks, filter));
        return filter;
    }

    private sealed record FilterCache(List<string> Source, AddressFilter Filter);
}

/// <summary>Sends plain HTTP visitors to the HTTPS port when both are enabled and the redirect is on.</summary>
internal sealed class HttpsRedirectMiddleware(RequestDelegate next, PanelBinding binding)
{
    public Task InvokeAsync(HttpContext context)
    {
        if (!binding.RedirectHttpToHttps || context.Request.IsHttps)
        {
            return next(context);
        }

        string host = context.Request.Host.Host;
        if (string.IsNullOrEmpty(host))
        {
            host = "localhost";
        }
        else if (host.Contains(':') && !host.StartsWith('['))
        {
            host = $"[{host}]";
        }

        string port = binding.HttpsPort == 443 ? "" : ":" + binding.HttpsPort;
        // 308 keeps the method and body of API calls.
        context.Response.StatusCode = StatusCodes.Status308PermanentRedirect;
        context.Response.Headers.Location =
            $"https://{host}{port}{context.Request.PathBase}{context.Request.Path}{context.Request.QueryString}";
        return Task.CompletedTask;
    }
}

/// <summary>
/// Requires the header <c>X-NutHub-Request: 1</c> on every request that can change something. Browsers cannot add
/// it cross-site without a CORS preflight, which this server never answers positively.
/// </summary>
internal sealed class CsrfMiddleware(RequestDelegate next)
{
    public const string HeaderName = "X-NutHub-Request";

    public Task InvokeAsync(HttpContext context)
    {
        string method = context.Request.Method;
        if (HttpMethods.IsGet(method) || HttpMethods.IsHead(method) ||
            context.Request.Headers[HeaderName] == "1")
        {
            return next(context);
        }

        return ApiErrorWriter.WriteAsync(context, StatusCodes.Status400BadRequest, ErrorCodes.Csrf,
                                         $"The header {HeaderName}: 1 is required.");
    }
}

/// <summary>Until an account has chosen a new password, the API only lets it do that (and sign out).</summary>
internal sealed class PasswordChangeGateMiddleware(RequestDelegate next)
{
    public Task InvokeAsync(HttpContext context)
    {
        PathString path = context.Request.Path;
        if (path.StartsWithSegments("/api") && !path.StartsWithSegments("/api/auth") &&
            WebIdentity.CurrentUser(context) is { MustChangePassword: true })
        {
            return ApiErrorWriter.WriteAsync(context, StatusCodes.Status403Forbidden, ErrorCodes.PasswordChangeRequired,
                                             "Choose a new password first.");
        }

        return next(context);
    }
}
