using System;
using System.Collections.Generic;
using BetterGenshinImpact.Service.Instance;
using Newtonsoft.Json.Linq;

namespace BetterGenshinImpact.Service.ExternalInterface;

/// <summary>
/// 模块一（BgiExternalInterface）v3 操作名，全部以 ext. 前缀命名。
/// 信封与帧格式完全复用 <see cref="InstanceIpcProtocol"/>（v2，一字不改）；
/// 版本与能力协商通过 ext.hello 的 capabilities 进行（DAP 规则：缺省即不支持）。
/// </summary>
internal static class ExternalInterfaceOperations
{
    public const string Prefix = "ext.";

    public const string Hello = "ext.hello";

    // 控制面（写操作，参与幂等窗口）
    public const string TaskStart = "ext.task.start";
    public const string TaskStop = "ext.task.stop";
    public const string TaskCancel = "ext.task.cancel";
    public const string TaskSuspend = "ext.task.suspend";
    public const string TaskResume = "ext.task.resume";
    public const string ConfigSetTaskEnabled = "ext.config.setTaskEnabled";
    public const string ConfigDescribe = "ext.config.describe";
    public const string ConfigApplyTaskState = "ext.config.applyTaskState";
    public const string ConfigPullGroup = "ext.config.pullGroup";
    public const string ConfigOpenRemoteEditor = "ext.config.openRemoteEditor";
    public const string ConfigRemoteEditorResult = "ext.config.remoteEditorResult";
    public const string ConfigApplyGroup = "ext.config.applyGroup";
    public const string ActionExecuteHotkey = "ext.action.executeHotkey";
    public const string ActionCloseGame = "ext.action.closeGame";

    // 查询面（只读，不参与幂等窗口）
    public const string TaskStatus = "ext.task.status";
    public const string ConfigList = "ext.config.list";

    /// <summary>[终态可拉取 2026-09-09] 队列项生命周期查询：按 taskHandle 拉取
    /// pending/running/completed/failed/queueCancelled/not_found。
    /// 事件推送只是快速路径，终态必须可拉取校验——单帧事件丢失不再让等待方永久挂起。</summary>
    public const string TaskQueueStatus = "ext.task.queueStatus";

    /// <summary>[A3.2] 统一作业注册表拉取（总计划 §6.4）：按 jobId 查询作业生命周期。
    /// jobId 与协调器 taskHandle 同一 Guid 别名；not_found + bgiEpoch 组合区分"句柄淘汰"与"BGI 重启"（§4.2）。</summary>
    public const string JobStatus = "ext.job.status";

    /// <summary>[A3.2] 注册表全量快照（在队+在跑+未淘汰终态）：助手 reconcile 循环（§4.5）的输入。</summary>
    public const string JobList = "ext.job.list";

    // 事件面
    public const string EventSubscribe = "ext.event.subscribe";
    public const string EventUnsubscribe = "ext.event.unsubscribe";

    /// <summary>服务端 → 客户端事件帧的操作名（Notification 语义，客户端不得回响应）。</summary>
    public const string EventPush = "ext.event";

    /// <summary>写操作集合：重复投递必须去重；查询/握手/订阅类天然幂等，不进窗口。</summary>
    public static bool IsWriteOperation(string operation) => operation is
        TaskStart or TaskStop or TaskCancel or TaskSuspend or TaskResume
        or ConfigSetTaskEnabled or ConfigApplyTaskState or ConfigPullGroup or ConfigOpenRemoteEditor
        or ConfigRemoteEditorResult or ConfigApplyGroup
        or ActionExecuteHotkey or ActionCloseGame;
}

/// <summary>ext.event 事件名清单（订阅过滤与文档化的唯一权威）。</summary>
internal static class ExternalInterfaceEventNames
{
    public const string TaskStarted = "task.started";
    public const string TaskProgress = "task.progress";
    public const string TaskStopped = "task.stopped";
    public const string HoeingProgress = "hoeing.progress";
    public const string OnlineTriggered = "online.triggered";
    public const string TaskSuspended = "task.suspended";
    public const string TaskResumed = "task.resumed";

    // [切片7] 任务协调器生命周期事件（《BGI任务协调层设计方案》§4.3）。
    // 注意：TaskStarted 与观察器（边沿检测）同名共存——协调器发布的 payload 带 taskHandle，
    // 按句柄路由的订阅方天然忽略无 handle 的旧事件，两者不互扰。
    public const string TaskQueued = "task.queued";
    public const string TaskCompleted = "task.completed";
    public const string TaskFailed = "task.failed";
    public const string TaskQueueCancelled = "task.queueCancelled";
    public const string TaskSlotReleased = "task.slotReleased";

    // [A3.1] 统一作业注册表事件族（总计划 §6.4）：JobRegistry.Transitioned 为唯一事实源。
    // 既有 task.* 事件保留双发一个版本周期作兼容别名，不删。
    public const string JobQueued = "job.queued";
    public const string JobStarted = "job.started";
    public const string JobCompleted = "job.completed";
    public const string JobFailed = "job.failed";
    public const string JobCancelled = "job.cancelled";
    /// <summary>心跳事件（§4.4）：发布器在心跳切片接线，事件名先放行订阅。</summary>
    public const string JobHeartbeat = "job.heartbeat";
    /// <summary>[A5-3] 龙父作业进度（父作业视角：currentIndex/total/currentItemName）。</summary>
    public const string JobProgress = "job.progress";

    public static readonly string[] All =
    [
        TaskStarted,
        TaskProgress,
        TaskStopped,
        HoeingProgress,
        OnlineTriggered,
        TaskSuspended,
        TaskResumed,
        TaskQueued,
        TaskCompleted,
        TaskFailed,
        TaskQueueCancelled,
        TaskSlotReleased,
        JobQueued,
        JobStarted,
        JobCompleted,
        JobFailed,
        JobCancelled,
        JobHeartbeat,
        JobProgress,
    ];

    private static readonly HashSet<string> KnownNames = new(All, StringComparer.Ordinal);

    public static bool IsKnown(string eventName) => KnownNames.Contains(eventName);
}

/// <summary>
/// ext.hello 握手与事件帧的协议常量/构造。
/// 设计文档 §3.3 原定"信封 version 升 3"——与代码冲突：InstanceConnection.ReceiveLoopAsync
/// 对 version 做严格相等校验（v2 之外直接抛 InvalidDataException），升 3 会拒掉所有老助手。
/// 以代码为准：信封保持 v2，protocolVersion 如实上报 2，功能演进只加 capability。
/// </summary>
internal static class ExternalInterfaceProtocol
{
    /// <summary>幂等窗口 TTL 已上移：<see cref="BetterGenshinImpact.Service.Execution.JobRegistry.IdempotencyWindowTtl"/>（A3.4，进程级 30min）。</summary>

    /// <summary>"联机锄地上线"等轻量任务的近因窗口（与 HandleTaskStatus 的 30s 语义一致）。</summary>
    public const double OnlineRecentWindowSeconds = 30;

    public static JObject BuildHelloData(
        Guid sessionId,
        int? windowsSessionId,
        int? processId)
    {
        return new JObject
        {
            ["protocolVersion"] = InstanceIpcProtocol.Version,
            ["bgiVersion"] = BetterGenshinImpact.Core.Config.Global.Version,
            ["sessionId"] = sessionId.ToString("N"),
            ["windowsSessionId"] = windowsSessionId,
            ["processId"] = processId,
            ["capabilities"] = new JObject
            {
                ["task.start"] = true,
                ["task.stop"] = true,
                ["task.suspend"] = true,
                ["task.takeover"] = true,
                ["execution.contract.v1"] = true,
                ["config.revision"] = true,
                ["config.applied"] = true,
                ["task.single.legacy"] = true,
                ["task.single.native"] = false,
                ["task.resume"] = true,
                ["task.status"] = true,
                ["config.list"] = true,
                ["config.setTaskEnabled"] = true,
                // v2.1 远程配置组编辑四操作（config.pull_group 等）的 ext 等价物
                ["config.remoteEdit"] = true,
                ["action.executeHotkey"] = true,
                ["action.closeGame"] = true,
                // v3 核心新增：事件订阅推送
                ["event.push"] = true,
                ["event.taskProgress"] = true,
                ["event.hoeingProgress"] = true,
                // 切片4：订阅可携带 lastKnownRevision，服务端从近因环形缓冲补发缺失事件（§4.6 模块一版）
                ["event.replay"] = true,
                // 切片7：队列式任务编排（ext.task.start 入队拿 taskHandle + 生命周期事件 + ext.task.cancel）
                ["task.queue"] = true,
                // 终态可拉取：ext.task.queueStatus 按句柄查询队列项生命周期（事件丢失时的校准安全网）
                ["task.queueStatus"] = true,
                ["idempotency.window"] = true,
                // [A3.1] 统一作业注册表观察面：job.* 事件族 + ext.job.status/ext.job.list 拉取
                // （jobId 与协调器 taskHandle 同一 Guid 别名，总计划 §6.4/§6.5）
                ["job.registry"] = true,
            },
            // [A3.1] 进程纪元 fencing（总计划 §4.2）：hello/事件帧/状态响应统一携带
            ["bgiEpoch"] = new JObject
            {
                ["processId"] = BetterGenshinImpact.Service.Execution.JobRegistry.CurrentEpoch.ProcessId,
                ["startTicksUtc"] = BetterGenshinImpact.Service.Execution.JobRegistry.CurrentEpoch.StartTicksUtc,
            },
        };
    }

    /// <summary>构造服务端 → 客户端事件帧 data：{ event, stateRevision, timestampUtc, bgiEpoch, payload }。</summary>
    public static JObject BuildEventData(
        string eventName,
        long stateRevision,
        object? payload)
    {
        return new JObject
        {
            ["event"] = eventName,
            ["stateRevision"] = stateRevision,
            ["timestampUtc"] = DateTime.UtcNow,
            // [A3.1] 纪元 fencing 全帧携带（§4.2）：客户端据此识别 BGI 重启，旧纪元句柄一律失效
            ["bgiEpoch"] = new JObject
            {
                ["processId"] = BetterGenshinImpact.Service.Execution.JobRegistry.CurrentEpoch.ProcessId,
                ["startTicksUtc"] = BetterGenshinImpact.Service.Execution.JobRegistry.CurrentEpoch.StartTicksUtc,
            },
            ["payload"] = payload is null
                ? new JObject()
                : JObject.FromObject(payload, InstanceIpcProtocol.Serializer),
        };
    }
}
