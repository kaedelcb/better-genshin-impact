using System.Collections.ObjectModel;
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
    public string SelectedDateFilter
    {
        get => _selectedDateFilter;
        set { if (SetProperty(ref _selectedDateFilter, value)) RebuildFiltered(); }
    }

    private string _recordStatus = "";
    public string RecordStatus { get => _recordStatus; set => SetProperty(ref _recordStatus, value); }

    /// <summary>点击记录 → 跳转到日志浏览 Tab 对应文件位置。</summary>
    public RelayCommand OpenRecordCommand => new(p =>
    {
        if (p is ExceptionRecord record) _shell.JumpToRecord(record);
    });

    /// <summary>重新从 JSONL 异常库加载历史记录。</summary>
    public RelayCommand RefreshRecordsCommand => new(_ => ReloadHistory());

    private void ReloadHistory()
    {
        Task.Run(() =>
        {
            var records = _service.LoadHistoryRecords();
            Application.Current.Dispatcher.BeginInvoke(() =>
            {
                _allRecords.Clear();
                _allRecords.AddRange(records);
                RebuildDateFilterItems();
                RebuildRuleFilterItems();
                RebuildFiltered();
            });
        });
    }

    private void OnRecordAdded(ExceptionRecord record, bool alert)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            // 时间倒序插入（实时新记录总在头部）
            _allRecords.Insert(0, record);
            if (_allRecords.Count > MaxRecordsInMemory)
                _allRecords.RemoveRange(MaxRecordsInMemory, _allRecords.Count - MaxRecordsInMemory);
            var date = record.Time.ToString("yyyy-MM-dd");
            if (!DateFilterItems.Contains(date)) DateFilterItems.Insert(1, date);
            if (!RuleFilterItems.Contains(record.RuleName)) RuleFilterItems.Add(record.RuleName);
            RebuildFiltered();
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
            FilteredRecords.Add(r);
            count++;
        }
        RecordStatus = $"共 {_allRecords.Count} 条记录，筛选后 {count} 条";
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

    private void RebuildDateFilterItems()
    {
        var keep = SelectedDateFilter;
        DateFilterItems.Clear();
        DateFilterItems.Add("全部日期");
        foreach (var d in _allRecords.Select(r => r.Time.ToString("yyyy-MM-dd")).Distinct().OrderByDescending(d => d))
            DateFilterItems.Add(d);
        SelectedDateFilter = DateFilterItems.Contains(keep) ? keep : "全部日期";
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
    public string MinLevel { get => _model.MinLevel; set { _model.MinLevel = value; OnPropertyChanged(); _onChanged(); } }
    public string Note { get => _model.Note; set { _model.Note = value; OnPropertyChanged(); _onChanged(); } }

    /// <summary>列表显示：Pattern 为空的级别兜底规则显示说明文字。</summary>
    public string PatternDisplay =>
        string.IsNullOrEmpty(Pattern) ? $"（级别 ≥ {MinLevel} 兜底）" : Pattern;
}
