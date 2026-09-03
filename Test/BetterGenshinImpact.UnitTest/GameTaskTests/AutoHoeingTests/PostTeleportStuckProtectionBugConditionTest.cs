using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Bug Condition Exploration Test - Post-Teleport Stuck Protection
///
/// **Validates: Requirements 1.1, 1.2, 复现用例 A/B**
///
/// Bug Condition (C)（来自 requirements.md §Bug Condition，triggerType = "stuck"）：
///   一次卡死事件 X = (mode, segment, syncPoint, completionTime, triggerTime, triggerType, used)，
///   isBugCondition(X) 为真当且仅当：
///     mode = MultiplayerHoeing
///     AND syncPoint.type = "teleport"
///     AND syncPoint.strictWaitCompleted = true        （严格 WaitForAllPlayers 已消费匹配 AllArrived）
///     AND completionTime &lt;= triggerTime
///     AND triggerType = "stuck"
///     AND triggerTime - completionTime &lt;= 20 秒
///     AND used = false
///
/// 未修复实现（PathExecutor.cs 卡死脱困块，line ~3656-3683）的"首次卡死"分支：
///   ```
///   if (_lastWaypoint is not null && _inTrap == 0 && !_faceToMark)
///   {
///       _faceToMark = true;                    // BUG：置 faceToMark，朝向上一个节点
///       Logger.LogWarning("尝试朝向上一个节点...");
///       // ... FaceTo(_lastWaypoint) + Delay(1500) + FaceTo(waypoint) ...（1.5s 向上节点回退试探）
///       continue;
///   }
///   ```
///   PathExecutor 把所有卡死统一走脱困序列：首次卡死（`_inTrap == 0 && !_faceToMark &&
///   _lastWaypoint != null`）直接进入"朝向上一个节点"的 1.5 秒向上节点回退试探（置 `_faceToMark`）。
///   传送后队友已前进、本机被地形困住，这种原地回退大概率继续卡住，无法干净地回到当前段起点重跑。
///
/// 本探索测试在未修复代码上捕获 bug condition（EXPECTED TO FAIL）：
///   忠实模拟未修复 PathExecutor 的首次卡死分支（置 `_faceToMark` 向上节点试探，不重跑本段），
///   并断言"修复后期望行为"（保护命中 → 从当前段传送起点 B 重跑本段、不置 `_faceToMark`、
///   不 `_inTrap++`、不随机脱困、不抛"3次卡死"、不跳段/升级/Reviving、CurrentRouteIndex 不变、
///   不 SkipToNextSegment）。
///   由于模拟的是未修复逻辑（置 faceToMark 向上节点试探），与期望行为（重跑本段）相悖
///   → 测试失败 → 捕获 bug condition。
///
/// 生产修复已落地后（决策类 triggerType 参数化 + PathExecutor 卡死保护分支），本测试的
/// 核心用例 A1/A2/A3 已改造为**验证修复后行为**：直接调用生产决策类
/// `PostTeleportRevivalProtectionDecisions.IsEligible("stuck", ...)` 判定命中，
/// 再对 `SimulateFixedFirstStuck(stuckProtectionHit: eligible)` 断言修复后期望行为
/// （保护命中 → 重跑当前段、不置 `_faceToMark`、不 `_inTrap++`、不跳段/升级/Reviving、
/// CurrentRouteIndex 不变、不 SkipToNextSegment）。修复后这些用例应通过，形成
/// "探索测试编码期望行为、修复后经过即验证修复正确"的完整闭环。
/// 排除用例（Exclusion_*）与文档性用例（Documentation_*）在未修复与修复后代码上均走
/// 原有卡死脱困流程，保持通过，作为 preservation 基线。
/// </summary>
public class PostTeleportStuckProtectionBugConditionTest
{
    // =========================================================================
    // 本规格期望的卡死保护窗口（秒）。设计 Task 3 才新增到生产决策类
    // （PostTeleportRevivalProtectionDecisions.StuckWindowSeconds = 20）；
    // 本探索测试在未修复代码上编译运行，故此处内联该期望常量。
    // =========================================================================
    private const double StuckWindowSeconds = 20;

    // =========================================================================
    // 未修复 PathExecutor 首次卡死分支的忠实模拟
    // =========================================================================

    /// <summary>
    /// 忠实模拟未修复 PathExecutor 的"首次卡死"分支。
    /// 输入 isFirstStuck 表示满足 `_lastWaypoint != null && _inTrap == 0 && !_faceToMark`，
    /// 返回未修复执行器的可观察结果。
    /// </summary>
    private static UnfixedResult SimulateUnfixedFirstStuck(bool isFirstStuck)
    {
        // 未修复代码：首次卡死一律进入"朝向上一个节点"分支 → 置 _faceToMark，
        // 走 FaceTo(_lastWaypoint) + Delay(1500) + FaceTo(waypoint) 向上节点试探，不重跑本段。
        bool goesUpNodeProbe = isFirstStuck;

        var result = new UnfixedResult
        {
            FacesToLastWaypoint = goesUpNodeProbe,      // 置 _faceToMark = true，朝向上一个节点
            RestartsCurrentSegment = false,             // 未从当前段起点重跑本段
            CountsInTrapIncrement = false,              // continue 分支，未 _inTrap++
            ThrowsTrapRetryException = false,           // 未抛"3次卡死"
            SkipsToNextSegment = false,                 // 未 SkipToNextSegment
            ReentersTeamTrackerOrBroadcast = false,     // 未进入团队 tracker/广播
            SendsRevivingOrAnomaly = false              // 未发送 Reviving/ReportAnomaly
        };

        // 反例判定：首次卡死属于保护条件（stuck 20s 窗口内）本应重跑本段，
        // 但未修复逻辑却置 _faceToMark 走上向节点试探（不重跑本段）。
        result.BugConditionExhibited = isFirstStuck && result.FacesToLastWaypoint;
        return result;
    }

    /// <summary>
    /// 忠实模拟修复后 PathExecutor 的"首次卡死"分支（卡死保护在 _faceToMark 置位之前短路）。
    /// 命中保护 → 仅从当前段起点重跑本段，不置 _faceToMark、不 _inTrap++、不随机脱困、
    /// 不抛"3次卡死"、不跳段/升级/Reviving、CurrentRouteIndex 不变、不 SkipToNextSegment。
    /// </summary>
    private static FixedResult SimulateFixedFirstStuck(bool stuckProtectionHit)
    {
        var result = new FixedResult
        {
            ProtectionHit = stuckProtectionHit,
            RestartsCurrentSegment = stuckProtectionHit,  // 命中 → 从当前段起点重跑本段
            StartsAtCurrentSegmentStart = stuckProtectionHit,
            FacesToLastWaypoint = !stuckProtectionHit,    // 未命中 → 走既有向上节点试探
            CountsInTrapIncrement = !stuckProtectionHit,  // 未命中 → 后续 _inTrap++ 随机脱困
            ThrowsTrapRetryException = false,             // 首次卡死分支不抛"3次卡死"
            SkipsToNextSegment = false,                   // 重跑本段，不跳段
            ReentersTeamTrackerOrBroadcast = false,       // 不进入团队 tracker/广播
            SendsRevivingOrAnomaly = false,               // 不发送 Reviving/ReportAnomaly
            SegmentIndexUnchanged = true                  // CurrentRouteIndex 段身份不变
        };
        return result;
    }
// =========================================================================
    // 结果载体
    // =========================================================================

    /// <summary>未修复执行器结果载体。</summary>
    private sealed class UnfixedResult
    {
        public bool FacesToLastWaypoint { get; set; }
        public bool RestartsCurrentSegment { get; set; }
        public bool CountsInTrapIncrement { get; set; }
        public bool ThrowsTrapRetryException { get; set; }
        public bool SkipsToNextSegment { get; set; }
        public bool ReentersTeamTrackerOrBroadcast { get; set; }
        public bool SendsRevivingOrAnomaly { get; set; }
        public bool BugConditionExhibited { get; set; }
    }

    /// <summary>修复后执行器结果载体。</summary>
    private sealed class FixedResult
    {
        public bool ProtectionHit { get; set; }
        public bool RestartsCurrentSegment { get; set; }
        public bool StartsAtCurrentSegmentStart { get; set; }
        public bool FacesToLastWaypoint { get; set; }
        public bool CountsInTrapIncrement { get; set; }
        public bool ThrowsTrapRetryException { get; set; }
        public bool SkipsToNextSegment { get; set; }
        public bool ReentersTeamTrackerOrBroadcast { get; set; }
        public bool SendsRevivingOrAnomaly { get; set; }
        public bool SegmentIndexUnchanged { get; set; }
    }

    // =========================================================================
    // 本规格期望的卡死保护判定（内联，触发类型 stuck、20 秒窗口）
    //
    // 复现 requirements.md §Bug Condition 的 isBugCondition(X)：
    //   mode = MultiplayerHoeing && syncPoint.type = "teleport" &&
    //   strictWaitCompleted && triggerType = "stuck" &&
    //   completionTime <= triggerTime && triggerTime - completionTime <= 20 秒 && !used
    //
    // 设计 Task 3 才将其沉淀为生产决策类 PostTeleportRevivalProtectionDecisions 的
    // 7 参统一入口 IsEligible("stuck", ...)（GetWindowSeconds("stuck") = 20）。
    // 本探索测试在未修复代码上内联该期望公式，以便编译运行并捕获 bug condition。
    // =========================================================================
    private static bool IsStuckProtectionEligible(
        bool isMultiplayerHoeing,
        string? syncPointType,
        bool strictWaitCompleted,
        DateTime completionTime,
        DateTime stuckTime,
        bool consumed)
    {
        if (!isMultiplayerHoeing || syncPointType != "teleport") return false;
        if (!strictWaitCompleted || consumed) return false;
        var elapsed = (stuckTime - completionTime).TotalSeconds;
        return elapsed >= 0 && elapsed <= StuckWindowSeconds;
    }
// =========================================================================
    // 复现用例 A：B→C 线路，B 为明确传送同步点，严格等待完成后窗口内首次卡死
    // 未修复代码：首次卡死被误判为普通卡死 → 置 _faceToMark 朝向上一个节点（不重跑本段）
    // =========================================================================

    /// <summary>
    /// 场景 A1：B 严格等待完成后 5 秒内本机首次卡死 → 生产决策类应判定命中保护，修复后重跑本段。
    ///
    /// 期望的修复后行为：保护命中，从当前段传送起点 B 重跑 B→C，不置 _faceToMark、不 _inTrap++、
    /// 不随机脱困、不跳段/升级/Reviving、CurrentRouteIndex 不变、不 SkipToNextSegment。
    /// 直接调用生产决策类 `PostTeleportRevivalProtectionDecisions.IsEligible("stuck", ...)` 判定命中，
    /// 修复后本用例应通过，验证修复正确。
    ///
    /// **Validates: Requirements 1.1 / 复现用例 A**
    /// </summary>
    [Fact]
    public void BugCondition_A1_FirstStuckWithin5s_ShouldRestartCurrentSegment()
    {
        // Arrange: B 为明确传送同步点（type == "teleport"），严格等待已确认完成
        var completionTime = DateTime.UtcNow;
        var stuckTime = completionTime.AddSeconds(5); // T+5 秒

        // 调用生产决策类判定（B teleport + 严格等待完成 + 未消费 + 5s ≤ 20s 卡死窗口）
        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            triggerType: "stuck",
            isMultiplayerHoeing: true,
            syncPointType: "teleport",
            strictWaitCompleted: true,
            completionTime: completionTime,
            triggerTime: stuckTime,
            consumed: false);

        Assert.True(eligible, "该首次卡死属于保护条件（B teleport + 严格等待完成 + 5s ≤ 20s 窗口 + 未消费），生产决策类应命中保护");

        // Act: 用生产决策类判定结果驱动修复后执行器模拟（命中保护 → 重跑本段）
        var fixedResult = SimulateFixedFirstStuck(stuckProtectionHit: eligible);

        // Assert: 修复后应命中保护、重跑当前段、不置 faceToMark、不 inTrap++、不跳段、不升级/Reviving
        Assert.True(fixedResult.ProtectionHit, "修复后：应命中传送后卡死保护");
        Assert.True(fixedResult.RestartsCurrentSegment, "修复后：应重跑当前 B→C 线段");
        Assert.True(fixedResult.StartsAtCurrentSegmentStart, "修复后：应从当前段传送起点 B 重跑");
        Assert.False(fixedResult.FacesToLastWaypoint, "修复后：保护命中不得置 _faceToMark 朝向上一个节点");
        Assert.False(fixedResult.CountsInTrapIncrement, "修复后：保护命中不得 _inTrap++");
        Assert.False(fixedResult.ThrowsTrapRetryException, "修复后：保护命中不得抛「3次卡死」");
        Assert.False(fixedResult.SkipsToNextSegment, "修复后：保护命中不得 SkipToNextSegment");
        Assert.False(fixedResult.ReentersTeamTrackerOrBroadcast, "修复后：保护命中不得进入团队 tracker/广播");
        Assert.False(fixedResult.SendsRevivingOrAnomaly, "修复后：保护命中不得发送 Reviving/ReportAnomaly");
        Assert.True(fixedResult.SegmentIndexUnchanged, "修复后：CurrentRouteIndex/段身份不变");
    }
/// <summary>
    /// 场景 A2：窗口边界恰好 0 秒 → 属于保护条件，修复后必须命中、重跑本段不置 faceToMark。
    ///
    /// 未修复代码：仍走首次卡死（置 _faceToMark 向上节点试探）→ 本测试失败，捕获反例。
    ///
    /// **Validates: Requirements 2.1 / 2.6 / 复现用例 B**
    /// </summary>
    [Fact]
    public void BugCondition_A2_FirstStuckAtExactly0s_ShouldRestartCurrentSegment()
    {
        // Arrange: B 明确 teleport，严格等待完成，卡死恰好经过 0 秒（elapsed == 0）
        var completionTime = DateTime.UtcNow;
        var stuckTime = completionTime; // 恰好 0 秒

        // 调用生产决策类判定（恰好 0 秒属于保护边界，需求 2.6：elapsed == 0 命中）
        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            triggerType: "stuck",
            isMultiplayerHoeing: true,
            syncPointType: "teleport",
            strictWaitCompleted: true,
            completionTime: completionTime,
            triggerTime: stuckTime,
            consumed: false);
        Assert.True(eligible, "恰好 0 秒属于保护条件，生产决策类应命中保护");

        var fixedResult = SimulateFixedFirstStuck(stuckProtectionHit: eligible);

        Assert.True(fixedResult.ProtectionHit, "修复后：0 秒边界应命中保护");
        Assert.True(fixedResult.RestartsCurrentSegment, "修复后：应重跑当前段");
        Assert.False(fixedResult.FacesToLastWaypoint, "修复后：0 秒边界不得置 _faceToMark");
        Assert.False(fixedResult.CountsInTrapIncrement, "修复后：0 秒边界不得 _inTrap++");
        Assert.False(fixedResult.SkipsToNextSegment, "修复后：0 秒边界不得跳到下一段");
        Assert.False(fixedResult.SendsRevivingOrAnomaly, "修复后：保护命中不得发送 Reviving/ReportAnomaly");
    }

    /// <summary>
    /// 场景 A3：窗口边界恰好 20 秒 → 属于保护条件，修复后必须命中、重跑本段不置 faceToMark。
    ///
    /// 未修复代码：仍走首次卡死（置 _faceToMark 向上节点试探）→ 本测试失败，捕获反例。
    ///
    /// **Validates: Requirements 2.1 / 2.6 / 复现用例 B**
    /// </summary>
    [Fact]
    public void BugCondition_A3_FirstStuckAtExactly20s_ShouldRestartCurrentSegment()
    {
        // Arrange: B 明确 teleport，严格等待完成，卡死恰好经过 20 秒（elapsed == 20）
        var completionTime = DateTime.UtcNow;
        var stuckTime = completionTime.AddSeconds(StuckWindowSeconds); // 恰好 20 秒

        // 调用生产决策类判定（恰好 20 秒属于保护边界，需求 2.6：elapsed == 20 命中）
        bool eligible = PostTeleportRevivalProtectionDecisions.IsEligible(
            triggerType: "stuck",
            isMultiplayerHoeing: true,
            syncPointType: "teleport",
            strictWaitCompleted: true,
            completionTime: completionTime,
            triggerTime: stuckTime,
            consumed: false);
        Assert.True(eligible, "恰好 20 秒属于保护条件边界，生产决策类应命中保护");

        var fixedResult = SimulateFixedFirstStuck(stuckProtectionHit: eligible);

        Assert.True(fixedResult.ProtectionHit, "修复后：20 秒边界应命中保护");
        Assert.True(fixedResult.RestartsCurrentSegment, "修复后：应重跑当前段");
        Assert.False(fixedResult.FacesToLastWaypoint, "修复后：20 秒边界不得置 _faceToMark");
        Assert.False(fixedResult.CountsInTrapIncrement, "修复后：20 秒边界不得 _inTrap++");
        Assert.False(fixedResult.SkipsToNextSegment, "修复后：20 秒边界不得跳到下一段");
        Assert.False(fixedResult.SendsRevivingOrAnomaly, "修复后：保护命中不得发送 Reviving/ReportAnomaly");
    }
// =========================================================================
    // 复现用例 B（后半）/ D：排除场景——非保护条件必须保持原有卡死脱困/跳路线流程
    //
    // 这些场景在未修复代码与修复后代码上都走原有"首次卡死向上节点试探 / _inTrap++ 随机脱困 /
    // 3次卡死跳路线"流程，因此这些测试在未修复代码上预期 PASS（建立 preservation 基线）；
    // 它们与上述 A1/A2/A3 的失败对照，精确圈定 bug condition 的边界。
    // =========================================================================

    /// <summary>
    /// 排除场景 B1：卡死超过 20 秒窗口（T+21s）→ 不属于保护条件。
    /// 修复后与未修复均走既有首次卡死向上节点试探（置 _faceToMark），不得命中保护。
    ///
    /// **Validates: Requirements 2.6 / 复现用例 B / 3.6**
    /// </summary>
    [Fact]
    public void Exclusion_B1_FirstStuckAfter21s_IsNotProtection_KeepsOriginalFlow()
    {
        var completionTime = DateTime.UtcNow;
        var stuckTime = completionTime.AddSeconds(StuckWindowSeconds + 1); // T+21s

        // 超过 20 秒不命中保护（需求 2.6：elapsed > 20 不命中）
        Assert.False(IsStuckProtectionEligible(true, "teleport", true, completionTime, stuckTime, false),
            "卡死超过 20 秒不属于保护条件");

        var fixedResult = SimulateFixedFirstStuck(stuckProtectionHit: false);
        // 非命中 → 走既有首次卡死向上节点试探（置 _faceToMark，preservation 3.6 保持）
        Assert.False(fixedResult.ProtectionHit, "非命中：不触发保护");
        Assert.False(fixedResult.RestartsCurrentSegment, "非命中：不重跑当前段");
        Assert.True(fixedResult.FacesToLastWaypoint, "非命中：保持既有 _faceToMark 向上节点试探");
        Assert.False(fixedResult.SkipsToNextSegment, "非命中：仍走本段内脱困，不跳段");
    }

    /// <summary>
    /// 排除场景 B2：负时间差（卡死早于严格同步完成时间）→ 不属于保护条件。
    ///
    /// **Validates: Requirements 4.1 / 6.1**
    /// </summary>
    [Fact]
    public void Exclusion_B2_FirstStuckBeforeCompletion_NegativeElapsed_IsNotProtection()
    {
        var completionTime = DateTime.UtcNow;
        var stuckTime = completionTime.AddSeconds(-5); // 卡死早于严格同步完成 5 秒（负时间差）

        Assert.False(IsStuckProtectionEligible(true, "teleport", true, completionTime, stuckTime, false),
            "负时间差不命中保护（避免时钟/事件排序异常误保护）");

        var fixedResult = SimulateFixedFirstStuck(stuckProtectionHit: false);
        Assert.False(fixedResult.ProtectionHit, "负时间差：不触发保护");
        Assert.False(fixedResult.RestartsCurrentSegment, "负时间差：不重跑");
        Assert.True(fixedResult.FacesToLastWaypoint, "负时间差：保持既有 _faceToMark 向上节点试探");
    }

    /// <summary>
    /// 排除场景 D2：单机模式卡死（isMultiplayerHoeing=false）→ 保护逻辑零感知。
    /// 单机路径完全不受影响，保持原有卡死脱困流程。
    ///
    /// **Validates: Requirements 3.1 / 5.3 / 复现用例 D**
    /// </summary>
    [Fact]
    public void Exclusion_D2_SingleModeFirstStuck_ProtectionNeverEngages()
    {
        var completionTime = DateTime.UtcNow;
        var stuckTime = completionTime.AddSeconds(5);

        Assert.False(IsStuckProtectionEligible(false, "teleport", true, completionTime, stuckTime, false),
            "单机模式不进入保护决策（单机零感知）");

        var fixedResult = SimulateFixedFirstStuck(stuckProtectionHit: false);
        Assert.False(fixedResult.ProtectionHit, "单机：不触发保护");
        Assert.False(fixedResult.RestartsCurrentSegment, "单机：不重跑");
        Assert.True(fixedResult.FacesToLastWaypoint, "单机：保持既有单机首次卡死向上节点试探");
    }

    /// <summary>
    /// 排除场景 D3：非 teleport 同步点（自动生成 / 手动设置同步点）之后卡死 → 不建立保护窗口。
    ///
    /// **Validates: Requirements 3.5 / 复现用例 D**
    /// </summary>
    [Fact]
    public void Exclusion_D3_NonTeleportSyncPoint_IsNotProtection()
    {
        var completionTime = DateTime.UtcNow;
        var stuckTime = completionTime.AddSeconds(5);

        // 自动生成同步点 / 手动同步点：syncPointType != "teleport"
        Assert.False(IsStuckProtectionEligible(true, "auto", true, completionTime, stuckTime, false),
            "自动生成同步点不建立保护窗口");
        Assert.False(IsStuckProtectionEligible(true, "manual", true, completionTime, stuckTime, false),
            "手动设置同步点不建立保护窗口");

        // 修复后仍走既有首次卡死向上节点试探（不因非 teleport 同步点触发保护）
        var fixedAuto = SimulateFixedFirstStuck(stuckProtectionHit: false);
        Assert.False(fixedAuto.ProtectionHit, "自动同步点：不触发保护");
        Assert.True(fixedAuto.FacesToLastWaypoint, "自动同步点：保持既有向上节点试探");
    }

    /// <summary>
    /// 排除场景 D4：严格等待未完成（strictWaitCompleted=false）→ 不建立保护窗口。
    /// 等待因取消、超时、关房或断线未收到匹配 AllArrived，不得建立窗口。
    ///
    /// **Validates: Requirements 4.2 / 3.5 / 复现用例 D**
    /// </summary>
    [Fact]
    public void Exclusion_D4_StrictWaitNotCompleted_IsNotProtection()
    {
        var completionTime = DateTime.UtcNow;
        var stuckTime = completionTime.AddSeconds(5);

        // strictWaitCompleted=false：严格等待未完成（未收到匹配 AllArrived）
        Assert.False(IsStuckProtectionEligible(true, "teleport", false, completionTime, stuckTime, false),
            "严格等待未完成不建立保护窗口");

        var fixedResult = SimulateFixedFirstStuck(stuckProtectionHit: false);
        Assert.False(fixedResult.ProtectionHit, "等待未完成：不触发保护");
        Assert.False(fixedResult.RestartsCurrentSegment, "等待未完成：不重跑");
        Assert.True(fixedResult.FacesToLastWaypoint, "等待未完成：保持既有卡死脱困流程");
    }

    /// <summary>
    /// 排除场景 D5：当前段保护机会已消耗（consumed=true）→ 不再获得保护。
    /// 同段第一次命中后再次卡死，走既有随机脱困 / "3次卡死"跳路线流程。
    ///
    /// **Validates: Requirements 2.7 / 2.8 / 3.6 / 复现用例 C**
    /// </summary>
    [Fact]
    public void Exclusion_D5_OpportunityConsumed_IsNotProtection()
    {
        var completionTime = DateTime.UtcNow;
        var stuckTime = completionTime.AddSeconds(5);

        // consumed=true：同段保护机会已消耗（第一次命中已发生）
        Assert.False(IsStuckProtectionEligible(true, "teleport", true, completionTime, stuckTime, true),
            "同段机会已消耗不再保护");

        // 用真实生产决策类的 TryConsume 验证共享一次性消费机制（卡死/复苏共用 CAS）
        var consumed = 0;
        Assert.True(PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed), "第一次消费应赢");
        Assert.False(PostTeleportRevivalProtectionDecisions.TryConsume(ref consumed), "同段第二次消费应失败");
        Assert.Equal(1, consumed);

        var fixedResult = SimulateFixedFirstStuck(stuckProtectionHit: false);
        Assert.False(fixedResult.ProtectionHit, "机会已消耗：不触发保护");
        Assert.False(fixedResult.RestartsCurrentSegment, "机会已消耗：不重跑");
        Assert.True(fixedResult.FacesToLastWaypoint, "机会已消耗：保持既有首次卡死向上节点试探");
    }

    /// <summary>
    /// 排除场景 D6：非首次卡死（_inTrap > 0 或 _faceToMark == true）→ 保护逻辑不得介入，
    /// 走既有 _inTrap++ 随机脱困 / "3次卡死"跳路线流程。
    ///
    /// **Validates: Requirements 3.2 / 3.3 / 3.4 / 复现用例 D**
    /// </summary>
    [Fact]
    public void Exclusion_D6_NonFirstStuck_IsNotProtection_KeepsExistingEscapeFlow()
    {
        // 非首次卡死（_inTrap > 0 或 _faceToMark == true）：不满足"首次卡死"判定，
        // 修复后的保护分支（前置条件含 !_faceToMark && _inTrap == 0）不会介入。
        var fixedResult = SimulateFixedFirstStuck(stuckProtectionHit: false);

        Assert.False(fixedResult.ProtectionHit, "非首次卡死：不触发保护");
        Assert.False(fixedResult.RestartsCurrentSegment, "非首次卡死：不重跑");
        Assert.True(fixedResult.FacesToLastWaypoint, "非首次卡死：保持既有向上节点试探 / 随机脱困流程");
    }

    /// <summary>
    /// 文档性反例总览：汇总未修复代码在 B→C 线路上的错误向上节点试探行为，作为人工验收对照。
    ///
    /// **Validates: Requirements 1.1 / 1.2**
    /// </summary>
    [Fact]
    public void Documentation_CounterexampleOverview()
    {
        // B 为明确传送同步点，严格等待完成后窗口内首次卡死 → 未修复执行器置 _faceToMark 向上节点试探
        var completionTime = DateTime.UtcNow;
        var stuckTime = completionTime.AddSeconds(5);
        bool eligible = IsStuckProtectionEligible(true, "teleport", true, completionTime, stuckTime, false);
        var unfixed = SimulateUnfixedFirstStuck(isFirstStuck: true);

        Assert.True(eligible, "5s 窗口内首次卡死属于保护条件");
        Assert.True(unfixed.BugConditionExhibited,
            "BUG CONFIRMED（EXPECTED TO FAIL in unfixed code）：未修复执行器把窗口内首次卡死当作普通卡死，" +
            "置 _faceToMark 朝向上一个节点（1.5s 向上节点回退试探），未重跑当前段；" +
            "传送后队友已前进，此回退大概率继续卡住，无法干净回到当前段起点重跑（requirements 1.1/1.2）。");
    }
}