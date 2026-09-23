using Microsoft.AspNetCore.Http;
using NutHub.Web.Hosting;

namespace NutHub.Web.Middleware;

/// <summary>
/// Adds the security headers to every answer, errors included (registered through OnStarting, so they survive a
/// cleared response). The policy allows nothing but the panel's own files: no inline script, no external resource.
/// </summary>
internal sealed class SecurityHeadersMiddleware(RequestDelegate next, PanelBinding binding)
{
    public const string ContentSecurityPolicy =
        "default-src 'self'; img-src 'self' data:; style-src 'self'; script-src 'self'; connect-src 'self'; " +
        "font-src 'self'; object-src 'none'; base-uri 'none'; form-action 'self'; frame-ancestors 'none'";

    public const string PermissionsPolicy =
        "accelerometer=(), camera=(), geolocation=(), gyroscope=(), magnetometer=(), microphone=(), payment=(), usb=()";

    public Task InvokeAsync(HttpContext context)
    {
        context.Response.OnStarting(static state =>
        {
            var (ctx, redirectsToHttps) = ((HttpContext, bool))state;
            IHeaderDictionary headers = ctx.Response.Headers;
            headers.ContentSecurityPolicy = ContentSecurityPolicy;
            headers.XContentTypeOptions = "nosniff";
            headers.XFrameOptions = "DENY";
            headers["Referrer-Policy"] = "no-referrer";
            headers["Permissions-Policy"] = PermissionsPolicy;
            headers["Cross-Origin-Opener-Policy"] = "same-origin";
            headers["Cross-Origin-Resource-Policy"] = "same-origin";
            // HSTS only over HTTPS, and only when plain HTTP is not meant to be used any more: pinning browsers to
            // HTTPS while the administrator still serves (and may fall back to) HTTP would lock them out.
            if (ctx.Request.IsHttps && redirectsToHttps)
            {
                headers.StrictTransportSecurity = "max-age=31536000";
            }

            if (ctx.Request.Path.StartsWithSegments("/api") && !headers.ContainsKey("Cache-Control"))
            {
                headers.CacheControl = "no-store";
            }

            return Task.CompletedTask;
        }, (context, binding.RedirectHttpToHttps));

        return next(context);
    }
}
