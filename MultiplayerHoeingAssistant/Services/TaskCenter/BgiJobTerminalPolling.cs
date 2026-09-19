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
        BgiExternalClient client, string jobId, TimeSpan budget, TimeSpan interval, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            string? status;
            BgiJobInfo? job;
            try
            {
                (status, job) = await client.QueryJobStatusAsync(jobId, ct).ConfigureAwait(false);
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