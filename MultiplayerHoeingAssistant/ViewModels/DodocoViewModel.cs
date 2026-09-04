using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 嘟嘟可（日志与监控系统）主 ViewModel —— 页面外壳 + 实时日志 Tab（F1）。
/// 手写 INPC（继承 ViewModelBase），不复用 5055 行的 MainViewModel 上帝类；
/// 所需主 VM 数据（BGI 路径配置等）经构造注入引用获取。
///
/// 实时日志管线：BgiLogTailService（后台线程）→ _pending 队列 → DispatcherTimer 200ms 合帧
/// 批量刷新到 VisibleEntries（环形缓冲 5000 条），避免日志风暴卡 UI。
/// </summary>
public sealed class DodocoViewModel : ViewModelBase, IDisposable
{
    /// <summary>内存环形缓冲上限（最近 5000 条）。</summary>
    private const int RingCapacity = 5000;
    /// <summary>UI 合帧刷新间隔。</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);

    private readonly MainViewModel _mainVm;
    private readonly BgiLogTailService _tailService;
    private readonly KeywordWatchService _watchService;
    private readonly HoeingStatsService _statsService;
    private readonly LogFileBrowser _logBrowser;
    private readonly DodocoSettingsService _settingsService;
    private readonly ScreenshotService _screenshotService;
    private readonly DiagnosticPackageService _diagService;
    private readonly MemberScreenshotRelayService _screenshotRelay;
    private readonly DispatcherTimer _flushTimer;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    /// <summary>全量环形缓冲（筛选前）。筛选条件变化时从此重建可见列表。</summary>
    private readonly List<LogEntry> _allEntries = new(RingCapacity);

    public DodocoViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;

        // 统一设置先行（静音/卡死阈值/截图参数都从这里读，其它服务依赖它）
        _settingsService = new DodocoSettingsService();

        // BGI 日志目录提供者：运行中配置变更也能在下次轮询生效
        _tailService = new BgiLogTailService(() =>
            BgiLogTailService.ResolveBgiLogDir(_mainVm.Config?.BgiPath));
        _watchService = new KeywordWatchService(_tailService,
            muteProvider: () => _settingsService.Current.MuteAll);
        _statsService = new HoeingStatsService(_tailService, () => _mainVm.CurrentOnlineGeneration);
        _logBrowser = new LogFileBrowser(() =>
            BgiLogTailService.ResolveBgiLogDir(_mainVm.Config?.BgiPath));
        _screenshotService = new ScreenshotService(() => _settingsService.Current.ThumbnailWidth);
        _diagService = new DiagnosticPackageService(
            () => BgiLogTailService.ResolveBgiLogDir(_mainVm.Config?.BgiPath),
            BuildMembersSnapshot);

        Browser = new LogBrowserViewModel(_logBrowser);
        Watch = new ExceptionWatchViewModel(_watchService, this);
        Stats = new HoeingStatsViewModel(_statsService, _mainVm, _tailService, RaiseAlert, _settingsService);
        Monitor = new ScreenshotViewModel(_screenshotService, _settingsService, _mainVm);

        // P5 远程巡检墙：截图汇聚（本机上报 + 成员帧接收 → 桌面监控 Tab）
        _screenshotRelay = new MemberScreenshotRelayService(
            _screenshotService, _settingsService, () => _mainVm.SignalR);
        _screenshotRelay.FrameReceived += frame =>
            Application.Current.Dispatcher.BeginInvoke(() => Monitor.OnRemoteFrame(frame));

        _tailService.EntryReceived += OnEntryReceived;
        _tailService.HistoryBatchReceived += OnHistoryBatch;
        _tailService.TargetFileChanged += OnTargetFileChanged;
        _watchService.RecordAdded += OnWatchRecordAdded;

        _flushTimer = new DispatcherTimer { Interval = FlushInterval };
        _flushTimer.Tick += (_, _) => FlushPending();
        _flushTimer.Start();

        // 进程退出时释放后台线程/文件句柄（窗口关闭只是最小化到托盘，不能依赖 Closing）
        Application.Current.Exit += (_, _) => Dispose();
    }

    // ========== 子页 ViewModel ==========

    /// <summary>日志浏览 Tab（P2 / F2）。</summary>
    public LogBrowserViewModel Browser { get; }
    /// <summary>异常监控 Tab（P2 / F3）。</summary>
    public ExceptionWatchViewModel Watch { get; }
    /// <summary>锄地数据 Tab（P3 / F5 + 卡死心跳）。</summary>
    public HoeingStatsViewModel Stats { get; }
    /// <summary>桌面监控 Tab（P4 / F4）。</summary>
    public ScreenshotViewModel Monitor { get; }

    // ========== Tab 导航 ==========

    private int _selectedTabIndex;
    /// <summary>当前 Tab：0=实时日志 1=日志浏览 2=异常监控 3=锄地数据 4=桌面监控。</summary>
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set
        {
            var old = _selectedTabIndex;
            if (!SetProperty(ref _selectedTabIndex, value)) return;
            if (value == 2)
            {
                // 打开异常监控 Tab 视为已读，消红点
                HasUnreadAlerts = false;
            }
            // 离开/进入桌面监控 Tab：自动刷新定时器随 Tab 启停（P4 要求离开即停）
            if (old == 4 && value != 4) Monitor.OnTabDeactivated();
            if (value == 4 && old != 4) Monitor.OnTabActivated();
        }
    }

    public RelayCommand SelectTabCommand => new(p =>
    {
        if (p != null && int.TryParse(p.ToString(), out var idx)) SelectedTabIndex = idx;
    });

    /// <summary>返回成员列表主页（耕地机）。</summary>
    public RelayCommand BackCommand => new(_ => _mainVm.CurrentPage = AppPage.Home);

    // ========== 实时日志：数据与筛选 ==========

    /// <summary>可见日志（经级别/实例筛选，环形缓冲 5000 条）。</summary>
    public ObservableCollection<LogEntry> VisibleEntries { get; } = new();

    private bool _showDbg;
    private bool _showInf = true;
    private bool _showWrn = true;
    private bool _showErr = true;

    public bool ShowDbg { get => _showDbg; set { if (SetProperty(ref _showDbg, value)) RebuildVisible(); } }
    public bool ShowInf { get => _showInf; set { if (SetProperty(ref _showInf, value)) RebuildVisible(); } }
    public bool ShowWrn { get => _showWrn; set { if (SetProperty(ref _showWrn, value)) RebuildVisible(); } }
    public bool ShowErr { get => _showErr; set { if (SetProperty(ref _showErr, value)) RebuildVisible(); } }

    /// <summary>实例筛选下拉（多开时按 [BgiInstance] 区分）。首项固定"全部实例"。</summary>
    public ObservableCollection<string> Instances { get; } = new() { "全部实例" };

    private string _selectedInstance = "全部实例";
    public string SelectedInstance
    {
        get => _selectedInstance;
        set { if (SetProperty(ref _selectedInstance, value)) RebuildVisible(); }
    }

    private bool _autoFollow = true;
    /// <summary>自动滚动跟尾（用户在列表上翻时由视图置 false 暂停跟尾，滚回底部恢复）。</summary>
    public bool AutoFollow { get => _autoFollow; set => SetProperty(ref _autoFollow, value); }

    private bool _isPaused;
    /// <summary>暂停接收（暂停期间新日志仍入缓冲但不刷界面，继续时追平）。</summary>
    public bool IsPaused
    {
        get => _isPaused;
        set
        {
            var wasPaused = _isPaused;
            if (!SetProperty(ref _isPaused, value)) return;
            OnPropertyChanged(nameof(PauseButtonText));
            // 恢复时从环形缓冲全量重建，兑现"继续时追平"（暂停期间入缓冲但未上屏的条目补回来）
            if (wasPaused && !value) RebuildVisible();
        }
    }
    public string PauseButtonText => IsPaused ? "▶ 继续" : "⏸ 暂停";

    public RelayCommand TogglePauseCommand => new(_ => IsPaused = !IsPaused);

    /// <summary>清空视图（只清内存缓冲，不影响日志文件）。</summary>
    public RelayCommand ClearViewCommand => new(_ =>
    {
        lock (_allEntries)
        {
            _allEntries.Clear();
            VisibleEntries.Clear();
        }
        UpdateStatus();
    });

    // ========== 状态栏 ==========

    private string _statusText = "等待 BGI 日志…（请确认已配置 BGI 路径且 BGI 已产生日志）";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private string? _currentTargetFile;
    /// <summary>当前 tail 的日志文件名（无目标时为 null）。</summary>
    public string? CurrentTargetFile { get => _currentTargetFile; set => SetProperty(ref _currentTargetFile, value); }

    private int _totalReceived;
    /// <summary>累计收到条数（含被筛选隐藏的）。</summary>
    public int TotalReceived { get => _totalReceived; set => SetProperty(ref _totalReceived, value); }

    // ========== 告警（红点 / 托盘 / 提示音） ==========

    private bool _hasUnreadAlerts;
    /// <summary>是否有未读告警（驱动 MainWindow 嘟嘟可导航按钮红点）。</summary>
    public bool HasUnreadAlerts { get => _hasUnreadAlerts; set => SetProperty(ref _hasUnreadAlerts, value); }

    /// <summary>全部静音开关（持久化到 dodoco_settings.json，P4 统一设置收口）。
    /// 静音时命中只记录，不红点/不响/不弹托盘。</summary>
    public bool MuteAll
    {
        get => _settingsService.Current.MuteAll;
        set
        {
            _settingsService.Update(s => s.MuteAll = value);
            OnPropertyChanged();
        }
    }

    public RelayCommand ClearUnreadCommand => new(_ => HasUnreadAlerts = false);

    // ========== P5 远程巡检墙：共享本机桌面截图 ==========

    /// <summary>共享我的桌面截图（持久化到 dodoco_settings.json）。
    /// 开启后每 10 秒把一帧 480px 低清 JPEG 上报到房间，供成员远程查看。</summary>
    public bool ShareDesktopScreenshot
    {
        get => _settingsService.Current.ShareDesktopScreenshot;
        set
        {
            _settingsService.Update(s => s.ShareDesktopScreenshot = value);
            OnPropertyChanged();
        }
    }

    // ========== 诊断包导出（P4 / §5-C） ==========

    private string _diagnosticTimeText = "";
    /// <summary>诊断包目标时间点（HH:mm 表示今天该时刻；空=当前时间）。</summary>
    public string DiagnosticTimeText { get => _diagnosticTimeText; set => SetProperty(ref _diagnosticTimeText, value); }

    private string _diagStatus = "";
    /// <summary>诊断包导出结果提示。</summary>
    public string DiagStatus { get => _diagStatus; set => SetProperty(ref _diagStatus, value); }

    private bool _diagExporting;

    /// <summary>导出诊断包：选目标时间点（默认当前）→ 打包 BGI 日志切片 + 助手日志 + 异常库 + 统计 + 成员快照。</summary>
    public RelayCommand ExportDiagnosticCommand => new(_ =>
    {
        if (_diagExporting) return;
        // 解析目标时间点：空=现在；"HH:mm"=今天该时刻；也接受完整 "yyyy-MM-dd HH:mm"
        var target = DateTime.Now;
        var text = DiagnosticTimeText.Trim();
        if (text.Length > 0)
        {
            if (TimeSpan.TryParseExact(text, @"hh\:mm", System.Globalization.CultureInfo.InvariantCulture, out var tod))
                target = DateTime.Today.Add(tod);
            else if (!DateTime.TryParse(text, out target))
            {
                DiagStatus = "时间格式无效（支持 HH:mm 或 yyyy-MM-dd HH:mm，留空=当前时间）";
                return;
            }
        }

        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "导出诊断包",
            FileName = $"dodoco_diag_{DateTime.Now:yyyyMMdd_HHmmss}.zip",
            Filter = "Zip 压缩包|*.zip"
        };
        if (dlg.ShowDialog() != true) return;

        _diagExporting = true;
        DiagStatus = "正在打包诊断包…";
        Task.Run(() =>
        {
            try
            {
                var summary = _diagService.Export(target, dlg.FileName);
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    _diagExporting = false;
                    DiagStatus = $"已导出 {dlg.FileName}（{summary.Split('\n').Length} 项内容）";
                });
            }
            catch (Exception ex)
            {
                Application.Current.Dispatcher.BeginInvoke(() =>
                {
                    _diagExporting = false;
                    DiagStatus = $"导出失败: {ex.Message}";
                });
            }
        });
    });

    /// <summary>成员状态快照（诊断包用；可能被后台线程调用，经 Dispatcher 回 UI 线程枚举）。</summary>
    private IReadOnlyList<Dictionary<string, object?>> BuildMembersSnapshot()
    {
        return Application.Current.Dispatcher.Invoke(() =>
            _mainVm.Members.Select(m => new Dictionary<string, object?>
            {
                ["playerName"] = m.PlayerName,
                ["uid"] = m.DisplayUid,
                ["online"] = m.Online,
                ["bgiStatus"] = m.BgiStatus,
                ["taskRunning"] = m.TaskRunning,
                ["currentTaskName"] = m.CurrentTaskName,
                ["currentTaskGroupName"] = m.CurrentTaskGroupName,
                ["currentRouteDisplay"] = m.CurrentRouteDisplay,
                ["autoHoeingProgress"] = m.AutoHoeingProgress
            }).ToList());
    }

    /// <summary>异常记录跳转：切到日志浏览 Tab 并定位到对应文件偏移。</summary>
    public void JumpToRecord(ExceptionRecord record)
    {
        SelectedTabIndex = 1;
        Browser.JumpTo(record.SourceFile, record.FileOffset);
    }

    /// <summary>重新探测 BGI 日志目录（用户刚改完 BGI 路径时调用，立即生效而不等下一轮轮询）。</summary>
    public RelayCommand RefreshTargetCommand => new(_ =>
    {
        _tailService.Poke();
        Browser.RefreshFiles();
    });

    // ========== 日志流接入（后台线程回调） ==========

    private void OnEntryReceived(LogEntry entry)
    {
        _pending.Enqueue(entry);
    }

    private void OnHistoryBatch(IReadOnlyList<LogEntry> batch)
    {
        foreach (var e in batch) _pending.Enqueue(e);
    }

    private void OnTargetFileChanged(string? path)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            CurrentTargetFile = path == null ? null : System.IO.Path.GetFileName(path);
            UpdateStatus();
        });
    }

    /// <summary>200ms 合帧：把队列中的新条目批量刷到界面。</summary>
    private void FlushPending()
    {
        if (_pending.IsEmpty) return;
        var batch = new List<LogEntry>();
        while (batch.Count < 2000 && _pending.TryDequeue(out var e)) batch.Add(e);
        if (batch.Count == 0) return;

        TotalReceived += batch.Count;
        lock (_allEntries)
        {
            foreach (var e in batch)
            {
                _allEntries.Add(e);
                // 收集实例列表（筛选用）
                if (e.Instance != null && !Instances.Contains(e.Instance))
                    Instances.Add(e.Instance);
            }
            // 环形缓冲：超出容量从头部裁
            if (_allEntries.Count > RingCapacity)
                _allEntries.RemoveRange(0, _allEntries.Count - RingCapacity);

            if (!IsPaused)
            {
                foreach (var e in batch)
                {
                    // 被裁剪掉的旧条目不重复添加（只加仍在缓冲内的）
                    if (PassesFilter(e)) VisibleEntries.Add(e);
                }
                while (VisibleEntries.Count > RingCapacity)
                    VisibleEntries.RemoveAt(0);
            }
        }
        UpdateStatus();
    }

    private bool PassesFilter(LogEntry e)
    {
        var levelOk = e.Level switch
        {
            LogLevels.Dbg => ShowDbg,
            LogLevels.Inf => ShowInf,
            LogLevels.Wrn => ShowWrn,
            LogLevels.Err => ShowErr,
            _ => true
        };
        if (!levelOk) return false;
        if (SelectedInstance != "全部实例" && e.Instance != SelectedInstance) return false;
        return true;
    }

    /// <summary>筛选条件变化：从环形缓冲全量重建可见列表。</summary>
    private void RebuildVisible()
    {
        lock (_allEntries)
        {
            VisibleEntries.Clear();
            foreach (var e in _allEntries)
                if (PassesFilter(e)) VisibleEntries.Add(e);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var file = CurrentTargetFile ?? "未定位到日志文件";
        StatusText = $"文件: {file} · 累计 {TotalReceived} 条 · 显示 {VisibleEntries.Count} 条" +
                     (IsPaused ? " · 已暂停" : "");
    }

    // ========== 告警接入 ==========

    /// <summary>
    /// 统一告警通道：导航红点 + 可选提示音 + 托盘气泡。
    /// 尊重"全部静音"：静音时只记录不出声/不亮红点（异常记录本身仍写 JSONL，不受静音影响）。
    /// 供异常监控（规则命中）与卡死心跳共用。须在 UI 线程或可切线程上下文调用（内部自行 Dispatcher）。
    /// </summary>
    internal void RaiseAlert(string title, string detail)
    {
        if (MuteAll) return;
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            HasUnreadAlerts = true;
            // 可选提示音（系统音，无需新 NuGet 包）
            try { System.Media.SystemSounds.Exclamation.Play(); } catch { }
            // 托盘气泡
            try
            {
                (Application.Current as App)?.ShowTrayBalloon(title, detail);
            }
            catch { /* 托盘不可用时静默 */ }
        });
    }

    private void OnWatchRecordAdded(ExceptionRecord record, bool alert)
    {
        if (!alert) return;
        RaiseAlert("嘟嘟可异常监控", $"[{record.RuleName}] {FirstLine(record.Message)}");
    }

    private static string FirstLine(string text)
    {
        var idx = text.IndexOf('\n');
        var line = idx >= 0 ? text[..idx] : text;
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    public void Dispose()
    {
        _flushTimer.Stop();
        _watchService.RecordAdded -= OnWatchRecordAdded;
        _tailService.EntryReceived -= OnEntryReceived;
        _tailService.HistoryBatchReceived -= OnHistoryBatch;
        _tailService.TargetFileChanged -= OnTargetFileChanged;
        Monitor.Dispose();
        Stats.Dispose();
        _screenshotRelay.Dispose();
        _statsService.Dispose();
        _watchService.Dispose();
        _tailService.Dispose();
    }
}
