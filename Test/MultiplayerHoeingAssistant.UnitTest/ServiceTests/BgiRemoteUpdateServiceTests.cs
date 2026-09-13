using System.IO;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// 服务器网络更新（"网络"来源）纯逻辑测试：清单解析（ParseManifest）+ 网络包合并（MergeNetworkPackage）。
/// 服务器约定：GET {server}/bgi-update/latest.json（服务端扫描目录自动生成）+ /bgi-update/{文件名}。
/// </summary>
public class BgiRemoteUpdateServiceTests
{
    // ===== 清单解析 =====

    [Fact]
    public void ParseManifest_FullFields_Parses()
    {
        var json = """
            {"fileName":"BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix16.7z","sizeBytes":489000000,"updatedAt":"2026-09-14T03:00:00Z"}
            """;
        var info = BgiRemoteUpdateService.ParseManifest(json, "http://srv:8080");
        Assert.NotNull(info);
        Assert.Equal("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix16.7z", info!.FileName);
        // url 按服务器根拼接（文件名做了 URL 转义）
        Assert.StartsWith("http://srv:8080/bgi-update/BetterGI_v0.64.2", info.Url);
        Assert.EndsWith("7z", info.Url);
        Assert.Equal(489000000, info.SizeBytes);
        Assert.NotNull(info.UpdatedAt);
        Assert.Equal("fix16", info.NameInfo.TailRest);
    }

    [Fact]
    public void ParseManifest_RelativeUrl_IsCombinedWithServerRoot()
    {
        var json = """{"fileName":"BetterGI_v0.64.2+lcb.22.7.7z","url":"/files/a.7z"}""";
        var info = BgiRemoteUpdateService.ParseManifest(json, "http://srv:8080/");
        Assert.Equal("http://srv:8080/files/a.7z", info!.Url);
    }

    [Fact]
    public void ParseManifest_AbsoluteUrl_Kept()
    {
        var json = """{"fileName":"BetterGI_v0.64.2+lcb.22.7.7z","url":"https://cos.example.com/a.7z"}""";
        var info = BgiRemoteUpdateService.ParseManifest(json, "http://srv:8080");
        Assert.Equal("https://cos.example.com/a.7z", info!.Url);
    }

    [Theory]
    [InlineData("{}")]                                                   // 缺 fileName
    [InlineData("""{"fileName":"random_garbage.7z"}""")]                 // 名字不合茶包规范 → 拒收（防误装非茶包版）
    [InlineData("""{"fileName":"BetterGI_v0.64.2.7z"}""")]               // 官方版无 lcb → 拒收
    [InlineData("not-json")]                                             // 坏 JSON
    public void ParseManifest_Invalid_Rejected(string json)
    {
        Assert.Null(BgiRemoteUpdateService.ParseManifest(json, "http://srv:8080"));
    }

    // ===== 网络包合并 =====

    [Fact]
    public void MergeNetworkPackage_AddsTaggedEntry_And_Sorts()
    {
        var info = BgiRemoteUpdateService.ParseManifest(
            """{"fileName":"BetterGI_v0.65.0+lcb.1.0.7z","sizeBytes":100}""", "http://srv:8080")!;
        var local = new BgiUpdatePackage("BetterGI_v0.64.2+lcb.22.7.7z",
            Path.Combine(Path.GetTempPath(), "BetterGI_v0.64.2+lcb.22.7.7z"), 1, DateTime.Now,
            ParseName("BetterGI_v0.64.2+lcb.22.7.7z"));

        var merged = BgiRemoteUpdateService.MergeNetworkPackage([local], info, @"D:\DOWN");
        Assert.Equal(2, merged.Count);
        Assert.Equal("BetterGI_v0.65.0+lcb.1.0.7z", merged[0].FileName);     // 更新版本排前
        Assert.Equal(BgiPackageSource.Network, merged[0].Source);
        Assert.Equal("网络", merged[0].SourceTagText);
        Assert.Equal(@"D:\DOWN\BetterGI_v0.65.0+lcb.1.0.7z", merged[0].FullPath); // 预期落盘路径
    }

    [Fact]
    public void MergeNetworkPackage_SamePathAlreadyListed_Skips()
    {
        var info = BgiRemoteUpdateService.ParseManifest(
            """{"fileName":"BetterGI_v0.65.0+lcb.1.0.7z"}""", "http://srv:8080")!;
        var existing = new BgiUpdatePackage("BetterGI_v0.65.0+lcb.1.0.7z",
            Path.Combine(@"D:\DOWN", "BetterGI_v0.65.0+lcb.1.0.7z"), 1, DateTime.Now,
            ParseName("BetterGI_v0.65.0+lcb.1.0.7z"), BgiPackageSource.PickedManually);

        var merged = BgiRemoteUpdateService.MergeNetworkPackage([existing], info, @"D:\DOWN");
        Assert.Single(merged);
        Assert.Equal(BgiPackageSource.PickedManually, merged[0].Source); // 已有条目优先，不重复加
    }

    [Fact]
    public void MergeNetworkPackage_NullInfo_ReturnsOriginal()
    {
        var local = new List<BgiUpdatePackage>();
        Assert.Same(local, BgiRemoteUpdateService.MergeNetworkPackage(local, null, @"D:\DOWN"));
    }

    private static BgiArchiveName ParseName(string fileName)
    {
        Assert.True(BgiUpdateDecisions.TryParse(fileName, out var info));
        return info!;
    }
}
