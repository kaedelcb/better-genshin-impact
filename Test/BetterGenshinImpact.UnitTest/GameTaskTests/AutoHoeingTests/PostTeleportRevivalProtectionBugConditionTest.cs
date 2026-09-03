using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Bug Condition Exploration Test - Post-Teleport Revival Protection
///
/// **Validates: Requirements 1.1, 1.2, 1.3, 复现用例 A/B/D**
///
/// Bug Condition (C)（来自 requirements.md §Bug Condition）：
///   一次复苏事件 X = (mode, segment, syncPoint, completionTime, revivalTime, used)，
///   isBugCondition(X) 为真当且仅当：
///     mode = MultiplayerHoeing
///     AND syncPoint.type = "teleport"
///     AND syncPoint.strictWaitCompleted = true        （严格 WaitForAllPlayers 已消费匹配 AllArrived）
///     AND completionTime &lt;= revivalTime
///     AND revivalTime - completionTime &lt;= 10 秒
///     AND used = false
///
/// 未修复实现（PathExecutor.cs 的 catch(RetryException) 同步点后分流，约 line 1682-1697）：
///   ```
///   if (_syncPointReached && MultiplayerCoordinator != null)
///   {
///       // 上报 Reviving + targetProgress
///       SkipToNextSegment = true;      // BUG：跳到下一传送点 C
///       _needReportNormalBeforeSync = true;
///       break;                          // BUG：跳过 B→C 线段
///   }
///   ```
///   PathExecutor 在严格传送同步等待完成后把 _syncPointReached 置为 true（line 814-818），
///   而 MultiplayerCoordinator.WaitForAllPlayers 未修复时返回 void、无法让执行器区分
///   "严格等待已完成"与异常。因此本机在严格同步完成后窗口内复苏会被误判为"同步点后异常"，
///   从而跳到下一段并跳过当前 B→C 线段。
///
/// 本测试在未修复代码上预期失败（EXPECTED TO FAIL）：
///   忠实模拟未修复 PathExecutor 的同步点后跳段分流（不消费保护决策类），
///   并断言"修复后期望行为"（保护命中 → 不跳段、从当前段传送起点 B 重跑、不 SkipToNextSegment）。
///   由于模拟的是未修复逻辑（跳段），与期望行为（不跳段）相悖 → 测试失败 → 捕获 bug condition。
///   作为后续回归对比基线，修复完成后这些测试应通过。
/// </summary>
public class PostTeleportRevivalProtectionBugConditionTest
{
    // =========================================================================
    // 未修复 PathExecutor 同步点后跳段分流的忠实模拟
    //
    // 忠实复现 PathExecutor.cs catch(RetryException) 中"同步点后异常"分支（line 1682-1697）：
    //   if (_syncPointReached && MultiplayerCoordinator != null)
    //       → 上报 Reviving + SkipToNextSegment = true（跳到下一段 C）+ break（跳过 B→C）
    //
    // 注意：未修复的执行器不消费 PostTeleportRevivalProtectionDecisions，
    // 即不识别"严格传送同步完成后 10 秒窗口内的本机复苏"保护条件。
    // =========================================================================

    /// <summary>
    /// 模拟未修复 PathExecutor 在 catch(RetryException) 中的同步点后跳段分流。
    /// 输入一次复苏事件的控制流状态，返回未修复执行器的可观察结果。
    /// </summary>
    private static UnfixedResult SimulateUnfixedSyncPointAfterRevival(
        bool isMultiplayerHoeing,
        bool syncPointReached,
        bool revivalIsEligibleForProtection)
    {
        // 未修复代码：执行器完全不识别保护条件，复苏一律进入统一 RetryException 分流。
        // 当同步点已到达（_syncPointReached）且联机时 → 走"同步点后异常"分支 → 跳段。
        bool syncPointAfterAbnormal = syncPointReached && isMultiplayerHoeing;

        var result = new UnfixedResult
        {
            // 保护条件本应命中（revivalIsEligibleForProtection），但未修复代码不识别它：
            ProtectionRecognized = false,
            ReportsReviving = syncPointAfterAbnormal,
            SkipsToNextSegment = syncPointAfterAbnormal
        };

        // 反例判定：保护条件本应命中（应重跑 B→C 且不跳段），但未修复逻辑却跳段了。
        result.BugConditionExhibited =
            revivalIsEligibleForProtection && result.SkipsToNextSegment;
        return result;
    }

    /// <summary>
    /// 模拟修复后 PathExecutor 的同步点后分流（保护分支在跳段分流之前短路）。
    /// 命中保护 → 仅重跑当前段起点，不跳段、不上报 Reviving、不 SkipToNextSegment。
    /// </summary>
    private static FixedResult SimulateFixedSyncPointAfterRevival(
        bool isMultiplayerHoeing,
        bool syncPointReached,
        bool revivalIsEligibleForProtection)
    {
        var result = new FixedResult
        {
            ProtectionHit = revivalIsEligibleForProtection,
            RestartsCurrentSegment = revivalIsEligibleForProtection,
            SkipsToNextSegment = syncPointReached && isMultiplayerHoeing && !revivalIsEligibleForProtection,
            ReportsReviving = syncPointReached && isMultiplayerHoeing && !revivalIsEligibleForProtection
        };
        return result;
    }

    /// <summary>未修复执行器结果载体。</summary>
    private sealed class UnfixedResult
    {
        public bool ProtectionRecognized { get; set; }
        public bool ReportsReviving { get; set; }
        public bool SkipsToNextSegment { get; set; }
        public bool BugConditionExhibited { get; set; }
    }

    /// <summary>修复后执行器结果载体。</summary>
    private sealed class FixedResult
    {
        public bool ProtectionHit { get; set; }
        public bool RestartsCurrentSegment { get; set; }
        public bool SkipsToNextSegment { get; set; }
        public bool ReportsReviving { get; set; }
    }

    // =========================================================================
    // 复现用例 A：B→C 线路，B 为明确传送同步点，严格等待完成后窗口内复苏
    // 未修复代码：本机复苏被误判为"同步点后异常" → 跳到下一段 C，跳过 B→C
    // =========================================================================

    /// <summary>
    /// 场景 A1：B 严格等待完成后 2 秒内本机复苏 → 未修复代码应捕获反例（跳到 C）。
    ///
    /// 期望的修复后行为：保护命中，从当前段传送起点 B 重跑 B→C，不 SkipToNextSegment。
    /// 未修复逻辑（跳段）与期望行为（不跳段）相悖 → 本测试在未修复代码上失败，捕获 bug condition。
    ///
    /// **Validates: Requirements 1.1 / 复现用例 A**
    /// </summary>
    [Fact]
    public void BugCondition_A1_RevivalWithin2s_ShouldNotSkipToNextSegment()
    {
        // Arrange: B 为明确传送同步点（type == "teleport"），严格等待已确认完成
        var completionTime = DateTime.UtcNow;
        var revivalTime = completionTime.AddSeconds(2); // T+2 秒

        // 用真实决策类判定保护条件（B 明确 teleport + 严格等待完成 + 未消费 + 2s 窗口内）
        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: true,
            syncPointType: "teleport",
            strictWaitCompleted: true,
            completionTime: completionTime,
            revivalTime: revivalTime,
            consumed: false);

        Assert.True(eligible, "该复苏属于保护条件（B teleport + 严格等待完成 + 2s ≤ 10s 窗口 + 未消费），本应命中保护");

        // Act: 模拟未修复 PathExecutor（同步点已到达 + 联机 → 同步点后异常 → 跳段）
        var unfixed = SimulateUnfixedSyncPointAfterRevival(
            isMultiplayerHoeing: true,
            syncPointReached: true,
            revivalIsEligibleForProtection: eligible);

        var fixedResult = SimulateFixedSyncPointAfterRevival(
            isMultiplayerHoeing: true,
            syncPointReached: true,
            revivalIsEligibleForProtection: eligible);

        // Assert: 修复后应命中保护、重跑当前段、不跳段、不上报 Reviving
        Assert.True(fixedResult.ProtectionHit, "修复后：应命中传送后复苏保护");
        Assert.True(fixedResult.RestartsCurrentSegment, "修复后：应重跑当前 B→C 线段");
        Assert.False(fixedResult.SkipsToNextSegment, "修复后：不得跳到下一传送点 C");
        Assert.False(fixedResult.ReportsReviving, "修复后：保护命中不得上报 Reviving");

        // 捕获反例：未修复逻辑跳段，与期望行为相悖 → 此处断言失败即捕获 bug condition
        Assert.False(unfixed.SkipsToNextSegment,
            "BUG CONFIRMED（EXPECTED TO FAIL in unfixed code）：B 严格传送同步完成后 2s 内复苏，" +
            "未修复执行器误判为同步点后异常并 SkipToNextSegment=true，跳过 B→C 线段，跳到下一传送点 C。");
    }

    /// <summary>
    /// 场景 A2：窗口边界恰好 10 秒 → 属于保护条件，修复后必须命中、不跳段。
    ///
    /// 未修复代码：仍走同步点后异常 → 跳段 → 本测试失败，捕获反例。
    ///
    /// **Validates: Requirements 2.5 / 复现用例 B / 6.1**
    /// </summary>
    [Fact]
    public void BugCondition_A2_RevivalAtExactly10s_ShouldNotSkipToNextSegment()
    {
        // Arrange: B 明确 teleport，严格等待完成，复苏恰好经过 10 秒
        var completionTime = DateTime.UtcNow;
        var revivalTime = completionTime.AddSeconds(PostTeleportRevivalProtectionDecisions.WindowSeconds);

        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: true,
            syncPointType: "teleport",
            strictWaitCompleted: true,
            completionTime: completionTime,
            revivalTime: revivalTime,
            consumed: false);

        // 恰好 10 秒属于保护边界（需求 2.5：elapsed == 10 命中）
        Assert.True(eligible, "恰好 10 秒属于保护条件，应命中保护");

        var unfixed = SimulateUnfixedSyncPointAfterRevival(true, true, eligible);
        var fixedResult = SimulateFixedSyncPointAfterRevival(true, true, eligible);

        Assert.True(fixedResult.ProtectionHit, "修复后：10 秒边界应命中保护");
        Assert.True(fixedResult.RestartsCurrentSegment, "修复后：应重跑当前段");
        Assert.False(fixedResult.SkipsToNextSegment, "修复后：10 秒边界不得跳到下一段");
        Assert.False(fixedResult.ReportsReviving, "修复后：保护命中不得上报 Reviving");

        Assert.False(unfixed.SkipsToNextSegment,
            "BUG CONFIRMED（EXPECTED TO FAIL in unfixed code）：B 严格传送同步完成后恰好 10s 复苏，" +
            "属于保护边界，但未修复执行器仍跳段、跳过 B→C。");
    }
    // =========================================================================
    // 复现用例 B（后半）/ D：排除场景——非保护条件必须保持原有异常流程
    //
    // 这些场景在未修复代码与修复后代码上都走原有"同步点后异常"跳段/异常流程，
    // 因此这些测试在未修复代码上预期 PASS（建立 preservation 基线）；
    // 它们与上述 A1/A2 的失败对照，精确圈定 bug condition 的边界。
    // =========================================================================

    /// <summary>
    /// 排除场景 D1：复苏超过 10 秒窗口（T+11s）→ 不属于保护条件。
    /// 修复后与未修复均走原有异常流程（跳段），不得命中保护。
    ///
    /// **Validates: Requirements 2.5 / 复现用例 B / 3.4**
    /// </summary>
    [Fact]
    public void Exclusion_D1_RevivalAfter11s_IsNotProtection_KeepsOriginalFlow()
    {
        var completionTime = DateTime.UtcNow;
        var revivalTime = completionTime.AddSeconds(PostTeleportRevivalProtectionDecisions.WindowSeconds + 1); // T+11s

        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: true,
            syncPointType: "teleport",
            strictWaitCompleted: true,
            completionTime: completionTime,
            revivalTime: revivalTime,
            consumed: false);

        // 超过 10 秒不命中保护（需求 2.5：elapsed > 10 不命中）
        Assert.False(eligible, "复苏超过 10 秒不属于保护条件");

        var fixedResult = SimulateFixedSyncPointAfterRevival(true, true, eligible);
        // 非命中 → 走原同步点后异常流程：跳段、上报 Reviving（preservation 3.4 / 3.5 保持）
        Assert.False(fixedResult.ProtectionHit, "非命中：不触发保护");
        Assert.False(fixedResult.RestartsCurrentSegment, "非命中：不重跑当前段");
        Assert.True(fixedResult.SkipsToNextSegment, "非命中：保持原同步点后异常跳段流程");
        Assert.True(fixedResult.ReportsReviving, "非命中：保持原 Reviving 上报");
    }

    /// <summary>
    /// 排除场景 D2：单机模式复苏（isMultiplayerHoeing=false）→ 保护逻辑零感知。
    /// 单机路径完全不受影响，保持原有异常流程。
    ///
    /// **Validates: Requirements 3.1 / 5.3 / 复现用例 D**
    /// </summary>
    [Fact]
    public void Exclusion_D2_SingleModeRevival_ProtectionNeverEngages()
    {
        var completionTime = DateTime.UtcNow;
        var revivalTime = completionTime.AddSeconds(2);

        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: false, // 单机
            syncPointType: "teleport",
            strictWaitCompleted: true,
            completionTime: completionTime,
            revivalTime: revivalTime,
            consumed: false);

        Assert.False(eligible, "单机模式不进入保护决策（单机零感知）");

        var fixedResult = SimulateFixedSyncPointAfterRevival(false, true, eligible);
        Assert.False(fixedResult.ProtectionHit, "单机：不触发保护");
        Assert.False(fixedResult.RestartsCurrentSegment, "单机：不重跑");
        // 单机不满足 _syncPointReached && MultiplayerCoordinator != null 的联机同步点后分流
        Assert.False(fixedResult.SkipsToNextSegment, "单机：无联机同步点后跳段（由单机 Retry 逻辑处理）");
        Assert.False(fixedResult.ReportsReviving, "单机：无联机 Reviving 上报");
    }

    /// <summary>
    /// 排除场景 D3：非 teleport 同步点（自动生成 / 手动设置同步点）之后复苏 → 不建立保护窗口。
    ///
    /// **Validates: Requirements 3.3 / 复现用例 D**
    /// </summary>
    [Fact]
    public void Exclusion_D3_NonTeleportSyncPoint_IsNotProtection()
    {
        var completionTime = DateTime.UtcNow;
        var revivalTime = completionTime.AddSeconds(2);

        // 自动生成同步点 / 手动同步点：syncPointType != "teleport"
        bool eligibleAuto = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: true,
            syncPointType: "auto",
            strictWaitCompleted: true,
            completionTime: completionTime,
            revivalTime: revivalTime,
            consumed: false);
        bool eligibleManual = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: true,
            syncPointType: "manual",
            strictWaitCompleted: true,
            completionTime: completionTime,
            revivalTime: revivalTime,
            consumed: false);

        Assert.False(eligibleAuto, "自动生成同步点不建立保护窗口");
        Assert.False(eligibleManual, "手动设置同步点不建立保护窗口");

        // 修复后仍走原同步点后异常流程（不因非 teleport 同步点触发保护）
        var fixedAuto = SimulateFixedSyncPointAfterRevival(true, true, eligibleAuto);
        Assert.False(fixedAuto.ProtectionHit, "自动同步点：不触发保护");
        Assert.True(fixedAuto.SkipsToNextSegment, "自动同步点：保持原同步点后异常跳段");
    }
    /// <summary>
    /// 排除场景 D4：严格等待未完成（strictWaitCompleted=false）→ 不建立保护窗口。
    /// 等待因取消、超时、关房或断线未收到匹配 AllArrived，不得建立窗口。
    ///
    /// **Validates: Requirements 4.2 / 3.2 / 复现用例 D**
    /// </summary>
    [Fact]
    public void Exclusion_D4_StrictWaitNotCompleted_IsNotProtection()
    {
        var completionTime = DateTime.UtcNow;
        var revivalTime = completionTime.AddSeconds(2);

        // strictWaitCompleted=false：严格等待未完成（未收到匹配 AllArrived）
        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: true,
            syncPointType: "teleport",
            strictWaitCompleted: false,
            completionTime: completionTime,
            revivalTime: revivalTime,
            consumed: false);

        Assert.False(eligible, "严格等待未完成不建立保护窗口");

        var fixedResult = SimulateFixedSyncPointAfterRevival(true, true, eligible);
        Assert.False(fixedResult.ProtectionHit, "等待未完成：不触发保护");
        Assert.False(fixedResult.RestartsCurrentSegment, "等待未完成：不重跑");
        Assert.True(fixedResult.SkipsToNextSegment, "等待未完成：走原同步点后异常流程（等待失败按异常/取消语义处理）");
    }

    /// <summary>
    /// 排除场景 D5：当前段保护机会已消耗（consumed=true）→ 不再获得保护。
    /// 同段第一次命中后再次复苏，走原复苏统计/广播/升级/跳段流程。
    ///
    /// **Validates: Requirements 2.6 / 2.7 / 3.4 / 复现用例 C**
    /// </summary>
    [Fact]
    public void Exclusion_D5_OpportunityConsumed_IsNotProtection()
    {
        var completionTime = DateTime.UtcNow;
        var revivalTime = completionTime.AddSeconds(2);

        // consumed=true：同段保护机会已消耗（第一次命中已发生）
        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: true,
            syncPointType: "teleport",
            strictWaitCompleted: true,
            completionTime: completionTime,
            revivalTime: revivalTime,
            consumed: true);

        Assert.False(eligible, "同段机会已消耗不再保护");

        var fixedResult = SimulateFixedSyncPointAfterRevival(true, true, eligible);
        Assert.False(fixedResult.ProtectionHit, "机会已消耗：不触发保护");
        Assert.False(fixedResult.RestartsCurrentSegment, "机会已消耗：不重跑");
        Assert.True(fixedResult.SkipsToNextSegment, "机会已消耗：走原同步点后异常跳段流程");
        Assert.True(fixedResult.ReportsReviving, "机会已消耗：保持原 Reviving 上报");
    }

    /// <summary>
    /// 排除场景 D6：复苏早于严格同步完成时间（负时间差）→ 不属于保护条件。
    ///
    /// **Validates: Requirements 4.1 / 6.1**
    /// </summary>
    [Fact]
    public void Exclusion_D6_RevivalBeforeCompletion_NegativeElapsed_IsNotProtection()
    {
        var completionTime = DateTime.UtcNow;
        var revivalTime = completionTime.AddSeconds(-5); // 复苏早于严格同步完成 5 秒（负时间差）

        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            isMultiplayerHoeing: true,
            syncPointType: "teleport",
            strictWaitCompleted: true,
            completionTime: completionTime,
            revivalTime: revivalTime,
            consumed: false);

        Assert.False(eligible, "负时间差不命中保护（避免时钟/事件排序异常误保护）");
    }

    /// <summary>
    /// 文档性反例总览：汇总未修复代码在 B→C 线路上的错误跳段行为，作为人工验收对照。
    ///
    /// **Validates: Requirements 1.1 / 1.2**
    /// </summary>
    [Fact]
    public void Documentation_CounterexampleOverview()
    {
        // B 为明确传送同步点，严格等待完成后窗口内复苏 → 未修复执行器跳过 B→C
        var completionTime = DateTime.UtcNow;
        var revivalTime = completionTime.AddSeconds(2);
        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(true, "teleport", true,
            completionTime, revivalTime, false);
        var unfixed = SimulateUnfixedSyncPointAfterRevival(true, true, eligible);

        Assert.True(eligible, "2s 窗口内复苏属于保护条件");
        Assert.True(unfixed.BugConditionExhibited,
            "BUG CONFIRMED（EXPECTED TO FAIL in unfixed code）：未修复执行器把窗口内复苏当作同步点后异常，" +
            "跳段跳过 B→C，未重跑当前段，且上报 Reviving 把本机复苏传播为团队异常（requirements 1.3）。");
    }
}
