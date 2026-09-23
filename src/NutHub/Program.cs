using NutHub.Cli;

// Every command goes through the command line dispatcher, including the server itself ("nuthub" / "nuthub run"),
// which the Windows service and the systemd unit start.
return await CommandLineApplication.RunAsync(args).ConfigureAwait(false);
