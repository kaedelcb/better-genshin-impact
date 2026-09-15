using System.Collections.Specialized;
using System.ComponentModel;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 奥黛塔桌宠 ViewModel（v2 状态机版）。
/// 并发契约：所有输入（成员行集合变化/告警跨线程事件/定时器）一律经 Dispatcher 汇聚到 UI 线程，
/// 状态机与导演层只在 UI 线程串行运行，杜绝竞态；同一 Dispatcher 帧内多次触发由 RefreshState 幂等合并。
///
/// 输入分家（自审结论⑦）：任务执行真相=本地状态快照 LatestLocalStatus（BGI 直连）；
/// 房间社交状态（上线人数/已上线）=成员行集合（服务器推送）。
///
/// 导演层时间规则：任务完成边沿后延迟 3s 才庆祝（链式下一任务 3s 内启动则取消）；
/// 告警爆发 30s 冷却防频闪；5 分钟内 ≥2 次告警升级为怒；空闲停留 20s 轮换备片。
/// </summary>
public class PetViewModel : ViewModelBase
{
    private const double SizeStep = 10;
    private const double SizeMin = 50, SizeMax = 250;
    /// <summary>任务完成后庆祝的宽限延迟（等链式下一任务）。</summary>
    private static readonly TimeSpan CelebrateGrace = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan BurstCelebrate = TimeSpan.FromSeconds(4);
    private static readonly TimeSpan BurstShort = TimeSpan.FromSeconds(2);
    private static readonly TimeSpan BurstAlert = TimeSpan.FromSeconds(3);
    /// <summary>同类惊慌爆发的冷却（防日志规则密集命中导致表情频闪）。</summary>
    private static readonly TimeSpan AlertBurstCooldown = TimeSpan.FromSeconds(30);
    /// <summary>怒的判定窗：5 分钟内 ≥2 次告警。</summary>
    private static readonly TimeSpan AngerWindow = TimeSpan.FromMinutes(5);
    /// <summary>告警惊慌的持续尾（爆发结束后仍显示无奈）。</summary>
    private static readonly TimeSpan AlertSustain = TimeSpan.FromSeconds(8);
    /// <summary>轮换节拍下限/上限：每拍随机 15~25s，避免固定周期带来的机械感。</summary>
    private static readonly TimeSpan RotateMin = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RotateSpan = TimeSpan.FromSeconds(10);
    /// <summary>悬停疑惑表情的节流。</summary>
    private static readonly TimeSpan HoverThrottle = TimeSpan.FromSeconds(15);

    private readonly MainViewModel _mainVm;
    private readonly DodocoViewModel _dodoco;
    private readonly PetSettingsService _settings;
    private readonly Action _openMainWindow;
    private readonly Dispatcher _dispatcher;
    private readonly DispatcherTimer _pollTimer;
    private readonly DispatcherTimer _transientTimer;
    private readonly DispatcherTimer _savePosTimer;
    private readonly DispatcherTimer _panelSaveTimer;

    private PetWindow? _window;
    private PetStatusPanelWindow? _panelWindow;
    private MemberViewModel? _self;
    private bool _disposed;

    // —— 好感任务跟踪（时间=本地计时；轮次=日志尾正则）——
    private readonly BgiLogTailService? _logTail;
    private DateTime? _affectionStart;
    private (int Cur, int Total)? _affectionRounds;
    private bool _lastHoeing;

    // —— 导演层运行态（仅 UI 线程访问）——
    private bool _lastTaskRunning;
    private bool _lastWasCancelled;
    private bool _lastOnlineReady;
    private DateTime _celebrateAt = DateTime.MaxValue;   // 待庆祝的触发时刻（宽限期）
    private string? _burstAnim;                          // 当前爆发动画 key（PetStateEngine.BurstToAnimKey 的输入）
    private DateTime _burstUntil = DateTime.MinValue;
    private DateTime _alertSustainUntil = DateTime.MinValue;
    private DateTime _lastAlertBurstAt = DateTime.MinValue;
    private readonly List<DateTime> _alertTimes = new();
    private DateTime _hoverLastAt = DateTime.MinValue;
    private PetState _baseState = PetState.Sleeping;
    private DateTime _baseStateSince = DateTime.UtcNow;
    /// <summary>轮换随机源与当前拍（加权随机池，见 PetStateEngine.PickRotationKey）。</summary>
    private readonly Random _rotateRng = new();
    private string _rotationKey = "sleep";
    private DateTime _nextRotateAt = DateTime.MinValue;

    public PetViewModel(MainViewModel mainVm, DodocoViewModel dodoco, PetSettingsService settings, Action openMainWindow,
        BgiLogTailService? logTail = null)
    {
        _mainVm = mainVm;
        _dodoco = dodoco;
        _settings = settings;
        _openMainWindow = openMainWindow;
        _logTail = logTail;
        _dispatcher = Dispatcher.CurrentDispatcher;

        _pollTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(5) };
        _pollTimer.Tick += (_, _) => RefreshState();
        _transientTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(250) };
        _transientTimer.Tick += (_, _) => OnTransientTick();
        _savePosTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _savePosTimer.Tick += (_, _) => { _savePosTimer.Stop(); SavePositionNow(); };
        _panelSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(600) };
        _panelSaveTimer.Tick += (_, _) => { _panelSaveTimer.Stop(); SavePanelLayoutNow(); };

        _mainVm.Members.CollectionChanged += OnMembersChanged;
        _dodoco.AlertRaised += OnAlert;
        _settings.SettingsChanged += OnSettingsChanged;
        if (_logTail != null)
        {
            // 日志尾在后台线程回调 → 回 UI 线程（并发契约）
            _logTail.EntryReceived += OnLogEntry;
        }
        HookSelf();

        // 吞设置快照（不经 setter，避免回写）
        var snap = _settings.Current;
        _enabled = snap.Enabled;
        _sizePx = Math.Clamp(snap.SizePx, SizeMin, SizeMax);
        _topmost = snap.Topmost;
        _showTaskLabel = snap.ShowTaskLabel;
        _clickThrough = snap.ClickThrough;
        _petOpacity = snap.PetOpacity;
        _panelEnabled = snap.PanelEnabled;
        _panelOpacity = snap.PanelOpacity;
        _panelMinimal = snap.PanelMinimal;
        _soundVolume = snap.SoundVolume;
        _soundMuted = snap.SoundMuted;

        RefreshState();
        _pollTimer.Start();
        ApplyVisibility();
        ApplyPanelVisibility();
        StartLocalExecutorPoll();
    }

    // ========== 设置项（改即持久化）==========

    private bool _enabled;
    /// <summary>桌宠显示开关。</summary>
    public bool Enabled
    {
        get => _enabled;
        set
        {
            if (!SetProperty(ref _enabled, value)) return;
            _settings.Update(s => s.Enabled = value);
        }
    }

    private double _sizePx;
    /// <summary>徽章边长（DIP，50~250）。</summary>
    public double SizePx
    {
        get => _sizePx;
        set
        {
            var clamped = Math.Clamp(double.IsNaN(value) ? _sizePx : value, SizeMin, SizeMax);
            if (!SetProperty(ref _sizePx, clamped)) return;
            _settings.Update(s => s.SizePx = clamped);
        }
    }

    private bool _topmost;
    public bool Topmost
    {
        get => _topmost;
        set
        {
            if (!SetProperty(ref _topmost, value)) return;
            _settings.Update(s => s.Topmost = value);
        }
    }

    private bool _showTaskLabel;
    /// <summary>是否显示宠物下方信息条（上线信息 chip）。</summary>
    public bool ShowTaskLabel
    {
        get => _showTaskLabel;
        set
        {
            if (!SetProperty(ref _showTaskLabel, value)) return;
            _settings.Update(s => s.ShowTaskLabel = value);
            OnPropertyChanged(nameof(ChipVisible));
        }
    }

    private bool _clickThrough;
    /// <summary>免打扰穿透模式：整窗点击穿透（连双击都穿过去），解锁只走助手托盘子菜单/设置页。</summary>
    public bool ClickThrough
    {
        get => _clickThrough;
        set
        {
            if (!SetProperty(ref _clickThrough, value)) return;
            _settings.Update(s => s.ClickThrough = value);
            ApplyClickThrough();
        }
    }

    private double _petOpacity = 1;
    /// <summary>宠物整体透明度（0.3~1）。</summary>
    public double PetOpacity
    {
        get => _petOpacity;
        set
        {
            var clamped = Math.Clamp(double.IsNaN(value) ? _petOpacity : value, 0.3, 1);
            if (!SetProperty(ref _petOpacity, clamped)) return;
            _settings.Update(s => s.PetOpacity = clamped);
        }
    }

    private bool _panelEnabled;
    /// <summary>任务状态详情面板显示开关（面板 ✕ 关闭即写回 false）。</summary>
    public bool PanelEnabled
    {
        get => _panelEnabled;
        set
        {
            if (!SetProperty(ref _panelEnabled, value)) return;
            _settings.Update(s => s.PanelEnabled = value);
            ApplyPanelVisibility();
        }
    }

    private double _panelOpacity = 1;
    /// <summary>详情面板透明度（0.3~1，与宠物独立）。</summary>
    public double PanelOpacity
    {
        get => _panelOpacity;
        set
        {
            var clamped = Math.Clamp(double.IsNaN(value) ? _panelOpacity : value, 0.3, 1);
            if (!SetProperty(ref _panelOpacity, clamped)) return;
            _settings.Update(s => s.PanelOpacity = clamped);
        }
    }

    private double _soundVolume = 0.6;
    /// <summary>音效音量（0~1）。</summary>
    public double SoundVolume
    {
        get => _soundVolume;
        set
        {
            var clamped = Math.Clamp(double.IsNaN(value) ? _soundVolume : value, 0, 1);
            if (!SetProperty(ref _soundVolume, clamped)) return;
            _settings.Update(s => s.SoundVolume = clamped);
        }
    }

    private bool _soundMuted;
    /// <summary>音效静音。</summary>
    public bool SoundMuted
    {
        get => _soundMuted;
        set
        {
            if (!SetProperty(ref _soundMuted, value)) return;
            _settings.Update(s => s.SoundMuted = value);
        }
    }

    /// <summary>播放音效（托盘/设置/状态边沿共用出口）。</summary>
    private void PlaySound(string key) => PetSoundPlayer.Play(key, _soundVolume, _soundMuted);

    // ========== 展示状态（窗口绑定）==========

    private string _displayKey = "sleep";
    /// <summary>当前动画 key（窗口播放器监听变化做交叉淡化切换）。爆发态优先于基础态。</summary>
    public string DisplayKey { get => _displayKey; private set => SetProperty(ref _displayKey, value); }

    private string _statusText = "";
    /// <summary>状态说明（tooltip）。</summary>
    public string StatusText { get => _statusText; private set => SetProperty(ref _statusText, value); }

    private string? _chipTimeText = "**:**";
    /// <summary>上线信息·定时时间段（空定时为 **:**）。</summary>
    public string? ChipTimeText { get => _chipTimeText; private set => SetProperty(ref _chipTimeText, value); }

    private string? _chipCountText = "0/4";
    /// <summary>上线信息·人数段（已上线/预期开锄）。</summary>
    public string? ChipCountText { get => _chipCountText; private set => SetProperty(ref _chipCountText, value); }

    private string? _chipPhaseText = "未上线";
    /// <summary>上线信息·三态词段（未上线/已上线/已联机）。</summary>
    public string? ChipPhaseText { get => _chipPhaseText; private set => SetProperty(ref _chipPhaseText, value); }

    /// <summary>上线信息纯文本（面板"上线信息"行用）。</summary>
    public string? ChipText => ChipTimeText is null ? null : $"{ChipTimeText} · {ChipCountText} · {ChipPhaseText}";

    /// <summary>信息条是否可见。</summary>
    public bool ChipVisible => ShowTaskLabel && !string.IsNullOrEmpty(ChipText);

    // ========== 命令 ==========

    public ICommand OpenMainWindowCommand => new RelayCommand(_ =>
    {
        PlaySound("click");
        _openMainWindow();
    });
    public ICommand HideCommand => new RelayCommand(_ => Enabled = false);
    public ICommand SetSizeCommand => new RelayCommand(p => { if (p is string s && double.TryParse(s, out var v)) SizePx = v; });

    /// <summary>滚轮缩放入口。</summary>
    public void AdjustSize(int direction) => SizePx += direction * SizeStep;

    /// <summary>拖拽抓起（窗口单击开始拖时调用）→ 惊。</summary>
    public void NotifyDragStarted() => StartBurst("drag", BurstShort);

    /// <summary>悬停 → 疑惑（节流）。</summary>
    public void NotifyHovered()
    {
        if ((DateTime.UtcNow - _hoverLastAt) < HoverThrottle) return;
        _hoverLastAt = DateTime.UtcNow;
        StartBurst("hover", BurstShort);
    }

    // ========== 设置联动 / 窗口生命周期 ==========

    private void OnSettingsChanged()
    {
        var snap = _settings.Current;
        SetProperty(ref _enabled, snap.Enabled, nameof(Enabled));
        SetProperty(ref _sizePx, Math.Clamp(snap.SizePx, SizeMin, SizeMax), nameof(SizePx));
        SetProperty(ref _topmost, snap.Topmost, nameof(Topmost));
        if (SetProperty(ref _showTaskLabel, snap.ShowTaskLabel, nameof(ShowTaskLabel)))
            OnPropertyChanged(nameof(ChipVisible));
        SetProperty(ref _clickThrough, snap.ClickThrough, nameof(ClickThrough));
        SetProperty(ref _petOpacity, snap.PetOpacity, nameof(PetOpacity));
        SetProperty(ref _panelOpacity, snap.PanelOpacity, nameof(PanelOpacity));
        SetProperty(ref _panelMinimal, snap.PanelMinimal, nameof(PanelMinimal));
        SetProperty(ref _soundVolume, snap.SoundVolume, nameof(SoundVolume));
        SetProperty(ref _soundMuted, snap.SoundMuted, nameof(SoundMuted));
        if (SetProperty(ref _panelEnabled, snap.PanelEnabled, nameof(PanelEnabled)))
            ApplyPanelVisibility();
        ApplyVisibility();
        ApplyClickThrough();
    }

    private void ApplyVisibility()
    {
        if (_disposed) return;
        if (Enabled)
        {
            if (_window == null)
            {
                _window = new PetWindow(this);
                _window.Closed += (_, _) => _window = null;
            }
            _window.Show();
        }
        else
        {
            _window?.Hide();
        }
    }

    // ========== 输入汇聚（全部已在 UI 线程）==========

    private void OnMembersChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        HookSelf();
        RefreshState();
    }

    private void HookSelf()
    {
        var self = _mainVm.Members.FirstOrDefault(m => m.IsSelf);
        if (ReferenceEquals(self, _self)) return;
        if (_self != null) _self.PropertyChanged -= OnSelfPropertyChanged;
        _self = self;
        if (_self != null) _self.PropertyChanged += OnSelfPropertyChanged;
    }

    private void OnSelfPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        // 服务器增量推送逐字段触发；状态类字段变化才重算（属性名 null=批量通知也重算）
        if (e.PropertyName is null
            || e.PropertyName is nameof(MemberViewModel.TaskRunning) or nameof(MemberViewModel.AutoHoeingRunning)
            or nameof(MemberViewModel.OnlineReady) or nameof(MemberViewModel.OnlineMode)
            or nameof(MemberViewModel.ScheduledOnlineTime) or nameof(MemberViewModel.CurrentTaskName)
            or nameof(MemberViewModel.CurrentTaskGroupName))
        {
            RefreshState();
        }
    }

    private void OnAlert(string title, string detail)
    {
        // RaiseAlert 在调用线程触发 → 必须回 UI 线程（并发契约）
        _dispatcher.BeginInvoke(() =>
        {
            var now = DateTime.UtcNow;
            _alertTimes.Add(now);
            _alertTimes.RemoveAll(t => (now - t) > AngerWindow);
            _alertSustainUntil = now + AlertSustain;

            // 冷却内不重复爆发（只刷新持续尾）；5 分钟 ≥2 次升级为怒（怒不受冷却限制）
            var heavy = _alertTimes.Count >= 2;
            if (heavy || (now - _lastAlertBurstAt) >= AlertBurstCooldown)
            {
                StartBurst(heavy ? "alertHeavy" : "alert", BurstAlert);
                PlaySound("alert");
                _lastAlertBurstAt = now;
            }
            RefreshState();
        });
    }

    // ========== 状态机 + 导演 ==========

    /// <summary>重算基础状态与展示。幂等：可被任意输入多次触发。</summary>
    private void RefreshState()
    {
        if (_disposed) return;
        var local = _mainVm.LatestLocalStatus;
        var now = DateTime.UtcNow;

        // 任务真相分源：执行端模式=本地快照；监控模式=本机跨会话执行端快照聚合
        // （监控端 LatestLocalStatus 恒空——跨会话管道不可达，桌宠表情须跟随执行端变化）
        bool taskRunning; bool hoeing; string? taskName; string? groupName; bool wasCancelled;
        if (_mainVm.IsObserverMode)
        {
            var running = _localExecutors.Where(e => e.TaskRunning).ToList();
            var primary = running.FirstOrDefault();
            taskRunning = primary != null;
            hoeing = running.Any(e => e.Hoeing);
            taskName = primary?.Task;
            groupName = primary?.Group;
            wasCancelled = !taskRunning && _localExecutors.Any(e => e.WasCancelled);
        }
        else
        {
            taskRunning = local?.TaskRunning ?? false;
            hoeing = local?.AutoHoeingRunning ?? false;
            taskName = local?.CurrentTaskName;
            groupName = local?.CurrentTaskGroupName;
            wasCancelled = local?.WasCancelled ?? false;
        }
        var kind = PetStateEngine.ClassifyTask(hoeing, taskName, groupName);
        bool onlineReady = _self?.OnlineReady ?? local?.OnlineReady ?? false;
        bool scheduled = (_self?.OnlineMode ?? local?.OnlineMode) == "scheduled"
                         || !string.IsNullOrEmpty(FirstNonEmpty(_self?.ScheduledOnlineTime, local?.ScheduledOnlineTime));

        // 任务完成边沿 → 安排宽限庆祝（3s 内新任务启动则取消，避免链式任务打断）
        if (_lastTaskRunning && !taskRunning)
            _celebrateAt = now + CelebrateGrace;
        // 取消边沿（BGI wasCancelled 置位且任务已停）→ 嫌弃
        if (wasCancelled && !_lastWasCancelled && !taskRunning)
            StartBurst("cancel", BurstCelebrate);
        // 上线成功边沿 → 得意 + 音效
        if (onlineReady && !_lastOnlineReady)
        {
            StartBurst("online", BurstShort);
            PlaySound("online");
        }
        // 开锄边沿 → 音效
        if (hoeing && !_lastHoeing)
            PlaySound("start");
        _lastHoeing = hoeing;

        // 好感任务计时：开始记起点，结束清零（轮次由日志尾持续刷新）
        if (taskRunning && kind == PetTaskKind.Affection)
            _affectionStart ??= now;
        else
            _affectionStart = null;

        _lastTaskRunning = taskRunning;
        _lastWasCancelled = wasCancelled;
        _lastOnlineReady = onlineReady;

        // 宽限期内有新任务启动 → 取消待庆祝
        if (_celebrateAt != DateTime.MaxValue && taskRunning)
            _celebrateAt = DateTime.MaxValue;

        var newState = PetStateEngine.ResolveBase(new PetFacts(
            BgiAlive: _mainVm.IsObserverMode ? _localExecutors.Count > 0 : IsBgiAlive(),
            TaskRunning: taskRunning, TaskKind: kind,
            OnlineReady: onlineReady, HoeingRunning: hoeing, HasScheduled: scheduled));
        if (newState != _baseState)
        {
            _baseState = newState;
            _baseStateSince = now;
            _nextRotateAt = DateTime.MinValue; // 状态切换立即重抽轮换表情
        }

        // 光晕/呼吸节奏挂语义状态（而非显示的表情 key）：
        // 好感任务显示 shy、爆发态瞬间切换 joy 等场景下，"正在执行任务"的光晕不丢
        IsTaskExecution = _baseState is PetState.Hoeing or PetState.WorkingArtifact
            or PetState.WorkingAffection or PetState.WorkingGather or PetState.WorkingOther;
        IsSleepingState = _baseState == PetState.Sleeping;

        // 过期自愈：监控模式下快照 15s 未更新且轮询线程已死 → 重新拉起（防表情/面板永久冻结）
        if (_mainVm.IsObserverMode
            && (_localExecutorsAt == null || now - _localExecutorsAt.Value > TimeSpan.FromSeconds(15)))
        {
            StartLocalExecutorPoll();
        }

        UpdateChip(hoeing, onlineReady, local);
        UpdateDisplay(now);
    }

    /// <summary>上线信息 chip 三段：定时时间/人数/三态词（图标在窗口 XAML 侧）。</summary>
    private void UpdateChip(bool hoeing, bool onlineReady, Models.ControlStatus? local)
    {
        var scheduledTime = FirstNonEmpty(_self?.ScheduledOnlineTime, local?.ScheduledOnlineTime);
        int readyCount = _mainVm.Members.Count(m => m.OnlineReady);
        // 分母（预期开锄人数）：监控模式取在线成员上报值的最小值（与服务端触发阈值同口径，
        // 修正"执行端改 1 人、监控端仍显示本机旧配置 4"）；执行端模式用本机配置。
        int expected;
        if (_mainVm.IsObserverMode)
        {
            var onlineExpected = _mainVm.Members.Where(m => m.Online)
                .Select(m => m.ExpectedHoeingPlayers).Where(v => v > 0).ToList();
            expected = onlineExpected.Count > 0 ? onlineExpected.Min()
                     : local?.ExpectedHoeingPlayers is > 0 ? local.ExpectedHoeingPlayers : 4;
        }
        else
        {
            expected = local?.ExpectedHoeingPlayers is > 0 ? local.ExpectedHoeingPlayers : 4;
        }
        // 已联机语义（用户实机纠偏）：AutoHoeingProgress 在单机锄地也会置位，仅凭它报"已联机"
        // 会把单机锄地误标成联机。修正：已联机 = 锄地任务在跑 且 今天走过上线流程（存在消费记录）。
        // 监控模式按房间成员聚合（远程小队锄地同样可见）；执行端模式看本机成员行。
        PetOnlinePhase phase;
        if (_mainVm.IsObserverMode)
        {
            var onlineMembers = _mainVm.Members.Where(m => m.Online).ToList();
            var anyHoeing = hoeing || onlineMembers.Any(m => m.AutoHoeingRunning);
            var onlineConsumed = onlineMembers.Any(m => m.OnlineHistory is { Count: > 0 });
            phase = anyHoeing && onlineConsumed ? PetOnlinePhase.Connected
                  : onlineMembers.Any(m => m.OnlineReady) ? PetOnlinePhase.Ready
                  : PetOnlinePhase.NotReady;
        }
        else
        {
            var onlineConsumed = _self?.OnlineHistory is { Count: > 0 };
            phase = hoeing && onlineConsumed ? PetOnlinePhase.Connected
                  : onlineReady ? PetOnlinePhase.Ready
                  : PetOnlinePhase.NotReady;
        }
        var (time, count, phaseText) = PetStateEngine.ComposeOnlineChipParts(scheduledTime, readyCount, expected, phase,
            ResolveRoomPlayerCount(local));
        LogChipPhaseTransition(phase, hoeing, onlineReady, local);
        ChipTimeText = time;
        ChipCountText = count;
        ChipPhaseText = phaseText;
        OnPropertyChanged(nameof(ChipText));
        OnPropertyChanged(nameof(ChipVisible));
    }

    private PetOnlinePhase _lastLoggedPhase = (PetOnlinePhase)(-1);

    /// <summary>chip 相位切换诊断：仅在相位变化时打一条，吐出全部判定输入（排查"已上线/已联机"停滞）。</summary>
    private void LogChipPhaseTransition(PetOnlinePhase phase, bool hoeing, bool onlineReady, Models.ControlStatus? local)
    {
        if (phase == _lastLoggedPhase) return;
        var prev = _lastLoggedPhase;
        _lastLoggedPhase = phase;
        if (prev == (PetOnlinePhase)(-1)) return; // 首次定相不刷日志
        string selfDesc;
        if (_mainVm.IsObserverMode)
        {
            var online = _mainVm.Members.Where(m => m.Online).ToList();
            selfDesc = $"members={_mainVm.Members.Count} online={online.Count} "
                     + $"anyMemberHoeing={online.Any(m => m.AutoHoeingRunning)} "
                     + $"anyConsumed={online.Any(m => m.OnlineHistory is { Count: > 0 })} "
                     + $"anyReady={online.Any(m => m.OnlineReady)} localExecutors={_localExecutors.Count} hoeing={hoeing}";
        }
        else
        {
            selfDesc = $"self={( _self == null ? "null" : "found" )} "
                     + $"selfHistory={_self?.OnlineHistory?.Count ?? -1} hoeing={hoeing} onlineReady={onlineReady} "
                     + $"localAutoHoeing={local?.AutoHoeingRunning.ToString() ?? "null"} roomPlayers={local?.RoomPlayerCount.ToString() ?? "null"}";
        }
        _mainVm.AddLog($"[桌宠chip] {prev} → {phase}（{selfDesc}）");
    }

    /// <summary>
    /// SignalR 房间当前人数（"已联机"态 chip 分子用）。
    /// 执行端模式：本机快照直读；监控模式：本机跨会话执行端聚合，取第一个在锄地且人数&gt;0 的执行端
    /// （同一房间的 SignalR 花名册对所有成员一致，取哪个都相同）。0=未知（旧 BGI/未在房间）→ chip 优雅降级。
    /// </summary>
    private int ResolveRoomPlayerCount(Models.ControlStatus? local)
    {
        if (!_mainVm.IsObserverMode)
            return local?.RoomPlayerCount ?? 0;
        return _localExecutors.Where(e => e.Hoeing && e.RoomPlayers > 0)
            .Select(e => e.RoomPlayers)
            .FirstOrDefault();
    }

    /// <summary>爆发态优先，其次告警持续尾（无奈），最后基础态（含空闲轮换备片）。</summary>
    private void UpdateDisplay(DateTime now)
    {
        string key;
        string status;
        if (_burstAnim != null && now < _burstUntil)
        {
            key = PetStateEngine.BurstToAnimKey(_burstAnim);
            status = DescribeBurst(_burstAnim);
        }
        else
        {
            _burstAnim = null;
            if (now < _alertSustainUntil && _baseState is not PetState.Sleeping)
            {
                // 持续尾内每 4 秒 无奈↔哀 交替（哀表情唯一的常驻出场位）
                var sinceSustain = (int)((now - (_alertSustainUntil - AlertSustain)).TotalSeconds);
                key = sinceSustain / 4 % 2 == 0 ? "helpless" : "sorrow";
                status = "唉…先盯着吧";
            }
            else
            {
                key = ResolveBaseDisplayKey(now);
                status = PetStateEngine.Describe(_baseState);
            }
        }
        DisplayKey = key;
        StatusText = status;
    }

    private string ResolveBaseDisplayKey(DateTime now)
    {
        // 加权随机轮换：节拍到期重抽一拍表情（主片过半权重，备片随机穿插不连拍），
        // 拍长 15~25s 随机抖动；爆发/告警尾不经过这里
        if (now >= _nextRotateAt)
        {
            _rotationKey = PetStateEngine.PickRotationKey(_baseState, _rotationKey, _rotateRng);
            _nextRotateAt = now + RotateMin + TimeSpan.FromSeconds(_rotateRng.NextDouble() * RotateSpan.TotalSeconds);
        }
        return _rotationKey;
    }

    private static string DescribeBurst(string burst) => burst switch
    {
        "celebrate" => "任务完成，撒花！",
        "cancel" => "任务被取消了…",
        "drag" => "哇，轻一点！",
        "hover" => "咦？",
        "online" => "上线成功！",
        "alertHeavy" => "接连出问题，气鼓鼓！",
        _ => "发现异常，快看嘟嘟可！"
    };

    /// <summary>启动爆发（覆盖当前展示，到期后由 OnTransientTick 收尾回基础态）。</summary>
    private void StartBurst(string burstKey, TimeSpan duration)
    {
        _burstAnim = burstKey;
        _burstUntil = DateTime.UtcNow + duration;
        if (!_transientTimer.IsEnabled) _transientTimer.Start();
        UpdateDisplay(DateTime.UtcNow);
    }

    /// <summary>日志尾回调（后台线程触发 → 回 UI 线程）：抓好感轮次。</summary>
    private void OnLogEntry(Models.LogEntry entry)
    {
        if (entry.Message is null) return;
        var round = PetStateEngine.ParseAffectionRound(entry.Message);
        if (round == null) return;
        _dispatcher.BeginInvoke(() => _affectionRounds = round);
    }

    /// <summary>瞬时态心跳：触发到期庆祝 / 爆发到期收尾。</summary>
    private void OnTransientTick()
    {
        var now = DateTime.UtcNow;
        if (_celebrateAt != DateTime.MaxValue && now >= _celebrateAt)
        {
            _celebrateAt = DateTime.MaxValue;
            // 庆祝前再确认没有新任务（宽限期内可能已取消）
            if (!IsAnyTaskRunningNow())
            {
                StartBurst("celebrate", BurstCelebrate);
                PlaySound("done");
            }
        }
        if (_burstAnim != null && now >= _burstUntil)
        {
            _burstAnim = null;
            UpdateDisplay(now);
        }
        if (_celebrateAt == DateTime.MaxValue && _burstAnim == null)
            _transientTimer.Stop();
    }

    private static string? FirstNonEmpty(string? a, string? b) => !string.IsNullOrWhiteSpace(a) ? a : b;

    private bool _isTaskExecution;
    /// <summary>语义状态：正在执行任务（驱动桌宠光晕与快节奏呼吸；与显示的表情 key 解耦）。</summary>
    public bool IsTaskExecution
    {
        get => _isTaskExecution;
        private set => SetProperty(ref _isTaskExecution, value);
    }

    private bool _isSleepingState;
    /// <summary>语义状态：BGI 离线沉睡（驱动慢呼吸档）。</summary>
    public bool IsSleepingState
    {
        get => _isSleepingState;
        private set => SetProperty(ref _isSleepingState, value);
    }

    private static bool IsBgiAlive()
    {
        try { return BgiProcessMonitor.GetCurrentSessionBgiProcesses().Length > 0; }
        catch { return false; }
    }

    /// <summary>聚合任务在跑判断：监控模式=任一本机执行端在跑；执行端模式=本地快照。</summary>
    private bool IsAnyTaskRunningNow()
    {
        if (_mainVm.IsObserverMode) return _localExecutors.Any(e => e.TaskRunning);
        return _mainVm.LatestLocalStatus?.TaskRunning ?? false;
    }

    // ========== 穿透模式 / 详情面板 ==========

    /// <summary>应用整窗点击穿透（WS_EX_TRANSPARENT；连双击都穿过去）。窗口句柄就绪后才有效；任务面板一并穿透。</summary>
    internal void ApplyClickThrough()
    {
        _window?.ApplyClickThrough(_clickThrough);
        _panelWindow?.ApplyClickThrough(_clickThrough);
    }

    private void ApplyPanelVisibility()
    {
        if (_disposed) return;
        if (PanelEnabled)
        {
            if (_panelWindow == null)
            {
                _panelWindow = new PetStatusPanelWindow(this);
                _panelWindow.Closed += (_, _) => _panelWindow = null;
            }
            _panelWindow.Show();
        }
        else
        {
            _panelWindow?.Close();
            _panelWindow = null;
        }
    }

    /// <summary>面板恢复位置：默认摆宠物右侧；有记忆值钳回虚拟屏。</summary>
    public (double X, double Y, bool IsDefault) GetPanelRestorePosition()
    {
        var snap = _settings.Current;
        if (snap.PanelX is { } x && snap.PanelY is { } y)
            return (ClampX(x), ClampY(y), false);
        var (px, py, _) = GetRestorePosition();
        return (Math.Min(px + SizePx + 16, SystemParameters.WorkArea.Right - snap.PanelW - 8), py, true);
    }

    /// <summary>面板记忆尺寸（W,H）。</summary>
    public (double W, double H) GetPanelSize()
    {
        var snap = _settings.Current;
        return (snap.PanelW, snap.PanelH);
    }

    public void NotifyPanelLayoutChanged(double x, double y, double w, double h)
    {
        _pendingPanel = (ClampX(x), ClampY(y),
            Math.Clamp(double.IsNaN(w) ? 300 : w, 220, 800),
            Math.Clamp(double.IsNaN(h) ? 330 : h, 200, 900));
        if (!_panelSaveTimer.IsEnabled) _panelSaveTimer.Start();
    }

    private bool _panelMinimal;
    /// <summary>任务面板极简模式：只显示任务与锄地进度，高度随内容自适应（开关在面板右键菜单）。</summary>
    public bool PanelMinimal
    {
        get => _panelMinimal;
        set
        {
            if (!SetProperty(ref _panelMinimal, value)) return;
            _settings.Update(s => s.PanelMinimal = value);
        }
    }

    private (double X, double Y, double W, double H) _pendingPanel;

    private void SavePanelLayoutNow()
    {
        var (x, y, w, h) = _pendingPanel;
        // 极简模式下高度随内容变化，不持久化（退出极简时恢复进入前记忆高度）
        _settings.Update(s => { s.PanelX = x; s.PanelY = y; s.PanelW = w; if (!PanelMinimal) s.PanelH = h; });
    }

    /// <summary>详情面板数据行（面板每秒拉取）。值恒非空（无数据显示 -）。</summary>
    public List<KeyValuePair<string, string>> BuildPanelRows()
    {
        var rows = new List<KeyValuePair<string, string>>();

        // 极简模式：只显示任务与锄地进度（监控模式取首个在跑执行端，执行端模式取本机）
        if (PanelMinimal)
        {
            string? taskName; string? progress;
            if (_mainVm.IsObserverMode)
            {
                var primary = _localExecutors.FirstOrDefault(e => e.TaskRunning);
                if (primary != null)
                {
                    taskName = primary.Task;
                    progress = primary.Progress;
                }
                else
                {
                    // 本地通道没有在跑执行端 → 回退房间成员广播
                    var member = _mainVm.Members.FirstOrDefault(m => m.Online && m.TaskRunning);
                    taskName = member?.CurrentTaskName;
                    progress = member?.AutoHoeingProgress;
                }
                // 数据源不可用时不做哑面板：直接显示原因
                if (_localExecutors.Count == 0 && taskName == null)
                    rows.Add(new("执行端", $"未发现（找到{_mainVm.LastLocalExecutorFound}台/成功{_mainVm.LastLocalExecutorOk}台）"));
            }
            else
            {
                var local = _mainVm.LatestLocalStatus;
                taskName = local?.CurrentTaskName;
                progress = local?.AutoHoeingProgress;
                if (local == null)
                    rows.Add(new("执行端", "未连接本机执行端"));
            }
            rows.Add(new("任务", OrDash(taskName)));
            if (!string.IsNullOrWhiteSpace(progress))
                rows.Add(new("锄地进度", progress!));
            return rows;
        }

        if (_mainVm.IsObserverMode)
        {
            // 监控（遥控器）模式：优先本地通道——枚举本机全部 Windows 会话中的 BGI 执行端，
            // 直连各自只读状态管道（启动中心跨会话监控同款机制，跨会话可用）；本机没有
            // 可查实例时才回退到服务端房间成员广播（覆盖监控端与执行端不同机器的部署）。
            // 行结构与执行端面板一致：每台执行端一组完整行，多台时行名加会话前缀防混淆。
            var locals = _localExecutors;
            if (locals.Count > 0)
            {
                var runningCount = locals.Count(e => e.TaskRunning);
                rows.Add(new("执行端", $"本机 {runningCount}/{locals.Count} 台在跑"));
                foreach (var e in locals)
                {
                    var label = !string.IsNullOrWhiteSpace(e.UserName) ? e.UserName! : $"会话{e.SessionId}";
                    if (label.Length > 8) label = label[..8] + "…";
                    rows.Add(new(label, e.TaskRunning ? "运行中" : "空闲"));
                    if (!e.TaskRunning) continue;
                    var prefix = locals.Count > 1 ? $"S{e.SessionId}·" : "";
                    rows.Add(new(prefix + "配置组", OrDash(e.Group)));
                    rows.Add(new(prefix + "任务", OrDash(e.Task)));
                    if (!string.IsNullOrWhiteSpace(e.Route))
                        rows.Add(new(prefix + "线路", e.Route!));
                    if (!string.IsNullOrWhiteSpace(e.ScriptRoute))
                        rows.Add(new(prefix + "线路信息", e.ScriptRoute!));
                    if (!string.IsNullOrWhiteSpace(e.Progress))
                        rows.Add(new(prefix + "锄地进度", e.Progress!));
                }
            }
            else
            {
                // 本机没有可查的 BGI 实例 → 服务端房间成员广播（跨机器拓扑），行结构与本地分支一致
                var members = _mainVm.Members.Where(m => m.Online).ToList();
                var running = members.Where(m => m.TaskRunning).ToList();
                if (members.Count == 0)
                {
                    rows.Add(new("执行端", $"未发现本机执行端（找到{_mainVm.LastLocalExecutorFound}台/成功{_mainVm.LastLocalExecutorOk}台），房间亦无在线成员"));
                }
                else
                {
                    rows.Add(new("执行端", $"{running.Count}/{members.Count} 台在跑"));
                    foreach (var m in running)
                    {
                        var name = string.IsNullOrWhiteSpace(m.PlayerName) ? m.PlayerUid : m.PlayerName!;
                        if (name.Length > 8) name = name[..8] + "…";
                        rows.Add(new(name, "运行中"));
                        var prefix = running.Count > 1 ? $"{name[..Math.Min(4, name.Length)]}·" : "";
                        rows.Add(new(prefix + "配置组", OrDash(m.CurrentTaskGroupName)));
                        rows.Add(new(prefix + "任务", OrDash(m.CurrentTaskName)));
                        if (!string.IsNullOrWhiteSpace(m.CurrentRouteDisplay))
                            rows.Add(new(prefix + "线路", m.CurrentRouteDisplay!));
                        if (!string.IsNullOrWhiteSpace(m.CurrentScriptRouteName))
                            rows.Add(new(prefix + "线路信息", m.CurrentScriptRouteName!));
                        if (!string.IsNullOrWhiteSpace(m.AutoHoeingProgress))
                            rows.Add(new(prefix + "锄地进度", m.AutoHoeingProgress!));
                    }
                }
            }
        }
        else
        {
            var local = _mainVm.LatestLocalStatus;
            rows.Add(new("茶包", local == null ? "-" : local.TaskRunning ? "运行中" : "空闲"));
            rows.Add(new("配置组", OrDash(local?.CurrentTaskGroupName)));
            rows.Add(new("任务", OrDash(local?.CurrentTaskName)));
            // 线路仅原生联机锄地任务有值，无信息时同样隐藏整行
            if (!string.IsNullOrWhiteSpace(local?.CurrentRouteDisplay))
                rows.Add(new("线路", local.CurrentRouteDisplay!));
            // 无信息时隐藏整行（显示 "-" 无信息量）
            if (!string.IsNullOrWhiteSpace(local?.CurrentScriptRouteName))
                rows.Add(new("线路信息", local.CurrentScriptRouteName!));
            if (!string.IsNullOrWhiteSpace(local?.AutoHoeingProgress))
                rows.Add(new("锄地进度", local.AutoHoeingProgress!));
        }

        rows.Add(new("上线信息", ChipText ?? "-"));
        if (_affectionStart is { } start)
        {
            var elapsed = DateTime.UtcNow - start;
            var roundText = _affectionRounds is { } r ? $" · 轮次 {r.Cur}/{r.Total}" : "";
            rows.Add(new("好感任务", $"已进行 {Math.Floor(elapsed.TotalMinutes)}分{elapsed.Seconds:00}秒{roundText}"));
        }
        return rows;
    }

    private static string OrDash(string? v) => string.IsNullOrWhiteSpace(v) ? "-" : v!;

    // ========== 监控模式：本机跨会话执行端轮询（后台查询，UI 线程落缓存） ==========

    /// <summary>一台本机执行端的展示快照。</summary>
    private sealed record LocalExecutorRow(int SessionId, string? UserName, bool TaskRunning, string? Group, string? Task, string? Route, string? ScriptRoute, string? Progress, bool Hoeing, bool WasCancelled, int RoomPlayers);

    /// <summary>最近一轮本机执行端快照（UI 线程读写；查询失败时保留旧值防闪烁）。</summary>
    private List<LocalExecutorRow> _localExecutors = [];
    private CancellationTokenSource? _localExecutorPollCts;

    private void StartLocalExecutorPoll()
    {
        if (_pollAlive) return; // 已在轮询（幂等；RefreshState 的过期自愈会调用这里）
        _pollAlive = true;
        _localExecutorPollCts = new CancellationTokenSource();
        var token = _localExecutorPollCts.Token;
        _ = Task.Run(async () =>
        {
            try
            {
                while (!token.IsCancellationRequested)
                {
                    // 循环体全包裹：任何异常都只留痕+保旧值，绝不静默杀死轮询线程
                    // （旧实现 RefreshState 在 Dispatcher 回调里抛一次异常，整个轮询就无声死亡，
                    //  _localExecutors 冻结在最后一帧——桌宠表情/面板数据"修好过又坏了"的根因）
                    try
                    {
                        var list = await _mainVm.QueryLocalExecutorsAsync(token).ConfigureAwait(false);
                        var view = list.Select(e => new LocalExecutorRow(
                            e.SessionId, e.UserName, e.TaskRunning,
                            e.CurrentTaskGroupName, e.CurrentTaskName,
                            e.CurrentRouteDisplay, e.CurrentScriptRouteName,
                            e.AutoHoeingProgress, e.AutoHoeingRunning,
                            e.WasCancelled, e.RoomPlayerCount)).ToList();
                        await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        {
                            _localExecutors = view;
                            _localExecutorsAt = DateTime.UtcNow;
                            try { RefreshState(); }
                            catch (Exception ex)
                            {
                                _mainVm.AddLog($"[桌宠] 状态刷新异常（轮询继续）: {ex.Message}");
                            }
                        });
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        if (_lastPollError != ex.Message)
                        {
                            _lastPollError = ex.Message;
                            _mainVm.AddLog($"[桌宠轮询] 查询异常（保旧值，下一轮自愈）: {ex.Message}");
                        }
                    }

                    try { await Task.Delay(TimeSpan.FromSeconds(3), token).ConfigureAwait(false); }
                    catch (OperationCanceledException) { break; }
                }
            }
            finally { _pollAlive = false; }
        });
    }

    /// <summary>轮询线程存活标志（防重复启动 + 死亡后可被过期自愈重新拉起）。</summary>
    private volatile bool _pollAlive;
    /// <summary>最近一次快照落缓存时间（过期检测用）。</summary>
    private DateTime? _localExecutorsAt;
    /// <summary>上次轮询异常指纹（去重留痕）。</summary>
    private string? _lastPollError;

    // ========== 位置记忆 ==========

    /// <summary>恢复位置：无记忆值则摆到主屏工作区右下角；有则钳制回虚拟屏。</summary>
    public (double X, double Y, bool IsDefault) GetRestorePosition()
    {
        var snap = _settings.Current;
        if (snap.PosX is { } x && snap.PosY is { } y)
            return (ClampX(x), ClampY(y), false);
        var area = SystemParameters.WorkArea;
        return (area.Right - SizePx - 32, area.Bottom - SizePx - 24, true);
    }

    public void NotifyDragged(double x, double y)
    {
        _pendingX = ClampX(x);
        _pendingY = ClampY(y);
        if (!_savePosTimer.IsEnabled) _savePosTimer.Start();
    }

    private double _pendingX, _pendingY;

    private void SavePositionNow()
    {
        double x = _pendingX, y = _pendingY;
        _settings.Update(s => { s.PosX = x; s.PosY = y; });
    }

    private static double ClampX(double x) => Math.Clamp(x,
        SystemParameters.VirtualScreenLeft - 80, SystemParameters.VirtualScreenLeft + SystemParameters.VirtualScreenWidth - 40);
    private static double ClampY(double y) => Math.Clamp(y,
        SystemParameters.VirtualScreenTop, SystemParameters.VirtualScreenTop + SystemParameters.VirtualScreenHeight - 40);

    // ========== 生命周期 ==========

    public void Shutdown()
    {
        if (_disposed) return;
        _disposed = true;
        _pollTimer.Stop();
        _transientTimer.Stop();
        _savePosTimer.Stop();
        _localExecutorPollCts?.Cancel();
        _mainVm.Members.CollectionChanged -= OnMembersChanged;
        _dodoco.AlertRaised -= OnAlert;
        _settings.SettingsChanged -= OnSettingsChanged;
        if (_logTail != null) _logTail.EntryReceived -= OnLogEntry;
        if (_self != null) _self.PropertyChanged -= OnSelfPropertyChanged;
        _window?.Close();
        _window = null;
        _panelWindow?.Close();
        _panelWindow = null;
    }
}
