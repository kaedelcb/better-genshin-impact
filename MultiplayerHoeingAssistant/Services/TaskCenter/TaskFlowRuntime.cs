using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public sealed record TaskFlowTriggerSnapshot(string RuntimeId, string WorkflowId, string RunId,
    string FlowNodeId, string StepId, string Kind, string Text, string? Note, StartupStep Step, string? TargetNodeId);

public sealed class TaskFlowOnlineParkException : Exception
{
    public TaskFlowOnlineParkException() : base("联机锄地接管期间，流程在已保存的步骤等待恢复。") { }
}

/// <summary>执行端共用的任务流程宿主。UI只消费快照；订阅、回调和撤销都属于运行实例。</summary>
public sealed class TaskFlowRuntime
{
    private readonly ConcurrentDictionary<string, Active> _active = new();
    private readonly ConcurrentDictionary<string, string> _faults = new();
    private readonly ConcurrentDictionary<string, byte> _preparedOnlineRuns = new();
    private readonly SemaphoreSlim _effectGate = new(1, 1);
    private readonly object _onlineStateGate = new();
    private CancellationTokenSource _onlineInterrupt = new();
    private volatile bool _onlineSuspended;
    private long _statusFreshAfterTicks;
    private DateTimeOffset? StatusFreshAfterUtc => Interlocked.Read(ref _statusFreshAfterTicks) is var ticks && ticks != 0 ? new DateTimeOffset(ticks, TimeSpan.Zero) : null;
    private readonly ConcurrentDictionary<string, DateTimeOffset> _preparedStatusAfter = new();
    public Func<bool>? OnlineGateClosed { get; set; }
    private bool IsOnlineClosed => _onlineSuspended || OnlineGateClosed?.Invoke() == true;
    public bool IsOnlinePaused => IsOnlineClosed;
    private bool CallbackBlocked(string? runId) => IsOnlineClosed && (runId is null || !_preparedOnlineRuns.ContainsKey(runId));
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
        public SemaphoreSlim ObservationGate = new(1, 1);
        public bool OnlinePaused;
        public int Segment;
        public string? CoveredSourceInstance;
        public string? CoveredSourcePath;
        public string? CoveredEpoch;
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

    private CancellationToken OnlineInterruptToken { get { lock (_onlineStateGate) return _onlineInterrupt.Token; } }

    /// <summary>联机命令发出前封住副作用并冻结本段日志。排队动作保留，不把联机日志记入原任务。</summary>
    public async Task SuspendForOnlineAsync(CancellationToken ct = default)
    {
        _onlineSuspended = true;
        _preparedOnlineRuns.Clear();
        lock (_onlineStateGate) _onlineInterrupt.Cancel();
        await _effectGate.WaitAsync(ct).ConfigureAwait(false);
        _effectGate.Release(); // 只等待正在进行的同步副作用，不等待整个等待/回调链。
        foreach (var a in _active.Values.ToArray())
        {
            await a.ObservationGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (a.OnlinePaused) continue;
                a.OnlinePaused = true;
                if (a.Observation is { } observation)
                {
                    try
                    {
                        var fact = await observation.FreezeAsync(ct).WaitAsync(TimeSpan.FromSeconds(6), ct).ConfigureAwait(false);
                        if (fact.State != "frozen") RecordFault(a, fact.Reason ?? "联机暂停前的日志覆盖未确认");
                        else
                        {
                            a.CoveredSourceInstance = fact.CoveredSourceInstance;
                            a.CoveredSourcePath = fact.CoveredSourcePath;
                            a.CoveredEpoch = fact.CoveredEpoch;
                        }
                    }
                    catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
                    catch (Exception ex) { RecordFault(a, "暂停日志观察未确认：" + ex.Message); }
                    finally { await observation.DisposeAsync().ConfigureAwait(false); a.Observation = null; }
                }
                a.View = a.View with { Note = "联机锄地期间暂停，原步骤与已接受动作保持" };
            }
            finally { a.ObservationGate.Release(); }
        }
        Notify();
    }

    /// <summary>宿主已核对票据、原节点和纪元，在恢复作业发送之前建立新观察段。</summary>
    public async Task PrepareOnlineResumeAsync(string runId, CancellationToken ct = default)
    {
        _preparedStatusAfter[runId] = DateTimeOffset.UtcNow;
        foreach (var a in _active.Values.Where(a => a.View.RunId == runId).ToArray())
            await ResumeObservationAsync(a, ct).ConfigureAwait(false);
        _preparedOnlineRuns[runId] = 0; // 此运行的恢复任务可先回调；普通后继派发仍由宿主发送门控制。
        Notify();
    }

    /// <summary>仅布防。必须在资源派发门已真正打开后再调用 OnlineSendOpened。</summary>
    public async Task ResumeAfterOnlineAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _statusFreshAfterTicks, DateTimeOffset.UtcNow.Ticks);
        foreach (var a in _active.Values.ToArray()) await ResumeObservationAsync(a, ct).ConfigureAwait(false);
    }

    public void OnlineSendOpened()
    {
        lock (_onlineStateGate)
        {
            if (!_onlineSuspended && !_onlineInterrupt.IsCancellationRequested) return;
            var previous = _onlineInterrupt;
            _onlineInterrupt = new CancellationTokenSource();
            previous.Dispose();
            _onlineSuspended = false;
        }
        Notify();
    }

    private void RecordFault(Active a, string reason)
    { a.Error = reason; _faults[a.View.RunId] = reason; }

    private async Task ResumeObservationAsync(Active a, CancellationToken ct)
    {
        await a.ObservationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (!a.OnlinePaused || a.Stop.IsCancellationRequested) return;
            if (a.Error is not null) throw new WorkflowObservationUnknownException(a.Error);
            if (a.View.Kind == "log")
            {
                if (ObservationSource is not IWorkflowLiveObservationSource source)
                    throw new WorkflowObservationUnknownException("恢复任务的日志源不可用");
                a.Observation = await source.ArmLiveContinuationAsync(a.View.RuntimeId + ":segment:" + (++a.Segment),
                    a.View.Step.LogKeyword, _ => QueueFire(a), a.CoveredSourceInstance, a.CoveredSourcePath, a.CoveredEpoch, ct).ConfigureAwait(false);
            }
            a.OnlinePaused = false;
            a.View = a.View with { Note = "已接续原任务观察，联机日志已排除" };
        }
        finally { a.ObservationGate.Release(); }
    }

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
            if (IsOnlineClosed) throw new TaskFlowOnlineParkException();
            var key = $"taskFlowStep:{node.NodeId}:{round}:{step.Id}";
            run.ExtensionData ??= new();
            string? state = null;
            if (run.ExtensionData.TryGetValue(key, out var recorded))
            {
                if (recorded.ValueKind != JsonValueKind.String)
                    throw new WorkflowObservationUnknownException("流程动作上次结果未确认，不能重放");
                state = recorded.GetString();
                if (state is "done" or "yes" or "no")
                {
                    if (step.Id == exitId) answer = state == "yes";
                    continue;
                }
                if (state is not ("conditionYes" or "conditionNo" or "waiting"))
                    throw new WorkflowObservationUnknownException("流程动作上次结果未确认，不能重放");
                if (state == "waiting" && step.Kind != StartupStepKinds.Wait
                    || state is "conditionYes" or "conditionNo" && step.NodeType != "condition")
                    throw new WorkflowObservationUnknownException("流程检查点与当前步骤类型不符，不能猜测恢复");
            }
            if (step.Id == exitId)
            {
                foreach (var a in _active.Values.Where(a => a.View.RunId == run.RunId && a.View.FlowNodeId == node.NodeId && a.Scope == "flow").ToArray())
                    if (await FreezeAsync(a).ConfigureAwait(false) != "complete") throw new WorkflowObservationUnknownException("流程范围内观察未完整结束");
                EnsureFacts(run, round);
                answer = await EvaluateFlowAsync(step, ct).ConfigureAwait(false);
                run.ExtensionData[key] = JsonSerializer.SerializeToElement(answer.Value ? "yes" : "no"); save();
                continue;
            }
            if (step.Kind == StartupStepKinds.Wait)
            {
                var waitKey = key + ":remaining";
                var remaining = state == "waiting" && run.ExtensionData.TryGetValue(waitKey, out var saved)
                    ? TimeSpan.FromTicks(saved.GetInt64()) : TimeSpan.FromSeconds(step.WaitSeconds);
                run.ExtensionData[key] = JsonSerializer.SerializeToElement("waiting");
                run.ExtensionData[waitKey] = JsonSerializer.SerializeToElement(remaining.Ticks); save();
                try
                {
                    while (remaining > TimeSpan.Zero)
                    {
                        if (IsOnlineClosed) throw new TaskFlowOnlineParkException();
                        var slice = remaining > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : remaining;
                        var started = Stopwatch.StartNew();
                        var pauseToken = OnlineInterruptToken;
                        using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(ct, pauseToken);
                        try { await DelayAsync(slice, interrupted.Token).ConfigureAwait(false); remaining -= slice; }
                        catch (OperationCanceledException) when (!ct.IsCancellationRequested && pauseToken.IsCancellationRequested)
                        { remaining -= started.Elapsed < slice ? started.Elapsed : slice; throw new TaskFlowOnlineParkException(); }
                    }
                }
                catch (TaskFlowOnlineParkException)
                { run.ExtensionData[waitKey] = JsonSerializer.SerializeToElement(Math.Max(0, remaining.Ticks)); save(); throw; }
                run.ExtensionData.Remove(waitKey);
            }
            else if (step.NodeType == "condition")
            {
                var passed = state is "conditionYes" or "conditionNo" ? state == "conditionYes" : await EvaluateFlowAsync(step, ct).ConfigureAwait(false);
                run.ExtensionData[key] = JsonSerializer.SerializeToElement(passed ? "conditionYes" : "conditionNo"); save();
                await ExecuteChainAsync(run, node, passed ? step.TrueSteps : step.FalseSteps, null, round, save, ct).ConfigureAwait(false);
            }
            else
            {
                await _effectGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (IsOnlineClosed) throw new TaskFlowOnlineParkException();
                    run.ExtensionData[key] = JsonSerializer.SerializeToElement("executing"); save();
                    if (TaskFlowNodeCatalog.IsTrigger(step.Kind))
                    {
                        var binding = new Binding(node.NodeId, round, Copy(step));
                        var bindingKey = BindingPrefix + node.NodeId + ":" + round + ":" + step.Id;
                        run.ExtensionData[bindingKey] = JsonSerializer.SerializeToElement(binding);
                        if (step.TriggerScope == "task") run.ExtensionData[FactPrefix + node.NodeId + ":" + round + ":" + step.Id] = JsonSerializer.SerializeToElement("pending");
                        else await ArmAsync(run, binding, null, ct).ConfigureAwait(false);
                    }
                    else await RunActionAsync(step, ct).ConfigureAwait(false);
                }
                finally { _effectGate.Release(); }
            }
            run.ExtensionData[key] = JsonSerializer.SerializeToElement("done"); save();
        }
        return answer;
    }

    private async Task<bool> EvaluateFlowAsync(StartupStep step, CancellationToken ct)
    {
        if (IsOnlineClosed) throw new TaskFlowOnlineParkException();
        if (step.Kind is StartupStepKinds.BgiTaskName or StartupStepKinds.BgiTaskRunning && StatusFreshAfterUtc is not null)
        {
            var snapshot = await AwaitFreshTaskStatusAsync(step, null, ct, true).ConfigureAwait(false);
            return StartupFlowRunner.EvaluateCondition(step, snapshot).passed;
        }
        if (step.Kind == StartupStepKinds.ManualConfirm)
        {
            var pauseToken = OnlineInterruptToken;
            using var interrupted = CancellationTokenSource.CreateLinkedTokenSource(ct, pauseToken);
            try { return await EvaluateAsync(step, interrupted.Token).ConfigureAwait(false); }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested && pauseToken.IsCancellationRequested) { throw new TaskFlowOnlineParkException(); }
        }
        await _effectGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (IsOnlineClosed) throw new TaskFlowOnlineParkException();
            return await EvaluateAsync(step, ct).ConfigureAwait(false);
        }
        finally { _effectGate.Release(); }
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
                var retained = _active.Values.FirstOrDefault(a => a.View.RunId == run.RunId && a.Round == binding.Round
                    && a.View.FlowNodeId == binding.FlowNodeId && a.View.StepId == binding.Step.Id && !a.Cancelled);
                if (retained is not null)
                {
                    if (retained.Error is not null) throw new WorkflowObservationUnknownException(retained.Error);
                    mounted.Add(retained); continue; // 明确在线停驻的同一次观察，不重跑初始化。
                }
                var fact = FactPrefix + binding.FlowNodeId + ":" + occurrence.LoopIteration + ":" + binding.Step.Id;
                var attemptKey = fact + ":attempt";
                if (run.ExtensionData!.TryGetValue(fact, out var priorState) && priorState.ValueKind == JsonValueKind.String && priorState.GetString() == "armed"
                    && run.ExtensionData.TryGetValue(attemptKey, out var priorAttempt) && priorAttempt.GetInt32() == attempt)
                    throw new WorkflowObservationUnknownException("该次任务曾已开始监听但结束结果丢失，禁止只观察重启后的片段再判未命中");
                run.ExtensionData[attemptKey] = JsonSerializer.SerializeToElement(attempt);
                run.ExtensionData[fact] = JsonSerializer.SerializeToElement("armed"); save();
                await RunStepsAsync(binding.Step.BeforeAttemptSteps, ct).ConfigureAwait(false);
                while (true)
                {
                    await WaitForCallbackPermissionAsync(null, ct).ConfigureAwait(false);
                    await _effectGate.WaitAsync(ct).ConfigureAwait(false);
                    try
                    {
                        if (IsOnlineClosed) continue;
                        mounted.Add(await ArmAsync(run, binding, node.NodeId + ":" + occurrence.Occurrence + ":" + attempt, ct).ConfigureAwait(false));
                        break;
                    }
                    finally { _effectGate.Release(); }
                }
            }
            return new TaskScope(async () =>
            {
                foreach (var a in mounted)
                {
                    if (CallbackBlocked(a.View.RunId)) continue;
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
            await RunStepsCoreAsync(a.View.Step.FireSteps, a.View.RunId, a.Stop.Token).ConfigureAwait(false);
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
                await WaitForCallbackPermissionAsync(a.View.RunId, a.Stop.Token).ConfigureAwait(false);
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
                    var current = await EvaluateCallbackAsync(probe, a.View.RunId, a.Stop.Token).ConfigureAwait(false);
                    if (current && !last)
                    {
                        var confirmed = true;
                        for (var i = 0; i < Math.Max(1, s.WatchConfirmTimes); i++)
                        {
                            await DelayAsync(TimeSpan.FromSeconds(Math.Max(1, s.WatchConfirmSeconds)), a.Stop.Token).ConfigureAwait(false);
                            await WaitForCallbackPermissionAsync(a.View.RunId, a.Stop.Token).ConfigureAwait(false);
                            if (!await EvaluateCallbackAsync(probe, a.View.RunId, a.Stop.Token).ConfigureAwait(false)) { confirmed = false; break; }
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
        if (CallbackBlocked(a.View.RunId)) throw new TaskFlowOnlineParkException();
        WorkflowObservationFact? fact = null;
        try { if (a.Observation is not null) fact = await a.Observation.FreezeAsync(CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(6)).ConfigureAwait(false); }
        catch (Exception ex) { a.Error = ex.Message; }
        while (!a.Drain.IsCompleted)
        {
            if (CallbackBlocked(a.View.RunId)) throw new TaskFlowOnlineParkException();
            await Task.WhenAny(a.Drain, Task.Delay(25)).ConfigureAwait(false);
        }
        await a.Drain.ConfigureAwait(false);
        var state = a.Cancelled || a.Error is not null || fact?.State == "unknown" ? "unknown" : "complete";
        await CloseAsync(a, CancellationToken.None).ConfigureAwait(false);
        return state;
    }

    private async Task CloseAsync(Active a, CancellationToken ct)
    {
        a.Stop.Cancel();
        await a.ObservationGate.WaitAsync(ct).ConfigureAwait(false);
        try
        {
            if (a.Observation is { } observation) await observation.DisposeAsync().ConfigureAwait(false);
            a.Observation = null;
        }
        finally { a.ObservationGate.Release(); }
        await a.Pump.WaitAsync(ct).ConfigureAwait(false);
        await a.Drain.WaitAsync(ct).ConfigureAwait(false);
        await a.Serial.WaitAsync(ct).ConfigureAwait(false); a.Serial.Release();
        _active.TryRemove(a.View.RuntimeId, out _); Notify();
    }

    public Task RunStepsAsync(IEnumerable<StartupStep> steps, CancellationToken ct)
        => RunStepsCoreAsync(steps, null, ct);

    private async Task WaitForCallbackPermissionAsync(string? runId, CancellationToken ct)
    {
        while (CallbackBlocked(runId)) await Task.Delay(25, ct).ConfigureAwait(false);
    }

    private async Task RunStepsCoreAsync(IEnumerable<StartupStep> steps, string? runId, CancellationToken ct)
    {
        foreach (var s in steps.Where(s => s.Enabled))
        {
            ct.ThrowIfCancellationRequested();
            if (s.Kind == StartupStepKinds.Wait)
            {
                var remaining = TimeSpan.FromSeconds(s.WaitSeconds);
                while (remaining > TimeSpan.Zero)
                {
                    await WaitForCallbackPermissionAsync(runId, ct).ConfigureAwait(false);
                    var slice = remaining > TimeSpan.FromMilliseconds(100) ? TimeSpan.FromMilliseconds(100) : remaining;
                    await DelayAsync(slice, ct).ConfigureAwait(false); remaining -= slice;
                }
                continue;
            }
            if (s.NodeType == "condition")
            {
                var choice = await EvaluateCallbackAsync(s, runId, ct).ConfigureAwait(false);
                await RunStepsCoreAsync(choice ? s.TrueSteps : s.FalseSteps, runId, ct).ConfigureAwait(false);
                continue;
            }
            while (true)
            {
                await WaitForCallbackPermissionAsync(runId, ct).ConfigureAwait(false);
                await _effectGate.WaitAsync(ct).ConfigureAwait(false);
                try
                {
                    if (CallbackBlocked(runId)) continue;
                    await RunActionAsync(s, ct).ConfigureAwait(false);
                    break;
                }
                finally { _effectGate.Release(); }
            }
        }
    }

    private async Task RunActionAsync(StartupStep s, CancellationToken ct)
    {
        switch (s.Kind)
        {
            case StartupStepKinds.SetRegister: (Registers ?? throw new InvalidOperationException("寄存器宿主未接线")).Set(s.RegisterWrite ?? throw new InvalidOperationException("设置参数缺失"), ct); break;
            case StartupStepKinds.StartProgram:
                if (StartProgramAsync is null) throw new InvalidOperationException("当前执行端未授权启动此辅助程序");
                await StartProgramAsync(s, ct).ConfigureAwait(false); break;
            default: throw new InvalidOperationException("此子链不支持节点：" + s.Kind);
        }
    }

    private async Task<bool> EvaluateCallbackAsync(StartupStep step, string? runId, CancellationToken ct)
    {
        while (true)
        {
            await WaitForCallbackPermissionAsync(runId, ct).ConfigureAwait(false);
            ControlStatus? checkedStatus = null;
            var fenced = step.Kind is StartupStepKinds.BgiTaskName or StartupStepKinds.BgiTaskRunning
                && (runId is not null && _preparedStatusAfter.ContainsKey(runId) || StatusFreshAfterUtc is not null);
            if (fenced) checkedStatus = await AwaitFreshTaskStatusAsync(step, runId, ct, false).ConfigureAwait(false);
            await _effectGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                if (CallbackBlocked(runId)) continue;
                if (checkedStatus is not null) return StartupFlowRunner.EvaluateCondition(step, checkedStatus).passed;
                return await EvaluateAsync(step, ct).ConfigureAwait(false);
            }
            finally { _effectGate.Release(); }
        }
    }

    private async Task<ControlStatus> AwaitFreshTaskStatusAsync(StartupStep step, string? runId, CancellationToken ct, bool park)
    {
        while (true)
        {
            ct.ThrowIfCancellationRequested();
            if (park && IsOnlineClosed) throw new TaskFlowOnlineParkException();
            if (!park) await WaitForCallbackPermissionAsync(runId, ct).ConfigureAwait(false);
            var status = StatusProvider?.Invoke();
            var after = runId is not null && _preparedStatusAfter.TryGetValue(runId, out var known) ? known : StatusFreshAfterUtc;
            if (status?.HasFreshTaskStatus(DateTimeOffset.UtcNow) == true && (after is null || status.TaskStatusObservedAtUtc >= after)) return status;
            await Task.Delay(25, ct).ConfigureAwait(false);
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
