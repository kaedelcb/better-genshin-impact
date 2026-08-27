#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Feature: hoeing-multiplayer-route-retry-mode 决策层属性测试。
/// 覆盖 RouteRetryModeDecisions（白名单纯函数）与 RevivalRecurrenceDecisions.Decide 的保守性。
/// 框架：FsCheck 2.16.6 + FsCheck.Xunit（[Property]）+ xUnit。
/// 详见 .kiro/specs/hoeing-multiplayer-route-retry-mode/design.md §5。
/// </summary>
public class RouteRetryModeDecisionsTest
{
    // ========== PBT-1：ParseKeywords 幂等 + 结果不含空白项 ==========
    [Property(MaxTest = 200)]
    public bool ParseKeywords_Idempotent_NoBlankEntries(string? raw)
    {
        var first = RouteRetryModeDecisions.ParseKeywords(raw);
        var second = RouteRetryModeDecisions.ParseKeywords(raw);

        // 幂等：两次调用结果集合相等（OrdinalIgnoreCase 语义下等价）
        var setEqual = new HashSet<string>(first, StringComparer.OrdinalIgnoreCase)
            .SetEquals(second);

        // 结果不含空白项，且每项均已 Trim（无首尾空白）
        var noBlank = first.All(kw => !string.IsNullOrWhiteSpace(kw) && kw == kw.Trim());

        return setEqual && noBlank;
    }

    // ========== PBT-2：空/空白/纯分隔符恒空集 ==========
    [Fact]
    public void ParseKeywords_NullOrBlankOrSeparatorsOnly_EmptySet()
    {
        Assert.Empty(RouteRetryModeDecisions.ParseKeywords(null));
        Assert.Empty(RouteRetryModeDecisions.ParseKeywords(""));
        Assert.Empty(RouteRetryModeDecisions.ParseKeywords("   "));
        Assert.Empty(RouteRetryModeDecisions.ParseKeywords(",，, "));
        Assert.Empty(RouteRetryModeDecisions.ParseKeywords("，,，"));
    }

    // ========== PBT-3：IsRetryRoute 空关键词集恒 false ==========
    [Property(MaxTest = 200)]
    public bool IsRetryRoute_EmptyKeywords_AlwaysFalse(string? fileName)
    {
        var empty = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return RouteRetryModeDecisions.IsRetryRoute(fileName, empty) == false;
    }

    // ========== PBT-4：命中一致性（含子串 → true；大小写不敏感） ==========
    [Property(MaxTest = 200)]
    public bool IsRetryRoute_ContainsKeyword_MatchesConsistently(NonEmptyString kwRaw, NonEmptyString prefix, NonEmptyString suffix)
    {
        var kw = kwRaw.Get.Trim();
        if (string.IsNullOrEmpty(kw)) return true; // 空关键词由 ParseKeywords 过滤，跳过

        var keywords = new HashSet<string>(new[] { kw }, StringComparer.OrdinalIgnoreCase);

        // 构造一个必然包含 kw 的文件名（大小写打乱验证 OrdinalIgnoreCase）
        var fileNameContains = prefix.Get + kw.ToUpperInvariant() + suffix.Get;
        var hit = RouteRetryModeDecisions.IsRetryRoute(fileNameContains, keywords);

        return hit; // 含子串（忽略大小写）必命中
    }

    // ========== PBT-4b：不含任何关键词 → false ==========
    [Fact]
    public void IsRetryRoute_NoKeywordPresent_False()
    {
        var keywords = new HashSet<string>(new[] { "深渊", "eremite" }, StringComparer.OrdinalIgnoreCase);
        Assert.False(RouteRetryModeDecisions.IsRetryRoute("蒙德风车平原_01", keywords));
        Assert.False(RouteRetryModeDecisions.IsRetryRoute("", keywords));
        Assert.False(RouteRetryModeDecisions.IsRetryRoute(null, keywords));
    }

    // ========== PBT-5：全/半角分隔等价 ==========
    [Fact]
    public void ParseKeywords_FullWidthAndHalfWidthSeparator_Equivalent()
    {
        var half = RouteRetryModeDecisions.ParseKeywords("甲,乙,丙");
        var full = RouteRetryModeDecisions.ParseKeywords("甲，乙，丙");
        var mixed = RouteRetryModeDecisions.ParseKeywords("甲,乙，丙");

        var expected = new HashSet<string>(new[] { "甲", "乙", "丙" }, StringComparer.OrdinalIgnoreCase);
        Assert.True(expected.SetEquals(half));
        Assert.True(expected.SetEquals(full));
        Assert.True(expected.SetEquals(mixed));
    }

    // ========== PBT-6：Decide 永不返回 RetrySegment（保守性） ==========
    // RetrySegment 只能由 RouteExecutionEngine 显式覆盖，Decide 决策层不得产生。
    [Property(MaxTest = 500)]
    public bool Decide_NeverReturnsRetrySegment(int stampCount, int nowOffset, int windowSeconds, int rapidThreshold, int routeCap)
    {
        var now = new DateTime(2025, 1, 1, 0, 0, 0, DateTimeKind.Utc);
        var n = Math.Abs(stampCount % 12);
        var stamps = new List<DateTime>();
        for (int i = 0; i < n; i++)
        {
            // 在 now 附近撒时间戳（含窗口内外）
            stamps.Add(now.AddSeconds(-(Math.Abs(nowOffset % 300)) + i));
        }

        var action = RevivalRecurrenceDecisions.Decide(
            stamps, now,
            Math.Abs(windowSeconds % 600),
            Math.Abs(rapidThreshold % 20),
            Math.Abs(routeCap % 20));

        return action != RevivalEscalationAction.RetrySegment;
    }
}
