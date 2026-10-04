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
        BetterGenshinImpact.Service.Execution.PreemptionGate.Renew(InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket"));
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

            case ExternalInterfaceOperations.ManualStopFence:
                var fence = BetterGenshinImpact.Service.Execution.ExecutionScope.GetManualStopFence();
                response = InstanceIpcEnvelope.Response(request, new
                {
                    bgiEpoch = EpochPayload(), stopVersion = fence.StopVersion,
                    lastManualStopTimestamp = fence.LastManualStopTimestamp,
                    monotonicFrequency = System.Diagnostics.Stopwatch.Frequency,
                });
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
        idempotencyKey = job.IdempotencyKey,
        requestFingerprint = job.RequestFingerprint,
        requestFingerprintVersion = job.RequestFingerprintVersion,
        requestOperation = job.RequestOperation,
        parentJobId = job.ParentJobId?.ToString("N"),
        workflowRunId = job.WorkflowRunId?.ToString("N"),
        nodeId = job.NodeId,
        iteration = job.Iteration,
        attemptId = job.JobId.ToString("N"),
        taskId = job.TaskId,
        configRevision = job.ConfigRevision,
        occurrence = job.Occurrence,
        attempt = job.Attempt,
        kind = job.Kind.ToString(),
        name = job.Name,
        source = job.Source.ToString(),
        generation = job.Generation,
        state = JobStateWord(job.State),
        errorCode = job.ErrorCode,
        errorMessage = job.ErrorMessage,
        wasCancelled = job.WasCancelled,
        exitConfirmed = job.ExitConfirmed,
        exitDisposition = job.ExitDisposition,
        exitConfirmedAtUtc = job.ExitConfirmedAtUtc,
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

        var registry = BetterGenshinImpact.Service.Execution.JobRegistry.IsCreated
            ? BetterGenshinImpact.Service.Execution.JobRegistry.Instance : null;
        registry?.TryConfirmExecutionExited(jobId,
            GameTask.Common.TaskControl.TaskSemaphore.CurrentCount != 0,
            BetterGenshinImpact.Service.Execution.ExecutionScope.HasActive);
        var payload = registry?.ProjectJob(jobId, job => new
        { status = JobStateWord(job.State), job = SerializeJob(job), bgiEpoch = EpochPayload() });
        return payload is null
            ? InstanceIpcEnvelope.Response(request, new { status = "not_found", bgiEpoch = EpochPayload() })
            : InstanceIpcEnvelope.Response(request, payload);
    }

    private static string JobStateWord(BetterGenshinImpact.Service.Execution.JobState state) =>
        state == BetterGenshinImpact.Service.Execution.JobState.ResultUnknown
            ? "result_unknown" : state.ToString().ToLowerInvariant();

    /// <summary>[A3.2] ext.job.list：全量快照（reconcile 输入）。附带触发器总开关状态（A2.5 只读视图出口）。</summary>
    private static InstanceIpcEnvelope HandleJobList(InstanceIpcEnvelope request)
    {
        var registryCreated = BetterGenshinImpact.Service.Execution.JobRegistry.IsCreated;
        var jobs = registryCreated
            ? BetterGenshinImpact.Service.Execution.JobRegistry.Instance.ProjectJobs(SerializeJob)
            : Array.Empty<object>();
        return InstanceIpcEnvelope.Response(request, new
        {
            bgiEpoch = EpochPayload(),
            triggerDispatcherRunning = registryCreated && BetterGenshinImpact.Service.Execution.JobRegistry.Instance.TriggerDispatcherRunning,
            jobs,
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
