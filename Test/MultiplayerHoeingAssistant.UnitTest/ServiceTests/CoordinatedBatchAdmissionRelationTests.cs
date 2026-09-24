using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>
/// **R5 批次 8：联机全队批次直发与统一准入的关系**（不接生产门）。
/// 本文件锁定"全队批次直发"路径在**当前真实实现**下的安全语义：
/// ①接受未知时不产生第二次执行身份（同一请求键重查/重发，且不自行换键）；
/// ②已受理句柄消失时按"未证退出"收口，**绝不重放**；
/// ③纪元变化使在飞项失效并退回待提交；
/// ④终态分类里"缺失/未知"永远不是成功。
/// 说明：该路径**不经过**助手统一准入面（`ArbitrationAdmissionService`／`ExternalStartAdmission`），
/// 与 owner B.1 最高优先级/抢占合同的关系见 R5.3 §24.104（缺口已登记，修复需 owner 放行）。
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
    public void BatchReconcile_UnknownAcceptance_ResubmitsSameRequestKeyOnly()
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
    public void BatchReconcile_KnownHandleDisappears_ConfirmsLostJobWithoutReplay()
    {
        // 已受理句柄消失（终态淘汰/权限丢失）**不证明未执行** ⇒ 按未证退出收口，绝不重发
        var items = new List<BatchExpectedItem> { Submitted("组A", jobId: Guid.NewGuid().ToString("N")) };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: true, generation: 1, nowUtc: Now);

        Assert.Empty(actions.OfType<BatchReconcileAction.Resubmit>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Submit>());
        var confirm = Assert.Single(actions.OfType<BatchReconcileAction.ConfirmTerminal>());
        Assert.Equal("lost_job", confirm.ErrorCode);
    }

    [Fact]
    public void BatchReconcile_EpochChanged_InvalidatesInFlightWithoutResubmitInSameTick()
    {
        var items = new List<BatchExpectedItem> { Submitted("组A", jobId: Guid.NewGuid().ToString("N")) };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: false, generation: 1, nowUtc: Now);

        Assert.Single(actions.OfType<BatchReconcileAction.EpochChanged>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Resubmit>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Submit>());
    }

    [Fact]
    public void BatchReconcile_AttachesExistingJobByRequestKey_WithoutSubmittingAgain()
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
    public void CoordinatedBatch_DoesNotReferenceUnifiedAdmissionSymbols()
    {
        // 结构守卫（审计结论的机械证据）：全队批次直发路径**不引用**统一准入面；
        // 若日后接线，本守卫会红——届时必须连同 §24.104 的合同说明一起更新。
        var root = RepoRoot();
        var text = File.ReadAllText(Path.Combine(root, "MultiplayerHoeingAssistant", "ViewModels", "MainViewModel.CoordinatedBatch.cs"));

        Assert.DoesNotContain("ArbitrationAdmissionService", text, StringComparison.Ordinal);
        Assert.DoesNotContain("ExternalStartAdmission", text, StringComparison.Ordinal);
        Assert.Contains("SubmitTaskStartAsync", text, StringComparison.Ordinal); // 确认扫的是直发路径本体
    }

    // ------------------------------------------------------------------
    // 交错夹具（R5 批次 8 ②）：全队批次 × E1/E3 × 最高级 × 未知占用 × 请求键冲突。
    // 夹具只断言**现有真实行为**（零发送／保守停驻／不重复发送），不改生产接线。
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
    public void Interleave_BatchArrivesWhileHoeingOccupantUnprovenHighest_HoldsLocallyWithoutSending()
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
    public void Interleave_BatchWithSelfReportedKeyIsNotTrustedSource_HoldsWithoutPreempting()
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
    public void Interleave_UnknownOccupancyFactsAreNeverIdle_ZeroSend()
    {
        // 未知占用事实（快照缺失/纪元未核验/台账不可读）**不是空闲**：不得按常规准入放行。
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
    public void Interleave_UnknownOccupantThenEpochChange_NoReplayAcrossEpoch()
    {
        // 纪元变化（BGI 重启）与未知占用叠加：在飞项失效并退回待提交，本拍零发送；不跨纪元重放。
        var items = new List<BatchExpectedItem> { Submitted("组A", jobId: Guid.NewGuid().ToString("N")) };

        var actions = BatchReconcileDecider.Decide(items, [], epochMatch: false, generation: 1, nowUtc: Now);

        Assert.Single(actions.OfType<BatchReconcileAction.EpochChanged>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Submit>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Resubmit>());
        Assert.Empty(actions.OfType<BatchReconcileAction.Attach>());
    }
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
