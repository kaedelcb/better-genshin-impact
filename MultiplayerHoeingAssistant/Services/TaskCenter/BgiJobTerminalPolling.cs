using System;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>
/// R4.6 B3：ext.job.status 轮询对账（前置/收尾适配器共用）。
/// 通道瞬态（status=null）不计事实、继续等；到达终态或预算耗尽返回。
/// 终态词表 = BGI JobState 小写（queued/running/cancelling 为活动态；succeeded/failed/cancelled/rejected/skipped 为终态）。
/// </summary>
internal static class BgiJobTerminalPolling
{
    /// <summary>
    /// 轮询至作业终态。Outcome ∈ succeeded / failed / cancelled / unknown
    /// （unknown = 不可考：预算耗尽 / 查无作业 / 未登记终态词； cancelled 含 WasCancelled 补位）。
    /// </summary>
    public static async Task<(string Outcome, BgiJobInfo? Job, string? Reason)> PollUntilTerminalAsync(
        BgiExternalClient client, string jobId, TimeSpan budget, TimeSpan interval, CancellationToken ct)
    {
        var deadline = DateTimeOffset.UtcNow + budget;
        while (DateTimeOffset.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            var (status, job) = await client.QueryJobStatusAsync(jobId, ct).ConfigureAwait(false);
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
                switch (job.State)
                {
                    case "succeeded":
                        return job.WasCancelled
                            ? ("cancelled", job, "远端取消已确认（WasCancelled）")
                            : ("succeeded", job, null);
                    case "failed":
                        return job.WasCancelled
                            ? ("cancelled", job, "远端取消已确认（WasCancelled）")
                            : ("failed", job, job.ErrorCode ?? "failed");
                    case "cancelled":
                        return ("cancelled", job, job.ErrorMessage ?? "远端已取消");
                    case "rejected":
                        return ("failed", job, job.ErrorCode ?? "rejected");
                    case "skipped":
                        return ("unknown", job, "前置/收尾作业不应出现 skipped 终态（协议违例）");
                    // queued / running / cancelling：活动态，继续等
                }
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
            if (status != "accepted") return (false, null, false);
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