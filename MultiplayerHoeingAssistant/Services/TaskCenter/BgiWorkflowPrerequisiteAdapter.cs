using System;
using System.Collections.Generic;
using System.Linq;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// R4.6 E1'/E2-8' 生产前置适配器（ext.prerequisite.account / ext.prerequisite.redeemCode 作业化客户端）。
/// 引擎先落盘 Intent 记录 → 本适配器填充线协议身份（WireRunId+nodeId+iteration=LoopIteration，occurrence/attempt
/// 作 add-only 扩展字段）+ 幂等键 + 指纹 + expiresAtUtc + bgiEpoch（I4：首次发送即冻结，传输重投同键同载荷同指纹）→
/// 发送前持久化 SendAttempted（发送窗口崩溃/取消不误判「未发送」）→ 受理即持久化 Submitted 事实（jobId）→
/// ext.job.status 对账至终态（统一解释器 InterpretJob，正常轮询与恢复对账同语义）→ 结构化 PrerequisiteResult。
/// I6 无状态：全部状态在 RunStore 记录与客户端能力快照；I3：账号标识仅落 SHA256 截断哈希（原值不落记录）。
/// I4 诚实边界：传输重投当前不实施（发送失败一律 Unknown 保守处置），RedeliveryCount 字段预留。
/// </summary>
public sealed class BgiWorkflowPrerequisiteAdapter : IWorkflowPrerequisiteAdapter
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ExpireWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExecuteBudget = TimeSpan.FromMinutes(5);
    private static readonly TimeSpan ConfirmBudget = TimeSpan.FromSeconds(10);

    private readonly IBgiExecutionPort _port;
    private readonly RunStore _runs;

    public BgiWorkflowPrerequisiteAdapter(BgiExternalClient client, RunStore runs)
        : this(new BgiExternalClientPort(client), runs) { }

    internal BgiWorkflowPrerequisiteAdapter(IBgiExecutionPort port, RunStore runs)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    }

    /// <summary>支持的前置类型 = 对端能力实况（I1：缺 capability 即不支持，Planner 预检据此阻止执行）。</summary>
    public IReadOnlySet<string> SupportedKinds
    {
        get
        {
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            if (_port.HasCapability(BgiExternalClient.CapabilityPrerequisiteAccount)) kinds.Add("prerequisite.account");
            if (_port.HasCapability(BgiExternalClient.CapabilityPrerequisiteRedeemCode)) kinds.Add("prerequisite.redeemCode");
            return kinds;
        }
    }

    public async Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
        WorkflowNodeOccurrence occurrence, CancellationToken ct)
    {
        // 引擎纪律：调用前已落盘 Intent 记录（完整身份匹配：节点+出现+轮次+attempt+策略索引+kind+账号标识）
        var accountKey = RunStore.DeriveAccountKey(strategy.GetString("uid"));
        // 引擎刚落盘的 Intent 记录 = 同出现身份+kind+账号标识的最新一条（单驱动循环串行，无并发歧义）
        var record = run.PrerequisiteActions.LastOrDefault(r =>
            r.NodeId == occurrence.NodeId && r.Occurrence == occurrence.Occurrence
            && r.LoopIteration == occurrence.LoopIteration && r.Attempt == 1
            && r.Kind == strategy.Kind && r.AccountKey == accountKey);
        if (record is not null && (record.SendAttempted || !string.IsNullOrEmpty(record.JobId)))
            return new PrerequisiteResult(PrerequisiteStatus.Unknown, "该意图已有发送责任；仅允许只读对账，不再次提交", record.JobId);
        if (record is null)
            return new PrerequisiteResult(PrerequisiteStatus.Failed, "前置意图记录缺失（违反引擎纪律：先落盘意图再执行）");

        var uid = strategy.GetString("uid");
        if (string.IsNullOrWhiteSpace(uid))
        {
            record.State = PrerequisiteActionState.Failed;
            record.Reason = "缺少 uid 参数（严格合同：空 UID 拒绝；可由同节点 prerequisite.account 提供身份）";
            _runs.Update(run);
            return new PrerequisiteResult(PrerequisiteStatus.Failed, record.Reason);
        }

        var epoch = _port.ServerEpoch; // Capture once; never reread a later epoch into this payload.
        if (run.StopRequested || run.StopAuthority is not { } authority
            || authority.Epoch != WorkflowStopAuthority.Epoch(epoch)
            || !_port.HasCapability(WorkflowStopAuthority.Capability))
            return new PrerequisiteResult(PrerequisiteStatus.Failed, "停止授权缺失或失效；未发送前置");

        // I4：线协议身份/幂等键/指纹/expiresAtUtc/bgiEpoch 首次发送即冻结（指纹覆盖冻结后的完整载荷）
        record.Epoch = authority.Epoch;
        record.WireRunId = run.WireRunId;
        record.TakeoverTicket = _port.TakeoverTicket;
        record.ExpiresAtUtc = DateTimeOffset.UtcNow.Add(ExpireWindow).ToString("O");
        record.IdempotencyKey = RunStore.DeriveSubmissionKey(run.RunId,
            $"{occurrence.NodeId}#{record.StrategyIndex}:{strategy.Kind}:{record.AccountKey}",
            occurrence.Occurrence, occurrence.LoopIteration, 1);
        var operation = strategy.Kind == "prerequisite.account"
            ? BgiExternalClient.ExternalOperations.PrerequisiteAccount
            : BgiExternalClient.ExternalOperations.PrerequisiteRedeemCode;
        var payload = new
        {
            executionContractVersion = 1,
            expectedStopVersion = authority.Version,
            takeoverTicket = record.TakeoverTicket,
            idempotencyKey = record.IdempotencyKey,
            expiresAtUtc = record.ExpiresAtUtc,
            bgiEpoch = new { processId = epoch!.ProcessId, startTicksUtc = epoch.StartTicksUtc },
            workflowRunId = run.WireRunId,
            nodeId = occurrence.NodeId,
            iteration = occurrence.LoopIteration,
            occurrence = occurrence.Occurrence,
            attempt = 1,
            uid,
            bindingCode = strategy.Kind == "prerequisite.account" ? strategy.GetString("bindingCode") : null,
        };
        record.Fingerprint = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(payload)))[..24].ToLowerInvariant();
        // 四轮阻断 1：发送前持久化「发送已尝试」——此后缺 jobId ≠ 未发送（取消确认不得当 Cancelled）
        var identity = Identity(record);
        var fingerprint = record.Fingerprint;
        record.SendAttempted = true;
        _runs.Update(run);

        // 提交：受理回执 = accepted + taskHandle（jobId 别名）；发送 ≠ 完成
        BgiExternalResponse response;
        try
        {
            response = await _port.SendCommandAsync(operation, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; } // 叶子取消交引擎确认链（B3；记录带 SendAttempted 供确认链判定）
        catch (Exception ex)
        {
            return new PrerequisiteResult(PrerequisiteStatus.Unknown,
                $"发送结果不可考（{ex.GetType().Name}），不盲目重发");
        }

        if (!response.Success)
        {
            var proof = BgiServerRejectionEvidence.Verify(response, operation, payload,
                identity.Epoch!, identity.Key!, fingerprint, _port.ServerEpoch);
            if (proof is null || record.Fingerprint != fingerprint || Identity(record) != identity
                || !string.IsNullOrEmpty(record.JobId))
                return new PrerequisiteResult(PrerequisiteStatus.Unknown, "拒绝无同身份类型化零执行证明；原发送责任保留");
            record.ServerRejectionEvidence = proof;
            record.ObservedTerminal = "rejected";
            record.ExecutionExitConfirmed = true;
            record.ExecutionExitDisposition = "server_rejected_before_acceptance";
            record.EffectState = "not_executed";
            record.State = PrerequisiteActionState.Failed;
            record.Reason = "执行端建job前拒绝：" + response.ErrorCode;
            _runs.Update(run);
            return new PrerequisiteResult(PrerequisiteStatus.Rejected, record.Reason);
        }

        var acceptance = BgiJobTerminalPolling.ParseAcceptance(response.Data);
        if (acceptance.AlreadyExecuted)
            return new PrerequisiteResult(PrerequisiteStatus.Unknown, "already_executed不能证明原作业终态及退出；只读对账，不重发");
        if (!acceptance.Accepted || acceptance.TaskHandle is null)
            return new PrerequisiteResult(PrerequisiteStatus.Unknown, "受理回执缺 taskHandle（协议违例），结果不可考");

        // 受理即持久化 submitted 事实（崩溃窗口可按 jobId 对账，不重发）
        record.State = PrerequisiteActionState.Submitted;
        record.JobId = acceptance.TaskHandle;
        _runs.Update(run);

        var result = await ObserveAsync(run, record, identity, ExecuteBudget, ct).ConfigureAwait(false);
        _runs.Update(run);
        return result;
    }

    private static BgiJobTerminalPolling.FrozenIdentity Identity(PrerequisiteActionRecord record)
        => new(record.Epoch, record.IdempotencyKey, record.WireRunId, record.NodeId,
            record.LoopIteration, record.Occurrence, record.Attempt);

    public Task<PrerequisiteResult> ReconcileAsync(PrerequisiteActionRecord record, CancellationToken ct)
    {
        var owner = BgiWorkflowObservationPersistence.FindOwner(_runs, BgiWorkflowObservationPersistence.Binding.Freeze(record), false);
        return owner is null ? Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Unknown, "原意图耐久所属运行不可确认", record.JobId))
            : ReconcileAsync(owner, record, ct);
    }

    public async Task<PrerequisiteResult> ReconcileAsync(WorkflowRunRecord run, PrerequisiteActionRecord record, CancellationToken ct)
    {
        var identity = Identity(record);
        var binding = BgiWorkflowObservationPersistence.Binding.Freeze(record);
        if (!identity.Complete) return new(PrerequisiteStatus.Unknown, "旧记录缺完整冻结身份，不能重基准化或补发", record.JobId);
        if (string.IsNullOrEmpty(record.JobId))
        {
            var hit = await BgiJobTerminalPolling.FindOriginalJobAsync(_port, identity, ct).ConfigureAwait(false);
            if (hit is null) return new(PrerequisiteStatus.Unknown, "原键只读对账无唯一同身份命中，不重发");
            BgiWorkflowObservationPersistence.Save(_runs, run, record, binding, null, hit.JobId!);
        }
        return await ObserveAsync(run, record, identity, ConfirmBudget, ct).ConfigureAwait(false);
    }

    public Task<PrerequisiteResult> ConfirmCancellationAsync(PrerequisiteActionRecord record, CancellationToken ct)
    {
        if (!record.SendAttempted && string.IsNullOrEmpty(record.JobId))
            return Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Cancelled, "意图未离开本机，无远端在飞"));
        var owner = BgiWorkflowObservationPersistence.FindOwner(_runs, BgiWorkflowObservationPersistence.Binding.Freeze(record), false);
        return owner is null ? Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Unknown, "原意图耐久所属运行不可确认", record.JobId))
            : ConfirmCancellationAsync(owner, record, ct);
    }

    public async Task<PrerequisiteResult> ConfirmCancellationAsync(WorkflowRunRecord run, PrerequisiteActionRecord record, CancellationToken ct)
    {
        if (record.ServerRejectionEvidence is not null && record.ExecutionExitConfirmed && record.ObservedTerminal == "rejected")
            return new(PrerequisiteStatus.Rejected, "类型化拒绝已证明零执行");
        if (!record.SendAttempted && string.IsNullOrEmpty(record.JobId))
            return new(PrerequisiteStatus.Cancelled, "意图未离开本机，无远端在飞");
        var identity = Identity(record);
        var binding = BgiWorkflowObservationPersistence.Binding.Freeze(record);
        if (!identity.Complete || WorkflowStopAuthority.Epoch(_port.ServerEpoch) != identity.Epoch) return new(PrerequisiteStatus.Unknown, "缺原身份或连接纪元已变，不能取消其他进程", record.JobId);
        if (string.IsNullOrEmpty(record.JobId))
        {
            try
            {
                using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var hit = await BgiJobTerminalPolling.FindOriginalJobAsync(_port, identity, lookup.Token).ConfigureAwait(false);
                if (hit is null) return new(PrerequisiteStatus.Unknown, "原键只读对账无唯一命中；保持发送责任");
                BgiWorkflowObservationPersistence.Save(_runs, run, record, binding, null, hit.JobId!);
            }
            catch { return new(PrerequisiteStatus.Unknown, "原键只读对账不可考；不重发"); }
        }
        try
        {
            BgiWorkflowObservationPersistence.Save(_runs, run, record, binding, record.JobId, record.JobId!);
        }
        catch { return new(PrerequisiteStatus.Unknown, "原作业耐久绑定未确认，不发送取消", record.JobId); }
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (_port.HasCapability("execution.cancel.identity.v1") && WorkflowStopAuthority.Epoch(_port.ServerEpoch) == identity.Epoch)
                await _port.CancelOriginalJobAsync(record.JobId!, identity, cancel.Token).ConfigureAwait(false);
        }
        catch { /* Cancel RPC failure/timeout never suppresses independent observation. */ }
        using var observation = new CancellationTokenSource(ConfirmBudget);
        try { return await ObserveAsync(run, record, identity, ConfirmBudget, observation.Token).ConfigureAwait(false); }
        catch { return new(PrerequisiteStatus.Unknown, "取消后同身份退出未确认", record.JobId); }
    }

    private async Task<PrerequisiteResult> ObserveAsync(WorkflowRunRecord run, PrerequisiteActionRecord record,
        BgiJobTerminalPolling.FrozenIdentity identity, TimeSpan budget, CancellationToken ct)
    {
        var binding = BgiWorkflowObservationPersistence.Binding.Freeze(record);
        if (binding.Identity != identity) return new(PrerequisiteStatus.Unknown, "冻结前置身份改变，观察未发布", record.JobId);
        var jobId = record.JobId!;
        var (outcome, job, reason) = await BgiJobTerminalPolling.PollUntilExitAsync(
            _port, identity, jobId, budget, PollInterval, ct,
            observed => BgiWorkflowObservationPersistence.Save(_runs, run, record, binding, jobId, jobId, observed)).ConfigureAwait(false);
        if (Identity(record) != identity || record.JobId != job?.JobId && job is not null)
            return new(PrerequisiteStatus.Unknown, "观察期间记录身份变化", record.JobId);
        var result = MapOutcome(outcome, job, reason, record.JobId);
        record.State = result.Status switch { PrerequisiteStatus.Proceed => PrerequisiteActionState.Succeeded,
            PrerequisiteStatus.Cancelled => PrerequisiteActionState.Cancelled,
            PrerequisiteStatus.Unknown => PrerequisiteActionState.Unknown, _ => PrerequisiteActionState.Failed };
        record.Reason = result.Reason;
        return result;
    }

    /// <summary>统一终态 → 结构化结果（四轮阻断 6：错误码不确定性保留，prerequisite_outcome_unknown 不降级为 Failed）。</summary>
    private static PrerequisiteResult MapOutcome(string outcome, BgiJobInfo? job, string? reason, string? jobId)
        => outcome switch
        {
            "succeeded" => new PrerequisiteResult(PrerequisiteStatus.Proceed, null, jobId),
            "rejected" => new PrerequisiteResult(PrerequisiteStatus.Rejected, reason ?? "远端拒绝", jobId),
            "cancelled" => new PrerequisiteResult(PrerequisiteStatus.Cancelled, reason ?? "远端已取消", jobId),
            "failed" => job?.ErrorCode switch
            {
                "account_mismatch" => new PrerequisiteResult(PrerequisiteStatus.Failed,
                    "当前账号与流程声明 UID 不符（account_mismatch）", jobId),
                "prerequisite_outcome_unknown" => new PrerequisiteResult(PrerequisiteStatus.Unknown,
                    "BGI 侧结果不可考（prerequisite_outcome_unknown）", jobId),
                _ => new PrerequisiteResult(PrerequisiteStatus.Failed, $"前置执行失败：{reason}", jobId),
            },
            _ => new PrerequisiteResult(PrerequisiteStatus.Unknown, reason ?? "终态不可考", jobId),
        };
}