using BgiCoordinatorServer.Gateway;
using Semver;

namespace BgiCoordinatorServer.Services;

/// <summary>Production always injects this policy, including when deployment configuration is absent.</summary>
public sealed class RoutePackagePolicy(RoutePackageStore store, GatewaySessionTracker? sessions = null)
{
    public string RequiredClientVersion => store.RequiredClientVersion;

    /// <summary>未配置 HOEING_REQUIRED_CLIENT_VERSION 时不按客户端版本限制联机锄地，只要求已有发布的线路包。</summary>
    public bool VersionPinned => !string.IsNullOrWhiteSpace(RequiredClientVersion);

    public static bool IsExactVersion(string? version)
        => !string.IsNullOrWhiteSpace(version) && version.Length <= 256 && version == version.Trim()
            && SemVersion.TryParse(version, SemVersionStyles.Strict, out _) && !VersionCompatibilityDecisions.IsWildcard(version);

    public void RequireConfigured()
    {
        if (VersionPinned && !IsExactVersion(RequiredClientVersion)) throw new InvalidOperationException("hoeing_required_version_unconfigured");
        if (!store.HasPublishedPackageForVersion(RequiredClientVersion))
            throw new InvalidOperationException("hoeing_route_package_uninitialized");
    }

    public void RequireVersion(string? version)
    {
        RequireConfigured();
        if (!VersionPinned) return;
        if (!IsExactVersion(version) || !string.Equals(version, RequiredClientVersion, StringComparison.Ordinal))
            throw new InvalidOperationException("hoeing_required_version_mismatch");
    }

    public void RequireConnectionVersion(string connectionId, string fallback, bool requireHello = false)
    {
        if (!VersionPinned) { RequireConfigured(); return; }
        // v3 reconnect must re-announce the real version in its hello; never trust a previous plan's cached version.
        if (sessions is not null && sessions.TryGet(connectionId, out var hello)) RequireVersion(hello?.ClientVersion);
        else if (requireHello && sessions is not null) throw new InvalidOperationException("hoeing_required_version_mismatch");
        else RequireVersion(fallback);
    }

    public void RequirePackage(string? id)
    {
        RequireConfigured();
        if (!store.IsPublishedAvailable(id, RequiredClientVersion)) throw new InvalidOperationException("hoeing_route_package_unavailable");
    }
}
