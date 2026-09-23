namespace NutHub.Cli;

/// <summary>Process exit codes of every command.</summary>
internal static class ExitCodes
{
    public const int Ok = 0;
    public const int Error = 1;
    public const int Usage = 2;
}

/// <summary>A malformed command line: the message and a pointer to the help are printed, exit code 2.</summary>
internal sealed class UsageException(string message) : Exception(message);

/// <summary>
/// An expected failure (unknown account, missing rights, failed system command): only the message is printed, since
/// a stack trace would not help anyone fix it. Exit code 1.
/// </summary>
internal sealed class CommandException(string message) : Exception(message);

/// <summary>The positional arguments and options of one command.</summary>
internal sealed class ParsedArguments
{
    private readonly Dictionary<string, string?> _options;

    public ParsedArguments(IReadOnlyList<string> positionals, Dictionary<string, string?> options)
    {
        Positionals = positionals;
        _options = options;
    }

    public IReadOnlyList<string> Positionals { get; }

    public bool Has(string option) => _options.ContainsKey(option);

    public string? Get(string option) => _options.TryGetValue(option, out string? value) ? value : null;

    /// <summary>The value of a comma-separated list option ("SET,FSD"), or null when absent.</summary>
    public IReadOnlyList<string>? GetList(string option) =>
        Get(option) is { } value
            ? value.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : null;

    /// <summary>Throws a usage error unless the command received exactly <paramref name="count"/> positionals.</summary>
    public void ExpectPositionals(int count, string usage)
    {
        if (Positionals.Count < count)
        {
            throw new UsageException($"Missing argument. Usage: {usage}");
        }

        if (Positionals.Count > count)
        {
            throw new UsageException($"Unexpected argument '{Positionals[count]}'. Usage: {usage}");
        }
    }
}

/// <summary>
/// A deliberately small parser: GNU-style "--name value" and "--name=value" options, flags without a value, and
/// positional arguments. Each command declares the options it accepts, so a typo is a usage error rather than a
/// silently ignored setting.
/// </summary>
internal static class ArgumentParser
{
    public static ParsedArguments Parse(IReadOnlyList<string> args, string command, IReadOnlyCollection<string> flags,
                                        IReadOnlyCollection<string> valued)
    {
        var positionals = new List<string>();
        var options = new Dictionary<string, string?>(StringComparer.Ordinal);
        bool onlyPositionals = false;

        for (int i = 0; i < args.Count; i++)
        {
            string arg = args[i];
            if (onlyPositionals || !arg.StartsWith('-') || arg == "-")
            {
                positionals.Add(arg);
                continue;
            }

            if (arg == "--")
            {
                onlyPositionals = true;
                continue;
            }

            string name = arg;
            string? inlineValue = null;
            int equals = arg.IndexOf('=');
            if (arg.StartsWith("--", StringComparison.Ordinal) && equals > 2)
            {
                name = arg[..equals];
                inlineValue = arg[(equals + 1)..];
            }

            if (flags.Contains(name))
            {
                if (inlineValue is not null)
                {
                    throw new UsageException($"The option {name} of '{command}' takes no value.");
                }

                options[name] = null;
            }
            else if (valued.Contains(name))
            {
                string? value = inlineValue;
                if (value is null)
                {
                    if (i + 1 >= args.Count)
                    {
                        throw new UsageException($"The option {name} of '{command}' needs a value.");
                    }

                    value = args[++i];
                }

                options[name] = value;
            }
            else
            {
                throw new UsageException($"Unknown option '{name}' for '{command}'.");
            }
        }

        return new ParsedArguments(positionals, options);
    }
}
