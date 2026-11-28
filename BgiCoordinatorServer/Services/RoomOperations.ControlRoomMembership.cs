using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private enum ControlRoomStage { Prepared, Ready, Leaving, Left }

    private sealed record ControlRoomSession(string Group, string PlayerUid, string PlayerName,
        string ClientInstanceId, string MachineId, bool Observer, IReadOnlyList<string> Roster)
    {
        public ControlRoomStage Stage { get; set; }
        public bool TransportAttached { get; set; }
    }

    private readonly ControlRoomAuth _controlRoomAuth;
    // Membership transitions and batch admission take _batchGate. The per-connection gates
    // also serialize their SignalR group operations and disconnect cleanup (no await under _batchGate).
    private readonly Dictionary<string, ControlRoomSession> _controlRoomSessions = new(StringComparer.Ordinal);
    private readonly SemaphoreSlim[] _controlConnectionGates = Enumerable.Range(0, 64).Select(_ => new SemaphoreSlim(1, 1)).ToArray();
    private SemaphoreSlim ControlConnectionGate(string connectionId) =>
        _controlConnectionGates[StringComparer.Ordinal.GetHashCode(connectionId) & 63];

    public ControlRoomCreateResult CreateControlRoom(GatewayHandlerContext ctx, string password, string playerUid,
        IReadOnlyList<string> allowedUids)
    {
        lock (_batchGate)
        {
            if (_controlRoomSessions.TryGetValue(ctx.ConnectionId, out var session)
                && (session.Stage != ControlRoomStage.Ready || session.PlayerUid != playerUid
                    || !IsInControlRoomOrRemote(ctx, session.Group)))
                return new(false, Reason: "当前连接身份不能登记房间");
        }
        try
        {
            var (roomCode, reason) = _controlRoomAuth.Create(password, playerUid, allowedUids);
            return new(roomCode.Length > 0, roomCode, reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogError(ex, "无法保存控制房间登记");
            return new(false, Reason: "无法保存控制房间登记");
        }
    }

    public async Task<ControlRoomPlayersResult> JoinControlRoomAsync(GatewayHandlerContext ctx, string roomCode,
        string password, string playerUid, string playerName, List<string>? allowedUids = null, bool isRemote = false,
        string clientInstanceId = "", string machineId = "", bool requireReady = false)
    {
        var gate = ControlConnectionGate(ctx.ConnectionId);
        await gate.WaitAsync();
        try
        {
            var group = $"CTRL_{roomCode}";
            var isWebClient = ControlRoomAuth.IsWebUid(playerUid);
            var isLogViewer = ControlRoomAuth.IsLogViewerUid(playerUid);
            var roster = allowedUids?.ToArray() ?? [];
            var session = new ControlRoomSession(group, playerUid, playerName, clientInstanceId, machineId,
                isRemote || isWebClient || isLogViewer, roster);
            ControlRoomSession? previous;
            string? rejection = null;
            var repeat = false;
            lock (_batchGate)
            {
                _controlRoomSessions.TryGetValue(ctx.ConnectionId, out previous);
                if (previous is not null)
                {
                    if (previous.Group != group || previous.Stage is ControlRoomStage.Left or ControlRoomStage.Leaving)
                        rejection = "请使用新连接加入控制房间";
                    else if (requireReady)
                    {
                        repeat = previous.PlayerUid == playerUid && previous.Observer == session.Observer
                            && previous.ClientInstanceId == clientInstanceId && previous.MachineId == machineId
                            && (previous.Stage == ControlRoomStage.Prepared || IsInControlRoomOrRemote(ctx, group));
                        if (!repeat) rejection = "候选连接身份已改变";
                    }
                    else if (previous.Stage == ControlRoomStage.Prepared)
                        rejection = "候选连接尚未就绪";
                    else if ((previous.PlayerUid != playerUid || previous.Observer != session.Observer
                        || previous.ClientInstanceId != clientInstanceId)
                        && IsControlRoomUidBusy(group, previous.PlayerUid, _roomManager.ControlUtcNow))
                        rejection = "当前房间还有未结清的上线意图或批次";
                }
            }

            if (rejection is null)
            {
                if (isLogViewer && !isRemote)
                    rejection = "日志协助连接必须以旁观模式加入";
                else
                {
                    rejection = AuthRejection(_controlRoomAuth.Authenticate(roomCode, password, playerUid, roster,
                        allowLegacyCreate: !requireReady, allowLegacyMigration: requireReady));
                    // 网页控制端仍要求成员昵称；日志协助等显式 isRemote 入口保持原权限。
                    if (rejection is null && isWebClient && !isRemote && !IsControlRoomMemberName(group, playerName))
                        rejection = "控制端昵称必须与房间内某个成员的昵称一致";
                }
            }
            if (rejection is not null) return await RejectControlJoinAsync(ctx, rejection, requireReady);

            if (requireReady)
            {
                if (!repeat)
                {
                    lock (_batchGate) _controlRoomSessions[ctx.ConnectionId] = session;
                }
                return new(true, PlayersUpdate: await ReadControlRoomPlayersBaselineAsync(group));
            }

            var result = await AttachControlRoomAsync(ctx, previous, session, null);
            return result.Success ? result : await RejectControlJoinAsync(ctx, result.Reason, requireReady);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "JoinControlRoom 失败");
            return await RejectControlJoinAsync(ctx, "加入控制房间失败，无法确认连接状态", requireReady);
        }
        finally { gate.Release(); }
    }

    public async Task<ControlRoomPlayersResult> ReadyControlRoomAsync(GatewayHandlerContext ctx, ControlStatus? status)
    {
        if (status is null) return new(false, "缺少候选房间状态");
        var gate = ControlConnectionGate(ctx.ConnectionId);
        await gate.WaitAsync();
        try
        {
            ControlRoomSession? session;
            lock (_batchGate)
            {
                if (!_controlRoomSessions.TryGetValue(ctx.ConnectionId, out session)
                    || session.Group != $"CTRL_{status.RoomCode}" || session.PlayerUid != status.PlayerUid
                    || (session.Stage != ControlRoomStage.Prepared
                        && !(session.Stage == ControlRoomStage.Ready && IsInControlRoomOrRemote(ctx, session.Group))))
                    return new(false, "候选房间或成员身份不匹配");
            }
            if (session.Stage == ControlRoomStage.Ready)
                return new(true, PlayersUpdate: await ReadControlRoomPlayersBaselineAsync(session.Group));
            if (status.OnlineHoeingGroupNames is null || status.ExpectedHoeingPlayers is < 1 or > 4)
                return new(false, "候选房间绑定或预期人数无效");
            return await AttachControlRoomAsync(ctx, session, session, status);
        }
        finally { gate.Release(); }
    }

    private async Task<ControlRoomPlayersResult> AttachControlRoomAsync(GatewayHandlerContext ctx,
        ControlRoomSession? previous, ControlRoomSession session, ControlStatus? initialStatus)
    {
        var group = session.Group;
        try
        {
            if (previous?.TransportAttached != true)
            {
                // A transport failure may have happened after subscribing. Leave can still retry transport removal.
                lock (_batchGate)
                {
                    _controlRoomSessions[ctx.ConnectionId] = session;
                    session.TransportAttached = true;
                }
                await _broadcaster.AddToGroupAsync(ctx, group);
                TrackGroup(ctx, group);
            }
            lock (_batchGate)
            {
                if (previous is { Stage: ControlRoomStage.Ready }
                    && (previous.PlayerUid != session.PlayerUid || previous.Observer != session.Observer
                        || previous.ClientInstanceId != session.ClientInstanceId))
                {
                    if (IsControlRoomUidBusy(group, previous.PlayerUid, _roomManager.ControlUtcNow))
                        return new(false, "当前房间还有未结清的上线意图或批次");
                    _roomManager.RemoveFromControlRoom(group, ctx.ConnectionId);
                    _roomManager.RemoveRemoteConnection(group, ctx.ConnectionId);
                }
                if (session.Observer)
                    _roomManager.RegisterRemoteConnection(group, ctx.ConnectionId, session.PlayerUid);
                else
                    _roomManager.AddToControlRoom(group, ctx.ConnectionId, session.PlayerUid,
                        session.PlayerName, session.ClientInstanceId, session.MachineId, initialStatus);
                session.Stage = ControlRoomStage.Ready;
                session.TransportAttached = true;
                _controlRoomSessions[ctx.ConnectionId] = session;
            }
        }
        catch (Exception ex)
        {
            lock (_batchGate)
            {
                DetachControlRoomMember(group, ctx.ConnectionId);
                session.Stage = ControlRoomStage.Leaving;
                _controlRoomSessions[ctx.ConnectionId] = session;
            }
            _logger.LogWarning(ex, "控制房间 {Group} 未确认加入", group);
            return new(false, "控制房间未确认就绪，请断开候选连接后核对");
        }

        if (!session.Observer)
        {
            ObserveRoomLog(context => context.RegisterControlRoster(group[5..], session.PlayerUid, session.Roster));
            if (initialStatus is not null) ObserveHoeingMember(group, ctx.ConnectionId, initialStatus);
        }
        _logger.LogInformation("玩家 {PlayerName}({PlayerUid}) 加入控制房间 {RoomCode} (Remote={IsRemote})",
            session.PlayerName, session.PlayerUid, group[5..], session.Observer);
        var snapshot = await BroadcastControlRoomPlayersAsync(group, forceFull: true);
        if (!session.Observer)
        {
            List<RemoteCommand> pending;
            lock (_batchGate)
            {
                pending = _roomManager.GetUidByConnectionId(group, ctx.ConnectionId) == session.PlayerUid
                    ? _roomManager.GetAndClearPendingCommands(group, session.PlayerUid) : [];
            }
            foreach (var command in pending)
            {
                if (_roomManager.GetUidByConnectionId(group, ctx.ConnectionId) != session.PlayerUid) break;
                if (command.ExpiresAtUtc <= _roomManager.ControlUtcNow) continue;
                try { await SendCommandInRoomAsync(group, ctx.ConnectionId, command); }
                catch (Exception ex) { _logger.LogWarning(ex, "控制房间 {Group} 缓存命令未送达", group); }
            }
            if (initialStatus is not null) await ReplayPendingBatchStartAsync(group, ctx.ConnectionId);
        }
        return new(true, PlayersUpdate: snapshot);
    }

    public async Task<ControlRoomLeaveResult> LeaveControlRoomAsync(GatewayHandlerContext ctx, string roomCode,
        bool withdrawOnlineIntent = false)
    {
        var gate = ControlConnectionGate(ctx.ConnectionId);
        await gate.WaitAsync();
        try
        {
            var group = $"CTRL_{roomCode}";
            ControlRoomSession session;
            lock (_batchGate)
            {
                if (!_controlRoomSessions.TryGetValue(ctx.ConnectionId, out session!) || session.Group != group)
                    return new(false, Reason: "当前连接未加入该房间");
                if (session.Stage == ControlRoomStage.Left) return new(true);
                if (session.Stage == ControlRoomStage.Ready)
                {
                    if (!session.Observer && _roomManager.GetUidByConnectionId(group, ctx.ConnectionId) == session.PlayerUid)
                    {
                        if (HasUnsettledControlRoomBatch(group, session.PlayerUid))
                            return new(false, true, "本机在原房间的联机批次仍在执行或收尾");
                        if (!withdrawOnlineIntent && _roomManager.HasLiveOnlineIntent(group, session.PlayerUid, _roomManager.ControlUtcNow))
                            return new(false, true, "本机在原房间的上线意图仍在等待开批");
                    }
                    // Removing this connection also disarms its waiting intent under the batch admission lock.
                    DetachControlRoomMember(group, ctx.ConnectionId);
                }
                session.Stage = ControlRoomStage.Leaving;
            }

            if (session.TransportAttached)
            {
                try { await _broadcaster.RemoveFromGroupAsync(ctx, group); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "控制房间 {Group} 订阅移除未确认", group);
                    return new(false, Reason: "控制房间订阅移除未确认，请重新核对");
                }
                UntrackGroup(ctx, group);
            }
            await ClearControlConnectionStateAsync(ctx.ConnectionId);
            if (session.TransportAttached) await BroadcastControlRoomPlayersAsync(group, forceFull: true);
            lock (_batchGate)
            {
                session.Stage = ControlRoomStage.Left;
                session.TransportAttached = false;
            }
            return new(true);
        }
        finally { gate.Release(); }
    }

    public async Task<ControlRoomPlayersResult> GetControlRoomSnapshotAsync(GatewayHandlerContext ctx, string roomCode)
    {
        var group = $"CTRL_{roomCode}";
        var gate = ControlConnectionGate(ctx.ConnectionId);
        await gate.WaitAsync();
        try
        {
            lock (_batchGate)
            {
                if (!IsInControlRoomOrRemote(ctx, group)
                    && !(_controlRoomSessions.TryGetValue(ctx.ConnectionId, out var session)
                        && session.Stage == ControlRoomStage.Prepared && session.Group == group))
                    return new(false, "当前连接未加入或准备该房间");
            }
            return new(true, PlayersUpdate: await ReadControlRoomPlayersBaselineAsync(group));
        }
        finally { gate.Release(); }
    }

    public ControlRoomAccessResult GetControlRoomAccess(string roomCode, string password, string playerUid)
    {
        if (!ControlRoomAuth.IsMemberUid(playerUid)) return new(false, Reason: "成员 UID 无效");
        try
        {
            var reason = AuthRejection(_controlRoomAuth.Authenticate(roomCode, password, playerUid, []));
            if (reason is not null) return new(false, Reason: reason);
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or System.Text.Json.JsonException)
        {
            _logger.LogWarning(ex, "无法读取控制房间登记");
            return new(false, Reason: "无法读取控制房间登记");
        }
        var group = $"CTRL_{roomCode}";
        lock (_batchGate)
        {
            var busy = IsControlRoomUidBusy(group, playerUid, _roomManager.ControlUtcNow);
            var joined = _controlRoomSessions.Values.Any(session => session.Group == group
                && session.PlayerUid == playerUid && session.Stage != ControlRoomStage.Left);
            return new(true, busy, joined, busy ? "该成员还有未结清的上线意图或批次" : "");
        }
    }

    private bool IsControlRoomUidBusy(string group, string uid, DateTime utcNow) =>
        _roomManager.HasLiveOnlineIntent(group, uid, utcNow) || HasUnsettledControlRoomBatch(group, uid);

    private bool HasUnsettledControlRoomBatch(string group, string uid) =>
        _controlBatches.TryGetValue(group, out var batch) && batch.HasUnsettledParticipant(uid);

    private static string? AuthRejection(ControlRoomAuthResult auth) => auth switch
    {
        ControlRoomAuthResult.RoomNotFound => "控制房间不存在",
        ControlRoomAuthResult.Rejected => "密码错误或 UID 不在白名单中",
        _ => null,
    };

    private async Task<ControlRoomPlayersResult> RejectControlJoinAsync(GatewayHandlerContext ctx, string reason, bool requireReady)
    {
        if (!requireReady)
        {
            try { await _broadcaster.SendToCallerAsync(ctx, "JoinRejected", new { reason }, reason); }
            catch (Exception ex) { _logger.LogDebug(ex, "未能发送旧控制房间拒绝事件"); }
        }
        return new(false, reason);
    }

    // The caller holds the admission gate. Detach exactly this connection, never a same-UID replacement.
    private void DetachControlRoomMember(string group, string connectionId)
    {
        ObserveHoeingDisconnected(group, connectionId);
        _roomManager.RemoveFromControlRoom(group, connectionId);
        _roomManager.RemoveRemoteConnection(group, connectionId);
        if (_controlBatches.TryGetValue(group, out var state)) ReleaseOfflineCompletionWaits(group, state);
    }

    private async Task ClearControlConnectionStateAsync(string connectionId)
    {
        await ClearControlPresenceConnectionAsync(connectionId);
        await ClearDesktopControlConnectionAsync(connectionId);
        ClearScreenshotConnection(connectionId);
        try
        {
            foreach (var (group, targetUid, count) in _roomManager.RemoveLogSubscriberEverywhere(connectionId))
                await NotifyLogSubscriberCountAsync(group, targetUid, count);
        }
        catch (Exception ex) { _logger.LogWarning(ex, "清理控制房间日志订阅失败"); }
    }

    private async Task DisconnectControlRoomAsync(string connectionId)
    {
        var gate = ControlConnectionGate(connectionId);
        await gate.WaitAsync();
        try
        {
            ControlRoomSession? session;
            lock (_batchGate)
            {
                _controlRoomSessions.Remove(connectionId, out session);
                if (session is { Stage: ControlRoomStage.Ready })
                    DetachControlRoomMember(session.Group, connectionId);
            }
            if (session?.TransportAttached == true)
                await BroadcastControlRoomPlayersAsync(session.Group, forceFull: true);
            await ClearControlConnectionStateAsync(connectionId);
        }
        finally { gate.Release(); }
    }
}
