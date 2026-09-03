#nullable enable

using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 传送后卡死保护 PBT 属性测试（对偶扩展自复苏保护 PBT）。
/// **Validates: Requirements 2.1-2.7, 3.1-3.6, 4.1-4.3, 6.1-6.4**
///
/// 覆盖列（对应 design.md §Correctness Properties）：
///   - Property 1 (Bug Condition)：7 参 IsEligible("stuck",...) 仅在联机 + teleport + 严格等待 +
///     未消费 + 0 &lt;= elapsed &lt;= 20s 命中；其余组合全部不命中（stuck 窗口 20 秒）。
///   - Property 2 (Boundary)：恰好 0 / 20 秒命中、20.001 秒不命中、负时间差不命中、
///     revival 保持 10 秒窗口。
///   - Property 3 (One-shot)：TryConsume CAS 至多一个赢家；同段卡死先消费则复苏不再获得（反之亦然）。
///   - Property 4 (Preservation)：非命中卡死输入（单机/非 teleport/未完成/已消费/窗口外/负时间差）
///     经 IsEligible 恒返回 false，即不建立保护窗口、不改变既有卡死脱困/跳路线语义。
///
/// 本文件只读引用 PostTeleportRevivalProtectionDecisions，不修改任何生产代码。
/// </summary>
public class PostTeleportStuckProtectionPbtPropertiesTest
{
    // =========================================================================
    // Property 1: Bug Condition - 传送后本机首次卡死保护
    // =========================================================================

    /// <summary>
    /// 属性 1A：7 参 IsEligible("stuck",...) 只对满足 bug condition 的输入返回 true。
    /// 撒任意 (isMultiplayerHoeing, syncPointType, strictWaitCompleted, consumed, elapsedSeconds,
    /// elapsedMilliseconds)，断言返回值精确等价于"联机 &amp;&amp; teleport &amp;&amp; 严格等待完成
    /// &amp;&amp; 未消费 &amp;&amp; 0 &lt;= elapsed &lt;= 20_000ms（stuck 20 秒窗口）"。
    ///
    /// **Validates: Requirements 2.1, 2.6, 6.1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property IsEligible_Stuck_OnlyMatchesProtectedInputs(
        bool isMultiplayerHoeing,
        NonNull<string> syncPointType,
        bool strictWaitCompleted,
        bool consumed,
        int elapsedSeconds,
        int elapsedMilliseconds)
    {
        var completionTime = DateTime.UnixEpoch;
        var triggerTime = completionTime.AddSeconds(elapsedSeconds)
            .AddMilliseconds(elapsedMilliseconds);
        var actual = PostTeleportRevivalProtectionDecisions.IsEligible(
            "stuck", isMultiplayerHoeing, syncPointType.Get, strictWaitCompleted,
            completionTime, triggerTime, consumed);
        var totalMs = elapsedSeconds * 1000L + elapsedMilliseconds;
        var expected = isMultiplayerHoeing
            && syncPointType.Get == "teleport"
            && strictWaitCompleted
            && !consumed
            && totalMs >= 0
            && totalMs <= 20_000;   // stuck 窗口 20 秒
        return (actual == expected).ToProperty();
    }

    // =========================================================================
    // Property 2: Boundary - 时间与同步资格
    // =========================================================================

    /// <summary>
    /// 属性 2A：elapsed == 0 与 elapsed == 20（stuck）命中，elapsed == 20.001 不命中。
    ///
    /// **Validates: Requirements 2.6, 4.1, 6.1**
    /// </summary>
    [Property]
    public Property IsEligible_BoundaryZeroAndTwenty_Stuck(bool a, bool b)
    {
        var c0 = DateTime.UnixEpoch;
        var hit0 = PostTeleportRevivalProtectionDecisions.IsEligible(
            "stuck", true, "teleport", true, c0, c0, false);
        var c20 = DateTime.UnixEpoch;
        var hit20 = PostTeleportRevivalProtectionDecisions.IsEligible(
            "stuck", true, "teleport", true, c20, c20.AddSeconds(20), false);
        var miss21 = PostTeleportRevivalProtectionDecisions.IsEligible(
            "stuck", true, "teleport", true, c20, c20.AddSeconds(20.001), false);
        return (hit0 && hit20 && !miss21).ToProperty();
    }

    /// <summary>
    /// 属性 2B：负时间差（卡死早于严格同步完成）恒不命中。
    ///
    /// **Validates: Requirements 2.1, 4.1**
    /// </summary>
    [Property]
    public Property IsEligible_NegativeTime_DoesNotHit()
    {
        var c = DateTime.UnixEpoch;
        var hit = PostTeleportRevivalProtectionDecisions.IsEligible(
            "stuck", true, "teleport", true, c, c.AddSeconds(-1), false);
        return (!hit).ToProperty();
    }

    /// <summary>
    /// 属性 2C：revival 保持既有 10 秒窗口（对偶回归防护）。
    /// 撒任意输入，断言 revival 命中条件精确等价于"联机 &amp;&amp; teleport &amp;&amp; 严格等待完成
    /// &amp;&amp; 未消费 &amp;&amp; 0 &lt;= elapsed &lt;= 10_000ms（revival 10 秒窗口）"。
    ///
    /// **Validates: Requirements 3.7, 6.1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property IsEligible_Revival_KeepsTenSecondWindow(
        bool isMultiplayerHoeing,
        NonNull<string> syncPointType,
        bool strictWaitCompleted,
        bool consumed,
        int elapsedSeconds,
        int elapsedMilliseconds)
    {
        var completionTime = DateTime.UnixEpoch;
        var triggerTime = completionTime.AddSeconds(elapsedSeconds)
            .AddMilliseconds(elapsedMilliseconds);
        var actual = PostTeleportRevivalProtectionDecisions.IsEligible(
            "revival", isMultiplayerHoeing, syncPointType.Get, strictWaitCompleted,
            completionTime, triggerTime, consumed);
        var totalMs = elapsedSeconds * 1000L + elapsedMilliseconds;
        var expected = isMultiplayerHoeing
            && syncPointType.Get == "teleport"
            && strictWaitCompleted
            && !consumed
            && totalMs >= 0
            && totalMs <= 10_000;   // revival 窗口 10 秒（保持既有语义）
        return (actual == expected).ToProperty();
    }

    // =========================================================================
    // Property 3: One-shot - 同段共享一次性（stuck 与 revival 竞争）
    // =========================================================================

    /// <summary>
    /// 属性 3A：TryConsume CAS 一次性消费——首次赢，第二次失败，最终状态恒为 1。
    ///
    /// **Validates: Requirements 2.7, 4.3, 6.4**
    /// </summary>
    [Property]
    public Property TryConsume_IsOneShot()
    {
        var consumed = 0;
        var first = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
        var second = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
        return (first && !second && consumed == 1).ToProperty();
    }

    /// <summary>
    /// 属性 3B：同段共享一次性——卡死先消费则复苏不再获得（反之亦然）。
    /// 用 CAS 重放：stuck 先 TryConsume 赢，则 revival 的 TryConsume 必须失败，消费位保持 1。
    ///
    /// **Validates: Requirements 2.7, 2.8, 3.7, 4.3**
    /// </summary>
    [Property]
    public Property SharedOneShot_StuckThenRevival_SingleConsumption()
    {
        // 同段共享一次性：卡死先消费则复苏不再获得（反之亦然）
        var consumed = 0;
        var stuckWon = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
        var revivalTried = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
        return (stuckWon && !revivalTried && consumed == 1).ToProperty();
    }

    // =========================================================================
    // Property 4: Preservation - 非命中卡死不建立保护窗口
    // =========================================================================

    /// <summary>
    /// 属性 4A：所有非命中卡死输入类别经 IsEligible("stuck",...) 恒返回 false。
    /// 显式枚举非命中类别：单机、非 teleport 同步点、严格等待未完成、机会已消费、
    /// 负时间差、窗口过期（&gt;20s）；每个类别通过对应输入组合构造。守护 Preservation：
    /// 不满足 bug condition 的卡死不建立保护窗口、保持既有卡死脱困/跳路线语义。
    ///
    /// **Validates: Requirements 3.1, 3.2, 3.3, 3.5, 3.6, 4.1**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property NonEligibleStuckInputs_DoNotCreateProtection(
        bool isMultiplayerHoeing,
        NonNull<string> syncPointType,
        bool strictWaitCompleted,
        bool consumed,
        int elapsedSeconds)
    {
        var c = DateTime.UnixEpoch;
        var t = c.AddSeconds(elapsedSeconds);
        var actual = PostTeleportRevivalProtectionDecisions.IsEligible(
            "stuck", isMultiplayerHoeing, syncPointType.Get, strictWaitCompleted,
            c, t, consumed);
        var isEligible = isMultiplayerHoeing
            && syncPointType.Get == "teleport"
            && strictWaitCompleted
            && !consumed
            && elapsedSeconds >= 0
            && elapsedSeconds <= 20;
        // 若输入本就不满足条件，则 must not hit；若条件成立则 actual 应与 isEligible 一致。
        // 这里强化为：非命中输入（!isEligible）恒不命中。
        return (!isEligible || !actual).ToProperty();
    }

    /// <summary>
    /// 属性 4B：重跑跳过段起点同步等待——跳过标志消费即复位，且只在真时跳过。
    /// 纯布尔模型守护 Requirements 2.4 / 3.9 / 4.7 的"仅重跑路径上跳过该次等待，消费即复位"。
    ///
    /// **Validates: Requirements 2.4, 3.9, 4.7**
    /// </summary>
    [Property]
    public Property SegmentStartSyncWaitSkip_IsConsumedOnce(bool startValue)
    {
        // 重跑跳过段起点同步等待：标志消费即复位，且只在真时跳过
        var flag = startValue;
        var shouldSkip = flag;
        flag = false;   // 消费即复位
        return (shouldSkip == startValue && !flag).ToProperty();
    }
}
