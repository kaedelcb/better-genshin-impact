using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;

namespace MultiplayerHoeingAssistant.ViewModels;

internal sealed class RoomFollowAuthenticationRequiredException : Exception { }

internal sealed record RoomFollowObservation(SignalRClient? Client, IReadOnlyList<ControlRoomPlayer> Players,
    bool Connecting, bool AuthenticationRequired, string Error);

/// <summary>
/// A normal, read-only observer login, using only the monitor's own, locally saved credentials.
/// This deliberately does NOT use the web/log spectator identities, executor leave/ready
/// transaction or any of the normal executor command handlers.
/// </summary>
internal sealed class RoomFollowObserverSession(RoomFollowRoomCredential credential) : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<string, ControlRoomPlayer> _players = new(StringComparer.Ordinal);
    private SignalRClient? _client;
    private Task? _worker, _stopping;
    private bool _disposed, _connected, _connecting, _hasBaseline, _authenticationRequired;
    private int _repairing;
    private long _revision;
    private string _error = "";
    internal RoomFollowRoomCredential Credential { get; } = credential;
    internal event Action? Changed;

    internal RoomFollowObservation Capture()
    {
        lock (_gate)
        {
            var ready = !_disposed && _connected && _hasBaseline && _client?.IsControlRoomReady == true;
            return new(ready ? _client : null, ready ? _players.Values.ToArray() : [],
                _connecting, _authenticationRequired, _error);
        }
    }

    internal void Start()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _worker ??= Task.Run(() => RunAsync(_lifetime.Token));
        }
    }

    private async Task RunAsync(CancellationToken ct)
    {
        while (!ct.IsCancellationRequested)
        {
            var client = new SignalRClient();
            var terminal = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate)
            {
                if (_disposed) return;
                _client = client;
                _connecting = true;
                _connected = false;
                _error = "";
                ClearBaseline();
            }
            client.CoordinateRoomJoin = async (_, join, token) =>
            {
                try
                {
                    // A normal join retains legacy auto-registration semantics. Verify that
                    // this existing room actually authorizes the local identity *before* every
                    // automatic join (including reconnects), without invoking createRoom.
                    await VerifyAccessAsync(Credential, token).ConfigureAwait(false);
                    await join(token).ConfigureAwait(false);
                }
                catch (Exception ex) when (ex is RoomFollowAuthenticationRequiredException or SignalRClient.RoomJoinRejectedException)
                {
                    Reject(client, terminal);
                    throw;
                }
            };
            client.OnJoinRejected += _ => Reject(client, terminal);
            client.OnPlayersUpdated += update => ApplyPlayers(client, update, ct);
            client.OnConnectionStateChanged += connected => ConnectionChanged(client, connected, ct);
            Changed?.Invoke();
            try
            {
                using (var budget = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    budget.CancelAfter(TimeSpan.FromSeconds(20));
                    if (ct.IsCancellationRequested) break;
                    await client.ConnectAsync(Credential.Server, Credential.Room, Credential.Password,
                        Credential.Uid, Credential.Name, [], isRemote: true, clientInstanceId: Credential.InstanceId,
                        bypassSystemProxy: Credential.BypassProxy, cancellationToken: budget.Token).ConfigureAwait(false);
                }
                ConnectionChanged(client, true, ct);
                // Ordinary SignalR reconnects are handled by SignalRClient. No metadata or
                // member-state polling; a lost initial connection is retried with backoff.
                await terminal.Task.WaitAsync(ct).ConfigureAwait(false);
            }
            catch (Exception) when (ct.IsCancellationRequested) { }
            catch (Exception ex) when (ex is RoomFollowAuthenticationRequiredException or SignalRClient.RoomJoinRejectedException)
            { Reject(client, terminal); }
            catch (Exception)
            {
                lock (_gate)
                    if (!_disposed && ReferenceEquals(_client, client) && !_authenticationRequired)
                        _error = "房间登录未确认，等待重新连接。";
            }
            finally
            {
                lock (_gate)
                {
                    if (ReferenceEquals(_client, client))
                    {
                        _client = null;
                        _connected = _connecting = false;
                        ClearBaseline();
                    }
                }
                try { client.RetireForReplacement(); await client.DisposeAsync().ConfigureAwait(false); }
                catch { /* Only the observation is lost; no control or configuration write. */ }
                Changed?.Invoke();
            }
            lock (_gate) { if (_disposed || _authenticationRequired) break; }
            try { await Task.Delay(TimeSpan.FromSeconds(10), ct).ConfigureAwait(false); }
            catch (OperationCanceledException) { break; }
        }
    }

    private static async Task VerifyAccessAsync(RoomFollowRoomCredential credential, CancellationToken ct)
    {
        await using var probe = new MhaGatewayClient();
        await probe.ConnectAsync(credential.Server, credential.BypassProxy, ct).ConfigureAwait(false);
        var reply = await probe.InvokeCommandAsync("control.roomAccess", new
        {
            roomCode = credential.Room, password = credential.Password, playerUid = credential.Uid
        }, ct: ct).ConfigureAwait(false);
        if (reply.Payload?["success"]?.GetValue<bool>() == false) throw new RoomFollowAuthenticationRequiredException();
        if (reply.Payload?["success"]?.GetValue<bool>() != true)
            throw new InvalidOperationException("普通房间鉴权未获得确认。");
    }

    private void Reject(SignalRClient client, TaskCompletionSource terminal)
    {
        lock (_gate)
        {
            if (_disposed || !ReferenceEquals(_client, client)) return;
            _authenticationRequired = true;
            _connected = _connecting = false;
            _error = "房间鉴权未通过，请结束跟随后在管理房间中正常验证。";
            ClearBaseline();
        }
        // Prevent SignalRClient's normal reconnect/legacy join path from repeatedly trying a
        // rejected authorization. The worker will dispose the connection after the join returns.
        client.RetireForReplacement();
        terminal.TrySetResult();
        Changed?.Invoke();
    }

    private void ClearBaseline()
    {
        _players.Clear();
        _hasBaseline = false;
        _revision = 0;
    }

    private void ConnectionChanged(SignalRClient client, bool connected, CancellationToken ct)
    {
        bool repair;
        lock (_gate)
        {
            if (_disposed || _authenticationRequired || !ReferenceEquals(_client, client)) return;
            _connected = connected && client.IsControlRoomReady;
            _connecting = !_connected;
            if (!_connected)
            {
                ClearBaseline();
                _error = "房间连接中断，等待普通重连。";
            }
            else _error = _hasBaseline ? "" : "房间成员快照待确认。";
            repair = _connected && !_hasBaseline;
        }
        Changed?.Invoke();
        if (repair) RepairSnapshot(client, ct);
    }

    private void ApplyPlayers(SignalRClient client, ControlRoomPlayersUpdate update, CancellationToken ct)
    {
        bool repair = false;
        lock (_gate)
        {
            if (_disposed || _authenticationRequired || !ReferenceEquals(_client, client) || client.IsRetired) return;
            if (update.Revision > 0 && update.Revision < _revision) return;
            if (update.Full)
            {
                ClearBaseline();
                _hasBaseline = true;
            }
            else
            {
                if (update.Revision > 0 && update.Revision <= _revision) return;
                if (!_hasBaseline || update.BaseRevision is { } baseline && baseline != _revision)
                {
                    ClearBaseline();
                    _error = "房间成员快照待确认。";
                    repair = true;
                }
            }
            if (_hasBaseline)
            {
                foreach (var uid in update.Removed ?? []) _players.Remove(uid);
                foreach (var player in (update.Full ? update.Players : update.Changed) ?? [])
                    if (!string.IsNullOrWhiteSpace(player.PlayerUid)) _players[player.PlayerUid] = player;
                _revision = update.Revision;
                if (_connected) _error = "";
            }
        }
        Changed?.Invoke();
        if (repair && client.IsControlRoomReady) RepairSnapshot(client, ct);
    }

    private void RepairSnapshot(SignalRClient client, CancellationToken ct)
    {
        if (Interlocked.CompareExchange(ref _repairing, 1, 0) != 0) return;
        var context = client.DesktopControlContext;
        _ = Task.Run(async () =>
        {
            try
            {
                using var budget = CancellationTokenSource.CreateLinkedTokenSource(ct);
                budget.CancelAfter(TimeSpan.FromSeconds(8));
                var snapshot = await client.ReadRoomSnapshotAsync(budget.Token).ConfigureAwait(false);
                if (snapshot is { Full: true } && client.IsControlRoomReady && client.DesktopControlContext == context)
                    ApplyPlayers(client, snapshot, ct);
            }
            catch { /* A missing baseline is never reported as confirmed. */ }
            finally { Interlocked.Exchange(ref _repairing, 0); }
        });
    }

    public ValueTask DisposeAsync()
    {
        lock (_gate)
        {
            if (_stopping is not null) return new(_stopping);
            _disposed = true;
            _client?.RetireForReplacement();
            _stopping = Task.Run(async () =>
            {
                try { await _lifetime.CancelAsync().ConfigureAwait(false); if (_worker is not null) await _worker.ConfigureAwait(false); }
                finally { _lifetime.Dispose(); }
            });
            return new(_stopping);
        }
    }
}
