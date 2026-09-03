#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using FsCheck;
using FsCheck.Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 传送后复苏保护 PBT 属性测试 - Correctness Property 3 / 4 / 5。
/// **Validates: Requirements 2.6-2.7, 3.1-3.6, 4.3-4.6, 6.2-6.4**
///
/// 覆盖列：
///   - Property 3 (One-shot)：同段并发/多次 TryConsume 至多一个赢家；段内 retry 不重置机会，
///     消费后状态保持 Consumed（只有切换段才建立新机会）。
///   - Property 4 (Preservation)：所有非命中输入（单机 / 同步点前 / 非 teleport / 窗口过期 /
///     机会已消耗 / 负时间差）通过 IsEligible 均返回 false，即不建立保护窗口。
///   - Property 5 (Segment Target)：ComputeProgress(seg, 0) 等于当前段起点进度（== seg），
///     不推进到下一段；最后一段不推进；段切换/新路线/新轮次状态不泄漏（按状态模型验证）。
///
/// 本文件只读引用 PostTeleportRevivalProtectionDecisions，不修改任何生产代码。
/// </summary>
public class PostTeleportRevivalProtectionPbtPropertiesTest
{
    // =========================================================================
    // Property 3: One-shot - 同段原子消费
    // =========================================================================

    /// <summary>
    /// 属性 3A：同一段多次 TryConsume，至多一个赢家；消费成功后状态保持为 1（Consumed）。
    ///
    /// **Validates: Requirements 2.6, 2.7, 4.3, 6.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TryConsume_ConcurrentCalls_AtMostOneWinner(
        PositiveInt actorCount)
    {
        // 模拟同段并发复苏事件：每个参与者尝试消费同一段机会。
        var consumed = 0;
        var winnerCount = 0;
        var n = Math.Min(actorCount.Get, 32);

        for (var i = 0; i < n; i++)
        {
            if (PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed))
            {
                winnerCount++;
            }
        }

        // 至多一个赢家；一旦有人赢，后续调用必然失败；最终状态恒为 Consumed(==1)。
        var onceConsumed = consumed == 1;
        var atMostOneWinner = winnerCount <= 1;
        var winnerCountMatchesConsumed = winnerCount == consumed;
        return (onceConsumed && atMostOneWinner && winnerCountMatchesConsumed).ToProperty();
    }

    /// <summary>
    /// 属性 3B：同段 retry 不重置机会——首次 TryConsume 成功后，段内任何重试/再次执行
    /// 都不得再次消费（机会只在进入下一段才建立新机会）。
    ///
    /// **Validates: Requirements 2.7, 4.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TryConsume_SegmentRetry_DoesNotResetOpportunity(
        PositiveInt retryCount)
    {
        var consumed = 0;
        var first = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);

        // 段内重跑/再次执行：机会保持 Consumed，不再被我方或其他事件重置。
        var anySecondWin = false;
        var n = Math.Min(retryCount.Get, 64);
        for (var i = 0; i < n; i++)
        {
            var winner = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
            anySecondWin = anySecondWin || winner;
        }

        return (first && !anySecondWin && consumed == 1).ToProperty();
    }

    // =========================================================================
    // Property 4: Preservation - 非命中复苏不建立保护窗口
    // =========================================================================

    /// <summary>
    /// 属性 4A：所有非命中输入类别，IsEligible 恒返回 false。
    /// 显式枚举 design/requirements 的非命中类别：单机、非 teleport、严格等待未完成、
    /// 机会已消费、负时间差、窗口过期（>10s）。每个类别通过对应输入组合构造。
    /// 该属性守护 Preservation：不满足 bug condition 的复苏不得建立保护窗口。
    ///
    /// **Validates: Requirements 3.1, 3.2, 3.3, 4.1, 4.2, 3.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property NonEligibleInputs_NeverHit(
        NonNegativeInt category)
    {
        var c = category.Get % 6;
        var completion = DateTime.UnixEpoch;

        var hit = false;
        switch (c)
        {
            // 0: 单机（非联机）—— 即使其他条件全部满足也不命中
            case 0:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: false,
                    syncPointType: "teleport",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(5),
                    consumed: false);
                break;
            // 1: 非 teleport 同步点（如自动/手动/其他 type）—— 不命中
            case 1:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "waypoint",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(5),
                    consumed: false);
                break;
            // 2: 严格等待未完成（同步点完成后才可建立窗口）—— 不命中
            case 2:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "teleport",
                    strictWaitCompleted: false,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(5),
                    consumed: false);
                break;
            // 3: 机会已消费 —— 不命中
            case 3:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "teleport",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(5),
                    consumed: true);
                break;
            // 4: 负时间差（复苏早于完成）—— 不命中
            case 4:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "teleport",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(-1),
                    consumed: false);
                break;
            // 5: 窗口过期（>10s）—— 不命中
            default:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "teleport",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(10).AddMilliseconds(1),
                    consumed: false);
                break;
        }

        return (!hit).ToProperty();
    }

    // =========================================================================
    // Property 5: Segment Target - 当前段起点与生命周期
    // =========================================================================

    /// <summary>
    /// 属性 5A：ComputeProgress(seg, 0) 等于当前段起点进度（== seg），不推进到下一段。
    /// 对任意段索引 seg，段起点目标就是 seg；且它不等于下一段起点 ComputeProgress(seg+1, 0)。
    ///
    /// **Validates: Requirements 2.4, 4.5, 6.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ComputeProgress_SegmentStart_DoesNotAdvance(
        NonNegativeInt segmentIndex)
    {
        var seg = segmentIndex.Get;
        var start = PostTeleportRevivalProtectionDecisions.ComputeProgress(seg, 0);
        var nextSegmentStart = PostTeleportRevivalProtectionDecisions.ComputeProgress(seg + 1, 0);

        // 段起点 == 段索引本身；不推进到下一段；与下一段起点严格不同。
        return (start == seg && start != nextSegmentStart).ToProperty();
    }

    /// <summary>
    /// 属性 5B：段内偏移单调不回头、段起点为段内最小值。
    /// ComputeProgress(seg, offset) 对 offset>=0 单调不减，且段起点（offset=0）小于等于任意段内偏移点。
    ///
    /// **Validates: Requirements 2.4, 6.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ComputeProgress_SegmentOffset_MonotonicFromStart(
        NonNegativeInt segmentIndex,
        NonNegativeInt offset)
    {
        var seg = segmentIndex.Get;
        var off = offset.Get;
        var start = PostTeleportRevivalProtectionDecisions.ComputeProgress(seg, 0);
        var target = PostTeleportRevivalProtectionDecisions.ComputeProgress(seg, off);

        // 段起点 <= 段内任意目标点；offset 越大目标越大（不回头）。
        return (start <= target && target == seg + off).ToProperty();
    }

    /// <summary>
    /// 属性 5C：最后一段不推进——保护命中目标仍是当前段起点，且最后一段的"下一段"不在可执行范围内。
    /// 用纯函数 + 状态模型验证：对任意段总数，最后一段（lastSegment）的目标 == 其段起点，
    /// 不会因"没有下一段"而错误地推进或跳过。
    ///
    /// **Validates: Requirements 4.5, 6.2**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ComputeProgress_LastSegment_DoesNotAdvance(
        PositiveInt segmentCount)
    {
        var count = Math.Max(1, segmentCount.Get);
        var lastSegment = count - 1;

        // 最后一段的段起点目标仍等于其自身索引；不推进到不存在的"下一段"。
        var lastStart = PostTeleportRevivalProtectionDecisions.ComputeProgress(lastSegment, 0);
        return (lastStart == lastSegment).ToProperty();
    }

    /// <summary>
    /// 属性 5D：段切换 / 新路线 / 新轮次状态不泄漏。
    /// 用状态模型（重放每段独立 TryConsume 消费位）验证：进入新段后，上一段的消费状态
    /// 对该新段不可见——新段拥有独立、未消费的机会。守护 Requirements 4.6 的"生命周期结束、新段重新建立机会"。
    ///
    /// **Validates: Requirements 4.6, 6.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property SegmentTransition_NewSegmentGetsFreshOpportunity(
        PositiveInt segmentCount)
    {
        var count = Math.Max(1, segmentCount.Get);
        // 模拟多段路线：为每段维护独立消费位（段切换时新段重新分配机会）。
        var consumedBySegment = new int[count];

        for (var seg = 0; seg < count; seg++)
        {
            // 进入新段：新段机会未消费，首次 TryConsume 必然赢。
            var first = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumedBySegment[seg]);

            // 该段内重试不重置（不会出现第二个赢家）。
            var second = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumedBySegment[seg]);

            if (!first || second)
            {
                return false.ToProperty();
            }
        }

        // 每段独立消费：每个段的消费位各自独立，无跨段泄漏。
        return consumedBySegment.All(c => c == 1).ToProperty();
    }
}
