namespace MultiplayerHoeingAssistant.Services;

/// <summary>A launch-only choice; persisted schedules and explicit user actions remain unchanged.</summary>
internal readonly record struct StartupLaunchIntent(bool ManualStart)
{
    internal bool AllowsAutomaticStartup => !ManualStart;

    internal static StartupLaunchIntent FromArguments(IEnumerable<string> arguments)
        => new(arguments.Any(value => string.Equals(value, "--manual-start", StringComparison.OrdinalIgnoreCase)));
}
