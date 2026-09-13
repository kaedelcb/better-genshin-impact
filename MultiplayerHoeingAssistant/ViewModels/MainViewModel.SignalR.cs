using System.Windows;
using MultiplayerHoeingAssistant.Services;
using Timer = System.Threading.Timer;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    /// <summary>
    /// [F9-2] 三份复刻收拢的共用原语（首连 ConnectSignalRAsync / 刷新 RefreshAsync / 失败重试共用）：
    /// 创建已接线的 SignalR 客户端——事件订阅（WireSignalRClient）与连接状态回调统一在此，
    /// 新增回调只改这一处。历史上三处复刻各自打补丁（幽灵连接/假死/单机竞态）互不同步，是本族 bug 的温床。
    /// </summary>
    private SignalRClient CreateWiredSignalRClient()
    {
        var client = new SignalRClient();
        WireSignalRClient(client);
        // 连接状态变化（断开/重连）同步到 IsConnected → 标题栏连接徽章实时刷新
        client.OnConnectionStateChanged += connected =>
        {
            Application.Current.Dispatcher.Invoke(() => IsConnected = connected);
            // 连接恢复后立即上报状态，无需等待 10 秒定时器
            if (connected) _ = ReportStatusAsync();
            // [B157] 断线时服务端已清零本成员上线事件，重连后补报上线意图（服务端按 gen 去重，安全）
            if (connected) _ = ReReportOnlineIntentIfNeededAsync();
        };
        return client;
    }

    /// <summary>[F9-2] 用当前配置发起连接（三处调用点的参数列表统一在此）。</summary>
    private Task ConnectSignalRWithConfigAsync(SignalRClient client) =>
        client.ConnectAsync(
            _config!.ServerUrl, RoomCode, _config.ControlRoomPassword,
            _config.PlayerUid, _config.PlayerName, _config.TeamUids, _config.ObserverMode, _config.ClientInstanceId,
            _config.BypassSystemProxy);

    /// <summary>
    /// [F9-2] 连接成功后的统一接管：所有权移交 _signalRClient + 徽章/日志（Dispatcher 内）+
    /// 可选 RefreshCompleted（仅刷新语义的路径触发，首连不触发——保持原行为）+ 状态上报 + 定时器幂等兜底。
    /// </summary>
    private async Task AdoptConnectedSignalRClientAsync(SignalRClient client, string logMessage, bool fireRefreshCompleted)
    {
        // 所有权移交与徽章/日志一起在 Dispatcher 内完成：Invoke 抛异常（关机竞态）时
        // _signalRClient 未被污染，调用方 finally 会正确销毁这个半成品客户端
        Application.Current.Dispatcher.Invoke(() =>
        {
            _signalRClient = client;
            IsConnected = true;
            AddLog(logMessage);
            // 统一在 Dispatcher 内触发：原刷新重试路径从线程池线程直接 Invoke，有跨线程碰 UI 的隐患
            if (fireRefreshCompleted) RefreshCompleted?.Invoke();
        });
        // 启动定时上报（每10秒）；通常已在 InitializeAsync 启动，此处幂等兜底
        EnsureStatusTimerStarted();
        await ReportStatusAsync(); // 内部全容错不抛（断线仅记日志），不会逃逸进调用方的销毁路径
    }

    /// <summary>
    /// [F9-2] 连接失败后的 10s 重试定时器（首连失败 / 刷新失败共用）。
    /// WithAutomaticReconnect 不重试"首次连接失败"，故由本定时器每 10 秒重建直到连上。
    /// 语义取刷新重试的加固版：每轮新建客户端（复用失败实例是幽灵连接温床）；
    /// StartAsync 在飞期间（假死服务器协商可挂 ~100s）不叠加（_retryRunning）；
    /// 在飞期间用户切单机 → 本轮放弃；在飞期间已有在位连接接管 → 本定时器自我销毁（原刷新重试空转不自销）。
    /// </summary>
    private void StartSignalRRetryTimer(bool fireRefreshCompletedOnSuccess)
    {
        _retryTimer?.Dispose();
        _retryTimer = new Timer(async _ =>
        {
            // 上次重试的 StartAsync 可能还挂着（假死服务器协商超时 ~100s），不叠加（详见 _retryRunning 注释）
            if (Interlocked.CompareExchange(ref _retryRunning, 1, 0) != 0) return;
            SignalRClient? client = null;
            try
            {
                // 重试在飞时用户切到离线：本轮放弃（定时器本身已由 GoStandaloneAsync 销毁）
                if (IsStandaloneMode) return;
                // 已有在位连接（刷新/自愈已接管）：本定时器使命完成，自我销毁
                if (_signalRClient != null || _config == null)
                {
                    _retryTimer?.Dispose();
                    _retryTimer = null;
                    return;
                }
                client = CreateWiredSignalRClient();
                await ConnectSignalRWithConfigAsync(client);
                if (_signalRClient != null)
                {
                    // 本次 StartAsync 在途期间（最长 ~100s）刷新已重建成功——不能接管 _signalRClient
                    // （会把在位连接挤成无人持有的幽灵连接），本次成果交 finally 销毁；
                    // 在位连接已负责状态，本定时器自我销毁
                    _retryTimer?.Dispose();
                    _retryTimer = null;
                    return;
                }
                // 连接在飞期间用户切到离线：销毁刚到手的连接，不接管，等 GoStandaloneAsync 收场
                if (IsStandaloneMode) return;
                await AdoptConnectedSignalRClientAsync(client, "已重新连接控制房间", fireRefreshCompletedOnSuccess);
                client = null; // 所有权已移交 _signalRClient
                _retryTimer?.Dispose();
                _retryTimer = null;
            }
            catch (Exception retryEx)
            {
                // 重试失败保持定时器继续，但失败原因必须可见——"连不回来"时靠它区分
                // 服务器不可达（网络/DNS/服务挂）与服务器拒绝（密码/白名单/协议不兼容）
                AddLog($"连接重试失败: {retryEx.Message}（10 秒后继续）");
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
            // [F9-2] 连接成功才接管 _signalRClient（原实现连接前就赋值，失败会留下非 null 的死实例）
            var client = CreateWiredSignalRClient();
            await ConnectSignalRWithConfigAsync(client);
            // 连接在飞期间（最长 ~100s）用户切到离线：销毁刚到手的连接，不接管
            // （原首连路径缺此检查——刷新路径有，收拢时对齐；否则会切单机后仍被在飞首连接管）
            if (IsStandaloneMode)
            {
                await client.DisposeAsync();
                AddLog("单机模式已开启，放弃本次建立的连接");
                return;
            }
            await AdoptConnectedSignalRClientAsync(client, "已连接控制房间", fireRefreshCompleted: false);
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
            StartSignalRRetryTimer(fireRefreshCompletedOnSuccess: false);
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
