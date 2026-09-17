using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;

namespace BetterGenshinImpact.Service.Execution;

/// <summary>
/// [A1.4] 统一作业注册表（总计划 §3/§6）：BGI 内"谁在跑/排队/已完结"的唯一事实源。
///
/// 本切片（A1.4）只落地核心存储与状态机：
/// - 进程级懒单例（IsCreated 守卫，无外部消费者时零开销，模式同 BgiTaskCoordinator）；
/// - 提交登记（幂等采用：idempotencyKey / generation+name 命中在队或在跑项时复用既有 jobId）；
/// - 状态推进（单一写入者：全部变更经本类锁内完成）；
/// - 按句柄查询 + 全量快照（未来 ext.job.status / ext.job.list 的事实源）。
///
/// 尚未接线：执行泵（A2.4 整编 BgiTaskCoordinator）、事件发布（A3）、心跳（A3）。
/// 各入口收敛为"提交作业"是 A2 各切片的事，本类不感知 TaskSemaphore/执行体。
/// </summary>
public sealed class JobRegistry
{
    private static readonly Lazy<JobRegistry> _lazy = new(() =>
    {
        var registry = new JobRegistry(startHeartbeatTimer: true);
        // [A3.1] 观察面接线：注册表创建即挂 job.* 事件出口。EventHub 为进程级单例，
        // 零订阅者时 Publish 仅 revision+环形缓冲入队（近零开销，与 task.* 既有行为一致）；
        // 保持懒语义：无注册表使用则连订阅关系都不建立。
        registry.Transitioned += ExternalInterface.ExternalInterfaceEventHub.Instance.PublishJobTransition;
        // [A3-心跳] job.heartbeat 出口（纪律同 Transitioned：锁外触发、失败留痕不反噬）
        registry.Heartbeated += ExternalInterface.ExternalInterfaceEventHub.Instance.PublishJobHeartbeat;
        return registry;
    });
    public static JobRegistry Instance => _lazy.Value;
    /// <summary>IsCreated 守卫：只读查询方不应强制创建单例（同 BgiTaskCoordinator 模式）。</summary>
    public static bool IsCreated => _lazy.IsValueCreated;

    /// <summary>终态表容量：超出后 FIFO 淘汰。not_found 的语义由纪元 fencing 消解（总计划 §4.2）。</summary>
    private const int TerminalCapacity = 64;

    private readonly object _gate = new();

    /// <summary>[A3-心跳] job.heartbeat 发布节拍（总计划 §4.4 = 30s）：在跑作业周期存活信号（助手 reconcile 的事件侧活性探针）。</summary>
    private static readonly TimeSpan HeartbeatInterval = TimeSpan.FromSeconds(30);

    /// <summary>进程级心跳定时器（随单例生命周期，不 Dispose；无在跑作业时每拍仅一次空快照）。
    /// 测试实例传 startHeartbeatTimer: false——活动 Timer 被计时器队列根住不可回收，用例隔离实例不应泄漏。</summary>
    private readonly System.Threading.Timer? _heartbeatTimer;

    public JobRegistry(bool startHeartbeatTimer)
    {
        if (startHeartbeatTimer)
        {
            _heartbeatTimer = new System.Threading.Timer(_ => PublishHeartbeats(), null, HeartbeatInterval, HeartbeatInterval);
        }
    }

    /// <summary>全部已知作业（在队/在跑 + 未淘汰的终态）。</summary>
    private readonly Dictionary<Guid, BgiJob> _jobs = new();

    /// <summary>终态淘汰顺序（队首最旧）。</summary>
    private readonly Queue<Guid> _terminalOrder = new();

    /// <summary>活跃（非终态）作业的幂等索引：idempotencyKey → jobId。</summary>
    private readonly Dictionary<string, Guid> _activeByIdempotencyKey = new();

    /// <summary>活跃作业的幂等索引："{generation}|{name}" → jobId（generation 可空时退化按 name）。</summary>
    private readonly Dictionary<string, Guid> _activeByGenerationName = new();

    /// <summary>
    /// 进程纪元（fencing，总计划 §4.2）：pid + 启动时间。BGI 重启后纪元变化，
    /// 旧纪元的 jobId 一律不被信任；查询/事件出口统一携带。
    /// [A3.1] 静态化：hello/事件帧等出口需要在不创建注册表实例的前提下携带纪元
    /// （进程级常量，首次访问即捕获，与实例 Epoch 同值）。
    /// </summary>
    public static (int ProcessId, long StartTicksUtc) CurrentEpoch { get; } = CaptureEpoch();

    /// <summary>实例形态纪元（== CurrentEpoch，保留给既有调用方）。</summary>
    public (int ProcessId, long StartTicksUtc) Epoch => CurrentEpoch;

    private static (int, long) CaptureEpoch()
    {
        try
        {
            using var p = Process.GetCurrentProcess();
            return (p.Id, p.StartTime.ToUniversalTime().Ticks);
        }
        catch
        {
            // 极端环境读不到启动时间时退化为仅 pid（纪元唯一性仍由 pid 复用窗口兜底）
            return (Environment.ProcessId, 0L);
        }
    }

    /// <summary>
    /// 提交登记（仅登记为 Queued，不触发执行——执行泵接线在 A2.4）。
    /// 幂等：idempotencyKey 或 generation+name 命中活跃作业时采用既有 jobId（Adopted=true）。
    /// [A2.4] jobId 显式指定时跳过按键认领直接新建（协调器 taskHandle 别名场景：
    /// 跨通道同 gen+name 并发的极端竞态下各建各的作业，幂等索引退化覆盖、后者赢，
    /// 仅影响该竞态下的去重精度，不影响执行正确性）。
    /// </summary>
    public JobSubmitResult Submit(JobKind kind, string name, JobSource source, int? generation = null,
        string? idempotencyKey = null, Guid? parentJobId = null, Guid? jobId = null)
    {
        BgiJob? created = null;
        JobSubmitResult result;
        lock (_gate)
        {
            if (jobId is { } explicitId && _jobs.TryGetValue(explicitId, out var existing))
                return new JobSubmitResult(existing, true);
            if (jobId is null)
            {
                if (!string.IsNullOrEmpty(idempotencyKey)
                    && _activeByIdempotencyKey.TryGetValue(idempotencyKey, out var byKey)
                    && _jobs.TryGetValue(byKey, out var keyJob) && !keyJob.IsTerminal)
                {
                    return new JobSubmitResult(keyJob, true);
                }

                var adoptKey = $"{kind}|{generation?.ToString() ?? "-"}|{parentJobId}|{name}";
                if (string.IsNullOrEmpty(idempotencyKey) && generation.HasValue && parentJobId == null
                    && _activeByGenerationName.TryGetValue(adoptKey, out var byGen)
                    && _jobs.TryGetValue(byGen, out var genJob) && !genJob.IsTerminal)
                {
                    return new JobSubmitResult(genJob, true);
                }
            }

            created = new BgiJob(kind, name, source, generation, idempotencyKey, parentJobId, jobId);
            _jobs[created.JobId] = created;
            if (!string.IsNullOrEmpty(idempotencyKey))
            {
                _activeByIdempotencyKey[idempotencyKey] = created.JobId;
            }
            var genKey = $"{kind}|{generation?.ToString() ?? "-"}|{parentJobId}|{name}";
            _activeByGenerationName[genKey] = created.JobId;
            result = new JobSubmitResult(created, false);
        }
        // [A3.1] job.queued 事件（锁外触发）
        FireTransitioned(created);
        return result;
    }

    /// <summary>推进到 Running。作业不存在或已是终态时返回 false。</summary>
    public bool TryMarkRunning(Guid jobId) => Transition(jobId, JobState.Running, null);

    /// <summary>推进到 Cancelling（取消已请求、执行体未退出）。</summary>
    public bool TryMarkCancelling(Guid jobId, string? reason = null) => Transition(jobId, JobState.Cancelling, reason);

    /// <summary>
    /// 推进到终态（Succeeded/Failed/Cancelled/Rejected）。登记终态并执行容量淘汰。
    /// 终态判定优先于一切在跑/在队读数（与 BgiTaskCoordinator 同一纪律）。
    /// </summary>
    public bool TryMarkTerminal(Guid jobId, JobState terminal, string? errorCode = null,
        string? errorMessage = null, bool wasCancelled = false)
    {
        if (terminal is not (JobState.Succeeded or JobState.Failed or JobState.Cancelled or JobState.Rejected))
        {
            throw new ArgumentException($"非终态: {terminal}", nameof(terminal));
        }

        BgiJob? transitioned = null;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || job.IsTerminal)
            {
                return false;
            }

            job.ErrorCode = errorCode;
            job.ErrorMessage = errorMessage;
            job.WasCancelled = wasCancelled;
            job.TransitionTo(terminal, errorMessage ?? errorCode);

            // 活跃幂等索引摘除
            if (!string.IsNullOrEmpty(job.IdempotencyKey))
            {
                if (_activeByIdempotencyKey.TryGetValue(job.IdempotencyKey, out var indexed) && indexed == jobId)
                    _activeByIdempotencyKey.Remove(job.IdempotencyKey);
            }
            var generationKey = $"{job.Kind}|{job.Generation?.ToString() ?? "-"}|{job.ParentJobId}|{job.Name}";
            if (_activeByGenerationName.TryGetValue(generationKey, out var named) && named == jobId)
                _activeByGenerationName.Remove(generationKey);

            // 终态表容量淘汰（FIFO）
            _terminalOrder.Enqueue(jobId);
            while (_terminalOrder.Count > TerminalCapacity)
            {
                var evicted = _terminalOrder.Dequeue();
                _jobs.Remove(evicted);
            }
            transitioned = job;
        }
        // [A3.1] 终态事件（锁外触发；纪律：终态登记先于终态事件——此处登记已在锁内完成）
        FireTransitioned(transitioned);
        return true;
    }

    /// <summary>[A3 心跳预埋] 更新在跑作业的心跳时间。</summary>
    public void TouchHeartbeat(Guid jobId)
    {
        lock (_gate)
        {
            if (_jobs.TryGetValue(jobId, out var job) && job.State == JobState.Running)
            {
                job.LastHeartbeatAtUtc = DateTime.UtcNow;
            }
        }
    }

    /// <summary>[A2.5] 是否存在活跃（非终态）作业。画中画水位等热路径只读视图用。</summary>
    public bool HasActiveJob()
    {
        lock (_gate)
        {
            return _jobs.Values.Any(j => !j.IsTerminal);
        }
    }

    /// <summary>
    /// [A3.3] 当前在跑作业（Running/Cancelling；单泵串行语义下至多一个，取先到者）。
    /// task.status 等派生视图的注册表并集/兜底读数用。
    /// </summary>
    public BgiJob? CurrentRunningJob()
    {
        lock (_gate)
        {
            return _jobs.Values.FirstOrDefault(j => j.State is JobState.Running or JobState.Cancelling);
        }
    }

    /// <summary>
    /// [A2.5] 触发器总开关（截图/触发循环）运行状态。进程级运行态而非 Job
    /// （触发器不占任务槽位、不产生作业——既有语义不变），供观察面只读视图（A3 出口）。
    /// </summary>
    public bool TriggerDispatcherRunning { get; private set; }

    /// <summary>[A2.5] 写入触发器总开关状态（单一写入者：仅 TaskTriggerDispatcher.Start/Stop 调用）。</summary>
    public void SetTriggerDispatcherRunning(bool running)
    {
        lock (_gate)
        {
            TriggerDispatcherRunning = running;
        }
    }

    // ===== [A3.4] 进程级幂等窗口（总计划 §4.1：幂等判定上移到注册表，跨连接存活）=====

    /// <summary>幂等窗口 TTL：由连接级 60s 上调为进程级 30 分钟（§4.1：覆盖一个批次的典型时长）。</summary>
    public static readonly TimeSpan IdempotencyWindowTtl = TimeSpan.FromMinutes(30);

    private const int IdempotencyWindowCapacity = 128;

    /// <summary>写操作成功响应缓存：key（key:{idempotencyKey} / rid:{requestId}）→ (首见时间, 响应)。</summary>
    private readonly Dictionary<string, (DateTime SeenAtUtc, Instance.InstanceIpcEnvelope Response)> _idempotencyWindow = new();

    /// <summary>
    /// 命中幂等窗口时返回缓存的成功响应。RequestId 重写为当前请求的——
    /// 跨连接重放（断线重连后新会话重发同 key 写操作）必须让响应能被客户端按 requestId 关联，
    /// 原样重放旧帧会被客户端相关器丢弃（信封 init 不可变，逐字段复制）。
    /// </summary>
    public bool TryReplayIdempotent(string windowKey, Guid currentRequestId, out Instance.InstanceIpcEnvelope? replay)
    {
        lock (_gate)
        {
            if (_idempotencyWindow.TryGetValue(windowKey, out var cached)
                && DateTime.UtcNow - cached.SeenAtUtc < IdempotencyWindowTtl)
            {
                replay = new Instance.InstanceIpcEnvelope
                {
                    RequestId = currentRequestId,
                    Operation = cached.Response.Operation,
                    Success = cached.Response.Success,
                    ErrorCode = cached.Response.ErrorCode,
                    ErrorMessage = cached.Response.ErrorMessage,
                    Data = cached.Response.Data, // JObject 引用共享：上线只序列化不改写，安全
                };
                return true;
            }
            replay = null;
            return false;
        }
    }

    /// <summary>登记写操作成功响应（失败不缓存：瞬态失败可重试，沿用连接级窗口既有纪律）。</summary>
    public void CacheIdempotentResponse(string windowKey, Instance.InstanceIpcEnvelope response)
    {
        lock (_gate)
        {
            if (_idempotencyWindow.Count >= IdempotencyWindowCapacity)
            {
                // 先淘汰过期项；仍满则淘汰最旧（幂等窗口是安全网，不是审计日志）
                var expiredBefore = DateTime.UtcNow - IdempotencyWindowTtl;
                foreach (var pair in _idempotencyWindow.Where(p => p.Value.SeenAtUtc < expiredBefore).ToList())
                {
                    _idempotencyWindow.Remove(pair.Key);
                }
                while (_idempotencyWindow.Count >= IdempotencyWindowCapacity)
                {
                    var oldest = _idempotencyWindow.Aggregate((a, b) => a.Value.SeenAtUtc < b.Value.SeenAtUtc ? a : b).Key;
                    _idempotencyWindow.Remove(oldest);
                }
            }
            _idempotencyWindow[windowKey] = (DateTime.UtcNow, response);
        }
    }

    /// <summary>按句柄查询。不存在（含被淘汰/旧纪元）返回 null，由调用方按纪元区分语义。</summary>
    public BgiJob? Query(Guid jobId)
    {
        lock (_gate)
        {
            return _jobs.TryGetValue(jobId, out var job) ? job : null;
        }
    }

    /// <summary>
    /// [A3.1] 状态转换观察事件（job.* 事件族的事实源）：Submit 新建与每次状态推进各触发一次。
    /// 锁外触发；订阅方异常被吞咽（日志由 EventHub 出口侧留痕），绝不反噬注册表状态机。
    /// </summary>
    public event Action<BgiJob>? Transitioned;

    private void FireTransitioned(BgiJob job)
    {
        try
        {
            Transitioned?.Invoke(job);
        }
        catch
        {
            // 观察面出口绝不反噬注册表（留痕职责在 EventHub.PublishJobTransition）
        }
    }

    /// <summary>全量快照（在队 + 在跑 + 未淘汰终态）。ext.job.list 的事实源。</summary>
    public IReadOnlyList<BgiJob> Snapshot()
    {
        lock (_gate)
        {
            return _jobs.Values.ToList();
        }
    }

    /// <summary>[A3-心跳] 在跑作业心跳事件（job.heartbeat 的事实源）：每拍刷新 LastHeartbeatAtUtc 并锁外触发。
    /// 纪律同 Transitioned：订阅方异常吞咽（留痕在 EventHub 出口侧），绝不反噬状态机。</summary>
    public event Action<BgiJob>? Heartbeated;

    private void PublishHeartbeats()
    {
        List<BgiJob> running;
        lock (_gate)
        {
            running = _jobs.Values.Where(j => j.State == JobState.Running).ToList();
            var now = DateTime.UtcNow;
            foreach (var job in running)
            {
                job.LastHeartbeatAtUtc = now;
            }
        }
        foreach (var job in running)
        {
            try
            {
                Heartbeated?.Invoke(job);
            }
            catch
            {
                // 观察面出口绝不反噬注册表（留痕职责在 EventHub.PublishJobHeartbeat）
            }
        }
    }

    private bool Transition(Guid jobId, JobState state, string? reason)
    {
        BgiJob? transitioned = null;
        lock (_gate)
        {
            if (!_jobs.TryGetValue(jobId, out var job) || job.IsTerminal)
            {
                return false;
            }
            job.TransitionTo(state, reason);
            transitioned = job;
        }
        // [A3.1] job.started 等中间态事件（锁外触发）
        FireTransitioned(transitioned);
        return true;
    }
}
