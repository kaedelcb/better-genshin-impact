using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>耕地机多房间只读旁观会话接口（便于单元测试注入模拟房间数据）。</summary>
internal interface IRoomSpectatorSession : IAsyncDisposable
{
    string ProfileId { get; }
    string ServerUrl { get; }
    string RoomCode { get; }
    string Password { get; }
    bool BypassSystemProxy { get; }
    bool IsConnected { get; }
    bool IsConnecting { get; }
    string? Error { get; }
    event Action? Changed;
    IReadOnlyList<ControlRoomPlayer> GetPlayersSnapshot();
    void Start();
    void ForceReconnect();
}

/// <summary>
/// 耕地机多房间只读旁观连接。
/// 使用独立 web_logviewer_ 旁观身份与 isRemote=true 加入目标控制房间，不注册执行端、不上报状态、
/// 不订阅任务命令，也不会挤掉同机或异地的真实执行端。
/// </summary>
internal sealed class RoomSpectatorSession : IRoomSpectatorSession
{
    private static readonly TimeSpan ConnectTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan RetryDelay = TimeSpan.FromSeconds(15);

    private readonly object _gate = new();
    private readonly List<string> _playerOrder = [];
    private readonly Dictionary<string, ControlRoomPlayer> _players = new(StringComparer.Ordinal);
    private readonly CancellationTokenSource _lifetimeCts = new();
    private SignalRClient? _client;
    private CancellationTokenSource? _reconnectCts;
    private int _loopRunning;
    private bool _joined;
    private bool _connected;
    private bool _connecting;
    private bool _disposed;
    private long _revision;

    public string ProfileId { get; }
    public string ServerUrl { get; }
    public string RoomCode { get; }
    public string Password { get; }
    public bool BypassSystemProxy { get; }
    public bool IsConnected { get { lock (_gate) return !_disposed && _joined && _connected; } }
    public bool IsConnecting { get { lock (_gate) return !_disposed && _connecting; } }
    public string? Error { get { lock (_gate) return _error; } }
    private string? _error;

    public event Action? Changed;

    public RoomSpectatorSession(string profileId, string serverUrl, string roomCode, string password, bool bypassSystemProxy)
    {
        ProfileId = profileId;
        ServerUrl = serverUrl;
        RoomCode = roomCode;
        Password = password;
        BypassSystemProxy = bypassSystemProxy;
    }

    public IReadOnlyList<ControlRoomPlayer> GetPlayersSnapshot()
    {
        lock (_gate)
        {
            if (!_joined || _playerOrder.Count == 0) return [];
            var result = new List<ControlRoomPlayer>(_playerOrder.Count);
            foreach (var uid in _playerOrder)
            {
                if (_players.TryGetValue(uid, out var player)) result.Add(player);
            }
            return result;
        }
    }

    public void Start()
    {
        if (Volatile.Read(ref _disposed)) return;
        if (Interlocked.CompareExchange(ref _loopRunning, 1, 0) != 0) return;
        _ = Task.Run(() => RunConnectLoopAsync(_lifetimeCts.Token));
    }

    public void ForceReconnect()
    {
        SignalRClient? oldClient;
        CancellationTokenSource? oldWait;
        lock (_gate)
        {
            if (_disposed) return;
            oldClient = _client;
            _client = null;
            _connected = false;
            oldWait = _reconnectCts;
            _reconnectCts = null;
        }
        try { oldWait?.Cancel(); } catch { }
        if (oldClient != null)
        {
            DetachClient(oldClient);
            _ = SafeDisposeClientAsync(oldClient);
        }
        if (Interlocked.CompareExchange(ref _loopRunning, 1, 0) == 0)
        {
            _ = Task.Run(() => RunConnectLoopAsync(_lifetimeCts.Token));
        }
    }

    private async Task RunConnectLoopAsync(CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                if (!RoomServerAddress.TryNormalize(ServerUrl, out var normalizedServer))
                {
                    SetError("协调服务器地址无效");
                    return;
                }
                if (string.IsNullOrWhiteSpace(RoomCode) || string.IsNullOrEmpty(Password))
                {
                    SetError("房间号或密码未填写");
                    return;
                }

                SetConnecting(true, null);
                var client = new SignalRClient();
                var firstSnapshot = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);

                void OnPlayers(ControlRoomPlayersUpdate update) => HandlePlayersUpdated(client, update, firstSnapshot);
                void OnRejected(string reason) => HandleRejected(client, reason, firstSnapshot);
                void OnConnChanged(bool connected) => HandleConnectionChanged(client, connected);

                client.OnPlayersUpdated += OnPlayers;
                client.OnJoinRejected += OnRejected;
                client.OnConnectionStateChanged += OnConnChanged;

                lock (_gate)
                {
                    if (_disposed)
                    {
                        client.OnPlayersUpdated -= OnPlayers;
                        client.OnJoinRejected -= OnRejected;
                        client.OnConnectionStateChanged -= OnConnChanged;
                        _ = SafeDisposeClientAsync(client);
                        return;
                    }
                    _client = client;
                }

                var connectedOk = false;
                try
                {
                    using var timeoutCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
                    timeoutCts.CancelAfter(ConnectTimeout);
                    // 独立旁观身份 + isRemote=true：不注册执行端，不挤掉同机或异地真实成员。
                    await client.ConnectAsync(normalizedServer, RoomCode, Password,
                        "web_logviewer_" + Guid.NewGuid().ToString("N"), "房间旁观", [],
                        isRemote: true, bypassSystemProxy: BypassSystemProxy, cancellationToken: timeoutCts.Token);
                    var rejection = await firstSnapshot.Task.WaitAsync(timeoutCts.Token);
                    if (rejection != null)
                        throw new InvalidOperationException(rejection);
                    connectedOk = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await CleanupAttemptAsync(client, OnPlayers, OnRejected, OnConnChanged);
                    return;
                }
                catch (Exception ex)
                {
                    await CleanupAttemptAsync(client, OnPlayers, OnRejected, OnConnChanged);
                    var msg = ex is OperationCanceledException ? "连接超时" : ex.Message;
                    SetError(msg);
                }

                if (connectedOk)
                {
                    SetConnecting(false, null);
                    // SignalRClient 自身具备自动重连与 30s 自愈；只需等待 ForceReconnect 或 Dispose。
                    var waitCts = new CancellationTokenSource();
                    lock (_gate)
                    {
                        if (_disposed || !ReferenceEquals(_client, client))
                        {
                            waitCts.Dispose();
                            continue;
                        }
                        _reconnectCts = waitCts;
                    }
                    try
                    {
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, waitCts.Token);
                        await Task.Delay(Timeout.Infinite, linked.Token);
                    }
                    catch (OperationCanceledException) { }
                    finally
                    {
                        lock (_gate)
                        {
                            if (ReferenceEquals(_reconnectCts, waitCts)) _reconnectCts = null;
                        }
                        waitCts.Dispose();
                    }
                    continue;
                }

                // 首次连接失败：等待重试，期间允许 ForceReconnect 立即唤醒。
                var retryCts = new CancellationTokenSource();
                lock (_gate)
                {
                    if (_disposed) { retryCts.Dispose(); return; }
                    _reconnectCts = retryCts;
                }
                try
                {
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, retryCts.Token);
                    await Task.Delay(RetryDelay, linked.Token);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    // 被 ForceReconnect 唤醒，立即重连
                }
                finally
                {
                    lock (_gate)
                    {
                        if (ReferenceEquals(_reconnectCts, retryCts)) _reconnectCts = null;
                    }
                    retryCts.Dispose();
                }
            }
        }
        finally
        {
            Interlocked.Exchange(ref _loopRunning, 0);
        }
    }

    private async Task CleanupAttemptAsync(SignalRClient client,
        Action<ControlRoomPlayersUpdate> onPlayers, Action<string> onRejected, Action<bool> onConnChanged)
    {
        client.OnPlayersUpdated -= onPlayers;
        client.OnJoinRejected -= onRejected;
        client.OnConnectionStateChanged -= onConnChanged;
        lock (_gate)
        {
            if (ReferenceEquals(_client, client))
            {
                _client = null;
                _connected = false;
            }
        }
        await SafeDisposeClientAsync(client);
    }

    private void HandlePlayersUpdated(SignalRClient source, ControlRoomPlayersUpdate update,
        TaskCompletionSource<string?> firstSnapshot)
    {
        var notify = false;
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_client, source)) return;
            if (!update.Full && (!_joined || (update.Revision > 0 && update.Revision <= _revision))) return;
            if (update.Full)
            {
                _players.Clear();
                _playerOrder.Clear();
                foreach (var player in update.Players ?? [])
                {
                    if (string.IsNullOrWhiteSpace(player.PlayerUid)) continue;
                    if (!_players.ContainsKey(player.PlayerUid)) _playerOrder.Add(player.PlayerUid);
                    _players[player.PlayerUid] = player;
                }
            }
            else
            {
                foreach (var player in update.Changed ?? [])
                {
                    if (string.IsNullOrWhiteSpace(player.PlayerUid)) continue;
                    if (!_players.ContainsKey(player.PlayerUid)) _playerOrder.Add(player.PlayerUid);
                    _players[player.PlayerUid] = player;
                }
                foreach (var uid in update.Removed ?? [])
                {
                    if (_players.Remove(uid)) _playerOrder.Remove(uid);
                }
            }
            _revision = update.Revision;
            _joined = true;
            _connected = true;
            _connecting = false;
            _error = null;
            notify = true;
        }
        if (update.Full) firstSnapshot.TrySetResult(null);
        if (notify) Changed?.Invoke();
    }

    private void HandleRejected(SignalRClient source, string reason, TaskCompletionSource<string?> firstSnapshot)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_client, source)) return;
            _joined = false;
            _connected = false;
            _connecting = false;
            _players.Clear();
            _playerOrder.Clear();
            _error = reason;
        }
        firstSnapshot.TrySetResult(reason);
        Changed?.Invoke();
    }

    private void HandleConnectionChanged(SignalRClient source, bool connected)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_client, source)) return;
            _connected = connected;
            if (!connected)
            {
                _revision = 0;
                _error = "连接已断开，等待重连";
            }
            else
            {
                _error = null;
            }
        }
        Changed?.Invoke();
    }

    private void SetConnecting(bool connecting, string? error)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _connecting = connecting;
            if (error != null) _error = error;
        }
        Changed?.Invoke();
    }

    private void SetError(string error)
    {
        lock (_gate)
        {
            if (_disposed) return;
            _connecting = false;
            _connected = false;
            _error = error;
        }
        Changed?.Invoke();
    }

    private static void DetachClient(SignalRClient client)
    {
        // 连接释放后标记退役，避免滞后事件回调干扰。
        client.RetireForReplacement();
    }

    private static async Task SafeDisposeClientAsync(SignalRClient client)
    {
        try { await client.DisposeAsync().ConfigureAwait(false); }
        catch { /* 旁观连接清理异常不影响主链路 */ }
    }

    public async ValueTask DisposeAsync()
    {
        SignalRClient? client;
        CancellationTokenSource? wait;
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _joined = false;
            _connected = false;
            _connecting = false;
            client = _client;
            _client = null;
            wait = _reconnectCts;
            _reconnectCts = null;
        }
        try { _lifetimeCts.Cancel(); } catch { }
        try { wait?.Cancel(); } catch { }
        if (client != null)
        {
            DetachClient(client);
            await SafeDisposeClientAsync(client).ConfigureAwait(false);
        }
        _lifetimeCts.Dispose();
    }
}
