using System.Runtime.InteropServices;
using NutHub.Core;
using NutHub.Core.Configuration;
using NutHub.ServiceSetup;

namespace NutHub.Cli;

/// <summary>
/// Dispatches the command line to the commands and turns expected failures into readable messages and exit codes.
/// </summary>
internal static class CommandLineApplication
{
    public static async Task<int> RunAsync(string[] args)
    {
        try
        {
            return await DispatchAsync(args).ConfigureAwait(false);
        }
        catch (UsageException ex)
        {
            Console.Error.WriteLine($"nuthub: {ex.Message}");
            Console.Error.WriteLine("Run 'nuthub help' for the list of commands and options.");
            return ExitCodes.Usage;
        }
        catch (CommandException ex)
        {
            Console.Error.WriteLine($"nuthub: {ex.Message}");
            return ExitCodes.Error;
        }
        catch (ConfigLoadException ex)
        {
            Console.Error.WriteLine($"nuthub: {ex.Message}{AccessHint(ex.InnerException)}");
            return ExitCodes.Error;
        }
        catch (ConfigValidationException ex)
        {
            Console.Error.WriteLine("nuthub: the change was not saved because the configuration would not be valid:");
            foreach (var (key, message) in ex.Errors)
            {
                Console.Error.WriteLine($"  {key}: {message}");
            }

            return ExitCodes.Error;
        }
        catch (Exception ex) when (ex is UnauthorizedAccessException or IOException)
        {
            Console.Error.WriteLine($"nuthub: {ex.Message}{AccessHint(ex)}");
            return ExitCodes.Error;
        }
        catch (OperationCanceledException)
        {
            Console.Error.WriteLine("nuthub: cancelled.");
            return ExitCodes.Error;
        }
        catch (Exception ex)
        {
            // Not an expected failure: the details are worth reporting as they are.
            Console.Error.WriteLine($"nuthub: unexpected error: {ex}");
            return ExitCodes.Error;
        }
    }

    private static Task<int> DispatchAsync(string[] args)
    {
        if (args.Length == 0)
        {
            return RunCommand.ExecuteAsync([]);
        }

        string command = args[0];
        string[] rest = args[1..];
        switch (command)
        {
            case "run":
                return RunCommand.ExecuteAsync(rest);
            case "service":
                return ServiceCommand.ExecuteAsync(rest);
            case "passwd":
                return PasswdCommand.ExecuteAsync(rest);
            case "nut-user":
                return NutUserCommand.ExecuteAsync(rest);
            case "devices":
                return DevicesCommand.ExecuteAsync(rest);
            case "check-config":
                return CheckConfigCommand.ExecuteAsync(rest);
            case "healthcheck":
                return HealthCheckCommand.ExecuteAsync(rest);
            case "version" or "--version":
                Console.WriteLine($"NutHub {NutHubInfo.Version} ({RuntimeInformation.FrameworkDescription}, " +
                                  $"{RuntimeInformation.RuntimeIdentifier})");
                return Task.FromResult(ExitCodes.Ok);
            case "help" or "--help" or "-h" or "-?" or "/?":
                Console.WriteLine(HelpText.Usage);
                return Task.FromResult(ExitCodes.Ok);
            default:
                // "nuthub --data-dir X" runs the server, like "nuthub run --data-dir X".
                if (command.StartsWith('-'))
                {
                    return RunCommand.ExecuteAsync(args);
                }

                throw new UsageException($"Unknown command '{command}'.");
        }
    }

    /// <summary>Most access errors come from running a command as a user that cannot read the service's files.</summary>
    private static string AccessHint(Exception? ex) =>
        ex is UnauthorizedAccessException
            ? OperatingSystem.IsWindows()
                ? " Run the command from an elevated prompt (Run as administrator), or use --data-dir for a private instance."
                : " Run the command as root (sudo), or use --data-dir for a private instance."
            : "";
}
