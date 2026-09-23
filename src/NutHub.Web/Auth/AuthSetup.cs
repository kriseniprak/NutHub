using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.DependencyInjection;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Security;
using NutHub.Web.Api;

namespace NutHub.Web.Auth;

/// <summary>The authorization policies of the API, one per role level.</summary>
internal static class Policies
{
    /// <summary>Viewer, or nobody when anonymous read is allowed.</summary>
    public const string Read = "read";

    public const string Operator = "operator";

    public const string Admin = "admin";
}

/// <summary>Cookie sessions, their validation against the accounts, data protection keys and the role policies.</summary>
internal static class AuthSetup
{
    public static void AddPanelAuthentication(this IServiceCollection services, NutHubPaths paths)
    {
        string keys = Path.Combine(paths.DataDirectory, "keys");
        bool existed = Directory.Exists(keys);
        Directory.CreateDirectory(keys);
        if (!existed)
        {
            FilePermissions.TryRestrictDirectory(keys);
        }

        // Keys on disk with a fixed application name: sessions survive restarts of NutHub and of the panel.
        IDataProtectionBuilder protection = services.AddDataProtection()
            .SetApplicationName("NutHub")
            .PersistKeysToFileSystem(new DirectoryInfo(keys));

        // On Windows the key ring is also encrypted for the machine; elsewhere the directory permissions protect it.
        if (OperatingSystem.IsWindows())
        {
            protection.ProtectKeysWithDpapi(protectToLocalMachine: true);
        }

        services.AddAuthentication(WebIdentity.Scheme)
            .AddCookie(WebIdentity.Scheme, options =>
            {
                options.Cookie.Name = WebIdentity.CookieName;
                options.Cookie.HttpOnly = true;
                options.Cookie.SameSite = SameSiteMode.Strict;
                options.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                options.Cookie.Path = "/";
                options.SlidingExpiration = true;
                options.Events = new CookieAuthenticationEvents
                {
                    OnValidatePrincipal = ValidatePrincipalAsync,
                    OnRedirectToLogin = context => ApiErrorWriter.WriteAsync(
                        context.HttpContext, StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized,
                        "Sign in to continue."),
                    OnRedirectToAccessDenied = context => ApiErrorWriter.WriteAsync(
                        context.HttpContext, StatusCodes.Status403Forbidden, ErrorCodes.Forbidden,
                        "Your role does not allow this."),
                    OnRedirectToLogout = _ => Task.CompletedTask,
                    OnRedirectToReturnUrl = _ => Task.CompletedTask,
                };
            });

        // Read per request from the options monitor, whose cache SessionOptionsRefresher clears on configuration
        // changes: a new session length applies without restarting the panel.
        services.AddOptions<CookieAuthenticationOptions>(WebIdentity.Scheme)
            .Configure<IConfigStore>((options, store) =>
                options.ExpireTimeSpan = TimeSpan.FromHours(Math.Clamp(store.Current.Web.SessionHours, 1, 2160)));
        services.AddHostedService<SessionOptionsRefresher>();

        services.AddSingleton<IAuthorizationHandler, RoleRequirementHandler>();
        services.AddAuthorizationBuilder()
            .AddPolicy(Policies.Read, p => p.AddRequirements(new RoleRequirement(WebRole.Viewer, true)))
            .AddPolicy(Policies.Operator, p => p.AddRequirements(new RoleRequirement(WebRole.Operator, false)))
            .AddPolicy(Policies.Admin, p => p.AddRequirements(new RoleRequirement(WebRole.Admin, false)));
    }

    /// <summary>
    /// Runs on every request with a session cookie: a deleted or disabled account, or a changed security stamp
    /// (password, role or state changed elsewhere), ends the session.
    /// </summary>
    private static async Task ValidatePrincipalAsync(CookieValidatePrincipalContext context)
    {
        var store = context.HttpContext.RequestServices.GetRequiredService<IConfigStore>();
        WebUserConfig? user = WebIdentity.FindValidUser(store.Current, context.Principal);
        bool expired = false;

        // The handler renews a session with the length it was opened with; follow web.sessionHours instead, so that
        // a shorter length also ends the idle sessions opened before the change.
        TimeSpan length = context.Options.ExpireTimeSpan;
        if (user is not null && context.Properties is { IssuedUtc: { } issued, ExpiresUtc: { } expires } &&
            expires - issued != length)
        {
            TimeProvider time = context.HttpContext.RequestServices.GetRequiredService<TimeProvider>();
            expired = issued + length <= time.GetUtcNow();
            context.Properties.ExpiresUtc = issued + length;
            context.ShouldRenew = true;
        }

        if (user is null || expired)
        {
            context.RejectPrincipal();
            await context.HttpContext.SignOutAsync(WebIdentity.Scheme).ConfigureAwait(false);
            return;
        }

        // A hand edit of the file can change a role without a new stamp: follow it rather than trusting the cookie.
        if (WebIdentity.Role(context.Principal!) != user.Role ||
            !string.Equals(context.Principal!.Identity?.Name, user.Name, StringComparison.Ordinal))
        {
            context.ReplacePrincipal(WebIdentity.CreatePrincipal(user));
            context.ShouldRenew = true;
        }

        WebIdentity.SetCurrentUser(context.HttpContext, user);
    }
}
