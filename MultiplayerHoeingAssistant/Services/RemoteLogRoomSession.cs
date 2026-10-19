using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>日志面板专用旁观连接。不注册执行端、不上报状态、不订阅任务命令，也不接触本房间连接。</summary>
internal sealed class RemoteLogRoomSession : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly Dictionary<string, ControlRoomPlayer> _players = new();
    private readonly TaskCompletionSource<string?> _firstSnapshot = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _joined;
    private bool _disposed;
    private long _revision;

    public SignalRClient Client { get; } = new();
    public string RoomCode { get; }
    public string? Error { get; private set; }
    public bool IsReady { get { lock (_gate) return !_disposed && _joined && Client.IsConnected; } }
    public event Action? Changed;

    private RemoteLogRoomSession(string roomCode)
    {
        RoomCode = roomCode;
        Client.OnPlayersUpdated += UpdatePlayers;
        Client.OnJoinRejected += Reject;
        Client.OnConnectionStateChanged += ConnectionChanged;
    }

    public static async Task<RemoteLogRoomSession> ConnectAsync(string serverUrl, string roomCode,
        string password, bool bypassSystemProxy, CancellationToken cancellationToken)
    {
        var session = new RemoteLogRoomSession(roomCode);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        timeout.CancelAfter(TimeSpan.FromSeconds(15));
        try
        {
            // 独立身份 + isRemote：即使与执行端同机，也不会挤掉执行端或进入其任务目标列表。
            await session.Client.ConnectAsync(serverUrl, roomCode, password,
                "log_viewer_" + Guid.NewGuid().ToString("N"), "日志协助", [], isRemote: true,
                bypassSystemProxy: bypassSystemProxy, cancellationToken: timeout.Token);
            // join 的 ACK 不代表认证成功；只有收到认证后的完整成员快照才切换面板。
            var rejection = await session._firstSnapshot.Task.WaitAsync(timeout.Token);
            if (rejection != null) throw new InvalidOperationException(rejection);
            if (!session.IsReady) throw new InvalidOperationException(session.Error ?? "协助房间连接已断开");
            return session;
        }
        catch
        {
            await session.DisposeAsync();
            throw;
        }
    }

    /// <summary>批量收集使用完整成员快照，离线成员也须列入结果。</summary>
    public List<ControlRoomPlayer> PlayersSnapshot()
    {
        lock (_gate)
            return _joined ? _players.Values.OrderBy(p => p.PlayerName).ToList() : [];
    }

    public List<ControlRoomPlayer> OnlinePlayers()
    {
        lock (_gate)
            return _joined ? _players.Values.Where(p => p.Online).OrderBy(p => p.PlayerName).ToList() : [];
    }

    private void UpdatePlayers(ControlRoomPlayersUpdate update)
    {
        lock (_gate)
        {
            if (_disposed) return;
            if (!update.Full && (!_joined || update.Revision > 0 && update.Revision <= _revision)) return;
            if (update.Full) _players.Clear();
            foreach (var player in (update.Full ? update.Players : update.Changed) ?? [])
                if (!string.IsNullOrWhiteSpace(player.PlayerUid)) _players[player.PlayerUid] = player;
            foreach (var uid in update.Removed ?? []) _players.Remove(uid);
            _revision = update.Revision;
            _joined = true;
            Error = null;
        }
        if (update.Full) _firstSnapshot.TrySetResult(null);
        Changed?.Invoke();
    }

    private void Reject(string reason)
    {
        lock (_gate)
        {
            _joined = false;
            _players.Clear();
            Error = "连接被拒绝：" + reason;
        }
        _firstSnapshot.TrySetResult(Error);
        Changed?.Invoke();
    }

    private void ConnectionChanged(bool connected)
    {
        if (!connected)
            lock (_gate)
            {
                _joined = false;
                _players.Clear();
                _revision = 0;
                Error = "协助房间已断线，等待重连；也可返回本房间";
            }
        Changed?.Invoke();
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; _joined = false; }
        Client.OnPlayersUpdated -= UpdatePlayers;
        Client.OnJoinRejected -= Reject;
        Client.OnConnectionStateChanged -= ConnectionChanged;
        await Client.DisposeAsync();
    }
}
