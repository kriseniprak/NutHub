using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Logging;
using NutHub.Core.Drivers;
using NutHub.Core.Model;
using NutHub.Core.Runtime;
using NutHub.Core.Security;

namespace NutHub.Core.Configuration;

/// <summary>Holds the current configuration and applies changes to it.</summary>
public interface IConfigStore
{
    /// <summary>The current configuration. A shared snapshot: read it, never modify it.</summary>
    NutHubConfig Current { get; }

    /// <summary>
    /// Applies a change: <paramref name="mutate"/> receives a private copy, which is then validated, saved atomically
    /// and published. Throws <see cref="ConfigValidationException"/> (nothing is saved) when the result is invalid.
    /// Updates are serialised; <paramref name="description"/> ends up in the event log.
    /// </summary>
    Task<NutHubConfig> UpdateAsync(Action<NutHubConfig> mutate, CommandOrigin origin, string description,
                                   CancellationToken cancellationToken = default);

    /// <summary>Raised after every successful change, including an edit of the file by hand.</summary>
    event EventHandler<ConfigChangedEventArgs>? Changed;
}

public sealed class ConfigChangedEventArgs(NutHubConfig previous, NutHubConfig current, CommandOrigin origin,
                                           string description) : EventArgs
{
    public NutHubConfig Previous { get; } = previous;

    public NutHubConfig Current { get; } = current;

    public CommandOrigin Origin { get; } = origin;

    public string Description { get; } = description;
}

/// <summary>Thrown at startup when the configuration file cannot be read.</summary>
public sealed class ConfigLoadException(string message, Exception? inner = null) : Exception(message, inner);

/// <summary>
/// The configuration in a JSON file. Writes go to a temporary file that replaces the real one, with the previous
/// version kept as ".bak". Hand edits of the file are picked up while running.
/// </summary>
public sealed class JsonConfigStore : IConfigStore, IDisposable
{
    private readonly NutHubPaths _paths;
    private readonly ISecretProtector _secrets;
    private readonly IPasswordHasher _hasher;
    private readonly IDriverCatalog _drivers;
    private readonly EventHub _hub;
    private readonly TimeProvider _time;
    private readonly ILogger<JsonConfigStore> _logger;
    private readonly SemaphoreSlim _gate = new(1, 1);
    private FileSystemWatcher? _watcher;
    private Timer? _reloadTimer;
    private byte[] _lastWrittenHash = [];
    private NutHubConfig _current;

    public JsonConfigStore(NutHubPaths paths, ISecretProtector secrets, IPasswordHasher hasher, IDriverCatalog drivers,
                           EventHub hub, TimeProvider time, ILogger<JsonConfigStore> logger)
    {
        _paths = paths;
        _secrets = secrets;
        _hasher = hasher;
        _drivers = drivers;
        _hub = hub;
        _time = time;
        _logger = logger;
        _current = LoadOrCreate();
    }

    public NutHubConfig Current => Volatile.Read(ref _current);

    /// <summary>
    /// The password generated for the first "admin" account in this run, or null. The host prints it on an
    /// interactive console; it is also in <see cref="NutHubPaths.InitialPasswordFile"/>.
    /// </summary>
    public string? GeneratedAdminPassword { get; private set; }

    public event EventHandler<ConfigChangedEventArgs>? Changed;

    public async Task<NutHubConfig> UpdateAsync(Action<NutHubConfig> mutate, CommandOrigin origin, string description,
                                                CancellationToken cancellationToken = default)
    {
        await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        NutHubConfig previous;
        NutHubConfig next;
        try
        {
            previous = Current;
            next = NutHubJson.Clone(previous);
            mutate(next);
            ProtectSecrets(next);
            ConfigValidator.ThrowIfInvalid(next, _drivers);
            Save(next);
            Volatile.Write(ref _current, next);
        }
        finally
        {
            _gate.Release();
        }

        _logger.LogInformation("Configuration changed by {Origin}: {Description}", origin, description);
        RaiseChanged(previous, next, origin, description);
        return next;
    }

    /// <summary>Starts watching the file for edits made by hand.</summary>
    public void StartWatching()
    {
        if (_watcher is not null)
        {
            return;
        }

        try
        {
            _reloadTimer = new Timer(_ => ReloadFromDisk(), null, Timeout.Infinite, Timeout.Infinite);
            _watcher = new FileSystemWatcher(_paths.ConfigDirectory, Path.GetFileName(_paths.ConfigFile))
            {
                NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size,
            };
            _watcher.Changed += (_, _) => _reloadTimer.Change(750, Timeout.Infinite);
            _watcher.Created += (_, _) => _reloadTimer.Change(750, Timeout.Infinite);
            _watcher.Renamed += (_, _) => _reloadTimer.Change(750, Timeout.Infinite);
            _watcher.EnableRaisingEvents = true;
        }
        catch (Exception ex) when (ex is IOException or ArgumentException or PlatformNotSupportedException)
        {
            _logger.LogWarning(ex, "Cannot watch {File} for changes; edits by hand need a restart.", _paths.ConfigFile);
        }
    }

    public void Dispose()
    {
        _watcher?.Dispose();
        _reloadTimer?.Dispose();
        _gate.Dispose();
    }

    private void RaiseChanged(NutHubConfig previous, NutHubConfig next, CommandOrigin origin, string description)
    {
        try
        {
            Changed?.Invoke(this, new ConfigChangedEventArgs(previous, next, origin, description));
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "A configuration change handler failed.");
        }

        _hub.Publish(new ConfigChangedMessage(previous, next, origin, description));
        _hub.Publish(new UpsEventMessage(UpsEvent.Create(UpsEventType.ConfigurationChanged, _time.GetUtcNow(), null,
                                                         description, origin.ToString())));
    }

    private void ReloadFromDisk()
    {
        try
        {
            byte[] bytes = File.ReadAllBytes(_paths.ConfigFile);
            if (SHA256.HashData(bytes).AsSpan().SequenceEqual(_lastWrittenHash))
            {
                return; // our own write
            }

            NutHubConfig loaded = Parse(bytes);
            Normalize(loaded);
            ConfigValidator.ThrowIfInvalid(loaded, _drivers);

            NutHubConfig previous;
            _gate.Wait();
            try
            {
                previous = Current;
                bool hadClearSecrets = ProtectSecrets(loaded);
                if (hadClearSecrets)
                {
                    Save(loaded);
                }
                else
                {
                    _lastWrittenHash = SHA256.HashData(bytes);
                }

                Volatile.Write(ref _current, loaded);
            }
            finally
            {
                _gate.Release();
            }

            _logger.LogInformation("Configuration reloaded from {File} after an edit.", _paths.ConfigFile);
            RaiseChanged(previous, loaded, new CommandOrigin("file"), "Configuration file edited");
        }
        catch (Exception ex) when (ex is IOException or JsonException or ConfigLoadException or ConfigValidationException
                                       or UnauthorizedAccessException)
        {
            _logger.LogError("The edited configuration file was not applied: {Message}", ex.Message);
        }
        catch (Exception ex)
        {
            // This runs on a timer thread, where an exception would end the process.
            _logger.LogError(ex, "The edited configuration file was not applied.");
        }
    }

    private NutHubConfig LoadOrCreate()
    {
        _paths.EnsureCreated();
        NutHubConfig config;
        bool mustSave = false;

        if (File.Exists(_paths.ConfigFile))
        {
            byte[] bytes;
            try
            {
                bytes = File.ReadAllBytes(_paths.ConfigFile);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                throw new ConfigLoadException($"Cannot read {_paths.ConfigFile}: {ex.Message}", ex);
            }

            config = Parse(bytes);
            _lastWrittenHash = SHA256.HashData(bytes);
        }
        else
        {
            _logger.LogInformation("No configuration at {File}; creating a new one.", _paths.ConfigFile);
            config = new NutHubConfig();
            mustSave = true;
        }

        Normalize(config);

        if (!config.WebUsers.Any(u => u.Role == WebRole.Admin && !u.Disabled))
        {
            CreateInitialAdmin(config);
            mustSave = true;
        }

        mustSave |= ProtectSecrets(config);

        var errors = ConfigValidator.Validate(config, _drivers);
        if (errors.Count > 0)
        {
            // Refuse to start on an invalid file rather than guess: the message tells what to fix.
            throw new ConfigLoadException($"The configuration file {_paths.ConfigFile} is not valid: " +
                                          string.Join("; ", errors.Select(e => $"{e.Key}: {e.Value}")));
        }

        if (mustSave)
        {
            Save(config);
        }

        // Only now that the account is really saved.
        if (GeneratedAdminPassword is not null)
        {
            WriteInitialPasswordFile(config.WebUsers[^1].Name, GeneratedAdminPassword);
        }

        return config;
    }

    private static NutHubConfig Parse(byte[] bytes)
    {
        // Notepad and PowerShell 5.1 save UTF-8 with a byte order mark, which the JSON reader refuses.
        ReadOnlySpan<byte> json = bytes;
        if (json.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
        {
            json = json[3..];
        }

        try
        {
            return JsonSerializer.Deserialize<NutHubConfig>(json, NutHubJson.File)
                   ?? throw new ConfigLoadException("The configuration file is empty.");
        }
        catch (JsonException ex)
        {
            throw new ConfigLoadException(
                $"The configuration file is not valid JSON (line {ex.LineNumber + 1}): {ex.Message}", ex);
        }
    }

    /// <summary>
    /// Fills in what an older or hand-written file may lack. A null left by hand in a list or as a value carries
    /// nothing and is dropped, so that nothing further on has to expect one.
    /// </summary>
    private static void Normalize(NutHubConfig config)
    {
        config.Server ??= new ServerSettings();
        config.Nut ??= new NutServerSettings();
        config.Nut.Listen = config.Nut.Listen is null ? [new ListenEndpoint()] : WithoutNulls(config.Nut.Listen);
        config.Nut.AllowedNetworks = WithoutNulls(config.Nut.AllowedNetworks);
        config.Nut.Tls ??= new NutTlsSettings();
        config.Web ??= new WebSettings();
        config.Web.AllowedNetworks = WithoutNulls(config.Web.AllowedNetworks);
        config.Ups = WithoutNulls(config.Ups);
        foreach (var ups in config.Ups)
        {
            ups.Options = WithoutNullValues(ups.Options, StringComparer.OrdinalIgnoreCase);
            ups.Overrides = WithoutNullValues(ups.Overrides, StringComparer.Ordinal);
            ups.LowBattery ??= new LowBatteryPolicy();
        }

        config.NutUsers = WithoutNulls(config.NutUsers);
        foreach (var user in config.NutUsers)
        {
            user.Actions = WithoutNulls(user.Actions);
            user.InstantCommands = WithoutNulls(user.InstantCommands);
            user.AllowedUps = WithoutNulls(user.AllowedUps);
        }

        config.WebUsers = WithoutNulls(config.WebUsers);
        foreach (var user in config.WebUsers)
        {
            if (string.IsNullOrEmpty(user.SecurityStamp))
            {
                user.SecurityStamp = Guid.NewGuid().ToString("N");
            }
        }

        config.Notifications ??= new NotificationSettings();
        config.Notifications.Events ??= [.. UpsEventCatalog.DefaultNotified];
        config.Notifications.Email ??= new EmailSettings();
        config.Notifications.Email.To = WithoutNulls(config.Notifications.Email.To);
        config.Notifications.Webhooks = WithoutNulls(config.Notifications.Webhooks);
        foreach (var hook in config.Notifications.Webhooks)
        {
            hook.Headers = WithoutNullValues(hook.Headers, StringComparer.OrdinalIgnoreCase);
        }

        config.Notifications.Commands = WithoutNulls(config.Notifications.Commands);
        config.HostProtection ??= new HostProtectionSettings();
        config.HostProtection.Ups = WithoutNulls(config.HostProtection.Ups);
        config.History ??= new HistorySettings();
        config.History.Variables = WithoutNulls(config.History.Variables);
        config.SchemaVersion = Math.Max(config.SchemaVersion, 1);
    }

    private static List<T> WithoutNulls<T>(List<T>? list)
        where T : class
    {
        if (list is null)
        {
            return [];
        }

        list.RemoveAll(item => item is null);
        return list;
    }

    private static Dictionary<string, string> WithoutNullValues(Dictionary<string, string>? map, StringComparer comparer) =>
        new((map ?? []).Where(kv => kv.Value is not null), comparer);

    private void CreateInitialAdmin(NutHubConfig config)
    {
        string password = Pbkdf2PasswordHasher.GeneratePassword();
        string name = config.WebUsers.Any(u => string.Equals(u.Name, "admin", StringComparison.OrdinalIgnoreCase))
            ? "admin" + RandomNumberGenerator.GetInt32(100, 999)
            : "admin";
        config.WebUsers.Add(new WebUserConfig
        {
            Name = name,
            DisplayName = "Administrator",
            Role = WebRole.Admin,
            PasswordHash = _hasher.Hash(password),
            MustChangePassword = true,
        });
        GeneratedAdminPassword = password;
    }

    private void WriteInitialPasswordFile(string name, string password)
    {
        try
        {
            string text =
                $"NutHub web panel - first sign-in{Environment.NewLine}" +
                $"User:     {name}{Environment.NewLine}" +
                $"Password: {password}{Environment.NewLine}" +
                $"You will be asked to choose a new password. Delete this file afterwards.{Environment.NewLine}";
            File.WriteAllText(_paths.InitialPasswordFile, text, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
            FilePermissions.TryRestrictFile(_paths.InitialPasswordFile);
            _logger.LogWarning(
                "Created the web account '{User}' with a generated password, saved in {File}. Sign in and change it.",
                name, _paths.InitialPasswordFile);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            _logger.LogError(ex, "Could not write {File}.", _paths.InitialPasswordFile);
        }
    }

    /// <summary>Encrypts every secret still in clear text. Returns true when something was encrypted.</summary>
    private bool ProtectSecrets(NutHubConfig config)
    {
        bool changed = false;

        string? P(string? value)
        {
            if (!string.IsNullOrEmpty(value) && !_secrets.IsProtected(value))
            {
                changed = true;
                return _secrets.Protect(value);
            }

            return value;
        }

        config.Nut.Tls.CertificatePassword = P(config.Nut.Tls.CertificatePassword);
        config.Web.CertificatePassword = P(config.Web.CertificatePassword);
        config.Notifications.Email.Password = P(config.Notifications.Email.Password);
        foreach (var hook in config.Notifications.Webhooks)
        {
            foreach (string key in hook.Headers.Keys.ToList())
            {
                hook.Headers[key] = P(hook.Headers[key])!;
            }
        }

        foreach (var ups in config.Ups)
        {
            // A missing driver is reported by the validation; its options cannot be told apart yet.
            IUpsDriverFactory? factory = string.IsNullOrEmpty(ups.Driver) ? null : _drivers.Find(ups.Driver);
            foreach (string key in DriverCatalog.SecretKeys(factory))
            {
                foreach (string actual in ups.Options.Keys.Where(k => string.Equals(k, key, StringComparison.OrdinalIgnoreCase)).ToList())
                {
                    ups.Options[actual] = P(ups.Options[actual])!;
                }
            }
        }

        return changed;
    }

    private void Save(NutHubConfig config)
    {
        byte[] bytes = JsonSerializer.SerializeToUtf8Bytes(config, NutHubJson.File);
        string file = _paths.ConfigFile;
        string tmp = file + ".tmp";
        Directory.CreateDirectory(_paths.ConfigDirectory);
        File.WriteAllBytes(tmp, bytes);
        FilePermissions.TryRestrictFile(tmp);
        if (File.Exists(file))
        {
            try
            {
                File.Copy(file, file + ".bak", overwrite: true);
                FilePermissions.TryRestrictFile(file + ".bak");
            }
            catch (IOException ex)
            {
                _logger.LogWarning(ex, "Could not keep a backup of {File}.", file);
            }
        }

        _lastWrittenHash = SHA256.HashData(bytes);
        File.Move(tmp, file, overwrite: true);
    }
}
