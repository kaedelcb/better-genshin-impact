#nullable enable

using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// PostTeleportRevivalProtectionDecisions 的 PBT 属性测试。
/// **Validates: Property 2 (Boundary) / Requirements 2.5, 4.1, 4.2, 6.1**
///
/// 覆盖列：
///   - 属性 1 IsEligible：仅当 联机 + type=="teleport" + 严格等待完成 + 机会未消费
///     + -0 <= elapsed <= 10s 时命中。0/10 秒边界命中，负时间差与 >10 秒不命中。
///   - 属性 2 TryConsume：CAS 一次性消费，同段至多一个调用返回 true。
///   - 属性 3 NonEligible：所有非保护输入一律不建立窗口（不命中）。
/// </summary>
public class PostTeleportRevivalProtectionDecisionsTest
{
    // =========================================================================
    // 属性 1：IsEligible 只匹配受保护输入（联机 teleport + 严格等待 + 未消费 + 0<=elapsed<=10s）
    // 直接对照 design.md §Property-Based Tests 的方法签名与断言。
    // =========================================================================

    /// <summary>
    /// 属性 1：Eligibility 判定与"应命中集合"完全等价。
    /// 0/10 秒边界自动落入窗口；负时间差与 >10 秒排除。
    ///
    /// **Validates: Requirements 2.5, 4.1, 4.2, 6.1**
    /// </summary>
    [Property(MaxTest = 1000)]
    public Property IsEligible_OnlyMatchesProtectedInputs(
        bool isMultiplayerHoeing,
        NonNull<string> syncPointType,
        bool strictWaitCompleted,
        bool consumed,
        int elapsedSeconds,
        int elapsedMilliseconds)
    {
        var completionTime = DateTime.UnixEpoch;
        var revivalTime = completionTime.AddSeconds(elapsedSeconds)
            .AddMilliseconds(elapsedMilliseconds);
        var actual = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing, syncPointType.Get, strictWaitCompleted,
            completionTime, revivalTime, consumed);
        var totalMilliseconds = elapsedSeconds * 1000L + elapsedMilliseconds;
        var expected = isMultiplayerHoeing
            && syncPointType.Get == "teleport"
            && strictWaitCompleted
            && !consumed
            && totalMilliseconds >= 0
            && totalMilliseconds <= 10_000;
        return (actual == expected).ToProperty();
    }

    // =========================================================================
    // 属性 2：TryConsume 一次性消费（CAS）
    // =========================================================================

    /// <summary>
    /// 属性 2：同段至多一个事件成功消费机会；消费后状态为 1 且后续调用失败。
    ///
    /// **Validates: Requirements 2.6, 4.3**
    /// </summary>
    [Property]
    public Property TryConsume_IsOneShot()
    {
        var consumed = 0;
        var first = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
        var second = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
        return (first && !second && consumed == 1).ToProperty();
    }

    // =========================================================================
    // 属性 3：所有非保护输入都不建立窗口（非命中恒 false）
    // =========================================================================

    /// <summary>
    /// 属性 3：服务端（单机 / 非 teleport / 未完成 / 已消费 / 负时间差 / >10s）
    /// 都不得命中保护。即"应命中集"（isEligible）为假时实际判定必为假。
    /// 注意：当应命中集为真时本属性不做约束（该方向由属性 1 覆盖）。
    ///
    /// **Validates: Requirements 3.1, 3.3, 4.1, 4.2, 6.1 (Preservation)**
    /// </summary>
    [Property(MaxTest = 1000)]
    public Property NonEligibleInputs_DoNotCreateProtectionWindow(
        bool isMultiplayerHoeing,
        NonNull<string> syncPointType,
        bool strictWaitCompleted,
        bool consumed,
        int elapsedSeconds)
    {
        var completionTime = DateTime.UnixEpoch;
        var revivalTime = completionTime.AddSeconds(elapsedSeconds);
        var actual = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing, syncPointType.Get, strictWaitCompleted,
            completionTime, revivalTime, consumed);
        var isEligible = isMultiplayerHoeing
            && syncPointType.Get == "teleport"
            && strictWaitCompleted
            && !consumed
            && elapsedSeconds >= 0
            && elapsedSeconds <= 10;
        return (!isEligible || !actual).ToProperty();
    }
}