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

/// <summary>提交受理结果（accepted/rejected 与终态分开，§7.1）。</summary>
public sealed record BoundarySubmitResult(bool Accepted, string? JobId, string? RejectReason)
{
    public static BoundarySubmitResult AcceptedWith(string jobId) => new(true, jobId, null);
    public static BoundarySubmitResult Rejected(string reason) => new(false, null, reason);
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

    /// <summary>提交节点执行（调用前引擎已持久化提交意图，D11）。</summary>
    Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct);

    /// <summary>等待作业终态（succeeded/failed/cancelled；未知不得返回 succeeded）。</summary>
    Task<string> AwaitTerminalAsync(string jobId, CancellationToken ct);
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
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds); // R4.6 I1：能力协商预检
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
        var preflight = plan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds); // R4.6 I1：能力协商预检
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
                    CommitOutcome(run, plan, occurrence, mapped.Result, mapped.Reason);
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
                    CommitOutcome(run, plan, occurrence, prereqOutcome.Result, prereqOutcome.Reason);
                    if (prereqOutcome.Result is "unknown" or "cancelUnconfirmed")
                    {
                        // B3：前置结果不确定——标 Unknown（不推进、不触发收尾、禁止自动重跑）
                        run.State = WorkflowRunState.Unknown;
                        run.Note = AppendNote(run.Note, "前置动作结果不确定，标 Unknown（不推进、不触发收尾、禁止自动重跑）。");
                        _runs.Update(run);
                        return run;
                    }
                    if (prereqOutcome.Result == "cancelled") throw new OperationCanceledException();
                    if (prereqOutcome.Result is "failed" or "rejected" && !_opt.ContinueOnNodeFailure) break;
                    occurrence = Relocate(run, plan); // skippedUser/skippedFilter/失败续跑：推进
                    continue;
                }

                // 提交（意图先行 → 提交 → 终态；观察终态+结果+游标单次落盘，B2/B3）
                var outcome = await SubmitAndAwaitAsync(run, plan, node, occurrence, control).ConfigureAwait(false);
                CommitOutcome(run, plan, occurrence, outcome.Result, outcome.Reason);
                if (outcome.Result == "cancelUnconfirmed")
                {
                    // B4：显式跳过后远端终态未确认——结果不确定，不推进、不触发收尾、禁止自动重跑
                    run.State = WorkflowRunState.Unknown;
                    run.Note = AppendNote(run.Note, "显式跳过后远端终态未确认，标 Unknown（不推进、不触发收尾、禁止自动重跑）。");
                    _runs.Update(run);
                    return run;
                }
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
            run.State = WorkflowRunState.Cancelled;
            run.Note = AppendNote(run.Note, "流程被取消（手动停止/BGI 取消事实）；不触发收尾。");
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

    /// <summary>提交 + 终态等待（意图先行落盘；SkipCurrent 经叶子令牌取消 + 远端确认，B4）。</summary>
    private async Task<(string Result, string? Reason)> SubmitAndAwaitAsync(
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

        var submit = await _boundary.SubmitAsync(
            new WorkflowSubmitRequest(run, occurrence, node,
                plan.Document.Execution?.SuppressConfigCompletionAction == true), ct)
            .ConfigureAwait(false);
        if (!submit.Accepted)
        {
            submission.Intent = SubmitIntentState.Rejected;
            return ("rejected", "提交被拒绝：" + submit.RejectReason);
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
            string terminal;
            try
            {
                terminal = await _boundary.AwaitTerminalAsync(submit.JobId!, leaf.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                // 叶子被取消（显式跳过）：远端取消确认后再推进（B4 确认阶段；Stop 会先取消运行令牌）
                return await ConfirmSkipAsync(run, submission, ct).ConfigureAwait(false);
            }
            return MapTerminal(terminal);
        }
        finally
        {
            lock (control.Sync) control.LeafCts = null;
            leaf.Dispose();
        }
    }

    /// <summary>显式跳过确认（B4：请求跳过→取消中→已确认/未知；未确认不得当成功推进）。</summary>
    private async Task<(string Result, string? Reason)> ConfirmSkipAsync(
        WorkflowRunRecord run, WorkflowSubmission submission, CancellationToken ct)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
        timeout.CancelAfter(_opt.SkipConfirmTimeout);
        try
        {
            var terminal = await _boundary.AwaitTerminalAsync(submission.JobId!, timeout.Token).ConfigureAwait(false);
            return terminal switch
            {
                "cancelled" => ("skippedUser", "显式跳过当前节点（远端取消已确认）"),
                // I2：显式跳过意图与远端正常跳过竞态——如实记 skippedFilter（不计 skippedUser；两者均不阻断收尾，D15）
                "skipped" => ("skippedFilter", "跳过请求到达时远端已正常跳过（来源保留，不计入显式跳过）"),
                "succeeded" => ("succeeded", "跳过请求到达时节点已完成（留痕，不算跳过）"),
                var t => ("failed", $"跳过请求后观察到意外终态 {t}"),
            };
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return ("cancelUnconfirmed", $"跳过请求后 {_opt.SkipConfirmTimeout.TotalSeconds:0}s 内远端终态未确认");
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

                var record = run.PrerequisiteActions.FirstOrDefault(r =>
                    r.Matches(occurrence.NodeId, occurrence.Occurrence, occurrence.LoopIteration, 1, i));
                if (record is { State: PrerequisiteActionState.Succeeded })
                    continue; // 同键成功事实（同 attempt 恢复不重发）
                if (record is { State: PrerequisiteActionState.Intent or PrerequisiteActionState.Submitted or PrerequisiteActionState.Unknown })
                {
                    // 恢复对账：先查远端权威终态，查不到不盲目重发（B2）
                    var reconciled = await _prerequisites.ReconcileAsync(record, ct).ConfigureAwait(false);
                    record.State = reconciled.Status switch
                    {
                        PrerequisiteStatus.Proceed => PrerequisiteActionState.Succeeded,
                        PrerequisiteStatus.Cancelled => PrerequisiteActionState.Cancelled,
                        PrerequisiteStatus.Unknown => PrerequisiteActionState.Unknown,
                        _ => PrerequisiteActionState.Failed,
                    };
                    record.Reason = reconciled.Reason;
                    _runs.Update(run);
                    if (record.State == PrerequisiteActionState.Succeeded) continue;
                    if (record.State == PrerequisiteActionState.Unknown)
                        return ("unknown", $"前置策略 {strategy.Kind} 结果不确定（对账未决）：{reconciled.Reason}");
                    return (record.State == PrerequisiteActionState.Cancelled ? "cancelled" : "failed",
                        $"前置策略 {strategy.Kind}（对账终态）：{reconciled.Reason}");
                }

                // 意图先行落盘（发送前；E2-8'）
                record = new PrerequisiteActionRecord
                {
                    NodeId = occurrence.NodeId,
                    Occurrence = occurrence.Occurrence,
                    LoopIteration = occurrence.LoopIteration,
                    Attempt = 1,
                    StrategyIndex = i,
                    Kind = strategy.Kind,
                    State = PrerequisiteActionState.Intent,
                    RecordedAt = _opt.Clock(),
                };
                run.PrerequisiteActions.Add(record);
                _runs.Update(run);

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
                    record.Reason = confirmed.Reason;
                    _runs.Update(run);
                    return confirmed.Status == PrerequisiteStatus.Cancelled
                        ? ("skippedUser", "前置期显式跳过（远端取消已确认）")
                        : ("cancelUnconfirmed", $"前置期跳过后远端终态未确认：{confirmed.Reason}");
                }

                // 终态与结果同写（引擎事务）
                record.State = result.Status switch
                {
                    PrerequisiteStatus.Proceed => PrerequisiteActionState.Succeeded,
                    PrerequisiteStatus.Cancelled => PrerequisiteActionState.Cancelled,
                    PrerequisiteStatus.Unknown => PrerequisiteActionState.Unknown,
                    _ => PrerequisiteActionState.Failed,
                };
                record.Reason = result.Reason;
                record.JobId ??= result.JobId;
                _runs.Update(run);

                switch (result.Status)
                {
                    case PrerequisiteStatus.Proceed: continue;
                    case PrerequisiteStatus.Rejected:
                        return ("rejected", $"前置策略 {strategy.Kind} 被拒绝：{result.Reason}");
                    case PrerequisiteStatus.Cancelled:
                        return ("cancelled", $"前置策略 {strategy.Kind} 被取消：{result.Reason}");
                    case PrerequisiteStatus.Unknown:
                        return ("unknown", $"前置策略 {strategy.Kind} 结果不确定：{result.Reason}");
                    default:
                        return ("failed", $"前置策略 {strategy.Kind} 未通过：{result.Reason}");
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
            var delayTask = _opt.DelayAsync(delay, ct);
            if (await Task.WhenAny(delayTask, control.PauseSignal.Task).ConfigureAwait(false) != delayTask)
                return; // 暂停打断：Wait 记录保留，恢复后按 NextTriggerAt 重排剩余
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
        var preflight = newPlan.Preflight(_boundary.SingleNativeSupported, _prerequisites.SupportedKinds, _terminal.SupportedKinds); // R4.6 I1：重载入口同接能力协商预检
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

    /// <summary>结果提交（B3-①：观察终态 + 节点结果 + 游标推进单次原子落盘，无中间态窗口）。</summary>
    private void CommitOutcome(WorkflowRunRecord run, WorkflowPlan plan, WorkflowNodeOccurrence occurrence,
        string result, string? reason)
    {
        if (run.CurrentSubmission is { ObservedTerminal: null } sub)
            sub.ObservedTerminal = result; // 提交终态与结果/游标同写
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = occurrence.NodeId,
            SequenceIndex = occurrence.SequenceIndex,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Result = result,
            Reason = reason,
        });
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