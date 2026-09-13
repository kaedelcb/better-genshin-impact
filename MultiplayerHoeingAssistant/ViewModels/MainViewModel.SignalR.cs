using System.Windows;
using MultiplayerHoeingAssistant.Services;
using Timer = System.Threading.Timer;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private async Task ConnectSignalRAsync()
    {
        // 离线优先：手动单机或服务器地址留空 = 单机模式，完全不发起连接（不连接/不重试/不刷失败日志）。
        // 本地状态采集循环独立于本方法运行（InitializeAsync 里已启动），槲寄生等本地功能不受影响。
        if (IsStandaloneMode)
        {
            IsConnected = false;
            AddLog("单机模式（手动单机或未填服务器地址）：跳过服务器连接，槲寄生等本地功能不受影响");
            return;
        }
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

            // 启动定时上报（每10秒）；通常已在 InitializeAsync 启动，此处幂等兜底
            EnsureStatusTimerStarted();
        }
        catch (Exception ex)
        {
            // 连接在飞时用户切到离线：失败属预期（连接被 Dispose），不再起重试
            if (IsStandaloneMode)
            {
                AddLog("单机模式已开启，停止服务器连接尝试");
                return;
            }
            AddLog($"连接失败: {ex.Message}，10 秒后自动重试");
            // WithAutomaticReconnect 不重试"首次连接失败"：这里每 10 秒重试，直到连上
            _retryTimer = new Timer(async _ =>
            {
                // 上次重试的 StartAsync 可能还挂着（假死服务器协商超时 ~100s），不叠加（详见 _retryRunning 注释）
                if (Interlocked.CompareExchange(ref _retryRunning, 1, 0) != 0) return;
                // 重试在飞时用户切到离线：本轮放弃（定时器本身已由 GoStandaloneAsync 销毁）
                if (IsStandaloneMode) return;
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
                        EnsureStatusTimerStarted();
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
    /// 总开关：手动切换单机/在线（标题栏按钮，弹窗确认）。切单机：断连 + 停重试 + 持久化；
    /// 切在线：持久化后立即发起连接。本地状态采集循环（_statusTimer）两种模式下都保持运行。
    /// </summary>
    private async Task ToggleStandaloneModeAsync()
    {
        if (_config == null || _configManager == null) return;
        if (IsStandaloneMode)
        {
            if (string.IsNullOrWhiteSpace(_config.ServerUrl))
            {
                AddLog("未配置服务器地址，无法切换在线；请先在「设置」中填写服务器地址");
                return;
            }
            if (!ShowSwitchConfirmDialog("切换为在线模式",
                "切换为在线模式后，将连接服务器，耕地机、嘟嘟可、槲寄生的联机功能恢复可用。是否继续切换？")) return;
            _config.StandaloneMode = false;
            _configManager.Save(_config);
            NotifyModeBindings();
            AddLog("已切换到在线模式，正在连接服务器...");
            await ConnectSignalRAsync();
        }
        else
        {
            if (!ShowSwitchConfirmDialog("切换为单机模式",
                "切换为单机模式后，将断开服务器连接，耕地机、嘟嘟可、槲寄生的联机功能全部不可用（本机功能照常）。是否继续切换？")) return;
            _config.StandaloneMode = true;
            _configManager.Save(_config);
            NotifyModeBindings();
            await GoStandaloneAsync("已手动切换到单机模式：服务器连接已断开，本地功能（槲寄生/本机日志）不受影响");
        }
    }

    /// <summary>转到单机态：停重试定时器并销毁 SignalR 客户端（复用 RefreshAsync 的 Dispose 路径语义）。
    /// 注意刻意不停 _statusTimer：本地状态采集单机时也必须继续（LatestLocalStatus 是槲寄生条件/电子狗的数据源）。</summary>
    private async Task GoStandaloneAsync(string reason)
    {
        _retryTimer?.Dispose();
        _retryTimer = null;
        var old = _signalRClient;
        _signalRClient = null;
        if (old != null)
        {
            await old.DisposeAsync();
        }
        IsConnected = false;
        AddLog(reason);
    }

    /// <summary>设置保存后的单机态同步：清空服务器地址 = 恒定单机模式，若仍连着服务器则断开；
    /// 反向（补填了地址）不自动重连，保持既有"刷新/重启后生效"行为，仅提示。</summary>
    private void SyncStandaloneStateAfterSettingsSaved()
    {
        NotifyModeBindings();
        if (IsStandaloneMode && _signalRClient != null)
        {
            _ = GoStandaloneAsync("服务器地址已清空：已转入恒定单机模式，服务器连接已断开，本地功能不受影响");
        }
        else if (!IsStandaloneMode && _signalRClient == null && _retryTimer == null)
        {
            AddLog("配置已具备联机条件，点标题栏「切到在线」或「刷新」建立连接");
        }
    }
}
