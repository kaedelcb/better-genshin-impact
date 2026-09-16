using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 路线边界锚点纯决策函数测试（route-anchor · 阶段 1）。
///
/// 本阶段不接入任何生产调用路径，只用测试锁定状态机语义，
/// 对应正文第 12 节要求：只新增模型、协议和测试，不接客户端路线循环。
///
/// 覆盖重点（正文第 11.1 节）：
///   · 身份与代际隔离（会话/世界/计划/序号）
///   · 边界报告校验（锚点、代际、参与者、重复、路线索引、终态）
///   · 缺席分类（心跳只判存活、宽限由有效活动推进、战斗/复苏各自预算）
///   · 放行裁决（未全员 Ready 不放行、Pull 失败停止、绝对截止停止、缺员策略）
///   · Pull 命令幂等与在途不重复下发
/// </summary>
public class RouteAnchorDecisionsTests
{
    private static readonly DateTime Now = new(2026, 9, 16, 12, 0, 0, DateTimeKind.Utc);
    private static readonly RouteAnchorDecisions.Budgets B = RouteAnchorDecisions.Budgets.Default;

    private static readonly string[] Participants = ["uid-A", "uid-B", "uid-C", "uid-D"];

    private static Dictionary<string, RouteAnchorMemberState> States(params (string Uid, RouteAnchorMemberState State)[] items)
        => items.ToDictionary(i => i.Uid, i => i.State, StringComparer.Ordinal);

    // =========================================================================
    // 身份与代际
    // =========================================================================

    [Fact]
    public void BuildAnchorId_ContainsAllIdentityParts()
    {
        var id = RouteAnchorDecisions.BuildAnchorId("S1", 2, "plan-8", 12, 3);

        Assert.Equal("route-boundary:S1:2:plan-8:12:3", id);
        Assert.Contains("S1", id);
        Assert.Contains("plan-8", id);
        Assert.Contains(":12:", id);
    }

    [Fact]
    public void BuildAnchorId_DifferentSessionOrEpochOrPlan_ProducesDifferentIds()
    {
        var baseline = RouteAnchorDecisions.BuildAnchorId("S1", 2, "plan-8", 12, 3);

        Assert.NotEqual(baseline, RouteAnchorDecisions.BuildAnchorId("S2", 2, "plan-8", 12, 3));
        Assert.NotEqual(baseline, RouteAnchorDecisions.BuildAnchorId("S1", 3, "plan-8", 12, 3));
        Assert.NotEqual(baseline, RouteAnchorDecisions.BuildAnchorId("S1", 2, "plan-9", 12, 3));
        Assert.NotEqual(baseline, RouteAnchorDecisions.BuildAnchorId("S1", 2, "plan-8", 13, 3));
        Assert.NotEqual(baseline, RouteAnchorDecisions.BuildAnchorId("S1", 2, "plan-8", 12, 4));
    }

    [Fact]
    public void IsSameAnchor_EmptyOrNullIds_AreNeverSame()
    {
        Assert.False(RouteAnchorDecisions.IsSameAnchor(null, "a"));
        Assert.False(RouteAnchorDecisions.IsSameAnchor("a", null));
        Assert.False(RouteAnchorDecisions.IsSameAnchor("", ""));
        Assert.True(RouteAnchorDecisions.IsSameAnchor("a", "a"));
        Assert.False(RouteAnchorDecisions.IsSameAnchor("a", "b"));
    }

    [Fact]
    public void IsStaleGeneration_DetectsSessionEpochPlanMismatch()
    {
        Assert.False(RouteAnchorDecisions.IsStaleGeneration("S1", 2, "p1", "S1", 2, "p1"));
        Assert.True(RouteAnchorDecisions.IsStaleGeneration("S0", 2, "p1", "S1", 2, "p1"));
        Assert.True(RouteAnchorDecisions.IsStaleGeneration("S1", 1, "p1", "S1", 2, "p1"));
        Assert.True(RouteAnchorDecisions.IsStaleGeneration("S1", 2, "p0", "S1", 2, "p1"));
    }

    // =========================================================================
    // 边界报告校验
    // =========================================================================

    [Fact]
    public void ValidateReport_AcceptsValidReport()
    {
        var reason = RouteAnchorDecisions.ValidateReport(
            "A1", "A1", "S1", 2, "p1", "S1", 2, "p1",
            reportedCompletedRouteIndex: 12, expectedCompletedRouteIndex: 12,
            playerUid: "uid-A", participants: Participants, alreadyReported: false,
            phase: RouteAnchorPhase.Waiting);

        Assert.Equal(RouteAnchorDecisions.ReportRejection.None, reason);
    }

    [Fact]
    public void ValidateReport_RejectsWrongAnchor()
        => Assert.Equal(
            RouteAnchorDecisions.ReportRejection.NotSameAnchor,
            RouteAnchorDecisions.ValidateReport("A0", "A1", "S1", 2, "p1", "S1", 2, "p1",
                12, 12, "uid-A", Participants, false, RouteAnchorPhase.Waiting));

    [Fact]
    public void ValidateReport_RejectsStaleGeneration()
        => Assert.Equal(
            RouteAnchorDecisions.ReportRejection.StaleGeneration,
            RouteAnchorDecisions.ValidateReport("A1", "A1", "S0", 2, "p1", "S1", 2, "p1",
                12, 12, "uid-A", Participants, false, RouteAnchorPhase.Waiting));

    [Fact]
    public void ValidateReport_RejectsNonParticipant()
        => Assert.Equal(
            RouteAnchorDecisions.ReportRejection.NotParticipant,
            RouteAnchorDecisions.ValidateReport("A1", "A1", "S1", 2, "p1", "S1", 2, "p1",
                12, 12, "uid-X", Participants, false, RouteAnchorPhase.Waiting));

    [Fact]
    public void ValidateReport_RejectsDuplicate()
        => Assert.Equal(
            RouteAnchorDecisions.ReportRejection.Duplicate,
            RouteAnchorDecisions.ValidateReport("A1", "A1", "S1", 2, "p1", "S1", 2, "p1",
                12, 12, "uid-A", Participants, alreadyReported: true, phase: RouteAnchorPhase.Waiting));

    [Fact]
    public void ValidateReport_RejectsClientSelfReportedWrongRouteIndex()
    {
        // 客户端自报 13 想把自己推进到下一条路线 → 必须拒绝（服务端权威）
        Assert.Equal(
            RouteAnchorDecisions.ReportRejection.WrongRouteIndex,
            RouteAnchorDecisions.ValidateReport("A1", "A1", "S1", 2, "p1", "S1", 2, "p1",
                reportedCompletedRouteIndex: 13, expectedCompletedRouteIndex: 12,
                "uid-A", Participants, false, RouteAnchorPhase.Waiting));

        // 倒退提交同样拒绝
        Assert.Equal(
            RouteAnchorDecisions.ReportRejection.WrongRouteIndex,
            RouteAnchorDecisions.ValidateReport("A1", "A1", "S1", 2, "p1", "S1", 2, "p1",
                reportedCompletedRouteIndex: 11, expectedCompletedRouteIndex: 12,
                "uid-A", Participants, false, RouteAnchorPhase.Waiting));
    }

    [Theory]
    [InlineData(RouteAnchorPhase.Released)]
    [InlineData(RouteAnchorPhase.Stopped)]
    public void ValidateReport_RejectsAfterTerminalPhase(RouteAnchorPhase phase)
        => Assert.Equal(
            RouteAnchorDecisions.ReportRejection.AlreadyTerminal,
            RouteAnchorDecisions.ValidateReport("A1", "A1", "S1", 2, "p1", "S1", 2, "p1",
                12, 12, "uid-A", Participants, false, phase));

    // =========================================================================
    // 缺席分类：心跳只判存活，宽限由有效活动推进
    // =========================================================================

    [Fact]
    public void ClassifyMissing_FreshActivityWithinGrace_Waits()
    {
        var decision = RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-5), Now.AddSeconds(-10), RouteAnchorMemberState.Pathing, B);

        Assert.Equal(RouteAnchorMissingDecision.Wait, decision);
    }

    [Fact]
    public void ClassifyMissing_StaleActivity_FreshHeartbeat_Pulls()
    {
        // 心跳很新（连接活着），但有效活动已超过普通宽限 → 应拉取
        var decision = RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-1), Now.AddSeconds(-(int)B.PathingGrace.TotalSeconds - 10),
            RouteAnchorMemberState.Pathing, B);

        Assert.Equal(RouteAnchorMissingDecision.Pull, decision);
    }

    [Fact]
    public void ClassifyMissing_RepeatedHeartbeat_DoesNotExtendGrace()
    {
        // 心跳每秒都在刷，但有效活动停在 5 分钟前 → 仍然必须进入拉取，不能被心跳无限刷新
        var decision = RouteAnchorDecisions.ClassifyMissing(
            Now, Now, Now.AddMinutes(-5), RouteAnchorMemberState.Pathing, B);

        Assert.Equal(RouteAnchorMissingDecision.Pull, decision);
    }

    [Fact]
    public void ClassifyMissing_FightingWithinFightGrace_Waits_EvenBeyondPathingGrace()
    {
        // 超过普通宽限(90s)但仍在战斗宽限(120s)内 → 正在打怪，必须继续等，不能误判停滞
        var elapsed = B.PathingGrace + TimeSpan.FromSeconds(5);

        var decision = RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-1), Now - elapsed, RouteAnchorMemberState.Fighting, B);

        Assert.Equal(RouteAnchorMissingDecision.Wait, decision);
    }

    [Fact]
    public void ClassifyMissing_FightingBeyondFightGrace_Pulls()
    {
        var elapsed = B.FightingGrace + TimeSpan.FromSeconds(1);

        var decision = RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-1), Now - elapsed, RouteAnchorMemberState.Fighting, B);

        Assert.Equal(RouteAnchorMissingDecision.Pull, decision);
    }

    [Fact]
    public void ClassifyMissing_RevivingUsesRecoveryGrace_NotFightGrace()
    {
        // 复苏宽限(180s)长于战斗宽限(120s)：在 150s 处必须仍在等待
        var elapsed = B.FightingGrace + TimeSpan.FromSeconds(30);

        Assert.Equal(RouteAnchorMissingDecision.Wait, RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-1), Now - elapsed, RouteAnchorMemberState.Reviving, B));

        Assert.Equal(RouteAnchorMissingDecision.Pull, RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-1), Now - B.RecoveryGrace - TimeSpan.FromSeconds(1),
            RouteAnchorMemberState.Reviving, B));
    }

    [Fact]
    public void ClassifyMissing_TeleportingUsesTeleportGrace()
    {
        var elapsed = B.TeleportGrace + TimeSpan.FromSeconds(1);
        Assert.Equal(RouteAnchorMissingDecision.Pull, RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-1), Now - elapsed, RouteAnchorMemberState.Teleporting, B));
    }

    [Fact]
    public void ClassifyMissing_StaleHeartbeat_Stops_RegardlessOfActivity()
    {
        var decision = RouteAnchorDecisions.ClassifyMissing(
            Now, Now - B.HeartbeatFreshness - TimeSpan.FromSeconds(1), Now.AddSeconds(-1),
            RouteAnchorMemberState.Pathing, B);

        Assert.Equal(RouteAnchorMissingDecision.Stop, decision);
    }

    [Theory]
    [InlineData(RouteAnchorMemberState.Offline)]
    [InlineData(RouteAnchorMemberState.PullFailed)]
    public void ClassifyMissing_OfflineOrPullFailed_Stops(RouteAnchorMemberState state)
    {
        var decision = RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-1), Now.AddSeconds(-1), state, B);

        Assert.Equal(RouteAnchorMissingDecision.Stop, decision);
    }

    [Fact]
    public void ClassifyMissing_PullInFlight_Waits_WithoutReissuing()
    {
        Assert.Equal(RouteAnchorMissingDecision.Wait, RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-1), Now.AddMinutes(-5), RouteAnchorMemberState.PullRequested, B));

        Assert.Equal(RouteAnchorMissingDecision.Wait, RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-1), Now.AddMinutes(-5), RouteAnchorMemberState.PullApplied, B));
    }

    [Fact]
    public void ClassifyMissing_NeverActive_TreatedAsStalledAndPulled()
    {
        // lastActivityUtc == default（从未有有效活动）→ 直接进入拉取
        Assert.Equal(RouteAnchorMissingDecision.Pull, RouteAnchorDecisions.ClassifyMissing(
            Now, Now.AddSeconds(-1), default, RouteAnchorMemberState.Unknown, B));
    }

    // =========================================================================
    // 放行裁决
    // =========================================================================

    [Fact]
    public void DecideRelease_NotAllReady_Waits()
    {
        var states = States(
            ("uid-A", RouteAnchorMemberState.Ready),
            ("uid-B", RouteAnchorMemberState.Ready),
            ("uid-C", RouteAnchorMemberState.Reported),
            ("uid-D", RouteAnchorMemberState.Pathing));

        var decision = RouteAnchorDecisions.DecideRelease(
            RouteAnchorPhase.WaitingArrival, Participants, states, allowMissingPolicy: false,
            Now, Now.AddMinutes(10));

        Assert.Equal(RouteAnchorReleaseDecision.Wait, decision);
    }

    [Fact]
    public void DecideRelease_AllReady_Releases()
    {
        var states = Participants.ToDictionary(
            uid => uid, _ => RouteAnchorMemberState.Ready, StringComparer.Ordinal);

        var decision = RouteAnchorDecisions.DecideRelease(
            RouteAnchorPhase.WaitingArrival, Participants, states, false, Now, Now.AddMinutes(10));

        Assert.Equal(RouteAnchorReleaseDecision.Release, decision);
    }

    [Fact]
    public void DecideRelease_AnyPullFailed_StopsEvenIfOthersReady()
    {
        var states = States(
            ("uid-A", RouteAnchorMemberState.Ready),
            ("uid-B", RouteAnchorMemberState.Ready),
            ("uid-C", RouteAnchorMemberState.Ready),
            ("uid-D", RouteAnchorMemberState.PullFailed));

        var decision = RouteAnchorDecisions.DecideRelease(
            RouteAnchorPhase.Pulling, Participants, states, false, Now, Now.AddMinutes(10));

        Assert.Equal(RouteAnchorReleaseDecision.Stop, decision);
    }

    [Fact]
    public void DecideRelease_AbsoluteDeadlinePassed_NotAllReady_Stops()
    {
        var states = States(
            ("uid-A", RouteAnchorMemberState.Ready),
            ("uid-B", RouteAnchorMemberState.Ready),
            ("uid-C", RouteAnchorMemberState.Ready),
            ("uid-D", RouteAnchorMemberState.Pathing));

        var decision = RouteAnchorDecisions.DecideRelease(
            RouteAnchorPhase.Waiting, Participants, states, false,
            nowUtc: Now, absoluteDeadlineUtc: Now.AddSeconds(-1));

        Assert.Equal(RouteAnchorReleaseDecision.Stop, decision);
    }

    [Fact]
    public void DecideRelease_MissingPolicyDisabled_RemovedMemberDoesNotPermitRelease()
    {
        var states = States(
            ("uid-A", RouteAnchorMemberState.Ready),
            ("uid-B", RouteAnchorMemberState.Ready),
            ("uid-C", RouteAnchorMemberState.Ready),
            ("uid-D", RouteAnchorMemberState.Removed));

        var decision = RouteAnchorDecisions.DecideRelease(
            RouteAnchorPhase.Waiting, Participants, states, allowMissingPolicy: false,
            Now, Now.AddMinutes(10));

        Assert.Equal(RouteAnchorReleaseDecision.Wait, decision);
    }

    [Fact]
    public void DecideRelease_MissingPolicyEnabled_RemovedMemberPermitsReleaseOfTheRest()
    {
        var states = States(
            ("uid-A", RouteAnchorMemberState.Ready),
            ("uid-B", RouteAnchorMemberState.Ready),
            ("uid-C", RouteAnchorMemberState.Ready),
            ("uid-D", RouteAnchorMemberState.Removed));

        var decision = RouteAnchorDecisions.DecideRelease(
            RouteAnchorPhase.Waiting, Participants, states, allowMissingPolicy: true,
            Now, Now.AddMinutes(10));

        Assert.Equal(RouteAnchorReleaseDecision.Release, decision);
    }

    [Fact]
    public void DecideRelease_NoParticipants_Stops()
        => Assert.Equal(RouteAnchorReleaseDecision.Stop,
            RouteAnchorDecisions.DecideRelease(
                RouteAnchorPhase.Collecting, [], States(), false, Now, Now.AddMinutes(10)));

    [Theory]
    [InlineData(RouteAnchorPhase.Released, RouteAnchorReleaseDecision.Release)]
    [InlineData(RouteAnchorPhase.Stopped, RouteAnchorReleaseDecision.Stop)]
    public void DecideRelease_TerminalPhaseIsSticky(RouteAnchorPhase phase, RouteAnchorReleaseDecision expected)
    {
        var states = States(("uid-A", RouteAnchorMemberState.Pathing));

        Assert.Equal(expected, RouteAnchorDecisions.DecideRelease(
            phase, Participants, states, false, Now, Now.AddMinutes(10)));
    }

    // =========================================================================
    // 阶段推导
    // =========================================================================

    [Fact]
    public void DerivePhase_NoReportYet_IsCollecting()
    {
        var states = States(
            ("uid-A", RouteAnchorMemberState.Pathing),
            ("uid-B", RouteAnchorMemberState.Fighting),
            ("uid-C", RouteAnchorMemberState.Pathing),
            ("uid-D", RouteAnchorMemberState.Pathing));

        Assert.Equal(RouteAnchorPhase.Collecting, RouteAnchorDecisions.DerivePhase(
            Participants, states, RouteAnchorReleaseDecision.Wait));
    }

    [Fact]
    public void DerivePhase_SomeReportedSomeStalled_IsWaiting()
    {
        var states = States(
            ("uid-A", RouteAnchorMemberState.Reported),
            ("uid-B", RouteAnchorMemberState.Pathing),
            ("uid-C", RouteAnchorMemberState.Fighting),
            ("uid-D", RouteAnchorMemberState.Pathing));

        Assert.Equal(RouteAnchorPhase.Waiting, RouteAnchorDecisions.DerivePhase(
            Participants, states, RouteAnchorReleaseDecision.Wait));
    }

    [Fact]
    public void DerivePhase_PullInFlight_IsPulling()
    {
        var states = States(
            ("uid-A", RouteAnchorMemberState.Reported),
            ("uid-B", RouteAnchorMemberState.PullRequested),
            ("uid-C", RouteAnchorMemberState.Reported),
            ("uid-D", RouteAnchorMemberState.Reported));

        Assert.Equal(RouteAnchorPhase.Pulling, RouteAnchorDecisions.DerivePhase(
            Participants, states, RouteAnchorReleaseDecision.Wait));
    }

    [Fact]
    public void DerivePhase_AllReportedNoPull_IsWaitingArrival()
    {
        var states = States(
            ("uid-A", RouteAnchorMemberState.Reported),
            ("uid-B", RouteAnchorMemberState.Reported),
            ("uid-C", RouteAnchorMemberState.Reported),
            ("uid-D", RouteAnchorMemberState.Reported));

        Assert.Equal(RouteAnchorPhase.WaitingArrival, RouteAnchorDecisions.DerivePhase(
            Participants, states, RouteAnchorReleaseDecision.Wait));
    }

    [Fact]
    public void DerivePhase_ReleaseDecisionDominates()
    {
        var states = Participants.ToDictionary(
            uid => uid, _ => RouteAnchorMemberState.Ready, StringComparer.Ordinal);

        Assert.Equal(RouteAnchorPhase.Released, RouteAnchorDecisions.DerivePhase(
            Participants, states, RouteAnchorReleaseDecision.Release));

        Assert.Equal(RouteAnchorPhase.Stopped, RouteAnchorDecisions.DerivePhase(
            Participants, states, RouteAnchorReleaseDecision.Stop));
    }

    // =========================================================================
    // Pull 命令与幂等
    // =========================================================================

    [Fact]
    public void ShouldAcceptPullApplied_OnlyMatchingNonEmptyCommandId()
    {
        Assert.True(RouteAnchorDecisions.ShouldAcceptPullApplied("cmd-1", "cmd-1"));
        Assert.False(RouteAnchorDecisions.ShouldAcceptPullApplied("cmd-1", "cmd-2"));
        Assert.False(RouteAnchorDecisions.ShouldAcceptPullApplied("", "cmd-1"));
        Assert.False(RouteAnchorDecisions.ShouldAcceptPullApplied("cmd-1", ""));
        Assert.False(RouteAnchorDecisions.ShouldAcceptPullApplied(null, null));
    }

    [Fact]
    public void BuildPullCommandId_StableForSameAttempt_ChangesPerAttempt()
    {
        var a = RouteAnchorDecisions.BuildPullCommandId("A1", "uid-A", 1);
        var b = RouteAnchorDecisions.BuildPullCommandId("A1", "uid-A", 1);
        var c = RouteAnchorDecisions.BuildPullCommandId("A1", "uid-A", 2);
        var d = RouteAnchorDecisions.BuildPullCommandId("A1", "uid-B", 1);

        Assert.Equal(a, b);          // 重发必须复用同一 ID（客户端据此去重）
        Assert.NotEqual(a, c);
        Assert.NotEqual(a, d);
    }

    [Fact]
    public void CanIssuePull_RespectsAttemptsHeartbeatAndState()
    {
        // 正常可拉
        Assert.True(RouteAnchorDecisions.CanIssuePull(
            RouteAnchorMemberState.Pathing, 0, 2, Now, Now.AddSeconds(-1), B));

        // 重试耗尽
        Assert.False(RouteAnchorDecisions.CanIssuePull(
            RouteAnchorMemberState.Pathing, 2, 2, Now, Now.AddSeconds(-1), B));

        // 心跳不新鲜
        Assert.False(RouteAnchorDecisions.CanIssuePull(
            RouteAnchorMemberState.Pathing, 0, 2, Now, Now - B.HeartbeatFreshness - TimeSpan.FromSeconds(1), B));

        // 已 Ready / 已移除 / 离线 / 已失败：不可拉
        foreach (var state in new[]
                 {
                     RouteAnchorMemberState.Ready, RouteAnchorMemberState.Removed,
                     RouteAnchorMemberState.Offline, RouteAnchorMemberState.PullFailed,
                 })
        {
            Assert.False(RouteAnchorDecisions.CanIssuePull(state, 0, 2, Now, Now.AddSeconds(-1), B));
        }
    }

    // =========================================================================
    // 拒绝原因可枚举（便于日志与排障）
    // =========================================================================

    [Fact]
    public void ReportRejection_HasDistinctValuesForAllReasons()
    {
        var values = Enum.GetValues<RouteAnchorDecisions.ReportRejection>();
        Assert.Equal(values.Length, values.Distinct().Count());
        Assert.Contains(RouteAnchorDecisions.ReportRejection.None, values);
    }
}
