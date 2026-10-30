using System.Reflection;

namespace NexusBGI.Legal;

/// <summary>Compiled into the server and assistant; only public build and license information.</summary>
internal static class OpenSourceInfo
{
    internal const string RepositoryUrl = "https://github.com/kaedelcb/better-genshin-impact";
    private static readonly Assembly ProductAssembly = typeof(OpenSourceInfo).Assembly;

    internal static string Version => ProductAssembly.GetCustomAttribute<AssemblyInformationalVersionAttribute>()?
        .InformationalVersion ?? ProductAssembly.GetName().Version?.ToString() ?? "未提供";

    internal static string Revision => ProductAssembly.GetCustomAttributes<AssemblyMetadataAttribute>()
        .FirstOrDefault(a => a.Key == "SourceRevisionId")?.Value ?? "";

    internal static bool HasRevision => Revision.Length == 40 && Revision.All(Uri.IsHexDigit);
    internal static string SourceUrl => HasRevision ? RepositoryUrl + "/tree/" + Revision : RepositoryUrl;
    internal static string VersionDescription => $"版本 {Version} · 源码提交：{(HasRevision ? Revision[..12] : "构建时未提供")}";

    internal static object ForWeb(string component, IEnumerable<string> licenseFiles) => new
    {
        component, version = Version, revision = HasRevision ? Revision : null,
        sourceUrl = SourceUrl,
        licenseFiles = licenseFiles.OrderBy(n => n, StringComparer.Ordinal).Select(n => new
        {
            name = n, url = "legal/licenses/" + Uri.EscapeDataString(n)
        })
    };
}
