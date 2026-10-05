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
public sealed partial class TaskCenterHost
{
    /// <summary>退出收敛上限（超时=进程退出语义，下次启动恢复扫描标 Interrupted/Unknown）。</summary>
    private static readonly TimeSpan ShutdownConvergeBudget = TimeSpan.FromSeconds(10);

    private static readonly WorkflowRunState[] ActiveStates =
    [
        WorkflowRunState.Planned, WorkflowRunState.Running, WorkflowRunState.Waiting,
        WorkflowRunState.Completing, WorkflowRunState.Paused,
        WorkflowRunState.LocalWaitParking, // [批次 20／Wave3／C11=(a)] 停驻运行＝活跃需关注（等待项就绪后重驱；历史清单自动排除）
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
    private readonly HashSet<DriveEntry> _driveCompletions = [];
    private readonly HashSet<string> _reservedWorkflows = new(StringComparer.Ordinal); // 预留（CreateRun 窗口覆盖）
    private Task? _recoverTask; // 恢复屏障任务（R4.8 二轮 阻断5：并发 Start/Resume 共同 await 同一扫描，失败重置允许重试）
    private bool _shutdown;
    private Task? _shutdownTask;
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
        public TaskCompletionSource Completion { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    /// <summary>生产构造：localExecutionCapability 必传（ASTRA 二轮 I2——监控端拒绝执行入口的守卫不得遗漏接线）。</summary>
    public TaskCenterHost(string flowsDir, string runsDir, string catalogCacheFile,
        Func<BgiExternalClient?> clientAccessor, Func<bool> localExecutionCapability,
        Func<ControlStatus?> statusSnapshotProvider, Action<string>? log = null,
        Func<CancellationToken, Task<string?>>? ensureExecutionReady = null)
        : this(flowsDir, runsDir, catalogCacheFile, clientAccessor, log, null, null, localExecutionCapability, statusSnapshotProvider, ensureExecutionReady, admissionWired: true, successorAdmissionWired: true)
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
        TimeSpan? snapshotWaitBudget = null,
        bool admissionWired = false, string? arbitrationDir = null, TaskCenterAdmissionSeams? admissionSeams = null,
        bool successorAdmissionWired = false)
    {
        _snapshotWaitBudget = snapshotWaitBudget ?? TimeSpan.FromSeconds(15);
        _workflows = new WorkflowStore(flowsDir);
        _runs = new RunStore(runsDir, requireOwnership: admissionWired);
        LocalWaitQueue = new LocalWaitQueueStore(runsDir);
        _catalog = new ResourceCatalogService(
            () => clientAccessor() is { } c ? new BgiExternalCatalogTransport(c) : null, catalogCacheFile);
        _clientAccessor = clientAccessor ?? throw new ArgumentNullException(nameof(clientAccessor));
        _log = log;
        _runnerFactory = runnerFactory;
        _readinessOverride = readinessOverride;
        _localExecutionCapability = localExecutionCapability;
        _statusSnapshotProvider = statusSnapshotProvider;
        _ensureExecutionReady = ensureExecutionReady;
        _admissionWired = admissionWired;
        _successorAdmissionWired = successorAdmissionWired;
        _arbitrationDir = arbitrationDir;
        _admissionSeams = admissionSeams;
        _runsDirPath = runsDir;
        WaitDecisionSource = new WaitDecisionSource(DecideLocalWait);
    }

    /// <summary>运行状态变更通知（终态/动作后触发；UI 以 2s 轮询为主、本事件为辅）。</summary>
    public event EventHandler? StateChanged;

    public WorkflowStore Workflows => _workflows;
    public RunStore Runs => _runs;
    public ResourceCatalogService Catalog => _catalog;
    public LocalWaitQueueStore LocalWaitQueue { get; }
    public WaitDecisionSource WaitDecisionSource { get; }

    /// <summary>启动屏障（一轮 B5）：任何 Start/Resume 前完成一次恢复扫描（Interrupted/Unknown 标记+留痕，绝不自动恢复）。幂等。</summary>
    public void EnsureRecovered()
    {
        lock (_gate)
        {
            if (_shutdown) throw new InvalidOperationException("任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } blocked) throw new InvalidOperationException(blocked);
        }
        EnsureRecoveredAsync().GetAwaiter().GetResult();
    }

    /// <summary>恢复屏障任务（二轮 阻断5）：所有 Start/Resume 共同 await 同一扫描任务——扫描完成前任何入口不得越过；
    /// 扫描失败重置为 null，下次调用重新扫描（不留永久假屏障）。</summary>
    private async Task EnsureRecoveredAsync()
    {
        // 每个合法首调独立观察租约；只共享取得资格之后的运行恢复扫描。
        if (_admissionWired)
        {
            await Task.Yield(); // 独立首调的同步观察探针不得串行阻塞下一调用进入。
            await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
        }
        Task recovery;
        lock (_gate)
        {
            _recoverTask ??= RecoverScanAsync();
            recovery = _recoverTask;
        }
        await recovery.ConfigureAwait(false);
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
    internal IResourceCatalogTransport? ResourceEditorTransportForTest { get; set; }

    private IResourceCatalogTransport GetResourceEditorTransport()
    {
        if (_localExecutionCapability is not { } capability || !capability())
            throw new InvalidOperationException("资源编辑仅在执行端可用");
        if (ResourceEditorTransportForTest is { } test) return test;
        var client = _clientAccessor() ?? throw new InvalidOperationException("执行端BGI未连接");
        return new BgiExternalCatalogTransport(client);
    }

    public Task OpenResourceEditorAsync(WorkflowNode node)
        => WorkflowResourceEditor.OpenAsync(node, GetResourceEditorTransport());

    public Task<string> ReadResourceRevisionAsync(WorkflowNode node)
        => WorkflowResourceEditor.ReadRevisionAsync(node, GetResourceEditorTransport());

    public async Task<HostActionResult> ActivateMigrationCandidateAsync(string workflowId)
    {
        try
        {
            return await Task.Run(() =>
            {
                lock (_gate)
                {
                    if (_shutdown || CapabilityBlockReason() is not null)
                        return HostActionResult.Unavailable("宿主已关闭或当前无本地执行能力");
                    if (_drives.ContainsKey(workflowId) || _reservedWorkflows.Contains(workflowId) ||
                        ListActiveRuns().Any(r => r.WorkflowId == workflowId))
                        return HostActionResult.Unavailable("该流程仍有运行或未决责任，不能切换迁移状态");
                    var transport = GetResourceEditorTransport();
                    var source = _workflows.LoadSnapshot(workflowId);
                    if (source.Document.Activation?.Status != "candidate-ready")
                        return HostActionResult.Unavailable("只有candidate-ready候选可以正式激活");
                    var standard = StandardMigrationConsumer.Find(LegacyCandidateRoot(), source, Directory.Exists(_workflows.MigrationRootFor(workflowId)));
                    if (standard != null)
                    {
                        if (_drives.Count != 0 || _reservedWorkflows.Count != 0 || ListActiveRuns().Any())
                            return HostActionResult.Unavailable("标准迁移安装要求先停止并结清本机所有流程，未修改配置");
                        StandardMigrationConsumer.InstallAsync(standard, transport).GetAwaiter().GetResult();
                    }
                    var prepared = WorkflowMigrationConsumer.PrepareAsync(_workflows, workflowId, transport).GetAwaiter().GetResult();
                    var id = WorkflowMigrationConsumer.Activate(_workflows, workflowId, prepared);
                    return HostActionResult.Effective("迁移候选已正式激活并提交；回退事务：" + id);
                }
            });
        }
        catch (Exception ex) { return HostActionResult.Unavailable(ex.Message); }
    }

    public async Task<HostActionResult> PrepareLegacyMigrationAsync(string sourceUserRoot)
    {
        try
        {
            if (_localExecutionCapability is not { } capability || !capability())
                return HostActionResult.Unavailable("迁移准备仅在执行端可用");
            return await Task.Run(() =>
            {
                lock (_gate)
                {
                    if (_shutdown || CapabilityBlockReason() is not null)
                        return HostActionResult.Unavailable("宿主已关闭或当前无本地执行能力");
                    var dataRoot = _admissionRoot ?? Directory.GetParent(_runsDirPath!)!.FullName;
                    var report = LegacyMigrationCandidateService.Prepare(sourceUserRoot,
                        Path.Combine(dataRoot,"legacy-migration-candidates"),_workflows);
                    return HostActionResult.Effective((report.Reused ? "已复用" : "已生成") +
                        $"正常旧数据迁移候选（{report.WorkflowIds.Count}个流程）。标准配置及报告：{report.CandidateDirectory}；原件保持，尚未安装标准配置或激活。");
                }
            });
        }
        catch (Exception ex) { return HostActionResult.Unavailable(ex.Message); }
    }

    private string LegacyCandidateRoot()
        => Path.Combine(_admissionRoot ?? Directory.GetParent(_runsDirPath!)!.FullName, "legacy-migration-candidates");

    public async Task<HostActionResult> RollbackMigrationAsync(string workflowId)
    {
        try
        {
            return await Task.Run(() =>
            {
                lock (_gate)
                {
                    if (_shutdown || CapabilityBlockReason() is not null)
                        return HostActionResult.Unavailable("宿主已关闭或当前无本地执行能力");
                    if (_drives.ContainsKey(workflowId) || _reservedWorkflows.Contains(workflowId) ||
                        ListActiveRuns().Any(r => r.WorkflowId == workflowId))
                        return HostActionResult.Unavailable("请先停止并结清该流程的运行责任，再执行迁移回退");
                    var recovery = _workflows.LoadMigrationRecovery(workflowId);
                    var standard = StandardMigrationConsumer.FindRecovery(LegacyCandidateRoot(), recovery, Directory.Exists(_workflows.MigrationRootFor(workflowId)));
                    if (standard != null && (_drives.Count != 0 || _reservedWorkflows.Count != 0 || ListActiveRuns().Any()))
                        return HostActionResult.Unavailable("标准配置回退要求先停止并结清本机所有流程");
                    if (standard != null)
                        StandardMigrationConsumer.ValidateRollbackPeerAsync(standard, GetResourceEditorTransport()).GetAwaiter().GetResult();
                    var transactionRoot = _workflows.MigrationRootFor(workflowId);
                    if (standard == null || Directory.Exists(transactionRoot))
                        WorkflowMigrationConsumer.Rollback(_workflows, workflowId);
                    else if (recovery.ActivationStatus != "candidate-ready")
                        return HostActionResult.Unavailable("流程事务缺失，不能猜测回退");
                    if (standard != null)
                        StandardMigrationConsumer.RollbackAsync(standard, GetResourceEditorTransport()).GetAwaiter().GetResult();
                    return HostActionResult.Effective("迁移已回退，原候选与数据已恢复");
                }
            });
        }
        catch (Exception ex) { return HostActionResult.Unavailable(ex.Message); }
    }

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
                        || r.State is WorkflowRunState.Unknown or WorkflowRunState.Interrupted) // [批次 20／Wave3／C11] 停驻运行经 ActiveStates 含于活动清单（冗余析取移除——R46 建议-2）
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
    public async Task<WorkflowStopAuthorityRecord?> RefreshStartupStopAuthorityAsync(
        StartupSourceIntent source, bool required, CancellationToken ct)
    {
        lock (_gate)
        {
            if (_shutdown) throw new InvalidOperationException("宿主已关闭，不能挂载来源");
            if (CapabilityBlockReason() is { } blocked) throw new InvalidOperationException(blocked);
        }
        if (source.Authority is { } original && _runs.IsStartupSourceRevoked(original))
            throw new InvalidOperationException("原来源已经耐久停止，自动回调不得以新 runId 恢复旧意图。");
        var client = _clientAccessor();
        if (client?.State != BgiExternalLinkState.Ready)
        {
            if (!required) return null;
            if (source.Authority is not null) throw new InvalidOperationException("已冻结来源目标离线，禁止启动新目标洗白原基线");
            if (await EnsureExecutionEnvironmentAsync(ct).ConfigureAwait(false) is { } error)
                throw new InvalidOperationException(error);
            client = _clientAccessor();
        }
        var runner = CreateRunner(client);
        if (source.Authority is { } inherited)
        {
            await runner.ValidateInheritedStopAuthorityAsync(inherited, ct).ConfigureAwait(false);
            return inherited;
        }
        return await runner.AcquireExplicitIntentStopAuthorityAsync(source.IntentId, source.IntentTimestamp, ct).ConfigureAwait(false);
    }

    public async Task<HostActionResult> StartWorkflowAsync(string workflowId)
    {
        var explicitIntentTimestamp = System.Diagnostics.Stopwatch.GetTimestamp();
        var explicitIntentId = Guid.NewGuid().ToString("N");
        // R4.9 I2：关闭/能力预检先于恢复屏障（恢复扫描会写运行文件，监控端不得先产生副作用）
        lock (_gate)
        {
            if (_shutdown) return HostActionResult.Unavailable("任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } capPre) return HostActionResult.Unavailable(capPre);
        }
        if (!_admissionWired) await EnsureRecoveredAsync().ConfigureAwait(false);
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

        // R5.2 B2（E1）：接线后面板启动一律经统一仲裁面（无双跑：BGI 执行锁物理互斥+门面逻辑准入互斥）；
        // 未接线=旧路径（既有测试接缝默认——R4 行为合同不变）。
        if (_admissionWired)
            return await SubmitFlowStartViaAdmissionAsync(workflowId, snapshot, explicitIntentId, explicitIntentTimestamp).ConfigureAwait(false);

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
            cts => runner.StartAsync(workflowId, cts.Token, explicitIntentTimestamp, explicitIntentId), $"已受理启动（流程「{snapshot.Document.Name}」）");
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
        var run = _runs.Load(runId);
        if (run is null) return HostActionResult.Unavailable("运行记录不存在：" + runId);
        if (run.NonExecutingDiagnostic)
            return HostActionResult.Unavailable("非执行终态诊断不可恢复");
        if (run.State == WorkflowRunState.Unknown)
            return HostActionResult.Unavailable("运行结果不确定（Unknown），需先按幂等键+job 查询对账，禁止自动恢复");
        if (_admissionWired)
        {
            if (CurrentArbitrationFacts().F11Active)
                return HostActionResult.Unavailable("F11 独立停止闸门激活（未发生租约副作用）");
            if (TryGetAdmissionScopeForResume(run.RunId, run.WorkflowId) is null)
                return HostActionResult.Unavailable("恢复缺少已登记固定来源（无面板流程登记且无移交受理登记 Scope；未产生任何租约副作用）");
        }
        await EnsureRecoveredAsync().ConfigureAwait(false);
        run = _runs.Load(runId);
        if (run is null) return HostActionResult.Unavailable("运行记录不存在：" + runId);
        if (run.State == WorkflowRunState.Unknown)
            return HostActionResult.Unavailable("运行结果不确定（Unknown），需先按幂等键+job 查询对账，禁止自动恢复");
        if (run.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused
            or WorkflowRunState.LocalWaitParking)) // [批次 20／Wave3／C11=(a)] 停驻运行可宿主重驱
            return HostActionResult.Unavailable($"仅 Interrupted/Paused/LocalWaitParking 可恢复（当前 {run.State}）"
                + "——[批次 20／Wave3／C11=(a)] 停驻运行经宿主恢复即重驱");

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

        // R5.2 B2-β（E2）：接线后恢复一律经恢复专用准入边界（§5.1：不排序不产候选、意图持久化后发送、保留原票据责任）；
        // 未接线=旧路径（既有测试接缝默认——R4 行为合同不变）。
        if (_admissionWired)
            return await SubmitResumeViaAdmissionAsync(run, "ui:panel:resume").ConfigureAwait(false);

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
        => RequestRunActionAsync(runId, action).GetAwaiter().GetResult();

    /// <summary>面板异步动作入口：等待原身份对账/终局写回时让出调用线程，结果仍按原耐久事实判定。</summary>
    public async Task<HostActionResult> RequestRunActionAsync(string runId, WorkflowRunAction action)
    {
        if (string.IsNullOrWhiteSpace(runId))
            return HostActionResult.Unavailable("运行 runId 为空，未执行动作");

        WorkflowRunRecord? run;
        try { run = _runs.Load(runId); }
        catch (Exception ex)
        {
            return HostActionResult.Unavailable("运行记录读取失败，未执行动作："
                + ex.GetType().Name + "（" + ex.Message + "）");
        }
        if (run is null) return HostActionResult.Unavailable("运行记录不存在：" + runId);
        if (string.IsNullOrWhiteSpace(runId) || string.IsNullOrWhiteSpace(run.RunId)
            || !string.Equals(run.RunId, runId, StringComparison.Ordinal))
            return HostActionResult.Unavailable("运行文件名与记录内 runId 不一致，拒绝执行动作");

        lock (_gate)
        {
            if (_shutdown) return HostActionResult.Unavailable("任务中心宿主已关闭");
            if (CapabilityBlockReason() is { } blocked) return HostActionResult.Unavailable(blocked);
        }
        if (run.NonExecutingDiagnostic)
            return action == WorkflowRunAction.Stop
                ? HostActionResult.Effective("非执行诊断已经取消，无执行责任")
                : HostActionResult.Unavailable("非执行终态诊断不可修改或恢复");
        if (_admissionWired)
        {
            try
            {
                var root = Directory.GetParent(_runsDirPath!)?.FullName ?? _runsDirPath!;
                var store = _admissionStore ?? new ArbitrationLeaseStore(_arbitrationDir ?? Path.Combine(root, "arbitration"));
                var read = store.Read();
                if (HasOriginalAdmissionMapping(run) && !IsCompleteAdmissionRead(read))
                    return HostActionResult.Unavailable("原受理存储不完整，停止责任未确认，原文件保留；恢复存储后再次执行 Stop 重试");
                await EnsureAdmissionFacadeAsync(_shutdownCts.Token).ConfigureAwait(false);
                _runs.VerifyBoundOwner();
            }
            catch (Exception ex) { return HostActionResult.Unavailable("运行写者资格不可确认，未执行动作：" + ex.Message); }
        }
        DriveEntry? stopEntry;
        lock (_gate) _drives.TryGetValue(run.WorkflowId, out stopEntry);
        if (action == WorkflowRunAction.Stop && run.State is not (WorkflowRunState.Succeeded or WorkflowRunState.Failed or WorkflowRunState.LocalWaitParking)
            && stopEntry?.Runner.HasActiveControl(runId) != true)
        {
            try
            {
                var published = _runs.UpdateMergingIf(runId, latest =>
                {
                    if (latest.StopRequested) return false;
                    latest.StopRequested = true;
                    return true;
                }, out var stopped);
                if (stopped is null || (!published && !stopped.StopRequested))
                    return HostActionResult.Unavailable("停止记录不可确认，未宣告停止完成");
                RunStore.RebaseOnto(run, stopped);
            }
            catch (Exception ex) { return HostActionResult.Unavailable("停止意图落盘失败，未宣告停止完成：" + ex.Message); }
        }

        if (action == WorkflowRunAction.Stop && run.State == WorkflowRunState.LocalWaitParking)
            return await StopParkedRunAsync(runId).ConfigureAwait(false);

        // Explicit retry path after a previous Stop durably cancelled the run but admission
        // reconciliation could not be confirmed. It is safe for any cancelled run: an empty
        // runBinding lookup is a confirmed no-op, and already-terminal operations are not rewritten.
        if (_admissionWired && action == WorkflowRunAction.Stop && run.State == WorkflowRunState.Cancelled)
            return await ReconcileAdmissionTerminalForExplicitStopAsync(runId,
                "运行已终态化，关联受理登记已核对").ConfigureAwait(false);

        // [Unknown 面板停止·原身份只读对账] Unknown=结果不可考：登记 StopRequested（上方第一个块已完成）后，
        // 按原身份只读对账——不创建新 run、不重发，只按幂等键+job 查询。对账成功（所有事实确认、基线未变）
        // → 转 Cancelled；对账失败/不可考 → 保持 Unknown 并保留责任。
        if (action == WorkflowRunAction.Stop && run.State == WorkflowRunState.Unknown)
        {
            try { return await ReconcileUnknownRunForStopAsync(runId).ConfigureAwait(false); }
            catch (Exception ex)
            {
                return HostActionResult.Unavailable("原身份停止对账或发布不可确认，保留耐久停止意图与未决责任：" + ex.GetType().Name);
            }
        }

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
                $"运行当前不在驱动中（状态 {run.State}）：Interrupted/Paused/LocalWaitParking 请用「恢复」，终态运行无需动作");
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

    /// <summary>停驻运行的显式放弃：先按完整队列绑定取消等待项，再把同一运行记为终态；绝不收尾或发送。</summary>
    private async Task<HostActionResult> StopParkedRunAsync(string runId)
    {
        lock (_gate)
        {
            WorkflowRunRecord? fresh;
            try { fresh = _runs.Load(runId); }
            catch (Exception ex)
            {
                return HostActionResult.Unavailable("停驻运行记录复核失败，未清理等待项："
                    + ex.GetType().Name + "（" + ex.Message + "）");
            }
            if (fresh is null || string.IsNullOrWhiteSpace(fresh.RunId)
                || !string.Equals(fresh.RunId, runId, StringComparison.Ordinal))
                return HostActionResult.Unavailable("运行文件名与记录内 runId 不一致，拒绝终态化");
            if (fresh?.State != WorkflowRunState.LocalWaitParking)
                return HostActionResult.Unavailable("运行状态已变化，请刷新后重试");
            if (_reservedWorkflows.Contains(fresh.WorkflowId) || _drives.ContainsKey(fresh.WorkflowId))
                return HostActionResult.Unavailable("停驻运行仍处于启动或驱动收敛窗口，请稍后重试");
            if (RunStore.HasUnresolvedExternalFact(fresh))
                return HostActionResult.Unavailable("存在未决发送或收尾事实，必须先按原身份对账；未停止运行、未清理等待项");
            if (HasUnresolvedPrerequisiteResponsibility(fresh))
                return HostActionResult.Unavailable("存在未决前置动作责任，必须按原身份对账；未停止运行、未清理等待项");
            if (!HasValidParkedDecision(fresh, out var binding))
                return HostActionResult.Unavailable("停驻运行的零发送裁定或游标绑定不完整，拒绝终态化");

            var cleanup = LocalWaitBindingCancelResult.Missing;
            if (binding is not null)
            {
                try
                {
                    cleanup = LocalWaitQueue.Cancel(binding, "停驻运行显式停止", DateTimeOffset.UtcNow);
                }
                catch (Exception ex)
                {
                    return HostActionResult.Unavailable("等待项清理失败，运行保持停驻以便重试："
                        + ex.GetType().Name + "（" + ex.Message + "）");
                }
                if (cleanup == LocalWaitBindingCancelResult.PayloadMismatch)
                    return HostActionResult.Unavailable("等待项身份或载荷已变化，拒绝取消并保留停驻运行");
            }

            fresh.StopRequested = true;
            fresh.State = WorkflowRunState.Cancelled;
            fresh.Note = (fresh.Note is null ? "" : fresh.Note + " ")
                + (binding is null
                    ? "持久拒登停驻已显式放弃（无等待绑定；零发送；不触发收尾）。"
                    : cleanup is LocalWaitBindingCancelResult.Cancelled or LocalWaitBindingCancelResult.AlreadyCancelled
                        ? "本地等待停驻已显式放弃（等待项已墓碑化；零发送；不触发收尾）。"
                        : "本地等待停驻已显式放弃（绑定等待项已不存在；零发送；不触发收尾）。");
            try
            {
                _runs.Update(fresh);
            }
            catch (Exception ex)
            {
                return HostActionResult.Unavailable("等待项处置已提交，但运行终态落盘失败；保留原运行记录并可重试："
                    + ex.GetType().Name + "（" + ex.Message + "）");
            }
        }

        NotifyStateChanged();
        return await ReconcileAdmissionTerminalForExplicitStopAsync(runId,
            "已放弃停驻运行（终态化，未触发收尾）").ConfigureAwait(false);
    }

    /// <summary>Stop 专用的前置动作责任护栏；恢复扫描的既有状态分类保持不变。</summary>
    /// <summary>
    /// [Unknown 面板停止·原身份只读对账] 对 Unknown 运行按原身份只读对账：不创建新 run、不重发。
    /// 逐项核验主体/前置/收尾的未决事实（TerminalReleaseEvidence），全部清偿且基线未变 → 转 Cancelled；
    /// 任一不可考/冲突/离线 → 保持 Unknown 并保留责任（StopRequested 已耐久登记，供后续重试对账）。
    /// </summary>
    private async Task<HostActionResult> ReconcileUnknownRunForStopAsync(string runId)
    {
        WorkflowRunRecord? run;
        try { run = _runs.Load(runId); }
        catch (Exception ex)
        {
            return HostActionResult.Unavailable("运行记录复核失败，未对账："
                + ex.GetType().Name + "（" + ex.Message + "）");
        }
        if (run?.State != WorkflowRunState.Unknown)
            return HostActionResult.Unavailable("运行状态已变化，请刷新后重试");

        var client = _clientAccessor();
        if (client is null || client.State != BgiExternalLinkState.Ready)
            return HostActionResult.Unavailable("BGI 离线，无法按原身份只读对账；保持 Unknown 待重试");

        var boundary = CreateProductionBoundary(client);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        if (_admissionWired)
            await EnsureAdmissionFacadeAsync(budget.Token).ConfigureAwait(false);
        // 主体提交对账（只读，不重发）。无 CurrentSubmission=无需对账项。
        if (run.CurrentSubmission is { } sub && !TerminalReleaseEvidence.BodySettled(run, sub))
        {
            var result = await ReconcileOriginalNodeSubmissionAsync(boundary, run, sub, budget.Token).ConfigureAwait(false);
            if (!result.Accepted) return HostActionResult.Unavailable("主体原轮次恢复/门面结清未成立：" + result.RejectReason);
            // Acceptance closes the send round, not the executor. Stop must retain responsibility
            // until the same original job has exited and that observation is durable.
            try
            {
                var accepted = run.CurrentSubmission!;
                var acceptedIdentity = TryResolveNodeSendIdentity(run, accepted);
                if (acceptedIdentity is null)
                    return HostActionResult.Unavailable("主体原轮次在停止观察前失效，保持 Unknown 待重试");
                await boundary.RequestSubmissionCancelAsync(run, accepted, budget.Token).ConfigureAwait(false);
                var exit = await boundary.AwaitSubmissionExitAsync(run, accepted, budget.Token).ConfigureAwait(false);
                var observed = _runs.Load(runId);
                if (exit.Uncertain || !exit.ExecutionExitConfirmed || observed?.CurrentSubmission is not { } persisted
                    || persisted.JobId != result.JobId
                    || TryResolveNodeSendIdentity(observed, persisted) != acceptedIdentity
                    || !TerminalReleaseEvidence.BodySettled(observed, persisted))
                    return HostActionResult.Unavailable("主体原作业退出与持久读回未确认，保持 Unknown 待重试");
                RunStore.RebaseOnto(run, observed);
            }
            catch (Exception) { return HostActionResult.Unavailable("主体原作业停止观察不可考，保持 Unknown 待重试"); }
        }
        // 前置动作对账（只读，不补发）。
        var prereq = new BgiWorkflowPrerequisiteAdapter(client, _runs);
        foreach (var action in run.PrerequisiteActions)
        {
            if (TerminalReleaseEvidence.PrerequisiteSettled(run, action)) continue;
            try { await prereq.ConfirmCancellationAsync(run, action, budget.Token).ConfigureAwait(false); }
            catch (Exception) { return HostActionResult.Unavailable("前置动作对账不可考，保持 Unknown 待重试"); }
        }
        // 收尾动作对账（只读，不补发）。
        if (run.PendingCompletion is { } completion && !TerminalReleaseEvidence.CompletionSettled(run, completion))
        {
            var terminal = new BgiWorkflowTerminalExecutor(client, _runs);
            try { await terminal.ConfirmCancellationAsync(run, completion, budget.Token).ConfigureAwait(false); }
            catch (Exception) { return HostActionResult.Unavailable("收尾动作对账不可考，保持 Unknown 待重试"); }
        }


        // [G7-residual·本批] **历史缺身份提交的恢复关联**：SubmissionHistory 中「有 jobId、缺 AcceptedSendIdentity」
        // 的旧提交无法被 NodeHash 定位（节点永不可封印）。按原身份只读对账确认唯一命中后，追加**只增不改**的
        // 恢复关联（不改历史原件、不放宽守卫）。对账不可考/多命中/身份冲突一律保守保留，保持 Unknown。
        foreach (var historical in run.SubmissionHistory.Where(s => !string.IsNullOrEmpty(s.JobId)
            && !string.IsNullOrEmpty(s.Key)).ToList())
        {
            var identity = TryResolveNodeSendIdentity(run, historical);
            if (identity is null) return HostActionResult.Unavailable("历史提交无法唯一关联原发送身份，保持 Unknown 待重试");
            if (!TerminalReleaseEvidence.UniqueOriginalOutcomeSet(run, historical, identity.SubmissionIdentity))
                return HostActionResult.Unavailable("历史原提交与结果关联缺失/重复/冲突，保持 Unknown 待重试");
            var priorObservations = run.RecoveryAssociations.Where(a => a.SubmissionIdentity == identity.SubmissionIdentity
                && TerminalReleaseEvidence.ValidRecoveryAssociation(run, a)).ToList();
            var existingSeal = run.NodeReleaseSeals.Where(s => s.SubmissionIdentity == identity.SubmissionIdentity).ToList();
            if (priorObservations.Count > 1 || existingSeal.Count > 1)
                return HostActionResult.Unavailable("历史恢复关联或封印歧义，保持 Unknown 待重试");
            if (existingSeal.Count == 1 && TerminalReleaseEvidence.BodySettled(run, historical)
                && TerminalReleaseEvidence.NodeHash(run, identity.SubmissionIdentity, true) == existingSeal[0].FactsHash)
            {
                if (!await SettleOriginalNodeAcceptanceAsync(run, identity, historical.JobId!).ConfigureAwait(false))
                    return HostActionResult.Unavailable("已封印历史原轮结清未成立，保持 Unknown 待重试");
                continue;
            }
            BgiJobInfo? observedExecution = priorObservations.Count == 1 ? priorObservations[0].ObservedExecution : null;
            string? hitJobId;
            try
            {
                if (observedExecution is null && (!TerminalReleaseEvidence.BodySettled(run, historical)
                    || !run.NodeOutcomes.Any(o => o.SubmissionKey == historical.Key)))
                    observedExecution = await boundary.ObserveHistoricalExecutionAsync(run, historical, identity.SubmissionIdentity, budget.Token).ConfigureAwait(false);
                hitJobId = observedExecution?.JobId
                    ?? await boundary.ReconcileHistoricalSubmissionAsync(run, historical, budget.Token, identity.SubmissionIdentity).ConfigureAwait(false);
            }
            catch (Exception) { return HostActionResult.Unavailable("历史提交对账不可考，保持 Unknown 待重试"); }
            if (hitJobId is null || !string.Equals(hitJobId, historical.JobId, StringComparison.Ordinal))
                return HostActionResult.Unavailable("历史提交无唯一同身份受理证据，保持 Unknown 待重试");
            var association = new RecoveryAssociationRecord
            {
                HistoryIndex = run.SubmissionHistory.FindIndex(s => TerminalReleaseEvidence.Hash(s) == TerminalReleaseEvidence.Hash(historical)),
                HistoryHash = TerminalReleaseEvidence.Hash(historical),
                OutcomeIndex = run.NodeOutcomes.FindIndex(outcome => outcome.SubmissionKey == historical.Key),
                OutcomeHash = run.NodeOutcomes.FirstOrDefault(outcome => outcome.SubmissionKey == historical.Key) is { } historicalOutcome
                    ? TerminalReleaseEvidence.Hash(historicalOutcome) : null,
                SubmissionKey = historical.Key,
                SubmissionIdentity = identity.SubmissionIdentity,
                SendSeq = identity.SendSeq,
                JobId = hitJobId,
                Epoch = historical.Epoch,
                EvidenceSource = observedExecution is null ? "host:reconcile_historical" : "host:historical_original_exit",
                ObservedAtUtc = DateTimeOffset.UtcNow,
                ObservedExecution = observedExecution,
            };
            if (priorObservations.Count == 1) association = priorObservations[0];
            // A fully bound original needs no additive association unless new exit/outcome evidence is required.
            var needsAssociation = string.IsNullOrEmpty(historical.AcceptedSendIdentity) || observedExecution is not null;
            if (needsAssociation && _runs.TryAppendRecoveryAssociation(runId, association) is null)
                return HostActionResult.Unavailable("历史提交恢复关联落盘失败/冲突，保持 Unknown 待重试");
            var associated = _runs.Load(runId);
            if (associated is null || needsAssociation && !TerminalReleaseEvidence.ValidRecoveryAssociation(associated, association))
                return HostActionResult.Unavailable("历史原轮关联读回未成立，保持 Unknown 待重试");
            var associatedOriginal = associated.SubmissionHistory[association.HistoryIndex];
            if (TryResolveNodeSendIdentity(associated, associatedOriginal) != identity
                || !await SettleOriginalNodeAcceptanceAsync(associated, identity, hitJobId).ConfigureAwait(false))
                return HostActionResult.Unavailable("历史原轮门面结清未成立，保持 Unknown 待重试");
            var originalOperation = _admissionStore?.Read().File?.Handoff is { } originalHandoff
                ? originalHandoff.Operations.Concat(originalHandoff.ArchivedOperations.Select(a => a.Operation))
                    .SingleOrDefault(o => o.RequestIdentity == identity.RequestIdentity) : null;
            if (originalOperation is null || _runs.TrySealTerminalNode(runId, originalOperation) is null)
                return HostActionResult.Unavailable("历史原轮封印未成立，保持 Unknown 待重试");
            RunStore.RebaseOnto(run, _runs.Load(runId)!);
        }
        // 对账后重读：全部事实清偿且基线未变 → 转 Cancelled；否则保持 Unknown。
        var fresh = _runs.Load(runId);
        if (fresh?.State != WorkflowRunState.Unknown)
            return HostActionResult.Unavailable("运行状态已变化，请刷新后重试");
        if (RunStore.HasUnresolvedTerminalResponsibility(fresh))
            return HostActionResult.Unavailable("存在未决发送/收尾/前置事实，对账未清偿；保持 Unknown 待重试");

        fresh.State = WorkflowRunState.Cancelled;
        fresh.Note = (fresh.Note is null ? "" : fresh.Note + " ")
            + "Unknown 经原身份只读对账，全部事实清偿，显式停止（不触发收尾、不重发）。";
        try { _runs.Update(fresh); }
        catch (Exception ex)
        {
            return HostActionResult.Unavailable("对账终态落盘失败，保持 Unknown 待重试："
                + ex.GetType().Name + "（" + ex.Message + "）");
        }
        NotifyStateChanged();
        return await ReconcileAdmissionTerminalForExplicitStopAsync(runId, "已停止（Unknown 经原身份只读对账，全部事实清偿）").ConfigureAwait(false);
    }

    private static bool HasUnresolvedPrerequisiteResponsibility(WorkflowRunRecord run)
        => run.PrerequisiteActions?.Any(action => action.State is not (
            PrerequisiteActionState.Succeeded or PrerequisiteActionState.Failed or PrerequisiteActionState.Cancelled)) == true;

    private static bool HasValidParkedDecision(WorkflowRunRecord run, out LocalWaitBinding? binding)
    {
        binding = null;
        var decision = run.LocalWaitDecision;
        if (decision is null || !decision.NoSendConfirmed
            || decision.Kind is not (LocalWaitDecisionKind.Wait or LocalWaitDecisionKind.Hold)
            || run.Cursor is not { } cursor
            || string.IsNullOrWhiteSpace(run.RunId)
            || string.IsNullOrWhiteSpace(run.WorkflowId)
            || string.IsNullOrWhiteSpace(run.WorkflowRevision)
            || string.IsNullOrWhiteSpace(cursor.NodeId)
            || cursor.Occurrence < 0 || cursor.Occurrence > 99_999_999
            || cursor.LoopIteration < 0 || cursor.LoopIteration > 99_999_999
            || cursor.Attempt <= 0 || cursor.Attempt > 99_999_999)
            return false;

        var context = decision.Context;
        if (context is null
            || string.IsNullOrWhiteSpace(context.RunId)
            || string.IsNullOrWhiteSpace(context.WorkflowId)
            || string.IsNullOrWhiteSpace(context.WorkflowRevision)
            || string.IsNullOrWhiteSpace(context.NodeId)
            || run.RecordRevision <= 0
            || context.RecordRevision <= 0 || context.RecordRevision > run.RecordRevision
            || context.SequenceIndex < 0 || context.Occurrence < 0
            || context.Occurrence > 99_999_999
            || context.LoopIteration < 0 || context.Attempt <= 0
            || context.LoopIteration > 99_999_999 || context.Attempt > 99_999_999
            || !string.Equals(context.RunId, run.RunId, StringComparison.Ordinal)
            || !string.Equals(context.WorkflowId, run.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(context.WorkflowRevision, run.WorkflowRevision, StringComparison.Ordinal)
            || !string.Equals(context.CursorNodeId, cursor.NodeId, StringComparison.Ordinal)
            || context.CursorOccurrence != cursor.Occurrence
            || context.CursorLoopIteration != cursor.LoopIteration
            || !string.Equals(context.NodeId, cursor.NodeId, StringComparison.Ordinal)
            || context.Occurrence != cursor.Occurrence
            || context.LoopIteration != cursor.LoopIteration
            || context.Attempt != cursor.Attempt
            || run.CurrentSubmission is not { } submission
            || submission.Intent != SubmitIntentState.LocalWaitDeferred
            || submission.SendAttempted
            || !string.IsNullOrEmpty(submission.JobId)
            || !string.IsNullOrEmpty(submission.AcceptedSendIdentity)
            || submission.ObservedTerminal is not null
            || !string.Equals(submission.NodeId, context.NodeId, StringComparison.Ordinal)
            || submission.Occurrence != context.Occurrence
            || submission.LoopIteration != context.LoopIteration
            || submission.Attempt != context.Attempt
            || !string.Equals(submission.Key,
                RunStore.DeriveSubmissionKey(run.RunId, context.NodeId, context.Occurrence,
                    context.LoopIteration, context.Attempt), StringComparison.Ordinal))
            return false;

        if (decision.Kind == LocalWaitDecisionKind.Hold)
            return decision.Binding is null;
        if (decision.Binding is not { } waitBinding
            || string.IsNullOrWhiteSpace(waitBinding.ItemId)
            || string.IsNullOrWhiteSpace(waitBinding.StableIdentity)
            || string.IsNullOrWhiteSpace(waitBinding.CandidateId)
            || string.IsNullOrWhiteSpace(waitBinding.AdmissionIdentity)
            || string.IsNullOrWhiteSpace(waitBinding.Namespace)
            || string.IsNullOrWhiteSpace(waitBinding.SourceIdentity)
            || string.IsNullOrWhiteSpace(waitBinding.Scope)
            || string.IsNullOrWhiteSpace(waitBinding.WorkflowRevision)
            || string.IsNullOrWhiteSpace(waitBinding.NodeId)
            || string.IsNullOrWhiteSpace(waitBinding.PrerequisiteReference)
            || waitBinding.RecordRevision <= 0 || waitBinding.RecordRevision > run.RecordRevision
            // The queue binding is immutable across explicit repark: a fresh decision context may
            // be newer, but it must never predate the binding snapshot it is validating.
            || waitBinding.RecordRevision > context.RecordRevision
            || waitBinding.SequenceIndex != context.SequenceIndex
            || !string.Equals(waitBinding.RunId, run.RunId, StringComparison.Ordinal)
            || !string.Equals(waitBinding.WorkflowId, run.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(waitBinding.Namespace, run.WorkflowId, StringComparison.Ordinal)
            || !string.Equals(waitBinding.WorkflowRevision, run.WorkflowRevision, StringComparison.Ordinal)
            || !string.Equals(waitBinding.NodeId, cursor.NodeId, StringComparison.Ordinal)
            || !string.Equals(waitBinding.CursorNodeId, cursor.NodeId, StringComparison.Ordinal)
            || waitBinding.CursorOccurrence != cursor.Occurrence
            || waitBinding.CursorLoopIteration != cursor.LoopIteration
            || waitBinding.Occurrence != cursor.Occurrence
            || waitBinding.LoopIteration != cursor.LoopIteration
            || waitBinding.Attempt != cursor.Attempt
            || waitBinding.SourceKind != context.SourceKind
            || !string.Equals(context.SourceIdentity, waitBinding.SourceIdentity, StringComparison.Ordinal)
            || !HasValidParkedSourceIdentity(run, waitBinding)
            || !string.Equals(context.Scope, waitBinding.Scope, StringComparison.Ordinal)
            || !string.Equals(context.CandidateId, waitBinding.CandidateId, StringComparison.Ordinal)
            || !string.Equals(context.AdmissionIdentity, waitBinding.AdmissionIdentity, StringComparison.Ordinal)
            || context.Tier != waitBinding.Tier
            || context.Priority != waitBinding.Priority
            || context.IsHoeingHighest != waitBinding.IsHoeingHighest
            || !context.HasTrustedRankingFacts
            || waitBinding.IsHoeingHighest
            || !Enum.IsDefined(waitBinding.SourceKind)
            || !Enum.IsDefined(waitBinding.Tier)
            || !IsCanonicalAdmissionScope(waitBinding.Scope)
            || !string.Equals(waitBinding.StableIdentity,
                run.RunId + "|" + cursor.NodeId + "|" + cursor.Occurrence + "|" + cursor.LoopIteration,
                StringComparison.Ordinal)
            || !string.Equals(waitBinding.ItemId, LocalWaitQueuePolicy.DeriveItemId(waitBinding.StableIdentity),
                StringComparison.Ordinal)
            || !LocalWaitIdentityTranslation.Translate(waitBinding.ToQueueItem()).Ok)
            return false;

        var candidate = BuildSuccessorIdentityCandidate(waitBinding.Scope, run.WorkflowId, run.RunId,
            cursor.NodeId, cursor.Occurrence, cursor.LoopIteration, cursor.Attempt);
        var expectedIdentity = LocalWaitIdentityTranslation.BuildAdmissionIdentity(candidate);
        if (!string.Equals(waitBinding.AdmissionIdentity, expectedIdentity.AdmissionIdentity, StringComparison.Ordinal)
            || !string.Equals(waitBinding.CandidateId, expectedIdentity.CandidateId, StringComparison.Ordinal))
            return false;

        binding = waitBinding;
        return true;
    }

    private static bool HasValidParkedSourceIdentity(WorkflowRunRecord run, LocalWaitBinding binding)
    {
        // Panel RequestIdentity is captured from the resolved FlowRegistration parent into both
        // decision snapshots; it is not duplicated in RunStore. Stop uses it only as paired
        // snapshot identity and never as submission authority.
        if (binding.SourceKind == LocalWaitSourceKind.PanelFlowRegistration)
            return true;

        // StartupHandoff has an independent durable source in this run record. Do not let a
        // matching context/binding pair substitute another run's handoff identity or scope.
        return binding.SourceKind == LocalWaitSourceKind.StartupHandoff
               && string.Equals(binding.SourceIdentity, run.RunId, StringComparison.Ordinal)
               && IsCanonicalAdmissionScope(run.AdmissionSourceScope)
               && string.Equals(binding.Scope, run.AdmissionSourceScope, StringComparison.Ordinal)
               && run.AdmissionParentSource is { } original && original.MatchesHandoff(run);
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
    internal static HandoffRegisterResult? SnapshotPrecheck(ControlStatus? snapshot, string mode, string? expectedBgiEpoch)
    {
        if (mode == StartupHandoffModes.ArmTrigger) return null;
        if (snapshot is null || !snapshot.HasFreshTaskStatus(DateTimeOffset.UtcNow)
            || (!string.IsNullOrWhiteSpace(expectedBgiEpoch)
                && !string.Equals(snapshot.TaskStatusBgiEpoch, expectedBgiEpoch, StringComparison.Ordinal)))
            return HandoffRegisterResult.Rejected(HandoffReasonCodes.StatusUncertain,
                "BGI 任务状态快照不可考、已过期或进程纪元不匹配，授权判断要求当前可考快照（锚点 3）");
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

        if (!_admissionWired) await EnsureRecoveredAsync().ConfigureAwait(false);
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
            var expectedBgiEpoch = request.Mode == StartupHandoffModes.ArmTrigger ? null : CurrentBgiEpoch();
            if (SnapshotPrecheck(snapshot, request.Mode, expectedBgiEpoch) is { } snapRejected)
                return RejectedWithLedgerRecheck(request, snapRejected.ReasonCode!, snapRejected.Reason!);
        }

        WorkflowSnapshot? startSnapshot = null;
        WorkflowRunner? handoffRunner = null;
        if (request.Mode != StartupHandoffModes.Resume)
        {
            try
            {
                startSnapshot = _workflows.LoadSnapshot(request.WorkflowId);
                handoffRunner = CreateRunner(_clientAccessor());
            }
            catch (Exception ex) { return RejectedWithLedgerRecheck(request, HandoffReasonCodes.FlowUnavailable, "执行组件组装/快照读取失败：" + ex.Message); }
            try
            {
                if (request.StopAuthority is { } inherited && _runs.IsStartupSourceRevoked(inherited))
                    throw new InvalidOperationException("原来源已经停止，需要新的明确启动意图");
                await handoffRunner.ValidateInheritedStopAuthorityAsync(request.StopAuthority, ensureCts.Token).ConfigureAwait(false);
            }
            catch (Exception ex) { return RejectedWithLedgerRecheck(request, HandoffReasonCodes.NotReady, ex.Message); }
        }
        if (_admissionWired)
        {
            var sameFlow = _runs.List().Where(r => r.WorkflowId == request.WorkflowId).ToList();
            if (sameFlow.Any(r => r.State == WorkflowRunState.Unknown))
                return RejectedWithLedgerRecheck(request, HandoffReasonCodes.Unknown, "该流程存在Unknown运行，需先对账");
            if (CurrentArbitrationFacts().F11Active)
                return RejectedWithLedgerRecheck(request, HandoffReasonCodes.NotReady, "F11 独立停止闸门激活（租约零副作用）");
            if (request.Mode == StartupHandoffModes.Resume)
            {
                var target = sameFlow.Where(r => !r.IsTerminal && !r.NonExecutingDiagnostic)
                    .OrderByDescending(r => r.UpdatedAt).FirstOrDefault();
                if (target is not null && TryGetAdmissionScopeForResume(target.RunId, target.WorkflowId) is null)
                    return RejectedWithLedgerRecheck(request, HandoffReasonCodes.NoResumableRun,
                        "恢复缺少已登记固定来源（未产生任何租约副作用）");
            }
        }
        await EnsureRecoveredAsync().ConfigureAwait(false);
        return request.Mode == StartupHandoffModes.Resume
            ? await RegisterResumeHandoff(request, ct).ConfigureAwait(false)
            : RegisterStartHandoff(request, ct, startSnapshot, handoffRunner);
    }

    /// <summary>
    /// **准入来源固定 Scope（G4a／[批次四十五]）**：启动移交受理时**捕获** `bgi:{实例}:{epoch}` 作为该 run 的
    /// 固定来源 Scope；当前 epoch 不可用 ⇒ 返回 null（该 run 无固定来源 ⇒ 后继准入**不签发、不发送**；
    /// **不得**读当前 epoch 补造，也不因此否决 R4.9 的既有受理合同）。
    /// </summary>
    private string? AdmissionSourceScopeForRun()
    {
        var epoch = CurrentBgiEpoch();
        return string.IsNullOrEmpty(epoch) ? null : "bgi:local:" + epoch;
    }

    /// <summary>start / armTrigger 语义受理（§4）：arm 仅 Waiting+有待触发时刻才算已挂载（身份追加登记后 AlreadyAccepted 幂等）；其余活动态响亮拒绝。</summary>
    private HandoffRegisterResult RegisterStartHandoff(StartupHandoffRequest request, CancellationToken ct,
        WorkflowSnapshot? preparedSnapshot = null, WorkflowRunner? preparedRunner = null)
    {
        // 锁外预检（只读无副作用；拒绝返回前经台账复核，I3）
        WorkflowSnapshot snapshot;
        try
        {
            snapshot = preparedSnapshot ?? _workflows.LoadSnapshot(request.WorkflowId); // 隔离/缺失响亮抛出
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
                runner = preparedRunner ?? CreateRunner(_clientAccessor());
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
                // G4a（[批次四十五]）：**准入来源固定 Scope 与受理同一次落盘**（`bgi:{实例}:{受理时 epoch}`）——
                // 来源权威＝运行台账的受理登记事实（AMD-1-5 第 2 类来源）；epoch 不可用 ⇒ 留空（后继准入
                // 不签发、不发送，**不得**读当前 epoch 补造），受理本身仍按 R4.9 既有合同成立。
                run = _runs.CreateRun(request.WorkflowId, snapshot.Revision,
                    note: $"启动中心移交受理（{request.Mode}，执行 {shortId}）",
                    handoff: BuildIdentity(request),
                    admissionSourceScope: AdmissionSourceScopeForRun(), stopAuthority: request.StopAuthority);
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
    private async Task<HandoffRegisterResult> RegisterResumeHandoff(StartupHandoffRequest request, CancellationToken ct)
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
                .Where(r => r.State is WorkflowRunState.Interrupted or WorkflowRunState.Paused
                    or WorkflowRunState.LocalWaitParking) // [批次 20／Wave3／C11=(a)；R46 重要-1 补齐]
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
                        or WorkflowRunState.LocalWaitParking // [批次 20／Wave3／C11=(a)] 停驻运行可移交 resume
                        ? null
                        : (HandoffReasonCodes.NoResumableRun, "目标运行状态已变化，请刷新后重试"),
                    ct, out committedBind) is { } bindError)
                return HandoffRegisterResult.Rejected(bindError.Code, bindError.Reason);
            if (!_admissionWired) _reservedWorkflows.Add(request.WorkflowId); // B2-β：接线后由恢复准入 sender 侧预留（避免双预留自拒）
            boundRunId = committedBind!.RunId;
        }

        // B2-β：接线后恢复动作经 E2 恢复专用准入边界（意图持久化后发送；发送=门面回调驱动 ResumeAsync）。
        // 受理不撤回——绑定台账已落盘；准入未放行=运行保持可恢复状态（与旧路径 launch Unavailable 同口径）。
        if (_admissionWired)
        {
            // 会诊 P1 处置：绑定台账已在上方锁内原子追加=受理已成立，本 await 起的任何异常（锁争用耗尽/门面异常）
            // 都不得把「已受理」变成对外异常——隔离异常并如实报告执行状态未知（受理不撤回，与 I1 同口径）。
            HostActionResult admitted;
            try
            {
                admitted = await SubmitResumeViaAdmissionAsync(committedBind!, "startup:handoff:resume").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                var unknownState = TryReadRunState(boundRunId) ?? committedBind!.State;
                return HandoffRegisterResult.Accepted(boundRunId,
                    $"已受理恢复绑定（受理≠执行成功）；准入边界异常，执行结果待核实：{ex.GetType().Name}", unknownState);
            }
            var confirmedWired = TryReadRunState(boundRunId) ?? committedBind!.State;
            return admitted.Status == HostActionStatus.Registered
                ? HandoffRegisterResult.Accepted(boundRunId, "已移交受理：恢复既有运行（仲裁准入通过，游标身份重定位；受理≠执行成功）", confirmedWired)
                : HandoffRegisterResult.Accepted(boundRunId, $"已受理恢复绑定（受理≠执行成功）；仲裁面未放行：{admitted.Message}（运行保持可恢复状态）", confirmedWired);
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
    public Task ShutdownAsync()
    {
        lock (_gate)
        {
            if (_shutdownTask is not null) return _shutdownTask;
            _shutdown = true;
            var drives = _driveCompletions.ToList();
            // Scheduling guarantees no user cancellation callback executes under _gate.
            // The same task represents cancellation, full observation and bounded convergence.
            _shutdownTask = Task.Run(() => ShutdownCoreAsync(drives));
            return _shutdownTask;
        }
    }

    private async Task ShutdownCoreAsync(List<DriveEntry> drives)
    {
        var cancellations = drives.Select(d => CancelIsolatedAsync(d.Cts)).Append(CancelIsolatedAsync(_shutdownCts));
        var complete = Task.WhenAll(cancellations.Concat(drives.Select(d => d.Completion.Task)));
        await Task.WhenAny(complete, Task.Delay(ShutdownConvergeBudget)).ConfigureAwait(false);
        // Both cancellation callbacks and terminal writeback consume the original 10s budget.
        // Late observers retain their original owner and cannot borrow a successor capability.
        ReleaseAdmissionLeaseOnShutdown();
        if (!complete.IsCompleted)
            _ = Task.Run(() => TryLog($"[任务中心] 宿主关闭：{drives.Count} 个运行未在 {ShutdownConvergeBudget.TotalSeconds:0}s 内收敛（在飞事实保留，下次启动恢复扫描标记）"));
    }

    private Task CancelIsolatedAsync(CancellationTokenSource cts)
        => Task.Run(() =>
        {
            try { cts.Cancel(); }
            catch (ObjectDisposedException) { /* Completed observer disposed its own source. */ }
            catch (Exception ex)
            {
                _ = Task.Run(() => TryLog("[任务中心] 退出取消回调异常（原观察责任保持）：" + ex.GetType().Name));
            }
        });

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
        if (IsCurrentTaskSnapshotUsable(snapshot)) return snapshot;
        var deadline = DateTime.UtcNow.Add(_snapshotWaitBudget);
        while (DateTime.UtcNow < deadline)
        {
            ct.ThrowIfCancellationRequested();
            // 会诊三轮 建议：轮询粒度不超出剩余预算（注入短预算的测试接缝不越界等待）
            var slice = deadline - DateTime.UtcNow;
            if (slice <= TimeSpan.Zero) break;
            await Task.Delay(slice < TimeSpan.FromMilliseconds(500) ? slice : TimeSpan.FromMilliseconds(500), ct).ConfigureAwait(false);
            snapshot = _statusSnapshotProvider();
            if (IsCurrentTaskSnapshotUsable(snapshot)) return snapshot;
        }
        return null;
    }

    private bool IsCurrentTaskSnapshotUsable(ControlStatus? snapshot)
    {
        if (snapshot is null || !snapshot.HasFreshTaskStatus(DateTimeOffset.UtcNow)) return false;
        var currentEpoch = CurrentBgiEpoch();
        return string.IsNullOrWhiteSpace(currentEpoch)
            || string.Equals(snapshot.TaskStatusBgiEpoch, currentEpoch, StringComparison.Ordinal);
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
        var boundary = CreateProductionBoundary(c);
        // R5.2 B2-γ 第 3 步（§13.10 A）：**节点提交**改道经仲裁面。双门：`_admissionWired`（E1/E2 入口已接线）
        // ＋ `_successorAdmissionWired`（第 3 步路径启用，§12.3 施工阻断）。缺任一＝保持原直通（R4 行为合同不变）。
        IWorkflowExecutionBoundary effective = _admissionWired && _successorAdmissionWired
            ? new ArbitrationWorkflowExecutionBoundary(boundary, SubmitSuccessorViaAdmissionAsync,
                (run, sub, ct) => ReconcileOriginalNodeSubmissionAsync(boundary, run, sub, ct))
            : boundary;
        return new WorkflowRunner(_workflows, _runs,
            effective,
            new BgiWorkflowPrerequisiteAdapter(c, _runs),
            new BgiWorkflowTerminalExecutor(c, _runs),
            options: new WorkflowRunnerOptions
            {
                FlexibleFactsProvider = () =>
                {
                    var facts = CurrentArbitrationFacts();
                    return new FlexibleWindowFacts
                    {
                        ExecutionOccupied = facts.ExecutionOccupied || facts.ExecutionFactsUnknown,
                        F11Cooldown = facts.F11Active,
                        ActiveTicket = facts.ActiveTicket is not null,
                        FixedScheduleDeclared = _runs.List().Any(r => r.State == WorkflowRunState.Waiting
                            && r.TriggerTiming is { Kind: "trigger.timeFixed" } timing && timing.ScheduledAt <= DateTimeOffset.Now),
                    };
                },
            },
            localWaitQueue: LocalWaitQueue,
            localWaitPrerequisiteReferenceProvider: LocalWaitPrerequisiteReference,
            localWaitAdmissionScopeProvider: run => TryGetAdmissionScope(run.RunId!, run.WorkflowId),
            waitDecisionSource: WaitDecisionSource);
    }

    /// <summary>登记驱动任务并观察至收敛（异常按在飞事实收敛 Unknown/Interrupted，绝不留 Running 僵尸）。</summary>
    private HostActionResult LaunchDrive(string workflowId, WorkflowRunner runner,
        Func<CancellationTokenSource, Task<WorkflowRunRecord>> start, string registeredMessage, string? knownRunId = null)
    {
        var cts = new CancellationTokenSource();
        var started = new TaskCompletionSource<WorkflowRunRecord>(); // Preserve synchronous fault convergence outside the Host gate.
        var entry = new DriveEntry { WorkflowId = workflowId, RunId = knownRunId, Runner = runner, Cts = cts, Task = started.Task };
        lock (_gate)
        {
            if (_shutdown)
            {
                _reservedWorkflows.Remove(workflowId);
                cts.Dispose();
                return HostActionResult.Unavailable("任务中心宿主已关闭（未启动驱动）");
            }
            // Register the full lifecycle before any synchronous prefix of start can run.
            _drives[workflowId] = entry;
            _driveCompletions.Add(entry);
        }
        _ = ObserveDriveAsync(entry);
        try
        {
            var task = start(cts);
            _ = CompleteStartedDriveAsync(task, started);
        }
        catch (Exception ex)
        {
            started.TrySetException(ex);
            return HostActionResult.Unavailable(ex.Message);
        }
        NotifyStateChanged();
        return HostActionResult.Registered(registeredMessage);
    }

    private static async Task CompleteStartedDriveAsync(Task<WorkflowRunRecord> task,
        TaskCompletionSource<WorkflowRunRecord> started)
    {
        try { started.TrySetResult(await task.ConfigureAwait(false)); }
        catch (Exception ex) { started.TrySetException(ex); }
    }

    private async Task ObserveDriveAsync(DriveEntry entry)
    {
        var terminalRunId = entry.RunId;
        var reconcileObservedTerminal = false;
        try
        {
            var run = await entry.Task.ConfigureAwait(false);
            terminalRunId = run.RunId;
            reconcileObservedTerminal = run.IsTerminal;
            TryLog($"[任务中心] 运行 {run.RunId} 终态：{run.State}");
        }
        catch (Exception ex)
        {
            // 一轮 B5 + 二轮 I1/I4：驱动异常收敛——未决外部事实→Unknown（结果不可考），否则→Interrupted（等价崩溃语义，可显式恢复）
            ConvergeDriveException(entry, ex);
            try { reconcileObservedTerminal = terminalRunId is not null && _runs.Load(terminalRunId)?.IsTerminal == true; }
            catch (Exception) { /* Preserve the unreadable record for explicit recovery. */ }
            TryLog($"[任务中心] 运行驱动异常（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            lock (_gate)
            {
                _drives.Remove(entry.WorkflowId);
                _reservedWorkflows.Remove(entry.WorkflowId);
            }
            try
            {
                // A parked/nonterminal drive does not own a later explicit Stop transaction.
                if (reconcileObservedTerminal)
                    await MarkAdmissionTerminalIfAnyAsync(terminalRunId).ConfigureAwait(false);
            }
            finally
            {
                lock (_gate) _driveCompletions.Remove(entry);
                entry.Cts.Dispose();
                entry.Completion.TrySetResult();
                NotifyStateChanged();
            }
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
                TryLog($"[任务中心] 驱动异常收敛失败（{cex.GetType().Name}）：{cex.Message}——记录保持非终态，下次启动恢复扫描兜底");
                return;
            }
        }
        TryLog($"[任务中心] 驱动异常收敛反复修订冲突（{entry.RunId ?? entry.WorkflowId}）——记录保持非终态，已留痕，下次启动恢复扫描兜底");
    }

}
