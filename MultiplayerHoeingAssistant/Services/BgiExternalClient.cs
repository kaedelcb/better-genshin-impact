using System.Collections.Concurrent;
using System.IO;
using System.IO.Pipes;
using System.Text;
using System.Text.Json;
using MultiplayerHoeingAssistant.Helpers;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>ext.* 链路状态：Down=未连接/断线中；Legacy=对端是老版本 BGI（无 ext.hello）；Ready=握手成功。</summary>
public enum BgiExternalLinkState
{
    Down,
    Legacy,
    Ready,
}

/// <summary>
/// [切片4] SDK 连接状态机（《通信方案》§3.2 会话层状态机的客户端半边）：
/// Connecting（建立/重试连接）→ Handshaking（ext.hello 协商中）→ Ready（通道可用）
/// → Degraded（曾就绪后断开 / 握手失败降级 / 对端老 BGI，可能恢复）→ Closed（Dispose 终态）。
/// 初始值 Degraded（尚未连接 = 不可用）；跨会话等不可恢复错误也落 Degraded（由调用方退避后重建 client 再探测）。
/// </summary>
public enum BgiExternalConnectionState
{
    Connecting,
    Handshaking,
    Ready,
    Degraded,
    Closed,
}

/// <summary>ext.event 事件名常量（与 BGI 侧 ExternalInterfaceEventNames 对齐）。</summary>
public static class BgiExternalEventNames
{
    public const string TaskStarted = "task.started";
    public const string TaskProgress = "task.progress";
    public const string TaskStopped = "task.stopped";
    public const string HoeingProgress = "hoeing.progress";
    public const string OnlineTriggered = "online.triggered";
    public const string TaskSuspended = "task.suspended";
    public const string TaskResumed = "task.resumed";

    // [切片7] 任务协调器生命周期事件（《BGI任务协调层设计方案》§4.3）
    public const string TaskQueued = "task.queued";
    public const string TaskCompleted = "task.completed";
    public const string TaskFailed = "task.failed";
    public const string TaskQueueCancelled = "task.queueCancelled";
    public const string TaskSlotReleased = "task.slotReleased";

    // [A3.1] 统一作业注册表事件族（总计划 §6.4）：jobId 与 taskHandle 同一 Guid 别名
    public const string JobQueued = "job.queued";
    public const string JobStarted = "job.started";
    public const string JobCompleted = "job.completed";
    public const string JobFailed = "job.failed";
    public const string JobCancelled = "job.cancelled";
    public const string JobHeartbeat = "job.heartbeat";
    /// <summary>[A5-3] 龙父作业进度（currentIndex/total/currentItemName）。订阅收帧即够，当前无消费方。</summary>
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
}

/// <summary>[切片7] ext.task.start 队列式提交的响应投影。</summary>
public sealed class BgiTaskSubmitResult
{
    public bool Success { get; init; }

    /// <summary>queued / adopted / already_executed。</summary>
    public string? Status { get; init; }

    /// <summary>服务端任务句柄（queued/adopted 时有效），事件路由键。</summary>
    public string? TaskHandle { get; init; }

    public int QueuePosition { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>[终态可拉取] ext.task.queueStatus 响应投影：pending/running/completed/failed/queueCancelled/not_found。</summary>
public sealed class BgiTaskQueueStatus
{
    public string? Status { get; init; }

    /// <summary>completed 专用：执行中被取消（F11 等）。</summary>
    public bool Cancelled { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>[A3.2] 进程纪元（fencing，总计划 §4.2）：pid + 启动 ticks。帧间比对识别 BGI 重启。</summary>
public sealed class BgiEpoch
{
    public int ProcessId { get; init; }

    public long StartTicksUtc { get; init; }
}

/// <summary>[A3.2] 注册表作业快照条目（ext.job.status/list 的 jobs[] 元素投影）。</summary>
public sealed class BgiJobInfo
{
    public string? WorkflowRunId { get; init; }
    public string? NodeId { get; init; }
    public int? Iteration { get; init; }
    public string? AttemptId { get; init; }
    public string? TaskId { get; init; }
    public string? ConfigRevision { get; init; }
    public string? IdempotencyKey { get; init; }
    public string? JobId { get; init; }

    public string? ParentJobId { get; init; }

    public string? Kind { get; init; }

    public string? Name { get; init; }

    public string? Source { get; init; }

    public int? Generation { get; init; }

    /// <summary>queued / running / cancelling / succeeded / failed / cancelled / rejected。</summary>
    public string? State { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }

    public bool WasCancelled { get; init; }

    public DateTime? EnqueuedAtUtc { get; init; }

    public DateTime? StartedAtUtc { get; init; }

    public DateTime? FinishedAtUtc { get; init; }
}

/// <summary>[A3.2] ext.job.list 响应投影：注册表全量快照（reconcile 对账输入）。</summary>
public sealed class BgiJobListSnapshot
{
    public BgiEpoch? Epoch { get; init; }

    public bool TriggerDispatcherRunning { get; init; }

    public IReadOnlyList<BgiJobInfo> Jobs { get; init; } = [];
}

/// <summary>[切片7] 任务终态事件种类。</summary>
public enum BgiTaskTerminalKind
{
    Completed,
    Failed,
    QueueCancelled,
}

/// <summary>[切片7] 一条任务终态事件（task.completed/failed/queueCancelled，按 taskHandle 路由）。</summary>
public sealed class BgiTaskTerminalEvent
{
    public required BgiTaskTerminalKind Kind { get; init; }

    public required string TaskHandle { get; init; }

    /// <summary>task.completed 专用：执行中被取消（F11 等）。</summary>
    public bool Cancelled { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>一条 ext.event 事件帧（服务端主动推送，Notification 语义）。</summary>
public sealed class BgiExternalEvent
{
    public required string Name { get; init; }

    /// <summary>与 ext.task.status 快照同源的状态版本号；客户端发现跳号可拉快照补齐。</summary>
    public long StateRevision { get; init; }

    public JsonElement Payload { get; init; }
}

/// <summary>[切片4] 一次 ext.task.status 快照（SDK 自动拉取：订阅基线 / revision 跳号 / 断线恢复校准）。</summary>
public sealed class BgiExternalStatusSnapshot
{
    /// <summary>快照 data 的原始 JSON 文本（字段与 v2 task.status 一致，另有 stateRevision）。</summary>
    public required string DataJson { get; init; }

    /// <summary>快照携带的 stateRevision；revision ≤ 此值的事件已被快照覆盖，SDK 不再分派。</summary>
    public required long StateRevision { get; init; }

    /// <summary>触发原因（subscribe-baseline / revision-gap / event:xxx / resync-required），供日志与测试。</summary>
    public required string Reason { get; init; }
}

/// <summary>ext.* 请求响应（信封 response 的投影）。</summary>
public sealed class BgiExternalResponse
{
    public bool Success { get; init; }

    /// <summary>data 对象的原始 JSON 文本（无 data 时为 null）。</summary>
    public string? Data { get; init; }

    public string? ErrorCode { get; init; }

    public string? ErrorMessage { get; init; }
}

/// <summary>
/// 模块一助手侧 SDK：封装 BGI 的 ext.* 外部接口（《通信方案》§3.2 分层架构的客户端半边）。
/// 一条长连接：连接后先 ext.hello 握手协商 capabilities，之后命令（request/response 按 requestId 关联）
/// 与事件（ext.event 推送）复用同一管道；断线自动重连并恢复订阅。
/// 对端是老版本 BGI（ext.hello 返回 unsupported_operation）→ State=Legacy，调用方降级回
/// IpcClient/CommandExecutor 旧路径（§3.9 优雅降级链），全程无报错。
/// 帧格式与 IpcClient 完全一致：[4字节 length][1字节 type=1][JSON]，length 不含 type 字节。
/// </summary>
public sealed class BgiExternalClient : IDisposable
{
    public string? TakeoverTicket { get; set; }
    public BgiEpoch? ServerEpoch { get; private set; }
    private const int MaxPayloadLength = 1024 * 1024;
    private static readonly TimeSpan CommandTimeout = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReconnectDelay = TimeSpan.FromSeconds(5);
    /// <summary>[A0 容错] 连接存活短于此阈值视为"连上即被对端断开"的抖动故障，重连走退避节拍而非零间隔。</summary>
    private static readonly TimeSpan MinHealthyLifetime = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan FirstHandshakeWait = TimeSpan.FromSeconds(7);

    private readonly string _pipeName;
    private readonly CancellationTokenSource _lifetimeCts = new();
    private readonly ConcurrentDictionary<string, TaskCompletionSource<BgiExternalResponse>> _pendingRequests = new();
    private readonly SemaphoreSlim _writeLock = new(1, 1);
    private readonly object _subscribeLock = new();

    private NamedPipeClientStream? _pipe;
    private Task? _managerTask;
    private List<string> _subscribedEvents = new();
    private bool _subscriptionActive;
    private bool _disposed;

    // ===== [切片4] revision 同源闭环状态（LSP 文档同步模型）=====
    // _maxDispatchedRevision：已分派给事件处理方的最大事件 revision；
    // _snapshotFloorRevision：已被快照覆盖的 revision 下界（≤ 它的事件不再分派）；
    // 恢复订阅时把两者较大者作为 lastKnownRevision 上报服务端续传。
    // 单管道帧流严格有序，会同源快照一起保证事件不丢不重（重复只在快照边界，处理方幂等吸收）。
    private readonly object _revisionLock = new();
    private long _maxDispatchedRevision;
    private long _snapshotFloorRevision;
    private DateTime _lastSnapshotPullUtc = DateTime.MinValue;
    private int _snapshotPullInFlight;

    private static readonly TimeSpan SnapshotPullThrottle = TimeSpan.FromMilliseconds(300);

    /// <summary>[切片4] 最近一次 ext.task.status 快照的 data JSON（事件驱动刷新；null = 尚未取得）。</summary>
    public string? LatestStatusSnapshotJson { get; private set; }

    /// <summary>[切片4] 最近快照的 stateRevision。</summary>
    public long LatestStatusRevision { get; private set; }

    /// <summary>[切片4] 快照更新通知（在 SDK 读线程/线程池触发；处理方须幂等、不得阻塞）。</summary>
    public event Action<BgiExternalStatusSnapshot>? StatusSnapshotUpdated;

    public BgiExternalLinkState State { get; private set; } = BgiExternalLinkState.Down;

    /// <summary>[切片4] 连接状态机（只读）；变更经 <see cref="ConnectionStateChanged"/> 广播。</summary>
    public BgiExternalConnectionState ConnectionState { get; private set; } = BgiExternalConnectionState.Degraded;

    /// <summary>连接状态机变更通知（在 SDK 管理/读线程触发，Handler 异常不影响连接管理）。</summary>
    public event Action<BgiExternalConnectionState>? ConnectionStateChanged;

    private void SetConnectionState(BgiExternalConnectionState next)
    {
        if (ConnectionState == next)
        {
            return;
        }

        ConnectionState = next;
        try
        {
            ConnectionStateChanged?.Invoke(next);
        }
        catch
        {
            // 状态通知异常不杀死连接管理循环
        }
    }

    public string? BgiVersion { get; private set; }

    public string? SessionId { get; private set; }

    public IReadOnlyDictionary<string, bool>? Capabilities { get; private set; }

    /// <summary>事件通道是否可用于替代轮询（Ready 且已恢复订阅）。</summary>
    public bool IsEventChannelActive =>
        State == BgiExternalLinkState.Ready && _subscriptionActive;

    /// <summary>服务端主动推送事件（在 SDK 读线程触发；Handler 内的异常不会杀死读循环）。</summary>
    public event Action<BgiExternalEvent>? EventReceived;

    public BgiExternalClient()
    {
        var sid = System.Security.Principal.WindowsIdentity.GetCurrent()?.User?.Value;
        _pipeName = $"BetterGI.v2.user-{sid}.root";
    }

    /// <summary>
    /// 启动连接管理（首次连接 + 握手）。在首次握手出结果前返回：
    /// Ready=新 BGI 可用；Legacy=老 BGI（调用方降级）；Down=BGI 未启动等（内部继续重连）。
    /// </summary>
    public async Task<BgiExternalLinkState> StartAsync(CancellationToken cancellationToken = default)
    {
        if (_managerTask is not null)
        {
            return State;
        }

        // 注意：不把外部 ct 链接进管理循环（链接 CTS 随 StartAsync 返回即释放），
        // 管理循环只受 _lifetimeCts（Dispose）控制；外部取消通过下方等待循环检查。
        _managerTask = Task.Run(
            () => ConnectionManagerAsync(_lifetimeCts.Token),
            CancellationToken.None);

        var deadline = Task.Delay(FirstHandshakeWait, CancellationToken.None);
        while (State == BgiExternalLinkState.Down
               && !deadline.IsCompleted
               && !_managerTask.IsCompleted
               && !cancellationToken.IsCancellationRequested)
        {
            await Task.Delay(100, CancellationToken.None);
        }

        return State;
    }

    /// <summary>发送 ext.* 命令并等待响应。通道未就绪时抛 InvalidOperationException（调用方降级）。</summary>
    public async Task<BgiExternalResponse> SendCommandAsync(
        string operation,
        object? payload = null,
        TimeSpan? timeout = null,
        CancellationToken cancellationToken = default)
    {
        var pipe = _pipe;
        if (State != BgiExternalLinkState.Ready || pipe is null || !pipe.IsConnected)
        {
            throw new InvalidOperationException("ext 通道未就绪");
        }

        var requestId = Guid.NewGuid().ToString("N");
        var completionSource = new TaskCompletionSource<BgiExternalResponse>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        if (!_pendingRequests.TryAdd(requestId, completionSource))
        {
            throw new InvalidOperationException($"重复的 ext 请求 ID：{requestId}");
        }

        try
        {
            if (TakeoverTicket != null || ServerEpoch != null && HasCapability("execution.contract.v1"))
            {
                var fields = System.Text.Json.JsonSerializer.SerializeToNode(payload ?? new { })!.AsObject();
                if (TakeoverTicket is { } ticket) fields["takeoverTicket"] = ticket;
                if (fields["bgiEpoch"] == null && ServerEpoch is { } epoch)
                    fields["bgiEpoch"] = System.Text.Json.JsonSerializer.SerializeToNode(new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc });
                payload = fields;
            }
            await WriteEnvelopeAsync(pipe, operation, requestId, payload, cancellationToken)
                .ConfigureAwait(false);
            using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(
                cancellationToken, _lifetimeCts.Token);
            return await completionSource.Task
                .WaitAsync(timeout ?? CommandTimeout, timeoutCts.Token)
                .ConfigureAwait(false);
        }
        finally
        {
            _pendingRequests.TryRemove(requestId, out _);
        }
    }

    /// <summary>
    /// 订阅事件。空列表 = 全部已知事件。成功后 IsEventChannelActive 变为 true；重连自动恢复订阅。
    /// [切片4] 服务端声明 event.replay 能力时携带 lastKnownRevision 续传（服务端补发缺失事件）；
    /// 订阅成功后自动拉一次 ext.task.status 基线快照（resyncRequired / 跳号 / 窗口期事件统一由快照校准兜底）。
    /// </summary>
    public async Task SubscribeAsync(
        IReadOnlyList<string> events,
        CancellationToken cancellationToken = default)
    {
        long? lastKnown = null;
        if (HasCapability(ExternalOperations.CapabilityEventReplay))
        {
            lock (_revisionLock)
            {
                var known = Math.Max(_maxDispatchedRevision, _snapshotFloorRevision);
                if (known > 0)
                {
                    lastKnown = known;
                }
            }
        }

        var response = await SendCommandAsync(
                ExternalOperations.EventSubscribe,
                lastKnown is { } knownRevision
                    ? new { events, lastKnownRevision = knownRevision }
                    : new { events },
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.Success)
        {
            throw new InvalidOperationException(
                $"ext.event.subscribe 失败: {response.ErrorMessage ?? response.ErrorCode ?? "未知错误"}");
        }

        lock (_subscribeLock)
        {
            _subscribedEvents = events.Count == 0
                ? new List<string>(BgiExternalEventNames.All)
                : events.ToList();
            _subscriptionActive = true;
        }

        // 基线快照：首订初始化状态、重连恢复校准（含 resyncRequired）统一走这里
        await RefreshStatusSnapshotAsync("subscribe-baseline", cancellationToken).ConfigureAwait(false);
    }

    public async Task UnsubscribeAsync(
        IReadOnlyList<string> events,
        CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(
                ExternalOperations.EventUnsubscribe,
                new { events },
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (response.Success)
        {
            lock (_subscribeLock)
            {
                if (events.Count == 0)
                {
                    _subscribedEvents.Clear();
                    _subscriptionActive = false;
                }
                else
                {
                    foreach (var name in events)
                    {
                        _subscribedEvents.Remove(name);
                    }

                    if (_subscribedEvents.Count == 0)
                    {
                        _subscriptionActive = false;
                    }
                }
            }
        }
    }

    /// <summary>v2 操作名 → ext.* 操作名映射（切片4 迁移期：调用点优先走 ext 长连接，失败回退 v2 短连接）。</summary>
    public static bool TryMapToExtOperation(string v2OpCode, out string extOperation)
    {
        extOperation = v2OpCode switch
        {
            "task.status" => ExternalOperations.TaskStatus,
            "config.list" => ExternalOperations.ConfigList,
            "config.pull_group" => ExternalOperations.ConfigPullGroup,
            "config.apply_group" => ExternalOperations.ConfigApplyGroup,
            "config.open_remote_editor" => ExternalOperations.ConfigOpenRemoteEditor,
            "config.remote_editor_result" => ExternalOperations.ConfigRemoteEditorResult,
            _ => string.Empty,
        };
        return extOperation.Length > 0;
    }

    /// <summary>能力查询（DAP 规则：缺省即不支持）。</summary>
    public bool HasCapability(string name)
        => Capabilities is not null
           && Capabilities.TryGetValue(name, out var supported)
           && supported;

    /// <summary>[切片7] 能力位：BGI 支持队列式任务编排（ext.task.start 入队 + 生命周期事件 + ext.task.cancel）。</summary>
    public const string CapabilityTaskQueue = "task.queue";

    /// <summary>[A3.1] 能力位：BGI 统一作业注册表观察面（job.* 事件族 + ext.job.status/ext.job.list + bgiEpoch 全帧）。</summary>
    public const string CapabilityJobRegistry = "job.registry";

    /// <summary>只请求取消给定句柄；应答不代表清理完成，调用方须查询该句柄终态。</summary>
    public Task<BgiExternalResponse> CancelOwnedTaskAsync(string taskHandle, CancellationToken ct)
        => SendCommandAsync(ExternalOperations.TaskCancel, new { taskHandle, ownedOnly = "v1" }, CommandTimeout, ct);

    /// <summary>停止当前任务，默认同时清空排队任务。</summary>
    public Task<BgiExternalResponse> StopTaskAsync(bool clearQueue = true, CancellationToken cancellationToken = default)
        => SendCommandAsync(ExternalOperations.TaskStop, new { clearQueue }, CommandTimeout, cancellationToken);

    /// <summary>
    /// [切片7] 队列式提交 task.start：入队拿 taskHandle 立即返回（BGI 侧串行派发）。
    /// 执行结果不走响应，走 task.completed/failed 事件（配合 CreateTaskTerminalWaiter 先订阅后动作）。
    /// 通道未就绪抛 InvalidOperationException（调用方降级 v2 路径）。
    /// [A6] preempt=true（仅 AllReady 批次/抢占下发置位，普通手动下发不置位）：BGI 队列项等槽窗口
    /// 缩为 3s、未果转主动抢占（保存恢复点+取消+30s 状态确认）。纯加法字段，老 BGI 忽略零感知；
    /// v2 通道无此字段（抢占闭环的 v2 task.start 前置 suspend+settle 已自行腾空槽位）。
    /// </summary>
    public async Task<BgiTaskSubmitResult> SubmitTaskStartAsync(
        string? groupName,
        string? configName,
        int startFromIndex,
        int generation,
        string? batchGroupNames = null,
        CancellationToken cancellationToken = default,
        bool preempt = false, string? idempotencyKey = null,
        string? expectedConfigRevision = null, object? bgiEpoch = null, DateTimeOffset? expiresAtUtc = null, bool coordinatedHoeing = false)
    {
        var payload = preempt
            ? (object)new { groupName, configName, startFromIndex, generation, batchGroupNames, preempt = true, idempotencyKey, expectedConfigRevision, bgiEpoch, expiresAtUtc, coordinatedHoeing = coordinatedHoeing ? "v1" : null }
            : new { groupName, configName, startFromIndex, generation, batchGroupNames, idempotencyKey, expectedConfigRevision, bgiEpoch, expiresAtUtc, coordinatedHoeing = coordinatedHoeing ? "v1" : null };
        var response = await SendCommandAsync(
                ExternalOperations.TaskStart,
                payload,
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);

        if (!response.Success || response.Data is null)
        {
            return new BgiTaskSubmitResult
            {
                Success = false,
                ErrorCode = response.ErrorCode,
                ErrorMessage = response.ErrorMessage,
            };
        }

        using var doc = JsonDocument.Parse(response.Data);
        var root = doc.RootElement;
        return new BgiTaskSubmitResult
        {
            Success = true,
            Status = root.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String
                ? stEl.GetString()
                : null,
            TaskHandle = root.TryGetProperty("taskHandle", out var thEl) && thEl.ValueKind == JsonValueKind.String
                ? thEl.GetString()
                : null,
            QueuePosition = root.TryGetProperty("queuePosition", out var qpEl) && qpEl.ValueKind == JsonValueKind.Number
                ? qpEl.GetInt32()
                : 0,
        };
    }

    /// <summary>
    /// [终态可拉取 2026-09-09] 按句柄拉取队列项生命周期（ext.task.queueStatus）——
    /// 终态事件的安全网校准：事件帧丢失/被吞时由轮询兜底，不再把批次进度押在"一帧必达"上。
    /// 返回 null = 通道瞬态失败或对端老 BGI 无此操作（调用方继续等事件，不误判）。
    /// </summary>
    public async Task<BgiTaskQueueStatus?> QueryTaskQueueStatusAsync(
        string taskHandle,
        CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(
                ExternalOperations.TaskQueueStatus,
                new { taskHandle },
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.Success || response.Data is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(response.Data);
        var root = doc.RootElement;
        return new BgiTaskQueueStatus
        {
            Status = root.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String
                ? stEl.GetString()
                : null,
            Cancelled = root.TryGetProperty("cancelled", out var cEl) && cEl.ValueKind == JsonValueKind.True,
            ErrorCode = root.TryGetProperty("errorCode", out var ecEl) && ecEl.ValueKind == JsonValueKind.String
                ? ecEl.GetString()
                : null,
            ErrorMessage = root.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()
                : null,
        };
    }

    /// <summary>
    /// [A3.2] 拉注册表全量快照（ext.job.list）——reconcile 对账循环（总计划 §4.5）的输入。
    /// 返回 null = 通道瞬态失败或对端老 BGI 无此操作（调用方保持既有路径，不误判）。
    /// </summary>
    public async Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(
                ExternalOperations.JobList,
                null,
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.Success || response.Data is null)
        {
            return null;
        }

        using var doc = JsonDocument.Parse(response.Data);
        var root = doc.RootElement;
        var jobs = new List<BgiJobInfo>();
        if (root.TryGetProperty("jobs", out var jobsEl) && jobsEl.ValueKind == JsonValueKind.Array)
        {
            foreach (var jobEl in jobsEl.EnumerateArray())
            {
                jobs.Add(ParseJobInfo(jobEl));
            }
        }

        return new BgiJobListSnapshot
        {
            Epoch = ParseEpoch(root),
            TriggerDispatcherRunning = root.TryGetProperty("triggerDispatcherRunning", out var tdEl)
                                       && tdEl.ValueKind == JsonValueKind.True,
            Jobs = jobs,
        };
    }

    /// <summary>
    /// [A3.2] 按 jobId 拉单个作业生命周期（ext.job.status）。
    /// 返回 (null, null) = 通道瞬态失败；(status, null) = 应答成功（status 可能为 not_found）。
    /// </summary>
    public async Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(
        string jobId,
        CancellationToken cancellationToken = default)
    {
        var response = await SendCommandAsync(
                ExternalOperations.JobStatus,
                new { jobId },
                CommandTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (!response.Success || response.Data is null)
        {
            return (null, null);
        }

        using var doc = JsonDocument.Parse(response.Data);
        var root = doc.RootElement;
        var status = root.TryGetProperty("status", out var stEl) && stEl.ValueKind == JsonValueKind.String
            ? stEl.GetString()
            : null;
        var job = root.TryGetProperty("job", out var jobEl) && jobEl.ValueKind == JsonValueKind.Object
            ? ParseJobInfo(jobEl)
            : null;
        return (status, job);
    }

    private static BgiJobInfo ParseJobInfo(JsonElement el)
    {
        static string? Str(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
        static DateTime? Ts(JsonElement e, string prop)
            => e.TryGetProperty(prop, out var v) && v.ValueKind == JsonValueKind.String
               && DateTime.TryParse(v.GetString(), out var t) ? t : null;

        return new BgiJobInfo
        {
            JobId = Str(el, "jobId"),
            IdempotencyKey = Str(el, "idempotencyKey"),
            ParentJobId = Str(el, "parentJobId"),
            WorkflowRunId = Str(el, "workflowRunId"),
            NodeId = Str(el, "nodeId"),
            Iteration = el.TryGetProperty("iteration", out var iterationEl) && iterationEl.ValueKind == JsonValueKind.Number ? iterationEl.GetInt32() : null,
            AttemptId = Str(el, "attemptId"),
            TaskId = Str(el, "taskId"),
            ConfigRevision = Str(el, "configRevision"),
            Kind = Str(el, "kind"),
            Name = Str(el, "name"),
            Source = Str(el, "source"),
            Generation = el.TryGetProperty("generation", out var genEl) && genEl.ValueKind == JsonValueKind.Number
                ? genEl.GetInt32()
                : null,
            State = Str(el, "state"),
            ErrorCode = Str(el, "errorCode"),
            ErrorMessage = Str(el, "errorMessage"),
            WasCancelled = el.TryGetProperty("wasCancelled", out var wcEl) && wcEl.ValueKind == JsonValueKind.True,
            EnqueuedAtUtc = Ts(el, "enqueuedAtUtc"),
            StartedAtUtc = Ts(el, "startedAtUtc"),
            FinishedAtUtc = Ts(el, "finishedAtUtc"),
        };
    }

    private static BgiEpoch? ParseEpoch(JsonElement root)
    {
        if (!root.TryGetProperty("bgiEpoch", out var epochEl) || epochEl.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        return new BgiEpoch
        {
            ProcessId = epochEl.TryGetProperty("processId", out var pidEl) && pidEl.ValueKind == JsonValueKind.Number
                ? pidEl.GetInt32()
                : 0,
            StartTicksUtc = epochEl.TryGetProperty("startTicksUtc", out var tkEl) && tkEl.ValueKind == JsonValueKind.Number
                ? tkEl.GetInt64()
                : 0,
        };
    }

    /// <summary>
    /// [切片7] 创建任务终态事件等待器：创建即订阅（先订阅后动作，红线7），之后按 taskHandle 等待。
    /// 断线重连后 SDK 恢复订阅 + lastKnownRevision 补发，事件经 EventReceived 分派，等待不丢。
    /// 用毕 Dispose 退订。
    /// </summary>
    public BgiTaskTerminalWaiter CreateTaskTerminalWaiter() => new(this);

    /// <summary>
    /// [切片7] 等一次 task.slotReleased 事件（P1-C settle 判定事件化：槽位真正释放才继续，
    /// 替代"轮询 running 翻转后立即 start 撞清理窗口"）。true = 槽位已释放；false = 超时（调用方走轮询兜底）。
    /// 先订阅后动作：订阅在本方法返回前的同步段完成。
    /// </summary>
    public Task<bool> WaitSlotReleasedAsync(TimeSpan timeout, CancellationToken cancellationToken = default)
    {
        var completionSource = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        void Handler(BgiExternalEvent evt)
        {
            if (evt.Name == BgiExternalEventNames.TaskSlotReleased)
            {
                completionSource.TrySetResult(true);
            }
        }

        EventReceived += Handler;
        return AwaitCoreAsync();

        async Task<bool> AwaitCoreAsync()
        {
            try
            {
                return await completionSource.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
            }
            catch (TimeoutException)
            {
                return false;
            }
            finally
            {
                EventReceived -= Handler;
            }
        }
    }

    /// <summary>
    /// [切片4] 拉取一次 ext.task.status 快照并更新 LatestStatusSnapshot* / 触发 StatusSnapshotUpdated。
    /// 节流 300ms + 在飞去重；通道非 Ready 或失败时静默返回（下一次事件/跳号会再触发）。
    /// </summary>
    public async Task RefreshStatusSnapshotAsync(string reason, CancellationToken cancellationToken = default)
    {
        if (State != BgiExternalLinkState.Ready)
        {
            return;
        }

        if (Interlocked.Exchange(ref _snapshotPullInFlight, 1) != 0)
        {
            return;
        }

        try
        {
            var sinceLast = DateTime.UtcNow - _lastSnapshotPullUtc;
            if (sinceLast < SnapshotPullThrottle)
            {
                await Task.Delay(SnapshotPullThrottle - sinceLast, cancellationToken).ConfigureAwait(false);
            }

            var response = await SendCommandAsync(
                    ExternalOperations.TaskStatus, null, CommandTimeout, cancellationToken)
                .ConfigureAwait(false);
            if (!response.Success || response.Data is null)
            {
                return;
            }

            long revision = 0;
            try
            {
                using var doc = JsonDocument.Parse(response.Data);
                if (doc.RootElement.TryGetProperty("stateRevision", out var revEl)
                    && revEl.ValueKind == JsonValueKind.Number)
                {
                    revision = revEl.GetInt64();
                }
            }
            catch (JsonException)
            {
                // stateRevision 缺失按 0 处理（不影响快照内容使用）
            }

            _lastSnapshotPullUtc = DateTime.UtcNow;
            LatestStatusSnapshotJson = response.Data;
            LatestStatusRevision = revision;
            if (revision > 0)
            {
                lock (_revisionLock)
                {
                    // 快照覆盖线单调前进：晚到的旧快照不回退覆盖线
                    _snapshotFloorRevision = Math.Max(_snapshotFloorRevision, revision);
                }
            }

            try
            {
                StatusSnapshotUpdated?.Invoke(new BgiExternalStatusSnapshot
                {
                    DataJson = response.Data,
                    StateRevision = revision,
                    Reason = reason,
                });
            }
            catch
            {
                // 快照通知异常不影响 SDK 主流程
            }
        }
        catch (Exception exception) when (exception is InvalidOperationException
                                          or IOException
                                          or TimeoutException
                                          or OperationCanceledException
                                          or JsonException)
        {
            // 通道瞬态不可用：静默，下一次事件/跳号会再触发
        }
        catch (Exception)
        {
            // 兜底吸收：本方法常以 fire-and-forget 调用（revision 跳号 / 事件触发刷新），
            // 任何逃逸异常都会冒泡到 TaskScheduler.UnobservedTaskException 弹全局异常框
            // （App.xaml.cs:85），必须就地吞掉；下一次事件/跳号会再触发重试
        }
        finally
        {
            Interlocked.Exchange(ref _snapshotPullInFlight, 0);
        }
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _lifetimeCts.Cancel();
        SetConnectionState(BgiExternalConnectionState.Closed);
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
            // 清理路径吞异常
        }

        foreach (var pair in _pendingRequests)
        {
            pair.Value.TrySetCanceled();
        }

        _pendingRequests.Clear();
        _writeLock.Dispose();
        _lifetimeCts.Dispose();
    }

    // ===== 连接管理 =====

    /// <summary>
    /// 连接管理主循环：建立连接 → 握手 → 读循环（挂起）→ 连接死亡 → 退避重连。
    /// Legacy / 不可信（跨会话）视为不可恢复，退出循环由调用方退避再探测。
    /// </summary>
    private async Task ConnectionManagerAsync(CancellationToken cancellationToken)
    {
        var delay = TimeSpan.Zero;
        while (!cancellationToken.IsCancellationRequested)
        {
            if (delay > TimeSpan.Zero)
            {
                try
                {
                    await Task.Delay(delay, cancellationToken).ConfigureAwait(false);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
            }

            try
            {
                SetConnectionState(BgiExternalConnectionState.Connecting);
                if (await TryEstablishOnceAsync(cancellationToken).ConfigureAwait(false))
                {
                    var connectedAtUtc = DateTime.UtcNow;
                    // 挂起直到连接死亡（读循环退出即连接关闭）
                    if (_readerLoop is { } reader)
                    {
                        await reader.ConfigureAwait(false);
                    }
                    // [A0 容错 2026-09-13] 热重连防护：连接存活过短（疑似对端反复接受/关闭的
                    // 循环故障）时按退避节拍重连，避免零间隔连接风暴；健康长连接的死亡仍立即重连。
                    if (DateTime.UtcNow - connectedAtUtc < MinHealthyLifetime)
                    {
                        delay = ReconnectDelay;
                        System.Diagnostics.Debug.WriteLine("[BgiExternalClient] 连接存活过短（疑似对端反复断开），退避重连");
                    }
                    else
                    {
                        delay = TimeSpan.Zero;
                    }
                }
                else
                {
                    delay = ReconnectDelay;
                }
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                break;
            }
            catch
            {
                // 单次建立失败（BGI 未启动/管道忙等），退避重试
                delay = ReconnectDelay;
            }

            if (State is BgiExternalLinkState.Legacy || _unrecoverable)
            {
                // 版本/会话问题不会在 BGI 运行期间自愈，退出，交给调用方退避再探测
                break;
            }
        }
    }

    private Task? _readerLoop;
    private bool _unrecoverable;

    /// <summary>单次建立连接 + ext.hello 握手 + 启动读循环。返回 false 表示本次未就绪。</summary>
    private async Task<bool> TryEstablishOnceAsync(CancellationToken cancellationToken)
    {
        DisposePipe();
        var pipe = new NamedPipeClientStream(
            ".", _pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
        try
        {
            await pipe.ConnectAsync(2000, cancellationToken).ConfigureAwait(false);
        }
        catch
        {
            pipe.Dispose();
            State = BgiExternalLinkState.Down;
            return false;
        }

        _pipe = pipe;
        SetConnectionState(BgiExternalConnectionState.Handshaking);

        BgiExternalResponse hello;
        try
        {
            var requestId = Guid.NewGuid().ToString("N");
            await WriteEnvelopeAsync(
                    pipe,
                    ExternalOperations.Hello,
                    requestId,
                    new
                    {
                        clientName = "MultiplayerHoeingAssistant",
                        clientVersion = System.Reflection.Assembly.GetExecutingAssembly()
                            .GetName().Version?.ToString() ?? "unknown",
                        requiredCapabilities = new[] { ExternalOperations.CapabilityEventPush },
                    },
                    cancellationToken)
                .ConfigureAwait(false);
            var envelope = await ReadEnvelopeAsync(pipe, cancellationToken).ConfigureAwait(false);
            hello = ToResponse(envelope)
                    ?? throw new IOException("ext.hello 未收到响应");
        }
        catch
        {
            DisposePipe();
            State = BgiExternalLinkState.Down;
            return false;
        }

        if (!hello.Success)
        {
            DisposePipe();
            if (hello.ErrorCode == "unsupported_operation")
            {
                // 老版本 BGI：优雅降级，无报错（§3.4/A2）
                State = BgiExternalLinkState.Legacy;
            }
            else if (hello.ErrorCode == "cross_session_rejected")
            {
                _unrecoverable = true;
                State = BgiExternalLinkState.Down;
            }
            else
            {
                State = BgiExternalLinkState.Down;
            }

            SetConnectionState(BgiExternalConnectionState.Degraded);
            return false;
        }

        // 会话校验：与 IpcClient.VerifyRemoteSessionAsync 同级的跨会话防护（BGI 侧守卫之外的客户端自检）
        if (hello.Data is not null)
        {
            try
            {
                using var doc = JsonDocument.Parse(hello.Data);
                var root = doc.RootElement;
                if (root.TryGetProperty("sessionId", out var sidEl)
                    && sidEl.ValueKind == JsonValueKind.String)
                {
                    // [切片4] 服务端会话变更 = BGI 进程已重启 → stateRevision 计数随进程归零，
                    // 本地 revision 追踪必须复位，否则旧高水位会把新事件流全部判成"重复"而永久跳过
                    if (SessionId is not null && sidEl.GetString() != SessionId)
                    {
                        lock (_revisionLock)
                        {
                            _maxDispatchedRevision = 0;
                            _snapshotFloorRevision = 0;
                        }

                        LatestStatusSnapshotJson = null;
                        LatestStatusRevision = 0;
                    }

                    SessionId = sidEl.GetString();
                }
                ServerEpoch = ParseEpoch(root);

                if (root.TryGetProperty("bgiVersion", out var verEl)
                    && verEl.ValueKind == JsonValueKind.String)
                {
                    BgiVersion = verEl.GetString();
                }

                if (root.TryGetProperty("capabilities", out var capsEl)
                    && capsEl.ValueKind == JsonValueKind.Object)
                {
                    Capabilities = JsonSerializer.Deserialize<Dictionary<string, bool>>(
                        capsEl.GetRawText());
                }

                if (root.TryGetProperty("windowsSessionId", out var wsEl)
                    && wsEl.ValueKind == JsonValueKind.Number
                    && wsEl.TryGetInt32(out var remoteSession))
                {
                    var localSession = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                    if (remoteSession != localSession)
                    {
                        // 跨会话：不采信、不使用（与 42b81a04 发送端阻断语义一致）
                        DisposePipe();
                        _unrecoverable = true;
                        State = BgiExternalLinkState.Down;
                        SetConnectionState(BgiExternalConnectionState.Degraded);
                        return false;
                    }
                }
            }
            catch
            {
                // 握手数据解析失败按不可用处理
                DisposePipe();
                State = BgiExternalLinkState.Down;
                SetConnectionState(BgiExternalConnectionState.Degraded);
                return false;
            }
        }

        State = BgiExternalLinkState.Ready;
        SetConnectionState(BgiExternalConnectionState.Ready);
        _readerLoop = Task.Run(() => ReaderLoopAsync(pipe, _lifetimeCts.Token), CancellationToken.None);

        // 重连后恢复订阅（订阅挂在会话上，新连接需要重新声明）
        List<string> eventsToRestore;
        lock (_subscribeLock)
        {
            eventsToRestore = new List<string>(_subscribedEvents);
        }

        if (eventsToRestore.Count > 0)
        {
            try
            {
                await SubscribeAsync(eventsToRestore, cancellationToken).ConfigureAwait(false);
            }
            catch
            {
                // 恢复订阅失败：连接仍可用，下一轮由调用方补订
                lock (_subscribeLock)
                {
                    _subscriptionActive = false;
                }
            }
        }

        return true;
    }

    private async Task ReaderLoopAsync(NamedPipeClientStream pipe, CancellationToken cancellationToken)
    {
        try
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                var envelope = await ReadEnvelopeAsync(pipe, cancellationToken).ConfigureAwait(false);
                if (envelope is null)
                {
                    break; // EOF：服务端关闭
                }

                if (envelope.Operation == ExternalOperations.EventPush)
                {
                    DispatchEvent(envelope);
                    continue;
                }

                if (envelope.Operation == ExternalOperations.Response
                    && !string.IsNullOrEmpty(envelope.RequestId)
                    && _pendingRequests.TryGetValue(NormalizeRequestId(envelope.RequestId), out var completionSource))
                {
                    completionSource.TrySetResult(ToResponse(envelope)!);
                }
            }
        }
        catch (Exception exception) when (exception is IOException
                                          or EndOfStreamException
                                          or OperationCanceledException
                                          or ObjectDisposedException
                                          or InvalidDataException
                                          or JsonException)
        {
            // 连接断开/取消：进入重连流程
        }
        finally
        {
            if (!_disposed)
            {
                State = BgiExternalLinkState.Down;
                SetConnectionState(BgiExternalConnectionState.Degraded);
                FailPendingRequests(new IOException("ext 连接已断开"));
                lock (_subscribeLock)
                {
                    // 订阅声明保留，重连成功后自动恢复
                    _subscriptionActive = false;
                }
            }
        }
    }

    private void DispatchEvent(Envelope envelope)
    {
        if (envelope.DataJson is null || EventReceived is null)
        {
            return;
        }

        try
        {
            using var doc = JsonDocument.Parse(envelope.DataJson);
            var root = doc.RootElement;
            if (root.TryGetProperty("event", out var nameEl)
                && nameEl.ValueKind == JsonValueKind.String
                && nameEl.GetString() is { } name)
            {
                long revision = 0;
                if (root.TryGetProperty("stateRevision", out var revEl)
                    && revEl.ValueKind == JsonValueKind.Number)
                {
                    revision = revEl.GetInt64();
                }

                // [切片4] revision 同源闭环：
                // - rev ≤ 快照覆盖线：状态已含在快照里 → 跳过分派（不重）；
                // - rev ≤ 已分派最大值：补发/乱序重复 → 跳过（单管道有序流，防御性）；
                // - rev > 已知最大 + 1：跳号（断线窗口漏帧）→ 自动拉快照补齐（LSP 文档同步模型）。
                // [终态免误吞 2026-09-09] 终态事件（task.completed/failed/queueCancelled）是"事实"而非
                // "状态"：快照覆盖线只覆盖状态，覆盖不了"某任务已终结"这件事。终态按 taskHandle 路由、
                // 等待器幂等消费（先到先得），迟到/乱序/补发的终态帧必须送达——被覆盖线吞掉会让
                // 批次循环永久挂起（实机确诊：批次跑完第一个组后停摆，BGI 侧其实已发布 completed）。
                if (revision > 0)
                {
                    var isTerminalEvent = name is BgiExternalEventNames.TaskCompleted
                        or BgiExternalEventNames.TaskFailed
                        or BgiExternalEventNames.TaskQueueCancelled;
                    var gapDetected = false;
                    lock (_revisionLock)
                    {
                        if (revision <= _snapshotFloorRevision || revision <= _maxDispatchedRevision)
                        {
                            if (!isTerminalEvent)
                            {
                                // [可观测性] 静默丢弃是此类故障最难查的形态，必须留痕
                                RuntimeLog.WriteLine(
                                    $"[ext] 事件被 revision 过滤丢弃：{name} rev={revision} floor={_snapshotFloorRevision} maxDispatched={_maxDispatchedRevision}");
                                return;
                            }

                            RuntimeLog.WriteLine(
                                $"[ext] 终态事件迟到/乱序仍送达（按句柄路由幂等消费）：{name} rev={revision} floor={_snapshotFloorRevision} maxDispatched={_maxDispatchedRevision}");
                        }
                        else
                        {
                            var knownBase = Math.Max(_maxDispatchedRevision, _snapshotFloorRevision);
                            if (knownBase > 0 && revision > knownBase + 1)
                            {
                                gapDetected = true;
                            }

                            _maxDispatchedRevision = revision;
                        }
                    }

                    if (gapDetected)
                    {
                        _ = RefreshStatusSnapshotAsync("revision-gap");
                    }
                }

                JsonElement payload = default;
                if (root.TryGetProperty("payload", out var payloadEl))
                {
                    payload = payloadEl.Clone();
                }

                EventReceived.Invoke(new BgiExternalEvent
                {
                    Name = name,
                    StateRevision = revision,
                    Payload = payload,
                });
            }
        }
        catch
        {
            // 单条事件解析失败不影响读循环
        }
    }

    private void FailPendingRequests(Exception exception)
    {
        foreach (var pair in _pendingRequests)
        {
            pair.Value.TrySetException(exception);
        }

        _pendingRequests.Clear();
    }

    /// <summary>
    /// requestId 归一化（[实机修复] 2026-09-05 根因修复）：
    /// 本 SDK 生成 requestId 用 Guid.ToString("N")（无连字符），而 BGI 侧信封 RequestId 是 Guid 类型——
    /// Newtonsoft 反序列化时把 "N" 解析为 Guid，响应回显时按默认 "D" 格式（带连字符）序列化，
    /// 直接字符串比较永不匹配 → 响应被静默丢弃、所有 ext 命令 5s 超时（事件推送无需关联故不受影响）。
    /// 统一按 Guid 解析回 "N" 格式再匹配；非 Guid 原样返回（前向兼容）。
    /// </summary>
    private static string NormalizeRequestId(string requestId)
        => Guid.TryParse(requestId, out var guid) ? guid.ToString("N") : requestId;

    private void DisposePipe()
    {
        try
        {
            _pipe?.Dispose();
        }
        catch
        {
            // 清理路径吞异常
        }

        _pipe = null;
    }

    // ===== 帧读写（与 IpcClient / InstanceIpcProtocol 逐字节一致）=====

    private async Task WriteEnvelopeAsync(
        NamedPipeClientStream pipe,
        string operation,
        string requestId,
        object? payload,
        CancellationToken cancellationToken)
    {
        var envelope = new
        {
            version = 2,
            requestId,
            operation,
            data = payload,
        };
        var json = JsonSerializer.Serialize(envelope);
        var bytes = Encoding.UTF8.GetBytes(json);

        var frame = new byte[4 + 1 + bytes.Length];
        BitConverter.GetBytes(bytes.Length).CopyTo(frame, 0);
        frame[4] = 1; // InstanceIpcPayloadType.Utf8Json
        Buffer.BlockCopy(bytes, 0, frame, 5, bytes.Length);

        await _writeLock.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            await pipe.WriteAsync(frame, cancellationToken).ConfigureAwait(false);
            await pipe.FlushAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _writeLock.Release();
        }
    }

    private static async Task<Envelope?> ReadEnvelopeAsync(
        Stream pipe,
        CancellationToken cancellationToken)
    {
        var header = new byte[4];
        var headerRead = await ReadExactAsync(pipe, header, 4, cancellationToken, allowEofOnFirstByte: true)
            .ConfigureAwait(false);
        if (headerRead == 0)
        {
            return null; // EOF
        }

        var payloadLength = BitConverter.ToInt32(header, 0);
        if (payloadLength <= 0 || payloadLength > MaxPayloadLength)
        {
            throw new InvalidDataException($"无效的 ext 响应长度: {payloadLength}");
        }

        // 流中实际占 length + 1 字节（1 字节 type + length 字节 JSON）
        var payload = new byte[payloadLength + 1];
        await ReadExactAsync(pipe, payload, payload.Length, cancellationToken, allowEofOnFirstByte: false)
            .ConfigureAwait(false);

        if (payload[0] != 1) throw new InvalidDataException("ext 响应不是 JSON 帧");

        using var doc = JsonDocument.Parse(Encoding.UTF8.GetString(payload, 1, payloadLength));
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("version", out var version)
            || version.ValueKind != JsonValueKind.Number || !version.TryGetInt32(out var protocolVersion) || protocolVersion != 2)
            throw new InvalidDataException("不支持的 ext 响应协议版本");

        string? operation = null;
        if (root.TryGetProperty("operation", out var opEl) && opEl.ValueKind == JsonValueKind.String)
        {
            operation = opEl.GetString();
        }

        string? requestId = null;
        if (root.TryGetProperty("requestId", out var ridEl) && ridEl.ValueKind == JsonValueKind.String)
        {
            requestId = ridEl.GetString();
        }

        var success = root.TryGetProperty("success", out var sEl)
                      && sEl.ValueKind == JsonValueKind.True;

        string? errorCode = null;
        if (root.TryGetProperty("errorCode", out var codeEl) && codeEl.ValueKind == JsonValueKind.String)
        {
            errorCode = codeEl.GetString();
        }

        string? errorMessage = null;
        if (root.TryGetProperty("errorMessage", out var msgEl) && msgEl.ValueKind == JsonValueKind.String)
        {
            errorMessage = msgEl.GetString();
        }

        string? dataJson = null;
        if (root.TryGetProperty("data", out var dataEl) && dataEl.ValueKind == JsonValueKind.Object)
        {
            dataJson = dataEl.GetRawText();
        }

        return new Envelope(
            success,
            errorCode,
            errorMessage,
            dataJson,
            operation ?? string.Empty,
            requestId ?? string.Empty);
    }

    private static async Task<int> ReadExactAsync(
        Stream pipe,
        byte[] buffer,
        int count,
        CancellationToken cancellationToken,
        bool allowEofOnFirstByte)
    {
        var read = 0;
        while (read < count)
        {
            var n = await pipe.ReadAsync(buffer.AsMemory(read, count - read), cancellationToken)
                .ConfigureAwait(false);
            if (n == 0)
            {
                if (allowEofOnFirstByte && read == 0)
                {
                    return 0;
                }

                throw new EndOfStreamException("ext 管道连接已断开");
            }

            read += n;
        }

        return read;
    }

    private static BgiExternalResponse? ToResponse(Envelope? envelope)
        => envelope is null
            ? null
            : new BgiExternalResponse
            {
                Success = envelope.Success,
                Data = envelope.DataJson,
                ErrorCode = envelope.ErrorCode,
                ErrorMessage = envelope.ErrorMessage,
            };

    private sealed record Envelope(
        bool Success,
        string? ErrorCode,
        string? ErrorMessage,
        string? DataJson,
        string Operation,
        string RequestId);

    /// <summary>ext.* 操作名与 capability 名（与 BGI 侧 ExternalInterfaceOperations 对齐）。</summary>
    private static class ExternalOperations
    {
        public const string Hello = "ext.hello";
        public const string TaskStart = "ext.task.start";
        public const string TaskStop = "ext.task.stop";
        public const string TaskCancel = "ext.task.cancel";
        public const string TaskStatus = "ext.task.status";
        public const string TaskQueueStatus = "ext.task.queueStatus";
        // [A3.2] 统一作业注册表拉取面
        public const string JobStatus = "ext.job.status";
        public const string JobList = "ext.job.list";
        public const string ConfigList = "ext.config.list";
        public const string ConfigPullGroup = "ext.config.pullGroup";
        public const string ConfigApplyGroup = "ext.config.applyGroup";
        public const string ConfigOpenRemoteEditor = "ext.config.openRemoteEditor";
        public const string ConfigRemoteEditorResult = "ext.config.remoteEditorResult";
        public const string EventSubscribe = "ext.event.subscribe";
        public const string EventUnsubscribe = "ext.event.unsubscribe";
        public const string EventPush = "ext.event";
        public const string Response = "response";
        public const string CapabilityEventPush = "event.push";
        public const string CapabilityEventReplay = "event.replay";
    }
}

/// <summary>
/// [切片7] 任务终态事件等待器：创建即订阅 SDK 事件流（先订阅后动作），按 taskHandle 路由
/// task.completed/failed/queueCancelled；句柄未知前先到的终态事件入缓冲，WaitForHandleAsync 时匹配。
/// 用毕 Dispose 退订（finally 语义由调用方 using 保证）。
/// </summary>
public sealed class BgiTaskTerminalWaiter : IDisposable
{
    private const int MaxBufferedEvents = 64;

    private readonly BgiExternalClient _client;
    private readonly object _lock = new();
    private readonly List<BgiTaskTerminalEvent> _buffered = new();
    private readonly Dictionary<string, TaskCompletionSource<BgiTaskTerminalEvent>> _waiters = new();

    internal BgiTaskTerminalWaiter(BgiExternalClient client)
    {
        _client = client;
        _client.EventReceived += OnEvent;
    }

    /// <summary>等待指定句柄的终态事件。返回 null = 超时（调用方按兜底语义处理）。</summary>
    public async Task<BgiTaskTerminalEvent?> WaitForHandleAsync(
        string taskHandle,
        TimeSpan timeout,
        CancellationToken cancellationToken = default)
    {
        var completionSource = new TaskCompletionSource<BgiTaskTerminalEvent>(
            TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_lock)
        {
            var hit = _buffered.FindIndex(e => e.TaskHandle == taskHandle);
            if (hit >= 0)
            {
                var buffered = _buffered[hit];
                _buffered.RemoveAt(hit);
                return buffered;
            }

            _waiters[taskHandle] = completionSource;
        }

        try
        {
            return await completionSource.Task.WaitAsync(timeout, cancellationToken).ConfigureAwait(false);
        }
        catch (TimeoutException)
        {
            return null;
        }
        finally
        {
            lock (_lock)
            {
                _waiters.Remove(taskHandle);
            }
        }
    }

    private void OnEvent(BgiExternalEvent evt)
    {
        var kind = evt.Name switch
        {
            BgiExternalEventNames.TaskCompleted => BgiTaskTerminalKind.Completed,
            BgiExternalEventNames.TaskFailed => BgiTaskTerminalKind.Failed,
            BgiExternalEventNames.TaskQueueCancelled => BgiTaskTerminalKind.QueueCancelled,
            _ => (BgiTaskTerminalKind?)null,
        };
        if (kind is null)
        {
            return;
        }

        if (!evt.Payload.TryGetProperty("taskHandle", out var handleEl)
            || handleEl.ValueKind != JsonValueKind.String
            || handleEl.GetString() is not { } handle)
        {
            return;
        }

        var terminal = new BgiTaskTerminalEvent
        {
            Kind = kind.Value,
            TaskHandle = handle,
            Cancelled = evt.Payload.TryGetProperty("cancelled", out var cEl)
                        && cEl.ValueKind == JsonValueKind.True,
            ErrorCode = evt.Payload.TryGetProperty("errorCode", out var ecEl)
                        && ecEl.ValueKind == JsonValueKind.String
                ? ecEl.GetString()
                : null,
            ErrorMessage = evt.Payload.TryGetProperty("message", out var msgEl)
                           && msgEl.ValueKind == JsonValueKind.String
                ? msgEl.GetString()
                : null,
        };

        TaskCompletionSource<BgiTaskTerminalEvent>? waiter = null;
        lock (_lock)
        {
            if (_waiters.Remove(handle, out waiter))
            {
                // 命中等待者（TCS 为 RunContinuationsAsynchronously，锁内 Set 安全）
            }
            else
            {
                // 句柄未知（Submit 还没返回）：入缓冲，WaitForHandleAsync 时匹配
                _buffered.Add(terminal);
                if (_buffered.Count > MaxBufferedEvents)
                {
                    _buffered.RemoveAt(0);
                }
            }
        }

        waiter?.TrySetResult(terminal);
    }

    public void Dispose()
    {
        _client.EventReceived -= OnEvent;
        lock (_lock)
        {
            foreach (var waiter in _waiters.Values)
            {
                waiter.TrySetCanceled();
            }

            _waiters.Clear();
            _buffered.Clear();
        }
    }
}
