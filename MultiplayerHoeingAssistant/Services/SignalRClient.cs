using Microsoft.AspNetCore.SignalR.Client;
using MultiplayerHoeingAssistant.Models;

using Timer = System.Threading.Timer;

namespace MultiplayerHoeingAssistant.Services;

public class SignalRClient : IAsyncDisposable
{
    private HubConnection? _connection;
    private string _roomCode = string.Empty;
    private string _playerUid = string.Empty;
    private string _playerName = string.Empty;
    private bool _isRemote;
    private string _clientInstanceId = "";

    // 持久的连接参数：供手动 RefreshAsync 重建连接使用
    private string _serverUrl = string.Empty;
    private string _password = string.Empty;
    private List<string> _teamUids = new();

    private bool _disposed;

    // [P1-F 止血] 自愈定时器：仅在内置重连耗尽（Closed）后启动，每 30s 对同一连接 StartAsync。
    // 同一时刻只允许一条自愈定时器；_selfHealRunning 防止定时器回调重入（StartAsync 超 30s 时）。
    private Timer? _selfHealTimer;
    private int _selfHealRunning;

    public event Action<List<ControlRoomPlayer>>? OnPlayersUpdated;
    public event Action<RemoteCommand>? OnRemoteCommand;
    public event Action<string>? OnJoinRejected;
    public event Action<bool>? OnConnectionStateChanged;
    /// <summary>全员就绪确认完成事件（各助手据此启动中断流程）。带 generation 参数，用于幂等保护。</summary>
    public event Action<int>? OnAllReadyConfirmed;
    /// <summary>收到 AllReadyConfirm 事件（服务端要求确认就绪，确认阶段用）。</summary>
    public event Action<int>? OnAllReadyConfirmReceived;
    /// <summary>日志回调（供外部输出探针日志）</summary>
    public Action<string>? OnLog { get; set; }

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string serverUrl, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids, bool isRemote = false, string clientInstanceId = "")
    {
        _roomCode = roomCode;
        _playerUid = playerUid;
        _playerName = playerName;
        _isRemote = isRemote;
        _clientInstanceId = clientInstanceId ?? "";
        _serverUrl = serverUrl;
        _password = password;
        _teamUids = teamUids;

        await EstablishAsync(serverUrl, roomCode, password, playerUid, playerName, teamUids, isRemote);
    }

    /// <summary>
    /// 建立一条完整的 SignalR 连接：创建 HubConnection、注册事件、StartAsync、加入控制房间。
    /// 每次调用都会新建 connection，因此事件处理器必须在这里重新注册到最新连接上。
    /// </summary>
    private async Task EstablishAsync(string serverUrl, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids, bool isRemote)
    {
        var connection = new HubConnectionBuilder()
            .WithUrl($"{serverUrl}/hub")
            .WithAutomaticReconnect(new[] { TimeSpan.Zero, TimeSpan.FromSeconds(2), TimeSpan.FromSeconds(10), TimeSpan.FromSeconds(30) })
            .Build();

        connection.On<List<ControlRoomPlayer>>("ControlRoomPlayersUpdated", players =>
            OnPlayersUpdated?.Invoke(players));
        connection.On<RemoteCommand>("RemoteCommand", cmd =>
            OnRemoteCommand?.Invoke(cmd));
        connection.On<string>("JoinRejected", reason =>
            OnJoinRejected?.Invoke(reason));
        connection.On<int>("AllReady", generation =>
        {
            OnLog?.Invoke("[探针助手] SignalRClient 收到 AllReady 事件, generation=" + generation);
            OnAllReadyConfirmed?.Invoke(generation);
        });
        connection.On<int>("AllReadyConfirm", generation =>
        {
            OnAllReadyConfirmReceived?.Invoke(generation);
        });

        // 重连中（SignalR 内置自动重连尝试期间）
        connection.Reconnecting += _ =>
        {
            System.Diagnostics.Debug.WriteLine("SignalR 重连中...");
            OnConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        // SignalR 内置自动重连成功
        connection.Reconnected += async _ =>
        {
            try
            {
                System.Diagnostics.Debug.WriteLine("SignalR 已重连，重新加入控制房间");
                await connection.InvokeAsync("JoinControlRoom", roomCode, password, playerUid, playerName, teamUids, isRemote, _clientInstanceId);
                OnConnectionStateChanged?.Invoke(true);
            }
            catch (Exception ex)
            {
                // Reconnected 是 async void lambda，异常若不在此捕获会冒泡到全局
                // TaskScheduler.UnobservedTaskException → App.xaml.cs 弹"任务异常"框，非常粗暴。
                // 重连成功后 JoinControlRoom 偶发失败（如房间已被服务端回收），仅记日志，等待下次重连。
                System.Diagnostics.Debug.WriteLine($"SignalR 重连后加入房间失败: {ex.Message}");
            }
        };

        // 连接断开。内置自动重连期间会走 Reconnecting/Reconnected，最终耗尽后才来到这里。
        // 注意：异常参数绝不能忽略——SignalR 在 ServerTimeout（30s 无心跳）等场景会把
        //   TimeoutException 传到这里，不"观察"会作为未观察任务异常冒泡到全局
        //   TaskScheduler.UnobservedTaskException → App 弹"未处理异常"框，非常粗暴。
        //   这里吞掉并转状态通知（IsConnected=false，徽章变"离线"）。
        // 血泪教训：自愈绝不能与内置重连并存——曾有过 ReconnectLoopAsync 自愈循环与内置重连并发，
        //   两者竞态：自愈循环在内置重连刚恢复后 Dispose 掉新连接、且重建失败时旧连接已被销毁
        //   → 彻底离线无法控制。因此自愈只允许在 Closed（内置重连 0s/2s/10s/30s 四次尝试耗尽）之后
        //   启动，且始终对同一连接 StartAsync，绝不 Dispose/重建连接。
        connection.Closed += exception =>
        {
            if (exception != null)
            {
                System.Diagnostics.Debug.WriteLine($"SignalR 连接已关闭: {exception.Message}");
            }
            OnConnectionStateChanged?.Invoke(false);
            // [P1-F 止血] 内置重连已耗尽，启动低频自愈：每 30s 对同一连接 StartAsync，
            // 成功后停止自愈并重新入房。避免"网络抖动 >42s 或服务器重启后助手永久离线"。
            StartSelfHeal(connection, roomCode, password, playerUid, playerName, teamUids, isRemote);
            return Task.CompletedTask;
        };

        await connection.StartAsync();
        await connection.InvokeAsync("JoinControlRoom", roomCode, password, playerUid, playerName, teamUids, isRemote, _clientInstanceId);

        // 必须在 StartAsync + JoinControlRoom 全部成功之后才把 _connection 指向新连接。
        // 若提前赋值、StartAsync 又失败，_connection 会指向"失败的新连接"，
        // 导致自愈循环里 closedConnection != _connection 判断提前 return、放弃重连。
        _connection = connection;
    }

    /// <summary>
    /// [P1-F 止血] Closed（内置重连耗尽）后启动自愈定时器：每 30s 对同一 HubConnection 调 StartAsync。
    /// 成功后停止定时器、触发 OnConnectionStateChanged(true) 并重新 JoinControlRoom（入房失败仅记日志）。
    /// 严禁在内置重连进行期间启动（历史竞态教训，见 Closed 注册处注释）；Closed 即代表内置重连已耗尽，此刻启动安全。
    /// 所有异常在回调内捕获，不得冒泡到 TaskScheduler.UnobservedTaskException。
    /// </summary>
    private void StartSelfHeal(HubConnection closedConnection, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids, bool isRemote)
    {
        if (_disposed) return;
        // 同一时刻只允许一条自愈定时器（如对旧连接的残留），先停掉再建
        StopSelfHeal();
        _selfHealTimer = new Timer(async _ =>
        {
            if (Interlocked.CompareExchange(ref _selfHealRunning, 1, 0) != 0) return;
            try
            {
                if (_disposed) return;
                // 连接已被替换（如用户手动 RefreshAsync 重建了新连接）或已恢复，放弃自愈
                if (!ReferenceEquals(closedConnection, _connection)
                    || closedConnection.State == HubConnectionState.Connected)
                {
                    StopSelfHeal();
                    return;
                }
                OnLog?.Invoke("[自愈] SignalR 内置重连已耗尽，尝试重新连接...");
                await closedConnection.StartAsync();
                // 重连成功：停止自愈，恢复在线状态，并重新加入控制房间（复用 Reconnected 的入房逻辑）
                StopSelfHeal();
                OnConnectionStateChanged?.Invoke(true);
                try
                {
                    await closedConnection.InvokeAsync("JoinControlRoom", roomCode, password, playerUid, playerName, teamUids, isRemote, _clientInstanceId);
                    OnLog?.Invoke("[自愈] SignalR 重连成功，已重新加入控制房间");
                }
                catch (Exception ex)
                {
                    // 入房失败（如房间已被服务端回收）仅记日志，连接本身已恢复
                    OnLog?.Invoke($"[自愈] SignalR 重连成功但加入房间失败: {ex.Message}");
                }
            }
            catch (Exception ex)
            {
                // 重连失败（服务器仍不可达等），仅记日志，等下一个 30s 周期
                OnLog?.Invoke($"[自愈] SignalR 重连失败: {ex.Message}");
            }
            finally
            {
                _selfHealRunning = 0;
            }
        }, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
    }

    /// <summary>停止并释放自愈定时器（幂等）。</summary>
    private void StopSelfHeal()
    {
        var timer = Interlocked.Exchange(ref _selfHealTimer, null);
        timer?.Dispose();
    }

    public async Task SendRemoteCommandAsync(RemoteCommand command)
    {
        if (_connection == null) return;
        command.RoomCode = _roomCode;
        try
        {
            await _connection.InvokeAsync("SendRemoteCommand", command);
        }
        catch (Exception ex)
        {
            // 发送瞬间断连等异常仅记日志不 throw：调用方多在 async void 事件处理器里，上抛会导致进程崩溃
            OnLog?.Invoke($"[探针助手] SendRemoteCommand({command.Cmd}) 调用失败: " + ex.Message);
        }
    }

    public async Task ConfirmAllReadyAsync(int generation)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ConfirmAllReady", generation);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke("[探针助手] ConfirmAllReady 调用失败: " + ex.Message);
        }
    }

    public async Task ReportControlStatusAsync(ControlStatus status)
    {
        if (_connection == null) return;
        if (_connection.State != HubConnectionState.Connected)
        {
            return; // 连接未就绪时静默跳过，避免连接断开瞬间大量并发调用被取消产生"状态上报失败"日志风暴
        }
        try
        {
            status.RoomCode = _roomCode;
            await _connection.InvokeAsync("ReportControlStatus", status);
        }
        catch (Exception ex)
        {
            // 连接断开时 InvokeAsync 会抛异常（TaskCanceledException / InvalidOperationException），
            // 仅记一条日志不再 throw；调用方 ReportStatusAsync 已有 catch 兜底，但门控+不 throw 可避免日志风暴。
            OnLog?.Invoke($"ReportControlStatusAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>上报上线事件（带 generation 代序号，供服务端状态机边沿检测）。</summary>
    public async Task ReportOnlineEventAsync(int generation, bool isOnlineReady)
    {
        if (_connection == null) return;
        if (_connection.State != HubConnectionState.Connected)
        {
            OnLog?.Invoke($"ReportOnlineEventAsync 跳过: 连接未就绪（State={_connection.State}）");
            return;
        }
        try
        {
            await _connection.InvokeAsync("ReportOnlineEvent", generation, isOnlineReady);
        }
        catch (Exception ex)
        {
            // 连接断开时 InvokeAsync 会抛异常（TaskCanceledException / InvalidOperationException），
            // 此处仅记日志不再 throw，避免上游调用方（如 ReportStatusAsync）连锁打印大量"状态上报失败"日志形成风暴。
            // 断线状态已由 Closed 事件同步 IsConnected=false，调用方也可通过 IsConnected 自行判断。
            OnLog?.Invoke($"ReportOnlineEventAsync 调用失败: {ex.Message}");
        }
    }

    /// <summary>清除指定成员的 OnlineHistory（已联机记录），由本人或房主调用。</summary>
    public async Task ClearOnlineHistoryAsync(string targetUid)
    {
        if (_connection == null)
        {
            OnLog?.Invoke("[清除记录] 清除失败: SignalR 未连接（_connection == null）");
            return;
        }
        try
        {
            await _connection.InvokeAsync("ClearOnlineHistory", targetUid);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"[清除记录] ClearOnlineHistoryAsync 调用失败: {ex.Message}");
        }
    }

    public async ValueTask DisposeAsync()
    {
        _disposed = true;
        // 连接对象 Dispose 时一并停掉自愈定时器，避免对已释放连接 StartAsync
        StopSelfHeal();
        if (_connection != null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}