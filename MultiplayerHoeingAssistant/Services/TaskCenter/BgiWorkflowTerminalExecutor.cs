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

    private readonly BgiExternalClient _client;
    private readonly RunStore _runs;

    public BgiWorkflowTerminalExecutor(BgiExternalClient client, RunStore runs)
    {
        _client = client ?? throw new ArgumentNullException(nameof(client));
        _runs = runs ?? throw new ArgumentNullException(nameof(runs));
    }

    /// <summary>支持的收尾类型 = 对端能力实况（I1：缺 capability 即不支持，Planner 预检据此阻止执行）。</summary>
    public IReadOnlySet<string> SupportedKinds
        => _client.HasCapability(BgiExternalClient.CapabilityTerminalCompletionAction)
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

        // I4：幂等键/指纹/expiresAtUtc/bgiEpoch 首次发送即冻结（传输重投同键同载荷同指纹）
        if (string.IsNullOrEmpty(record.IdempotencyKey))
        {
            record.IdempotencyKey = RunStore.DeriveSubmissionKey(run.RunId, "$flow", 0, 0, 1);
            record.ExpiresAtUtc = DateTimeOffset.UtcNow.Add(ExpireWindow).ToString("O");
        }
        record.Occurrence = 0; // E1' 扩展字段：收尾身份 occurrence=0 / attempt=1
        record.Attempt = 1;
        var payload = new
        {
            executionContractVersion = 1,
            idempotencyKey = record.IdempotencyKey,
            expiresAtUtc = record.ExpiresAtUtc,
            bgiEpoch = _client.ServerEpoch is { } se
                ? new { processId = se.ProcessId, startTicksUtc = se.StartTicksUtc } : null,
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
        record.State = "dispatching";
        _runs.Update(run);

        BgiExternalResponse response;
        try
        {
            response = await _client.SendCommandAsync(
                BgiExternalClient.ExternalOperations.TerminalCompletionAction, payload, null, ct).ConfigureAwait(false);
        }
        catch (OperationCanceledException) { throw; } // B8：dispatching 事实交引擎外层标 unknown，禁止补发
        catch (Exception ex)
        {
            return TerminalExecutionResult.UnknownWith(null, $"发送结果不可考（{ex.GetType().Name}），禁止补发");
        }

        if (!response.Success)
        {
            // 副作用前拒绝（合同校验/队列满/协调器不可用）：动作确定未受理——回到 pending 待人工处置
            record.State = "pending";
            _runs.Update(run);
            return TerminalExecutionResult.RejectedWith($"受理被拒绝：{response.ErrorCode}");
        }

        var acceptance = BgiJobTerminalPolling.ParseAcceptance(response.Data);
        if (acceptance.AlreadyExecuted)
        {
            // I4 传输重投命中同键已执行事实：等同 executed，不产生第二次执行
            return TerminalExecutionResult.Executed(null);
        }
        if (!acceptance.Accepted || acceptance.TaskHandle is null)
            return TerminalExecutionResult.UnknownWith(null, "受理回执缺 taskHandle（协议违例），结果不可考");

        // 受理即持久化 submitted 事实（E3'：accepted 后不可逆，断线/取消按 unknown 对账）
        record.State = "submitted";
        record.JobId = acceptance.TaskHandle;
        _runs.Update(run);

        var (outcome, job, reason) = await BgiJobTerminalPolling.PollUntilTerminalAsync(
            _client, acceptance.TaskHandle, ExecuteBudget, PollInterval, ct).ConfigureAwait(false);
        return outcome switch
        {
            "succeeded" => TerminalExecutionResult.Executed(acceptance.TaskHandle),
            "cancelled" => TerminalExecutionResult.CancelledWith(acceptance.TaskHandle, reason ?? "远端已取消"),
            "failed" when job?.ErrorCode == "terminal_conflict" =>
                TerminalExecutionResult.RejectedWith("存在其他活动作业（terminal_conflict），动作未执行"),
            "failed" =>
                TerminalExecutionResult.UnknownWith(acceptance.TaskHandle, $"收尾作业失败：{reason}（失败语义不可考，禁止补发）"),
            _ => TerminalExecutionResult.UnknownWith(acceptance.TaskHandle,
                reason ?? "终态不可考（进程自杀类允许永远 unknown，禁止补发）"),
        };
    }
}