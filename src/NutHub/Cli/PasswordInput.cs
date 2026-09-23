using System.Text;
using NutHub.Core.Security;

namespace NutHub.Cli;

/// <summary>
/// Gets a new password from --password, --generate, standard input (for scripts: nothing shows up in the process
/// list) or an interactive prompt without echo.
/// </summary>
internal static class PasswordInput
{
    public const int MinimumLength = 8;
    public const int MaximumLength = 128;

    public static readonly string[] Flags = ["--generate"];
    public static readonly string[] Valued = ["--password"];

    /// <param name="parsed">The command line.</param>
    /// <param name="subject">What the password is for, e.g. "the web account 'admin'".</param>
    /// <param name="generated">True when the password was generated and must be shown to the user.</param>
    public static string Obtain(ParsedArguments parsed, string subject, out bool generated)
    {
        generated = false;
        if (parsed.Has("--generate"))
        {
            if (parsed.Has("--password"))
            {
                throw new UsageException("Use either --password or --generate, not both.");
            }

            generated = true;
            return Pbkdf2PasswordHasher.GeneratePassword(20);
        }

        string password;
        if (parsed.Get("--password") is { } given)
        {
            password = given;
        }
        else if (Console.IsInputRedirected)
        {
            password = Console.In.ReadLine()?.TrimEnd('\r', '\n')
                       ?? throw new CommandException("No password on standard input.");
        }
        else
        {
            password = Prompt($"New password for {subject}: ");
            string again = Prompt("Type it again: ");
            if (!string.Equals(password, again, StringComparison.Ordinal))
            {
                throw new CommandException("The two passwords are different; nothing was changed.");
            }
        }

        Validate(password);
        return password;
    }

    private static void Validate(string password)
    {
        if (password.Length < MinimumLength)
        {
            throw new CommandException($"The password needs at least {MinimumLength} characters.");
        }

        if (password.Length > MaximumLength)
        {
            throw new CommandException($"The password can have at most {MaximumLength} characters.");
        }

        if (password.Any(char.IsControl))
        {
            throw new CommandException("The password cannot contain control characters.");
        }
    }

    /// <summary>Reads a line without echoing it; the prompt goes to standard error so standard output stays clean.</summary>
    private static string Prompt(string label)
    {
        Console.Error.Write(label);
        var text = new StringBuilder();
        while (true)
        {
            ConsoleKeyInfo key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }

                continue;
            }

            if (key.KeyChar != '\0' && !char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return text.ToString();
    }
}
