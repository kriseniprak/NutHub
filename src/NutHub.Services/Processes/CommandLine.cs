using System.Text;

namespace NutHub.Services.Processes;

/// <summary>
/// Splits a command line typed in the web panel into separate arguments, so programs are started with
/// <see cref="System.Diagnostics.ProcessStartInfo.ArgumentList"/> and never through a shell: a value substituted into
/// an argument (a UPS name, an event message) can then never inject another command.
/// </summary>
/// <remarks>
/// The rules are the same on every operating system, so a configuration means the same thing everywhere:
/// white space separates arguments; double quotes group text containing spaces and are removed; <c>\"</c> is a
/// literal double quote; every other backslash is literal (Windows paths need no doubling); <c>""</c> is an empty
/// argument. Single quotes have no special meaning.
/// </remarks>
internal static class CommandLine
{
    public static List<string> Split(string? commandLine)
    {
        var result = new List<string>();
        if (string.IsNullOrWhiteSpace(commandLine))
        {
            return result;
        }

        var current = new StringBuilder();
        bool inQuotes = false;
        bool hasToken = false;
        for (int i = 0; i < commandLine.Length; i++)
        {
            char c = commandLine[i];
            if (c == '\\' && i + 1 < commandLine.Length && commandLine[i + 1] == '"')
            {
                current.Append('"');
                hasToken = true;
                i++;
            }
            else if (c == '"')
            {
                inQuotes = !inQuotes;
                hasToken = true;
            }
            else if (!inQuotes && char.IsWhiteSpace(c))
            {
                if (hasToken)
                {
                    result.Add(current.ToString());
                    current.Clear();
                    hasToken = false;
                }
            }
            else
            {
                current.Append(c);
                hasToken = true;
            }
        }

        if (hasToken)
        {
            result.Add(current.ToString());
        }

        return result;
    }

    /// <summary>Formats a program and its arguments for logs and the web panel (quotes what needs quoting).</summary>
    public static string Format(string fileName, IEnumerable<string> arguments) =>
        string.Join(' ', new[] { fileName }.Concat(arguments).Select(Quote));

    private static string Quote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        return "\"" + argument.Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }
}
