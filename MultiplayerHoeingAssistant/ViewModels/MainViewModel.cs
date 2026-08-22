using System.Collections.ObjectModel;
using System.ComponentModel;
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
    private string _roomCode = "";
    private bool _isConnected;
    private string _lastLoggedProgress = "";
    private Timer? _statusTimer;
    private Timer? _retryTimer;
    private Timer? _onlineTimer;
    private Timer? _resumeTimeoutTimer;
    private bool _isOnlineReady;
    private string _onlineMode = "none";
    /// <summary>记录定时上线今天是否已触发过（按日期去重，设定新时间时重置）。</summary>
    private DateTime _lastScheduledFireDate = DateTime.MinValue;
    /// <summary>本地定时上线自增 generation（用于驱动服务端 AllReady 判定，代替 BGI 的 onlineGeneration）。</summary>
    private int _localOnlineGeneration = 0;
    // 边沿检测：记录上次处理过的 BGI 上线事件代序号与 AllReady 代序号，用于幂等保护
    private int _lastOnlineGeneration = 0;
    private int _lastProcessedAllReadyGeneration;
    /// <summary>用户手动停止时设为 true，后台依次执行序列检查到此标志后跳过剩余配置组。</summary>
    private bool _isAllReadySequenceCancelled;
    /// <summary>用户手动清除上线后置 true，抑制定时自动上线。手动设定定时上线时清除。</summary>
    private bool _manuallyClearedOnline;
    private bool _wasAutoHoeingRunning;

    /// <summary>成员角色头像池（按加入顺序循环分配，file=资源名，ring=元素色描边）。</summary>
    private static readonly (string file, string ring)[] AvatarPool =
    {
        ("Ayaka", "#9FD1E8"), ("Kazuha", "#6FD8CE"), ("Ganyu", "#9FD1E8"), ("Yoimiya", "#E88A6F"),
        ("Shougun", "#C9A0E8"), ("Furina", "#8FC1E8"), ("Zhongli", "#D9A84E"), ("Nahida", "#A8D878"),
    };

    public ObservableCollection<MemberViewModel> Members { get; } = new();
    public ObservableCollection<string> CommandLogs { get; } = new();

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

    private bool _isShowingSettings;
    /// <summary>是否正在显示设置页面（true=设置页，false=成员列表主页）</summary>
    public bool IsShowingSettings
    {
        get => _isShowingSettings;
        set { _isShowingSettings = value; OnPropertyChanged(); }
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    /// <summary>刷新（重新加载页面）完成后触发，供 MainWindow 重建标签区 / 刷新成员卡片 UI。</summary>
    public event Action? RefreshCompleted;

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

    /// <summary>打开房间设置弹窗（复用 SettingsWindow）。</summary>
    public RelayCommand OpenRoomSettingsCommand => new(_ => OpenRoomSettings());

    /// <summary>关闭设置页面（返回成员列表主页）。</summary>
    public RelayCommand CloseSettingsCommand => new(_ => IsShowingSettings = false);

    public async Task InitializeAsync()
    {
        _configManager = new AssistConfigManager();
        _config = _configManager.Load();

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
        }

        // 生成房间码
        RoomCode = AssistConfigManager.GenerateControlRoomCode(_config.TeamUids);

        // 启动定时上线定时器（设定过 scheduledOnlineTime 才会真正到点触发）
        StartOnlineScheduler();

        // 遥控器模式：跳过 BGI 进程监控和命令执行器（本机无 BGI，所有操作通过远程命令）
        if (_config.ObserverMode)
        {
            AddLog("遥控器模式已启用，跳过 BGI 进程监控");
        }
        else
        {
            // 初始化进程监控
            // 守护 BGI（GuardBgi）：仅当开关开启时才启动 BGI 崩溃检测定时器。
            // _processMonitor 始终创建（供 CommandExecutor 手动 stop/start 时杀进程/重启用），
            // 但只有 GuardBgi=true 时才 Start()（开始每 5 秒检测 BGI 进程，崩溃即自动重启）。
            if (!string.IsNullOrEmpty(_config.BgiPath))
            {
                _processMonitor = new BgiProcessMonitor(_config.BgiPath);
                _processMonitor.OnBgiCrashed += () =>
                {
                    AddLog("BGI 已崩溃，自动重启");
                    _processMonitor.RestartBgi();
                    AddLog("BGI 已自动重启");
                    _ = ReportStatusAsync();
                };
                // GuardBgi=true 才开启守护；false 时不启动崩溃检测（BGI 关闭不自动重启）。
                if (_config.GuardBgi)
                {
                    _processMonitor.Start();
                }
                _commandExecutor = new CommandExecutor(_processMonitor, _config.BgiPath);
            }
        }

        // 连接 SignalR
        await ConnectSignalRAsync();
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
            };

            await _signalRClient.ConnectAsync(
                _config!.ServerUrl, RoomCode, _config.ControlRoomPassword,
                _config.PlayerUid, _config.PlayerName, _config.TeamUids, _config.ObserverMode);

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
                try
                {
                    if (_signalRClient == null || !_signalRClient.IsConnected)
                    {
                        await _signalRClient!.ConnectAsync(
                            _config!.ServerUrl, RoomCode, _config.ControlRoomPassword,
                            _config.PlayerUid, _config.PlayerName, _config.TeamUids);
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
                catch
                {
                    // 重试失败，保持定时器继续
                }
            }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
        }
    }

    private async Task ReportStatusAsync()
    {
        if (_signalRClient == null) return;

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
        var bgiRunning = false;

        // 遥控器模式：跳过 IPC 连接，直接上报 observer 状态
        if (_config?.ObserverMode == true)
        {
            // 跳过 IPC 连接，不上报配置组/任务状态
        }
        else
        {
            try
            {
                using var ipcClient = new IpcClient();
                await ipcClient.ConnectAsync(2000);
                var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "config.list" });
                if (response.Success && !string.IsNullOrEmpty(response.Data))
                {
                    var data = JsonSerializer.Deserialize<JsonElement>(response.Data);
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
                }
                else
                {
                    AddLog($"IPC config.list 失败: {response.ErrorMessage ?? "无响应"}");
                }

                // 轮询 task.status 获取当前任务状态
                var statusResp = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.status" });
                if (statusResp.Success && !string.IsNullOrEmpty(statusResp.Data))
                {
                    var sdata = JsonSerializer.Deserialize<JsonElement>(statusResp.Data);
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
                }
            }
            catch (Exception ex)
            {
                AddLog($"IPC 不可用: {ex.Message}");
            }
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
            AutoHoeingRunning = autoHoeingRunning,
            AutoHoeingProgress = autoHoeingProgress,
            OnlineReady = _isOnlineReady,
            OnlineMode = _onlineMode,
            ScheduledOnlineTime = _config?.ScheduledOnlineTime ?? "",
            OnlineHoeingGroupNames = _config?.OnlineHoeingGroupNames ?? [],
            ExpectedHoeingPlayers = _config?.ExpectedHoeingPlayers ?? 4
        };

        // 检测"联机锄地上线"任务已执行（通过 onlineGeneration 代序号边沿检测 + recentTaskName 降级）
        // 优先读 onlineGeneration（新字段），比 _lastOnlineGeneration 大才触发（边沿检测）。
        // 如果 onlineGeneration 不存在，降级到 recentTaskName 电平检测（旧 BGI 兼容）。
        // 触发后上报服务端（ReportOnlineEvent），由服务端状态机做就绪判断，助手端不做本地状态决策。
        try
        {
            using var recentTaskClient = new IpcClient();
            await recentTaskClient.ConnectAsync(2000);
            var statusResp = await recentTaskClient.SendCommandAsync(new IpcRequest { OpCode = "task.status" });
            if (statusResp.Success && !string.IsNullOrEmpty(statusResp.Data))
            {
                var sdata = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(statusResp.Data);
                // 优先读 onlineGeneration（新字段，边沿检测）
                if (sdata.TryGetProperty("onlineGeneration", out var ogEl) && ogEl.ValueKind == System.Text.Json.JsonValueKind.Number)
                {
                    var gen = ogEl.GetInt32();
                    if (gen > _lastOnlineGeneration)
                    {
                        _lastOnlineGeneration = gen;
                        // 命令上线：BGI 报告 onlineGeneration 递增 → 标记已上线（命令模式），
                        // 避免后续 ReportStatusAsync 继续上报 OnlineReady=false 覆盖服务端。
                        _isOnlineReady = true;
                        _onlineMode = "command";
                        // 同步更新本地已构造的 status 对象，防止后续 ReportControlStatusAsync 用 OnlineReady=false 覆盖服务端
                        status.OnlineReady = true;
                        status.OnlineMode = "command";
                        // 上报服务端，由服务端状态机协调
                        if (_signalRClient != null)
                        {
                            await _signalRClient.ReportOnlineEventAsync(gen, true);
                        }
                    }
                }
                // 降级：读 recentTaskName（旧 BGI 兼容）
                else if (sdata.TryGetProperty("recentTaskName", out var rtn) && rtn.ValueKind == System.Text.Json.JsonValueKind.String)
                {
                    var recentTask = rtn.GetString();
                    if (recentTask == "联机锄地上线" && !_isOnlineReady)
                    {
                        _ = MarkOnlineAsync("command");
                    }
                }
            }
        }
        catch
        {
            // IPC 不可用时不影响
        }

        // 检测联机锄地是否结束（autoHoeingRunning 从 true 变为 false）
        // 通过 IPC 查询 BGI 是否有中断上下文，不使用 _config 引用
        if (_wasAutoHoeingRunning && !autoHoeingRunning)
        {
            // 检查 BGI 是否有中断上下文
            bool hasContext = false;
            try
            {
                using var ctxCheckClient = new IpcClient();
                await ctxCheckClient.ConnectAsync(2000);
                var ctxResp = await ctxCheckClient.SendCommandAsync(new IpcRequest { OpCode = "task.status" });
                if (ctxResp.Success && !string.IsNullOrEmpty(ctxResp.Data))
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
                AddLog("联机锄地已结束，10 秒后自动恢复原任务...");
                // 启动恢复定时器（10 秒后自动恢复）
                _resumeTimeoutTimer?.Dispose();
                _resumeTimeoutTimer = new System.Threading.Timer(async _ =>
                {
                    if (_commandExecutor != null)
                    {
                        var result = await _commandExecutor.ExecuteResumeAsync();
                        if (result.Status == "success")
                        {
                            AddLog("原任务已自动恢复");
                        }
                    }
                }, null, TimeSpan.FromSeconds(10), TimeSpan.FromMilliseconds(-1));
            }
            _wasAutoHoeingRunning = autoHoeingRunning;
        }

        // 状态上报放 try-catch 内：连接断开时（如 ServerTimeout 后）InvokeAsync 会抛异常，
        // 若漏掉会作为未观察任务异常冒泡到全局 TaskScheduler.UnobservedTaskException → App 弹"未处理异常"框。
        // 这里捕获并仅记日志（断线状态已由 Closed 事件同步 IsConnected=false，右上角徽章变"离线"）。
        try
        {
            await _signalRClient.ReportControlStatusAsync(status);
        }
        catch (Exception ex)
        {
            AddLog($"状态上报失败（连接不可用）: {ex.Message}");
        }
    }

    /// <summary>标记已上线并上报服务端</summary>
    private async Task MarkOnlineAsync(string mode)
    {
        _isOnlineReady = true;
        _onlineMode = mode;
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

        // 上报上线事件，驱动服务端 AllReady（全员就绪）检查
        if (_signalRClient != null)
        {
            _localOnlineGeneration++;
            try
            {
                await _signalRClient.ReportOnlineEventAsync(_localOnlineGeneration, true);
                AddLog($"已上报上线事件 generation={_localOnlineGeneration}，等待服务端全员就绪开锄");
            }
            catch (Exception ex)
            {
                AddLog($"上报上线事件失败: {ex.Message}");
            }
        }
    }

    /// <summary>启动定时上线定时器（每 30 秒检查一次）。
    /// 不依赖 _isOnlineReady（避免状态残留阻塞），改用按天去重防止重复触发。</summary>
    private void StartOnlineScheduler()
    {
        _onlineTimer?.Dispose();
        _onlineTimer = new Timer(async _ =>
        {
            if (_config == null) return;
            if (string.IsNullOrEmpty(_config.ScheduledOnlineTime)) return;

            // 用户手动清除上线后，抑制定时自动上线（除非重新设定定时上线清除标志）
            if (_manuallyClearedOnline) return;

            var now = DateTime.Now;
            if (!TimeSpan.TryParse(_config.ScheduledOnlineTime, out var targetTime)) return;

            var target = now.Date.Add(targetTime);
            if (now < target) return;                              // 还没到点
            if (_lastScheduledFireDate == now.Date) return;        // 今天已触发过

            await MarkOnlineAsync("scheduled");
            _lastScheduledFireDate = now.Date;                     // 标记今天已触发
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

    private void OnStop(object? parameter)
    {
        // 标记依次执行序列已取消（用户手动停止后，剩余配置组不再执行）
        _isAllReadySequenceCancelled = true;
        if (parameter is MemberViewModel member)
        {
            _ = ExecuteLocalCommandAsync("stop", null, [member.PlayerUid]);
        }
        else
        {
            _ = ExecuteLocalCommandAsync("stop", null, null);
        }
    }

    private void OnStartBgi(object? parameter)
    {
        // 点自己：本地启动本机 BGI；点别人：通过 SignalR 下发命令让目标启动其 BGI。
        if (parameter is MemberViewModel member && member.PlayerUid != _config?.PlayerUid)
        {
            // 点别人的卡片：远程下发 start_bgi，由目标成员的助手本地执行启动。
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
        }
        else
        {
            // 点自己卡片（或未指定）：本地启动本机 BGI。
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
        var time = ShowScheduledOnlineTimeDialog(isSelf ? _config.ScheduledOnlineTime : "");
        if (time == null) return; // 用户取消

        if (isSelf)
        {
            if (_config?.ObserverMode == true)
            {
                // 遥控器模式：发给执行端（同 UID 的另一端）
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

    /// <summary>清除已上线状态（不清除定时闹钟）。点自己卡清自己；点别人卡远程下发清除。</summary>
    private async void OnClearOnline(object? parameter)
    {
        if (_config == null) return;
        var targetMember = parameter as MemberViewModel;
        var isSelf = targetMember == null || targetMember.PlayerUid == _config.PlayerUid;

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

    /// <summary>本地清除已上线状态：复位 _isOnlineReady / _onlineMode 并上报服务端。</summary>
    private async Task ClearLocalOnline()
    {
        _isOnlineReady = false;
        _onlineMode = "none";
        _manuallyClearedOnline = true; // 手动清除后，抑制定时自动上线
        // 关键：把 _lastOnlineGeneration 提升到当前 BGI 的 onlineGeneration 值，
        // 这样 ReportStatusAsync 的边沿探测 (`gen > _lastOnlineGeneration`) 不再触发重复上线。
        // 但真正的命令上线（BGI 新执行"联机锄地上线"，generation 递增）仍能触发（新值 > 当前值）。
        // 读取失败时用一个很大的值兜底（保证本会话内不再被旧 generation 触发）。

        // 遥控器模式：本机没有 BGI 进程，跳过 IPC 调用（避免死锁），直接设最大兜底值
        if (_config?.ObserverMode == true)
        {
            _lastOnlineGeneration = int.MaxValue;
        }
        else
        {
            try
            {
                using var ipcClient = new IpcClient();
                await ipcClient.ConnectAsync(2000);
                var resp = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "task.status" });
                if (resp.Success && !string.IsNullOrEmpty(resp.Data))
                {
                    var sdata = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(resp.Data);
                    if (sdata.TryGetProperty("onlineGeneration", out var og)
                        && og.ValueKind == System.Text.Json.JsonValueKind.Number)
                    {
                        _lastOnlineGeneration = og.GetInt32();
                    }
                    else
                    {
                        _lastOnlineGeneration = int.MaxValue;
                    }
                }
                else
                {
                    _lastOnlineGeneration = int.MaxValue;
                }
            }
            catch
            {
                _lastOnlineGeneration = int.MaxValue;
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
        if (_signalRClient == null) return;
        try
        {
            await _signalRClient.ClearOnlineHistoryAsync(member.PlayerUid);
            AddLog($"已清除 {member.PlayerName} 的联机记录");

            // 如果是清自己，重置本地状态以允许重新上线
            if (_config != null && member.PlayerUid == _config.PlayerUid)
            {
                _lastScheduledFireDate = DateTime.MinValue;
                _manuallyClearedOnline = false;
                AddLog("已重置本地状态，可重新上线");
            }
        }
        catch (Exception ex)
        {
            AddLog($"清除记录失败: {ex.Message}");
        }
    }

    /// <summary>定时上线时间弹窗（深色鎏金主题，时/分 ListBox 选择）。
    /// 返回值：null=用户取消，""=清除定时上线，"HH:mm"=设定时间。</summary>
    private string? ShowScheduledOnlineTimeDialog(string currentTime)
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
            Width = 340, Height = 350,
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
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 0 标题
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(16) }); // 1 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 2 时/分选择器（Star 限高，按 §21.5 防按钮被顶出）
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) }); // 3 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto });  // 4 提示文字
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(18) }); // 5 间距
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
        return result;
    }

    /// <summary>应用定时上线时间：写配置、保存、更新自身卡片、重启定时器并立即上报。
    /// 设定新时间时重置按天去重标记，让新设定能正常触发。</summary>
    private void ApplyScheduledOnlineTime(string time)
    {
        _config!.ScheduledOnlineTime = time;
        _configManager?.Save(_config);

        // 手动设定/清除定时上线 = 用户主动操作，清除"已手动清除上线"抑制标志，允许重新上线
        _manuallyClearedOnline = false;

        // 定时上线语义 = 闹钟：设/清时间只更新闹钟显示，不改变当前上线状态。
        // 是否上线由 _isOnlineReady 决定（定时到点/命令上线才触发 MarkOnlineAsync 置 true）；
        // 清除上线请用"清除上线"按钮。
        var self = Members.FirstOrDefault(m => m.PlayerUid == _config.PlayerUid);
        if (self != null)
        {
            self.ScheduledOnlineTime = time;
        }

        // 重置按天去重标记，让新设定的时间能正常触发
        _lastScheduledFireDate = DateTime.MinValue;

        // 若设定的时刻已过（now >= target），抑制今天的重复触发（避免"设定过去时间立即上线"导致状态瞬间残留），
        // 下次在明天同一时刻触发。
        if (!string.IsNullOrEmpty(time)
            && TimeSpan.TryParse(time, out var tgt)
            && DateTime.Now >= DateTime.Now.Date.Add(tgt))
        {
            _lastScheduledFireDate = DateTime.Now.Date;
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
        if (isSelf)
        {
            // 遥控器模式：从其他在线成员取配置组列表
            if (_config?.ObserverMode == true)
            {
                var target = Members.FirstOrDefault(m => m.PlayerUid == _config.PlayerUid && m.Online
                    && (m.ConfigGroups?.Count > 0));
                if (target != null)
                    allGroups = (target.ConfigGroups ?? []).Where(g => !string.IsNullOrEmpty(g)).ToList();
            }
            else
            {
                try
                {
                    using var ipc = new IpcClient();
                    await ipc.ConnectAsync(2000);
                    var resp = await ipc.SendCommandAsync(new IpcRequest { OpCode = "config.list" });
                    if (resp.Success && !string.IsNullOrEmpty(resp.Data))
                    {
                        var data = System.Text.Json.JsonSerializer.Deserialize<System.Text.Json.JsonElement>(resp.Data);
                        if (data.TryGetProperty("configGroups", out var groups) && groups.ValueKind == System.Text.Json.JsonValueKind.Array)
                        {
                            foreach (var g in groups.EnumerateArray()) allGroups.Add(g.GetString() ?? "");
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
        }

        if (allGroups.Count == 0)
        {
            MessageBox.Show(isSelf
                ? "未获取到 BGI 配置组列表，请确认 BGI 已启动且配置组目录存在。"
                : $"未获取到 {targetMember?.PlayerName ?? "对方"} 的配置组列表（可能对方尚未上报配置组）。",
                "提示", MessageBoxButton.OK, MessageBoxImage.Information);
            return;
        }

        // 构建可排序的深色主题绑定弹窗
        // 使用与主窗口一致的深色原神主题风格
        var window = new System.Windows.Window
        {
            Title = "绑定联机锄地配置组",
            Width = 420,
            Height = 500,
            WindowStartupLocation = System.Windows.WindowStartupLocation.CenterOwner,
            Owner = System.Windows.Application.Current?.MainWindow,
            WindowStyle = System.Windows.WindowStyle.SingleBorderWindow,
            ResizeMode = System.Windows.ResizeMode.NoResize,
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

        // 颜色常量
        var gold = System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D);
        var goldDeep = System.Windows.Media.Color.FromRgb(0xC9, 0xA5, 0x3F);
        var dim = System.Windows.Media.Color.FromRgb(0x9C, 0x97, 0xC0);
        var white = System.Windows.Media.Color.FromRgb(0xF4, 0xF2, 0xFA);
        var cardBg = System.Windows.Media.Color.FromArgb(0xCC, 0x26, 0x23, 0x4E);
        var cardEdge = System.Windows.Media.Color.FromArgb(0x47, 0xD4, 0xAF, 0x37);

        var panel = new System.Windows.Controls.Grid { Margin = new System.Windows.Thickness(16) };
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto }); // 标题
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(10) }); // 间距
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) }); // 已选列表
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto }); // 间距
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = new System.Windows.GridLength(1, System.Windows.GridUnitType.Star) }); // 可选列表
        panel.RowDefinitions.Add(new System.Windows.Controls.RowDefinition { Height = System.Windows.GridLength.Auto }); // 按钮

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

        // ========== 已选配置组列表（带排序） ==========
        var selectedBorder = new System.Windows.Controls.Border
        {
            CornerRadius = new System.Windows.CornerRadius(8),
            BorderThickness = new System.Windows.Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(cardEdge),
            Background = new System.Windows.Media.SolidColorBrush(cardBg),
            Padding = new System.Windows.Thickness(8)
        };
        System.Windows.Controls.Grid.SetRow(selectedBorder, 2);

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
            Height = 140,
            BorderThickness = new System.Windows.Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            FontSize = 12
        };
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
            Height = 120,
            BorderThickness = new System.Windows.Thickness(0),
            Background = System.Windows.Media.Brushes.Transparent,
            FontSize = 12
        };
        System.Windows.Controls.ScrollViewer.SetHorizontalScrollBarVisibility(availableListBox, System.Windows.Controls.ScrollBarVisibility.Disabled);

        // 刷新可选列表（排除已选的）
        System.Action RefreshAvailableList = null!;
        RefreshAvailableList = () =>
        {
            availableListBox.Items.Clear();
            foreach (var g in allGroups)
            {
                if (currentSelected.Contains(g)) continue;
                var item = new System.Windows.Controls.ListBoxItem
                {
                    Content = g,
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
        panel.Children.Add(selectedBorder);

        // ========== 可选配置组列表 ==========
        var availableBorder = new System.Windows.Controls.Border
        {
            CornerRadius = new System.Windows.CornerRadius(8),
            BorderThickness = new System.Windows.Thickness(1),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x47, 0x9C, 0x97, 0xC0)),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromArgb(0x2E, 0x6E, 0x6E, 0xB4)),
            Padding = new System.Windows.Thickness(8)
        };
        System.Windows.Controls.Grid.SetRow(availableBorder, 4);

        var availableInnerPanel = new System.Windows.Controls.StackPanel();

        var availableHeader = new System.Windows.Controls.TextBlock
        {
            Text = "可选配置组（点击添加）",
            FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(dim),
            Margin = new System.Windows.Thickness(4, 2, 0, 6)
        };
        availableInnerPanel.Children.Add(availableHeader);

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
        panel.Children.Add(availableBorder);

        // ========== 按钮栏 ==========
        var btnPanel = new System.Windows.Controls.StackPanel
        {
            Orientation = System.Windows.Controls.Orientation.Horizontal,
            HorizontalAlignment = System.Windows.HorizontalAlignment.Right,
            Margin = new System.Windows.Thickness(0, 10, 0, 0)
        };
        System.Windows.Controls.Grid.SetRow(btnPanel, 5);

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

            if (selected.Count > 0)
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
                                    { "groupNames", selected },
                                    { "groupIndex", 0 }
                                }
                            };
                            AddLog($"遥控器模式: 向执行端下发绑定联机锄地配置组（按顺序执行）: {string.Join(" → ", selected)}");
                            await _signalRClient.SendRemoteCommandAsync(cmd);
                        }
                    }
                    else
                    {
                        // 改自己：直接保存到本机配置
                        _config.OnlineHoeingGroupNames = selected;
                        _config.OnlineHoeingGroupIndex = 0;
                        _configManager?.Save(_config);
                        AddLog($"已绑定联机锄地配置组（按顺序执行）: {string.Join(" → ", selected)}");
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
                            { "groupNames", selected },
                            { "groupIndex", 0 }
                        }
                    };
                    AddLog($"向 {targetMember.PlayerName} 下发绑定联机锄地配置组（按顺序执行）: {string.Join(" → ", selected)}");
                    await _signalRClient.SendRemoteCommandAsync(cmd);
                }
            }
            window.Close();
        };

        btnPanel.Children.Add(cancelBtn);
        btnPanel.Children.Add(saveBtn);
        panel.Children.Add(btnPanel);
        window.Content = panel;
        window.ShowDialog();
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
    /// 刷新助手所有状态：断开并重建 SignalR 连接（重新加入控制房间，服务端会重新广播所有
    /// 玩家最新状态 → OnPlayersUpdated 刷新成员列表），等效于"重新加载页面"，用于界面异常时恢复。
    /// 刷新期间保留本机配置、本机命令执行器与连接参数（ServerUrl/房间码/密码/UID/队UID）。
    /// </summary>
    private async Task RefreshAsync()
    {
        AddLog("正在刷新助手状态...");
        try
        {
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
                var client = new SignalRClient();
                WireSignalRClient(client);
                client.OnConnectionStateChanged += connected =>
                {
                    Application.Current.Dispatcher.Invoke(() => IsConnected = connected);
                };
                await client.ConnectAsync(
                    _config.ServerUrl, RoomCode, _config.ControlRoomPassword,
                    _config.PlayerUid, _config.PlayerName, _config.TeamUids);
                _signalRClient = client;
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
            AddLog($"刷新失败: {ex.Message}");
            // 刷新失败时尽量保持可连接：若尚未成功重建，回到 10 秒重试（复用现有重试分支逻辑）
            if (_signalRClient == null)
            {
                _retryTimer = new Timer(async _ =>
                {
                    try
                    {
                        if (_signalRClient == null && _config != null)
                        {
                            var client = new SignalRClient();
                            WireSignalRClient(client);
                            await client.ConnectAsync(
                                _config.ServerUrl, RoomCode, _config.ControlRoomPassword,
                                _config.PlayerUid, _config.PlayerName, _config.TeamUids);
                            _signalRClient = client;
                            Application.Current.Dispatcher.Invoke(() =>
                            {
                                IsConnected = true;
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
                    catch
                    {
                        // 重试失败，保持定时器继续
                    }
                }, null, TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(10));
            }
        }
    }

    /// <summary>把 SignalR 的成员/命令/入房被拒事件接到 MainViewModel（在重建连接时复用同一组订阅逻辑）。</summary>
    private void WireSignalRClient(SignalRClient client)
    {
        // 绑定日志回调（探针日志输出到助手界面）
        client.OnLog = msg => Application.Current.Dispatcher.Invoke(() => AddLog(msg));

        client.OnPlayersUpdated += players =>
        {
            Application.Current.Dispatcher.Invoke(() =>
            {
                var byUid = new Dictionary<string, ControlRoomPlayer>();
                foreach (var p in players) byUid[p.PlayerUid] = p;
                for (int i = Members.Count - 1; i >= 0; i--)
                {
                    var m = Members[i];
                    if (byUid.TryGetValue(m.PlayerUid, out var np))
                    {
                        m.PlayerName = np.PlayerName;
                        m.Online = np.Online;
                        m.BgiStatus = np.BgiStatus;
                        m.ConfigGroups = np.ConfigGroups;
                        m.OneClickConfigs = np.OneClickConfigs;
                        m.AutoHoeingRunning = np.AutoHoeingRunning;
                        m.TaskRunning = np.TaskRunning;
                        m.CurrentTaskName = np.CurrentTaskName;
                        m.Hotkeys = np.Hotkeys;
                        m.ConfigGroupTasksWithStatus = np.ConfigGroupTasksWithStatus;
                        m.OneClickTasksWithStatus = np.OneClickTasksWithStatus;
                        m.OnlineReady = np.OnlineReady;
                        m.OnlineMode = np.OnlineMode;
                        m.ScheduledOnlineTime = np.ScheduledOnlineTime;
                        m.OnlineHoeingGroupNames = np.OnlineHoeingGroupNames ?? [];
                        m.OnlineHistory = np.OnlineHistory;
                        byUid.Remove(m.PlayerUid);
                    }
                    else
                    {
                        Members.RemoveAt(i);
                    }
                }
                foreach (var np in byUid.Values)
                {
                    var (file, ring) = AvatarPool[Members.Count % AvatarPool.Length];
                    Members.Add(new MemberViewModel
                    {
                        PlayerUid = np.PlayerUid,
                        PlayerName = np.PlayerName,
                        Online = np.Online,
                        BgiStatus = np.BgiStatus,
                        ConfigGroups = np.ConfigGroups,
                        OneClickConfigs = np.OneClickConfigs,
                        AutoHoeingRunning = np.AutoHoeingRunning,
                        TaskRunning = np.TaskRunning,
                        CurrentTaskName = np.CurrentTaskName,
                        Hotkeys = np.Hotkeys,
                        ConfigGroupTasksWithStatus = np.ConfigGroupTasksWithStatus,
                        OneClickTasksWithStatus = np.OneClickTasksWithStatus,
                        OnlineReady = np.OnlineReady,
                        OnlineMode = np.OnlineMode,
                        ScheduledOnlineTime = np.ScheduledOnlineTime,
                        OnlineHistory = np.OnlineHistory,
                        AvatarPath = $"pack://application:,,,/Assets/Images/{file}.png",
                        AvatarRing = ring,
                        IsSelected = true
                    });
                }
            });
        };

        client.OnAllReadyConfirmed += generation =>
        {
            _ = OnAllReadyConfirmedInternal(generation);
        };

        client.OnAllReadyConfirmReceived += async generation =>
        {
            if (generation <= _lastProcessedAllReadyGeneration)
            {
                AddLog("[探针] OnAllReadyConfirmReceived 忽略 (generation=" + generation + " <= " + _lastProcessedAllReadyGeneration + ")");
                return;
            }
            if (_signalRClient != null)
            {
                await _signalRClient.ConfirmAllReadyAsync(generation);
                AddLog("[探针] 已回复 AllReady 确认, generation=" + generation);
            }
            await OnAllReadyConfirmedInternal(generation);
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

            // bind_hoeing_group 命令：直接在助手本地处理，不走 BGI IPC
            if (cmd.Cmd == "bind_hoeing_group")
            {
                var groupNames = cmd.Params?.GetValueOrDefault("groupNames");
                if (groupNames is System.Text.Json.JsonElement je && je.ValueKind == System.Text.Json.JsonValueKind.Array)
                {
                    var names = je.EnumerateArray().Select(e => e.GetString() ?? "").Where(s => !string.IsNullOrEmpty(s)).ToList();
                    if (names.Count > 0 && _config != null)
                    {
                        _config.OnlineHoeingGroupNames = names;
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
                    foreach (var groupName in groupNames)
                    {
                        if (_commandExecutor == null) break;
                        var hoeingCmd = new RemoteCommand
                        {
                            Cmd = "start_group",
                            Params = new Dictionary<string, object>
                            {
                                ["groupName"] = groupName,
                                ["startFromIndex"] = 0
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
            var groupName = ShowConfigSelectionDialog("配置组", member.ConfigGroups);
            if (string.IsNullOrEmpty(groupName)) return;

            // 从本机 BGI 读取该配置组的任务列表（联机场景 4 台配置通常一致）
            _ = StartGroupWithTaskListAsync(groupName, member);
        }
    }

    private void OnStartOneClick(object? parameter)
    {
        if (parameter is MemberViewModel member)
        {
            var configName = ShowConfigSelectionDialog("一条龙", member.OneClickConfigs);
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

        using var ipcClient = new IpcClient();
        await ipcClient.ConnectAsync(2000);
        var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "config.list" });
        if (!response.Success || string.IsNullOrEmpty(response.Data))
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
        var remoteCmd = new RemoteCommand
        {
            Cmd = cmd,
            Sender = _config?.PlayerName ?? "",
            SenderUid = _config?.PlayerUid ?? "",
            Target = targetUids ?? GetSelectedTargets(),
            CommandId = "local_" + DateTime.Now.Ticks,
            Params = param
        };

        if (_commandExecutor != null)
        {
            // 有本地 BGI：走本地 IPC 执行（对自己或对别人，取决于 targetUids）
            var result = await _commandExecutor.ExecuteAsync(remoteCmd);
            AddLog($"命令结果: {result.Message}");
        }
        else if (_config?.ObserverMode == true)
        {
            // 遥控器模式：无本地 BGI，直接通过 SignalR 发送远程命令给目标成员
            if (_signalRClient != null)
            {
                AddLog($"遥控器模式: 通过 SignalR 发送 {cmd} 命令");
                await _signalRClient.SendRemoteCommandAsync(remoteCmd);
            }
            else
            {
                AddLog("SignalR 未连接，无法发送命令");
            }
        }
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
            using var ipcClient = new IpcClient();
            await ipcClient.ConnectAsync(2000);
            var response = await ipcClient.SendCommandAsync(new IpcRequest { OpCode = "config.list" });
            if (response.Success && !string.IsNullOrEmpty(response.Data))
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

    /// <summary>随 BGI 启动（开关，切换即保存 + 即时生效）</summary>
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
                // 即时生效：切换立即启动/停止 BGI 监控
                if (Application.Current is App app)
                    app.SetAutoLaunchWithBgi(value);
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
    }

    private void OpenSettings()
    {
        if (_config == null || _configManager == null) return;
        var newConfig = SettingsWindow.ShowSettingsDialog(_config);
        if (newConfig != null)
        {
            _config = newConfig;
            _configManager.Save(_config);
            RoomCode = AssistConfigManager.GenerateControlRoomCode(_config.TeamUids);
            AddLog("配置已保存并生效");
        }
    }

    /// <summary>配置一个一键按钮的绑定（弹窗选配置组或一条龙）。返回 true 表示绑定成功。</summary>
    private async Task<bool> BindQuickCommandAsync(string key)
    {
        if (_config == null || _configManager == null) return false;
        var (groups, oneClicks) = await GetLocalConfigsAsync();
        if (groups.Count == 0 && oneClicks.Count == 0)
        {
            MessageBox.Show("无法读取本机 BGI 的配置组/一条龙列表，请确认 BGI 已启动且已同步脚本");
            return false;
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
            Text = $"为「{key}」选择要执行的配置组或一条龙：",
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
            _config.QuickCommands[key] = (isOneClick ? "ONEDRAGON:" : "GROUP:") + value;
            _configManager.Save(_config);
            AddLog($"{key} 已绑定: {value} ({(isOneClick ? "一条龙" : "配置组")})");
            return true;
        }
        return false;
    }

    /// <summary>执行一键命令：未绑定则弹设置；已绑定则确认后下发；"修改"后直接返回（不再弹确认）。</summary>
    private async Task ExecuteQuickCommandAsync(string key)
    {
        if (_config == null || _configManager == null || _signalRClient == null)
        {
            MessageBox.Show("助手未初始化或未连接");
            return;
        }
        if (!_config.QuickCommands.TryGetValue(key, out var binding) || string.IsNullOrEmpty(binding))
        {
            // 未绑定：弹设置绑定后直接返回，用户想执行再点一次
            await BindQuickCommandAsync(key);
            return;
        }

        while (true)
        {
            var isOneClick = binding.StartsWith("ONEDRAGON:");
            var value = isOneClick ? binding["ONEDRAGON:".Length..] : binding["GROUP:".Length..];
            var targets = GetSelectedTargets();
            if (targets.Count == 0)
            {
                MessageBox.Show("没有在线且被选中的成员可下发，请勾选要下发的在线成员");
                return;
            }

            var action = ShowQuickConfirmDialog(key, value, isOneClick, targets.Count);
            if (action == "confirm")
            {
                await SendQuickStartAsync(key, isOneClick, value, targets);
                return;
            }
            if (action == "cancel") return;

            // action == "modify"：重新绑定后直接返回，不再弹确认
            await BindQuickCommandAsync(key);
            return;
        }
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

    private List<string> GetSelectedTargets()
    {
        // 只收"在线且被选中"的成员；离线或未勾选的一律不下发。
        // 返回空 = 无可下发目标（调用方据此提示并阻止）。
        return Members.Where(m => m.IsSelected && m.Online).Select(m => m.PlayerUid).ToList();
    }

    private string? ShowConfigSelectionDialog(string type, List<string> configs)
    {
        if (configs.Count == 0)
        {
            MessageBox.Show($"该成员没有可用的{type}配置");
            return null;
        }

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
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 标题
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(10) }); // 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表（star，可滚动）
        panel.RowDefinitions.Add(new RowDefinition { Height = new GridLength(14) }); // 间距
        panel.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 按钮

        var titleLabel = new TextBlock
        {
            Text = $"请选择{type}配置:",
            FontSize = 14,
            FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xE8, 0xC9, 0x6D))
        };
        Grid.SetRow(titleLabel, 0);
        panel.Children.Add(titleLabel);

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
        Grid.SetRow(listBox, 2);
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
        Grid.SetRow(btnPanel, 4);
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
            // 获取启用状态
            var enabled = true;
            if (!isFirst && tasksWithStatus != null && i - 1 < tasksWithStatus.Count)
            {
                var statusInfo = tasksWithStatus[i - 1];
                if (statusInfo is System.Text.Json.JsonElement je)
                {
                    if (isOneClick)
                        enabled = je.TryGetProperty("enabled", out var enEl) ? enEl.GetBoolean() : true;
                    else
                        enabled = je.TryGetProperty("status", out var stEl) ? stEl.GetString() != "Disabled" : true;
                }
            }
            listBox.Items.Add(new TaskListItemViewModel
            {
                Index = i,
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
        okBtn.Click += (_, _) =>
        {
            if (listBox.SelectedItem is TaskListItemViewModel sel)
                result = sel.Index;
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
                        changes[item.Index] = item.IsEnabled;
                    }
                }
            }

            if (changes.Count > 0)
            {
                foreach (var kv in changes)
                {
                    var param = new Dictionary<string, object>
                    {
                        { isOneClick ? "configName" : "groupName", configName },
                        { "taskIndex", kv.Key },
                        { "enabled", kv.Value }
                    };
                    _ = ExecuteLocalCommandAsync("set_task_enabled", param, targetUids);
                }
                AddLog($"已更新 {changes.Count} 个任务的启用状态");
            }

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

    private void AddLog(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CommandLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            if (CommandLogs.Count > 100)
                CommandLogs.RemoveAt(CommandLogs.Count - 1);
            CommandLogsText = string.Join("\n", CommandLogs);
        });

        // 同时写入文件：保存在助手程序所在目录的 assistant_runtime.log
        try
        {
            var logDir = System.IO.Path.GetDirectoryName(Environment.ProcessPath) ?? ".";
            var logPath = System.IO.Path.Combine(logDir, "assistant_runtime.log");
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

    private async Task OnAllReadyConfirmedInternal(int generation)
    {
        // 幂等保护：同一 generation 只处理一次（防止 async void 并发或重复广播）
        if (generation <= _lastProcessedAllReadyGeneration)
        {
            return;
        }
        _lastProcessedAllReadyGeneration = generation;

        // 获取绑定的联机配置组列表
        var groupNames = _config?.OnlineHoeingGroupNames ?? [];
        var groupIndex = _config?.OnlineHoeingGroupIndex ?? 0;
        var groupName = (groupIndex >= 0 && groupIndex < groupNames.Count) ? groupNames[groupIndex] : null;

        if (string.IsNullOrEmpty(groupName))
        {
            AddLog("未绑定联机锄地配置组，无法启动联机锄地");
            return;
        }

        // 检查 CommandExecutor 是否可用（依赖 BgiPath 配置）
        if (_commandExecutor == null)
        {
            AddLog("CommandExecutor 不可用（BgiPath 未配置），无法通过 IPC 启动 BGI 配置组");
            if (_processMonitor != null)
            {
                AddLog("尝试直接启动 BGI 带配置组参数...");
                _processMonitor.RestartBgi($"--startGroups \"{groupName}\"");
            }
            else
            {
                AddLog("_processMonitor 也为 null，无法启动 BGI。请在设置页配置 BGI 路径");
            }
            _isOnlineReady = false;
            _onlineMode = "none";
            _ = ReportStatusAsync();
            return;
        }

        // 先 task.suspend 中断当前任务
        var suspendResult = await _commandExecutor.ExecuteSuspendAsync(groupName);
        if (suspendResult.Status != "success")
        {
            AddLog("task.suspend 失败，尝试杀进程重启...");
            if (_processMonitor != null)
            {
                _processMonitor.KillBgi();
                await Task.Delay(2000);
                _processMonitor.RestartBgi($"--startGroups \"{groupName}\"");
            }
            _isOnlineReady = false;
            _onlineMode = "none";
            _ = ReportStatusAsync();
            return;
        }

        // 等待 BGI 内部的 CancellationContext 取消状态传播完毕，避免取消令牌残留影响后续 start_group
        await Task.Delay(1500);

        // 依次执行所有绑定的配置组
        _ = Task.Run(async () =>
        {
            try
            {
                for (int i = 0; i < groupNames.Count; i++)
                {
                    if (_isAllReadySequenceCancelled)
                    {
                        _isAllReadySequenceCancelled = false;
                        break;
                    }
                    var currentGroup = groupNames[i];
                    var startCmd = new RemoteCommand
                    {
                        Cmd = "start_group",
                        Params = new Dictionary<string, object> { { "groupName", currentGroup }, { "startFromIndex", 0 }, { "generation", generation } }
                    };
                    var startResult = await _commandExecutor.ExecuteAsync(startCmd);
                    if (startResult.Status == "cancelled")
                    {
                        _isAllReadySequenceCancelled = true;
                        break;
                    }
                    if (startResult.Status != "success")
                    {
                        AddLog($"启动配置组 \"{currentGroup}\" 失败，跳过");
                        continue;
                    }
                    if (_isAllReadySequenceCancelled)
                    {
                        break;
                    }
                }
                _isOnlineReady = false;
                _onlineMode = "none";
                _ = ReportStatusAsync();
            }
            catch (Exception ex)
            {
                AddLog($"依次执行配置组异常: {ex.Message}");
                _isOnlineReady = false;
                _onlineMode = "none";
                _ = ReportStatusAsync();
            }
        });
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
    public bool Online { get => _online; set { if (_online != value) { _online = value; OnPropertyChanged(); } } }

    private string _bgiStatus = "unknown";
    public string BgiStatus { get => _bgiStatus; set { if (_bgiStatus != value) { _bgiStatus = value; OnPropertyChanged(); } } }

    private bool _autoHoeingRunning;
    public bool AutoHoeingRunning { get => _autoHoeingRunning; set { if (_autoHoeingRunning != value) { _autoHoeingRunning = value; OnPropertyChanged(); } } }

    private bool _taskRunning;
    public bool TaskRunning { get => _taskRunning; set { if (_taskRunning != value) { _taskRunning = value; OnPropertyChanged(); } } }

    private string? _currentTaskName;
    public string? CurrentTaskName { get => _currentTaskName; set { if (_currentTaskName != value) { _currentTaskName = value; OnPropertyChanged(); } } }
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
    public string Text { get; set; } = "";
    public string SubText { get; set; } = "";
    public bool IsTask { get; set; }
    public bool IsEnabled { get; set; } = true;

    public override string ToString() => Text;
}