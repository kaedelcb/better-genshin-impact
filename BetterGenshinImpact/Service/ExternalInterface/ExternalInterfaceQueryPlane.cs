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
                response = handler.HandleTaskStatus(connection, request);
                // HandleTaskStatus 成功路径必然带 Data；仅成功响应补 revision 字段
                if (response.Success == true && response.Data is not null)
                {
                    response.Data["stateRevision"] = ExternalInterfaceEventHub.Instance.CurrentRevision;
                }
                return true;

            case ExternalInterfaceOperations.ConfigList:
                response = handler.HandleConfigList(connection, request);
                return true;

            default:
                response = null!;
                return false;
        }
    }
}
