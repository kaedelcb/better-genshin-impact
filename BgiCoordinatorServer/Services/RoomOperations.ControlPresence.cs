using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private sealed class PresenceLease(string group, string requester, string targetUid, string targetConnection,
        string sessionId, string frameId, string controlId, long windowStarted, long lastSeen)
    {
        public readonly string Group = group, Requester = requester, TargetUid = targetUid,
            TargetConnection = targetConnection, SessionId = sessionId, FrameId = frameId, ControlId = controlId;
        public readonly long WindowStarted = windowStarted;
        public long LastSeen = lastSeen;
        public bool Confirmed;
    }
    private sealed record PresencePending(string Group, string Requester, string TargetConnection,
        string TargetUid, string ControlId, string Action, long SentAt);
    private readonly Dictionary<(string Group, string Requester, string TargetUid), PresenceLease> _presenceLeases = new();
    private readonly Dictionary<(string Group, string RequestId), PresencePending> _presencePending = new();
    private readonly Dictionary<(string Group, string Requester, string ControlId), long> _presenceClosed = new();
    private const int MaxPresenceRecords = 8192;
    // 若暂时放不下墓碑，阻止新 start 直到最长重放窗口过去，不能把满表当成允许复活。
    private long? _presenceOverflowFence;

    // 调用方持有 _screenshotGate。过期后保留墓碑，迟到 start/renew 不能重新点亮提示。
    private void PrunePresence(long now)
    {
        foreach (var key in _presenceClosed.Where(x => ScreenshotExpired(x.Value, now)).Select(x => x.Key).ToArray())
            _presenceClosed.Remove(key);
        if (_presenceOverflowFence is { } fence && ScreenshotExpired(fence, now)) _presenceOverflowFence = null;
        foreach (var pair in _presenceLeases.Where(x => ScreenshotExpired(x.Value.WindowStarted, now)
                     || _screenshotTime.GetElapsedTime(x.Value.LastSeen, now) >= DesktopControlProtocol.PresenceLifetime).ToArray())
        {
            _presenceLeases.Remove(pair.Key);
            MarkPresenceClosed(pair.Value.Group, pair.Value.Requester, pair.Value.ControlId, now);
        }
        foreach (var key in _presencePending.Where(x => _screenshotTime.GetElapsedTime(x.Value.SentAt, now)
                     >= DesktopControlProtocol.RequestLifetime).Select(x => x.Key).ToArray())
            _presencePending.Remove(key);
    }

    private void MarkPresenceClosed(string group, string requester, string controlId, long now)
    {
        var key = (group, requester, controlId);
        if (_presenceClosed.ContainsKey(key)) return;
        if (_presenceClosed.Count < MaxPresenceRecords) _presenceClosed.Add(key, now);
        else _presenceOverflowFence = now;
    }

    private bool HasConfirmedPresence(string group, string requester, string targetUid, string targetConnection,
        string sessionId, string controlId, long now)
    {
        PrunePresence(now);
        return _presenceLeases.TryGetValue((group, requester, targetUid), out var lease)
            && lease.Confirmed && lease.ControlId == controlId && lease.SessionId == sessionId
            && lease.TargetConnection == targetConnection;
    }

    public async Task<object> RequestControlPresenceAsync(GatewayHandlerContext ctx, string roomCode, DesktopControlPresenceRequest? request)
    {
        var group = $"CTRL_{roomCode}";
        if (request == null || !Guid.TryParseExact(request.RequestId, "N", out _)
            || !Guid.TryParseExact(request.ControlId, "N", out _)
            || request.TargetUid is not { Length: > 0 and <= 128 }
            || request.Action is not ("start" or "renew" or "end")
            || !TryParseScreenshotSession(request.FrameId, out var sessionId) || sessionId.Length == 0)
            return ControlError("invalid_args", "无效的远控会话");
        DesktopControlPresenceRequest? outgoing = null, superseded = null;
        ControlPending[] cancelled = [];
        string target, supersededTarget = "";
        lock (_screenshotGate)
        {
            if (!IsScreenshotRequesterInRoom(group, ctx.ConnectionId)) return ControlError("not_in_room", "不在该控制房间");
            var now = _screenshotTime.GetTimestamp();
            PrunePresence(now);
            var key = (group, ctx.ConnectionId, request.TargetUid);
            var closed = (group, ctx.ConnectionId, request.ControlId);
            target = _roomManager.GetConnectionIdByUid(group, request.TargetUid) ?? "";
            _presenceLeases.TryGetValue(key, out var lease);
            if (request.Action == "end")
            {
                MarkPresenceClosed(group, ctx.ConnectionId, request.ControlId, now);
                // 旧会话的结束不能清除同一观看者已重新开启的新会话。
                if (lease != null && lease.ControlId == request.ControlId && lease.SessionId == sessionId)
                {
                    _presenceLeases.Remove(key);
                    target = lease.TargetConnection;
                    // 最新截图可能已为下一次控制准备，不能由旧租约结束移除。
                    cancelled = _controlPending.Values.Where(x => x.Group == group && x.Requester == ctx.ConnectionId
                        && x.Command.TargetUid == request.TargetUid && x.Command.ControlId == request.ControlId).ToArray();
                    foreach (var item in cancelled) _controlPending.Remove((item.Group, item.Command.RequestId));
                }
                foreach (var pending in _presencePending.Where(x => x.Value.Group == group && x.Value.Requester == ctx.ConnectionId
                             && x.Value.ControlId == request.ControlId).Select(x => x.Key).ToArray()) _presencePending.Remove(pending);
                if (target.Length > 0) outgoing = request with { RequesterConnectionId = ctx.ConnectionId, RoomCode = roomCode, LifetimeMilliseconds = 0 };
            }
            else
            {
                if (_presenceClosed.ContainsKey(closed)
                    || (request.Action == "start" && (_presenceClosed.Count >= MaxPresenceRecords || _presenceOverflowFence.HasValue)))
                    return ControlError("presence_closed", "远控已结束或暂不可用，请稍后重新开启");
                if (_presencePending.Count >= MaxPresenceRecords || _presencePending.ContainsKey((group, request.RequestId)))
                    return ControlError("busy", "控制状态请求过多或编号重复");
                if (target.Length == 0) return ControlError("target_offline", "目标成员离线");
                if (request.Action == "start")
                {
                    if (!_controlFrames.TryGetValue(key, out var frame) || frame.FrameId != request.FrameId
                        || frame.TargetConnection != target || frame.SessionId != sessionId
                        || ScreenshotExpired(frame.WindowStarted, now)
                        || _screenshotTime.GetElapsedTime(frame.ReceivedAt, now) >= DesktopControlProtocol.FrameLifetime)
                        return ControlError("frame_unavailable", "请刷新已授权的最新画面");
                    if (lease != null && lease.ControlId == request.ControlId)
                        return ControlError("duplicate_request", "该控制会话已经开始");
                    if (lease != null)
                    {
                        MarkPresenceClosed(group, ctx.ConnectionId, lease.ControlId, now);
                        supersededTarget = lease.TargetConnection;
                        superseded = new DesktopControlPresenceRequest { RequestId = Guid.NewGuid().ToString("N"),
                            ControlId = lease.ControlId, TargetUid = lease.TargetUid, FrameId = lease.FrameId,
                            Action = "end", RequesterConnectionId = ctx.ConnectionId, RoomCode = roomCode };
                        cancelled = _controlPending.Values.Where(x => x.Group == group && x.Requester == ctx.ConnectionId
                            && x.Command.TargetUid == request.TargetUid && x.Command.ControlId == lease.ControlId).ToArray();
                        foreach (var item in cancelled) _controlPending.Remove((item.Group, item.Command.RequestId));
                        foreach (var pending in _presencePending.Where(x => x.Value.Group == group && x.Value.Requester == ctx.ConnectionId
                                     && x.Value.ControlId == lease.ControlId).Select(x => x.Key).ToArray()) _presencePending.Remove(pending);
                    }
                    if (lease == null && _presenceLeases.Count >= MaxPresenceRecords)
                        return ControlError("busy", "控制会话过多，请稍后重试");
                    lease = new(group, ctx.ConnectionId, request.TargetUid, target, sessionId, request.FrameId,
                        request.ControlId, frame.WindowStarted, now);
                    _presenceLeases[key] = lease;
                    // start 的帧龄为目标端提供跨机器时钟无关的三秒显示确认预算。
                    var remainingWindow = ScreenshotWindow - _screenshotTime.GetElapsedTime(frame.WindowStarted, now);
                    outgoing = request with { RequesterConnectionId = ctx.ConnectionId, RoomCode = roomCode,
                        LifetimeMilliseconds = Math.Min(DesktopControlProtocol.PresenceLifetime.TotalMilliseconds, remainingWindow.TotalMilliseconds),
                        FrameAgeMilliseconds = _screenshotTime.GetElapsedTime(frame.ReceivedAt, now).TotalMilliseconds };
                }
                else
                {
                    if (lease == null || !lease.Confirmed || lease.ControlId != request.ControlId
                        || lease.SessionId != sessionId || lease.TargetConnection != target)
                        return ControlError("presence_expired", "远控会话已结束，请刷新画面后重新开启");
                    lease.LastSeen = now;
                    var remainingWindow = ScreenshotWindow - _screenshotTime.GetElapsedTime(lease.WindowStarted, now);
                    outgoing = request with { RequesterConnectionId = ctx.ConnectionId, RoomCode = roomCode,
                        LifetimeMilliseconds = Math.Min(DesktopControlProtocol.PresenceLifetime.TotalMilliseconds, remainingWindow.TotalMilliseconds),
                        FrameAgeMilliseconds = 0 };
                }
                _presencePending.Add((group, request.RequestId), new(group, ctx.ConnectionId, target,
                    request.TargetUid, request.ControlId, request.Action, now));
            }
        }
        if (superseded != null) await SendPresenceEventAsync(supersededTarget, roomCode, superseded);
        await NotifyControlCancelledAsync(cancelled);
        if (outgoing != null) await SendPresenceEventAsync(target, roomCode, outgoing);
        return new { accepted = true };
    }

    public async Task<object> RespondControlPresenceAsync(GatewayHandlerContext ctx, string roomCode, DesktopControlPresenceResult? result)
    {
        var group = $"CTRL_{roomCode}";
        if (result == null || !Guid.TryParseExact(result.RequestId, "N", out _)
            || !Guid.TryParseExact(result.ControlId, "N", out _) || result.TargetUid is not { Length: > 0 and <= 128 }
            || result.Status is not ("active" or "denied") || result.Message is not { Length: <= 1000 })
            return ControlError("invalid_args", "无效的控制状态回执");
        PresencePending pending;
        lock (_screenshotGate)
        {
            var now = _screenshotTime.GetTimestamp();
            PrunePresence(now);
            if (!_presencePending.TryGetValue((group, result.RequestId), out pending!)
                || pending.TargetConnection != ctx.ConnectionId || pending.TargetUid != result.TargetUid
                || pending.ControlId != result.ControlId || !IsScreenshotRequesterInRoom(group, pending.Requester)
                || _roomManager.GetConnectionIdByUid(group, result.TargetUid) != ctx.ConnectionId
                || !_presenceLeases.TryGetValue((group, pending.Requester, result.TargetUid), out var lease)
                || lease.ControlId != result.ControlId || lease.TargetConnection != ctx.ConnectionId)
                return ControlError("invalid_request", "控制会话已结束或身份不匹配");
            _presencePending.Remove((group, result.RequestId));
            if (result.Status == "active") lease.Confirmed = true;
            else
            {
                _presenceLeases.Remove((group, pending.Requester, result.TargetUid));
                MarkPresenceClosed(group, pending.Requester, result.ControlId, now);
            }
        }
        await _broadcaster.SendEventOnlyToConnectionAsync(pending.Requester, "desktop.control.presence.result", result, roomCode);
        return new { accepted = true };
    }

    private async Task ClearControlPresenceConnectionAsync(string connectionId)
    {
        PresenceLease[] ended;
        lock (_screenshotGate)
        {
            var now = _screenshotTime.GetTimestamp();
            ended = _presenceLeases.Values.Where(x => x.Requester == connectionId || x.TargetConnection == connectionId).ToArray();
            foreach (var lease in ended)
            {
                _presenceLeases.Remove((lease.Group, lease.Requester, lease.TargetUid));
                MarkPresenceClosed(lease.Group, lease.Requester, lease.ControlId, now);
            }
            foreach (var key in _presencePending.Where(x => x.Value.Requester == connectionId || x.Value.TargetConnection == connectionId)
                         .Select(x => x.Key).ToArray()) _presencePending.Remove(key);
        }
        foreach (var lease in ended.Where(x => x.Requester == connectionId && x.TargetConnection != connectionId))
            await SendPresenceEventAsync(lease.TargetConnection, lease.Group[5..], new DesktopControlPresenceRequest
            {
                RequestId = Guid.NewGuid().ToString("N"), ControlId = lease.ControlId, TargetUid = lease.TargetUid,
                FrameId = lease.FrameId, Action = "end", RequesterConnectionId = lease.Requester, RoomCode = lease.Group[5..]
            });
    }

    private async Task SendPresenceEventAsync(string connection, string roomCode, DesktopControlPresenceRequest request)
    {
        try { await _broadcaster.SendEventOnlyToConnectionAsync(connection, "desktop.control.presence.requested", request, roomCode); }
        catch (Exception ex) { _logger.LogDebug(ex, "发送远控状态失败，目标租约会自行过期"); }
    }
}
