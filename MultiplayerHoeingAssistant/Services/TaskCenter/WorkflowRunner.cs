using System.Collections.Concurrent;
using System.IO;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>节点执行提交请求（流程只持类型化引用；收尾抑制标记随调用传递，D10）。</summary>
public sealed record WorkflowSubmitRequest(
    WorkflowRunRecord Run,
    WorkflowNodeOccurrence Occurrence,
    WorkflowNode Node,
    bool SuppressConfigCompletionAction);

/// <summary>提交受理结果（accepted/rejected/unknown 与终态分开，§7.1）。
/// R4.8 一轮 B1 三态化：Uncertain=受理与否不可考（传输异常/回执畸形/对账未命中/无法解释的 already_executed）——
/// 引擎走 Unknown 停驻（游标不推进、ObservedTerminal 保持空、禁止自动重跑），绝不按拒绝/失败推进；
/// Rejected 仅限可证实的未受理（本地校验失败或对端副作用前协议拒绝）。不猜成功，也不猜失败/拒绝。</summary>
public sealed record BoundarySubmitResult(bool Accepted, string? JobId, string? RejectReason, bool Uncertain = false)
{
    public static BoundarySubmitResult AcceptedWith(string jobId) => new(true, jobId, null);
    public static BoundarySubmitResult Rejected(string reason) => new(false, null, reason);
    public static BoundarySubmitResult UnknownWith(string reason) => new(false, null, reason, Uncertain: true);
}

/// <summary>边界观察终态（R4.8 一轮 B1/I3 结构化：远端原词与本地查询不可考分开）。
/// Terminal=远端终态原词（succeeded/failed/cancelled/skipped/rejected；null=未观察到）；
/// Uncertain=true 时调用方走 Unknown 停驻，不得经词汇映射落 failed；
/// Reason/ErrorCode 受控原因上 UI。</summary>
public sealed record BoundaryTerminalResult(string? Terminal, bool Uncertain, string? Reason, string? ErrorCode = null)
{
    public static BoundaryTerminalResult Observed(string terminal, string? reason = null, string? errorCode = null)
        => new(terminal, false, reason, errorCode);
    public static BoundaryTerminalResult UncertainWith(string reason) => new(null, true, reason);
}

/// <summary>
/// 执行边界（R4.5 可测试接缝）：流程引擎 → BGI 单项/配置组/整龙能力的唯一出口。
/// 生产实现经 ext.task.start（严格合同）+ ext.job.status 终态查询；测试用假实现。
/// 等待不占槽位由引擎保证：等待期间不调用本接口、不提交任何作业。
/// </summary>
public interface IWorkflowExecutionBoundary
{
    /// <summary>执行端 task.single.native 能力实况（D4 预检输入）。</summary>
    bool SingleNativeSupported { get; }

    /// <summary>执行端 execution.suppressConfigCompletionAction 能力实况（B6/E4'；缺省 true=测试接缝免接线，生产按 capability 实况）。</summary>
    bool SuppressConfigCompletionSupported => true;

    /// <summary>提交节点执行（调用前引擎已持久化提交意图，D11）。</summary>
    Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct);

    /// <summary>等待作业终态（R4.8 一轮 B3 纯观察：取消只终止等待，绝不再发远端取消——取消走 RequestCancelAsync）。</summary>
    Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct);

    /// <summary>请求远端取消在飞作业（R4.8 一轮 B3：best-effort，应答不代表清理完成，终态以 AwaitTerminalAsync 观察为准）。
    /// 默认 no-op（测试假实现免接线）；生产实现 = ext.task.cancel（ownedOnly=v1）。</summary>
    Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
}

/// <summary>前置结果状态（R4.6 B3 结构化：Unknown 不按普通失败续跑，走保守路径）。</summary>
public enum PrerequisiteStatus
{
    /// <summary>放行资源执行。</summary>
    Proceed,
    /// <summary>真实失败（受控原因）。</summary>
    Failed,
    /// <summary>受理即拒（能力/队列/合同）。</summary>
    Rejected,
    /// <summary>取消（远端已确认——显式跳过确认链走完）。</summary>
    Cancelled,
    /// <summary>结果不确定（禁止续跑/重发，标 Unknown 待对账）。</summary>
    Unknown,
}

/// <summary>前置结果（R4.6 B3 结构化；JobId 受理即回报，供引擎落盘 submitted 事实）。</summary>
public sealed record PrerequisiteResult(PrerequisiteStatus Status, string? Reason, string? JobId = null)
{
    public static readonly PrerequisiteResult ProceedInstance = new(PrerequisiteStatus.Proceed, null);
    public bool Proceed => Status == PrerequisiteStatus.Proceed;
    public static PrerequisiteResult FailedWith(string reason) => new(PrerequisiteStatus.Failed, reason);
}

/// <summary>前置策略适配器（D8：账号/兑换等前置动作的受控执行出口；生产实现 = BgiWorkflowPrerequisiteAdapter）。</summary>
public interface IWorkflowPrerequisiteAdapter
{
    /// <summary>支持的前置策略类型（R4.6 I1 能力协商；缺省=全部——测试假实现免接线；生产按 ext capability 实况）。</summary>
    IReadOnlySet<string> SupportedKinds => WorkflowKindCatalog.StrategyKinds;

    /// <summary>执行前置策略；事实绑定 run/节点出现/attempt（意图记录由引擎先行落盘）。</summary>
    Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
        WorkflowNodeOccurrence occurrence, CancellationToken ct);

    /// <summary>恢复对账（R4.6 B2：意图/在飞记录先查远端权威终态；查不到=Unknown，不盲目重发）。默认 Unknown（保守）。</summary>
    Task<PrerequisiteResult> ReconcileAsync(PrerequisiteActionRecord record, CancellationToken ct)
        => Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Unknown, "适配器不支持对账", record.JobId));

    /// <summary>前置期显式跳过的远端取消确认（B4 同构；确认超时=Unknown）。默认直接确认（测试假实现无远端）。</summary>
    Task<PrerequisiteResult> ConfirmCancellationAsync(PrerequisiteActionRecord record, CancellationToken ct)
        => Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Cancelled, null, record.JobId));
}

/// <summary>收尾执行结果（R4.6 E3'：executed / rejected / unknown / cancelled；发送 ≠ 完成）。</summary>
public sealed record TerminalExecutionResult(string State, string? JobId, string? Reason)
{
    public static TerminalExecutionResult Executed(string? jobId) => new("executed", jobId, null);
    public static TerminalExecutionResult RejectedWith(string? reason) => new("rejected", null, reason);
    public static TerminalExecutionResult UnknownWith(string? jobId, string? reason) => new("unknown", jobId, reason);
    public static TerminalExecutionResult CancelledWith(string? jobId, string? reason) => new("cancelled", jobId, reason);
}

/// <summary>终止动作执行器（D10：仅流程成功边界调用；生产实现 = BgiWorkflowTerminalExecutor）。</summary>
public interface IWorkflowTerminalExecutor
{
    /// <summary>支持的收尾类型（缺省=全部——测试假实现免接线；生产按 ext capability 实况）。</summary>
    IReadOnlySet<string> SupportedKinds => WorkflowKindCatalog.TerminalKinds;

    /// <summary>执行收尾动作；生产实现受理即持久化 submitted 事实（jobId），再等待 executed。</summary>
    Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct);
}

/// <summary>显式运行动作（锚点 2：立即生效走显式动作，不硬切执行中叶子）。</summary>
public enum WorkflowRunAction
{
    /// <summary>停止流程（终态 Cancelled；不触发收尾，D10）。</summary>
    Stop,
    /// <summary>跳过当前节点（绑定请求时出现身份；远端取消确认后推进，B4）。</summary>
    SkipCurrent,
    /// <summary>重载流程定义（运行中改流：新修订在下一节点边界生效）。</summary>
    ReloadDefinition,
    /// <summary>暂停（≠停止；节点边界生效，保留等待记录；显式 ResumeAsync 恢复）。</summary>
    Pause,
}

/// <summary>引擎选项（失败策略/时钟/延时工厂/确认超时——测试可注入，全计时可取消）。</summary>
public sealed class WorkflowRunnerOptions
{
    /// <summary>节点失败/拒绝后是否继续后续节点（D12；默认 false=停止流程，保守防假成功续跑）。</summary>
    public bool ContinueOnNodeFailure { get; init; }

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>可取消延时（测试用手动时钟快进；生产 Task.Delay）。</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    /// <summary>显式跳过后确认远端终态的超时（B4：超时 = cancelUnconfirmed → Unknown，不猜成功）。</summary>
    public TimeSpan SkipConfirmTimeout { get; init; } = TimeSpan.FromSeconds(15);

    /// <summary>日志出口（留痕纪律；不含敏感账号字段）。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// 槲寄生 · 任务中心——WorkflowRunner / Reconciler（R4.5，R4 分解 D7/D11/D12 + ASTRA 二轮处置）。
/// 单运行单驱动循环（串行状态转换）：所有触发（节点终态/动作队列/修订检查/定时唤醒）
/// 统一进入驱动循环边界处理，不并发推进。
/// ASTRA 二轮处置落点：
/// - B1 修订寻址：Store 一致性快照（文档+修订同源）；重载后按稳定出现身份从最后完成节点
///   重算后继，旧序列坐标绝不直接寻址新定义；链尾也是节点边界（追加节点会被执行）；
/// - B2 一提交一身份：幂等键按 runId+出现身份+attempt 确定性派生，提交事实不跨节点残留；
/// - B3 崩溃窗口：观察终态+节点结果+游标推进单次原子落盘；游标 null 由 TailReached 消歧；
///   ResumeAsync 显式恢复入口；聚合只信 NodeOutcomes（含恢复后历史结果重建）；
/// - B4 显式跳过：请求绑定出现身份，叶子建立空窗不丢动作，远端取消确认（超时=Unknown）后推进；
/// - B5 收尾：成功边界先落盘收尾意图（Completing+PendingCompletion 记录）再执行，收尾失败记 Failed；
/// - B9 轮次等待：新一轮边界统一执行（成功/过滤/失败续跑同路径），skipAcrossDays 公式化；
/// - 等待不占槽位：纯本地可取消延时，不持有执行锁、不提交等待作业；暂停可打断等待。
/// </summary>
public sealed class WorkflowRunner
{
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;
    private readonly IWorkflowExecutionBoundary _boundary;
    private readonly IWorkflowPrerequisiteAdapter _prerequisites;
    private readonly IWorkflowTerminalExecutor _terminal;
    private readonly WorkflowRunnerOptions _opt;
    private readonly ConcurrentDictionary<string, RunControl> _controls = new(StringComparer.Ordinal);

    /// <summary>跳过请求（B4：绑定请求时的出现身份；身份漂移则丢弃，不误伤后续节点）。</summary>
    private sealed record SkipRequest(string NodeId, int Occurrence, int LoopIteration)
    {
        public bool Matches(WorkflowNodeOccurrence occ)
            => NodeId == occ.NodeId && Occurrence == occ.Occurrence && LoopIteration == occ.LoopIteration;
    }

    private sealed class RunControl
    {
        public required CancellationTokenSource RunCts { get; init; }
        public ConcurrentQueue<WorkflowRunAction> Actions { get; } = new();

        /// <summary>LeafCts/PendingSkip 互斥（B4：Cancel/Dispose 竞争消除）。</summary>
        public object Sync { get; } = new();
        public CancellationTokenSource? LeafCts;
        public SkipRequest? PendingSkip;
        public bool PauseRequested;
        public TaskCompletionSource PauseSignal { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public WorkflowRunner(WorkflowStore workflows, RunStore runs, IWorkflowExecutionBoundary boundary,
        IWorkflowPrerequisiteAdapter prerequisites, IWorkflowTerminalExecutor terminal,
        WorkflowRunnerOptions? options = null)
    {
        _workflows = workflows;
        _runs = runs;
        _boundary = boundary;
        _prerequisites = prerequisites;
        _terminal = terminal;
        _opt = options ?? new WorkflowRunnerOptions();
    }

    /// <summary>运行控制登记实况（R4.8 一轮 I5：宿主动作结构化反馈用——Paused/终态运行无登记，动作不得无声吞）。</summary>
    public bool HasActiveControl(string runId) => _controls.ContainsKey(runId);

    /// <summary>登记显式动作（线程安全；驱动循环在下一边界消费，Stop 同时取消运行令牌）。</summary>
    public void RequestAction(string runId, WorkflowRunAction action)
    {
        if (!_controls.TryGetValue(runId, out var control)) return;
        control.Actions.Enqueue(action);
        switch (action)
        {
            case WorkflowRunAction.Stop:
                control.RunCts.Cancel();
                break;
            case WorkflowRunAction.Pause:
                control.PauseRequested = true;
                control.PauseSignal.TrySetResult();
                break;
            case WorkflowRunAction.SkipCurrent:
            {
                // B4：绑定请求时的当前游标身份；等待期间无执行中节点，不登记（等待可经 Stop 中断）
                var rec = _runs.Load(runId);
                if (rec?.State == WorkflowRunState.Waiting)
                {
                    Log(runId, "等待期间无执行中节点，跳过请求不登记。");
                    break;
                }
                control.PendingSkip = rec?.Cursor is { } cursor
                    ? new SkipRequest(cursor.NodeId, cursor.Occurrence, cursor.LoopIteration)
                    : new SkipRequest("", -1, -1); // 无游标：永不命中，仅留痕
                lock (control.Sync) control.LeafCts?.Cancel();
                break;
            }
        }
    }

    /// <summary>
    /// 启动流程运行（预检 → 建运行 → 驱动至终态/中断）。
    /// 预检失败抛 InvalidOperationException（响亮，不建运行）。
    /// </summary>
    public async Task<WorkflowRunRecord> StartAsync(string workflowId, CancellationToken ct = default)
    {
        var snapshot = _workflows.LoadSnapshot(workflowId); // 隔离文件在此响亮抛出；文档+修订同源（B1）
        var plan = new WorkflowPlan(snapshot.Document);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported); // R4.6 I1/B6：能力协商预检（含 suppress 能力）
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));

        var run = _runs.CreateRun(workflowId, snapshot.Revision);
        var control = new RunControl { RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
        if (!_controls.TryAdd(run.RunId, control))
            throw new InvalidOperationException("运行登记冲突：" + run.RunId);
        try
        {
            return await DriveAsync(run, plan, control).ConfigureAwait(false);
        }
        finally
        {
            _controls.TryRemove(run.RunId, out _);
            control.RunCts.Dispose();
        }
    }

    /// <summary>
    /// 移交受理前预检（R4.9 + ASTRA 二轮 B3：权威计划预检先于受理落盘——不可执行流程响亮拒绝、不消耗 IntentKey、不留运行记录）。
    /// 无执行副作用；与 StartExistingRunAsync/ResumeAsync 内的预检同口径（驱动入口仍各自复验，防御纵深）。
    /// </summary>
    public void PreflightStartable(WorkflowSnapshot snapshot)
    {
        var plan = new WorkflowPlan(snapshot.Document);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported);
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));
    }
    /// <summary>
    /// 驱动既有 Planned 运行（R4.9 移交受理路径：受理点=宿主 CreateRun 落盘（含移交身份），本入口不再建运行——
    /// 与 StartAsync 的「预检→建运行→驱动」不同，这里是「驱动已受理运行」。预检失败/隔离文件响亮抛出
    /// （运行记录保留——受理不撤回，由宿主侧收敛标注，R4.9 §3 步骤 4）。
    /// armTriggerLaunch=true（armTrigger 移交）：在本入口实际加载的定义快照上复验可挂载前提（七轮 重要3）——
    /// 受理→驱动重载窗口内前提失效时不激活执行（受理事实保留，运行收敛 Interrupted 可处置，绝不提交）。
    /// </summary>
    public async Task<WorkflowRunRecord> StartExistingRunAsync(string runId, CancellationToken ct = default,
        bool armTriggerLaunch = false)
    {
        var run = _runs.Load(runId) ?? throw new FileNotFoundException("运行记录不存在：" + runId);
        if (run.State is not WorkflowRunState.Planned)
            throw new InvalidOperationException($"仅 Planned 新运行可经移交入口驱动（当前 {run.State}）。");

        var snapshot = _workflows.LoadSnapshot(run.WorkflowId); // 隔离响亮抛出；文档+修订同源（B1）
        if (armTriggerLaunch && !HasMountableTrigger(snapshot.Document))
        {
            // 七轮 重要3：arm 前提必须在驱动实际使用的定义快照上复验——受理（快照 A 有 trigger.time）→驱动重载
            // （快照 B 触发器已被移除）窗口内前提失效时不得顺势进入执行：受理事实保留、运行收敛 Interrupted
            // （可处置/可显式恢复），绝不做任何提交
            run.State = WorkflowRunState.Interrupted;
            run.Note = AppendNote(run.Note,
                "armTrigger 挂载前提失效（受理后流程已无 trigger.time 触发器），未激活执行（受理事实保留）。");
            _runs.Update(run);
            return run;
        }
        var plan = new WorkflowPlan(snapshot.Document);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported); // R4.6 I1/B6：能力协商预检（含 suppress 能力）
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));

        run.WorkflowRevision = snapshot.Revision; // 与 Resume 同口径：起步对账到当前修订（受理与驱动同窗口，正常相等）
        var control = new RunControl { RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
        if (!_controls.TryAdd(run.RunId, control))
            throw new InvalidOperationException("运行登记冲突：" + runId);
        try
        {
            return await DriveAsync(run, plan, control).ConfigureAwait(false);
        }
        finally
        {
            _controls.TryRemove(run.RunId, out _);
            control.RunCts.Dispose();
        }
    }
    /// <summary>
    /// 显式恢复运行（B3 恢复入口 + D7 生命周期触发的消费侧）。
    /// 仅接受 Interrupted/Paused；Unknown 拒绝自动恢复（结果不确定，需先按幂等键+job 查询对账）。
    /// 恢复点 = 游标身份在当前修订中重定位；已完成节点不重放；历史失败结果参与聚合。
    /// </summary>
    public async Task<WorkflowRunRecord> ResumeAsync(string runId, CancellationToken ct = default)
    {
        var run = _runs.Load(runId) ?? throw new FileNotFoundException("运行记录不存在：" + runId);
        if (run.State == WorkflowRunState.Unknown)
            throw new InvalidOperationException("运行结果不确定（Unknown），需先按幂等键+job 查询对账，禁止自动恢复。");
        if (run.State is not (WorkflowRunState.Interrupted or WorkflowRunState.Paused))
            throw new InvalidOperationException($"仅 Interrupted/Paused 可显式恢复（当前 {run.State}）。");

        var snapshot = _workflows.LoadSnapshot(run.WorkflowId);
        var plan = new WorkflowPlan(snapshot.Document);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported); // R4.6 I1/B6：能力协商预检（含 suppress 能力）
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));

        run.WorkflowRevision = snapshot.Revision; // 恢复即对账到当前修订（节点边界语义）
        run.State = WorkflowRunState.Running;
        run.Note = AppendNote(run.Note, "显式恢复运行（游标身份重定位，不重放已完成节点）。");
        _runs.Update(run);

        var control = new RunControl { RunCts = CancellationTokenSource.CreateLinkedTokenSource(ct) };
        if (!_controls.TryAdd(run.RunId, control))
            throw new InvalidOperationException("运行登记冲突：" + run.RunId);
        try
        {
            return await DriveAsync(run, plan, control).ConfigureAwait(false);
        }
        finally
        {
            _controls.TryRemove(run.RunId, out _);
            control.RunCts.Dispose();
        }
    }

    private async Task<WorkflowRunRecord> DriveAsync(WorkflowRunRecord run, WorkflowPlan plan, RunControl control)
    {
        var ct = control.RunCts.Token;
        var flowFailure = false;
        try
        {
            // 顶层触发器：入口等待（不占槽位；已消费则跳过——恢复不重等，B3）
            if (!run.TriggerConsumed)
            {
                await AwaitFlowTriggersAsync(run, plan, control, ct).ConfigureAwait(false);
                if (control.PauseRequested) return Pause(run);
                run.TriggerConsumed = true;
                run.State = WorkflowRunState.Running;
                _runs.Update(run);
            }

            var occurrence = Relocate(run, plan);
            if (occurrence is not null && run.Cursor is null)
            {
                // 首个待执行节点即落盘游标（B4：跳过动作绑定出现身份对首节点同样成立）
                ApplyRelocation(run, occurrence);
                _runs.Update(run);
            }
            while (true)
            {
                ct.ThrowIfCancellationRequested();
                if (control.PauseRequested) return Pause(run);

                // 边界①：显式动作 + 修订对账（新修订按稳定身份重算后继；链尾亦对账，B1）
                (plan, occurrence) = ProcessBoundaryActions(run, plan, occurrence, control);
                if (occurrence is null) break; // 链尾（TailReached 已落盘）

                // B3-③：提交终态已观察但结果未提交（仅遗留/手工记录可达；正常路径单次写已消除窗口）
                // ——按提交事实补记结果，绝不重跑该节点
                if (run.CurrentSubmission is { ObservedTerminal: { } observed } pendingSub
                    && pendingSub.NodeId == occurrence.NodeId
                    && pendingSub.Occurrence == occurrence.Occurrence
                    && pendingSub.LoopIteration == occurrence.LoopIteration)
                {
                    var mapped = MapTerminal(observed);
                    Log(run, $"提交 {pendingSub.Key} 终态已观察（{observed}）但结果未提交，按事实补记，不重跑。");
                    CommitOutcome(run, plan, occurrence, mapped.Result, mapped.Reason, rawTerminal: observed); // I2：ObservedTerminal 已是原始词
                    if (mapped.Result == "cancelled") throw new OperationCanceledException();
                    if (mapped.Result is "failed" or "rejected" && !_opt.ContinueOnNodeFailure) break;
                    occurrence = Relocate(run, plan);
                    continue;
                }

                // B9：轮次起点等待统一在新一轮边界（成功/过滤跳过/失败续跑同路径；不占槽位）
                if (occurrence is { SequenceIndex: 0, LoopIteration: > 0 }
                    && occurrence.LoopIteration != run.LastScheduledRoundWait)
                {
                    if (!await AwaitLoopRoundStartAsync(run, plan, occurrence, control, ct).ConfigureAwait(false))
                    {
                        flowFailure = true;
                        break;
                    }
                    if (control.PauseRequested) return Pause(run);
                }

                var node = plan.NodeAt(occurrence);
                var gate = plan.EvaluateNode(occurrence, _opt.Clock(), _boundary.SingleNativeSupported);
                if (gate.Action == NodeGateAction.Skip)
                {
                    Log(run, $"节点 {occurrence.NodeId}#{occurrence.LoopIteration} 过滤跳过：{gate.Reason}");
                    CommitOutcome(run, plan, occurrence, "skippedFilter", gate.Reason);
                    occurrence = Relocate(run, plan);
                    continue;
                }
                if (gate.Action == NodeGateAction.Reject)
                {
                    Log(run, $"节点 {occurrence.NodeId}#{occurrence.LoopIteration} 响亮拒绝：{gate.Reason}");
                    CommitOutcome(run, plan, occurrence, "rejected", gate.Reason);
                    if (!_opt.ContinueOnNodeFailure) break;
                    occurrence = Relocate(run, plan);
                    continue;
                }

                // 前置策略（D8 + R4.6 结构化结果：Proceed/Failed/Rejected/Cancelled/Unknown；
                // 前置段已建立叶子令牌，SkipCurrent 可取消在飞前置并经确认链对账）
                if (await ExecutePrerequisitesAsync(run, node, occurrence, control, ct).ConfigureAwait(false) is { } prereqOutcome)
                {
                    if (prereqOutcome.Result is "unknown" or "cancelUnconfirmed")
                    {
                        // B3/四轮阻断 5：结果不确定——先置状态再 CommitOutcome（单次原子落盘，游标不推进；不触发收尾、禁止自动重跑）
                        run.State = WorkflowRunState.Unknown;
                        run.Note = AppendNote(run.Note, "前置动作结果不确定，标 Unknown（不推进、不触发收尾、禁止自动重跑）。");
                        CommitOutcome(run, plan, occurrence, prereqOutcome.Result, prereqOutcome.Reason);
                        return run;
                    }
                    CommitOutcome(run, plan, occurrence, prereqOutcome.Result, prereqOutcome.Reason);
                    if (prereqOutcome.Result == "cancelled") throw new OperationCanceledException();
                    if (prereqOutcome.Result is "failed" or "rejected" && !_opt.ContinueOnNodeFailure) break;
                    occurrence = Relocate(run, plan); // skippedUser/skippedFilter/失败续跑：推进
                    continue;
                }

                // 提交（意图先行 → 提交 → 终态；观察终态+结果+游标单次落盘，B2/B3）
                var outcome = await SubmitAndAwaitAsync(run, plan, node, occurrence, control).ConfigureAwait(false);
                if (outcome.Result is "cancelUnconfirmed" or "unknown")
                {
                    // B4/四轮阻断 5：远端终态未确认——先置状态再 CommitOutcome（单次原子落盘，游标不推进）；
                    // rawTerminal=null → ObservedTerminal 保持 null（未观察到原始词），恢复扫描按在飞标 Unknown
                    run.State = WorkflowRunState.Unknown;
                    run.Note = AppendNote(run.Note, "远端终态未确认，标 Unknown（不推进、不触发收尾、禁止自动重跑）。");
                    CommitOutcome(run, plan, occurrence, outcome.Result, outcome.Reason, rawTerminal: null);
                    return run;
                }
                CommitOutcome(run, plan, occurrence, outcome.Result, outcome.Reason, outcome.RawTerminal);
                if (outcome.Result == "cancelled") throw new OperationCanceledException(); // BGI 取消事实 → 流程取消（D12）
                if (outcome.Result is "failed" or "rejected" && !_opt.ContinueOnNodeFailure) break;
                occurrence = Relocate(run, plan);
            }

            // 流程边界：聚合判定只信 NodeOutcomes（B3：含恢复后的历史结果重建，失败不被成功覆盖）
            var hadBadOutcome = run.NodeOutcomes.Any(o =>
                o.Result is "failed" or "rejected" or "cancelled" or "cancelUnconfirmed" or "unknown"); // R4.6 B3
            if (hadBadOutcome || flowFailure)
            {
                run.State = WorkflowRunState.Failed;
                run.Note = AppendNote(run.Note, flowFailure
                    ? "循环/触发时刻计算失败，聚合结果 Failed。"
                    : "存在失败/拒绝节点，聚合结果 Failed（失败不被后续成功覆盖）。");
                _runs.Update(run);
                return run;
            }

            // B5/E3'：收尾意图先行落盘（Completing + PendingCompletion 记录），再执行收尾动作；
            // Planner 预检保证每流程至多一个收尾动作（E3'：多收尾无逐动作水位，响亮拒绝）；
            // executed 才清偿意图；rejected/异常 → Failed 保留意图；unknown/cancelled → Failed + 意图标 unknown 禁止补发。
            // D15 定案：skippedUser/skippedFilter 不算坏结果，不阻断成功边界收尾（用户显式跳过视为认可完成）。
            var terminalActions = plan.Document.Terminal;
            if (terminalActions.Count > 0)
            {
                var action = terminalActions[0]; // E3'：单动作（多收尾已被 Planner 预检响亮拒绝）
                run.State = WorkflowRunState.Completing;
                run.PendingCompletion = new PendingCompletionRecord
                {
                    ActionId = "$flow#0", // B1：收尾身份 nodeId=$flow、iteration=动作序号（0 起）
                    Kind = action.Kind,
                    Action = action.GetString("action"),
                    State = "pending",
                };
                _runs.Update(run);
                TerminalExecutionResult terminalResult;
                try
                {
                    terminalResult = await _terminal.ExecuteAsync(action, run, ct).ConfigureAwait(false);
                }
                catch (OperationCanceledException) { throw; } // B8：意图事实交外层按 pending/submitted 纪律处置
                catch (Exception ex)
                {
                    run.State = WorkflowRunState.Failed;
                    run.Note = AppendNote(run.Note,
                        $"收尾动作 {action.Kind} 执行失败：{Sanitize(ex.Message)}（流程主体成功，收尾失败不记成功；待执行收尾保留待人工处置）。");
                    _runs.Update(run);
                    return run;
                }
                switch (terminalResult.State)
                {
                    case "executed":
                        run.PendingCompletion = null; // 已证实执行——意图清偿
                        break;
                    case "rejected":
                        run.State = WorkflowRunState.Failed;
                        run.Note = AppendNote(run.Note,
                            $"收尾动作被拒绝：{Sanitize(terminalResult.Reason)}（动作未执行；意图保留待人工处置）。");
                        _runs.Update(run);
                        return run;
                    default: // unknown / cancelled：结果不可考——事实持久保留，禁止自动补发
                        run.PendingCompletion.State = "unknown";
                        run.PendingCompletion.JobId ??= terminalResult.JobId;
                        run.State = WorkflowRunState.Failed;
                        run.Note = AppendNote(run.Note,
                            $"收尾动作结果不确定（{terminalResult.State}）：{Sanitize(terminalResult.Reason)}（事实持久保留，禁止自动补发，需人工对账）。");
                        _runs.Update(run);
                        return run;
                }
            }

            run.State = WorkflowRunState.Succeeded;
            _runs.Update(run);
            return run;
        }
        catch (OperationCanceledException)
        {
            // R4.8 一轮 B3：Stop 对在飞提交 best-effort 远端取消（独立短令牌 ≤5s；运行令牌已取消不可复用）；
            // 取消未确认不猜远端已停——在飞事实（ObservedTerminal 空）原样保留，Note 标注需对账
            var inflightJob = run.CurrentSubmission is { InFlight: true, JobId: { } j } ? j : null;
            if (inflightJob is not null)
            {
                try
                {
                    using var cancelBudget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
                    await _boundary.RequestCancelAsync(inflightJob, cancelBudget.Token).ConfigureAwait(false);
                }
                catch { /* best-effort：取消请求结果不阻断本地停止 */ }
            }
            run.State = WorkflowRunState.Cancelled;
            run.Note = AppendNote(run.Note, inflightJob is not null
                ? "流程被取消（手动停止）；在飞作业已请求远端取消（未确认，事实保留，需人工对账）；不触发收尾。"
                : "流程被取消（手动停止/BGI 取消事实）；不触发收尾。");
            // D10/B8：取消不清算为可执行收尾——pending（未提交）清除意图；submitted（已受理未证实）保留事实标 unknown，禁止补发
            if (run.PendingCompletion is { } pendingCompletion)
            {
                if (pendingCompletion.State == "pending") run.PendingCompletion = null;
                else pendingCompletion.State = "unknown";
            }
            _runs.Update(run);
            return run;
        }
    }

    /// <summary>提交 + 终态等待（意图先行落盘；SkipCurrent 经叶子令牌取消 + 远端确认，B4）。
    /// RawTerminal = 边界观察到的原始线协议词（I2；未确认路径为 null）。</summary>
    private async Task<(string Result, string? Reason, string? RawTerminal)> SubmitAndAwaitAsync(
        WorkflowRunRecord run, WorkflowPlan plan, WorkflowNode node, WorkflowNodeOccurrence occurrence, RunControl control)
    {
        var ct = control.RunCts.Token;
        const int attempt = 1; // 有界重试机制挂账 R4.6+（键结构已含 attempt，身份合同就绪）
        var submission = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, occurrence.NodeId, occurrence.Occurrence,
                occurrence.LoopIteration, attempt),
            NodeId = occurrence.NodeId,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Attempt = attempt,
        };
        _runs.RecordIntent(run, submission); // 提交意图先行（B2/B3：崩溃后按意图对账，不重跑）

        // B6/E4' 定案：任务中心提交固定 suppress=true（与流程是否声明 terminal 无关；原生手动入口缺省 false 不变）
        var submit = await _boundary.SubmitAsync(
            new WorkflowSubmitRequest(run, occurrence, node, SuppressConfigCompletionAction: true), ct)
            .ConfigureAwait(false);
        if (submit.Uncertain)
        {
            // R4.8 一轮 B1：受理与否不可考——Intent 保持 Submitted（发送已尝试事实），走 Unknown 停驻（调用点）；
            // 不按拒绝推进、不改游标；ObservedTerminal 保持空 = 在飞事实保留，恢复扫描按在飞标 Unknown
            // [R5.2 G6 会诊] **不得降级已确认的受理事实**：边界可能已把本轮落盘为 Accepted（含 jobId），
            // 受理事实是接管/恢复依据，只允许增强不允许回退——否则后续异常会被当作「未受理」处理。
            submission.Intent = submission.Intent == SubmitIntentState.Accepted
                ? SubmitIntentState.Accepted
                : SubmitIntentState.Submitted;
            return ("unknown", "提交结果不可考：" + Sanitize(submit.RejectReason), null);
        }
        if (!submit.Accepted)
        {
            // 同理：已 Accepted 的提交不得因本层拿到 Rejected 而回退（受理事实优先，冲突留待对账）。
            // 且此时**不得按「确定拒绝」推进游标**（会记拒绝结果并可能继续下一节点）——冲突一律按 Unknown 停驻。
            if (submission.Intent == SubmitIntentState.Accepted)
                return ("unknown", "提交被拒但已存在受理事实（冲突，保守 Unknown 留待对账）："
                                   + Sanitize(submit.RejectReason), null);
            submission.Intent = SubmitIntentState.Rejected;
            return ("rejected", "提交被拒绝：" + Sanitize(submit.RejectReason), null);
        }
        submission.Intent = SubmitIntentState.Accepted;
        submission.JobId = submit.JobId;
        _runs.Update(run); // 受理事实落盘（提交仍在飞：ObservedTerminal 未填写）

        CancellationTokenSource leaf;
        lock (control.Sync)
        {
            leaf = CancellationTokenSource.CreateLinkedTokenSource(ct);
            control.LeafCts = leaf;
            if (control.PendingSkip is { } skip)
            {
                control.PendingSkip = null;
                if (skip.Matches(occurrence)) leaf.Cancel(); // B4：空窗到达的跳过在叶子建立时生效
                else Log(run, "过期跳过动作已丢弃（出现身份漂移，不误伤后续节点）。");
            }
        }
        try
        {
            BoundaryTerminalResult terminal;
            try
            {
                terminal = await _boundary.AwaitTerminalAsync(submit.JobId!, leaf.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 叶子被取消（显式跳过）：远端取消确认后再推进（B4 确认阶段；Stop 会先取消运行令牌）
                return await ConfirmSkipAsync(run, submission, ct).ConfigureAwait(false);
            }
            if (terminal.Uncertain)
            {
                // R4.8 一轮 B1：终态查询不可考——Unknown 停驻（调用点），rawTerminal=null（ObservedTerminal 保持空）
                return ("unknown", "终态查询不可考：" + Sanitize(terminal.Reason), null);
            }
            var mapped = MapTerminal(terminal.Terminal!);
            return (mapped.Result, mapped.Reason ?? terminal.Reason, terminal.Terminal); // I2：原始词随结果返回，ObservedTerminal 只存它
        }
        finally
        {
            lock (control.Sync) control.LeafCts = null;
            leaf.Dispose();
        }
    }

    /// <summary>显式跳过确认（B4：请求跳过→取消中→已确认/未知；未确认不得当成功推进）。
    /// 第三元 = 确认阶段观察到的原始线协议词（I2；超时未观察到 = null）。</summary>
    private async Task<(string Result, string? Reason, string? RawTerminal)> ConfirmSkipAsync(
        WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
    {
        // R4.8 一轮 B3：取消与观察分离——先请求远端取消一次（独立有界令牌 ≤5s，best-effort 忽略结果），
        // 再纯观察确认；AwaitTerminalAsync 不再承担取消副作用（确认超时不重发取消，15s 预算即实际退出上界）
        try
        {
            using var cancelBudget = CancellationTokenSource.CreateLinkedTokenSource(ct);
            cancelBudget.CancelAfter(TimeSpan.FromSeconds(5));
            await _boundary.RequestCancelAsync(submission.JobId!, cancelBudget.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch { /* best-effort：取消请求失败不阻断确认观察，终态以观察为准 */ }

        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opt.SkipConfirmTimeout);
        try
        {
            var terminal = await _boundary.AwaitTerminalAsync(submission.JobId!, timeout.Token).ConfigureAwait(false);
            if (terminal.Uncertain)
                return ("cancelUnconfirmed", $"跳过请求后远端终态不可考（{Sanitize(terminal.Reason)}）", null);
            return terminal.Terminal switch
            {
                "cancelled" => ("skippedUser", "显式跳过当前节点（远端取消已确认）", terminal.Terminal),
                // I2：显式跳过意图与远端正常跳过竞态——如实记 skippedFilter（不计 skippedUser；两者均不阻断收尾，D15）
                "skipped" => ("skippedFilter", "跳过请求到达时远端已正常跳过（来源保留，不计入显式跳过）", terminal.Terminal),
                "succeeded" => ("succeeded", "跳过请求到达时节点已完成（留痕，不算跳过）", terminal.Terminal),
                var t => ("failed", $"跳过请求后观察到意外终态 {Sanitize(t)}", terminal.Terminal),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("cancelUnconfirmed", $"跳过请求后 {_opt.SkipConfirmTimeout.TotalSeconds:0}s 内远端终态未确认", null);
        }
    }

    /// <summary>
    /// 边界终态词汇 → 节点结果（R4.6 I2：原始线协议词与归一化业务词分开识别——ObservedTerminal 只存原始词，
    /// 恢复回流（B3-③）经本映射不会把 skippedUser/skippedFilter 误判 failed；未知词汇按 failed，保守不猜成功）。
    /// </summary>
    private static (string Result, string? Reason) MapTerminal(string terminal)
        => terminal switch
        {
            "succeeded" => ("succeeded", null),
            "cancelled" => ("cancelled", "BGI 侧取消事实"),
            "skipped" => ("skippedFilter", "BGI 侧正常跳过（远端词表 skipped；D15 不阻断收尾）"),
            // 归一化业务词恒等映射（仅恢复回流路径可达）
            "skippedUser" => ("skippedUser", null),
            "skippedFilter" => ("skippedFilter", null),
            "failed" => ("failed", null),
            "rejected" => ("rejected", null),
            var t => ("failed", $"作业终态 {t}"),
        };

    /// <summary>
    /// 前置策略执行（D8 + R4.6 E2-8'/B2/B3）：逐策略实例意图先行落盘（PrerequisiteActionRecord，发送前）；
    /// 同键成功事实跳过（崩溃恢复不重发）；在飞/未知记录先对账（查不到不盲目重发）；
    /// 前置段建立叶子令牌（SkipCurrent 绑定出现身份取消在飞前置，远端取消确认链同 B4）。
    /// 返回 null = 全部放行；否则 (结果词, 原因) 由调用点按结构化结果处置。
    /// </summary>
    private async Task<(string Result, string? Reason)?> ExecutePrerequisitesAsync(WorkflowRunRecord run, WorkflowNode node,
        WorkflowNodeOccurrence occurrence, RunControl control, CancellationToken ct)
    {
        if (node.Strategies.All(s => s.Kind == "condition.weekdays")) return null;

        CancellationTokenSource leaf;
        lock (control.Sync)
        {
            leaf = CancellationTokenSource.CreateLinkedTokenSource(ct);
            control.LeafCts = leaf;
            if (control.PendingSkip is { } skip)
            {
                control.PendingSkip = null;
                if (skip.Matches(occurrence)) leaf.Cancel(); // 空窗到达的跳过在叶子建立时生效
                else Log(run, "过期跳过动作已丢弃（出现身份漂移，不误伤后续节点）。");
            }
        }
        try
        {
            for (var i = 0; i < node.Strategies.Count; i++)
            {
                var strategy = node.Strategies[i];
                if (strategy.Kind == "condition.weekdays") continue; // 闸门已评估

                // E2-8' 身份来源：redeemCode 缺 uid 时注入同节点 prerequisite.account 的 uid（均无则适配器响亮失败；Planner 预检已拦截）
                var effective = strategy;
                if (strategy.Kind == "prerequisite.redeemCode" && string.IsNullOrWhiteSpace(strategy.GetString("uid")))
                {
                    var siblingUid = node.Strategies.FirstOrDefault(s => s.Kind == "prerequisite.account")?.GetString("uid");
                    if (!string.IsNullOrWhiteSpace(siblingUid))
                    {
                        var merged = strategy.Params is not null
                            ? new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>(strategy.Params)
                            : new System.Collections.Generic.Dictionary<string, System.Text.Json.JsonElement>();
                        merged["uid"] = System.Text.Json.JsonSerializer.SerializeToElement(siblingUid);
                        effective = new WorkflowStrategy { Kind = strategy.Kind, Params = merged };
                    }
                }
                var accountKey = RunStore.DeriveAccountKey(effective.GetString("uid"));

                // 四轮阻断 3：完整动作身份比对（出现身份 + 策略索引 + 操作类型 + 账号标识哈希）
                var record = run.PrerequisiteActions.FirstOrDefault(r =>
                    r.Matches(occurrence.NodeId, occurrence.Occurrence, occurrence.LoopIteration, 1, i,
                        effective.Kind, accountKey));
                if (record is { State: PrerequisiteActionState.Succeeded })
                    continue; // 同键成功事实（游戏态事实跨纪元有效：切号/兑换效果不随 BGI 重启消失；同 attempt 恢复不重发）
                if (record is { State: PrerequisiteActionState.Failed or PrerequisiteActionState.Cancelled })
                {
                    // 四轮阻断 3/I4：既有终态事实不自动重试（新执行尝试 R4.6 不开放；attempt 恒 1，需人工处置）
                    return (record.State == PrerequisiteActionState.Cancelled ? "cancelled" : "failed",
                        $"前置策略 {effective.Kind} 存在既有{record.State}终态事实（不自动重试，需人工处置）：{record.Reason}");
                }
                if (record is { State: PrerequisiteActionState.Intent or PrerequisiteActionState.Submitted or PrerequisiteActionState.Unknown })
                {
                    // 恢复对账：先查远端权威终态，查不到不盲目重发（B2）；叶子令牌可中断对账（四轮阻断 1）
                    PrerequisiteResult reconciled;
                    try
                    {
                        reconciled = await _prerequisites.ReconcileAsync(record, leaf.Token).ConfigureAwait(false);
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        // 对账期叶子取消 = 显式跳过：同走远端取消确认链
                        var confirmed0 = await _prerequisites.ConfirmCancellationAsync(record, ct).ConfigureAwait(false);
                        record.State = confirmed0.Status == PrerequisiteStatus.Cancelled
                            ? PrerequisiteActionState.Cancelled : PrerequisiteActionState.Unknown;
                        record.Reason = Sanitize(confirmed0.Reason);
                        _runs.Update(run);
                        return confirmed0.Status == PrerequisiteStatus.Cancelled
                            ? ("skippedUser", "前置对账期显式跳过（远端取消已确认）")
                            : ("cancelUnconfirmed", $"前置对账期跳过后远端终态未确认：{Sanitize(confirmed0.Reason)}");
                    }
                    record.State = reconciled.Status switch
                    {
                        PrerequisiteStatus.Proceed => PrerequisiteActionState.Succeeded,
                        PrerequisiteStatus.Cancelled => PrerequisiteActionState.Cancelled,
                        PrerequisiteStatus.Unknown => PrerequisiteActionState.Unknown,
                        _ => PrerequisiteActionState.Failed,
                    };
                    record.Reason = Sanitize(reconciled.Reason);
                    _runs.Update(run);
                    if (record.State == PrerequisiteActionState.Succeeded) continue;
                    if (record.State == PrerequisiteActionState.Unknown)
                        return ("unknown", $"前置策略 {effective.Kind} 结果不确定（对账未决）：{Sanitize(reconciled.Reason)}");
                    return (record.State == PrerequisiteActionState.Cancelled ? "cancelled" : "failed",
                        $"前置策略 {effective.Kind}（对账终态）：{Sanitize(reconciled.Reason)}");
                }

                // 意图先行落盘（发送前；E2-8'）
                record = new PrerequisiteActionRecord
                {
                    NodeId = occurrence.NodeId,
                    Occurrence = occurrence.Occurrence,
                    LoopIteration = occurrence.LoopIteration,
                    Attempt = 1,
                    StrategyIndex = i,
                    Kind = effective.Kind,
                    AccountKey = accountKey,
                    State = PrerequisiteActionState.Intent,
                    RecordedAt = _opt.Clock(),
                };
                run.PrerequisiteActions.Add(record);
                _runs.Update(run);

                PrerequisiteResult result;
                try
                {
                    result = await _prerequisites.ExecuteAsync(effective, run, occurrence, leaf.Token).ConfigureAwait(false);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 叶子取消 = 显式跳过：远端取消确认链（确认超时 = Unknown，不猜成功）
                    var confirmed = await _prerequisites.ConfirmCancellationAsync(record, ct).ConfigureAwait(false);
                    record.State = confirmed.Status == PrerequisiteStatus.Cancelled
                        ? PrerequisiteActionState.Cancelled : PrerequisiteActionState.Unknown;
                    record.Reason = Sanitize(confirmed.Reason);
                    _runs.Update(run);
                    return confirmed.Status == PrerequisiteStatus.Cancelled
                        ? ("skippedUser", "前置期显式跳过（远端取消已确认）")
                        : ("cancelUnconfirmed", $"前置期跳过后远端终态未确认：{Sanitize(confirmed.Reason)}");
                }

                // 终态与结果同写（引擎事务）
                record.State = result.Status switch
                {
                    PrerequisiteStatus.Proceed => PrerequisiteActionState.Succeeded,
                    PrerequisiteStatus.Cancelled => PrerequisiteActionState.Cancelled,
                    PrerequisiteStatus.Unknown => PrerequisiteActionState.Unknown,
                    _ => PrerequisiteActionState.Failed,
                };
                record.Reason = Sanitize(result.Reason);
                record.JobId ??= result.JobId;
                _runs.Update(run);

                switch (result.Status)
                {
                    case PrerequisiteStatus.Proceed: continue;
                    case PrerequisiteStatus.Rejected:
                        return ("rejected", $"前置策略 {effective.Kind} 被拒绝：{Sanitize(result.Reason)}");
                    case PrerequisiteStatus.Cancelled:
                        return ("cancelled", $"前置策略 {effective.Kind} 被取消：{Sanitize(result.Reason)}");
                    case PrerequisiteStatus.Unknown:
                        return ("unknown", $"前置策略 {effective.Kind} 结果不确定：{Sanitize(result.Reason)}");
                    default:
                        return ("failed", $"前置策略 {effective.Kind} 未通过：{Sanitize(result.Reason)}");
                }
            }
            return null;
        }
        finally
        {
            lock (control.Sync) control.LeafCts = null;
            leaf.Dispose();
        }
    }

    /// <summary>可挂载触发器判定（armTrigger 语义，宿主受理预验与驱动起步复验共用同一定义——七轮 重要3 单一事实源）。
    /// 四轮 重要5 收窄：**仅 trigger.time 入口等待才算可挂载**——结构性循环首轮立即执行不算（AwaitLoopRoundStartAsync
    /// 只在 LoopIteration&gt;0 生效），若承认 loop 可挂载，arm 会绕过 start 应有的混用/快照守卫却立即提交。</summary>
    internal static bool HasMountableTrigger(WorkflowDocument doc)
        => doc.Triggers.Any(t => t.Kind == "trigger.time");

    /// <summary>I3：持久化备注脱敏——长数字串（UID 形态）打码；受控原因码+脱敏摘要，不存原始敏感面。</summary>
    internal static string Sanitize(string? text)
        => string.IsNullOrEmpty(text) ? "" : System.Text.RegularExpressions.Regex.Replace(text, "\\d{5,}", "***");

    /// <summary>顶层触发器等待（多触发器取最近；未知触发器响亮失败；暂停可打断）。</summary>
    private async Task AwaitFlowTriggersAsync(WorkflowRunRecord run, WorkflowPlan plan, RunControl control, CancellationToken ct)
    {
        if (plan.Document.Triggers.Count == 0) return;
        DateTimeOffset? earliest = null;
        foreach (var trigger in plan.Document.Triggers)
        {
            var next = WorkflowTriggerSchedule.NextFire(trigger, _opt.Clock(), out var reason)
                ?? throw new InvalidOperationException("触发器不可用：" + reason);
            earliest = earliest is null || next < earliest ? next : earliest;
        }
        run.State = WorkflowRunState.Waiting;
        _runs.Update(run);
        await WaitAsync(run, "trigger.time", earliest!.Value, control, ct).ConfigureAwait(false);
    }

    /// <summary>
    /// 轮次起点等待（B9：新一轮边界统一入口；skipAcrossDays 公式化，见 WorkflowLoopSchedule）。
    /// 返回 false = 时刻计算失败（流程级失败）。暂停打断时不记 LastScheduledRoundWait（恢复重排本轮）。
    /// </summary>
    private async Task<bool> AwaitLoopRoundStartAsync(WorkflowRunRecord run, WorkflowPlan plan,
        WorkflowNodeOccurrence occurrence, RunControl control, CancellationToken ct)
    {
        var loop = plan.Document.Loop;
        if (loop is null) return true; // LoopIteration>0 蕴含循环定义；防御性放行
        var next = WorkflowLoopSchedule.NextRoundStart(loop, _opt.Clock(), out var reason);
        if (next is null)
        {
            Log(run, "循环时刻计算失败：" + reason);
            return false;
        }
        if (next.Value > _opt.Clock())
        {
            await WaitAsync(run, "loop.scheduled", next.Value, control, ct).ConfigureAwait(false);
            if (control.PauseRequested) return true;
        }
        run.LastScheduledRoundWait = occurrence.LoopIteration;
        _runs.Update(run);
        return true;
    }

    /// <summary>等待（不占槽位：纯本地可取消延时 + RunStore 等待状态持久化；暂停打断保留等待记录）。</summary>
    private async Task WaitAsync(WorkflowRunRecord run, string kind, DateTimeOffset until, RunControl control, CancellationToken ct)
    {
        run.State = WorkflowRunState.Waiting;
        run.Wait = new WaitStateRecord
        {
            Kind = kind,
            NextTriggerAt = until,
            TriggerOccurrenceId = $"{kind}:{until:yyyyMMddHHmm}",
        };
        _runs.Update(run);
        Log(run, $"进入等待（{kind}）至 {until:yyyy-MM-dd HH:mm}（不持有执行锁、不提交等待作业）");
        var delay = until - _opt.Clock();
        if (delay > TimeSpan.Zero)
        {
            // R4.8 一轮 I5：延时走独立链接令牌——暂停打断即取消遗留 delayTask（手动时钟夹具不悬挂、计时器不泄漏）
            using var delayCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            var delayTask = _opt.DelayAsync(delay, delayCts.Token);
            if (await Task.WhenAny(delayTask, control.PauseSignal.Task).ConfigureAwait(false) != delayTask)
            {
                delayCts.Cancel();
                try { await delayTask.ConfigureAwait(false); }
                catch (OperationCanceledException) { /* 暂停打断的主动取消，按暂停语义返回 */ }
                return; // 暂停打断：Wait 记录保留，恢复后按 NextTriggerAt 重排剩余
            }
            await delayTask.ConfigureAwait(false); // 传播 Stop 取消
        }
        run.Wait = null;
        run.State = WorkflowRunState.Running;
        _runs.Update(run);
    }

    /// <summary>边界动作处理：修订对账（默认节点边界生效）+ 显式动作消费。</summary>
    private (WorkflowPlan Plan, WorkflowNodeOccurrence? Occurrence) ProcessBoundaryActions(
        WorkflowRunRecord run, WorkflowPlan plan, WorkflowNodeOccurrence? occurrence, RunControl control)
    {
        var reloadRequested = false;
        while (control.Actions.TryDequeue(out var action))
        {
            switch (action)
            {
                case WorkflowRunAction.ReloadDefinition:
                    reloadRequested = true;
                    break;
                case WorkflowRunAction.SkipCurrent:
                    Log(run, "显式跳过请求已登记（绑定请求时出现身份；叶子建立即生效）。");
                    break;
                // Stop/Pause 经运行令牌/暂停标志生效，不在此消费
            }
        }

        var snapshot = _workflows.LoadSnapshot(run.WorkflowId); // 文档+修订同源（B1）
        if (!reloadRequested && snapshot.Revision == run.WorkflowRevision) return (plan, occurrence);

        var newPlan = new WorkflowPlan(snapshot.Document);
        var preflight = newPlan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds,
            _boundary.SuppressConfigCompletionSupported); // R4.6 I1：重载入口同接能力协商预检
        if (!preflight.Executable)
        {
            Log(run, "流程新修订预检未通过，沿用旧定义继续：" + string.Join("；", preflight.BlockingReasons));
            return (plan, occurrence);
        }

        // 新修订节点边界生效：从最后完成身份在新定义中重算后继（B1：插入/删除/重排不错位；
        // occurrence 为 null 时即链尾对账——新修订追加的节点会被执行）
        var relocated = RecomputeSuccessor(run, newPlan);
        run.WorkflowRevision = snapshot.Revision;
        ApplyRelocation(run, relocated);
        _runs.Update(run);
        Log(run, $"流程定义已重载（修订 {snapshot.Revision[..Math.Min(8, snapshot.Revision.Length)]}…），按稳定身份重算后继，节点边界生效。");
        return (newPlan, relocated);
    }

    /// <summary>按稳定出现身份重算后继：最后完成节点在新定义中的 Next；无完成节点取链首；锚失效按链尾（不静默重排）。</summary>
    private WorkflowNodeOccurrence? RecomputeSuccessor(WorkflowRunRecord run, WorkflowPlan plan)
    {
        var last = run.NodeOutcomes.LastOrDefault();
        if (last is null) return plan.FirstOccurrence();
        if (plan.TryLocate(last.NodeId, last.Occurrence, last.LoopIteration, out var lastOcc))
            return plan.Next(lastOcc);
        Log(run, $"最后完成身份 {last.NodeId}#{last.Occurrence} 在新修订中已消失，按链尾处理（已完成节点不重跑）。");
        return null;
    }

    /// <summary>游标 → 当前计划中的出现（恢复/推进共用；身份失效按最后完成身份重算，不回链首重跑）。</summary>
    private WorkflowNodeOccurrence? Relocate(WorkflowRunRecord run, WorkflowPlan plan)
    {
        if (run.TailReached) return null;
        if (run.Cursor is null) return plan.FirstOccurrence();
        if (plan.TryLocate(run.Cursor.NodeId, run.Cursor.Occurrence, run.Cursor.LoopIteration, out var occ))
            return occ;
        var relocated = RecomputeSuccessor(run, plan);
        ApplyRelocation(run, relocated);
        _runs.Update(run);
        return relocated;
    }

    private static void ApplyRelocation(WorkflowRunRecord run, WorkflowNodeOccurrence? relocated)
    {
        if (relocated is null)
        {
            run.Cursor = null;
            run.TailReached = true;
        }
        else
        {
            run.TailReached = false;
            run.Cursor = new WorkflowNodeCursor
            {
                NodeId = relocated.NodeId,
                Occurrence = relocated.Occurrence,
                LoopIteration = relocated.LoopIteration,
                Attempt = 1,
            };
        }
    }

    /// <summary>
    /// 结果提交（B3-①：观察终态 + 节点结果 + 游标推进单次原子落盘，无中间态窗口）。
    /// I2/四轮重要 9：ObservedTerminal 只存原始线协议词（rawTerminal），业务词与「未确认」（null）绝不写入；
    /// 四轮阻断 5：unknown/cancelUnconfirmed 游标不推进（调用方先置 Unknown 状态再进本方法，保持单次原子落盘）；
    /// I3/四轮重要 10：持久化原因统一脱敏。
    /// </summary>
    private void CommitOutcome(WorkflowRunRecord run, WorkflowPlan plan, WorkflowNodeOccurrence occurrence,
        string result, string? reason, string? rawTerminal = null)
    {
        if (rawTerminal is not null && run.CurrentSubmission is { ObservedTerminal: null } sub)
            sub.ObservedTerminal = rawTerminal; // 提交终态与结果/游标同写（仅原始线协议词）
        // G8／§12.3：结果若来自**边界观察到的权威终态**，则把「产生它的提交键＋attempt」随结果落盘——
        // 供租约侧按完整发送关联独立结清该节点 Operation（不得凭「同一出现曾有过某结果」结清另一笔责任）。
        // 仅当当前提交确实属于本次出现身份时才记录（前置/闸门/恢复路径的 CurrentSubmission 可能属别的出现）。
        var producing = run.CurrentSubmission is { } cur
                        && string.Equals(cur.NodeId, occurrence.NodeId, StringComparison.Ordinal)
                        && cur.Occurrence == occurrence.Occurrence
                        && cur.LoopIteration == occurrence.LoopIteration
            ? cur
            : null;
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = occurrence.NodeId,
            SequenceIndex = occurrence.SequenceIndex,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Result = result,
            RawTerminal = rawTerminal,
            Reason = Sanitize(reason),
            SubmissionKey = rawTerminal is not null ? producing?.Key : null,
            Attempt = rawTerminal is not null ? producing?.Attempt : null,
            // 完整发送身份（租约侧签发）：与 SubmissionKey/Attempt 同规则随结果落盘，
            // 使「节点操作独立终局」可证明该结果属于**这一笔发送**（提交键在同 attempt 多 sendSeq 间可复用）。
            AcceptedSendIdentity = rawTerminal is not null ? producing?.AcceptedSendIdentity : null,
        });
        if (result is "unknown" or "cancelUnconfirmed")
            ApplyRelocation(run, occurrence); // 结果不确定：游标留在当前出现（恢复回到本节点对账），绝不推进
        else
            ApplyRelocation(run, plan.Next(occurrence));
        _runs.Update(run);
    }

    private WorkflowRunRecord Pause(WorkflowRunRecord run)
    {
        run.State = WorkflowRunState.Paused;
        run.Note = AppendNote(run.Note, "已暂停（≠停止；修订按节点边界生效；显式 ResumeAsync 恢复）。");
        _runs.Update(run);
        return run;
    }

    private void Log(WorkflowRunRecord run, string message) => Log(run.RunId, message);

    private void Log(string runId, string message) => _opt.Log?.Invoke($"[{runId}] {message}");

    private static string AppendNote(string? note, string addition)
        => string.IsNullOrEmpty(note) ? addition : note + " | " + addition;
}
