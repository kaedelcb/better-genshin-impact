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
/// 作 add-only 扩展字段）+ 幂等键 + 指纹 + expiresAtUtc（I4：首次发送即冻结，传输重投同键同载荷同指纹）→
/// 受理即持久化 Submitted 事实（jobId）→ ext.job.status 对账至终态 → 结构化 PrerequisiteResult。
/// I6 无状态：全部状态在 RunStore 记录与客户端能力快照；I3：账号标识仅落脱敏形态（原值不落记录）。
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
        // 引擎纪律：调用前已落盘 Intent 记录（同节点+出现身份+kind 的最新一条）
        var record = run.PrerequisiteActions.LastOrDefault(r =>
            r.NodeId == occurrence.NodeId && r.Occurrence == occurrence.Occurrence
            && r.LoopIteration == occurrence.LoopIteration && r.Attempt == 1
            && r.Kind == strategy.Kind && r.State == PrerequisiteActionState.Intent);
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

        // I4：线协议身份/幂等键/指纹/expiresAtUtc 首次发送即冻结（传输重投不刷新）
        record.Epoch = _client.ServerEpoch is { } epoch ? $"{epoch.ProcessId}:{epoch.StartTicksUtc}" : null;
        record.AccountKey = MaskAccount(uid);
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
        _runs.Update(run);

        // 提交：受理回执 = accepted + taskHandle（jobId 别名）；发送 ≠ 完成
        BgiExternalResponse response;
        try
        {
            response = await _client.SendCommandAsync(operation, payload, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; } // 叶子取消交引擎确认链（B3）
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
        return outcome switch
        {
            "succeeded" => new PrerequisiteResult(PrerequisiteStatus.Proceed, null, acceptance.TaskHandle),
            "cancelled" => new PrerequisiteResult(PrerequisiteStatus.Cancelled, reason ?? "远端已取消", acceptance.TaskHandle),
            "failed" => job?.ErrorCode switch
            {
                "account_mismatch" => new PrerequisiteResult(PrerequisiteStatus.Failed,
                    "当前账号与流程声明 UID 不符（account_mismatch）", acceptance.TaskHandle),
                "prerequisite_outcome_unknown" => new PrerequisiteResult(PrerequisiteStatus.Unknown,
                    "BGI 侧结果不可考（prerequisite_outcome_unknown）", acceptance.TaskHandle),
                _ => new PrerequisiteResult(PrerequisiteStatus.Failed, $"前置执行失败：{reason}", acceptance.TaskHandle),
            },
            _ => new PrerequisiteResult(PrerequisiteStatus.Unknown, reason ?? "终态不可考", acceptance.TaskHandle),
        };
    }

    /// <summary>恢复对账：先查远端权威终态（ext.job.status），查不到不盲目重发（B2）。</summary>
    public async Task<PrerequisiteResult> ReconcileAsync(PrerequisiteActionRecord record, CancellationToken ct)
    {
        if (record.JobId is null)
            return new PrerequisiteResult(PrerequisiteStatus.Unknown, "意图已落盘但无受理事实（无 jobId），禁止盲目重发");
        var (status, job) = await _client.QueryJobStatusAsync(record.JobId, ct).ConfigureAwait(false);
        if (status is null)
            return new PrerequisiteResult(PrerequisiteStatus.Unknown, "通道瞬态故障，对账未决", record.JobId);
        if (status == "not_found" || job is null)
            return new PrerequisiteResult(PrerequisiteStatus.Unknown, "作业不存在于当前纪元（not_found，跨纪元事实不沿用）", record.JobId);
        return job.State switch
        {
            "succeeded" => new PrerequisiteResult(PrerequisiteStatus.Proceed, null, record.JobId),
            "cancelled" => new PrerequisiteResult(PrerequisiteStatus.Cancelled, "对账：远端已取消", record.JobId),
            "failed" => new PrerequisiteResult(PrerequisiteStatus.Failed, $"对账：远端失败（{job.ErrorCode}）", record.JobId),
            "rejected" => new PrerequisiteResult(PrerequisiteStatus.Rejected, "对账：远端拒绝", record.JobId),
            _ => new PrerequisiteResult(PrerequisiteStatus.Unknown, $"对账：作业仍在活动态（{job.State}）", record.JobId),
        };
    }

    /// <summary>前置期显式跳过的远端取消确认链（B3）：确认 cancelled 才算数，否则 Unknown（不猜成功）。</summary>
    public async Task<PrerequisiteResult> ConfirmCancellationAsync(PrerequisiteActionRecord record, CancellationToken ct)
    {
        if (record.JobId is null)
            return new PrerequisiteResult(PrerequisiteStatus.Cancelled, "未受理（无 jobId），无远端在飞");
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

    /// <summary>I3：账号标识脱敏（≤5 位全遮盖，否则前 3***后 2；与 BGI SensitiveTextMask 同规则，稳定可比对）。</summary>
    internal static string MaskAccount(string uid)
        => uid.Length <= 5 ? new string('*', uid.Length) : uid[..3] + "***" + uid[^2..];
}