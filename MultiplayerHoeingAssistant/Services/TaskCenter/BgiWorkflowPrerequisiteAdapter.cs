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

    private readonly BgiExternalClient _client;
    private readonly RunStore _runs;

    public BgiWorkflowPrerequisiteAdapter(BgiExternalClient client, RunStore runs)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    }

    /// <summary>支持的前置类型 = 对端能力实况（I1：缺 capability 即不支持，Planner 预检据此阻止执行）。</summary>
    public IReadOnlySet<string> SupportedKinds
    {
        get
        {
            var kinds = new HashSet<string>(StringComparer.Ordinal);
            if (_client.HasCapability(BgiExternalClient.CapabilityPrerequisiteAccount)) kinds.Add("prerequisite.account");
            if (_client.HasCapability(BgiExternalClient.CapabilityPrerequisiteRedeemCode)) kinds.Add("prerequisite.redeemCode");
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
            && r.Kind == strategy.Kind && r.AccountKey == accountKey
            && r.State == PrerequisiteActionState.Intent);
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

        // I4：线协议身份/幂等键/指纹/expiresAtUtc/bgiEpoch 首次发送即冻结（指纹覆盖冻结后的完整载荷）
        record.Epoch = _client.ServerEpoch is { } epoch ? $"{epoch.ProcessId}:{epoch.StartTicksUtc}" : null;
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
            idempotencyKey = record.IdempotencyKey,
            expiresAtUtc = record.ExpiresAtUtc,
            bgiEpoch = _client.ServerEpoch is { } se
                ? new { processId = se.ProcessId, startTicksUtc = se.StartTicksUtc } : null,
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
        record.SendAttempted = true;
        _runs.Update(run);

        // 提交：受理回执 = accepted + taskHandle（jobId 别名）；发送 ≠ 完成
        BgiExternalResponse response;
        try
        {
            response = await _client.SendCommandAsync(operation, payload, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; } // 叶子取消交引擎确认链（B3；记录带 SendAttempted 供确认链判定）
        catch (Exception ex)
        {
            return new PrerequisiteResult(PrerequisiteStatus.Unknown,
                $"发送结果不可考（{ex.GetType().Name}），不盲目重发");
        }

        if (!response.Success)
        {
            // 副作用前拒绝（合同校验/队列满/协调器不可用）：动作确定未受理
            record.State = PrerequisiteActionState.Failed;
            record.Reason = $"受理被拒绝：{response.ErrorCode}";
            _runs.Update(run);
            return new PrerequisiteResult(PrerequisiteStatus.Rejected, record.Reason);
        }

        var acceptance = BgiJobTerminalPolling.ParseAcceptance(response.Data);
        if (acceptance.AlreadyExecuted)
        {
            // I4 传输重投命中同键已执行事实：等同成功，不产生第二次执行
            record.State = PrerequisiteActionState.Succeeded;
            _runs.Update(run);
            return new PrerequisiteResult(PrerequisiteStatus.Proceed, "同键已执行（幂等命中，未重复副作用）");
        }
        if (!acceptance.Accepted || acceptance.TaskHandle is null)
            return new PrerequisiteResult(PrerequisiteStatus.Unknown, "受理回执缺 taskHandle（协议违例），结果不可考");

        // 受理即持久化 submitted 事实（崩溃窗口可按 jobId 对账，不重发）
        record.State = PrerequisiteActionState.Submitted;
        record.JobId = acceptance.TaskHandle;
        _runs.Update(run);

        var (outcome, job, reason) = await BgiJobTerminalPolling.PollUntilTerminalAsync(
            _client, acceptance.TaskHandle, ExecuteBudget, PollInterval, ct).ConfigureAwait(false);
        return MapOutcome(outcome, job, reason, acceptance.TaskHandle);
    }

    /// <summary>恢复对账：先查远端权威终态（ext.job.status），查不到不盲目重发（B2；与正常轮询共用终态解释器）。</summary>
    public async Task<PrerequisiteResult> ReconcileAsync(PrerequisiteActionRecord record, CancellationToken ct)
    {
        if (record.JobId is null)
            return new PrerequisiteResult(PrerequisiteStatus.Unknown,
                record.SendAttempted
                    ? "发送已尝试但无受理事实（无 jobId），禁止盲目重发"
                    : "意图已落盘但未发送（无 jobId），禁止盲目重发");
        (string? Status, BgiJobInfo? Job) query;
        try
        {
            query = await _client.QueryJobStatusAsync(record.JobId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception)
        {
            return new PrerequisiteResult(PrerequisiteStatus.Unknown, "通道瞬态故障，对账未决", record.JobId);
        }
        if (query.Status is null)
            return new PrerequisiteResult(PrerequisiteStatus.Unknown, "通道瞬态故障，对账未决", record.JobId);
        if (query.Status == "not_found" || query.Job is null)
            return new PrerequisiteResult(PrerequisiteStatus.Unknown,
                "作业不存在于当前纪元（not_found，跨纪元事实不沿用）", record.JobId);
        var (outcome, reason) = BgiJobTerminalPolling.InterpretJob(query.Job);
        if (outcome == "active")
            return new PrerequisiteResult(PrerequisiteStatus.Unknown,
                $"对账：作业仍在活动态（{query.Job.State}）", record.JobId);
        if (outcome == "rejected")
            return new PrerequisiteResult(PrerequisiteStatus.Rejected, "对账：远端拒绝", record.JobId);
        return MapOutcome(outcome, query.Job, reason, record.JobId);
    }

    /// <summary>前置期显式跳过的远端取消确认链（B3 + 四轮阻断 1：确认 cancelled 才算数；发送窗口不可考 = Unknown）。</summary>
    public async Task<PrerequisiteResult> ConfirmCancellationAsync(PrerequisiteActionRecord record, CancellationToken ct)
    {
        if (record.JobId is null)
        {
            // 四轮阻断 1：区分「确定未发送」与「可能已发送」——后者绝不判 Cancelled
            return record.SendAttempted
                ? new PrerequisiteResult(PrerequisiteStatus.Unknown, "发送已尝试但受理未证实（无 jobId），远端可能在飞")
                : new PrerequisiteResult(PrerequisiteStatus.Cancelled, "未发送（意图未离开本机），无远端在飞");
        }
        try
        {
            await _client.CancelOwnedTaskAsync(record.JobId, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            return new PrerequisiteResult(PrerequisiteStatus.Unknown,
                $"取消请求发送失败（{ex.GetType().Name}），远端终态未确认", record.JobId);
        }

        var (outcome, _, reason) = await BgiJobTerminalPolling.PollUntilTerminalAsync(
            _client, record.JobId, ConfirmBudget, PollInterval, ct).ConfigureAwait(false);
        return outcome == "cancelled"
            ? new PrerequisiteResult(PrerequisiteStatus.Cancelled, "远端取消已确认", record.JobId)
            : new PrerequisiteResult(PrerequisiteStatus.Unknown, $"取消后远端终态未确认（{reason ?? outcome}）", record.JobId);
    }

    /// <summary>统一终态 → 结构化结果（四轮阻断 6：错误码不确定性保留，prerequisite_outcome_unknown 不降级为 Failed）。</summary>
    private static PrerequisiteResult MapOutcome(string outcome, BgiJobInfo? job, string? reason, string? jobId)
        => outcome switch
        {
            "succeeded" => new PrerequisiteResult(PrerequisiteStatus.Proceed, null, jobId),
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