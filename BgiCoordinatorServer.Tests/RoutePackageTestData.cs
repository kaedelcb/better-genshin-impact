using System.IO.Compression;
using System.Text;
using BgiCoordinatorServer.Services;
using NexusBGI.RoutePackages;

namespace BgiCoordinatorServer.Tests;

/// <summary>Only regenerated fixture data, never a running service's data or BGI User.</summary>
internal sealed class RoutePackageTestData : IDisposable
{
    internal const string Version = "0.57.0+lcb.20261117";
    internal const string Route = """{"info":{"name":"正常线路"},"positions":[{"x":1.25,"y":-2,"type":"path","move_mode":"walk","action":null,"sync_point_id":"fight_1","point_ext_params":{"auto_fight":{"reward_type":"mora","mora_values":[200,600]}}}]}""";
    internal string Root { get; } = Path.Combine(Environment.GetEnvironmentVariable("BGI_ROUTE_TEST_ROOT") ?? Path.GetTempPath(), "bgi-route-tests-" + Guid.NewGuid().ToString("N"));
    internal string StoreRoot => Path.Combine(Root, "store");
    internal RoutePackageTestData() => Directory.CreateDirectory(Root);
    internal string Zip(params (string Path, string Content)[] entries)
    {
        var path = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".zip");
        using var file = File.Create(path);
        using var zip = new ZipArchive(file, ZipArchiveMode.Create, false, Encoding.UTF8);
        foreach (var item in entries)
        {
            var entry = zip.CreateEntry(item.Path, CompressionLevel.NoCompression);
            using var writer = new StreamWriter(entry.Open(), new UTF8Encoding(false));
            writer.Write(item.Content);
        }
        return path;
    }
    internal string ValidZip(string? content = null) => Zip(("builtin/中文线路/001.json", content ?? Route));
    internal async Task<RoutePackageDescriptor> Upload(RoutePackageStore store, string? path = null)
    {
        await using var file = File.OpenRead(path ?? ValidZip());
        return await store.UploadAsync(file, "测试草稿", "test");
    }
    public void Dispose()
    {
        if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true);
    }
}
