using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class MistletoeViewModel
{
    private static readonly string TriggerSnapshotEpoch = Guid.NewGuid().ToString("N");
    private static long _triggerSnapshotSequence;
    private readonly Dictionary<string, (string Plan, string? Target)> _triggerDisplayContexts = new(StringComparer.Ordinal);
    private readonly ObservableCollection<UnifiedTriggerRowViewModel> _unifiedTriggerRows = [];
    private readonly System.Windows.Threading.Dispatcher _triggerUiDispatcher = System.Windows.Threading.Dispatcher.CurrentDispatcher;
    private IReadOnlyList<UnifiedTriggerInfo> _latestUnifiedTriggers = [];
    private bool _taskTriggerStatusAvailable;
    private DateTimeOffset _lastUnifiedSnapshotAt;
    private string _lastUnifiedSnapshotEpoch = "";
    private long _lastUnifiedSnapshotSequence;
    private bool _triggersExpanded;
    private int _triggerSourceFilter;
    public ObservableCollection<UnifiedTriggerRowViewModel> UnifiedTriggerRows => _unifiedTriggerRows;
    public bool TriggersExpanded { get => _triggersExpanded; set => SetProperty(ref _triggersExpanded, value); }
    public int TriggerSourceFilter
    {
        get => _triggerSourceFilter;
        set { if (SetProperty(ref _triggerSourceFilter, value)) RenderUnifiedTriggerRows(); }
    }
    public bool HasUnifiedTriggers => _latestUnifiedTriggers.Count > 0 || IsRemoteMode && (!_taskTriggerStatusAvailable || _remoteStartupStale);
    public string UnifiedTriggerCount => IsRemoteMode && _remoteStartupStale ? "状态未知" :
        IsRemoteMode && !_taskTriggerStatusAvailable ? "已知 " + _latestUnifiedTriggers.Count : _latestUnifiedTriggers.Count.ToString();
    public string UnifiedTriggerSummary => $"启动中心 {_latestUnifiedTriggers.Count(i => i.Source == "startup")} · 任务中心 " +
        (IsRemoteMode && !_taskTriggerStatusAvailable ? "该端未提供" : _latestUnifiedTriggers.Count(i => i.Source == "taskCenter").ToString());
    public string UnifiedTriggerStatus => IsRemoteMode
        ? _remoteStartupStale ? "执行端状态未确认；以下为上次快照，操作已暂停。"
            : !_taskTriggerStatusAvailable ? "该执行端未提供任务中心触发器；现有启动实例仍可查看。" : "当前执行端的运行实例；取消不会删除配置。"
        : "取消仅撤下所选实例；计划定时在所属计划中管理。";

    private void InitializeUnifiedTriggers(RemoteStartupSnapshot? initial)
    {
        if (!IsRemoteMode)
        {
            ArmedTimersMutable.CollectionChanged += (_, _) => RefreshUnifiedLocalTriggers();
            ArmedWatchdogsMutable.CollectionChanged += (_, _) => RefreshUnifiedLocalTriggers();
            ArmedLogTriggersMutable.CollectionChanged += (_, _) => RefreshUnifiedLocalTriggers();
            var triggerDispatcher = _triggerUiDispatcher;
            _mainVm.TaskCenterHost.FlowRuntime.Changed += () =>
            {
                if (triggerDispatcher.HasShutdownStarted) return;
                if (triggerDispatcher.CheckAccess()) RefreshUnifiedLocalTriggers();
                else triggerDispatcher.BeginInvoke(new Action(RefreshUnifiedLocalTriggers));
            };
            RefreshUnifiedLocalTriggers();
        }
        else
        {
            if (initial is null) _remoteStartupStale = true;
            ApplyUnifiedTriggerSnapshot(initial?.UnifiedTriggers, initial?.Triggers ?? []);
        }
    }

    public UnifiedTriggerSnapshot CaptureUnifiedTriggers()
    {
        var items = ArmedTimers.Select(x => new UnifiedTriggerInfo(x.RuntimeId, "startup", "timer", x.StatusLine, Step: CloneTriggerStep(x.Step), StepId: x.Step.Id))
            .Concat(ArmedWatchdogs.Select(x => new UnifiedTriggerInfo(x.RuntimeId, "startup", "watchdog", x.StatusLine, x.LastCheckNote, CloneTriggerStep(x.Step), StepId: x.Step.Id)))
            .Concat(ArmedLogTriggers.Select(x => new UnifiedTriggerInfo(x.RuntimeId, "startup", "log", x.StatusLine, x.StatusNote, CloneTriggerStep(x.Step), StepId: x.Step.Id))).ToList();
        var host = _mainVm.TaskCenterHost;
        var documents = new Dictionary<string, WorkflowDocument?>(StringComparer.Ordinal);
        (string Plan, string? Target) Context(string instance, string workflowId, string? targetId)
        {
            if (!documents.TryGetValue(workflowId, out var document))
            {
                try { document = host.LoadFlowSnapshot(workflowId).Document; }
                catch (Exception error) when (error is System.IO.IOException or System.IO.InvalidDataException or JsonException or InvalidOperationException)
                { document = null; }
                documents[workflowId] = document;
            }
            _triggerDisplayContexts.TryGetValue(instance, out var mounted);
            var plan = document is null ? mounted.Plan ?? "原计划已变更" :
                string.IsNullOrWhiteSpace(document.Name) ? "未命名计划" : document.Name;
            var target = targetId is null ? null : document?.Nodes.FirstOrDefault(n => n.NodeId == targetId)?.Ref?.Config;
            target ??= mounted.Target;
            var context = (plan, target);
            _triggerDisplayContexts[instance] = context;
            return context;
        }
        foreach (var trigger in host.FlowRuntime.List())
        {
            var context = Context(trigger.RuntimeId, trigger.WorkflowId, trigger.TargetNodeId);
            var scope = trigger.Step.TriggerScope switch
            {
                "task" => "指定任务：" + (context.Target ?? "原目标已变更"),
                "flow" => "本流程行", _ => "本轮计划"
            };
            items.Add(new UnifiedTriggerInfo(trigger.RuntimeId, "taskCenter", trigger.Kind,
                context.Plan + " · " + trigger.Text, scope + (string.IsNullOrEmpty(trigger.Note) ? "" : " · " + trigger.Note),
                CloneTriggerStep(trigger.Step), trigger.WorkflowId, trigger.RunId, trigger.FlowNodeId, trigger.StepId));
        }
        foreach (var run in host.ListActiveRuns().Where(x => host.IsDriving(x.WorkflowId)
            && x.State == WorkflowRunState.Waiting && !x.TriggerConsumed && x.TriggerTiming is not null
            && x.Wait?.Kind == x.TriggerTiming.Kind))
        {
            var runtimeId = "root:" + run.RunId + ":" + run.Wait!.TriggerOccurrenceId;
            var context = Context(runtimeId, run.WorkflowId, null);
            items.Add(new UnifiedTriggerInfo(runtimeId, "taskCenter", "planTimer",
                $"计划定时 · {context.Plan} — {run.TriggerTiming!.ScheduledAt:MM-dd HH:mm}",
                run.TriggerTiming.WindowEndsAt is { } end ? $"等待执行权，窗口至 {end:MM-dd HH:mm}" : "等待本次计划触发",
                WorkflowId: run.WorkflowId, RunId: run.RunId, CanCancel: false, IsRootTimer: true));
        }
        var liveIds = items.Where(i => i.Source == "taskCenter").Select(i => i.RuntimeId).ToHashSet(StringComparer.Ordinal);
        foreach (var old in _triggerDisplayContexts.Keys.Where(id => !liveIds.Contains(id)).ToArray()) _triggerDisplayContexts.Remove(old);
        return new UnifiedTriggerSnapshot
        {
            Epoch = TriggerSnapshotEpoch, Sequence = Interlocked.Increment(ref _triggerSnapshotSequence),
            CapturedAtUtc = DateTimeOffset.UtcNow, Items = items.DistinctBy(x => x.Key)
                .OrderBy(x => x.Source, StringComparer.Ordinal).ThenBy(x => x.WorkflowId, StringComparer.Ordinal)
                .ThenBy(x => x.RuntimeId, StringComparer.Ordinal).ToList()
        };
    }

    internal IReadOnlyList<StartupStep> GetAllowedTaskPrograms()
    {
        if (!_triggerUiDispatcher.CheckAccess()) return _triggerUiDispatcher.Invoke(GetAllowedTaskPrograms);
        static IEnumerable<StartupStep> Walk(IEnumerable<StartupStep> chain)
        {
            foreach (var item in chain)
            {
                yield return item;
                foreach (var child in Walk(item.TrueSteps.Concat(item.FalseSteps).Concat(item.FireSteps))) yield return child;
            }
        }
        return Walk(_config.Steps).Where(x => x.Kind == StartupStepKinds.StartProgram && x.Enabled)
            .Select(x => new StartupStep { Kind = x.Kind, Name = x.Name, Path = x.Path, Arguments = x.Arguments, WindowsUserName = x.WindowsUserName }).ToArray();
    }

    private async Task StartConfiguredTaskProgramAsync(StartupStep step, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested();
        if (!GetAllowedTaskPrograms().Any(x => x.Kind == StartupStepKinds.StartProgram && x.Enabled
            && string.Equals(x.Path, step.Path, StringComparison.OrdinalIgnoreCase)
            && x.Arguments == step.Arguments && string.Equals(x.WindowsUserName.Trim(), step.WindowsUserName.Trim(), StringComparison.OrdinalIgnoreCase)))
            throw new InvalidOperationException("此辅助程序尚未在执行端启动中心配置；未允许计划启动任意程序");
        if (string.IsNullOrWhiteSpace(step.Path) || !System.IO.File.Exists(step.Path))
            throw new InvalidOperationException("辅助程序路径不存在");
        if (!string.IsNullOrWhiteSpace(step.WindowsUserName))
            await Services.WindowsUserProgramLauncher.StartAsync(step.Path, step.Arguments, step.WindowsUserName.Trim(), ct);
        else
        {
            ct.ThrowIfCancellationRequested();
            using var launched = System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            {
                FileName = step.Path, Arguments = step.Arguments, UseShellExecute = true,
                WorkingDirectory = System.IO.Path.GetDirectoryName(step.Path) ?? ""
            });
            if (launched is null) throw new InvalidOperationException("辅助程序启动结果未确认");
        }
    }

    private static StartupStep CloneTriggerStep(StartupStep step)
        => JsonSerializer.Deserialize<StartupStep>(JsonSerializer.Serialize(step))!;

    private void RefreshUnifiedLocalTriggers()
    {
        if (IsRemoteMode) return;
        ApplyUnifiedTriggerSnapshot(CaptureUnifiedTriggers(), []);
    }

    private void ApplyUnifiedTriggerSnapshot(UnifiedTriggerSnapshot? snapshot, IReadOnlyList<RemoteTriggerInfo> legacy)
    {
        if (snapshot is { Version: 1 })
        {
            if (snapshot.CapturedAtUtc < _lastUnifiedSnapshotAt
                || snapshot.Epoch == _lastUnifiedSnapshotEpoch && snapshot.Sequence < _lastUnifiedSnapshotSequence) return;
            _lastUnifiedSnapshotAt = snapshot.CapturedAtUtc;
            _lastUnifiedSnapshotEpoch = snapshot.Epoch;
            _lastUnifiedSnapshotSequence = snapshot.Sequence;
            _taskTriggerStatusAvailable = snapshot.TaskCenterAvailable;
            _latestUnifiedTriggers = snapshot.Items.Where(x => x.Source is "startup" or "taskCenter").DistinctBy(x => x.Key).ToArray();
        }
        else
        {
            _taskTriggerStatusAvailable = false;
            _latestUnifiedTriggers = legacy.Select(x => new UnifiedTriggerInfo(x.RuntimeId, "startup", x.Kind, x.Text, x.Note,
                x.Step, StepId: x.Step?.Id)).DistinctBy(x => x.Key).ToArray();
        }
        RenderUnifiedTriggerRows();
    }

    private void RenderUnifiedTriggerRows()
    {
        var latest = _latestUnifiedTriggers.Where(x => TriggerSourceFilter == 0 ||
            x.Source == (TriggerSourceFilter == 1 ? "startup" : "taskCenter")).ToArray();
        for (var index = 0; index < latest.Length; index++)
        {
            var old = _unifiedTriggerRows.Select((row, i) => (row, i)).FirstOrDefault(x => x.row.Key == latest[index].Key);
            if (old.row is null) _unifiedTriggerRows.Insert(index, new UnifiedTriggerRowViewModel(this, latest[index]));
            else if (old.i != index) _unifiedTriggerRows.Move(old.i, index);
            _unifiedTriggerRows[index].Update(latest[index]);
        }
        while (_unifiedTriggerRows.Count > latest.Length) _unifiedTriggerRows.RemoveAt(_unifiedTriggerRows.Count - 1);
        foreach (var property in new[] { nameof(HasUnifiedTriggers), nameof(UnifiedTriggerCount), nameof(UnifiedTriggerSummary), nameof(UnifiedTriggerStatus) })
            OnPropertyChanged(property);
    }

    internal void ReportUnifiedTriggerError(Exception error) => StatusText = "触发器操作未确认：" + error.Message;
    internal bool CanOperateUnifiedTrigger => !IsRemoteMode || !_remoteStartupStale && !_remoteAbandoned;
    internal void LocateUnifiedTrigger(UnifiedTriggerInfo info)
    {
        if (info.Source == "startup") { SelectedTabIndex = 0; LocateRemoteStep(info.StepId ?? info.Step?.Id); return; }
        SelectedTabIndex = 1;
        if (info.WorkflowId is null || !TaskCenter.LocateFlowStep(info.WorkflowId, info.FlowNodeId, info.StepId))
            StatusText = "原计划或节点已变化；仍可查看该实例挂载时的流程快照。";
    }
    internal void ViewUnifiedTrigger(UnifiedTriggerInfo info)
    {
        if (info.IsRootTimer)
        {
            MessageBox.Show(Application.Current.MainWindow, info.Text + "\n" + info.Note + "\n计划：" + info.WorkflowId,
                "计划定时 · 本次运行", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }
        ViewFlow(info.Step?.FireSteps ?? [], $"{(info.Source == "startup" ? "启动中心" : "任务中心")}触发器「{info.Text}」的挂载快照");
    }
    internal async Task CancelUnifiedTriggerAsync(UnifiedTriggerInfo info)
    {
        if (!CanOperateUnifiedTrigger) return;
        if (info.IsRootTimer) { LocateUnifiedTrigger(info); return; }
        if (IsRemoteMode)
        {
            var reply = await _mainVm.RequestRemoteCenterAsync(_remoteTarget!, info.Source == "startup" ? "startup.trigger.cancel" : "task.trigger.cancel",
                info.Source == "startup" ? (object)info.RuntimeId : new TaskTriggerCancelArgs(info.RuntimeId, info.RunId ?? ""));
            StatusText = reply.Message;
            await RefreshRemoteStartupStatusAsync();
        }
        else if (info.Source == "taskCenter")
        {
            var instance = _mainVm.TaskCenterHost.FlowRuntime.List().FirstOrDefault(x => x.RuntimeId == info.RuntimeId && x.RunId == info.RunId);
            StatusText = instance is not null && await _mainVm.TaskCenterHost.FlowRuntime.CancelAsync(info.RuntimeId)
                ? "已撤下本次任务触发器；依赖该观察的分流须核对结果。" : "实例已变化或撤销未确认，请核对状态。";
            RefreshUnifiedLocalTriggers();
        }
        else
        {
            if (ArmedTimers.FirstOrDefault(x => x.RuntimeId == info.RuntimeId) is { } timer) CancelTimer(timer);
            else if (ArmedWatchdogs.FirstOrDefault(x => x.RuntimeId == info.RuntimeId) is { } watchdog) CancelWatchdog(watchdog);
            else if (ArmedLogTriggers.FirstOrDefault(x => x.RuntimeId == info.RuntimeId) is { } log) CancelLogTrigger(log);
            RefreshUnifiedLocalTriggers();
        }
    }
}

public sealed class UnifiedTriggerRowViewModel : ViewModelBase
{
    private readonly MistletoeViewModel _owner;
    private UnifiedTriggerInfo _info;
    private bool _pending;
    public UnifiedTriggerRowViewModel(MistletoeViewModel owner, UnifiedTriggerInfo info) { _owner = owner; _info = info; }
    public string Key => _info.Key;
    public string SourceLabel => _info.Source == "startup" ? "启动中心" : "任务中心";
    public string StatusLine => _info.Text;
    public string Note => _pending ? "正在撤销；等待执行端确认…" : _info.Note ?? "";
    public string ActionLabel => _info.IsRootTimer ? "管理计划" : "取消";
    public bool CanOperate => !_pending && _owner.CanOperateUnifiedTrigger;
    public RelayCommand LocateCommand => new(_ => _owner.LocateUnifiedTrigger(_info));
    public RelayCommand ViewFlowCommand => new(_ => _owner.ViewUnifiedTrigger(_info));
    public RelayCommand CancelCommand => new(async _ =>
    {
        if (!CanOperate) return;
        _pending = true; OnPropertyChanged(nameof(CanOperate)); OnPropertyChanged(nameof(Note));
        try { await _owner.CancelUnifiedTriggerAsync(_info); }
        catch (Exception error) { _owner.ReportUnifiedTriggerError(error); }
        finally { _pending = false; OnPropertyChanged(nameof(CanOperate)); OnPropertyChanged(nameof(Note)); }
    });
    public void Update(UnifiedTriggerInfo info)
    {
        _info = info;
        foreach (var p in new[] { nameof(SourceLabel), nameof(StatusLine), nameof(Note), nameof(ActionLabel), nameof(CanOperate) }) OnPropertyChanged(p);
    }
}
