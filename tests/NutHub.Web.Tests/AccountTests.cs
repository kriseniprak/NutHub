using System.Net;
using System.Security.Claims;
using System.Text.Json.Nodes;
using NutHub.Core.Configuration;
using NutHub.Web.Auth;
using NutHub.Web.Tests.TestSupport;

namespace NutHub.Web.Tests;

public sealed class AccountTests
{
    [Fact]
    public async Task Web_accounts_never_expose_hashes_and_validate_passwords()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        string list = await (await admin.GetAsync("/api/admin/web-users")).Content.ReadAsStringAsync();
        Assert.DoesNotContain("pbkdf2", list);
        Assert.DoesNotContain("passwordHash", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("securityStamp", list, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("\"password\"", list);

        HttpResponseMessage shortPassword = await admin.PostAsync("/api/admin/web-users",
            Json.Body(new { name = "dave", role = "viewer", password = "short" }));
        Assert.Equal(HttpStatusCode.BadRequest, shortPassword.StatusCode);
        Assert.True((await Json.ReadAsync(shortPassword))["fields"]!.AsObject().ContainsKey("password"));

        HttpResponseMessage created = await admin.PostAsync("/api/admin/web-users",
            Json.Body(new { name = "dave", displayName = "Dave", role = "operator", password = "dave-password" }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dave = await Json.ReadAsync(created);
        Assert.Equal("operator", dave["role"]!.GetValue<string>());
        Assert.False(dave.AsObject().ContainsKey("password"));

        Assert.Equal(HttpStatusCode.Conflict, (await admin.PostAsync("/api/admin/web-users",
            Json.Body(new { name = "DAVE", role = "viewer", password = "dave-password" }))).StatusCode);

        HttpResponseMessage invalidName = await admin.PostAsync("/api/admin/web-users",
            Json.Body(new { name = "bad name", role = "viewer", password = "long-enough" }));
        Assert.True((await Json.ReadAsync(invalidName))["fields"]!.AsObject().ContainsKey("name"));
    }

    [Theory]
    [InlineData("/api/admin/web-users", ".")]
    [InlineData("/api/admin/web-users", "..")]
    [InlineData("/api/admin/nut-users", "..")]
    public async Task Names_the_api_could_never_address_again_are_refused(string list, string name)
    {
        // "/api/admin/web-users/.." is "/api/admin/" once the path is normalised: such an account could not be
        // changed, disabled or deleted any more.
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage created = await admin.PostAsync(list,
            Json.Body(new { name, role = "admin", password = "long-enough" }));
        Assert.Equal(HttpStatusCode.BadRequest, created.StatusCode);
        Assert.True((await Json.ReadAsync(created))["fields"]!.AsObject().ContainsKey("name"));

        HttpResponseMessage renamed = await admin.PutAsync("/api/admin/web-users/viewer", Json.Body(new { name }));
        Assert.Equal(HttpStatusCode.BadRequest, renamed.StatusCode);
        Assert.DoesNotContain(host.Config.Current.WebUsers, u => u.Name == name);
        Assert.DoesNotContain(host.Config.Current.NutUsers, u => u.Name == name);
    }

    [Fact]
    public async Task Roles_are_names_and_an_unknown_role_grants_nothing()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        // A number is not a role: 7 would rank above every real one. (A file with such a role does not even load:
        // the configuration validator rejects it.)
        HttpResponseMessage numeric = await admin.PostAsync("/api/admin/web-users",
            Json.Raw("""{"name":"dave","password":"dave-password","role":7}"""));
        Assert.Equal(HttpStatusCode.BadRequest, numeric.StatusCode);
        Assert.DoesNotContain(host.Config.Current.WebUsers, u => u.Name == "dave");
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, "7")], "test"));
        Assert.Null(WebIdentity.Role(principal));
    }

    [Theory]
    [InlineData("{\"disabled\":true}")]
    [InlineData("{\"role\":\"operator\"}")]
    public async Task Administrators_cannot_disable_or_demote_themselves(string body)
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage response = await admin.PutAsync("/api/admin/web-users/admin", Json.Raw(body));

        Assert.Equal(HttpStatusCode.Conflict, response.StatusCode);
        Assert.Equal("self", await Json.ErrorAsync(response));
        Assert.Equal("self", await Json.ErrorAsync(await admin.DeleteAsync("/api/admin/web-users/admin")));
    }

    [Fact]
    public async Task Changing_role_or_state_ends_the_sessions_of_that_account()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");
        HttpClient viewer = await host.SignedInAsync("viewer");

        var updated = await Json.ReadAsync(await admin.PutAsync("/api/admin/web-users/viewer",
            Json.Body(new { role = "operator", displayName = "Promoted" })));

        Assert.Equal("operator", updated["role"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.Unauthorized, (await viewer.GetAsync("/api/overview")).StatusCode);

        // A display name alone keeps the sessions.
        HttpClient oper = await host.SignedInAsync("operator");
        await admin.PutAsync("/api/admin/web-users/operator", Json.Body(new { displayName = "Op" }));
        Assert.Equal(HttpStatusCode.OK, (await oper.GetAsync("/api/overview")).StatusCode);

        // Editing their own password keeps the administrator signed in.
        HttpResponseMessage self = await admin.PutAsync("/api/admin/web-users/admin", Json.Body(new { password = "admin-password-2" }));
        Assert.Equal(HttpStatusCode.OK, self.StatusCode);
        Assert.Equal(HttpStatusCode.OK, (await admin.GetAsync("/api/admin/web-users")).StatusCode);

        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/web-users/operator")).StatusCode);
        Assert.Equal(HttpStatusCode.Unauthorized, (await oper.GetAsync("/api/overview")).StatusCode);
    }

    [Fact]
    public async Task Nut_accounts_crud()
    {
        await using var host = await WebTestHost.StartAsync();
        HttpClient admin = await host.SignedInAsync("admin");

        HttpResponseMessage noPassword = await admin.PostAsync("/api/admin/nut-users", Json.Body(new { name = "upsmon" }));
        Assert.Equal(HttpStatusCode.BadRequest, noPassword.StatusCode);

        HttpResponseMessage created = await admin.PostAsync("/api/admin/nut-users", Json.Body(new
        {
            name = "upsmon",
            password = "upsmon-secret",
            monitor = "primary",
            actions = new[] { "set", "FSD" },
            instantCommands = new[] { "all" },
            allowedUps = new[] { "sim1" },
        }));
        Assert.Equal(HttpStatusCode.Created, created.StatusCode);
        var dto = await Json.ReadAsync(created);
        Assert.False(dto.AsObject().ContainsKey("password"));
        Assert.Equal("primary", dto["monitor"]!.GetValue<string>());
        Assert.Equal(["SET", "FSD"], dto["actions"]!.AsArray().Select(a => a!.GetValue<string>()));
        Assert.Equal(["ALL"], dto["instantCommands"]!.AsArray().Select(a => a!.GetValue<string>()));
        string hash = host.Config.Current.NutUsers.Single().PasswordHash;
        Assert.True(WebTestHost.Hasher.Verify("upsmon-secret", hash));

        HttpResponseMessage badAction = await admin.PutAsync("/api/admin/nut-users/upsmon",
            Json.Body(new { actions = new[] { "REBOOT" } }));
        Assert.Equal(HttpStatusCode.BadRequest, badAction.StatusCode);
        Assert.True((await Json.ReadAsync(badAction))["fields"]!.AsObject().ContainsKey("actions"));

        var renamed = await Json.ReadAsync(await admin.PutAsync("/api/admin/nut-users/upsmon",
            Json.Body(new { name = "monitor", monitor = "secondary" })));
        Assert.Equal("monitor", renamed["name"]!.GetValue<string>());
        NutUserConfig stored = host.Config.Current.NutUsers.Single();
        Assert.Equal(hash, stored.PasswordHash);
        Assert.Equal(NutMonitorRole.Secondary, stored.Monitor);

        JsonArray list = (await Json.ReadAsync(await admin.GetAsync("/api/admin/nut-users"))).AsArray();
        Assert.Equal("monitor", Assert.Single(list)!["name"]!.GetValue<string>());
        Assert.Equal(HttpStatusCode.NoContent, (await admin.DeleteAsync("/api/admin/nut-users/monitor")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await admin.DeleteAsync("/api/admin/nut-users/monitor")).StatusCode);
    }
}
