using System;
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

            default:
                response = null!;
                return false;
        }
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
