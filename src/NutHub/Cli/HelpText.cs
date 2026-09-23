namespace NutHub.Cli;

/// <summary>The text of "nuthub help".</summary>
internal static class HelpText
{
    public const string Usage =
        """
        NutHub - UPS server compatible with Network UPS Tools, with a web panel.

        Usage: nuthub [command] [options]

        Commands:
          run                    Run the server in the foreground; this is also what the Windows service and the
                                 systemd unit run. The default command.
          service <action>       Manage the Windows service / systemd unit (needs administrator or root rights):
                                   install     register it (start at boot, restart on failure) and start it
                                   uninstall   stop and remove it (configuration and data are kept)
                                   start | stop | status
          passwd <user>          Set the password of a web panel account (also while NutHub runs).
          nut-user <action>      Manage the accounts NUT clients (upsmon...) log in with:
                                   list
                                   add <name>            [--monitor primary|secondary|none] (default: secondary)
                                                         [--actions SET,FSD] [--instcmds ALL|cmd,...] [--ups a,b]
                                   set-password <name>
                                   remove <name>
          devices                List the UPSes the drivers find on this machine (USB, serial ports).
          check-config           Validate the configuration file; exit code 0 when it is valid.
          healthcheck            Exit code 0 when the server running on this machine answers (for containers).
          version                Print the version.
          help                   Print this help.

        Options:
          --data-dir DIR         Data directory (database, logs, keys). Default: %ProgramData%\NutHub on Windows,
                                 /var/lib/nuthub as root on Linux, ~/.local/share/nuthub otherwise.
                                 Environment variable: NUTHUB_DATA_DIR.
          --config FILE          Configuration file. Default: nuthub.json in the data directory on Windows,
                                 /etc/nuthub/nuthub.json as root on Linux. Environment variable: NUTHUB_CONFIG.
          --password P           passwd, nut-user: the new password (at least 8 characters). Without it the
                                 password is read from the console (typed twice) or from standard input.
          --generate             passwd, nut-user: generate a random password and print it.
          --must-change          passwd: ask for a new password at the next sign-in.
          --timeout SECONDS      devices: how long each driver may search (default 20).
          --no-start             service install: register the service without starting it.
          --dry-run              service: print the commands and files instead of applying them.
          --platform windows|linux
                                 service --dry-run: preview the plan for the other operating system.

        Logging: rolling files in <data-dir>/logs (14 days). Levels follow the standard "Logging" configuration,
        e.g. the environment variable Logging__LogLevel__Default=Debug, or appsettings.json next to the executable.

        Exit codes: 0 success, 1 error, 2 invalid command line ("service status": 0 running, 3 stopped).
        """;
}
