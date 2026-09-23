using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Core.Tests.Support;

namespace NutHub.Core.Tests.Configuration;

public sealed class JsonConfigStoreTests : IDisposable
{
    private readonly TempDirectory _dir = new();
    private readonly NutHubPaths _paths;
    private readonly AesSecretProtector _secrets = new(RandomNumberGenerator.GetBytes(32));
    private readonly Pbkdf2PasswordHasher _hasher = new(1_000);
    private readonly IDriverCatalog _drivers = new DriverCatalog([new FakeDriverFactory("fake")]);
    private readonly EventHub _hub = TestTime.Hub();
    private readonly List<JsonConfigStore> _stores = [];

    public JsonConfigStoreTests()
    {
        _paths = new NutHubPaths(_dir.Path);
    }

    public void Dispose()
    {
        foreach (JsonConfigStore store in _stores)
        {
            store.Dispose();
        }

        _dir.Dispose();
    }

    private JsonConfigStore Open()
    {
        var store = new JsonConfigStore(_paths, _secrets, _hasher, _drivers, _hub, TestTime.Create(),
                                        NullLogger<JsonConfigStore>.Instance);
        _stores.Add(store);
        return store;
    }

    private void WriteConfig(string json) => File.WriteAllText(_paths.ConfigFile, json);

    private string AdminJson() =>
        $$"""{ "name": "root", "role": "admin", "passwordHash": "{{_hasher.Hash("root-password")}}", "securityStamp": "s1" }""";

    [Fact]
    public void A_new_installation_gets_an_administrator_with_a_generated_password()
    {
        JsonConfigStore store = Open();

        WebUserConfig admin = Assert.Single(store.Current.WebUsers);
        Assert.Equal("admin", admin.Name);
        Assert.Equal(WebRole.Admin, admin.Role);
        Assert.True(admin.MustChangePassword);
        Assert.NotNull(store.GeneratedAdminPassword);
        Assert.True(_hasher.Verify(store.GeneratedAdminPassword!, admin.PasswordHash));

        string passwordFile = File.ReadAllText(_paths.InitialPasswordFile);
        Assert.Contains("User:     admin", passwordFile, StringComparison.Ordinal);
        Assert.Contains(store.GeneratedAdminPassword!, passwordFile, StringComparison.Ordinal);

        // Saved right away, camelCase with enums as strings.
        string json = File.ReadAllText(_paths.ConfigFile);
        Assert.Contains("\"webUsers\"", json, StringComparison.Ordinal);
        Assert.Contains("\"role\": \"admin\"", json, StringComparison.Ordinal);
        Assert.DoesNotContain(store.GeneratedAdminPassword!, json, StringComparison.Ordinal);
    }

    [Fact]
    public void An_existing_file_is_loaded_as_it_is()
    {
        WriteConfig($$"""
            {
              // comments and trailing commas are accepted in a file edited by hand
              "server": { "name": "Rack A", },
              "ups": [ { "name": "ups1", "driver": "fake", "options": { "Port": "COM3" } } ],
              "webUsers": [ {{AdminJson()}} ],
            }
            """);
        byte[] before = File.ReadAllBytes(_paths.ConfigFile);

        JsonConfigStore store = Open();

        Assert.Null(store.GeneratedAdminPassword);
        Assert.False(File.Exists(_paths.InitialPasswordFile));
        Assert.Equal("Rack A", store.Current.Server.Name);
        // Driver options ignore case, as drivers read them.
        Assert.Equal("COM3", store.Current.Ups[0].Options["port"]);
        // Defaults for what the file does not mention.
        Assert.Equal(15, store.Current.Nut.MaxAgeSeconds);
        Assert.NotEmpty(store.Current.Notifications.Events);
        // Nothing to fix: the file is not rewritten.
        Assert.Equal(before, File.ReadAllBytes(_paths.ConfigFile));
    }

    [Fact]
    public void Missing_sections_and_nulls_are_filled_in()
    {
        WriteConfig($$"""
            { "nut": { "listen": null, "tls": null }, "web": null, "notifications": { "email": null, "webhooks": null },
              "ups": [ { "name": "ups1", "driver": "fake", "options": null, "overrides": null, "lowBattery": null } ],
              "nutUsers": [ { "name": "mon", "passwordHash": "h", "actions": null } ],
              "webUsers": [ {{AdminJson()}}, { "name": "viewer", "passwordHash": "h" } ], "history": { "variables": null } }
            """);

        NutHubConfig config = Open().Current;

        Assert.Single(config.Nut.Listen);
        Assert.NotNull(config.Web);
        Assert.NotNull(config.Ups[0].LowBattery);
        Assert.Empty(config.NutUsers[0].Actions);
        Assert.False(string.IsNullOrEmpty(config.WebUsers[1].SecurityStamp));
        Assert.Empty(config.History.Variables);
    }

    // A null written by hand in a list or as a value carries nothing: it is dropped instead of failing further on
    // (a UPS option or override that is null would otherwise break the driver manager at every start).
    [Fact]
    public void Null_list_entries_and_null_values_are_dropped()
    {
        WriteConfig($$"""
            { "nut": { "listen": [ null, { "address": "127.0.0.1", "port": 3493 } ], "allowedNetworks": [ null ] },
              "web": { "allowedNetworks": [ null ] },
              "ups": [ null, { "name": "ups1", "driver": "fake", "options": { "port": "COM1", "community": null },
                               "overrides": { "ups.id": null } } ],
              "nutUsers": [ null, { "name": "mon", "passwordHash": "h", "actions": [ null ], "instantCommands": [ null ],
                                    "allowedUps": [ null ] } ],
              "webUsers": [ null, {{AdminJson()}} ],
              "notifications": { "email": { "to": [ null ] }, "webhooks": [ null, { "url": "https://example.test/h",
                                 "headers": { "X-Token": null } } ], "commands": [ null ] },
              "hostProtection": { "ups": [ null ] }, "history": { "variables": [ null ] } }
            """);

        NutHubConfig config = Open().Current;

        Assert.Equal("127.0.0.1", Assert.Single(config.Nut.Listen).Address);
        Assert.Empty(config.Nut.AllowedNetworks);
        Assert.Empty(config.Web.AllowedNetworks);
        UpsConfig ups = Assert.Single(config.Ups);
        Assert.Equal("COM1", Assert.Single(ups.Options).Value);
        Assert.Empty(ups.Overrides);
        NutUserConfig mon = Assert.Single(config.NutUsers);
        Assert.Empty(mon.Actions);
        Assert.Empty(mon.InstantCommands);
        Assert.Empty(mon.AllowedUps);
        Assert.Equal("root", Assert.Single(config.WebUsers).Name);
        Assert.Empty(config.Notifications.Email.To);
        Assert.Empty(Assert.Single(config.Notifications.Webhooks).Headers);
        Assert.Empty(config.Notifications.Commands);
        Assert.Empty(config.HostProtection.Ups);
        Assert.Empty(config.History.Variables);
    }

    [Fact]
    public void A_ups_without_driver_is_reported_as_invalid()
    {
        WriteConfig($$"""{ "ups": [ { "name": "ups1", "driver": null } ], "webUsers": [ {{AdminJson()}} ] }""");

        var ex = Assert.Throws<ConfigLoadException>(Open);

        Assert.Contains("ups[0].driver", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task A_change_that_removes_the_driver_fails_validation()
    {
        JsonConfigStore store = Open();

        var ex = await Assert.ThrowsAsync<ConfigValidationException>(() =>
            store.UpdateAsync(c => c.Ups.Add(new UpsConfig { Name = "ups1", Driver = null! }), CommandOrigin.System, "x"));

        Assert.Contains("ups[0].driver", ex.Errors.Keys);
    }

    [Fact]
    public void An_existing_account_named_admin_that_is_not_an_administrator_gets_a_new_administrator_next_to_it()
    {
        WriteConfig("""{ "webUsers": [ { "name": "admin", "role": "viewer", "passwordHash": "h" } ] }""");

        JsonConfigStore store = Open();

        Assert.Equal(2, store.Current.WebUsers.Count);
        WebUserConfig created = store.Current.WebUsers[1];
        Assert.Matches("^admin[0-9]{3}$", created.Name);
        Assert.Equal(WebRole.Admin, created.Role);
        Assert.NotNull(store.GeneratedAdminPassword);
    }

    [Fact]
    public void Invalid_json_is_reported_with_its_line()
    {
        WriteConfig("{\n  \"server\": {\n    \"name\": \"x\"\n  }\n  \"nut\": {}\n}\n");

        var ex = Assert.Throws<ConfigLoadException>(Open);

        Assert.Contains("not valid JSON (line 5)", ex.Message, StringComparison.Ordinal);
    }

    // Windows editors and PowerShell 5.1 (Set-Content / Out-File -Encoding UTF8) save UTF-8 with a byte order mark.
    [Fact]
    public void A_file_saved_with_a_byte_order_mark_loads()
    {
        File.WriteAllText(_paths.ConfigFile, $$"""{ "server": { "name": "BOM" }, "webUsers": [ {{AdminJson()}} ] }""",
                          new UTF8Encoding(encoderShouldEmitUTF8Identifier: true));

        Assert.Equal("BOM", Open().Current.Server.Name);
    }

    [Fact]
    public void A_file_containing_null_is_reported_as_empty()
    {
        WriteConfig("null");
        var ex = Assert.Throws<ConfigLoadException>(Open);
        Assert.Contains("empty", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void An_invalid_configuration_refuses_to_load_and_names_the_fields()
    {
        WriteConfig($$"""{ "ups": [ { "name": "bad name", "driver": "nope" } ], "webUsers": [ {{AdminJson()}} ] }""");

        var ex = Assert.Throws<ConfigLoadException>(Open);

        Assert.Contains("ups[0].name", ex.Message, StringComparison.Ordinal);
        Assert.Contains("ups[0].driver", ex.Message, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Updates_are_saved_atomically_with_a_backup_and_announced()
    {
        JsonConfigStore store = Open();
        string before = File.ReadAllText(_paths.ConfigFile);
        NutHubConfig previous = store.Current;
        using var recorder = new HubRecorder(_hub);
        ConfigChangedEventArgs? changed = null;
        store.Changed += (_, e) => changed = e;
        var origin = CommandOrigin.Web("alice", "10.0.0.5");

        NutHubConfig next = await store.UpdateAsync(c => c.Server.Name = "Renamed", origin, "Server renamed");

        Assert.Same(next, store.Current);
        Assert.NotSame(previous, next);
        Assert.Equal("NutHub", previous.Server.Name); // the old snapshot is never modified
        Assert.Contains("\"name\": \"Renamed\"", File.ReadAllText(_paths.ConfigFile), StringComparison.Ordinal);
        Assert.Equal(before, File.ReadAllText(_paths.ConfigFile + ".bak"));
        Assert.False(File.Exists(_paths.ConfigFile + ".tmp"));

        Assert.NotNull(changed);
        Assert.Same(previous, changed!.Previous);
        Assert.Same(next, changed.Current);
        Assert.Equal(origin, changed.Origin);
        Assert.Contains(recorder.Messages, m => m is ConfigChangedMessage { Description: "Server renamed" });
        UpsEvent audit = Assert.Single(recorder.Events);
        Assert.Equal(UpsEventType.ConfigurationChanged, audit.Type);
        Assert.Equal("web:alice@10.0.0.5", audit.Actor);
    }

    [Fact]
    public async Task A_change_that_fails_validation_leaves_everything_untouched()
    {
        JsonConfigStore store = Open();
        byte[] before = File.ReadAllBytes(_paths.ConfigFile);
        NutHubConfig current = store.Current;
        bool changed = false;
        store.Changed += (_, _) => changed = true;

        var ex = await Assert.ThrowsAsync<ConfigValidationException>(() =>
            store.UpdateAsync(c => c.Web.HttpPort = 0, CommandOrigin.System, "bad port"));

        Assert.Contains("web.httpPort", ex.Errors.Keys);
        Assert.Same(current, store.Current);
        Assert.Equal(before, File.ReadAllBytes(_paths.ConfigFile));
        Assert.False(File.Exists(_paths.ConfigFile + ".bak"));
        Assert.False(changed);
    }

    [Fact]
    public async Task An_exception_in_the_change_leaves_everything_untouched_and_the_store_usable()
    {
        JsonConfigStore store = Open();
        NutHubConfig current = store.Current;

        await Assert.ThrowsAsync<InvalidOperationException>(() =>
            store.UpdateAsync(_ => throw new InvalidOperationException("no"), CommandOrigin.System, "x"));

        Assert.Same(current, store.Current);
        await store.UpdateAsync(c => c.Server.Location = "Basement", CommandOrigin.System, "ok");
        Assert.Equal("Basement", store.Current.Server.Location);
    }

    [Fact]
    public async Task Secrets_are_encrypted_when_saved()
    {
        JsonConfigStore store = Open();

        await store.UpdateAsync(c =>
        {
            c.Notifications.Email.Password = "smtp-secret";
            c.Web.CertificatePassword = "pfx-secret";
            c.Notifications.Webhooks.Add(new WebhookSettings
            {
                Url = "https://hooks.example/x",
                Headers = { ["Authorization"] = "Bearer token-secret" },
            });
            c.Ups.Add(new UpsConfig
            {
                Name = "ups1",
                Driver = "fake",
                Options = { ["Community"] = "snmp-secret", ["port"] = "COM3" },
            });
        }, CommandOrigin.System, "secrets");

        string json = File.ReadAllText(_paths.ConfigFile);
        foreach (string secret in new[] { "smtp-secret", "pfx-secret", "token-secret", "snmp-secret" })
        {
            Assert.DoesNotContain(secret, json, StringComparison.Ordinal);
        }

        Assert.Contains("\"port\": \"COM3\"", json, StringComparison.Ordinal); // not a secret option
        NutHubConfig config = store.Current;
        Assert.Equal("smtp-secret", _secrets.Unprotect(config.Notifications.Email.Password));
        Assert.Equal("Bearer token-secret", _secrets.Unprotect(config.Notifications.Webhooks[0].Headers["authorization"]));
        Assert.Equal("snmp-secret", _secrets.Unprotect(config.Ups[0].Options["community"]));
    }

    [Fact]
    public async Task Updated_configurations_keep_case_insensitive_driver_options()
    {
        JsonConfigStore store = Open();
        await store.UpdateAsync(c => c.Ups.Add(new UpsConfig { Name = "ups1", Driver = "fake", Options = { ["Port"] = "COM3" } }),
                                CommandOrigin.System, "add");
        await store.UpdateAsync(c => c.Server.Name = "x", CommandOrigin.System, "another change");

        Assert.Equal("COM3", store.Current.Ups[0].Options["PORT"]);
    }

    [Fact]
    public void Secrets_typed_by_hand_are_encrypted_at_load()
    {
        WriteConfig($$"""
            { "notifications": { "email": { "password": "typed-by-hand" } }, "webUsers": [ {{AdminJson()}} ] }
            """);

        JsonConfigStore store = Open();

        Assert.True(_secrets.IsProtected(store.Current.Notifications.Email.Password));
        Assert.DoesNotContain("typed-by-hand", File.ReadAllText(_paths.ConfigFile), StringComparison.Ordinal);
    }

    [Fact]
    public async Task Hand_edits_are_picked_up_while_running()
    {
        JsonConfigStore store = Open();
        store.StartWatching();
        var changed = new TaskCompletionSource<ConfigChangedEventArgs>(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, e) => changed.TrySetResult(e);

        string json = File.ReadAllText(_paths.ConfigFile)
            .Replace("\"name\": \"NutHub\"", "\"name\": \"Edited by hand\"", StringComparison.Ordinal);
        File.WriteAllText(_paths.ConfigFile, json);

        ConfigChangedEventArgs e = await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Edited by hand", store.Current.Server.Name);
        Assert.Equal("file", e.Origin.Source);
    }

    [Fact]
    public async Task An_invalid_hand_edit_is_ignored()
    {
        JsonConfigStore store = Open();
        store.StartWatching();
        NutHubConfig current = store.Current;
        bool changed = false;
        store.Changed += (_, _) => changed = true;

        File.WriteAllText(_paths.ConfigFile, "{ this is not json");
        await Task.Delay(1500);

        Assert.Same(current, store.Current);
        Assert.False(changed);

        // The next valid edit still applies.
        var applied = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, _) => applied.TrySetResult();
        string json = JsonSerializer.Serialize(current, NutHubJson.File).Replace("\"NutHub\"", "\"Fixed\"", StringComparison.Ordinal);
        File.WriteAllText(_paths.ConfigFile, json);
        await applied.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("Fixed", store.Current.Server.Name);
    }

    // The reload runs on a timer thread: an exception escaping from it would end the whole process.
    [Fact]
    public async Task A_hand_edit_with_null_entries_is_applied_without_them()
    {
        JsonConfigStore store = Open();
        store.StartWatching();
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        store.Changed += (_, _) => changed.TrySetResult();

        WriteConfig($$"""
            { "ups": [ null, { "name": "ups1", "driver": "fake", "options": { "port": null } } ],
              "webUsers": [ null, {{AdminJson()}} ] }
            """);

        await changed.Task.WaitAsync(TimeSpan.FromSeconds(10));
        UpsConfig ups = Assert.Single(store.Current.Ups);
        Assert.Empty(ups.Options);
        Assert.Single(store.Current.WebUsers);
    }

    [Fact]
    public async Task The_store_does_not_reload_its_own_writes()
    {
        JsonConfigStore store = Open();
        store.StartWatching();
        int changes = 0;
        store.Changed += (_, _) => Interlocked.Increment(ref changes);

        await store.UpdateAsync(c => c.Server.Location = "Lab", CommandOrigin.System, "own write");
        await Task.Delay(1500);

        Assert.Equal(1, changes);
    }
}
