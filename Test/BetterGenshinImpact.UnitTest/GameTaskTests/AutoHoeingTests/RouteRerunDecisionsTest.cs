#nullable enable

using System.Collections.Generic;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Bug Condition Exploration Test - Property 1: 轮末统一重跑只标记一次
///
/// **Validates: Requirements 2.1, 2.4, 2.6**
///
/// 目标：确认「需重跑线路」在**未标记且未重跑**时 SHALL 被标记（ShouldMarkRerun 返回 true）；
/// 已标记或已重跑则返回 false（"只重跑一次"）。
/// </summary>
public class RouteRerunDecisionsTest
{
    // ========== Property 1: 未标记未重跑 → true ==========

    [Property(MaxTest = 200)]
    public bool ShouldMarkRerun_WhenNotMarkedAndNotDone_ReturnsTrue(
        NonNegativeInt routeIndex,
        List<NonNegativeInt> arbitraryMarkSet,
        List<NonNegativeInt> arbitraryDoneSet)
    {
        int index = routeIndex.Get;
        var markSet = new HashSet<int>(arbitraryMarkSet.Select(x => x.Get));
        var doneSet = new HashSet<int>(arbitraryDoneSet.Select(x => x.Get));

        markSet.Remove(index);
        doneSet.Remove(index);

        return RouteRerunDecisions.ShouldMarkRerun(index, markSet, doneSet);
    }

    // ========== Property 2: 已标记 → false ==========

    [Property(MaxTest = 200)]
    public bool ShouldMarkRerun_WhenAlreadyMarked_ReturnsFalse(
        NonNegativeInt routeIndex,
        List<NonNegativeInt> arbitraryDoneSet)
    {
        int index = routeIndex.Get;
        var markSet = new HashSet<int> { index };
        var doneSet = new HashSet<int>(arbitraryDoneSet.Select(x => x.Get));

        return !RouteRerunDecisions.ShouldMarkRerun(index, markSet, doneSet);
    }

    // ========== Property 3: 已重跑 → false ==========

    [Property(MaxTest = 200)]
    public bool ShouldMarkRerun_WhenAlreadyDone_ReturnsFalse(
        NonNegativeInt routeIndex,
        List<NonNegativeInt> arbitraryMarkSet)
    {
        int index = routeIndex.Get;
        var markSet = new HashSet<int>(arbitraryMarkSet.Select(x => x.Get));
        var doneSet = new HashSet<int> { index };

        return !RouteRerunDecisions.ShouldMarkRerun(index, markSet, doneSet);
    }

    // ========== Property 4: 已标记且已重跑 → false ==========

    [Property(MaxTest = 200)]
    public bool ShouldMarkRerun_WhenMarkedAndDone_ReturnsFalse(NonNegativeInt routeIndex)
    {
        int index = routeIndex.Get;
        var markSet = new HashSet<int> { index };
        var doneSet = new HashSet<int> { index };

        return !RouteRerunDecisions.ShouldMarkRerun(index, markSet, doneSet);
    }

    // ========== Property 5: 两集合均空 → 任意 routeIndex 返回 true ==========

    [Property(MaxTest = 200)]
    public bool ShouldMarkRerun_WhenBothSetsEmpty_ReturnsTrue(NonNegativeInt routeIndex)
    {
        int index = routeIndex.Get;
        var markSet = new HashSet<int>();
        var doneSet = new HashSet<int>();

        return RouteRerunDecisions.ShouldMarkRerun(index, markSet, doneSet);
    }

    // ========== 基础单元测试 ==========

    [Fact]
    public void ShouldMarkRerun_RouteNotInAnySet_ReturnsTrue()
    {
        Assert.True(RouteRerunDecisions.ShouldMarkRerun(5, new HashSet<int> { 1, 2 }, new HashSet<int> { 3, 4 }));
    }

    [Fact]
    public void ShouldMarkRerun_RouteInMarkSet_ReturnsFalse()
    {
        Assert.False(RouteRerunDecisions.ShouldMarkRerun(5, new HashSet<int> { 5 }, new HashSet<int>()));
    }

    [Fact]
    public void ShouldMarkRerun_RouteInDoneSet_ReturnsFalse()
    {
        Assert.False(RouteRerunDecisions.ShouldMarkRerun(5, new HashSet<int>(), new HashSet<int> { 5 }));
    }
}
