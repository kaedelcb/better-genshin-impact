#nullable enable

using System;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using FsCheck;
using FsCheck.Xunit;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// Feature: hoeing-multiplayer-guard-auto-restart 决策层属性测试。
/// 覆盖 HoeingGuardDecisions 的未执行数计算（ComputeUnexecutedCount）、阈值 clamp（ClampThreshold）、
/// 未正常完成判定（IsIncompleteRun）、守护重开判定（ShouldRestart 6 条件短路）。
/// 框架：FsCheck 2.16.6 + FsCheck.Xunit（[Property]）+ xUnit。
/// 对应 design.md §6 的 P-1~P-8。
///
/// 说明：stopReason 为 string?，FsCheck 随机字符串会混入 " "（空白非空）等歧义值，
/// 故统一用 bool 生成器 + MakeStopReason 映射为 null / 非空字符串两个明确语义分支，
/// 空白字符串边界另由 IsIncompleteRun_WhitespaceStopReason_TreatedAsNonEmpty 显式覆盖。
/// </summary>
public class HoeingGuardDecisionsTests
{
    /// <summary>把 bool 映射为语义明确的 stopReason：true → 非空（异常退出），false → null（正常完成）。</summary>
    private static string? MakeStopReason(bool abnormal) => abnormal ? "被踢出联机世界" : null;

    /// <summary>把任意 int 收敛到 [0, 99]，避免 FsCheck 极端值导致减法溢出。</summary>
    private static int Bound(int v) => Math.Abs(v % 100);

    // ========== P-1 ComputeUnexecutedCount：永不为负，且 = max(0, planned - executed) ==========
    [Property(MaxTest = 200)]
    public bool ComputeUnexecuted_NeverNegative_AndMatchesFormula(int planned, int executed)
    {
        var p = Bound(planned);
        var e = Bound(executed);
        var r = HoeingGuardDecisions.ComputeUnexecutedCount(p, e);
        return r >= 0 && r == Math.Max(0, p - e);
    }

    /// <summary>已执行数 ≥ 计划数（正常跑完 / 累加器口径偏移）时未执行数恒为 0，不会误触发。</summary>
    [Property(MaxTest = 200)]
    public bool ComputeUnexecuted_ExecutedGePlanned_IsZero(int planned, int extra)
    {
        var p = Bound(planned);
        var e = p + Bound(extra);
        return HoeingGuardDecisions.ComputeUnexecutedCount(p, e) == 0;
    }

    // ========== P-2 ClampThreshold：下限 1 ==========
    [Property(MaxTest = 200)]
    public bool ClampThreshold_MinOne(int v)
    {
        return HoeingGuardDecisions.ClampThreshold(v) >= 1;
    }

    /// <summary>已 ≥1 的阈值不被改动（clamp 只兜下限，不篡改合法值）。</summary>
    [Property(MaxTest = 200)]
    public bool ClampThreshold_KeepsValidValue(int v)
    {
        var valid = 1 + Bound(v);
        return HoeingGuardDecisions.ClampThreshold(valid) == valid;
    }

    // ========== IsIncompleteRun：异常退出 ∨ 未执行数达阈值（含两道防线）==========
    [Property(MaxTest = 300)]
    public bool IsIncompleteRun_MatchesOrSemantics(bool abnormal, int unexecuted, int threshold)
    {
        var sr = MakeStopReason(abnormal);
        var u = Bound(unexecuted);
        var t = HoeingGuardDecisions.ClampThreshold(threshold);
        // 防线 A：未执行数为 0 时恒 false（hoeing-guard-false-restart-on-normal-close）
        var expected = u == 0 ? false : (abnormal || u >= t);
        return HoeingGuardDecisions.IsIncompleteRun(sr, u, t) == expected;
    }

    /// <summary>防线 B：completedNormally 为真时恒不判未完成（任意 stopReason / 未执行数 / 阈值）。</summary>
    [Property(MaxTest = 300)]
    public bool IsIncompleteRun_CompletedNormally_AlwaysFalse(
        bool abnormal, int unexecuted, int threshold)
    {
        return HoeingGuardDecisions.IsIncompleteRun(
            MakeStopReason(abnormal), Bound(unexecuted),
            HoeingGuardDecisions.ClampThreshold(threshold),
            completedNormally: true) == false;
    }

    /// <summary>防线 A：未执行数为 0 时恒不判未完成（任意 stopReason / 阈值）。</summary>
    [Property(MaxTest = 300)]
    public bool IsIncompleteRun_ZeroUnexecuted_AlwaysFalse(bool abnormal, int threshold)
    {
        return HoeingGuardDecisions.IsIncompleteRun(
            MakeStopReason(abnormal), 0,
            HoeingGuardDecisions.ClampThreshold(threshold)) == false;
    }

    /// <summary>
    /// EB-3：两道防线均不成立且有异常时，必定判未完成（真异常不被漏掉）。
    /// unexecuted 严格 > 0（防线 A 不成立），completedNormally = false（防线 B 不成立）。
    /// </summary>
    [Property(MaxTest = 300)]
    public bool IsIncompleteRun_AbnormalWithPendingRoutes_AlwaysTrue(int unexecuted, int threshold)
    {
        var u = 1 + Bound(unexecuted);   // 严格 > 0，防线 A 不短路
        var t = HoeingGuardDecisions.ClampThreshold(threshold);
        return HoeingGuardDecisions.IsIncompleteRun(
            "房间已关闭: 房主已断开连接", u, t, completedNormally: false) == true;
    }

    /// <summary>正常完成（stopReason 为 null 且未执行数 0）恒不判为未完成——保护"正常锄地不被重开"（P3）。</summary>
    [Property(MaxTest = 200)]
    public bool IsIncompleteRun_NormalCompletion_IsFalse(int threshold)
    {
        var t = HoeingGuardDecisions.ClampThreshold(threshold);
        return HoeingGuardDecisions.IsIncompleteRun(null, 0, t) == false;
    }

    /// <summary>
    /// 边界：空白字符串 " " 不是 IsNullOrEmpty，按"异常退出"处理。
    /// 显式钉住该语义，防止未来有人改成 IsNullOrWhiteSpace 而悄悄改变触发面。
    /// 注：unexecutedCount 用 1（而非 0）绕开防线 A 的短路，单独隔离 stopReason 语义。
    /// （hoeing-guard-false-restart-on-normal-close：防线 A 使 unexecuted==0 时恒 false，
    ///  保留 unexecuted==1 < threshold==3 场景以继续验证原语义）
    /// </summary>
    [Fact]
    public void IsIncompleteRun_WhitespaceStopReason_TreatedAsNonEmpty()
    {
        Assert.True(HoeingGuardDecisions.IsIncompleteRun(" ", 1, 3));
        Assert.False(HoeingGuardDecisions.IsIncompleteRun("", 1, 3));
        Assert.False(HoeingGuardDecisions.IsIncompleteRun(null, 1, 3));
    }

    // ========== 组队失败豁免防线 A（hoeing-multiplayer-party-fail-restart）==========

    /// <summary>
    /// 组队失败（partyFailed=true）：即使未执行线路数为 0（尚未开锄、计划数=0），
    /// 只要 stopReason 非空即判"未正常完成"，豁免防线 A 的短路。
    /// 修复前：组队失败因 unexecuted==0 被防线 A 拦下，异常队员不重开。
    /// </summary>
    [Property(MaxTest = 300)]
    public bool IsIncompleteRun_PartyFailed_ExemptsZeroUnexecuted(int threshold)
    {
        var t = HoeingGuardDecisions.ClampThreshold(threshold);
        return HoeingGuardDecisions.IsIncompleteRun(
            "组队超时", 0, t, completedNormally: false, partyFailed: true) == true;
    }

    /// <summary>组队失败但无异常原因（stopReason 为 null）仍不判未完成（防空标记误重开）。</summary>
    [Property(MaxTest = 200)]
    public bool IsIncompleteRun_PartyFailed_NoReason_IsFalse(int threshold)
    {
        var t = HoeingGuardDecisions.ClampThreshold(threshold);
        return HoeingGuardDecisions.IsIncompleteRun(
            null, 0, t, completedNormally: false, partyFailed: true) == false;
    }

    /// <summary>防线 B 优先于组队失败：到达正常完成点后，即使 partyFailed=true 也不判未完成。</summary>
    [Fact]
    public void IsIncompleteRun_CompletedNormally_OverridesPartyFailed()
    {
        Assert.False(HoeingGuardDecisions.IsIncompleteRun(
            "组队超时", 0, 3, completedNormally: true, partyFailed: true));
    }

    // ========== Bug 复现：正常结束被误判重开（hoeing-guard-false-restart-on-normal-close）==========

    /// <summary>
    /// Bug 复现（BC-1）：全部线路跑完（unexecutedCount==0）后房主正常关房写入 stopReason，
    /// 守护不得判"未正常完成"。修复前因 IsIncompleteRun 的 || 逻辑返回 true 而失败。
    /// </summary>
    [Fact]
    public void IsIncompleteRun_AllRoutesDone_NormalClose_NotIncomplete()
    {
        Assert.False(HoeingGuardDecisions.IsIncompleteRun(
            "房间已关闭: 房主已关闭房间", 0, 3));
    }

    /// <summary>
    /// Bug 复现（BC-2）：到达正常完成点后，即使仍有未执行线路残留（口径偏移）也不得重开。
    /// </summary>
    [Fact]
    public void IsIncompleteRun_CompletedNormally_NotIncomplete()
    {
        Assert.False(HoeingGuardDecisions.IsIncompleteRun(
            "房间已关闭: 房主已关闭房间", 5, 3, completedNormally: true));
    }

    /// <summary>
    /// EB-3 防回归基线：房主中途掉线（未到完成点 + 仍有未执行线路）必须照常重开。
    /// 本用例在修复前后都应通过，用于证明两道防线没有漏掉真异常。
    /// </summary>
    [Fact]
    public void ShouldRestart_HostDisconnectedMidRun_StillRestarts()
    {
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            guardMode: true, multiplayerEnabled: true,
            stopReason: "房间已关闭: 房主已断开连接",
            unexecutedCount: 12, threshold: 3,
            userCancelled: false, expCapStopTriggered: false,
            isGuardRestartRun: false));
    }

    // ========== P-3 手动停止绝不重开（RK2 守护）==========
    [Property(MaxTest = 300)]
    public bool ShouldRestart_UserCancelled_NeverRestart(
        bool guardMode, bool multiplayer, bool abnormal,
        int unexecuted, int threshold, bool expCap, bool isRestartRun)
    {
        return HoeingGuardDecisions.ShouldRestart(
            guardMode, multiplayer, MakeStopReason(abnormal),
            Bound(unexecuted), HoeingGuardDecisions.ClampThreshold(threshold),
            userCancelled: true, expCap, isRestartRun) == false;
    }

    // ========== P-4 经验上限正常停止绝不重开（RK3 守护）==========
    [Property(MaxTest = 300)]
    public bool ShouldRestart_ExpCapStop_NeverRestart(
        bool guardMode, bool multiplayer, bool abnormal,
        int unexecuted, int threshold, bool isRestartRun)
    {
        return HoeingGuardDecisions.ShouldRestart(
            guardMode, multiplayer, MakeStopReason(abnormal),
            Bound(unexecuted), HoeingGuardDecisions.ClampThreshold(threshold),
            userCancelled: false, expCapStopTriggered: true, isRestartRun) == false;
    }

    // ========== P-5 已是守护重开的运行绝不再重开（RK1 无限重开守护）==========
    [Property(MaxTest = 300)]
    public bool ShouldRestart_AlreadyGuardRestart_NeverRestart(
        bool guardMode, bool multiplayer, bool abnormal,
        int unexecuted, int threshold)
    {
        return HoeingGuardDecisions.ShouldRestart(
            guardMode, multiplayer, MakeStopReason(abnormal),
            Bound(unexecuted), HoeingGuardDecisions.ClampThreshold(threshold),
            userCancelled: false, expCapStopTriggered: false,
            isGuardRestartRun: true) == false;
    }

    // ========== P-6 单机绝不重开（P1 单机零感知）==========
    [Property(MaxTest = 300)]
    public bool ShouldRestart_SinglePlayer_NeverRestart(
        bool guardMode, bool abnormal, int unexecuted, int threshold, bool isRestartRun)
    {
        return HoeingGuardDecisions.ShouldRestart(
            guardMode, multiplayerEnabled: false, stopReason: MakeStopReason(abnormal),
            unexecutedCount: Bound(unexecuted),
            threshold: HoeingGuardDecisions.ClampThreshold(threshold),
            userCancelled: false, expCapStopTriggered: false,
            isGuardRestartRun: isRestartRun) == false;
    }

    // ========== P-7 守护关闭绝不重开 ==========
    [Property(MaxTest = 300)]
    public bool ShouldRestart_GuardOff_NeverRestart(
        bool multiplayer, bool abnormal, int unexecuted, int threshold, bool isRestartRun)
    {
        return HoeingGuardDecisions.ShouldRestart(
            guardMode: false, multiplayerEnabled: multiplayer,
            stopReason: MakeStopReason(abnormal),
            unexecutedCount: Bound(unexecuted),
            threshold: HoeingGuardDecisions.ClampThreshold(threshold),
            userCancelled: false, expCapStopTriggered: false,
            isGuardRestartRun: isRestartRun) == false;
    }

    // ========== 全条件放行时，结果完全由 IsIncompleteRun 决定 ==========
    [Property(MaxTest = 300)]
    public bool ShouldRestart_AllGatesOpen_EqualsIsIncompleteRun(
        bool abnormal, int unexecuted, int threshold)
    {
        var sr = MakeStopReason(abnormal);
        var u = Bound(unexecuted);
        var t = HoeingGuardDecisions.ClampThreshold(threshold);
        var actual = HoeingGuardDecisions.ShouldRestart(
            guardMode: true, multiplayerEnabled: true, stopReason: sr,
            unexecutedCount: u, threshold: t,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false);
        return actual == HoeingGuardDecisions.IsIncompleteRun(sr, u, t);
    }

    // ========== P-8 关键路径打点（Happy path / 正常完成 / 各否决条件）==========
    [Fact]
    public void ShouldRestart_HappyPath_AndNormalCompletion()
    {
        // 异常退出（有未执行线路）→ 重开
        // 注：unexecutedCount 用 5（> 0）——真异常场景必然有未执行线路，
        // 若用 0 则防线 A 生效（全跑完了，不该重开）。
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            true, true, "掉线", 5, 3, false, false, false));
        // 未执行数达阈值（stopReason 为 null）→ 重开
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            true, true, null, 3, 3, false, false, false));
        // 正常完成（无异常 + 未执行 0）→ 不重开
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            true, true, null, 0, 3, false, false, false));
        // 未执行数未达阈值 → 不重开
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            true, true, null, 2, 3, false, false, false));
        // 全跑完 + 有 stopReason（正常关房场景）→ 不重开（防线 A）
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            true, true, "房间已关闭: 房主已关闭房间", 0, 3, false, false, false));
    }

    /// <summary>
    /// 逐条否决：从"必重开"的输入出发，每次只翻转一个否决条件，结果都必须变为不重开。
    /// 钉住 6 个条件各自独立生效，防止未来改动漏掉任一短路。
    /// </summary>
    [Fact]
    public void ShouldRestart_EachGateIndependentlyVetoes()
    {
        // 基线：全条件满足 → 重开
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            guardMode: true, multiplayerEnabled: true, stopReason: "掉线",
            unexecutedCount: 5, threshold: 3,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false));

        // 条件1：守护关
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            false, true, "掉线", 5, 3, false, false, false));
        // 单机
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            true, false, "掉线", 5, 3, false, false, false));
        // 条件3：手动停止
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            true, true, "掉线", 5, 3, true, false, false));
        // 条件4：经验上限正常停止
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            true, true, "掉线", 5, 3, false, true, false));
        // 条件5：已是守护重开
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            true, true, "掉线", 5, 3, false, false, true));
        // 条件2：既无异常也未达阈值
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            true, true, null, 0, 3, false, false, false));
    }

    /// <summary>
    /// ShouldRestart 透传 partyFailed：全条件放行 + 组队失败 + 未执行数 0 → 仍重开
    /// （修复核心：第 0 轮 / 轮次中间组队失败也要重开，保证全队重新组队跑完）。
    /// </summary>
    [Fact]
    public void ShouldRestart_PartyFailed_ZeroUnexecuted_StillRestarts()
    {
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            guardMode: true, multiplayerEnabled: true,
            stopReason: "组队超时", unexecutedCount: 0, threshold: 3,
            userCancelled: false, expCapStopTriggered: false,
            isGuardRestartRun: false, partyFailed: true));
    }

    /// <summary>组队失败不绕过硬否决条件：守护关 / 手动停止 / 经验上限 / 已是重开 仍绝不重开。</summary>
    [Property(MaxTest = 300)]
    public bool ShouldRestart_PartyFailed_HardGatesStillVeto(
        bool guardMode, bool userCancelled, bool expCap, bool isRestartRun)
    {
        // 除 guardMode 外，其余硬否决条件任一为真时恒不重开（即便 partyFailed=true 且 stopReason 非空）。
        var expected =
            guardMode
            && !userCancelled
            && !expCap
            && !isRestartRun;
        return HoeingGuardDecisions.ShouldRestart(
            guardMode: guardMode, multiplayerEnabled: true,
            stopReason: "组队超时", unexecutedCount: 0, threshold: 3,
            userCancelled: userCancelled, expCapStopTriggered: expCap,
            isGuardRestartRun: isRestartRun, partyFailed: true) == expected;
    }

    // ========== R5 UpdatePeerBaseline：执行期队友掉线基准更新 ==========

    /// <summary>首帧（baseline=-1）捕获当前人数为基准，不判 below。</summary>
    [Property(MaxTest = 200)]
    public bool UpdatePeerBaseline_FirstFrame_Captures(int cur)
    {
        var c = 1 + Bound(cur); // 有效人数 ≥1
        var (nb, below) = HoeingGuardDecisions.UpdatePeerBaseline(-1, c);
        return nb == c && below == false;
    }

    /// <summary>人数上升（迟到者/列表补齐）抬高基准，不判 below。</summary>
    [Property(MaxTest = 200)]
    public bool UpdatePeerBaseline_Rise_LiftsBaseline(int baseline, int extra)
    {
        var b = 1 + Bound(baseline);
        var c = b + 1 + Bound(extra); // 严格大于基准
        var (nb, below) = HoeingGuardDecisions.UpdatePeerBaseline(b, c);
        return nb == c && below == false;
    }

    /// <summary>人数下降（疑似掉线）基准不变，判 below=true。</summary>
    [Property(MaxTest = 200)]
    public bool UpdatePeerBaseline_Drop_FlagsBelow(int baseline, int drop)
    {
        var b = 2 + Bound(baseline); // 基准 ≥2，留下降空间
        var d = 1 + (Bound(drop) % b); // 1..b
        var c = b - d;                  // 严格小于基准，可能到 0/负——但检测方保证 cur>0 才调；这里仍测函数纯语义
        var (nb, below) = HoeingGuardDecisions.UpdatePeerBaseline(b, c);
        return nb == b && below == (c < b);
    }

    /// <summary>人数持平：基准不变，不判 below。</summary>
    [Property(MaxTest = 200)]
    public bool UpdatePeerBaseline_Equal_NoChange(int baseline)
    {
        var b = 1 + Bound(baseline);
        var (nb, below) = HoeingGuardDecisions.UpdatePeerBaseline(b, b);
        return nb == b && below == false;
    }

    /// <summary>关键路径打点：4 人基准下 3 人触发 below；回到 4 人不触发；迟到到 5 人抬高基准。</summary>
    [Fact]
    public void UpdatePeerBaseline_KeyPaths()
    {
        // 首帧捕获 4 人
        var (b1, below1) = HoeingGuardDecisions.UpdatePeerBaseline(-1, 4);
        Assert.Equal(4, b1);
        Assert.False(below1);
        // 掉到 3 人 → below
        var (b2, below2) = HoeingGuardDecisions.UpdatePeerBaseline(b1, 3);
        Assert.Equal(4, b2);
        Assert.True(below2);
        // 迟到者加入到 5 人 → 抬高基准，不 below
        var (b3, below3) = HoeingGuardDecisions.UpdatePeerBaseline(b1, 5);
        Assert.Equal(5, b3);
        Assert.False(below3);
    }
}
