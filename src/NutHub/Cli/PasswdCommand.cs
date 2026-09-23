using System.Text.RegularExpressions;
using Microsoft.Extensions.DependencyInjection;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Security;

namespace NutHub.Cli;

/// <summary>
/// "nuthub passwd &lt;web-user&gt;": sets the password of a web panel account, e.g. to recover a lost administrator
/// password. Goes through the configuration store, so a running server applies it through its file watcher.
/// </summary>
internal static class PasswdCommand
{
    private const string Usage = "nuthub passwd <web-user> [--password P | --generate] [--must-change]";

    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args)
    {
        ParsedArguments parsed = ArgumentParser.Parse(args, "passwd", [.. PasswordInput.Flags, "--must-change"],
                                                      [.. PasswordInput.Valued, "--data-dir", "--config"]);
        parsed.ExpectPositionals(1, Usage);
        string name = parsed.Positionals[0];

        using ToolHost tool = ToolHost.Create(parsed);
        JsonConfigStore store = tool.LoadConfiguration();
        var hasher = tool.Services.GetRequiredService<IPasswordHasher>();

        WebUserConfig? existing = FindUser(store.Current, name);
        if (existing is null)
        {
            string known = string.Join(", ", store.Current.WebUsers.Select(u => u.Name));
            throw new CommandException($"There is no web account named '{name}' in {tool.Paths.ConfigFile}" +
                                       (known.Length > 0 ? $" (accounts: {known})." : "."));
        }

        string password = PasswordInput.Obtain(parsed, $"the web account '{existing.Name}'", out bool generated);
        bool mustChange = parsed.Has("--must-change");
        string hash = hasher.Hash(password);

        await store.UpdateAsync(config =>
            {
                WebUserConfig user = FindUser(config, name)
                                     ?? throw new CommandException($"The web account '{name}' was removed meanwhile.");
                user.PasswordHash = hash;
                user.MustChangePassword = mustChange;
                // A new stamp ends the account's open sessions, as a password change from the panel does.
                user.SecurityStamp = Guid.NewGuid().ToString("N");
            },
            new CommandOrigin("cli", Environment.UserName),
            $"Password of the web account '{existing.Name}' set from the command line").ConfigureAwait(false);

        ServiceAccountFiles.RestoreOwnership(tool.Paths);
        DeleteStaleInitialPassword(tool.Paths.InitialPasswordFile, existing.Name);

        if (generated)
        {
            Console.WriteLine($"New password of the web account '{existing.Name}': {password}");
        }

        Console.WriteLine($"Password of '{existing.Name}' changed in {tool.Paths.ConfigFile}" +
                          (mustChange ? "; it must be changed at the next sign-in." : "."));
        if (existing.Disabled)
        {
            Console.WriteLine($"Note: the account '{existing.Name}' is disabled; enable it in the web panel.");
        }

        Console.WriteLine("A running NutHub applies the change within a few seconds.");
        return ExitCodes.Ok;
    }

    private static WebUserConfig? FindUser(NutHubConfig config, string name) =>
        config.WebUsers.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase));

    /// <summary>The first-start password file would now show a password that no longer works.</summary>
    private static void DeleteStaleInitialPassword(string file, string user)
    {
        try
        {
            if (File.Exists(file) &&
                Regex.IsMatch(File.ReadAllText(file), $@"^User:\s*{Regex.Escape(user)}\s*$",
                              RegexOptions.Multiline | RegexOptions.IgnoreCase))
            {
                File.Delete(file);
            }
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // Harmless: the file only holds an outdated password.
        }
    }
}
