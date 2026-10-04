using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// R4.6 B3：ext.job.status 轮询对账（前置/收尾适配器共用）。
/// 通道瞬态（status=null / 传输异常）不计事实、预算内继续等；到达终态或预算耗尽返回。
/// 终态词表 = BGI JobState 小写（queued/running/cancelling 为活动态；succeeded/failed/cancelled/rejected/skipped 为终态）。
/// </summary>
internal static class BgiJobTerminalPolling
{
    internal sealed record FrozenIdentity(string? Epoch, string? Key, string? WireRunId,
        string NodeId, int Iteration, int Occurrence, int Attempt)
    {
        internal bool Complete => !string.IsNullOrWhiteSpace(Epoch) && !string.IsNullOrWhiteSpace(Key)
            && !string.IsNullOrWhiteSpace(WireRunId) && !string.IsNullOrWhiteSpace(NodeId) && Attempt > 0;
        internal bool Matches(BgiJobInfo job) => job.IdempotencyKey == Key && job.WorkflowRunId == WireRunId
            && job.NodeId == NodeId && job.Iteration == Iteration && job.Occurrence == Occurrence && job.Attempt == Attempt;
    }

    internal static bool IsTerminal(string? state) => state is "succeeded" or "failed" or "cancelled" or "rejected" or "skipped";

    // A terminal business result and executor cleanup are independent facts. Never rewrite raw state via WasCancelled.
    internal static async Task<(string Outcome, BgiJobInfo? Job, string? Reason)> PollUntilExitAsync(
        IBgiExecutionPort port, FrozenIdentity identity, string jobId, TimeSpan budget, TimeSpan interval, CancellationToken ct,
        Action<BgiJobInfo>? persistObservation = null, Func<BgiJobInfo, bool>? validateOriginalPayload = null)
    {
        if (!identity.Complete || !port.HasCapability("execution.exit.confirmed.v1"))
            return ("unknown", null, "缺少完整冻结身份或执行退出证明能力");
        var deadline = DateTimeOffset.UtcNow + budget;
        BgiJobInfo? last = null;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var before = WorkflowStopAuthority.Epoch(port.ServerEpoch);
            if (before != identity.Epoch) return ("unknown", null, "冻结纪元已变化");
            string? status; BgiJobInfo? job;
            try { (status, job) = await port.QueryJobStatusAsync(jobId, ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { throw; }
            catch { status = null; job = null; }
            if (WorkflowStopAuthority.Epoch(port.ServerEpoch) != identity.Epoch)
                return ("unknown", null, "查询期间连接纪元变化");
            if (status == "not_found") return ("unknown", null, "原作业不存在；不猜未执行");
            if (job is not null)
            {
                if (WorkflowStopAuthority.Epoch(job.Epoch) != identity.Epoch || job.JobId != jobId || !identity.Matches(job))
                    return ("unknown", null, "作业、出现身份或响应纪元不符");
                if (validateOriginalPayload is not null && !validateOriginalPayload(job))
                    return ("unknown", null, "原请求载荷、任务或配置证据不符，未发布观察事实");
                if (last is not null && IsTerminal(last.State) && job.State != last.State)
                    return ("unknown", last, "已读业务终态与后来状态冲突；原事实保留");
                persistObservation?.Invoke(job); // Persist before the next query/delay can cancel or throw.
                last = job;
                if (IsTerminal(job.State) && job.ExecutionExitConfirmed
                    && job.ExecutionExitDisposition is "execution_exited" or "never_started")
                    return (job.State!, job, job.ErrorMessage ?? job.ErrorCode);
                if (job.State is not ("queued" or "running" or "cancelling") && !IsTerminal(job.State))
                    return ("unknown", job, "原始终态或实际效果未知");
            }
            var remaining = deadline - DateTimeOffset.UtcNow;
            if (remaining > TimeSpan.Zero) await Task.Delay(remaining < interval ? remaining : interval, ct).ConfigureAwait(false);
        }
        return ("unknown", last, "等待同身份退出超预算");
    }

    // Read-only reconciliation by original key. Never submit again, even when the register has evicted a job.
    internal static async Task<BgiJobInfo?> FindOriginalJobAsync(IBgiExecutionPort port, FrozenIdentity identity, CancellationToken ct)
    {
        if (!identity.Complete || WorkflowStopAuthority.Epoch(port.ServerEpoch) != identity.Epoch) return null;
        var snapshot = await port.QueryJobListAsync(ct).ConfigureAwait(false);
        if (snapshot is null || WorkflowStopAuthority.Epoch(port.ServerEpoch) != identity.Epoch
            || WorkflowStopAuthority.Epoch(snapshot.Epoch) != identity.Epoch) return null;
        var hits = snapshot.Jobs.Where(j => j.IdempotencyKey == identity.Key).ToList();
        return hits.Count == 1 && !string.IsNullOrWhiteSpace(hits[0].JobId) && identity.Matches(hits[0])
            ? hits[0] : null;
    }


    /// <summary>兼容重载（既有调用方持具体客户端）——转端口后走同一实现，行为不变。</summary>
    public static Task<(string Outcome, BgiJobInfo? Job, string? Reason)> PollUntilTerminalAsync(
        BgiExternalClient client, string jobId, TimeSpan budget, TimeSpan interval, CancellationToken ct)
        => PollUntilTerminalAsync(
            new BgiExternalClientPort(client ?? throw new ArgumentNullException(nameof(client))),
            jobId, budget, interval, ct);

    /// <summary>
    /// 统一终态解释（四轮阻断 6：正常轮询与恢复对账共用同一解释器——WasCancelled 优先于成功/失败，
    /// skipped 对前置/收尾作业是协议违例按不可考）。Outcome ∈ succeeded/failed/cancelled/unknown/active。
    /// </summary>
    public static (string Outcome, string? Reason) InterpretJob(BgiJobInfo job)
        => job.State switch
        {
            "succeeded" => job.WasCancelled ? ("cancelled", "远端取消已确认（WasCancelled）") : ("succeeded", null),
            "failed" => job.WasCancelled ? ("cancelled", "远端取消已确认（WasCancelled）") : ("failed", job.ErrorCode ?? "failed"),
            "cancelled" => ("cancelled", job.ErrorMessage ?? "远端已取消"),
            "rejected" => ("failed", job.ErrorCode ?? "rejected"),
            "skipped" => ("unknown", "前置/收尾作业不应出现 skipped 终态（协议违例）"),
            _ => ("active", null), // queued / running / cancelling
        };

    /// <summary>
    /// 轮询至作业终态。Outcome ∈ succeeded / failed / cancelled / unknown
    /// （unknown = 不可考：预算耗尽 / 查无作业 / 协议违例； cancelled 含 WasCancelled 补位）。
    /// 四轮重要 11：查询传输/协议异常按通道瞬态处理（预算内继续等），不抛出炸掉运行循环。
    /// </summary>
    public static async Task<(string Outcome, BgiJobInfo? Job, string? Reason)> PollUntilTerminalAsync(
        IBgiExecutionPort port, string jobId, TimeSpan budget, TimeSpan interval, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            string? status;
            BgiJobInfo? job;
            try
            {
                (status, job) = await port.QueryJobStatusAsync(jobId, ct).ConfigureAwait(false);
            }
            catch (OperationCanceledException) { throw; } // 用户取消/叶子取消绝不降级为瞬态
            catch (Exception)
            {
                status = null; job = null; // 传输/协议异常 = 通道瞬态，不计事实，预算内继续
            }

            if (status is null)
            {
                // 通道瞬态失败：不计事实，继续等（对端可能在执行中）
            }
            else if (status == "not_found")
            {
                return ("unknown", null, "作业不存在于当前纪元（not_found）");
            }
            else if (job is not null)
            {
                var (outcome, reason) = InterpretJob(job);
                if (outcome != "active") return (outcome, job, reason);
            }

            await Task.Delay(interval, ct).ConfigureAwait(false);
        }

        return ("unknown", null, "等待终态超预算（结果不可考）");
    }

    /// <summary>
    /// 受理回执解析：accepted → (true, taskHandle, false)；already_executed → (true, null, true)；其余 → (false, ...)。
    /// taskHandle 即统一注册表 jobId 别名（E1'）。
    /// </summary>
    public static (bool Accepted, string? TaskHandle, bool AlreadyExecuted) ParseAcceptance(string? dataJson)
    {
        if (dataJson is null) return (false, null, false);
        try
        {
            using var doc = JsonDocument.Parse(dataJson);
            var root = doc.RootElement;
            var status = root.TryGetProperty("status", out var s) && s.ValueKind == JsonValueKind.String
                ? s.GetString() : null;
            if (status == "already_executed") return (true, null, true);
            // 真实线路词汇（R4 真实段 2026-09-19 实锤）：BGI ext.task.start 受理回执=queued（新入队）/adopted（认领既有排队作业），
            // 均携带 taskHandle；"accepted" 为早期假设词保留兼容。其余一律不视为受理。
            if (status is not ("accepted" or "queued" or "adopted")) return (false, null, false);
            var handle = root.TryGetProperty("taskHandle", out var h) && h.ValueKind == JsonValueKind.String
                ? h.GetString() : null;
            return (true, handle, false);
        }
        catch (JsonException)
        {
            return (false, null, false);
        }
    }
}
