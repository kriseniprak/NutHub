using System.Text;

using NutHub.Cli;

namespace NutHub.ServiceSetup;

/// <summary>The operating system a service setup is for (the current one, or another one in a --dry-run preview).</summary>
internal enum TargetPlatform
{
    Windows,
    Linux,
}

/// <summary>
/// One step of installing or controlling the service. Plans are built first and then either executed or printed
/// (--dry-run), so what a dry run shows is exactly what would run.
/// </summary>
internal abstract record SetupStep(string Description);

/// <summary>Runs a system tool.</summary>
/// <param name="IgnoreFailure">A failure is expected in some states (stopping a stopped service).</param>
/// <param name="ShowOutput">Print what the tool prints (status commands).</param>
internal sealed record CommandStep(string Description, string FileName, IReadOnlyList<string> Arguments,
                                   bool IgnoreFailure = false, bool ShowOutput = false) : SetupStep(Description);

/// <summary>Writes a text file (world-readable, like the other files of /etc), creating its directory.</summary>
internal sealed record WriteFileStep(string Description, string Path, string Content) : SetupStep(Description);

/// <summary>Deletes a file if it exists.</summary>
internal sealed record DeleteFileStep(string Description, string Path) : SetupStep(Description);

/// <summary>An in-process action (Event Log source, waiting for the service state).</summary>
internal sealed record ActionStep(string Description, string Preview, Action Execute) : SetupStep(Description);

/// <summary>A line of information for the user, printed in both modes.</summary>
internal sealed record NoteStep(string Description) : SetupStep(Description);

/// <summary>Executes a plan, or prints it for --dry-run.</summary>
internal sealed class StepRunner(bool dryRun, TargetPlatform platform, TextWriter output)
{
    /// <summary>
    /// Runs the steps in order and stops at the first failure (a <see cref="CommandException"/>).
    /// Returns the exit code of the last step that shows its output, or 0.
    /// </summary>
    public int Run(IEnumerable<SetupStep> steps)
    {
        int exitCode = 0;
        foreach (SetupStep step in steps)
        {
            if (step is NoteStep note)
            {
                output.WriteLine(note.Description);
                continue;
            }

            if (dryRun)
            {
                Preview(step);
                continue;
            }

            switch (step)
            {
                case CommandStep command:
                    exitCode = Execute(command);
                    break;
                case WriteFileStep write:
                    output.WriteLine($"==> {write.Description}");
                    WriteFile(write);
                    break;
                case DeleteFileStep delete:
                    if (File.Exists(delete.Path))
                    {
                        output.WriteLine($"==> {delete.Description}");
                        File.Delete(delete.Path);
                    }

                    break;
                case ActionStep action:
                    output.WriteLine($"==> {action.Description}");
                    action.Execute();
                    break;
            }
        }

        return exitCode;
    }

    private int Execute(CommandStep command)
    {
        if (!command.ShowOutput)
        {
            output.WriteLine($"==> {command.Description}");
        }

        ProcessResult result = ProcessRunner.Run(command.FileName, command.Arguments);
        if (command.ShowOutput)
        {
            output.Write(result.Output);
            return result.ExitCode;
        }

        if (result.ExitCode != 0 && !command.IgnoreFailure)
        {
            string details = result.Output.Trim();
            throw new CommandException($"{Format(command)} failed (exit code {result.ExitCode})" +
                                       (details.Length > 0 ? ":" + Environment.NewLine + details : "."));
        }

        return result.ExitCode;
    }

    private static void WriteFile(WriteFileStep write)
    {
        string? directory = Path.GetDirectoryName(write.Path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        string temporary = write.Path + ".tmp";
        File.WriteAllText(temporary, write.Content, new UTF8Encoding(encoderShouldEmitUTF8Identifier: false));
        if (!OperatingSystem.IsWindows())
        {
            File.SetUnixFileMode(temporary, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.GroupRead |
                                            UnixFileMode.OtherRead);
        }

        File.Move(temporary, write.Path, overwrite: true);
    }

    private void Preview(SetupStep step)
    {
        output.WriteLine($"# {step.Description}");
        switch (step)
        {
            case CommandStep command:
                output.WriteLine(Format(command) + (command.IgnoreFailure ? "    (a failure is ignored)" : ""));
                break;
            case WriteFileStep write:
                output.WriteLine($"write {write.Path}:");
                foreach (string line in write.Content.TrimEnd('\n').Split('\n'))
                {
                    output.WriteLine("    " + line);
                }

                break;
            case DeleteFileStep delete:
                output.WriteLine(platform == TargetPlatform.Windows ? $"del \"{delete.Path}\"" : $"rm -f {ShellQuote(delete.Path)}");
                break;
            case ActionStep action:
                output.WriteLine(action.Preview);
                break;
        }
    }

    /// <summary>The command as it would be typed in cmd.exe or a POSIX shell.</summary>
    private string Format(CommandStep command)
    {
        string program = Path.IsPathRooted(command.FileName) && platform == TargetPlatform.Windows
            ? Path.GetFileName(command.FileName)
            : command.FileName;
        IEnumerable<string> arguments = platform == TargetPlatform.Windows
            ? command.Arguments.Select(WindowsQuote)
            : command.Arguments.Select(ShellQuote);
        return string.Join(' ', arguments.Prepend(program));
    }

    /// <summary>Quoting of the Microsoft C runtime (what Process.ArgumentList produces on Windows).</summary>
    internal static string WindowsQuote(string argument)
    {
        if (argument.Length > 0 && !argument.Any(c => char.IsWhiteSpace(c) || c == '"'))
        {
            return argument;
        }

        var builder = new StringBuilder("\"");
        int backslashes = 0;
        foreach (char c in argument)
        {
            if (c == '\\')
            {
                backslashes++;
                continue;
            }

            if (c == '"')
            {
                builder.Append('\\', backslashes * 2 + 1).Append('"');
            }
            else
            {
                builder.Append('\\', backslashes).Append(c);
            }

            backslashes = 0;
        }

        builder.Append('\\', backslashes * 2).Append('"');
        return builder.ToString();
    }

    internal static string ShellQuote(string argument) =>
        argument.Length > 0 && argument.All(c => char.IsAsciiLetterOrDigit(c) || "-_./=:,@%+".Contains(c))
            ? argument
            : "'" + argument.Replace("'", "'\\''", StringComparison.Ordinal) + "'";
}
