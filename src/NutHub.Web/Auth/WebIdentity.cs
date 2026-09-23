using System.Net;
using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Http;
using NutHub.Core.Configuration;
using NutHub.Core.Model;

namespace NutHub.Web.Auth;

/// <summary>The claims of a panel session and helpers to read who is calling.</summary>
internal static class WebIdentity
{
    public const string Scheme = CookieAuthenticationDefaults.AuthenticationScheme;
    public const string CookieName = "nuthub_session";
    public const string StampClaim = "nuthub:stamp";

    private const string UserItem = "nuthub.user";

    public static ClaimsPrincipal CreatePrincipal(WebUserConfig user)
    {
        var identity = new ClaimsIdentity(
            [
                new Claim(ClaimTypes.Name, user.Name),
                new Claim(ClaimTypes.Role, user.Role.ToString()),
                new Claim(StampClaim, user.SecurityStamp),
            ],
            Scheme, ClaimTypes.Name, ClaimTypes.Role);
        return new ClaimsPrincipal(identity);
    }

    /// <summary>The account of a principal, when it is still valid (exists, enabled, same security stamp).</summary>
    public static WebUserConfig? FindValidUser(NutHubConfig config, ClaimsPrincipal? principal)
    {
        if (principal?.Identity?.IsAuthenticated != true)
        {
            return null;
        }

        string? name = principal.FindFirstValue(ClaimTypes.Name);
        string? stamp = principal.FindFirstValue(StampClaim);
        if (name is null || stamp is null)
        {
            return null;
        }

        WebUserConfig? user = FindUser(config, name);
        return user is not null && !user.Disabled && string.Equals(user.SecurityStamp, stamp, StringComparison.Ordinal)
            ? user
            : null;
    }

    /// <summary>Finds an account by name: exact match first, then ignoring case (people type "Admin").</summary>
    public static WebUserConfig? FindUser(NutHubConfig config, string name) =>
        config.WebUsers.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.Ordinal)) ??
        config.WebUsers.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>Remembers the validated account for the rest of the request.</summary>
    public static void SetCurrentUser(HttpContext context, WebUserConfig user) => context.Items[UserItem] = user;

    /// <summary>The signed-in account of this request, validated by the cookie handler; null for anonymous.</summary>
    public static WebUserConfig? CurrentUser(HttpContext context) =>
        context.User.Identity?.IsAuthenticated == true ? context.Items[UserItem] as WebUserConfig : null;

    /// <summary>The role of a signed-in principal; null for anonymous, and for a value that is not a role (a
    /// number in a hand-edited file), which would otherwise rank above admin.</summary>
    public static WebRole? Role(ClaimsPrincipal principal) =>
        principal.Identity?.IsAuthenticated == true &&
        Enum.TryParse(principal.FindFirstValue(ClaimTypes.Role), ignoreCase: true, out WebRole role) &&
        Enum.IsDefined(role)
            ? role
            : null;

    /// <summary>The client address without the IPv4-in-IPv6 wrapping, for logs, events and filters.</summary>
    public static IPAddress? ClientAddress(HttpContext context)
    {
        IPAddress? address = context.Connection.RemoteIpAddress;
        return address is { IsIPv4MappedToIPv6: true } ? address.MapToIPv4() : address;
    }

    public static CommandOrigin Origin(HttpContext context) =>
        CommandOrigin.Web(context.User.Identity?.IsAuthenticated == true ? context.User.Identity.Name : null,
                          ClientAddress(context)?.ToString());
}
