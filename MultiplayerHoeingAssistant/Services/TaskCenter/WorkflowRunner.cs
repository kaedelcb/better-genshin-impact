using System.Collections.Concurrent;
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

/// <summary>前置策略适配器（D8：账号/兑换等前置动作的受控执行出口；生产实现 R4.6）。</summary>
public interface IWorkflowPrerequisiteAdapter
{
    /// <summary>执行前置策略；返回是否放行资源执行。事实绑定 run/节点出现/attempt。</summary>
    Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
        WorkflowNodeOccurrence occurrence, CancellationToken ct);
}

public sealed record PrerequisiteResult(bool Proceed, string? Reason)
{
    public static readonly PrerequisiteResult ProceedInstance = new(true, null);
}

/// <summary>终止动作执行器（D10：仅流程成功边界调用；生产实现 R4.6/R4.8）。</summary>
public interface IWorkflowTerminalExecutor
{
    Task ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct);
}

/// <summary>显式运行动作（锚点 2：立即生效走显式动作，不硬切执行中叶子）。</summary>
public enum WorkflowRunAction
{
    /// <summary>停止流程（终态 Cancelled；不触发收尾，D10）。</summary>
    Stop,
    /// <summary>跳过当前节点（当前叶子等待被取消，节点记 skippedUser，流程推进）。</summary>
    SkipCurrent,
    /// <summary>重载流程定义（运行中改流：新修订在下一节点边界生效）。</summary>
    ReloadDefinition,
}

/// <summary>引擎选项（失败策略/时钟/延时工厂——测试可注入，全计时可取消）。</summary>
public sealed class WorkflowRunnerOptions
{
    /// <summary>节点失败/拒绝后是否继续后续节点（D12；默认 false=停止流程，保守防假成功续跑）。</summary>
    public bool ContinueOnNodeFailure { get; init; }

    public Func<DateTimeOffset> Clock { get; init; } = () => DateTimeOffset.Now;

    /// <summary>可取消延时（测试用手动时钟快进；生产 Task.Delay）。</summary>
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; init; } = Task.Delay;

    /// <summary>日志出口（留痕纪律；不含敏感账号字段）。</summary>
    public Action<string>? Log { get; init; }
}

/// <summary>
/// 槲寄生 · 任务中心——WorkflowRunner / Reconciler（R4.5，R4 分解 D7/D11/D12）。
/// 单运行单驱动循环（串行状态转换）：所有触发（节点终态/动作队列/修订检查/定时唤醒）
/// 统一进入驱动循环边界处理，不并发推进。
/// - 修订对账：每节点边界比对 WorkflowStore 当前修订，变化即重载（运行中改流节点边界生效）；
/// - 提交意图先行：每次提交前 RunStore.RecordIntent 落盘（固定幂等键）；
/// - 聚合规则：任何节点 failed/rejected → 流程终态 Failed（失败不被后续成功覆盖）；
/// - 等待不占槽位：触发/轮次等待 = 纯本地可取消延时，不持有执行锁、不提交等待作业；
/// - 收尾：仅全部节点成功/过滤跳过的成功边界触发；停止/失败/拒绝/未知/等待中不触发（D10）。
/// </summary>
public sealed class WorkflowRunner
{
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;
    private readonly IWorkflowExecutionBoundary _boundary;
    private readonly IWorkflowPrerequisiteAdapter _prerequisites;
    private readonly IWorkflowTerminalExecutor _terminal;
    private readonly WorkflowRunnerOptions _opt;

    private sealed class RunControl
    {
        public required CancellationTokenSource RunCts { get; init; }
        public CancellationTokenSource? LeafCts { get; set; }
        public ConcurrentQueue<WorkflowRunAction> Actions { get; } = new();
    }

    private readonly ConcurrentDictionary<string, RunControl> _controls = new();

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
        if (action == WorkflowRunAction.Stop) control.RunCts.Cancel();
        if (action == WorkflowRunAction.SkipCurrent) control.LeafCts?.Cancel();
    }

    /// <summary>
    /// 启动流程运行（预检 → 建运行 → 驱动至终态/中断）。
    /// 预检失败抛 InvalidOperationException（响亮，不建运行）。
    /// </summary>
    public async Task<WorkflowRunRecord> StartAsync(string workflowId, CancellationToken ct = default)
    {
        var doc = _workflows.Load(workflowId); // 隔离文件在此响亮抛出
        var revision = CurrentRevision(workflowId);
        var plan = new WorkflowPlan(doc);
        var preflight = plan.Preflight(_boundary.SingleNativeSupported);
        if (!preflight.Executable)
            throw new InvalidOperationException("流程预检未通过：" + string.Join("；", preflight.BlockingReasons));

        var run = _runs.CreateRun(workflowId, revision);
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
        var hadBadOutcome = false;
        try
        {
            // 顶层触发器：入口等待（不占槽位）
            await AwaitFlowTriggersAsync(run, plan, ct).ConfigureAwait(false);

            run.State = WorkflowRunState.Running;
            _runs.Update(run);

            var occurrence = run.Cursor is null
                ? plan.FirstOccurrence()
                : new WorkflowNodeOccurrence(run.Cursor.NodeId, FindSequenceIndex(plan, run.Cursor),
                    run.Cursor.Occurrence, run.Cursor.LoopIteration);

            while (occurrence is not null)
            {
                ct.ThrowIfCancellationRequested();

                // 边界①：显式动作 + 修订对账（运行中改流节点边界生效，D7）
                plan = ProcessBoundaryActions(run, plan, control);

                var node = plan.NodeAt(occurrence);
                var gate = plan.EvaluateNode(occurrence, _opt.Clock(), _boundary.SingleNativeSupported);
                if (gate.Action == NodeGateAction.Skip)
                {
                    RecordOutcome(run, occurrence, "skippedFilter", gate.Reason);
                    Log(run, $"节点 {occurrence.NodeId}#{occurrence.LoopIteration} 过滤跳过：{gate.Reason}");
                    occurrence = Advance(run, plan, occurrence);
                    continue;
                }
                if (gate.Action == NodeGateAction.Reject)
                {
                    hadBadOutcome = true;
                    RecordOutcome(run, occurrence, "rejected", gate.Reason);
                    Log(run, $"节点 {occurrence.NodeId}#{occurrence.LoopIteration} 响亮拒绝：{gate.Reason}");
                    if (!_opt.ContinueOnNodeFailure) break;
                    occurrence = Advance(run, plan, occurrence);
                    continue;
                }

                // 前置策略（D8：适配器受控执行；不通过则按失败策略处理）
                var prereqBlock = await ExecutePrerequisitesAsync(run, node, occurrence, ct).ConfigureAwait(false);
                if (prereqBlock is not null)
                {
                    hadBadOutcome = true;
                    RecordOutcome(run, occurrence, "failed", prereqBlock);
                    if (!_opt.ContinueOnNodeFailure) break;
                    occurrence = Advance(run, plan, occurrence);
                    continue;
                }

                // 提交（意图先行 → 提交 → 终态；D11）
                var outcome = await SubmitAndAwaitAsync(run, plan, node, occurrence, control).ConfigureAwait(false);
                if (outcome.CancelledByStop) throw new OperationCanceledException();
                if (outcome.SkippedByUser)
                {
                    RecordOutcome(run, occurrence, "skippedUser", "显式跳过当前节点");
                    occurrence = Advance(run, plan, occurrence);
                    continue;
                }
                if (outcome.Result != "succeeded")
                {
                    hadBadOutcome = true;
                    RecordOutcome(run, occurrence, outcome.Result == "cancelled" ? "cancelled" : "failed", outcome.Reason);
                    if (outcome.Result == "cancelled") throw new OperationCanceledException(); // BGI 侧取消事实 → 流程取消（D12）
                    if (!_opt.ContinueOnNodeFailure) break;
                    occurrence = Advance(run, plan, occurrence);
                    continue;
                }

                RecordOutcome(run, occurrence, "succeeded", null);
                occurrence = Advance(run, plan, occurrence);

                // 结构性循环：轮次等待（不占槽位）
                if (occurrence is not null && occurrence.SequenceIndex == 0 && occurrence.LoopIteration > 0
                    && plan.Document.Loop is { } loop && loop.Mode == "scheduled")
                {
                    var next = WorkflowLoopSchedule.NextRoundStart(loop, _opt.Clock(), out var loopReason);
                    if (next is null)
                    {
                        hadBadOutcome = true;
                        Log(run, "循环时刻计算失败：" + loopReason);
                        break;
                    }
                    await WaitAsync(run, "loop.scheduled", next.Value, ct).ConfigureAwait(false);
                }
            }

            // 流程边界：聚合判定（D6）→ 收尾（D10）
            if (hadBadOutcome)
            {
                run.State = WorkflowRunState.Failed;
                run.Note = AppendNote(run.Note, "存在失败/拒绝节点，聚合结果 Failed（失败不被后续成功覆盖）。");
                _runs.Update(run);
                return run;
            }

            run.State = WorkflowRunState.Succeeded;
            _runs.Update(run);
            foreach (var action in plan.Document.Terminal)
            {
                await _terminal.ExecuteAsync(action, run, ct).ConfigureAwait(false);
            }
            run.PendingCompletionAction = null;
            _runs.Update(run);
            return run;
        }
        catch (OperationCanceledException)
        {
            run.State = WorkflowRunState.Cancelled;
            run.Note = AppendNote(run.Note, "流程被取消（手动停止/BGI 取消事实）；不触发收尾。");
            run.PendingCompletionAction = null; // D10：取消不清算为可执行收尾
            _runs.Update(run);
            return run;
        }
    }

    /// <summary>提交 + 终态等待（意图先行落盘；SkipCurrent 经叶子令牌取消）。</summary>
    private async Task<(string? Result, string? Reason, bool SkippedByUser, bool CancelledByStop)> SubmitAndAwaitAsync(
        WorkflowRunRecord run, WorkflowPlan plan, WorkflowNode node, WorkflowNodeOccurrence occurrence, RunControl control)
    {
        var ct = control.RunCts.Token;
        run.Cursor = new WorkflowNodeCursor
        {
            NodeId = occurrence.NodeId,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Attempt = run.Cursor?.Attempt ?? 1,
        };
        run.BoundResource = new BoundResourceRef
        {
            Kind = node.Kind,
            Config = node.Ref?.Config,
            TaskId = node.Ref?.TaskId,
            ConfigRevision = node.Ref?.Revision,
        };
        _runs.RecordIntent(run); // 提交意图先行（D11：崩溃后可对账，不重跑）

        var submit = await _boundary.SubmitAsync(
            new WorkflowSubmitRequest(run, occurrence, node,
                plan.Document.Execution?.SuppressConfigCompletionAction == true), ct)
            .ConfigureAwait(false);
        if (!submit.Accepted)
        {
            run.SubmitIntent = SubmitIntentState.Rejected;
            _runs.Update(run);
            return ("failed", "提交被拒绝：" + submit.RejectReason, false, false);
        }
        run.SubmitIntent = SubmitIntentState.Accepted;
        run.JobId = submit.JobId;
        _runs.Update(run);

        control.LeafCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        try
        {
            var terminal = await _boundary.AwaitTerminalAsync(submit.JobId!, control.LeafCts.Token)
                .ConfigureAwait(false);
            run.ObservedTerminal = terminal;
            _runs.Update(run);
            return (terminal, null, false, false);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested)
        {
            return (null, null, true, false); // 叶子被取消 = 显式跳过当前（Stop 会先取消运行令牌）
        }
        finally
        {
            control.LeafCts.Dispose();
            control.LeafCts = null;
        }
    }

    /// <summary>前置策略执行（未知策略类型响亮阻止；账号/兑换经适配器）。</summary>
    private async Task<string?> ExecutePrerequisitesAsync(WorkflowRunRecord run, WorkflowNode node,
        WorkflowNodeOccurrence occurrence, CancellationToken ct)
    {
        foreach (var strategy in node.Strategies)
        {
            if (strategy.Kind == "condition.weekdays") continue; // 闸门已评估
            var result = await _prerequisites.ExecuteAsync(strategy, run, occurrence, ct).ConfigureAwait(false);
            if (!result.Proceed)
                return $"前置策略 {strategy.Kind} 未通过：{result.Reason}";
        }
        return null;
    }

    /// <summary>顶层触发器等待（多触发器取最近；未知触发器响亮失败）。</summary>
    private async Task AwaitFlowTriggersAsync(WorkflowRunRecord run, WorkflowPlan plan, CancellationToken ct)
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
        await WaitAsync(run, "trigger.time", earliest!.Value, ct).ConfigureAwait(false);
    }

    /// <summary>等待（不占槽位：纯本地可取消延时 + RunStore 等待状态持久化）。</summary>
    private async Task WaitAsync(WorkflowRunRecord run, string kind, DateTimeOffset until, CancellationToken ct)
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
            await _opt.DelayAsync(delay, ct).ConfigureAwait(false);
        run.Wait = null;
        run.State = WorkflowRunState.Running;
        _runs.Update(run);
    }

    /// <summary>边界动作处理：修订对账（默认节点边界生效）+ 显式重载。</summary>
    private WorkflowPlan ProcessBoundaryActions(WorkflowRunRecord run, WorkflowPlan plan, RunControl control)
    {
        var reload = false;
        while (control.Actions.TryDequeue(out var action))
        {
            if (action == WorkflowRunAction.ReloadDefinition) reload = true;
            // Stop/SkipCurrent 经令牌生效，不在此处理
        }
        var current = CurrentRevision(run.WorkflowId);
        if (!reload && current == run.WorkflowRevision) return plan;

        // 运行中改流：新修订在节点边界生效（执行中叶子已完成到这里，无硬切）
        var doc = _workflows.Load(run.WorkflowId);
        var newPlan = new WorkflowPlan(doc);
        var preflight = newPlan.Preflight(_boundary.SingleNativeSupported);
        if (!preflight.Executable)
        {
            Log(run, "流程新修订预检未通过，沿用旧定义继续本节点边界：" + string.Join("；", preflight.BlockingReasons));
            return plan;
        }
        run.WorkflowRevision = current;
        _runs.Update(run);
        Log(run, $"流程定义已重载（修订 {current[..Math.Min(8, current.Length)]}…），节点边界生效");
        return newPlan;
    }

    private WorkflowNodeOccurrence? Advance(WorkflowRunRecord run, WorkflowPlan plan, WorkflowNodeOccurrence current)
    {
        var next = plan.Next(current);
        if (next is null)
        {
            run.Cursor = null; // 链尾：清空游标（成功边界由调用方收口）
        }
        else
        {
            run.Cursor = new WorkflowNodeCursor
            {
                NodeId = next.NodeId,
                Occurrence = next.Occurrence,
                LoopIteration = next.LoopIteration,
                Attempt = 1,
            };
        }
        _runs.Update(run);
        return next;
    }

    private void RecordOutcome(WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, string result, string? reason)
    {
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = occurrence.NodeId,
            SequenceIndex = occurrence.SequenceIndex,
            Occurrence = occurrence.Occurrence,
            LoopIteration = occurrence.LoopIteration,
            Result = result,
            Reason = reason,
        });
        _runs.Update(run);
    }

    private string CurrentRevision(string workflowId)
        => _workflows.List().FirstOrDefault(e => e.WorkflowId == workflowId)?.Revision
           ?? throw new InvalidOperationException("流程不在目录中：" + workflowId);

    private static int FindSequenceIndex(WorkflowPlan plan, WorkflowNodeCursor cursor)
    {
        // 游标 → 序列位置：按 nodeId + 出现序号定位（不按名称；找不到=定义已改，回链首由调用方处理）
        var count = -1;
        for (var i = 0; i < plan.Document.Nodes.Count; i++)
        {
            if (plan.Document.Nodes[i].NodeId != cursor.NodeId) continue;
            count++;
            if (count == cursor.Occurrence) return i;
        }
        return 0;
    }

    private void Log(WorkflowRunRecord run, string message) => _opt.Log?.Invoke($"[{run.RunId}] {message}");

    private static string AppendNote(string? note, string addition)
        => string.IsNullOrEmpty(note) ? addition : note + " | " + addition;
}
