using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NutHub.Services.Processes;

/// <summary>The outcome of running an external program.</summary>
/// <param name="ExitCode">The exit code; null when the program could not be started or was killed.</param>
/// <param name="TimedOut">The program was killed because it ran longer than allowed.</param>
/// <param name="StandardError">The first lines the program wrote to its standard error.</param>
/// <param name="StartError">Why the program could not be started (not found, access denied...), or null.</param>
internal sealed record ProcessOutcome(int? ExitCode, bool TimedOut, string StandardError, string? StartError)
{
    public bool Succeeded => ExitCode == 0 && !TimedOut && StartError is null;

    /// <summary>A one-line description for logs and the delivery list.</summary>
    public string Describe(TimeSpan timeout)
    {
        if (StartError is not null)
        {
            return StartError;
        }

        string detail = StandardError.Length > 0 ? ": " + StandardError.ReplaceLineEndings(" | ") : "";
        if (TimedOut)
        {
            return $"Killed after {timeout.TotalSeconds:0} s without finishing{detail}";
        }

        return ExitCode == 0 ? "Exit code 0" + detail : $"Exit code {ExitCode}{detail}";
    }
}

/// <summary>
/// Runs a program without a shell, with a time limit, and kills the whole process tree when the limit is reached
/// (a hook script may start children that would otherwise keep running forever).
/// </summary>
internal static class ProcessRunner
{
    private const int MaxErrorLines = 5;
    private const int MaxErrorChars = 500;

    public static async Task<ProcessOutcome> RunAsync(string fileName, IReadOnlyList<string> arguments,
                                                      IReadOnlyDictionary<string, string>? environment,
                                                      TimeSpan timeout, CancellationToken cancellationToken)
    {
        var startInfo = new ProcessStartInfo(fileName)
        {
            UseShellExecute = false,
            CreateNoWindow = true,
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            RedirectStandardInput = true,
            StandardErrorEncoding = Encoding.UTF8,
            StandardOutputEncoding = Encoding.UTF8,
        };
        if (OperatingSystem.IsWindows() && IsBatchFile(fileName))
        {
            // Windows runs a batch file through cmd.exe, which parses the command line again: & | < > and %VAR% in
            // an argument (an event message) would become commands and variables. The command line is built here
            // with the escaping cmd.exe understands instead.
            if (BatchCommandLine(fileName, arguments) is not { } commandLine)
            {
                return new ProcessOutcome(null, false, "", $"Could not start '{fileName}': not a valid file name.");
            }

            startInfo.FileName = Path.Combine(Environment.SystemDirectory, "cmd.exe");
            startInfo.Arguments = commandLine;
        }
        else
        {
            foreach (string argument in arguments)
            {
                startInfo.ArgumentList.Add(argument);
            }
        }

        if (Path.IsPathRooted(fileName) && Path.GetDirectoryName(fileName) is { Length: > 0 } directory &&
            Directory.Exists(directory))
        {
            startInfo.WorkingDirectory = directory;
        }

        if (environment is not null)
        {
            foreach ((string key, string value) in environment)
            {
                startInfo.Environment[key] = value;
            }
        }

        using var process = new Process { StartInfo = startInfo };
        try
        {
            if (!process.Start())
            {
                return new ProcessOutcome(null, false, "", $"Could not start '{fileName}'.");
            }
        }
        catch (Exception ex) when (ex is Win32Exception or InvalidOperationException or PlatformNotSupportedException)
        {
            return new ProcessOutcome(null, false, "", $"Could not start '{fileName}': {ex.Message}");
        }

        // Nothing is ever typed into a hook: closing stdin makes a script that asks a question fail instead of hang.
        try
        {
            process.StandardInput.Close();
        }
        catch (IOException)
        {
        }

        Task<string> errorTask = ReadHeadAsync(process.StandardError);
        Task drainTask = DrainAsync(process.StandardOutput);

        using var timeoutCts = new CancellationTokenSource(timeout);
        using var linked = CancellationTokenSource.CreateLinkedTokenSource(timeoutCts.Token, cancellationToken);
        bool timedOut = false;
        try
        {
            await process.WaitForExitAsync(linked.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            timedOut = timeoutCts.IsCancellationRequested;
            Kill(process);
            if (!timedOut)
            {
                await WaitBrieflyAsync(process).ConfigureAwait(false);
                throw;
            }
        }

        if (timedOut)
        {
            await WaitBrieflyAsync(process).ConfigureAwait(false);
        }

        // The pipes close when the process (and every child holding them) is gone; do not wait forever for a
        // grandchild that inherited them.
        string stderr = await WithTimeout(errorTask, "").ConfigureAwait(false);
        await WithTimeout(drainTask.ContinueWith(_ => "", TaskScheduler.Default), "").ConfigureAwait(false);
        int? exitCode = timedOut || !process.HasExited ? null : process.ExitCode;
        return new ProcessOutcome(exitCode, timedOut, stderr, null);
    }

    /// <summary>Whether Windows would start this program through cmd.exe (trailing dots and spaces are ignored).</summary>
    internal static bool IsBatchFile(string fileName)
    {
        string name = fileName.TrimEnd('.', ' ');
        return name.EndsWith(".bat", StringComparison.OrdinalIgnoreCase) ||
               name.EndsWith(".cmd", StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// The arguments of cmd.exe that run <paramref name="script"/> with <paramref name="arguments"/>, each one
    /// arriving as one argument with its text intact. The rules are those of Rust's std::process (the fix of
    /// CVE-2024-24576): quotes around anything that is not plainly safe, <c>"</c> doubled, <c>%</c> followed by an
    /// empty substring expansion so no variable is ever expanded, delayed expansion (<c>!</c>) off. Line breaks would
    /// end the command and become spaces. Null when the script name cannot be a file name.
    /// </summary>
    internal static string? BatchCommandLine(string script, IReadOnlyList<string> arguments)
    {
        if (script.Contains('"', StringComparison.Ordinal) || script.EndsWith('\\') || script.Contains('\0', StringComparison.Ordinal))
        {
            return null;
        }

        // The outer quotes are removed by cmd.exe /c; everything inside is run as it is.
        var sb = new StringBuilder("/e:ON /v:OFF /d /c \"\"").Append(script).Append('"');
        foreach (string argument in arguments)
        {
            sb.Append(' ');
            AppendBatchArgument(sb, argument);
        }

        return sb.Append('"').ToString();
    }

    private static void AppendBatchArgument(StringBuilder sb, string argument)
    {
        const string SafeSymbols = "#$*+-./:?@\\_";
        bool quote = argument.Length == 0 || argument[^1] == '\\' ||
                     argument.Any(c => char.IsControl(c) || (char.IsAscii(c) && !char.IsAsciiLetterOrDigit(c) && !SafeSymbols.Contains(c)));
        if (quote)
        {
            sb.Append('"');
        }

        int backslashes = 0;
        foreach (char original in argument)
        {
            char c = original is '\r' or '\n' or '\0' ? ' ' : original;
            if (c == '\\')
            {
                backslashes++;
            }
            else
            {
                if (c == '"')
                {
                    sb.Append('\\', backslashes).Append('"');
                }
                else if (c == '%')
                {
                    sb.Append("%%cd:~,");
                }

                backslashes = 0;
            }

            sb.Append(c);
        }

        if (quote)
        {
            sb.Append('\\', backslashes).Append('"');
        }
    }

    private static void Kill(Process process)
    {
        try
        {
            process.Kill(entireProcessTree: true);
        }
        catch (Exception ex) when (ex is InvalidOperationException or Win32Exception or NotSupportedException)
        {
            // Already gone.
        }
    }

    private static async Task WaitBrieflyAsync(Process process)
    {
        using var cts = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try
        {
            await process.WaitForExitAsync(cts.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
        }
    }

    private static async Task<string> WithTimeout(Task<string> task, string fallback)
    {
        Task finished = await Task.WhenAny(task, Task.Delay(TimeSpan.FromSeconds(2))).ConfigureAwait(false);
        return finished == task && task.IsCompletedSuccessfully ? task.Result : fallback;
    }

    private static async Task<string> ReadHeadAsync(StreamReader reader)
    {
        var lines = new List<string>();
        int chars = 0;
        try
        {
            while (await reader.ReadLineAsync().ConfigureAwait(false) is { } line)
            {
                line = line.Trim();
                if (line.Length == 0 || lines.Count >= MaxErrorLines || chars >= MaxErrorChars)
                {
                    continue; // keep reading so the program never blocks on a full pipe
                }

                if (chars + line.Length > MaxErrorChars)
                {
                    line = line[..(MaxErrorChars - chars)] + "...";
                }

                lines.Add(line);
                chars += line.Length;
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }

        return string.Join(Environment.NewLine, lines);
    }

    private static async Task DrainAsync(StreamReader reader)
    {
        char[] buffer = new char[4096];
        try
        {
            while (await reader.ReadAsync(buffer).ConfigureAwait(false) > 0)
            {
            }
        }
        catch (Exception ex) when (ex is IOException or ObjectDisposedException)
        {
        }
    }
}
