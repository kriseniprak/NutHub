using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Routing;
using NutHub.Core.Configuration;
using NutHub.Core.Security;
using NutHub.Web.Admin;
using NutHub.Web.Api.Dto;
using NutHub.Web.Auth;

namespace NutHub.Web.Api.Endpoints;

/// <summary>NUT accounts (upsd.users) and web panel accounts (admin).</summary>
internal static class AccountEndpoints
{
    public static void Map(RouteGroupBuilder admin)
    {
        admin.MapGet("/nut-users", (ConfigWriter writer) => writer.Current.NutUsers.Select(NutUserView).ToList());
        admin.MapPost("/nut-users", CreateNutUserAsync);
        admin.MapPut("/nut-users/{name}", UpdateNutUserAsync);
        admin.MapDelete("/nut-users/{name}", DeleteNutUserAsync);
        admin.MapGet("/web-users", (ConfigWriter writer) => writer.Current.WebUsers.Select(WebUserView).ToList());
        admin.MapPost("/web-users", CreateWebUserAsync);
        admin.MapPut("/web-users/{name}", UpdateWebUserAsync);
        admin.MapDelete("/web-users/{name}", DeleteWebUserAsync);
    }

    public static NutUserDto NutUserView(NutUserConfig u) =>
        new(u.Name, null, u.Monitor, u.Actions, u.InstantCommands, u.AllowedUps);

    public static WebUserDto WebUserView(WebUserConfig u) =>
        new(u.Name, u.DisplayName, u.Role, u.Disabled, u.MustChangePassword, null);

    // ----- NUT accounts -----

    private static async Task<IResult> CreateNutUserAsync(NutUserDto body, HttpContext context, ConfigWriter writer,
                                                          IPasswordHasher hasher)
    {
        string name = RequireName(body.Name);
        string hash = hasher.Hash(RequirePassword(body.Password, required: true)!);
        var user = new NutUserConfig { Name = name, PasswordHash = hash };
        ApplyNutUser(body, user);
        int index = -1;
        NutHubConfig saved = await writer.UpdateAsync(context, config =>
        {
            if (config.NutUsers.Any(u => u.Name == name))
            {
                throw ApiException.Conflict($"A NUT account named '{name}' already exists.");
            }

            config.NutUsers.Add(user);
            index = config.NutUsers.Count - 1;
        }, $"NUT account {name} added", key => MapAccountField(FieldMaps.Item("nutUsers", index)(key))).ConfigureAwait(false);

        return Results.Created($"/api/admin/nut-users/{Uri.EscapeDataString(name)}",
                               NutUserView(saved.NutUsers.First(u => u.Name == name)));
    }

    private static async Task<NutUserDto> UpdateNutUserAsync(string name, NutUserDto body, HttpContext context,
                                                             ConfigWriter writer, IPasswordHasher hasher)
    {
        string? password = RequirePassword(body.Password, required: false);
        string? hash = password is null ? null : hasher.Hash(password);
        string? newName = body.Name is null ? null : RequireName(body.Name);
        int index = -1;
        string finalName = name;
        NutHubConfig saved = await writer.UpdateAsync(context, config =>
        {
            NutUserConfig user = FindNutUser(config, name) ?? throw ApiException.NotFound($"There is no NUT account '{name}'.");
            index = config.NutUsers.IndexOf(user);
            if (newName is not null && newName != user.Name)
            {
                if (config.NutUsers.Any(u => u.Name == newName))
                {
                    throw ApiException.Conflict($"A NUT account named '{newName}' already exists.");
                }

                user.Name = newName;
            }

            if (hash is not null)
            {
                user.PasswordHash = hash;
            }

            ApplyNutUser(body, user);
            finalName = user.Name;
        }, $"NUT account {name} changed", key => MapAccountField(FieldMaps.Item("nutUsers", index)(key))).ConfigureAwait(false);

        return NutUserView(saved.NutUsers.First(u => u.Name == finalName));
    }

    private static async Task<IResult> DeleteNutUserAsync(string name, HttpContext context, ConfigWriter writer)
    {
        await writer.UpdateAsync(context, config =>
        {
            NutUserConfig user = FindNutUser(config, name) ?? throw ApiException.NotFound($"There is no NUT account '{name}'.");
            config.NutUsers.Remove(user);
        }, $"NUT account {name} deleted").ConfigureAwait(false);
        return Results.NoContent();
    }

    private static void ApplyNutUser(NutUserDto body, NutUserConfig user)
    {
        user.Monitor = body.Monitor ?? user.Monitor;
        if (body.Actions is not null)
        {
            user.Actions = Clean(body.Actions, upper: true);
        }

        if (body.InstantCommands is not null)
        {
            user.InstantCommands = Clean(body.InstantCommands, upper: false)
                .Select(c => string.Equals(c, "ALL", StringComparison.OrdinalIgnoreCase) ? "ALL" : c).ToList();
        }

        if (body.AllowedUps is not null)
        {
            user.AllowedUps = Clean(body.AllowedUps, upper: false);
        }
    }

    // NUT user names are case-sensitive in upsd; accept another case in the URL only when it is unambiguous.
    private static NutUserConfig? FindNutUser(NutHubConfig config, string name) =>
        config.NutUsers.FirstOrDefault(u => u.Name == name) ??
        (config.NutUsers.Count(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)) == 1
            ? config.NutUsers.First(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase))
            : null);

    // ----- Web accounts -----

    private static async Task<IResult> CreateWebUserAsync(WebUserDto body, HttpContext context, ConfigWriter writer,
                                                          IPasswordHasher hasher)
    {
        string name = RequireName(body.Name);
        string hash = hasher.Hash(RequirePassword(body.Password, required: true)!);
        var user = new WebUserConfig
        {
            Name = name,
            PasswordHash = hash,
            DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim(),
            Role = RequireRole(body.Role) ?? WebRole.Viewer,
            Disabled = body.Disabled ?? false,
            MustChangePassword = body.MustChangePassword ?? false,
        };
        int index = -1;
        NutHubConfig saved = await writer.UpdateAsync(context, config =>
        {
            if (WebIdentity.FindUser(config, name) is not null)
            {
                throw ApiException.Conflict($"A web account named '{name}' already exists.");
            }

            config.WebUsers.Add(user);
            index = config.WebUsers.Count - 1;
        }, $"Web account {name} added", key => MapAccountField(FieldMaps.Item("webUsers", index)(key))).ConfigureAwait(false);

        return Results.Created($"/api/admin/web-users/{Uri.EscapeDataString(name)}",
                               WebUserView(WebIdentity.FindUser(saved, name)!));
    }

    private static async Task<WebUserDto> UpdateWebUserAsync(string name, WebUserDto body, HttpContext context,
                                                             ConfigWriter writer, IPasswordHasher hasher)
    {
        string? password = RequirePassword(body.Password, required: false);
        string? hash = password is null ? null : hasher.Hash(password);
        string? newName = body.Name is null ? null : RequireName(body.Name);
        WebRole? newRole = RequireRole(body.Role);
        string me = WebIdentity.CurrentUser(context)?.Name ?? "";
        int index = -1;
        string finalName = name;
        bool self = false;
        NutHubConfig saved = await writer.UpdateAsync(context, config =>
        {
            WebUserConfig user = WebIdentity.FindUser(config, name)
                                 ?? throw ApiException.NotFound($"There is no web account '{name}'.");
            index = config.WebUsers.IndexOf(user);
            self = string.Equals(user.Name, me, StringComparison.OrdinalIgnoreCase);
            WebRole role = newRole ?? user.Role;
            bool disabled = body.Disabled ?? user.Disabled;
            if (self && (disabled || role < user.Role))
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Self,
                                       "You cannot disable or demote your own account.");
            }

            if (newName is not null && !string.Equals(newName, user.Name, StringComparison.Ordinal))
            {
                if (config.WebUsers.Any(u => !ReferenceEquals(u, user) && string.Equals(u.Name, newName, StringComparison.OrdinalIgnoreCase)))
                {
                    throw ApiException.Conflict($"A web account named '{newName}' already exists.");
                }

                user.Name = newName;
            }

            bool rotate = hash is not null || role != user.Role || disabled != user.Disabled;
            user.Role = role;
            user.Disabled = disabled;
            if (body.DisplayName is not null)
            {
                user.DisplayName = string.IsNullOrWhiteSpace(body.DisplayName) ? null : body.DisplayName.Trim();
            }

            user.MustChangePassword = body.MustChangePassword ?? user.MustChangePassword;
            if (hash is not null)
            {
                user.PasswordHash = hash;
            }

            if (rotate)
            {
                // Ends the other sessions of the account (they carry the old stamp).
                user.SecurityStamp = Guid.NewGuid().ToString("N");
            }

            ThrowIfNoAdminLeft(config);
            finalName = user.Name;
        }, $"Web account {name} changed", key => MapAccountField(FieldMaps.Item("webUsers", index)(key))).ConfigureAwait(false);

        WebUserConfig updated = WebIdentity.FindUser(saved, finalName)!;
        if (self)
        {
            // Keep the session of the administrator who edited their own account.
            await context.SignInAsync(WebIdentity.Scheme, WebIdentity.CreatePrincipal(updated),
                                      AuthEndpoints.SessionProperties()).ConfigureAwait(false);
        }

        return WebUserView(updated);
    }

    private static async Task<IResult> DeleteWebUserAsync(string name, HttpContext context, ConfigWriter writer)
    {
        string me = WebIdentity.CurrentUser(context)?.Name ?? "";
        await writer.UpdateAsync(context, config =>
        {
            WebUserConfig user = WebIdentity.FindUser(config, name)
                                 ?? throw ApiException.NotFound($"There is no web account '{name}'.");
            if (string.Equals(user.Name, me, StringComparison.OrdinalIgnoreCase))
            {
                throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.Self, "You cannot delete your own account.");
            }

            config.WebUsers.Remove(user);
            ThrowIfNoAdminLeft(config);
        }, $"Web account {name} deleted").ConfigureAwait(false);
        return Results.NoContent();
    }

    private static void ThrowIfNoAdminLeft(NutHubConfig config)
    {
        if (!config.WebUsers.Any(u => u.Role == WebRole.Admin && !u.Disabled))
        {
            throw new ApiException(StatusCodes.Status409Conflict, ErrorCodes.LastAdmin,
                                   "At least one enabled administrator account must remain.");
        }
    }

    // ----- Shared -----

    private static string RequireName(string? name)
    {
        string trimmed = name?.Trim() ?? "";
        if (trimmed.Length == 0)
        {
            throw ApiException.Validation("name", "A name is required.");
        }

        // "/api/admin/web-users/.." is "/api/admin/" once the path is normalised: the account could never be changed
        // or deleted again.
        return trimmed.All(c => c == '.')
            ? throw ApiException.Validation("name", "Use at least one letter or digit.")
            : trimmed;
    }

    /// <returns>The password, or null when it may be kept and none was given.</returns>
    private static string? RequirePassword(string? password, bool required)
    {
        if (string.IsNullOrEmpty(password))
        {
            return required ? throw ApiException.Validation("password", "A password is required.") : null;
        }

        return password.Length >= AuthEndpoints.MinPasswordLength
            ? password
            : throw ApiException.Validation("password", $"Use at least {AuthEndpoints.MinPasswordLength} characters.");
    }

    // The enum reader also takes numbers: 7 is no role, yet it would rank above admin.
    private static WebRole? RequireRole(WebRole? role) =>
        role is { } r && !Enum.IsDefined(r)
            ? throw ApiException.Validation("role", "Use viewer, operator or admin.")
            : role;

    private static string MapAccountField(string key) => key == "passwordHash" ? "password" : key;

    private static List<string> Clean(IEnumerable<string> values, bool upper) =>
        values.Where(v => !string.IsNullOrWhiteSpace(v))
              .Select(v => upper ? v.Trim().ToUpperInvariant() : v.Trim())
              .Distinct(StringComparer.OrdinalIgnoreCase)
              .ToList();
}
