using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>耕地机多房间旁观/单卡远控会话接口（便于单元测试注入模拟房间数据与远控回执）。</summary>
internal interface IRoomSpectatorSession : IAsyncDisposable
{
    string ProfileId { get; }
    string ServerUrl { get; }
    string RoomCode { get; }
    string Password { get; }
    bool BypassSystemProxy { get; }
    string SpectatorUid { get; }
    bool IsConnected { get; }
    bool IsConnecting { get; }
    string? Error { get; }
    event Action? Changed;
    event Action<RemoteCommand>? ReplyReceived;
    event Action<RemoteCommandResult>? CommandResultReceived;
    IReadOnlyList<ControlRoomPlayer> GetPlayersSnapshot();
    void Start();
    void ForceReconnect();
    Task<bool> SendRemoteCommandAsync(RemoteCommand command);
    Task<bool> ClearOnlineHistoryAsync(string targetUid);
    Task<CoordinatedBatchSnapshot?> GetCurrentCoordinatedBatchAsync(CancellationToken ct = default);
    Task<CoordinatedBatchResetResult?> ResetCoordinatedBatchAsync(
        string resetId, IReadOnlyList<string> requestedTargets,
        int? expectedGeneration = null, string? expectedBatchId = null, CancellationToken ct = default);
}

/// <summary>
/// 耕地机多房间旁观与单卡片远控连接。
/// 使用独立 web_logviewer_ 旁观身份与 isRemote=true 加入目标控制房间，不注册执行端、不上报状态、
/// 不执行入站控制命令，也不会挤掉同机或异地的真实执行端；仅允许显式发起单卡片远控并接收对应的请求应答/回执。
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
    public string SpectatorUid { get; } = "web_logviewer_" + Guid.NewGuid().ToString("N");
    public bool IsConnected { get { lock (_gate) return !_disposed && _joined && _connected; } }
    public bool IsConnecting { get { lock (_gate) return !_disposed && _connecting; } }
    public string? Error { get { lock (_gate) return _error; } }
    private string? _error;

    public event Action? Changed;
    public event Action<RemoteCommand>? ReplyReceived;
    public event Action<RemoteCommandResult>? CommandResultReceived;

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
                void OnCommand(RemoteCommand cmd) => HandleReplyReceived(client, cmd);
                void OnCommandResult(RemoteCommandResult result) => HandleCommandResultReceived(client, result);

                client.OnPlayersUpdated += OnPlayers;
                client.OnJoinRejected += OnRejected;
                client.OnConnectionStateChanged += OnConnChanged;
                client.OnRemoteCommand += OnCommand;
                client.OnRemoteCommandResult += OnCommandResult;

                lock (_gate)
                {
                    if (_disposed)
                    {
                        client.OnPlayersUpdated -= OnPlayers;
                        client.OnJoinRejected -= OnRejected;
                        client.OnConnectionStateChanged -= OnConnChanged;
                        client.OnRemoteCommand -= OnCommand;
                        client.OnRemoteCommandResult -= OnCommandResult;
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
                    // 独立旁观身份 + isRemote=true：不注册执行端，不挤掉同机或异地真实成员；
                    // 稳定 SpectatorUid 让服务端可以把 ack / .data / .push_result / 回执准确回投到本连接。
                    await client.ConnectAsync(normalizedServer, RoomCode, Password,
                        SpectatorUid, "房间远控", [],
                        isRemote: true, bypassSystemProxy: BypassSystemProxy, cancellationToken: timeoutCts.Token);
                    var rejection = await firstSnapshot.Task.WaitAsync(timeoutCts.Token);
                    if (rejection != null)
                        throw new InvalidOperationException(rejection);
                    connectedOk = true;
                }
                catch (OperationCanceledException) when (ct.IsCancellationRequested)
                {
                    await CleanupAttemptAsync(client, OnPlayers, OnRejected, OnConnChanged, OnCommand, OnCommandResult);
                    return;
                }
                catch (Exception ex)
                {
                    await CleanupAttemptAsync(client, OnPlayers, OnRejected, OnConnChanged, OnCommand, OnCommandResult);
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

    private SignalRClient? GetReadyClient()
    {
        lock (_gate)
        {
            return !_disposed && _joined && _connected ? _client : null;
        }
    }

    public async Task<bool> SendRemoteCommandAsync(RemoteCommand command)
    {
        var client = GetReadyClient();
        if (client is null || !client.IsConnected) return false;
        command.RoomCode = RoomCode;
        command.SenderUid = SpectatorUid;
        await client.SendRemoteCommandAsync(command).ConfigureAwait(false);
        return true;
    }

    public async Task<bool> ClearOnlineHistoryAsync(string targetUid)
    {
        var client = GetReadyClient();
        if (client is null || !client.IsConnected || string.IsNullOrWhiteSpace(targetUid)) return false;
        await client.ClearOnlineHistoryAsync(targetUid).ConfigureAwait(false);
        return true;
    }

    public async Task<CoordinatedBatchSnapshot?> GetCurrentCoordinatedBatchAsync(CancellationToken ct = default)
    {
        var client = GetReadyClient();
        if (client is null || !client.IsConnected) return null;
        return await client.GetCurrentCoordinatedBatchAsync(ct).ConfigureAwait(false);
    }

    public async Task<CoordinatedBatchResetResult?> ResetCoordinatedBatchAsync(
        string resetId, IReadOnlyList<string> requestedTargets,
        int? expectedGeneration = null, string? expectedBatchId = null, CancellationToken ct = default)
    {
        var client = GetReadyClient();
        if (client is null || !client.IsConnected) return null;
        return await client.ResetCoordinatedBatchAsync(resetId, requestedTargets, expectedGeneration, expectedBatchId, ct)
            .ConfigureAwait(false);
    }

    private void HandleReplyReceived(SignalRClient source, RemoteCommand cmd)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_client, source)) return;
        }
        // 旁观连接绝不执行任何入站控制命令，仅转发应答/确认类回复（ack / *.data / *.push_result）。
        if (cmd is null || string.IsNullOrEmpty(cmd.Cmd)) return;
        if (!string.Equals(cmd.Cmd, "ack", StringComparison.Ordinal)
            && !cmd.Cmd.EndsWith(".data", StringComparison.Ordinal)
            && !cmd.Cmd.EndsWith(".push_result", StringComparison.Ordinal))
        {
            return;
        }
        ReplyReceived?.Invoke(cmd);
    }

    private void HandleCommandResultReceived(SignalRClient source, RemoteCommandResult result)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_client, source)) return;
        }
        if (result is null) return;
        CommandResultReceived?.Invoke(result);
    }

    private async Task CleanupAttemptAsync(SignalRClient client,
        Action<ControlRoomPlayersUpdate> onPlayers, Action<string> onRejected, Action<bool> onConnChanged,
        Action<RemoteCommand> onCommand, Action<RemoteCommandResult> onCommandResult)
    {
        client.OnPlayersUpdated -= onPlayers;
        client.OnJoinRejected -= onRejected;
        client.OnConnectionStateChanged -= onConnChanged;
        client.OnRemoteCommand -= onCommand;
        client.OnRemoteCommandResult -= onCommandResult;
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
