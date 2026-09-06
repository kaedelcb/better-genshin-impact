using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.IO;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 桌面监控 Tab 的 ViewModel（P4 / F4 本机截图，P5 远程成员画面·按需取图）。
/// 手动刷新 + 可选自动刷新（1/3/5/10 秒，默认手动，仅本机）；历史帧环形 10 条，底部缩略图条点击切换；
/// 离开 Tab 自动停止自动刷新定时器（由 DodocoViewModel 的 Tab 切换驱动 OnTabDeactivated）。
/// 截图/编码在后台线程执行，BitmapImage 创建回 UI 线程。
/// P5 pull 模式：选中远程成员（或选中后点「📷 刷新」）→ 经 relay 向对方请求一帧 → 对方开了
/// 「共享我的桌面截图」就当场截一帧单播回来；选中远程时本机自动刷新暂停，选回本机恢复。
/// 监控模式（ObserverMode）特例："本机"画面源不截本机监控会话桌面（那里没有游戏），
/// 改为经 relay 向同 UID 的执行端按需取图（服务端 _controlRooms 里同 UID 只有执行端条目，请求会路由到执行端）。
/// 历史帧按画面源分桶（本机 / 每个成员 uid 各保留最近 10 帧），底部缩略图条只显示当前选中画面源的历史。
/// 隐私：本机截图仅本地显示；远程帧来自成员自愿共享、按需应答的 JPEG 压缩画面（宽度由共享方设置，默认 1280）。
/// </summary>
public sealed class ScreenshotViewModel : ViewModelBase, IDisposable
{
    /// <summary>自动刷新间隔选项（秒），下标即 IntervalIndex。</summary>
    public static readonly int[] IntervalOptions = { 1, 3, 5, 10 };

    /// <summary>本机画面源的 Key。</summary>
    public const string LocalKey = "local";

    /// <summary>远程缓存帧超过此时长视为陈旧，选中时自动重新请求一帧。</summary>
    private static readonly TimeSpan RemoteFrameStaleAfter = TimeSpan.FromSeconds(30);

    private readonly ScreenshotService _service;
    private readonly DodocoSettingsService _settings;
    private readonly MainViewModel _mainVm;
    private readonly MemberScreenshotRelayService _relay;
    private readonly DispatcherTimer _autoTimer;
    private bool _isCapturing;
    /// <summary>Tab 是否可见（离开时自动刷新暂停，回来恢复）。</summary>
    private bool _tabActive;
    private bool _rebuildingOptions;
    /// <summary>等待应答的请求目标 uid（收到该 uid 的帧或发起新请求时清空；超时提示用）。</summary>
    private string? _pendingRequestUid;

    /// <summary>历史帧分桶：画面源 Key（LocalKey 或成员 uid）→ 最近 10 帧（新帧在末尾）。
    /// 底部缩略图条 Frames 只是当前选中画面源对应桶的视图。</summary>
    private readonly Dictionary<string, List<FrameThumb>> _history = new();

    /// <summary>历史帧缓存总帧数（内存遥测用；跨线程只读计数，容忍竞态）。</summary>
    internal int RemoteFrameCount => _history.Values.Sum(l => l.Count);

    public ScreenshotViewModel(ScreenshotService service, DodocoSettingsService settings, MainViewModel mainVm,
        MemberScreenshotRelayService relay)
    {
        _service = service;
        _settings = settings;
        _mainVm = mainVm;
        _relay = relay;
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(CurrentIntervalSeconds) };
        _autoTimer.Tick += (_, _) => _ = CaptureAsync();

        RebuildMonitorOptions();
        _mainVm.Members.CollectionChanged += OnMembersChanged;
    }

    // ========== 当前帧 ==========

    private BitmapImage? _currentImage;
    /// <summary>大图显示的当前帧。</summary>
    public BitmapImage? CurrentImage { get => _currentImage; set => SetProperty(ref _currentImage, value); }

    /// <summary>当前大图的 JPEG 原始字节（下载/放大窗口保存用；与 CurrentImage 同步维护）。</summary>
    public byte[]? CurrentJpegBytes { get; private set; }
    /// <summary>当前帧的拍摄时刻（文件名用；无帧时为 null）。</summary>
    private DateTime? _currentFrameTime;

    /// <summary>当前帧的建议保存文件名（本机/远程 + 拍摄时刻）。</summary>
    public string SuggestedFrameFileName
    {
        get
        {
            var src = IsLocalSelected ? "本机" : SanitizeFileName(SelectedMonitor?.Label ?? "远程");
            return $"dodoco_screen_{src}_{(_currentFrameTime ?? DateTime.Now):yyyyMMdd_HHmmss}.jpg";
        }
    }

    /// <summary>下载当前画面：把当前大图的 JPEG 字节写盘（本机截图为原图，远程帧为对方共享的压缩图）。</summary>
    public RelayCommand SaveFrameCommand => new(_ =>
    {
        if (CurrentJpegBytes == null) { StatusText = "尚无画面可下载"; return; }
        var dlg = new Microsoft.Win32.SaveFileDialog
        {
            Title = "下载当前画面",
            FileName = SuggestedFrameFileName,
            Filter = "JPEG 图片|*.jpg"
        };
        if (dlg.ShowDialog() != true) return;
        try
        {
            File.WriteAllBytes(dlg.FileName, CurrentJpegBytes);
            StatusText = $"已下载到 {dlg.FileName}";
        }
        catch (Exception ex)
        {
            StatusText = $"下载失败: {ex.Message}";
        }
    });

    /// <summary>同步当前帧字节/时刻（每次更新 CurrentImage 时调用）。</summary>
    private void SetCurrentFrame(byte[]? jpeg, DateTime? time)
    {
        CurrentJpegBytes = jpeg;
        _currentFrameTime = time;
        OnPropertyChanged(nameof(CurrentJpegBytes));
    }

    /// <summary>文件名非法字符替换为下划线（成员名可能含路径非法字符）。</summary>
    private static string SanitizeFileName(string name)
    {
        var invalid = Path.GetInvalidFileNameChars();
        var chars = name.ToCharArray();
        for (var i = 0; i < chars.Length; i++)
            if (Array.IndexOf(invalid, chars[i]) >= 0) chars[i] = '_';
        return new string(chars);
    }

    private string _currentFrameInfo = "尚未截图";
    public string CurrentFrameInfo { get => _currentFrameInfo; set => SetProperty(ref _currentFrameInfo, value); }

    private bool _hasFrame;
    public bool HasFrame { get => _hasFrame; set => SetProperty(ref _hasFrame, value); }

    // ========== 历史帧缩略图条 ==========

    public ObservableCollection<FrameThumb> Frames { get; } = new();

    private FrameThumb? _selectedThumb;
    /// <summary>缩略图条选中项：点击切换大图查看历史帧。</summary>
    public FrameThumb? SelectedThumb
    {
        get => _selectedThumb;
        set
        {
            if (!SetProperty(ref _selectedThumb, value) || value == null) return;
            // 点历史帧：大图切到该帧（停自动刷新期间查看历史不被顶掉是用户预期）
            CurrentImage = ToImage(value.Frame.JpegBytes);
            SetCurrentFrame(value.Frame.JpegBytes, value.Frame.Time);
            CurrentFrameInfo = $"{value.Frame.Time:HH:mm:ss} · {value.Frame.Width}×{value.Frame.Height} · 历史帧";
        }
    }

    // ========== 画面源（本机 / 远程成员） ==========

    public ObservableCollection<MonitorOption> MonitorOptions { get; } = new();

    private MonitorOption? _selectedMonitor;
    /// <summary>画面源选中项：本机或某个远程成员。</summary>
    public MonitorOption? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (!SetProperty(ref _selectedMonitor, value)) return;
            if (_rebuildingOptions) return;
            RefreshViewForSelection();
            ApplyTimerState();
        }
    }

    /// <summary>当前是否选中本机画面源（下拉首项）。</summary>
    private bool IsLocalSelected => SelectedMonitor == null || SelectedMonitor.Key == LocalKey;

    /// <summary>本机直截：选中"本机"且非监控模式。监控模式下本机没有游戏画面，
    /// "本机"画面源改走按需取图（目标是同 UID 的执行端）。</summary>
    private bool IsLocalCapture => IsLocalSelected && !_mainVm.IsObserverMode;

    /// <summary>当前画面源走"按需取图"时的目标 uid；本机直截时为 null。</summary>
    private string? PullTargetUid => IsLocalCapture ? null
        : IsLocalSelected ? _mainVm.Config?.PlayerUid : SelectedMonitor?.Key;

    /// <summary>当前画面源的历史桶 Key：本机直截为 local，按需取图为目标 uid。</summary>
    private string CurrentSourceKey => PullTargetUid ?? LocalKey;

    /// <summary>取（或建）指定画面源的历史桶。</summary>
    private List<FrameThumb> GetHistory(string key)
    {
        if (!_history.TryGetValue(key, out var list))
        {
            list = new List<FrameThumb>(ScreenshotService.HistoryCapacity);
            _history[key] = list;
        }
        return list;
    }

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e) => RebuildMonitorOptions();

    /// <summary>按房间成员重建画面源选项（首项本机，其余为在线成员，尽量保留选中）。</summary>
    private void RebuildMonitorOptions()
    {
        var prevKey = SelectedMonitor?.Key ?? LocalKey;
        _rebuildingOptions = true;
        try
        {
            MonitorOptions.Clear();
            // 监控模式下"本机"实为同 UID 执行端的画面（经房间按需取图），标签如实标注
            MonitorOptions.Add(new MonitorOption(LocalKey,
                _mainVm.IsObserverMode ? "执行端画面（本机 UID）" : "主屏（本机）"));
            foreach (var m in _mainVm.Members)
            {
                if (m.IsSelf || string.IsNullOrEmpty(m.PlayerUid)) continue;
                MonitorOptions.Add(new MonitorOption(m.PlayerUid, $"{m.PlayerName}（远程）"));
            }
            var keep = MonitorOptions.FirstOrDefault(o => o.Key == prevKey) ?? MonitorOptions[0];
            // 成员掉线导致回退本机时，需要走一遍选中逻辑恢复视图与定时器
            _selectedMonitor = keep;
            OnPropertyChanged(nameof(SelectedMonitor));
        }
        finally
        {
            _rebuildingOptions = false;
        }
        RefreshViewForSelection();
        ApplyTimerState();
    }

    /// <summary>按当前选中画面源刷新大图区与历史缩略图条（历史条只显示该画面源的历史帧）。</summary>
    private void RefreshViewForSelection()
    {
        RebuildFramesView();
        var label = SelectedMonitor?.Label ?? "主屏（本机）";
        if (_history.TryGetValue(CurrentSourceKey, out var list) && list.Count > 0)
        {
            var f = list[^1].Frame;
            CurrentImage = ToImage(f.JpegBytes);
            SetCurrentFrame(f.JpegBytes, f.Time);
            HasFrame = true;
            if (IsLocalCapture)
            {
                CurrentFrameInfo = $"{f.Time:HH:mm:ss} · {f.Width}×{f.Height}";
            }
            else
            {
                CurrentFrameInfo = $"远程成员画面（按需取图，点「📷 刷新」取新帧） · {f.Time:HH:mm:ss}";
                // 缓存帧陈旧 → 自动请求一帧新的
                if (DateTime.Now - f.Time > RemoteFrameStaleAfter) RequestRemoteFrame();
            }
            return;
        }

        CurrentImage = null;
        SetCurrentFrame(null, null);
        HasFrame = false;
        if (IsLocalCapture)
        {
            CurrentFrameInfo = "尚未截图";
            return;
        }

        // 按需取图源（远程成员，或监控模式下的同 UID 执行端）：
        // 中间态明示：成员离线 / 对方 BGI 未运行 / 等待首帧，避免用户面对空白画面分不清是哪种情况
        if (PullTargetUid is not { Length: > 0 })
        {
            CurrentFrameInfo = "未配置本机 UID，无法向执行端取图";
            return;
        }
        var m = _mainVm.Members.FirstOrDefault(x => x.PlayerUid == PullTargetUid);
        CurrentFrameInfo = m switch
        {
            null => $"{label} 已退出房间，暂无画面",
            { Online: false } => $"{label} 已离线，暂无画面",
            { BgiStatus: "stopped" } => $"{label} 的助手在线，但其 BGI 未运行（桌面共享仍在对方助手上，需对方开启「共享我的桌面截图」）",
            _ => $"等待 {label} 的首帧画面（需对方开启「共享我的桌面截图」）…"
        };
        // 成员在线 → 自动请求首帧（离线/退房不请求，无意义）
        if (m is { Online: true }) RequestRemoteFrame();
    }

    /// <summary>把底部历史缩略图条重建为当前画面源的历史桶内容。</summary>
    private void RebuildFramesView()
    {
        Frames.Clear();
        if (_history.TryGetValue(CurrentSourceKey, out var list))
            foreach (var t in list) Frames.Add(t);
        // 清空选中（直接写字段避免 setter 触发大图切换）
        _selectedThumb = null;
        OnPropertyChanged(nameof(SelectedThumb));
    }

    /// <summary>观看端：向当前画面源请求一帧桌面截图（远程成员，或监控模式下的同 UID 执行端）；
    /// 5 秒无应答给超时提示。</summary>
    private void RequestRemoteFrame()
    {
        var uid = PullTargetUid;
        if (string.IsNullOrEmpty(uid)) return; // 本机直截（或监控模式未配 UID）不走这里
        if (_mainVm.SignalR?.IsConnected != true)
        {
            StatusText = "未连接房间，无法请求远程画面";
            return;
        }
        var label = SelectedMonitor?.Label ?? uid;
        _pendingRequestUid = uid;
        StatusText = $"已请求 {label} 的桌面画面…";
        _ = _relay.RequestFrameAsync(uid);
        // 超时提示：5 秒后仍是这个等待中的请求且用户还在看该画面源 → 对方未响应（离线/旧版/未开共享）
        Task.Delay(5000).ContinueWith(_ =>
        {
            if (_pendingRequestUid != uid) return; // 期间已收到帧或发起了新请求
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (_pendingRequestUid == uid && PullTargetUid == uid)
                    StatusText = $"{label} 未响应：对方可能离线、助手版本过旧或未开启「共享我的桌面截图」";
            });
        }, TaskScheduler.Default);
    }

    /// <summary>收到一帧远程成员截图（须在 UI 线程调用，由 DodocoViewModel Dispatcher 过来）。
    /// 帧入该成员的历史桶；正在看该画面源时同步上大屏和历史缩略图条。</summary>
    internal void OnRemoteFrame(MemberScreenshotFrame frame)
    {
        if (string.IsNullOrEmpty(frame.Uid) || string.IsNullOrEmpty(frame.JpegBase64)) return;
        if (frame.Uid == _pendingRequestUid) _pendingRequestUid = null; // 按需请求的应答到了
        byte[] bytes;
        try { bytes = Convert.FromBase64String(frame.JpegBase64); }
        catch { return; }

        var localTime = frame.CapturedAt.ToLocalTime();
        var thumb = new FrameThumb(
            new ScreenFrame { Time = localTime, JpegBytes = bytes, Width = frame.Width, Height = frame.Height },
            ToImage(bytes, 160));
        var list = GetHistory(frame.Uid);
        list.Add(thumb);
        while (list.Count > ScreenshotService.HistoryCapacity) list.RemoveAt(0);

        // 正在看这个画面源 → 直接上大屏，历史条同步追加
        if (PullTargetUid != frame.Uid) return;
        Frames.Add(thumb);
        while (Frames.Count > ScreenshotService.HistoryCapacity) Frames.RemoveAt(0);
        CurrentImage = ToImage(bytes);
        SetCurrentFrame(bytes, localTime);
        CurrentFrameInfo = $"远程成员画面（按需取图，点「📷 刷新」取新帧） · {localTime:HH:mm:ss}";
        HasFrame = true;
    }

    // ========== 刷新控制 ==========

    public RelayCommand CaptureCommand => new(_ => _ = CaptureAsync());

    /// <summary>自动刷新开关（持久化到 dodoco_settings.json；离开 Tab 时定时器自动停）。</summary>
    public bool AutoRefresh
    {
        get => _settings.Current.ScreenshotAutoRefresh;
        set
        {
            _settings.Update(s => s.ScreenshotAutoRefresh = value);
            OnPropertyChanged();
            ApplyTimerState();
        }
    }

    private int CurrentIntervalSeconds
    {
        get
        {
            var v = _settings.Current.ScreenshotIntervalSeconds;
            return IntervalOptions.Contains(v) ? v : 3;
        }
    }

    /// <summary>间隔下拉索引：0=1秒 1=3秒 2=5秒 3=10秒（持久化）。</summary>
    public int IntervalIndex
    {
        get => Array.IndexOf(IntervalOptions, CurrentIntervalSeconds);
        set
        {
            if (value < 0 || value >= IntervalOptions.Length) return;
            _settings.Update(s => s.ScreenshotIntervalSeconds = IntervalOptions[value]);
            OnPropertyChanged();
            _autoTimer.Interval = TimeSpan.FromSeconds(IntervalOptions[value]);
        }
    }

    /// <summary>隐私提示（固定文案）。</summary>
    public string PrivacyNote => "本机截图仅本地显示；远程帧为成员自愿共享的压缩画面。点击大图可放大，「⬇ 下载」保存当前帧";

    private string _statusText = "";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    /// <summary>清空当前画面源的历史帧（本机桶连带清空 ScreenshotService 的环形缓冲）。</summary>
    public RelayCommand ClearHistoryCommand => new(_ =>
    {
        if (CurrentSourceKey == LocalKey) _service.ClearHistory();
        _history.Remove(CurrentSourceKey);
        Frames.Clear();
        _selectedThumb = null;
        OnPropertyChanged(nameof(SelectedThumb));
        CurrentImage = null;
        SetCurrentFrame(null, null);
        HasFrame = false;
        CurrentFrameInfo = "尚未截图";
        StatusText = "已清空历史帧";
    });

    /// <summary>Tab 激活：恢复自动刷新（若开关开着）。由 DodocoViewModel Tab 切换驱动。</summary>
    internal void OnTabActivated()
    {
        _tabActive = true;
        ApplyTimerState();
    }

    /// <summary>离开 Tab：自动停止自动刷新定时器（开关状态保留，回来继续）。</summary>
    internal void OnTabDeactivated()
    {
        _tabActive = false;
        _autoTimer.Stop();
    }

    private void ApplyTimerState()
    {
        // 选中远程成员时自动刷新暂停（避免持续向他人索图）；选中"本机"时恢复——
        // 监控模式下"本机"=同 UID 执行端按需取图，同样允许自动刷新（服务端限流 1 次/秒，间隔最小 1 秒）
        if (AutoRefresh && _tabActive && IsLocalSelected)
        {
            _autoTimer.Interval = TimeSpan.FromSeconds(CurrentIntervalSeconds);
            _autoTimer.Start();
        }
        else
        {
            _autoTimer.Stop();
        }
    }

    /// <summary>截一帧（后台线程截图+编码，UI 线程建图）。重入守卫：上一帧未完不叠加。
    /// 选中按需取图画面源（远程成员，或监控模式下"本机"=同 UID 执行端）时改为向对方请求一帧（"点一下发一张图"）。</summary>
    private async Task CaptureAsync()
    {
        if (_isCapturing) return;
        if (!IsLocalCapture)
        {
            // 远程成员（或监控模式下的同 UID 执行端）：向对方按需请求一帧（"点一下发一张图"）
            RequestRemoteFrame();
            return;
        }
        _isCapturing = true;
        StatusText = "截图中…";
        try
        {
            var frame = await Task.Run(() => _service.Capture());
            if (frame == null)
            {
                StatusText = "截图失败（会话可能已锁定或显示器不可用）";
                return;
            }
            var thumb = new FrameThumb(frame, ToImage(frame.JpegBytes, 160));
            var list = GetHistory(LocalKey);
            list.Add(thumb);
            while (list.Count > ScreenshotService.HistoryCapacity) list.RemoveAt(0);
            Frames.Add(thumb);
            while (Frames.Count > ScreenshotService.HistoryCapacity) Frames.RemoveAt(0);
            SelectedThumb = thumb; // setter 里会刷新大图（含帧字节同步）
            CurrentImage = ToImage(frame.JpegBytes);
            SetCurrentFrame(frame.JpegBytes, frame.Time);
            CurrentFrameInfo = $"{frame.Time:HH:mm:ss} · {frame.Width}×{frame.Height}";
            HasFrame = true;
            StatusText = $"最近截图 {frame.Time:HH:mm:ss} · 历史 {Frames.Count}/{ScreenshotService.HistoryCapacity} 帧";
        }
        catch (Exception ex)
        {
            StatusText = $"截图异常: {ex.Message}";
        }
        finally
        {
            _isCapturing = false;
        }
    }

    /// <summary>JPEG 字节 → BitmapImage（须在 UI 线程；OnLoad 立即解码，Freeze 后可跨线程访问）。</summary>
    private static BitmapImage ToImage(byte[] jpeg, int decodeWidth = 0)
    {
        var bi = new BitmapImage();
        bi.BeginInit();
        bi.CacheOption = BitmapCacheOption.OnLoad; // 立即完整解码，流随即可回收
        bi.StreamSource = new MemoryStream(jpeg);
        if (decodeWidth > 0) bi.DecodePixelWidth = decodeWidth;
        bi.EndInit();
        bi.Freeze();
        return bi;
    }

    public void Dispose()
    {
        _autoTimer.Stop();
        _mainVm.Members.CollectionChanged -= OnMembersChanged;
    }
}

/// <summary>历史帧缩略图项（底部缩略图条）。</summary>
public sealed class FrameThumb
{
    public FrameThumb(ScreenFrame frame, BitmapImage thumb)
    {
        Frame = frame;
        Thumb = thumb;
    }

    public ScreenFrame Frame { get; }
    public BitmapImage Thumb { get; }
    public string TimeText => Frame.Time.ToString("HH:mm:ss");
}

/// <summary>画面源下拉项：本机或某个远程成员。</summary>
public sealed class MonitorOption
{
    public MonitorOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    /// <summary>"local" 或成员 UID。</summary>
    public string Key { get; }
    public string Label { get; }
}
