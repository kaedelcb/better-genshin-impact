using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;
using Mistletoe.Shared;
using Newtonsoft.Json.Linq;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// R4.6 E3'/B8 生产收尾执行器（ext.terminal.completionAction 作业化客户端）。
/// 引擎在成功边界先落盘 PendingCompletion（state=pending）→ 本执行器填充幂等键/指纹/expiresAtUtc/bgiEpoch
/// （I4：首次发送即冻结，指纹覆盖冻结后的完整载荷）→ 发送前持久化 dispatching（发送窗口取消/崩溃不丢事实）→
/// 受理即持久化 submitted 事实（jobId）→ ext.job.status 对账（统一解释器 InterpretJob）。
/// 进程自杀类动作（closeSoftware/shutdown 等）允许永远 unknown——事实持久保留，禁止自动补发。
/// I6 无状态：全部状态在 RunStore 记录与客户端能力快照。
/// </summary>
public sealed class BgiWorkflowTerminalExecutor : IWorkflowTerminalExecutor
{
    private static readonly TimeSpan PollInterval = TimeSpan.FromSeconds(1);
    private static readonly TimeSpan ExpireWindow = TimeSpan.FromMinutes(10);
    private static readonly TimeSpan ExecuteBudget = TimeSpan.FromMinutes(2);

    private readonly IBgiExecutionPort _port;
    private readonly RunStore _runs;
    private readonly ITerminalEffectObserver _effects;

    public BgiWorkflowTerminalExecutor(BgiExternalClient client, RunStore runs)
        : this(new BgiExternalClientPort(client), runs) { }

    internal BgiWorkflowTerminalExecutor(IBgiExecutionPort port, RunStore runs, ITerminalEffectObserver? effects = null)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
        _effects = effects ?? new WindowsTerminalEffectObserver();
    }

    /// <summary>支持的收尾类型 = 对端能力实况（I1：缺 capability 即不支持，Planner 预检据此阻止执行）。</summary>
    public IReadOnlySet<string> SupportedKinds
        => _port.HasCapability(BgiExternalClient.CapabilityTerminalCompletionAction)
            ? WorkflowKindCatalog.TerminalKinds
            : new HashSet<string>(StringComparer.Ordinal);

    public async Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
        CancellationToken ct)
    {
        var record = run.PendingCompletion
            ?? throw new InvalidOperationException("收尾意图记录缺失（违反引擎纪律：先落盘意图再执行）");
        var actionName = action.GetString("action");
        if (record.Action != actionName)
            return TerminalExecutionResult.RejectedWith("收尾动作与原持久意图不符；未发送收尾");
        if (actionName is not ("closeGame" or "closeSoftware" or "closeGameAndSoftware" or "shutdown"))
            return TerminalExecutionResult.RejectedWith($"未知收尾动作：{actionName ?? "(空)"}（动作未执行）");

        if (record.SendAttempted || record.State != "pending" || !string.IsNullOrEmpty(record.JobId))
            return TerminalExecutionResult.UnknownWith(record.JobId, "收尾已有发送责任，只读对账，不补发");
        var epoch = _port.ServerEpoch;
        if (run.StopRequested || run.StopAuthority is not { } authority
            || authority.Epoch != WorkflowStopAuthority.Epoch(epoch)
            || !_port.HasCapability(WorkflowStopAuthority.Capability))
            return TerminalExecutionResult.RejectedWith("停止授权缺失或失效；未发送收尾");

        var independent = TerminalEffectJournal.NeedsIndependentEffect(actionName);
        if (independent && !_port.HasCapability(TerminalEffectJournal.Capability))
            return TerminalExecutionResult.RejectedWith("执行端缺收尾耐久观察能力；未发送收尾");
        using var originalProcess = independent ? _effects.PinOriginalProcess(epoch!) : null;
        if (independent && originalProcess is null)
            return TerminalExecutionResult.RejectedWith("原执行进程身份不可确认；未发送收尾");

        // I4：幂等键/指纹/expiresAtUtc/bgiEpoch 首次发送即冻结（传输重投同键同载荷同指纹）
        if (string.IsNullOrEmpty(record.IdempotencyKey))
        {
            record.IdempotencyKey = RunStore.DeriveSubmissionKey(run.RunId, "$flow", 0, 0, 1);
            record.ExpiresAtUtc = DateTimeOffset.UtcNow.Add(ExpireWindow).ToString("O");
        }
        record.Epoch = authority.Epoch;
        record.WireRunId = run.WireRunId;
        record.TakeoverTicket = _port.TakeoverTicket;
        record.Occurrence = 0; // E1' 扩展字段：收尾身份 occurrence=0 / attempt=1
        record.Attempt = 1;
        if (independent) record.TerminalEffectToken = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var rawPayload = new
        {
            executionContractVersion = 1,
            expectedStopVersion = authority.Version,
            takeoverTicket = record.TakeoverTicket,
            idempotencyKey = record.IdempotencyKey,
            expiresAtUtc = record.ExpiresAtUtc,
            bgiEpoch = new { processId = epoch!.ProcessId, startTicksUtc = epoch.StartTicksUtc },
            workflowRunId = run.WireRunId,
            nodeId = "$flow", // B1：收尾身份 nodeId=$flow、iteration=动作序号（0 起）
            iteration = 0,
            occurrence = 0,
            attempt = 1,
            action = actionName,
        };
        object payload = rawPayload;
        if (independent)
        {
            var terminalPayload = JObject.FromObject(rawPayload);
            terminalPayload["terminalEffectToken"] = record.TerminalEffectToken;
            record.TerminalRequestFingerprint = TerminalEffectJournal.Fingerprint(
                BgiExternalClient.ExternalOperations.TerminalCompletionAction, terminalPayload);
            payload = JsonSerializer.Deserialize<JsonElement>(terminalPayload.ToString(Newtonsoft.Json.Formatting.None));
        }
        record.Fingerprint = Convert.ToHexString(SHA256.HashData(
            JsonSerializer.SerializeToUtf8Bytes(payload)))[..24].ToLowerInvariant();
        // 四轮阻断 2：发送前持久化 dispatching——此后 OCE/崩溃绝不按「未发送」清除意图
        var identity = Identity(record);
        var fingerprint = record.Fingerprint;
        record.SendAttempted = true;
        record.State = "dispatching";
        _runs.Update(run);

        BgiExternalResponse response;
        try
        {
            response = await _port.SendCommandAsync(
                BgiExternalClient.ExternalOperations.TerminalCompletionAction, payload, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; } // B8：dispatching 事实交引擎外层标 unknown，禁止补发
        catch (Exception ex)
        {
            if (independent)
                return await ObserveIndependentAsync(run, record, originalProcess, ExecuteBudget, ct).ConfigureAwait(false);
            return TerminalExecutionResult.UnknownWith(null, $"发送结果不可考（{ex.GetType().Name}），禁止补发");
        }

        if (!response.Success)
        {
            var proof = BgiServerRejectionEvidence.Verify(response, BgiExternalClient.ExternalOperations.TerminalCompletionAction,
                payload, identity.Epoch!, identity.Key!, fingerprint, _port.ServerEpoch);
            if (proof is null || record.Fingerprint != fingerprint || Identity(record) != identity
                || !string.IsNullOrEmpty(record.JobId))
                return TerminalExecutionResult.UnknownWith(record.JobId, "拒绝无同身份类型化零执行证明；原发送责任保留");
            record.ServerRejectionEvidence = proof;
            record.ObservedTerminal = "rejected";
            record.ExecutionExitConfirmed = true;
            record.ExecutionExitDisposition = "server_rejected_before_acceptance";
            record.EffectState = "not_executed";
            record.State = "rejected"; // Keep attempted request/proof; never revert dispatching to unsent pending.
            _runs.Update(run);
            return TerminalExecutionResult.RejectedWith("执行端建job前拒绝：" + response.ErrorCode);
        }

        var acceptance = BgiJobTerminalPolling.ParseAcceptance(response.Data);
        if (acceptance.AlreadyExecuted)
            return TerminalExecutionResult.UnknownWith(null, "already_executed不能证明退出及效果；仅只读对账，不补发");
        if (!acceptance.Accepted || acceptance.TaskHandle is null)
        {
            if (independent)
                return await ObserveIndependentAsync(run, record, originalProcess, ExecuteBudget, ct).ConfigureAwait(false);
            return TerminalExecutionResult.UnknownWith(null, "受理回执缺 taskHandle（协议违例），结果不可考");
        }

        // 受理即持久化 submitted 事实（E3'：accepted 后不可逆，断线/取消按 unknown 对账）
        record.State = "submitted";
        record.JobId = acceptance.TaskHandle;
        _runs.Update(run);

        var result = independent
            ? await ObserveIndependentAsync(run, record, originalProcess, ExecuteBudget, ct).ConfigureAwait(false)
            : await ObserveAsync(run, record, identity, ExecuteBudget, ct).ConfigureAwait(false);
        _runs.Update(run);
        return result;
    }

    private static BgiJobTerminalPolling.FrozenIdentity Identity(PendingCompletionRecord record)
        => new(record.Epoch, record.IdempotencyKey, record.WireRunId, "$flow", 0, record.Occurrence, record.Attempt);

    public Task<TerminalExecutionResult> ConfirmCancellationAsync(PendingCompletionRecord record, CancellationToken ct)
    {
        if (!record.SendAttempted && record.State == "pending" && string.IsNullOrEmpty(record.JobId) && string.IsNullOrEmpty(record.Fingerprint))
            return Task.FromResult(TerminalExecutionResult.CancelledWith(null, "收尾意图未发送"));
        var owner = BgiWorkflowObservationPersistence.FindOwner(_runs, BgiWorkflowObservationPersistence.Binding.Freeze(record), true);
        return owner is null ? Task.FromResult(TerminalExecutionResult.UnknownWith(record.JobId, "原收尾耐久所属运行不可确认"))
            : ConfirmCancellationAsync(owner, record, ct);
    }

    public async Task<TerminalExecutionResult> ConfirmCancellationAsync(WorkflowRunRecord run, PendingCompletionRecord record, CancellationToken ct)
    {
        if (record.ServerRejectionEvidence is not null && record.ExecutionExitConfirmed && record.ObservedTerminal == "rejected")
            return TerminalExecutionResult.RejectedWith("类型化建job前拒绝，零执行已证明");
        if (!record.SendAttempted && record.State == "pending" && string.IsNullOrEmpty(record.JobId)
            && string.IsNullOrEmpty(record.Fingerprint))
            return TerminalExecutionResult.CancelledWith(null, "收尾意图未发送");
        var identity = Identity(record);
        var binding = BgiWorkflowObservationPersistence.Binding.Freeze(record);
        if (TerminalEffectJournal.ValidToken(record.TerminalEffectToken)
            && TryPublishIndependent(run, record, null) is { } independent) return independent;
        if (!identity.Complete || WorkflowStopAuthority.Epoch(_port.ServerEpoch) != identity.Epoch) return TerminalExecutionResult.UnknownWith(record.JobId, "缺原身份或连接纪元已变，不取消其他进程");
        if (string.IsNullOrEmpty(record.JobId))
        {
            try
            {
                using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var hit = await BgiJobTerminalPolling.FindOriginalJobAsync(_port, identity, lookup.Token).ConfigureAwait(false);
                if (hit is null) return TerminalExecutionResult.UnknownWith(null, "原键只读对账无唯一命中，不补发");
                BgiWorkflowObservationPersistence.Save(_runs, run, record, binding, null, hit.JobId!);
            }
            catch { return TerminalExecutionResult.UnknownWith(null, "原键只读对账不可考，不补发"); }
        }
        try
        {
            BgiWorkflowObservationPersistence.Save(_runs, run, record, binding, record.JobId, record.JobId!);
        }
        catch { return TerminalExecutionResult.UnknownWith(record.JobId, "原作业耐久绑定未确认，不发送取消"); }
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (_port.HasCapability("execution.cancel.identity.v1") && WorkflowStopAuthority.Epoch(_port.ServerEpoch) == identity.Epoch)
                await _port.CancelOriginalJobAsync(record.JobId!, identity, cancel.Token).ConfigureAwait(false);
        }
        catch { /* Always continue independent exit/effect observation. */ }
        using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { return await ObserveAsync(run, record, identity, TimeSpan.FromSeconds(10), observation.Token).ConfigureAwait(false); }
        catch { return TerminalExecutionResult.UnknownWith(record.JobId, "收尾退出/效果未确认"); }
    }

    private async Task<TerminalExecutionResult> ObserveAsync(WorkflowRunRecord run, PendingCompletionRecord record,
        BgiJobTerminalPolling.FrozenIdentity identity, TimeSpan budget, CancellationToken ct)
    {
        var binding = BgiWorkflowObservationPersistence.Binding.Freeze(record);
        if (binding.Identity != identity) return TerminalExecutionResult.UnknownWith(record.JobId, "冻结收尾身份改变，观察未发布");
        var jobId = record.JobId!;
        var (outcome, job, reason) = await BgiJobTerminalPolling.PollUntilExitAsync(
            _port, identity, jobId, budget, PollInterval, ct,
            observed => BgiWorkflowObservationPersistence.Save(_runs, run, record, binding, jobId, jobId, observed)).ConfigureAwait(false);
        if (Identity(record) != identity || record.JobId != job?.JobId && job is not null)
            return TerminalExecutionResult.UnknownWith(record.JobId, "观察期间收尾身份变化");
        return outcome switch
        {
            "succeeded" => TerminalExecutionResult.Executed(record.JobId),
            "cancelled" => TerminalExecutionResult.CancelledWith(record.JobId, reason),
            "rejected" => TerminalExecutionResult.RejectedWith(reason),
            "failed" when job?.ErrorCode == "terminal_conflict" => TerminalExecutionResult.RejectedWith("其他活动作业，动作未执行"),
            _ => TerminalExecutionResult.UnknownWith(record.JobId, reason ?? "收尾退出/效果未知，不补发"),
        };
    }

    private TerminalExecutionResult? TryPublishIndependent(WorkflowRunRecord run, PendingCompletionRecord record, IDisposable? originalProcess)
    {
        var binding = BgiWorkflowObservationPersistence.Binding.Freeze(record);
        var proof = _effects.Observe(record, originalProcess);
        if (proof is null || !TerminalEffectJournal.ProofMatches(proof, record.TerminalEffectToken!, record.Epoch,
            record.JobId, record.IdempotencyKey, record.WireRunId, record.Action, record.TerminalRequestFingerprint)) return null;
        BgiWorkflowObservationPersistence.SaveTerminalEffect(_runs, run, record, binding, proof);
        return TerminalExecutionResult.Executed(record.JobId);
    }

    // No IPC, cancellation or resubmission on restart. The original independent proof is authoritative.
    internal static bool ReconcileDurableEffect(RunStore runs, WorkflowRunRecord run, ITerminalEffectObserver? observer = null)
    {
        if (run.PendingCompletion is not { SendAttempted: true } record
            || !TerminalEffectJournal.ValidToken(record.TerminalEffectToken)) return false;
        var binding = BgiWorkflowObservationPersistence.Binding.Freeze(record);
        var proof = (observer ?? new WindowsTerminalEffectObserver()).Observe(record, null);
        if (proof is null) return false;
        BgiWorkflowObservationPersistence.SaveTerminalEffect(runs, run, record, binding, proof);
        var finalized = runs.UpdateMergingIf(run.RunId, latest =>
        {
            if (latest.PendingCompletion is not { } live || BgiWorkflowObservationPersistence.Binding.Freeze(live) != binding
                || !TerminalReleaseEvidence.CompletionSettled(latest, live) || !live.BodyCompletedBeforeTerminal
                || latest.StopRequested) return false;
            var previous = latest.State; latest.State = WorkflowRunState.Succeeded;
            if (!TerminalReleaseEvidence.RunSettled(latest)) { latest.State = previous; return false; }
            latest.CompletionHistory.Add(live); latest.PendingCompletion = null;
            return true;
        }, out var saved);
        if (finalized && saved is not null) RunStore.RebaseOnto(run, saved);
        return true;
    }

    private async Task<TerminalExecutionResult> ObserveIndependentAsync(WorkflowRunRecord run, PendingCompletionRecord record,
        IDisposable? originalProcess, TimeSpan budget, CancellationToken ct)
    {
        var binding = BgiWorkflowObservationPersistence.Binding.Freeze(record);
        var until = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < until)
        {
            ct.ThrowIfCancellationRequested();
            if (BgiWorkflowObservationPersistence.Binding.Freeze(record) != binding)
                return TerminalExecutionResult.UnknownWith(record.JobId, "收尾冻结载荷改变，不发布观察");
            if (TryPublishIndependent(run, record, originalProcess) is { } observed) return observed;
            // IPC can prove a conflict/rejection/cancellation while still connected. Successful destructive
            // effects must use the independent observer even if a remote terminal word says succeeded.
            if (!string.IsNullOrEmpty(record.JobId) && WorkflowStopAuthority.Epoch(_port.ServerEpoch) == record.Epoch)
            {
                try
                {
                    var (_, job) = await _port.QueryJobStatusAsync(record.JobId, ct).ConfigureAwait(false);
                    if (WorkflowStopAuthority.Epoch(_port.ServerEpoch) == record.Epoch && job is not null
                        && WorkflowStopAuthority.Epoch(job.Epoch) == record.Epoch && job.JobId == record.JobId && Identity(record).Matches(job)
                        && job.State is "failed" or "rejected" or "cancelled" && job.ExecutionExitConfirmed)
                    {
                        BgiWorkflowObservationPersistence.Save(_runs, run, record, binding, record.JobId, record.JobId, job);
                        return job.State == "cancelled" ? TerminalExecutionResult.CancelledWith(record.JobId, job.ErrorMessage)
                            : TerminalExecutionResult.RejectedWith(job.ErrorMessage ?? job.ErrorCode ?? "收尾失败");
                    }
                }
                catch (OperationCanceledException) { throw; }
                catch { }
            }
            // OS shutdown is observable only after the next OS start; do not hold the UI for two minutes.
            if (record.Action == "shutdown") return TerminalExecutionResult.UnknownWith(record.JobId, "关机效果将在下次启动后只读核验；不补发");
            await Task.Delay(PollInterval, ct).ConfigureAwait(false);
        }
        return TerminalExecutionResult.UnknownWith(record.JobId, "独立收尾效果未确认；原责任保留，不补发");
    }
}
