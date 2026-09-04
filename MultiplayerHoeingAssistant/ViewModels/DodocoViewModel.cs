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
    /// <summary>内存环形缓冲上限（每个日志来源各最近 5000 条）。</summary>
    private const int RingCapacity = 5000;
    /// <summary>UI 合帧刷新间隔。</summary>
    private static readonly TimeSpan FlushInterval = TimeSpan.FromMilliseconds(200);
    /// <summary>本机日志来源的 Key。</summary>
    public const string LocalSourceKey = "local";

    private readonly MainViewModel _mainVm;
    private readonly BgiLogTailService _tailService;
    private readonly KeywordWatchService _watchService;
    private readonly HoeingStatsService _statsService;
    private readonly LogFileBrowser _logBrowser;
    private readonly DodocoSettingsService _settingsService;
    private readonly ScreenshotService _screenshotService;
    private readonly DiagnosticPackageService _diagService;
    private readonly MemberScreenshotRelayService _screenshotRelay;
    private readonly MemberLogRelayService _logRelay;
    /// <summary>远程日志下载·被下载端（应答文件列表 / 分块上行）。</summary>
    private readonly MemberLogShareService _logShare;
    private readonly DispatcherTimer _flushTimer;
    private readonly ConcurrentQueue<LogEntry> _pending = new();
    /// <summary>全量环形缓冲（筛选前）：来源 Key（"local" 或成员 uid）→ 该来源的条目。筛选/切换来源时从此重建可见列表。</summary>
    private readonly Dictionary<string, List<LogEntry>> _buffers = new();
    /// <summary>成员 uid → 最近已知名字（成员退出房间后保留下拉项用）。</summary>
    private readonly Dictionary<string, string> _memberNames = new();
    /// <summary>各远程来源的省流（仅 INF+）标志，随批更新，用于状态栏提示。仅 UI 线程访问。</summary>
    private readonly Dictionary<string, bool> _sourceInfoOnly = new();
    /// <summary>缓冲与可见列表的锁（本机/远程两路都在 UI 线程入缓冲，锁仅作防御）。</summary>
    private readonly object _bufLock = new();

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

        Browser = new LogBrowserViewModel(_logBrowser, _mainVm);
        Watch = new ExceptionWatchViewModel(_watchService, this);
        Stats = new HoeingStatsViewModel(_statsService, _mainVm, _tailService, RaiseAlert, _settingsService);
        Monitor = new ScreenshotViewModel(_screenshotService, _settingsService, _mainVm);

        // P5 远程巡检墙：截图汇聚（本机上报 + 成员帧接收 → 桌面监控 Tab）
        _screenshotRelay = new MemberScreenshotRelayService(
            _screenshotService, _settingsService, () => _mainVm.SignalR);
        _screenshotRelay.FrameReceived += frame =>
            Application.Current.Dispatcher.BeginInvoke(() => Monitor.OnRemoteFrame(frame));

        // 房间实时日志汇聚：本机上报（500ms 合批）+ 成员日志批接收 → 实时日志 Tab 多来源
        _logRelay = new MemberLogRelayService(_tailService, _settingsService, () => _mainVm.SignalR);
        _logRelay.BatchReceived += OnLogBatchReceived;

        // 远程成员完整日志下载·被下载端：应答文件列表请求 / 分块上行（懒绑定由 FlushPending 节拍驱动）
        _logShare = new MemberLogShareService(_settingsService, () => _mainVm.SignalR,
            () => BgiLogTailService.ResolveBgiLogDir(_mainVm.Config?.BgiPath));

        RebuildLogSources();
        _mainVm.Members.CollectionChanged += OnMembersChanged;
        // 成员 Online 是原地更新（不触发 CollectionChanged），靠这个事件驱动离线退订/上线重订
        _mainVm.MemberOnlineChanged += OnMemberOnlineChanged;
        // 离开嘟嘟可页面（CurrentPage 变化）是退订时机之一
        _mainVm.PropertyChanged += OnMainVmPropertyChanged;

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
            // 切离/切回实时日志 Tab：日志订阅随 Tab 退订/重订（观众驱动）
            if ((old == 0) != (value == 0)) EvaluateSubscription();
        }
    }

    public RelayCommand SelectTabCommand => new(p =>
    {
        if (p != null && int.TryParse(p.ToString(), out var idx)) SelectedTabIndex = idx;
    });

    /// <summary>返回成员列表主页（耕地机）。</summary>
    public RelayCommand BackCommand => new(_ => _mainVm.CurrentPage = AppPage.Home);

    // ========== 实时日志：数据与筛选 ==========

    /// <summary>可见日志（经级别/实例筛选，显示当前选中来源的环形缓冲内容）。</summary>
    public ObservableCollection<LogEntry> VisibleEntries { get; } = new();

    // ---- 日志来源（本机 / 房间成员，房间实时日志汇聚） ----

    /// <summary>日志来源下拉：首项"本机"，其余为房间成员（含已掉线成员的缓存流，标注离线）。</summary>
    public ObservableCollection<LogSourceOption> LogSources { get; } = new();

    private LogSourceOption? _selectedSource;
    /// <summary>当前选中的日志来源（null 视为本机）。切换即按该来源缓冲重建可见列表。</summary>
    public LogSourceOption? SelectedSource
    {
        get => _selectedSource;
        set
        {
            if (!SetProperty(ref _selectedSource, value)) return;
            RebuildVisible();
            EvaluateSubscription(); // 切来源即退旧订新
        }
    }

    /// <summary>当前来源 Key（"local" 或成员 uid）。</summary>
    private string CurrentSourceKey => _selectedSource?.Key ?? LocalSourceKey;

    /// <summary>共享我的实时日志开关（持久化；默认开，联机小队互相盯用）。
    /// 语义：允许房间成员订阅我的日志（观众驱动，无人订阅时零上报）。</summary>
    public bool ShareRealtimeLog
    {
        get => _settingsService.Current.ShareRealtimeLog;
        set
        {
            _settingsService.Update(s => s.ShareRealtimeLog = value);
            OnPropertyChanged();
        }
    }

    /// <summary>省流开关（持久化；默认关=全级别）：被订阅时不转发 DBG 级。</summary>
    public bool ShareLogInfoOnly
    {
        get => _settingsService.Current.ShareLogInfoOnly;
        set
        {
            _settingsService.Update(s => s.ShareLogInfoOnly = value);
            OnPropertyChanged();
        }
    }

    /// <summary>共享我的完整日志文件开关（持久化；默认开）：允许房间成员请求本机日志文件列表并下载。
    /// 完整日志可能包含本机路径等环境信息，在意可手动关。</summary>
    public bool ShareLogFiles
    {
        get => _settingsService.Current.ShareLogFiles;
        set
        {
            _settingsService.Update(s => s.ShareLogFiles = value);
            OnPropertyChanged();
        }
    }

    private List<LogEntry> BufferFor(string key)
    {
        if (!_buffers.TryGetValue(key, out var buf))
            _buffers[key] = buf = new List<LogEntry>(RingCapacity);
        return buf;
    }

    /// <summary>按房间成员重建来源下拉（本机 + 在线/离线成员 + 有缓存流的已退出成员），尽量保留选中。</summary>
    private void RebuildLogSources()
    {
        var prevKey = CurrentSourceKey;
        foreach (var m in _mainVm.Members)
            if (!string.IsNullOrEmpty(m.PlayerUid)) _memberNames[m.PlayerUid] = m.PlayerName;

        LogSources.Clear();
        LogSources.Add(new LogSourceOption(LocalSourceKey, "本机"));
        // 旧服务端无订阅方法：远程项标注（该标记在 HubException 后置位，新连接重置）
        var subscribeUnsupported = _mainVm.SignalR?.LogSubscribeUnsupported == true;
        foreach (var m in _mainVm.Members)
        {
            if (m.IsSelf || string.IsNullOrEmpty(m.PlayerUid)) continue;
            var label = m.Online ? m.PlayerName : $"{m.PlayerName}（离线）";
            if (subscribeUnsupported && m.Online) label += "（需新版服务端）";
            LogSources.Add(new LogSourceOption(m.PlayerUid, label));
        }
        // 有缓存流但已退出房间的成员：保留下拉项，缓存仍可回看
        foreach (var uid in _buffers.Keys)
        {
            if (uid == LocalSourceKey || LogSources.Any(o => o.Key == uid)) continue;
            LogSources.Add(new LogSourceOption(uid,
                $"{(_memberNames.TryGetValue(uid, out var n) ? n : uid)}（离线）"));
        }
        _selectedSource = LogSources.FirstOrDefault(o => o.Key == prevKey) ?? LogSources[0];
        OnPropertyChanged(nameof(SelectedSource));
        // 中危1：选中项因成员变动回退（key 变了）时重建可见列表，否则界面还停在旧来源内容
        if (CurrentSourceKey != prevKey) RebuildVisible();
        // 成员上线/掉线变化会影响订阅决策（成员掉线是退订时机之一）
        EvaluateSubscription();
        // 日志浏览 Tab 的远程成员下载下拉跟着成员列表走（在线且非自己）
        Browser.RefreshRemoteMembers();
    }

    // ========== 日志订阅（观众驱动：选中远程成员且在实时日志 Tab 才订阅，切走即退订） ==========

    /// <summary>当前已发出的订阅目标（来源 key），幂等判断用。</summary>
    private string? _currentSubscription;
    /// <summary>观看端懒绑定的 SignalR 客户端（重连补订阅用）。</summary>
    private SignalRClient? _viewerHooked;

    /// <summary>期望的订阅目标：选中远程在线成员 + 在实时日志 Tab + 在嘟嘟可页面。不满足则为 null。</summary>
    private string? DesiredSubscriptionTarget()
    {
        if (SelectedTabIndex != 0) return null;
        if (_mainVm.CurrentPage != AppPage.Dodoco) return null;
        var key = CurrentSourceKey;
        if (key == LocalSourceKey || key.StartsWith("name:")) return null; // name: 兜底键无 uid 可订
        var member = _mainVm.Members.FirstOrDefault(m => m.PlayerUid == key);
        return member is { Online: true } ? key : null; // 成员掉线不订阅（缓存流仍可回看）
    }

    /// <summary>订阅状态求值：与期望不一致时退旧订新（幂等，相同目标不重复发）。</summary>
    private void EvaluateSubscription()
    {
        EnsureViewerHooked();
        var want = DesiredSubscriptionTarget();
        if (want == _currentSubscription) return;
        var client = _mainVm.SignalR;
        if (_currentSubscription != null)
        {
            _ = client?.UnsubscribeMemberLogAsync(_currentSubscription);
            _currentSubscription = null;
        }
        if (want != null && client != null)
        {
            _ = client.SubscribeMemberLogAsync(want);
            _currentSubscription = want;
        }
    }

    /// <summary>观看端懒绑定 SignalR 客户端（连接状态事件：重连后补订阅）。</summary>
    private void EnsureViewerHooked()
    {
        var client = _mainVm.SignalR;
        if (ReferenceEquals(client, _viewerHooked)) return;
        if (_viewerHooked != null) _viewerHooked.OnConnectionStateChanged -= OnViewerConnectionState;
        _viewerHooked = client;
        if (_viewerHooked != null) _viewerHooked.OnConnectionStateChanged += OnViewerConnectionState;
    }

    /// <summary>断线重连（一致性细节 10）：服务端订阅表已被断线清理清空，仍选中远程成员则补发订阅。</summary>
    private void OnViewerConnectionState(bool connected)
    {
        Application.Current.Dispatcher.BeginInvoke(() =>
        {
            if (connected)
            {
                _currentSubscription = null; // 强制 Evaluate 重发
                EvaluateSubscription();
            }
            // 连接更替可能带来服务端能力变化（旧服务端标注/新连接重置），刷一遍来源下拉标注
            RebuildLogSources();
        });
    }

    /// <summary>收到远程成员日志批（UI 线程入口）：自滤、补下拉项，解析与异常监控喂入放后台线程。</summary>
    private void OnRemoteBatch(MemberLogBatch batch)
    {
        if (batch.Lines.Count == 0) return;

        // 自滤（中危5）：服务端广播含发送者。uid 非空按 uid 滤；uid 为空用发送者名字兜底；
        // 两者都判不出（名字缺失/本机名取不到）直接丢弃该批——宁可不显示也不多显示
        var selfUid = _mainVm.Config?.PlayerUid;
        string key;
        if (!string.IsNullOrEmpty(batch.Uid))
        {
            if (batch.Uid == selfUid) return;
            key = batch.Uid;
        }
        else
        {
            var selfName = _mainVm.Config?.PlayerName;
            if (string.IsNullOrEmpty(batch.SenderName)) return;
            if (!string.IsNullOrEmpty(selfName) && batch.SenderName == selfName) return;
            if (string.IsNullOrEmpty(selfName)) return;
            key = $"name:{batch.SenderName}";
        }

        if (!string.IsNullOrEmpty(batch.SenderName) && !string.IsNullOrEmpty(batch.Uid))
            _memberNames[batch.Uid] = batch.SenderName;
        // 省流标志随批更新；当前正看该来源时立即刷状态栏
        if (!_sourceInfoOnly.TryGetValue(key, out var prevInfoOnly) || prevInfoOnly != batch.InfoOnly)
        {
            _sourceInfoOnly[key] = batch.InfoOnly;
            if (CurrentSourceKey == key) UpdateStatus();
        }
        // 来源下拉补项（成员可能在我们重建下拉后才首次发言，或已掉线但流仍在到）
        if (LogSources.All(o => o.Key != key))
            LogSources.Add(new LogSourceOption(key, batch.SenderName.Length > 0 ? batch.SenderName : key));

        // 中危2：行解析 + 喂异常监控放后台线程（KeywordWatchService.OnEntry 全程在 _lock 内，线程安全），
        // 只有入缓冲/上屏回 UI 线程，远程风暴时不卡界面
        var lines = batch.Lines;
        var sourceTag = $"远程:{batch.SenderName}";
        var fallback = batch.ServerTime.ToLocalTime();
        Task.Run(() =>
        {
            var entries = new List<LogEntry>(lines.Count);
            foreach (var line in lines)
            {
                var e0 = MemberLogLineCodec.Parse(line, fallback, sourceTag);
                // 实例段带成员名前缀（实时列表实例列与异常库命中记录都能看出是哪台机器）
                var e = batch.SenderName.Length > 0
                    ? e0 with { Instance = e0.Instance != null ? $"{batch.SenderName}·{e0.Instance}" : batch.SenderName }
                    : e0;
                entries.Add(e);
                _watchService.FeedRemoteEntry(e);
            }
            Application.Current.Dispatcher.BeginInvoke(() => AddRemoteEntries(key, entries));
        });
    }

    /// <summary>远程批次的解析结果入缓冲与上屏（UI 线程）。</summary>
    private void AddRemoteEntries(string key, List<LogEntry> entries)
    {
        lock (_bufLock)
        {
            var buf = BufferFor(key);
            foreach (var e in entries)
            {
                buf.Add(e);
                if (!IsPaused && CurrentSourceKey == key && PassesFilter(e))
                    VisibleEntries.Add(e);
            }
            if (buf.Count > RingCapacity)
                buf.RemoveRange(0, buf.Count - RingCapacity);
            while (VisibleEntries.Count > RingCapacity)
                VisibleEntries.RemoveAt(0);
            TotalReceived += entries.Count;
        }
        UpdateStatus();
    }

    private bool _showDbg;
    private bool _showInf = true;
    private bool _showWrn = true;
    private bool _showErr = true;

    public bool ShowDbg { get => _showDbg; set { if (SetProperty(ref _showDbg, value)) RebuildVisible(); } }
    public bool ShowInf { get => _showInf; set { if (SetProperty(ref _showInf, value)) RebuildVisible(); } }
    public bool ShowWrn { get => _showWrn; set { if (SetProperty(ref _showWrn, value)) RebuildVisible(); } }
    public bool ShowErr { get => _showErr; set { if (SetProperty(ref _showErr, value)) RebuildVisible(); } }

    /// <summary>实例筛选下拉（多开时按 [BgiInstance] 区分，仅收集本机实例）。首项固定"全部实例"。</summary>
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
    /// <summary>暂停接收（暂停期间新日志仍入缓冲但不刷界面，继续时追平）。对所有来源统一生效。</summary>
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

    /// <summary>清空视图（只清当前选中来源的内存缓冲，不影响日志文件和其它来源）。</summary>
    public RelayCommand ClearViewCommand => new(_ =>
    {
        lock (_bufLock)
        {
            BufferFor(CurrentSourceKey).Clear();
            VisibleEntries.Clear();
        }
        UpdateStatus();
    });

    // ========== 状态栏 ==========

    private string _statusText = "等待 BGI 日志…（请确认已配置 BGI 路径且 BGI 已产生日志）";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    private string _logWatcherText = "";
    /// <summary>正在观看本机日志的人数指示（空串=无人观看，状态栏不显示）。</summary>
    public string LogWatcherText { get => _logWatcherText; set => SetProperty(ref _logWatcherText, value); }
    private int _lastWatcherCount = -1;

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

    /// <summary>200ms 合帧：把队列中的本机新条目批量入本机缓冲并刷到界面（选中本机来源时）。</summary>
    private void FlushPending()
    {
        EnsureViewerHooked(); // 200ms 节拍顺带保持观看端 SignalR 懒绑定（重连补订阅依赖此事件）
        // 远程日志下载两端的 SignalR 懒绑定也挂在这个节拍上（零额外 Timer）
        _logShare.EnsureHooked();
        Browser.EnsureSignalRHooked();
        // 观看人数指示：轮询 relay 的订阅数，变化才更新避免无谓 INPC
        var watchers = _logRelay.SubscriberCount;
        if (watchers != _lastWatcherCount)
        {
            _lastWatcherCount = watchers;
            LogWatcherText = watchers > 0 ? $"👁 {watchers} 人在看我的日志" : "";
        }
        if (_pending.IsEmpty) return;
        var batch = new List<LogEntry>();
        while (batch.Count < 2000 && _pending.TryDequeue(out var e)) batch.Add(e);
        if (batch.Count == 0) return;

        TotalReceived += batch.Count;
        lock (_bufLock)
        {
            var local = BufferFor(LocalSourceKey);
            foreach (var e in batch)
            {
                local.Add(e);
                // 收集实例列表（筛选用，仅本机）
                if (e.Instance != null && !Instances.Contains(e.Instance))
                    Instances.Add(e.Instance);
            }
            // 环形缓冲：超出容量从头部裁
            if (local.Count > RingCapacity)
                local.RemoveRange(0, local.Count - RingCapacity);

            if (!IsPaused && CurrentSourceKey == LocalSourceKey)
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
        // 中危4：实例筛选只对本机来源生效——远程行的实例段是对方机器的实例标识，
        // 套用本机下拉值会把远程视图滤成空白
        if (CurrentSourceKey == LocalSourceKey &&
            SelectedInstance != "全部实例" && e.Instance != SelectedInstance) return false;
        return true;
    }

    /// <summary>筛选/来源切换：从当前来源的环形缓冲全量重建可见列表。</summary>
    private void RebuildVisible()
    {
        lock (_bufLock)
        {
            VisibleEntries.Clear();
            foreach (var e in BufferFor(CurrentSourceKey))
                if (PassesFilter(e)) VisibleEntries.Add(e);
        }
        UpdateStatus();
    }

    private void UpdateStatus()
    {
        var file = CurrentTargetFile ?? "未定位到日志文件";
        var sourceLabel = _selectedSource?.Label ?? "本机";
        var infoOnlyHint = CurrentSourceKey != LocalSourceKey
                           && _sourceInfoOnly.TryGetValue(CurrentSourceKey, out var io) && io
            ? " · 对方已开启省流（仅 INF+）"
            : "";
        StatusText = CurrentSourceKey == LocalSourceKey
            ? $"来源: {sourceLabel} · 文件: {file} · 累计 {TotalReceived} 条 · 显示 {VisibleEntries.Count} 条" +
              (IsPaused ? " · 已暂停" : "")
            : $"来源: {sourceLabel}（远程转发，约 0.5–1 秒延迟） · 显示 {VisibleEntries.Count} 条" +
              infoOnlyHint +
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
        // 远程成员命中：文案带成员标识（SourceFile 形如 "远程:玩家名"，由日志汇聚入口标记）
        var remoteFrom = record.SourceFile.StartsWith("远程:") ? $"（成员 {record.SourceFile[3..]}）" : "";
        RaiseAlert("嘟嘟可异常监控", $"[{record.RuleName}]{remoteFrom} {FirstLine(record.Message)}");
    }

    private static string FirstLine(string text)
    {
        var idx = text.IndexOf('\n');
        var line = idx >= 0 ? text[..idx] : text;
        return line.Length > 120 ? line[..120] + "…" : line;
    }

    private void OnMembersChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => RebuildLogSources();

    /// <summary>
    /// 成员 Online 原地更新后触发（UI 线程）：重建来源列表并重估订阅，
    /// 离线成员 DesiredSubscriptionTarget 返回 null 即退订，上线则重订。
    /// </summary>
    private void OnMemberOnlineChanged() => RebuildLogSources();

    /// <summary>主 VM 属性变化：离开/回到嘟嘟可页面时重估日志订阅。</summary>
    private void OnMainVmPropertyChanged(object? sender, System.ComponentModel.PropertyChangedEventArgs e)
    {
        if (e.PropertyName == nameof(MainViewModel.CurrentPage)) EvaluateSubscription();
    }

    /// <summary>日志批接收（SignalR/Timer 线程回调）：切 UI 线程入缓冲。</summary>
    private void OnLogBatchReceived(MemberLogBatch batch)
        => Application.Current.Dispatcher.BeginInvoke(() => OnRemoteBatch(batch));

    public void Dispose()
    {
        _flushTimer.Stop();
        // 页面销毁前退订（服务端断线清理是兜底，主动退订让对方尽早停发）
        if (_currentSubscription != null)
        {
            _ = _mainVm.SignalR?.UnsubscribeMemberLogAsync(_currentSubscription);
            _currentSubscription = null;
        }
        if (_viewerHooked != null)
        {
            _viewerHooked.OnConnectionStateChanged -= OnViewerConnectionState;
            _viewerHooked = null;
        }
        _mainVm.PropertyChanged -= OnMainVmPropertyChanged;
        _mainVm.MemberOnlineChanged -= OnMemberOnlineChanged;
        _watchService.RecordAdded -= OnWatchRecordAdded;
        _tailService.EntryReceived -= OnEntryReceived;
        _tailService.HistoryBatchReceived -= OnHistoryBatch;
        _tailService.TargetFileChanged -= OnTargetFileChanged;
        _mainVm.Members.CollectionChanged -= OnMembersChanged;
        _logRelay.BatchReceived -= OnLogBatchReceived;
        Browser.Dispose();
        Monitor.Dispose();
        Stats.Dispose();
        _screenshotRelay.Dispose();
        _logShare.Dispose();
        _logRelay.Dispose();
        _statsService.Dispose();
        _watchService.Dispose();
        _tailService.Dispose();
    }
}

/// <summary>实时日志来源下拉项：本机或某个房间成员（离线成员保留可回看）。</summary>
public sealed class LogSourceOption
{
    public LogSourceOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    /// <summary>"local" 或成员 UID。</summary>
    public string Key { get; }
    public string Label { get; }
}
