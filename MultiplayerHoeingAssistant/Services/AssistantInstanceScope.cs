using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>Ordinary data retains the original session singleton. Explicit isolated data has its own singleton.</summary>
internal static class AssistantInstanceScope
{
    internal static string CurrentSuffix { get; } = ResolveSuffix(
        System.Diagnostics.Process.GetCurrentProcess().SessionId,
        AssistantDataDirectory.IsExplicit ? AssistantDataDirectory.Root : null,
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusBGI"));

    internal static string ResolveSuffix(int sessionId, string? explicitRoot, string defaultRoot)
    {
        var session = "Session" + sessionId;
        if (string.IsNullOrEmpty(explicitRoot)) return session;
        var root = Canonicalize(explicitRoot);
        if (root.Equals(Canonicalize(defaultRoot), StringComparison.Ordinal)) return session;
        var digest = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(root)))[..24];
        return session + "_Data" + digest;
    }

    private static string Canonicalize(string root)
        => Path.TrimEndingDirectorySeparator(Path.GetFullPath(root)).ToUpperInvariant();
}
