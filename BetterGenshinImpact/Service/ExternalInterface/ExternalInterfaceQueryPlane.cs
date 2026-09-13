using System;
using System.Collections.Generic;
using System.Linq;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>
/// L3 查询面：ext.* 只读快照。ext.task.status 相对 v2 的唯一增量是
/// stateRevision 字段（与事件流同源的版本号）：客户端发现事件 revision 跳号时
/// 主动拉一次快照补齐（LSP 文档同步模型，§3.6 事件面）。
/// </summary>
internal static class ExternalInterfaceQueryPlane
{
    public static bool TryDispatch(
        InstanceRequestHandler handler,
        InstanceConnection connection,
        InstanceIpcEnvelope request,
        out InstanceIpcEnvelope response)
    {
        switch (request.Operation)
        {
            case ExternalInterfaceOperations.TaskStatus:
                // 先取 revision 再读状态：快照版本号 ≤ 实际已发布事件，客户端对 rev > 快照版本 的事件
                // 照常分派（其状态可能已含在快照内，重复应用幂等无害）；反之若先读后取号，
                // 读与取号之间发布的事件会被客户端误跳过（微秒级窗口，但方向必须是安全的）。
                var revisionSnapshot = ExternalInterfaceEventHub.Instance.CurrentRevision;
                response = handler.HandleTaskStatus(connection, request);
                // HandleTaskStatus 成功路径必然带 Data；仅成功响应补 revision 字段
                if (response.Success == true && response.Data is not null)
                {
                    response.Data["stateRevision"] = revisionSnapshot;
                }
                return true;

            case ExternalInterfaceOperations.ConfigList:
                response = handler.HandleConfigList(connection, request);
                return true;

            case ExternalInterfaceOperations.TaskQueueStatus:
                response = HandleTaskQueueStatus(request);
                return true;

            case ExternalInterfaceOperations.JobStatus:
                response = HandleJobStatus(request);
                return true;

            case ExternalInterfaceOperations.JobList:
                response = HandleJobList(request);
                return true;

            default:
                response = null!;
                return false;
        }
    }

    /// <summary>[A3.2] 作业序列化（ext.job.status/list 共用形态）。state 小写字符串与 task.* 词汇表同风格。</summary>
    private static object SerializeJob(BetterGenshinImpact.Service.Execution.BgiJob job) => new
    {
        jobId = job.JobId.ToString("N"),
        parentJobId = job.ParentJobId?.ToString("N"),
        kind = job.Kind.ToString(),
        name = job.Name,
        source = job.Source.ToString(),
        generation = job.Generation,
        state = job.State.ToString().ToLowerInvariant(),
        errorCode = job.ErrorCode,
        errorMessage = job.ErrorMessage,
        wasCancelled = job.WasCancelled,
        enqueuedAtUtc = job.EnqueuedAtUtc,
        startedAtUtc = job.StartedAtUtc,
        finishedAtUtc = job.FinishedAtUtc,
        lastHeartbeatAtUtc = job.LastHeartbeatAtUtc,
    };

    /// <summary>[A3.2] 纪元帧片段（§4.2）：静态常量，不为查询创建注册表实例。</summary>
    private static object EpochPayload() => new
    {
        processId = BetterGenshinImpact.Service.Execution.JobRegistry.CurrentEpoch.ProcessId,
        startTicksUtc = BetterGenshinImpact.Service.Execution.JobRegistry.CurrentEpoch.StartTicksUtc,
    };

    /// <summary>[A3.2] ext.job.status：按 jobId 查单个作业。not_found + bgiEpoch 供客户端区分淘汰/重启。</summary>
    private static InstanceIpcEnvelope HandleJobStatus(InstanceIpcEnvelope request)
    {
        var jobIdRaw = request.Data?["jobId"]?.ToString();
        if (!Guid.TryParse(jobIdRaw, out var jobId))
        {
            return InstanceIpcEnvelope.Failure(request, "invalid_request", "jobId 缺失或格式错误");
        }

        // IsCreated 守卫：注册表未创建 = 本进程从未有作业登记，等价 not_found（不为查询创建单例）
        var job = BetterGenshinImpact.Service.Execution.JobRegistry.IsCreated
            ? BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(jobId)
            : null;
        if (job is null)
        {
            return InstanceIpcEnvelope.Response(request, new { status = "not_found", bgiEpoch = EpochPayload() });
        }

        return InstanceIpcEnvelope.Response(request, new
        {
            status = job.State.ToString().ToLowerInvariant(),
            job = SerializeJob(job),
            bgiEpoch = EpochPayload(),
        });
    }

    /// <summary>[A3.2] ext.job.list：全量快照（reconcile 输入）。附带触发器总开关状态（A2.5 只读视图出口）。</summary>
    private static InstanceIpcEnvelope HandleJobList(InstanceIpcEnvelope request)
    {
        var registryCreated = BetterGenshinImpact.Service.Execution.JobRegistry.IsCreated;
        var jobs = registryCreated
            ? BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Snapshot()
            : (IReadOnlyList<BetterGenshinImpact.Service.Execution.BgiJob>)[];
        return InstanceIpcEnvelope.Response(request, new
        {
            bgiEpoch = EpochPayload(),
            triggerDispatcherRunning = registryCreated && BetterGenshinImpact.Service.Execution.JobRegistry.Instance.TriggerDispatcherRunning,
            jobs = jobs.Select(SerializeJob).ToArray(),
        });
    }

    /// <summary>
    /// [终态可拉取 2026-09-09] ext.task.queueStatus：按 taskHandle 返回队列项生命周期。
    /// 事件推送（task.completed 等）只是快速路径——单帧事件丢失时，等待方用本查询做安全网校准，
    /// 不再把批次进度押在"一帧必达"上。
    /// </summary>
    private static InstanceIpcEnvelope HandleTaskQueueStatus(InstanceIpcEnvelope request)
    {
        var handleRaw = request.Data?["taskHandle"]?.ToString();
        if (!Guid.TryParse(handleRaw, out var handle))
        {
            return InstanceIpcEnvelope.Failure(request, "invalid_request", "taskHandle 缺失或格式错误");
        }

        var status = BgiTaskCoordinator.Instance.QueryItemStatus(handle);
        return InstanceIpcEnvelope.Response(request, new
        {
            status = status.Status,
            cancelled = status.Cancelled,
            errorCode = status.ErrorCode,
            message = status.Message,
        });
    }
}
