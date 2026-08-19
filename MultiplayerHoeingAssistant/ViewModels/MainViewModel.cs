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
    public RelayCommand StartGroupCommand => new(OnStartGroup);
    public RelayCommand StartOneClickCommand => new(OnStartOneClick);
    public RelayCommand ExecuteHotkeyCommand => new(OnExecuteHotkey);
    public RelayCommand CloseGameCommand => new(OnCloseGame);
    public RelayCommand ExitCommand => new(_ => OnExit());
    public RelayCommand RefreshCommand => new(_ => _ = RefreshAsync());

    // ===== 一键快捷命令（给所有在线成员下发执行绑定配置组/一条龙）=====
    public RelayCommand QuickLegendCommand => new(_ => _ = ExecuteQuickCommandAsync("一键传奇"));
    public RelayCommand QuickShieldCommand => new(_ => _ = ExecuteQuickCommandAsync("一键次数盾"));
    public RelayCommand QuickEliteCommand => new(_ => _ = ExecuteQuickCommandAsync("一键精英"));
    public RelayCommand QuickMultiCommand => new(_ => _ = ExecuteQuickCommandAsync("一键小怪"));
    public RelayCommand QuickCustomCommand => new(_ => _ = ExecuteQuickCommandAsync("一键自定义"));

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
                _config.PlayerUid, _config.PlayerName, _config.TeamUids);

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
            BgiStatus = _processMonitor?.IsBgiRunning == true ? "running" : "stopped",
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
            AutoHoeingProgress = autoHoeingProgress
        };

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
        if (parameter is MemberViewModel member)
        {
            _ = ExecuteLocalCommandAsync("stop", null, [member.PlayerUid]);
        }
        else
        {
            _ = ExecuteLocalCommandAsync("stop", null, null);
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
                                AddLog("刷新后已重新连接控制房间");
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
                        AvatarPath = $"pack://application:,,,/Assets/Images/{file}.png",
                        AvatarRing = ring,
                        IsSelected = true
                    });
                }
            });
        };

        client.OnRemoteCommand += async cmd =>
        {
            if (cmd.Cmd == "ack")
            {
                return;
            }
            AddLog($"收到远程命令: {cmd.Cmd} 来自 {cmd.Sender}");
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
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf5, 0xf5, 0xf7)),
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize
        };
        var stack = new StackPanel { Margin = new Thickness(20) };
        stack.Children.Add(new TextBlock
        {
            Text = "选择要执行的快捷键：",
            FontSize = 14, FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            Margin = new Thickness(0, 0, 0, 10)
        });
        var listBox = new ListBox
        {
            Height = 250,
            ItemsSource = names,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd1, 0xd1, 0xd6)),
            BorderThickness = new Thickness(1),
            Background = System.Windows.Media.Brushes.White,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            FontSize = 13,
            ItemContainerStyle = CreateListBoxItemStyle()
        };
        listBox.SelectedIndex = 0;
        stack.Children.Add(listBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 14, 0, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 90, Height = 32, Margin = new Thickness(0, 0, 10, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xe8, 0xe8, 0xed)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        var okBtn = new Button { Content = "执行", Width = 90, Height = 32,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0a, 0x84, 0xff)),
            Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
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
        AddLog($"执行本地命令: {cmd}");
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
            var result = await _commandExecutor.ExecuteAsync(remoteCmd);
            AddLog($"命令结果: {result.Message}");
        }
    }

    /// <summary>从本机 BGI 读配置组与一条龙名称列表（用于一键命令绑定选择）。</summary>
    private async Task<(List<string> groups, List<string> oneClicks)> GetLocalConfigsAsync()
    {
        List<string> groups = [];
        List<string> oneClicks = [];
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
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf5, 0xf5, 0xf7)),
        };
        var panel = new StackPanel { Margin = new Thickness(16) };
        panel.Children.Add(new TextBlock
        {
            Text = $"为「{key}」选择要执行的配置组或一条龙：",
            FontSize = 14,
            Margin = new Thickness(0, 0, 0, 12),
            TextWrapping = TextWrapping.Wrap
        });
        var list = new ListBox { ItemsSource = names, Height = 260, FontSize = 13 };
        panel.Children.Add(list);
        var okBtn = new Button { Content = "绑定", Width = 120, Margin = new Thickness(0, 12, 0, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 120, Margin = new Thickness(8, 12, 0, 0) };
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
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf5, 0xf5, 0xf7)),
            ResizeMode = ResizeMode.NoResize
        };
        var panel = new StackPanel { Margin = new Thickness(20) };
        panel.Children.Add(new TextBlock
        {
            Text = $"确认对 {onlineCount} 个在线成员下发「{key}」→ 本机{(isOneClick ? "一条龙" : "配置组")}「{value}」？",
            FontSize = 14,
            TextWrapping = TextWrapping.Wrap
        });
        var btnRow = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right, Margin = new Thickness(0, 24, 0, 0) };
        var cancelBtn = new Button { Content = "取消", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var modifyBtn = new Button { Content = "修改", Width = 90, Margin = new Thickness(0, 0, 8, 0) };
        var confirmBtn = new Button { Content = "确认", Width = 90 };
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
                ["startFromIndex"] = 0
            }
        };
        await _signalRClient.SendRemoteCommandAsync(remoteCmd);
        AddLog($"已向 {targets.Count} 个在线成员下发 {key}：执行{value}");
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
            Width = 420, Height = 240,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf5, 0xf5, 0xf7)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize
        };
        var stack = new StackPanel { Margin = new Thickness(18) };
        stack.Children.Add(new TextBlock 
        { 
            Text = $"请选择{type}配置:", 
            FontSize = 14,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            Margin = new Thickness(0, 0, 0, 12)
        });

        var combo = new ComboBox
        {
            ItemsSource = configs,
            SelectedIndex = 0,
            Background = System.Windows.Media.Brushes.White,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd1, 0xd1, 0xd6)),
            BorderThickness = new Thickness(1),
            Height = 34,
            FontSize = 13,
            Margin = new Thickness(0, 0, 0, 14)
        };
        // 下拉列表项样式：白色背景 + 深色文字，悬浮浅蓝，选中蓝底
        var itemContainerStyle = new Style(typeof(ComboBoxItem));
        itemContainerStyle.Setters.Add(new Setter(Control.BackgroundProperty, System.Windows.Media.Brushes.White));
        itemContainerStyle.Setters.Add(new Setter(Control.ForegroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e))));
        itemContainerStyle.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 30.0));
        var hoverTrigger = new Trigger { Property = ComboBoxItem.IsMouseOverProperty, Value = true };
        hoverTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xe8, 0xf1, 0xff))));
        itemContainerStyle.Triggers.Add(hoverTrigger);
        var selTrigger = new Trigger { Property = ComboBoxItem.IsSelectedProperty, Value = true };
        selTrigger.Setters.Add(new Setter(Control.BackgroundProperty, new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd6, 0xe8, 0xff))));
        selTrigger.Setters.Add(new Setter(Control.FontWeightProperty, FontWeights.SemiBold));
        itemContainerStyle.Triggers.Add(selTrigger);
        combo.ItemContainerStyle = itemContainerStyle;
        stack.Children.Add(combo);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = "确定", Width = 80, Height = 32, Margin = new Thickness(0, 0, 10, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0a, 0x84, 0xff)),
            Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        var cancelBtn = new Button { Content = "取消", Width = 80, Height = 32,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xe9, 0xe9, 0xeb)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        btnPanel.Children.Add(okBtn);
        btnPanel.Children.Add(cancelBtn);
        stack.Children.Add(btnPanel);
        dialog.Content = stack;

        string? result = null;
        okBtn.Click += (_, _) => { result = combo.SelectedItem?.ToString(); dialog.DialogResult = true; };
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
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf5, 0xf5, 0xf7)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize
        };

        var stack = new StackPanel { Margin = new Thickness(20) };

        // 标题
        stack.Children.Add(new TextBlock
        {
            Text = $"「{configName}」共 {taskList.Count} 个任务",
            FontSize = 15, FontWeight = FontWeights.SemiBold,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            Margin = new Thickness(0, 0, 0, 4), TextWrapping = TextWrapping.Wrap
        });
        stack.Children.Add(new TextBlock
        {
            Text = "选择从哪个任务开始（勾选切换启用状态）",
            FontSize = 12,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8a, 0x8a, 0x8e)),
            Margin = new Thickness(0, 0, 0, 12)
        });

        // 任务列表（ListBox 单选，每行含 CheckBox）
        var listBox = new ListBox
        {
            Height = 300,
            BorderBrush = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xd1, 0xd1, 0xd6)),
            BorderThickness = new Thickness(1),
            Background = System.Windows.Media.Brushes.White,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            FontSize = 13,
            ItemContainerStyle = CreateListBoxItemStyle()
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
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xe8, 0xe8, 0xed)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        var okBtn = new Button { Content = "确定", Width = 90, Height = 32,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0a, 0x84, 0xff)),
            Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
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
            Width = 400, Height = 200,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            Owner = Application.Current.MainWindow,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xf5, 0xf5, 0xf7)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            WindowStyle = WindowStyle.SingleBorderWindow,
            ResizeMode = ResizeMode.NoResize
        };
        var stack = new StackPanel { Margin = new Thickness(16) };
        stack.Children.Add(new TextBlock
        {
            Text = $"请选择从第几个任务开始执行（{configName}）:",
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            Margin = new Thickness(0, 0, 0, 12), TextWrapping = TextWrapping.Wrap
        });

        var numBox = new TextBox { Text = "0", Height = 36,
            Background = System.Windows.Media.Brushes.White,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            Margin = new Thickness(0, 0, 0, 12) };
        stack.Children.Add(new TextBlock { Text = "0 = 从头开始，1 = 从第2个任务开始", FontSize = 11,
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x8a, 0x8a, 0x8e)), Margin = new Thickness(0, 0, 0, 4) });
        stack.Children.Add(numBox);

        var btnPanel = new StackPanel { Orientation = Orientation.Horizontal, HorizontalAlignment = HorizontalAlignment.Right };
        var okBtn = new Button { Content = "确定", Width = 80, Height = 30, Margin = new Thickness(0, 0, 8, 0),
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x0a, 0x84, 0xff)),
            Foreground = System.Windows.Media.Brushes.White, BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
        var cancelBtn = new Button { Content = "取消", Width = 80, Height = 30,
            Background = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0xe8, 0xe8, 0xed)),
            Foreground = new System.Windows.Media.SolidColorBrush(System.Windows.Media.Color.FromRgb(0x1c, 0x1c, 0x1e)),
            BorderThickness = new Thickness(0), Cursor = System.Windows.Input.Cursors.Hand };
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

    private void AddLog(string message)
    {
        Application.Current.Dispatcher.Invoke(() =>
        {
            CommandLogs.Insert(0, $"[{DateTime.Now:HH:mm:ss}] {message}");
            if (CommandLogs.Count > 100)
                CommandLogs.RemoveAt(CommandLogs.Count - 1);
            CommandLogsText = string.Join("\n", CommandLogs);
        });
    }

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
    {
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
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