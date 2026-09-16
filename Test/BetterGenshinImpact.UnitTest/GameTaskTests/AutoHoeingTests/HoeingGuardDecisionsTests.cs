#nullable enable

using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// HoeingGuardDecisions.ShouldRestart / IsIncompleteRun 新参数
/// coordinatedRestartRequired / unexecutedZeroProvesComplete 的单元测试
/// （hoeing-multiplayer-coordinated-abort-restart）。
/// </summary>
public class HoeingGuardDecisionsTests
{
    // 基准：全部硬条件放行的"干净"输入（coordinatedRestartRequired 由用例单独控制）
    private const bool GuardMode = true;
    private const bool MultiplayerEnabled = true;

    // C-1 协同中止强制标志为 true，但硬条件为 false：守护开关关闭 → 不重开
    [Fact]
    public void ShouldRestart_CoordinatedRestart_GuardModeOff_IsFalse()
    {
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            false, MultiplayerEnabled, "协同中止", 5, 1,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false,
            coordinatedRestartRequired: true));
    }

    // C-2 协同中止强制标志为 true，但单机模式 → 不重开（单机零感知）
    [Fact]
    public void ShouldRestart_CoordinatedRestart_MultiplayerOff_IsFalse()
    {
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            GuardMode, false, "协同中止", 5, 1,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false,
            coordinatedRestartRequired: true));
    }

    // C-3 用户手动取消优先于强制标志 → 不重开
    [Fact]
    public void ShouldRestart_CoordinatedRestart_UserCancelled_IsFalse()
    {
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, "协同中止", 5, 1,
            userCancelled: true, expCapStopTriggered: false, isGuardRestartRun: false,
            coordinatedRestartRequired: true));
    }

    // C-4 经验上限正常停止优先于强制标志 → 不重开
    [Fact]
    public void ShouldRestart_CoordinatedRestart_ExpCapStop_IsFalse()
    {
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, "协同中止", 5, 1,
            userCancelled: false, expCapStopTriggered: true, isGuardRestartRun: false,
            coordinatedRestartRequired: true));
    }

    // C-5 已是守护重开产生的运行 → 不重开（重开只一次，防广播-重开-再中止死循环）
    [Fact]
    public void ShouldRestart_CoordinatedRestart_IsGuardRestartRun_IsFalse()
    {
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, "协同中止", 5, 1,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: true,
            coordinatedRestartRequired: true));
    }

    // C-6 完工单优先于强制标志：正常全部跑完恒不重开（收尾期信号不误重开）
    [Fact]
    public void ShouldRestart_CoordinatedRestart_CompletedNormally_IsFalse()
    {
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, "协同中止", 5, 1,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false,
            completedNormally: true, coordinatedRestartRequired: true));
    }

    // C-7 全部硬条件放行：即使未执行数=0 且 stopReason 为空，强制标志为 true 即重开
    [Fact]
    public void ShouldRestart_CoordinatedRestart_AllConditionsPassed_IsTrue()
    {
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, null, 0, 1,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false,
            coordinatedRestartRequired: true));
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, "", 0, 1,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false,
            coordinatedRestartRequired: true));
    }

    // C-8 多世界关防线 A：unexecuted=0 + stopReason 非空 → 未完成（重开）
    [Fact]
    public void ShouldRestart_UnexecutedZeroNotProof_StopReasonNonEmpty_IsTrue()
    {
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, "成员掉线", 0, 1,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false,
            unexecutedZeroProvesComplete: false));
    }

    // C-9 多世界关防线 A：unexecuted=0 + stopReason 为空 → 未达阈值也不重开
    [Fact]
    public void ShouldRestart_UnexecutedZeroNotProof_StopReasonEmpty_IsFalse()
    {
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, null, 0, 1,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false,
            unexecutedZeroProvesComplete: false));
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, "", 0, 1,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false,
            unexecutedZeroProvesComplete: false));
    }

    // C-10 默认参数回归（不传两个新参）：防线 A 旧行为——unexecuted=0 恒不重开
    [Fact]
    public void ShouldRestart_DefaultParams_UnexecutedZero_IsFalse()
    {
        Assert.False(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, "有异常原因", 0, 1,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false));
    }

    // C-11 默认参数回归：unexecuted >= threshold → 重开
    [Fact]
    public void ShouldRestart_DefaultParams_UnexecutedAtThreshold_IsTrue()
    {
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, null, 3, 3,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false));
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, null, 5, 3,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false));
    }

    // C-12 默认参数回归：stopReason 非空 + unexecuted>0 → 重开
    [Fact]
    public void ShouldRestart_DefaultParams_StopReasonNonEmptyAndUnexecutedPositive_IsTrue()
    {
        Assert.True(HoeingGuardDecisions.ShouldRestart(
            GuardMode, MultiplayerEnabled, "房主掉线", 1, 3,
            userCancelled: false, expCapStopTriggered: false, isGuardRestartRun: false));
    }
}
