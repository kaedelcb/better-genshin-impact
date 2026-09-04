using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>
/// L3 控制面：ext.* 写命令。语义与 v2 操作完全对齐——直接委托既有
/// InstanceRequestHandler 私有实现（单一事实源，§8 风险对策），旧入口与 ext 入口
/// 跑的是同一份代码，行为逐字节一致。幂等窗口在会话层。
/// </summary>
internal static class ExternalInterfaceCommandPlane
{
    public static async Task<InstanceIpcEnvelope> DispatchAsync(
        InstanceRequestHandler handler,
        InstanceConnection connection,
        InstanceIpcEnvelope request,
        CancellationToken cancellationToken)
        => request.Operation switch
        {
            ExternalInterfaceOperations.TaskStart =>
                await handler.HandleTaskStart(connection, request),
            ExternalInterfaceOperations.TaskStop =>
                handler.HandleTaskStop(connection, request),
            ExternalInterfaceOperations.TaskSuspend =>
                await handler.HandleTaskSuspend(connection, request),
            ExternalInterfaceOperations.TaskResume =>
                await handler.HandleTaskResume(connection, request),
            ExternalInterfaceOperations.ConfigSetTaskEnabled =>
                await handler.HandleSetTaskEnabled(connection, request),
            ExternalInterfaceOperations.ConfigPullGroup =>
                handler.HandleConfigPullGroup(connection, request),
            ExternalInterfaceOperations.ConfigOpenRemoteEditor =>
                handler.HandleConfigOpenRemoteEditor(connection, request),
            ExternalInterfaceOperations.ConfigRemoteEditorResult =>
                handler.HandleConfigRemoteEditorResult(connection, request),
            ExternalInterfaceOperations.ConfigApplyGroup =>
                await handler.HandleConfigApplyGroup(connection, request),
            ExternalInterfaceOperations.ActionExecuteHotkey =>
                await handler.HandleExecuteHotkey(connection, request),
            ExternalInterfaceOperations.ActionCloseGame =>
                handler.HandleCloseGame(connection, request),
            _ => InstanceIpcEnvelope.Failure(
                request,
                "unsupported_operation",
                $"不支持的 ext.* 操作：{request.Operation}"),
        };
}
