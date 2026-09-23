using System.ComponentModel;
using System.Diagnostics;
using System.Text;

namespace NutHub.ServiceSetup;

/// <summary>The exit code and the combined standard output and error of a finished program.</summary>
internal sealed record ProcessResult(int ExitCode, string Output);

/// <summary>Runs system tools (sc.exe, systemctl, useradd...) and collects what they print.</summary>
internal static class ProcessRunner
{
    private static readonly TimeSpan DefaultTimeout = TimeSpan.FromMinutes(2);

    /// <summary>
    /// Runs a program to completion. A program that cannot be started gives exit code 127 (like a shell), so callers
    /// handle "not installed" like any other failure.
    /// </summary>
    public static ProcessResult Run(string fileName, IReadOnlyList<string> arguments, TimeSpan? timeout = null)
    {
        var start = new ProcessStartInfo(fileName)
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            RedirectStandardInput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        foreach (string argument in arguments)
        {
            start.ArgumentList.Add(argument);
        }

        var output = new StringBuilder();
        using var process = new Process { StartInfo = start };
        process.OutputDataReceived += (_, e) => Append(output, e.Data);
        process.ErrorDataReceived += (_, e) => Append(output, e.Data);
        try
        {
            process.Start();
        }
        catch (Win32Exception ex)
        {
            return new ProcessResult(127, $"{fileName}: {ex.Message}");
        }

        process.StandardInput.Close();
        process.BeginOutputReadLine();
        process.BeginErrorReadLine();
        if (!process.WaitForExit(timeout ?? DefaultTimeout))
        {
            try
            {
                process.Kill(entireProcessTree: true);
            }
            catch (InvalidOperationException)
            {
                // Exited meanwhile.
            }

            return new ProcessResult(124, $"{fileName} did not finish in time.");
        }

        process.WaitForExit(); // flushes the asynchronous output readers
        lock (output)
        {
            return new ProcessResult(process.ExitCode, output.ToString());
        }
    }

    private static void Append(StringBuilder output, string? line)
    {
        if (line is null)
        {
            return;
        }

        lock (output)
        {
            output.AppendLine(line);
        }
    }
}
