using Microsoft.Extensions.DependencyInjection;
using NutHub.Core.Configuration;
using NutHub.Core.Model;
using NutHub.Core.Security;

namespace NutHub.Cli;

/// <summary>
/// "nuthub nut-user list|add|set-password|remove": the accounts NUT clients log in with (upsd.users), for scripted
/// deployments. The web panel manages the same accounts.
/// </summary>
internal static class NutUserCommand
{
    private const string Usage =
        "nuthub nut-user list | add <name> [--password P | --generate] [--monitor primary|secondary|none] " +
        "[--actions SET,FSD] [--instcmds ALL|cmd,...] [--ups name,...] | set-password <name> [--password P | " +
        "--generate] | remove <name>";

    public static async Task<int> ExecuteAsync(IReadOnlyList<string> args)
    {
        if (args.Count == 0)
        {
            throw new UsageException($"Missing action. Usage: {Usage}");
        }

        string action = args[0];
        IReadOnlyList<string> rest = args.Skip(1).ToList();
        return action switch
        {
            "list" => List(Parse(rest, "nut-user list", [], [])),
            "add" => await AddAsync(Parse(rest, "nut-user add", PasswordInput.Flags,
                                          [.. PasswordInput.Valued, "--monitor", "--actions", "--instcmds", "--ups"]))
                .ConfigureAwait(false),
            "set-password" => await SetPasswordAsync(Parse(rest, "nut-user set-password", PasswordInput.Flags,
                                                           PasswordInput.Valued)).ConfigureAwait(false),
            "remove" => await RemoveAsync(Parse(rest, "nut-user remove", [], [])).ConfigureAwait(false),
            _ => throw new UsageException($"Unknown action '{action}'. Usage: {Usage}"),
        };
    }

    private static ParsedArguments Parse(IReadOnlyList<string> args, string command, IReadOnlyCollection<string> flags,
                                         IReadOnlyCollection<string> valued) =>
        ArgumentParser.Parse(args, command, flags, [.. valued, "--data-dir", "--config"]);

    private static int List(ParsedArguments parsed)
    {
        parsed.ExpectPositionals(0, "nuthub nut-user list");
        using ToolHost tool = ToolHost.Create(parsed);
        NutHubConfig config = tool.LoadConfiguration().Current;
        if (config.NutUsers.Count == 0)
        {
            Console.WriteLine($"No NUT account in {tool.Paths.ConfigFile}.");
            return ExitCodes.Ok;
        }

        foreach (NutUserConfig user in config.NutUsers)
        {
            string actions = user.Actions.Count == 0 ? "-" : string.Join(",", user.Actions);
            string commands = user.InstantCommands.Count == 0 ? "-" : string.Join(",", user.InstantCommands);
            string ups = user.AllowedUps.Count == 0 ? "all" : string.Join(",", user.AllowedUps);
            Console.WriteLine($"{user.Name}\tmonitor={user.Monitor.ToString().ToLowerInvariant()}\t" +
                              $"actions={actions}\tinstcmds={commands}\tups={ups}");
        }

        return ExitCodes.Ok;
    }

    private static async Task<int> AddAsync(ParsedArguments parsed)
    {
        parsed.ExpectPositionals(1, "nuthub nut-user add <name> [options]");
        string name = parsed.Positionals[0];
        if (!ConfigValidator.IsValidAccountName(name))
        {
            throw new CommandException($"'{name}' is not a valid account name: use 1 to 64 letters, digits, '.', " +
                                       "'_', '-' or '@'.");
        }

        NutMonitorRole monitor = ParseMonitor(parsed.Get("--monitor") ?? "secondary");
        List<string> actions = [.. (parsed.GetList("--actions") ?? []).Select(a => a.ToUpperInvariant()).Distinct()];
        List<string> commands = [.. (parsed.GetList("--instcmds") ?? []).Distinct(StringComparer.OrdinalIgnoreCase)];
        List<string> allowedUps = [.. (parsed.GetList("--ups") ?? []).Distinct(StringComparer.OrdinalIgnoreCase)];

        using ToolHost tool = ToolHost.Create(parsed);
        JsonConfigStore store = tool.LoadConfiguration();
        if (Find(store.Current, name) is not null)
        {
            throw new CommandException($"The NUT account '{name}' already exists; use 'nuthub nut-user set-password'.");
        }

        string password = PasswordInput.Obtain(parsed, $"the NUT account '{name}'", out bool generated);
        string hash = tool.Services.GetRequiredService<IPasswordHasher>().Hash(password);
        await store.UpdateAsync(config =>
            {
                if (Find(config, name) is not null)
                {
                    throw new CommandException($"The NUT account '{name}' was added meanwhile.");
                }

                config.NutUsers.Add(new NutUserConfig
                {
                    Name = name,
                    PasswordHash = hash,
                    Monitor = monitor,
                    Actions = actions,
                    InstantCommands = commands,
                    AllowedUps = allowedUps,
                });
            },
            Origin, $"NUT account '{name}' added from the command line").ConfigureAwait(false);

        ServiceAccountFiles.RestoreOwnership(tool.Paths);
        if (generated)
        {
            Console.WriteLine($"Password of the NUT account '{name}': {password}");
        }

        Console.WriteLine($"NUT account '{name}' added to {tool.Paths.ConfigFile} " +
                          $"(monitor: {monitor.ToString().ToLowerInvariant()}).");
        return ExitCodes.Ok;
    }

    private static async Task<int> SetPasswordAsync(ParsedArguments parsed)
    {
        parsed.ExpectPositionals(1, "nuthub nut-user set-password <name> [--password P | --generate]");
        string name = parsed.Positionals[0];

        using ToolHost tool = ToolHost.Create(parsed);
        JsonConfigStore store = tool.LoadConfiguration();
        NutUserConfig existing = Find(store.Current, name) ?? throw NotFound(name, store.Current);

        string password = PasswordInput.Obtain(parsed, $"the NUT account '{existing.Name}'", out bool generated);
        string hash = tool.Services.GetRequiredService<IPasswordHasher>().Hash(password);
        await store.UpdateAsync(config => (Find(config, name) ?? throw NotFound(name, config)).PasswordHash = hash,
                                Origin, $"Password of the NUT account '{existing.Name}' set from the command line")
            .ConfigureAwait(false);

        ServiceAccountFiles.RestoreOwnership(tool.Paths);
        if (generated)
        {
            Console.WriteLine($"New password of the NUT account '{existing.Name}': {password}");
        }

        Console.WriteLine($"Password of the NUT account '{existing.Name}' changed in {tool.Paths.ConfigFile}.");
        return ExitCodes.Ok;
    }

    private static async Task<int> RemoveAsync(ParsedArguments parsed)
    {
        parsed.ExpectPositionals(1, "nuthub nut-user remove <name>");
        string name = parsed.Positionals[0];

        using ToolHost tool = ToolHost.Create(parsed);
        JsonConfigStore store = tool.LoadConfiguration();
        NutUserConfig existing = Find(store.Current, name) ?? throw NotFound(name, store.Current);
        await store.UpdateAsync(config => config.NutUsers.RemoveAll(u => string.Equals(u.Name, existing.Name, StringComparison.Ordinal)),
                                Origin, $"NUT account '{existing.Name}' removed from the command line")
            .ConfigureAwait(false);

        ServiceAccountFiles.RestoreOwnership(tool.Paths);
        Console.WriteLine($"NUT account '{existing.Name}' removed from {tool.Paths.ConfigFile}.");
        return ExitCodes.Ok;
    }

    private static CommandOrigin Origin => new("cli", Environment.UserName);

    // NUT account names are case-sensitive, as in upsd.users; an exact match wins over a case-insensitive one.
    // An ambiguous case-insensitive match ("Mon" and "mon" both exist) matches nothing.
    private static NutUserConfig? Find(NutHubConfig config, string name)
    {
        NutUserConfig? exact = config.NutUsers.FirstOrDefault(u => string.Equals(u.Name, name, StringComparison.Ordinal));
        if (exact is not null)
        {
            return exact;
        }

        var similar = config.NutUsers.Where(u => string.Equals(u.Name, name, StringComparison.OrdinalIgnoreCase)).ToList();
        return similar.Count == 1 ? similar[0] : null;
    }

    private static CommandException NotFound(string name, NutHubConfig config)
    {
        string known = string.Join(", ", config.NutUsers.Select(u => u.Name));
        return new CommandException($"There is no NUT account named '{name}'" +
                                    (known.Length > 0 ? $" (accounts: {known})." : "."));
    }

    private static NutMonitorRole ParseMonitor(string value) => value.ToLowerInvariant() switch
    {
        "primary" or "master" => NutMonitorRole.Primary,
        "secondary" or "slave" => NutMonitorRole.Secondary,
        "none" => NutMonitorRole.None,
        _ => throw new UsageException($"--monitor must be primary, secondary or none (not '{value}')."),
    };
}
