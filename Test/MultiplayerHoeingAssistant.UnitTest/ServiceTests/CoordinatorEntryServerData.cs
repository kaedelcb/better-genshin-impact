using System.Diagnostics;
using System.IO;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// Give out-of-process entry tests their own valid, published route catalog and exact BGI version.
/// This is fixture setup, not a route-publication test; the production server still validates the ZIP,
/// manifest and version at startup/admission. Never read or change the running server's catalog.
/// </summary>
internal static class CoordinatorEntryServerData
{
    internal const string BgiVersion = "0.57.0+isolated-entry";

    internal static void Configure(ProcessStartInfo start, string caseName)
    {
        Assert.True(AssistantDataDirectory.IsExplicit);
        var root = Path.Combine(AssistantDataDirectory.Root, "coordinator-" + caseName + "-" + Guid.NewGuid().ToString("N"));
        var routes = Path.Combine(root, "route-packages");
        Directory.CreateDirectory(routes);
        var id = Guid.NewGuid().ToString("N");
        const string routePath = "builtin/isolated/001.json";
        var content = Encoding.UTF8.GetBytes("""{"info":{"name":"隔离协议线路，不执行游戏"},"positions":[{"x":1,"y":2,"type":"path","move_mode":"walk"}]}""");
        var zipPath = Path.Combine(routes, id + ".zip");
        using (var file = new FileStream(zipPath, FileMode.CreateNew, FileAccess.Write, FileShare.None))
        using (var archive = new ZipArchive(file, ZipArchiveMode.Create))
        using (var entry = archive.CreateEntry(routePath).Open())
            entry.Write(content);
        var zip = File.ReadAllBytes(zipPath);
        var package = new
        {
            schemaVersion = 1, id, sha256 = Digest(zip), sizeBytes = zip.Length,
            createdAtUtc = DateTimeOffset.UtcNow, description = "entry-test-only", requiredClientVersion = BgiVersion,
            fileCount = 1, routeCount = 1,
            files = new[] { new { path = routePath, sizeBytes = content.Length, sha256 = Digest(content) } },
            downloadUrl = "/api/hoeing-routes/packages/" + id + "/download", state = "published",
        };
        File.WriteAllText(Path.Combine(routes, "catalog.json"), JsonSerializer.Serialize(new
        {
            schemaVersion = 1, revision = 1, currentId = id, packages = new[] { package }, audit = Array.Empty<object>(),
        }));
        start.Environment["ROOM_LOG_ROOT"] = Path.Combine(root, "room-logs");
        start.Environment["ROUTE_PACKAGE_ROOT"] = routes;
        start.Environment["HOEING_REQUIRED_CLIENT_VERSION"] = BgiVersion;
        start.Environment["ADMIN_MONITOR_KEY_ROOT"] = Path.Combine(root, "server-keys");
        start.Environment["ASPNETCORE_ENVIRONMENT"] = "Development";
        start.Environment["Logging__EventLog__LogLevel__Default"] = "None";
    }

    private static string Digest(byte[] content) => Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant();
}
