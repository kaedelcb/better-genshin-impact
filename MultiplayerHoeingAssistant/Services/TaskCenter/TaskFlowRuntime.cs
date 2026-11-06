using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed record TaskFlowTriggerSnapshot(string RuntimeId, string WorkflowId, string RunId,
    string FlowNodeId, string StepId, string Kind, string Text, string? Note, StartupStep Step, string? TargetNodeId);

/// <summary>执行端共用的任务流程宿主。UI只消费快照；订阅、回调和撤销都属于运行实例。</summary>
public sealed class TaskFlowRuntime
{
    private readonly ConcurrentDictionary<string, Active> _active = new();
    private readonly ConcurrentDictionary<string, string> _faults = new();
    public RegisterBankService? Registers { get; set; }
    public Func<ControlStatus?>? StatusProvider { get; set; }
    public IWorkflowObservationSource? ObservationSource { get; set; }
    public Func<StartupStep, CancellationToken, Task<bool>>? ConfirmAsync { get; set; }
    public Func<StartupStep, CancellationToken, Task>? StartProgramAsync { get; set; }
    public Func<DateTimeOffset> Clock { get; set; } = () => DateTimeOffset.Now;
    public Func<TimeSpan, CancellationToken, Task> DelayAsync { get; set; } = Task.Delay;
    public event Action? Changed;

    private sealed class Active
    {
        public required TaskFlowTriggerSnapshot View;
        public required string Scope;
        public required int Round;
        public CancellationTokenSource Stop = new();
        public SemaphoreSlim Serial = new(1, 1);
        public Task Pump = Task.CompletedTask;
        public IWorkflowObservationSession? Observation;
        public string? Error;
        public bool Cancelled;
        public int Fired;
        public readonly object CallbackGate = new();
        public bool Pending;
        public bool Draining;
        public bool OnceAccepted;
        public Task Drain = Task.CompletedTask;
    }

    private const string BindingPrefix = "taskFlowBinding:";
    private const string FactPrefix = "taskFlowFact:";
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };
    private static StartupStep Copy(StartupStep step) => JsonSerializer.Deserialize<StartupStep>(JsonSerializer.Serialize(step), Json)!;
    private void Notify() { try { Changed?.Invoke(); } catch (Exception e) { Debug.WriteLine(e); } }
    public IReadOnlyList<TaskFlowTriggerSnapshot> List() => _active.Values.Select(a => a.View with
        { Note = a.Error ?? (a.Cancelled ? "撤销中" : a.View.Note) }).ToArray();

    public async Task<bool> CancelAsync(string runtimeId, CancellationToken ct = default)
    {
        if (!_active.TryGetValue(runtimeId, out var a)) return false;
        a.Cancelled = true;
        _faults[a.View.RunId] = "本次运行的触发器被手动撤销，依赖结果需核对";
        await CloseAsync(a, ct).ConfigureAwait(false);
        return true;
    }

    public string? GetError(string runId) => _faults.TryGetValue(runId, out var error) ? error : null;

    public async Task FinishRoundAsync(string runId, CancellationToken ct)
    {
        foreach (var a in _active.Values.Where(a => a.View.RunId == runId).ToArray())
            if (await FreezeAsync(a).ConfigureAwait(false) != "complete") throw new WorkflowObservationUnknownException("本轮后台观察未完整结束");
    }

    public async Task CloseRunAsync(string runId, CancellationToken ct = default)
    {
        foreach (var a in _active.Values.Where(a => a.View.RunId == runId).ToArray())
            await CloseAsync(a, ct).ConfigureAwait(false);
    }

    public async Task<bool?> ExecuteAsync(WorkflowRunRecord run, WorkflowNode node, int round,
        Action save, CancellationToken ct)
    {
        if (_faults.TryGetValue(run.RunId, out var error)) throw new WorkflowObservationUnknownException(error);
        foreach (var pair in run.ExtensionData?.Where(p => p.Key.StartsWith(BindingPrefix, StringComparison.Ordinal)) ?? [])
        {
            var binding = pair.Value.Deserialize<Binding>(Json);
            if (binding is { Step.TriggerScope: "round" } && binding.Round == round
                && !_active.Values.Any(a => a.View.RunId == run.RunId && a.View.StepId == binding.Step.Id && a.Round == round))
                throw new WorkflowObservationUnknownException("本轮后台触发器在重启或暂停期间失去连续观察，不能自动恢复为未触发");
        }
        var flow = node.Flow ?? throw new InvalidOperationException("流程定义缺失");
        var errors = TaskFlowNodeCatalog.Validate(flow);
        if (errors.Count > 0) throw new InvalidOperationException(string.Join("；", errors));
        foreach (var old in _active.Values.Where(a => a.View.RunId == run.RunId && a.Round != round).ToArray())
            await CloseAsync(old, ct).ConfigureAwait(false);
        var answer = await ExecuteChainAsync(run, node, flow.Steps, flow.ExitStepId, round, save, ct).ConfigureAwait(false);
        foreach (var a in _active.Values.Where(a => a.View.RunId == run.RunId && a.View.FlowNodeId == node.NodeId && a.Scope == "flow").ToArray())
            if (await FreezeAsync(a).ConfigureAwait(false) != "complete") throw new WorkflowObservationUnknownException("流程范围内观察未完整结束");
        return answer;
    }

    private async Task<bool?> ExecuteChainAsync(WorkflowRunRecord run, WorkflowNode node, IEnumerable<StartupStep> steps,
        string? exitId, int round, Action save, CancellationToken ct)
    {
        bool? answer = null;
        foreach (var step in steps)
        {
            if (!step.Enabled) continue;
            ct.ThrowIfCancellationRequested();
            var key = $"taskFlowStep:{node.NodeId}:{round}:{step.Id}";
            run.ExtensionData ??= new();
            if (run.ExtensionData.TryGetValue(key, out var recorded))
            {
                if (recorded.ValueKind != JsonValueKind.String || recorded.GetString() == "executing")
                    throw new WorkflowObservationUnknownException("流程动作上次结果未确认，不能重放");
                if (step.Id == exitId) answer = recorded.GetString() == "yes";
                continue;
            }
            if (step.Id == exitId)
            {
                foreach (var a in _active.Values.Where(a => a.View.RunId == run.RunId && a.View.FlowNodeId == node.NodeId && a.Scope == "flow").ToArray())
                    if (await FreezeAsync(a).ConfigureAwait(false) != "complete") throw new WorkflowObservationUnknownException("流程范围内观察未完整结束");
                EnsureFacts(run, round);
                answer = await EvaluateAsync(step, ct).ConfigureAwait(false);
                run.ExtensionData[key] = JsonSerializer.SerializeToElement(answer.Value ? "yes" : "no");
                save();
                continue;
            }
            // 副作用意图先保存；崩溃后不能再启动程序或把已确认写入当作未执行。
            run.ExtensionData[key] = JsonSerializer.SerializeToElement("executing"); save();
            if (TaskFlowNodeCatalog.IsTrigger(step.Kind))
            {
                var binding = new Binding(node.NodeId, round, Copy(step));
                var bindingKey = BindingPrefix + node.NodeId + ":" + round + ":" + step.Id;
                run.ExtensionData[bindingKey] = JsonSerializer.SerializeToElement(binding);
                if (step.TriggerScope == "task")
                    run.ExtensionData[FactPrefix + node.NodeId + ":" + round + ":" + step.Id] = JsonSerializer.SerializeToElement("pending");
                if (step.TriggerScope != "task") await ArmAsync(run, binding, null, ct).ConfigureAwait(false);
            }
            else if (step.NodeType == "condition")
                await ExecuteChainAsync(run, node, await EvaluateAsync(step, ct).ConfigureAwait(false) ? step.TrueSteps : step.FalseSteps,
                    null, round, save, ct).ConfigureAwait(false);
            else await RunStepsAsync([step], ct).ConfigureAwait(false);
            run.ExtensionData[key] = JsonSerializer.SerializeToElement("done"); save();
        }
        return answer;
    }

    private sealed record Binding(string FlowNodeId, int Round, StartupStep Step);
    private IEnumerable<Binding> Bindings(WorkflowRunRecord run, WorkflowNode node, int round) =>
        run.ExtensionData?.Where(p => p.Key.StartsWith(BindingPrefix, StringComparison.Ordinal))
            .Select(p => p.Value.Deserialize<Binding>(Json)).OfType<Binding>()
            .Where(b => b.Round == round && b.Step.TriggerScope == "task" && b.Step.TargetNodeId == node.NodeId) ?? [];

    public async Task<IAsyncDisposable> BeginTaskAsync(WorkflowRunRecord run, WorkflowNode node,
        WorkflowNodeOccurrence occurrence, int attempt, Action save, CancellationToken ct)
    {
        var mounted = new List<Active>();
        try
        {
            foreach (var binding in Bindings(run, node, occurrence.LoopIteration).ToArray())
            {
                var fact = FactPrefix + binding.FlowNodeId + ":" + occurrence.LoopIteration + ":" + binding.Step.Id;
                var attemptKey = fact + ":attempt";
                if (run.ExtensionData!.TryGetValue(fact, out var priorState) && priorState.ValueKind == JsonValueKind.String && priorState.GetString() == "armed"
                    && run.ExtensionData.TryGetValue(attemptKey, out var priorAttempt) && priorAttempt.GetInt32() == attempt)
                    throw new WorkflowObservationUnknownException("该次任务曾已开始监听但结束结果丢失，禁止只观察重启后的片段再判未命中");
                run.ExtensionData[attemptKey] = JsonSerializer.SerializeToElement(attempt);
                run.ExtensionData[fact] = JsonSerializer.SerializeToElement("armed"); save();
                await RunStepsAsync(binding.Step.BeforeAttemptSteps, ct).ConfigureAwait(false);
                mounted.Add(await ArmAsync(run, binding, node.NodeId + ":" + occurrence.Occurrence + ":" + attempt, ct).ConfigureAwait(false));
            }
            return new TaskScope(async () =>
            {
                foreach (var a in mounted)
                {
                    var state = await FreezeAsync(a).ConfigureAwait(false);
                    run.ExtensionData![FactPrefix + a.View.FlowNodeId + ":" + occurrence.LoopIteration + ":" + a.View.StepId] = JsonSerializer.SerializeToElement(state);
                    save();
                }
            });
        }
        catch
        {
            foreach (var a in mounted) await CloseAsync(a, CancellationToken.None).ConfigureAwait(false);
            throw;
        }
    }

    private void EnsureFacts(WorkflowRunRecord run, int round)
    {
        if (_faults.TryGetValue(run.RunId, out var error)) throw new WorkflowObservationUnknownException(error);
        foreach (var pair in run.ExtensionData?.Where(p => p.Key.StartsWith(BindingPrefix, StringComparison.Ordinal)) ?? [])
        {
            var binding = pair.Value.Deserialize<Binding>(Json);
            if (binding is null || binding.Round != round || binding.Step.TriggerScope != "task") continue;
            var key = FactPrefix + binding.FlowNodeId + ":" + round + ":" + binding.Step.Id;
            if (!run.ExtensionData!.TryGetValue(key, out var state) || state.ValueKind != JsonValueKind.String || state.GetString() != "complete")
                throw new WorkflowObservationUnknownException("前置任务的日志/触发观察不完整，不能按未命中选择车道");
        }
    }

    private async Task<Active> ArmAsync(WorkflowRunRecord run, Binding binding, string? target, CancellationToken ct)
    {
        var s = Copy(binding.Step);
        var kind = s.Kind == StartupStepKinds.TimerTrigger ? "timer" : s.Kind == StartupStepKinds.Watchdog ? "watchdog" : "log";
        var a = new Active { View = new(Guid.NewGuid().ToString("N"), run.WorkflowId, run.RunId,
            binding.FlowNodeId, s.Id, kind, string.IsNullOrWhiteSpace(s.Name) ? StartupStepKinds.Find(s.Kind)?.DisplayName ?? s.Kind : s.Name,
            kind == "log" ? "监听：" + s.LogKeyword : "已挂载", s, s.TargetNodeId), Scope = s.TriggerScope, Round = binding.Round };
        if (kind == "log")
        {
            if (ObservationSource is not IWorkflowLiveObservationSource source)
                throw new WorkflowObservationUnknownException("日志实时触发源尚未就绪");
            a.Observation = await source.ArmLiveAsync(a.View.RuntimeId, s.LogKeyword,
                _ => QueueFire(a), ct).ConfigureAwait(false);
        }
        _active[a.View.RuntimeId] = a; Notify();
        if (kind != "log") a.Pump = PumpAsync(a);
        return a;
    }

    private void QueueFire(Active a)
    {
        lock (a.CallbackGate)
        {
            if (a.Stop.IsCancellationRequested || !a.View.Step.WatchRepeat && a.OnceAccepted) return;
            a.OnceAccepted = true;
            a.Pending = true;
            if (a.Draining) return;
            a.Draining = true;
            a.Drain = Task.Run(async () =>
            {
                while (true)
                {
                    lock (a.CallbackGate)
                    {
                        if (!a.Pending || a.Stop.IsCancellationRequested) { a.Draining = false; return; }
                        a.Pending = false;
                    }
                    await FireAsync(a).ConfigureAwait(false);
                }
            });
        }
    }

    private async Task FireAsync(Active a)
    {
        var entered = false;
        try
        {
            await a.Serial.WaitAsync(a.Stop.Token).ConfigureAwait(false);
            entered = true;
            a.Stop.Token.ThrowIfCancellationRequested();
            if (!a.View.Step.WatchRepeat && a.Fired > 0) return;
            await RunStepsAsync(a.View.Step.FireSteps, a.Stop.Token).ConfigureAwait(false);
            Interlocked.Increment(ref a.Fired);
            a.View = a.View with { Note = "已触发 " + a.Fired + " 次，等待范围结束" }; Notify();
        }
        catch (OperationCanceledException) when (a.Stop.IsCancellationRequested) { }
        catch (Exception ex) { a.Error = ex.Message; _faults[a.View.RunId] = ex.Message; Notify(); }
        finally { if (entered) a.Serial.Release(); }
    }

    private async Task PumpAsync(Active a)
    {
        var s = a.View.Step;
        try
        {
            bool last = false;
            do
            {
                if (s.Kind == StartupStepKinds.TimerTrigger)
                {
                    var t = TimeOnly.Parse(s.TriggerTime);
                    var now = Clock(); var due = new DateTimeOffset(now.Date + t.ToTimeSpan(), now.Offset);
                    if (due <= now) due = due.AddDays(1);
                    await DelayAsync(due - now, a.Stop.Token).ConfigureAwait(false);
                    await FireAsync(a).ConfigureAwait(false);
                    if (!s.RepeatDaily) break;
                }
                else
                {
                    var probe = Copy(s); probe.Kind = s.WatchKind; probe.NodeType = "condition";
                    var current = await EvaluateAsync(probe, a.Stop.Token).ConfigureAwait(false);
                    if (current && !last)
                    {
                        var confirmed = true;
                        for (var i = 0; i < Math.Max(1, s.WatchConfirmTimes); i++)
                        {
                            await DelayAsync(TimeSpan.FromSeconds(Math.Max(1, s.WatchConfirmSeconds)), a.Stop.Token).ConfigureAwait(false);
                            if (!await EvaluateAsync(probe, a.Stop.Token).ConfigureAwait(false)) { confirmed = false; break; }
                        }
                        if (confirmed) await FireAsync(a).ConfigureAwait(false);
                        if (confirmed && !s.WatchRepeat) break;
                    }
                    last = current;
                    await DelayAsync(TimeSpan.FromSeconds(Math.Max(1, s.WatchIntervalSeconds)), a.Stop.Token).ConfigureAwait(false);
                }
            } while (!a.Stop.IsCancellationRequested);
        }
        catch (OperationCanceledException) when (a.Stop.IsCancellationRequested) { }
        catch (Exception ex) { a.Error = ex.Message; _faults[a.View.RunId] = ex.Message; Notify(); }
    }

    private async Task<string> FreezeAsync(Active a)
    {
        WorkflowObservationFact? fact = null;
        try { if (a.Observation is not null) fact = await a.Observation.FreezeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
        catch (Exception ex) { a.Error = ex.Message; }
        await a.Drain.ConfigureAwait(false);
        var state = a.Cancelled || a.Error is not null || fact?.State == "unknown" ? "unknown" : "complete";
        await CloseAsync(a, CancellationToken.None).ConfigureAwait(false);
        return state;
    }

    private async Task CloseAsync(Active a, CancellationToken ct)
    {
        a.Stop.Cancel();
        if (a.Observation is not null) await a.Observation.DisposeAsync().ConfigureAwait(false);
        await a.Pump.WaitAsync(ct).ConfigureAwait(false);
        await a.Drain.WaitAsync(ct).ConfigureAwait(false);
        await a.Serial.WaitAsync(ct).ConfigureAwait(false); a.Serial.Release();
        _active.TryRemove(a.View.RuntimeId, out _); Notify();
    }

    public async Task RunStepsAsync(IEnumerable<StartupStep> steps, CancellationToken ct)
    {
        foreach (var s in steps.Where(s => s.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            if (s.NodeType == "condition")
            { await RunStepsAsync(await EvaluateAsync(s, ct).ConfigureAwait(false) ? s.TrueSteps : s.FalseSteps, ct).ConfigureAwait(false); continue; }
            switch (s.Kind)
            {
                case StartupStepKinds.Wait: await DelayAsync(TimeSpan.FromSeconds(s.WaitSeconds), ct).ConfigureAwait(false); break;
                case StartupStepKinds.SetRegister: (Registers ?? throw new InvalidOperationException("寄存器宿主未接线")).Set(s.RegisterWrite ?? throw new InvalidOperationException("设置参数缺失"), ct); break;
                case StartupStepKinds.StartProgram:
                    if (StartProgramAsync is null) throw new InvalidOperationException("当前执行端未授权启动此辅助程序");
                    await StartProgramAsync(s, ct).ConfigureAwait(false); break;
                default: throw new InvalidOperationException("此子链不支持节点：" + s.Kind);
            }
        }
    }

    public async Task<bool> EvaluateAsync(StartupStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (step.Kind == StartupStepKinds.JudgeRegister)
            return (Registers ?? throw new InvalidOperationException("寄存器宿主未接线")).Evaluate(step.RegisterCondition ?? throw new InvalidOperationException("判断参数缺失")).Passed;
        if (step.Kind == StartupStepKinds.ManualConfirm)
            return ConfirmAsync is null ? throw new WorkflowObservationUnknownException("人工确认入口未就绪") : await ConfirmAsync(step, ct).ConfigureAwait(false);
        if (step.Kind == StartupStepKinds.Weekday) return step.Weekdays.Contains(Clock().DayOfWeek == DayOfWeek.Sunday ? 7 : (int)Clock().DayOfWeek);
        if (step.Kind == StartupStepKinds.TimeRange)
        {
            var start = TimeOnly.Parse(step.TimeStart); var end = TimeOnly.Parse(step.TimeEnd); var now = TimeOnly.FromDateTime(Clock().DateTime);
            return start <= end ? now >= start && now <= end : now >= start || now <= end;
        }
        var status = StatusProvider?.Invoke();
        if (step.Kind is StartupStepKinds.BgiTaskName or StartupStepKinds.BgiTaskRunning && (status is null || !status.HasFreshTaskStatus(DateTimeOffset.UtcNow)))
            throw new WorkflowObservationUnknownException("BGI任务状态尚未取得，不能当作否");
        return StartupFlowRunner.EvaluateCondition(step, status).passed;
    }

    private sealed class TaskScope(Func<Task> close) : IAsyncDisposable
    {
        public ValueTask DisposeAsync() => new(close());
    }
}
