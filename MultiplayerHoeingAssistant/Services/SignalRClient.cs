using Microsoft.AspNetCore.SignalR.Client;
using MultiplayerHoeingAssistant.Models;

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
        // 说明：重连完全依赖 SignalR 内置 WithAutomaticReconnect（0s/2s/10s/30s 四次尝试），
        //   不再叠加自定义自愈循环。曾有过 ReconnectLoopAsync 自愈循环与内置重连并存，两者并发
        //   导致竞态：自愈循环在内置重连刚恢复后 Dispose 掉新连接、且重建失败时旧连接已被销毁
        //   → 彻底离线无法控制。故移除自愈循环，只保留内置重连；若内置重连耗尽（服务器长期不可用），
        //   状态仍会置为离线，用户可手动刷新（RefreshAsync）或手动重连恢复。
        connection.Closed += exception =>
        {
            if (exception != null)
            {
                System.Diagnostics.Debug.WriteLine($"SignalR 连接已关闭: {exception.Message}");
            }
            OnConnectionStateChanged?.Invoke(false);
            return Task.CompletedTask;
        };

        await connection.StartAsync();
        await connection.InvokeAsync("JoinControlRoom", roomCode, password, playerUid, playerName, teamUids, isRemote, _clientInstanceId);

        // 必须在 StartAsync + JoinControlRoom 全部成功之后才把 _connection 指向新连接。
        // 若提前赋值、StartAsync 又失败，_connection 会指向"失败的新连接"，
        // 导致自愈循环里 closedConnection != _connection 判断提前 return、放弃重连。
        _connection = connection;
    }

    public async Task SendRemoteCommandAsync(RemoteCommand command)
    {
        if (_connection == null) return;
        command.RoomCode = _roomCode;
        await _connection.InvokeAsync("SendRemoteCommand", command);
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
        status.RoomCode = _roomCode;
        await _connection.InvokeAsync("ReportControlStatus", status);
    }

    /// <summary>上报上线事件（带 generation 代序号，供服务端状态机边沿检测）。</summary>
    public async Task ReportOnlineEventAsync(int generation, bool isOnlineReady)
    {
        if (_connection == null) return;
        try
        {
            await _connection.InvokeAsync("ReportOnlineEvent", generation, isOnlineReady);
        }
        catch (Exception ex)
        {
            OnLog?.Invoke($"ReportOnlineEventAsync 调用失败: {ex.Message}");
            throw;
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
        if (_connection != null)
        {
            await _connection.DisposeAsync();
            _connection = null;
        }
    }
}