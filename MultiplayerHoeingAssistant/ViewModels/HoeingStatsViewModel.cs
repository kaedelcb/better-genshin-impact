using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 锄地数据 Tab 的 ViewModel（P3 / F5 + §5-A 卡死心跳）。
/// 上半：成员实时状态墙（直接绑定主 VM 的 Members 集合，10s 状态流零成本）；
/// 下半：当前批次实时视图 + 历史批次列表（读 log/dodoco_stats.*.jsonl）。
/// 卡死心跳：本地任务运行中（复用主 VM 最近状态快照，不新起 IPC）但日志超 N 分钟无新行 →
/// SuspectedStall=true（红色横幅）+ 走嘟嘟可告警通道（红点+提示音+托盘），恢复有日志自动解除。
/// </summary>
public sealed class HoeingStatsViewModel : ViewModelBase, IDisposable
{
    /// <summary>当前批次显示刷新间隔（时长数字跳动）。</summary>
    private static readonly TimeSpan DisplayRefreshInterval = TimeSpan.FromSeconds(5);
    /// <summary>历史批次内存上限。</summary>
    private const int MaxHistoryInMemory = 200;

    private readonly HoeingStatsService _stats;
    private readonly MainViewModel _mainVm;
    private readonly BgiLogTailService _tail;
    private readonly DodocoSettingsService _settings;
    /// <summary>告警通道（嘟嘟可导航红点 + 提示音 + 托盘气泡，由 DodocoViewModel 提供，内部尊重全部静音）。</summary>
    private readonly Action<string, string> _raiseAlert;
    private readonly DispatcherTimer _refreshTimer;
    /// <summary>本次卡死 episode 是否已告警（一次卡死只告警一次，恢复后重置）。</summary>
    private bool _stallAlerted;

    public HoeingStatsViewModel(HoeingStatsService stats, MainViewModel mainVm,
        BgiLogTailService tail, Action<string, string> raiseAlert, DodocoSettingsService settings)
    {
        _stats = stats;
        _mainVm = mainVm;
        _tail = tail;
        _raiseAlert = raiseAlert;
        _settings = settings;

        _stats.BatchChanged += OnBatchChanged;
        _stats.BatchFinalized += OnBatchFinalized;

        _refreshTimer = new DispatcherTimer { Interval = DisplayRefreshInterval };
        _refreshTimer.Tick += (_, _) => { RefreshCurrentBatch(); CheckStall(); };
        _refreshTimer.Start();

        ReloadHistory();
    }

    // ========== 成员实时状态墙（直接复用主 VM 成员集合） ==========

    /// <summary>成员列表（含 TaskRunning/CurrentTaskGroupName/CurrentRouteDisplay/AutoHoeingProgress）。</summary>
    public ObservableCollection<MemberViewModel> Members => _mainVm.Members;

    // ========== 当前批次 ==========

    private bool _hasCurrentBatch;
    public bool HasCurrentBatch { get => _hasCurrentBatch; set => SetProperty(ref _hasCurrentBatch, value); }

    private string _currentBatchTitle = "暂无进行中的锄地批次";
    public string CurrentBatchTitle { get => _currentBatchTitle; set => SetProperty(ref _currentBatchTitle, value); }

    public ObservableCollection<GroupStatView> CurrentGroups { get; } = new();

    private string _currentRouteText = "";
    /// <summary>当前进行中路线（最后一条未结束的路线）。</summary>
    public string CurrentRouteText { get => _currentRouteText; set => SetProperty(ref _currentRouteText, value); }

    private string _currentElapsedText = "";
    /// <summary>批次累计时长。</summary>
    public string CurrentElapsedText { get => _currentElapsedText; set => SetProperty(ref _currentElapsedText, value); }

    private string _currentEstimateText = "";
    /// <summary>最近一次收益预估。</summary>
    public string CurrentEstimateText { get => _currentEstimateText; set => SetProperty(ref _currentEstimateText, value); }

    // ========== P5-D 收益报表 ==========

    private string _currentProfitText = "";
    /// <summary>当前批次收益效率（预估摩拉 ÷ 已运行小时）。</summary>
    public string CurrentProfitText { get => _currentProfitText; set => SetProperty(ref _currentProfitText, value); }

    /// <summary>最近 7 天每日收益汇总。</summary>
    public ObservableCollection<DailyProfitView> DailyProfits { get; } = new();

    /// <summary>历史批次原始记录镜像（供收益聚合；与 HistoryBatches 同序同步）。</summary>
    private readonly List<HoeingBatchRecord> _historyRecords = new();

    /// <summary>批次的预估摩拉合计（新数据读 Estimates，旧 JSONL 兜底单条 Estimate）。</summary>
    private static int BatchMora(HoeingBatchRecord b) =>
        b.Estimates.Count > 0 ? b.Estimates.Sum(e => e.Mora) : b.Estimate?.Mora ?? 0;

    private void ComputeCurrentProfit(HoeingBatchRecord? batch)
    {
        if (batch == null)
        {
            CurrentProfitText = "";
            return;
        }
        var mora = BatchMora(batch);
        var hours = (DateTime.Now - batch.StartTime).TotalHours;
        CurrentProfitText = mora > 0 && hours > 0.001
            ? $"本批次预估 {mora:N0} 摩拉 · 约 {mora / hours:N0} 摩拉/小时"
            : "本批次暂无收益预估（选择路线时产生）";
    }

    /// <summary>聚合最近 7 天每日收益（总摩拉预估 / 总时长 / 摩拉每小时，横条相对最大值）。</summary>
    private void RebuildDailyProfits()
    {
        var since = DateTime.Today.AddDays(-6);
        var rows = _historyRecords
            .Where(b => b.StartTime >= since)
            .GroupBy(b => b.StartTime.Date)
            .Select(g => new
            {
                Date = g.Key,
                Mora = g.Sum(BatchMora),
                Duration = TimeSpan.FromSeconds(g.Sum(b => b.DurationSeconds ?? 0))
            })
            .OrderByDescending(r => r.Date)
            .ToList();
        var max = rows.Count > 0 ? rows.Max(r => r.Mora) : 0;
        DailyProfits.Clear();
        foreach (var r in rows)
        {
            DailyProfits.Add(new DailyProfitView
            {
                DateText = r.Date.ToString("MM-dd"),
                TotalMoraText = $"{r.Mora:N0} 摩拉",
                DurationText = HoeingStatsViewModel.FormatDuration(r.Duration),
                MoraPerHourText = r.Duration.TotalHours > 0.001
                    ? $"{r.Mora / r.Duration.TotalHours:N0} 摩拉/时"
                    : "-",
                BarPct = max > 0 ? Math.Max(2, r.Mora * 100.0 / max) : 0
            });
        }
    }

    // ========== P5-F 甘特时间线 ==========

    /// <summary>甘特条集合（组条在前，路线条在后）。</summary>
    public ObservableCollection<GanttBarView> GanttBars { get; } = new();

    private string _ganttTitle = "事件时间线";
    public string GanttTitle { get => _ganttTitle; set => SetProperty(ref _ganttTitle, value); }

    private string _ganttAxisText = "";
    /// <summary>轴文案（起止时刻 + 全程时长）。</summary>
    public string GanttAxisText { get => _ganttAxisText; set => SetProperty(ref _ganttAxisText, value); }

    private HoeingBatchView? _selectedHistoryBatch;
    /// <summary>选中的历史批次：非空时甘特显示该批次，空时跟随当前批次（5s 刷新重绘）。</summary>
    public HoeingBatchView? SelectedHistoryBatch
    {
        get => _selectedHistoryBatch;
        set { if (SetProperty(ref _selectedHistoryBatch, value)) RebuildGantt(); }
    }

    public RelayCommand ShowCurrentGanttCommand => new(_ => SelectedHistoryBatch = null);

    private void RebuildGantt()
    {
        var record = _selectedHistoryBatch?.Record ?? _stats.GetCurrentBatchSnapshot();
        GanttBars.Clear();
        if (record == null)
        {
            GanttTitle = "事件时间线（暂无批次数据）";
            GanttAxisText = "";
            return;
        }

        var start = record.StartTime;
        var end = record.EndTime ?? DateTime.Now;
        var total = (end - start).TotalSeconds;
        if (total < 1) total = 1;
        double Pct(DateTime t) => Math.Clamp((t - start).TotalSeconds / total * 100.0, 0, 100);

        foreach (var g in record.Groups)
        {
            var l = Pct(g.StartTime);
            var r = Pct(g.EndTime ?? end);
            GanttBars.Add(new GanttBarView
            {
                Name = g.Name,
                LeftPct = l,
                WidthPct = Math.Max(1, r - l),
                IsGroup = true,
                ToolTip = $"配置组 {g.Name}\n{g.StartTime:HH:mm:ss} → {(g.EndTime?.ToString("HH:mm:ss") ?? "进行中")}" +
                          $" · {FormatDuration((g.EndTime ?? end) - g.StartTime)}"
            });
        }
        foreach (var rt in record.Routes)
        {
            var l = Pct(rt.StartTime);
            var r = Pct(rt.EndTime ?? end);
            GanttBars.Add(new GanttBarView
            {
                Name = rt.Name,
                LeftPct = l,
                WidthPct = Math.Max(1, r - l),
                IsGroup = false,
                Interrupted = rt.Interrupted,
                ToolTip = $"{(rt.Interrupted ? "⚠ 中断 " : "")}路线 {rt.Name}\n{rt.StartTime:HH:mm:ss} → {(rt.EndTime?.ToString("HH:mm:ss") ?? "进行中")}" +
                          $" · {FormatDuration((rt.EndTime ?? end) - rt.StartTime)}"
            });
        }

        GanttTitle = _selectedHistoryBatch != null
            ? $"时间线 · {_selectedHistoryBatch.Title}"
            : "时间线 · 当前批次（5 秒刷新）";
        GanttAxisText = $"{start:HH:mm} → {end:HH:mm} · 全程 {FormatDuration(end - start)}";
    }

    private int _currentInterruptedCount;
    public int CurrentInterruptedCount
    {
        get => _currentInterruptedCount;
        set { SetProperty(ref _currentInterruptedCount, value); OnPropertyChanged(nameof(CurrentInterruptedText)); }
    }
    public string CurrentInterruptedText => CurrentInterruptedCount > 0 ? $"中断 {CurrentInterruptedCount} 次" : "";

    // ========== 历史批次 ==========

    public ObservableCollection<HoeingBatchView> HistoryBatches { get; } = new();

    public RelayCommand RefreshHistoryCommand => new(_ => ReloadHistory());

    // ========== 卡死心跳检测（§5-A） ==========

    private bool _suspectedStall;
    /// <summary>疑似卡死：任务运行中但日志超时无新行。</summary>
    public bool SuspectedStall
    {
        get => _suspectedStall;
        set { SetProperty(ref _suspectedStall, value); OnPropertyChanged(nameof(StallBannerText)); }
    }

    /// <summary>卡死判定阈值（分钟）：日志超过该时长无新行且任务运行中 → 疑似卡死。
    /// 默认 3 分钟，持久化到 dodoco_settings.json（P4 统一设置收口）。</summary>
    public int StallThresholdMinutes
    {
        get => _settings.Current.StallThresholdMinutes;
        set
        {
            if (value < 1) return;
            _settings.Update(s => s.StallThresholdMinutes = value);
            OnPropertyChanged();
        }
    }

    /// <summary>红色横幅文本。</summary>
    public string StallBannerText =>
        SuspectedStall
            ? $"⚠ 疑似卡死：任务运行中，但 BGI 日志已超过 {StallThresholdMinutes} 分钟没有新行（最后日志 {LastLogAgoText}）"
            : "";

    /// <summary>最后一条日志距今的显示文本。</summary>
    public string LastLogAgoText
    {
        get
        {
            var last = _tail.LastEntryTime;
            return last == DateTime.MinValue ? "从未收到" : $"{(int)(DateTime.Now - last).TotalMinutes} 分钟前";
        }
    }

    /// <summary>心跳检测：任务运行中（主 VM 最近状态快照）且日志超时无新行 → 疑似卡死。</summary>
    private void CheckStall()
    {
        OnPropertyChanged(nameof(LastLogAgoText));
        var status = _mainVm.LatestLocalStatus;
        var taskRunning = status is { TaskRunning: true } or { AutoHoeingRunning: true };
        var last = _tail.LastEntryTime;
        var silent = last != DateTime.MinValue && DateTime.Now - last > TimeSpan.FromMinutes(StallThresholdMinutes);

        if (taskRunning && silent)
        {
            if (!SuspectedStall) SuspectedStall = true;
            if (!_stallAlerted)
            {
                _stallAlerted = true;
                _raiseAlert("嘟嘟可卡死心跳",
                    $"任务运行中但日志超过 {StallThresholdMinutes} 分钟无新行，BGI 疑似卡死");
            }
        }
        else if (SuspectedStall && (!taskRunning || (last != DateTime.MinValue && DateTime.Now - last <= TimeSpan.FromMinutes(StallThresholdMinutes))))
        {
            // 恢复：有新日志或任务停止 → 解除卡死状态
            SuspectedStall = false;
            _stallAlerted = false;
        }
        else if (!taskRunning)
        {
            _stallAlerted = false;
        }
    }

    // ========== 批次显示刷新 ==========

    private void OnBatchChanged(HoeingBatchRecord? snapshot)
    {
        Application.Current.Dispatcher.BeginInvoke(() => ApplyBatchSnapshot(snapshot));
    }

    private void OnBatchFinalized(HoeingBatchRecord record)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            HistoryBatches.Insert(0, new HoeingBatchView(record));
            while (HistoryBatches.Count > MaxHistoryInMemory)
                HistoryBatches.RemoveAt(HistoryBatches.Count - 1);
            _historyRecords.Insert(0, record);
            while (_historyRecords.Count > MaxHistoryInMemory)
                _historyRecords.RemoveAt(_historyRecords.Count - 1);
            RebuildDailyProfits();
        });
    }

    /// <summary>5s 轮询刷新：让进行中的时长数字跳动（无新事件时也走这里）。</summary>
    private void RefreshCurrentBatch() => ApplyBatchSnapshot(_stats.GetCurrentBatchSnapshot());

    private void ApplyBatchSnapshot(HoeingBatchRecord? batch)
    {
        HasCurrentBatch = batch != null;
        ComputeCurrentProfit(batch);
        // 甘特源为当前批次时随 5 秒刷新重绘；选中历史批次时不打扰
        if (_selectedHistoryBatch == null) RebuildGantt();
        if (batch == null)
        {
            CurrentBatchTitle = "暂无进行中的锄地批次";
            CurrentGroups.Clear();
            CurrentRouteText = "";
            CurrentElapsedText = "";
            CurrentEstimateText = "";
            CurrentInterruptedCount = 0;
            return;
        }

        var gen = batch.Generation != null ? $" · 上线代 {batch.Generation}" : "";
        CurrentBatchTitle = $"批次 {batch.BatchId}{gen} · 开始于 {batch.StartTime:HH:mm:ss}";
        CurrentElapsedText = $"累计 {FormatDuration(DateTime.Now - batch.StartTime)}";

        // 组列表：同名同开始时间的行复用（避免整列重建闪动），否则重建
        if (CurrentGroups.Count != batch.Groups.Count ||
            CurrentGroups.Zip(batch.Groups).Any(p => p.First.Name != p.Second.Name))
        {
            CurrentGroups.Clear();
            foreach (var g in batch.Groups) CurrentGroups.Add(new GroupStatView(g));
        }
        else
        {
            for (var i = 0; i < batch.Groups.Count; i++) CurrentGroups[i].Refresh(batch.Groups[i]);
        }

        // 当前路线 = 最后一条未结束的路线
        var openRoute = batch.Routes.LastOrDefault(r => r.EndTime == null);
        CurrentRouteText = openRoute != null
            ? $"当前路线: {openRoute.Name}（已跑 {FormatDuration(DateTime.Now - openRoute.StartTime)}）"
            : "";
        CurrentInterruptedCount = batch.InterruptedCount;
        CurrentEstimateText = batch.Estimate != null
            ? $"收益预估: 精英 {batch.Estimate.Elite}, 小怪 {batch.Estimate.Mobs}, {batch.Estimate.Mora} 摩拉, 预计 {batch.Estimate.EtaText}"
            : "";
    }

    private void ReloadHistory()
    {
        Task.Run(() =>
        {
            var batches = _stats.LoadHistoryBatches();
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                HistoryBatches.Clear();
                foreach (var b in batches.Take(MaxHistoryInMemory))
                    HistoryBatches.Add(new HoeingBatchView(b));
                _historyRecords.Clear();
                _historyRecords.AddRange(batches.Take(MaxHistoryInMemory));
                // 刷新后原选中对象已失效，甘特回退到当前批次
                if (_selectedHistoryBatch != null) SelectedHistoryBatch = null;
                RebuildDailyProfits();
            });
        });
    }

    /// <summary>时长格式化：1时23分 / 5分12秒 / 45秒。</summary>
    internal static string FormatDuration(TimeSpan ts)
    {
        if (ts.TotalHours >= 1) return $"{(int)ts.TotalHours}时{ts.Minutes}分";
        if (ts.TotalMinutes >= 1) return $"{(int)ts.TotalMinutes}分{ts.Seconds}秒";
        return $"{(int)ts.TotalSeconds}秒";
    }

    public void Dispose()
    {
        _refreshTimer.Stop();
        _stats.BatchChanged -= OnBatchChanged;
        _stats.BatchFinalized -= OnBatchFinalized;
    }
}

/// <summary>当前批次里的组显示项（进行中的组时长实时跳动）。</summary>
public sealed class GroupStatView : ViewModelBase
{
    public GroupStatView(GroupStat g) => Refresh(g);

    public string Name { get; private set; } = "";
    public string StartText { get; private set; } = "";
    public string DurationText { get; private set; } = "";
    public bool IsRunning { get; private set; }

    public void Refresh(GroupStat g)
    {
        Name = g.Name;
        StartText = g.StartTime.ToString("HH:mm:ss");
        IsRunning = g.EndTime == null;
        var duration = g.EndTime != null ? g.EndTime.Value - g.StartTime : DateTime.Now - g.StartTime;
        DurationText = (IsRunning ? "进行中 · " : "") + HoeingStatsViewModel.FormatDuration(duration);
        OnPropertyChanged(nameof(Name));
        OnPropertyChanged(nameof(StartText));
        OnPropertyChanged(nameof(DurationText));
        OnPropertyChanged(nameof(IsRunning));
    }
}

/// <summary>历史批次显示项（摘要 + 展开明细）。</summary>
public sealed class HoeingBatchView
{
    private readonly HoeingBatchRecord _record;

    public HoeingBatchView(HoeingBatchRecord record) => _record = record;

    /// <summary>原始记录（甘特时间线取数据用）。</summary>
    internal HoeingBatchRecord Record => _record;

    public string Title => $"{_record.StartTime:MM-dd HH:mm} 批次（{HoeingStatsViewModel.FormatDuration(Duration)}）";
    public TimeSpan Duration =>
        _record.EndTime != null ? _record.EndTime.Value - _record.StartTime : TimeSpan.Zero;

    public string Summary
    {
        get
        {
            var parts = new List<string> { $"{_record.Groups.Count} 组", $"{_record.Routes.Count} 条路线" };
            if (_record.InterruptedCount > 0) parts.Add($"中断 {_record.InterruptedCount} 次");
            if (!string.IsNullOrEmpty(_record.EndReason)) parts.Add(_record.EndReason);
            return string.Join(" · ", parts);
        }
    }

    /// <summary>各组时长明细。</summary>
    public IEnumerable<string> GroupDetails => _record.Groups.Select(g =>
        $"{g.Name}：{(g.DurationSeconds != null ? HoeingStatsViewModel.FormatDuration(TimeSpan.FromSeconds(g.DurationSeconds.Value)) : "进行中收尾")}" +
        $"（{g.StartTime:HH:mm} 开始）");

    /// <summary>路线耗时 TOP5（最慢在前，中断的标 ⚠）。</summary>
    public IEnumerable<string> TopRoutes => _record.Routes
        .OrderByDescending(r => r.DurationSeconds ?? 0)
        .Take(5)
        .Select(r => $"{(r.Interrupted ? "⚠ " : "")}{r.Name}：" +
                     $"{(r.DurationSeconds != null ? HoeingStatsViewModel.FormatDuration(TimeSpan.FromSeconds(r.DurationSeconds.Value)) : "-")}");

    /// <summary>联机会话明细（人数/时长/退出原因）。</summary>
    public IEnumerable<string> SessionDetails => _record.Sessions.Select(s =>
        $"联机会话{(s.PlayerCount != null ? $"（{s.PlayerCount} 人）" : "")}：" +
        $"{(s.DurationSeconds != null ? HoeingStatsViewModel.FormatDuration(TimeSpan.FromSeconds(s.DurationSeconds.Value)) : "-")}" +
        $"{(s.ExitReason != null ? $" · 退出原因: {s.ExitReason}" : "")}");

    public string EstimateText => _record.Estimate != null
        ? $"收益预估: 精英 {_record.Estimate.Elite}, 小怪 {_record.Estimate.Mobs}, {_record.Estimate.Mora} 摩拉, 预计 {_record.Estimate.EtaText}"
        : "";
}

/// <summary>每日收益汇总行（P5-D 收益报表）。</summary>
public sealed class DailyProfitView
{
    public string DateText { get; set; } = "";
    /// <summary>当日预估摩拉合计。</summary>
    public string TotalMoraText { get; set; } = "";
    /// <summary>当日锄地总时长。</summary>
    public string DurationText { get; set; } = "";
    /// <summary>摩拉/小时。</summary>
    public string MoraPerHourText { get; set; } = "";
    /// <summary>横条百分比（相对 7 天内最大值，0-100）。</summary>
    public double BarPct { get; set; }
}

/// <summary>甘特条（P5-F 事件时间线）：组条与路线条共用，LeftPct/WidthPct 映射到车道宽度。</summary>
public sealed class GanttBarView
{
    public string Name { get; set; } = "";
    /// <summary>左缘百分比（0-100）。</summary>
    public double LeftPct { get; set; }
    /// <summary>宽度百分比（最小 1，0-100）。</summary>
    public double WidthPct { get; set; }
    /// <summary>是否配置组条（金色）；false=路线条（Water 色）。</summary>
    public bool IsGroup { get; set; }
    /// <summary>中断路线（Pyro 红边框）。</summary>
    public bool Interrupted { get; set; }
    public string ToolTip { get; set; } = "";
}
