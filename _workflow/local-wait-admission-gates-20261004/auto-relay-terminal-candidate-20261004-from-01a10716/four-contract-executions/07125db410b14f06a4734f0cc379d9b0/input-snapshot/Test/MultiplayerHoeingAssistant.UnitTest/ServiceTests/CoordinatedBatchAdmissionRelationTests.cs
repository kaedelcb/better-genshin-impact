using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// **R5 批次 8：联机全队批次直发与统一准入的关系**（不接生产门）。
/// 范围声明：本文件是**未接线纯函数（`BatchReconcileDecider.Decide`／`CoordinatedBatchOutcome.Classify`）
/// 与仲裁器（`RunningOccupancyArbiter.Decide`）的契约夹具**，外加对指定直发文件的**文本**结构守卫。
/// 它**不**观测直发路径（`MainViewModel.CoordinatedBatch.cs`）的实际发送、超时或收尾行为；
/// 该路径的审计结论另行陈述（见 §24.104），不由此夹具证明。
/// 夹具所锁定的安全语义：
/// ①接受未知时不产生第二次执行身份（同一请求键重查/重发，且不自行换键）；
/// ②已受理句柄消失时按"未证退出"收口，**绝不重放**；
/// ③纪元变化使在飞项失效并退回待提交；
/// ④终态分类里"缺失/未知"永远不是成功。
/// 说明：直发路径**不经过**助手统一准入面（`ArbitrationAdmissionService`／`ExternalStartAdmission`），
/// 与 owner B.1 最高优先级/抢占合同的关系见 R5.3 §24.104（缺口已登记，修复需 owner 放行）。
/// 该"不经过"结论由本文件的**文本守卫**与 §24.104 的审计陈述共同支撑，不等于运行调用链证明。
/// </summary>
public sealed class CoordinatedBatchAdmissionRelationTests
{
    private static readonly DateTime Now = new(2026, 9, 24, 12, 0, 0, DateTimeKind.Utc);

    private static BatchExpectedItem Submitted(string name, string? jobId = null, int attempts = 1)
    {
        var item = new BatchExpectedItem(name, isOneDragon: false) { State = BatchItemState.Submitted, SubmitAttempts = attempts };
        item.JobId = jobId;
        return item;
    }

    private static BatchJobObservation Observed(string jobId, string state, string? errorCode = null, bool cancelled = false, string? idempotencyKey = null)
        => new(jobId, "组A", 1, state, cancelled, errorCode, idempotencyKey);

    [Theory]
    [InlineData("pending", false, null, false, "")]
    [InlineData("running", false, null, false, "")]
    [InlineData("completed", false, null, false, "succeeded")]
    [InlineData("completed", true, null, false, "cancelled")]
    [InlineData("queueCancelled", false, null, false, "cancelled")]
    [InlineData("queueCancelled", true, null, false, "cancelled")]
    [InlineData("failed", false, "hoeing_incomplete", false, "failed")]
    [InlineData("failed", false, "task_cancelled", false, "unknown")]   // 非 stopping 的 task_cancelled 不构成停止证据
    [InlineData("failed", false, null, false, "unknown")]               // 缺失错误码不得当失败（更不得当成功）
    [InlineData("mystery_state", false, null, false, "unknown")]
    // stopping 且未取消：completed 仍是"确认成功"（stop 与结果正交，不因停止意图改判成功为 stopped）
    [InlineData("completed", false, null, true, "succeeded")]
    [InlineData("completed", true, null, true, "stopped")]
    [InlineData("queueCancelled", false, null, true, "stopped")]
    [InlineData("failed", false, "task_cancelled", true, "stopped")]
    public void CoordinatedBatchOutcome_Classify_NeverTreatsAbsenceOrUnknownAsSuccess(
        string status, bool cancelled, string? errorCode, bool stopping, string expected)
    {
        var terminal = new BgiTaskQueueStatus { Status = status, Cancelled = cancelled, ErrorCode = errorCode };

        Assert.Equal(expected, CoordinatedBatchOutcome.Classify(terminal, stopping));
    }

    [Fact]
    public void CoordinatedBatchOutcome_Classify_NullOrEmptyStatusIsUnknownNotSuccess()
    {
        // 观测缺失（作业已被淘汰/不可读）⇒ unknown，绝不等于成功
        Assert.Equal("unknown", CoordinatedBatchOutcome.Classify(null, stopping: false));
        Assert.Equal("unknown", CoordinatedBatchOutcome.Classify(new BgiTaskQueueStatus { Status = null }, false));
    }

    [Fact]
    public void BatchReconcile_UnknownAcceptance_ReturnsResubmitForSameRequestKey_ThisTickOnly()
    {
        // 已提交但无 jobId、快照里也找不到（应答丢失）：只能在**同一请求键**下重发，不换键、不新增执行身份
        var items = new List<BatchExpectedItem> { Submitted("组A") };
        var requestKeyBefore = items[0].RequestKey;

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now);

        var resubmit = Assert.Single(actions.OfType<BatchReconcileAction.Resubmit>());
        Assert.Equal(0, resubmit.Index);
        Assert.Equal(requestKeyBefore, items[0].RequestKey); // 请求键不变（不产生第二次身份）
    }

    [Fact]
    public void BatchReconcile_UnknownAcceptance_ExhaustedAttempts_ConfirmsResultUnknownWithoutResubmit()
    {
        var items = new List<BatchExpectedItem> { Submitted("组A", attempts: BatchReconcileDecider.MaxSubmitAttempts) };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now);

        Assert.Empty(actions.OfType<BatchReconcileAction.Resubmit>());
        var confirm = Assert.Single(actions.OfType<BatchReconcileAction.ConfirmTerminal>());
        Assert.Equal(0, confirm.Index);
        Assert.False(confirm.Cancelled);
        Assert.Equal("result_unknown", confirm.ErrorCode);
    }

    [Fact]
    public void BatchReconcile_KnownHandleDisappears_ReturnsLostJobConfirm_WithoutSubmitOrResubmitThisTick()
    {
        // 已受理句柄消失（终态淘汰/权限丢失）**不证明未执行** ⇒ 按未证退出收口；本拍不返回 Submit/Resubmit
        var items = new List<BatchExpectedItem> { Submitted("组A", jobId: Guid.NewGuid().ToString("N")) };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now);

        Assert.Empty(actions.OfType<BatchReconcileAction.Resubmit>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Submit>());
        var confirm = Assert.Single(actions.OfType<BatchReconcileAction.ConfirmTerminal>());
        Assert.Equal("lost_job", confirm.ErrorCode);
    }

    [Fact]
    public void BatchReconcile_EpochChanged_ReturnsEpochChanged_WithoutSubmitOrResubmitThisTick()
    {
        var items = new List<BatchExpectedItem> { Submitted("组A", jobId: Guid.NewGuid().ToString("N")) };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: false, generation: 1, nowUtc: Now);

        Assert.Single(actions.OfType<BatchReconcileAction.EpochChanged>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Resubmit>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Submit>());
    }

    [Fact]
    public void BatchReconcile_AttachesExistingJobByRequestKey_ReturnsNoSubmitThisTick()
    {
        // 重复广播/应答丢失后按请求键找回：只 Attach，不 Submit
        var items = new List<BatchExpectedItem> { Submitted("组A") };
        var jobId = Guid.NewGuid().ToString("N");

        var actions = BatchReconcileDecider.Decide(items, [Observed(jobId, "running", idempotencyKey: items[0].RequestKey)], epochMatch: true, generation: 1, nowUtc: Now);

        var attach = Assert.Single(actions.OfType<BatchReconcileAction.Attach>());
        Assert.Equal(0, attach.Index);
        Assert.Equal(jobId, attach.JobId);
        Assert.Empty(actions.OfType<BatchReconcileAction.Submit>());
    }

    [Fact]
    public void BatchReconcile_LostJobConfirmation_ReturnsCompleteWithUnresolved_NotCompleteSucceeded()
    {
        // R5 批次 13（D5，owner 2026-09-24 裁决 A 的落地）：完成类型拆分的**反例先行夹具**。
        // 旧行为（_CurrentlyAlsoReturnsComplete_ExposedNotEndorsed）：已知句柄消失 ⇒ ConfirmTerminal("lost_job")，
        // 同一拍"全部确认"分支再返回**无载荷**的 Complete；而该 Complete 的既有注释关联
        // "完成后动作（RunSpecified 收尾）"——等于把"未证退出"当成"可以执行完成动作"。
        // 新语义（D5）：只有**全部项均以成功终态确认**时才产生 CompleteSucceeded；
        // 任何"有未确认/未决结果"的收口（此处 lost_job）只产生 CompleteWithUnresolved，
        // 且**绝不**产生 CompleteSucceeded，也不把未知改写为成功或已证实失败。
        var items = new List<BatchExpectedItem> { Submitted("组A", jobId: Guid.NewGuid().ToString("N")) };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now);

        // 先落地本拍动作（调用方语义镜像）：ConfirmTerminal(lost_job) 会把该项标为 TerminalConfirmed；
        // 再看**收口之后**的拍次产出什么类型的完成动作，避免把"同一拍的确认+完成"混读为同一帧事实。
        ApplyCallerSemantics(items, actions);
        var tick2 = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now.AddSeconds(1));

        Assert.Equal("lost_job", Assert.Single(actions.OfType<BatchReconcileAction.ConfirmTerminal>()).ErrorCode);
        // 新语义：有未决项 ⇒ 只能是 CompleteWithUnresolved（同一批次的后续拍）
        var unresolved = Assert.Single(tick2.OfType<BatchReconcileAction.CompleteWithUnresolved>());
        // 关键安全断言：不得产出"可执行 RunSpecified 收尾"的全成功完成
        Assert.Empty(tick2.OfType<BatchReconcileAction.CompleteSucceeded>());
        // 未决项必须可枚举（承载证据，不得改写成成功或已证实失败）
        var entry = Assert.Single(unresolved.Unresolved);
        Assert.Equal(0, entry.Index);
        Assert.Equal("组A", entry.Name);
        Assert.Equal("lost_job", entry.ErrorCode);
        Assert.False(entry.Cancelled);
        Assert.DoesNotContain(actions, a => a is BatchReconcileAction.AbortUserCancelled);
    }

    [Fact]
    public void BatchReconcile_ExhaustedAttemptsUnknownResult_ReturnsCompleteWithUnresolved_NotCompleteSucceeded()
    {
        // 同上：重提耗尽 ⇒ ConfirmTerminal("result_unknown")，收口只能是有未决的完成。
        // 未知结果**不得**被改写为成功；也**不得**被改写为已证实失败（errorCode 原样保留 result_unknown）。
        var items = new List<BatchExpectedItem> { Submitted("组A", attempts: BatchReconcileDecider.MaxSubmitAttempts) };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now);

        Assert.Equal("result_unknown", Assert.Single(actions.OfType<BatchReconcileAction.ConfirmTerminal>()).ErrorCode);
        // [D5 同拍] 单期望项：确认与收口在同一拍发生，同拍就不得授予收尾许可（与 lost_job 同拍反例同强度）。
        Assert.Empty(actions.OfType<BatchReconcileAction.CompleteSucceeded>());
        var unresolvedSameTick = Assert.Single(actions.OfType<BatchReconcileAction.CompleteWithUnresolved>());
        var sameTickEntry = Assert.Single(unresolvedSameTick.Unresolved);
        Assert.Equal("result_unknown", sameTickEntry.ErrorCode);
        // 下一拍：已确认的未决结论保持（幂等复现，不退化为 CompleteSucceeded）
        ApplyCallerSemantics(items, actions);
        var tick2 = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now.AddSeconds(1));

        Assert.Empty(tick2.OfType<BatchReconcileAction.CompleteSucceeded>());
        var unresolved = Assert.Single(tick2.OfType<BatchReconcileAction.CompleteWithUnresolved>());
        var entry = Assert.Single(unresolved.Unresolved);
        Assert.Equal("组A", entry.Name);
        Assert.Equal("result_unknown", entry.ErrorCode);
        Assert.False(entry.Cancelled);
    }

    [Fact]
    public void CoordinatedBatch_DoesNotReferenceUnifiedAdmissionSymbols()
    {
        // 结构守卫（审计结论的机械证据）：**指定直发文件** `MainViewModel.CoordinatedBatch.cs`
        // 的**原始文本**中不含统一准入面符号；若日后接线，本守卫会红——届时必须连同
        // §24.104 的合同说明一起更新。局限：只覆盖该单一文件的文本，注释提及符号也会红，
        // 且不能证明所有 partial 文件或运行调用链均未经过统一准入。
        var root = RepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "MultiplayerHoeingAssistant", "ViewModels", "MainViewModel.CoordinatedBatch.cs"));

        Assert.DoesNotContain("ArbitrationAdmissionService", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ExternalStartAdmission", text, StringComparison.Ordinal);
        Assert.Contains("SubmitTaskStartAsync", text, StringComparison.Ordinal); // 确认扫的是直发路径本体
    }

    // ------------------------------------------------------------------
    // 交错夹具（R5 批次 8 ②）：全队批次 × E1/E3 × 最高级 × 未知占用 × 请求键冲突。
    // 夹具只断言**仲裁器/决策器返回值**（保守停驻判定、本拍不返回提交动作），不观测实际发送、不改生产接线。
    // ------------------------------------------------------------------

    private const string OccupantInstance = "11111111-1111-1111-1111-111111111111";

    private static RunningOccupantFacts Occupied(bool trustedIdentity = true, bool hoeingClass = true, bool? highestClass = null,
        ArbitrationTier? tier = null, int? priority = null, string? instanceId = OccupantInstance)
        => new()
        {
            State = OccupantFactsState.Occupied,
            Reference = "test_occupant",
            HasTrustedIdentity = trustedIdentity,
            ExecutionInstanceId = instanceId,
            HoeingClass = hoeingClass,
            HighestClass = highestClass,
            Tier = tier,
            Priority = priority,
        };

    private static IncomingRequestFacts Incoming(bool highest, ArbitrationTier tier = ArbitrationTier.Plan, int priority = 0,
        bool trustedIdentity = true)
        => new() { IsHoeingHighest = highest, Tier = tier, Priority = priority, HasTrustedIdentity = trustedIdentity };

    [Fact]
    public void Interleave_BatchArrivesWhileHoeingOccupantUnprovenHighest_HoldsWithoutPreemptTarget()
    {
        // 联机全队批次属"锄地类"但**不能证明**是 owner B.1 的两类最高级（房间授权/请求键不是最高级来源）
        // ⇒ 到来者不得据此抢占；占用者又无法证明是否最高级 ⇒ 保守停驻、零新发送。
        var encounter = RunningOccupancyArbiter.Decide(
            Occupied(hoeingClass: true, highestClass: null),
            Incoming(highest: false));

        Assert.Equal(RunningEncounterVerdict.HoldUnknownOccupant, encounter.Verdict);
        Assert.Null(encounter.PreemptTargetInstanceId);
    }

    [Fact]
    public void Interleave_UntrustedIncomingIdentity_HoldsWithoutPreemptTarget()
    {
        // 远程/房间自报 key（HasTrustedIdentity=false）不构成级别证明 ⇒ 不得抢占，按本地等待处理。
        var encounter = RunningOccupancyArbiter.Decide(
            Occupied(highestClass: false, tier: ArbitrationTier.Plan, priority: 0),
            Incoming(highest: true, trustedIdentity: false));

        Assert.Equal(RunningEncounterVerdict.WaitLocally, encounter.Verdict);
        Assert.Null(encounter.PreemptTargetInstanceId);
    }

    [Fact]
    public void Interleave_ProvenHighestOccupantVsOrdinaryIncoming_WaitsLocally()
    {
        // 占用者已证明为最高级锄地类：普通任务不得打断 ⇒ 本地持久等待（不是 BGI 已入队/已受理）。
        var encounter = RunningOccupancyArbiter.Decide(
            Occupied(highestClass: true),
            Incoming(highest: false, tier: ArbitrationTier.System, priority: 100));

        Assert.Equal(RunningEncounterVerdict.WaitLocally, encounter.Verdict);
        Assert.Null(encounter.PreemptTargetInstanceId);
    }

    [Fact]
    public void Interleave_TwoHighestClassMeet_LaterArrivalPreemptsWithBindableIdentity()
    {
        // 两类最高级相遇（owner B.1）：同级由后来者打断，但**必须**能绑定被切任务身份用于退出确认。
        var encounter = RunningOccupancyArbiter.Decide(Occupied(highestClass: true), Incoming(highest: true));

        Assert.Equal(RunningEncounterVerdict.PreemptNow, encounter.Verdict);
        Assert.Equal(OccupantInstance, encounter.PreemptTargetInstanceId);
    }

    [Fact]
    public void Interleave_HighestArrivalWithoutBindableOccupantIdentity_HoldsWithZeroPreempt()
    {
        // 占用者身份不可核验（无受信身份）⇒ 即便到来者是最高级也必须停驻：不得返回可抢占目标。
        var encounter = RunningOccupancyArbiter.Decide(
            Occupied(trustedIdentity: false, instanceId: null),
            Incoming(highest: true));

        Assert.Equal(RunningEncounterVerdict.HoldUnknownOccupant, encounter.Verdict);
        Assert.Null(encounter.PreemptTargetInstanceId);
    }

    [Fact]
    public void Interleave_UnknownOccupancyFacts_HoldFactsUnknown_WithoutPreemptTarget()
    {
        // 未知占用事实（快照缺失/纪元未核验/台账不可读）**不是空闲**：判定为 HoldFactsUnknown，本拍不返回可抢占目标。
        foreach (var reference in new[] { "bgi_status_unavailable", "bgi_epoch_unverified", "external_start_ledger_unreadable" })
        {
            var encounter = RunningOccupancyArbiter.Decide(RunningOccupantFacts.Unknown(reference), Incoming(highest: true));

            Assert.Equal(RunningEncounterVerdict.HoldFactsUnknown, encounter.Verdict);
            Assert.Null(encounter.PreemptTargetInstanceId);
        }
    }

    [Fact]
    public void Interleave_LowerClassIncomingGoesToLocalWaitSet_NotBgiQueue()
    {
        // 低优先级到来者：本地持久等待（非 BGI 入队/已受理），当前结束后按等待集合次序重新比较。
        var encounter = RunningOccupancyArbiter.Decide(
            Occupied(hoeingClass: false, highestClass: false, tier: ArbitrationTier.System, priority: 5),
            Incoming(highest: false, tier: ArbitrationTier.Plan, priority: 5));
        Assert.Equal(RunningEncounterVerdict.WaitLocally, encounter.Verdict);

        var next = RunningOccupancyArbiter.SelectNextFromWaitSet(
        [
            new WaitingRequestFacts { CandidateId = "batch", StableIdentity = "batch", Tier = ArbitrationTier.Plan, Priority = 5 },
            new WaitingRequestFacts { CandidateId = "oneDragon", StableIdentity = "oneDragon", IsHoeingHighest = true, Tier = ArbitrationTier.System, Priority = 0 },
        ]);

        Assert.Equal("oneDragon", next?.CandidateId); // 最高级锄地类优先于普通计划层
    }

    [Fact]
    public void Interleave_RequestKeyReuseAfterLostAck_KeepsSingleExecutionIdentity()
    {
        // 全队批次与 E3 外部启动共享同一请求键语义：应答丢失后**只能同键重查/重发**，绝不换键产生第二次执行身份。
        var item = Submitted("组A");
        var key = item.RequestKey;

        var tick1 = BatchReconcileDecider.Decide([item], [], epochMatch: true, generation: 1, nowUtc: Now);
        Assert.Single(tick1.OfType<BatchReconcileAction.Resubmit>());
        Assert.Equal(key, item.RequestKey);

        // 下一拍快照里同键作业出现 ⇒ 只 Attach，不 Submit（不产生第二次执行身份）
        var jobId = Guid.NewGuid().ToString("N");
        var tick2 = BatchReconcileDecider.Decide([item],
            [Observed(jobId, "running", idempotencyKey: key)], epochMatch: true, generation: 1, nowUtc: Now);
        Assert.Empty(tick2.OfType<BatchReconcileAction.Submit>());
        Assert.Empty(tick2.OfType<BatchReconcileAction.Resubmit>());
        Assert.Equal(jobId, Assert.Single(tick2.OfType<BatchReconcileAction.Attach>()).JobId);
    }

    [Fact]
    public void Interleave_DuplicateBroadcastWithDifferentRequestKey_IsNotSilentlyMerged()
    {
        // 重复广播但键不同（例如两端各自生成键）：既有纯函数**不会**把它当成同一作业——
        // 按各自键独立对账（不静默合并、不互相 Attach），因此不得把"键相同"当作跨端最高级证明。
        var a = Submitted("组A");
        var b = Submitted("组A");
        Assert.NotEqual(a.RequestKey, b.RequestKey);

        var jobA = Guid.NewGuid().ToString("N");
        var actions = BatchReconcileDecider.Decide([a, b],
            [Observed(jobA, "running", idempotencyKey: a.RequestKey)], epochMatch: true, generation: 1, nowUtc: Now);

        var attach = Assert.Single(actions.OfType<BatchReconcileAction.Attach>());
        Assert.Equal(0, attach.Index);
        Assert.Equal(jobA, attach.JobId);
        // 第二项（不同键）不得被静默附着到 A 的作业上
        Assert.DoesNotContain(actions.OfType<BatchReconcileAction.Attach>(), x => x.Index == 1);
    }

    [Fact]
    public void Interleave_EpochChange_ReturnsEpochChangedOnly_NoSubmitThisTick()
    {
        // 纪元变化（BGI 重启）：本拍只返回 EpochChanged，不返回 Submit/Resubmit/Attach；跨拍重放不在此夹具范围。
        var items = new List<BatchExpectedItem> { Submitted("组A", jobId: Guid.NewGuid().ToString("N")) };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: false, generation: 1, nowUtc: Now);

        Assert.Single(actions.OfType<BatchReconcileAction.EpochChanged>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Submit>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Resubmit>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Attach>());
    }
    // ------------------------------------------------------------------
    // [D5 / R5 批次 13] 完成类型拆分的正向与边界夹具：
    // ①全成功 ⇒ CompleteSucceeded（唯一可执行 RunSpecified 收尾的完成动作）；
    // ②未决/取消收口 ⇒ CompleteWithUnresolved 且**不**产生 CompleteSucceeded；
    // ③空批次归类（CompleteSucceeded，空集合全成功为真，另见调用方空批次守卫）；
    // ④"此前各拍已确认的取消项"归 CompleteWithUnresolved（本拍取消走 AbortUserCancelled，不到本分支）。
    // 局限：只断言决策器返回值，不观测生产接线/发送/收尾行为（生产仍零消费点）。
    // ------------------------------------------------------------------

    [Fact]
    public void BatchReconcile_AllItemsSucceeded_ReturnsCompleteSucceededOnly()
    {
        var items = new List<BatchExpectedItem> { Confirmed("组A"), Confirmed("组B") };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now);

        Assert.Single(actions.OfType<BatchReconcileAction.CompleteSucceeded>());
        Assert.Empty(actions.OfType<BatchReconcileAction.CompleteWithUnresolved>());
    }

    [Fact]
    public void BatchReconcile_NeverStartedItem_ReturnsCompleteWithUnresolved()
    {
        // 业务拒绝（如配置组不存在）：Started=false 却已终态确认 ⇒ 从未真正执行，
        // 不得据此执行 RunSpecified 收尾（2026-09-13 空批次事故的同类风险，见 BatchExpectedItem.Started 注解）。
        var business = new BatchExpectedItem("组A", isOneDragon: false)
        {
            State = BatchItemState.TerminalConfirmed,
            Started = false,
            TerminalErrorCode = "group_not_found",
        };
        var items = new List<BatchExpectedItem> { Confirmed("组B"), business };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now);

        Assert.Empty(actions.OfType<BatchReconcileAction.CompleteSucceeded>());
        var unresolved = Assert.Single(actions.OfType<BatchReconcileAction.CompleteWithUnresolved>());
        var entry = Assert.Single(unresolved.Unresolved);
        Assert.Equal(1, entry.Index);
        Assert.Equal("组A", entry.Name);
        Assert.Equal("group_not_found", entry.ErrorCode);
    }

    [Fact]
    public void BatchReconcile_PreviouslyConfirmedCancellation_ReturnsCompleteWithUnresolved()
    {
        // 用户取消收尾（TerminalWasCancelled=true）：不是成功收尾 ⇒ 只能是 CompleteWithUnresolved。
        // 本拍取消的项在第 257–264 行已提前 return（AbortUserCancelled），故此处覆盖"此前各拍已确认"的可达情形。
        var cancelled = new BatchExpectedItem("组A", isOneDragon: false)
        {
            State = BatchItemState.TerminalConfirmed,
            Started = true,
            TerminalWasCancelled = true,
        };
        var items = new List<BatchExpectedItem> { cancelled, Confirmed("组B") };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now);

        Assert.Empty(actions.OfType<BatchReconcileAction.CompleteSucceeded>());
        var unresolved = Assert.Single(actions.OfType<BatchReconcileAction.CompleteWithUnresolved>());
        var entry = Assert.Single(unresolved.Unresolved);
        Assert.True(entry.Cancelled);
        Assert.Equal("组A", entry.Name);
        Assert.Null(entry.ErrorCode);
    }

    [Fact]
    public void BatchReconcile_EmptyBatch_ReturnsCompleteSucceeded_VacuousTruth()
    {
        // [D5] 空批次归类 CompleteSucceeded：全成功判据在空集合上为真，且无未决项。
        // 这不等于"真的锄过地"——空批次误触发 RunSpecified 的那次实机事故由调用方的
        // "全部项未启动"守卫兜住（本决策器只回答"有无未决结果"）。
        var actions = BatchReconcileDecider.Decide([], [], epochMatch: true, generation: 1, nowUtc: Now);

        Assert.Single(actions.OfType<BatchReconcileAction.CompleteSucceeded>());
        Assert.Empty(actions.OfType<BatchReconcileAction.CompleteWithUnresolved>());
    }

    [Fact]
    public void BatchReconcile_LastItemLostJob_SameTick_ReturnsCompleteWithUnresolved_NotCompleteSucceeded()
    {
        // [D5 必改反例 / gpt-6-sol 第 1 项] 未决判据必须按**本拍确认动作与既有状态合成后**的逐项结果分类：
        // 最后一项在本拍才被 ConfirmTerminal("lost_job") 时，items 上的 TerminalErrorCode 在本拍仍是 null（动作未应用），
        // 若只读旧字段就会在同一拍产出 CompleteSucceeded —— 直接违反"只有全成功才能执行 RunSpecified 收尾"。
        // 本夹具锁定"同一拍"的完成类型（不是只看下一拍），故它能抓住该类误判。
        var items = new List<BatchExpectedItem>
        {
            Confirmed("组A"),
            Submitted("组B", jobId: Guid.NewGuid().ToString("N")),
        };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now);

        Assert.Equal("lost_job",
            Assert.Single(actions.OfType<BatchReconcileAction.ConfirmTerminal>()).ErrorCode);
        // 同拍：不得授予收尾许可
        Assert.Empty(actions.OfType<BatchReconcileAction.CompleteSucceeded>());
        var unresolved = Assert.Single(actions.OfType<BatchReconcileAction.CompleteWithUnresolved>());
        var entry = Assert.Single(unresolved.Unresolved);
        Assert.Equal(1, entry.Index);
        Assert.Equal("组B", entry.Name);
        Assert.Equal("lost_job", entry.ErrorCode);
    }

    [Fact]
    public void BatchReconcile_LastItemSucceeded_SameTick_ReturnsCompleteSucceeded()
    {
        // 对照组：同一形态但最后一项真成功 ⇒ 同拍必须授予收尾许可（证明拆分不是"一律不完成"）。
        var items = new List<BatchExpectedItem>
        {
            Confirmed("组A"),
            Submitted("组B", jobId: Guid.NewGuid().ToString("N")),
        };

        var actions = BatchReconcileDecider.Decide(items,
            [Observed(items[1].JobId!, "succeeded")], epochMatch: true, generation: 1, nowUtc: Now);

        Assert.Single(actions.OfType<BatchReconcileAction.CompleteSucceeded>());
        Assert.Empty(actions.OfType<BatchReconcileAction.CompleteWithUnresolved>());
    }

    [Fact]
    public void BatchReconcile_ReprojectedSuccessTicket_DoesNotRegressOnRepeatTicks()
    {
        // 水平触发对账：同一"全部可证成功"的批次在后续拍重复返回 CompleteSucceeded（幂等，未决列表恒为空）。
        var items = new List<BatchExpectedItem> { Confirmed("组A") };

        Assert.Single(BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now)
            .OfType<BatchReconcileAction.CompleteSucceeded>());
        Assert.Single(BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now.AddSeconds(1))
            .OfType<BatchReconcileAction.CompleteSucceeded>());
    }

    /// <summary>调用方语义镜像（**只**做本文件用得到的子集）：ConfirmTerminal 推进为已确认终态；
    /// Submit 之后**受理成立**才记 `Started=true`。`Started` 的生产定义是「真正被 BGI 接受启动过」
    /// （`BatchReconcilePlan.cs` 第 43–46 行），故镜像是「提交动作 ⇒ 视为本条已被受理」的**夹具假设**，
    /// **不是**「发出提交意图即等受理事实」；真实调用方（未接线）须以应答／幂等命中／附着找回为受理证据。
    /// 业务拒绝（配置组不存在等）应保持 `Started=false`。</summary>
    private static void ApplyCallerSemantics(List<BatchExpectedItem> items, IReadOnlyList<BatchReconcileAction> actions)
    {
        foreach (var action in actions)
        {
            switch (action)
            {
                case BatchReconcileAction.Submit s:
                    items[s.Index].State = BatchItemState.Submitted;
                    items[s.Index].SubmitAttempts++;
                    items[s.Index].Started = true;
                    break;
                case BatchReconcileAction.ConfirmTerminal c:
                    items[c.Index].State = BatchItemState.TerminalConfirmed;
                    items[c.Index].TerminalWasCancelled = c.Cancelled;
                    items[c.Index].TerminalErrorCode = c.ErrorCode;
                    break;
            }
        }
    }

    /// <summary>已确认成功终态项（Started ∧ TerminalConfirmed ∧ 未取消 ∧ 无 errorCode）。</summary>
    private static BatchExpectedItem Confirmed(string name)
        => new(name, isOneDragon: false)
        {
            State = BatchItemState.TerminalConfirmed,
            Started = true,
        };

    private static string RepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null)
        {
            if (File.Exists(Path.Combine(dir.FullName, "MultiplayerHoeingAssistant", "Services", "CommandExecutor.cs")))
                return dir.FullName;
            dir = dir.Parent;
        }

        throw new InvalidOperationException("未能定位仓库根目录。");
    }
}
