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
    public const string TaskSuspend = "ext.task.suspend";
    public const string TaskResume = "ext.task.resume";
    public const string ConfigSetTaskEnabled = "ext.config.setTaskEnabled";
    public const string ConfigPullGroup = "ext.config.pullGroup";
    public const string ConfigOpenRemoteEditor = "ext.config.openRemoteEditor";
    public const string ConfigRemoteEditorResult = "ext.config.remoteEditorResult";
    public const string ConfigApplyGroup = "ext.config.applyGroup";
    public const string ActionExecuteHotkey = "ext.action.executeHotkey";
    public const string ActionCloseGame = "ext.action.closeGame";

    // 查询面（只读，不参与幂等窗口）
    public const string TaskStatus = "ext.task.status";
    public const string ConfigList = "ext.config.list";

    // 事件面
    public const string EventSubscribe = "ext.event.subscribe";
    public const string EventUnsubscribe = "ext.event.unsubscribe";

    /// <summary>服务端 → 客户端事件帧的操作名（Notification 语义，客户端不得回响应）。</summary>
    public const string EventPush = "ext.event";

    /// <summary>写操作集合：重复投递必须去重；查询/握手/订阅类天然幂等，不进窗口。</summary>
    public static bool IsWriteOperation(string operation) => operation is
        TaskStart or TaskStop or TaskSuspend or TaskResume
        or ConfigSetTaskEnabled or ConfigPullGroup or ConfigOpenRemoteEditor
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

    public static readonly string[] All =
    [
        TaskStarted,
        TaskProgress,
        TaskStopped,
        HoeingProgress,
        OnlineTriggered,
        TaskSuspended,
        TaskResumed,
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
    /// <summary>幂等窗口 TTL（§3.5）。</summary>
    public const int IdempotencyWindowSeconds = 60;

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
                ["idempotency.window"] = true,
            },
        };
    }

    /// <summary>构造服务端 → 客户端事件帧 data：{ event, stateRevision, timestampUtc, payload }。</summary>
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
            ["payload"] = payload is null
                ? new JObject()
                : JObject.FromObject(payload, InstanceIpcProtocol.Serializer),
        };
    }
}
