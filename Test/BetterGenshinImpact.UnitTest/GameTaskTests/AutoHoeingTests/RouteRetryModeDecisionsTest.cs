#nullable enable

using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// RouteRetryModeDecisions 的保留行为测试。
/// **Validates: Requirements 3.1, 3.3, 3.5**
/// </summary>
public class RouteRetryModeDecisionsTest
{
    // PBT-1: 空值和空白输入都不产生关键词。
    [Fact]
    public void ParseKeywords_NullOrWhitespace_ReturnsEmpty()
    {
        Assert.Empty(RouteRetryModeDecisions.ParseKeywords(null));
        Assert.Empty(RouteRetryModeDecisions.ParseKeywords(string.Empty));
        Assert.Empty(RouteRetryModeDecisions.ParseKeywords("  ， , \t"));
    }

    // PBT-2: 全/半角分隔、Trim、空项和大小写重复均按约定处理。
    [Fact]
    public void ParseKeywords_SupportsSeparatorsTrimmingAndOrdinalIgnoreCaseDeduplication()
    {
        var actual = RouteRetryModeDecisions.ParseKeywords("  Alpha， beta, ,ALPHA，\tbeta  ,  Gamma ");

        Assert.Equal(3, actual.Count);
        Assert.Contains(actual, keyword => string.Equals(keyword, "alpha", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(actual, keyword => string.Equals(keyword, "beta", System.StringComparison.OrdinalIgnoreCase));
        Assert.Contains(actual, keyword => string.Equals(keyword, "gamma", System.StringComparison.OrdinalIgnoreCase));
    }

    // PBT-3: 空关键词集合恒不命中。
    [Property(MaxTest = 200)]
    public bool IsRetryRoute_WithEmptyKeywords_IsAlwaysFalse(NonEmptyString fileName)
    {
        return !RouteRetryModeDecisions.IsRetryRoute(
            fileName.Get,
            new HashSet<string>(System.StringComparer.OrdinalIgnoreCase));
    }

    [Fact]
    public void IsRetryRoute_NullOrEmptyFileName_IsFalse()
    {
        var keywords = new HashSet<string> { "route" };

        Assert.False(RouteRetryModeDecisions.IsRetryRoute(null, keywords));
        Assert.False(RouteRetryModeDecisions.IsRetryRoute(string.Empty, keywords));
    }

    // PBT-4: 命中任一关键词，且匹配不区分大小写。
    [Theory]
    [InlineData("DailyRoute-01", "route")]
    [InlineData("dailyroute-01", "ROUTE")]
    [InlineData("WorldBoss", "boss")]
    public void IsRetryRoute_WhenAnyKeywordIsContained_IsCaseInsensitive(string fileName, string keyword)
    {
        Assert.True(RouteRetryModeDecisions.IsRetryRoute(fileName, new[] { "unmatched", keyword }));
    }

    // PBT-5: 全角和半角分隔解析出的集合等价。
    [Fact]
    public void ParseKeywords_FullWidthAndHalfWidthSeparators_AreEquivalent()
    {
        var halfWidth = RouteRetryModeDecisions.ParseKeywords("RouteA, RouteB, routeC");
        var fullWidth = RouteRetryModeDecisions.ParseKeywords("RouteA， RouteB， routeC");

        Assert.True(halfWidth.ToHashSet(System.StringComparer.OrdinalIgnoreCase)
            .SetEquals(fullWidth));
    }

    // PBT-6: 已重跑线路不应再次被标记。
    [Property(MaxTest = 200)]
    public bool RouteRerunDecisions_WhenRouteIsDone_IsAlwaysFalse(NonNegativeInt routeIndex)
    {
        var index = routeIndex.Get;
        var doneSet = new HashSet<int> { index };

        return !RouteRerunDecisions.ShouldMarkRerun(index, new HashSet<int>(), doneSet);
    }

    [Fact]
    public void RouteRerunDecisions_WhenRouteIsDone_ReturnsFalse()
    {
        Assert.False(RouteRerunDecisions.ShouldMarkRerun(
            7,
            new HashSet<int>(),
            new HashSet<int> { 7 }));
    }
}
