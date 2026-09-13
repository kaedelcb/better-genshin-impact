using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// /bgi-update/latest.json 目录扫描决策测试（BgiUpdateCatalogDecisions，与助手侧 BgiUpdateDecisions 同规则镜像）。
/// 服务端职责只有两件事：只认合法茶包名（必须带 +lcb.，防误发非茶包版）、从中选版本最新的。
/// </summary>
public class BgiUpdateCatalogDecisionsTests
{
    [Theory]
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z", true)]
    [InlineData("BetterGI_v0.64.2+lcb.22.7.7z", true)]
    [InlineData("BetterGI_v0.64.2.7z", false)]                      // 官方版无 lcb → 不入清单
    [InlineData("random_garbage.7z", false)]
    [InlineData("BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.zip", false)]
    public void TryParse_TeabagNamesOnly(string fileName, bool expected)
    {
        Assert.Equal(expected, BgiUpdateCatalogDecisions.TryParse(fileName, out _));
    }

    [Fact]
    public void PickNewest_PicksNewestByVersion()
    {
        var newest = BgiUpdateCatalogDecisions.PickNewest(
        [
            "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z",
            "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix14.7z",   // 数字比较：fix14 最新
            "BetterGI_v0.64.2+lcb.22.9.7z",
            "BetterGI_v0.63.0+lcb.99.9.7z",                  // 大版本压制 lcb
            "random_garbage.7z",                             // 非法名忽略
            "BetterGI_v0.64.3+lcb.22.10.7z",                 // 大版本 0.64.3 最新
        ]);
        Assert.NotNull(newest);
        Assert.Equal("BetterGI_v0.64.3+lcb.22.10.7z", newest!.FileName);
    }

    [Fact]
    public void PickNewest_EmptyOrAllInvalid_ReturnsNull()
    {
        Assert.Null(BgiUpdateCatalogDecisions.PickNewest([]));
        Assert.Null(BgiUpdateCatalogDecisions.PickNewest(["garbage.7z", "BetterGI_v0.64.2.7z"]));
    }
}
