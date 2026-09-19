using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>宿主动作状态（R4.8 一轮 I5：结构化反馈——已登记/已生效/不可用，动作不再无声吞）。</summary>
public enum HostActionStatus
{
    /// <summary>已登记（驱动循环在下一节点边界消费，如暂停/跳过/重载）。</summary>
    Registered,
    /// <summary>已生效（同步完成，如暂停态终态化停止）。</summary>
    Effective,
    /// <summary>不可用（护栏拒绝/状态不符，Message 带原因）。</summary>
    Unavailable,
}

public sealed record HostActionResult(HostActionStatus Status, string Message)
{
    public bool Ok => Status != HostActionStatus.Unavailable;
    public static HostActionResult Registered(string message) => new(HostActionStatus.Registered, message);
    public static HostActionResult Effective(string message) => new(HostActionStatus.Effective, message);
    public static HostActionResult Unavailable(string message) => new(HostActionStatus.Unavailable, message);
}

/// <summary>
/// R4.8 Batch C 任务中心进程内宿主（设计稿 §4.6）。
/// 职责：Store/Runner 组装（生产边界/前置/收尾三件套按当前 client 实况）、RecoverOnStart 启动屏障、
/// 同流程运行互斥（同一临界区「检查→预留→注册」，覆盖 Start/Resume）、驱动任务在册与异常收敛、
/// 退出限时收敛、结构化动作结果。
/// I6：宿主无自有持久化状态——内存任务表/锁不是持久化；全部事实在 WorkflowStore/RunStore。
/// 互斥语义（一轮 B4）：同 workflowId 同时只允许一个活动运行（本期产品限制，响亮拒绝不静默吞）；
/// 活动态 = Planned/Running/Waiting/Completing/Paused + 本进程预留/驱动；Unknown 运行禁止同流程新跑（需先对账）；
/// Interrupted 允许另开新运行（旧记录不动，可显式恢复）。与 R4.9 入口原子提交登记互补不替代。
/// </summary>
public sealed class TaskCenterHost
{
    /// <summary>退出收敛上限（超时=进程退出语义，下次启动恢复扫描标 Interrupted/Unknown）。</summary>
    private static readonly TimeSpan ShutdownConvergeBudget = TimeSpan.FromSeconds(10);

    private static readonly WorkflowRunState[] ActiveStates =
    [
        WorkflowRunState.Planned, WorkflowRunState.Running, WorkflowRunState.Waiting,
        WorkflowRunState.Completing, WorkflowRunState.Paused,
    ];

    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;
    private readonly ResourceCatalogService _catalog;
    private readonly Func<BgiExternalClient?> _clientAccessor;
    private readonly Action<string>? _log;

    /// <summary>测试接缝：替换生产 Runner 组装（假边界/假前置/假收尾）。</summary>
    private readonly Func<BgiExternalClient?, WorkflowStore, RunStore, WorkflowRunner>? _runnerFactory;

    /// <summary>测试接缝：替换执行就绪判定（离线拒绝与互斥护栏分开测；生产=client Ready 实况）。</summary>
    private readonly Func<(bool Ready, string? Reason)>? _readinessOverride;

    private readonly object _gate = new();
    private readonly Dictionary<string, DriveEntry> _drives = new(StringComparer.Ordinal); // key=workflowId（互斥保证唯一）
    private readonly HashSet<string> _reservedWorkflows = new(StringComparer.Ordinal); // 预留（CreateRun 窗口覆盖）
    private bool _recovered;
    private bool _shutdown;

    private sealed class DriveEntry
    {
        public required string WorkflowId { get; init; }
        public required WorkflowRunner Runner { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task<WorkflowRunRecord> Task { get; init; }
    }

    public TaskCenterHost(string flowsDir, string runsDir, string catalogCacheFile,
        Func<BgiExternalClient?> clientAccessor, Action<string>? log = null)
        : this(flowsDir, runsDir, catalogCacheFile, clientAccessor, log, null, null)
    {
    }

    /// <summary>测试接缝构造（runnerFactory/readinessOverride 均为 null 即生产行为）。</summary>
    internal TaskCenterHost(string flowsDir, string runsDir, string catalogCacheFile,
        Func<BgiExternalClient?> clientAccessor, Action<string>? log,
        Func<BgiExternalClient?, WorkflowStore, RunStore, WorkflowRunner>? runnerFactory,
        Func<(bool Ready, string? Reason)>? readinessOverride)
    {
        _workflows = new WorkflowStore(flowsDir);
        _runs = new RunStore(runsDir);
        _catalog = new ResourceCatalogService(
            () => clientAccessor() is { } c ? new BgiExternalCatalogTransport(c) : null, catalogCacheFile);
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _log = log;
        _runnerFactory = runnerFactory;
        _readinessOverride = readinessOverride;
    }

    /// <summary>运行状态变更通知（终态/动作后触发；UI 以 2s 轮询为主、本事件为辅）。</summary>
    public event EventHandler? StateChanged;

    public WorkflowStore Workflows => _workflows;
    public RunStore Runs => _runs;
    public ResourceCatalogService Catalog => _catalog;

    /// <summary>启动屏障（一轮 B5）：任何 Start/Resume 前完成一次恢复扫描（Interrupted/Unknown 标记+留痕，绝不自动恢复）。幂等。</summary>
    public void EnsureRecovered()
    {
        lock (_gate)
        {
            if (_recovered) return;
            _recovered = true;
        }
        foreach (var run in _runs.RecoverOnStart())
        {
            _log?.Invoke(run.State == WorkflowRunState.Unknown
                ? $"[任务中心] 恢复扫描：运行 {run.RunId}（流程 {run.WorkflowId}）存在在飞/未确认事实 → Unknown（需对账后才可恢复，禁止自动重跑）"
                : $"[任务中心] 恢复扫描：运行 {run.RunId}（流程 {run.WorkflowId}）→ Interrupted（可显式恢复）");
        }
    }

    /// <summary>流程目录（含隔离条目；Store 实时判型）。</summary>
    public IReadOnlyList<WorkflowCatalogEntry> ListFlows() => _workflows.List();

    /// <summary>一致性快照加载（编辑/预览用；隔离文件响亮抛出）。</summary>
    public WorkflowSnapshot LoadFlowSnapshot(string workflowId) => _workflows.LoadSnapshot(workflowId);

    /// <summary>保存流程定义（修订守卫；返回新修订号）。</summary>
    public string SaveFlow(WorkflowDocument doc, string? expectedRevision) => _workflows.Save(doc, expectedRevision);

    /// <summary>活动运行（占用槽位或需关注：Planned/Running/Waiting/Completing/Paused/Unknown/Interrupted，UpdatedAt 倒序）。</summary>
    public IReadOnlyList<WorkflowRunRecord> ListActiveRuns()
        => _runs.List()
            .Where(r => ActiveStates.Contains(r.State)
                        || r.State is WorkflowRunState.Unknown or WorkflowRunState.Interrupted)
            .OrderByDescending(r => r.UpdatedAt).ToList();

    /// <summary>历史运行（终态，UpdatedAt 倒序，至多 historyLimit 条只读展示）。</summary>
    public IReadOnlyList<WorkflowRunRecord> ListHistoryRuns(int historyLimit = 20)
        => _runs.List()
            .Where(r => !ActiveStates.Contains(r.State)
                        && r.State is not (WorkflowRunState.Unknown or WorkflowRunState.Interrupted))
            .OrderByDescending(r => r.UpdatedAt).Take(historyLimit).ToList();

    /// <summary>流程是否在本进程驱动中（UI 动作可用性判定）。</summary>
    public bool IsDriving(string workflowId)
    {
        lock (_gate) return _drives.ContainsKey(workflowId);
    }

    /// <summary>
    /// 启动流程运行（互斥护栏 + 就绪守卫 + 预检反馈 → 后台驱动在册）。
    /// Registered=已受理（运行记录随后出现在 Store）；Unavailable=响亮拒绝（原因可读）。
    /// </summary>
    public Task<HostActionResult> StartWorkflowAsync(string workflowId)
    {
        EnsureRecovered();
        WorkflowSnapshot snapshot;
        try
        {
            snapshot = _workflows.LoadSnapshot(workflowId); // 隔离/缺失响亮抛出
        }
        catch (Exception ex)
        {
            return Task.FromResult(HostActionResult.Unavailable(ex.Message));
        }

        BgiExternalClient? client;
        lock (_gate)
        {
            if (_shutdown) return Task.FromResult(HostActionResult.Unavailable("任务中心宿主已关闭"));
            var readiness = ExecutionReadiness();
            if (!readiness.Ready)
                return Task.FromResult(HostActionResult.Unavailable(readiness.Reason!));
            client = _clientAccessor();

            // 同流程互斥（一轮 B4）：活动态/Unknown/本进程预留+驱动 全集合检查
            var sameFlow = _runs.List().Where(r => r.WorkflowId == workflowId).ToList();
            if (sameFlow.Any(r => ActiveStates.Contains(r.State)))
                return Task.FromResult(HostActionResult.Unavailable("该流程已有活动运行（同流程同时只允许一个运行）"));
            if (sameFlow.Any(r => r.State == WorkflowRunState.Unknown))
                return Task.FromResult(HostActionResult.Unavailable("该流程存在结果不确定（Unknown）的运行，需先对账再启动"));
            if (_reservedWorkflows.Contains(workflowId) || _drives.ContainsKey(workflowId))
                return Task.FromResult(HostActionResult.Unavailable("该流程运行正在启动/驱动中"));
            _reservedWorkflows.Add(workflowId); // 预留：覆盖「检查 → CreateRun 落盘」窗口
        }

        // 引擎 StartAsync 内权威预检（同步响亮抛出 → LaunchDrive 捕获转 Unavailable 带原因，UX 反馈不另建重复预检）
        var runner = CreateRunner(client);
        return Task.FromResult(LaunchDrive(workflowId, runner,
            cts => runner.StartAsync(workflowId, cts.Token), $"已受理启动（流程「{snapshot.Document.Name}」）"));
    }

    /// <summary>显式恢复运行（Interrupted/Paused；Unknown 拒绝——需先对账）。与 Start 共用互斥临界区。</summary>
    public Task<HostActionResult> ResumeRunAsync(string runId)
    {
        EnsureRecovered();
        var run = _runs.Load(runId);
        if (run is null) return Task.FromResult(HostActionResult.Unavailable("运行记录不存在：" + runId));
        if (run.State == WorkflowRunState.Unknown)
            return Task.FromResult(HostActionResult.Unavailable("运行结果不确定（Unknown），需先按幂等键+job 查询对账，禁止自动恢复"));
        if (run.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused))
            return Task.FromResult(HostActionResult.Unavailable($"仅 Interrupted/Paused 可恢复（当前 {run.State}）"));

        BgiExternalClient? client;
        lock (_gate)
        {
            if (_shutdown) return Task.FromResult(HostActionResult.Unavailable("任务中心宿主已关闭"));
            var readiness = ExecutionReadiness();
            if (!readiness.Ready)
                return Task.FromResult(HostActionResult.Unavailable(readiness.Reason!));
            client = _clientAccessor();
            if (_reservedWorkflows.Contains(run.WorkflowId) || _drives.ContainsKey(run.WorkflowId))
                return Task.FromResult(HostActionResult.Unavailable("该流程已有运行正在驱动（禁止双驱动）"));
            _reservedWorkflows.Add(run.WorkflowId);
        }

        var runner = CreateRunner(client);
        return Task.FromResult(LaunchDrive(run.WorkflowId, runner,
            cts => runner.ResumeAsync(runId, cts.Token), $"已受理恢复（运行 {runId}，游标身份重定位）"));
    }

    /// <summary>
    /// 运行动作转发（结构化结果，一轮 I5）：
    /// - 驱动中 → Runner.RequestAction（Registered；暂停/跳过/重载均节点边界生效）；
    /// - Paused + Stop（无驱动）→ 宿主终态化（校验无在飞提交；有在飞→拒绝提示对账）；
    /// - 其他无驱动 → Unavailable 带状态指引。
    /// </summary>
    public HostActionResult RequestRunAction(string runId, WorkflowRunAction action)
    {
        var run = _runs.Load(runId);
        if (run is null) return HostActionResult.Unavailable("运行记录不存在：" + runId);

        if (action == WorkflowRunAction.Stop && run.State == WorkflowRunState.Paused)
        {
            // 暂停态无驱动（引擎 _controls 已移除）：无在飞作业（暂停节点边界生效），宿主直接终态化
            if (run.CurrentSubmission is { InFlight: true })
                return HostActionResult.Unavailable("存在在飞提交事实（结果不可考），请按幂等键对账后再处置");
            lock (_gate)
            {
                var fresh = _runs.Load(runId);
                if (fresh?.State != WorkflowRunState.Paused)
                    return HostActionResult.Unavailable("运行状态已变化，请刷新后重试");
                fresh.State = WorkflowRunState.Cancelled;
                fresh.Note = (fresh.Note is null ? "" : fresh.Note + " ")
                    + "暂停态显式停止（无在飞作业；不触发收尾）。";
                _runs.Update(fresh);
            }
            StateChanged?.Invoke(this, EventArgs.Empty);
            return HostActionResult.Effective("已停止（暂停态终态化，未触发收尾）");
        }

        DriveEntry? entry;
        lock (_gate) _drives.TryGetValue(run.WorkflowId, out entry);
        if (entry is null || !entry.Runner.HasActiveControl(runId))
            return HostActionResult.Unavailable(
                $"运行当前不在驱动中（状态 {run.State}）：Interrupted/Paused 请用「恢复」，终态运行无需动作");
        entry.Runner.RequestAction(runId, action);
        return HostActionResult.Registered(action switch
        {
            WorkflowRunAction.Stop => "停止已登记（运行令牌取消，在飞作业将请求远端取消）",
            WorkflowRunAction.SkipCurrent => "跳过当前节点已登记（远端取消确认后推进，未确认则 Unknown 停驻）",
            WorkflowRunAction.Pause => "暂停请求已登记（当前节点结束后生效，≠停止）",
            WorkflowRunAction.ReloadDefinition => "重载修订已登记（下一节点边界生效）",
            _ => "动作已登记",
        });
    }

    /// <summary>退出：取消全部驱动 + 限时收敛（未收敛=进程退出语义，在飞事实保留待下次恢复扫描）。</summary>
    public async Task ShutdownAsync()
    {
        List<DriveEntry> drives;
        lock (_gate)
        {
            if (_shutdown) return;
            _shutdown = true;
            drives = _drives.Values.ToList();
            foreach (var d in drives) d.Cts.Cancel();
        }
        if (drives.Count == 0) return;
        var all = Task.WhenAll(drives.Select(d => d.Task));
        await Task.WhenAny(all, Task.Delay(ShutdownConvergeBudget)).ConfigureAwait(false);
        if (!all.IsCompleted)
            _log?.Invoke($"[任务中心] 宿主关闭：{drives.Count} 个运行未在 {ShutdownConvergeBudget.TotalSeconds:0}s 内收敛（在飞事实保留，下次启动恢复扫描标记）");
    }

    // ================= 内部 =================

    private (bool Ready, string? Reason) ExecutionReadiness()
    {
        if (_readinessOverride is not null) return _readinessOverride();
        var client = _clientAccessor();
        return client is null || client.State != BgiExternalLinkState.Ready
            ? (false, "BGI 离线或 ext 通道未就绪（本地可管理/预览流程，执行需本机 IPC 连接）")
            : (true, null);
    }

    private WorkflowRunner CreateRunner(BgiExternalClient? client)
    {
        if (_runnerFactory is not null) return _runnerFactory(client, _workflows, _runs);
        var c = client ?? throw new InvalidOperationException("BGI 客户端缺失（就绪守卫之外不得组装生产 Runner）");
        return new WorkflowRunner(_workflows, _runs,
            new BgiWorkflowExecutionBoundary(c, _runs),
            new BgiWorkflowPrerequisiteAdapter(c, _runs),
            new BgiWorkflowTerminalExecutor(c, _runs));
    }

    /// <summary>登记驱动任务并观察至收敛（异常按在飞事实收敛 Unknown/Interrupted，绝不留 Running 僵尸）。</summary>
    private HostActionResult LaunchDrive(string workflowId, WorkflowRunner runner,
        Func<CancellationTokenSource, Task<WorkflowRunRecord>> start, string registeredMessage)
    {
        var cts = new CancellationTokenSource();
        Task<WorkflowRunRecord> task;
        try
        {
            task = start(cts); // 预检/隔离等同步响亮抛出（在首个 await 前）
        }
        catch (Exception ex)
        {
            lock (_gate) _reservedWorkflows.Remove(workflowId);
            cts.Dispose();
            return HostActionResult.Unavailable(ex.Message);
        }
        var entry = new DriveEntry { WorkflowId = workflowId, Runner = runner, Cts = cts, Task = task };
        lock (_gate)
        {
            if (_shutdown)
            {
                _reservedWorkflows.Remove(workflowId);
                cts.Cancel();
                cts.Dispose();
                return HostActionResult.Unavailable("任务中心宿主已关闭");
            }
            _drives[workflowId] = entry;
        }
        _ = ObserveDriveAsync(entry);
        StateChanged?.Invoke(this, EventArgs.Empty);
        return HostActionResult.Registered(registeredMessage);
    }

    private async Task ObserveDriveAsync(DriveEntry entry)
    {
        try
        {
            var run = await entry.Task.ConfigureAwait(false);
            _log?.Invoke($"[任务中心] 运行 {run.RunId} 终态：{run.State}");
        }
        catch (Exception ex)
        {
            // 一轮 B5：驱动异常收敛——当前提交在飞→Unknown（结果不可考），否则→Interrupted（等价崩溃语义，可显式恢复）
            try
            {
                var run = _runs.List()
                    .Where(r => r.WorkflowId == entry.WorkflowId && ActiveStates.Contains(r.State))
                    .OrderByDescending(r => r.CreatedAt).FirstOrDefault();
                if (run is not null)
                {
                    run.State = run.CurrentSubmission is { InFlight: true }
                        ? WorkflowRunState.Unknown : WorkflowRunState.Interrupted;
                    run.Note = (run.Note is null ? "" : run.Note + " ")
                        + $"驱动异常（{ex.GetType().Name}），按{(run.State == WorkflowRunState.Unknown ? "在飞事实标 Unknown" : "Interrupted")}收敛。";
                    _runs.Update(run);
                }
            }
            catch { /* 收敛失败不遮原异常 */ }
            _log?.Invoke($"[任务中心] 运行驱动异常（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _drives.Remove(entry.WorkflowId);
                _reservedWorkflows.Remove(entry.WorkflowId);
            }
            entry.Cts.Dispose();
            StateChanged?.Invoke(this, EventArgs.Empty);
        }
    }

}