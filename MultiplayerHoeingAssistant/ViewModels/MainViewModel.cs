using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.AspNetCore.SignalR.Client;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;
using Timer = System.Threading.Timer;

namespace MultiplayerHoeingAssistant.ViewModels;

public class MainViewModel : INotifyPropertyChanged
{
    private SignalRClient? _signalRClient;
    private IpcClient? _ipcClient;
    private BgiProcessMonitor? _processMonitor;
    private CommandExecutor? _commandExecutor;
    private AssistConfig? _config;
    private AssistConfigManager? _configManager;
    private MemberConfigCacheManager? _cacheManager;
    /// <summary>远程配置组编辑会话状态机（契约见 Docs/远程配置组编辑-实施方案.md §5）。</summary>
    private RemoteConfigEditService? _remoteConfigEditService;
    /// <summary>任务策略远程互改通道（task_policy.pull/push，按 CommandId 关联 TCS；仅上线锄地完成后动作三项）。</summary>
    private TaskPolicySyncService? _taskPolicySync;
    private string _roomCode = "";
    private bool _isConnected;
    private string _lastLoggedProgress = "";
    private Timer? _statusTimer;
    private Timer? _retryTimer;
    private Timer? _onlineTimer;
    private Timer? _resumeTimeoutTimer;
    /// <summary>[冷却倒计时] 防抖拦截提示的 1s 节拍计时器（DispatcherTimer，与项目其他 VM 展示刷新同款）：
    /// 仅防抖冷却窗口内运行，窗口结束自动停表清空文本；null = 未点亮过/已停。</summary>
    private System.Windows.Threading.DispatcherTimer? _debounceCountdownTimer;
    /// <summary>[P1] 上线意图生命周期状态机（Idle/Armed/Executing）：收编原散落本类 12 处直接赋值点的
    /// _isOnlineReady/_onlineMode/_localOnlineGeneration/_genLock/_lastOnlineGeneration/
    /// _lastProcessedAllReadyGeneration/_lastReportedOnlineGen/_lastOnlineEventReportedAtUtc/
    /// _pendingOnlineReportGen/_onlineBaselineInitialized/_manuallyClearedOnline 与 generation 持久化的全部读写。
    /// 事故背景：异常路径漏改残留状态曾劫持后续行为（幻影上线循环、幻影开锄）。网络 IO 留在本类。</summary>
    private readonly OnlineIntentLifecycle _intentLifecycle;
    /// <summary>记录定时上线今天是否已触发过（按日期去重，设定新时间时重置）。
    /// [实机修复 2026-09-05] 持久化到 assistant-online-fired-date.txt：重启不再失忆（原实现重启即复位，
    /// 1:00 的定时 1:30 打开助手会被"补触发"上线——非用户意图）。</summary>
    private DateTime _lastScheduledFireDate = DateTime.MinValue;
    /// <summary>定时上线补触发宽限窗：到点后超过此时长才看到 = 当时人不在/没开机，跳过今日不再补上线。</summary>
    private static readonly TimeSpan ScheduledFireGrace = TimeSpan.FromMinutes(15);
    // [P1] 本地 generation（_localOnlineGeneration/_genLock）、边沿基线（_lastOnlineGeneration）、
    // AllReady 守卫（_lastProcessedAllReadyGeneration）、上报跟踪（_lastReportedOnlineGen/
    // _lastOnlineEventReportedAtUtc/_pendingOnlineReportGen/_onlineBaselineInitialized/
    // OnlineEdgeFreshnessWindow/OnlineReportDebounce）已全部收编进 OnlineIntentLifecycle。
    /// <summary>[切片1] ext.event 事件通道客户端（BgiExternalClient SDK）；null = 尚未建立/已降级。</summary>
    private BgiExternalClient? _externalClient;
    /// <summary>[切片1] 事件通道探测退避：Legacy（老 BGI）或暂时连不上时，到此时间点之前不再探测。</summary>
    private DateTime _externalNextProbeUtc = DateTime.MinValue;
    /// <summary>[切片4] 事件驱动维护的 ext.task.status 快照（SDK 基线/跳号/事件触发刷新产物）；null = 尚未取得。</summary>
    private string? _latestExtStatusJson;
    /// <summary>[P1b] 当前 AllReady 批次句柄（收编原 fire-and-forget 批次与共享 bool _isAllReadySequenceCancelled：
    /// 外部"用户手动停止"改为取消当前批次 CTS；新一轮 AllReady 到达先取消旧批次再启动）。</summary>
    private OnlineHoeingBatch? _activeBatch;
    /// <summary>[P1b] _activeBatch 读写锁（批次在后台线程完结，新一轮判定在 SignalR 回调线程）。</summary>
    private readonly object _batchGate = new();
    /// <summary>[P1b] 策略收尾恰好一次守卫：已完成收尾的最高批次 generation（单调不减，代序号比对）。
    /// 新一轮 AllReady 不重置——重置会让旧轮次的 10s 恢复定时器在新一轮里重跑收尾。</summary>
    private int _teardownDoneGeneration;
    /// <summary>[P1b] _teardownDoneGeneration 读写锁。</summary>
    private readonly object _teardownGate = new();
    /// <summary>[P1b] 最近一次策略收尾实际执行时间（UTC）：10s 恢复定时器的无批次键直接路径据此去重——
    /// 批次末尾收尾与 autoHoeingRunning 边沿几乎同时命中时，30s 内的迟到边沿直接跳过；
    /// 超过 30s 判定为无关手动会话，正常执行。读写均须在 _teardownGate 下。</summary>
    private DateTime _lastTeardownUtc = DateTime.MinValue;
    /// <summary>互斥锁：防止两轮 AllReady 并发执行 OnAllReadyConfirmedInternal（patterns §31）。</summary>
    private int _isAllReadyProcessing;
    // [P2 仲裁] 原 _isBgiRestarting 重入守卫已删除：崩溃重启收编进 BgiProcessMonitor 仲裁器，
    // SemaphoreSlim 串行化全部杀/启操作，守卫语义重复且 async 等待期间长占位会挡住合法重启。
    /// <summary>重入守卫：ReportStatusAsync 触发源众多（10s 定时器、连接恢复、ext 事件等约 20 处），
    /// 且单轮可能卡在 IPC 读上超过 10s；不设防会并发叠加 → 同一秒多条"ReportControlStatusAsync 调用失败"
    /// 日志风暴 + 每轮各持一条命名管道连接。状态是快照语义，跳过本轮由下一轮补齐即可。</summary>
    private int _reportStatusRunning;
    /// <summary>重入守卫：连点"刷新房间"/切模式并发跑多轮 RefreshAsync 会互相 Dispose 对方刚建好的
    /// 连接（_signalRClient 是共享字段），后到的刷新会把先到的成功成果拆掉。串行化即可。</summary>
    private int _refreshRunning;
    /// <summary>重入守卫：重试定时器回调里 ConnectAsync 的 StartAsync 对"假死服务器"（TCP 通、应用不应答）
    /// 可能挂到协商超时（~100s）才返回，周期 10s 不设防会叠加多条半开连接——服务器恢复时可能同时
    /// 成功多条，只有最后一条被 _signalRClient 收留，其余成幽灵连接占用服务端组。两个重试定时器
    /// （首次连接/刷新后重建）互不同时存活，共用此守卫。</summary>
    private int _retryRunning;
    /// <summary>[P1] 手动清除上线抑制标志已收编进 OnlineIntentLifecycle.ManuallyClearedOnline。</summary>
    private bool _wasAutoHoeingRunning;

    /// <summary>成员角色头像池（按加入顺序循环分配，file=资源名，ring=元素色描边）。</summary>
    private static readonly (string file, string ring)[] AvatarPool =
    {
        ("Ayaka", "#9FD1E8"), ("Kazuha", "#6FD8CE"), ("Ganyu", "#9FD1E8"), ("Yoimiya", "#E88A6F"),
        ("Shougun", "#C9A0E8"), ("Furina", "#8FC1E8"), ("Zhongli", "#D9A84E"), ("Nahida", "#A8D878"),
    };

    public ObservableCollection<MemberViewModel> Members { get; } = new();
    public ObservableCollection<string> CommandLogs { get; } = new();

    /// <summary>本机 BGI 进程是否在运行（嘟嘟可中间态提示用；进程监控未启动时视为未运行）。</summary>
    public bool IsBgiRunning => _processMonitor?.IsBgiRunning == true;

    private string _commandLogsText = "";
    public string CommandLogsText
    {
        get => _commandLogsText;
        set { _commandLogsText = value; OnPropertyChanged(); }
    }

    public string RoomCode
    {
        get => _roomCode;
        set { _roomCode = value; OnPropertyChanged(); }
    }

    public bool IsConnected
    {
        get => _isConnected;
        set { _isConnected = value; OnPropertyChanged(); }
    }

    private string _onlineDebounceCountdownText = "";
    /// <summary>[冷却倒计时] 命令上线 60s 防抖拦截提示文本（如"重复触发冷却 42s"）：
    /// 空串 = 不显示（XAML 侧绑空串隐藏）。仅发生 Debounced 拦截后点亮，窗口结束自动清空。</summary>
    public string OnlineDebounceCountdownText
    {
        get => _onlineDebounceCountdownText;
        set { if (_onlineDebounceCountdownText == value) return; _onlineDebounceCountdownText = value; OnPropertyChanged(); }
    }

    private bool _isIpcSessionUntrusted;
    /// <summary>
    /// IPC 管道是否不可信：命名管道指向了其他 Windows 会话的 BGI 实例，或无法确认对端会话归属。
    /// 为 true 时状态轮询忽略管道返回的 task.status、控制指令被阻断，标题栏显示"跨会话"警告徽章。
    /// </summary>
    public bool IsIpcSessionUntrusted
    {
        get => _isIpcSessionUntrusted;
        private set { if (_isIpcSessionUntrusted == value) return; _isIpcSessionUntrusted = value; OnPropertyChanged(); }
    }

    /// <summary>
    /// 根据本轮 IPC 握手结果更新 <see cref="IsIpcSessionUntrusted"/>，并做边沿检测：
    /// 仅在可信状态发生变化时打日志，避免 10 秒轮询刷屏。
    /// </summary>
    private void UpdateIpcSessionTrust(IpcClient ipcClient)
    {
        var untrusted = !ipcClient.IsSessionTrusted;
        if (untrusted == IsIpcSessionUntrusted) return;
        IsIpcSessionUntrusted = untrusted;

        if (untrusted)
        {
            var localSid = System.Diagnostics.Process.GetCurrentProcess().SessionId;
            AddLog(ipcClient.SessionCheck == IpcSessionCheck.CrossSession
                ? $"[IPC] 警告：命名管道指向了其他 Windows 会话的 BGI 实例（对端 Session={ipcClient.RemoteSessionId?.ToString() ?? "?"} PID={ipcClient.RemoteProcessId?.ToString() ?? "?"}，本会话 Session={localSid}），其任务状态已忽略、控制指令已阻断。请检查是否存在多会话多开"
                : "[IPC] 警告：无法确认管道对端 BGI 所属会话（Ping 握手未通过），按不可信处理：任务状态已忽略、控制指令已阻断");
        }
        else
        {
            AddLog("[IPC] 管道对端已确认为本会话的 BGI 实例，任务状态恢复采信");
        }
    }

    private AppPage _currentPage;
    /// <summary>当前内容区页面（三态导航：Home=成员列表主页 / Settings=设置页 / Dodoco=嘟嘟可日志监控）。</summary>
    public AppPage CurrentPage
    {
        get => _currentPage;
        set
        {
            if (_currentPage == value) return;
            _currentPage = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(IsShowingSettings));
        }
    }

    /// <summary>是否正在显示设置页面（兼容旧二态写法；读=CurrentPage==Settings，写=true→Settings / false→Home）</summary>
    public bool IsShowingSettings
    {
        get => _currentPage == AppPage.Settings;
        set => CurrentPage = value ? AppPage.Settings : AppPage.Home;
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>刷新（重新加载页面）完成后触发，供 MainWindow 重建标签区 / 刷新成员卡片 UI。</summary>
    public event Action? RefreshCompleted;

    /// <summary>
    /// 成员在线状态在 OnPlayersUpdated 原地更新（不触发 CollectionChanged）后触发一次，
    /// 供 DodocoViewModel 重新评估日志订阅（成员离线退订 / 上线重订）。始终在 UI 线程触发。
    /// </summary>
    public event Action? MemberOnlineChanged;

    public RelayCommand StopCommand => new(OnStop);
    public RelayCommand StartBgiCommand => new(OnStartBgi);
    public RelayCommand StartGroupCommand => new(OnStartGroup);
    public RelayCommand StartOneClickCommand => new(OnStartOneClick);
    public RelayCommand ExecuteHotkeyCommand => new(OnExecuteHotkey);
    public RelayCommand CloseGameCommand => new(OnCloseGame);
    public RelayCommand ExitCommand => new(_ => OnExit());
    public RelayCommand RefreshCommand => new(_ => _ = RefreshAsync());
    public RelayCommand ShowOnlineHistoryCommand => new(OnShowOnlineHistory);
    public RelayCommand ScheduledOnlineCommand => new(OnScheduledOnline);
    public RelayCommand ClearOnlineCommand => new(OnClearOnline);
    public RelayCommand ClearOnlineHistoryCommand => new(OnClearOnlineHistory);
    public RelayCommand BindHoeingGroupCommand => new(OnBindHoeingGroup);
    /// <summary>远程编辑成员配置组（成员卡片昵称右侧 ⚙ 按钮）。</summary>
    public RelayCommand RemoteConfigEditCommand => new(OnRemoteConfigEdit);
    public RelayCommand ClearLogCommand => new(_ => ClearLog());

    /// <summary>切换执行/监控模式（点击连接徽章触发）</summary>
    public RelayCommand SwitchModeCommand => new(_ => _ = SwitchModeAsync());

    // ===== 一键快捷命令（给所有在线成员下发执行绑定配置组/一条龙）=====
    public RelayCommand QuickLegendCommand => new(_ => _ = ExecuteQuickCommandAsync("一键传奇"));
    public RelayCommand QuickShieldCommand => new(_ => _ = ExecuteQuickCommandAsync("一键次数盾"));
    public RelayCommand QuickEliteCommand => new(_ => _ = ExecuteQuickCommandAsync("一键精英"));
    public RelayCommand QuickMultiCommand => new(_ => _ = ExecuteQuickCommandAsync("一键小怪"));
    public RelayCommand QuickCustomCommand => new(_ => _ = ExecuteQuickCommandAsync("一键自定义"));
    /// <summary>一键锄地：向所有选中成员下发执行绑定的联机锄地配置组（按顺序执行）。</summary>
    public RelayCommand QuickHoeingCommand => new(_ => _ = ExecuteQuickHoeingAsync());

    /// <summary>打开设置页面（切换右侧内容区为设置页）。</summary>
    public RelayCommand OpenSettingsCommand => new(_ => ToggleSettings());

    /// <summary>打开嘟嘟可页面（日志与监控系统，切换右侧内容区为 DodocoPage）。</summary>
    public RelayCommand ShowDodocoCommand => new(_ => CurrentPage = AppPage.Dodoco);

    /// <summary>打开槲寄生页面（调度器，切换右侧内容区为 MistletoePage）。</summary>
    public RelayCommand ShowMistletoeCommand => new(_ => CurrentPage = AppPage.Mistletoe);

    /// <summary>
    /// InitializeAsync 全部完成后触发（SignalR 首连已发起）。供槲寄生启动中心做"助手启动后自动执行流程"的挂点。
    /// </summary>
    public event Action? Initialized;

    /// <summary>打开房间设置弹窗（复用 SettingsWindow）。</summary>
    public RelayCommand OpenRoomSettingsCommand => new(_ => OpenRoomSettings());

    /// <summary>关闭设置页面（返回成员列表主页）。</summary>
    public RelayCommand CloseSettingsCommand => new(_ => IsShowingSettings = false);

    /// <summary>[P1] 显式构造函数：创建上线意图状态机（构造内恢复持久化的本地 generation）。
    /// 不能用字段内联初始化——C# 字段初始化器不能引用实例方法 AddLog（CS0236）。</summary>
    public MainViewModel()
    {
        _intentLifecycle = new OnlineIntentLifecycle(AddLog);
    }

    public async Task InitializeAsync()
    {
        _configManager = new AssistConfigManager();
        _config = _configManager.Load();
        _cacheManager = new MemberConfigCacheManager();

        // 配置加载完成后刷新设置页绑定（否则 UI 初次绑定时 _config 为 null，控件显示未选/未读状态）
        RefreshSetupBindings();

        // 免责声明：首次启动弹窗，用户必须勾选同意才能继续
        if (!_config.DisclaimerAccepted)
        {
            var accepted = DisclaimerWindow.ShowDisclaimer();
            if (accepted)
            {
                _config.DisclaimerAccepted = true;
                _configManager.Save(_config);
            }
            else
            {
                Application.Current.Shutdown();
                return;
            }
        }

        // 设置弹窗：如果配置不完整（缺密码 / 缺 TeamUids / 缺 PlayerName / 缺 PlayerUid），弹出一次性设置弹窗
        if (string.IsNullOrEmpty(_config.ControlRoomPassword)
            || _config.TeamUids.Count == 0
            || string.IsNullOrEmpty(_config.PlayerName)
            || string.IsNullOrEmpty(_config.PlayerUid))
        {
            var newConfig = SettingsWindow.ShowSettingsDialog(_config);
            if (newConfig == null)
            {
                MessageBox.Show("设置已取消，请重新启动助手完成配置。");
                Application.Current.Shutdown();
                return;
            }
            _config = newConfig;
            _configManager.Save(_config);
            NotifyModeBindings(); // 首启设置弹窗可能改了遥控器模式，同步刷新模式显隐绑定
        }

        // 生成房间码
        RoomCode = AssistConfigManager.GenerateControlRoomCode(_config.TeamUids);

        // S3：generation 持久化恢复已收编进 OnlineIntentLifecycle 构造函数（语义不变：重启不复位）。

        // [实机修复 2026-09-05] 定时上线"已触发日期"持久化恢复：重启不失忆，
        // 到点已过且超出宽限窗的场景（如设 1:00、1:30 才打开助手）不再被补触发上线。
        _lastScheduledFireDate = LoadPersistedScheduledFireDate();

        // S1：重启后按"是否已配置定时上线时间"初始化武装状态——配置了时间则解除手动清除抑制，否则重启后定时器永久静默 return。
        // 抑制标志不持久化：本会话内手动清除上线（ClearLocalOnline）后仍抑制到重新设定为止（语义不变）。
        if (!string.IsNullOrEmpty(_config.ScheduledOnlineTime))
        {
            _intentLifecycle.NoteScheduledOnlineConfigured();
        }

        // 启动定时上线定时器（设定过 scheduledOnlineTime 才会真正到点触发）
        StartOnlineScheduler();

        // 生成实例标识（UUID），用于服务端区分同 UID 的多个连接实例
        if (string.IsNullOrEmpty(_config.ClientInstanceId))
        {
            _config.ClientInstanceId = Guid.NewGuid().ToString("N");
            _configManager?.Save(_config);
        }

        // 根据配置应用模式运行时（启动/跳过 BGI 进程监控）。与 SwitchModeAsync 共享同一逻辑。
        ApplyModeRuntime(_config.ObserverMode);

        // 连接 SignalR
        await ConnectSignalRAsync();

        // 槲寄生启动中心挂点：初始化全部完成后广播（自动执行启动流程监听此事件）
        Initialized?.Invoke();
    }

    private async Task ConnectSignalRAsync()
    {
        try
        {
            _signalRClient = new SignalRClient();
            WireSignalRClient(_signalRClient);

            // 连接状态变化（断开/重连）同步到 IsConnected → 标题栏连接徽章实时刷新
            _signalRClient.OnConnectionStateChanged += connected =>
            {
                Application.Current.Dispatcher.Invoke(() => IsConnected = connected);
                // 连接恢复后立即上报状态，无需等待 10 秒定时器
                if (connected) _ = ReportStatusAsync();
                // [B157] 断线时服务端已清零本成员上线事件，重连后补报上线意图（服务端按 gen 去重，安全）
                if (connected) _ = ReReportOnlineIntentIfNeededAsync();
            };

            await _signalRClient.ConnectAsync(
                _config!.ServerUrl, RoomCode, _config.ControlRoomPassword,
                _config.PlayerUid, _config.PlayerName, _config.TeamUids, _config.ObserverMode, _config.ClientInstanceId,
                _config.BypassSystemProxy);

            IsConnected = true;
            AddLog("已连接控制房间");

            // 上报状态
            await ReportStatusAsync();

            // 启动定时上报（每10秒）
            _statusTimer = new Timer(async _ => await ReportStatusAsync(), null, 
                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
        catch (Exception ex)
        {
            AddLog($"连接失败: {ex.Message}，10 秒后自动重试");
            // WithAutomaticReconnect 不重试"首次连接失败"：这里每 10 秒重试，直到连上
            _retryTimer = new Timer(async _ =>
            {
                // 上次重试的 StartAsync 可能还挂着（假死服务器协商超时 ~100s），不叠加（详见 _retryRunning 注释）
                if (Interlocked.CompareExchange(ref _retryRunning, 1, 0) != 0) return;
                try
                {
                    if (_signalRClient == null || !_signalRClient.IsConnected)
                    {
                        await _signalRClient!.ConnectAsync(
                            _config!.ServerUrl, RoomCode, _config.ControlRoomPassword,
                            _config.PlayerUid, _config.PlayerName, _config.TeamUids, _config.ObserverMode, _config.ClientInstanceId,
                            _config.BypassSystemProxy);
                        Application.Current.Dispatcher.Invoke(() =>
                        {
                            IsConnected = true;
                            AddLog("已连接控制房间");
                        });
                        if (_statusTimer == null)
                        {
                            _statusTimer = new Timer(async _2 => await ReportStatusAsync(), null,
                                TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                        }
                        _ = ReportStatusAsync();
                        _retryTimer?.Dispose();
                        _retryTimer = null;
                    }
                }
                catch (Exception retryEx)
                {
                    // 重试失败保持定时器继续，但失败原因必须可见——"连不回来"时靠它区分
                    // 服务器不可达（网络/DNS/服务挂）与服务器拒绝（密码/白名单/协议不兼容）
                    AddLog($"连接重试失败: {retryEx.Message}（10 秒后继续）");
                }
                finally
                {
                    _retryRunning = 0;
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    /// <summary>
    /// [切片1/4] 确保 ext.* 通道接管 BGI 状态同步：切片1 只订阅 online.triggered；
    /// 切片4 起订阅全部已知事件——task.status 轮询改为"事件触发 SDK 快照刷新 + 快照缓存"驱动，
    /// 通道不可用时所有读取点回退 v2 IpcClient 轮询（兜底路径逐字节保留）。
    /// 返回 true = 事件通道活跃（Ready 且订阅已恢复）；false = 降级走原有 v2 轮询路径。
    /// 老 BGI（ext.hello 不支持）→ Legacy 静默降级、1 分钟后再探测，全程无报错。
    /// 在状态轮询 Timer 线程调用；事件回调跑在 SDK 读线程，两者都是线程池后台线程（同级）。
    /// </summary>
    private async Task<bool> TryEstablishExternalChannelAsync()
    {
        if (_config?.ObserverMode == true)
        {
            return false;
        }

        try
        {
            if (_externalClient == null)
            {
                if (DateTime.UtcNow < _externalNextProbeUtc)
                {
                    return false;
                }

                var client = new BgiExternalClient();
                var state = await client.StartAsync();
                if (state == BgiExternalLinkState.Ready)
                {
                    client.EventReceived += OnBgiExternalEvent;
                    client.StatusSnapshotUpdated += OnBgiStatusSnapshotUpdated;
                    client.ConnectionStateChanged += OnBgiExternalConnectionStateChanged;
                    _externalClient = client;
                }
                else
                {
                    // Legacy（老 BGI）或暂时连不上：退避后由下一轮轮询再探测，本轮走 v2 轮询
                    client.Dispose();
                    _externalNextProbeUtc = DateTime.UtcNow.AddMinutes(1);
                    return false;
                }
            }

            // 连接内断线重连后订阅会失效（订阅挂在 BGI 侧会话上），补订；
            // SDK 恢复订阅时自动携带 lastKnownRevision 续传缺失事件并拉基线快照校准
            if (_externalClient is { State: BgiExternalLinkState.Ready } readyClient
                && !readyClient.IsEventChannelActive)
            {
                await readyClient.SubscribeAsync([]);
            }

            return _externalClient.IsEventChannelActive;
        }
        catch
        {
            // 事件通道故障不阻塞主流程：本轮降级 v2 轮询，下轮再试
            return false;
        }
    }

    /// <summary>[切片4] ext 连接状态机变更日志（验收②：Degraded→重连→Ready 全流程可观测）。</summary>
    private void OnBgiExternalConnectionStateChanged(BgiExternalConnectionState state)
    {
        try
        {
            AddLog($"[ext] BGI 外部接口通道状态 → {state}");
        }
        catch
        {
            // 日志失败不影响连接管理
        }
    }

    /// <summary>
    /// [切片4] SDK 快照更新通知：缓存最新 ext.task.status 快照（ReportStatusAsync 直接取用，
    /// 不再周期轮询 task.status），并做 onlineGeneration 边沿检测——与 v2 轮询路径的 P0-B
    /// 基线同步语义逐条一致（快照校准场景补报断线窗口内错过的上线事件）。
    /// </summary>
    private void OnBgiStatusSnapshotUpdated(BgiExternalStatusSnapshot snapshot)
    {
        try
        {
            _latestExtStatusJson = snapshot.DataJson;

            using var doc = System.Text.Json.JsonDocument.Parse(snapshot.DataJson);
            if (!doc.RootElement.TryGetProperty("onlineGeneration", out var ogEl)
                || ogEl.ValueKind != System.Text.Json.JsonValueKind.Number
                || !ogEl.TryGetInt32(out var gen))
            {
                return;
            }

            // [B157] 首见边沿新鲜度校验用：BGI task.status 聚合的 onlineTriggeredAt（UTC ISO）
            DateTime? triggeredAt = null;
            if (doc.RootElement.TryGetProperty("onlineTriggeredAt", out var taEl)
                && taEl.ValueKind == System.Text.Json.JsonValueKind.String
                && DateTime.TryParse(taEl.GetString(), out var taVal))
            {
                triggeredAt = taVal.Kind == DateTimeKind.Utc ? taVal : taVal.ToUniversalTime();
            }
            // [P1] 首见判定/新鲜度校验/基线同步/双来源对齐/防抖全部收编进状态机
            ApplyOnlineGenerationEdge(gen, triggeredAt);
        }
        catch
        {
            // 快照解析失败不影响主流程
        }
    }

    /// <summary>[P1] onlineGeneration 边沿入口（事件帧/快照/v2 轮询共用）：状态判定全部收口到
    /// OnlineIntentLifecycle.ApplyEdge（BGI 重启归零下行对齐、双来源对齐写盘、2 分钟新鲜度窗、60s 防抖）；
    /// 意图被接受（Accepted）时由本类完成 SignalR 上报（网络 IO 不进状态机）。</summary>
    private void ApplyOnlineGenerationEdge(int gen, DateTime? triggeredAtUtc = null)
    {
        var edgeResult = _intentLifecycle.ApplyEdge(gen, triggeredAtUtc, out var genToReport);
        if (edgeResult == OnlineEdgeResult.Accepted)
        {
            _ = ReportOnlineEventTrackedAsync(genToReport);
        }
        else if (edgeResult == OnlineEdgeResult.Debounced)
        {
            // [冷却倒计时] 防抖拦截只记日志时用户完全无感知（反复触发看不到冷却），点亮 UI 倒计时
            LightUpOnlineDebounceCountdown();
        }
    }

    /// <summary>[冷却倒计时] 防抖拦截后点亮（或重置）倒计时：冷却基准是状态机的 _lastOnlineEventReportedAtUtc，
    /// 口径为"只在发生拦截后显示"（窗口内无拦截不打扰）。调用方可能在后台线程（状态轮询/ext 事件），
    /// DispatcherTimer 必须在 UI 线程操作，故走 BeginInvoke。</summary>
    private void LightUpOnlineDebounceCountdown()
    {
        Application.Current?.Dispatcher.BeginInvoke(() =>
        {
            _debounceCountdownTimer ??= new System.Windows.Threading.DispatcherTimer { Interval = TimeSpan.FromSeconds(1) };
            _debounceCountdownTimer.Tick -= OnDebounceCountdownTick;
            _debounceCountdownTimer.Tick += OnDebounceCountdownTick;
            RefreshOnlineDebounceCountdown();
            _debounceCountdownTimer.Start();
        });
    }

    private void OnDebounceCountdownTick(object? sender, EventArgs e) => RefreshOnlineDebounceCountdown();

    /// <summary>[冷却倒计时] 刷新剩余秒数（UI 线程）：窗口内显示"重复触发冷却 Ns"（向上取整），
    /// 窗口结束自动清空文本并停表。</summary>
    private void RefreshOnlineDebounceCountdown()
    {
        var remaining = _intentLifecycle.GetDebounceRemaining();
        if (remaining <= TimeSpan.Zero)
        {
            OnlineDebounceCountdownText = "";
            _debounceCountdownTimer?.Stop();
        }
        else
        {
            OnlineDebounceCountdownText = $"重复触发冷却 {(int)Math.Ceiling(remaining.TotalSeconds)}s";
        }
    }

    /// <summary>[冷却倒计时] 停掉倒计时并清空文本（Shutdown/切遥控器模式时调用，防泄漏与残留显示）。
    /// DispatcherTimer 只能在其 Dispatcher 线程操作，调用方可能不在 UI 线程，做 CheckAccess 守卫。</summary>
    private void StopOnlineDebounceCountdown()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess())
        {
            _debounceCountdownTimer?.Stop();
            OnlineDebounceCountdownText = "";
        }
        else
        {
            dispatcher.BeginInvoke(() =>
            {
                _debounceCountdownTimer?.Stop();
                OnlineDebounceCountdownText = "";
            });
        }
    }

    /// <summary>[B157] 上报上线事件并跟踪结果：送达则记入状态机送达台账（防抖与重连补报基准）；
    /// 未送达（无客户端/未连接/调用失败）则挂起待补报，由 10s 轮询与重连钩子自动补报。
    /// 替代旧的 fire-and-forget 静默丢弃（那是"偶发执行了不上线"的直接根因）。</summary>
    private async Task<bool> ReportOnlineEventTrackedAsync(int gen)
    {
        if (_signalRClient == null)
        {
            _intentLifecycle.NoteReportPending(gen);
            return false;
        }
        var ok = await _signalRClient.ReportOnlineEventAsync(gen, true);
        if (ok)
        {
            _intentLifecycle.NoteReportDelivered(gen);
        }
        else
        {
            _intentLifecycle.NoteReportPending(gen);
            AddLog($"上线事件（generation={gen}）未送达服务端，已挂起，连接恢复后每 10 秒自动补报");
        }
        return ok;
    }

    /// <summary>[B157] 上线意图补报：断线时服务端已清零本成员上线事件（RemoveFromControlRoom），
    /// 只重发状态不补报事件会让本成员从就绪集合静默消失（UI 显示已上线但服务端不算就绪）。
    /// 服务端按 generation 边沿去重（gen ≤ 槽位且未消费时忽略），重复补报安全。
    /// 调用点：连接恢复钩子、10s 状态轮询（仅挂起待补报时）。</summary>
    private async Task ReReportOnlineIntentIfNeededAsync()
    {
        if (_signalRClient?.IsConnected != true) return;
        if (!_intentLifecycle.TryGetReReportGen(out var gen)) return;
        if (await _signalRClient.ReportOnlineEventAsync(gen, true))
        {
            _intentLifecycle.NoteReReportDelivered(gen);
            AddLog($"已向服务端补报上线意图（generation={gen}）");
        }
    }

    /// <summary>
    /// [切片1/4] ext.event 事件回调（SDK 读线程）。online.triggered 直接消费（语义与 v2 轮询
    /// 边沿检测一致）；切片4 起 task.*/hoeing.* 事件触发 SDK 快照刷新（事件驱动替代 10s
    /// task.status 轮询），快照由 OnBgiStatusSnapshotUpdated 应用。
    /// </summary>
    private void OnBgiExternalEvent(BgiExternalEvent evt)
    {
        try
        {
            if (evt.Name == BgiExternalEventNames.OnlineTriggered)
            {
                if (!evt.Payload.TryGetProperty("generation", out var genEl)
                    || !genEl.TryGetInt32(out var gen))
                {
                    return;
                }

                ApplyOnlineGenerationEdge(gen);
                return;
            }

            // 任务/锄地状态事件：触发一次快照刷新（SDK 内部 300ms 节流 + 在飞去重）
            if (evt.Name is BgiExternalEventNames.TaskStarted
                or BgiExternalEventNames.TaskStopped
                or BgiExternalEventNames.TaskProgress
                or BgiExternalEventNames.HoeingProgress
                or BgiExternalEventNames.TaskSuspended
                or BgiExternalEventNames.TaskResumed)
            {
                _ = _externalClient?.RefreshStatusSnapshotAsync($"event:{evt.Name}");
            }
        }
        catch
        {
            // 事件处理失败不影响读循环与主流程
        }
    }

    /// <summary>
    /// [切片4] ext 优先的 BGI IPC 发送（查询/轻操作迁移点共用）：ext 通道 Ready 且操作有 ext 映射时
    /// 走 BgiExternalClient 长连接；否则（老 BGI/未连接/无映射/通道瞬态失败）回退 v2 IpcClient 短连接。
    /// 返回 null = 两条路径都不可用（调用方按原有容错语义处理）。v2 旧路径代码保留不删（老 BGI 降级用）。
    /// </summary>
    private async Task<IpcResponse?> SendBgiIpcPreferredAsync(string v2OpCode, string? payloadJson, int connectTimeoutMs = 2000)
    {
        var ext = _externalClient;
        if (ext is { State: BgiExternalLinkState.Ready }
            && BgiExternalClient.TryMapToExtOperation(v2OpCode, out var extOp))
        {
            try
            {
                var extResp = await ext.SendCommandAsync(
                    extOp,
                    payloadJson is null ? null : JsonSerializer.Deserialize<JsonElement>(payloadJson),
                    TimeSpan.FromMilliseconds(Math.Max(connectTimeoutMs, 2000)));
                return new IpcResponse
                {
                    Success = extResp.Success,
                    Data = extResp.Data,
                    ErrorMessage = extResp.ErrorMessage,
                    ErrorCode = extResp.ErrorCode,
                };
            }
            catch
            {
                // ext 通道瞬态失败 → 落回 v2 短连接
            }
        }

        try
        {
            using var ipc = new IpcClient();
            await ipc.ConnectAsync(connectTimeoutMs);
            return await ipc.SendCommandAsync(new IpcRequest { OpCode = v2OpCode, Payload = payloadJson });
        }
        catch
        {
            return null;
        }
    }

    /// <summary>[切片4] ext 通道可信状态同步：SDK 的 ext.hello 已校验同会话（跨会话在 SDK 侧被拒），Ready 即可信。</summary>
    private void UpdateExtSessionTrust()
    {
        if (!IsIpcSessionUntrusted) return;
        IsIpcSessionUntrusted = false;
        AddLog("[IPC] 管道对端已确认为本会话的 BGI 实例（ext.hello 校验），任务状态恢复采信");
    }

    /// <summary>config.list 解析结果（v2 轮询与 ext 长连接两条路径共用，字段语义逐条对齐）。</summary>
    private sealed record ConfigListPollResult(
        List<string> ConfigGroups,
        List<string> OneClickConfigs,
        Dictionary<string, List<string>> ConfigGroupTasks,
        Dictionary<string, List<string>> OneClickTasks,
        Dictionary<string, List<object>> ConfigGroupTasksWithStatus,
        Dictionary<string, List<object>> OneClickTasksWithStatus,
        List<object> Hotkeys);

    private static ConfigListPollResult ParseConfigListData(JsonElement data)
    {
        var configGroups = new List<string>();
        var oneClickConfigs = new List<string>();
        var configGroupTasks = new Dictionary<string, List<string>>();
        var oneClickTasks = new Dictionary<string, List<string>>();
        var configGroupTasksWithStatus = new Dictionary<string, List<object>>();
        var oneClickTasksWithStatus = new Dictionary<string, List<object>>();
        var hotkeys = new List<object>();

        if (data.TryGetProperty("configGroups", out var groups))
            configGroups = JsonSerializer.Deserialize<List<string>>(groups.GetRawText()) ?? [];
        if (data.TryGetProperty("oneClickConfigs", out var oneClick))
            oneClickConfigs = JsonSerializer.Deserialize<List<string>>(oneClick.GetRawText()) ?? [];
        if (data.TryGetProperty("configGroupTasks", out var gTasks) && gTasks.ValueKind == JsonValueKind.Object)
            configGroupTasks = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(gTasks.GetRawText()) ?? [];
        if (data.TryGetProperty("oneClickTasks", out var oTasks) && oTasks.ValueKind == JsonValueKind.Object)
            oneClickTasks = JsonSerializer.Deserialize<Dictionary<string, List<string>>>(oTasks.GetRawText()) ?? [];
        if (data.TryGetProperty("configGroupTasksWithStatus", out var gTasksWs) && gTasksWs.ValueKind == JsonValueKind.Object)
            configGroupTasksWithStatus = JsonSerializer.Deserialize<Dictionary<string, List<object>>>(gTasksWs.GetRawText()) ?? [];
        if (data.TryGetProperty("oneClickTasksWithStatus", out var oTasksWs) && oTasksWs.ValueKind == JsonValueKind.Object)
            oneClickTasksWithStatus = JsonSerializer.Deserialize<Dictionary<string, List<object>>>(oTasksWs.GetRawText()) ?? [];
        if (data.TryGetProperty("hotkeys", out var hks) && hks.ValueKind == JsonValueKind.Array)
            hotkeys = JsonSerializer.Deserialize<List<object>>(hks.GetRawText()) ?? [];

        return new ConfigListPollResult(
            configGroups, oneClickConfigs, configGroupTasks, oneClickTasks,
            configGroupTasksWithStatus, oneClickTasksWithStatus, hotkeys);
    }

    /// <summary>task.status 解析结果（v2 轮询与 ext 事件驱动快照共用，字段语义逐条对齐）。</summary>
    private sealed record TaskStatusPollResult(
        bool BgiRunning,
        string? CurrentTaskName,
        string? CurrentTaskGroupName,
        string? CurrentRouteDisplay,
        string? CurrentScriptRouteName,
        bool AutoHoeingRunning,
        string? AutoHoeingProgress);

    private static TaskStatusPollResult ParseTaskStatusData(JsonElement sdata)
    {
        var bgiRunning = false;
        string? currentTaskName = null;
        string? currentTaskGroupName = null;
        string? currentRouteDisplay = null;
        string? currentScriptRouteName = null;
        var autoHoeingRunning = false;
        string? autoHoeingProgress = null;

        if (sdata.TryGetProperty("running", out var running))
            bgiRunning = running.GetBoolean();
        // 任务停止后（bgiRunning=false），taskName 可能仍有残留值，必须忽略避免状态停留
        if (bgiRunning && sdata.TryGetProperty("taskName", out var tn) && tn.ValueKind == JsonValueKind.String)
            currentTaskName = tn.GetString();
        if (sdata.TryGetProperty("autoHoeingRunning", out var hoeing))
            autoHoeingRunning = hoeing.GetBoolean();
        if (autoHoeingRunning && sdata.TryGetProperty("autoHoeingProgress", out var progress)
            && progress.ValueKind == JsonValueKind.String)
            autoHoeingProgress = progress.GetString();
        // 读取配置组名与线路展示文本（新增字段，旧 BGI 无此字段时保持 null）
        if (bgiRunning && sdata.TryGetProperty("groupName", out var gn) && gn.ValueKind == JsonValueKind.String)
            currentTaskGroupName = gn.GetString();
        if (bgiRunning && sdata.TryGetProperty("currentRouteDisplay", out var rd) && rd.ValueKind == JsonValueKind.String)
            currentRouteDisplay = rd.GetString();
        // 配置组内脚本任务（JS/地图追踪）当前线路名（旧 BGI 无此字段 → null，显示退回"配置组 · 任务名"）
        if (bgiRunning && sdata.TryGetProperty("currentScriptRouteName", out var srn) && srn.ValueKind == JsonValueKind.String)
            currentScriptRouteName = srn.GetString();

        return new TaskStatusPollResult(
            bgiRunning, currentTaskName, currentTaskGroupName,
            currentRouteDisplay, currentScriptRouteName, autoHoeingRunning, autoHoeingProgress);
    }

    private async Task ReportStatusAsync()

    {
        // 防重入：多源并发触发时只跑一轮（详见 _reportStatusRunning 注释）
        if (Interlocked.CompareExchange(ref _reportStatusRunning, 1, 0) != 0) return;
        try
        {
            await ReportStatusCoreAsync();
        }
        finally
        {
            _reportStatusRunning = 0;
        }
    }

    private async Task ReportStatusCoreAsync()
    {
        // 快照到局部变量：RefreshAsync 会把 _signalRClient 置 null 并 Dispose，
        // 裸用字段会在方法中途踩 NullReferenceException（"状态上报失败: Object reference not set..."）
        var signalRClient = _signalRClient;
        if (signalRClient == null) return;

        // [B157] 上线事件上报失败的待补报重试（每 10s 一跳）：旧实现 fire-and-forget 静默丢弃后
        // 客户端已自认"已上线"，不再重试——偶发断线即永久不上线。服务端按 gen 边沿去重，重试安全。
        if (_intentLifecycle.PendingOnlineReportGen > 0)
        {
            _ = ReReportOnlineIntentIfNeededAsync();
        }

        List<string> configGroups = [];
        List<string> oneClickConfigs = [];
        Dictionary<string, List<string>> configGroupTasks = [];
        Dictionary<string, List<string>> oneClickTasks = [];
        Dictionary<string, List<object>> configGroupTasksWithStatus = [];
        Dictionary<string, List<object>> oneClickTasksWithStatus = [];
        List<object> hotkeys = [];
        var autoHoeingRunning = false;
        var autoHoeingProgress = (string?)null;
        var currentTaskName = (string?)null;
        var currentTaskGroupName = (string?)null;
        var currentRouteDisplay = (string?)null;
        var currentScriptRouteName = (string?)null;

        var bgiRunning = false;
        // 本轮 IPC 会话校验结果：不可信（跨会话/无法确认）时不采信管道返回的任何任务状态
        var ipcSessionTrusted = true;

        // 遥控器模式：跳过 IPC 连接，直接上报 observer 状态
        if (_config?.ObserverMode == true)
        {
            // 跳过 IPC 连接，不上报配置组/任务状态
            IsIpcSessionUntrusted = false; // 遥控器模式不连本机管道，清除可能残留的跨会话警告
        }
        else
        {
            // 加载缓存，用于 IPC 失败时回退
            var cache = _cacheManager?.Load();
            bool hasCache = cache != null && (cache.ConfigGroups.Count > 0 || cache.OneClickConfigs.Count > 0);

            // [切片4] ext 通道活跃：task.status 不再周期轮询——由事件驱动 SDK 快照刷新维护
            // （订阅基线 / 事件触发 / revision 跳号自动校准），config.list 走 ext 长连接（不再每轮新建管道）；
            // 通道不可用（老 BGI/未连接/刚断线）→ 原 v2 轮询路径（兜底，逐字节保留）
            if (await TryEstablishExternalChannelAsync())
            {
                // SDK 的 ext.hello 已完成同会话校验（跨会话在 SDK 侧即被拒绝），通道 Ready 即可信
                ipcSessionTrusted = true;
                UpdateExtSessionTrust();

                IpcResponse? extConfigResponse = null;
                try
                {
                    if (BgiExternalClient.TryMapToExtOperation("config.list", out var extListOp))
                    {
                        var extResp = await _externalClient!.SendCommandAsync(extListOp);
                        extConfigResponse = new IpcResponse
                        {
                            Success = extResp.Success,
                            Data = extResp.Data,
                            ErrorMessage = extResp.ErrorMessage,
                            ErrorCode = extResp.ErrorCode,
                        };
                    }
                }
                catch (Exception ex)
                {
                    AddLog($"[ext] config.list 通道异常: {ex.Message}（本轮回退缓存）");
                }

                if (extConfigResponse is { Success: true, Data: { } extCfgData })
                {
                    var parsed = ParseConfigListData(JsonSerializer.Deserialize<JsonElement>(extCfgData));
                    configGroups = parsed.ConfigGroups;
                    oneClickConfigs = parsed.OneClickConfigs;
                    configGroupTasks = parsed.ConfigGroupTasks;
                    oneClickTasks = parsed.OneClickTasks;
                    configGroupTasksWithStatus = parsed.ConfigGroupTasksWithStatus;
                    oneClickTasksWithStatus = parsed.OneClickTasksWithStatus;
                    hotkeys = parsed.Hotkeys;

                    // 成功 → 更新缓存（无论数据是否为空，都是 BGI 当前真实状态）
                    _cacheManager?.Save(new MemberConfigCache
                    {
                        ConfigGroups = configGroups,
                        OneClickConfigs = oneClickConfigs,
                        ConfigGroupTasksWithStatus = configGroupTasksWithStatus,
                        OneClickTasksWithStatus = oneClickTasksWithStatus,
                        Hotkeys = hotkeys,
                        LastUpdated = DateTime.UtcNow
                    });
                }
                else
                {
                    AddLog($"ext config.list 失败: {extConfigResponse?.ErrorMessage ?? "无响应"}");
                    if (hasCache)
                    {
                        configGroups = cache!.ConfigGroups;
                        oneClickConfigs = cache!.OneClickConfigs;
                        configGroupTasksWithStatus = cache!.ConfigGroupTasksWithStatus;
                        oneClickTasksWithStatus = cache!.OneClickTasksWithStatus;
                        hotkeys = cache!.Hotkeys;
                    }
                }

                // 任务状态：事件驱动快照缓存（字段解析与 v2 task.status 轮询同款）
                if (_latestExtStatusJson is { } extStatusJson)
                {
                    try
                    {
                        var parsedStatus = ParseTaskStatusData(JsonSerializer.Deserialize<JsonElement>(extStatusJson));
                        bgiRunning = parsedStatus.BgiRunning;
                        currentTaskName = parsedStatus.CurrentTaskName;
                        currentTaskGroupName = parsedStatus.CurrentTaskGroupName;
                        currentRouteDisplay = parsedStatus.CurrentRouteDisplay;
                        currentScriptRouteName = parsedStatus.CurrentScriptRouteName;
                        autoHoeingRunning = parsedStatus.AutoHoeingRunning;
                        autoHoeingProgress = parsedStatus.AutoHoeingProgress;
                    }
                    catch
                    {
                        // 快照解析失败：本轮任务状态保持默认值，下一轮事件刷新会自愈
                    }
                }
            }
            else
            {
            try
            {
                using var ipcClient = new IpcClient();
                await ipcClient.ConnectAsync(2000);

                // 会话校验：多用户多开时命名管道按用户 SID 共享，可能被其他会话先启动的 Primary BGI 独占，
                // 此时管道返回的 config.list / task.status 都是"别人会话的 BGI"的数据，一律不采信
                ipcSessionTrusted = ipcClient.IsSessionTrusted;
                UpdateIpcSessionTrust(ipcClient);

                if (!ipcSessionTrusted)
                {
                    // 不可信 → 配置回退缓存（与 IPC 失败路径一致），任务状态保持默认值（bgiRunning=false 等）
                    if (hasCache)
                    {
                        configGroups = cache!.ConfigGroups;
                        oneClickConfigs = cache!.OneClickConfigs;
                        configGroupTasksWithStatus = cache!.ConfigGroupTasksWithStatus;
                        oneClickTasksWithStatus = cache!.OneClickTasksWithStatus;
                        hotkeys = cache!.Hotkeys;
                    }
                }
                else
                {
                var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "config.list" });
                if (response.Success && !string.IsNullOrEmpty(response.Data))
                {
                    var parsed = ParseConfigListData(JsonSerializer.Deserialize<JsonElement>(response.Data));
                    configGroups = parsed.ConfigGroups;
                    oneClickConfigs = parsed.OneClickConfigs;
                    configGroupTasks = parsed.ConfigGroupTasks;
                    oneClickTasks = parsed.OneClickTasks;
                    configGroupTasksWithStatus = parsed.ConfigGroupTasksWithStatus;
                    oneClickTasksWithStatus = parsed.OneClickTasksWithStatus;
                    hotkeys = parsed.Hotkeys;

                    // IPC 成功 → 更新缓存（无论数据是否为空，都是 BGI 当前真实状态）
                    _cacheManager?.Save(new MemberConfigCache
                    {
                        ConfigGroups = configGroups,
                        OneClickConfigs = oneClickConfigs,
                        ConfigGroupTasksWithStatus = configGroupTasksWithStatus,
                        OneClickTasksWithStatus = oneClickTasksWithStatus,
                        Hotkeys = hotkeys,
                        LastUpdated = DateTime.UtcNow
                    });
                }
                else
                {
                    AddLog($"IPC config.list 失败: {response.ErrorMessage ?? "无响应"}");
                    // IPC 失败（BGI 不可达）→ 回退到缓存
                    if (hasCache)
                    {
                        configGroups = cache!.ConfigGroups;
                        oneClickConfigs = cache!.OneClickConfigs;
                        configGroupTasksWithStatus = cache!.ConfigGroupTasksWithStatus;
                        oneClickTasksWithStatus = cache!.OneClickTasksWithStatus;
                        hotkeys = cache!.Hotkeys;
                    }
                }

                // 轮询 task.status 获取当前任务状态
                var statusResp = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.status" });
                if (statusResp.Success && !string.IsNullOrEmpty(statusResp.Data))
                {
                    var parsedStatus = ParseTaskStatusData(JsonSerializer.Deserialize<JsonElement>(statusResp.Data));
                    bgiRunning = parsedStatus.BgiRunning;
                    currentTaskName = parsedStatus.CurrentTaskName;
                    currentTaskGroupName = parsedStatus.CurrentTaskGroupName;
                    currentRouteDisplay = parsedStatus.CurrentRouteDisplay;
                    currentScriptRouteName = parsedStatus.CurrentScriptRouteName;
                    autoHoeingRunning = parsedStatus.AutoHoeingRunning;
                    autoHoeingProgress = parsedStatus.AutoHoeingProgress;
                }
                } // end else（IPC 会话可信）
            }
            catch (Exception ex)
            {
                // [IPC_PROBE] 探针：记录 IPC 失败时的管道名、当前会话、进程信息，用于诊断"全程不间断 IPC 不可用"的原因
                var pipeName = new IpcClient().GetPipeName();
                var sessionId = System.Diagnostics.Process.GetCurrentProcess().SessionId;
                var pid = System.Diagnostics.Process.GetCurrentProcess().Id;
                AddLog($"IPC 不可用: {ex.Message} [IPC_PROBE] pipe={pipeName} session={sessionId} pid={pid} type={ex.GetType().Name}");
                // IPC 异常 → 回退到缓存
                if (hasCache)
                {
                    configGroups = cache!.ConfigGroups;
                    oneClickConfigs = cache!.OneClickConfigs;
                    configGroupTasksWithStatus = cache!.ConfigGroupTasksWithStatus;
                    oneClickTasksWithStatus = cache!.OneClickTasksWithStatus;
                    hotkeys = cache!.Hotkeys;
                }
            }
            } // end else（v2 兜底轮询路径）
        }

        // 进度文本变化时写日志（仅自己看）
        if (!string.IsNullOrEmpty(autoHoeingProgress) && autoHoeingProgress != _lastLoggedProgress)
        {
            _lastLoggedProgress = autoHoeingProgress;
            AddLog(autoHoeingProgress!);
        }

        var status = new ControlStatus
        {
            PlayerUid = _config!.PlayerUid,
            PlayerName = _config.PlayerName,
            BgiStatus = _config?.ObserverMode == true ? "observer" : (_processMonitor?.IsBgiRunning == true ? "running" : "stopped"),
            ConfigGroups = configGroups,
            OneClickConfigs = oneClickConfigs,
            ConfigGroupTasks = configGroupTasks,
            OneClickTasks = oneClickTasks,
            ConfigGroupTasksWithStatus = configGroupTasksWithStatus,
            OneClickTasksWithStatus = oneClickTasksWithStatus,
            Hotkeys = hotkeys,
            TaskRunning = bgiRunning,
            CurrentTaskName = currentTaskName,
            CurrentTaskGroupName = currentTaskGroupName,
            CurrentRouteDisplay = currentRouteDisplay,
            CurrentScriptRouteName = currentScriptRouteName,
            AutoHoeingRunning = autoHoeingRunning,
            AutoHoeingProgress = autoHoeingProgress,
            OnlineReady = _intentLifecycle.IsOnlineReady,
            OnlineMode = _intentLifecycle.OnlineMode,
            ScheduledOnlineTime = _config?.ScheduledOnlineTime ?? "",
            OnlineHoeingGroupNames = _config?.OnlineHoeingGroupNames ?? [],
            QuickCommands = _config?.QuickCommands ?? new(),
            ExpectedHoeingPlayers = _config?.ExpectedHoeingPlayers ?? 4
        };

        // 检测"联机锄地上线"任务已执行（通过 onlineGeneration 代序号边沿检测 + recentTaskName 降级）
        // 优先读 onlineGeneration（新字段），比状态机内边沿基线大才触发（边沿检测）。
        // 如果 onlineGeneration 不存在，降级到 recentTaskName 电平检测（旧 BGI 兼容）。
        // 触发后上报服务端（ReportOnlineEvent），由服务端状态机做就绪判断，助手端不做本地状态决策。
        // [切片1] ext 事件通道可用时 online.triggered 由事件驱动（A5：秒级到达 vs 10s 轮询），跳过本段 v2 轮询；
        // 通道不可用（老 BGI/未连接）时 TryEstablishExternalChannelAsync 返回 false，走原有轮询路径，行为逐字节不变
        if (!await TryEstablishExternalChannelAsync())
        try
        {
            using var recentTaskClient = new IpcClient();
            await recentTaskClient.ConnectAsync(2000);
            // 跨会话/无法确认时跳过：不能把其他会话 BGI 的 onlineGeneration / recentTaskName 误报为"我上线了"
            if (recentTaskClient.IsSessionTrusted)
            {
            var statusResp = await recentTaskClient.SendCommandAsync(new IpcRequest { OpCode = "task.status" });
            if (statusResp.Success && !string.IsNullOrEmpty(statusResp.Data))
            {
                var sdata = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(statusResp.Data);
                    // 优先读 onlineGeneration（新字段，边沿检测）
                    if (sdata.TryGetProperty("onlineGeneration", out var ogEl) && ogEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        var gen = ogEl.GetInt32();
                        // [B157] 首见边沿新鲜度校验用：onlineTriggeredAt（UTC ISO）
                        DateTime? triggeredAt = null;
                        if (sdata.TryGetProperty("onlineTriggeredAt", out var taEl)
                            && taEl.ValueKind == System.Text.Json.JsonValueKind.String
                            && DateTime.TryParse(taEl.GetString(), out var taVal))
                        {
                            triggeredAt = taVal.Kind == DateTimeKind.Utc ? taVal : taVal.ToUniversalTime();
                        }
                        // [P1] 首见判定收编进状态机；边沿检测/基线同步/双来源对齐/防抖/上报全部收口到共享方法（与快照路径同款语义）
                        ApplyOnlineGenerationEdge(gen, triggeredAt);
                        // 同步本地已构造的 status 对象，防止后续 ReportControlStatusAsync 用旧值覆盖服务端
                        status.OnlineReady = _intentLifecycle.IsOnlineReady;
                        status.OnlineMode = _intentLifecycle.OnlineMode;
                    }
                    // 降级：读 recentTaskName（旧 BGI 兼容）
                    else if (sdata.TryGetProperty("recentTaskName", out var rtn) && rtn.ValueKind == System.Text.Json.JsonValueKind.String)
                    {
                        var recentTask = rtn.GetString() ?? "";
                        if (recentTask == "联机锄地上线" && !_intentLifecycle.IsOnlineReady)
                        {
                            _ = MarkOnlineAsync("command");
                        }
                    }
            }
            } // end if（IPC 会话可信才探测上线事件）
        }
        catch
        {
            // IPC 不可用时不影响
        }

        // 检测联机锄地是否结束（autoHoeingRunning 从 true 变为 false）
        // 通过 IPC 查询 BGI 是否有中断上下文，不使用 _config 引用
        // IPC 会话不可信时跳过：autoHoeingRunning 此时恒为默认值 false，
        // 若之前同会话锄地中突变为跨会话，边沿条件会误触发"锄地结束"并启动恢复定时器，必须压住
        if (!ipcSessionTrusted)
        {
            _wasAutoHoeingRunning = false;
        }
        else if (_wasAutoHoeingRunning && !autoHoeingRunning)
        {
            // 检查 BGI 是否有中断上下文（[切片4] ext 通道优先，v2 短连接兜底）
            bool hasContext = false;
            try
            {
                var ctxResp = await SendBgiIpcPreferredAsync("task.status", null);
                if (ctxResp is { Success: true } && !string.IsNullOrEmpty(ctxResp.Data))
                {
                    var ctxData = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(ctxResp.Data);
                    if (ctxData.TryGetProperty("hasSuspendedTaskContext", out var hsc))
                    {
                        hasContext = hsc.GetBoolean();
                    }
                }
            }
            catch
            {
                // IPC 不可用时跳过
            }

            if (hasContext)
            {
                // 联机锄地已结束，在助手房间内显示恢复提示（不弹窗）
                var policyAtEdge = SnapshotOnlineHoeingPolicy();
                AddLog($"联机锄地已结束，10 秒后按任务冲突策略（{policyAtEdge.PolicyDisplayName}）处置被中断的原任务...");
                // [P1b] 捕获边沿时刻的批次句柄：本定时器与批次末尾收尾可能同时命中同一轮，
                // 必须走同一"恰好一次"守卫（RunSpecified 策略双执行会重复启动指定任务）
                OnlineHoeingBatch? batchAtEdge;
                lock (_batchGate) { batchAtEdge = _activeBatch; }
                // 启动恢复定时器（10 秒后按策略收尾：恢复 / 清上下文停止 / 清上下文并启动指定任务）
                _resumeTimeoutTimer?.Dispose();
                _resumeTimeoutTimer = new System.Threading.Timer(async _ =>
                {
                    if (_commandExecutor != null)
                    {
                        // [任务冲突策略] 第二个恢复触发器（10s 轮询边沿检测）：与批次内收尾走同一策略闭环
                        if (batchAtEdge != null)
                        {
                            await ApplyPolicyTeardownOnceAsync(batchAtEdge, "联机锄地", userCancelled: false);
                        }
                        else
                        {
                            // 非 AllReady 批次的锄地（如手动启动）：无批次键可比对——若 30s 内刚实际执行过
                            // 策略收尾（批次末尾收尾与本边沿几乎同时的双执行场景），跳过迟到边沿；
                            // 超过 30s 判定为无关手动会话，保持原行为直接收尾
                            bool recentlyDone;
                            lock (_teardownGate) { recentlyDone = (DateTime.UtcNow - _lastTeardownUtc) < TimeSpan.FromSeconds(30); }
                            if (recentlyDone)
                            {
                                AddLog("刚完成策略收尾，跳过迟到边沿");
                            }
                            else
                            {
                                await _commandExecutor.ApplyPolicyTeardownAsync(SnapshotOnlineHoeingPolicy(), "联机锄地", userCancelled: false, AddLog);
                                lock (_teardownGate) { _lastTeardownUtc = DateTime.UtcNow; }
                            }
                        }
                    }
                }, null, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(-1));
            }
            _wasAutoHoeingRunning = autoHoeingRunning;
        }

        // 状态上报放 try-catch 内：连接断开时（如 ServerTimeout 后）InvokeAsync 会抛异常，
        // 若漏掉会作为未观察任务异常冒泡到全局 TaskScheduler.UnobservedTaskException → App 弹"未处理异常"框。
        // 这里捕获并仅记日志（断线状态已由 Closed 事件同步 IsConnected=false，右上角徽章变"离线"）。
        // 嘟嘟可卡死心跳检测用：缓存最近一次本地任务状态快照（10s 状态轮询产物，不新起 IPC）。
        LatestLocalStatus = status;
        try
        {
            await signalRClient.ReportControlStatusAsync(status);
        }
        catch (Exception ex)
        {
            AddLog($"状态上报失败（连接不可用）: {ex.Message}");
        }
    }

    /// <summary>标记已上线并上报服务端。返回 false 表示上线事件未送达服务端（无客户端/未连接），
    /// 定时器路径据此不标记当天已触发、下跳 30 秒自动重试（S2）；非定时路径调用方可忽略返回值。
    /// [P1] 连接就绪判定留在本方法（依赖 SignalRClient）；gen 自增+写盘+清零守卫+置 Armed 收口进状态机。</summary>
    private async Task<bool> MarkOnlineAsync(string mode)
    {
        // 上报上线事件，驱动服务端 AllReady（全员就绪）检查
        if (_signalRClient == null)
        {
            return false;
        }
        // S2：上报前检查连接状态——未连接时 ReportOnlineEventAsync 会静默丢弃事件，
        // 这里不增 generation、不置 Armed，返回 false 让定时器路径下跳 30 秒自动重试。
        // （旧实现先置 _isOnlineReady=true 再判连接，未连接时残留"本地已上线但服务端不知道"
        // 的幻影状态，recentTaskName 降级路径因此被 !IsOnlineReady 挡住永不重试——事故根因之一。）
        if (!_signalRClient.IsConnected)
        {
            AddLog(mode == "scheduled"
                ? "已到定时上线时间，但服务器未连接，将在 30 秒后重试"
                : "上线事件未上报：服务器未连接");
            return false;
        }

        // [P1] 状态跃迁（置 Armed）、gen 自增+写盘、清零 AllReady 执行守卫，全部在状态机内完成
        var gen = _intentLifecycle.RaiseLocalIntent(mode);
        AddLog($"已上线（{mode}）");
        // 立即上报服务端，让卡片实时更新（不等下一次 10 秒轮询）
        try
        {
            await ReportStatusAsync();
        }
        catch
        {
            // 上报失败不影响上线状态标记
        }

        try
        {
            // [B157] 走跟踪上报：送达记入防抖基准/重连补报基准；未送达挂起待补报（10s 轮询重试）
            if (await ReportOnlineEventTrackedAsync(gen))
            {
                AddLog($"已上报上线事件 generation={gen}，等待服务端全员就绪开锄");
            }
        }
        catch (Exception ex)
        {
            AddLog($"上报上线事件失败: {ex.Message}");
        }
        return true;
    }

    // [P1] 本地 generation 持久化（LocalGenerationFilePath/LoadPersistedLocalGeneration/PersistLocalGeneration）
    // 已随状态机搬入 OnlineIntentLifecycle。

    /// <summary>[实机修复 2026-09-05] 定时上线"已触发日期"持久化文件（与 assistant-config.json 同目录）。</summary>
    private static string ScheduledFireDateFilePath => Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "NexusBGI", "assistant-online-fired-date.txt");

    /// <summary>启动时读取已触发日期。文件不存在/解析失败 = 未触发过。</summary>
    private DateTime LoadPersistedScheduledFireDate()
    {
        try
        {
            var path = ScheduledFireDateFilePath;
            if (File.Exists(path)
                && DateTime.TryParse(File.ReadAllText(path).Trim(), out var saved))
            {
                return saved.Date;
            }
        }
        catch (Exception ex)
        {
            AddLog($"[定时上线] 读取 assistant-online-fired-date.txt 失败，按未触发处理: {ex.Message}");
        }
        return DateTime.MinValue;
    }

    /// <summary>触发/过期跳过后写盘。失败仅记日志。</summary>
    private void PersistScheduledFireDate(DateTime date)
    {
        try
        {
            File.WriteAllText(ScheduledFireDateFilePath, date.ToString("yyyy-MM-dd"));
        }
        catch (Exception ex)
        {
            AddLog($"[定时上线] 写入 assistant-online-fired-date.txt 失败: {ex.Message}");
        }
    }

    /// <summary>回滚（上报失败需重试）或重新设定时间时清除持久化标记。</summary>
    private void ClearPersistedScheduledFireDate()
    {
        try
        {
            if (File.Exists(ScheduledFireDateFilePath))
            {
                File.Delete(ScheduledFireDateFilePath);
            }
        }
        catch
        {
            // 清理失败仅影响极端边界（重启后少触发一次），可忽略
        }
    }

    /// <summary>启动定时上线定时器（每 30 秒检查一次）。
    /// 不依赖上线状态（避免状态残留阻塞），改用按天去重防止重复触发。</summary>
    private void StartOnlineScheduler()
    {
        _onlineTimer?.Dispose();
        _onlineTimer = new Timer(async _ =>
        {
            // S4：async void 回调整体兜底——未处理异常会杀进程，这里捕获后仅记日志。
            try
            {
                if (_config == null) return;
                // 监控模式：本机不占成员名额也不参与上线（服务端成员表无本端条目，上报是 no-op），
                // 跳过避免本地误打"已上线(scheduled)"日志误导；给执行端设定时上线走弹窗远程下发，不经本定时器
                if (_config.ObserverMode) return;
                if (string.IsNullOrEmpty(_config.ScheduledOnlineTime)) return;

                // 用户手动清除上线后，抑制定时自动上线（除非重新设定定时上线清除标志）。
                // 静默 return：不每 30 秒刷日志（S1）。
                if (_intentLifecycle.ManuallyClearedOnline) return;

                var now = DateTime.Now;
                if (!TimeSpan.TryParse(_config.ScheduledOnlineTime, out var targetTime)) return;

                var target = now.Date.Add(targetTime);
                if (now < target) return;                              // 还没到点
                if (_lastScheduledFireDate == now.Date) return;        // 今天已触发过（或已判定过期跳过）

                // [实机修复 2026-09-05] 补触发宽限窗：到点后超过 15 分钟才看到 = 当时人不在/没开机，
                // "补上线"非用户意图（实机病例：设 1:00，1:30 打开助手被直接上线）。过期即消费今日额度。
                if (now - target > ScheduledFireGrace)
                {
                    _lastScheduledFireDate = now.Date;
                    PersistScheduledFireDate(now.Date);
                    AddLog($"已过定时上线时间 {_config.ScheduledOnlineTime} 超过 {(int)ScheduledFireGrace.TotalMinutes} 分钟，今日自动上线已跳过（需要上线请手动操作）");
                    return;
                }

                // S4：先置触发标记防 30 秒重入双触发；S2：上报失败（断线）时回滚，下跳 30 秒自动重试（仍在宽限窗内才重试）。
                _lastScheduledFireDate = now.Date;
                PersistScheduledFireDate(now.Date);
                if (!await MarkOnlineAsync("scheduled"))
                {
                    _lastScheduledFireDate = DateTime.MinValue;
                    ClearPersistedScheduledFireDate();
                }
            }
            catch (Exception ex)
            {
                AddLog($"定时上线检查异常: {ex.Message}");
            }
        }, null, TimeSpan.Zero, TimeSpan.FromSeconds(30));
    }

    private async Task SendAckAsync(RemoteCommand originalCmd, string status, string message)
    {
        if (_signalRClient == null) return;
        var ack = new RemoteCommand
        {
            Cmd = "ack",
            Sender = _config?.PlayerName ?? "",
            SenderUid = _config?.PlayerUid ?? "",
            Target = [originalCmd.SenderUid],
            CommandId = originalCmd.CommandId,
            Params = new Dictionary<string, object> { { "status", status }, { "message", message } }
        };
        await _signalRClient.SendRemoteCommandAsync(ack);
    }

    // ===== 远程配置组编辑（契约见 Docs/远程配置组编辑-实施方案.md §1/§2/§5）=====

    /// <summary>
    /// 远程编辑成员配置组：弹配置组选择窗 → 交给 RemoteConfigEditService 走完整流程。
    /// 在 UI 线程执行（RelayCommand 回调）。
    /// </summary>
    private void OnRemoteConfigEdit(object? parameter)
    {
        if (parameter is not MemberViewModel member) return;
        if (string.IsNullOrEmpty(member.PlayerUid)) return;
        if (member.PlayerUid == _config?.PlayerUid)
        {
            AddLog("不能远程编辑自己的配置组（请在本机直接修改）");
            return;
        }
        if (!member.Online)
        {
            AddLog($"成员 {member.PlayerName} 不在线，无法远程编辑其配置组");
            return;
        }
        if (_signalRClient == null || !_signalRClient.IsConnected)
        {
            AddLog("SignalR 未连接，无法发起远程编辑");
            return;
        }
        var groups = member.ConfigGroups ?? [];
        if (groups.Count == 0)
        {
            AddLog($"成员 {member.PlayerName} 没有可用的配置组（可能状态尚未同步）");
            return;
        }

        var groupName = RemoteConfigGroupSelectWindow.ShowSelectDialog(groups, member.PlayerName, Application.Current.MainWindow);
        if (string.IsNullOrEmpty(groupName)) return; // 用户取消

        _remoteConfigEditService ??= new RemoteConfigEditService(
            sendAsync: async rc =>
            {
                var client = _signalRClient;
                if (client == null || !client.IsConnected) return false;
                await client.SendRemoteCommandAsync(rc);
                return true;
            },
            getSelfUid: () => _config?.PlayerUid ?? "",
            getSelfName: () => _config?.PlayerName ?? "",
            report: AddLog,
            // [切片4] 本机 IPC（open_remote_editor/remote_editor_result）ext 通道优先，v2 兜底
            getExternalClient: () => _externalClient,
            // BGI 未运行时自动拉起本机 BGI 并等待 IPC 就绪（监控/执行模式通用）
            ensureBgiReadyAsync: EnsureLocalBgiReadyAsync);
        _ = _remoteConfigEditService.RunAsync(member.PlayerUid, member.PlayerName, groupName);
    }

    /// <summary>
    /// 确保本机 BGI 可用（远程编辑开窗前置）：BGI 未运行时按配置路径自动拉起，
    /// 并轮询等待 IPC 管道就绪（最长 90 秒，BGI 冷启动可能较慢）。返回 true = IPC 已可连接。
    /// 监控模式下无 _processMonitor/_commandExecutor，直接按配置的 BgiPath 启动，不依赖模式。
    /// </summary>
    private async Task<bool> EnsureLocalBgiReadyAsync()
    {
        try
        {
            if (BgiProcessMonitor.GetCurrentSessionBgiProcesses().Length == 0)
            {
                var bgiPath = _config?.BgiPath;
                if (string.IsNullOrEmpty(bgiPath) || !File.Exists(bgiPath))
                {
                    AddLog("无法自动启动 BGI：未配置 BGI 路径或文件不存在（请先在设置中配置 BGI 路径）");
                    return false;
                }
                if (_processMonitor != null)
                {
                    // 执行模式：走标准启动路径（含启动探针日志与 OnBgiStarted 事件）
                    _processMonitor.RestartBgi();
                }
                else
                {
                    System.Diagnostics.Process.Start(new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = bgiPath,
                        UseShellExecute = true
                    });
                }
                AddLog("已自动启动 BGI，等待其就绪...");
            }

            // 轮询等待 IPC 就绪（进程可能刚拉起，管道尚未监听）
            var deadline = DateTime.UtcNow.AddSeconds(90);
            while (DateTime.UtcNow < deadline)
            {
                try
                {
                    using var ipc = new IpcClient();
                    await ipc.ConnectAsync(3000);
                    return true;
                }
                catch
                {
                    await Task.Delay(2000);
                }
            }
            AddLog("等待本机 BGI IPC 就绪超时（90 秒）");
            return false;
        }
        catch (Exception ex)
        {
            AddLog($"自动启动 BGI 失败: {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// 处理 remote_config.pull：对方请求拉取本机某个配置组。
    /// IPC config.pull_group → 回 remote_config.data（Target=[对方 UID]，CommandId 原样，Params 全 string）。
    /// </summary>
    private async Task HandleRemoteConfigPullAsync(RemoteCommand cmd)
    {
        var groupName = GetRemoteParam(cmd.Params, "groupName") ?? "";
        var ok = "false";
        string? error = null;
        string? packageJson = null;

        try
        {
            // [切片4] ext.config.pullGroup 优先（长连接），v2 config.pull_group 短连接兜底
            var resp = await SendBgiIpcPreferredAsync("config.pull_group", JsonSerializer.Serialize(new { groupName }), 3000);
            if (resp is { Success: true } && !string.IsNullOrEmpty(resp.Data))
            {
                using var doc = JsonDocument.Parse(resp.Data);
                if (doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True
                    && doc.RootElement.TryGetProperty("package", out var pkgEl) && pkgEl.ValueKind == JsonValueKind.Object)
                {
                    ok = "true";
                    packageJson = pkgEl.GetRawText();
                }
                else if (doc.RootElement.TryGetProperty("error", out var errEl) && errEl.ValueKind == JsonValueKind.String)
                {
                    error = errEl.GetString() ?? "未知错误";
                }
                else
                {
                    error = "BGI 返回数据格式不完整";
                }
            }
            else
            {
                error = resp?.ErrorMessage ?? "BGI 未返回数据";
            }
        }
        catch (Exception ex)
        {
            error = $"本机 BGI IPC 不可用: {ex.Message}";
        }

        AddLog(ok == "true"
            ? $"已将配置组「{groupName}」的配置发送给 {cmd.Sender}"
            : $"远程拉取配置组「{groupName}」失败（来自 {cmd.Sender}）: {error}");

        if (_signalRClient == null) return;
        var replyParams = ok == "true"
            ? new Dictionary<string, object> { ["ok"] = "true", ["packageJson"] = packageJson! }
            : new Dictionary<string, object> { ["ok"] = "false", ["error"] = error ?? "未知错误" };
        var reply = new RemoteCommand
        {
            Cmd = "remote_config.data",
            Sender = _config?.PlayerName ?? "",
            SenderUid = _config?.PlayerUid ?? "",
            Target = [cmd.SenderUid],
            CommandId = cmd.CommandId,
            Params = replyParams
        };
        await _signalRClient.SendRemoteCommandAsync(reply);
    }

    /// <summary>
    /// 处理 remote_config.push：对方回传编辑后的配置。
    /// IPC config.apply_group → 回 remote_config.push_result（ok/message 全 string）。
    /// </summary>
    private async Task HandleRemoteConfigPushAsync(RemoteCommand cmd)
    {
        var groupName = GetRemoteParam(cmd.Params, "groupName") ?? "";

        // 组装 IPC payload：可选字段仅在有值时携带
        var payloadDict = new Dictionary<string, string>
        {
            ["groupName"] = groupName,
            ["baseMd5"] = GetRemoteParam(cmd.Params, "baseMd5") ?? ""
        };
        foreach (var key in new[] { "scriptGroupConfigJson", "soloTaskName", "soloTaskSettingsJson" })
        {
            var v = GetRemoteParam(cmd.Params, key);
            if (!string.IsNullOrEmpty(v)) payloadDict[key] = v;
        }

        var ok = "false";
        string message;
        try
        {
            // [切片4] ext.config.applyGroup 优先（长连接），v2 config.apply_group 短连接兜底
            var resp = await SendBgiIpcPreferredAsync("config.apply_group", JsonSerializer.Serialize(payloadDict), 3000);
            if (resp is { Success: true } && !string.IsNullOrEmpty(resp.Data))
            {
                using var doc = JsonDocument.Parse(resp.Data);
                ok = doc.RootElement.TryGetProperty("ok", out var okEl) && okEl.ValueKind == JsonValueKind.True
                    ? "true" : "false";
                message = doc.RootElement.TryGetProperty("message", out var msgEl) && msgEl.ValueKind == JsonValueKind.String
                    ? msgEl.GetString() ?? "" : "";
            }
            else
            {
                message = resp?.ErrorMessage ?? "BGI 未返回结果";
            }
        }
        catch (Exception ex)
        {
            message = $"本机 BGI IPC 不可用: {ex.Message}";
        }

        AddLog(ok == "true"
            ? $"收到 {cmd.Sender} 远程修改的配置组「{groupName}」，已应用。{message}"
            : $"收到 {cmd.Sender} 远程修改的配置组「{groupName}」，应用失败：{message}");

        if (_signalRClient == null) return;
        var reply = new RemoteCommand
        {
            Cmd = "remote_config.push_result",
            Sender = _config?.PlayerName ?? "",
            SenderUid = _config?.PlayerUid ?? "",
            Target = [cmd.SenderUid],
            CommandId = cmd.CommandId,
            Params = new Dictionary<string, object> { ["ok"] = ok, ["message"] = message }
        };
        await _signalRClient.SendRemoteCommandAsync(reply);
    }

    /// <summary>
    /// 从 RemoteCommand.Params 安全取出字符串值。
    /// SignalR 反序列化后 value 可能是 string 或 JsonElement，需分别处理（同 CommandExecutor.GetStringParam）。
    /// </summary>
    private static string? GetRemoteParam(Dictionary<string, object>? dict, string key)
    {
        if (dict == null || !dict.TryGetValue(key, out var val) || val == null) return null;
        if (val is string s) return s;
        if (val is JsonElement je)
        {
            return je.ValueKind == JsonValueKind.String ? je.GetString() : je.ToString();
        }
        return val.ToString();
    }

    /// <summary>
    /// 处理 task_policy.pull：对方请求拉取本机上线锄地「完成后动作」配置。
    /// 回 task_policy.data（Params 全 string）：policy/specifiedType/specifiedName。
    /// </summary>
    private async Task HandleTaskPolicyPullAsync(RemoteCommand cmd)
    {
        if (_signalRClient == null) return;
        var replyParams = new Dictionary<string, object>
        {
            ["ok"] = "true",
            ["policy"] = _config?.OnlineHoeingCompletionPolicy ?? "resume",
            ["specifiedType"] = _config?.OnlineHoeingSpecifiedTaskType ?? "group",
            ["specifiedName"] = _config?.OnlineHoeingSpecifiedTaskName ?? ""
        };
        var reply = new RemoteCommand
        {
            Cmd = "task_policy.data",
            Sender = _config?.PlayerName ?? "",
            SenderUid = _config?.PlayerUid ?? "",
            Target = [cmd.SenderUid],
            CommandId = cmd.CommandId,
            Params = replyParams
        };
        await _signalRClient.SendRemoteCommandAsync(reply);
    }

    /// <summary>
    /// 处理 task_policy.push：对方回传编辑后的上线锄地「完成后动作」配置。
    /// policy/specifiedType/specifiedName 存在时落 AssistConfig 并保存（持久化，重启保留）。
    /// 回 task_policy.push_result（ok/message 全 string）。
    /// </summary>
    private async Task HandleTaskPolicyPushAsync(RemoteCommand cmd)
    {
        var applied = new List<string>();

        // 上线锄地「完成后动作」（持久化）
        var policy = GetRemoteParam(cmd.Params, "policy");
        if (policy is "resume" or "stop" or "runSpecified" && _config != null)
        {
            _config.OnlineHoeingCompletionPolicy = policy;
            var specifiedType = GetRemoteParam(cmd.Params, "specifiedType");
            if (specifiedType is "group" or "onedragon")
                _config.OnlineHoeingSpecifiedTaskType = specifiedType;
            var specifiedName = GetRemoteParam(cmd.Params, "specifiedName");
            if (specifiedName != null)
                _config.OnlineHoeingSpecifiedTaskName = specifiedName;
            _configManager?.Save(_config);
            applied.Add("上线锄地完成后动作（已持久化）");
        }

        var ok = applied.Count > 0;
        var message = ok ? $"已应用: {string.Join("、", applied)}" : "无可应用字段（参数为空或非法）";
        AddLog($"收到 {cmd.Sender} 远程修改任务策略：{message}");

        if (_signalRClient == null) return;
        var reply = new RemoteCommand
        {
            Cmd = "task_policy.push_result",
            Sender = _config?.PlayerName ?? "",
            SenderUid = _config?.PlayerUid ?? "",
            Target = [cmd.SenderUid],
            CommandId = cmd.CommandId,
            Params = new Dictionary<string, object> { ["ok"] = ok ? "true" : "false", ["message"] = message }
        };
        await _signalRClient.SendRemoteCommandAsync(reply);
    }

    private void OnStop(object? parameter)
    {
        // 停止前弹窗确认，可选择是否发送给所有人执行（参考"清除上线"，见 ShowClearOnlineConfirmDialog）
        var (confirmed, broadcastAll) = ShowStopStartConfirmDialog("确认停止 BGI", "确定要停止 BGI 吗？");
        if (!confirmed) return; // 用户取消

        // [P1b] 用户手动停止 = 取消当前 AllReady 批次（原共享 bool _isAllReadySequenceCancelled 已并入批次 CTS），
        // 后台依次执行序列在下一次迭代检查点退出，剩余配置组不再执行
        lock (_batchGate) { _activeBatch?.Cancel(); }

        if (broadcastAll)
        {
            // 缓存问题：不在线成员收不到广播命令，弹窗提醒（缓存数据可能过时）
            if (!WarnOfflineMembers())
            {
                AddLog("没有在线成员，未发送停止命令");
                return;
            }
            // 发送给所有人执行：广播 Target=[*]（含本机，各自通过 OnRemoteCommand 执行）
            if (_signalRClient != null)
            {
                var cmd = new RemoteCommand
                {
                    Cmd = "stop",
                    Sender = _config?.PlayerName ?? "",
                    SenderUid = _config?.PlayerUid ?? "",
                    Target = ["*"],
                    CommandId = "remote_" + DateTime.Now.Ticks
                };
                AddLog("已发送停止 BGI 命令给所有成员");
                _ = _signalRClient.SendRemoteCommandAsync(cmd);
            }
            else
            {
                AddLog("SignalR 未连接，无法发送停止命令给所有成员");
            }
            return;
        }

        if (parameter is MemberViewModel member)
        {
            _ = ExecuteLocalCommandAsync("stop", null, [member.PlayerUid]);
        }
        else
        {
            _ = ExecuteLocalCommandAsync("stop", null, null);
        }
    }

    /// <summary>停止/启动 BGI 确认弹窗（深色鎏金主题，样式参考 ShowClearOnlineConfirmDialog）。
    /// 返回值：confirmed=是否确认，broadcastAll=是否发送给所有人执行。</summary>
    private (bool confirmed, bool broadcastAll) ShowStopStartConfirmDialog(string title, string message)
    {
        bool confirmed = false, broadcastAll = false;
        var window = new Window
        {
            Title = title,
            Width = 330, Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new Grid { Margin = new Thickness(20) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0 标题
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); // 1 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2 提示文字
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) }); // 3 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4 同步勾选框
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 5 弹性
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 6 按钮

        var titleLabel = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(titleLabel, 0);
        panel.Children.Add(titleLabel);

        var tip = new TextBlock
        {
            Text = message,
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(tip, 2);
        panel.Children.Add(tip);

        var syncCheckBox = new System.Windows.Controls.CheckBox
        {
            Content = "发送给所有人执行",
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsChecked = false
        };
        syncCheckBox.Checked += (_, _) => broadcastAll = true;
        syncCheckBox.Unchecked += (_, _) => broadcastAll = false;
        Grid.SetRow(syncCheckBox, 4);
        panel.Children.Add(syncCheckBox);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnPanel.Children.Add(new Button
        {
            Content = "取消",
            Width = 80, Height = 30,
            Margin = new Thickness(0, 0, 10, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Cursor = System.Windows.Input.Cursors.Hand
        });
        ((Button)btnPanel.Children[^1]).Click += (_, _) => window.Close();

        var okBtn = new Button
        {
            Content = "确定",
            Width = 80, Height = 30,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        okBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        okBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        okBtn.Click += (_, _) =>
        {
            confirmed = true;
            window.Close();
        };
        btnPanel.Children.Add(okBtn);

        Grid.SetRow(btnPanel, 6);
        panel.Children.Add(btnPanel);

        window.Content = panel;
        window.ShowDialog();
        return (confirmed, broadcastAll);
    }

    /// <summary>统计不在线成员并弹窗提醒（缓存问题：离线成员收不到命令，可能使用过时数据）。
    /// 返回是否有在线成员可下发。</summary>
    private bool WarnOfflineMembers()
    {
        var offline = Members.Where(m => !m.Online).Select(m => m.PlayerName).ToList();
        if (offline.Count > 0)
        {
            var names = string.Join("、", offline);
            MessageBox.Show($"有 {offline.Count} 个成员不在线，命令可能因使用缓存数据而无法生效：\n{names}\n（不在线成员收不到命令）",
                "离线成员提醒", MessageBoxButton.OK, MessageBoxImage.Warning);
        }
        return Members.Any(m => m.Online);
    }

    private void OnStartBgi(object? parameter)
    {
        // 启动前弹窗确认，可选择是否发送给所有人执行（参考"清除上线"，见 ShowClearOnlineConfirmDialog）
        var (confirmed, broadcastAll) = ShowStopStartConfirmDialog("确认启动 BGI", "确定要启动 BGI 吗？");
        if (!confirmed) return; // 用户取消

        if (broadcastAll)
        {
            // 缓存问题：不在线成员收不到广播命令，弹窗提醒（缓存数据可能过时）
            if (!WarnOfflineMembers())
            {
                AddLog("没有在线成员，未发送启动命令");
                return;
            }
            // 发送给所有人执行：广播 Target=[*]（含本机，各自通过 OnRemoteCommand 执行）
            if (_signalRClient != null)
            {
                var cmd = new RemoteCommand
                {
                    Cmd = "start_bgi",
                    Sender = _config?.PlayerName ?? "",
                    SenderUid = _config?.PlayerUid ?? "",
                    Target = ["*"],
                    CommandId = "remote_" + DateTime.Now.Ticks
                };
                AddLog("已发送启动 BGI 命令给所有成员");
                _ = _signalRClient.SendRemoteCommandAsync(cmd);
            }
            else
            {
                AddLog("SignalR 未连接，无法发送启动 BGI 命令给所有成员");
            }
            return;
        }

        // 点别人：远程下发 start_bgi
        if (parameter is MemberViewModel member && member.PlayerUid != _config?.PlayerUid)
        {
            if (_signalRClient != null)
            {
                var cmd = new RemoteCommand
                {
                    Cmd = "start_bgi",
                    Sender = _config?.PlayerName ?? "",
                    SenderUid = _config?.PlayerUid ?? "",
                    Target = [member.PlayerUid],
                    CommandId = "remote_" + DateTime.Now.Ticks
                };
                AddLog($"向 {member.PlayerName} 下发启动 BGI");
                _ = _signalRClient.SendRemoteCommandAsync(cmd);
            }
            else
            {
                AddLog("SignalR 未连接，无法向下发启动 BGI 命令");
            }
            return;
        }

        // 点自己卡片（或未指定）：
        if (_config?.ObserverMode == true)
        {
            // 监控模式：无本地 BGI，通过 SignalR 只发给与自己同 UID 的执行端
            if (_signalRClient != null)
            {
                var cmd = new RemoteCommand
                {
                    Cmd = "start_bgi",
                    Sender = _config.PlayerName ?? "",
                    SenderUid = _config.PlayerUid,
                    Target = [_config.PlayerUid],
                    CommandId = "remote_" + DateTime.Now.Ticks
                };
                AddLog("监控模式: 向执行端下发启动 BGI");
                _ = _signalRClient.SendRemoteCommandAsync(cmd);
            }
            else
            {
                AddLog("SignalR 未连接，无法下发启动 BGI 命令");
            }
        }
        else
        {
            // 执行模式：本地启动本机 BGI
            _ = ExecuteLocalCommandAsync("start_bgi", null, null);
        }
    }

    private void OnExecuteHotkey(object? parameter)
    {
        if (parameter is MemberViewModel member && member.Hotkeys.Count > 0)
        {
            var selected = ShowHotkeySelectDialog(member.Hotkeys);
            if (!string.IsNullOrEmpty(selected))
            {
                _ = ExecuteLocalCommandAsync("hotkey_execute",
                    new Dictionary<string, object> { { "hotkeyConfigName", selected } },
                    [member.PlayerUid]);
            }
        }
        else
        {
            AddLog("该成员没有可用的快捷键");
        }
    }

    private void OnCloseGame(object? parameter)
    {
        if (parameter is MemberViewModel member)
        {
            var result = MessageBox.Show("确定要关闭该成员的游戏吗？", "关闭游戏", MessageBoxButton.YesNo, MessageBoxImage.Question);
            if (result == MessageBoxResult.Yes)
            {
                _ = ExecuteLocalCommandAsync("close_game", null, [member.PlayerUid]);
            }
        }
    }

    /// <summary>显示该成员当天的上线消费记录（各自卡片只显示自己的）。</summary>
    private void OnShowOnlineHistory(object? parameter)
    {
        if (parameter is MemberViewModel member)
        {
            if (member.OnlineHistory == null || member.OnlineHistory.Count == 0)
            {
                ShowOnlineHistoryDialog("上线记录", "暂无上线记录", member);
                return;
            }

            // 格式化记录：上线方式 + 上线时间 + 消费时间
            var lines = member.OnlineHistory
                .OfType<System.Text.Json.JsonElement>()
                .Select(h =>
                {
                    var mode = h.TryGetProperty("mode", out var m) ? m.GetString() ?? "unknown" : "unknown";
                    var online = h.TryGetProperty("onlineTime", out var ot) ? ot.GetString() ?? "" : "";
                    var consume = h.TryGetProperty("consumeTime", out var ct) ? ct.GetString() ?? "" : "";
                    var modeText = mode == "scheduled" ? "定时" : mode == "command" ? "命令" : mode;
                    return $"· {modeText} 上线 {online} → 联机 {consume}";
                }).ToList();

            var content = lines.Count > 0 ? string.Join("\n", lines) : "暂无上线记录";
            // 若记录是匿名对象（非 JsonElement），兜底解析
            if (content == "暂无上线记录" && member.OnlineHistory.Count > 0)
            {
                var alt = member.OnlineHistory.Select(h => h.ToString()).ToList();
                content = string.Join("\n", alt);
            }
            ShowOnlineHistoryDialog("当天上线记录", content, member);
        }
    }

    /// <summary>打开定时上线设置弹窗（设定 HH:mm，保存到本地配置并重启定时器、立即上报）。</summary>
    private void OnScheduledOnline(object? parameter)
    {
        if (_config == null) return;
        var targetMember = parameter as MemberViewModel;
        var isSelf = targetMember == null || targetMember.PlayerUid == _config.PlayerUid;

        // 弹窗选时间（self 和 remote 共享同一个弹窗）
        var (time, syncToAll) = ShowScheduledOnlineTimeDialog(isSelf ? _config.ScheduledOnlineTime : "");
        if (time == null) return; // 用户取消

        if (syncToAll)
        {
            // 同步给所有成员：先更新本地 config 与卡片，再发给房间内所有在线成员（含遥控器模式的执行端）
            ApplyScheduledOnlineTime(time);
            if (_signalRClient != null)
            {
                var cmd = new RemoteCommand
                {
                    Cmd = "set_scheduled_online_time",
                    Sender = _config.PlayerName ?? "",
                    SenderUid = _config.PlayerUid,
                    Target = ["*"],
                    CommandId = "local_" + DateTime.Now.Ticks,
                    Params = new Dictionary<string, object> { { "scheduledOnlineTime", time } }
                };
                AddLog(string.IsNullOrEmpty(time)
                    ? "已清除所有成员的定时上线（同步给所有成员）"
                    : $"已将定时上线时间 {time} 同步给所有成员");
                _ = _signalRClient.SendRemoteCommandAsync(cmd);
            }
        }
        else if (isSelf)
        {
            if (_config?.ObserverMode == true)
            {
                // 遥控器模式：先更新本地 config 与卡片，再发给执行端；
                // 否则本地 _config.ScheduledOnlineTime 保持旧值，OnPlayersUpdated 会用旧值覆盖广播的新时间
                ApplyScheduledOnlineTime(time);
                if (_signalRClient != null)
                {
                    var cmd = new RemoteCommand
                    {
                        Cmd = "set_scheduled_online_time",
                        Sender = _config.PlayerName ?? "",
                        SenderUid = _config.PlayerUid,
                        Target = [_config.PlayerUid],
                        CommandId = "local_" + DateTime.Now.Ticks,
                        Params = new Dictionary<string, object> { { "scheduledOnlineTime", time } }
                    };
                    AddLog($"遥控器模式: 向执行端下发定时上线时间: {time}");
                    _ = _signalRClient.SendRemoteCommandAsync(cmd);
                }
            }
            else
            {
                ApplyScheduledOnlineTime(time);
            }
        }
        else if (_signalRClient != null)
        {
            var cmd = new RemoteCommand
            {
                Cmd = "set_scheduled_online_time",
                Sender = _config.PlayerName ?? "",
                SenderUid = _config.PlayerUid,
                Target = [targetMember!.PlayerUid],
                CommandId = "local_" + DateTime.Now.Ticks,
                Params = new Dictionary<string, object> { { "scheduledOnlineTime", time } }
            };
            AddLog($"向 {targetMember.PlayerName} 下发定时上线时间: {time}");
            _ = _signalRClient.SendRemoteCommandAsync(cmd);
        }
    }

    /// <summary>清除已上线状态（不清除定时闹钟）。点自己卡清自己；点别人卡远程下发清除。
    /// 弹窗确认：可勾选"同时清除所有成员已上线状态"（仅清 OnlineReady，保留上线记录 OnlineHistory）。</summary>
    private async void OnClearOnline(object? parameter)
    {
        if (_config == null) return;
        var targetMember = parameter as MemberViewModel;
        var isSelf = targetMember == null || targetMember.PlayerUid == _config.PlayerUid;

        // 清除前弹窗确认，可选择是否同步清除所有成员已上线状态
        var (confirmed, clearAll) = ShowClearOnlineConfirmDialog(isSelf);
        if (!confirmed) return; // 用户取消

        if (clearAll)
        {
            // 一键清除所有成员：先清本地（自己或遥控器执行端），再广播给所有成员
            if (isSelf)
            {
                if (_config?.ObserverMode == true)
                {
                    if (_signalRClient != null)
                    {
                        var cmd = new RemoteCommand
                        {
                            Cmd = "clear_online",
                            Sender = _config.PlayerName ?? "",
                            SenderUid = _config.PlayerUid,
                            Target = [_config.PlayerUid],
                            CommandId = "local_" + DateTime.Now.Ticks,
                            Params = new Dictionary<string, object>()
                        };
                        AddLog("遥控器模式: 向执行端下发清除上线");
                        _ = _signalRClient.SendRemoteCommandAsync(cmd);
                    }
                }
                else
                {
                    await ClearLocalOnline();
                }
            }
            if (_signalRClient != null)
            {
                var cmd = new RemoteCommand
                {
                    Cmd = "clear_online",
                    Sender = _config.PlayerName ?? "",
                    SenderUid = _config.PlayerUid,
                    Target = ["*"],
                    CommandId = "local_" + DateTime.Now.Ticks,
                    Params = new Dictionary<string, object>()
                };
                AddLog("已清除所有成员的已上线状态（同步给所有成员）");
                _ = _signalRClient.SendRemoteCommandAsync(cmd);
            }
            return;
        }

        if (isSelf)
        {
            if (_config?.ObserverMode == true)
            {
                // 遥控器模式：发给执行端（同 UID 的另一端）
                if (_signalRClient != null)
                {
                    var cmd = new RemoteCommand
                    {
                        Cmd = "clear_online",
                        Sender = _config.PlayerName ?? "",
                        SenderUid = _config.PlayerUid,
                        Target = [_config.PlayerUid],
                        CommandId = "local_" + DateTime.Now.Ticks,
                        Params = new Dictionary<string, object>()
                    };
                    AddLog($"遥控器模式: 向执行端下发清除上线");
                    _ = _signalRClient.SendRemoteCommandAsync(cmd);
                }
            }
            else
            {
                await ClearLocalOnline();
            }
        }
        else if (_signalRClient != null)
        {
            var cmd = new RemoteCommand
            {
                Cmd = "clear_online",
                Sender = _config.PlayerName ?? "",
                SenderUid = _config.PlayerUid,
                Target = [targetMember!.PlayerUid],
                CommandId = "local_" + DateTime.Now.Ticks,
                Params = new Dictionary<string, object>()
            };
            AddLog($"向 {targetMember.PlayerName} 下发清除上线");
            _ = _signalRClient.SendRemoteCommandAsync(cmd);
        }
    }

    /// <summary>清除上线确认弹窗（深色鎏金主题）。
    /// 返回值：confirmed=是否确认，clearAll=是否同时清除所有成员已上线状态。</summary>
    private (bool confirmed, bool clearAll) ShowClearOnlineConfirmDialog(bool isSelf)
    {
        bool confirmed = false, clearAll = false;
        var window = new Window
        {
            Title = "清除上线",
            Width = 300, Height = 180,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new Grid { Margin = new Thickness(20) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0 标题
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); // 1 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2 提示文字
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) }); // 3 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 4 同步勾选框
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 5 弹性
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 6 按钮

        var titleLabel = new TextBlock
        {
            Text = "确认清除上线",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(titleLabel, 0);
        panel.Children.Add(titleLabel);

        var tip = new TextBlock
        {
            Text = "确定要清除已上线状态吗？",
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(tip, 2);
        panel.Children.Add(tip);

        var syncCheckBox = new System.Windows.Controls.CheckBox
        {
            Content = "同时清除所有成员已上线状态",
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsChecked = false
        };
        syncCheckBox.Checked += (_, _) => clearAll = true;
        syncCheckBox.Unchecked += (_, _) => clearAll = false;
        Grid.SetRow(syncCheckBox, 4);
        panel.Children.Add(syncCheckBox);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        btnPanel.Children.Add(new Button
        {
            Content = "取消",
            Width = 80, Height = 30,
            Margin = new Thickness(0, 0, 10, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Cursor = System.Windows.Input.Cursors.Hand
        });
        ((Button)btnPanel.Children[^1]).Click += (_, _) => window.Close();

        var okBtn = new Button
        {
            Content = "确定",
            Width = 80, Height = 30,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        okBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        okBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        okBtn.Click += (_, _) =>
        {
            confirmed = true;
            window.Close();
        };
        btnPanel.Children.Add(okBtn);

        Grid.SetRow(btnPanel, 6);
        panel.Children.Add(btnPanel);

        window.Content = panel;
        window.ShowDialog();
        return (confirmed, clearAll);
    }

    /// <summary>本地清除已上线状态：状态机复位为 Idle 并封口边沿基线，然后上报服务端。
    /// [P1] 状态部分（含 int.MaxValue 封口语义）收编进 OnlineIntentLifecycle；BGI 实际 gen 的 IPC 读取留在本类。</summary>
    private async Task ClearLocalOnline()
    {
        // 立即部分：Idle、清挂起补报（不再补报已撤销的上线意图）、置手动清除抑制
        _intentLifecycle.OnManualCleared();
        // 关键：把边沿基线提升到当前 BGI 的 onlineGeneration 值，
        // 这样 ReportStatusAsync 的边沿探测（gen > 基线）不再触发重复上线。
        // 但真正的命令上线（BGI 新执行"联机锄地上线"，generation 递增）仍能触发（新值 > 当前值）。
        // 读取失败时用一个很大的值兜底（保证本会话内不再被旧 generation 触发）。

        // 遥控器模式：本机没有 BGI 进程，跳过 IPC 调用（避免死锁），直接设最大兜底值
        if (_config?.ObserverMode == true)
        {
            _intentLifecycle.SealBaselineAfterManualClear(int.MaxValue);
        }
        else
        {
            try
            {
                // [切片4] ext 通道优先，v2 短连接兜底
                var resp = await SendBgiIpcPreferredAsync("task.status", null);
                if (resp is { Success: true } && !string.IsNullOrEmpty(resp.Data))
                {
                    var sdata = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(resp.Data);
                    if (sdata.TryGetProperty("onlineGeneration", out var og)
                        && og.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        // 按 BGI 实际 gen 封口：BGI 后续新触发（gen 递增）仍可解除封口上线
                        _intentLifecycle.SealBaselineAfterManualClear(og.GetInt32());
                    }
                    else
                    {
                        _intentLifecycle.SealBaselineAfterManualClear(int.MaxValue);
                    }
                }
                else
                {
                    _intentLifecycle.SealBaselineAfterManualClear(int.MaxValue);
                }
            }
            catch
            {
                _intentLifecycle.SealBaselineAfterManualClear(int.MaxValue);
            }
        }

        var self = Members.FirstOrDefault(m => m.PlayerUid == _config?.PlayerUid);
        if (self != null)
        {
            self.OnlineReady = false;
            self.OnlineMode = "none";
        }
        AddLog("已清除上线状态");
        _ = ReportStatusAsync();
    }

    private void ClearLog()
    {
        CommandLogs.Clear();
        CommandLogsText = "";
    }

    /// <summary>清除指定成员的已联机记录（OnlineHistory），清自己和清他人通用入口。</summary>
    private async void OnClearOnlineHistory(object? parameter)
    {
        if (parameter is MemberViewModel member)
        {
            await ClearOnlineHistoryInternalAsync(member);
        }
    }

    /// <summary>清除指定成员的 OnlineHistory，通过 SignalR 通知服务端清空并广播。</summary>
    private async Task ClearOnlineHistoryInternalAsync(MemberViewModel member)
    {
        if (_signalRClient == null)
        {
            AddLog("清除记录失败：SignalR 未连接（_signalRClient == null）");
            return;
        }
        try
        {
            await _signalRClient.ClearOnlineHistoryAsync(member.PlayerUid);
            AddLog($"已清除 {member.PlayerName} 的联机记录");

            // 如果是清自己，重置本地状态以允许重新上线
            if (_config != null && member.PlayerUid == _config.PlayerUid)
            {
                _lastScheduledFireDate = DateTime.MinValue;
                _intentLifecycle.NoteOnlineHistoryCleared(); // [P1] 解除手动清除抑制
                AddLog("已重置本地状态，可重新上线");
            }
        }
        catch (Exception ex)
        {
            AddLog($"清除记录失败: {ex.Message}");
        }
    }

    /// <summary>定时上线时间弹窗（深色鎏金主题，时/分 ListBox 选择）。
    /// 返回值：time=null 用户取消，""=清除定时上线，"HH:mm"=设定时间；syncToAll=是否同步给所有成员。</summary>
    private (string? time, bool syncToAll) ShowScheduledOnlineTimeDialog(string currentTime)
    {
        // 解析当前已设定时间作初始选择（兼容 HH:mm 与单数 H:mm）
        int initHour = -1, initMinute = 0;
        if (TimeSpan.TryParse(currentTime, out var cur))
        {
            initHour = cur.Hours;
            initMinute = cur.Minutes;
        }

        var window = new Window
        {
            Title = "定时上线",
            Width = 340, Height = 380,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new Grid { Margin = new Thickness(20) };
        string? result = null; // 确定后回填选定的 "HH:mm"；取消保持 null
        bool syncToAll = false; // 勾选"同步给所有成员"
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 0 标题
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); // 1 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2 时/分选择器（Star 限高，按 §21.5 防按钮被顶出）
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) }); // 3 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 4 提示文字
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 5 同步勾选框行
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 6 按钮（固定底部，永不溢出）

        var titleLabel = new TextBlock
        {
            Text = "设定定时上线时间",
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(titleLabel, 0);
        panel.Children.Add(titleLabel);

        // 时/分选择器：用两个并排 ListBox（复用 §21 深色 ListBox 样式，白字深底可读；ComboBox 白底弹层看不清，禁用）
        var darkItem = CreateDarkListBoxItemStyle();
        var hourBox = new System.Windows.Controls.ListBox
        {
            // 高度由外围 Grid 的 Star 行约束（§21.5），内部自动出现滚动条
            Width = 72,
            FontSize = 15,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
            BorderThickness = new Thickness(1),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E)),
            ItemContainerStyle = darkItem
        };
        for (var h = 0; h < 24; h++) hourBox.Items.Add(h.ToString("00"));
        hourBox.SelectedIndex = initHour is >= 0 and < 24 ? initHour : DateTime.Now.Hour;
        hourBox.ScrollIntoView(hourBox.SelectedItem);

        var minuteBox = new System.Windows.Controls.ListBox
        {
            // 高度由外围 Grid 的 Star 行约束（§21.5），内部自动出现滚动条
            Width = 72,
            FontSize = 15,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
            BorderThickness = new Thickness(1),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E)),
            ItemContainerStyle = darkItem
        };
        for (var m = 0; m < 60; m++) minuteBox.Items.Add(m.ToString("00"));
        minuteBox.SelectedIndex = initMinute is >= 0 and < 60 ? initMinute : 0;
        minuteBox.ScrollIntoView(minuteBox.SelectedItem);

        var colon = new TextBlock
        {
            Text = ":", FontSize = 20, FontWeight = FontWeights.Bold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(8, 0, 8, 2)
        };
        var picker = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Center };
        picker.Children.Add(hourBox);
        picker.Children.Add(colon);
        picker.Children.Add(minuteBox);
        Grid.SetRow(picker, 2);
        panel.Children.Add(picker);

        var tip = new TextBlock
        {
            Text = "选择到点自动上线的时刻（时 : 分）",
            FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(tip, 4);
        panel.Children.Add(tip);

        // 同步给所有成员勾选框
        var syncCheckBox = new System.Windows.Controls.CheckBox
        {
            Content = "同步给所有成员",
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)),
            HorizontalAlignment = HorizontalAlignment.Center,
            IsChecked = false
        };
        syncCheckBox.Checked += (_, _) => syncToAll = true;
        syncCheckBox.Unchecked += (_, _) => syncToAll = false;
        Grid.SetRow(syncCheckBox, 5);
        panel.Children.Add(syncCheckBox);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 16, 0, 0)
        };
        btnPanel.Children.Add(new Button
        {
            Content = "取消",
            Width = 80, Height = 30,
            Margin = new Thickness(0, 0, 10, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Cursor = System.Windows.Input.Cursors.Hand
        });
        // 取消
        ((Button)btnPanel.Children[^1]).Click += (_, _) => window.Close();

        // 清除定时上线按钮（深色，危险操作）。点击后 result="" 表示清除。
        btnPanel.Children.Add(new Button
        {
            Content = "清除定时",
            Width = 88, Height = 30,
            Margin = new Thickness(0, 0, 10, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x8E, 0x6E, 0x6E)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0x6E, 0x6E)),
            Cursor = System.Windows.Input.Cursors.Hand
        });
        ((Button)btnPanel.Children[^1]).Click += (_, _) => { result = ""; window.Close(); };

        var okBtn = new Button
        {
            Content = "确定",
            Width = 80, Height = 30,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        okBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        okBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        okBtn.Click += (_, _) =>
        {
            var hh = hourBox.SelectedIndex is >= 0 and < 24 ? hourBox.SelectedIndex : DateTime.Now.Hour;
            var mm = minuteBox.SelectedIndex is >= 0 and < 60 ? minuteBox.SelectedIndex : 0;
            result = $"{hh:00}:{mm:00}";
            window.Close();
        };
        btnPanel.Children.Add(okBtn);

        Grid.SetRow(btnPanel, 6);
        panel.Children.Add(btnPanel);

        window.Content = panel;
        window.ShowDialog();
        return (result, syncToAll);
    }

    /// <summary>应用定时上线时间：写配置、保存、更新自身卡片、重启定时器并立即上报。
    /// 设定新时间时重置按天去重标记，让新设定能正常触发。</summary>
    private void ApplyScheduledOnlineTime(string time)
    {
        _config!.ScheduledOnlineTime = time;
        _configManager?.Save(_config);

        // 手动设定/清除定时上线 = 用户主动操作，清除"已手动清除上线"抑制标志，允许重新上线
        _intentLifecycle.NoteScheduledOnlineConfigured();

        // 定时上线语义 = 闹钟：设/清时间只更新闹钟显示，不改变当前上线状态。
        // 是否上线由状态机 IsOnlineReady 决定（定时到点/命令上线才触发 MarkOnlineAsync 置 Armed）；
        // 清除上线请用"清除上线"按钮。
        var self = Members.FirstOrDefault(m => m.PlayerUid == _config.PlayerUid);
        if (self != null)
        {
            self.ScheduledOnlineTime = time;
        }

        // 重置按天去重标记，让新设定的时间能正常触发
        _lastScheduledFireDate = DateTime.MinValue;
        ClearPersistedScheduledFireDate();

        // 若设定的时刻已过（now >= target），抑制今天的重复触发（避免"设定过去时间立即上线"导致状态瞬间残留），
        // 下次在明天同一时刻触发。[实机修复 2026-09-05] 该抑制一并持久化，重启后仍有效。
        if (!string.IsNullOrEmpty(time)
            && TimeSpan.TryParse(time, out var tgt)
            && DateTime.Now >= DateTime.Now.Date.Add(tgt))
        {
            _lastScheduledFireDate = DateTime.Now.Date;
            PersistScheduledFireDate(DateTime.Now.Date);
        }

        // 重启定时器（StartOnlineScheduler 内部会 Dispose 旧的）
        StartOnlineScheduler();

        AddLog(string.IsNullOrEmpty(time) ? "已清除定时上线" : $"已设定定时上线: {time}");
        _ = ReportStatusAsync();
    }

    /// <summary>显示上线记录弹窗（深色鎏金主题，与绑定锄地配置组弹窗一致）。</summary>
    private void ShowOnlineHistoryDialog(string title, string content, MemberViewModel? member)
    {
        var window = new Window
        {
            Title = title,
            Width = 460, Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new Grid { Margin = new Thickness(20) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        var titleLabel = new TextBlock
        {
            Text = title,
            FontSize = 15,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(titleLabel, 0);
        Grid.SetColumnSpan(titleLabel, 2);
        panel.Children.Add(titleLabel);

        var contentBox = new TextBox
        {
            Text = content,
            IsReadOnly = true,
            TextWrapping = TextWrapping.Wrap,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
            BorderThickness = new Thickness(1),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA)),
            FontSize = 13,
            Padding = new Thickness(10, 8, 10, 8)
        };
        Grid.SetRow(contentBox, 2);
        Grid.SetColumnSpan(contentBox, 2);
        panel.Children.Add(contentBox);

        // 清除记录按钮（左侧）
        if (member != null)
        {
            var clearBtn = new Button
            {
                Content = "清除记录",
                Width = 80, Height = 30,
                HorizontalAlignment = HorizontalAlignment.Left,
                FontWeight = FontWeights.SemiBold,
                BorderThickness = new Thickness(0),
                Cursor = System.Windows.Input.Cursors.Hand,
                Background = new System.Windows.Media.LinearGradientBrush
                {
                    StartPoint = new System.Windows.Point(0, 0),
                    EndPoint = new System.Windows.Point(1, 1),
                    GradientStops =
                    {
                        new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xE8, 0x6D, 0x6D), 0),
                        new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0x37, 0x37), 1)
                    }
                },
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xFF, 0xF0, 0xF0))
            };
            clearBtn.Click += (_, _) =>
            {
                window.Close();
                _ = ClearOnlineHistoryInternalAsync(member);
            };
            Grid.SetRow(clearBtn, 3);
            Grid.SetColumn(clearBtn, 0);
            panel.Children.Add(clearBtn);
        }

        var okBtn = new Button
        {
            Content = "确定",
            Width = 80, Height = 30,
            HorizontalAlignment = HorizontalAlignment.Right,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        okBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        okBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        okBtn.Click += (_, _) => window.Close();
        Grid.SetRow(okBtn, 3);
        Grid.SetColumn(okBtn, 1);
        panel.Children.Add(okBtn);

        window.Content = panel;
        window.ShowDialog();
    }

    /// <summary>打开联机锄地配置组绑定弹窗（多选）。所有人可编辑。</summary>
    private async void OnBindHoeingGroup(object? parameter)
    {
        if (_config == null) return;

        // 判断目标：点自己的卡片还是别人的
        var targetMember = parameter as MemberViewModel;
        var isSelf = targetMember == null || targetMember.PlayerUid == _config.PlayerUid;

        // 配置组列表来源：改自己 = 本机 BGI；改别人 = 对方的配置组（来自服务端该成员上报的 ConfigGroups）
        List<string> allGroups = [];
        List<string> allOneClicks = [];
        if (isSelf)
        {
            // 遥控器模式：从其他在线成员取配置组列表
            if (_config?.ObserverMode == true)
            {
                var target = Members.FirstOrDefault(m => m.PlayerUid == _config.PlayerUid && m.Online
                    && (m.ConfigGroups?.Count > 0 || m.OneClickConfigs?.Count > 0));
                if (target != null)
                {
                    allGroups = (target.ConfigGroups ?? []).Where(g => !string.IsNullOrEmpty(g)).ToList();
                    allOneClicks = (target.OneClickConfigs ?? []).Where(o => !string.IsNullOrEmpty(o)).ToList();
                }
            }
            else
            {
                try
                {
                    // [切片4] ext 通道优先，v2 短连接兜底
                    var resp = await SendBgiIpcPreferredAsync("config.list", null);
                    if (resp is { Success: true } && !string.IsNullOrEmpty(resp.Data))
                    {
                        var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(resp.Data);
                        if (data.TryGetProperty("configGroups", out var groups) && groups.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var g in groups.EnumerateArray()) allGroups.Add(g.GetString() ?? "");
                        }
                        if (data.TryGetProperty("oneClickConfigs", out var oneClicks) && oneClicks.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var oc in oneClicks.EnumerateArray()) allOneClicks.Add(oc.GetString() ?? "");
                        }
                    }
                }
                catch
                {
                    // IPC 不可用时跳过
                }
            }
        }
        else if (targetMember != null)
        {
            allGroups = (targetMember.ConfigGroups ?? []).Where(g => !string.IsNullOrEmpty(g)).ToList();
            allOneClicks = (targetMember.OneClickConfigs ?? []).Where(o => !string.IsNullOrEmpty(o)).ToList();
        }

        // 判断获取的配置组/一条龙列表是否来自缓存（BGI 未运行或离线时，配置来自之前上报的缓存）
        // 与 ShowConfigSelectionDialog 的 isCached 判定保持一致
        bool isCached = false;
        if (!(isSelf && _config?.ObserverMode != true))
        {
            // isSelf 且非遥控器模式 → 实时 IPC 查询，非缓存
            // 其他情况（遥控器模式 / 查看他人）→ 看来源成员的 BGI 运行状态
            if (isSelf)
            {
                // 遥控器模式：取同 UID 的执行端成员作为来源
                var srcSelf = Members.FirstOrDefault(m => m.PlayerUid == _config?.PlayerUid);
                isCached = srcSelf != null && (srcSelf.BgiStatus != "running" || !srcSelf.Online);
            }
            else
            {
                isCached = targetMember != null && (targetMember.BgiStatus != "running" || !targetMember.Online);
            }
        }

        if (allGroups.Count == 0 && allOneClicks.Count == 0)
        {
            MessageBox.Show(isSelf
                ? "未获取到 BGI 配置组或一条龙列表，请确认 BGI 已启动且配置组/一条龙目录存在。"
                : $"未获取到 {targetMember?.PlayerName ?? "对方"} 的配置组或一条龙列表（可能对方尚未上报配置组）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // [任务策略] 「完成后动作」初始值：改自己=读本机持久化配置；改别人=先定向拉取对方当前配置（超时则显示默认值）
        var initPolicy = PolicyFromString(_config?.OnlineHoeingCompletionPolicy);
        var initSpecifiedType = _config?.OnlineHoeingSpecifiedTaskType ?? "group";
        var initSpecifiedName = _config?.OnlineHoeingSpecifiedTaskName ?? "";
        // 改别人时若拉取失败：弹窗里的「完成后动作」并非对方真实值，直接保存会把默认值覆盖到对方配置上。
        // 因此此时不静默放过，而是在弹窗内显示醒目警告 + 默认禁止保存，由用户显式确认后才允许提交。
        var policyPullFailed = false;
        if (!isSelf && targetMember != null)
        {
            if (targetMember.Online && _signalRClient is { IsConnected: true })
            {
                AddLog($"正在拉取 {targetMember.PlayerName} 的上线锄地「完成后动作」配置...");
                var pulled = await GetTaskPolicySync().PullAsync(targetMember.PlayerUid);
                if (pulled != null)
                {
                    initPolicy = PolicyFromString(pulled.GetValueOrDefault("policy"));
                    initSpecifiedType = pulled.GetValueOrDefault("specifiedType") ?? "group";
                    initSpecifiedName = pulled.GetValueOrDefault("specifiedName") ?? "";
                }
                else
                {
                    policyPullFailed = true;
                    AddLog($"拉取 {targetMember.PlayerName} 的策略配置超时/失败，弹窗显示默认值；保存将覆盖对方配置");
                }
            }
            else
            {
                policyPullFailed = true;
                AddLog($"{targetMember.PlayerName} 不在线或连接不可用，「完成后动作」显示默认值；保存将覆盖对方配置");
            }
        }

        // 构建可排序的深色主题绑定弹窗
        // 使用与主窗口一致的深色原神主题风格
        // 高度自适应：不再写死 Height，改为按内容 SizeToContent=Height 量测后钳到工作区上限
        // （内容超限时由下方两个列表回收剩余空间，见 Loaded 回调）。历史 bug：写死 720 且
        // NoResize，内容一长（如新增的拉取失败警告条）上下就被屏幕/窗口边裁掉，按钮够不到。
        var window = new System.Windows.Window
        {
            Title = "绑定联机锄地配置组",
            Width = 460,
            SizeToContent = System.Windows.SizeToContent.Height,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Application.Current?.MainWindow,
            WindowStyle = System.Windows.WindowStyle.SingleBorderWindow,
            ResizeMode = System.Windows.ResizeMode.CanResize,
            MinWidth = 460,
            MaxWidth = 720,
            // 下限：标题 + 一小段列表 + 按钮至少可见（实际会被工作区封顶下调，见 SourceInitialized）
            MinHeight = 320,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };

        // 滚动条统一为整体鎏金细窄款：把 App.xaml 的 GoldScrollViewer 作为"隐式样式"放进弹窗资源，
        // 覆盖弹窗内所有未显式指定样式的 ScrollViewer —— 含中间区、两个 ListBox 模板内部的
        // ScrollViewer、以及「完成后动作」下拉框 Popup 里的列表，避免出现系统默认灰白滚动条。
        // 与 DodocoPage.xaml 的隐式 ScrollViewer 样式同一做法（该模式已在该页验证可用）。
        // 必须在构建/挂载内容之前设置，隐式样式才能对整棵树生效。
        if (Application.Current?.TryFindResource("GoldScrollViewer") is Style goldScrollViewer)
        {
            window.Resources[typeof(System.Windows.Controls.ScrollViewer)] = goldScrollViewer;
        }

        // 颜色常量
        var gold = System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D);
        var goldDeep = System.Windows.Media.Color.FromRgb(0xC9, 0xA5, 0x3F);
        var dim = System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0);
        var white = System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA);
        var cardBg = System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E);
        var cardEdge = System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37);

        var panel = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(16) };
        // 三段式布局：标题/缓存提示（固定）｜可滚动内容区（占满剩余）｜按钮（固定钉底）。
        // 关键：中间区必须能滚动——200% 缩放下逻辑工作区只有 672 DIP，窗口再"自适应"也放不下全部内容，
        // 没有滚动容器时 WPF 只会把超出部分裁掉（表现为底部按钮被截），Star 行救不了：
        // 内容的最小高度之和一旦超过可用高度，裁剪不可避免。
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto }); // 标题
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto }); // 缓存提示
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) }); // 可滚动内容（已选/可选列表 + 完成后动作 + 警告）
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(10) }); // 间距
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto }); // 按钮（钉底，永不被裁）

        // 标题
        var titleLabel = new System.Windows.Controls.TextBlock
        {
            Text = "绑定联机锄地配置组",
            FontSize = 15,
            FontWeight = System.Windows.FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(gold),
            HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
            Margin = new System.Windows.Thickness(0, 0, 0, 0)
        };
        System.Windows.Controls.Grid.SetRow(titleLabel, 0);
        panel.Children.Add(titleLabel);

        // 如果是缓存数据，添加提示行（与 ShowConfigSelectionDialog 样式一致）
        if (isCached)
        {
            var cacheHint = new System.Windows.Controls.TextBlock
            {
                Text = "⚠ 该成员 BGI 未连接，以下为缓存配置，执行前请确认 BGI 已启动",
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0xA8, 0x4E)),
                TextWrapping = System.Windows.TextWrapping.Wrap,
                Margin = new System.Windows.Thickness(0, 4, 0, 4)
            };
            System.Windows.Controls.Grid.SetRow(cacheHint, 1);
            panel.Children.Add(cacheHint);
        }

        // ========== 可滚动内容区（已选列表 + 可选列表 + 完成后动作 + 拉取失败警告）==========
        // 内容放 StackPanel，外层套 ScrollViewer：内容装得下时自然铺开不显示滚动条，
        // 装不下（高缩放/小屏/条目多）时中间区滚动，标题与底部按钮始终可见可点。
        var scrollContent = new System.Windows.Controls.StackPanel();

        // ========== 已选配置组列表（带排序） ==========
        var selectedBorder = new System.Windows.Controls.Border
        {
            CornerRadius = new System.Windows.CornerRadius(8),
            BorderThickness = new System.Windows.Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(cardEdge),
            Background = new System.Windows.Media.SolidColorBrush(cardBg),
            Padding = new System.Windows.Thickness(8)
        };

        var selectedInnerPanel = new System.Windows.Controls.StackPanel();

        var selectedHeader = new System.Windows.Controls.TextBlock
        {
            Text = "已选配置组（拖动排序）",
            FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(gold),
            Margin = new System.Windows.Thickness(4, 2, 0, 6)
        };
        selectedInnerPanel.Children.Add(selectedHeader);

        var selectedListBox = new System.Windows.Controls.ListBox
        {
            // 高度随条目自适应：MinHeight 保证空列表也占位，MaxHeight 防止条目过多时把窗口顶爆
            MinHeight = 60,
            MaxHeight = 220,
            BorderThickness = new System.Windows.Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            FontSize = 12
        };
        System.Windows.Controls.ScrollViewer.SetVerticalScrollBarVisibility(selectedListBox, System.Windows.Controls.ScrollBarVisibility.Auto);
        System.Windows.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(selectedListBox, System.Windows.Controls.ScrollBarVisibility.Disabled);
        // 当前已选配置组列表（可修改的副本）。改别人时用对方的已选配置组（来自服务端同步），改自己用本机绑定。
        // 遥控器模式：从执行端成员取已绑定的配置组（本机无 BGI，无本地配置）。
        var currentSelected = new System.Collections.ObjectModel.ObservableCollection<string>(
            isSelf
                ? (_config?.ObserverMode == true
                    ? (Members.FirstOrDefault(m => m.PlayerUid == _config.PlayerUid)?.OnlineHoeingGroupNames ?? [])
                    : (_config.OnlineHoeingGroupNames ?? []))
                : (targetMember?.OnlineHoeingGroupNames ?? []));

        // 先声明可选列表框（供 RefreshAvailableList 使用）
        var availableListBox = new System.Windows.Controls.ListBox
        {
            // 同已选列表：随条目自适应，上限防止顶爆窗口
            MinHeight = 60,
            MaxHeight = 220,
            BorderThickness = new System.Windows.Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            FontSize = 12
        };
        System.Windows.Controls.ScrollViewer.SetVerticalScrollBarVisibility(availableListBox, System.Windows.Controls.ScrollBarVisibility.Auto);
        System.Windows.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(availableListBox, System.Windows.Controls.ScrollBarVisibility.Disabled);

        // 刷新可选列表（排除已选的）
        System.Action RefreshAvailableList = null!;
        RefreshAvailableList = () =>
        {
            availableListBox.Items.Clear();
            // 配置组（前缀 [配置]）
            foreach (var g in allGroups)
            {
                if (currentSelected.Contains("[配置]" + g)) continue;
                var item = new System.Windows.Controls.ListBoxItem
                {
                    Content = "[配置]" + g,
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new System.Windows.Thickness(0),
                    Foreground = new System.Windows.Media.SolidColorBrush(dim),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Padding = new System.Windows.Thickness(8, 2, 8, 2)
                };
                availableListBox.Items.Add(item);
            }
            // 一条龙（前缀 [一条龙]）
            foreach (var o in allOneClicks)
            {
                if (currentSelected.Contains("[一条龙]" + o)) continue;
                var item = new System.Windows.Controls.ListBoxItem
                {
                    Content = "[一条龙]" + o,
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new System.Windows.Thickness(0),
                    Foreground = new System.Windows.Media.SolidColorBrush(dim),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    Padding = new System.Windows.Thickness(8, 2, 8, 2)
                };
                availableListBox.Items.Add(item);
            }
        };

        // 刷新已选列表
        System.Action rebuildSelectedList = null!;
        rebuildSelectedList = () =>
        {
            selectedListBox.Items.Clear();
            for (int idx = 0; idx < currentSelected.Count; idx++)
            {
                var gName = currentSelected[idx];
                var item = new System.Windows.Controls.ListBoxItem
                {
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new System.Windows.Thickness(0),
                    Padding = new System.Windows.Thickness(4, 2, 4, 2),
                    Focusable = false
                };

                var row = new System.Windows.Controls.Grid();
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto }); // 序号
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) }); // 名称
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto }); // 上移
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto }); // 下移
                row.ColumnDefinitions.Add(new System.Windows.Controls.ColumnDefinition { Width = System.Windows.GridLength.Auto }); // 删除

                // 序号徽章（金色胶囊）
                var badge = new System.Windows.Controls.Border
                {
                    CornerRadius = new System.Windows.CornerRadius(9),
                    Background = new System.Windows.Media.SolidColorBrush(gold),
                    Width = 22, Height = 22,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    Margin = new System.Windows.Thickness(0, 0, 6, 0)
                };
                var badgeText = new System.Windows.Controls.TextBlock
                {
                    Text = (idx + 1).ToString(),
                    FontSize = 11,
                    FontWeight = System.Windows.FontWeights.SemiBold,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16)),
                    HorizontalAlignment = System.Windows.HorizontalAlignment.Center,
                    VerticalAlignment = System.Windows.VerticalAlignment.Center
                };
                badge.Child = badgeText;
                System.Windows.Controls.Grid.SetColumn(badge, 0);
                row.Children.Add(badge);

                // 配置组名称
                var nameText = new System.Windows.Controls.TextBlock
                {
                    Text = gName,
                    FontSize = 12,
                    Foreground = new System.Windows.Media.SolidColorBrush(white),
                    VerticalAlignment = System.Windows.VerticalAlignment.Center,
                    TextTrimming = System.Windows.TextTrimming.CharacterEllipsis,
                    Margin = new System.Windows.Thickness(0, 0, 6, 0)
                };
                System.Windows.Controls.Grid.SetColumn(nameText, 1);
                row.Children.Add(nameText);

                // 上移按钮
                var upBtn = new System.Windows.Controls.Button
                {
                    Content = "↑",
                    Width = 24, Height = 24,
                    FontSize = 12,
                    FontWeight = System.Windows.FontWeights.Bold,
                    Foreground = new System.Windows.Media.SolidColorBrush(dim),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new System.Windows.Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "上移",
                    IsEnabled = idx > 0,
                    Margin = new System.Windows.Thickness(0, 0, 2, 0)
                };
                var capturedIdx = idx;
                var capturedName = gName;
                upBtn.Click += (_, _) =>
                {
                    var ci = currentSelected.IndexOf(capturedName);
                    if (ci > 0)
                    {
                        currentSelected.Move(ci, ci - 1);
                        rebuildSelectedList();
                        // 同步从可选列表取消选中该配置组
                        RefreshAvailableList();
                    }
                };
                System.Windows.Controls.Grid.SetColumn(upBtn, 2);
                row.Children.Add(upBtn);

                // 下移按钮
                var downBtn = new System.Windows.Controls.Button
                {
                    Content = "↓",
                    Width = 24, Height = 24,
                    FontSize = 12,
                    FontWeight = System.Windows.FontWeights.Bold,
                    Foreground = new System.Windows.Media.SolidColorBrush(dim),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new System.Windows.Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "下移",
                    IsEnabled = idx < currentSelected.Count - 1,
                    Margin = new System.Windows.Thickness(0, 0, 2, 0)
                };
                downBtn.Click += (_, _) =>
                {
                    var ci = currentSelected.IndexOf(capturedName);
                    if (ci < currentSelected.Count - 1)
                    {
                        currentSelected.Move(ci, ci + 1);
                        rebuildSelectedList();
                        RefreshAvailableList();
                    }
                };
                System.Windows.Controls.Grid.SetColumn(downBtn, 3);
                row.Children.Add(downBtn);

                // 删除按钮
                var delBtn = new System.Windows.Controls.Button
                {
                    Content = "✕",
                    Width = 24, Height = 24,
                    FontSize = 12,
                    Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0x8A, 0x6F)),
                    Background = System.Windows.Media.Brushes.Transparent,
                    BorderThickness = new System.Windows.Thickness(0),
                    Cursor = System.Windows.Input.Cursors.Hand,
                    ToolTip = "移除"
                };
                delBtn.Click += (_, _) =>
                {
                    currentSelected.Remove(capturedName);
                    rebuildSelectedList();
                    RefreshAvailableList();
                };
                System.Windows.Controls.Grid.SetColumn(delBtn, 4);
                row.Children.Add(delBtn);

                item.Content = row;
                selectedListBox.Items.Add(item);
            }
        };

        rebuildSelectedList();

        selectedInnerPanel.Children.Add(selectedListBox);
        selectedBorder.Child = selectedInnerPanel;
        scrollContent.Children.Add(selectedBorder);

        // ========== 可选配置组列表 ==========
        var availableBorder = new System.Windows.Controls.Border
        {
            CornerRadius = new System.Windows.CornerRadius(8),
            BorderThickness = new System.Windows.Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Padding = new System.Windows.Thickness(8),
            Margin = new System.Windows.Thickness(0, 10, 0, 0) // 原 Grid 行间距改由 Margin 提供
        };

        var availableInnerPanel = new System.Windows.Controls.StackPanel();

        var availableHeader = new System.Windows.Controls.TextBlock
        {
            Text = "可选配置组（点击添加）",
            FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(dim),
            Margin = new System.Windows.Thickness(4, 2, 0, 2)
        };
        availableInnerPanel.Children.Add(availableHeader);

        // 添加任务说明
        var taskDesc = new System.Windows.Controls.TextBlock
        {
            Text = "[配置] = 上线任务（定时上线后自动执行）  [一条龙] = 一键锄地（手动触发）",
            FontSize = 10,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)),
            TextWrapping = System.Windows.TextWrapping.Wrap,
            Margin = new System.Windows.Thickness(4, 0, 0, 4)
        };
        availableInnerPanel.Children.Add(taskDesc);

        // 保留事件绑定到已声明的 availableListBox
        // 点击可选列表项，添加到已选
        availableListBox.PreviewMouseDown += (sender, e) =>
        {
            var item = (e.OriginalSource as System.Windows.FrameworkElement)?.DataContext as System.Windows.Controls.ListBoxItem;
            if (item == null)
            {
                // 尝试从点击位置获取 ListBoxItem
                var dep = e.OriginalSource as System.Windows.DependencyObject;
                while (dep != null && !(dep is System.Windows.Controls.ListBoxItem))
                    dep = System.Windows.Media.VisualTreeHelper.GetParent(dep);
                if (dep is System.Windows.Controls.ListBoxItem li)
                    item = li;
            }
            // 通过命中测试找 ListBoxItem
            if (item == null)
            {
                var pos = e.GetPosition(availableListBox);
                var hit = availableListBox.InputHitTest(pos) as System.Windows.DependencyObject;
                while (hit != null && !(hit is System.Windows.Controls.ListBoxItem))
                    hit = System.Windows.Media.VisualTreeHelper.GetParent(hit);
                if (hit is System.Windows.Controls.ListBoxItem li2)
                    item = li2;
            }
            if (item != null && item.Content is string gName && !string.IsNullOrEmpty(gName))
            {
                currentSelected.Add(gName);
                rebuildSelectedList();
                RefreshAvailableList();
            }
        };

        RefreshAvailableList();

        availableInnerPanel.Children.Add(availableListBox);
        availableBorder.Child = availableInnerPanel;
        scrollContent.Children.Add(availableBorder);

        // ========== 完成后动作（上线锄地打断的原任务如何处置；全员就绪触发上线锄地固定打断，此处只配完成后动作）==========
        var completionPanel = new CompletionActionPanel(
            initPolicy, initSpecifiedType, initSpecifiedName, allGroups, allOneClicks,
            isSelf
                ? "全员就绪触发上线锄地必定打断当前任务（固定行为）。此处配置锄地完成后如何处置被中断的原任务；配置持久化保存，重启保留。"
                : $"全员就绪触发上线锄地必定打断当前任务（固定行为）。此处配置 {targetMember?.PlayerName ?? "对方"} 锄地完成后如何处置被中断的原任务；保存后落到对方本机配置并持久化。");
        scrollContent.Children.Add(completionPanel.Root);

        // 拉取失败警告：说明下方「完成后动作」不是对方真实值，保存会覆盖；需显式确认才允许提交
        var policyWarnBox = new System.Windows.Controls.Border
        {
            CornerRadius = new System.Windows.CornerRadius(6),
            BorderThickness = new System.Windows.Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0x77, 0x4E)),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x33, 0xD9, 0x77, 0x4E)),
            Padding = new System.Windows.Thickness(8, 6, 8, 6),
            Margin = new System.Windows.Thickness(0, 0, 0, 8),
            Visibility = policyPullFailed ? Visibility.Visible : Visibility.Collapsed
        };
        var policyWarnPanel = new System.Windows.Controls.StackPanel();
        policyWarnPanel.Children.Add(new System.Windows.Controls.TextBlock
        {
            Text = $"⚠ 未取到 {targetMember?.PlayerName ?? "对方"} 当前的「完成后动作」配置，下方显示的是默认值而非对方实际设置。",
            FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF0, 0xB0, 0x8A)),
            TextWrapping = System.Windows.TextWrapping.Wrap
        });
        var policyWarnCheck = new System.Windows.Controls.CheckBox
        {
            Content = "我已确认要用当前显示的动作覆盖对方的配置",
            FontSize = 11,
            Margin = new System.Windows.Thickness(0, 6, 0, 0),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA)),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        policyWarnPanel.Children.Add(policyWarnCheck);
        policyWarnBox.Child = policyWarnPanel;
        scrollContent.Children.Add(policyWarnBox);

        // 内容装入 ScrollViewer（垂直按需出滚动条；水平禁用，避免换行文本被拉成横向滚动）
        var contentScroll = new System.Windows.Controls.ScrollViewer
        {
            VerticalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Auto,
            HorizontalScrollBarVisibility = System.Windows.Controls.ScrollBarVisibility.Disabled,
            Padding = new System.Windows.Thickness(0, 0, 2, 0), // 给滚动条留一点空隙
            Content = scrollContent
        };
        // 显式套用鎏金样式（不依赖隐式查找时机），保证这个主滚动条一定不是系统默认款
        if (Application.Current?.TryFindResource("GoldScrollViewer") is Style goldSvStyle)
            contentScroll.Style = goldSvStyle;
        System.Windows.Controls.Grid.SetRow(contentScroll, 2);
        panel.Children.Add(contentScroll);

        // ========== 按钮栏 ==========
        var btnPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new System.Windows.Thickness(0, 10, 0, 0)
        };
        System.Windows.Controls.Grid.SetRow(btnPanel, 4);

        var cancelBtn = new System.Windows.Controls.Button
        {
            Content = "取消",
            Width = 80,
            Height = 30,
            Margin = new System.Windows.Thickness(0, 0, 8, 0),
            Cursor = System.Windows.Input.Cursors.Hand,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new System.Windows.Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0))
        };
        cancelBtn.Click += (_, _) => window.Close();

        // 鎏金保存按钮
        var saveBtn = new System.Windows.Controls.Button
        {
            Content = "保存",
            Width = 80,
            Height = 30,
            Cursor = System.Windows.Input.Cursors.Hand,
            FontWeight = System.Windows.FontWeights.SemiBold,
            BorderThickness = new System.Windows.Thickness(0)
        };
        // 鎏金渐变背景
        saveBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        saveBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        saveBtn.Click += async (_, _) =>
        {
            var selected = currentSelected.ToList();

            // 解析带前缀的名字为 (Name, Type) 元组
            var names = new List<string>();
            var types = new List<string>();
            foreach (var item in selected)
            {
                if (item.StartsWith("[一条龙]"))
                {
                    names.Add(item.Substring(5));
                    types.Add("onedragon");
                }
                else if (item.StartsWith("[配置]"))
                {
                    names.Add(item.Substring(4));
                    types.Add("group");
                }
                else
                {
                    // 无前缀（兼容旧数据）
                    names.Add(item);
                    types.Add("group");
                }
            }

            // 「完成后动作」取值（恢复任务=Resume / 执行动作+停止=Stop / 执行动作+执行指定任务=RunSpecified）
            var completionPolicy = completionPanel.Policy;
            var completionType = completionPanel.SpecifiedType;
            var completionName = completionPanel.SpecifiedName;
            if (completionPolicy == TaskConflictPolicy.RunSpecified && string.IsNullOrEmpty(completionName))
            {
                MessageBox.Show("已选择「执行动作 → 执行指定任务」，但未填写指定任务名称。\n请填写或改选其他完成后动作。",
                    "提示", MessageBoxButton.OK, MessageBoxImage.Information);
                return; // 不关闭弹窗，让用户补填
            }
            var completionDesc = completionPolicy switch
            {
                TaskConflictPolicy.Stop => "执行动作-停止",
                TaskConflictPolicy.RunSpecified => $"执行动作-执行指定任务（{(completionType == "onedragon" ? "一条龙" : "配置组")}「{completionName}」）",
                _ => "恢复任务"
            };

            // 拉取失败时「完成后动作」显示的是默认值而非对方实际配置，直接保存会静默覆盖对方设置。
            // 要求用户勾选确认才允许提交（名单部分不受影响，可正常保存）。
            if (policyPullFailed && !isSelf && policyWarnCheck.IsChecked == true)
            {
                AddLog($"已确认以默认值覆盖 {targetMember?.PlayerName ?? "对方"} 的「完成后动作」配置");
            }
            else if (policyPullFailed && !isSelf && policyWarnCheck.IsChecked != true)
            {
                var keep = MessageBox.Show(
                    $"未能读取 {targetMember?.PlayerName ?? "对方"} 当前的「完成后动作」配置，弹窗中显示的是默认值。\n\n" +
                    $"若继续保存，对方原有的「完成后动作」设置将被当前的「{completionDesc}」覆盖（无法自动还原）。\n\n" +
                    "是否仍要继续保存？（建议先确认对方在线后重新打开本弹窗）",
                    "确认覆盖对方配置", MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (keep != MessageBoxResult.Yes) return; // 不关闭弹窗，让用户重新选择
            }

            if (names.Count > 0)
            {
                if (isSelf)
                {
                    if (_config?.ObserverMode == true)
                    {
                        // 遥控器模式：通过 SignalR 下发命令给同 UID 的执行端
                        if (_signalRClient != null)
                        {
                            var cmd = new RemoteCommand
                            {
                                Cmd = "bind_hoeing_group",
                                Sender = _config?.PlayerName ?? "",
                                SenderUid = _config?.PlayerUid ?? "",
                                Target = [_config.PlayerUid],
                                CommandId = "local_" + DateTime.Now.Ticks,
                                Params = new Dictionary<string, object>
                                {
                                    { "groupNames", names },
                                    { "groupTypes", types },
                                    { "groupIndex", 0 }
                                }
                            };
                            AddLog($"遥控器模式: 向执行端下发绑定联机锄地配置组（按顺序执行）: {string.Join(" → ", names)}");
                            await _signalRClient.SendRemoteCommandAsync(cmd);
                        }
                    }
                    else
                    {
                        // 改自己：直接保存到本机配置
                        _config.OnlineHoeingGroupNames = names;
                        _config.OnlineHoeingGroupTypes = types;
                        _config.OnlineHoeingGroupIndex = 0;
                        _configManager?.Save(_config);
                        AddLog($"已绑定联机锄地配置组（按顺序执行）: {string.Join(" → ", names)}");
                        // 立即上报给服务端，让所有人可见
                        _ = ReportStatusAsync();
                    }
                }
                else if (targetMember != null && _signalRClient != null)
                {
                    // 改别人：通过 SignalR 下发命令，让对方保存到本机配置
                    var cmd = new RemoteCommand
                    {
                        Cmd = "bind_hoeing_group",
                        Sender = _config?.PlayerName ?? "",
                        SenderUid = _config?.PlayerUid ?? "",
                        Target = [targetMember.PlayerUid],
                        CommandId = "local_" + DateTime.Now.Ticks,
                        Params = new Dictionary<string, object>
                        {
                            { "groupNames", names },
                            { "groupTypes", types },
                            { "groupIndex", 0 }
                        }
                    };
                    AddLog($"向 {targetMember.PlayerName} 下发绑定联机锄地配置组（按顺序执行）: {string.Join(" → ", names)}");
                    await _signalRClient.SendRemoteCommandAsync(cmd);
                }
            }

            // 「完成后动作」持久化/回传（与配置组名单解耦：不改名单只改动作也能保存）
            if (isSelf && _config?.ObserverMode != true)
            {
                // 改自己（执行模式）：落到本机 AssistConfig 并保存，重启保留
                if (_config != null)
                {
                    _config.OnlineHoeingCompletionPolicy = PolicyToString(completionPolicy);
                    _config.OnlineHoeingSpecifiedTaskType = completionType;
                    _config.OnlineHoeingSpecifiedTaskName = completionName;
                    _configManager?.Save(_config);
                    AddLog($"已保存上线锄地「完成后动作」: {completionDesc}");
                }
            }
            else
            {
                // 改别人 / 遥控器模式改自己：task_policy.push 回传，对方落 AssistConfig 并保存
                var pushUid = isSelf ? _config?.PlayerUid : targetMember?.PlayerUid;
                var pushName = isSelf ? "执行端" : targetMember?.PlayerName ?? "对方";
                if (!string.IsNullOrEmpty(pushUid) && _signalRClient is { IsConnected: true })
                {
                    AddLog($"正在向 {pushName} 回传上线锄地「完成后动作」: {completionDesc}");
                    var pushResult = await GetTaskPolicySync().PushAsync(pushUid, new Dictionary<string, object>
                    {
                        ["policy"] = PolicyToString(completionPolicy),
                        ["specifiedType"] = completionType,
                        ["specifiedName"] = completionName
                    });
                    AddLog(pushResult switch
                    {
                        null => $"回传 {pushName} 的「完成后动作」超时（对方可能已离线），修改可能未生效",
                        { ok: true } r => $"对方已应用「完成后动作」: {r.message}",
                        { } r => $"对方应用「完成后动作」失败：{r.message}"
                    });
                }
                else
                {
                    AddLog("SignalR 未连接或目标不可用，「完成后动作」未回传");
                }
            }
            window.Close();
        };

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(saveBtn);
        panel.Children.Add(btnPanel);
        window.Content = panel;

        // 高度自适应 + 位置钳制：按内容量测（SizeToContent=Height），并用"所在显示器的逻辑工作区"封顶。
        // DPI 关键点：2560x1344 @200% 缩放下逻辑工作区只有 1280x672 DIP（rcWork 已排除任务栏），
        // 内容自然高度约 730 DIP，超出即被裁；而 CenterOwner 只按属主窗口居中、根本不管任务栏，
        // 属主靠下时弹窗底部会直接伸进任务栏被盖住——所以尺寸和位置都必须显式钳到工作区内。
        // 中间区是 ScrollViewer，装不下时滚动而非裁剪，按钮永远可见。
        // 显示器按 Owner 定位：此刻弹窗自身尚未居中，用自身句柄可能取到错误的屏幕。
        var owner = window.Owner;
        window.SourceInitialized += (_, _) =>
        {
            if (Helpers.DpiAwarenessController.GetLogicalWorkAreaRect(window, owner) is not { } wa) return;
            const double margin = 12;
            double maxW = Math.Max(300, wa.Width - margin * 2);
            double maxH = Math.Max(240, wa.Height - margin * 2);
            window.MaxWidth = Math.Min(window.MaxWidth, maxW);
            window.MaxHeight = maxH;
            // 极端小屏下 Min 不得大过 Max，否则 WPF 以 Min 为准又会被裁
            if (window.MinWidth > maxW) window.MinWidth = maxW;
            if (window.MinHeight > maxH) window.MinHeight = maxH;
            // 首帧位置：尚无量测高度，用最坏情况（MaxHeight）先摆一个必定落在工作区内的位置
            Helpers.DpiAwarenessController.PlaceWithinWorkArea(window, wa, maxH, margin);
        };

        // 兜底 + 收口：SourceInitialized 取不到工作区时再钳一次；随后按真实高度重新摆正，
        // 并把 SizeToContent 转 Manual —— 否则 CanResize 下用户拖拽高度会被 SizeToContent
        // 立刻按内容复原（表现为"拉不动"）。
        window.Loaded += (_, _) =>
        {
            var wa = Helpers.DpiAwarenessController.GetLogicalWorkAreaRect(window);
            if (wa is { } area)
            {
                if (double.IsPositiveInfinity(window.MaxHeight))
                {
                    double maxH = Math.Max(240, area.Height - 24);
                    window.MaxHeight = maxH;
                    if (window.MinHeight > maxH) window.MinHeight = maxH;
                }
            }
            if (window.SizeToContent != System.Windows.SizeToContent.Manual)
            {
                window.SizeToContent = System.Windows.SizeToContent.Manual;
                window.Height = window.ActualHeight;
            }
            // 量测完成后按真实高度重新居中一次（高度已 ≤ 工作区，故必然完全可见）
            if (wa is { } area2)
                Helpers.DpiAwarenessController.PlaceWithinWorkArea(window, area2, window.ActualHeight, 12);
            // 诊断探针：实机核对"封顶值/最终高度/位置/是否仍被任务栏盖住"
            Helpers.DpiAwarenessController.LogDialogMetrics(
                "bind_dialog/Loaded",
                $"max={window.MaxHeight:F1} actual={window.ActualHeight:F1} min={window.MinHeight:F1} " +
                $"top={window.Top:F1} bottom={window.Top + window.ActualHeight:F1} " +
                $"workBottom={(wa is { } a3 ? a3.Bottom : -1):F1} scrollable={contentScroll.ScrollableHeight:F1}");
        };

        window.ShowDialog();
    }

    /// <summary>
    /// 代码构建弹窗共用的深色下拉框。优先应用 App.xaml 应用级资源中的 GoldComboBox/GoldComboItem
    /// 暗色金样式（含下拉弹出层配色，见 App.xaml）；应用未初始化等极端情况下找不到资源时，
    /// 退化为手工配色的深色框（下拉弹出层为系统默认样式，仅兜底）。
    /// </summary>
    private static ComboBox NewDarkCombo(double width = 0)
    {
        var combo = new ComboBox
        {
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x26, 0x23, 0x4E)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
            BorderThickness = new Thickness(1),
            FontSize = 12,
            Padding = new Thickness(6, 3, 6, 3),
            VerticalAlignment = VerticalAlignment.Center
        };
        if (Application.Current?.TryFindResource("GoldComboBox") is Style comboStyle)
            combo.Style = comboStyle;
        if (Application.Current?.TryFindResource("GoldComboItem") is Style itemStyle)
            combo.ItemContainerStyle = itemStyle;
        if (width > 0) combo.Width = width;
        return combo;
    }

    /// <summary>
    /// 「完成后动作」两层选项 UI 块（上线锄地绑定弹窗用，代码构建深色风格）：
    /// 第一层「恢复任务（默认）｜执行动作」；选「执行动作」展开第二层「停止｜执行指定任务」；
    /// 指定任务 = 类型（配置组/一条龙）+ 名称（候选来自对应范围的配置清单，可手动输入；执行时校验存在性）。
    /// 映射：恢复任务=Resume、执行动作+停止=Stop、执行动作+执行指定任务=RunSpecified。
    /// </summary>
    private sealed class CompletionActionPanel
    {
        private readonly ComboBox _layer1Combo;    // 0=恢复任务 1=执行动作
        private readonly ComboBox _layer2Combo;    // 0=停止 1=执行指定任务
        private readonly ComboBox _typeCombo;      // 0=配置组 1=一条龙
        private readonly ComboBox _nameCombo;      // IsEditable，候选随类型切换
        private readonly StackPanel _layer2Panel;
        private readonly StackPanel _specifiedPanel;
        private readonly StackPanel _root;
        private readonly List<string> _groups;
        private readonly List<string> _oneClicks;
        private bool _initializing = true;

        public UIElement Root => _root;

        public CompletionActionPanel(TaskConflictPolicy policy, string specifiedType, string specifiedName,
            List<string> groups, List<string> oneClicks, string description)
        {
            _groups = groups;
            _oneClicks = oneClicks;
            var gold = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D));
            var dim = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0));

            _root = new StackPanel { Margin = new Thickness(0, 10, 0, 0) };
            var border = new Border
            {
                CornerRadius = new CornerRadius(8),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E)),
                Padding = new Thickness(10, 8, 10, 10)
            };
            _root.Children.Add(border);
            var inner = new StackPanel();
            border.Child = inner;

            inner.Children.Add(new TextBlock
            {
                Text = "完成后动作",
                FontSize = 12, FontWeight = FontWeights.SemiBold, Foreground = gold
            });
            inner.Children.Add(new TextBlock
            {
                Text = description,
                FontSize = 10, Foreground = dim, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 2, 0, 8)
            });

            // 第一层：恢复任务（默认）｜执行动作
            _layer1Combo = NewDarkCombo();
            _layer1Combo.Items.Add("恢复任务");
            _layer1Combo.Items.Add("执行动作");
            inner.Children.Add(_layer1Combo);

            // 第二层（选「执行动作」时显示）：停止｜执行指定任务
            _layer2Panel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            _layer2Combo = NewDarkCombo();
            _layer2Combo.Items.Add("停止");
            _layer2Combo.Items.Add("执行指定任务");
            _layer2Panel.Children.Add(_layer2Combo);

            // 指定任务行（选「执行指定任务」时显示）：类型 + 名称（可手动输入）
            _specifiedPanel = new StackPanel { Margin = new Thickness(0, 8, 0, 0) };
            var specifiedRow = new Grid();
            specifiedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            specifiedRow.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
            _typeCombo = NewDarkCombo(96);
            _typeCombo.Margin = new Thickness(0, 0, 8, 0);
            _typeCombo.Items.Add("配置组");
            _typeCombo.Items.Add("一条龙");
            Grid.SetColumn(_typeCombo, 0);
            specifiedRow.Children.Add(_typeCombo);
            _nameCombo = NewDarkCombo();
            _nameCombo.IsEditable = true; // BGI 未启动时允许手动输入；执行时校验存在性，不存在退化为停止
            Grid.SetColumn(_nameCombo, 1);
            specifiedRow.Children.Add(_nameCombo);
            _specifiedPanel.Children.Add(specifiedRow);
            _specifiedPanel.Children.Add(new TextBlock
            {
                Text = "候选来自该成员的 BGI 配置清单；未获取到时可直接手动输入。执行时校验存在性，不存在退化为停止并打日志。",
                FontSize = 10, Foreground = dim, TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 0)
            });
            _layer2Panel.Children.Add(_specifiedPanel);
            inner.Children.Add(_layer2Panel);

            // 初始值
            _layer1Combo.SelectedIndex = policy == TaskConflictPolicy.Resume ? 0 : 1;
            _layer2Combo.SelectedIndex = policy == TaskConflictPolicy.RunSpecified ? 1 : 0;
            _typeCombo.SelectedIndex = specifiedType == "onedragon" ? 1 : 0;
            RefreshNameCandidates();
            _nameCombo.Text = specifiedName;
            _layer2Panel.Visibility = _layer1Combo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            _specifiedPanel.Visibility = _layer2Combo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;

            _layer1Combo.SelectionChanged += (_, _) =>
            {
                if (_initializing) return;
                _layer2Panel.Visibility = _layer1Combo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            };
            _layer2Combo.SelectionChanged += (_, _) =>
            {
                if (_initializing) return;
                _specifiedPanel.Visibility = _layer2Combo.SelectedIndex == 1 ? Visibility.Visible : Visibility.Collapsed;
            };
            _typeCombo.SelectionChanged += (_, _) =>
            {
                if (_initializing) return;
                var current = _nameCombo.Text;
                RefreshNameCandidates();
                // 类型切换后名称候选随之切换；原名称不在新候选中时保留文本（允许手动输入的名字跨类型保留由用户决定）
                _nameCombo.Text = current;
            };
            _initializing = false;
        }

        private void RefreshNameCandidates()
        {
            var candidates = _typeCombo.SelectedIndex == 1 ? _oneClicks : _groups;
            _nameCombo.Items.Clear();
            foreach (var name in candidates)
                _nameCombo.Items.Add(name);
        }

        /// <summary>当前选择的策略（恢复任务=Resume / 执行动作+停止=Stop / 执行动作+执行指定任务=RunSpecified）。</summary>
        public TaskConflictPolicy Policy => _layer1Combo.SelectedIndex != 1
            ? TaskConflictPolicy.Resume
            : _layer2Combo.SelectedIndex == 1 ? TaskConflictPolicy.RunSpecified : TaskConflictPolicy.Stop;

        /// <summary>指定任务类型："group"=配置组，"onedragon"=一条龙。</summary>
        public string SpecifiedType => _typeCombo.SelectedIndex == 1 ? "onedragon" : "group";

        /// <summary>指定任务名称（手动输入或候选选择）。</summary>
        public string SpecifiedName => _nameCombo.Text.Trim();
    }

    /// <summary>完全退出助手软件（先弹确认框）。</summary>
    private void OnExit()
    {
        var result = MessageBox.Show("是否完全退出 Nexus-BGI 联机助手？", "退出确认",
            MessageBoxButton.YesNo, MessageBoxImage.Question);
        if (result == MessageBoxResult.Yes)
        {
            Application.Current.Shutdown();
        }
    }

    /// <summary>
    /// 应用完全退出时集中释放后台资源，避免进程残留（任务管理器里 MultiplayerHoeingAssistant.exe 反复存留）。
    /// Application.Shutdown() 会同步触发 App.OnExit，再由 App 在此调用本方法：
    /// 逐项断开 SignalR 连接、停止全部业务定时器、释放进程监控与命令执行器。
    /// 仅新增清理逻辑，不改变进程退出前任何现有功能行为（对运行中状态零影响）。
    /// </summary>
    public void Shutdown()
    {
        // 断开 SignalR 连接（HubConnection 未 Dispose 会持有网络连接/心跳定时资源）
        var signalR = _signalRClient;
        _signalRClient = null;
        if (signalR != null)
        {
            // OnExit 是同步回调，无法阻塞等待；以 fire-and-forget 异步断开。
            // 内部已 try-catch 观察异常，避免退出路径产生未观察任务异常触发全局弹窗。
            _ = ReleaseSignalRAsync(signalR);
        }

        // 停止全部业务定时器（状态上报 10s / 首次连接失败重试 10s / 定时上线 30s / 恢复原任务 10s / [冷却倒计时] 防抖倒计时 1s）
        _statusTimer?.Dispose();
        _statusTimer = null;
        _retryTimer?.Dispose();
        _retryTimer = null;
        _onlineTimer?.Dispose();
        _onlineTimer = null;
        _resumeTimeoutTimer?.Dispose();
        _resumeTimeoutTimer = null;
        StopOnlineDebounceCountdown();
        _debounceCountdownTimer = null;

        // 释放进程监控（内部 5 秒守护 Timer）与命令执行器（依赖进程监控）
        _processMonitor?.Dispose();
        _processMonitor = null;
        _commandExecutor = null;

        // [切片1] 释放 ext.event 事件通道（命名管道 + 内部重连循环）
        _externalClient?.Dispose();
        _externalClient = null;
    }

    /// <summary>后台异步断开 SignalR 连接；退出路径异常仅写日志，不影响进程退出。</summary>
    private static async Task ReleaseSignalRAsync(SignalRClient client)
    {
        try
        {
            await client.DisposeAsync();
        }
        catch (Exception ex)
        {
            // 退出路径：连接关闭失败不影响进程退出，仅记录到助手运行日志
            try
            {
                var logDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "log");
                System.IO.Directory.CreateDirectory(logDir);
                var logPath = System.IO.Path.Combine(logDir, $"assistant_runtime.{DateTime.Now:yyyy-MM-dd}.s{System.Diagnostics.Process.GetCurrentProcess().SessionId}.log");
                System.IO.File.AppendAllText(logPath,
                    $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] [EXIT_CLEANUP] SignalR 断开失败: {ex.Message}\n");
            }
            catch
            {
                // 日志写入失败不影响退出
            }
        }
    }

    /// <summary>
    /// 刷新助手所有状态：断开并重建 SignalR 连接（重新加入控制房间，服务端会重新广播所有
    /// 玩家最新状态 → OnPlayersUpdated 刷新成员列表），等效于"重新加载页面"，用于界面异常时恢复。
    /// 刷新期间保留本机配置、本机命令执行器与连接参数（ServerUrl/房间码/密码/UID/队UID）。
    /// </summary>
    private async Task RefreshAsync()
    {
        // 防重入：连点"刷新房间"时后到的刷新会把先到的刚建好的连接 Dispose 掉（详见 _refreshRunning 注释）
        if (Interlocked.CompareExchange(ref _refreshRunning, 1, 0) != 0) return;
        // 半成品客户端（StartAsync 已成功但 JoinControlRoom 抛异常 → 挂着一条活连接）必须在失败路径销毁，
        // 否则每轮失败刷新/重试都泄漏一条幽灵连接占用服务端 CTRL_ 组
        SignalRClient? pendingClient = null;
        try
        {
            AddLog("正在刷新助手状态...");
            // 1. 停止旧的定时任务（状态上报 Timer 与首次连接失败的重试 Timer），避免与重建后的重复上报。
            _statusTimer?.Dispose();
            _statusTimer = null;
            _retryTimer?.Dispose();
            _retryTimer = null;

            // 2. 断开当前 SignalR 连接（DisposeAsync 会置 _disposed=true，同时也终止内置自动重连/自愈循环）。
            var old = _signalRClient;
            _signalRClient = null;
            if (old != null)
            {
                await old.DisposeAsync();
            }
            IsConnected = false;

            // 3. 用同一份连接参数重建连接（重新加入房间 → 服务端重新广播所有玩家最新状态）。
            if (_config != null)
            {
                pendingClient = new SignalRClient();
                WireSignalRClient(pendingClient);
                pendingClient.OnConnectionStateChanged += connected =>
                {
                    Application.Current.Dispatcher.Invoke(() => IsConnected = connected);
                    // 连接恢复后立即上报状态，无需等待 10 秒定时器
                    if (connected) _ = ReportStatusAsync();
                    // [B157] 断线时服务端已清零本成员上线事件，重连后补报上线意图（服务端按 gen 去重，安全）
                    if (connected) _ = ReReportOnlineIntentIfNeededAsync();
                };
                await pendingClient.ConnectAsync(
                    _config.ServerUrl, RoomCode, _config.ControlRoomPassword,
                    _config.PlayerUid, _config.PlayerName, _config.TeamUids, _config.ObserverMode, _config.ClientInstanceId,
                    _config.BypassSystemProxy);
                _signalRClient = pendingClient;
                pendingClient = null; // 所有权已移交 _signalRClient
                IsConnected = true;
                AddLog("刷新完成，已重新建立连接");
                await ReportStatusAsync();
                RefreshCompleted?.Invoke();
                _statusTimer = new Timer(async _ => await ReportStatusAsync(), null,
                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            }
        }
        catch (Exception ex)
        {
            if (pendingClient != null)
            {
                try { await pendingClient.DisposeAsync(); } catch { /* 销毁失败不影响重试 */ }
            }
            AddLog($"刷新失败: {ex.Message}");
            // 刷新失败时尽量保持可连接：若尚未成功重建，回到 10 秒重试（复用现有重试分支逻辑）
            if (_signalRClient == null)
            {
                _retryTimer = new Timer(async _ =>
                {
                    // 上次重建的 StartAsync 可能还挂着（假死服务器协商超时 ~100s），不叠加（详见 _retryRunning 注释）
                    if (Interlocked.CompareExchange(ref _retryRunning, 1, 0) != 0) return;
                    SignalRClient? client = null;
                    try
                    {
                        if (_signalRClient == null && _config != null)
                        {
                            client = new SignalRClient();
                            WireSignalRClient(client);
                            // 与主路径一致：连接状态变化同步标题栏徽章，恢复后立即上报
                            client.OnConnectionStateChanged += connected =>
                            {
                                Application.Current.Dispatcher.Invoke(() => IsConnected = connected);
                                if (connected) _ = ReportStatusAsync();
                                // [B157] 断线时服务端已清零本成员上线事件，重连后补报上线意图（服务端按 gen 去重，安全）
                                if (connected) _ = ReReportOnlineIntentIfNeededAsync();
                            };
                            await client.ConnectAsync(
                                _config.ServerUrl, RoomCode, _config.ControlRoomPassword,
                                _config.PlayerUid, _config.PlayerName, _config.TeamUids, _config.ObserverMode, _config.ClientInstanceId,
                                _config.BypassSystemProxy);
                            if (_signalRClient != null)
                            {
                                // 本次 StartAsync 在途期间（最长 ~100s）用户又点了刷新并已重建成功——
                                // 不能接管 _signalRClient（会把刷新成果挤成无人持有的幽灵连接），
                                // 本次成果交 finally 销毁，后续状态由在位的连接负责
                                return;
                            }
                            _signalRClient = client;
                            client = null; // 所有权已移交 _signalRClient
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                IsConnected = true;
                                AddLog("已重新连接控制房间");
                            });
                            RefreshCompleted?.Invoke();
                            if (_statusTimer == null)
                            {
                                _statusTimer = new Timer(async _2 => await ReportStatusAsync(), null,
                                    TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
                            }
                            _ = ReportStatusAsync();
                            _retryTimer?.Dispose();
                            _retryTimer = null;
                        }
                    }
                    catch (Exception retryEx)
                    {
                        // 重试失败保持定时器继续，但失败原因必须可见——"连不回来"时靠它区分
                        // 服务器不可达（网络/DNS/服务挂）与服务器拒绝（密码/白名单/协议不兼容）
                        AddLog($"重连失败: {retryEx.Message}（10 秒后继续）");
                    }
                    finally
                    {
                        // 重建失败（含 StartAsync 成功但入房抛异常）的半成品客户端必须销毁，防幽灵连接泄漏
                        if (client != null)
                        {
                            try { await client.DisposeAsync(); } catch { /* 销毁失败不影响下一轮重试 */ }
                        }
                        _retryRunning = 0;
                    }
                }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            }
        }
        finally
        {
            _refreshRunning = 0;
        }
    }

    /// <summary>把 SignalR 的成员/命令/入房被拒事件接到 MainViewModel（在重建连接时复用同一组订阅逻辑）。</summary>
    private void WireSignalRClient(SignalRClient client)
    {
        // 绑定日志回调（探针日志输出到助手界面）
        client.OnLog = msg => Application.Current.Dispatcher.Invoke(() => AddLog(msg));

        client.OnPlayersUpdated += update =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                // 单成员合并/新增：已存在按 uid 原地逐字段更新，不存在则轮换头像新增到末尾。
                void UpsertMember(ControlRoomPlayer np)
                {
                    var existing = Members.FirstOrDefault(m => m.PlayerUid == np.PlayerUid);
                    if (existing != null)
                    {
                        existing.PlayerName = np.PlayerName;
                        existing.Online = np.Online;
                        existing.BgiStatus = np.BgiStatus;
                        existing.ConfigGroups = np.ConfigGroups;
                        existing.OneClickConfigs = np.OneClickConfigs;
                        existing.AutoHoeingRunning = np.AutoHoeingRunning;
                        existing.AutoHoeingProgress = np.AutoHoeingProgress;
                        existing.TaskRunning = np.TaskRunning;
                        existing.CurrentTaskName = np.CurrentTaskName;
                        existing.CurrentTaskGroupName = np.CurrentTaskGroupName;
                        existing.CurrentRouteDisplay = np.CurrentRouteDisplay;
                        existing.CurrentScriptRouteName = np.CurrentScriptRouteName;
                        existing.Hotkeys = np.Hotkeys;
                        existing.ConfigGroupTasksWithStatus = np.ConfigGroupTasksWithStatus;
                        existing.OneClickTasksWithStatus = np.OneClickTasksWithStatus;
                        existing.OnlineReady = np.OnlineReady;
                        existing.OnlineMode = np.OnlineMode;
                        existing.ScheduledOnlineTime = np.ScheduledOnlineTime;
                        existing.OnlineHoeingGroupNames = np.OnlineHoeingGroupNames ?? [];
                        existing.QuickCommands = np.QuickCommands ?? new();
                        existing.OnlineHistory = np.OnlineHistory;
                        return;
                    }
                    var (file, ring) = AvatarPool[Members.Count % AvatarPool.Length];
                    Members.Add(new MemberViewModel
                    {
                        PlayerUid = np.PlayerUid,
                        PlayerName = np.PlayerName,
                        IsSelf = np.PlayerUid == _config?.PlayerUid,
                        Online = np.Online,
                        BgiStatus = np.BgiStatus,
                        ConfigGroups = np.ConfigGroups,
                        OneClickConfigs = np.OneClickConfigs,
                        AutoHoeingRunning = np.AutoHoeingRunning,
                        AutoHoeingProgress = np.AutoHoeingProgress,
                        TaskRunning = np.TaskRunning,
                        CurrentTaskName = np.CurrentTaskName,
                        CurrentTaskGroupName = np.CurrentTaskGroupName,
                        CurrentRouteDisplay = np.CurrentRouteDisplay,
                        CurrentScriptRouteName = np.CurrentScriptRouteName,
                        Hotkeys = np.Hotkeys,
                        ConfigGroupTasksWithStatus = np.ConfigGroupTasksWithStatus,
                        OneClickTasksWithStatus = np.OneClickTasksWithStatus,
                        OnlineReady = np.OnlineReady,
                        OnlineMode = np.OnlineMode,
                        ScheduledOnlineTime = np.ScheduledOnlineTime,
                        // 新建分支必须与上方更新分支字段对齐：增量广播下状态不变的成员不会再被下发，
                        // 这里漏掉的字段（曾漏 OnlineHoeingGroupNames/QuickCommands）会永远停在默认值，
                        // 表现为"绑定弹窗看不到别人的已选配置组"（服务端数据是真的，本端 Add 路径丢了）。
                        OnlineHoeingGroupNames = np.OnlineHoeingGroupNames ?? [],
                        QuickCommands = np.QuickCommands ?? new(),
                        OnlineHistory = np.OnlineHistory,
                        AvatarPath = $"pack://application:,,,/Assets/Images/{file}.png",
                        AvatarRing = ring,
                        IsSelected = true
                    });
                }

                if (update.Full)
                {
                    // 全量：不在服务端列表中的成员移除；其余按服务端广播顺序 upsert
                    // （已存在成员原地更新保持显示位置，新成员按服务端顺序追加到末尾，
                    //  确保显示顺序与加入顺序一致）。
                    var keep = new HashSet<string>((update.Players ?? []).Select(p => p.PlayerUid));
                    for (int i = Members.Count - 1; i >= 0; i--)
                    {
                        if (!keep.Contains(Members[i].PlayerUid))
                        {
                            Members.RemoveAt(i);
                        }
                    }
                    foreach (var p in update.Players ?? [])
                    {
                        UpsertMember(p);
                    }
                }
                else
                {
                    // 增量：changed 逐个 upsert，removed 按 uid 移除；
                    // 不在 changed/removed 中的成员保持不变，不做全量 reconcile。
                    foreach (var p in update.Changed ?? [])
                    {
                        UpsertMember(p);
                    }
                    if (update.Removed is { Count: > 0 })
                    {
                        var removedSet = new HashSet<string>(update.Removed);
                        for (int i = Members.Count - 1; i >= 0; i--)
                        {
                            if (removedSet.Contains(Members[i].PlayerUid))
                            {
                                Members.RemoveAt(i);
                            }
                        }
                    }
                }

                // 监控端：检测执行端（同 UID 的成员）联机锄地进度变化，输出到冒险日志
                if (_config?.ObserverMode == true)
                {
                    var execMember = Members.FirstOrDefault(m => m.PlayerUid == _config?.PlayerUid);
                    if (execMember?.AutoHoeingProgress != null
                        && execMember.AutoHoeingProgress != _lastLoggedProgress)
                    {
                        _lastLoggedProgress = execMember.AutoHoeingProgress;
                        AddLog(execMember.AutoHoeingProgress);
                    }
                }

                // 成员 Online 状态是原地更新（不触发 CollectionChanged），这里统一通知一次，
                // 让 DodocoViewModel 重新评估日志订阅（离线退订 / 上线重订）。
                MemberOnlineChanged?.Invoke();
            });
        };

        client.OnAllReadyConfirmed += generation =>
        {
            _ = OnAllReadyConfirmedInternal(generation);
        };

        client.OnAllReadyConfirmReceived += async generation =>
        {
            // [实机修复] 确认回执永远先回：服务端在等 ack，不回会 30s×3 超时后整轮放弃开锄。
            // 服务端 RegisterConfirmAck 已按"当前轮次 generation + confirming 状态"校验，过期 ack 安全丢弃。
            // 历史 bug：先查 AllReady 执行守卫再回执，守卫被旧轮次冲高后
            // 确认被静默丢弃 → 服务端永远等不到 ack → 不开锄。
            if (_signalRClient != null)
            {
                await _signalRClient.ConfirmAllReadyAsync(generation);
            }
            // 是否真正执行仍受 generation 守卫（OnAllReadyConfirmedInternal 内部还有二次守卫+互斥锁）
            if (generation <= _intentLifecycle.LastProcessedAllReadyGeneration)
            {
                return;
            }
            await OnAllReadyConfirmedInternal(generation);
        };

        client.OnAllReadyAbort += (generation, targetUids) =>
        {
            // [B157] 服务端确认超时放弃本轮（缺人不开锄）：仅当本端是本轮参与者才复位本地"已上线"。
            // 旧实现服务端耗尽后无任何通知，_isOnlineReady 永久残留，后续命令上线全被客户端守卫吞掉。
            if (!targetUids.Contains(_config?.PlayerUid)) return;
            // [P1] 复位+清挂起补报收口进状态机；本就不在上线状态时无需处理
            if (!_intentLifecycle.OnAllReadyAborted()) return;
            AddLog($"本轮联机开锄未凑齐确认（generation={generation}），服务端已放弃本轮；可重新执行\"联机锄地上线\"");
            _ = ReportStatusAsync();
        };

        client.OnRemoteCommand += async cmd =>
        {
            if (cmd.Cmd == "ack")
            {
                // 显示 ack 确认日志（不执行、不回 ack，阻断循环）
                var msg = cmd.Params?.GetValueOrDefault("message")?.ToString() ?? "";
                AddLog($"确认: {cmd.Sender} - {msg}");
                return;
            }

            // ===== 远程配置组编辑（契约见 Docs/远程配置组编辑-实施方案.md §1）：4 个新 Cmd =====
            // remote_config.data / remote_config.push_result：转给远程编辑会话状态机（不刷日志，结果由流程方法统一报）
            if (cmd.Cmd is "remote_config.data" or "remote_config.push_result")
            {
                if (_remoteConfigEditService == null || !_remoteConfigEditService.TryComplete(cmd.CommandId, cmd))
                {
                    // 超时后迟到/重复回复/无进行中会话：记一行日志便于排查（不影响主流程）
                    AddLog($"收到迟到或无法关联的远程配置回复（{cmd.Cmd}，CommandId={cmd.CommandId}，来自 {cmd.Sender}），已忽略");
                }
                return;
            }

            // remote_config.pull：对方请求拉取本机某个配置组 → IPC config.pull_group → 回 remote_config.data
            if (cmd.Cmd == "remote_config.pull")
            {
                await HandleRemoteConfigPullAsync(cmd);
                return;
            }

            // remote_config.push：对方回传编辑后的配置 → IPC config.apply_group → 回 remote_config.push_result
            if (cmd.Cmd == "remote_config.push")
            {
                await HandleRemoteConfigPushAsync(cmd);
                return;
            }

            // ===== 任务策略远程互改（task_policy.*，模式复刻 remote_config.*，服务器零改动）=====
            // task_policy.data / task_policy.push_result：转给策略同步会话状态机（按 CommandId 关联 TCS）
            if (cmd.Cmd is "task_policy.data" or "task_policy.push_result")
            {
                if (_taskPolicySync == null || !_taskPolicySync.TryComplete(cmd.CommandId, cmd))
                {
                    AddLog($"收到迟到或无法关联的任务策略回复（{cmd.Cmd}，CommandId={cmd.CommandId}，来自 {cmd.Sender}），已忽略");
                }
                return;
            }

            // task_policy.pull：对方请求拉取本机上线锄地「完成后动作」配置 → 回 task_policy.data
            if (cmd.Cmd == "task_policy.pull")
            {
                await HandleTaskPolicyPullAsync(cmd);
                return;
            }

            // task_policy.push：对方回传编辑后的上线锄地「完成后动作」→ 应用并落盘 → 回 task_policy.push_result
            if (cmd.Cmd == "task_policy.push")
            {
                await HandleTaskPolicyPushAsync(cmd);
                return;
            }

            // bind_hoeing_group 命令：直接在助手本地处理，不走 BGI IPC
            if (cmd.Cmd == "bind_hoeing_group")
            {
                var groupNames = cmd.Params?.GetValueOrDefault("groupNames");
                if (groupNames is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var names = je.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                    // 读取类型列表（兼容旧命令不传 groupTypes）
                    var types = new List<string>();
                    if (cmd.Params?.TryGetValue("groupTypes", out var typesObj) == true
                        && typesObj is System.Text.Json.JsonElement typesJe
                        && typesJe.ValueKind == System.Text.Json.JsonValueKind.Array)
                    {
                        types = typesJe.EnumerateArray().Select(e => e.GetString() ?? "group").ToList();
                    }
                    // 类型列表长度不够时，默认全部为 "group"（兼容旧数据）
                    while (types.Count < names.Count) types.Add("group");
                    if (names.Count > 0 && _config != null)
                    {
                        _config.OnlineHoeingGroupNames = names;
                        _config.OnlineHoeingGroupTypes = types;
                        _config.OnlineHoeingGroupIndex = 0;
                        _configManager?.Save(_config);
                        AddLog($"收到绑定联机锄地配置组: {string.Join(", ", names)}（来自 {cmd.Sender}）");
                        await ReportStatusAsync();
                        await SendAckAsync(cmd, "success", $"已绑定: {string.Join(", ", names)}");
                    }
                    else
                    {
                        await SendAckAsync(cmd, "failed", "配置组列表为空或配置不可用");
                    }
                }
                else
                {
                    await SendAckAsync(cmd, "failed", "解析配置组列表失败");
                }
                return;
            }

            // set_quick_command 命令：接收端保存快捷指令绑定到本地并持久化
            if (cmd.Cmd == "set_quick_command")
            {
                var key = cmd.Params?.GetValueOrDefault("key")?.ToString();
                var value = cmd.Params?.GetValueOrDefault("value")?.ToString();
                if (!string.IsNullOrEmpty(key) && !string.IsNullOrEmpty(value) && _config != null)
                {
                    _config.QuickCommands[key] = value;
                    _configManager?.Save(_config);
                    var isOneClick = value.StartsWith("ONEDRAGON:");
                    var displayValue = isOneClick ? value["ONEDRAGON:".Length..] : value["GROUP:".Length..];
                    AddLog($"收到绑定 {key}: {(isOneClick ? "一条龙" : "配置组")}「{displayValue}」（来自 {cmd.Sender}）");
                    await SendAckAsync(cmd, "success", $"{key} 已绑定: {value}");
                }
                else
                {
                    await SendAckAsync(cmd, "failed", "参数不完整或配置不可用");
                }
                return;
            }

            // set_scheduled_online_time 命令：接收端保存定时上线时间到本地并重启定时器
            if (cmd.Cmd == "set_scheduled_online_time")
            {
                // 注意：SignalR 反序列化 Dictionary<string,object> 的 value 是 JsonElement 而非 string，
                // 必须兼容 JsonElement / string / null，否则 as string 得到 null → 被误判为"清除"。
                object? tv = cmd.Params?.GetValueOrDefault("scheduledOnlineTime");
                var timeStr = tv switch
                {
                    null => "",
                    string s => s,
                    System.Text.Json.JsonElement je when je.ValueKind == System.Text.Json.JsonValueKind.String => je.GetString() ?? "",
                    _ => tv.ToString() ?? ""
                };
                if (_config != null)
                {
                    // 空串 = 清除定时上线；非空 = 设定
                    ApplyScheduledOnlineTime(timeStr);
                    AddLog($"收到远程定时上线时间:{(string.IsNullOrEmpty(timeStr) ? "（已清除）" : $" {timeStr}")}（来自 {cmd.Sender}）");
                    await SendAckAsync(cmd, "success", string.IsNullOrEmpty(timeStr) ? "已清除定时上线" : $"已设定定时上线: {timeStr}");
                }
                else
                {
                    await SendAckAsync(cmd, "failed", "配置不可用");
                }
                return;
            }

            // clear_online 命令：清除该成员的已上线状态（定时触发或命令触发产生的；不清除定时闹钟）
            if (cmd.Cmd == "clear_online")
            {
                if (_config != null)
                {
                    await ClearLocalOnline();
                    AddLog($"收到远程清除上线（来自 {cmd.Sender}）");
                    await SendAckAsync(cmd, "success", "已清除上线");
                }
                else
                {
                    await SendAckAsync(cmd, "failed", "配置不可用");
                }
                return;
            }

            // 快捷命令：若 Params 带 key，则用 key 查自己绑定的配置组/一条龙，替换传下来的值
            // 这样每个队友执行的是自己绑定的配置，而不是房主绑定的。
            if (cmd.Cmd is "start_group" or "start_oneclick"
                && cmd.Params?.ContainsKey("key") == true
                && _config != null)
            {
                var key = cmd.Params["key"]?.ToString();

                // 特殊处理"一键锄地"：key="一键锄地" 时，遍历自己的 OnlineHoeingGroupNames 依次执行
                if (key == "一键锄地")
                {
                    var groupNames = _config.OnlineHoeingGroupNames ?? [];
                    if (groupNames.Count == 0)
                    {
                        AddLog("收到一键锄地，但未绑定联机锄地配置组，跳过");
                        return;
                    }
                    AddLog($"收到一键锄地，开始执行本地绑定的 {groupNames.Count} 个配置组...");
                    // [批次标记] 每次新的一键锄地下发都是新批次：循环开始前重置回退标记，
                    // 避免上一批次 KillBgi+RestartBgi 回退残留的 _hasRestartedThisBatch
                    // 导致本批次所有配置组被误判为"同批次后续组"而跳过 IPC（BGI 侧命令行串行早已结束/被 F11 停止）。
                    // 循环内部不重置：多配置组共用一次回退重启的保护仍有效。
                    _commandExecutor?.ResetBatch();
                    foreach (var groupName in groupNames)
                    {
                        if (_commandExecutor == null) break;
                        var hoeingCmd = new RemoteCommand
                        {
                            Cmd = "start_group",
                            Params = new Dictionary<string, object>
                            {
                                ["groupName"] = groupName,
                                ["startFromIndex"] = 0,
                                ["batchGroupNames"] = string.Join(",", groupNames)
                            }
                        };
                        var result = await _commandExecutor.ExecuteAsync(hoeingCmd);
                        if (result.Status == "cancelled") break;
                        AddLog($"  - 执行配置组「{groupName}」: {result.Status}");
                    }
                    AddLog("一键锄地执行完毕");
                    return;
                }

                if (!string.IsNullOrEmpty(key) && _config.QuickCommands.TryGetValue(key, out var localBinding) && !string.IsNullOrEmpty(localBinding))
                {
                    var isOneClick = localBinding.StartsWith("ONEDRAGON:");
                    var localValue = isOneClick ? localBinding["ONEDRAGON:".Length..] : localBinding["GROUP:".Length..];
                    cmd.Params[isOneClick ? "configName" : "groupName"] = localValue;
                    AddLog($"已替换为本地绑定: {key} → {(isOneClick ? "一条龙" : "配置组")}「{localValue}」");
                }
                else
                {
                    // 未绑定本地快捷命令，回退到房主传下来的值执行
                    AddLog($"本地未绑定{key}，回退到房主传下来的值执行");
                }
            }

            if (_commandExecutor != null)
            {
                // [批次标记] 每次新的远程 start_group/start_oneclick 下发都是新批次：重置回退标记，
                // 避免上一批次回退残留的 _hasRestartedThisBatch 把本次启动吞掉（静默不执行）。
                if (cmd.Cmd is "start_group" or "start_oneclick")
                {
                    _commandExecutor.ResetBatch();
                }
                var result = await _commandExecutor.ExecuteAsync(cmd);
                await SendAckAsync(cmd, result.Status, result.Message);
            }
        };

        client.OnJoinRejected += reason =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                MessageBox.Show($"加入控制房间失败: {reason}");
                if (_config != null && _configManager != null)
                {
                    _config.ControlRoomPassword = "";
                    _configManager.Save(_config);
                }
            });
        };
    }

    private string? ShowHotkeySelectDialog(List<object> hotkeys)
    {
        if (hotkeys.Count == 0) return null;

        var names = new List<string>();
        foreach (var hk in hotkeys)
        {
            if (hk is System.Text.Json.JsonElement je)
            {
                var configName = je.TryGetProperty("configName", out var cn) ? cn.GetString() ?? "" : "";
                var funcName = je.TryGetProperty("functionName", out var fn) ? fn.GetString() ?? "" : "";
                var hotkeyText = je.TryGetProperty("hotkeyText", out var ht) ? ht.GetString() ?? "" : "";
                names.Add($"{funcName} ({hotkeyText})");
            }
        }

        var dialog = new Window
        {
            Title = "选择快捷键",
            Width = 440, Height = 400,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock
        {
            Text = "选择要执行的快捷键：",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            Margin = new Thickness(0, 0, 0, 10)
        });
        var listBox = new ListBox
        {
            Height = 250,
            ItemsSource = names,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
            BorderThickness = new Thickness(1),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA)),
            FontSize = 13,
            ItemContainerStyle = CreateDarkListBoxItemStyle()
        };
        listBox.SelectedIndex = 0;
        stack.Children.Add(listBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 90, Height = 32, Margin = new Thickness(0, 0, 10, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Cursor = System.Windows.Input.Cursors.Hand };
        // 鎏金渐变执行按钮
        var okBtn = new Button { Content = "执行", Width = 90, Height = 32,
            FontWeight = FontWeights.SemiBold,
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        okBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        okBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(okBtn);
        stack.Children.Add(btnPanel);
        dialog.Content = stack;

        string? result = null;
        okBtn.Click += (_, _) =>
        {
            if (listBox.SelectedIndex >= 0 && listBox.SelectedIndex < hotkeys.Count)
            {
                if (hotkeys[listBox.SelectedIndex] is System.Text.Json.JsonElement je)
                    result = je.TryGetProperty("configName", out var cn) ? cn.GetString() : null;
            }
            dialog.DialogResult = true;
        };
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;
        return dialog.ShowDialog() == true ? result : null;
    }

    private void OnStartGroup(object? parameter)
    {
        if (parameter is MemberViewModel member)
        {
            // 打开选择配置组对话框
            var groupName = ShowConfigSelectionDialog("配置组", member.ConfigGroups, member);
            if (string.IsNullOrEmpty(groupName)) return;

            // 从本机 BGI 读取该配置组的任务列表（联机场景 4 台配置通常一致）
            _ = StartGroupWithTaskListAsync(groupName, member);
        }
    }

    private void OnStartOneClick(object? parameter)
    {
        if (parameter is MemberViewModel member)
        {
            var configName = ShowConfigSelectionDialog("一条龙", member.OneClickConfigs, member);
            if (string.IsNullOrEmpty(configName)) return;

            _ = StartOneClickWithTaskListAsync(configName, member);
        }
    }

    private async Task StartGroupWithTaskListAsync(string groupName, MemberViewModel member)
    {
        try
        {
            var (taskList, tasksWithStatus) = await GetLocalTaskListAsync(groupName, isOneClick: false);
            var startFrom = ShowStartFromDialog(groupName, taskList, isOneClick: false, tasksWithStatus: tasksWithStatus, targetUids: [member.PlayerUid]);
            if (startFrom == null) return; // 用户取消
            _ = ExecuteLocalCommandAsync("start_group",
                new Dictionary<string, object> { { "groupName", groupName }, { "startFromIndex", startFrom.Value } },
                [member.PlayerUid]);
        }
        catch (Exception ex)
        {
            AddLog($"读取配置组任务列表失败: {ex.Message}");
            var startFrom = ShowStartFromDialog(groupName, null);
            if (startFrom == null) return; // 用户取消
            _ = ExecuteLocalCommandAsync("start_group",
                new Dictionary<string, object> { { "groupName", groupName }, { "startFromIndex", startFrom.Value } },
                [member.PlayerUid]);
        }
    }

    private async Task StartOneClickWithTaskListAsync(string configName, MemberViewModel member)
    {
        try
        {
            var (taskList, tasksWithStatus) = await GetLocalTaskListAsync(configName, isOneClick: true);
            var startFrom = ShowStartFromDialog(configName, taskList, isOneClick: true, tasksWithStatus: tasksWithStatus, targetUids: [member.PlayerUid]);
            if (startFrom == null) return; // 用户取消
            _ = ExecuteLocalCommandAsync("start_oneclick",
                new Dictionary<string, object> { { "configName", configName }, { "startFromIndex", startFrom.Value } },
                [member.PlayerUid]);
        }
        catch (Exception ex)
        {
            AddLog($"读取一条龙任务列表失败: {ex.Message}");
            var startFrom = ShowStartFromDialog(configName, null);
            if (startFrom == null) return; // 用户取消
            _ = ExecuteLocalCommandAsync("start_oneclick",
                new Dictionary<string, object> { { "configName", configName }, { "startFromIndex", startFrom.Value } },
                [member.PlayerUid]);
        }
    }

    /// <summary>
    /// 从配置标签点击直接启动（跳过下拉选择框，直接弹"从此处开始执行"）。
    /// </summary>
    public async Task StartGroupFromConfigAsync(MemberViewModel member, string configName)
    {
        await StartGroupWithTaskListAsync(configName, member);
    }

    /// <summary>
    /// 从配置标签点击直接启动一条龙（跳过下拉选择框，直接弹"从此处开始执行"）。
    /// </summary>
    public async Task StartOneClickFromConfigAsync(MemberViewModel member, string configName)
    {
        await StartOneClickWithTaskListAsync(configName, member);
    }

    /// <summary>
    /// 从本机 BGI 读取指定配置组/一条龙的任务名称列表和启用状态。
    /// </summary>
    private async Task<(List<string>? tasks, List<object>? tasksWithStatus)> GetLocalTaskListAsync(string configName, bool isOneClick)
    {
        // 遥控器模式：从在线成员获取任务列表
        if (_config?.ObserverMode == true)
        {
            // 重试循环：最多 5 次，每次等 1 秒，应对执行端数据尚未同步到遥控端的场景
            for (int retry = 0; retry < 5; retry++)
            {
                var target = Members.FirstOrDefault(m => m.PlayerUid == _config.PlayerUid && m.Online
                    && (m.ConfigGroups?.Count > 0 || m.OneClickConfigs?.Count > 0));
                if (target == null)
                {
                    if (retry < 4) await Task.Delay(1000);
                    continue;
                }

                // 从 MemberViewModel 的同步任务列表字段获取
                if (isOneClick)
                {
                    // 一条龙：从 OneClickConfigs 和 OneClickTasksWithStatus 取
                    if (target.OneClickConfigs?.Contains(configName) == true)
                    {
                        List<string>? tasks2 = null;
                        List<object>? tasksWithStatus2 = null;
                        if (target.OneClickTasksWithStatus.TryGetValue(configName, out var statusList))
                        {
                            tasksWithStatus2 = statusList;
                            tasks2 = statusList
                                .Select(s => s is System.Text.Json.JsonElement je
                                    && je.TryGetProperty("name", out var n)
                                    ? n.GetString() ?? "" : "")
                                .Where(n => !string.IsNullOrEmpty(n))
                                .ToList()!;
                        }
                        return (tasks2, tasksWithStatus2);
                    }
                }
                else
                {
                    // 配置组：从 ConfigGroupTasksWithStatus 取
                    if (target.ConfigGroups?.Contains(configName) == true)
                    {
                        List<string>? tasks2 = null;
                        List<object>? tasksWithStatus2 = null;
                        if (target.ConfigGroupTasksWithStatus.TryGetValue(configName, out var statusList))
                        {
                            tasksWithStatus2 = statusList;
                            tasks2 = statusList
                                .Select(s => s is System.Text.Json.JsonElement je
                                    && je.TryGetProperty("name", out var n)
                                    ? n.GetString() ?? "" : "")
                                .Where(n => !string.IsNullOrEmpty(n))
                                .ToList()!;
                        }
                        return (tasks2, tasksWithStatus2);
                    }
                }

                // 配置组名存在但任务状态字典没有该配置名 → 说明数据还在路上，继续等
                if (retry < 4 && target.ConfigGroups?.Contains(configName) != true
                    && target.OneClickConfigs?.Contains(configName) != true)
                {
                    await Task.Delay(1000);
                    continue;
                }
                break;
            }
            return (null, null);
        }

        // [切片4] ext 通道优先，v2 短连接兜底（传输失败返回 null，与原"返回 (null, null)"语义一致）
        var response = await SendBgiIpcPreferredAsync("config.list", null);
        if (response is not { Success: true } || string.IsNullOrEmpty(response.Data))
            return (null, null);

        var data = JsonSerializer.Deserialize<JsonElement>(response.Data);
        var dictKey = isOneClick ? "oneClickTasks" : "configGroupTasks";
        var dictStatusKey = isOneClick ? "oneClickTasksWithStatus" : "configGroupTasksWithStatus";

        List<string>? tasks = null;
        if (data.TryGetProperty(dictKey, out var taskDict)
            && taskDict.ValueKind == JsonValueKind.Object
            && taskDict.TryGetProperty(configName, out var taskArr))
        {
            tasks = JsonSerializer.Deserialize<List<string>>(taskArr.GetRawText()) ?? [];
        }

        List<object>? tasksWithStatus = null;
        if (data.TryGetProperty(dictStatusKey, out var statusDict)
            && statusDict.ValueKind == JsonValueKind.Object
            && statusDict.TryGetProperty(configName, out var statusArr))
        {
            tasksWithStatus = JsonSerializer.Deserialize<List<object>>(statusArr.GetRawText()) ?? [];
        }

        return (tasks, tasksWithStatus);
    }

    private async Task ExecuteLocalCommandAsync(string cmd, Dictionary<string, object>? param, List<string>? targetUids)
    {
        var selfUid = _config?.PlayerUid ?? "";
        var targets = (targetUids ?? GetSelectedTargets())
            .Where(t => !string.IsNullOrEmpty(t)).Distinct().ToList();

        RemoteCommand NewCmd(List<string> target) => new()
        {
            Cmd = cmd,
            Sender = _config?.PlayerName ?? "",
            SenderUid = selfUid,
            Target = target,
            CommandId = "local_" + Guid.NewGuid().ToString("N"),
            Params = param
        };

        if (_commandExecutor == null)
        {
            // 遥控器模式：无本地 BGI，全部目标（含同 UID 的执行端）都走 SignalR
            if (_config?.ObserverMode == true)
            {
                if (targets.Count == 0)
                {
                    AddLog("没有在线且被选中的成员，未发送命令");
                    return;
                }
                if (_signalRClient != null)
                {
                    AddLog($"遥控器模式: 通过 SignalR 发送 {cmd} 命令");
                    await _signalRClient.SendRemoteCommandAsync(NewCmd(targets));
                }
                else
                {
                    AddLog("SignalR 未连接，无法发送命令");
                }
            }
            return;
        }

        // 执行模式：按目标分流——自己走本地 IPC，别人走 SignalR 定向下发。
        // （历史 bug：以前不管点谁的卡片都在本机执行，"给别人启动任务"从未真正生效）
        var selfTargeted = targets.Count == 0 || targets.Contains(selfUid);
        var remoteTargets = targets.Where(t => t != selfUid).ToList();

        if (selfTargeted)
        {
            // 有本地 BGI：走本地 IPC 执行
            // [批次标记] 每次新的本地下发都是新批次：重置回退标记（同 OnRemoteCommand 接收端）
            if (cmd is "start_group" or "start_oneclick")
            {
                _commandExecutor.ResetBatch();
            }
            var result = await _commandExecutor.ExecuteAsync(NewCmd([selfUid]));
            AddLog($"命令结果: {result.Message}");
        }
        if (remoteTargets.Count > 0)
        {
            if (_signalRClient != null)
            {
                var names = string.Join("、",
                    remoteTargets.Select(u => Members.FirstOrDefault(m => m.PlayerUid == u)?.PlayerName ?? u));
                AddLog($"已向 {names} 远程下发 {cmd} 命令");
                await _signalRClient.SendRemoteCommandAsync(NewCmd(remoteTargets));
            }
            else
            {
                AddLog("SignalR 未连接，无法向远程成员下发命令");
            }
        }
        if (!selfTargeted && remoteTargets.Count == 0)
        {
            AddLog("没有有效目标，命令未执行");
        }
    }

    /// <summary>
    /// [槲寄生·启动中心] 本机 BGI 命令执行入口（仅供 StartupFlowRunner 注入使用）。
    /// 与 ExecuteLocalCommandAsync 的区别：不选目标、不广播 SignalR，只对本机 BGI；
    /// "kill_bgi" 是启动中心私有 cmd（强杀本会话 BGI 进程），其余透传 CommandExecutor。
    /// 监控端无本地 BGI：直接返回失败结果，由调用方记日志按节点策略处理。
    /// </summary>
    internal async Task<CommandResult> ExecuteLocalBgiCommandAsync(string cmd, Dictionary<string, object>? param)
    {
        if (_config?.ObserverMode == true)
        {
            return new CommandResult { Status = "failed", Message = "监控端无本地 BGI，该节点已跳过" };
        }

        if (cmd == "kill_bgi")
        {
            if (_processMonitor == null)
                return new CommandResult { Status = "failed", Message = "未配置 BGI 路径，无法关闭 BGI" };
            // [P2 仲裁] 收编到仲裁器：信号量串行 + 有意杀死抑制（防守护把本次强杀误判为崩溃再拉起）
            // + 等进程真正退净；杀不掉（提权）时如实返回失败
            var killed = await _processMonitor.KillBgiControlledAsync("启动中心 kill_bgi 节点");
            return killed
                ? new CommandResult { Status = "success", Message = "已强制结束当前会话的 BGI 进程" }
                : new CommandResult { Status = "failed", Message = "无法终止现有 BGI 进程（可能提权运行），请手动关闭 BGI 后重试" };
        }

        if (_commandExecutor == null)
        {
            return new CommandResult { Status = "failed", Message = "未配置 BGI 路径，无法执行 BGI 命令" };
        }

        // [批次标记] 启动流程里的任务启动是新的下发边界，与 ExecuteLocalCommandAsync 同纪律
        if (cmd is "start_group" or "start_oneclick")
        {
            _commandExecutor.ResetBatch();
        }

        var selfUid = _config?.PlayerUid ?? "";
        return await _commandExecutor.ExecuteAsync(new RemoteCommand
        {
            Cmd = cmd,
            Sender = _config?.PlayerName ?? "",
            SenderUid = selfUid,
            Target = [selfUid],
            CommandId = "startup_" + Guid.NewGuid().ToString("N"),
            Params = param
        });
    }

    /// <summary>从本机 BGI 或在线成员读配置组与一条龙名称列表（用于一键命令绑定选择）。</summary>
    private async Task<(List<string> groups, List<string> oneClicks)> GetLocalConfigsAsync()
    {
        List<string> groups = [];
        List<string> oneClicks = [];

        // 遥控器模式：从在线成员获取配置组/一条龙列表
        if (_config?.ObserverMode == true)
        {
            var target = Members.FirstOrDefault(m => m.PlayerUid == _config.PlayerUid && m.Online
                && (m.ConfigGroups?.Count > 0 || m.OneClickConfigs?.Count > 0));
            if (target != null)
            {
                groups = target.ConfigGroups?.Where(g => !string.IsNullOrEmpty(g)).ToList() ?? [];
                oneClicks = target.OneClickConfigs?.Where(o => !string.IsNullOrEmpty(o)).ToList() ?? [];
            }
            return (groups, oneClicks);
        }

        try
        {
            // [切片4] ext 通道优先，v2 短连接兜底
            var response = await SendBgiIpcPreferredAsync("config.list", null);
            if (response is { Success: true } && !string.IsNullOrEmpty(response.Data))
            {
                var data = JsonSerializer.Deserialize<JsonElement>(response.Data);
                if (data.TryGetProperty("configGroups", out var g) && g.ValueKind == JsonValueKind.Array)
                    groups = JsonSerializer.Deserialize<List<string>>(g.GetRawText()) ?? [];
                if (data.TryGetProperty("oneClickConfigs", out var oc) && oc.ValueKind == JsonValueKind.Array)
                    oneClicks = JsonSerializer.Deserialize<List<string>>(oc.GetRawText()) ?? [];
            }
        }
        catch (Exception ex)
        {
            AddLog($"读取本机配置列表失败: {ex.Message}");
        }
        return (groups, oneClicks);
    }

    /// <summary>根据模式应用运行时边界（创建/销毁 BGI 进程监控和命令执行器）。</summary>
    private void ApplyModeRuntime(bool observerMode)
    {
        if (observerMode)
        {
            _processMonitor?.Dispose();
            _processMonitor = null;
            _commandExecutor = null;
            // [P1] 复位上线状态/清挂起补报/基线封口 int.MaxValue（避免切回执行模式后边沿检测自动触发上线），
            // 全部收口进状态机
            _intentLifecycle.OnModeSwitched();
            // [冷却倒计时] 切遥控器模式后不再跑边沿检测，停掉残留倒计时显示
            StopOnlineDebounceCountdown();
            AddLog("遥控器模式已启用，跳过 BGI 进程监控");
        }
        else if (!string.IsNullOrEmpty(_config?.BgiPath))
        {
            _processMonitor = new BgiProcessMonitor(_config.BgiPath, AddLog);
            // [P1] 原 OnBgiStarted 处理器（`_lastOnlineGeneration = 0`）已整体删除、不再订阅：
            // BGI 重启后进程内 onlineGeneration 归零，状态机 ApplyEdge 的下行对齐（gen < 基线 → 基线降为 gen）
            // 天然处理归零场景；而主动清零基线是幻影上线放大器——BGI 持久化/重启瞬间的高 gen 残留
            // 会在基线被清零后被当成"新边沿"触发幻影上线。
            _processMonitor.OnBgiCrashed += async () =>
            {
                // [P2 仲裁] 崩溃重启收编到仲裁器：原 Interlocked 重入守卫（_isBgiRestarting）已删除——
                // 仲裁器 SemaphoreSlim 已串行化全部杀/启操作（含回退重启与 kill_bgi），双守卫语义重复，
                // 且 async 等待（杀+等退净最长十余秒）期间 Interlocked 长时间占位会挡住合法重启。
                // async void 事件处理器必须 try/catch 兜底，异常不能逃逸出事件回调。
                // [常开守护 2026-09-11] 守护判定已改为电平式（没进程就拉起），本处理器不再有"补判"场景。
                try
                {
                    AddLog("BGI 已崩溃，自动重启");
                    var restarted = await _processMonitor.RestartBgiControlledAsync(null, "崩溃守护");
                    if (!restarted)
                    {
                        // 两种失败：旧进程杀不掉（提权）／启动调用失败。守护保持常开，冷却后会自动重试，不再静默。
                        AddLog("崩溃自动重启未完成（无法终止残留进程或启动失败，详见上方日志）；守护将在冷却结束后自动重试");
                        return;
                    }
                    AddLog("BGI 已自动重启");
                    _ = ReportStatusAsync();
                }
                catch (Exception ex)
                {
                    AddLog($"崩溃自动重启异常: {ex.Message}");
                }
            };
            if (_config.GuardBgi)
            {
                _processMonitor.Start();
            }
            // [P3 对账] isBatchInFlight：供 CommandExecutor 区分"批次在跑的中断上下文"（无损拒绝保护）
            // 与"孤儿残留上下文"（对账清除），判定以 _activeBatch 存活为准
            _commandExecutor = new CommandExecutor(_processMonitor, _config.BgiPath, () => _externalClient,
                AddLog, () => { lock (_batchGate) { return _activeBatch?.IsAlive == true; } });
        }
    }

    /// <summary>切换执行/监控模式。点击右上角连接徽章触发。</summary>
    private async Task SwitchModeAsync()
    {
        if (_config == null) return;
        var targetObserver = !_config.ObserverMode;
        var modeName = targetObserver ? "监控" : "执行";

        if (!ShowModeSwitchConfirm(modeName)) return;

        _config.ObserverMode = targetObserver;
        _configManager?.Save(_config);
        ApplyModeRuntime(targetObserver);
        OnPropertyChanged(nameof(IsObserverMode));
        OnPropertyChanged(nameof(IsExecutorMode));
        AddLog($"已切换为{modeName}模式，正在重建连接...");
        await RefreshAsync();
    }

    /// <summary>切换模式确认弹窗（深色鎏金主题）。</summary>
    private bool ShowModeSwitchConfirm(string modeName)
    {
        var window = new Window
        {
            Title = "切换模式",
            Width = 380, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new Grid { Margin = new Thickness(20) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(12) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) });
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });

        var titleLabel = new TextBlock
        {
            Text = $"切换为{modeName}模式",
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            HorizontalAlignment = HorizontalAlignment.Center
        };
        Grid.SetRow(titleLabel, 0);
        panel.Children.Add(titleLabel);

        var tip = new TextBlock
        {
            Text = modeName == "监控"
                ? "切换为监控模式后，本机 BGI 进程将继续运行，但助手将不再监控 BGI 状态。是否继续切换？"
                : "切换为执行模式后，助手将重新监控本机 BGI 状态并参与联机任务。是否继续切换？",
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)),
            TextWrapping = TextWrapping.Wrap
        };
        Grid.SetRow(tip, 2);
        panel.Children.Add(tip);

        var btnPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right
        };
        var cancelBtn = new Button
        {
            Content = "取消", Width = 80, Height = 30, Margin = new Thickness(0, 0, 10, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var okBtn = new Button
        {
            Content = "确定切换", Width = 80, Height = 30,
            FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand
        };
        okBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        okBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(okBtn);
        Grid.SetRow(btnPanel, 4);
        panel.Children.Add(btnPanel);

        window.Content = panel;
        bool result = false;
        okBtn.Click += (_, _) => { result = true; window.Close(); };
        cancelBtn.Click += (_, _) => window.Close();
        window.ShowDialog();
        return result;
    }

    /// <summary>打开设置弹窗（复用首次配置向导 SettingsWindow）。</summary>
    /// <summary>切换设置页面显示（成员列表 ↔ 设置页）。</summary>
    private void ToggleSettings()
    {
        IsShowingSettings = !IsShowingSettings;
    }

    /// <summary>打开房间设置弹窗（复用 SettingsWindow）。</summary>
    private void OpenRoomSettings()
    {
        if (_config == null || _configManager == null) return;
        var newConfig = SettingsWindow.ShowSettingsDialog(_config);
        if (newConfig != null)
        {
            _config = newConfig;
            _configManager.Save(_config);
            RoomCode = AssistConfigManager.GenerateControlRoomCode(_config.TeamUids);
            AddLog("房间配置已保存并生效");
        }
        }

    /// <summary>当前配置（供 XAML 绑定启动策略开关/下拉选择）。</summary>
    public AssistConfig? Config => _config;

    /// <summary>是否处于遥控器模式（ObserverMode=true）。供连接徽章 MultiDataTrigger 判断。</summary>
    public bool IsObserverMode => _config?.ObserverMode == true;

    /// <summary>执行模式（IsObserverMode 的反向，供 IsEnabled 绑定；正向属性免引入反向转换器）。</summary>
    public bool IsExecutorMode => !IsObserverMode;

    /// <summary>最近一次本地任务状态快照（10s 状态轮询的 task.status 产物）。
    /// 嘟嘟可卡死心跳检测用：只读缓存，不新起 IPC 轮询。</summary>
    public ControlStatus? LatestLocalStatus { get; private set; }

    /// <summary>最近一次联机上线代序号（onlineGeneration 边沿检测的最近已处理值，[P1] 转发状态机快照）。
    /// 嘟嘟可批次统计用做批次键；int.MaxValue 为"未知"兜底值，调用方应视为拿不到。</summary>
    public int? CurrentOnlineGeneration => _intentLifecycle.CurrentOnlineGeneration;

    /// <summary>SignalR 客户端出口（可能为 null，懒解析即可）。嘟嘟可 P5 截图汇聚用。</summary>
    internal SignalRClient? SignalR => _signalRClient;

    /// <summary>随 BGI 启动（开关，切换即保存，不执行即时窗口动作）。
    /// 生效时机：BGI 下次启动时由 BGI 主程序读取配置并拉起助手。</summary>
    public bool AutoLaunchWithBgi
    {
        get => _config?.AutoLaunchWithBgi ?? false;
        set
        {
            if (_config != null && _config.AutoLaunchWithBgi != value)
            {
                _config.AutoLaunchWithBgi = value;
                SaveConfig();
                OnPropertyChanged();
                // 注意：不再执行即时生效（不调 App.SetAutoLaunchWithBgi）。
                // 用户明确要求：勾选开关只保存配置，不应立即隐藏/弹窗窗口。
                // 生效时机由 BGI 侧 TryAutoLaunchAssistant 在下次 BGI 启动时完成。
            }
        }
    }

    /// <summary>开机自启动（开关，切换即保存 + 即时生效）</summary>
    public bool AutoLaunchOnBoot
    {
        get => _config?.AutoLaunchOnBoot ?? false;
        set
        {
            if (_config != null && _config.AutoLaunchOnBoot != value)
            {
                _config.AutoLaunchOnBoot = value;
                SaveConfig();
                OnPropertyChanged();
                // 即时生效：开关切换立即注册/取消注册
                if (value)
                    App.RegisterAutoStartup();
                else
                    App.UnregisterAutoStartup();
            }
        }
    }

    /// <summary>守护 BGI（开关，切换即保存 + 即时生效）</summary>
    public bool GuardBgi
    {
        get => _config?.GuardBgi ?? false;
        set
        {
            if (_config != null && _config.GuardBgi != value)
            {
                _config.GuardBgi = value;
                SaveConfig();
                OnPropertyChanged();
                // 即时生效：开关切换立即启动/停止守护检测
                if (value)
                    _processMonitor?.Start();
                else
                    _processMonitor?.Stop();
            }
        }
    }

    /// <summary>随 BGI 启动模式：0=弹窗启动，1=静默缩小到托盘</summary>
    public int AutoLaunchWithBgiModeIndex
    {
        get => _config?.AutoLaunchWithBgiMinimized == true ? 1 : 0;
        set
        {
            if (_config != null)
            {
                _config.AutoLaunchWithBgiMinimized = value == 1;
                SaveConfig();
            }
        }
    }

    /// <summary>开机自启动模式：0=弹窗启动，1=静默缩小到托盘</summary>
    public int AutoLaunchOnBootModeIndex
    {
        get => _config?.AutoLaunchOnBootMinimized == true ? 1 : 0;
        set
        {
            if (_config != null)
            {
                _config.AutoLaunchOnBootMinimized = value == 1;
                SaveConfig();
            }
        }
    }

    // ===== 上线锄地「完成后动作」（持久化：AssistConfig.onlineHoeingCompletion*；绑定弹窗配置）=====

    /// <summary>策略枚举 ↔ 持久化字符串映射（"resume"/"stop"/"runSpecified"）。</summary>
    private static string PolicyToString(TaskConflictPolicy policy) => policy switch
    {
        TaskConflictPolicy.Stop => "stop",
        TaskConflictPolicy.RunSpecified => "runSpecified",
        _ => "resume"
    };

    private static TaskConflictPolicy PolicyFromString(string? value) => value switch
    {
        "stop" => TaskConflictPolicy.Stop,
        "runSpecified" => TaskConflictPolicy.RunSpecified,
        _ => TaskConflictPolicy.Resume
    };

    /// <summary>[任务策略] 上线锄地完成后动作快照（持久化字段 → 不可变快照，传给 CommandExecutor 收尾用；
    /// 批次末尾与 10s resume 定时器两处共用）。</summary>
    private TaskConflictPolicySettings SnapshotOnlineHoeingPolicy() => new()
    {
        Policy = PolicyFromString(_config?.OnlineHoeingCompletionPolicy),
        SpecifiedTaskType = _config?.OnlineHoeingSpecifiedTaskType ?? "group",
        SpecifiedTaskName = _config?.OnlineHoeingSpecifiedTaskName ?? ""
    };

    /// <summary>任务策略远程互改通道（惰性构造，与 RemoteConfigEditService 同模式）。</summary>
    private TaskPolicySyncService GetTaskPolicySync()
        => _taskPolicySync ??= new TaskPolicySyncService(
            sendAsync: async rc =>
            {
                var client = _signalRClient;
                if (client == null || !client.IsConnected) return false;
                await client.SendRemoteCommandAsync(rc);
                return true;
            },
            getSelfUid: () => _config?.PlayerUid ?? "",
            getSelfName: () => _config?.PlayerName ?? "");

    private void SaveConfig()
    {
        _configManager?.Save(_config!);
    }

    /// <summary>
    /// 配置加载后刷新设置页绑定（否则 UI 初次绑定时 _config 为 null，控件显示未选/未读状态）。
    /// 在 InitializeAsync 里 _config 加载完成后调用。
    /// </summary>
    private void RefreshSetupBindings()
    {
        // 触发三个 CheckBox 和两个 ComboBox 的 UI 刷新
        OnPropertyChanged(nameof(AutoLaunchWithBgi));
        OnPropertyChanged(nameof(AutoLaunchOnBoot));
        OnPropertyChanged(nameof(GuardBgi));
        OnPropertyChanged(nameof(AutoLaunchWithBgiModeIndex));
        OnPropertyChanged(nameof(AutoLaunchOnBootModeIndex));
        // （上线锄地「完成后动作」已移至绑定弹窗、按键策略移至按键设置弹窗，均为代码构建弹窗，不走设置页绑定刷新）
        // 模式派生属性同样依赖 _config，初次绑定发生在配置加载前，必须一并刷新
        // （缺这两条曾导致：监控模式下按 IsExecutorMode 隐藏设置的绑定永远停在初值 Visible）
        NotifyModeBindings();
    }

    /// <summary>模式派生属性通知：_config 加载/替换后调用，驱动依赖 ObserverMode 的 UI 显隐。</summary>
    private void NotifyModeBindings()
    {
        OnPropertyChanged(nameof(IsObserverMode));
        OnPropertyChanged(nameof(IsExecutorMode));
    }

    private void OpenSettings()
    {
        if (_config == null || _configManager == null) return;
        var newConfig = SettingsWindow.ShowSettingsDialog(_config);
        if (newConfig != null)
        {
            _config = newConfig;
            _configManager.Save(_config);
            NotifyModeBindings(); // 可能在弹窗里改了遥控器模式 → 刷新依赖 ObserverMode 的 UI 显隐
            RoomCode = AssistConfigManager.GenerateControlRoomCode(_config.TeamUids);
            AddLog("配置已保存并生效");
        }
    }

    /// <summary>配置一个一键按钮的绑定（弹窗选配置组或一条龙）。返回 true 表示绑定成功。
    /// 绑别人时列表取该成员上报的配置缓存（targetMember.ConfigGroups/OneClickConfigs），
    /// 不用本机列表——各成员的 BGI 配置清单不同，绑错名字对方执行时找不到配置。</summary>
    private async Task<bool> BindQuickCommandAsync(string key, MemberViewModel? targetMember = null)
    {
        if (_config == null || _configManager == null) return false;
        List<string> groups;
        List<string> oneClicks;
        var bindingOther = targetMember != null && targetMember.PlayerUid != _config.PlayerUid;
        if (bindingOther)
        {
            // 绑别人：用对方周期上报的配置清单（可能是缓存，BGI 未连时为空或过期）
            groups = (targetMember!.ConfigGroups ?? []).Where(g => !string.IsNullOrEmpty(g)).ToList();
            oneClicks = (targetMember.OneClickConfigs ?? []).Where(o => !string.IsNullOrEmpty(o)).ToList();
            if (groups.Count == 0 && oneClicks.Count == 0)
            {
                MessageBox.Show($"未获取到 {targetMember.PlayerName} 的配置组/一条龙列表（对方 BGI 未连接或未上报）");
                return false;
            }
        }
        else
        {
            (groups, oneClicks) = await GetLocalConfigsAsync();
            if (groups.Count == 0 && oneClicks.Count == 0)
            {
                MessageBox.Show("无法读取本机 BGI 的配置组/一条龙列表，请确认 BGI 已启动且已同步脚本");
                return false;
            }
        }

        var names = new List<string>();
        names.AddRange(groups.Select(g => "[配置组] " + g));
        names.AddRange(oneClicks.Select(o => "[一条龙] " + o));

        var dialog = new Window
        {
            Title = $"绑定 {key}",
            Width = 460, Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = bindingOther
                ? $"为 {targetMember!.PlayerName} 的「{key}」选择要执行的配置组或一条龙（列表来自对方上报）："
                : $"为「{key}」选择要执行的配置组或一条龙：",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        });
        var list = new ListBox
        {
            ItemsSource = names, Height = 260, FontSize = 13,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
            BorderThickness = new Thickness(1),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA)),
            ItemContainerStyle = CreateDarkListBoxItemStyle()
        };
        panel.Children.Add(list);
        var bindBtnBg = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        var okBtn = new Button { Content = "绑定", Width = 120, Margin = new Thickness(0, 12, 8, 0), FontWeight = FontWeights.SemiBold, Background = bindBtnBg, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16)), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        var cancelBtn = new Button { Content = "取消", Width = 120, Margin = new Thickness(8, 12, 0, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)), BorderThickness = new Thickness(1), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)), Cursor = System.Windows.Input.Cursors.Hand };
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        btnRow.Children.Add(okBtn);
        btnRow.Children.Add(cancelBtn);
        panel.Children.Add(btnRow);
        dialog.Content = panel;

        string? selected = null;
        okBtn.Click += (_, _) => { selected = list.SelectedItem?.ToString(); dialog.DialogResult = true; };
        // 双击选择
        list.MouseDoubleClick += (_, _) => { selected = list.SelectedItem?.ToString(); dialog.DialogResult = true; };
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;

        if (dialog.ShowDialog() == true && !string.IsNullOrEmpty(selected))
        {
            // 去掉"[配置组] "/"[一条龙] "前缀
            var value = selected.StartsWith("[配置组] ") ? selected["[配置组] ".Length..]
                        : selected.StartsWith("[一条龙] ") ? selected["[一条龙] ".Length..] : selected;
            var isOneClick = selected.StartsWith("[一条龙] ");
            if (targetMember == null || targetMember.PlayerUid == _config?.PlayerUid)
            {
                // 绑自己：直接保存
                var boundValue = (isOneClick ? "ONEDRAGON:" : "GROUP:") + value;
                _config.QuickCommands[key] = boundValue;
                _configManager.Save(_config);
                AddLog($"{key} 已绑定: {value} ({(isOneClick ? "一条龙" : "配置组")})");
                // 同步更新 MemberViewModel 的 QuickCommands，使弹窗能立即反映绑定状态
                if (targetMember != null)
                {
                    targetMember.QuickCommands[key] = boundValue;
                }
                // 遥控器模式：本机没有 BGI，绑"自己"实际是绑同 UID 的执行端，
                // 必须推送 set_quick_command 让执行端保存新绑定，否则执行端读旧值执行。
                if (_config?.ObserverMode == true && _signalRClient != null)
                {
                    var cmd = new RemoteCommand
                    {
                        Cmd = "set_quick_command",
                        Sender = _config?.PlayerName ?? "",
                        SenderUid = _config?.PlayerUid ?? "",
                        Target = [targetMember?.PlayerUid ?? _config?.PlayerUid ?? ""],
                        CommandId = "quickcmd_" + DateTime.Now.Ticks,
                        Params = new Dictionary<string, object>
                        {
                            { "key", key },
                            { "value", boundValue },
                            { "isOneClick", isOneClick }
                        }
                    };
                    await _signalRClient.SendRemoteCommandAsync(cmd);
                    AddLog($"遥控模式：已向执行端下发绑定 {key}：{value}");
                }
            }
            else
            {
                // 绑别人：下发远程命令
                var cmd = new RemoteCommand
                {
                    Cmd = "set_quick_command",
                    Sender = _config?.PlayerName ?? "",
                    SenderUid = _config?.PlayerUid ?? "",
                    Target = [targetMember.PlayerUid],
                    CommandId = "quickcmd_" + DateTime.Now.Ticks,
                    Params = new Dictionary<string, object>
                    {
                        { "key", key },
                        { "value", (isOneClick ? "ONEDRAGON:" : "GROUP:") + value },
                        { "isOneClick", isOneClick }
                    }
                };
                AddLog($"向 {targetMember.PlayerName} 下发绑定 {key}：{(isOneClick ? "一条龙" : "配置组")}「{value}」");
                if (_signalRClient != null)
                    await _signalRClient.SendRemoteCommandAsync(cmd);
            }
            return true;
        }
        return false;
    }

    /// <summary>执行一键命令：始终弹分成员列弹窗，可配置各成员绑定并执行。</summary>
    private async Task ExecuteQuickCommandAsync(string key)
    {
        if (_config == null || _configManager == null || _signalRClient == null)
        {
            MessageBox.Show("助手未初始化或未连接");
            return;
        }
        // 始终弹分成员列弹窗，无论是否有绑定
        await ShowQuickCommandBindForMembersDialog(key);
    }

    /// <summary>显示一键命令的确认弹窗（含"修改"按钮）。返回 confirm/cancel/modify。</summary>
    private string ShowQuickConfirmDialog(string key, string value, bool isOneClick, int onlineCount)
    {
        var dialog = new Window
        {
            Title = $"确认下发",
            Width = 440,
            Height = 190,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"确认对 {onlineCount} 个在线成员下发「{key}」→ 本机{(isOneClick ? "一条龙" : "配置组")}「{value}」？",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            TextWrapping = TextWrapping.Wrap
        });
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var confirmBtnBg = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        var cancelBtn = new Button { Content = "取消", Width = 90, Margin = new Thickness(0, 0, 8, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)), BorderThickness = new Thickness(1), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)), Cursor = System.Windows.Input.Cursors.Hand };
        var modifyBtn = new Button { Content = "修改", Width = 90, Margin = new Thickness(0, 0, 8, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)), BorderThickness = new Thickness(1), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)), Cursor = System.Windows.Input.Cursors.Hand };
        var confirmBtn = new Button { Content = "确认", Width = 90, FontWeight = FontWeights.SemiBold, Background = confirmBtnBg, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16)), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(modifyBtn);
        btnRow.Children.Add(confirmBtn);
        panel.Children.Add(btnRow);
        dialog.Content = panel;

        string result = "cancel";
        confirmBtn.Click += (_, _) => { result = "confirm"; dialog.DialogResult = true; };
        modifyBtn.Click += (_, _) => { result = "modify"; dialog.DialogResult = true; };
        cancelBtn.Click += (_, _) => { result = "cancel"; dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true) return result;
        return "cancel";
    }

    /// <summary>快捷指令分成员绑定弹窗：显示所有在线成员，各自可绑定配置组/一条龙，底部"确认执行"完成下发。</summary>
    private async Task ShowQuickCommandBindForMembersDialog(string key)
    {
        var onlineMembers = Members.Where(m => m.Online).ToList();
        if (onlineMembers.Count == 0)
        {
            MessageBox.Show("没有在线成员可配置");
            return;
        }

        var dialog = new Window
        {
            Title = $"为成员配置「{key}」",
            Width = 800,
            Height = 420,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new Grid { Margin = new Thickness(16) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: 标题
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 1: 成员列
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 2: 按钮

        // 标题
        panel.Children.Add(new TextBlock
        {
            Text = $"为每个在线成员配置「{key}」要执行的配置组或一条龙，点击成员名称旁的\"选择\"按钮进行绑定：",
            FontSize = 13,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 8)
        });

        // 成员列：用 Grid 动态分列
        var memberGrid = new Grid { Margin = new Thickness(0, 0, 0, 8) };
        for (int i = 0; i < onlineMembers.Count; i++)
        {
            memberGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        }
        Grid.SetRow(memberGrid, 1);
        panel.Children.Add(memberGrid);

        var bindingLabels = new Dictionary<string, TextBlock>();
        for (int i = 0; i < onlineMembers.Count; i++)
        {
            var member = onlineMembers[i];
            var colPanel = new StackPanel { Margin = new Thickness(4) };

            // 成员名
            colPanel.Children.Add(new TextBlock
            {
                Text = $"{member.PlayerName}",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA)),
                HorizontalAlignment = HorizontalAlignment.Center
            });

            // 当前绑定显示：自己优先从持久化配置读（_config 不会因服务端广播覆盖而丢失），别人从 MemberViewModel 读
            var isSelfMember = member.PlayerUid == _config?.PlayerUid;
            var currentBinding = isSelfMember
                ? (_config?.QuickCommands?.GetValueOrDefault(key) ?? "")
                : (member.QuickCommands?.GetValueOrDefault(key) ?? "");
            var bindingLabel = new TextBlock
            {
                Text = string.IsNullOrEmpty(currentBinding) ? "未绑定" : currentBinding,
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(string.IsNullOrEmpty(currentBinding)
                    ? System.Windows.Media.Color.FromRgb(0xD9, 0xA8, 0x4E)
                    : System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 4, 0, 8),
                TextWrapping = TextWrapping.Wrap
            };
            bindingLabels[member.PlayerUid] = bindingLabel;
            colPanel.Children.Add(bindingLabel);

            // 选择按钮
            var selectBtn = new Button
            {
                Content = "选择绑定",
                Width = 100,
                Height = 28,
                HorizontalAlignment = HorizontalAlignment.Center,
                FontSize = 12,
                Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
                BorderThickness = new Thickness(1),
                BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
                Cursor = System.Windows.Input.Cursors.Hand
            };
            var capturedMember = member;
            selectBtn.Click += async (_, _) =>
            {
                await BindQuickCommandAsync(key, capturedMember);
                // 刷新绑定显示
                var newBinding = capturedMember.QuickCommands?.GetValueOrDefault(key) ?? "";
                bindingLabels[capturedMember.PlayerUid].Text = string.IsNullOrEmpty(newBinding) ? "未绑定" : newBinding;
                bindingLabels[capturedMember.PlayerUid].Foreground = new System.Windows.Media.SolidColorBrush(string.IsNullOrEmpty(newBinding)
                    ? System.Windows.Media.Color.FromRgb(0xD9, 0xA8, 0x4E)
                    : System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0));
            };
            colPanel.Children.Add(selectBtn);

            Grid.SetColumn(colPanel, i);
            memberGrid.Children.Add(colPanel);
        }

        // 按钮
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        Grid.SetRow(btnRow, 2);
        var cancelBtn = new Button
        {
            Content = "取消", Width = 90, Margin = new Thickness(0, 0, 8, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Cursor = System.Windows.Input.Cursors.Hand
        };
        var executeBtn = new Button
        {
            Content = "确认执行", Width = 110, FontWeight = FontWeights.SemiBold,
            Cursor = System.Windows.Input.Cursors.Hand, BorderThickness = new Thickness(0)
        };
        executeBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        executeBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(executeBtn);
        panel.Children.Add(btnRow);

        dialog.Content = panel;

        bool executed = false;
        executeBtn.Click += async (_, _) =>
        {
            executed = true;
            dialog.Close();
            var targets = GetSelectedTargets();
            if (targets.Count == 0)
            {
                MessageBox.Show("没有在线且被选中的成员可下发，请勾选要下发的在线成员");
                return;
            }
            foreach (var member in onlineMembers.Where(m => m.IsSelected && m.Online))
            {
                // 自己优先从持久化配置读（_config 不会因服务端广播覆盖而丢失），别人从 MemberViewModel 读
                var isSelf = member.PlayerUid == _config?.PlayerUid;
                var binding = isSelf
                    ? (_config?.QuickCommands?.GetValueOrDefault(key) ?? "")
                    : (member.QuickCommands?.GetValueOrDefault(key) ?? "");
                if (string.IsNullOrEmpty(binding)) continue;
                var isOneClick = binding.StartsWith("ONEDRAGON:");
                var value = isOneClick ? binding["ONEDRAGON:".Length..] : binding["GROUP:".Length..];
                if (member.PlayerUid == _config?.PlayerUid)
                {
                    if (_commandExecutor != null)
                    {
                        // [批次标记] 每次新的弹窗确认下发都是新批次：重置回退标记（同 OnRemoteCommand 接收端）
                        _commandExecutor.ResetBatch();
                        var localCmd = new RemoteCommand
                        {
                            Cmd = isOneClick ? "start_oneclick" : "start_group",
                            Params = new Dictionary<string, object>
                            {
                                [isOneClick ? "configName" : "groupName"] = value,
                                ["startFromIndex"] = 0
                            }
                        };
                        await _commandExecutor.ExecuteAsync(localCmd);
                    }
                    else
                    {
                        // 遥控器模式：无本地 BGI，发给同 UID 的执行端
                        await SendQuickStartAsync(key, isOneClick, value, [member.PlayerUid]);
                    }
                }
                else
                {
                    await SendQuickStartAsync(key, isOneClick, value, [member.PlayerUid]);
                }
            }
        };
        cancelBtn.Click += (_, _) => dialog.Close();

        dialog.ShowDialog();
        if (!executed) return;
        AddLog($"分成员绑定执行完成");
    }

    /// <summary>给选定的在线成员下发执行本机绑定的配置组/一条龙。</summary>
    private async Task SendQuickStartAsync(string key, bool isOneClick, string value, List<string> targets)
    {
        if (_signalRClient == null) return;
        var remoteCmd = new RemoteCommand
        {
            Cmd = isOneClick ? "start_oneclick" : "start_group",
            Sender = _config!.PlayerName,
            SenderUid = _config.PlayerUid,
            Target = targets,
            CommandId = key + "_" + DateTime.Now.Ticks,
            Params = new Dictionary<string, object>
            {
                [isOneClick ? "configName" : "groupName"] = value,
                ["startFromIndex"] = 0,
                ["key"] = key  // 新增：传入命令类型名，供队友查自己的绑定
            }
        };
        await _signalRClient.SendRemoteCommandAsync(remoteCmd);
        AddLog($"已向 {targets.Count} 个在线成员下发 {key}：执行{value}");
    }

    /// <summary>一键锄地：向所有选中成员下发执行他们各自绑定的联机锄地配置组。</summary>
    private async Task ExecuteQuickHoeingAsync()
    {
        var targets = GetSelectedTargets();
        if (targets.Count == 0)
        {
            AddLog("没有在线且被选中的成员可下发，请勾选要下发的在线成员");
            return;
        }

        if (_signalRClient == null) return;
        // 一键锄地是全局危险操作（给所有选中成员下发执行锄地配置组），先弹确认，
        // 说明确认后会发生什么，避免误触。
        var targetMembers = Members.Where(m => m.IsSelected && m.Online).ToList();
        if (!ShowQuickHoeingConfirmDialog(targetMembers))
        {
            AddLog("已取消一键锄地下发");
            return;
        }
        // 下发 start_group 命令，带 key="一键锄地" 但不带 groupName，
        // 接收端收到后会用 key 检测到"一键锄地"，遍历自己的 OnlineHoeingGroupNames 执行。
        var remoteCmd = new RemoteCommand
        {
            Cmd = "start_group",
            Sender = _config!.PlayerName,
            SenderUid = _config.PlayerUid,
            Target = targets,
            CommandId = "hoeing_" + DateTime.Now.Ticks,
            Params = new Dictionary<string, object>
            {
                ["groupName"] = "",
                ["startFromIndex"] = 0,
                ["key"] = "一键锄地"
            }
        };
        await _signalRClient.SendRemoteCommandAsync(remoteCmd);
        AddLog($"已向 {targets.Count} 个在线成员下发一键锄地（各成员执行自己绑定的配置组）");
    }

    /// <summary>一键锄地确认弹窗：展示每个成员绑定的锄地任务列表，未绑定者标注提示。</summary>
    private bool ShowQuickHoeingConfirmDialog(List<MemberViewModel> targets)
    {
        var dialog = new Window
        {
            Title = "确认下发一键锄地",
            Width = 520,
            Height = Math.Min(180 + targets.Count * 60, 420),
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"确认对 {targets.Count} 个成员下发「一键锄地」？",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            TextWrapping = TextWrapping.Wrap,
            Margin = new Thickness(0, 0, 0, 12)
        });

        // 成员列表
        var memberPanel = new StackPanel { Margin = new Thickness(0, 0, 0, 12) };
        foreach (var m in targets)
        {
            var hasBinding = m.OnlineHoeingGroupNames?.Count > 0;
            var memberRow = new StackPanel { Margin = new Thickness(0, 0, 0, 8) };
            memberRow.Children.Add(new TextBlock
            {
                Text = $"👤 {m.PlayerName}（{m.DisplayUid}）",
                FontSize = 13,
                FontWeight = FontWeights.SemiBold,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA))
            });
            memberRow.Children.Add(new TextBlock
            {
                Text = hasBinding
                    ? $"📋 绑定任务：{string.Join(", ", m.OnlineHoeingGroupNames)}"
                    : "⚠ 未绑定联机锄地配置组",
                FontSize = 12,
                Foreground = new System.Windows.Media.SolidColorBrush(hasBinding
                    ? System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)
                    : System.Windows.Media.Color.FromRgb(0xD9, 0xA8, 0x4E)),
                Margin = new Thickness(16, 2, 0, 0),
                TextWrapping = TextWrapping.Wrap
            });
            memberPanel.Children.Add(memberRow);
        }
        panel.Children.Add(memberPanel);

        // 未绑定提示
        if (targets.Any(m => m.OnlineHoeingGroupNames?.Count == 0))
        {
            panel.Children.Add(new TextBlock
            {
                Text = "⚠ 未绑定成员的锄地任务将不会被下发执行。",
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0xA8, 0x4E)),
                Margin = new Thickness(0, 0, 0, 8),
                TextWrapping = TextWrapping.Wrap
            });
        }

        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 8, 0, 0) };
        var confirmBtnBg = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        var cancelBtn = new Button { Content = "取消", Width = 90, Margin = new Thickness(0, 0, 8, 0), Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)), Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)), BorderThickness = new Thickness(1), BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)), Cursor = System.Windows.Input.Cursors.Hand };
        var confirmBtn = new Button { Content = "确认", Width = 90, FontWeight = FontWeights.SemiBold, Background = confirmBtnBg, Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16)), BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        btnRow.Children.Add(cancelBtn);
        btnRow.Children.Add(confirmBtn);
        panel.Children.Add(btnRow);
        dialog.Content = panel;

        bool result = false;
        confirmBtn.Click += (_, _) => { result = true; dialog.DialogResult = true; };
        cancelBtn.Click += (_, _) => { result = false; dialog.DialogResult = true; };
        if (dialog.ShowDialog() == true) return result;
        return false;
    }

    private List<string> GetSelectedTargets()
    {
        // 只收"在线且被选中"的成员；离线或未勾选的一律不下发。
        // 返回空 = 无可下发目标（调用方据此提示并阻止）。
        return Members.Where(m => m.IsSelected && m.Online).Select(m => m.PlayerUid).ToList();
    }

    private string? ShowConfigSelectionDialog(string type, List<string> configs, MemberViewModel? member = null)
    {
        if (configs.Count == 0)
        {
            MessageBox.Show($"该成员没有可用的{type}配置");
            return null;
        }

        // 判断是否为缓存数据：BGI 未运行 或 离线
        bool isCached = member != null && (member.BgiStatus != "running" || !member.Online);

        var dialog = new Window
        {
            Title = $"选择{type}",
            Width = 420, Height = 360,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var panel = new Grid { Margin = new Thickness(18) };
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 0: 标题
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 1: 缓存提示
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) }); // 2: 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 3: 列表
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) }); // 4: 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 5: 按钮

        var titleLabel = new TextBlock
        {
            Text = $"请选择{type}配置:",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D))
        };
        Grid.SetRow(titleLabel, 0);
        panel.Children.Add(titleLabel);

        // 如果是缓存数据，添加提示行
        if (isCached)
        {
            var cacheHint = new TextBlock
            {
                Text = "⚠ 该成员 BGI 未连接，以下为缓存配置，执行前请确认 BGI 已启动",
                FontSize = 11,
                Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xD9, 0xA8, 0x4E)),
                TextWrapping = TextWrapping.Wrap,
                Margin = new Thickness(0, 4, 0, 4)
            };
            Grid.SetRow(cacheHint, 1);
            panel.Children.Add(cacheHint);
        }

        var listBox = new ListBox
        {
            ItemsSource = configs,
            SelectedIndex = 0,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
            BorderThickness = new Thickness(1),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA)),
            FontSize = 13,
            MinHeight = 200,
            ItemContainerStyle = CreateDarkListBoxItemStyle()
        };
        Grid.SetRow(listBox, 3);
        panel.Children.Add(listBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = "确定", Width = 80, Height = 32, Margin = new Thickness(0, 0, 10, 0),
            FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        okBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        okBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        var cancelBtn = new Button { Content = "取消", Width = 80, Height = 32,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Cursor = System.Windows.Input.Cursors.Hand };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        Grid.SetRow(btnPanel, 5);
        panel.Children.Add(btnPanel);
        dialog.Content = panel;

        string? result = null;
        okBtn.Click += (_, _) => { result = listBox.SelectedItem?.ToString(); dialog.DialogResult = true; };
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;
        return dialog.ShowDialog() == true ? result : null;
    }

    private int? ShowStartFromDialog(string configName, List<string>? taskList, bool isOneClick = false, List<object>? tasksWithStatus = null, List<string>? targetUids = null)
    {
        // 无任务列表时回退到数字输入框（兼容旧行为/配置读取失败）
        if (taskList == null || taskList.Count == 0)
            return ShowStartFromDialogByIndex(configName);

        // 构建任务选择列表：第 0 项为"从头开始"，其余为真实任务名
        var options = new List<string> { "从头开始" };
        options.AddRange(taskList);

        var dialog = new Window
        {
            Title = "从此处开始执行",
            Width = 460, Height = 520,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };

        var stack = new StackPanel { Margin = new Thickness(20) };

        // 标题
        stack.Children.Add(new TextBlock
        {
            Text = $"「{configName}」共 {taskList.Count} 个任务",
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = "选择从哪个任务开始（勾选切换启用状态）",
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)),
            Margin = new Thickness(0, 0, 0, 12)
        });

        // 任务列表（ListBox 单选，每行含 CheckBox）
        var listBox = new ListBox
        {
            Height = 300,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
            BorderThickness = new Thickness(1),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA)),
            FontSize = 13,
            ItemContainerStyle = CreateDarkListBoxItemStyle()
        };
        for (var i = 0; i < options.Count; i++)
        {
            var isFirst = i == 0;
            // 获取启用状态与真实任务键（config.list 条目自带 index：配置组=项目 Index，一条龙=TaskEnabledList 键）
            var enabled = true;
            var taskKey = i; // 回退：位置序号
            if (!isFirst && tasksWithStatus != null && i - 1 < tasksWithStatus.Count)
            {
                var statusInfo = tasksWithStatus[i - 1];
                if (statusInfo is System.Text.Json.JsonElement je)
                {
                    // 仅采信 >0 的 index：老配置组文件可能持久化了全 0 的 index，
                    // 采信 0 反而连 BGI 端"重排后按位置匹配"的兜底都配不上
                    if (je.TryGetProperty("index", out var idxEl) && idxEl.TryGetInt32(out var realKey) && realKey > 0)
                        taskKey = realKey;
                    if (isOneClick)
                        enabled = je.TryGetProperty("enabled", out var enEl) ? enEl.GetBoolean() : true;
                    else
                        enabled = je.TryGetProperty("status", out var stEl) ? stEl.GetString() != "Disabled" : true;
                }
            }
            listBox.Items.Add(new TaskListItemViewModel
            {
                Index = i,
                TaskKey = taskKey,
                Text = isFirst ? options[i] : $"{i}. {options[i]}",
                SubText = isFirst ? "第一个任务" : "",
                IsTask = !isFirst,
                IsEnabled = enabled
            });
        }
        listBox.SelectedIndex = 0;
        // 自定义 ItemTemplate 显示 CheckBox + 文本（双向绑定 IsEnabled）
        var itemTemplate = new DataTemplate();
        var factory = new FrameworkElementFactory(typeof(System.Windows.Controls.StackPanel));
        factory.SetValue(System.Windows.Controls.StackPanel.OrientationProperty, System.Windows.Controls.Orientation.Horizontal);
        var checkBox = new FrameworkElementFactory(typeof(System.Windows.Controls.CheckBox));
        checkBox.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        checkBox.SetValue(FrameworkElement.MarginProperty, new Thickness(0, 0, 6, 0));
        var cbBinding = new System.Windows.Data.Binding("IsEnabled")
        {
            Mode = System.Windows.Data.BindingMode.TwoWay,
            UpdateSourceTrigger = System.Windows.Data.UpdateSourceTrigger.PropertyChanged
        };
        checkBox.SetBinding(System.Windows.Controls.CheckBox.IsCheckedProperty, cbBinding);
        factory.AppendChild(checkBox);
        var textBlock = new FrameworkElementFactory(typeof(System.Windows.Controls.TextBlock));
        textBlock.SetValue(FrameworkElement.VerticalAlignmentProperty, System.Windows.VerticalAlignment.Center);
        textBlock.SetBinding(System.Windows.Controls.TextBlock.TextProperty, new System.Windows.Data.Binding("Text"));
        factory.AppendChild(textBlock);
        itemTemplate.VisualTree = factory;
        listBox.ItemTemplate = itemTemplate;
        stack.Children.Add(listBox);

        // 按钮区
        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 90, Height = 32, Margin = new Thickness(0, 0, 10, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Cursor = System.Windows.Input.Cursors.Hand };
        var okBtn = new Button { Content = "确定", Width = 90, Height = 32,
            FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        okBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        okBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(okBtn);
        stack.Children.Add(btnPanel);

        dialog.Content = stack;

        int result = 0;
        bool confirmed = false;
        okBtn.Click += async (_, _) =>
        {
            // 保存期间禁用按钮：防止 await 未完成时用户重复点击确定/取消，
            // 导致重复下发或取消后 continuation 在已关闭窗口上设 DialogResult 抛异常
            okBtn.IsEnabled = false;
            cancelBtn.IsEnabled = false;

            if (listBox.SelectedItem is TaskListItemViewModel sel)
                // 一条龙传真实任务键（TaskEnabledList 键）：BGI 端按真实键比对 NextTaskIndex，
                // 位置序号在键有空洞/乱序时必不命中（打 warning 后从头开始）。
                // TaskKey<=0（"从头开始"项/无真实键）回退位置序号，与 0e2a0828 的回退纪律一致。
                // 配置组分支保持传位置序号不变（BGI 端有按位置重排兜底）。
                result = isOneClick && sel.TaskKey > 0 ? sel.TaskKey : sel.Index;
            confirmed = true;

            // 收集启用状态变更并下发
            var changes = new Dictionary<int, bool>();
            for (var i = 1; i < listBox.Items.Count; i++)
            {
                if (listBox.Items[i] is TaskListItemViewModel item)
                {
                    var originalEnabled = true;
                    if (tasksWithStatus != null && i - 1 < tasksWithStatus.Count)
                    {
                        var statusInfo = tasksWithStatus[i - 1];
                        if (statusInfo is System.Text.Json.JsonElement je)
                        {
                            if (isOneClick)
                                originalEnabled = je.TryGetProperty("enabled", out var enEl) ? enEl.GetBoolean() : true;
                            else
                                originalEnabled = je.TryGetProperty("status", out var stEl) ? stEl.GetString() != "Disabled" : true;
                        }
                    }
                    if (item.IsEnabled != originalEnabled)
                    {
                        // 用真实任务键（TaskKey）下发，不用位置序号——一条龙删过/重排过任务后两者会错位
                        changes[item.TaskKey] = item.IsEnabled;
                    }
                }
            }

            if (changes.Count > 0)
            {
                // 等所有保存完成再关弹窗：调用方在弹窗返回后立即发 start_group/start_oneclick，
                // fire-and-forget 会让 task.start 抢在 set_task_enabled 写盘前读旧状态（竞态）。
                // 本地 IPC 结果在 ExecuteLocalCommandAsync 内逐条打日志；远程由接收端 ack 回日志。
                var saveTasks = new List<Task>();
                foreach (var kv in changes)
                {
                    var param = new Dictionary<string, object>
                    {
                        { isOneClick ? "configName" : "groupName", configName },
                        { "taskIndex", kv.Key },
                        { "enabled", kv.Value }
                    };
                    saveTasks.Add(ExecuteLocalCommandAsync("set_task_enabled", param, targetUids));
                }
                try
                {
                    await Task.WhenAll(saveTasks);
                    AddLog($"已更新 {changes.Count} 个任务的启用状态");
                }
                catch (Exception ex)
                {
                    AddLog($"保存任务启用状态异常: {ex.Message}");
                }
            }

            // 窗口可能已被用户用 X 关闭（await 期间），只在窗口仍在显示时设置 DialogResult
            if (dialog.IsLoaded)
                dialog.DialogResult = true;
        };
        cancelBtn.Click += (_, _) => { dialog.DialogResult = false; };
        dialog.ShowDialog();
        return confirmed ? result : null;
    }

    /// <summary>
    /// 无任务列表时的回退：数字输入框（保持旧行为）。
    /// </summary>
    private int? ShowStartFromDialogByIndex(string configName)
    {
        var dialog = new Window
        {
            Title = "从此处开始执行",
            Width = 400, Height = 210,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize,
            FontFamily = new System.Windows.Media.FontFamily("HarmonyOS Sans SC, Microsoft YaHei"),
            Background = new System.Windows.Media.LinearGradientBrush
            {
                StartPoint = new System.Windows.Point(0.5, 0),
                EndPoint = new System.Windows.Point(0.5, 1),
                GradientStops =
                {
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x14, 0x15, 0x34), 0),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x22, 0x1F, 0x4E), 0.6),
                    new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0x1B, 0x19, 0x43), 1)
                }
            }
        };
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = $"请选择从第几个任务开始执行（{configName}）:",
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D)),
            Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap
        });

        var numBox = new TextBox { Text = "0", Height = 36,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37)),
            BorderThickness = new Thickness(1),
            Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(new TextBlock { Text = "0 = 从头开始，1 = 从第2个任务开始", FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0)), Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(numBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = "确定", Width = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0),
            FontWeight = FontWeights.SemiBold, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        okBtn.Background = new System.Windows.Media.LinearGradientBrush
        {
            StartPoint = new System.Windows.Point(0, 0),
            EndPoint = new System.Windows.Point(1, 1),
            GradientStops =
            {
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xEF, 0xD6, 0x8A), 0),
                new System.Windows.Media.GradientStop(System.Windows.Media.Color.FromRgb(0xD4, 0xAF, 0x37), 1)
            }
        };
        okBtn.Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x3A, 0x2F, 0x16));
        var cancelBtn = new Button { Content = "取消", Width = 80, Height = 30,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xC9, 0xC4, 0xE6)),
            BorderThickness = new Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Cursor = System.Windows.Input.Cursors.Hand };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(btnPanel);
        dialog.Content = stack;

        int result = 0;
        bool confirmed = false;
        okBtn.Click += (_, _) => { int.TryParse(numBox.Text, out result); confirmed = true; dialog.DialogResult = true; };
        cancelBtn.Click += (_, _) => dialog.DialogResult = false;
        dialog.ShowDialog();
        return confirmed ? result : null;
    }

    /// <summary>
    /// 创建 Apple 风 ListBox 行样式（无选中高亮边框、悬浮底色）。
    /// </summary>
    private static Style CreateListBoxItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, System.Windows.Media.Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e))));

        var hoverTrigger = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xe8, 0xf1, 0xff))));
        style.Triggers.Add(hoverTrigger);

        var selTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd6, 0xe8, 0xff))));
        selTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e))));
        selTrigger.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Triggers.Add(selTrigger);

        return style;
    }

    /// <summary>
    /// 创建深色 ListBox 行样式（深色主题弹窗用：浅色文字、半透明背景、悬浮亮紫、选中鎏金描边）。
    /// </summary>
    private static Style CreateDarkListBoxItemStyle()
    {
        var style = new Style(typeof(ListBoxItem));
        style.Setters.Add(new Setter(FrameworkElement.MarginProperty, new Thickness(0, 1, 0, 1)));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(8, 6, 8, 6)));
        style.Setters.Add(new Setter(FrameworkElement.CursorProperty, System.Windows.Input.Cursors.Hand));
        style.Setters.Add(new Setter(Control.BorderBrushProperty, System.Windows.Media.Brushes.Transparent));
        style.Setters.Add(new Setter(Control.BorderThicknessProperty, new Thickness(0)));
        style.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.Transparent));
        style.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA))));
        style.Setters.Add(new Setter(FrameworkElement.FocusVisualStyleProperty, null));

        var hoverTrigger = new Trigger { Property = ListBoxItem.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x35, 0x31, 0x5E))));
        style.Triggers.Add(hoverTrigger);

        var selTrigger = new Trigger { Property = ListBoxItem.IsSelectedProperty, Value = true };
        selTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x4A, 0x44, 0x7E))));
        selTrigger.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA))));
        selTrigger.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        style.Triggers.Add(selTrigger);

        return style;
    }

    internal void AddLog(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CommandLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            if (CommandLogs.Count > 100)
                CommandLogs.RemoveAt(CommandLogs.Count - 1);
            CommandLogsText = string.Join("\n", CommandLogs);
        });

        // 同时写入文件：保存在助手程序目录 log/ 子目录，按日期 + Windows 会话 ID 分文件
        try
        {
            var logDir = System.IO.Path.Combine(System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? ".", "log");
            System.IO.Directory.CreateDirectory(logDir);
            var logPath = System.IO.Path.Combine(logDir, $"assistant_runtime.{DateTime.Now:yyyy-MM-dd}.s{System.Diagnostics.Process.GetCurrentProcess().SessionId}.log");
            System.IO.File.AppendAllText(logPath, $"[{DateTime.Now:yyyy-MM-dd HH:mm:ss}] {message}\n");
        }
        catch
        {
            // 文件写入失败不应影响主流程
        }
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    /// <summary>
    /// [P1b] 策略收尾恰好一次守卫：批次正常末尾 / 批次内 F11 取消分支 / 10s 恢复定时器三个入口互斥，
    /// 同一轮上线锄地（按批次 generation 代序号比对）只执行一次——恢复定时器由 autoHoeingRunning
    /// 边沿触发，与批次末尾收尾可能同时命中，RunSpecified 策略双执行会重复启动指定任务（实机事故）。
    /// 守卫单调不减、新一轮 AllReady 不重置（重置会让旧轮次迟到的恢复定时器在新一轮里重跑收尾）；
    /// generation ≤ 0 的异常批次不进门，保持原行为直接执行。
    /// </summary>
    private async Task ApplyPolicyTeardownOnceAsync(OnlineHoeingBatch batch, string executedDesc, bool userCancelled)
    {
        lock (_teardownGate)
        {
            if (batch.Generation > 0 && _teardownDoneGeneration >= batch.Generation) return;
            if (batch.Generation > _teardownDoneGeneration) _teardownDoneGeneration = batch.Generation;
        }
        if (_commandExecutor != null)
        {
            await _commandExecutor.ApplyPolicyTeardownAsync(SnapshotOnlineHoeingPolicy(), executedDesc, userCancelled, AddLog);
            // 记录实际执行时间：10s 恢复定时器的无批次键直接路径据此跳过 30s 内的迟到边沿
            lock (_teardownGate) { _lastTeardownUtc = DateTime.UtcNow; }
        }
    }

    private async Task OnAllReadyConfirmedInternal(int generation)
    {
        // 互斥锁：防止两轮 AllReady 并发执行 OnAllReadyConfirmedInternal（patterns §31）
        if (Interlocked.CompareExchange(ref _isAllReadyProcessing, 1, 0) != 0)
        {
            AddLog("[上线探针] OnAllReadyConfirmedInternal 已在执行中，跳过");
            return;
        }
        // [P1] 幂等保护收口进状态机：同一 generation 只处理一次（防止 async void 并发或重复广播）；
        // 通过后状态 Armed → Executing
        if (!_intentLifecycle.TryBeginAllReady(generation))
        {
            _isAllReadyProcessing = 0;
            return;
        }

        // [P1b] 新一轮到达而上一轮批次仍存活：先取消旧批次（CTS.Cancel + 短超时等退出）再启动新批次，
        // 杜绝两轮 AllReady 批次并发交错（原 fire-and-forget 无句柄可寻，旧批次不可取消）
        OnlineHoeingBatch? previous;
        lock (_batchGate) { previous = _activeBatch; }
        if (previous is { IsAlive: true })
        {
            AddLog($"新一轮 AllReady（generation={generation}）到达，取消上一轮未完成批次（generation={previous.Generation}）");
            previous.Cancel();
            if (previous.RunTask != null)
            {
                try
                {
                    await previous.RunTask.WaitAsync(TimeSpan.FromSeconds(3));
                }
                catch
                {
                    // 超时/取消/异常均继续：旧批次残余会在下一次迭代检查点读到令牌后自行退出
                }
            }
        }
        var batch = new OnlineHoeingBatch(generation);
        lock (_batchGate) { _activeBatch = batch; }

        // 获取绑定的联机配置组列表
        var groupNames = _config?.OnlineHoeingGroupNames ?? [];
        var groupIndex = _config?.OnlineHoeingGroupIndex ?? 0;
        var groupName = (groupIndex >= 0 && groupIndex < groupNames.Count) ? groupNames[groupIndex] : null;

        if (string.IsNullOrEmpty(groupName))
        {
            _isAllReadyProcessing = 0;
            AddLog("未绑定联机锄地配置组，无法启动联机锄地");
            // [P1b] 早退也要复位状态机（Executing → Idle），否则生命周期卡在 Executing 永不回收
            _intentLifecycle.OnBatchFinished(generation, "未绑定联机锄地配置组");
            return;
        }

        // 检查 CommandExecutor 是否可用（依赖 BgiPath 配置）
        if (_commandExecutor == null)
        {
            _isAllReadyProcessing = 0;
            AddLog("CommandExecutor 不可用（BgiPath 未配置），无法通过 IPC 启动 BGI 配置组");
            if (_processMonitor != null)
            {
                // 命令行 --startGroups 支持多个配置组（BGI 侧 OnStartMultiScriptGroupWithNamesAsync 串行执行），
                // 必须一次性带上全部绑定组，否则冷启动只执行第一个组就停。
                // 一条龙配置无法用 --startGroups 启动，跳过并提示。
                var cliGroups = new List<string>();
                for (int i = 0; i < groupNames.Count; i++)
                {
                    var t = (_config?.OnlineHoeingGroupTypes?.Count > i) ? _config.OnlineHoeingGroupTypes[i] : "group";
                    if (t == "onedragon")
                        AddLog($"绑定的一条龙「{groupNames[i]}」无法通过命令行启动，已跳过（请配置 BGI 路径以启用 IPC）");
                    else
                        cliGroups.Add(groupNames[i]);
                }
                if (cliGroups.Count > 0)
                {
                    var groupArgs = string.Join(" ", cliGroups.Select(n => $"\"{n}\""));
                    AddLog($"尝试直接启动 BGI 带配置组参数: {groupArgs}");
                    _processMonitor.RestartBgi($"--startGroups {groupArgs}");
                }
            }
            else
            {
                AddLog("_processMonitor 也为 null，无法启动 BGI。请在设置页配置 BGI 路径");
            }
            // [P1b] 批次未启动的统一复位出口（Executing → Idle）
            _intentLifecycle.OnBatchFinished(generation, $"CommandExecutor 不可用，AllReady 批次未启动（generation={generation}）");
            _ = ReportStatusAsync();
            return;
        }

        // 先 task.suspend 中断当前任务
        var suspendResult = await _commandExecutor.ExecuteSuspendAsync(groupName);
        if (suspendResult.Status != "success")
        {
            // suspend 失败（典型场景：BGI 未运行，IPC 连接超时）。
            // 不要在此 KillBgi + RestartBgi 单组并 return —— 那样冷启动只执行第一个绑定组就停，
            // 其余绑定组永远不会执行（本 bug 根因）。直接落入下方批次循环：首个 start_group 的
            // IPC 传输失败会在 CommandExecutor.StartGroupAsync 内触发批次回退
            // （KillBgi + RestartBgi --startGroups 全部绑定组），由 BGI 命令行串行执行；
            // 若 BGI 实际在运行而 suspend 仅业务失败，也不会误杀进程。
            AddLog($"task.suspend 未成功（{suspendResult.Message}），继续走批次启动流程（IPC 不通时由批次回退统一重启 BGI 并串行执行全部绑定组）");
        }

        // 等待 BGI 内部的 CancellationContext 取消状态传播完毕，避免取消令牌残留影响后续 start_group
        // [P1-C 止血] 固定 1500ms 盲等改为轮询 IPC task.status（200ms 间隔、上限 6s）：
        // 确认 BGI 无任务在运行（或中断上下文已就位 hasSuspendedTaskContext=true）后再进入批次 task.start。
        // 超时仅记警告日志后继续，保持原有容错语义。
        // [切片4/切片7] settle 判定已抽为 CommandExecutor.WaitTaskSlotSettledAsync 共享方法
        // （ext slotReleased 事件 + 快照探测 + 200ms×30 轮询兜底），按键抢占路径复用同一实现。
        await _commandExecutor.WaitTaskSlotSettledAsync("[上线探针]", AddLog);

        // [P1b] 依次执行所有绑定的配置组（批次有主句柄：取消语义走 batch.Cts，原共享 bool 已废弃）
        batch.RunTask = Task.Run(async () =>
        {
            try
            {
                // [架构级批次管理] 新的一批开始时重置回退标记，避免上一批次的 _hasRestartedThisBatch
                // 状态残留导致后续批次的所有配置组被跳过（标记只应在同一批次内生效）。
                _commandExecutor?.ResetBatch();

                for (int i = 0; i < groupNames.Count; i++)
                {
                    // 取消检查点：外部"用户手动停止"（OnStop）或新一轮 AllReady 顶替都会置令牌
                    if (batch.IsCancellationRequested)
                    {
                        break;
                    }
                    var currentGroup = groupNames[i];
                    var groupType = (_config?.OnlineHoeingGroupTypes?.Count > i)
                        ? _config.OnlineHoeingGroupTypes[i]
                        : "group";
                    var isOneClick = groupType == "onedragon";
                    var startCmd = new RemoteCommand
                    {
                        Cmd = isOneClick ? "start_oneclick" : "start_group",
                        Params = new Dictionary<string, object>
                        {
                            { isOneClick ? "configName" : "groupName", currentGroup },
                            { "startFromIndex", 0 },
                            { "generation", generation },
                            { "batchGroupNames", string.Join(",", groupNames) }
                        }
                    };
                    var startResult = await _commandExecutor.ExecuteAsync(startCmd);
                    if (startResult.Status == "cancelled")
                    {
                        // 区分取消来源：令牌已被外部置位（新一轮 AllReady 顶替 / OnStop 手动停止），
                        // 说明取消不是用户 F11——跳过 userCancelled:true 收尾直接退出，
                        // 否则会把新轮刚建立的中断上下文清掉
                        if (batch.IsCancellationRequested)
                        {
                            AddLog("批次被新轮/手动停止取消，跳过 F11 收尾");
                            break;
                        }
                        batch.Cancel(); // 用户 F11 取消：记入批次令牌，阻止批次末尾再走策略收尾
                        // 用户 F11 取消永远压过配置策略：清上下文，不恢复、不启动指定任务
                        // [P1b] 走恰好一次守卫：10s 恢复定时器若同时命中则跳过
                        await ApplyPolicyTeardownOnceAsync(batch, "联机锄地配置组", userCancelled: true);
                        break;
                    }
                    if (startResult.Status != "success")
                    {
                        AddLog($"启动配置组 \"{currentGroup}\" 失败，跳过");
                        continue;
                    }
                    if (batch.IsCancellationRequested)
                    {
                        break;
                    }
                }
                // [P1b] 批次正常结束的统一复位出口（Executing → Idle）
                _intentLifecycle.OnBatchFinished(batch.Generation, $"AllReady 批次执行完毕（generation={generation}）");

                // 执行完所有绑定的配置组后，按"上线锄地策略"处置被中断的原任务
                // （恢复 / 不恢复直接停止 / 不恢复并执行指定任务，收尾逻辑共享 CommandExecutor.ApplyPolicyTeardownAsync）。
                // 此位置在 for 循环全部执行完后，天然覆盖两个场景：
                //   场景A: 绑定配置组是联机锄地（AutoHoeingTask）
                //   场景B: 绑定配置组是普通配置组（如"采集"）
                // 注意：如果配置组已被用户取消（F11），已在 cancelled 分支中按"取消优先"清除中断上下文，
                // 不需要再执行策略收尾（否则会打误导日志/误启动指定任务）
                // [P1b] 走恰好一次守卫：与 10s 恢复定时器互斥（同一代序号只收尾一次）
                if (!batch.IsCancellationRequested)
                {
                    await ApplyPolicyTeardownOnceAsync(batch, "联机锄地", userCancelled: false);
                }

                _ = ReportStatusAsync();
            }
            catch (Exception ex)
            {
                AddLog($"依次执行配置组异常: {ex.Message}");
                // [P1b] 批次异常的统一复位出口（Executing → Idle）
                _intentLifecycle.OnBatchFinished(batch.Generation, $"AllReady 批次异常（generation={generation}）");
                _ = ReportStatusAsync();
            }
            finally
            {
                // [P1b] 批次完结后句柄归 null（仅当仍指向本批次）：否则已完结批次残留，
                // 后续手动锄地结束的边沿捕获到旧批次，守卫因 _teardownDoneGeneration 已越代而
                // 直接 return，手动会话的策略收尾被静默吞掉（RunSpecified 下指定任务不启动）
                lock (_batchGate) { if (ReferenceEquals(_activeBatch, batch)) _activeBatch = null; }
            }
        });
        // 注意：互斥锁只覆盖启动窗口（suspend+settle+发起批次），Task.Run 启动后即释放是刻意的——
        // 后台序列可能横跨整个锄地会话，锁全程持有会挡住后续合法轮次；并发第二轮的穿透风险由
        // 服务端"参与者集合消费"（B157，不再残留武装事件幻影触发）+ [P1b] 批次句柄顶替取消兜底。
        _isAllReadyProcessing = 0;
    }

    /// <summary>查询只读任务状态；专供启动中心跨会话监控，不授予任何控制权限。</summary>
    public async Task<ControlStatus?> QueryReadOnlyTaskStatusAsync(StartupStep step, CancellationToken ct)
    {
        try
        {
            var targets = BgiProcessMonitor.SelectBgiProcesses(step.StatusSource, step.StatusTargetOrder, step.StatusTargetUser);
            if (targets.Length != 1)
            {
                foreach (var target in targets) target.Dispose();
                throw new InvalidOperationException("目标会话没有唯一可查询的 BGI 实例");
            }
            using var process = targets[0];
            var expectedStartTicks = process.StartTime.ToUniversalTime().Ticks;
            var expectedSession = process.SessionId;
            var pipeName = $"BetterGI.v2.status-p{process.Id}";
            using var pipe = new System.IO.Pipes.NamedPipeClientStream(".", pipeName, System.IO.Pipes.PipeDirection.InOut, System.IO.Pipes.PipeOptions.Asynchronous);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(3));
            await pipe.ConnectAsync(timeout.Token).ConfigureAwait(false);
            WindowsSessionIdentity.VerifyPipeServer(pipe, process.Id);
            var request = System.Text.Encoding.UTF8.GetBytes("GET_STATUS\n");
            await pipe.WriteAsync(request, timeout.Token).ConfigureAwait(false);
            await pipe.FlushAsync(timeout.Token).ConfigureAwait(false);
            using var reader = new StreamReader(pipe, System.Text.Encoding.UTF8, false, 1024, true);
            var text = new System.Text.StringBuilder();
            var character = new char[1];
            while (true)
            {
                if (await reader.ReadAsync(character.AsMemory(), timeout.Token).ConfigureAwait(false) == 0)
                    throw new EndOfStreamException("状态响应缺少结束标记");
                if (character[0] == '\n') break;
                if (text.Length >= 65536) throw new InvalidDataException("状态响应超过长度上限");
                text.Append(character[0]);
            }
            var json = text.ToString();
            using var doc = JsonDocument.Parse(json);
            var root = doc.RootElement;
            if (root.GetProperty("processId").GetInt32() != process.Id
                || root.GetProperty("windowsSessionId").GetInt32() != expectedSession
                || root.GetProperty("processStartTicks").GetInt64() != expectedStartTicks
                || process.HasExited)
                throw new InvalidDataException("只读状态实例身份已变化，拒绝采信");
            if (root.GetProperty("running").ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                throw new InvalidDataException("任务状态缺少有效 running 字段");
            var parsed = ParseTaskStatusData(root);
            return new ControlStatus
            {
                BgiStatus = parsed.BgiRunning ? "running" : "idle",
                TaskRunning = parsed.BgiRunning,
                CurrentTaskName = parsed.CurrentTaskName,
                CurrentTaskGroupName = parsed.CurrentTaskGroupName,
                CurrentRouteDisplay = parsed.CurrentRouteDisplay,
                CurrentScriptRouteName = parsed.CurrentScriptRouteName
            };
        }
        catch (Exception ex)
        {
            ct.ThrowIfCancellationRequested();
            AddLog($"[IPC] 只读任务状态查询失败：{ex.Message}");
            return null;
        }
    }

}

public class MemberViewModel : INotifyPropertyChanged
{
    public string PlayerUid { get; set; } = "";

    /// <summary>脱敏后的 UID（中间4位以 * 隐藏），用于 UI 显示。</summary>
    public string DisplayUid
    {
        get
        {
            if (string.IsNullOrEmpty(PlayerUid) || PlayerUid.Length < 9)
                return PlayerUid;
            return PlayerUid[..2] + "****" + PlayerUid[^3..];
        }
    }

    /// <summary>角色头像图片资源路径（按加入顺序从角色池分配，仅用于展示）。</summary>
    public string AvatarPath { get; set; } = "";
    /// <summary>头像元素色描边（十六进制色值）。</summary>
    public string AvatarRing { get; set; } = "#8FC1E8";

    private string _playerName = "";
    public string PlayerName { get => _playerName; set { if (_playerName != value) { _playerName = value; OnPropertyChanged(); } } }

    private bool _online;
    public bool Online { get => _online; set { if (_online != value) { _online = value; OnPropertyChanged(); OnPropertyChanged(nameof(CanRemoteEdit)); } } }

    /// <summary>是否为本机自己（由 MainViewModel 创建时按 UID 比较设置），用于禁用对自己的远程编辑。</summary>
    public bool IsSelf { get; set; }

    /// <summary>是否可对其发起远程配置组编辑（UID 非空 && 在线 && 非自己）。</summary>
    public bool CanRemoteEdit => !IsSelf && Online && !string.IsNullOrEmpty(PlayerUid);

    private string _bgiStatus = "unknown";
    public string BgiStatus { get => _bgiStatus; set { if (_bgiStatus != value) { _bgiStatus = value; OnPropertyChanged(); } } }

    private bool _autoHoeingRunning;
    public bool AutoHoeingRunning { get => _autoHoeingRunning; set { if (_autoHoeingRunning != value) { _autoHoeingRunning = value; OnPropertyChanged(); } } }

    private string? _autoHoeingProgress;
    public string? AutoHoeingProgress { get => _autoHoeingProgress; set { if (_autoHoeingProgress != value) { _autoHoeingProgress = value; OnPropertyChanged(); } } }

    private bool _taskRunning;
    public bool TaskRunning { get => _taskRunning; set { if (_taskRunning != value) { _taskRunning = value; OnPropertyChanged(); OnPropertyChanged(nameof(TaskDisplayText)); } } }

    private string? _currentTaskName;
    public string? CurrentTaskName { get => _currentTaskName; set { if (_currentTaskName != value) { _currentTaskName = value; OnPropertyChanged(); OnPropertyChanged(nameof(TaskDisplayText)); } } }
    private string? _currentTaskGroupName;
    public string? CurrentTaskGroupName { get => _currentTaskGroupName; set { if (_currentTaskGroupName != value) { _currentTaskGroupName = value; OnPropertyChanged(); OnPropertyChanged(nameof(TaskDisplayText)); } } }
    private string? _currentRouteDisplay;
    public string? CurrentRouteDisplay { get => _currentRouteDisplay; set { if (_currentRouteDisplay != value) { _currentRouteDisplay = value; OnPropertyChanged(); OnPropertyChanged(nameof(TaskDisplayText)); OnPropertyChanged(nameof(RouteLineText)); } } }
    private string? _currentScriptRouteName;
    /// <summary>配置组内脚本任务（JS/地图追踪）当前执行的具体线路名；非脚本任务为 null。</summary>
    public string? CurrentScriptRouteName { get => _currentScriptRouteName; set { if (_currentScriptRouteName != value) { _currentScriptRouteName = value; OnPropertyChanged(); OnPropertyChanged(nameof(TaskDisplayText)); OnPropertyChanged(nameof(RouteLineText)); } } }

    /// <summary>成员状态墙「路线」行文本：脚本任务的具体线路名优先（JS 任务的任务名是脚本名，线路要单独一层），
    /// 否则显示联机锄地线路进度；都为空显示 -。</summary>
    public string RouteLineText => !string.IsNullOrEmpty(CurrentScriptRouteName)
        ? CurrentScriptRouteName!
        : !string.IsNullOrEmpty(CurrentRouteDisplay) ? CurrentRouteDisplay! : "-";

    /// <summary>任务执行中时显示的完整文本：groupName · taskName · 线路（空段跳过）；
    /// 联机锄地时当前线路非空则优先显示线路（锄地任务名冗余），脚本任务（JS/地图追踪）
    /// 因为任务名只是脚本名、线路才是关键信息，三者全上。</summary>
    public string? TaskDisplayText
    {
        get
        {
            if (!TaskRunning) return null;
            var parts = new List<string>();
            if (!string.IsNullOrEmpty(CurrentTaskGroupName)) parts.Add(CurrentTaskGroupName);
            if (!string.IsNullOrEmpty(CurrentScriptRouteName))
            {
                if (!string.IsNullOrEmpty(CurrentTaskName)) parts.Add(CurrentTaskName);
                parts.Add(CurrentScriptRouteName);
            }
            else
            {
                if (!string.IsNullOrEmpty(CurrentTaskName) && string.IsNullOrEmpty(CurrentRouteDisplay)) parts.Add(CurrentTaskName);
                if (!string.IsNullOrEmpty(CurrentRouteDisplay)) parts.Add(CurrentRouteDisplay);
            }
            return parts.Count > 0 ? string.Join(" · ", parts) : CurrentTaskName ?? "任务执行中";
        }
    }
    private List<string> _configGroups = [];
    public List<string> ConfigGroups
    {
        get => _configGroups;
        set
        {
            // SignalR 反序列化每次都是新 List 引用：内容相同则不更新、不通知（避免标签区无谓重建闪烁）
            if (!ReferenceEquals(_configGroups, value) && !_configGroups.SequenceEqual(value))
            {
                _configGroups = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(ConfigGroupsDisplay));
            }
        }
    }

    private List<string> _oneClickConfigs = [];
    public List<string> OneClickConfigs
    {
        get => _oneClickConfigs;
        set
        {
            if (!ReferenceEquals(_oneClickConfigs, value) && !_oneClickConfigs.SequenceEqual(value))
            {
                _oneClickConfigs = value;
                OnPropertyChanged();
                OnPropertyChanged(nameof(OneClickConfigsDisplay));
            }
        }
    }
    public Dictionary<string, List<object>> ConfigGroupTasksWithStatus { get; set; } = [];
    public Dictionary<string, List<object>> OneClickTasksWithStatus { get; set; } = [];
    public List<object> Hotkeys { get; set; } = [];
    private bool _isSelected = true;
    public bool IsSelected { get => _isSelected; set { _isSelected = value; OnPropertyChanged(); } }

    private bool _onlineReady;
    public bool OnlineReady { get => _onlineReady; set { if (_onlineReady != value) { _onlineReady = value; OnPropertyChanged(); } } }

    private string _onlineMode = "none";
    public string OnlineMode { get => _onlineMode; set { if (_onlineMode != value) { _onlineMode = value; OnPropertyChanged(); } } }

    private string _scheduledOnlineTime = "";
    public string ScheduledOnlineTime { get => _scheduledOnlineTime; set { if (_scheduledOnlineTime != value) { _scheduledOnlineTime = value; OnPropertyChanged(); } } }

    private List<string> _onlineHoeingGroupNames = [];
    public List<string> OnlineHoeingGroupNames { get => _onlineHoeingGroupNames; set { if (!ReferenceEquals(_onlineHoeingGroupNames, value)) { _onlineHoeingGroupNames = value; OnPropertyChanged(); } } }

    private Dictionary<string, string> _quickCommands = new();
    public Dictionary<string, string> QuickCommands { get => _quickCommands; set { if (!ReferenceEquals(_quickCommands, value)) { _quickCommands = value; OnPropertyChanged(); } } }

    private List<object> _onlineHistory = [];
    public List<object> OnlineHistory { get => _onlineHistory; set { if (!ReferenceEquals(_onlineHistory, value)) { _onlineHistory = value; OnPropertyChanged(); OnPropertyChanged(nameof(HasOnlineToday)); } } }

    /// <summary>今天是否有上线消费记录（用于"已联机"状态显示：上线过且已消费）。</summary>
    public bool HasOnlineToday
    {
        get
        {
            if (_onlineHistory == null || _onlineHistory.Count == 0) return false;
            var today = DateTime.Now.ToString("yyyy-MM-dd");
            foreach (var h in _onlineHistory)
            {
                if (h is System.Text.Json.JsonElement je
                    && je.TryGetProperty("date", out var d)
                    && d.ValueKind == System.Text.Json.JsonValueKind.String
                    && d.GetString() == today)
                {
                    return true;
                }
            }
            return false;
        }
    }

    public string ConfigGroupsDisplay => ConfigGroups.Count > 0 ? string.Join(", ", ConfigGroups) : "无";
    public string OneClickConfigsDisplay => OneClickConfigs.Count > 0 ? string.Join(", ", OneClickConfigs) : "无";

    public event PropertyChangedEventHandler? PropertyChanged;
    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    /// <summary>强制触发全部可绑定属性通知（供刷新页面时即使数据未变也重建对应 UI 绑定）。</summary>
    public void NotifyAllPropertiesChanged()
    {
        OnPropertyChanged(nameof(PlayerName));
        OnPropertyChanged(nameof(DisplayUid));
        OnPropertyChanged(nameof(Online));
        OnPropertyChanged(nameof(BgiStatus));
        OnPropertyChanged(nameof(TaskRunning));
        OnPropertyChanged(nameof(CurrentTaskName));
        OnPropertyChanged(nameof(ConfigGroups));
        OnPropertyChanged(nameof(OneClickConfigs));
        OnPropertyChanged(nameof(ConfigGroupsDisplay));
        OnPropertyChanged(nameof(OneClickConfigsDisplay));
        OnPropertyChanged(nameof(OnlineReady));
        OnPropertyChanged(nameof(OnlineMode));
        OnPropertyChanged(nameof(ScheduledOnlineTime));
        OnPropertyChanged(nameof(OnlineHoeingGroupNames));
        OnPropertyChanged(nameof(CanRemoteEdit));
    }
}

public class RelayCommand : System.Windows.Input.ICommand
{
    private readonly Action<object?> _execute;
    private readonly Func<object?, bool>? _canExecute;

    public RelayCommand(Action<object?> execute, Func<object?, bool>? canExecute = null)
    {
        _execute = execute;
        _canExecute = canExecute;
    }

    public event EventHandler? CanExecuteChanged
    {
        add => System.Windows.Input.CommandManager.RequerySuggested += value;
        remove => System.Windows.Input.CommandManager.RequerySuggested -= value;
    }

    public bool CanExecute(object? parameter) => _canExecute?.Invoke(parameter) ?? true;
    public void Execute(object? parameter) => _execute(parameter);
}
public class TaskListItemViewModel
{
    public int Index { get; set; }

    /// <summary>
    /// 真实任务键：配置组 = ScriptGroupProject.Index（文件持久化值），一条龙 = TaskEnabledList 的键。
    /// 取自 config.list 返回条目的 index 字段；无效（≤0）或缺失时回退为位置序号 Index。
    /// set_task_enabled 下发必须用它而不是位置序号——一条龙删除/重排后键有空洞，位置序号会错位或静默落空。
    /// </summary>
    public int TaskKey { get; set; }

    public string Text { get; set; } = "";
    public string SubText { get; set; } = "";
    public bool IsTask { get; set; }
    public bool IsEnabled { get; set; } = true;

    public override string ToString() => Text;
}
