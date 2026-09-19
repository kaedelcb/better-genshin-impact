using System;
using System.Collections.Generic;
using System.IO;
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

    /// <summary>本地执行能力提供方（R4.9 §6.2：生产=MainViewModel.IsExecutorMode，监控端 false；
    /// 移交入口与 Start/Resume 公共检查，先于台账写入与任何副作用——绕过 VM 的调用同样被拦；null=不检查（测试接缝默认）。</summary>
    private readonly Func<bool>? _localExecutionCapability;
    /// <summary>BGI 任务状态快照提供方（三轮 B1：快照辅助判定在宿主内、台账之后执行；null=测试接缝跳过快照判定，生产必传）。</summary>
    private readonly Func<ControlStatus?>? _statusSnapshotProvider;

    /// <summary>执行环境确保委托（2026-09-20 修复：任务中心启动时 BGI 未运行不会被拉起，启动直接被「BGI 离线」拒绝）。
    /// 仅三个执行入口（Start/Resume/启动移交）在就绪检查前调用：锁外、有界等待；返回 null=环境已就绪继续原流程，
    /// 非 null=响亮拒绝原因（未产生任何受理副作用）。null=不确保（测试接缝默认——保持原离线拒绝行为）。
    /// 纪律：纯查询/监控路径绝不调用本委托（监控端零副作用合同 R4.10 不破）。</summary>
    private readonly Func<CancellationToken, Task<string?>>? _ensureExecutionReady;
    private readonly object _gate = new();
    private readonly Dictionary<string, DriveEntry> _drives = new(StringComparer.Ordinal); // key=workflowId（互斥保证唯一）
    private readonly HashSet<string> _reservedWorkflows = new(StringComparer.Ordinal); // 预留（CreateRun 窗口覆盖）
    private Task? _recoverTask; // 恢复屏障任务（R4.8 二轮 阻断5：并发 Start/Resume 共同 await 同一扫描，失败重置允许重试）
    private bool _shutdown;
    /// <summary>退出取消源（会诊 阻断2：环境确保等锁外等待纳入退出管理——Shutdown 即取消，等待不得漏网）。</summary>
    private readonly CancellationTokenSource _shutdownCts = new();
    /// <summary>快照兜底窗口（会诊 重要1；生产 15s，测试接缝可注入缩短——不改变「仍空即 StatusUncertain」语义）。</summary>
    private readonly TimeSpan _snapshotWaitBudget;

    private sealed class DriveEntry
    {
        public required string WorkflowId { get; init; }
        /// <summary>准确运行身份（R4.9 移交路径已知；面板 Start 路径运行由引擎自建、为 null，收敛倒查兜底）。</summary>
        public string? RunId { get; init; }
        public required WorkflowRunner Runner { get; init; }
        public required CancellationTokenSource Cts { get; init; }
        public required Task<WorkflowRunRecord> Task { get; init; }
    }

    /// <summary>生产构造：localExecutionCapability 必传（ASTRA 二轮 I2——监控端拒绝执行入口的守卫不得遗漏接线）。</summary>
    public TaskCenterHost(string flowsDir, string runsDir, string catalogCacheFile,
        Func<BgiExternalClient?> clientAccessor, Func<bool> localExecutionCapability,
        Func<ControlStatus?> statusSnapshotProvider, Action<string>? log = null,
        Func<CancellationToken, Task<string?>>? ensureExecutionReady = null)
        : this(flowsDir, runsDir, catalogCacheFile, clientAccessor, log, null, null, localExecutionCapability, statusSnapshotProvider, ensureExecutionReady)
    {
    }

    /// <summary>测试接缝构造（runnerFactory/readinessOverride 均为 null 即生产行为）。</summary>
    internal TaskCenterHost(string flowsDir, string runsDir, string catalogCacheFile,
        Func<BgiExternalClient?> clientAccessor, Action<string>? log,
        Func<BgiExternalClient?, WorkflowStore, RunStore, WorkflowRunner>? runnerFactory,
        Func<(bool Ready, string? Reason)>? readinessOverride,
        Func<bool>? localExecutionCapability = null,
        Func<ControlStatus?>? statusSnapshotProvider = null,
        Func<CancellationToken, Task<string?>>? ensureExecutionReady = null,
        TimeSpan? snapshotWaitBudget = null)
    {
        _snapshotWaitBudget = snapshotWaitBudget ?? TimeSpan.FromSeconds(15);
        _workflows = new WorkflowStore(flowsDir);
        _runs = new RunStore(runsDir);
        _catalog = new ResourceCatalogService(
            () => clientAccessor() is { } c ? new BgiExternalCatalogTransport(c) : null, catalogCacheFile);
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _log = log;
        _runnerFactory = runnerFactory;
        _readinessOverride = readinessOverride;
        _localExecutionCapability = localExecutionCapability;
        _statusSnapshotProvider = statusSnapshotProvider;
        _ensureExecutionReady = ensureExecutionReady;    }

    /// <summary>运行状态变更通知（终态/动作后触发；UI 以 2s 轮询为主、本事件为辅）。</summary>
    public event EventHandler? StateChanged;

    public WorkflowStore Workflows => _workflows;
    public RunStore Runs => _runs;
    public ResourceCatalogService Catalog => _catalog;

    /// <summary>启动屏障（一轮 B5）：任何 Start/Resume 前完成一次恢复扫描（Interrupted/Unknown 标记+留痕，绝不自动恢复）。幂等。</summary>
    public void EnsureRecovered() => EnsureRecoveredAsync().GetAwaiter().GetResult();

    /// <summary>恢复屏障任务（二轮 阻断5）：所有 Start/Resume 共同 await 同一扫描任务——扫描完成前任何入口不得越过；
    /// 扫描失败重置为 null，下次调用重新扫描（不留永久假屏障）。</summary>
    private Task EnsureRecoveredAsync()
    {
        lock (_gate)
        {
            _recoverTask ??= RecoverScanAsync();
            return _recoverTask;
        }
    }

    private async Task RecoverScanAsync()
    {
        try
        {
            await Task.Run(() =>
            {
                foreach (var run in _runs.RecoverOnStart())
                {
                    _log?.Invoke(run.State == WorkflowRunState.Unknown
                        ? $"[任务中心] 恢复扫描：运行 {run.RunId}（流程 {run.WorkflowId}）存在在飞/未确认事实 → Unknown（需对账后才可恢复，禁止自动重跑）"
                        : $"[任务中心] 恢复扫描：运行 {run.RunId}（流程 {run.WorkflowId}）→ Interrupted（可显式恢复）");
                }
            }).ConfigureAwait(false);
        }
        catch
        {
            lock (_gate) _recoverTask = null; // 失败允许重试
            throw;
        }
    }

    /// <summary>流程目录（含隔离条目；Store 实时判型）。</summary>
    public IReadOnlyList<WorkflowCatalogEntry> ListFlows() => _workflows.List();

    /// <summary>一致性快照加载（编辑/预览用；隔离文件响亮抛出）。</summary>
    public WorkflowSnapshot LoadFlowSnapshot(string workflowId) => _workflows.LoadSnapshot(workflowId);

    /// <summary>保存流程定义（修订守卫；返回新修订号）。
    /// 二轮（阻断2）：candidate-ready 候选只读——宿主层独立禁写（不依赖 UI 列表新鲜度；激活归 R5 专用入口）。</summary>
    public string SaveFlow(WorkflowDocument doc, string? expectedRevision)
    {
        if (doc.WorkflowId is { } id)
        {
            try
            {
                if (string.Equals(_workflows.LoadSnapshot(id).Document.Activation?.Status,
                        "candidate-ready", StringComparison.Ordinal))
                    throw new InvalidOperationException(
                        "candidate-ready 候选流程为只读预览（激活归 R5 专用入口），禁止经任务中心保存覆盖。");
            }
            catch (FileNotFoundException) { /* 新建流程：无既有文件可查，放行 */ }
        }
        return _workflows.Save(doc, expectedRevision);
    }

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
    public async Task<HostActionResult> StartWorkflowAsync(string workflowId)
    {
        // R4.9 I2：关闭/能力预检先于恢复屏障（恢复扫描会写运行文件，监控端不得先产生副作用）
        lock (_gate)
        {
            if (_shutdown) return HostActionResult.Unavailable("任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } capPre) return HostActionResult.Unavailable(capPre);
        }
        await EnsureRecoveredAsync().ConfigureAwait(false);
        WorkflowSnapshot snapshot;
        try
        {
            snapshot = _workflows.LoadSnapshot(workflowId); // 隔离/缺失响亮抛出
        }
        catch (Exception ex)
        {
            return HostActionResult.Unavailable(ex.Message);
        }
        // 二轮（阻断2）：候选只读——宿主层独立禁启动（不依赖 UI 列表新鲜度）
        if (string.Equals(snapshot.Document.Activation?.Status, "candidate-ready", StringComparison.Ordinal))
            return HostActionResult.Unavailable("candidate-ready 候选流程为只读预览，禁止启动（激活归 R5 专用入口）");

        // 环境确保（2026-09-20，锁外有界等待）：BGI 未运行/通道未就绪 → 自动拉起并等待；仍不就绪响亮拒绝（未产生副作用）；
        // 等待随宿主退出取消（会诊 阻断2：确保纳入退出管理，退出期不得继续拉起/探测）
        try
        {
            if (await EnsureExecutionEnvironmentAsync(_shutdownCts.Token).ConfigureAwait(false) is { } startEnvError)
                return HostActionResult.Unavailable(startEnvError);
        }
        catch (OperationCanceledException)
        {
            return HostActionResult.Unavailable("任务中心宿主正在退出，启动已取消（未发送任何任务）");
        }

        BgiExternalClient? client;
        lock (_gate)
        {
            if (_shutdown) return HostActionResult.Unavailable("任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } capBlock) return HostActionResult.Unavailable(capBlock);
            var readiness = ExecutionReadiness();
            if (!readiness.Ready)
                return HostActionResult.Unavailable(readiness.Reason!);
            client = _clientAccessor();

            // 同流程互斥（一轮 B4）：活动态/Unknown/本进程预留+驱动 全集合检查
            var sameFlow = _runs.List().Where(r => r.WorkflowId == workflowId).ToList();
            if (sameFlow.Any(r => ActiveStates.Contains(r.State)))
                return HostActionResult.Unavailable("该流程已有活动运行（同流程同时只允许一个运行）");
            if (sameFlow.Any(r => r.State == WorkflowRunState.Unknown))
                return HostActionResult.Unavailable("该流程存在结果不确定（Unknown）的运行，需先对账再启动");
            if (_reservedWorkflows.Contains(workflowId) || _drives.ContainsKey(workflowId))
                return HostActionResult.Unavailable("该流程运行正在启动/驱动中");
            _reservedWorkflows.Add(workflowId); // 预留：覆盖「检查 → CreateRun 落盘」窗口
        }

        // 引擎 StartAsync 内权威预检（同步响亮抛出 → LaunchDrive 捕获转 Unavailable 带原因，UX 反馈不另建重复预检）
        // 二轮（重要5）：组装失败必须释放预留槽，否则该流程被永久拒绝
        WorkflowRunner runner;
        try
        {
            runner = CreateRunner(client);
        }
        catch (Exception ex)
        {
            lock (_gate) _reservedWorkflows.Remove(workflowId);
            return HostActionResult.Unavailable("执行组件组装失败：" + ex.Message);
        }
        return LaunchDrive(workflowId, runner,
            cts => runner.StartAsync(workflowId, cts.Token), $"已受理启动（流程「{snapshot.Document.Name}」）");
    }

    /// <summary>显式恢复运行（Interrupted/Paused；Unknown 拒绝——需先对账）。与 Start 共用互斥临界区。</summary>
    public async Task<HostActionResult> ResumeRunAsync(string runId)
    {
        // R4.9 I2：关闭/能力预检先于恢复屏障（恢复扫描会写运行文件，监控端不得先产生副作用）
        lock (_gate)
        {
            if (_shutdown) return HostActionResult.Unavailable("任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } capPre) return HostActionResult.Unavailable(capPre);
        }
        await EnsureRecoveredAsync().ConfigureAwait(false);
        var run = _runs.Load(runId);
        if (run is null) return HostActionResult.Unavailable("运行记录不存在：" + runId);
        if (run.State == WorkflowRunState.Unknown)
            return HostActionResult.Unavailable("运行结果不确定（Unknown），需先按幂等键+job 查询对账，禁止自动恢复");
        if (run.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused))
            return HostActionResult.Unavailable($"仅 Interrupted/Paused 可恢复（当前 {run.State}）");

        // 环境确保（同 Start：锁外有界等待，仍不就绪响亮拒绝；等待随宿主退出取消）
        try
        {
            if (await EnsureExecutionEnvironmentAsync(_shutdownCts.Token).ConfigureAwait(false) is { } resumeEnvError)
                return HostActionResult.Unavailable(resumeEnvError);
        }
        catch (OperationCanceledException)
        {
            return HostActionResult.Unavailable("任务中心宿主正在退出，恢复已取消（未发送任何任务）");
        }

        BgiExternalClient? client;
        lock (_gate)
        {
            if (_shutdown) return HostActionResult.Unavailable("任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } capBlock) return HostActionResult.Unavailable(capBlock);
            var readiness = ExecutionReadiness();
            if (!readiness.Ready)
                return HostActionResult.Unavailable(readiness.Reason!);
            client = _clientAccessor();
            // 二轮（阻断5）：与 Start 同一互斥判定——同流程其他活动运行/Unknown 一律拒绝（仅排除自身这条可恢复记录）
            var sameFlow = _runs.List().Where(r => r.WorkflowId == run.WorkflowId && r.RunId != runId).ToList();
            if (sameFlow.Any(r => ActiveStates.Contains(r.State)))
                return HostActionResult.Unavailable("该流程已有其他活动运行（同流程同时只允许一个运行）");
            if (sameFlow.Any(r => r.State == WorkflowRunState.Unknown))
                return HostActionResult.Unavailable("该流程存在结果不确定（Unknown）的其他运行，需先对账再恢复");
            if (_reservedWorkflows.Contains(run.WorkflowId) || _drives.ContainsKey(run.WorkflowId))
                return HostActionResult.Unavailable("该流程已有运行正在驱动（禁止双驱动）");
            _reservedWorkflows.Add(run.WorkflowId);
        }

        WorkflowRunner runner;
        try
        {
            runner = CreateRunner(client);
        }
        catch (Exception ex)
        {
            lock (_gate) _reservedWorkflows.Remove(run.WorkflowId);
            return HostActionResult.Unavailable("执行组件组装失败：" + ex.Message);
        }
        return LaunchDrive(run.WorkflowId, runner,
            cts => runner.ResumeAsync(runId, cts.Token), $"已受理恢复（运行 {runId}，游标身份重定位）");
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
            NotifyStateChanged();
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

    // ================= R4.9 启动移交受理入口 =================

    /// <summary>本地执行能力守卫（R4.9 §6.2 + ASTRA 二轮 I2：先于恢复屏障与台账写入等一切副作用；绕过 VM 的调用同样被拦；
    /// 提交临界区内复核——能力可能在两次检查间动态变化）。</summary>
    private string? CapabilityBlockReason()
        => _localExecutionCapability is { } cap && !cap()
            ? "当前为监控端（无本地执行能力），任务中心执行入口由能力守卫统一拒绝"
            : null;

    /// <summary>快照辅助判定（R4.9 §6.1 辅助证据 + ASTRA 三轮 B1：在宿主内、台账查询之后、仅对未命中新受理执行——受理事实优先）：
    /// start/resume 要求快照可考（不可考 → StatusUncertain，锚点 3 授权判断必须可考）且 BGI 空闲（在跑 → BgiBusy 含任务名）；
    /// armTrigger 不受限（挂载等待不占槽位，到点执行由引擎边界权威裁决）。返回 null=通过。</summary>
    internal static HandoffRegisterResult? SnapshotPrecheck(ControlStatus? snapshot, string mode)
    {
        if (mode == StartupHandoffModes.ArmTrigger) return null;
        if (snapshot is null)
            return HandoffRegisterResult.Rejected(HandoffReasonCodes.StatusUncertain,
                "BGI 任务状态快照不可考（尚无快照），授权判断要求可考（锚点 3）");
        if (snapshot.TaskRunning)
        {
            var name = snapshot.CurrentTaskGroupName is { Length: > 0 } g
                ? snapshot.CurrentTaskName is { Length: > 0 } n ? $"{g} · {n}" : g
                : snapshot.CurrentTaskName ?? "（未上报任务名）";
            return HandoffRegisterResult.Rejected(HandoffReasonCodes.BgiBusy,
                $"BGI 正在运行任务「{name}」（非本启动链提交），与移交冲突");
        }
        return null;
    }

    /// <summary>可挂载触发器判定（§4 armTrigger）。四轮 重要5 收窄：**仅 trigger.time 入口等待才算可挂载**——
    /// 结构性循环不计：DriveAsync 的轮次等待（AwaitLoopRoundStartAsync）只在 LoopIteration&gt;0 生效，首轮立即执行，
    /// 若承认 loop 可挂载，arm 会绕过 start 应有的混用/快照守卫却立即提交。带 trigger.time 的流程（含叠加循环）入口等待不变。</summary>
    private static bool HasMountableTrigger(WorkflowDocument doc)
        => WorkflowRunner.HasMountableTrigger(doc); // 七轮 重要3：与驱动起步复验共用同一定义（单一事实源）

    /// <summary>
    /// 台账命中回执（§2：命中=受理事实存在——AlreadyAccepted 附原 runId 与当前状态；ASTRA 二轮 S1 定案：
    /// Unknown 同样 AlreadyAccepted（不重新驱动、不新建运行，提示需对账），未命中请求遇同流程 Unknown 才拒绝）。
    /// 内容核对按命中的那条绑定（多绑定模型）：workflowId/mode 一致才算同一计划出现，不一致 Rejected 身份冲突。
    /// </summary>
    private static HandoffRegisterResult LedgerHitReceipt(WorkflowRunRecord hit, HandoffIdentity binding, StartupHandoffRequest request)
    {
        if (!string.Equals(hit.WorkflowId, request.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(binding.Mode, request.Mode, StringComparison.Ordinal))
            return HandoffRegisterResult.Rejected(HandoffReasonCodes.IdentityConflict,
                $"同一计划出现身份已受理为不同内容（台账 流程 {hit.WorkflowId}/语义 {binding.Mode} ≠ 本次 {request.WorkflowId}/{request.Mode}），拒绝覆盖");
        var note = hit.State switch
        {
            WorkflowRunState.Unknown =>
                $"此前已受理同一计划出现（运行 {hit.RunId} 结果不确定 Unknown，需先按幂等键+job 查询对账；不重新驱动、不新建运行）",
            _ when hit.IsTerminal =>
                $"该计划出现已完结（运行 {hit.RunId} 终态 {hit.State}）",
            WorkflowRunState.Interrupted or WorkflowRunState.Paused =>
                $"此前已受理同一计划出现（运行 {hit.RunId} 当前 {hit.State}，可在运行状态卡显式恢复，禁止自动换键重跑）",
            _ => $"此前已受理同一计划出现（运行 {hit.RunId} 当前 {hit.State}）",
        };
        return HandoffRegisterResult.AlreadyAccepted(hit.RunId, note, hit.State);
    }

    /// <summary>台账查询统一入口（I5 三态：Incomplete → Rejected LedgerIncomplete；命中 → 内容核对回执；未命中 → null 放行新受理）。</summary>
    private HandoffRegisterResult? LedgerGate(StartupHandoffRequest request)
    {
        var ledger = _runs.QueryHandoffLedger(request.IntentKey);
        if (ledger.State == HandoffLedgerState.Incomplete)
            return HandoffRegisterResult.Rejected(HandoffReasonCodes.LedgerIncomplete,
                "权威台账不完整（存在无法解析的运行记录文件）——读不到 ≠ 未受理，拒绝受理新意图，请先修复或隔离处置");
        if (ledger is { State: HandoffLedgerState.Hit, Run: { } hit, Binding: { } binding })
            return LedgerHitReceipt(hit, binding, request);
        return null;
    }

    /// <summary>锁外预检拒绝前的台账复核（ASTRA 二轮 I3：并发同键可能刚刚受理——返回其回执而非预检拒绝）。</summary>
    private HandoffRegisterResult RejectedWithLedgerRecheck(StartupHandoffRequest request, string reasonCode, string reason)
    {
        lock (_gate)
        {
            // 三轮 重要9：快返回复核与最终临界区同顺序——关闭 → 能力 → 台账（等待期间状态可能已变化）
            if (_shutdown)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.Shutdown, "任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } capBlock)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.NoCapability, capBlock);
            if (LedgerGate(request) is { } receipt)
                return receipt;
        }
        return HandoffRegisterResult.Rejected(reasonCode, reason);
    }

    /// <summary>向在册运行原子追加身份绑定（五轮 重6：修订冲突 → 重读最新记录重试，有界 3 次——
    /// RunStore 修订守卫（RunRecordConflictException）下引擎并发推进会响亮冲突；追加方重读收敛，
    /// 绝不拿旧对象覆盖（不丢既有绑定、不让引擎状态倒退）。每次重试整体重读，绑定绝不重复追加）。
    /// stateGuard 校验重读后的目标状态仍满足语义（返回拒绝原因码+文案，null=通过）。返回 null=追加成功。</summary>
    /// <summary>向在册运行原子追加身份绑定（五轮 重6：修订冲突 → 重读最新记录重试，有界 3 次——
    /// RunStore 修订守卫（RunRecordConflictException）下引擎并发推进会响亮冲突；追加方重读收敛，
    /// 绝不拿旧对象覆盖（不丢既有绑定、不让引擎状态倒退）。每次重试整体重读，绑定绝不重复追加）。
    /// stateGuard 校验重读后的目标状态仍满足语义（返回拒绝原因码+文案，null=通过）。
    /// 六轮 重要1/4：静态化注入 load/update（夹具可直接驱动重试路径）；成功时 committed=提交时快照——
    /// 调用方回执必须用它（提交后引擎可能已推进，重读回报可能把已受理假报为异常/状态文案失真）。</summary>
    internal static (string Code, string Reason)? TryAppendHandoffBinding(
        Func<string, WorkflowRunRecord?> load, Action<WorkflowRunRecord> update,
        string runId, StartupHandoffRequest request, string note,
        Func<WorkflowRunRecord, (string Code, string Reason)?> stateGuard,
        CancellationToken ct, out WorkflowRunRecord? committed)
    {
        committed = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            ct.ThrowIfCancellationRequested(); // 七轮 建议1：每轮入口复核——取消与状态失效并发时取消优先（OCE 统一表现）
            var fresh = load(runId);
            if (fresh is null)
                return (HandoffReasonCodes.HandoffError, "目标运行记录已不存在，请刷新后重试");
            if (stateGuard(fresh) is { } bad)
                return bad;
            ct.ThrowIfCancellationRequested(); // 取消复核贴近身份修改（三轮 重要9 + 四轮 重要3）
            fresh.Handoffs.Add(BuildIdentity(request));
            fresh.Note = (fresh.Note is null ? "" : fresh.Note + " ") + note;
            try
            {
                update(fresh);
                committed = fresh; // 提交时快照：回执用它能保证「追加成功时状态确为所报」
                return null;
            }
            catch (RunRecordConflictException)
            {
                // 修订冲突=引擎/他方并发推进——重读最新记录重试（有界）；绝不拿旧对象覆盖
            }
            catch (Exception ex)
            {
                return (HandoffReasonCodes.HandoffError, "受理落盘失败：" + ex.Message);
            }
        }
        ct.ThrowIfCancellationRequested(); // 七轮 建议1：耗尽出口复核——重试间到达的取消以 OCE 表现而非 HandoffError
        return (HandoffReasonCodes.HandoffError, "受理落盘反复修订冲突（并发推进繁忙），请稍后重试");
    }

    /// <summary>状态通知隔离（八轮 重要2）：订阅者异常绝不允许改变受理/动作结论（受理已落盘不得反转为异常回执），
    /// 也不允许成为未观察任务异常（观察器 finally 内的通知同样隔离）。</summary>
    private void NotifyStateChanged()
    {
        try { StateChanged?.Invoke(this, EventArgs.Empty); }
        catch (Exception ex)
        {
            try { _log?.Invoke($"[任务中心] 状态通知订阅者异常（已隔离，不影响受理/动作结论）：{ex.Message}"); }
            catch { /* 九轮 重要2：隔离边界自身必须成立——诊断日志异常同样隔离，通知路径绝不向外抛 */ }
        }
    }

    /// <summary>驱动在册实况（八轮 建议1：夹具直证「驱动出册」=观察器 finally 移除登记，而非仅状态到位）。</summary>
    internal bool HasDrive(string workflowId)
    {
        lock (_gate) return _drives.ContainsKey(workflowId);
    }

    /// <summary>受理后回执状态尽力刷新（七轮 重要1）：读取失败/记录缺失返回 null——调用方回落受理提交时快照，
    /// 回执结论（已受理）绝不因刷新失败反转为异常拒绝。</summary>
    internal WorkflowRunState? TryReadRunState(string runId) // internal：夹具直接证明「读取失败不抛出」（兜底语义）
    {
        try { return _runs.Load(runId)?.State; }
        catch { return null; }
    }

    private static HandoffIdentity BuildIdentity(StartupHandoffRequest request) => new()
    {
        IntentKey = request.IntentKey,
        ExecutionId = request.ExecutionId ?? "",
        StepId = request.StepId ?? "",
        TriggerKind = request.TriggerKind,
        Mode = request.Mode,
    };

    private static string ShortExecutionId(StartupHandoffRequest request)
        => request.ExecutionId is { Length: > 8 } id ? id[..8] : request.ExecutionId ?? "?";

    /// <summary>
    /// 启动中心移交受理入口（R4.9 设计稿 §3 + ASTRA 二轮处置）：受理点=RunStore 落盘（start/arm=CreateRun 携带首条绑定；
    /// resume/arm 幂等挂载=绑定原子追加 Update），台账=Handoffs[].IntentKey（随记录永存，无淘汰）；
    /// 崩溃窗 Interrupted → 重放同键 AlreadyAccepted 不换键；受理后驱动异常由观察器收敛（在飞/未决事实→Unknown）。
    /// 顺序（一轮 B3 + 二轮 I2/I3 + 三轮 B1）：参数/mode 校验 → 关闭/能力预检 → 恢复屏障 → 台账查询 → 快照辅助判定（仅未命中新受理）→
    /// 最终临界区（关闭→能力复核→台账双检→就绪→取消检查→互斥→组装+权威预检→受理落盘→预留）→ 锁外驱动激活。
    /// 启动链 CTS 仅覆盖受理点之前；受理提交后任务归宿主（启动链取消不撤销已受理运行）。
    /// </summary>
    public async Task<HandoffRegisterResult> RegisterHandoffAsync(StartupHandoffRequest request, CancellationToken ct = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (!StartupHandoffModes.IsKnown(request.Mode))
            return HandoffRegisterResult.Rejected(HandoffReasonCodes.UnsupportedMode,
                $"未支持的移交语义「{request.Mode}」（支持 start/resume/armTrigger，不静默回落 start）");
        if (string.IsNullOrWhiteSpace(request.WorkflowId))
            return HandoffRegisterResult.Rejected(HandoffReasonCodes.ConfigMissing, "未配置目标流程（移交请求 WorkflowId 为空）");
        // ASTRA 二轮 I6：空 IntentKey/ExecutionId 是身份合同错误（非旧配置缺参），按终止链处置（HandoffError 非 ConfigMissing）
        if (string.IsNullOrWhiteSpace(request.IntentKey) || string.IsNullOrWhiteSpace(request.ExecutionId))
            return HandoffRegisterResult.Rejected(HandoffReasonCodes.HandoffError,
                "移交身份不完整（IntentKey/ExecutionId 为空）——调用方合同违反");

        // I2：关闭/能力预检先于恢复屏障（恢复扫描会写运行文件，监控端不得先产生副作用）
        lock (_gate)
        {
            if (_shutdown)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.Shutdown, "任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } preBlock)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.NoCapability, preBlock);
        }

        await EnsureRecoveredAsync().ConfigureAwait(false);
        ct.ThrowIfCancellationRequested(); // 启动链 CTS 仅覆盖受理点之前

        // 台账查询（读路径无副作用）：命中 → 内容核对（§2）；不完整 → 拒绝新受理（I5）
        if (LedgerGate(request) is { } receipt)
            return receipt;

        // 环境确保（2026-09-20 修复+会诊处置：台账之后、快照之前——受理事实优先（重放同键由 LedgerGate 先回执，不触发拉起）；
        // 锁外有界等待，取消随调用方 ct 与宿主退出联动。失败经台账复核回执：等待期间同键可能已被并发请求受理，
        // 不得把「已受理」误报为拒绝（会诊 阻断1）；arm 挂载同样确保——R4.9 合同维持挂载需就绪，仅把「离线即拒绝」升级为「先拉起再判定」）
        using var ensureCts = CancellationTokenSource.CreateLinkedTokenSource(ct, _shutdownCts.Token);
        if (await EnsureExecutionEnvironmentAsync(ensureCts.Token).ConfigureAwait(false) is { } envError)
            return RejectedWithLedgerRecheck(request, HandoffReasonCodes.NotReady, envError);

        // 快照辅助判定（§6.1；三轮 B1：必须在台账之后——受理事实优先，命中（含 Unknown）已由 LedgerGate 回执 AlreadyAccepted；
        // 仅未命中新受理才查快照；拒绝返回前经台账复核，与最终临界区双检同构；测试接缝 provider=null 时跳过）
        // 会诊 重要1：刚拉起/刚就绪时快照可能尚未到达（旧序会立刻 StatusUncertain 挡死冷启动）——给有界窗口再判定；
        // 仍不可考按原语义 StatusUncertain 拒绝，不为拉起 BGI 放宽「状态不可考则拒绝」。arm 模式快照不参与判定，不等待。
        if (_statusSnapshotProvider is not null) // provider=null（测试接缝）保持原语义：整个快照预检跳过
        {
            var snapshot = request.Mode == StartupHandoffModes.ArmTrigger
                ? _statusSnapshotProvider()
                : await AwaitSnapshotIfMissingAsync(ensureCts.Token).ConfigureAwait(false);
            if (SnapshotPrecheck(snapshot, request.Mode) is { } snapRejected)
                return RejectedWithLedgerRecheck(request, snapRejected.ReasonCode!, snapRejected.Reason!);
        }

        return request.Mode == StartupHandoffModes.Resume
            ? RegisterResumeHandoff(request, ct)
            : RegisterStartHandoff(request, ct);
    }

    /// <summary>start / armTrigger 语义受理（§4）：arm 仅 Waiting+有待触发时刻才算已挂载（身份追加登记后 AlreadyAccepted 幂等）；其余活动态响亮拒绝。</summary>
    private HandoffRegisterResult RegisterStartHandoff(StartupHandoffRequest request, CancellationToken ct)
    {
        // 锁外预检（只读无副作用；拒绝返回前经台账复核，I3）
        WorkflowSnapshot snapshot;
        try
        {
            snapshot = _workflows.LoadSnapshot(request.WorkflowId); // 隔离/缺失响亮抛出
        }
        catch (Exception ex)
        {
            return RejectedWithLedgerRecheck(request, HandoffReasonCodes.FlowUnavailable, ex.Message);
        }
        // 候选只读（与 SaveFlow/StartWorkflowAsync 同口径：宿主层独立护栏）
        if (string.Equals(snapshot.Document.Activation?.Status, "candidate-ready", StringComparison.Ordinal))
            return RejectedWithLedgerRecheck(request, HandoffReasonCodes.FlowUnavailable,
                "candidate-ready 候选流程为只读预览，禁止移交启动（激活归 R5 专用入口）");

        var arm = request.Mode == StartupHandoffModes.ArmTrigger;
        if (arm && !HasMountableTrigger(snapshot.Document))
            return RejectedWithLedgerRecheck(request, HandoffReasonCodes.NoTrigger,
                "流程无可挂载触发器（须带 trigger.time 触发器；结构性循环首轮立即执行，不算可挂载——四轮 重要5）");

        var shortId = ShortExecutionId(request);
        WorkflowRunner runner;
        WorkflowRunRecord run;
        lock (_gate)
        {
            // 最终临界区（I3 顺序）：关闭 → 能力复核 → 台账双检 → 就绪 → 取消检查 → 互斥 → 组装+权威预检 → 受理落盘 → 预留
            if (_shutdown)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.Shutdown, "任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } capBlock)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.NoCapability, capBlock);
            if (LedgerGate(request) is { } raced)
                return raced;
            var readiness = ExecutionReadiness();
            if (!readiness.Ready)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.NotReady, readiness.Reason!);
            ct.ThrowIfCancellationRequested(); // 取消检查贴近提交点（I3）

            var sameFlow = _runs.List().Where(r => r.WorkflowId == request.WorkflowId).ToList();
            if (sameFlow.Any(r => r.State == WorkflowRunState.Unknown))
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.Unknown,
                    "该流程存在结果不确定（Unknown）的运行，需先对账");
            if (arm)
            {
                var waiting = sameFlow
                    .Where(r => r.State == WorkflowRunState.Waiting && r.Wait?.NextTriggerAt is not null)
                    .OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
                if (waiting is not null)
                {
                    // B2：幂等挂载成立前，先把本次身份原子追加到在等运行（落盘失败不得报成功；旧绑定保留；
                    // 五轮 重6：修订冲突重读收敛）
                    if (TryAppendHandoffBinding(id => _runs.Load(id), r => _runs.Update(r), waiting.RunId, request,
                            $"启动中心移交受理（armTrigger 幂等挂载，执行 {shortId}）。",
                            r => r.State == WorkflowRunState.Waiting && r.Wait?.NextTriggerAt is not null
                                ? null
                                : (HandoffReasonCodes.AlreadyRunning, "挂载目标运行状态已变化，请刷新后重试"),
                            ct, out var committedMount) is { } mountError)
                        return HandoffRegisterResult.Rejected(mountError.Code, mountError.Reason);
                    // 六轮 重要1：回执用提交时快照（提交后引擎可能已推进/清除 Wait——不重读，不把已受理假报为异常）
                    return HandoffRegisterResult.AlreadyAccepted(committedMount!.RunId,
                        $"流程触发器已在挂载中（运行 {committedMount.RunId} 等待 {committedMount.Wait!.NextTriggerAt:MM-dd HH:mm} 触发），本次身份已登记为幂等挂载",
                        committedMount.State);
                }
                if (sameFlow.Any(r => ActiveStates.Contains(r.State)))
                    return HandoffRegisterResult.Rejected(HandoffReasonCodes.AlreadyRunning,
                        "该流程已有非挂载中的活动运行（不假报已挂载）");
            }
            else if (sameFlow.Any(r => ActiveStates.Contains(r.State)))
            {
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.AlreadyRunning,
                    "该流程已有活动运行（同流程同时只允许一个运行）");
            }
            if (_reservedWorkflows.Contains(request.WorkflowId) || _drives.ContainsKey(request.WorkflowId))
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.AlreadyRunning, "该流程运行正在启动/驱动中");

            // B3：受理前权威预检（组装/计划预检失败=Rejected——不消耗 IntentKey、不留运行记录、不标 Failed）
            try
            {
                runner = CreateRunner(_clientAccessor());
                runner.PreflightStartable(snapshot);
            }
            catch (Exception ex)
            {
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.FlowUnavailable, ex.Message);
            }

            ct.ThrowIfCancellationRequested(); // 三轮 重要9：组装+权威预检之后、受理落盘之前再查取消

            try
            {
                // 受理提交点（锁内）：移交身份随创建原子落盘，runId 直接来自记录（绝不倒推查询）
                run = _runs.CreateRun(request.WorkflowId, snapshot.Revision,
                    note: $"启动中心移交受理（{request.Mode}，执行 {shortId}）",
                    handoff: BuildIdentity(request));
            }
            catch (Exception ex)
            {
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.HandoffError, "受理落盘失败：" + ex.Message);
            }
            _reservedWorkflows.Add(request.WorkflowId);
        }

        // 驱动激活（锁外；B4 关闭竞态由 LaunchDrive 册外观察兜底）。受理已提交不撤回；回执状态尽力刷新（I1）
        var launch = LaunchDrive(request.WorkflowId, runner,
            cts => runner.StartExistingRunAsync(run.RunId, cts.Token, armTriggerLaunch: arm), "", knownRunId: run.RunId);
        // 七轮 重要1：回执状态读取失败/记录缺失回落受理提交时快照（Planned）——绝不把已受理假报为异常拒绝
        var confirmed = TryReadRunState(run.RunId) ?? run.State;
        if (launch.Status == HostActionStatus.Unavailable)
            return HandoffRegisterResult.Accepted(run.RunId,
                $"已受理（受理≠执行成功）；{launch.Message}（运行按退出语义留待恢复扫描/显式处置）", confirmed);
        // 八轮 重要1：回执不假报「已挂载」——驱动刚起步（异步推进），挂载结果以运行状态为准。
        // 九轮 重要1：Interrupted 是「无未决外部事实的驱动异常」通用收敛态（前提失效只是其中一种）——
        // 回执按状态如实描述，具体原因一律指向运行备注，不用状态反推原因
        var armReason = confirmed == WorkflowRunState.Interrupted
            ? "已受理挂载请求；运行已中断（原因见运行备注），受理事实保留"
            : "已受理挂载请求（挂载等待不占 BGI 执行槽，到点由引擎推进；受理≠执行成功，挂载结果见运行状态）";
        return HandoffRegisterResult.Accepted(run.RunId,
            arm ? armReason : "已移交受理，任务中心接管（受理≠执行成功）",
            confirmed);
    }

    /// <summary>resume 语义受理（§4）：绑定最新（UpdatedAt 最大）Interrupted/Paused 运行；受理点=身份绑定原子追加落盘；重放同键 AlreadyAccepted 不重选。</summary>
    private HandoffRegisterResult RegisterResumeHandoff(StartupHandoffRequest request, CancellationToken ct)
    {
        var shortId = ShortExecutionId(request);
        WorkflowRunner runner;
        string boundRunId;
        WorkflowRunRecord? committedBind = null; // 七轮 重要1：受理提交时快照（锁外回执兜底用）
        lock (_gate)
        {
            // 最终临界区（I3）：关闭 → 能力复核 → 台账双检 → 就绪 → 取消检查 → 互斥/目标 → 流程与权威预检 → 身份追加 → 预留
            if (_shutdown)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.Shutdown, "任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } capBlock)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.NoCapability, capBlock);
            if (LedgerGate(request) is { } raced)
                return raced;
            var readiness = ExecutionReadiness();
            if (!readiness.Ready)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.NotReady, readiness.Reason!);
            ct.ThrowIfCancellationRequested();

            var sameFlow = _runs.List().Where(r => r.WorkflowId == request.WorkflowId).ToList();
            if (sameFlow.Any(r => r.State == WorkflowRunState.Unknown))
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.Unknown,
                    "该流程存在结果不确定（Unknown）的运行，需先对账再恢复");
            var target = sameFlow
                .Where(r => r.State is WorkflowRunState.Interrupted or WorkflowRunState.Paused)
                .OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
            if (target is null)
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.NoResumableRun,
                    "该流程无可恢复运行（需 Interrupted/Paused 记录；启动新计划请用 start 语义）");
            if (sameFlow.Any(r => ActiveStates.Contains(r.State) && r.RunId != target.RunId))
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.AlreadyRunning,
                    "该流程已有其他活动运行（同流程同时只允许一个运行）");
            if (_reservedWorkflows.Contains(request.WorkflowId) || _drives.ContainsKey(request.WorkflowId))
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.AlreadyRunning, "该流程运行正在启动/驱动中");

            // B3：resume 同样先做流程可用性/候选/权威预检——拒绝时原记录及其身份保持不变
            WorkflowSnapshot snapshot;
            try
            {
                snapshot = _workflows.LoadSnapshot(target.WorkflowId);
            }
            catch (Exception ex)
            {
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.FlowUnavailable, ex.Message);
            }
            if (string.Equals(snapshot.Document.Activation?.Status, "candidate-ready", StringComparison.Ordinal))
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.FlowUnavailable,
                    "candidate-ready 候选流程为只读预览，禁止移交恢复（激活归 R5 专用入口）");
            try
            {
                runner = CreateRunner(_clientAccessor());
                runner.PreflightStartable(snapshot);
            }
            catch (Exception ex)
            {
                return HandoffRegisterResult.Rejected(HandoffReasonCodes.FlowUnavailable, ex.Message);
            }

            // 受理点（锁内）：身份绑定原子追加（B1：绝不替换旧绑定，旧意图去重依据永存），落盘失败不留预留；
            // 五轮 重6：修订冲突重读收敛（TryAppendHandoffBinding 内置取消复核贴近身份修改）
            if (TryAppendHandoffBinding(id => _runs.Load(id), r => _runs.Update(r), target.RunId, request,
                    $"启动中心移交受理（resume 绑定，执行 {shortId}）。",
                    r => r.State is WorkflowRunState.Interrupted or WorkflowRunState.Paused
                        ? null
                        : (HandoffReasonCodes.NoResumableRun, "目标运行状态已变化，请刷新后重试"),
                    ct, out committedBind) is { } bindError)
                return HandoffRegisterResult.Rejected(bindError.Code, bindError.Reason);
            _reservedWorkflows.Add(request.WorkflowId);
            boundRunId = committedBind!.RunId;
        }

        // 驱动激活（锁外，复用 Resume 驱动路径；B4 关闭竞态由册外观察兜底；受理不撤回——运行保持可恢复状态）
        var launch = LaunchDrive(request.WorkflowId, runner,
            cts => runner.ResumeAsync(boundRunId, cts.Token), "", knownRunId: boundRunId);
        // 七轮 重要1：回执状态尽力刷新——读取失败/记录缺失回落受理提交时快照，绝不把已受理假报为异常拒绝
        var confirmed = TryReadRunState(boundRunId) ?? committedBind!.State;
        if (launch.Status == HostActionStatus.Unavailable)
            return HandoffRegisterResult.Accepted(boundRunId,
                $"已受理恢复绑定（受理≠执行成功）；{launch.Message}（运行保持可恢复状态）", confirmed);
        return HandoffRegisterResult.Accepted(boundRunId, "已移交受理：恢复既有运行（游标身份重定位；受理≠执行成功）", confirmed);
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
        _shutdownCts.Cancel(); // 锁外取消（取消回调不持卡）：在途环境确保/快照等待立即退出
        if (drives.Count == 0) return;
        var all = Task.WhenAll(drives.Select(d => d.Task));
        await Task.WhenAny(all, Task.Delay(ShutdownConvergeBudget)).ConfigureAwait(false);
        if (!all.IsCompleted)
            _log?.Invoke($"[任务中心] 宿主关闭：{drives.Count} 个运行未在 {ShutdownConvergeBudget.TotalSeconds:0}s 内收敛（在飞事实保留，下次启动恢复扫描标记）");
    }

    // ================= 内部 =================

    /// <summary>执行入口环境确保（2026-09-20；锁外调用，有界等待在委托内）：
    /// 未就绪且配置了确保委托 → 委托自动启动 BGI 并等待 ext 通道就绪；随后仍由原临界区就绪检查权威复核
    /// （确保期间状态可能再变化，最终判定不旁路锁内检查）。返回非 null = 响亮拒绝原因（未产生任何受理副作用）。</summary>
    private async Task<string?> EnsureExecutionEnvironmentAsync(CancellationToken ct)
    {
        if (_ensureExecutionReady is null || ExecutionReadiness().Ready) return null;
        _log?.Invoke("[任务中心] 执行环境未就绪（BGI 离线或 ext 通道未就绪），尝试自动启动 BGI 并等待通道就绪…");
        try
        {
            // 取消纪律（会诊 阻断2）：OCE 不吞——移交路径随启动链/宿主退出取消传播，面板路径由调用点映射为响亮取消
            return await _ensureExecutionReady(ct).ConfigureAwait(false);
        }
        catch (Exception ex) when (ex is not OperationCanceledException)
        {
            return "自动启动 BGI 过程异常（未发送任何任务）：" + ex.Message;
        }
    }

    /// <summary>快照短时兜底（会诊 重要1：冷启动刚就绪时快照可能尚未到达，给有界窗口而非立刻 StatusUncertain；
    /// 仍空按原语义拒绝——不为拉起 BGI 放宽「状态不可考则拒绝」）。provider=null（测试接缝）直接返回 null。</summary>
    private async Task<ControlStatus?> AwaitSnapshotIfMissingAsync(CancellationToken ct)
    {
        if (_statusSnapshotProvider is null) return null;
        var snapshot = _statusSnapshotProvider();
        if (snapshot is not null) return snapshot;
        var deadline = DateTime.UtcNow.Add(_snapshotWaitBudget);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            // 会诊三轮 建议：轮询粒度不超出剩余预算（注入短预算的测试接缝不越界等待）
            var slice = deadline - DateTime.UtcNow;
            if (slice <= TimeSpan.Zero) break;
            await Task.Delay(slice < TimeSpan.FromMilliseconds(500) ? slice : TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            snapshot = _statusSnapshotProvider();
            if (snapshot is not null) return snapshot;
        }
        return null;
    }

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
        Func<CancellationTokenSource, Task<WorkflowRunRecord>> start, string registeredMessage, string? knownRunId = null)
    {
        var cts = new CancellationTokenSource();
        Task<WorkflowRunRecord> task;
        try
        {
            // 注意（ASTRA 二轮 I1）：异步入口的异常进入返回的 Task（不会在首个 await 前同步抛出）——
            // 此 catch 仅兜底真正的同步抛出；任务失败统一由观察器收敛（ObserveDriveAsync/ObserveOrphanAsync）
            task = start(cts);
        }
        catch (Exception ex)
        {
            lock (_gate) _reservedWorkflows.Remove(workflowId);
            cts.Dispose();
            return HostActionResult.Unavailable(ex.Message);
        }
        var entry = new DriveEntry { WorkflowId = workflowId, RunId = knownRunId, Runner = runner, Cts = cts, Task = task };
        lock (_gate)
        {
            if (_shutdown)
            {
                // B4：任务已启动就必须被观察——取消令牌不证明远端已停；转册外观察收敛（在飞事实→Unknown，否则→Interrupted）
                _reservedWorkflows.Remove(workflowId);
                cts.Cancel();
                _ = ObserveOrphanAsync(entry);
                return HostActionResult.Unavailable("任务中心宿主已关闭（驱动已启动，转关闭竞态册外观察收敛）");
            }
            _drives[workflowId] = entry;
        }
        _ = ObserveDriveAsync(entry);
        NotifyStateChanged();
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
            // 一轮 B5 + 二轮 I1/I4：驱动异常收敛——未决外部事实→Unknown（结果不可考），否则→Interrupted（等价崩溃语义，可显式恢复）
            ConvergeDriveException(entry, ex);
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
            NotifyStateChanged();
        }
    }

    /// <summary>
    /// 驱动异常收敛（ASTRA 二轮 I1/I4）：优先按准确 RunId 收敛（移交路径），面板 Start 路径倒查兜底；
    /// 未决外部事实判定（RunStore.HasUnresolvedExternalFact：主体提交/收尾在飞；前置在飞按 R4.6 合同走 Interrupted+恢复时对账）→Unknown，否则→Interrupted；
    /// 已是 Unknown/Interrupted/终态的不改写（幂等，不遮更保守的既有结论）。
    /// 六轮 重要2：修订冲突（并发追加绑定/引擎推进）→ 重读重判有界重试——不再吞冲突留下「Waiting 无驱动、恢复屏障已过」的假挂载窗口；
    /// 重试耗尽/其他失败 → 响亮留痕（记录保持非终态，下次启动恢复扫描兜底标记）。
    /// </summary>
    private void ConvergeDriveException(DriveEntry entry, Exception ex)
    {
        for (var attempt = 0; attempt < 3; attempt++)
        {
            try
            {
                var run = entry.RunId is not null
                    ? _runs.Load(entry.RunId)
                    : _runs.List()
                        .Where(r => r.WorkflowId == entry.WorkflowId && ActiveStates.Contains(r.State))
                        .OrderByDescending(r => r.CreatedAt).FirstOrDefault();
                if (run is null || run.IsTerminal || run.State is WorkflowRunState.Unknown or WorkflowRunState.Interrupted)
                    return;
                run.State = RunStore.HasUnresolvedExternalFact(run) ? WorkflowRunState.Unknown : WorkflowRunState.Interrupted;
                run.Note = (run.Note is null ? "" : run.Note + " ")
                    + $"驱动异常（{ex.GetType().Name}），按{(run.State == WorkflowRunState.Unknown ? "未决外部事实标 Unknown" : "Interrupted")}收敛。";
                _runs.Update(run);
                return;
            }
            catch (RunRecordConflictException)
            {
                // 并发推进/追加绑定——重读重判重试（有界），绝不拿旧对象覆盖
            }
            catch (Exception cex)
            {
                _log?.Invoke($"[任务中心] 驱动异常收敛失败（{cex.GetType().Name}）：{cex.Message}——记录保持非终态，下次启动恢复扫描兜底");
                return;
            }
        }
        _log?.Invoke($"[任务中心] 驱动异常收敛反复修订冲突（{entry.RunId ?? entry.WorkflowId}）——记录保持非终态，已留痕，下次启动恢复扫描兜底");
    }

    /// <summary>册外观察（ASTRA 二轮 B4：关闭竞态下已启动但未登记的驱动同样观察至收敛——绝不无人认领；不触发 StateChanged，宿主正在退出）。</summary>
    private async Task ObserveOrphanAsync(DriveEntry entry)
    {
        try
        {
            var run = await entry.Task.ConfigureAwait(false);
            _log?.Invoke($"[任务中心] 运行 {run.RunId} 终态：{run.State}（关闭竞态册外驱动）");
        }
        catch (Exception ex)
        {
            ConvergeDriveException(entry, ex);
            _log?.Invoke($"[任务中心] 册外驱动异常收敛（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            entry.Cts.Dispose();
        }
    }

}