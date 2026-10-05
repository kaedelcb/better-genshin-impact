using System;
using Newtonsoft.Json.Linq;
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
/// 立即返回，执行结果走事件）；协调器不可用时明确拒绝，不回退到无编号的 v2 task.start。
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
            ExternalInterfaceOperations.ConfigMigrateStandard =>
                await StandardConfigurationMigration.DispatchAsync(request),
            ExternalInterfaceOperations.ConfigOpenResourceEditor =>
                await ExternalResourceEditor.DispatchAsync(request),
            ExternalInterfaceOperations.ConfigDescribe or ExternalInterfaceOperations.ConfigApplyTaskState =>
                await ExternalInterfaceConfigurationPlane.DispatchAsync(request),
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
            // R4.6 E1'：前置/收尾操作面（作业化生命周期；严格合同强制；语义独立于 v2 操作）
            ExternalInterfaceOperations.PrerequisiteAccount or ExternalInterfaceOperations.PrerequisiteRedeemCode
                or ExternalInterfaceOperations.TerminalCompletionAction =>
                handler.HandlePrerequisiteOperation(connection, request),
            _ => InstanceIpcEnvelope.Failure(
                request,
                "unsupported_operation",
                $"不支持的 ext.* 操作：{request.Operation}"),
        };

    /// <summary>
    /// [切片7] ext.task.start：入队拿 taskHandle 立即返回（拒绝式语义退役，Actor Mailbox）。
    /// 执行段仍走 ExecuteTaskStartCoreAsync（与 v2 共用执行事实源）。协调器不可用（进程退出中）时
    /// 明确拒绝并保持零发送；此带编号通道绝不回退到 v2。
    /// </summary>
    private static Task<InstanceIpcEnvelope> DispatchTaskStartAsync(
        InstanceRequestHandler handler,
        InstanceConnection connection,
        InstanceIpcEnvelope request)
    {
        // 显式 "key":null 归一化为 C# null（否则 Name 被 "" 短路、幂等去重误判，见 GetStringOrNull 注释）
        if (Execution.ExecutionRequestContract.Validate(request) is { } invalid) return Task.FromResult(Execution.ExecutionRequestContract.RejectBeforeAcceptance(request, invalid));
        var identity = Execution.ExecutionRequestContract.ReadIdentity(request.Data);
        var groupName = InstanceIpcProtocol.GetStringOrNull(request.Data, "groupName");
        var configName = InstanceIpcProtocol.GetStringOrNull(request.Data, "configName");
        var startFromIndex = request.Data?["startFromIndex"]?.ToObject<int>() ?? 0;
        var startFromTaskId = InstanceIpcProtocol.GetStringOrNull(request.Data, "startFromTaskId"); // R3：一条龙起点为字符串任务 ID
        var generation = request.Data?["generation"]?.ToObject<int>() ?? 0;
        // [批次名单 2026-09-13] 与 v2 HandleTaskStart 同语义：批次绑定名单透传到执行段
        var batchGroupNames = InstanceRequestHandler.ParseBatchGroupNames(request.Data?["batchGroupNames"]?.ToString());
        // [A6] 抢占式下发（联机锄地批次/按键抢占置位）：等槽 3s 未果转主动抢占（有界退出契约）。默认 false 零变化
        var preempt = request.Data?["preempt"]?.ToObject<bool?>() ?? false;

        // 双空 task.start 是脏请求：执行段不命中任何分支，只会先取消当前任务再空跑（等价隐式停止），
        // ext 通道直接拒绝、绝不入队。v2 通道行为冻结，不加此校验。
        if (groupName is null && configName is null)
        {
            return Task.FromResult(InstanceIpcEnvelope.Failure(request, "invalid_request", "task.start 需要提供 groupName 或 configName"));
        }

        // [手动停止冷却] 先于入队：F11/停止热键后窗口内拒绝外部 task.start（与 v2 同守卫，零副作用）
        if (InstanceRequestHandler.CheckManualStopCooldown(request, "ext") is { } cooldownRejection)
        {
            return Task.FromResult(cooldownRejection);
        }

        var scriptService = App.ServiceProvider.GetService<BetterGenshinImpact.Service.Interface.IScriptService>();
        if (scriptService == null)
        {
            return Task.FromResult(InstanceIpcEnvelope.Failure(request, "service_unavailable", "脚本服务不可用"));
        }

        var takeoverTicket = InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket");
        if (!BetterGenshinImpact.Service.Execution.PreemptionGate.Authorize(takeoverTicket)
            || preempt && string.IsNullOrEmpty(takeoverTicket))
            return Task.FromResult(InstanceIpcEnvelope.Failure(request, "takeover_conflict", "接管票据无效或原流程尚未完成可靠接管"));
        var submission = new BgiTaskCoordinator.TaskSubmission(
            generation,
            groupName,
            configName,
            startFromIndex,
            // [A2.4] Executor 首参 = taskHandle（注册表 jobId 别名），透传执行段供漏斗认领既有 Queued 作业
            (handle, token) => handler.ExecuteTaskStartCoreAsync(scriptService, groupName, configName, startFromIndex, startFromTaskId, batchGroupNames, generation, handle, preempt,
                InstanceIpcProtocol.GetStringOrNull(request.Data, "takeoverTicket"), token,
                workflowRunId: identity?.WorkflowRunId, executionIdentity: identity,
                executionRequest: request))
        {
            Preempt = preempt,
            IdempotencyKey = InstanceIpcProtocol.GetStringOrNull(request.Data, "idempotencyKey"),
            PayloadFingerprint = BetterGenshinImpact.Service.Execution.ExecutionRequestContract.Fingerprint(request),
            RequestOperation = request.Operation,
            Identity = identity,
        };

        var result = BgiTaskCoordinator.Instance.Submit(submission);
        return Task.FromResult(MapTaskStartQueueResult(request, result, generation));
    }

    internal static InstanceIpcEnvelope MapTaskStartQueueResult(
        InstanceIpcEnvelope request, BgiTaskCoordinator.SubmitResult result, int generation)
        => result.Status switch
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
            BgiTaskCoordinator.SubmitStatus.IdempotencyConflict => InstanceIpcEnvelope.Failure(
                request, "idempotency_conflict", "同一发送键已绑定不同的任务内容"),
            BgiTaskCoordinator.SubmitStatus.InvalidSubmission => InstanceIpcEnvelope.Failure(
                request, "invalid_request", "带发送键的任务缺少可信请求指纹"),
            // 仅当调用发生在提交之前，Unavailable 才会到达这里；明确零发送拒绝，不回退 v2。
            BgiTaskCoordinator.SubmitStatus.Unavailable => InstanceIpcEnvelope.Failure(
                request, "queue_unavailable", "任务队列协调器不可用；未切换到 v2 task.start，请恢复连接后重新发起"),
            _ => InstanceIpcEnvelope.Failure(request, "service_unavailable", "任务队列返回了未知状态；未切换到 v2 task.start"),
        };

    /// <summary>
    /// [切片7] ext.task.stop：新增可选参数 clearQueue（ext 通道默认 true——"停止"含"别再继续"语义，
    /// 清空时在队项逐项发 task.queueCancelled）；v2 task.stop 无此参数，行为不变。
    /// [R5 A4] 携带定向停止身份键的请求**不清队列、也不读 clearQueue**：队列清理属于全量停止语义，
    /// 而定向请求可能因身份过期被拒绝——先清队列就构成"拒绝前已产生副作用"。
    /// 协调器按需惰性取得（`factory`）：定向请求与 clearQueue=false 都不触碰进程级单例。
    /// </summary>
    internal static InstanceIpcEnvelope DispatchTaskStop(
        InstanceRequestHandler handler,
        InstanceConnection connection,
        InstanceIpcEnvelope request)
        => DispatchTaskStop(handler, connection, request, () => BgiTaskCoordinator.Instance);

    /// <summary>可注入协调器的内部重载（测试用；生产走进程级单例）。</summary>
    internal static InstanceIpcEnvelope DispatchTaskStop(
        InstanceRequestHandler handler,
        InstanceConnection connection,
        InstanceIpcEnvelope request,
        Func<BgiTaskCoordinator> coordinatorFactory)
    {
        if (!InstanceRequestHandler.HasDirectionalStopIntent(request)
            && (request.Data?["clearQueue"]?.ToObject<bool?>() ?? true))
        {
            coordinatorFactory().ClearQueue();
        }

        return handler.HandleTaskStop(connection, request);
    }

    /// <summary>
    /// [切片7] ext.task.cancel {taskHandle}：在队 → 移除+task.queueCancelled；
    /// 在跑且句柄匹配 → 等价 task.stop（复用 HandleTaskStop 单一事实源）；否则 task_not_found。
    /// [R5 A4] 该操作不接受定向停止身份字段：一旦携带就在**任何状态变更之前**拒绝，
    /// 避免"先按句柄取消、再因身份不匹配失败"的混合语义。
    /// </summary>
    private static InstanceIpcEnvelope DispatchTaskCancel(
        InstanceRequestHandler handler,
        InstanceConnection connection,
        InstanceIpcEnvelope request)
    {
        if (InstanceRequestHandler.HasDirectionalStopIntent(request))
        {
            return InstanceIpcEnvelope.Failure(request, "invalid_request",
                "ext.task.cancel 不接受执行身份字段；按执行身份定向停止请使用带 executionInstanceId 的 task.stop 通道");
        }

        var handleRaw = request.Data?["taskHandle"]?.ToString();
        if (!Guid.TryParse(handleRaw, out var handle))
        {
            return InstanceIpcEnvelope.Failure(request, "invalid_request", "taskHandle 缺失或格式错误");
        }

        if (request.Data?["cancelIdentity"] is { } frozen &&
            (frozen is not Newtonsoft.Json.Linq.JObject identity || !MatchesCancelIdentity(identity,
                BetterGenshinImpact.Service.Execution.JobRegistry.IsCreated
                    ? BetterGenshinImpact.Service.Execution.JobRegistry.Instance.Query(handle) : null)))
            return InstanceIpcEnvelope.Failure(request, "cancel_identity_mismatch", "原纪元或作业出现身份不可核对；未取消");

        var ownedOnly = request.Data?["ownedOnly"]?.ToString() == "v1";
        return BgiTaskCoordinator.Instance.CancelByHandle(handle, ownedOnly) switch
        {
            BgiTaskCoordinator.CancelOutcome.CancelledQueued => InstanceIpcEnvelope.Response(
                request, new { status = "cancelled", taskHandle = handleRaw, wasQueued = true }),
            BgiTaskCoordinator.CancelOutcome.StopRequestedRunning when ownedOnly =>
                InstanceIpcEnvelope.Response(request, new { status = "stop_requested", taskHandle = handleRaw }),
            BgiTaskCoordinator.CancelOutcome.StopRequestedRunning =>
                handler.HandleTaskStop(connection, request),
            _ => InstanceIpcEnvelope.Failure(
                request, "task_not_found", $"任务句柄不存在或已结束: {handleRaw}"),
        };
    }
    internal static bool MatchesCancelIdentity(Newtonsoft.Json.Linq.JObject identity,
        BetterGenshinImpact.Service.Execution.BgiJob? job)
    {
        var epoch = BetterGenshinImpact.Service.Execution.JobRegistry.CurrentEpoch;
        try
        {
            return job is not null && identity["epoch"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                && identity["epoch"]!.ToString() == $"{epoch.ProcessId}:{epoch.StartTicksUtc}"
                && identity["idempotencyKey"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                && !string.IsNullOrWhiteSpace(job.IdempotencyKey) && identity["idempotencyKey"]!.ToString() == job.IdempotencyKey
                && Guid.TryParse(identity["workflowRunId"]?.ToString(), out var run) && run == job.WorkflowRunId
                && identity["nodeId"]?.Type == Newtonsoft.Json.Linq.JTokenType.String
                && identity["nodeId"]!.ToString() == job.NodeId
                && identity["iteration"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer && identity["iteration"]!.Value<int>() == job.Iteration
                && identity["occurrence"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer && identity["occurrence"]!.Value<int>() == job.Occurrence
                && identity["attempt"]?.Type == Newtonsoft.Json.Linq.JTokenType.Integer && identity["attempt"]!.Value<int>() == job.Attempt;
        }
        catch (Exception ex) when (ex is ArgumentException or InvalidCastException or FormatException or OverflowException) { return false; }
    }

}
