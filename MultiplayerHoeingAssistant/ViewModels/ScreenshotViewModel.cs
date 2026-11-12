using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.IO;
using BgiCoordinatorServer.Models;
using System.Windows;
using System.Windows.Media.Imaging;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 桌面监控 Tab 的 ViewModel（本机、远程成员及全员画面墙）。
/// 手动刷新 + 可选自动刷新（1/3/5/10 秒，默认手动）；历史帧按来源环形保留 10 条，单画面底部可切换；
/// 离开 Tab 自动停止自动刷新定时器（由 DodocoViewModel 的 Tab 切换驱动 OnTabDeactivated）。
/// 截图/编码在后台线程执行，BitmapImage 创建回 UI 线程。
/// pull 模式：全员视图向各在线成员并行请求；单画面选中远程成员时按需请求一帧。
/// 已连房的执行端收到请求便截帧单播回应；离开 Tab 停止自动刷新。
/// 监控模式（ObserverMode）特例："本机"画面源不截本机监控会话桌面（那里没有游戏），
/// 改为经 relay 向同 UID 的执行端按需取图（服务端 _controlRooms 里同 UID 只有执行端条目，请求会路由到执行端）。
/// 历史帧按画面源分桶（本机 / 每个成员 uid 各保留最近 10 帧），底部缩略图条只显示当前选中画面源的历史。
/// 已加入房间的成员可按需查看彼此的 JPEG 桌面帧；服务端仅向请求者单播，不存储。
/// </summary>
public sealed class ScreenshotViewModel : ViewModelBase, IDisposable
{
    /// <summary>自动刷新间隔选项（秒），下标即 IntervalIndex。</summary>
    public static readonly int[] IntervalOptions = { 1, 3, 5, 10 };

    /// <summary>本机画面源的 Key。</summary>
    public const string LocalKey = "local";

    /// <summary>所有房间成员同屏画面墙的 Key。</summary>
    public const string OverviewKey = "overview";

    /// <summary>远程缓存帧超过此时长视为陈旧，选中时自动重新请求一帧。</summary>
    private static readonly TimeSpan RemoteFrameStaleAfter = TimeSpan.FromSeconds(30);

    /// <summary>同屏画面放大后保留足够解码分辨率，避免把 480px 缩略图拉糊。</summary>
    private const int OverviewDecodeWidth = 960;

    private readonly ScreenshotService _service;
    private readonly DodocoSettingsService _settings;
    private readonly MainViewModel _mainVm;
    private readonly MemberScreenshotRelayService _relay;
    private readonly DispatcherTimer _autoTimer;
    private readonly TimeProvider _timeProvider;
    private readonly Dispatcher _dispatcher;
    private ITimer? _sessionTimer;
    private static readonly TimeSpan SessionDuration = TimeSpan.FromMinutes(3);
    private const string SessionExpiredText = "桌面监控已满 3 分钟，请退出后重新进入以继续查看";
    private long _sessionStarted;
    private string? _sessionId;
    private bool _sessionExpired;
    private bool _disposed;
    internal string? SessionId => _sessionId;
    private bool _isCapturing;
    /// <summary>Tab 是否可见（离开时自动刷新暂停，回来恢复）。</summary>
    private bool _tabActive;
    private bool _rebuildingOptions;
    /// <summary>每个目标各自等待应答，避免全员并行请求互相覆盖超时状态。</summary>
    private readonly Dictionary<string, DateTime> _pendingRequests = new();
    private readonly Dictionary<string, DateTime> _lastRequests = new();
    private readonly Func<DesktopControlCommand, bool, CancellationToken, Task<DesktopControlResult>>? _sendControl;
    private readonly Func<string, string?, Task>? _cancelControl;
    private readonly Func<bool> _controlConnected;
    private readonly Dictionary<string, long> _lastControlRequests = new();
    private readonly HashSet<string> _usedControlFrames = new(StringComparer.Ordinal);
    private FrameThumb? _currentControlFrame;
    private CancellationTokenSource? _controlCancellation;
    private ITimer? _controlStateTimer;
    private SignalRClient? _controlConnection;
    private int _controlVersion;
    private bool _controlBusy;
    private bool _awaitingControlFrame;
    private string? _controlModeTarget;
    private string? _controlRoomCode;
    private static readonly TimeSpan ControlRefreshDelay = TimeSpan.FromMilliseconds(1100);

    /// <summary>历史帧分桶：画面源 Key（LocalKey 或成员 uid）→ 最近 10 帧（新帧在末尾）。
    /// 底部缩略图条 Frames 只是当前选中画面源对应桶的视图。</summary>
    private readonly Dictionary<string, List<FrameThumb>> _history = new();

    /// <summary>历史帧缓存总帧数（内存遥测用；跨线程只读计数，容忍竞态）。</summary>
    internal int RemoteFrameCount => _history.Values.Sum(l => l.Count);

    public ScreenshotViewModel(ScreenshotService service, DodocoSettingsService settings, MainViewModel mainVm,
        MemberScreenshotRelayService relay, TimeProvider? timeProvider = null, DesktopControlService? desktopControl = null)
    {
        _timeProvider = timeProvider ?? TimeProvider.System;
        _dispatcher = Dispatcher.CurrentDispatcher;
        _service = service;
        _settings = settings;
        _mainVm = mainVm;
        _relay = relay;
        _controlConnected = () => _mainVm.SignalR?.DesktopControlReady == true;
        if (desktopControl != null)
        {
            _sendControl = (command, local, cancellation) => local
                ? desktopControl.ExecuteLocalAsync(command, _timeProvider.GetUtcNow() + DesktopControlProtocol.RequestLifetime, cancellation)
                : desktopControl.RequestAsync(command, cancellation);
            _cancelControl = desktopControl.CancelAsync;
        }
        _autoTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(CurrentIntervalSeconds) };
        _autoTimer.Tick += (_, _) => _ = CaptureAsync();

        RebuildMonitorOptions();
        _mainVm.Members.CollectionChanged += OnMembersChanged;
        _mainVm.MemberOnlineChanged += OnMemberOnlineChanged;
        _mainVm.PropertyChanged += OnControlContextChanged;
        _settings.SettingsChanged += OnControlSettingsChanged;
    }

    // 测试只替换输入通道与连接状态，仍经过正式 ViewModel 的帧/会话/频率闸门。
    internal ScreenshotViewModel(ScreenshotService service, DodocoSettingsService settings, MainViewModel mainVm,
        MemberScreenshotRelayService relay, TimeProvider timeProvider,
        Func<DesktopControlCommand, bool, CancellationToken, Task<DesktopControlResult>> sendControl,
        Func<string, string?, Task> cancelControl, Func<bool> connected)
        : this(service, settings, mainVm, relay, timeProvider)
    {
        _sendControl = sendControl;
        _cancelControl = cancelControl;
        _controlConnected = connected;
    }

    internal void ReloadConfigTransferSettings()
    {
        OnPropertyChanged(nameof(AutoRefresh));
        OnPropertyChanged(nameof(IntervalIndex));
        OnControlSettingsChanged();
        ApplyTimerState();
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
    private void SetCurrentFrame(byte[]? jpeg, DateTime? time, FrameThumb? controlFrame = null)
    {
        CurrentJpegBytes = jpeg;
        _currentFrameTime = time;
        _currentControlFrame = controlFrame;
        OnPropertyChanged(nameof(CurrentJpegBytes));
        NotifyControlState();
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
            DisableControlMode("历史帧仅供查看，请刷新后重新开启控制");
            // 点历史帧：大图切到该帧（停自动刷新期间查看历史不被顶掉是用户预期）
            CurrentImage = ToImage(value.Frame.JpegBytes);
            SetCurrentFrame(value.Frame.JpegBytes, value.Frame.Time);
            CurrentFrameInfo = $"{value.Frame.Time:HH:mm:ss} · {value.Frame.Width}×{value.Frame.Height} · 历史帧";
        }
    }

    // ========== 画面源（全员 / 本机 / 远程成员） ==========

    public ObservableCollection<MonitorOption> MonitorOptions { get; } = new();
    public ObservableCollection<MonitorTile> OverviewTiles { get; } = new();
    public ObservableCollection<MonitorTile> OverviewScreenTiles { get; } = new();
    public ObservableCollection<MonitorTile> OverviewStatusTiles { get; } = new();
    public int OverviewColumns => OverviewScreenTiles.Count switch { <= 1 => 1, <= 4 => 2, _ => 3 };
    public int OverviewStatusColumns => Math.Min(4, Math.Max(1, OverviewStatusTiles.Count));
    public bool HasOverviewScreens => OverviewScreenTiles.Count > 0;
    public bool HasOverviewStatuses => OverviewStatusTiles.Count > 0;

    /// <summary>有在线画面的成员占满主区域；尚无画面或已离线的成员只占紧凑状态栏。</summary>
    private void RebuildOverviewLayout()
    {
        OverviewScreenTiles.Clear();
        OverviewStatusTiles.Clear();
        foreach (var tile in OverviewTiles)
        {
            if (tile.IsOnline && tile.HasFrame) OverviewScreenTiles.Add(tile);
            else OverviewStatusTiles.Add(tile);
        }
        OnPropertyChanged(nameof(OverviewColumns));
        OnPropertyChanged(nameof(OverviewStatusColumns));
        OnPropertyChanged(nameof(HasOverviewScreens));
        OnPropertyChanged(nameof(HasOverviewStatuses));
    }

    private MonitorOption? _selectedMonitor;
    /// <summary>画面源选中项：本机或某个远程成员。</summary>
    public MonitorOption? SelectedMonitor
    {
        get => _selectedMonitor;
        set
        {
            if (!SetProperty(ref _selectedMonitor, value)) return;
            if (_rebuildingOptions) return;
            DisableControlMode("画面源已切换，控制模式已关闭");
            NotifySelectionChanged();
            RefreshViewForSelection();
            ApplyTimerState();
        }
    }

    public bool IsOverviewSelected => SelectedMonitor?.Key == OverviewKey;
    public bool IsSingleSelected => !IsOverviewSelected;
    public string CaptureButtonText => IsOverviewSelected ? "📷 刷新全部" : "📷 刷新";

    /// <summary>复用耕地机原有快捷键选择和执行链路，独立于截图输入模式。</summary>
    public void ExecuteBgiHotkey()
    {
        var uid = IsLocalCapture ? _mainVm.Config?.PlayerUid : PullTargetUid;
        var member = IsOverviewSelected ? null : _mainVm.Members.FirstOrDefault(m => m.PlayerUid == uid);
        if (member == null || (!IsLocalCapture && !member.Online))
        {
            StatusText = "请先选择一位在线成员，再执行 BGI 快捷功能";
            return;
        }
        if (member.Hotkeys.Count == 0)
        {
            StatusText = "该成员尚未上报 BGI 快捷键，请等待状态更新";
            return;
        }
        _mainVm.ExecuteHotkeyCommand.Execute(member);
    }
    private void NotifySelectionChanged()
    {
        OnPropertyChanged(nameof(IsOverviewSelected));
        OnPropertyChanged(nameof(IsSingleSelected));
        OnPropertyChanged(nameof(CaptureButtonText));
    }

    private bool IsLocalSelected => SelectedMonitor?.Key == LocalKey;

    /// <summary>本机直截：选中"本机"且非监控模式。监控模式下本机没有游戏画面，
    /// "本机"画面源改走按需取图（目标是同 UID 的执行端）。</summary>
    private bool IsLocalCapture => IsLocalSelected && !_mainVm.IsObserverMode;

    /// <summary>当前画面源走"按需取图"时的目标 uid；本机直截时为 null。</summary>
    private string? PullTargetUid => IsOverviewSelected || IsLocalCapture ? null
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
    private void OnMemberOnlineChanged()
    {
        var becameOnline = false;
        var selectedChanged = false;
        var layoutChanged = false;
        foreach (var tile in OverviewTiles)
        {
            if (tile.TargetUid == null) continue;
            var online = _mainVm.Members.Any(m => m.PlayerUid == tile.TargetUid && m.Online);
            if (tile.IsOnline == online) continue;
            tile.IsOnline = online;
            layoutChanged = true;
            if (online)
            {
                becameOnline = true;
                tile.Info = tile.Image == null ? "等待首帧" : tile.LastFrameText;
            }
            else
                tile.Info = tile.Image == null ? "成员离线，暂无画面" : $"成员离线 · {tile.LastFrameText}";
            selectedChanged |= PullTargetUid == tile.TargetUid;
        }
        if (layoutChanged) RebuildOverviewLayout();
        if (!ControlTargetAvailable) DisableControlMode("成员已离线，控制模式已关闭");
        if (selectedChanged && !IsOverviewSelected) RefreshViewForSelection();
        if (becameOnline && _tabActive && IsOverviewSelected) _ = RefreshOverviewAsync();
    }

    /// <summary>总开关单机切换：重建画面源列表（远程项随单机消失/恢复）。由 DodocoViewModel 转发主 VM 通知调用。</summary>
    public void RefreshForStandaloneChange() => RebuildMonitorOptions();

    /// <summary>执行/监控模式切换会改变本机画面的真实来源。</summary>
    public void RefreshForObserverModeChange() => RebuildMonitorOptions();

    /// <summary>按房间成员重建画面源与画面墙，保留当前选择和各成员的最后画面。</summary>
    private void RebuildMonitorOptions()
    {
        var prevKey = SelectedMonitor?.Key ?? OverviewKey;
        var existingTiles = OverviewTiles.ToDictionary(t => t.Key);
        _rebuildingOptions = true;
        try
        {
            MonitorOptions.Clear();
            MonitorOptions.Add(new MonitorOption(OverviewKey, "所有成员（同屏）"));
            // 监控模式下"本机"实为同 UID 执行端的画面（经房间按需取图），标签如实标注
            MonitorOptions.Add(new MonitorOption(LocalKey,
                _mainVm.IsObserverMode ? "执行端画面（本机 UID）" : "主屏（本机）"));
            foreach (var m in _mainVm.Members)
            {
                // 单机模式：画面源只留本机，不列任何远程成员
                if (_mainVm.IsStandaloneMode) break;
                if (m.IsSelf || string.IsNullOrEmpty(m.PlayerUid)) continue;
                MonitorOptions.Add(new MonitorOption(m.PlayerUid, $"{m.PlayerName}（远程）"));
            }
            OverviewTiles.Clear();
            foreach (var option in MonitorOptions.Skip(1))
            {
                var tile = existingTiles.GetValueOrDefault(option.Key) ?? new MonitorTile(option.Key);
                tile.Label = option.Label;
                tile.IsLocalCapture = option.Key == LocalKey && !_mainVm.IsObserverMode;
                var targetUid = tile.IsLocalCapture ? null
                    : option.Key == LocalKey ? _mainVm.Config?.PlayerUid : option.Key;
                if (tile.TargetUid != targetUid)
                {
                    tile.Image = null;
                    tile.LastFrameTime = null;
                }
                tile.TargetUid = targetUid;
                var member = tile.TargetUid == null ? null
                    : _mainVm.Members.FirstOrDefault(m => m.PlayerUid == tile.TargetUid);
                tile.IsOnline = tile.IsLocalCapture || member?.Online == true;
                if (tile.IsLocalCapture)
                    tile.Info = tile.Image == null ? "等待本机画面" : tile.LastFrameText;
                else if (string.IsNullOrEmpty(tile.TargetUid))
                    tile.Info = "未配置本机 UID，无法向执行端取图";
                else if (member?.Online != true)
                    tile.Info = tile.Image == null ? "成员离线，暂无画面" : $"成员离线 · {tile.LastFrameText}";
                else if (tile.Image == null)
                    tile.Info = "等待首帧";
                else if (!_pendingRequests.ContainsKey(tile.TargetUid))
                    tile.Info = tile.LastFrameText;
                OverviewTiles.Add(tile);
            }
            RebuildOverviewLayout();
            var keep = MonitorOptions.FirstOrDefault(o => o.Key == prevKey) ?? MonitorOptions[0];
            // 成员掉线导致回退本机时，需要走一遍选中逻辑恢复视图与定时器
            _selectedMonitor = keep;
            OnPropertyChanged(nameof(SelectedMonitor));
            NotifySelectionChanged();
        }
        finally
        {
            _rebuildingOptions = false;
        }
        if ((_isControlMode || _controlBusy) && (_controlModeTarget != ControlTargetUid || !ControlTargetAvailable))
            DisableControlMode("画面源或成员状态已变化，控制模式已关闭");
        RefreshViewForSelection();
        ApplyTimerState();
    }

    /// <summary>按当前选中画面源刷新大图区与历史缩略图条（历史条只显示该画面源的历史帧）。</summary>
    private void RefreshViewForSelection()
    {
        if (_controlBusy) return;
        RebuildFramesView();
        if (IsOverviewSelected)
        {
            CurrentImage = null;
            SetCurrentFrame(null, null);
            HasFrame = false;
            CurrentFrameInfo = "点击任一画面查看大图与历史";
            if (_tabActive) _ = RefreshOverviewAsync();
            return;
        }
        var label = SelectedMonitor?.Label ?? "主屏（本机）";
        if (_history.TryGetValue(CurrentSourceKey, out var list) && list.Count > 0)
        {
            var latest = list[^1];
            var f = latest.Frame;
            CurrentImage = ToImage(f.JpegBytes);
            SetCurrentFrame(f.JpegBytes, f.Time, latest);
            HasFrame = true;
            if (IsLocalCapture)
            {
                CurrentFrameInfo = $"{f.Time:HH:mm:ss} · {f.Width}×{f.Height}";
            }
            else
            {
                var online = _mainVm.Members.Any(m => m.PlayerUid == PullTargetUid && m.Online);
                CurrentFrameInfo = online
                    ? $"远程成员画面（按需取图，点「📷 刷新」取新帧） · {f.Time:HH:mm:ss}"
                    : $"成员已离线 · 最后画面 {f.Time:HH:mm:ss}";
                // 缓存帧陈旧 → 自动请求一帧新的
                if (_tabActive && online && DateTime.Now - f.Time > RemoteFrameStaleAfter) RequestRemoteFrame();
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
            { BgiStatus: "stopped" } => $"{label} 的助手在线，BGI 未运行；仍可查看对方桌面",
            _ => $"等待 {label} 的首帧画面…"
        };
        // 成员在线 → 自动请求首帧（离线/退房不请求，无意义）
        if (_tabActive && m is { Online: true }) RequestRemoteFrame();
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

    /// <summary>观看端：向目标成员请求一帧；每个目标独立跟踪等待与超时。</summary>
    private void RequestRemoteFrame() => RequestRemoteFrame(PullTargetUid, SelectedMonitor?.Label);

    private void RequestRemoteFrame(string? uid, string? label)
    {
        if (!CanCapture() || _controlBusy || string.IsNullOrEmpty(uid)) return;
        var sessionId = _sessionId!;
        if (_mainVm.IsStandaloneMode)
        {
            StatusText = "单机模式：不请求远程画面";
            return;
        }
        if (_mainVm.SignalR?.IsConnected != true)
        {
            StatusText = "未连接房间，无法请求远程画面";
            return;
        }
        var now = DateTime.UtcNow;
        if (_pendingRequests.ContainsKey(uid)) return;
        // 与服务端每目标 1 次/秒限流对齐，避免快速切换和 1 秒定时器产生大量被丢弃的请求。
        if (_lastRequests.TryGetValue(uid, out var last) && now - last < TimeSpan.FromMilliseconds(1100)) return;
        _lastRequests[uid] = now;
        _pendingRequests[uid] = now;
        NotifyControlState();
        label ??= uid;
        var tile = OverviewTiles.FirstOrDefault(t => t.TargetUid == uid);
        if (tile != null) tile.Info = "请求中…";
        if (!IsOverviewSelected) StatusText = $"已请求 {label} 的桌面画面…";
        _ = _relay.RequestFrameAsync(uid, sessionId);
        // 请求可能被旧版助手忽略；超时只更新对应成员，避免覆盖其他成员的结果。
        Task.Delay(5000).ContinueWith(_ =>
        {
            Application.Current?.Dispatcher.BeginInvoke(() =>
            {
                if (!CanCapture(sessionId) || !_pendingRequests.TryGetValue(uid, out var pending) || pending != now) return;
                _pendingRequests.Remove(uid);
                NotifyControlState();
                if (tile != null && OverviewTiles.Contains(tile)) tile.Info = "未响应：对方可能离线或助手版本过旧";
                if (!IsOverviewSelected && PullTargetUid == uid)
                    StatusText = $"{label} 未响应：对方可能离线、助手版本过旧或截图失败";
            });
        }, TaskScheduler.Default);
    }

    /// <summary>收到一帧远程成员截图（须在 UI 线程调用，由 DodocoViewModel Dispatcher 过来）。
    /// 帧入该成员的历史桶；正在看该画面源时同步上大屏和历史缩略图条。</summary>
    internal void OnRemoteFrame(MemberScreenshotFrame frame)
    {
        if (!CanCapture() || frame.RequestId?.Length != 72
            || frame.RequestId.StartsWith($"screen:{_sessionId}:", StringComparison.Ordinal) != true
            || !Guid.TryParseExact(frame.RequestId[40..], "N", out _)
            || string.IsNullOrEmpty(frame.Uid) || string.IsNullOrEmpty(frame.JpegBase64)) return;
        if (PullTargetUid == frame.Uid && !frame.ControlAllowed)
            DisableControlMode("对方未授权桌面控制，控制模式已关闭");
        if (_controlBusy && PullTargetUid == frame.Uid) return;
        if (_history.TryGetValue(frame.Uid, out var existing) && existing.Any(item => item.Frame.FrameId == frame.RequestId)) return;
        var sessionId = _sessionId!;
        var receivedAt = _timeProvider.GetTimestamp();
        byte[] bytes;
        try { bytes = Convert.FromBase64String(frame.JpegBase64); }
        catch { return; }

        // 远程负载可能不是有效 JPEG，解码失败不能让 UI 线程异常退出。
        BitmapImage thumbImage;
        BitmapImage tileImage;
        BitmapImage? fullImage;
        try
        {
            thumbImage = ToImage(bytes, 160);
            tileImage = ToImage(bytes, OverviewDecodeWidth);
            fullImage = PullTargetUid == frame.Uid ? ToImage(bytes) : null;
        }
        catch { return; }
        if (!CanCapture(sessionId)) return;
        _pendingRequests.Remove(frame.Uid);

        var localTime = frame.CapturedAt.ToLocalTime();
        var thumb = new FrameThumb(
            new ScreenFrame { Time = localTime, JpegBytes = bytes, Width = frame.Width, Height = frame.Height,
                FrameId = frame.RequestId, Geometry = frame.Geometry },
            thumbImage, receivedAt, frame.ControlAllowed, sessionId);
        var list = GetHistory(frame.Uid);
        list.Add(thumb);
        while (list.Count > ScreenshotService.HistoryCapacity) list.RemoveAt(0);

        var tile = OverviewTiles.FirstOrDefault(t => t.TargetUid == frame.Uid);
        if (tile != null)
        {
            var firstVisibleFrame = tile.IsOnline && !tile.HasFrame;
            tile.Image = tileImage;
            tile.LastFrameTime = localTime;
            tile.Info = tile.IsOnline ? tile.LastFrameText : $"成员离线 · {tile.LastFrameText}";
            if (firstVisibleFrame) RebuildOverviewLayout();
        }

        // 正在看这个画面源 → 直接上大屏，历史条同步追加
        if (PullTargetUid != frame.Uid) return;
        Frames.Add(thumb);
        while (Frames.Count > ScreenshotService.HistoryCapacity) Frames.RemoveAt(0);
        _selectedThumb = null;
        OnPropertyChanged(nameof(SelectedThumb));
        _awaitingControlFrame = false;
        CurrentImage = fullImage;
        SetCurrentFrame(bytes, localTime, thumb);
        CurrentFrameInfo = $"远程成员画面（按需取图，点「📷 刷新」取新帧） · {localTime:HH:mm:ss}";
        HasFrame = true;
        StatusText = $"已收到 {SelectedMonitor?.Label ?? frame.Uid} 的画面";
    }

    // ========== 低频桌面控制 ==========

    /// <summary>接收本机桌面输入的独立授权；共享截图不等于允许控制。</summary>
    public bool AllowDesktopControl
    {
        get => _settings.Current.AllowDesktopControl;
        set
        {
            if (!_settings.TryUpdate(s => s.AllowDesktopControl = value))
                ControlStatusText = "桌面控制授权保存失败，请检查设置文件是否可写";
            OnControlSettingsChanged();
        }
    }

    public IReadOnlyList<string> ControlKeys => DesktopControlProtocol.Keys;
    public IReadOnlyList<string> MouseActions { get; } = ["左键", "右键", "双击"];
    private int _mouseActionIndex;
    public int MouseActionIndex
    {
        get => _mouseActionIndex;
        set { if (value is >= 0 and <= 2) SetProperty(ref _mouseActionIndex, value); }
    }

    private bool _isControlMode;
    public bool IsControlMode
    {
        get => _isControlMode;
        set
        {
            if (!value) { DisableControlMode("控制模式已关闭"); return; }
            if (_isControlMode) return;
            EnsureControlConnection();
            if (!CanEnableControl)
            {
                ControlStatusText = "请选择已授权成员的新鲜最新画面，再开启控制";
                OnPropertyChanged();
                return;
            }
            _controlModeTarget = ControlTargetUid;
            _isControlMode = true;
            ControlStatusText = "控制已开启：点画面发送所选鼠标动作，或用鼠标点击下方键盘按钮";
            NotifyControlState();
        }
    }

    public string ControlNotice => "输入作用于对方当前桌面；BGI 或本机鼠标同时操作可能干扰，可手动暂停 BGI；不支持锁屏/UAC 安全桌面";
    private string _controlStatusText = "控制模式默认关闭；仅最新画面可控，每个被控端两秒一次";
    public string ControlStatusText { get => _controlStatusText; private set => SetProperty(ref _controlStatusText, value); }
    public bool ControlBusy => _controlBusy;
    public bool CanRefresh => !_controlBusy;
    public bool CanToggleControl => IsControlMode || CanEnableControl;
    public bool CanEnableControl => IsSessionOpen && _sendControl != null && !_controlBusy && !_isCapturing
        && !_awaitingControlFrame && HasFreshControlFrame && ControlTargetAvailable
        && (IsLocalCapture ? AllowDesktopControl : !_pendingRequests.ContainsKey(ControlTargetUid!));
    public bool CanSendControl => IsControlMode && CanEnableControl && ControlIntervalElapsed;

    private bool IsSessionOpen => !_disposed && _tabActive && !_sessionExpired && _sessionId != null
        && _timeProvider.GetElapsedTime(_sessionStarted) < SessionDuration;
    private string? ControlTargetUid => IsOverviewSelected ? null : IsLocalCapture ? LocalKey : PullTargetUid;
    private bool ControlTargetAvailable => IsLocalCapture || (!_mainVm.IsStandaloneMode && _controlConnected()
        && ControlTargetUid is { Length: > 0 } target
        && _mainVm.Members.Any(member => member.PlayerUid == target && member.Online));
    private bool ControlIntervalElapsed => ControlTargetUid is { } target
        && (!_lastControlRequests.TryGetValue(target, out var last)
            || _timeProvider.GetElapsedTime(last) >= DesktopControlProtocol.InputInterval);
    private bool HasFreshControlFrame => !IsOverviewSelected && _currentControlFrame is { } frame
        && frame.SessionId == _sessionId && frame.ControlAllowed && frame.Frame.Geometry?.IsValid == true
        && !string.IsNullOrEmpty(frame.Frame.FrameId) && !_usedControlFrames.Contains(frame.Frame.FrameId)
        && _history.TryGetValue(CurrentSourceKey, out var history) && ReferenceEquals(history.LastOrDefault(), frame)
        && FrameRemaining(frame) > TimeSpan.Zero;

    private TimeSpan FrameRemaining(FrameThumb frame)
    {
        var receivedAge = _timeProvider.GetElapsedTime(frame.ReceivedTimestamp);
        // 远程 CapturedAt 属于另一台机器的墙钟，只用于显示；实际捕获帧龄由目标端登记校验。
        // 查看端使用本机单调收帧时间，避免两机时钟偏差导致新图永远不可控制。
        if (!IsLocalCapture) return DesktopControlProtocol.FrameLifetime - receivedAge;
        var capturedAge = _timeProvider.GetUtcNow() - new DateTimeOffset(frame.Frame.Time.ToUniversalTime());
        return DesktopControlProtocol.FrameLifetime - (receivedAge > capturedAge ? receivedAge : capturedAge);
    }

    /// <summary>WPF Uniform 内容区到 0..1 坐标；所有参数均为同一控件的 DIP，不乘观看端 DPI。</summary>
    internal static bool TryMapControlPoint(Point position, Size viewport, Size image, out Point normalized)
    {
        normalized = default;
        if (!double.IsFinite(position.X) || !double.IsFinite(position.Y)
            || !double.IsFinite(viewport.Width) || !double.IsFinite(viewport.Height)
            || !double.IsFinite(image.Width) || !double.IsFinite(image.Height)
            || viewport.Width <= 0 || viewport.Height <= 0 || image.Width <= 0 || image.Height <= 0) return false;
        var scale = Math.Min(viewport.Width / image.Width, viewport.Height / image.Height);
        var width = image.Width * scale;
        var height = image.Height * scale;
        var x = (position.X - (viewport.Width - width) / 2) / width;
        var y = (position.Y - (viewport.Height - height) / 2) / height;
        if (!double.IsFinite(x) || !double.IsFinite(y) || x < 0 || x > 1 || y < 0 || y > 1) return false;
        normalized = new Point(x, y);
        return true;
    }

    internal Task SendMouseAsync(Point position, Size viewport, Size image)
    {
        if (!TryMapControlPoint(position, viewport, image, out var point))
        {
            ControlStatusText = "留白区域不能发送输入，请点击实际画面内部";
            return Task.CompletedTask;
        }
        var action = MouseActionIndex switch { 1 => "right", 2 => "double", _ => "left" };
        return SendControlAsync(action, point.X, point.Y);
    }

    internal Task SendKeyAsync(string key) => DesktopControlProtocol.Keys.Contains(key, StringComparer.Ordinal)
        ? SendControlAsync("key", 0, 0, key) : Task.CompletedTask;

    private async Task SendControlAsync(string action, double x, double y, string key = "")
    {
        EnsureControlConnection();
        // 执行后的画面响应等待也属于 busy；重复点击不能覆盖已确认的执行结果。
        if (_controlBusy) return;
        if (!CanCapture() || !CanSendControl || _currentControlFrame == null)
        {
            ControlStatusText = "暂不能发送输入：请确认控制已开启、两秒间隔和新鲜最新画面";
            return;
        }
        var local = IsLocalCapture;
        var target = ControlTargetUid!;
        var session = _sessionId!;
        var version = ++_controlVersion;
        var command = new DesktopControlCommand
        {
            RequestId = Guid.NewGuid().ToString("N"), TargetUid = target,
            FrameId = _currentControlFrame.Frame.FrameId, Action = action, X = x, Y = y, Key = key
        };
        using var cancellation = new CancellationTokenSource();
        _controlCancellation = cancellation;
        _usedControlFrames.Add(command.FrameId); // 一经尝试，任何结果都不能再操作同一画面。
        _lastControlRequests[target] = _timeProvider.GetTimestamp();
        _awaitingControlFrame = true;
        _controlBusy = true;
        _autoTimer.Stop();
        ControlStatusText = "正在等待系统输入回执…";
        NotifyControlState();
        var executed = false;
        try
        {
            var result = await _sendControl!(command, local, cancellation.Token)
                .WaitAsync(DesktopControlProtocol.RequestLifetime, _timeProvider, cancellation.Token);
            if (version != _controlVersion || !CanCapture(session)) return;
            if (result.RequestId != command.RequestId || result.TargetUid != target)
            {
                ControlStatusText = "回执与本次输入不匹配，执行状态未知；请刷新画面确认";
                return;
            }
            executed = result.Status == "executed";
            ControlStatusText = executed ? "系统已接受输入；请以刷新画面确认效果"
                : $"输入结果：{result.Status} · {result.Message}；请刷新画面后再操作";
            if (executed)
                await Task.Delay(ControlRefreshDelay, _timeProvider, cancellation.Token);
        }
        catch (TimeoutException)
        {
            if (version == _controlVersion)
                ControlStatusText = "输入回执超时，执行状态未知；请刷新画面确认，不会自动重发";
            cancellation.Cancel();
            await CancelRemoteControlAsync(session, local ? null : target);
        }
        catch (OperationCanceledException)
        {
            if (version == _controlVersion) ControlStatusText = "输入已取消；已送入系统的输入无法撤回，请刷新确认";
        }
        catch (Exception ex)
        {
            if (version == _controlVersion) ControlStatusText = $"输入结果未知：{ex.Message}；请刷新画面确认";
            cancellation.Cancel();
            await CancelRemoteControlAsync(session, local ? null : target);
        }
        finally
        {
            if (ReferenceEquals(_controlCancellation, cancellation)) _controlCancellation = null;
            if (version == _controlVersion)
            {
                _controlBusy = false;
                NotifyControlState();
                if (executed && CanCapture(session)) await CaptureAsync();
                ApplyTimerState();
            }
        }
    }

    private void DisableControlMode(string message)
    {
        var wasBusy = _controlBusy;
        var wasActive = _isControlMode || wasBusy;
        var target = _controlModeTarget;
        _isControlMode = false;
        _controlModeTarget = null;
        ++_controlVersion;
        _controlCancellation?.Cancel();
        _controlCancellation = null;
        _controlBusy = false;
        _awaitingControlFrame = false;
        if (wasActive)
        {
            ControlStatusText = wasBusy ? $"{message}；已送入系统的输入无法撤回，请刷新确认" : message;
            if (_sessionId is { } session && target != LocalKey) _ = CancelRemoteControlAsync(session, target);
        }
        NotifyControlState();
        ApplyTimerState();
    }

    private async Task CancelRemoteControlAsync(string session, string? target)
    {
        if (_cancelControl == null || target == null) return;
        try { await _cancelControl(session, target); }
        catch (Exception ex) { System.Diagnostics.Debug.WriteLine($"[桌面控制] 取消请求失败: {ex.Message}"); }
    }

    private void NotifyControlState()
    {
        OnPropertyChanged(nameof(IsControlMode));
        OnPropertyChanged(nameof(CanEnableControl));
        OnPropertyChanged(nameof(CanToggleControl));
        OnPropertyChanged(nameof(CanSendControl));
        OnPropertyChanged(nameof(ControlBusy));
        OnPropertyChanged(nameof(CanRefresh));
        _controlStateTimer?.Dispose();
        _controlStateTimer = null;
        if (!IsSessionOpen || _currentControlFrame == null) return;
        var remaining = FrameRemaining(_currentControlFrame);
        if (ControlTargetUid is { } target && _lastControlRequests.TryGetValue(target, out var last))
        {
            var interval = DesktopControlProtocol.InputInterval - _timeProvider.GetElapsedTime(last);
            if (interval > TimeSpan.Zero && (remaining <= TimeSpan.Zero || interval < remaining)) remaining = interval;
        }
        if (remaining <= TimeSpan.Zero) return;
        var version = _controlVersion;
        _controlStateTimer = _timeProvider.CreateTimer(_ =>
        {
            if (_dispatcher.HasShutdownStarted) return;
            _dispatcher.BeginInvoke(new Action(() =>
            {
                if (_disposed || version != _controlVersion) return;
                if (IsControlMode && !_controlBusy && !_awaitingControlFrame && !HasFreshControlFrame)
                    DisableControlMode("画面已过期，控制模式已关闭；请刷新后重新开启");
                else NotifyControlState();
            }));
        }, null, remaining, Timeout.InfiniteTimeSpan);
    }

    private void OnControlSettingsChanged()
    {
        if (!_dispatcher.CheckAccess()) { _dispatcher.BeginInvoke(new Action(OnControlSettingsChanged)); return; }
        if (_disposed) return;
        OnPropertyChanged(nameof(AllowDesktopControl));
        if (IsLocalCapture && !AllowDesktopControl) DisableControlMode("本机接收授权已关闭，控制已取消");
        else NotifyControlState();
    }

    private void InvalidateControlFrames()
    {
        foreach (var frame in _history.Values.SelectMany(frames => frames))
            if (!string.IsNullOrEmpty(frame.Frame.FrameId)) _usedControlFrames.Add(frame.Frame.FrameId);
        _currentControlFrame = null;
    }

    private void OnControlContextChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (e.PropertyName is nameof(MainViewModel.IsObserverMode) or nameof(MainViewModel.IsStandaloneMode)
            or nameof(MainViewModel.RoomCode))
        {
            InvalidateControlFrames();
            DisableControlMode("连接或运行模式已变化，控制模式已关闭；请刷新画面");
        }
        if (e.PropertyName == nameof(MainViewModel.IsConnected) && !_controlConnected())
            OnControlConnectionState(false);
        EnsureControlConnection();
    }

    internal void EnsureControlConnection()
    {
        if (_disposed) return;
        var client = _mainVm.SignalR;
        var room = _mainVm.RoomCode;
        if (ReferenceEquals(client, _controlConnection) && room == _controlRoomCode) return;
        if (_controlConnection != null)
        {
            _controlConnection.OnConnectionStateChanged -= OnControlConnectionState;
            _controlConnection.DesktopControlContextChanged -= OnControlTransportChanged;
        }
        var changed = _controlConnection != null || _controlRoomCode != null;
        _controlConnection = client;
        _controlRoomCode = room;
        if (client != null)
        {
            client.OnConnectionStateChanged += OnControlConnectionState;
            client.DesktopControlContextChanged += OnControlTransportChanged;
        }
        if (changed)
        {
            InvalidateControlFrames();
            DisableControlMode("房间连接已变化，控制模式已关闭；请刷新画面");
        }
        else NotifyControlState();
    }

    private void OnControlTransportChanged() => OnControlConnectionState(false);

    private void OnControlConnectionState(bool connected)
    {
        if (!_dispatcher.CheckAccess())
        {
            _dispatcher.BeginInvoke(new Action(() => OnControlConnectionState(connected)));
            return;
        }
        if (_disposed) return;
        if (!connected)
        {
            InvalidateControlFrames();
            DisableControlMode("连接已断开，控制模式已关闭");
        }
        else NotifyControlState();
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

    /// <summary>房间内桌面可见性提示。</summary>
    public string PrivacyNote => _sessionExpired ? SessionExpiredText
        : "加入房间后，成员可按需查看彼此的桌面；画面只发给请求者；每次进入最多查看 3 分钟";

    private string _statusText = "";
    public string StatusText { get => _statusText; set => SetProperty(ref _statusText, value); }

    /// <summary>清空当前画面源的历史帧（本机桶连带清空 ScreenshotService 的环形缓冲）。</summary>
    public RelayCommand ClearHistoryCommand => new(_ =>
    {
        DisableControlMode("历史已清空，控制模式已关闭");
        if (CurrentSourceKey == LocalKey) _service.ClearHistory();
        _history.Remove(CurrentSourceKey);
        Frames.Clear();
        _selectedThumb = null;
        OnPropertyChanged(nameof(SelectedThumb));
        CurrentImage = null;
        SetCurrentFrame(null, null);
        HasFrame = false;
        CurrentFrameInfo = "尚未截图";
        var tile = OverviewTiles.FirstOrDefault(t => t.Key == SelectedMonitor?.Key);
        if (tile != null)
        {
            var wasVisible = tile.IsOnline && tile.HasFrame;
            tile.Image = null;
            tile.LastFrameTime = null;
            tile.Info = "已清空历史";
            if (wasVisible) RebuildOverviewLayout();
        }
        StatusText = "已清空历史帧";
    });

    /// <summary>Tab 激活：恢复自动刷新（若开关开着）。由 DodocoViewModel Tab 切换驱动。</summary>
    internal void OnTabActivated()
    {
        if (_disposed || _tabActive) return;
        _tabActive = true;
        _sessionExpired = false;
        _sessionId = Guid.NewGuid().ToString("N");
        _sessionStarted = _timeProvider.GetTimestamp();
        _usedControlFrames.Clear();
        _awaitingControlFrame = false;
        EnsureControlConnection();
        _pendingRequests.Clear();
        _lastRequests.Clear();
        StatusText = "每次进入桌面监控最多查看 3 分钟";
        OnPropertyChanged(nameof(PrivacyNote));
        var sessionId = _sessionId;
        _sessionTimer = _timeProvider.CreateTimer(_ =>
        {
            if (_dispatcher.HasShutdownStarted) return;
            _dispatcher.BeginInvoke(new Action(() => { CanCapture(sessionId); }));
        }, null, SessionDuration, Timeout.InfiniteTimeSpan);
        RefreshViewForSelection();
        ApplyTimerState();
    }

    /// <summary>离开 Tab：自动停止自动刷新定时器（开关状态保留，回来继续）。</summary>
    internal void OnTabDeactivated()
    {
        _tabActive = false;
        DisableControlMode("已离开桌面监控，控制模式已关闭");
        _sessionId = null;
        _autoTimer.Stop();
        _sessionTimer?.Dispose();
        _sessionTimer = null;
        _controlStateTimer?.Dispose();
        _controlStateTimer = null;
        _pendingRequests.Clear();
    }

    // 所有取图入口与异步返回共用单调截止时间；刷新设置、重连和重复激活不续期。
    private bool CanCapture(string? expectedSessionId = null)
    {
        if (_disposed || !_tabActive || _sessionId == null
            || (expectedSessionId != null && expectedSessionId != _sessionId)) return false;
        if (!_sessionExpired && _timeProvider.GetElapsedTime(_sessionStarted) < SessionDuration) return true;
        if (_sessionExpired) return false;
        _sessionExpired = true;
        DisableControlMode("桌面监控已满 3 分钟，控制模式已关闭");
        _autoTimer.Stop();
        _sessionTimer?.Dispose();
        _sessionTimer = null;
        _pendingRequests.Clear();
        StatusText = SessionExpiredText;
        OnPropertyChanged(nameof(PrivacyNote));
        return false;
    }

    private void ApplyTimerState()
    {
        // 单画面远程按需刷新；全员画面墙和本机/同 UID 执行端可自动刷新。
        if (IsSessionOpen && !_controlBusy && AutoRefresh && (IsOverviewSelected || IsLocalSelected))
        {
            _autoTimer.Interval = TimeSpan.FromSeconds(CurrentIntervalSeconds);
            _autoTimer.Start();
        }
        else
        {
            _autoTimer.Stop();
        }
    }

    /// <summary>刷新当前画面；全员模式同时刷新本机及各在线成员。</summary>
    private async Task CaptureAsync()
    {
        if (!CanCapture() || _controlBusy) return;
        if (IsOverviewSelected)
        {
            await RefreshOverviewAsync();
            return;
        }
        if (!IsLocalCapture)
        {
            RequestRemoteFrame();
            return;
        }
        await CaptureLocalAsync();
    }

    private async Task RefreshOverviewAsync()
    {
        if (!CanCapture() || _controlBusy || !IsOverviewSelected) return;
        var captureLocal = false;
        var remoteCount = 0;
        var connected = !_mainVm.IsStandaloneMode && _mainVm.SignalR?.IsConnected == true;
        foreach (var tile in OverviewTiles.ToArray())
        {
            if (tile.IsLocalCapture)
            {
                captureLocal = true;
                continue;
            }
            if (string.IsNullOrEmpty(tile.TargetUid))
            {
                tile.Info = "未配置本机 UID，无法向执行端取图";
                continue;
            }
            var member = _mainVm.Members.FirstOrDefault(m => m.PlayerUid == tile.TargetUid);
            if (member?.Online != true)
            {
                tile.Info = tile.Image == null ? "成员离线，暂无画面" : $"成员离线 · {tile.LastFrameText}";
                continue;
            }
            remoteCount++;
            if (connected)
                RequestRemoteFrame(tile.TargetUid, tile.Label);
            else
                tile.Info = tile.Image == null ? "未连接房间，暂无画面" : $"未连接房间 · {tile.LastFrameText}";
        }
        if (!CanCapture()) return;
        StatusText = remoteCount > 0 && !connected
            ? "未连接房间，无法刷新远程成员画面"
            : remoteCount > 0
            ? $"正在查看 {remoteCount} 位在线成员的桌面；点击画面可查看大图与历史"
            : "暂无在线远程成员；点击画面可查看大图与历史";
        if (captureLocal) await CaptureLocalAsync();
    }

    /// <summary>本机截图在后台执行，当前单画面或画面墙按选中状态分别更新。</summary>
    private async Task CaptureLocalAsync()
    {
        if (!CanCapture() || _controlBusy || _isCapturing) return;
        var sessionId = _sessionId!;
        var started = _sessionStarted;
        var capturedAt = _timeProvider.GetTimestamp();
        var controlVersion = _controlVersion;
        var allowed = AllowDesktopControl;
        _isCapturing = true;
        NotifyControlState();
        if (!IsOverviewSelected) StatusText = "截图中…";
        try
        {
            var frame = await Task.Run(() =>
                !_disposed && _tabActive && _sessionId == sessionId
                && _timeProvider.GetElapsedTime(started) < SessionDuration ? _service.Capture() : null);
            if (!CanCapture(sessionId) || _controlBusy || controlVersion != _controlVersion) return;
            if (frame == null)
            {
                StatusText = "截图失败（会话可能已锁定或显示器不可用）";
                var missing = OverviewTiles.FirstOrDefault(t => t.Key == LocalKey);
                if (missing != null) missing.Info = "本机截图失败";
                return;
            }
            var thumb = new FrameThumb(frame, ToImage(frame.JpegBytes, 160), capturedAt,
                allowed && _service.TryGetControlFrame(frame.FrameId, out _), sessionId);
            var list = GetHistory(LocalKey);
            list.Add(thumb);
            while (list.Count > ScreenshotService.HistoryCapacity) list.RemoveAt(0);
            var tile = OverviewTiles.FirstOrDefault(t => t.Key == LocalKey);
            if (tile != null)
            {
                var firstVisibleFrame = tile.IsOnline && !tile.HasFrame;
                tile.Image = ToImage(frame.JpegBytes, OverviewDecodeWidth);
                tile.LastFrameTime = frame.Time;
                tile.Info = tile.LastFrameText;
                if (firstVisibleFrame) RebuildOverviewLayout();
            }
            if (IsLocalCapture)
            {
                Frames.Add(thumb);
                while (Frames.Count > ScreenshotService.HistoryCapacity) Frames.RemoveAt(0);
                _selectedThumb = null;
                OnPropertyChanged(nameof(SelectedThumb));
                _awaitingControlFrame = false;
                CurrentImage = ToImage(frame.JpegBytes);
                SetCurrentFrame(frame.JpegBytes, frame.Time, thumb);
                CurrentFrameInfo = $"{frame.Time:HH:mm:ss} · {frame.Width}×{frame.Height}";
                HasFrame = true;
                StatusText = $"最近截图 {frame.Time:HH:mm:ss} · 历史 {Frames.Count}/{ScreenshotService.HistoryCapacity} 帧";
            }
        }
        catch (Exception ex)
        {
            if (CanCapture(sessionId)) StatusText = $"截图异常: {ex.Message}";
        }
        finally
        {
            _isCapturing = false;
            NotifyControlState();
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
        if (_disposed) return;
        _disposed = true;
        OnTabDeactivated();
        _mainVm.Members.CollectionChanged -= OnMembersChanged;
        _mainVm.MemberOnlineChanged -= OnMemberOnlineChanged;
        _mainVm.PropertyChanged -= OnControlContextChanged;
        _settings.SettingsChanged -= OnControlSettingsChanged;
        if (_controlConnection != null)
        {
            _controlConnection.OnConnectionStateChanged -= OnControlConnectionState;
            _controlConnection.DesktopControlContextChanged -= OnControlTransportChanged;
        }
        _controlConnection = null;
    }
}

/// <summary>全员画面墙中的一名成员；最后一帧与状态独立更新。</summary>
public sealed class MonitorTile : ViewModelBase
{
    public MonitorTile(string key) => Key = key;

    public string Key { get; }
    public string? TargetUid { get; set; }
    public bool IsLocalCapture { get; set; }
    public bool IsOnline { get; set; }

    private string _label = "";
    public string Label { get => _label; set => SetProperty(ref _label, value); }

    private BitmapImage? _image;
    public BitmapImage? Image
    {
        get => _image;
        set
        {
            if (!SetProperty(ref _image, value)) return;
            OnPropertyChanged(nameof(HasFrame));
        }
    }
    public bool HasFrame => Image != null;

    public DateTime? LastFrameTime { get; set; }
    public string LastFrameText => LastFrameTime is { } time ? $"最近画面 {time:HH:mm:ss}" : "尚无画面";

    private string _info = "等待首帧";
    public string Info { get => _info; set => SetProperty(ref _info, value); }
}

/// <summary>历史帧缩略图项（底部缩略图条）。</summary>
public sealed class FrameThumb
{
    public FrameThumb(ScreenFrame frame, BitmapImage thumb, long receivedTimestamp = 0,
        bool controlAllowed = false, string? sessionId = null)
    {
        Frame = frame;
        Thumb = thumb;
        ReceivedTimestamp = receivedTimestamp;
        ControlAllowed = controlAllowed;
        SessionId = sessionId;
    }

    public ScreenFrame Frame { get; }
    public BitmapImage Thumb { get; }
    internal long ReceivedTimestamp { get; }
    internal bool ControlAllowed { get; }
    internal string? SessionId { get; }
    public string TimeText => Frame.Time.ToString("HH:mm:ss");
}

/// <summary>画面源下拉项：全员、本机或某个远程成员。</summary>
public sealed class MonitorOption
{
    public MonitorOption(string key, string label)
    {
        Key = key;
        Label = label;
    }

    /// <summary>"overview"、"local" 或成员 UID。</summary>
    public string Key { get; }
    public string Label { get; }

    public override string ToString() => Label;
}
