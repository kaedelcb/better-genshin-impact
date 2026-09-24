using System;
using System.Collections.Generic;
using System.Linq;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;
using V = MultiplayerHoeingAssistant.Models.RunningEncounterVerdict;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// R5 批次 9：RunningOccupancyArbiter 的原型叉积状态表夹具（§17.4-A 第 6 条）。
/// 本文件枚举 Decide 的 19 个占用者原型 × 5 个到来者原型 = 95 格、
/// DecideResume 的 9 个占用者原型 × 10 个恢复原型 = 90 格，并补充 SelectNextFromWaitSet
/// 的对抗性排序与 FromStatus 的映射/优先级表。
/// 这些矩阵是"已列原型叉积"的枚举化证据，**不**声称覆盖无界字段数学全空间、
/// 真实 IPC 时序或端到端接线。不修改生产代码。
/// </summary>
public sealed class RunningOccupancyArbiterStateTableTests
{
    private const string ValidInstanceId = "aabbccddeeff00112233445566778899";
    private const string DifferentInstanceId = "112233445566778899aabbccddeeff00";
    private const string ThirdInstanceId = "00112233445566778899aabbccddeeff";
    private const string MalformedInstanceId = "not-a-guid";

    // ============================================================
    // §1  Decide 原型叉积矩阵（19 占用者原型 × 5 到来者原型 = 95 格）
    // ============================================================

    private static readonly (string Label, RunningOccupantFacts Facts)[] OccupantArchetypes =
    {
        ("O01-Unknown", RunningOccupantFacts.Unknown("matrix_test")),
        ("O02-Idle", RunningOccupantFacts.Idle()),
        ("O03-UntrustedNoId", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: null, hasTrustedIdentity: false)),
        ("O04-UntrustedValidId", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: ValidInstanceId, hasTrustedIdentity: false)),
        ("O05-TrustedNullId", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: null)),
        ("O06-TrustedEmptyGuid", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: Guid.Empty.ToString("N"))),
        ("O07-TrustedMalformedId", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: MalformedInstanceId)),
        ("O08-ProvenHighest", OccupiedBind(true, true, ArbitrationTier.System, 50)),
        ("O09-HoeingKnownNotHighest", OccupiedBind(false, true, ArbitrationTier.System, 50)),
        ("O10-HoeingUnprovenHighest", OccupiedBind(null, true, ArbitrationTier.System, 50)),
        ("O11-TierNullPriorityNull", OccupiedBind(false, false, null, null)),
        ("O12-TierNullPriorityKnown", OccupiedBind(false, false, null, 50)),
        ("O13-PriorityNullTierKnown", OccupiedBind(false, false, ArbitrationTier.Plan, null)),
        ("O14-Plan5-DifferentTarget", OccupiedBind(false, false, ArbitrationTier.Plan, 5,
            instanceId: DifferentInstanceId)),
        ("O15-Fixed0", OccupiedBind(false, false, ArbitrationTier.Fixed, 0)),
        ("O16-Plan0-ThirdTarget", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: ThirdInstanceId)),
        ("O17-System0", OccupiedBind(false, false, ArbitrationTier.System, 0)),
        ("O18-System99", OccupiedBind(false, false, ArbitrationTier.System, 99)),
        ("O19-HighestMissingLevels", OccupiedBind(true, true, null, null)),
    };

    private static readonly (string Label, IncomingRequestFacts Facts)[] IncomingArchetypes =
    {
        ("I1-UntrustedHighest", new IncomingRequestFacts
        {
            IsHoeingHighest = true, HasTrustedIdentity = false, Tier = ArbitrationTier.System, Priority = 99,
        }),
        ("I2-TrustedHighest", new IncomingRequestFacts
        {
            IsHoeingHighest = true, HasTrustedIdentity = true, Tier = ArbitrationTier.Plan, Priority = 0,
        }),
        ("I3-TrustedHigher", new IncomingRequestFacts
        {
            IsHoeingHighest = false, HasTrustedIdentity = true, Tier = ArbitrationTier.System, Priority = 99,
        }),
        ("I4-TrustedSame", new IncomingRequestFacts
        {
            IsHoeingHighest = false, HasTrustedIdentity = true, Tier = ArbitrationTier.Plan, Priority = 0,
        }),
        ("I5-TrustedLower", new IncomingRequestFacts
        {
            IsHoeingHighest = false, HasTrustedIdentity = true, Tier = ArbitrationTier.Plan, Priority = -1,
        }),
    };

    private static readonly (V Verdict, bool ExpectPreemptTarget)[,] ExpectedDecideMatrix =
    {
        // I1 不可信最高 / I2 可信最高 / I3 可信普通更高 / I4 可信普通同级 / I5 可信普通更低
        { (V.HoldFactsUnknown, false), (V.HoldFactsUnknown, false), (V.HoldFactsUnknown, false), (V.HoldFactsUnknown, false), (V.HoldFactsUnknown, false) }, // O01 Unknown
        { (V.ProceedIdle, false), (V.ProceedIdle, false), (V.ProceedIdle, false), (V.ProceedIdle, false), (V.ProceedIdle, false) }, // O02 Idle
        { (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false) }, // O03 不可信且无实例
        { (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false) }, // O04 不可信但合法实例
        { (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false) }, // O05 可信标记但实例 null
        { (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false) }, // O06 可信标记但空 GUID
        { (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false) }, // O07 可信标记但非法 GUID
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.WaitLocally, false), (V.WaitLocally, false), (V.WaitLocally, false) }, // O08 已证明最高级
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.PreemptNow, true), (V.WaitLocally, false), (V.WaitLocally, false) }, // O09 锄地类但已证明非最高级
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false) }, // O10 锄地类最高级未知
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false) }, // O11 级别与优先级都缺失
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false) }, // O12 仅级别缺失
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false), (V.HoldUnknownOccupant, false) }, // O13 仅优先级缺失
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.PreemptNow, true), (V.WaitLocally, false), (V.WaitLocally, false) }, // O14 Plan/5，另一个目标
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.PreemptNow, true), (V.WaitLocally, false), (V.WaitLocally, false) }, // O15 Fixed/0
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.PreemptNow, true), (V.PreemptNow, true), (V.WaitLocally, false) }, // O16 Plan/0，第三个目标
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.PreemptNow, true), (V.WaitLocally, false), (V.WaitLocally, false) }, // O17 System/0
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.PreemptNow, true), (V.WaitLocally, false), (V.WaitLocally, false) }, // O18 System/99
        { (V.WaitLocally, false), (V.PreemptNow, true), (V.WaitLocally, false), (V.WaitLocally, false), (V.WaitLocally, false) }, // O19 已证明最高级但级别缺失
    };

    [Fact]
    public void Decide_FullPrototypeCrossProduct_All95CellsMatchExpected()
    {
        Assert.Equal(19, OccupantArchetypes.Length);
        Assert.Equal(5, IncomingArchetypes.Length);
        Assert.Equal(OccupantArchetypes.Select(x => x.Label).Distinct(StringComparer.Ordinal).Count(), OccupantArchetypes.Length);
        Assert.Equal(IncomingArchetypes.Select(x => x.Label).Distinct(StringComparer.Ordinal).Count(), IncomingArchetypes.Length);
        Assert.Equal(OccupantArchetypes.Length, ExpectedDecideMatrix.GetLength(0));
        Assert.Equal(IncomingArchetypes.Length, ExpectedDecideMatrix.GetLength(1));

        var failures = new List<string>();
        for (var o = 0; o < OccupantArchetypes.Length; o++)
        for (var i = 0; i < IncomingArchetypes.Length; i++)
        {
            var (oLabel, occupant) = OccupantArchetypes[o];
            var (iLabel, incoming) = IncomingArchetypes[i];
            var (expectedVerdict, expectPreempt) = ExpectedDecideMatrix[o, i];

            var encounter = RunningOccupancyArbiter.Decide(occupant, incoming);
            if (encounter.Verdict != expectedVerdict)
                failures.Add($"[{oLabel} x {iLabel}] Verdict expected={expectedVerdict}, actual={encounter.Verdict}");
            if ((encounter.PreemptTargetInstanceId is not null) != expectPreempt)
                failures.Add($"[{oLabel} x {iLabel}] PreemptTargetNotNull expected={expectPreempt}, actual={encounter.PreemptTargetInstanceId is not null}");
            if (string.IsNullOrWhiteSpace(encounter.Reason))
                failures.Add($"[{oLabel} x {iLabel}] Reason is empty");
        }

        Assert.True(failures.Count == 0,
            $"Decide prototype cross-product had {failures.Count} mismatch(es):\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void Decide_PreemptNowCells_BindOccupantIdentityAndUseAtLeastThreeDistinctTargets()
    {
        var targets = new List<string>();
        for (var o = 0; o < OccupantArchetypes.Length; o++)
        for (var i = 0; i < IncomingArchetypes.Length; i++)
        {
            if (!ExpectedDecideMatrix[o, i].ExpectPreemptTarget) continue;

            var (oLabel, occupant) = OccupantArchetypes[o];
            var (iLabel, incoming) = IncomingArchetypes[i];
            var encounter = RunningOccupancyArbiter.Decide(occupant, incoming);

            Assert.Equal(V.PreemptNow, encounter.Verdict);
            Assert.True(encounter.PreemptTargetInstanceId == occupant.ExecutionInstanceId,
                $"[{oLabel} x {iLabel}] target={encounter.PreemptTargetInstanceId}, occupant={occupant.ExecutionInstanceId}");
            Assert.False(string.IsNullOrWhiteSpace(encounter.PreemptTargetInstanceId));
            targets.Add(encounter.PreemptTargetInstanceId!);
        }

        Assert.Equal(19, targets.Count);
        Assert.True(targets.Distinct(StringComparer.Ordinal).Count() >= 3,
            $"PreemptNow 目标原型不足 3 个：{string.Join(",", targets.Distinct(StringComparer.Ordinal))}");
    }

    [Fact]
    public void Decide_UntrustedOccupantWithValidGuid_StillHoldsBeforeIncomingComparison()
    {
        var occupant = OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: ValidInstanceId, hasTrustedIdentity: false);

        foreach (var incoming in IncomingArchetypes.Select(x => x.Facts))
        {
            var encounter = RunningOccupancyArbiter.Decide(occupant, incoming);
            Assert.Equal(V.HoldUnknownOccupant, encounter.Verdict);
            Assert.Null(encounter.PreemptTargetInstanceId);
        }
    }

    [Fact]
    public void Decide_ProvenHighestWithMissingLevels_TrustedIncomingHighestPreemptsBeforeHighestClassGate()
    {
        var occupant = OccupiedBind(true, true, tier: null, priority: null);
        var incomingHighest = IncomingArchetypes.Single(x => x.Label == "I2-TrustedHighest").Facts;
        var incomingNormal = IncomingArchetypes.Single(x => x.Label == "I3-TrustedHigher").Facts;

        var preempt = RunningOccupancyArbiter.Decide(occupant, incomingHighest);
        Assert.Equal(V.PreemptNow, preempt.Verdict);
        Assert.Equal(ValidInstanceId, preempt.PreemptTargetInstanceId);

        var wait = RunningOccupancyArbiter.Decide(occupant, incomingNormal);
        Assert.Equal(V.WaitLocally, wait.Verdict);
        Assert.Null(wait.PreemptTargetInstanceId);
    }

    // ============================================================
    // §2  DecideResume 原型叉积矩阵（9 占用者原型 × 10 恢复原型 = 90 格）
    // ============================================================

    private static readonly (string Label, RunningOccupantFacts Facts)[] ResumeOccupantArchetypes =
    {
        ("R01-Unknown", RunningOccupantFacts.Unknown("resume_matrix")),
        ("R02-Idle", RunningOccupantFacts.Idle()),
        ("R03-UntrustedNoId", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: null, hasTrustedIdentity: false)),
        ("R04-UntrustedValidId", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: ValidInstanceId, hasTrustedIdentity: false)),
        ("R05-TrustedNullId", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: null)),
        ("R06-TrustedEmptyGuid", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: Guid.Empty.ToString("N"))),
        ("R07-TrustedMalformedId", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: MalformedInstanceId)),
        ("R08-SameInstance", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: ValidInstanceId)),
        ("R09-DifferentInstance", OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: DifferentInstanceId)),
    };

    private static readonly (string Label, ResumptionFacts Facts)[] ResumptionArchetypes =
    {
        ("S01-InvalidTicketNoInstance", new ResumptionFacts { HasValidTicket = false, SuspendedIdentity = "susp", SuspendedExecutionInstanceId = null }),
        ("S02-InvalidTicketWithInstance", new ResumptionFacts { HasValidTicket = false, SuspendedIdentity = "susp", SuspendedExecutionInstanceId = ValidInstanceId }),
        ("S03-EmptyIdentity", new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "", SuspendedExecutionInstanceId = null }),
        ("S04-WhitespaceIdentity", new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "   ", SuspendedExecutionInstanceId = ValidInstanceId }),
        ("S05-NullInstance", new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "susp", SuspendedExecutionInstanceId = null }),
        ("S06-EmptyGuid", new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "susp", SuspendedExecutionInstanceId = Guid.Empty.ToString("N") }),
        ("S07-MalformedGuid", new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "susp", SuspendedExecutionInstanceId = MalformedInstanceId }),
        ("S08-SameInstanceCanonical", new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "susp", SuspendedExecutionInstanceId = ValidInstanceId }),
        ("S09-SameInstanceEquivalentFormat", new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "susp", SuspendedExecutionInstanceId = Guid.Parse(ValidInstanceId).ToString("D").ToUpperInvariant() }),
        ("S10-DifferentInstance", new ResumptionFacts { HasValidTicket = true, SuspendedIdentity = "susp", SuspendedExecutionInstanceId = DifferentInstanceId }),
    };

    private static readonly ResumeVerdict[,] ExpectedResumeMatrix =
    {
        // S01 无效票据无实例 / S02 无效票据有实例 / S03 空身份 / S04 空白身份 / S05 null 实例 / S06 空 GUID / S07 非法 GUID / S08 同实例规范 / S09 同实例等价格式 / S10 异实例
        { ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown }, // R01 Unknown
        { ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.AllowResume, ResumeVerdict.AllowResume, ResumeVerdict.AllowResume, ResumeVerdict.AllowResume, ResumeVerdict.AllowResume, ResumeVerdict.AllowResume }, // R02 Idle
        { ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown }, // R03 不可信且无实例
        { ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown }, // R04 不可信但合法实例
        { ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown }, // R05 可信标记但实例 null
        { ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown }, // R06 可信标记但空 GUID
        { ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown }, // R07 可信标记但非法 GUID
        { ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.RefuseAlreadyOwner, ResumeVerdict.RefuseAlreadyOwner, ResumeVerdict.RefuseOccupiedByOther }, // R08 同实例
        { ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseTicketInvalid, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.RefuseSuspendedIdentityUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.HoldFactsUnknown, ResumeVerdict.RefuseOccupiedByOther, ResumeVerdict.RefuseOccupiedByOther, ResumeVerdict.RefuseAlreadyOwner }, // R09 异实例
    };

    [Fact]
    public void DecideResume_FullPrototypeCrossProduct_All90CellsMatchExpected()
    {
        Assert.Equal(9, ResumeOccupantArchetypes.Length);
        Assert.Equal(10, ResumptionArchetypes.Length);
        Assert.Equal(ResumeOccupantArchetypes.Select(x => x.Label).Distinct(StringComparer.Ordinal).Count(), ResumeOccupantArchetypes.Length);
        Assert.Equal(ResumptionArchetypes.Select(x => x.Label).Distinct(StringComparer.Ordinal).Count(), ResumptionArchetypes.Length);
        Assert.Equal(ResumeOccupantArchetypes.Length, ExpectedResumeMatrix.GetLength(0));
        Assert.Equal(ResumptionArchetypes.Length, ExpectedResumeMatrix.GetLength(1));

        var failures = new List<string>();
        for (var o = 0; o < ResumeOccupantArchetypes.Length; o++)
        for (var s = 0; s < ResumptionArchetypes.Length; s++)
        {
            var (oLabel, occupant) = ResumeOccupantArchetypes[o];
            var (sLabel, resumption) = ResumptionArchetypes[s];
            var actual = RunningOccupancyArbiter.DecideResume(occupant, resumption);
            if (actual != ExpectedResumeMatrix[o, s])
                failures.Add($"[{oLabel} x {sLabel}] expected={ExpectedResumeMatrix[o, s]}, actual={actual}");
        }

        Assert.True(failures.Count == 0,
            $"DecideResume prototype cross-product had {failures.Count} mismatch(es):\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void DecideResume_UntrustedOccupantWithValidGuid_StillHoldsFactsUnknown()
    {
        var occupant = OccupiedBind(false, false, ArbitrationTier.Plan, 0,
            instanceId: ValidInstanceId, hasTrustedIdentity: false);

        foreach (var resumption in ResumptionArchetypes.Where(x => x.Facts.HasValidTicket && !string.IsNullOrWhiteSpace(x.Facts.SuspendedIdentity)).Select(x => x.Facts))
        {
            Assert.Equal(ResumeVerdict.HoldFactsUnknown,
                RunningOccupancyArbiter.DecideResume(occupant, resumption));
        }
    }

    [Fact]
    public void DecideResume_InvalidTicketWithNonEmptyIdentity_IsRejectedBeforeOccupancyInspection()
    {
        var resumption = ResumptionArchetypes.Single(x => x.Label == "S02-InvalidTicketWithInstance").Facts;
        Assert.Equal(ResumeVerdict.RefuseTicketInvalid,
            RunningOccupancyArbiter.DecideResume(ResumeOccupantArchetypes.Single(x => x.Label == "R02-Idle").Facts, resumption));
        Assert.Equal(ResumeVerdict.RefuseTicketInvalid,
            RunningOccupancyArbiter.DecideResume(ResumeOccupantArchetypes.Single(x => x.Label == "R08-SameInstance").Facts, resumption));
    }

    // ============================================================
    // §3  SelectNextFromWaitSet 对抗性排序与全键不变量
    // ============================================================

    [Fact]
    public void SelectNext_AdversarialInput_OrdersByHoeingThenTierThenPriorityThenScheduleThenIdentity_ForWholeSet()
    {
        var early = DateTimeOffset.Parse("2026-09-24T09:00:00+08:00");
        var late = early.AddHours(1);
        var input = new[]
        {
            new WaitingRequestFacts { CandidateId = "p1", StableIdentity = "e", Tier = ArbitrationTier.Plan, Priority = int.MaxValue, ScheduledAt = early },
            new WaitingRequestFacts { CandidateId = "f1", StableIdentity = "d", Tier = ArbitrationTier.Fixed, Priority = int.MaxValue, ScheduledAt = early },
            new WaitingRequestFacts { CandidateId = "s4", StableIdentity = "c", Tier = ArbitrationTier.System, Priority = 50, ScheduledAt = null },
            new WaitingRequestFacts { CandidateId = "s3", StableIdentity = "b", Tier = ArbitrationTier.System, Priority = 50, ScheduledAt = early },
            new WaitingRequestFacts { CandidateId = "s1", StableIdentity = "a", Tier = ArbitrationTier.System, Priority = 99, ScheduledAt = late },
            new WaitingRequestFacts { CandidateId = "s2", StableIdentity = "z", Tier = ArbitrationTier.System, Priority = 99, ScheduledAt = early },
            new WaitingRequestFacts { CandidateId = "h1", StableIdentity = "A", IsHoeingHighest = true, Tier = ArbitrationTier.Plan, Priority = 0, ScheduledAt = late },
            new WaitingRequestFacts { CandidateId = "nr", StableIdentity = "0", Tier = ArbitrationTier.System, Priority = int.MaxValue, ScheduledAt = early, PrerequisiteReady = false },
        };
        var expected = new[] { "h1", "s2", "s1", "s3", "s4", "f1", "p1" };

        var actual = new List<string>();
        var remaining = input.ToList();
        while (true)
        {
            var next = RunningOccupancyArbiter.SelectNextFromWaitSet(remaining);
            if (next is null) break;
            actual.Add(next.CandidateId);
            remaining.Remove(next);
        }

        Assert.Equal(expected, actual);
    }

    [Fact]
    public void SelectNext_StableIdentityUsesOrdinalBeforeCandidateId()
    {
        var sameTime = DateTimeOffset.Parse("2026-09-24T09:00:00+08:00");
        var upper = new WaitingRequestFacts { CandidateId = "z", StableIdentity = "A", Tier = ArbitrationTier.Plan, Priority = 0, ScheduledAt = sameTime };
        var lower = new WaitingRequestFacts { CandidateId = "A", StableIdentity = "a", Tier = ArbitrationTier.Plan, Priority = 0, ScheduledAt = sameTime };

        Assert.Equal("z", RunningOccupancyArbiter.SelectNextFromWaitSet(new[] { lower, upper })!.CandidateId);
        Assert.Equal("z", RunningOccupancyArbiter.SelectNextFromWaitSet(new[] { upper, lower })!.CandidateId);
    }

    [Fact]
    public void SelectNext_CandidateIdIsUltimateOrdinalTiebreak()
    {
        var sameTime = DateTimeOffset.Parse("2026-09-24T09:00:00+08:00");
        var lowerId = new WaitingRequestFacts { CandidateId = "a", StableIdentity = "same", Tier = ArbitrationTier.Plan, Priority = 0, ScheduledAt = sameTime };
        var upperId = new WaitingRequestFacts { CandidateId = "A", StableIdentity = "same", Tier = ArbitrationTier.Plan, Priority = 0, ScheduledAt = sameTime };

        Assert.Equal("A", RunningOccupancyArbiter.SelectNextFromWaitSet(new[] { lowerId, upperId })!.CandidateId);
        Assert.Equal("A", RunningOccupancyArbiter.SelectNextFromWaitSet(new[] { upperId, lowerId })!.CandidateId);
    }

    [Fact]
    public void SelectNext_NotReadyCandidatesNeverWin()
    {
        var ready = new WaitingRequestFacts { CandidateId = "ready", StableIdentity = "r", Tier = ArbitrationTier.Plan, Priority = 0, PrerequisiteReady = true };
        var notReady = new WaitingRequestFacts { CandidateId = "nr", StableIdentity = "n", Tier = ArbitrationTier.System, Priority = 99, PrerequisiteReady = false };

        Assert.Equal("ready", RunningOccupancyArbiter.SelectNextFromWaitSet(new[] { notReady, ready })!.CandidateId);
        Assert.Null(RunningOccupancyArbiter.SelectNextFromWaitSet(new[] { notReady }));
    }

    [Fact]
    public void SelectNext_NullOrEmptyReturnsNull()
    {
        Assert.Null(RunningOccupancyArbiter.SelectNextFromWaitSet(null));
        Assert.Null(RunningOccupancyArbiter.SelectNextFromWaitSet(Array.Empty<WaitingRequestFacts>()));
    }

    // ============================================================
    // §4  FromStatus 映射、优先级和载荷字段
    // ============================================================

    [Fact]
    public void FromStatus_StateTableAndPrecedence_AllCellsMatchExpected()
    {
        var freshIdle = FreshStatus(false);
        var freshIdleHoeing = FreshStatus(false);
        freshIdleHoeing.AutoHoeingRunning = true;
        var freshRunningWithoutExecution = FreshStatus(true);
        freshRunningWithoutExecution.AutoHoeingRunning = true;
        freshRunningWithoutExecution.CurrentTaskName = "fallback";

        var validExecution = new TaskExecutionIdentitySnapshot(
            Guid.Parse(ValidInstanceId), 7, Guid.Parse("11111111222233334444555566667777"),
            Guid.Parse("99999999888877776666555544443333"), "Group", "Ext", "test", true);
        var freshRunningWithIdentity = FreshStatus(true);
        freshRunningWithIdentity.AutoHoeingRunning = true;
        freshRunningWithIdentity.CurrentExecution = validExecution;

        var freshRunningEmptyGuid = FreshStatus(true);
        freshRunningEmptyGuid.CurrentExecution = new TaskExecutionIdentitySnapshot(
            Guid.Empty, 1, Guid.Empty, null, "Group", "Ext", "empty", false);

        var ledgerOccupiedWithExecution = FreshStatus(true);
        ledgerOccupiedWithExecution.AutoHoeingRunning = true;
        ledgerOccupiedWithExecution.CurrentExecution = validExecution;

        var ledgerOccupiedWithEmptyGuid = FreshStatus(true);
        ledgerOccupiedWithEmptyGuid.CurrentExecution = new TaskExecutionIdentitySnapshot(
            Guid.Empty, 1, Guid.Empty, null, "Group", "Ext", "empty", false);

        var cases = new (string Label, ControlStatus? Status, bool LedgerOccupied, bool LedgerUnknown,
            bool EpochVerified, OccupantFactsState ExpectedState, string? ExpectedReference,
            bool ExpectedTrusted, bool ExpectedHoeing, string? ExpectedExecutionInstanceId)[]
        {
            ("LedgerUnreadableOverridesAll", null, true, true, false, OccupantFactsState.Unknown, "external_start_ledger_unreadable", false, false, null),
            ("StatusNullBeforeEpoch", null, false, false, false, OccupantFactsState.Unknown, "bgi_status_unavailable", false, false, null),
            ("EpochUnverifiedBeforeStale", MakeStaleStatus(), false, false, false, OccupantFactsState.Unknown, "bgi_epoch_unverified", false, false, null),
            ("StaleAfterVerified", MakeStaleStatus(), false, false, true, OccupantFactsState.Unknown, "bgi_status_stale", false, false, null),
            ("StaleBeforeLedgerOccupied", MakeStaleStatus(), true, false, true, OccupantFactsState.Unknown, "bgi_status_stale", false, false, null),
            ("StatusNullBeforeLedgerOccupied", null, true, false, true, OccupantFactsState.Unknown, "bgi_status_unavailable", false, false, null),
            ("LedgerOccupiedIdleSnapshot", freshIdle, true, false, true, OccupantFactsState.Occupied, "external_start_ledger_accepted_unfinished", false, false, null),
            ("LedgerOccupiedRunningWithValidExecution", ledgerOccupiedWithExecution, true, false, true, OccupantFactsState.Occupied, "external_start_ledger_accepted_unfinished", false, true, null),
            ("LedgerOccupiedRunningWithEmptyGuidExecution", ledgerOccupiedWithEmptyGuid, true, false, true, OccupantFactsState.Occupied, "external_start_ledger_accepted_unfinished", false, false, null),
            ("FreshIdle", freshIdle, false, false, true, OccupantFactsState.Idle, null, false, false, null),
            ("FreshIdleWithHoeingFlag", freshIdleHoeing, false, false, true, OccupantFactsState.Idle, null, false, false, null),
            ("FreshRunningTrustedIdentity", freshRunningWithIdentity, false, false, true, OccupantFactsState.Occupied, null, true, true, ValidInstanceId),
            ("FreshRunningEmptyGuid", freshRunningEmptyGuid, false, false, true, OccupantFactsState.Occupied, null, false, false, Guid.Empty.ToString("N")),
            ("FreshRunningNoExecution", freshRunningWithoutExecution, false, false, true, OccupantFactsState.Occupied, null, false, true, null),
            ("LedgerUnreadableOverridesValidStatus", freshRunningWithIdentity, true, true, true, OccupantFactsState.Unknown, "external_start_ledger_unreadable", false, false, null),
            ("EpochUnverifiedOverridesLedgerOccupied", ledgerOccupiedWithExecution, true, false, false, OccupantFactsState.Unknown, "bgi_epoch_unverified", false, false, null),
        };

        Assert.Equal(16, cases.Length);
        var failures = new List<string>();
        foreach (var (label, status, ledgerOccupied, ledgerUnknown, epochVerified, expectedState,
                     expectedReference, expectedTrusted, expectedHoeing, expectedExecutionInstanceId) in cases)
        {
            var facts = RunningOccupantFacts.FromStatus(status, ledgerOccupied, ledgerUnknown, epochVerified);
            if (facts.State != expectedState)
                failures.Add($"[{label}] State expected={expectedState}, actual={facts.State}");
            if (!string.Equals(facts.Reference, expectedReference, StringComparison.Ordinal))
                failures.Add($"[{label}] Reference expected={expectedReference ?? "<null>"}, actual={facts.Reference ?? "<null>"}");
            if (facts.HasTrustedIdentity != expectedTrusted)
                failures.Add($"[{label}] HasTrustedIdentity expected={expectedTrusted}, actual={facts.HasTrustedIdentity}");
            if (facts.HoeingClass != expectedHoeing)
                failures.Add($"[{label}] HoeingClass expected={expectedHoeing}, actual={facts.HoeingClass}");
            if (!string.Equals(facts.ExecutionInstanceId, expectedExecutionInstanceId, StringComparison.Ordinal))
                failures.Add($"[{label}] ExecutionInstanceId expected={expectedExecutionInstanceId ?? "<null>"}, actual={facts.ExecutionInstanceId ?? "<null>"}");
        }

        Assert.True(failures.Count == 0,
            $"FromStatus state table had {failures.Count} mismatch(es):\n{string.Join("\n", failures)}");
    }

    [Fact]
    public void FromStatus_LedgerOccupiedWithExecution_DoesNotLeakUntrustedExecutionPayload()
    {
        var status = FreshStatus(true);
        status.AutoHoeingRunning = true;
        status.CurrentTaskName = "fallback-must-not-be-used";
        status.CurrentExecution = new TaskExecutionIdentitySnapshot(
            Guid.Parse(ValidInstanceId), 7, Guid.Parse("11111111222233334444555566667777"),
            Guid.Parse("99999999888877776666555544443333"), "Group", "Ext", "explicit-must-not-be-used", true);

        var facts = RunningOccupantFacts.FromStatus(status, ledgerOccupied: true, ledgerUnknown: false,
            bgiEpochVerified: true);

        Assert.Equal(OccupantFactsState.Occupied, facts.State);
        Assert.Equal("external_start_ledger_accepted_unfinished", facts.Reference);
        Assert.False(facts.HasTrustedIdentity);
        Assert.Null(facts.ExecutionInstanceId);
        Assert.Null(facts.RunId);
        Assert.Null(facts.JobId);
        Assert.Null(facts.Kind);
        Assert.Null(facts.Source);
        Assert.Null(facts.Name);
        Assert.True(facts.HoeingClass);
        Assert.False(facts.StopRequested);
    }

    [Fact]
    public void FromStatus_FreshRunning_PreservesExecutionPayloadAndFallsBackToTaskName()
    {
        var runId = Guid.Parse("11111111222233334444555566667777");
        var jobId = Guid.Parse("99999999888877776666555544443333");
        var status = FreshStatus(true);
        status.CurrentTaskName = "fallback-name";
        status.CurrentExecution = new TaskExecutionIdentitySnapshot(
            Guid.Parse(ValidInstanceId), 7, runId, jobId, "Group", "Ext", "explicit-name", true);

        var facts = RunningOccupantFacts.FromStatus(status, false, false, true);

        Assert.Equal(ValidInstanceId, facts.ExecutionInstanceId);
        Assert.Equal(runId.ToString("N"), facts.RunId);
        Assert.Equal(jobId.ToString("N"), facts.JobId);
        Assert.Equal("Group", facts.Kind);
        Assert.Equal("Ext", facts.Source);
        Assert.Equal("explicit-name", facts.Name);
        Assert.True(facts.StopRequested);

        status.CurrentExecution = null;
        var noExecution = RunningOccupantFacts.FromStatus(status, false, false, true);
        Assert.Null(noExecution.ExecutionInstanceId);
        Assert.Null(noExecution.RunId);
        Assert.Null(noExecution.JobId);
        Assert.Null(noExecution.Kind);
        Assert.Null(noExecution.Source);
        Assert.Equal("fallback-name", noExecution.Name);
        Assert.False(noExecution.StopRequested);
    }

    [Fact]
    public void FromStatus_DirectReturn_LevelFieldsStayUnknownUntilResolverFillsThem()
    {
        var status = FreshStatus(true);
        status.CurrentExecution = new TaskExecutionIdentitySnapshot(
            Guid.Parse(ValidInstanceId), 7, Guid.Parse("11111111222233334444555566667777"),
            null, "Group", "Ext", "test", false);

        var facts = RunningOccupantFacts.FromStatus(status, false, false, true);

        Assert.Null(facts.Tier);
        Assert.Null(facts.Priority);
        Assert.Null(facts.HighestClass);

        var filled = facts.WithLevelFacts(ArbitrationTier.Fixed, 42, true);
        Assert.Equal(ArbitrationTier.Fixed, filled.Tier);
        Assert.Equal(42, filled.Priority);
        Assert.True(filled.HighestClass);
        Assert.Equal(facts.ExecutionInstanceId, filled.ExecutionInstanceId);
        Assert.Equal(facts.HasTrustedIdentity, filled.HasTrustedIdentity);

        var preserved = filled.WithLevelFacts(null, null, null);
        Assert.Equal(ArbitrationTier.Fixed, preserved.Tier);
        Assert.Equal(42, preserved.Priority);
        Assert.True(preserved.HighestClass);
    }

    // ============================================================
    // 辅助方法
    // ============================================================

    private static RunningOccupantFacts OccupiedBind(bool? highestClass, bool hoeing,
        ArbitrationTier? tier, int? priority, string? instanceId = ValidInstanceId,
        bool hasTrustedIdentity = true)
        => new()
        {
            State = OccupantFactsState.Occupied,
            HasTrustedIdentity = hasTrustedIdentity,
            ExecutionInstanceId = instanceId,
            HighestClass = highestClass,
            HoeingClass = hoeing,
            Tier = tier,
            Priority = priority,
        };

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

    private static ControlStatus MakeStaleStatus()
    {
        var status = new ControlStatus
        {
            TaskStatusAvailable = true,
            TaskStatusBgiEpoch = "9:900",
            TaskStatusObservedAtUtc = DateTimeOffset.UtcNow.AddHours(-1),
        };
        status.TaskRunning = false;
        return status;
    }
}


