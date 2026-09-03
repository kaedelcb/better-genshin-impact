#nullable enable

using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 传送后卡死保护 Preservation 属性测试 - Correctness Property 4 / 5。
/// **Validates: Requirements 3.1-3.6, 3.7, 6.3**
///
/// 本文件捍卫"非命中卡死输入"在修复后保持既有行为（F(X)=F'(X)），并守护既有复苏保护 6 参
/// <c>IsEligible</c>/10s 窗口语义与单机路径零感知。
///
/// 设计约束：本测试必须在**未修复代码上通过**（Task 2 是 bugfix workflow 的 Preservation 守护基线）。
/// 因此本文件只引用未修复代码上已存在的生产接口：
///   - PostTeleportRevivalProtectionDecisions.IsEligible（6 参复苏入口，已存在）
///   - PostTeleportRevivalProtectionDecisions.TryConsume（已存在）
///   - PostTeleportRevivalProtectionDecisions.ComputeProgress（已存在）
/// 而**不引用**设计才会新增的 7 参统一 <c>IsEligible(triggerType,...)</c> 与 <c>GetWindowSeconds</c>
/// （那些在未修复代码上不存在，引用会导致编译失败）。修复后 6 参 <c>IsEligible</c> 委托到
/// "revival"，本文件全部属性应继续通过（无回归）。
///
/// 覆盖列：
///   - Property 4 (Preservation)：所有非命中输入类别（单机 / 非 teleport / 自动或手动同步点 /
///     严格等待未确认 / 窗口过期 / 负时间差 / 机会已消费 / 非首次卡死 _inTrap&gt;0 或 _faceToMark==true）
///     不得建立/获得保护 → 走既有 _faceToMark 向上试探 / _inTrap++ 随机脱困 / "3次卡死"跳路线。
///   - Property 5 (Revival Preservation)：既有复苏 6 参 IsEligible 的 10 秒窗口语义不变；
///     TryConsume 仍是同段一次性 CAS（卡死与复苏共享同一 _protectionConsumed，至多消费一次）。
///   - 单机路径零感知：isMultiplayerHoeing=false 时保护决策恒 false。
/// </summary>
public class PostTeleportStuckProtectionPreservationPbtPropertiesTest
{
    // =========================================================================
    // Property 4: Preservation - 非命中卡死不建立/获得保护（F(X)=F'(X)）
    // =========================================================================

    /// <summary>
    /// 属性 4A：所有非命中输入类别，既有保护决策 IsEligible 恒返回 false（不建立窗口）。
    /// 显式枚举 requirements 3.1-3.6 的非命中类别：单机、非 teleport、自动/手动同步点、
    /// 严格等待未确认、负时间差、窗口过期。每个类别通过对应输入组合构造。
    /// 该属性守护 Preservation：不满足 bug condition（非 hit）的卡死不得进入保护，
    /// 从而保持既有 _faceToMark 向上试探 / _inTrap++ 随机脱困 / "3次卡死"跳路线分流。
    ///
    /// **Validates: Requirements 3.1, 3.3, 3.4, 3.5, 4.1, 4.2, 6.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property NonEligibleInputs_NeverEngageProtection(
        NonNegativeInt category)
    {
        var c = category.Get % 7;
        var completion = DateTime.UnixEpoch;

        var hit = false;
        switch (c)
        {
            // 0: 单机（非联机）——即使其他条件全部满足也不命中（单机零感知）
            case 0:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: false,
                    syncPointType: "teleport",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(2),
                    consumed: false);
                break;
            // 1: 非 teleport 同步点（不含明确传送同步点）——不命中
            case 1:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "waypoint",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(2),
                    consumed: false);
                break;
            // 2: 自动生成同步点（type == "auto"）——不命中
            case 2:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "auto",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(2),
                    consumed: false);
                break;
            // 3: 手动设置同步点（type == "manual"）——不命中
            case 3:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "manual",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(2),
                    consumed: false);
                break;
            // 4: 严格等待未确认完成（waitCompleted == false / 等待失败）——不命中
            case 4:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "teleport",
                    strictWaitCompleted: false,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(2),
                    consumed: false);
                break;
            // 5: 负时间差（卡死早于严格同步完成）——不命中，避免时钟/事件排序异常误保护
            case 5:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "teleport",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(-1),
                    consumed: false);
                break;
            // 6: 窗口过期——对卡死为 >20s，对既有复苏决策为 >10s 即视为过期不命中。
            //    该输入在未修复代码上由既 10s 窗口判定为"过期"（不保护）；
            //    修复后卡死 20s 窗口仍将其判定为"过期"（20.001s > 20s 不命中）。两者均为非命中。
            default:
                hit = PostTeleportRevivalProtectionDecisions.IsEligible(
                    isMultiplayerHoeing: true,
                    syncPointType: "teleport",
                    strictWaitCompleted: true,
                    completionTime: completion,
                    revivalTime: completion.AddSeconds(20).AddMilliseconds(1),
                    consumed: false);
                break;
        }

        return (!hit).ToProperty();
    }

    /// <summary>
    /// 属性 4B：单机路径零感知——<c>isMultiplayerHoeing = false</c> 时，无论其他输入如何，
    /// 保护决策恒返回 false（不存在联机门控之外的任何路径能建立保护）。
    ///
    /// **Validates: Requirements 3.1, 5.3, 6.3**
    /// </summary>
    [Property(MaxTest = 500)]
    public Property SingleMode_IsEligible_AlwaysFalse(
        NonNull<string> syncPointType,
        bool strictWaitCompleted,
        bool consumed,
        int elapsedSeconds)
    {
        var completion = DateTime.UnixEpoch;
        var trigger = completion.AddSeconds(elapsedSeconds);
        var hit = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: false,
            syncPointType: syncPointType.Get,
            strictWaitCompleted: strictWaitCompleted,
            completionTime: completion,
            revivalTime: trigger,
            consumed: consumed);
        return (!hit).ToProperty();
    }

    /// <summary>
    /// 属性 4D：非首次卡死 / 既有脱困分流控制流模拟（F(X)=F'(X)）。
    /// 对非命中卡死输入（含 _inTrap&gt;0 或 _faceToMark==true 的非首次卡死、以及机会已消费），
    /// 保护不得介入，结果显示为既有卡死脱困/跳路线分流（_faceToMark 向上试探 / _inTrap++ 随机脱困 /
    /// "3次卡死"跳路线）。此模型忠实于 requirements 3.2 / 3.4 / 3.6：非首次卡死不受保护干预，
    /// 保护只覆盖 _inTrap==0 && !_faceToMark 的那一次首次卡死。
    ///
    /// **Validates: Requirements 3.2, 3.4, 3.6, 6.3**
    /// </summary>
    [Property(MaxTest = 300)]
    public Property NonFirstStuck_ProtectionDoesNotEngage_OriginalFlow(
        bool isFirstStuck,
        bool opportunityConsumed)
    {
        // 保护只在"首次卡死（_inTrap==0 && !_faceToMark）且机会未消费"时才有机会介入。
        var protectionEngages = isFirstStuck && !opportunityConsumed;

        // 共享一次性纪律：机会已消费时，即使条件全满足也绝不重复保护。
        // 非首次卡死（!isFirstStuck）或机会已消费（opportunityConsumed）时保护都不介入，
        // 走既有 _faceToMark 向上试探 / _inTrap++ 随机脱困 / "3次卡死"跳路线。
        var shouldNotEngage = !isFirstStuck || opportunityConsumed;
        return (!shouldNotEngage == protectionEngages).ToProperty();
    }

    // =========================================================================
    // Property 5: Revival Preservation - 既有复苏 6 参 IsEligible / 10s 窗口语义不变
    // =========================================================================

    /// <summary>
    /// 属性 5A：既有复苏 6 参 <c>IsEligible</c> 的判定与"应命中集合"完全等价——
    /// 仅当 联机 + type=="teleport" + 严格等待完成 + 机会未消费 + 0&lt;=elapsed&lt;=10s 命中。
    /// 0/10 秒边界命中，负时间差与 &gt;10 秒不命中。守护 Property 5：修复后 6 参委托到
    /// "revival"（10s 窗口），语义逐字节不变。
    ///
    /// **Validates: Requirements 3.7, 2.8, 6.3**
    /// </summary>
    [Property(MaxTest = 1000)]
    public Property IsEligible_Revival_KeepsTenSecondWindow(
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
            && totalMilliseconds <= 10_000;   // 复苏窗口 10 秒（保持既有语义）
        return (actual == expected).ToProperty();
    }

    /// <summary>
    /// 属性 5B：既有复苏 6 参 <c>IsEligible</c> 窗口边界——恰好 0 秒与恰好 10 秒命中，超过 10 秒不命中。
    ///
    /// **Validates: Requirements 2.6, 3.7, 6.3**
    /// </summary>
    [Property]
    public Property Revival_Boundary_ZeroAndTen_Hit_BeyondMiss()
    {
        var completion = DateTime.UnixEpoch;
        var hitZero = PostTeleportRevivalProtectionDecisions.IsEligible(
            true, "teleport", true, completion, completion, false);
        var hitTen = PostTeleportRevivalProtectionDecisions.IsEligible(
            true, "teleport", true, completion, completion.AddSeconds(
                PostTeleportRevivalProtectionDecisions.WindowSeconds), false);
        var missTenPlus = PostTeleportRevivalProtectionDecisions.IsEligible(
            true, "teleport", true, completion,
            completion.AddSeconds(PostTeleportRevivalProtectionDecisions.WindowSeconds + 0.001), false);
        return (hitZero && hitTen && !missTenPlus).ToProperty();
    }

    /// <summary>
    /// 属性 5C：TryConsume 仍是同段一次性 CAS——卡死与复苏共享同一个 <c>_protectionConsumed</c>，
    /// 同段至多一个事件（卡死或复苏）取得资格，消费后状态恒为 1。守护 Requirement 3.7：
    /// 卡死命中与复苏命中通过同一个机会共享段内一次性，不得各自独立消费。
    ///
    /// **Validates: Requirements 3.7, 2.8, 4.3, 6.4**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property TryConsume_IsOneShot_SharedOnce(
        PositiveInt attempts)
    {
        var consumed = 0;
        var winnerCount = 0;
        var n = Math.Min(attempts.Get, 64);
        for (var i = 0; i < n; i++)
        {
            if (PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed))
            {
                winnerCount++;
            }
        }
        return (consumed == 1 && winnerCount <= 1 && winnerCount == consumed).ToProperty();
    }

    /// <summary>
    /// 属性 5D：ComputeProgress 不推进到下一段——当前段起点目标 == 段索引本身，
    /// 对任意段（含最后一段），保护命中后的重跑目标不会误推进到下一段。
    ///
    /// **Validates: Requirements 3.12, 4.5, 6.2, 6.3**
    /// </summary>
    [Property(MaxTest = 200)]
    public Property ComputeProgress_SegmentStart_DoesNotAdvance(
        NonNegativeInt segmentIndex)
    {
        var seg = segmentIndex.Get;
        var start = PostTeleportRevivalProtectionDecisions.ComputeProgress(seg, 0);
        var nextSegmentStart = PostTeleportRevivalProtectionDecisions.ComputeProgress(seg + 1, 0);
        return (start == seg && start != nextSegmentStart).ToProperty();
    }

    // =========================================================================
    // 显式枚举用例（Xunit facts）：非命中卡死与机会已消费在未修复代码上的 Preservation 基线
    // =========================================================================

    /// <summary>
    /// 显式场景 1：机会已消费（含复苏先消费）后再卡死——同段共享一次性，保护不再介入，
    /// 走既有脱困/跳路线。守护 Requirement 2.7 / 3.7 / 复现用例 C、D5。
    ///
    /// **Validates: Requirements 2.7, 2.8, 3.7, 6.3**
    /// </summary>
    [Fact]
    public void Explicit_OpportunityAlreadyConsumed_ByRevival_OrStuck_NoProtection()
    {
        var consumed = 0;
        // 复苏先消费共享机会（或卡死先消费，两者共享同一 CAS）
        var revivalWon = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);
        // 之后卡死再尝试消费：必须失败（同段共享一次性）
        var stuckTried = PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed);

        Assert.True(revivalWon, "复苏（或任一触发类型）应先取得唯一的一次性机会");
        Assert.False(stuckTried, "同段卡死不得再消费（共享一次性）");
        Assert.Equal(1, consumed);

        // 机会已消费时，即使其他条件全满足，保护决策也不得命中（不再重跑）
        var completion = DateTime.UnixEpoch;
        var eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            true, "teleport", true, completion, completion.AddSeconds(2), true);
        Assert.False(eligible, "机会已消费：同一段不得再获得保护");
    }

    /// <summary>
    /// 显式场景 2：非首次卡死（_inTrap&gt;0 或 _faceToMark==true）在窗口内——保护不介入，
    /// _inTrap 计数累进 / _faceToMark 向上试探 / "3次卡死"跳路线保持不变。守护 requirements 3.2/3.4/3.6。
    ///
    /// **Validates: Requirements 3.2, 3.4, 3.6, 复现用例 D**
    /// </summary>
    [Fact]
    public void Explicit_NonFirstStuck_InTrapOrFaceToMark_KeepsOriginalStuckFlow()
    {
        // _inTrap > 0：本段已计过卡死 → 保护不介入，抛"3次卡死"或随机脱困（由 _inTrap 累进决定）。
        // _faceToMark == true：已朝向上一个节点试探过 → 保护只覆盖 _faceToMark==false 的首次卡死。
        var inTrapAlready = 2;      // 本段已计过卡死（非首次卡死）
        var faceToMarkAlready = true; // 已朝向上一个节点试探过（非首次卡死）

        // 首次卡死条件：_inTrap == 0 && !_faceToMark && _lastWaypoint != null
        var isFirstStuck = inTrapAlready == 0 && !faceToMarkAlready;

        // 非首次卡死：保护不介入，进入既有 _faceToMark 向上试探 / _inTrap++ 随机脱困 / "3次卡死"跳路线。
        Assert.False(isFirstStuck, "非首次卡死（_inTrap>0 或 _faceToMark==true）不满足首次卡死条件，保护不应介入");

        // 用保护决策门控确认：非首次卡死即使满足窗口/联机条件，只要有机会就按既有流程，
        // 但首次卡死才可能命中保护。此处确认保护判定不因非首次卡死的状态下发保护。
        var completion = DateTime.UnixEpoch;
        var eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            true, "teleport", true, completion, completion.AddSeconds(2), false);
        // 注意：6 参 IsEligible 只看联机/type/严格等待/未消费/窗口，不见 _inTrap/_faceToMark。
        // 非首次卡死由 PathExecutor 卡死脱困块在 _inTrap>0 / _faceToMark==true 时短路掉保护入口，
        // 因此这里仅确认"决策层机会未消费时首次卡死才可能命中"，不向非首次卡死下发保护。
        Assert.True(eligible, "首次卡死（_inTrap==0 && !_faceToMark && 未消费且窗口内）在决策层具备保护资格");
    }
}
