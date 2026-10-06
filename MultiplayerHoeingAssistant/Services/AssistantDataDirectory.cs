using System.IO;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>One process-lifetime root for all user settings, flows, runs and caches.</summary>
public static class AssistantDataDirectory
{
    public const string EnvironmentVariable = "NEXUSBGI_DATA_ROOT";
    private static readonly string? ExplicitRoot = Environment.GetEnvironmentVariable(EnvironmentVariable);

    // Capture once: changing the environment later must not split a running
    // assistant between two stores. An invalid explicit root never falls back.
    public static string Root { get; } = ResolveRoot(ExplicitRoot,
        string.IsNullOrEmpty(ExplicitRoot)
            ? Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData) : "");

    public static bool IsExplicit => !string.IsNullOrEmpty(ExplicitRoot);

    internal static string ResolveRoot(string? explicitRoot, string applicationData)
    {
        if (string.IsNullOrEmpty(explicitRoot))
            return Path.Combine(applicationData, "NexusBGI");
        if (!Path.IsPathFullyQualified(explicitRoot))
            throw new ArgumentException("NEXUSBGI_DATA_ROOT must be an absolute directory path.", nameof(explicitRoot));
        return Path.GetFullPath(explicitRoot);
    }
}
