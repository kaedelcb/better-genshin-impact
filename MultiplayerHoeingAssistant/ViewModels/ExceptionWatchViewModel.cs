using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 异常监控 Tab 的 ViewModel（P2 / F3）。
/// 规则管理（增删改、启用开关、即时持久化）+ 异常记录列表（时间倒序，按规则/级别/日期筛选）
/// + 点击记录跳转到日志浏览 Tab 对应位置（经 DodocoViewModel.JumpToRecord，用 FileOffset 定位）。
/// </summary>
public sealed class ExceptionWatchViewModel : ViewModelBase
{
    private readonly KeywordWatchService _service;
    private readonly DodocoViewModel _shell;
    /// <summary>内存记录上限（防长期运行内存膨胀，JSONL 落盘不受影响）。</summary>
    private const int MaxRecordsInMemory = 2000;

    public ExceptionWatchViewModel(KeywordWatchService service, DodocoViewModel shell)
    {
        _service = service;
        _shell = shell;

        foreach (var r in _service.GetRules()) Rules.Add(new WatchRuleItem(r, PersistRules));

        _service.RecordAdded += OnRecordAdded;
        _service.RecordMerged += OnRecordMerged;

        // 异常记录按天分组显示（组头=日期，组内时间倒序与集合顺序一致）
        var view = System.Windows.Data.CollectionViewSource.GetDefaultView(FilteredRecords);
        view.GroupDescriptions.Add(new System.Windows.Data.PropertyGroupDescription(nameof(ExceptionRecord.DayGroup)));

        ReloadHistory();
    }

    // ========== 规则管理 ==========

    public ObservableCollection<WatchRuleItem> Rules { get; } = new();

    // 新规则编辑表单
    private string _newRuleName = "";
    public string NewRuleName { get => _newRuleName; set => SetProperty(ref _newRuleName, value); }

    private string _newRulePattern = "";
    public string NewRulePattern { get => _newRulePattern; set => SetProperty(ref _newRulePattern, value); }

    private bool _newRuleIsRegex;
    public bool NewRuleIsRegex { get => _newRuleIsRegex; set => SetProperty(ref _newRuleIsRegex, value); }

    private int _newRuleMinLevelIndex = 2; // 默认 WRN 起
    /// <summary>最低级别下拉索引：0=DBG 1=INF 2=WRN 3=ERR。</summary>
    public int NewRuleMinLevelIndex { get => _newRuleMinLevelIndex; set => SetProperty(ref _newRuleMinLevelIndex, value); }

    private bool _newRuleAlert = true;
    public bool NewRuleAlert { get => _newRuleAlert; set => SetProperty(ref _newRuleAlert, value); }

    private bool _newRuleSnapshot;
    /// <summary>新规则是否命中存快照（默认关，防误开宽规则涨磁盘）。</summary>
    public bool NewRuleSnapshot { get => _newRuleSnapshot; set => SetProperty(ref _newRuleSnapshot, value); }

    private string _ruleStatus = "";
    public string RuleStatus { get => _ruleStatus; set => SetProperty(ref _ruleStatus, value); }

    public RelayCommand AddRuleCommand => new(_ =>
    {
        if (string.IsNullOrWhiteSpace(NewRulePattern) && NewRuleMinLevelIndex < 3)
        {
            RuleStatus = "请填写匹配内容（或把最低级别设为 ERR 做级别兜底规则）";
            return;
        }
        if (NewRuleIsRegex)
        {
            try { _ = new System.Text.RegularExpressions.Regex(NewRulePattern); }
            catch (Exception ex) { RuleStatus = $"正则无效: {ex.Message}"; return; }
        }
        var rule = new WatchRule
        {
            Name = string.IsNullOrWhiteSpace(NewRuleName) ? NewRulePattern : NewRuleName.Trim(),
            Pattern = NewRulePattern.Trim(),
            IsRegex = NewRuleIsRegex,
            MinLevel = LogLevels.All[Math.Clamp(NewRuleMinLevelIndex, 0, 3)],
            Alert = NewRuleAlert,
            Snapshot = NewRuleSnapshot,
            Enabled = true
        };
        Rules.Add(new WatchRuleItem(rule, PersistRules));
        NewRuleName = "";
        NewRulePattern = "";
        RuleStatus = $"已添加规则「{rule.Name}」";
        PersistRules();
        RebuildRuleFilterItems();
    });

    public RelayCommand DeleteRuleCommand => new(p =>
    {
        if (p is not WatchRuleItem item) return;
        Rules.Remove(item);
        PersistRules();
        RebuildRuleFilterItems();
    });

    /// <summary>规则有任何变动（启用/告警开关、内容编辑）即时持久化。</summary>
    private void PersistRules()
    {
        _service.SaveRules(Rules.Select(r => r.ToModel()).ToList());
    }

    // ========== 异常记录列表 ==========

    /// <summary>全部记录（时间倒序）。</summary>
    private readonly List<ExceptionRecord> _allRecords = [];
    /// <summary>筛选后的可见记录。</summary>
    public ObservableCollection<ExceptionRecord> FilteredRecords { get; } = new();

    /// <summary>诊断时间范围联动筛选（null=不过滤）；由 DodocoViewModel 在诊断范围变更时推入。</summary>
    private DateTime? _timeRangeStart;
    private DateTime? _timeRangeEnd;

    /// <summary>设置诊断时间范围联动筛选（两个都 null=清除）。范围=全天时调用方传 null。</summary>
    public void SetTimeRangeFilter(DateTime? start, DateTime? end)
    {
        _timeRangeStart = start;
        _timeRangeEnd = end;
        RebuildFiltered();
    }

    public ObservableCollection<string> RuleFilterItems { get; } = new() { "全部规则" };
    private string _selectedRuleFilter = "全部规则";
    public string SelectedRuleFilter
    {
        get => _selectedRuleFilter;
        set { if (SetProperty(ref _selectedRuleFilter, value)) RebuildFiltered(); }
    }

    public ObservableCollection<string> LevelFilterItems { get; } = new() { "全部级别", "DBG", "INF", "WRN", "ERR" };
    private string _selectedLevelFilter = "全部级别";
    public string SelectedLevelFilter
    {
        get => _selectedLevelFilter;
        set { if (SetProperty(ref _selectedLevelFilter, value)) RebuildFiltered(); }
    }

    public ObservableCollection<string> DateFilterItems { get; } = new() { "全部日期" };
    private string _selectedDateFilter = "全部日期";
    /// <summary>已加载进内存的日期（yyyy-MM-dd）；未选择的日期不预加载（历史量大时全量读盘+渲染会卡）。</summary>
    private readonly HashSet<string> _loadedDates = new();
    /// <summary>异常库磁盘上已有记录的日期（文件名解析），与内存记录日期合并出日期筛选项。</summary>
    private List<string> _fileDates = new();
    public string SelectedDateFilter
    {
        get => _selectedDateFilter;
        set
        {
            if (SetProperty(ref _selectedDateFilter, value))
            {
                EnsureDateLoaded(value);
                RebuildFiltered();
            }
        }
    }

    private string _recordStatus = "";
    public string RecordStatus { get => _recordStatus; set => SetProperty(ref _recordStatus, value); }

    private bool _isLoadingHistory;
    /// <summary>异常库历史加载中（大 JSONL 读取在后台线程，期间界面显示加载动画，避免"卡住"观感）。</summary>
    public bool IsLoadingHistory { get => _isLoadingHistory; set => SetProperty(ref _isLoadingHistory, value); }

    /// <summary>打开某条异常记录对应的事发快照目录（规则名+时刻 ±5 秒匹配 incidents 目录；无匹配给提示）。</summary>
    public RelayCommand OpenRecordIncidentCommand => new(p =>
    {
        if (p is not ExceptionRecord record) return;
        var dir = IncidentSnapshotService.FindIncidentDir(record.Time, record.RuleName);
        if (dir == null)
        {
            RecordStatus = "未找到该记录的事发快照目录（规则未开快照、触发时零帧或已被自动清理）";
            return;
        }
        try
        {
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RecordStatus = $"打开快照目录失败: {ex.Message}";
        }
    });

    /// <summary>点击记录 → 跳转到日志浏览 Tab 对应文件位置。</summary>
    public RelayCommand OpenRecordCommand => new(p =>
    {
        if (p is ExceptionRecord record) _shell.JumpToRecord(record);
    });

    /// <summary>刷新：重新读盘当前筛选范围的日期（实时新增的内存记录按 RecordKey 去重保留，RepeatCount 以内存为准）。</summary>
    public RelayCommand RefreshRecordsCommand => new(_ =>
    {
        if (SelectedDateFilter == "全部日期")
            _loadedDates.Clear();
        else
            _loadedDates.Remove(SelectedDateFilter);
        EnsureDateLoaded(SelectedDateFilter);
    });

    /// <summary>清空异常记录：范围跟随当前日期筛选（"全部日期"=清空全部；具体日期=只清当天）。
    /// 删除落盘 JSONL 文件并同步剔除内存记录。</summary>
    public RelayCommand ClearRecordsCommand => new(_ =>
    {
        var all = SelectedDateFilter == "全部日期";
        var scope = all ? "全部日期的" : $" {SelectedDateFilter} 当天的";
        if (MessageBox.Show($"确定删除{scope}异常记录吗？\n落盘的异常库文件将被删除，不可恢复。",
                "清空异常记录", MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes)
            return;
        var deleted = _service.DeleteRecords(all ? null : SelectedDateFilter);
        _allRecords.RemoveAll(r => all || r.Time.ToString("yyyy-MM-dd") == SelectedDateFilter);
        if (all)
        {
            _loadedDates.Clear();
            _loadedDates.Add(DateTime.Now.ToString("yyyy-MM-dd"));
        }
        _fileDates = _service.ListRecordDates(); // 删后从磁盘重列日期清单
        RebuildDateFilterItems();
        RebuildRuleFilterItems();
        RebuildFiltered();
        RecordStatus = $"已删除{scope}异常记录（{deleted} 个文件）";
    });

    /// <summary>复制命中时所在的路线名到剪贴板（仅路线名本身，不含配置组）。</summary>
    public RelayCommand CopyRouteCommand => new(p =>
    {
        if (p is not ExceptionRecord { RouteName: { } route }) return;
        try
        {
            Clipboard.SetText(route);
            RecordStatus = $"已复制路线名: {route}";
        }
        catch (Exception ex)
        {
            RecordStatus = $"复制失败: {ex.Message}";
        }
    });

    /// <summary>打开事发快照目录（log/incidents/，事发录像功能落盘点；复用"打开日志目录"思路）。</summary>
    public RelayCommand OpenIncidentDirCommand => new(_ =>
    {
        try
        {
            var dir = IncidentSnapshotService.IncidentRootDir;
            Directory.CreateDirectory(dir);
            System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
            { FileName = "explorer.exe", Arguments = $"\"{dir}\"", UseShellExecute = true });
        }
        catch (Exception ex)
        {
            RecordStatus = $"打开快照目录失败: {ex.Message}";
        }
    });

    /// <summary>初始加载：后台列日期清单（不读内容）+ 只读当天的记录，默认筛选当天。
    /// 历史日期不预加载——之前全量加载所有 JSONL 是卡顿根源。</summary>
    private void ReloadHistory()
    {
        IsLoadingHistory = true;
        Task.Run(() =>
        {
            var dates = _service.ListRecordDates();
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            var records = _service.LoadRecordsOfDay(today);
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _allRecords.Clear();
                _allRecords.AddRange(records);
                _loadedDates.Clear();
                _loadedDates.Add(today);
                _fileDates = dates;
                RebuildDateFilterItems();
                // 默认显示当天（即使当天暂无记录，筛选项里也固定有今天）
                SelectedDateFilter = DateFilterItems.Contains(today) ? today : "全部日期";
                RebuildRuleFilterItems();
                RebuildFiltered();
                IsLoadingHistory = false;
            });
        });
    }

    /// <summary>选中了未加载的日期时后台补加载（"全部日期"=补加载所有未载日期）。
    /// 加载期间 IsLoadingHistory=true，界面显示加载动画。</summary>
    private void EnsureDateLoaded(string dateFilter)
    {
        List<string> toLoad = dateFilter == "全部日期"
            ? DateFilterItems.Where(d => d != "全部日期" && !_loadedDates.Contains(d)).ToList()
            : _loadedDates.Contains(dateFilter) ? [] : [dateFilter];
        if (toLoad.Count == 0) return;
        foreach (var d in toLoad) _loadedDates.Add(d); // 先标记，防重复触发
        IsLoadingHistory = true;
        Task.Run(() =>
        {
            var records = toLoad.SelectMany(d => _service.LoadRecordsOfDay(d)).ToList();
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                MergeRecords(records);
                IsLoadingHistory = false;
                RebuildRuleFilterItems();
                RebuildFiltered();
            });
        });
    }

    /// <summary>合并读盘结果进内存列表并按时间倒序重排（去重：实时新增的记录已在内存，
    /// 且 RepeatCount 可能已合并增长，保留内存版）。</summary>
    private void MergeRecords(List<ExceptionRecord> loaded)
    {
        var keys = new HashSet<string>(_allRecords.Select(RecordKey));
        foreach (var r in loaded)
            if (keys.Add(RecordKey(r)))
                _allRecords.Add(r);
        _allRecords.Sort((a, b) => b.Time.CompareTo(a.Time));
    }

    /// <summary>记录去重键（时间|规则|文件偏移|命中行；不含 RepeatCount——合并计数仅内存更新，落盘保留首次值）。</summary>
    private static string RecordKey(ExceptionRecord r) =>
        $"{r.Time.Ticks}|{r.RuleId}|{r.FileOffset}|{r.MatchedLine}";

    private void OnRecordAdded(ExceptionRecord record, bool alert)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // 时间倒序插入（实时新记录总在头部）
            _allRecords.Insert(0, record);
            if (_allRecords.Count > MaxRecordsInMemory)
                _allRecords.RemoveRange(MaxRecordsInMemory, _allRecords.Count - MaxRecordsInMemory);
            var date = record.Time.ToString("yyyy-MM-dd");
            _loadedDates.Add(date); // 实时新增视为已加载（落盘可能滞后 ≤15 秒，重复读盘也靠 RecordKey 去重兜底）
            if (!DateFilterItems.Contains(date)) DateFilterItems.Insert(1, date);
            if (!RuleFilterItems.Contains(record.RuleName)) RuleFilterItems.Add(record.RuleName);
            RebuildFiltered();

            // 快照目录是后台异步落盘的（前段立即写、后段补采完才封盘），记录首渲染时目录可能还没建出来，
            // HasIncidentSnapshot 会求得 false；对开了存快照的规则的记录延迟重渲染一次（替换集合项触发绑定重求值，
            // 同 OnRecordMerged 的思路），让「📷 快照」按钮在封盘后自动出现。延迟时长跟随事发后秒数设置。
            if (Rules.Any(r => r.ToModel().Id == record.RuleId && r.Snapshot))
            {
                _ = Task.Delay(_shell.IncidentSnapshotFinalizeDelayMs()).ContinueWith(_ => Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    var idx = FilteredRecords.IndexOf(record);
                    if (idx >= 0) FilteredRecords[idx] = record;
                }), TaskContinuationOptions.OnlyOnRanToCompletion);
            }
        });
    }

    private void OnRecordMerged(ExceptionRecord record)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // 合并计数更新：替换集合项触发 UI 刷新（ExceptionRecord 无 INPC）
            var idx = FilteredRecords.IndexOf(record);
            if (idx >= 0) FilteredRecords[idx] = record;
        });
    }

    private void RebuildFiltered()
    {
        FilteredRecords.Clear();
        var count = 0;
        foreach (var r in _allRecords)
        {
            if (SelectedRuleFilter != "全部规则" && r.RuleName != SelectedRuleFilter) continue;
            if (SelectedLevelFilter != "全部级别" && r.Level != SelectedLevelFilter) continue;
            if (SelectedDateFilter != "全部日期" && r.Time.ToString("yyyy-MM-dd") != SelectedDateFilter) continue;
            // 诊断时间范围联动（全天=不过滤，由调用方转成 null）
            if (_timeRangeStart is { } ts && r.Time < ts) continue;
            if (_timeRangeEnd is { } te && r.Time > te) continue;
            FilteredRecords.Add(r);
            count++;
        }
        RecordStatus = $"共 {_allRecords.Count} 条记录，筛选后 {count} 条"
            + (_timeRangeStart != null || _timeRangeEnd != null ? " · 已按诊断时间范围过滤" : "");
    }

    private void RebuildRuleFilterItems()
    {
        var keep = SelectedRuleFilter;
        RuleFilterItems.Clear();
        RuleFilterItems.Add("全部规则");
        foreach (var name in _allRecords.Select(r => r.RuleName).Concat(Rules.Select(r => r.Name)).Distinct())
            RuleFilterItems.Add(name);
        SelectedRuleFilter = RuleFilterItems.Contains(keep) ? keep : "全部规则";
    }

    /// <summary>日期筛选项 = 磁盘已有记录的日期 ∪ 内存记录日期 ∪ 今天（今天固定存在，保证默认能选中当天）。</summary>
    private void RebuildDateFilterItems()
    {
        var keep = SelectedDateFilter;
        var today = DateTime.Now.ToString("yyyy-MM-dd");
        DateFilterItems.Clear();
        DateFilterItems.Add("全部日期");
        foreach (var d in _fileDates
                     .Concat(_allRecords.Select(r => r.Time.ToString("yyyy-MM-dd")))
                     .Append(today)
                     .Distinct().OrderByDescending(d => d))
            DateFilterItems.Add(d);
        SelectedDateFilter = DateFilterItems.Contains(keep) ? keep : today;
    }
}

/// <summary>规则列表项（带 INPC 的编辑包装；任何属性变动即时持久化）。</summary>
public sealed class WatchRuleItem : ViewModelBase
{
    private readonly WatchRule _model;
    private readonly Action _onChanged;

    public WatchRuleItem(WatchRule model, Action onChanged)
    {
        _model = model;
        _onChanged = onChanged;
    }

    public WatchRule ToModel() => _model;

    public string Name { get => _model.Name; set { _model.Name = value; OnPropertyChanged(); _onChanged(); } }
    public string Pattern { get => _model.Pattern; set { _model.Pattern = value; OnPropertyChanged(); OnPropertyChanged(nameof(PatternDisplay)); _onChanged(); } }
    public bool IsRegex { get => _model.IsRegex; set { _model.IsRegex = value; OnPropertyChanged(); _onChanged(); } }
    public bool Enabled { get => _model.Enabled; set { _model.Enabled = value; OnPropertyChanged(); _onChanged(); } }
    public bool Alert { get => _model.Alert; set { _model.Alert = value; OnPropertyChanged(); _onChanged(); } }
    /// <summary>命中时保存事发快照（事发前后桌面帧 + 触发日志到 log/incidents/，需总开关开启；前后秒数见事发录像设置）。</summary>
    public bool Snapshot { get => _model.Snapshot; set { _model.Snapshot = value; OnPropertyChanged(); _onChanged(); } }
    public string MinLevel { get => _model.MinLevel; set { _model.MinLevel = value; OnPropertyChanged(); _onChanged(); } }
    public string Note { get => _model.Note; set { _model.Note = value; OnPropertyChanged(); _onChanged(); } }

    /// <summary>列表显示：Pattern 为空的级别兜底规则显示说明文字。</summary>
    public string PatternDisplay =>
        string.IsNullOrEmpty(Pattern) ? $"（级别 ≥ {MinLevel} 兜底）" : Pattern;
}
