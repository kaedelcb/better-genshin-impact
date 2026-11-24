#nullable enable
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

internal sealed record HostWorldRouteCompletion(string RouteName, double DurationSeconds, DateTime CompletedAtUtc);

internal sealed record HostWorldState(string SessionId, string HostUid, string[] ExecutorUids,
    string Phase, string[]? RouteNames, string? Reason, string? ManifestDigest = null, string[]? StandbyUids = null,
    HostWorldRouteCompletion[]? RouteCompletions = null)
{
    public bool IsSuccess => Phase is "completed" or "noRoutes" or "expCap";
    public bool IsTerminal => IsSuccess || Phase == "aborted";

    internal void RequireIdentity(string sessionId, string uid, bool isHost, string? knownHostUid,
        HostWorldState? frozen, bool standbyOnly = false)
    {
        if (SessionId != sessionId || string.IsNullOrWhiteSpace(HostUid) || string.IsNullOrWhiteSpace(uid)
            || (isHost != (HostUid == uid)) || (!string.IsNullOrEmpty(knownHostUid) && HostUid != knownHostUid)
            || ExecutorUids == null || ExecutorUids.Any(string.IsNullOrWhiteSpace)
            || ExecutorUids.Distinct(StringComparer.Ordinal).Count() != ExecutorUids.Length
            || Phase is not ("waiting" or "regular" or "ready" or "running" or "completed" or "noRoutes" or "expCap" or "aborted"))
            throw new MultiplayerSessionFailedException("借世界房间实例、角色或执行者名单不一致");

        var standby = StandbyUids ?? [];
        if (Phase == "regular" && (standbyOnly || standby.Length != 0 || ExecutorUids.Length != 0))
            throw new MultiplayerSessionFailedException("房间未确认本机选择的待机身份");
        if (Phase is not ("waiting" or "regular" or "aborted")
            && (ExecutorUids.Length == 0 || standby.Length == 0 || standby.Any(string.IsNullOrWhiteSpace)
                || standby.Distinct(StringComparer.Ordinal).Count() != standby.Length
                || standby.Intersect(ExecutorUids, StringComparer.Ordinal).Any()
                || !standby.Concat(ExecutorUids).Contains(HostUid, StringComparer.Ordinal)
                || !(standbyOnly ? standby : ExecutorUids).Contains(uid, StringComparer.Ordinal)))
            throw new MultiplayerSessionFailedException("房间确认的执行/待机身份与本机选择不符");
        if (frozen != null && (frozen.HostUid != HostUid
            || !frozen.ExecutorUids.ToHashSet(StringComparer.Ordinal).SetEquals(ExecutorUids)
            || !(frozen.StandbyUids ?? []).ToHashSet(StringComparer.Ordinal).SetEquals(standby)
            || Phase == "waiting" || (frozen.Phase == "regular") != (Phase == "regular")))
            throw new MultiplayerSessionFailedException("借世界房间的已确认名单发生变化");
        if ((Phase is "running" or "completed" && (RouteNames is not { Length: > 0 }
                || ManifestDigest is not { Length: 64 } || !ManifestDigest.All(Uri.IsHexDigit)))
            || (Phase == "noRoutes" && RouteNames is not { Length: 0 }))
            throw new MultiplayerSessionFailedException("借世界房间未确认路线发布结果");
    }
}

public partial class CoordinatorClient
{
    internal bool NegotiatesPlayerStandby { get { lock (_roomLock) return _roomSession?.StandbySessionId != null; } }
    internal bool IsHostWorldOnlyRoom { get { lock (_roomLock) return _roomSession?.FrozenHostWorld?.StandbyUids is { Length: > 0 }; } }

    // Only route execution uses this projection. Party admission, in-game detection and heartbeat use the full roster.
    internal bool IsRouteExecutor(string uid)
    {
        lock (_roomLock)
            return _roomSession?.FrozenHostWorld?.StandbyUids is not { Length: > 0 }
                || _roomSession.FrozenHostWorld.ExecutorUids.Contains(uid, StringComparer.Ordinal);
    }

    internal int? FrozenRouteExecutorCount
    {
        get { lock (_roomLock) return IsHostWorldOnlyRoom ? _roomSession!.FrozenHostWorld!.ExecutorUids.Length : null; }
    }

    private static string? ReadHostWorldBinding(GatewayEnvelope response)
    {
        var sessionId = response.GetString("standbySessionId");
        if (response.GetBool("negotiatesStandby") == string.IsNullOrWhiteSpace(sessionId))
            throw new MultiplayerSessionFailedException("借世界房间模式与实例回执不一致");
        return string.IsNullOrWhiteSpace(sessionId) ? null : sessionId;
    }

    internal async Task ResetStandbyWorldRoundAsync(int newRound, CancellationToken ct)
    {
        RoomSession session;
        lock (_roomLock)
            session = _roomSession is { StandbySessionId: not null, IsRoomOwner: true } current ? current
                : throw new MultiplayerSessionFailedException("没有可初始化的协商房主房间");
        for (var attempt = 0; ; attempt++)
        {
            using var budget = CreateRpcBudget(ct);
            try
            {
                await WaitForBusinessReadyAsync(budget.Token);
                if (!IsCurrent(session)) throw new MultiplayerSessionFailedException("本轮房间已变更");
                var response = await _gateway!.InvokeCommandAsync(GatewayProtocol.Names.WorldResetForNewRound,
                    new { newRound, sessionId = session.StandbySessionId }, session.RoomCode, budget.Token);
                budget.Token.ThrowIfCancellationRequested();
                if (!IsCurrent(session) || !response.GetBool("ack") || response.GetInt("worldRound") != newRound
                    || response.GetString("sessionId") != session.StandbySessionId)
                    throw new MultiplayerSessionFailedException("未收到本轮房间的初始化确认");
                return;
            }
            catch (Exception ex) when (attempt == 0 && !ct.IsCancellationRequested && IsCurrent(session) && IsTransportFailure(ex))
            {
                // Retry the exact room instance while the owner still has not published readiness.
            }
        }
    }

    internal async Task<HostWorldState> ReadOrUpdateHostWorldAsync(CancellationToken ct, string? action = null,
        IReadOnlyList<string>? routeNames = null, bool allowPartial = false, int? partyCount = null,
        string? manifestDigest = null, string? routeName = null, double? durationSeconds = null)
    {
        RoomSession session;
        lock (_roomLock)
            session = _roomSession is { StandbySessionId: not null } room ? room
                : throw new MultiplayerSessionFailedException("本次任务没有已绑定的借世界房间");
        var gateway = _gateway ?? throw new MultiplayerSessionFailedException("借世界房间连接未建立");
        // Replay is bound to this saved instance, even if room.close won the race with an RPC reply.
        using var budget = CreateRpcBudget(ct, includeRoom: false);
        if (IsCurrent(session))
        {
            try { await WaitForBusinessReadyAsync(budget.Token); }
            catch (MultiplayerSessionFailedException) when (!IsCurrent(session) && !ct.IsCancellationRequested) { }
        }
        var response = action == null
            ? await gateway.QueryAsync(GatewayProtocol.Names.RoomHostWorldState,
                new { sessionId = session.StandbySessionId }, session.RoomCode, budget.Token)
            : await gateway.InvokeCommandAsync(GatewayProtocol.Names.RoomHostWorldUpdate,
                new { sessionId = session.StandbySessionId, action, routeNames, allowPartial, partyCount, manifestDigest,
                    routeName, durationSeconds },
                session.RoomCode, budget.Token);
        budget.Token.ThrowIfCancellationRequested();
        var state = response.Get<HostWorldState>("state")
            ?? throw new MultiplayerSessionFailedException("未收到借世界房间状态回执");
        lock (_roomLock)
        {
            if (!ReferenceEquals(_roomSession, session))
                throw new MultiplayerSessionFailedException("借世界房间回执已失去原实例");
            state.RequireIdentity(session.StandbySessionId!, session.Uid ?? "", session.IsRoomOwner,
                HostPlayerUid, session.FrozenHostWorld, session.StandbyOnly);
            if (!state.IsTerminal && session.Ended)
                throw new MultiplayerSessionFailedException("借世界房间已关闭，未确认完工");
            if (state.Phase is "regular" or "ready" or "running" or "completed" or "expCap" or "noRoutes")
                session.FrozenHostWorld ??= state;
        }
        return state;
    }
}
