using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
using NutHub.Core.Configuration;

namespace NutHub.Web.Auth;

/// <summary>At least <paramref name="MinimumRole"/>; with <paramref name="AllowAnonymousRead"/>, anonymous visitors too
/// while <c>web.allowAnonymousRead</c> is on.</summary>
internal sealed record RoleRequirement(WebRole MinimumRole, bool AllowAnonymousRead) : IAuthorizationRequirement;

/// <summary>
/// Evaluates <see cref="RoleRequirement"/> against the current configuration, so turning anonymous read off takes
/// effect on the next request. A failure of an anonymous visitor becomes 401, of a signed-in user 403.
/// </summary>
internal sealed class RoleRequirementHandler(IConfigStore config) : AuthorizationHandler<RoleRequirement>
{
    protected override Task HandleRequirementAsync(AuthorizationHandlerContext context, RoleRequirement requirement)
    {
        WebRole? role = WebIdentity.Role(context.User);
        if (role is { } r && r >= requirement.MinimumRole)
        {
            context.Succeed(requirement);
        }
        else if (role is null && requirement.AllowAnonymousRead && config.Current.Web.AllowAnonymousRead)
        {
            context.Succeed(requirement);
        }

        return Task.CompletedTask;
    }
}

/// <summary>Drops the cached cookie options when the configuration changes, so a new session length applies.</summary>
internal sealed class SessionOptionsRefresher(IConfigStore config, IOptionsMonitorCache<CookieAuthenticationOptions> cache)
    : IHostedService
{
    public Task StartAsync(CancellationToken cancellationToken)
    {
        config.Changed += OnChanged;
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        config.Changed -= OnChanged;
        return Task.CompletedTask;
    }

    private void OnChanged(object? sender, ConfigChangedEventArgs e)
    {
        if (e.Previous.Web.SessionHours != e.Current.Web.SessionHours)
        {
            cache.TryRemove(WebIdentity.Scheme);
        }
    }
}
