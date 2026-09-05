using System.Collections.ObjectModel;
using System.Text;
using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 每日运行日报（锄地数据 Tab）：以天为单位展示各配置组运行时长，
/// 括号内为与前一有数据日的同组时长差；含联机锄地的组可展开「本轮锄地结束统计」轮次明细。
/// 数据来自 DailyReportService 解析 BGI 按天日志；选中"今天"时每 30 秒自动重建（当天数据持续更新）。
/// </summary>
public sealed class DailyReportViewModel : ViewModelBase, IDisposable
{
    /// <summary>选中"今天"时的自动刷新间隔。</summary>
    private static readonly TimeSpan AutoRefreshInterval = TimeSpan.FromSeconds(30);

    private readonly DailyReportService _service;
    private readonly DispatcherTimer _autoTimer;
    /// <summary>重建日期下拉时抑制选择变化触发的重复解析。</summary>
    private bool _suppressSelectionChanged;
    /// <summary>防重入（后台解析未结束时跳过新请求）。</summary>
    private int _refreshing;

    public DailyReportViewModel(DailyReportService service)
    {
        _service = service;
        RefreshCommand = new RelayCommand(_ => Rebuild());

        _autoTimer = new DispatcherTimer { Interval = AutoRefreshInterval };
        _autoTimer.Tick += (_, _) =>
        {
            if (SelectedDateIsToday()) Rebuild();
        };
        _autoTimer.Start();

        Rebuild();
    }

    // ========== 日期选择 ==========

    /// <summary>可选日期（yyyy-MM-dd，今天带标注），倒序最新在前。</summary>
    public ObservableCollection<string> DateItems { get; } = new();

    private string? _selectedDateItem;
    public string? SelectedDateItem
    {
        get => _selectedDateItem;
        set
        {
            if (SetProperty(ref _selectedDateItem, value) && !_suppressSelectionChanged)
                Rebuild();
        }
    }

    // ========== 报告内容 ==========

    public ObservableCollection<DailyReportGroupView> Groups { get; } = new();

    private string _reportTitle = "配置组运行日报";
    public string ReportTitle { get => _reportTitle; set => SetProperty(ref _reportTitle, value); }

    private string _compareBaseText = "";
    /// <summary>对比基准说明（括号内 diff 的参照日）。</summary>
    public string CompareBaseText { get => _compareBaseText; set => SetProperty(ref _compareBaseText, value); }

    private string _totalText = "";
    public string TotalText { get => _totalText; set => SetProperty(ref _totalText, value); }

    private string _emptyText = "";
    /// <summary>无数据提示（有数据时为空串）。</summary>
    public string EmptyText { get => _emptyText; set => SetProperty(ref _emptyText, value); }

    // ========== 全天任务总览（与日报同一日期选择） ==========

    /// <summary>总览行（扁平化文本：顶层编号行 + 全角空格缩进的子单元行）。</summary>
    public ObservableCollection<string> OverviewRows { get; } = new();

    private string _overviewTotalText = "";
    /// <summary>当天 BGI 总运行时长（顶层单元互斥求和）。</summary>
    public string OverviewTotalText { get => _overviewTotalText; set => SetProperty(ref _overviewTotalText, value); }

    private bool _hasOverview;
    public bool HasOverview { get => _hasOverview; set => SetProperty(ref _hasOverview, value); }

    public RelayCommand RefreshCommand { get; }

    // ========== 复制报告 ==========

    public RelayCommand CopyReportCommand => new(_ => CopyReport());

    private string _copyStatusText = "";
    /// <summary>复制结果反馈（显示在报告标题旁）。</summary>
    public string CopyStatusText { get => _copyStatusText; set => SetProperty(ref _copyStatusText, value); }

    /// <summary>把当前报告按用户分享格式输出到剪贴板（标题 + 组行 + 合计 + 联机轮次明细 + 全天任务总览）。</summary>
    private void CopyReport()
    {
        if (Groups.Count == 0 && OverviewRows.Count == 0)
        {
            CopyStatusText = "当前无报告内容";
            return;
        }
        var sb = new StringBuilder();
        if (Groups.Count > 0)
        {
            sb.AppendLine(ReportTitle);
            foreach (var g in Groups)
            {
                sb.Append(g.LineText);
                if (!string.IsNullOrEmpty(g.MergedNote)) sb.Append($"（{g.MergedNote}）");
                sb.AppendLine();
            }
            if (!string.IsNullOrEmpty(TotalText)) sb.AppendLine(TotalText);

            var roundGroups = Groups.Where(g => g.Rounds.Count > 0).ToList();
            if (roundGroups.Count > 0)
            {
                sb.AppendLine();
                foreach (var g in roundGroups)
                {
                    if (roundGroups.Count > 1) sb.AppendLine($"【{g.Name}】");
                    foreach (var r in g.Rounds) sb.AppendLine(r);
                }
            }
        }

        if (OverviewRows.Count > 0)
        {
            sb.AppendLine();
            sb.AppendLine($"BGI 全天任务总览（{OverviewTotalText}）");
            foreach (var r in OverviewRows) sb.AppendLine(r);
        }

        try
        {
            Clipboard.SetText(sb.ToString());
            CopyStatusText = "已复制到剪贴板";
        }
        catch (Exception)
        {
            // 剪贴板被其它程序占用时 SetText 抛 COMException
            CopyStatusText = "复制失败（剪贴板被占用），请重试";
        }
    }

    // ========== 解析与重建 ==========

    private bool SelectedDateIsToday() =>
        TryParseSelectedDate(out var d) && d == DateOnly.FromDateTime(DateTime.Today);

    private bool TryParseSelectedDate(out DateOnly date)
    {
        date = default;
        var s = _selectedDateItem;
        return s != null && s.Length >= 10
            && DateOnly.TryParseExact(s[..10], "yyyy-MM-dd", out date);
    }

    /// <summary>重建日期列表 + 解析选中日（后台线程解析，UI 线程回填）。</summary>
    private void Rebuild()
    {
        if (Interlocked.Exchange(ref _refreshing, 1) == 1) return;
        var selectedDate = TryParseSelectedDate(out var d) ? d : (DateOnly?)null;
        Task.Run(() =>
        {
            try
            {
                var dates = _service.EnumerateDates();
                var target = selectedDate ?? dates.FirstOrDefault();
                if (target == default)
                {
                    Apply(dates, target, null, null, null);
                    return;
                }
                var report = _service.BuildReport(target);
                var overview = _service.BuildOverview(target);
                // 对比基准：早于选中日的最近一个有日志的日期
                var baseDate = dates.Where(x => x < target).DefaultIfEmpty().Max();
                var baseReport = baseDate != default ? _service.BuildReport(baseDate) : null;
                Apply(dates, target, report, baseReport, overview);
            }
            finally
            {
                Interlocked.Exchange(ref _refreshing, 0);
            }
        });
    }

    private void Apply(List<DateOnly> dates, DateOnly target, DailyReport? report, DailyReport? baseReport, DayOverview? overview)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // 日期下拉重建（今天标注），尽量保持原选择
            _suppressSelectionChanged = true;
            try
            {
                var today = DateOnly.FromDateTime(DateTime.Today);
                DateItems.Clear();
                foreach (var dt in dates)
                    DateItems.Add(dt == today ? $"{dt:yyyy-MM-dd}（今天）" : $"{dt:yyyy-MM-dd}");
                var wanted = target == today ? $"{target:yyyy-MM-dd}（今天）" : $"{target:yyyy-MM-dd}";
                if (DateItems.Contains(wanted)) _selectedDateItem = wanted;
                else _selectedDateItem = DateItems.FirstOrDefault();
                OnPropertyChanged(nameof(SelectedDateItem));
            }
            finally
            {
                _suppressSelectionChanged = false;
            }

            // ---- 配置组日报 ----
            Groups.Clear();
            if (report == null || report.Groups.Count == 0)
            {
                ReportTitle = target == default ? "配置组运行日报" : $"配置组运行日报 ({target:yyyy-MM-dd})";
                CompareBaseText = "";
                TotalText = "";
                EmptyText = DateItems.Count == 0
                    ? "未找到 BGI 日志（请先在设置中配置 BGI 路径）"
                    : "该日无配置组运行记录";
            }
            else
            {
                ReportTitle = $"配置组运行日报 ({report.Date:yyyy-MM-dd})";
                CompareBaseText = baseReport != null
                    ? $"括号 = 与 {baseReport.Date:yyyy-MM-dd} 对比的时间差"
                    : "无更早日志可对比";
                EmptyText = "";

                var baseByName = baseReport?.Groups.ToDictionary(g => g.Name, g => g, StringComparer.Ordinal);
                for (var i = 0; i < report.Groups.Count; i++)
                {
                    var g = report.Groups[i];
                    var diff = DiffText(g, baseByName);
                    var running = g.HasUnclosedRun ? "（含未正常结束的一次）" : "";
                    Groups.Add(new DailyReportGroupView
                    {
                        Name = g.Name,
                        LineText = $"{i}.【{g.Name}--{FormatHms(g.TotalDuration)}{running}】{diff}",
                        MergedNote = g.RunCount > 1 ? $"当天执行 {g.RunCount} 次，已合并" : "",
                        Rounds = g.Rounds.ToList()
                    });
                }
                TotalText = $"【合计--{FormatHms(report.TotalDuration)}】";
            }

            // ---- 全天任务总览（顶层单元互斥计时，子层仅展示） ----
            OverviewRows.Clear();
            if (overview == null || overview.TopUnits.Count == 0)
            {
                HasOverview = false;
                OverviewTotalText = "";
            }
            else
            {
                HasOverview = true;
                OverviewTotalText = $"全天总运行 {FormatHms(overview.TotalDuration)}";
                for (var i = 0; i < overview.TopUnits.Count; i++)
                {
                    var u = overview.TopUnits[i];
                    OverviewRows.Add($"{i}.【{UnitName(u)}--{FormatHms(u.Duration)}{UnclosedNote(u)}】");
                    AppendChildren(u, 1);
                }
            }
        });
    }

    /// <summary>子单元行（递归扁平化，全角空格缩进 + ├ 前缀）。</summary>
    private void AppendChildren(OverviewUnit unit, int depth)
    {
        foreach (var c in unit.Children.OrderBy(c => c.Start))
        {
            OverviewRows.Add($"{new string('　', depth)}├ {UnitName(c)}--{FormatHms(c.Duration)}{UnclosedNote(c)}");
            AppendChildren(c, depth + 1);
        }
    }

    private static string UnitName(OverviewUnit u) =>
        string.IsNullOrEmpty(u.Name) ? u.Kind : $"{u.Kind} · {u.Name}";

    private static string UnclosedNote(OverviewUnit u) => u.Unclosed ? "（未正常结束）" : "";

    /// <summary>与基准日同组时长的差值文本：负值 (-5m1s)、正值 (1m21s)、持平 (0s)；基准日无此组 (新)；无基准日 (—)。</summary>
    private static string DiffText(DailyReportGroup g, Dictionary<string, DailyReportGroup>? baseByName)
    {
        if (baseByName == null) return "(—)";
        if (!baseByName.TryGetValue(g.Name, out var prev)) return "(新)";
        var diff = g.TotalDuration - prev.TotalDuration;
        return diff < TimeSpan.Zero ? $"(-{FormatHms(diff.Negate())})" : $"({FormatHms(diff)})";
    }

    /// <summary>时长格式化（日报样式）：1h32m18s / 9m0s / 37s。</summary>
    internal static string FormatHms(TimeSpan ts)
    {
        var totalSeconds = (int)ts.TotalSeconds;
        var h = totalSeconds / 3600;
        var m = totalSeconds % 3600 / 60;
        var s = totalSeconds % 60;
        if (h > 0) return $"{h}h{m}m{s}s";
        if (m > 0) return $"{m}m{s}s";
        return $"{s}s";
    }

    public void Dispose() => _autoTimer.Stop();
}

/// <summary>日报组行（LineText 即用户报告格式：0.【特产采集--1h32m18s】(-5m1s)）。</summary>
public sealed class DailyReportGroupView
{
    /// <summary>配置组名（复制报告时轮次明细的分节标题用）。</summary>
    public string Name { get; set; } = "";

    /// <summary>整行文本：序号.【组名--时长】（与基准日差值）。</summary>
    public string LineText { get; set; } = "";

    /// <summary>同名组当天多次执行的合并说明（仅执行多次时非空）。</summary>
    public string MergedNote { get; set; } = "";

    /// <summary>联机轮次明细（「本轮锄地结束统计」原文行）。</summary>
    public List<string> Rounds { get; set; } = new();

    public bool HasRounds => Rounds.Count > 0;
}
