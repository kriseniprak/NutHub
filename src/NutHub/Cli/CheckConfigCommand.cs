using System.Security.Cryptography;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.Core.Drivers;
using NutHub.Core.Runtime;
using NutHub.Core.Security;
using NutHub.Hosting;

namespace NutHub.Cli;

/// <summary>
/// "nuthub check-config": loads and validates the configuration exactly as the server would, without touching the
/// real file (the server saves on load when it encrypts secrets typed in clear text or creates the first
/// administrator). Exit code 0 when valid.
/// </summary>
internal static class CheckConfigCommand
{
    private const string Usage = "nuthub check-config [--data-dir DIR] [--config FILE]";

    public static Task<int> ExecuteAsync(IReadOnlyList<string> args)
    {
        ParsedArguments parsed = ArgumentParser.Parse(args, "check-config", [], ["--data-dir", "--config"]);
        parsed.ExpectPositionals(0, Usage);
        NutHubPaths paths = NutHubPaths.Resolve(parsed.Get("--data-dir"), parsed.Get("--config"));

        if (!File.Exists(paths.ConfigFile))
        {
            Console.WriteLine($"There is no configuration file at {paths.ConfigFile}: NutHub creates one with the " +
                              "default settings when it first starts.");
            return Task.FromResult(ExitCodes.Ok);
        }

        using ToolHost tool = ToolHost.Create(paths);
        var catalog = tool.Services.GetRequiredService<IDriverCatalog>();

        string scratch = Path.Combine(Path.GetTempPath(), "nuthub-check-" + Guid.NewGuid().ToString("N"));
        try
        {
            var scratchPaths = new NutHubPaths(Path.Combine(scratch, "data"), Path.Combine(scratch, "nuthub.json"));
            Directory.CreateDirectory(scratch);
            File.Copy(paths.ConfigFile, scratchPaths.ConfigFile);

            NutHubConfig config;
            try
            {
                using var store = new JsonConfigStore(scratchPaths, new AesSecretProtector(RandomNumberGenerator.GetBytes(32)),
                                                      new Pbkdf2PasswordHasher(), catalog,
                                                      new EventHub(NullLogger<EventHub>.Instance), TimeProvider.System,
                                                      NullLogger<JsonConfigStore>.Instance);
                config = store.Current;
            }
            catch (ConfigLoadException ex)
            {
                string message = ex.Message.Replace(scratchPaths.ConfigFile, paths.ConfigFile, StringComparison.Ordinal);
                Console.Error.WriteLine(message.Contains(paths.ConfigFile, StringComparison.Ordinal)
                                            ? message
                                            : $"{paths.ConfigFile}: {message}");
                return Task.FromResult(ExitCodes.Error);
            }

            // What the copy cannot tell: whether the encrypted values match this installation's key.
            List<string> errors = CheckSecrets(paths, config, out string? secretsNote);
            foreach (UpsConfig ups in config.Ups.Where(u => u.Enabled))
            {
                if (catalog.Find(ups.Driver) is { } factory && !catalog.IsSupportedHere(factory))
                {
                    Console.WriteLine($"Warning: the UPS '{ups.Name}' uses the driver '{ups.Driver}', which does not " +
                                      "work on this operating system.");
                }
            }

            if (secretsNote is not null)
            {
                Console.WriteLine(secretsNote);
            }

            if (errors.Count > 0)
            {
                Console.Error.WriteLine($"The configuration file {paths.ConfigFile} has problems:");
                foreach (string error in errors)
                {
                    Console.Error.WriteLine("  " + error);
                }

                return Task.FromResult(ExitCodes.Error);
            }

            Console.WriteLine($"The configuration file {paths.ConfigFile} is valid.");
            Console.WriteLine($"  UPS: {Describe(config.Ups.Select(u => $"{u.Name} ({u.Driver}{(u.Enabled ? "" : ", disabled")})"))}");
            Console.WriteLine($"  NUT server: {(config.Nut.Enabled ? string.Join(", ", config.Nut.Listen) : "disabled")}; " +
                              $"{config.NutUsers.Count} NUT account(s)");
            IReadOnlyList<string> urls = PanelAddresses.WebUrls(config.Web);
            Console.WriteLine($"  Web panel: {(urls.Count > 0 ? string.Join("  ", urls) : "disabled")}; " +
                              $"{config.WebUsers.Count} web account(s)");
            Console.WriteLine($"  Host protection: {(config.HostProtection.Enabled ? (config.HostProtection.DryRun ? "enabled (dry run)" : "enabled") : "disabled")}");
            return Task.FromResult(ExitCodes.Ok);
        }
        finally
        {
            try
            {
                Directory.Delete(scratch, recursive: true);
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // A leftover in the temporary folder is harmless.
            }
        }
    }

    private static string Describe(IEnumerable<string> items)
    {
        string text = string.Join(", ", items);
        return text.Length == 0 ? "none" : text;
    }

    /// <summary>Tries to decrypt every protected value with the installation's secret key.</summary>
    private static List<string> CheckSecrets(NutHubPaths paths, NutHubConfig config,
                                             out string? note)
    {
        note = null;
        var protectedValues = new List<(string Where, string Value)>();
        void Add(string where, string? value)
        {
            if (value is not null && value.StartsWith("enc:", StringComparison.Ordinal))
            {
                protectedValues.Add((where, value));
            }
        }

        Add("nut.tls.certificatePassword", config.Nut.Tls.CertificatePassword);
        Add("web.certificatePassword", config.Web.CertificatePassword);
        Add("notifications.email.password", config.Notifications.Email.Password);
        for (int i = 0; i < config.Notifications.Webhooks.Count; i++)
        {
            foreach (var (header, value) in config.Notifications.Webhooks[i].Headers)
            {
                Add($"notifications.webhooks[{i}].headers.{header}", value);
            }
        }

        for (int i = 0; i < config.Ups.Count; i++)
        {
            foreach (var (key, value) in config.Ups[i].Options)
            {
                Add($"ups[{i}].options.{key}", value);
            }
        }

        // The copy was loaded with a throw-away key: values typed in clear text were encrypted by the check itself
        // and are fine. Only the values encrypted in the original file are checked.
        string original = File.ReadAllText(paths.ConfigFile);
        protectedValues.RemoveAll(p => !original.Contains(p.Value, StringComparison.Ordinal));
        var errors = new List<string>();
        if (protectedValues.Count == 0)
        {
            return errors;
        }

        if (!File.Exists(paths.SecretKeyFile))
        {
            errors.Add($"The file has encrypted values but the secret key {paths.SecretKeyFile} is missing: " +
                       "type those passwords again.");
            return errors;
        }

        AesSecretProtector protector;
        try
        {
            protector = AesSecretProtector.LoadOrCreate(paths.SecretKeyFile);
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            note = $"Note: the encrypted values were not checked ({paths.SecretKeyFile}: {ex.Message}).";
            return errors;
        }
        catch (Exception ex) when (ex is FormatException or ArgumentException)
        {
            errors.Add($"The secret key {paths.SecretKeyFile} is corrupt: {ex.Message}");
            return errors;
        }

        foreach (var (where, value) in protectedValues)
        {
            try
            {
                protector.Unprotect(value);
            }
            catch (CryptographicException)
            {
                errors.Add($"{where}: cannot be decrypted with {paths.SecretKeyFile} (was the file copied from " +
                           "another installation?); type the value again.");
            }
        }

        return errors;
    }
}
