using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// **R5 批次 4（A6）运行中占用与到来者相遇**的分场景夹具（owner 裁决 B.1 最小裁决矩阵；
/// **只覆盖下列已列场景，不声称"全格"**）：
/// 最高级锄地类恒打断；同级后来者打断；低级别本地等待；身份/级别/优先级不可核验 ⇒ 保守停驻；
/// 事实未知**不是**空闲。另覆盖本地等待集合选择、恢复资格与生产映射（不知道的一律留空）。
/// </summary>
public sealed class RunningOccupancyArbiterTests
{
    private static RunningOccupantFacts Occupied(ArbitrationTier? tier, int? priority, bool trustedIdentity = true,
        string? instanceId = "aabbccddeeff00112233445566778899", bool hoeing = false, bool? highestClass = false)
        => new()
        {
            State = OccupantFactsState.Occupied,
            HasTrustedIdentity = trustedIdentity,
            ExecutionInstanceId = trustedIdentity ? instanceId : null,
            Name = "占用者",
            HoeingClass = hoeing,
            HighestClass = highestClass,
            Tier = tier,
            Priority = priority,
        };

    private static IncomingRequestFacts Incoming(ArbitrationTier tier, int priority, bool hoeingHighest = false)
        => new() { Tier = tier, Priority = priority, IsHoeingHighest = hoeingHighest };

    [Fact]
    public void Decide_UnknownFacts_NeverTreatedAsIdle()
    {
        var unknown = RunningOccupantFacts.Unknown("bgi_status_stale");

        var encounter = RunningOccupancyArbiter.Decide(unknown, Incoming(ArbitrationTier.System, 999));

        Assert.Equal(RunningEncounterVerdict.HoldFactsUnknown, encounter.Verdict);
        Assert.Contains("bgi_status_stale", encounter.Reason);
        Assert.Null(encounter.PreemptTargetInstanceId);
    }

    [Fact]
    public void Decide_Idle_Proceeds()
    {
        Assert.Equal(RunningEncounterVerdict.ProceedIdle,
            RunningOccupancyArbiter.Decide(RunningOccupantFacts.Idle(), Incoming(ArbitrationTier.Plan, -5)).Verdict);
    }

    [Fact]
    public void Decide_HoeingHighest_PreemptsAnyOccupantIncludingHighest()
    {
        // 占用者是最高级（System/极大），到来的一键锄地仍打断（owner：两种最高级相遇由后来者打断）
        foreach (var occupant in new[]
                 {
                     Occupied(ArbitrationTier.System, int.MaxValue),
                     Occupied(ArbitrationTier.System, int.MaxValue, hoeing: true),
                     Occupied(ArbitrationTier.Plan, int.MinValue),
                 })
        {
            var encounter = RunningOccupancyArbiter.Decide(occupant, Incoming(ArbitrationTier.Plan, 0, hoeingHighest: true));
            Assert.Equal(RunningEncounterVerdict.PreemptNow, encounter.Verdict);
            Assert.Equal(occupant.ExecutionInstanceId, encounter.PreemptTargetInstanceId);
        }
    }

    [Fact]
    public void Decide_UnknownOccupantIdentity_HoldsEvenForHoeingHighest()
    {
        // 台账占用/身份缺失：无法绑定被切任务 ⇒ 连最高级也不得抢（B.1 ③ 保守停驻）
        var encounter = RunningOccupancyArbiter.Decide(
            Occupied(tier: null, priority: null, trustedIdentity: false),
            Incoming(ArbitrationTier.Plan, 0, hoeingHighest: true));

        Assert.Equal(RunningEncounterVerdict.HoldUnknownOccupant, encounter.Verdict);
        Assert.Null(encounter.PreemptTargetInstanceId);
    }

    [Fact]
    public void Decide_UnknownOccupantLevel_HoldsForNonHoeingIncoming()
    {
        var encounter = RunningOccupancyArbiter.Decide(
            Occupied(tier: null, priority: null),
            Incoming(ArbitrationTier.System, 10));

        Assert.Equal(RunningEncounterVerdict.HoldUnknownOccupant, encounter.Verdict);
    }

    [Fact]
    public void Decide_ProvenHighestOccupant_BlocksNormalIncoming_ButNotHoeingHighest()
    {
        // 占用者已证明是 B.1 最高级（上线锄地／一键锄地）：普通任务（即便级别更高）只能本地等待
        var occupant = Occupied(ArbitrationTier.System, 50, hoeing: true, highestClass: true);
        Assert.Equal(RunningEncounterVerdict.WaitLocally,
            RunningOccupancyArbiter.Decide(occupant, Incoming(ArbitrationTier.System, 60)).Verdict);
        Assert.Equal(RunningEncounterVerdict.WaitLocally,
            RunningOccupancyArbiter.Decide(occupant, Incoming(ArbitrationTier.System, 50)).Verdict);

        // 但最高级锄地到来者仍打断它（两种最高级相遇由后来者打断）
        Assert.Equal(RunningEncounterVerdict.PreemptNow,
            RunningOccupancyArbiter.Decide(occupant, Incoming(ArbitrationTier.Plan, 0, hoeingHighest: true)).Verdict);
    }

    [Fact]
    public void Decide_HoeingClassWithUnprovenHighestLevel_HoldsNormalIncoming()
    {
        // 只知道"锄地类在跑"不足以等同最高级：普通任务既不能抢占也不能断言等待 ⇒ 保守停驻
        var occupant = Occupied(ArbitrationTier.System, 50, hoeing: true, highestClass: null);

        Assert.Equal(RunningEncounterVerdict.HoldUnknownOccupant,
            RunningOccupancyArbiter.Decide(occupant, Incoming(ArbitrationTier.System, 99)).Verdict);
    }

    [Fact]
    public void Decide_PreemptNeverReturnedWithoutBindableTarget()
    {
        // 可信标记为真但实例 ID 缺失/空 GUID ⇒ 不得返回 PreemptNow
        var noInstance = Occupied(ArbitrationTier.Plan, 0, instanceId: null);
        Assert.Equal(RunningEncounterVerdict.HoldUnknownOccupant,
            RunningOccupancyArbiter.Decide(noInstance, Incoming(ArbitrationTier.System, 10, hoeingHighest: true)).Verdict);

        var emptyInstance = Occupied(ArbitrationTier.Plan, 0, instanceId: Guid.Empty.ToString("N"));
        Assert.Equal(RunningEncounterVerdict.HoldUnknownOccupant,
            RunningOccupancyArbiter.Decide(emptyInstance, Incoming(ArbitrationTier.System, 10)).Verdict);
    }

    [Fact]
    public void Decide_UntrustedIncomingNeverPreempts()
    {
        // 自报 key 等不可信来源：不得被当作最高级或高级别凭证
        var occupant = Occupied(ArbitrationTier.Plan, 0);
        var untrustedHighest = new IncomingRequestFacts { IsHoeingHighest = true, HasTrustedIdentity = false, Tier = ArbitrationTier.Plan, Priority = 0 };
        var untrustedHigher = new IncomingRequestFacts { IsHoeingHighest = false, HasTrustedIdentity = false, Tier = ArbitrationTier.System, Priority = 99 };

        Assert.Equal(RunningEncounterVerdict.WaitLocally,
            RunningOccupancyArbiter.Decide(occupant, untrustedHighest).Verdict);
        Assert.Equal(RunningEncounterVerdict.WaitLocally,
            RunningOccupancyArbiter.Decide(occupant, untrustedHigher).Verdict);
    }

    [Theory]
    [InlineData(ArbitrationTier.System, 50, ArbitrationTier.System, 51, RunningEncounterVerdict.WaitLocally)] // 同级严格更低
    [InlineData(ArbitrationTier.System, -1, ArbitrationTier.System, -2, RunningEncounterVerdict.PreemptNow)] // 负值更高
    [InlineData(ArbitrationTier.Fixed, int.MaxValue, ArbitrationTier.System, int.MinValue, RunningEncounterVerdict.WaitLocally)] // 跨级优先，低级别优先值再大也不越级
    public void Decide_LevelMatrix_CrossTierAndNegative(ArbitrationTier incomingTier, int incomingPriority,
        ArbitrationTier occupantTier, int occupantPriority, RunningEncounterVerdict expected)
        => Assert.Equal(expected, RunningOccupancyArbiter.Decide(
            Occupied(occupantTier, occupantPriority), Incoming(incomingTier, incomingPriority)).Verdict);

    [Fact]
    public void Decide_MissingSingleLevelComponent_Holds()
    {
        Assert.Equal(RunningEncounterVerdict.HoldUnknownOccupant,
            RunningOccupancyArbiter.Decide(new RunningOccupantFacts
            {
                State = OccupantFactsState.Occupied,
                HasTrustedIdentity = true,
                ExecutionInstanceId = "aabbccddeeff00112233445566778899",
                Tier = ArbitrationTier.System,
                Priority = null,
            }, Incoming(ArbitrationTier.System, 1)).Verdict);

        Assert.Equal(RunningEncounterVerdict.HoldUnknownOccupant,
            RunningOccupancyArbiter.Decide(new RunningOccupantFacts
            {
                State = OccupantFactsState.Occupied,
                HasTrustedIdentity = true,
                ExecutionInstanceId = "aabbccddeeff00112233445566778899",
                Tier = null,
                Priority = 5,
            }, Incoming(ArbitrationTier.System, 1)).Verdict);
    }

    [Fact]
    public void SelectNextFromWaitSet_SameTierHigherPriorityWins_AndNullScheduleSortsLast()
    {
        var waitSet = new List<WaitingRequestFacts>
        {
            new() { CandidateId = "c-p5", StableIdentity = "s-5", Tier = ArbitrationTier.System, Priority = 5 },
            new() { CandidateId = "c-p1", StableIdentity = "s-1", Tier = ArbitrationTier.System, Priority = 1 },
            new() { CandidateId = "c-nulltime", StableIdentity = "s-n", Tier = ArbitrationTier.System, Priority = 1, ScheduledAt = null },
            new() { CandidateId = "c-timed", StableIdentity = "s-t", Tier = ArbitrationTier.System, Priority = 1, ScheduledAt = DateTimeOffset.Parse("2026-09-24T09:00:00+08:00") },
        };

        Assert.Equal("c-p5", RunningOccupancyArbiter.SelectNextFromWaitSet(waitSet)!.CandidateId);
        waitSet.RemoveAll(w => w.CandidateId == "c-p5");
        Assert.Equal("c-timed", RunningOccupancyArbiter.SelectNextFromWaitSet(waitSet)!.CandidateId); // 有值时刻优先于 null
        waitSet.RemoveAll(w => w.CandidateId == "c-timed");
        // 剩余两项级别/优先级/时刻全同（null 时刻）：按 StableIdentity Ordinal 兜底（"s-1" < "s-n"）
        Assert.Equal("c-p1", RunningOccupancyArbiter.SelectNextFromWaitSet(waitSet)!.CandidateId);
    }

    [Fact]
    public void SelectNextFromWaitSet_IsOrderIndependentWhenAllSortKeysEqual()
    {
        var a = new WaitingRequestFacts { CandidateId = "c-a", StableIdentity = "s-same", Tier = ArbitrationTier.Plan, Priority = 0 };
        var b = new WaitingRequestFacts { CandidateId = "c-b", StableIdentity = "s-same", Tier = ArbitrationTier.Plan, Priority = 0 };

        // 稳定身份相同 ⇒ 只剩**候选号**兜底；两序都取字典序更小者（排列无关）
        Assert.Equal("c-a", RunningOccupancyArbiter.SelectNextFromWaitSet([a, b])!.CandidateId);
        Assert.Equal("c-a", RunningOccupancyArbiter.SelectNextFromWaitSet([b, a])!.CandidateId);
    }

    [Fact]
    public void SelectNextFromWaitSet_NullScheduleAlwaysSortsAfterRealMaxTimestamp()
    {
        var waitSet = new List<WaitingRequestFacts>
        {
            // 让 null 项的稳定身份**字典序更小**：若实现把 null 当 MaxValue 打平（旧行为），
            // 兜底会选中 null 项；只有"有值者一律先于 null"的独立排序键才能稳定选出 c-max。
            new() { CandidateId = "c-null", StableIdentity = "s-a", Tier = ArbitrationTier.Plan, ScheduledAt = null },
            new() { CandidateId = "c-max", StableIdentity = "s-b", Tier = ArbitrationTier.Plan, ScheduledAt = DateTimeOffset.MaxValue },
        };

        // 有值（即便等于 MaxValue）一律先于 null：两序都得到同一结果
        Assert.Equal("c-max", RunningOccupancyArbiter.SelectNextFromWaitSet(waitSet)!.CandidateId);
        Assert.Equal("c-max", RunningOccupancyArbiter.SelectNextFromWaitSet([waitSet[1], waitSet[0]])!.CandidateId);
    }

    [Theory]
    // 到来者（级别/优先级） … 占用者（级别/优先级） … 期望
    [InlineData(ArbitrationTier.System, 10, ArbitrationTier.Plan, 0, RunningEncounterVerdict.PreemptNow)]     // 更高
    [InlineData(ArbitrationTier.System, 50, ArbitrationTier.System, 50, RunningEncounterVerdict.PreemptNow)]  // 同级：后来者打断
    [InlineData(ArbitrationTier.Plan, 0, ArbitrationTier.Plan, 0, RunningEncounterVerdict.PreemptNow)]        // 同级：后来者打断
    [InlineData(ArbitrationTier.System, 50, ArbitrationTier.System, 60, RunningEncounterVerdict.WaitLocally)] // 同级更低
    [InlineData(ArbitrationTier.Fixed, 0, ArbitrationTier.Plan, 0, RunningEncounterVerdict.PreemptNow)]       // 级别更高
    [InlineData(ArbitrationTier.Plan, 99, ArbitrationTier.Fixed, 0, RunningEncounterVerdict.WaitLocally)]     // 级别更低（优先级不得越级）
    [InlineData(ArbitrationTier.Plan, -3, ArbitrationTier.Plan, -3, RunningEncounterVerdict.PreemptNow)]      // 负优先级同级
    public void Decide_LevelMatrix(ArbitrationTier incomingTier, int incomingPriority,
        ArbitrationTier occupantTier, int occupantPriority, RunningEncounterVerdict expected)
    {
        var encounter = RunningOccupancyArbiter.Decide(
            Occupied(occupantTier, occupantPriority),
            Incoming(incomingTier, incomingPriority));

        Assert.Equal(expected, encounter.Verdict);
        if (expected == RunningEncounterVerdict.PreemptNow)
        {
            Assert.Equal("aabbccddeeff00112233445566778899", encounter.PreemptTargetInstanceId);
        }
        else
        {
            Assert.Null(encounter.PreemptTargetInstanceId);
        }
    }

    [Fact]
    public void SelectNextFromWaitSet_OrdersByHoeingThenLevelThenPriorityThenScheduledThenIdentity()
    {
        var waitSet = new List<WaitingRequestFacts>
        {
            new() { CandidateId = "c-plan", StableIdentity = "s-plan", Tier = ArbitrationTier.Plan, Priority = 0 },
            new() { CandidateId = "c-system-low", StableIdentity = "s-sys-low", Tier = ArbitrationTier.System, Priority = 1 },
            new() { CandidateId = "c-system-mid-late", StableIdentity = "s-sys-mid-late", Tier = ArbitrationTier.System, Priority = 5, ScheduledAt = DateTimeOffset.Parse("2026-09-24T10:00:00+08:00") },
            new() { CandidateId = "c-system-mid-early", StableIdentity = "s-sys-mid-early", Tier = ArbitrationTier.System, Priority = 5, ScheduledAt = DateTimeOffset.Parse("2026-09-24T09:00:00+08:00") },
            new() { CandidateId = "c-hoeing", StableIdentity = "s-hoeing", IsHoeingHighest = true, Tier = ArbitrationTier.Plan, Priority = 0 },
        };

        Assert.Equal("c-hoeing", RunningOccupancyArbiter.SelectNextFromWaitSet(waitSet)!.CandidateId);

        waitSet.RemoveAll(w => w.CandidateId == "c-hoeing");
        Assert.Equal("c-system-mid-early", RunningOccupancyArbiter.SelectNextFromWaitSet(waitSet)!.CandidateId);

        waitSet.RemoveAll(w => w.CandidateId is "c-system-mid-early" or "c-system-mid-late");
        Assert.Equal("c-system-low", RunningOccupancyArbiter.SelectNextFromWaitSet(waitSet)!.CandidateId);

        waitSet.RemoveAll(w => w.CandidateId == "c-system-low");
        Assert.Equal("c-plan", RunningOccupancyArbiter.SelectNextFromWaitSet(waitSet)!.CandidateId);

        Assert.Null(RunningOccupancyArbiter.SelectNextFromWaitSet([]));
        Assert.Null(RunningOccupancyArbiter.SelectNextFromWaitSet(null));
    }

    [Fact]
    public void SelectNextFromWaitSet_SkipsNotReadyPrerequisite()
    {
        var waitSet = new List<WaitingRequestFacts>
        {
            new() { CandidateId = "c-not-ready", StableIdentity = "s-1", IsHoeingHighest = true, PrerequisiteReady = false },
            new() { CandidateId = "c-ready", StableIdentity = "s-2", Tier = ArbitrationTier.Plan },
        };

        Assert.Equal("c-ready", RunningOccupancyArbiter.SelectNextFromWaitSet(waitSet)!.CandidateId);
        Assert.Null(RunningOccupancyArbiter.SelectNextFromWaitSet(
            [new WaitingRequestFacts { CandidateId = "c-x", StableIdentity = "s-x", PrerequisiteReady = false }]));
    }

    [Fact]
    public void DecideResume_RequiresTicketAndIdleOccupant()
    {
        var idle = RunningOccupantFacts.Idle();
        var unknown = RunningOccupantFacts.Unknown("bgi_status_stale");
        var other = Occupied(ArbitrationTier.Plan, 0, instanceId: "11111111222233334444555566667777");
        var same = Occupied(ArbitrationTier.Plan, 0, instanceId: "aabbccddeeff00112233445566778899");
        var valid = new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "suspended", SuspendedExecutionInstanceId = "aabbccddeeff00112233445566778899" };

        Assert.Equal(ResumeVerdict.RefuseTicketInvalid,
            RunningOccupancyArbiter.DecideResume(idle, new ResumptionFacts { HasValidTicket = false }));
        // 被暂停身份缺失：即便票据布尔为真也不得恢复
        Assert.Equal(ResumeVerdict.RefuseSuspendedIdentityUnknown,
            RunningOccupancyArbiter.DecideResume(idle, new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "" }));
        Assert.Equal(ResumeVerdict.HoldFactsUnknown, RunningOccupancyArbiter.DecideResume(unknown, valid));
        Assert.Equal(ResumeVerdict.AllowResume, RunningOccupancyArbiter.DecideResume(idle, valid));
        Assert.Equal(ResumeVerdict.RefuseOccupiedByOther, RunningOccupancyArbiter.DecideResume(other, valid));
        Assert.Equal(ResumeVerdict.RefuseAlreadyOwner, RunningOccupancyArbiter.DecideResume(same, valid));
        // 占用者身份不可核验 / 被暂停实例未知：保守停驻（不得说成"他人占用"或"已在运行"）
        Assert.Equal(ResumeVerdict.HoldFactsUnknown,
            RunningOccupancyArbiter.DecideResume(Occupied(null, null, trustedIdentity: false), valid));
        Assert.Equal(ResumeVerdict.HoldFactsUnknown, RunningOccupancyArbiter.DecideResume(same,
            new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "suspended", SuspendedExecutionInstanceId = null }));
        // 被暂停实例非法/空 GUID ⇒ 身份未知（保守停驻），不得表述为"他人占用"
        Assert.Equal(ResumeVerdict.HoldFactsUnknown, RunningOccupancyArbiter.DecideResume(other,
            new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "suspended", SuspendedExecutionInstanceId = "not-a-guid" }));
        Assert.Equal(ResumeVerdict.HoldFactsUnknown, RunningOccupancyArbiter.DecideResume(other,
            new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "suspended", SuspendedExecutionInstanceId = Guid.Empty.ToString("N") }));
        // 同一身份的**不同字符串格式**（D 格式/大写）必须按 GUID 值判为同一物主
        Assert.Equal(ResumeVerdict.RefuseAlreadyOwner, RunningOccupancyArbiter.DecideResume(same,
            new ResumptionFacts
            {
                HasValidTicket = true,
                SuspendedIdentity = "suspended",
                SuspendedExecutionInstanceId = Guid.Parse("aabbccddeeff00112233445566778899").ToString("D").ToUpperInvariant(),
            }));
    }

    [Fact]
    public void FromStatus_UnknownsStayUnknownAndNeverIdle()
    {
        Assert.Equal(OccupantFactsState.Unknown, RunningOccupantFacts.FromStatus(null, false, false, true).State);
        Assert.Equal("bgi_status_stale", RunningOccupantFacts.FromStatus(new ControlStatus(), false, false, true).Reference);
        Assert.Equal("external_start_ledger_unreadable", RunningOccupantFacts.FromStatus(FreshStatus(running: false), false, true, true).Reference);
        // 纪元未核验（旧纪元快照/纪元缺失）：一律未知，即使快照"新鲜"、running=false 也不算空闲
        Assert.Equal("bgi_epoch_unverified",
            RunningOccupantFacts.FromStatus(FreshStatus(running: false), false, false, bgiEpochVerified: false).Reference);
    }

    [Fact]
    public void FromStatus_LedgerOccupancyHasNoTrustedIdentity()
    {
        var facts = RunningOccupantFacts.FromStatus(FreshStatus(running: true), ledgerOccupied: true, ledgerUnknown: false, bgiEpochVerified: true);

        Assert.Equal(OccupantFactsState.Occupied, facts.State);
        Assert.False(facts.HasTrustedIdentity);
        Assert.Equal("external_start_ledger_accepted_unfinished", facts.Reference);
    }

    [Fact]
    public void FromStatus_FreshIdleIsIdle_OccupiedCarriesIdentityButLeavesLevelUnknown()
    {
        Assert.Equal(OccupantFactsState.Idle, RunningOccupantFacts.FromStatus(FreshStatus(running: false), false, false, true).State);
        // 台账占用 + 快照空闲：按占用处理（台账已受理未终结不得因快照空闲而丢占用）
        Assert.Equal(OccupantFactsState.Occupied,
            RunningOccupantFacts.FromStatus(FreshStatus(running: false), ledgerOccupied: true, ledgerUnknown: false, bgiEpochVerified: true).State);

        var instanceId = Guid.Parse("aabbccddeeff00112233445566778899");
        var status = FreshStatus(running: true);
        status.CurrentTaskName = "锄地一条龙";
        status.AutoHoeingRunning = true;
        status.CurrentExecution = new TaskExecutionIdentitySnapshot(
            instanceId, 7, Guid.Parse("11111111222233334444555566667777"), null,
            "Group", "Ext", "锄地一条龙", false);

        var facts = RunningOccupantFacts.FromStatus(status, false, false, true);

        Assert.Equal(OccupantFactsState.Occupied, facts.State);
        Assert.True(facts.HasTrustedIdentity);
        Assert.Equal(instanceId.ToString("N"), facts.ExecutionInstanceId);
        Assert.Equal("Group", facts.Kind);
        Assert.Equal("Ext", facts.Source);
        Assert.True(facts.HoeingClass);
        // 占用者的级别/优先级来源未接线 ⇒ 必须保持未知（不得用默认值冒充已知）
        Assert.Null(facts.Tier);
        Assert.Null(facts.Priority);
        Assert.Null(facts.HighestClass);

        // 空 GUID 身份不得被当作可核验身份
        status.CurrentExecution = new TaskExecutionIdentitySnapshot(
            Guid.Empty, 1, Guid.Empty, null, "Group", "Ext", "无名", false);
        Assert.False(RunningOccupantFacts.FromStatus(status, false, false, true).HasTrustedIdentity);
    }

    private static ControlStatus FreshStatus(bool running)
    {
        var status = new ControlStatus
        {
            TaskStatusAvailable = true,
            TaskStatusBgiEpoch = "9:900",
            TaskStatusObservedAtUtc = DateTimeOffset.UtcNow,
        };
        status.TaskRunning = running;
        return status;
    }
}
