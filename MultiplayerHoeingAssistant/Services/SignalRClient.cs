using Microsoft.AspNetCore.SignalR.Client;
using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

public class SignalRClient : IAsyncDisposable
{
    private HubConnection? _connection;
    private string _roomCode = string.Empty;
    private string _playerUid = string.Empty;
    private string _playerName = string.Empty;

    // 持久的连接参数：Closed（自动重连耗尽）后自愈重连循环需要它们重建连接
    private string _serverUrl = string.Empty;
    private string _password = string.Empty;
    private List<string> _teamUids = new();

    // 自愈重连循环的状态：防止同一个 client 里并发跑多个重连循环
    private readonly object _reconnectLock = new();
    private bool _reconnectLoopRunning;
    private bool _disposed;

    public event Action<List<ControlRoomPlayer>>? OnPlayersUpdated;
    public event Action<RemoteCommand>? OnRemoteCommand;
    public event Action<string>? OnJoinRejected;
    public event Action<bool>? OnConnectionStateChanged;

    public bool IsConnected => _connection?.State == HubConnectionState.Connected;

    public async Task ConnectAsync(string serverUrl, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids)
    {
        _roomCode = roomCode;
        _playerUid = playerUid;
        _playerName = playerName;
        _serverUrl = serverUrl;
        _password = password;
        _teamUids = teamUids;

        await EstablishAsync(serverUrl, roomCode, password, playerUid, playerName, teamUids);
    }

    /// <summary>
    /// 建立一条完整的 SignalR 连接：创建 HubConnection、注册事件、StartAsync、加入控制房间。
    /// 每次调用都会新建 connection，因此事件处理器必须在这里重新注册到最新连接上。
    /// </summary>
    private async Task EstablishAsync(string serverUrl, string roomCode, string password,
        string playerUid, string playerName, List<string> teamUids)
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
            System.Diagnostics.Debug.WriteLine("SignalR 已重连，重新加入控制房间");
            await connection.InvokeAsync("JoinControlRoom", roomCode, password, playerUid, playerName, teamUids);
            OnConnectionStateChanged?.Invoke(true);
        };

        // 连接彻底断开（SignalR 内置自动重连已耗尽）。
        // 注意 1：异常参数绝不能忽略——SignalR 在 ServerTimeout（30s 无心跳）等场景会把
        //   TimeoutException 传到这里，不"观察"会作为未观察任务异常冒泡到全局
        //   TaskScheduler.UnobservedTaskException → App 弹"未处理异常"框，非常粗暴。
        //   这里吞掉并转状态通知（IsConnected=false，徽章变"离线"）+ 记日志。
        // 注意 2：内置自动重连只配了 4 个间隔，耗尽后就走这里；必须启动自愈循环持续重建连接，
        //   否则就永久停在"离线"（除非手动重启助手）。
        connection.Closed += exception =>
        {
            if (exception != null)
            {
                System.Diagnostics.Debug.WriteLine($"SignalR 连接已关闭: {exception.Message}");
            }
            var closed = connection;
            OnConnectionStateChanged?.Invoke(false);
            _ = ReconnectLoopAsync(closed);
            return Task.CompletedTask;
        };

        await connection.StartAsync();
        await connection.InvokeAsync("JoinControlRoom", roomCode, password, playerUid, playerName, teamUids);

        // 必须在 StartAsync + JoinControlRoom 全部成功之后才把 _connection 指向新连接。
        // 若提前赋值、StartAsync 又失败，_connection 会指向"失败的新连接"，
        // 导致自愈循环里 closedConnection != _connection 判断提前 return、放弃重连。
        _connection = connection;
    }

    /// <summary>
    /// 自愈重连循环：内置自动重连耗尽（Closed）后，周期性地重建连接，直到连上为止。
    /// 每次只允许一个循环在跑（同一时刻只重建一次连接，避免并发连接互相干扰）。
    /// </summary>
    private async Task ReconnectLoopAsync(HubConnection closedConnection)
    {
        // 只有在"这个 Closed 连接仍旧是当前连接"时才需要重连；
        // 如果期间已有新的连接建立（如手动 ConnectAsync 成功），则放弃本次重建。
        if (closedConnection != _connection) return;

        lock (_reconnectLock)
        {
            if (_reconnectLoopRunning) return;
            _reconnectLoopRunning = true;
        }

        try
        {
            // 重建的间隔不宜过短，避免在服务器持续不可用时期疯狂打请求
            const int delayMs = 10_000;
            while (!_disposed)
            {
                await Task.Delay(delayMs);
                if (_disposed || !ReferenceEquals(closedConnection, _connection)) return;

                try
                {
                    var serverUrl = _serverUrl;
                    var roomCode = _roomCode;
                    var password = _password;
                    var playerUid = _playerUid;
                    var playerName = _playerName;
                    var teamUids = _teamUids;

                    await closedConnection.DisposeAsync();
                    await EstablishAsync(serverUrl, roomCode, password, playerUid, playerName, teamUids);

                    OnConnectionStateChanged?.Invoke(true);
                    return; // 连上后退出循环
                }
                catch (Exception ex)
                {
                    System.Diagnostics.Debug.WriteLine($"SignalR 自愈重连失败，稍后重试: {ex.Message}");
                }
            }
        }
        finally
        {
            lock (_reconnectLock)
            {
                _reconnectLoopRunning = false;
            }
        }
    }

    public async Task SendRemoteCommandAsync(RemoteCommand command)
    {
        if (_connection == null) return;
        command.RoomCode = _roomCode;
        await _connection.InvokeAsync("SendRemoteCommand", command);
    }

    public async Task ReportControlStatusAsync(ControlStatus status)
    {
        if (_connection == null) return;
        status.RoomCode = _roomCode;
        await _connection.InvokeAsync("ReportControlStatus", status);
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