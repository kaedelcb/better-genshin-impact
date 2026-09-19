using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// SwitchGuardPolicy（R5.1 §7）验收夹具：静止前置判定纯函数。
/// 优先顺序固定：pending_handoff → pending_recovery → not_quiescent。
/// </summary>
public class SwitchGuardPolicyTests
{
    // SwitchGuardFacts(HasRunningExecution, HasPendingSubmission, HasPendingHandoff, HasRecoveryDuty)

    [Fact]
    public void Quiescent_Allowed()
    {
        var v = SwitchGuardPolicy.Evaluate(new SwitchGuardFacts(false, false, false, false));

        Assert.True(v.Allowed);
        Assert.Null(v.Reason);
    }

    [Fact]
    public void PendingHandoff_Rejected()
    {
        var v = SwitchGuardPolicy.Evaluate(new SwitchGuardFacts(false, false, true, false));

        Assert.False(v.Allowed);
        Assert.Equal("pending_handoff", v.Reason);
    }

    [Fact]
    public void RecoveryDuty_Rejected()
    {
        var v = SwitchGuardPolicy.Evaluate(new SwitchGuardFacts(false, false, false, true));

        Assert.False(v.Allowed);
        Assert.Equal("pending_recovery", v.Reason);
    }

    [Fact]
    public void Running_Or_PendingSubmission_NotQuiescent()
    {
        var running = SwitchGuardPolicy.Evaluate(new SwitchGuardFacts(true, false, false, false));
        Assert.False(running.Allowed);
        Assert.Equal("not_quiescent", running.Reason);

        var pendingSubmission = SwitchGuardPolicy.Evaluate(new SwitchGuardFacts(false, true, false, false));
        Assert.False(pendingSubmission.Allowed);
        Assert.Equal("not_quiescent", pendingSubmission.Reason);
    }

    [Fact]
    public void Priority_PendingHandoff_First()
    {
        // 四项全 true → pending_handoff 最优先
        var all = SwitchGuardPolicy.Evaluate(new SwitchGuardFacts(true, true, true, true));
        Assert.False(all.Allowed);
        Assert.Equal("pending_handoff", all.Reason);

        // 仅 recovery + running → pending_recovery 先于 not_quiescent
        var recoveryAndRunning = SwitchGuardPolicy.Evaluate(new SwitchGuardFacts(true, false, false, true));
        Assert.False(recoveryAndRunning.Allowed);
        Assert.Equal("pending_recovery", recoveryAndRunning.Reason);
    }
}
