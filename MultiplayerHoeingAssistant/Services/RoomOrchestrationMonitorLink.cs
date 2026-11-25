using MultiplayerHoeingAssistant.Models;

namespace MultiplayerHoeingAssistant.Services;

/// <summary>A separate, authenticated same-UID observer connection to one saved monitor room.
/// It only discovers instances and exchanges center request/reply messages. It does not join as
/// an executor, change the main monitor's room or replace a spectator/single-card session.</summary>
internal sealed class RoomOrchestrationMonitorLink : IAsyncDisposable
{
    private readonly object _gate = new();
    private readonly SignalRClient _client = new();
    private readonly CancellationTokenSource _lifetime = new();
    private readonly Dictionary<(string Uid, string Instance), ControlRoomPlayer> _players = new();
    private readonly Dictionary<string, (string Uid, string Instance, TaskCompletionSource<RemoteCenterReply> Completion)> _pending = new();
    private volatile bool _disposed;
    private bool _established;
    internal bool InitialDiscoveryComplete { get { lock (_gate) return _established || _connecting is { IsCompleted: true }; } }
    private readonly string _observerInstance = "room-graph-monitor-" + Guid.NewGuid().ToString("N");
    private Task? _connecting;
    private DateTimeOffset _retryAt;
    private long _revision;
    internal string ProfileId { get; }
    internal string ServerUrl { get; }
    internal string RoomCode { get; }
    internal string Uid { get; }
    private readonly string _name;
    private readonly string _password;
    private readonly string[] _members;
    private readonly bool _bypassProxy;

    internal RoomOrchestrationMonitorLink(AssistantRoomProfile profile, string server, string uid, string name, bool bypassProxy)
    {
        ProfileId = profile.Id; ServerUrl = server; RoomCode = profile.RoomCode; Uid = uid;
        _name = name; _password = profile.Password; _members = profile.TeamUids.ToArray(); _bypassProxy = bypassProxy;
        _client.OnPlayersUpdated += OnPlayers;
        _client.OnConnectionStateChanged += OnConnection;
        _client.OnRemoteCommand += OnReply;
    }

    internal bool Matches(AssistantRoomProfile profile, string server, string uid, bool bypassProxy)
        => ProfileId == profile.Id && Uid == uid && RoomCode == profile.RoomCode && _password == profile.Password
            && RoomServerAddress.Same(ServerUrl, server) && _bypassProxy == bypassProxy
            && _members.SequenceEqual(profile.TeamUids);
    internal bool IsReady => !_disposed && _client.IsControlRoomReady && _client.CurrentRoomCode == RoomCode
        && RoomServerAddress.Same(_client.CurrentServerUrl, ServerUrl);

    internal void EnsureConnected()
    {
        lock (_gate)
        {
            if (_disposed || _connecting is { IsCompleted: false } || DateTimeOffset.UtcNow < _retryAt) return;
            if (_established && (!IsReady || _revision > 0)) return; // SignalRClient owns reconnects after first success.
            _retryAt = DateTimeOffset.UtcNow.AddSeconds(12);
            _connecting = Task.Run(_established ? RepairSnapshotAsync : ConnectAsync);
        }
    }

    private async Task ConnectAsync()
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(12));
            await _client.ConnectAsync(ServerUrl, RoomCode, _password, Uid, _name, [.. _members], isRemote: true,
                clientInstanceId: _observerInstance, bypassSystemProxy: _bypassProxy,
                cancellationToken: budget.Token).ConfigureAwait(false);
            lock (_gate) { _established = true; _retryAt = DateTimeOffset.MinValue; }
        }
        catch { /* No guessed membership or stale live display on disconnect. */ }
    }

    private async Task RepairSnapshotAsync()
    {
        try
        {
            using var budget = CancellationTokenSource.CreateLinkedTokenSource(_lifetime.Token);
            budget.CancelAfter(TimeSpan.FromSeconds(8));
            var context = _client.DesktopControlContext;
            var snapshot = await _client.ReadRoomSnapshotAsync(budget.Token).ConfigureAwait(false);
            if (snapshot is { Full: true } && IsReady && context == _client.DesktopControlContext) OnPlayers(snapshot);
        }
        catch { /* The next refresh retries a missing baseline. */ }
    }

    private void OnConnection(bool connected)
    {
        if (connected) return;
        lock (_gate)
        {
            _players.Clear(); _revision = 0;
            foreach (var item in _pending.Values) item.Completion.TrySetException(new InvalidOperationException("编排监控连接已中断"));
        }
    }

    private void OnPlayers(ControlRoomPlayersUpdate update)
    {
        lock (_gate)
        {
            if (_disposed || update.Revision > 0 && update.Revision < _revision) return;
            if (update.Full) { _players.Clear(); _revision = 0; }
            else if (update.BaseRevision is { } baseline && _revision > 0 && baseline != _revision)
            { _players.Clear(); _revision = 0; _retryAt = DateTimeOffset.MinValue; return; }
            if (!update.Full && _revision == 0) return;
            if (update.Revision > 0 && update.Revision <= _revision && !update.Full) return;
            _revision = update.Revision > 0 ? update.Revision : Math.Max(1, _revision);
            foreach (var uid in update.Removed ?? [])
                foreach (var key in _players.Keys.Where(k => k.Uid == uid).ToArray()) _players.Remove(key);
            foreach (var player in (update.Full ? update.Players : update.Changed) ?? [])
            {
                if (player.PlayerUid != Uid) continue;
                if (string.IsNullOrWhiteSpace(player.ClientInstanceId))
                {
                    if (!update.Full) { _players.Clear(); _revision = 0; _retryAt = DateTimeOffset.MinValue; return; }
                    continue;
                }
                var key = (player.PlayerUid, player.ClientInstanceId);
                if (player.Online) _players[key] = player;
                else _players.Remove(key);
            }
        }
    }

    internal RemoteCenterTarget[] Targets()
    {
        lock (_gate)
        {
            if (!IsReady) return [];
            return _players.Values.Where(p => p.Online && p.PlayerUid == Uid
                && !string.IsNullOrWhiteSpace(p.ClientInstanceId) && !string.IsNullOrWhiteSpace(p.MachineId))
                .Select(p => new RemoteCenterTarget { PlayerUid = Uid, PlayerName = p.PlayerName,
                    ClientInstanceId = p.ClientInstanceId, MachineId = p.MachineId, Online = true }).ToArray();
        }
    }

    private void OnReply(RemoteCommand command)
    {
        if (command.Cmd != RemoteCenterProtocol.ReplyCommand || command.RoomCode != RoomCode) return;
        lock (_gate)
        {
            if (!_pending.TryGetValue(command.CommandId, out var target) || command.SenderUid != target.Uid) return;
            try
            {
                var reply = System.Text.Json.JsonSerializer.Deserialize<RemoteCenterReply>(RemoteCenterProtocol.PayloadOf(command) ?? "");
                if (reply?.TargetInstanceId == target.Instance) target.Completion.TrySetResult(reply);
            }
            catch (Exception ex) { target.Completion.TrySetException(ex); }
        }
    }

    internal async Task<RemoteCenterReply> RequestAsync(RemoteCenterTarget target, RemoteCenterRequest request, string id, CancellationToken ct)
    {
        if (target.PlayerUid != Uid || request.TargetInstanceId != target.ClientInstanceId
            || !Targets().Any(x => x.ClientInstanceId == target.ClientInstanceId && x.MachineId == target.MachineId))
            return RemoteCenterReply.Rejected("执行端在该房间的实例已变化，请重新读取");
        var command = RemoteCenterProtocol.Request(RoomCode, Uid, _name, Uid, request, id);
        var context = _client.DesktopControlContext;
        try
        {
            if (!string.IsNullOrEmpty(HostMachineIdentity.Current) && target.MachineId == HostMachineIdentity.Current)
            {
                var probe = RemoteCenterProtocol.Request(RoomCode, Uid, _name, Uid,
                    new RemoteCenterRequest { Operation = "probe", TargetInstanceId = target.ClientInstanceId });
                var endpoint = await LocalConfigEditChannel.FindAsync(probe, _password, RemoteCenterProtocol.ReplyCommand, "center", ServerUrl);
                ct.ThrowIfCancellationRequested();
                if (endpoint is null || !IsReady || _client.DesktopControlContext != context)
                    return RemoteCenterReply.Rejected("同机执行端原房间通道未就绪，未改走网络重发");
                var response = await LocalConfigEditChannel.SendAsync(endpoint, command, _password, serverUrl: ServerUrl, cancellationToken: ct);
                var local = response?.Cmd == RemoteCenterProtocol.ReplyCommand
                    ? System.Text.Json.JsonSerializer.Deserialize<RemoteCenterReply>(RemoteCenterProtocol.PayloadOf(response) ?? "") : null;
                return local?.TargetInstanceId == target.ClientInstanceId ? local : RemoteCenterReply.Unknown(id, "同机执行端回复待核对");
            }
            var completion = new TaskCompletionSource<RemoteCenterReply>(TaskCreationOptions.RunContinuationsAsynchronously);
            lock (_gate) _pending.Add(id, (Uid, target.ClientInstanceId, completion));
            try
            {
                ct.ThrowIfCancellationRequested();
                if (!IsReady || _client.DesktopControlContext != context) return RemoteCenterReply.Rejected("原房间连接已变化");
                await _client.SendRemoteCenterCommandAsync(command, ct).ConfigureAwait(false);
                return await completion.Task.WaitAsync(TimeSpan.FromSeconds(20), ct).ConfigureAwait(false);
            }
            finally { lock (_gate) _pending.Remove(id); }
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
        catch (Exception ex) { return RemoteCenterReply.Unknown(id, "编排远程请求结果未确认：" + ex.Message); }
    }

    public async ValueTask DisposeAsync()
    {
        lock (_gate) { if (_disposed) return; _disposed = true; }
        _lifetime.Cancel(); _client.RetireForReplacement(); OnConnection(false);
        _client.OnPlayersUpdated -= OnPlayers; _client.OnConnectionStateChanged -= OnConnection; _client.OnRemoteCommand -= OnReply;
        try { if (_connecting is { } connecting) await connecting.ConfigureAwait(false); }
        catch { }
        await _client.DisposeAsync().ConfigureAwait(false);
        _lifetime.Dispose();
    }
}
