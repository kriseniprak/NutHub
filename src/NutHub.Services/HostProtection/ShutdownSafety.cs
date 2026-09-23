namespace NutHub.Services.HostProtection;

/// <summary>
/// Decides whether the real shutdown and the UPS power-off may happen at all, independently of the configured dry
/// run: never in a debug build (tests and development machines), never when <see cref="HostShutdown.DisableEnvironmentVariable"/>
/// is set.
/// </summary>
internal sealed class ShutdownSafety(Func<string?> disabledReason)
{
    /// <summary>Why the shutdown must not be executed, or null when it may.</summary>
    public string? DisabledReason => disabledReason();

    public static ShutdownSafety FromEnvironment() => new(EnvironmentReason);

    private static string? EnvironmentReason()
    {
#if DEBUG
        return "debug build";
#else
        string? value = Environment.GetEnvironmentVariable(HostShutdown.DisableEnvironmentVariable)?.Trim();
        return value is "1" || string.Equals(value, "true", StringComparison.OrdinalIgnoreCase)
            ? $"{HostShutdown.DisableEnvironmentVariable} is set"
            : null;
#endif
    }
}
