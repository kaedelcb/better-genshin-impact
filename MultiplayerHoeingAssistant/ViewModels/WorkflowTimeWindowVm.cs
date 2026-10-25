using System.Diagnostics;
using System.Text.Json;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class WorkflowEditVm
{
    private bool _nodeTimeWindowEnabled;
    private DateTimeOffset _timeWindowNow = DateTimeOffset.Now;

    /// <summary>整张计划的时段总开关；关闭时只保留时段设置。</summary>
    public bool NodeTimeWindowEnabled
    {
        get => _nodeTimeWindowEnabled;
        set
        {
            if (!SetProperty(ref _nodeTimeWindowEnabled, value)) return;
            RefreshTimeWindows(_timeWindowNow);
        }
    }

    private void InitializeTimeWindows()
    {
        _nodeTimeWindowEnabled = Draft.NodeTimeWindowEnabled;
        RefreshTimeWindows(DateTimeOffset.Now);
        Nodes.CollectionChanged += (_, _) => RefreshTimeWindows(_timeWindowNow);
    }

    /// <summary>只更新展示上下文，不修改草稿、撤销历史或可写编辑字段。</summary>
    internal void RefreshTimeWindows(DateTimeOffset now)
    {
        _timeWindowNow = now;
        foreach (var node in Nodes) node.RefreshTimeWindowState(NodeTimeWindowEnabled, now);
    }
}

public sealed partial class NodeEditVm
{
    private bool _timeWindowEnabled;
    private DateTimeOffset _timeWindowNow = DateTimeOffset.Now;

    public WorkflowTimeWindow? TimeWindow => Model.TimeWindow;
    public bool HasTimeWindow => TimeWindow is not null;
    public string TimeWindowStart => TimeWindow?.Start ?? "00:00";
    public string TimeWindowEnd => TimeWindow?.End ?? "00:00";
    public string TimeWindowDisplay => WorkflowTimeWindowPolicy.Display(TimeWindow);
    public bool IsOutsideTimeWindow => _timeWindowEnabled
        && !WorkflowTimeWindowPolicy.Allows(TimeWindow, _timeWindowNow);
    public string TimeWindowStatusText => !IncludedInPlan ? "已停用"
        : !_timeWindowEnabled ? "未启用时段限制"
        : !WorkflowTimeWindowPolicy.TryValidate(TimeWindow, out _) ? "时段无效"
        : IsOutsideTimeWindow ? "时段外 · 跳过"
        : HasTimeWindow ? "时段内" : "全天";

    /// <summary>弹窗确认后一次提交完整时段，避免时、分选择过程落盘。</summary>
    public void SetTimeWindow(string start, string end)
    {
        var next = TimeWindow is null ? new WorkflowTimeWindow()
            : JsonSerializer.Deserialize<WorkflowTimeWindow>(JsonSerializer.Serialize(TimeWindow))!;
        next.Start = start;
        next.End = end;
        if (!WorkflowTimeWindowPolicy.TryValidate(next, out var error))
            throw new InvalidOperationException(error);
        if (TimeWindow is { } current && current.Start == start && current.End == end) return;
        BeforeEdit?.Invoke();
        Model.TimeWindow = next;
        NotifyTimeWindowChanged();
    }

    public void ClearTimeWindow()
    {
        if (TimeWindow is null) return;
        BeforeEdit?.Invoke();
        Model.TimeWindow = null;
        NotifyTimeWindowChanged();
    }

    internal void NotifyTimeWindowChanged()
    {
        OnPropertyChanged(nameof(TimeWindow));
        OnPropertyChanged(nameof(HasTimeWindow));
        OnPropertyChanged(nameof(TimeWindowStart));
        OnPropertyChanged(nameof(TimeWindowEnd));
        OnPropertyChanged(nameof(TimeWindowDisplay));
        NotifyTimeWindowState();
    }

    internal void RefreshTimeWindowState(bool enabled, DateTimeOffset now)
    {
        _timeWindowEnabled = enabled;
        _timeWindowNow = now;
        NotifyTimeWindowState();
    }

    internal void NotifyTimeWindowState()
    {
        OnPropertyChanged(nameof(IsOutsideTimeWindow));
        OnPropertyChanged(nameof(TimeWindowStatusText));
    }
}

public sealed partial class TaskCenterPanelViewModel
{
    private DispatcherTimer? _timeWindowTimer;
    private readonly WorkflowTimeWindowClock _timeWindowClock = new();

    public string TimeWindowClockHint => _remoteTarget is null ? "按本机时间实时判断"
        : _timeWindowClock.HasRemoteTime ? "按执行端时间实时判断"
        : "执行端未提供时钟，暂按本机时间显示";

    private void SynchronizeRemoteTimeWindowClock(DateTimeOffset? serverTime, long receivedTimestamp)
    {
        _timeWindowClock.Synchronize(serverTime, receivedTimestamp);
        OnPropertyChanged(nameof(TimeWindowClockHint));
        RefreshTimeWindows();
    }

    private void InitializeTimeWindowRefresh(bool start)
    {
        _timeWindowTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
        _timeWindowTimer.Tick += (_, _) => RefreshTimeWindows();
        if (start) _timeWindowTimer.Start();
    }

    internal void RefreshTimeWindows(DateTimeOffset? now = null)
        => Editing?.RefreshTimeWindows(now ?? (_remoteTarget is null ? DateTimeOffset.Now
            : _timeWindowClock.Current(DateTimeOffset.Now, Stopwatch.GetTimestamp())));
}

/// <summary>展示时钟：保留执行端的时区偏移，轮询之间以单调时钟推进，不受监控端系统调时影响。</summary>
internal sealed class WorkflowTimeWindowClock
{
    private DateTimeOffset? _serverTime;
    private long _receivedTimestamp;
    internal bool HasRemoteTime => _serverTime.HasValue;

    internal void Synchronize(DateTimeOffset? serverTime, long receivedTimestamp)
    {
        _serverTime = serverTime;
        _receivedTimestamp = receivedTimestamp;
    }

    internal DateTimeOffset Current(DateTimeOffset localNow, long timestamp)
        => _serverTime is { } serverTime
            ? serverTime + Stopwatch.GetElapsedTime(_receivedTimestamp, timestamp)
            : localNow;
}
