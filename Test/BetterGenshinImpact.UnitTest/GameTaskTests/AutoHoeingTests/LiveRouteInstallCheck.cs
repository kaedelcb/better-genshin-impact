using System.IO.Compression;
using System.Net.Http;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 手动运行的线上端到端验证：用默认官方地址下载线上当前线路包，安装到临时隔离目录。
/// 默认跳过，需要设置环境变量 BGI_LIVE_ROUTE_CHECK=1，避免普通测试依赖外网。
/// </summary>
public class LiveRouteInstallCheck
{
    [Fact]
    public async Task OfficialServiceInstallsTheCurrentPackageIntoAnIsolatedRoot()
    {
        if (Environment.GetEnvironmentVariable("BGI_LIVE_ROUTE_CHECK") != "1") return;
        var root = Path.Combine(Path.GetTempPath(), "bgi-live-route-" + Guid.NewGuid().ToString("N"));
        try
        {
            using var http = new HttpClient(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromMinutes(5) };
            var updater = new AutoHoeingRouteUpdater(http, root, "live-check-client");
            var id = await updater.EnsureAsync(AutoHoeingRouteUpdater.ResolveServerAddress(""), null, CancellationToken.None);
            Assert.Equal(32, id.Length);
            var builtin = Path.Combine(updater.CurrentDirectory, "builtin");
            Assert.True(Directory.Exists(builtin));
            var json = Directory.GetFiles(builtin, "*.json", SearchOption.AllDirectories).Length;
            Assert.True(json > 100, "installed route json count " + json);
            Assert.NotEmpty(Directory.GetFiles(builtin, "README.md", SearchOption.AllDirectories));
            Console.WriteLine($"LIVE_INSTALL package={id[..8]} json={json} root={root}");
        }
        finally { try { Directory.Delete(root, true); } catch (IOException) { } }
    }
}
