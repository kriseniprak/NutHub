using System.Text;
using System.Globalization;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using Microsoft.Extensions.Logging;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Web.Admin;
using NutHub.Web.Api.Dto;
using NutHub.Web.Auth;

namespace NutHub.Web.Api.Endpoints;

/// <summary>Sign-in, sign-out, session state and password change (docs/API.md, "Authentication").</summary>
internal static class AuthEndpoints
{
    public const int MinPasswordLength = 8;

    // Verified when the user name is unknown, so that a wrong name costs as much time as a wrong password.
    private static string? s_dummyHash;

    public static void Map(RouteGroupBuilder api)
    {
        RouteGroupBuilder auth = api.MapGroup("/auth");
        auth.MapGet("/state", State);
        auth.MapPost("/login", LoginAsync);
        // Typed as a route handler (not a RequestDelegate) so that its IResult is written.
        auth.MapPost("/logout", (Func<HttpContext, Task<IResult>>)LogoutAsync);
        auth.MapPost("/password", ChangePasswordAsync).RequireAuthorization();
    }

    public static UserInfoDto UserInfo(WebUserConfig user) =>
        new(user.Name, user.DisplayName, user.Role, user.MustChangePassword);

    public static AuthenticationProperties SessionProperties() => new() { IsPersistent = true, AllowRefresh = true };

    private static AuthStateDto State(HttpContext context, IConfigStore config)
    {
        NutHubConfig current = config.Current;
        WebUserConfig? user = WebIdentity.CurrentUser(context);
        return new AuthStateDto(user is not null, user is null ? null : UserInfo(user), current.Web.AllowAnonymousRead,
                                current.Server.Name, NutHubInfo.Version);
    }

    private static async Task<IResult> LoginAsync(LoginRequest body, HttpContext context, IConfigStore config,
                                                  IPasswordHasher hasher, LoginRateLimiter limiter, EventHub hub,
                                                  TimeProvider time, ILoggerFactory loggers)
    {
        string address = WebIdentity.ClientAddress(context)?.ToString() ?? "unknown";
        string username = (body.Username ?? "").Trim();
        if (limiter.TryBeginAttempt(address, username) is { } wait)
        {
            return RateLimited(context, wait);
        }

        WebUserConfig? user = null;
        bool valid = false;
        try
        {
            user = username.Length is > 0 and <= 256 ? WebIdentity.FindUser(config.Current, username) : null;
            string hash = user?.PasswordHash is { Length: > 0 } h ? h : s_dummyHash ??= hasher.Hash(Guid.NewGuid().ToString("N"));
            valid = hasher.Verify(body.Password ?? "", hash) && user is { Disabled: false };
        }
        finally
        {
            // The attempt counted from its start, so parallel requests cannot all get past the limit.
            limiter.EndAttempt(address, username, failed: !valid);
        }

        ILogger logger = loggers.CreateLogger("NutHub.Web.Auth");
        if (!valid || user is null)
        {
            string shown = Printable(username);
            logger.LogWarning("Failed web sign-in for '{User}' from {Address}.", shown, address);
            hub.Publish(new UpsEventMessage(UpsEvent.Create(
                UpsEventType.UserLoginFailed, time.GetUtcNow(), null,
                $"Failed sign-in to the web panel as '{shown}' from {address}.",
                CommandOrigin.Web(shown, address).ToString(),
                new Dictionary<string, string> { ["user"] = shown, ["address"] = address })));
            return ApiErrorWriter.Result(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials,
                                         "Wrong user name or password.");
        }

        limiter.RecordSuccess(user.Name);
        await context.SignInAsync(WebIdentity.Scheme, WebIdentity.CreatePrincipal(user), SessionProperties())
            .ConfigureAwait(false);
        logger.LogInformation("{User} signed in to the web panel from {Address}.", user.Name, address);
        hub.Publish(new UpsEventMessage(UpsEvent.Create(
            UpsEventType.UserLogin, time.GetUtcNow(), null,
            $"{user.Name} signed in to the web panel from {address}.",
            CommandOrigin.Web(user.Name, address).ToString(),
            new Dictionary<string, string> { ["user"] = user.Name, ["address"] = address })));
        return Results.Ok(new LoginResponse(UserInfo(user)));
    }

    private static async Task<IResult> LogoutAsync(HttpContext context)
    {
        await context.SignOutAsync(WebIdentity.Scheme).ConfigureAwait(false);
        return Results.NoContent();
    }

    private static async Task<IResult> ChangePasswordAsync(PasswordChangeRequest body, HttpContext context,
                                                           ConfigWriter writer, IPasswordHasher hasher,
                                                           LoginRateLimiter limiter)
    {
        WebUserConfig user = WebIdentity.CurrentUser(context)
                             ?? throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized,
                                                       "Sign in to continue.");
        string address = WebIdentity.ClientAddress(context)?.ToString() ?? "unknown";
        if (limiter.TryBeginAttempt(address, user.Name) is { } wait)
        {
            return RateLimited(context, wait);
        }

        // The current password is checked even in a valid session: a borrowed, unlocked browser must not be enough.
        bool correct = false;
        try
        {
            correct = hasher.Verify(body.CurrentPassword ?? "", user.PasswordHash);
        }
        finally
        {
            limiter.EndAttempt(address, user.Name, failed: !correct);
        }

        if (!correct)
        {
            return ApiErrorWriter.Result(StatusCodes.Status401Unauthorized, ErrorCodes.InvalidCredentials,
                                         "The current password is wrong.");
        }

        string newPassword = body.NewPassword ?? "";
        if (newPassword.Length < MinPasswordLength)
        {
            throw ApiException.Validation("newPassword", $"Use at least {MinPasswordLength} characters.");
        }

        if (string.Equals(newPassword, body.CurrentPassword, StringComparison.Ordinal))
        {
            throw ApiException.Validation("newPassword", "Choose a password different from the current one.");
        }

        string hash = hasher.Hash(newPassword);
        string stamp = Guid.NewGuid().ToString("N");
        NutHubConfig saved = await writer.UpdateAsync(context, config =>
        {
            WebUserConfig target = WebIdentity.FindUser(config, user.Name)
                                   ?? throw new ApiException(StatusCodes.Status401Unauthorized, ErrorCodes.Unauthorized,
                                                             "The account no longer exists.");
            target.PasswordHash = hash;
            target.MustChangePassword = false;
            target.SecurityStamp = stamp;
        }, $"Password of the web account {user.Name} changed").ConfigureAwait(false);

        // Every other session of the account ends with the old stamp; this one continues with the new one.
        WebUserConfig updated = WebIdentity.FindUser(saved, user.Name)!;
        await context.SignInAsync(WebIdentity.Scheme, WebIdentity.CreatePrincipal(updated), SessionProperties())
            .ConfigureAwait(false);
        return Results.NoContent();
    }

    private static IResult RateLimited(HttpContext context, TimeSpan wait)
    {
        int seconds = Math.Max(1, (int)Math.Ceiling(wait.TotalSeconds));
        context.Response.Headers.RetryAfter = seconds.ToString(CultureInfo.InvariantCulture);
        return Results.Json(new ApiError(ErrorCodes.RateLimited,
                                         $"Too many failed attempts; try again in {seconds} seconds.",
                                         RetryAfterSeconds: seconds),
                            ApiJson.Options, statusCode: StatusCodes.Status429TooManyRequests);
    }

    /// <summary>A user name typed by anyone, safe for logs and events: no control characters, bounded length.</summary>
    private static string Printable(string text)
    {
        // By code point, so the cut never splits a surrogate pair (unpaired halves become U+FFFD).
        string clean = string.Concat(text.EnumerateRunes().Where(r => !Rune.IsControl(r)).Take(64).Select(r => r.ToString()));
        return clean.Length == 0 ? "(empty)" : clean;
    }
}
