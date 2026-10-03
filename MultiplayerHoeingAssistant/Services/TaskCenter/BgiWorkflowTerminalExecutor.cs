using System;
using System.Collections.Generic;
using System.Security.Cryptography;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;

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

    public BgiWorkflowTerminalExecutor(BgiExternalClient client, RunStore runs)
        : this(new BgiExternalClientPort(client), runs) { }

    internal BgiWorkflowTerminalExecutor(IBgiExecutionPort port, RunStore runs)
    {
        _port = port ?? throw new ArgumentNullException(nameof(port));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
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
        if (actionName is not ("closeGame" or "closeSoftware" or "closeGameAndSoftware" or "shutdown"))
            return TerminalExecutionResult.RejectedWith($"未知收尾动作：{actionName ?? "(空)"}（动作未执行）");

        if (record.SendAttempted || record.State != "pending" || !string.IsNullOrEmpty(record.JobId))
            return TerminalExecutionResult.UnknownWith(record.JobId, "收尾已有发送责任，只读对账，不补发");
        var epoch = _port.ServerEpoch;
        if (run.StopRequested || run.StopAuthority is not { } authority
            || authority.Epoch != WorkflowStopAuthority.Epoch(epoch)
            || !_port.HasCapability(WorkflowStopAuthority.Capability))
            return TerminalExecutionResult.RejectedWith("停止授权缺失或失效；未发送收尾");

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
        var payload = new
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
            return TerminalExecutionResult.UnknownWith(null, "受理回执缺 taskHandle（协议违例），结果不可考");

        // 受理即持久化 submitted 事实（E3'：accepted 后不可逆，断线/取消按 unknown 对账）
        record.State = "submitted";
        record.JobId = acceptance.TaskHandle;
        _runs.Update(run);

        var result = await ObserveAsync(record, identity, ExecuteBudget, ct).ConfigureAwait(false);
        _runs.Update(run);
        return result;
    }

    private static BgiJobTerminalPolling.FrozenIdentity Identity(PendingCompletionRecord record)
        => new(record.Epoch, record.IdempotencyKey, record.WireRunId, "$flow", 0, record.Occurrence, record.Attempt);

    public async Task<TerminalExecutionResult> ConfirmCancellationAsync(PendingCompletionRecord record, CancellationToken ct)
    {
        if (record.ServerRejectionEvidence is not null && record.ExecutionExitConfirmed && record.ObservedTerminal == "rejected")
            return TerminalExecutionResult.RejectedWith("类型化建job前拒绝，零执行已证明");
        if (!record.SendAttempted && record.State == "pending" && string.IsNullOrEmpty(record.JobId)
            && string.IsNullOrEmpty(record.Fingerprint))
            return TerminalExecutionResult.CancelledWith(null, "收尾意图未发送");
        var identity = Identity(record);
        if (!identity.Complete || WorkflowStopAuthority.Epoch(_port.ServerEpoch) != identity.Epoch) return TerminalExecutionResult.UnknownWith(record.JobId, "缺原身份或连接纪元已变，不取消其他进程");
        if (string.IsNullOrEmpty(record.JobId))
        {
            try
            {
                using var lookup = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                var hit = await BgiJobTerminalPolling.FindOriginalJobAsync(_port, identity, lookup.Token).ConfigureAwait(false);
                if (hit is null) return TerminalExecutionResult.UnknownWith(null, "原键只读对账无唯一命中，不补发");
                record.JobId = hit.JobId;
            }
            catch { return TerminalExecutionResult.UnknownWith(null, "原键只读对账不可考，不补发"); }
        }
        try
        {
            using var cancel = new CancellationTokenSource(TimeSpan.FromSeconds(5));
            if (_port.HasCapability("execution.cancel.identity.v1") && WorkflowStopAuthority.Epoch(_port.ServerEpoch) == identity.Epoch)
                await _port.CancelOriginalJobAsync(record.JobId!, identity, cancel.Token).ConfigureAwait(false);
        }
        catch { /* Always continue independent exit/effect observation. */ }
        using var observation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { return await ObserveAsync(record, identity, TimeSpan.FromSeconds(10), observation.Token).ConfigureAwait(false); }
        catch { return TerminalExecutionResult.UnknownWith(record.JobId, "收尾退出/效果未确认"); }
    }

    private async Task<TerminalExecutionResult> ObserveAsync(PendingCompletionRecord record,
        BgiJobTerminalPolling.FrozenIdentity identity, TimeSpan budget, CancellationToken ct)
    {
        var (outcome, job, reason) = await BgiJobTerminalPolling.PollUntilExitAsync(
            _port, identity, record.JobId!, budget, PollInterval, ct).ConfigureAwait(false);
        if (Identity(record) != identity || record.JobId != job?.JobId && job is not null)
            return TerminalExecutionResult.UnknownWith(record.JobId, "观察期间收尾身份变化");
        if (job is not null)
        {
            record.ObservedTerminal = job.State;
            record.ExecutionExitConfirmed = job.ExecutionExitConfirmed
                && job.ExecutionExitDisposition is "execution_exited" or "never_started";
            record.ExecutionExitDisposition = job.ExecutionExitDisposition;
            record.EffectState = outcome == "unknown" ? "unknown" : job.State;
        }
        return outcome switch
        {
            "succeeded" => TerminalExecutionResult.Executed(record.JobId),
            "cancelled" => TerminalExecutionResult.CancelledWith(record.JobId, reason),
            "rejected" => TerminalExecutionResult.RejectedWith(reason),
            "failed" when job?.ErrorCode == "terminal_conflict" => TerminalExecutionResult.RejectedWith("其他活动作业，动作未执行"),
            _ => TerminalExecutionResult.UnknownWith(record.JobId, reason ?? "收尾退出/效果未知，不补发"),
        };
    }
}