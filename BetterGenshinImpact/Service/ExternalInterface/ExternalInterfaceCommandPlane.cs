using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Service.Instance;
using BetterGenshinImpact.Service.Instance.MessageHandlers;
using Microsoft.Extensions.DependencyInjection;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>
/// L3 控制面：ext.* 写命令。语义与 v2 操作完全对齐——直接委托既有
/// InstanceRequestHandler 私有实现（单一事实源，§8 风险对策），旧入口与 ext 入口
/// 跑的是同一份代码，行为逐字节一致。幂等窗口在会话层。
/// [切片7] ext.task.start 按 capability task.queue 接 BgiTaskCoordinator（入队拿 taskHandle
/// 立即返回，执行结果走事件）；协调器不可用时回退 v2 逐字节旧路径。
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
                await DispatchTaskStartAsync(handler, connection, request),
            ExternalInterfaceOperations.TaskStop =>
                DispatchTaskStop(handler, connection, request),
            ExternalInterfaceOperations.TaskCancel =>
                DispatchTaskCancel(handler, connection, request),
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

    /// <summary>
    /// [切片7] ext.task.start：入队拿 taskHandle 立即返回（拒绝式语义退役，Actor Mailbox）。
    /// 执行段仍走 ExecuteTaskStartCoreAsync（v2 同一事实源）。协调器不可用（进程退出中）时
    /// 回退 v2 逐字节旧路径（spec §4.5 防御性分支）。
    /// </summary>
    private static async Task<InstanceIpcEnvelope> DispatchTaskStartAsync(
        InstanceRequestHandler handler,
        InstanceConnection connection,
        InstanceIpcEnvelope request)
    {
        var groupName = request.Data?["groupName"]?.ToString();
        var configName = request.Data?["configName"]?.ToString();
        var startFromIndex = request.Data?["startFromIndex"]?.ToObject<int>() ?? 0;
        var generation = request.Data?["generation"]?.ToObject<int>() ?? 0;

        var scriptService = App.ServiceProvider.GetService<BetterGenshinImpact.Service.Interface.IScriptService>();
        if (scriptService == null)
        {
            return InstanceIpcEnvelope.Failure(request, "service_unavailable", "脚本服务不可用");
        }

        var submission = new BgiTaskCoordinator.TaskSubmission(
            generation,
            groupName,
            configName,
            startFromIndex,
            _ => handler.ExecuteTaskStartCoreAsync(scriptService, groupName, configName, startFromIndex));

        var result = BgiTaskCoordinator.Instance.Submit(submission);
        return result.Status switch
        {
            BgiTaskCoordinator.SubmitStatus.Queued => InstanceIpcEnvelope.Response(request, new
            {
                status = "queued",
                taskHandle = result.TaskHandle.ToString("N"),
                queuePosition = result.QueuePosition,
            }),
            BgiTaskCoordinator.SubmitStatus.Adopted => InstanceIpcEnvelope.Response(request, new
            {
                status = "adopted",
                taskHandle = result.TaskHandle.ToString("N"),
                generation,
            }),
            BgiTaskCoordinator.SubmitStatus.AlreadyExecuted => InstanceIpcEnvelope.Response(
                request, new { status = "already_executed", generation }),
            BgiTaskCoordinator.SubmitStatus.QueueFull => InstanceIpcEnvelope.Failure(
                request, "queue_full", $"任务队列已满（容量 {BgiTaskCoordinator.QueueCapacity}），请稍后重试或先取消排队项"),
            // Unavailable：协调器已销毁（进程退出中）——回退 v2 逐字节旧路径（spec §4.5 防御性分支）
            _ => await handler.HandleTaskStart(connection, request),
        };
    }

    /// <summary>
    /// [切片7] ext.task.stop：新增可选参数 clearQueue（ext 通道默认 true——"停止"含"别再继续"语义，
    /// 清空时在队项逐项发 task.queueCancelled）；v2 task.stop 无此参数，行为不变。
    /// </summary>
    private static InstanceIpcEnvelope DispatchTaskStop(
        InstanceRequestHandler handler,
        InstanceConnection connection,
        InstanceIpcEnvelope request)
    {
        var clearQueue = request.Data?["clearQueue"]?.ToObject<bool?>() ?? true;
        if (clearQueue)
        {
            BgiTaskCoordinator.Instance.ClearQueue();
        }

        return handler.HandleTaskStop(connection, request);
    }

    /// <summary>
    /// [切片7] ext.task.cancel {taskHandle}：在队 → 移除+task.queueCancelled；
    /// 在跑且句柄匹配 → 等价 task.stop（复用 HandleTaskStop 单一事实源）；否则 task_not_found。
    /// </summary>
    private static InstanceIpcEnvelope DispatchTaskCancel(
        InstanceRequestHandler handler,
        InstanceConnection connection,
        InstanceIpcEnvelope request)
    {
        var handleRaw = request.Data?["taskHandle"]?.ToString();
        if (!Guid.TryParse(handleRaw, out var handle))
        {
            return InstanceIpcEnvelope.Failure(request, "invalid_request", "taskHandle 缺失或格式错误");
        }

        return BgiTaskCoordinator.Instance.CancelByHandle(handle) switch
        {
            BgiTaskCoordinator.CancelOutcome.CancelledQueued => InstanceIpcEnvelope.Response(
                request, new { status = "cancelled", taskHandle = handleRaw, wasQueued = true }),
            BgiTaskCoordinator.CancelOutcome.StopRequestedRunning =>
                handler.HandleTaskStop(connection, request),
            _ => InstanceIpcEnvelope.Failure(
                request, "task_not_found", $"任务句柄不存在或已结束: {handleRaw}"),
        };
    }
}
