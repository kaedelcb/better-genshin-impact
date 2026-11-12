using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private sealed record ControlFrame(string FrameId, string TargetConnection, string SessionId,
        long WindowStarted, long ReceivedAt, DesktopCaptureGeometry Geometry);
    private sealed record ControlPending(string Group, string Requester, string Target, string SessionId,
        DesktopControlCommand Command, long SentAt, long WindowStarted);
    // 与截图表共用锁，权限依据只来自实际转发给该观看连接的帧。
    private readonly Dictionary<(string Group, string Requester, string TargetUid), ControlFrame> _controlFrames = new();
    private readonly Dictionary<(string Group, string RequestId), ControlPending> _controlPending = new();
    private readonly Dictionary<(string Group, string Requester, string RequestId), long> _controlSeen = new();
    private readonly Dictionary<string, long> _controlLastInput = new(StringComparer.Ordinal);
    private const int MaxControlRecords = 8192;

    private static object ControlError(string code, string message) => new { error = new { code, message } };

    private void RememberControlFrame(string group, string frameId, ScreenshotRequest request,
        DesktopCaptureGeometry? geometry, bool controlAllowed)
    {
        lock (_screenshotGate)
        {
            var key = (group, request.RequesterConnectionId, request.TargetUid);
            _controlFrames.Remove(key); // 无控制权限/元数据的最新帧同样撤销旧帧依据。
            var now = _screenshotTime.GetTimestamp();
            PruneControlRecords(now);
            if (!controlAllowed || geometry?.IsValid != true || request.SessionId.Length == 0
                || ScreenshotExpired(request.StartedAt, now) || _controlFrames.Count >= MaxControlRecords) return;
            _controlFrames[key] = new(frameId, request.TargetConnectionId, request.SessionId, request.StartedAt, now, geometry);
        }
    }

    private void PruneControlRecords(long now)
    {
        foreach (var item in _controlFrames.Where(x => ScreenshotExpired(x.Value.WindowStarted, now)
                     || _screenshotTime.GetElapsedTime(x.Value.ReceivedAt, now) >= DesktopControlProtocol.FrameLifetime).Select(x => x.Key).ToArray())
            _controlFrames.Remove(item);
        foreach (var item in _controlPending.Where(x => ScreenshotExpired(x.Value.WindowStarted, now)
                     || _screenshotTime.GetElapsedTime(x.Value.SentAt, now) >= DesktopControlProtocol.RequestLifetime).Select(x => x.Key).ToArray())
            _controlPending.Remove(item);
        foreach (var item in _controlSeen.Where(x => ScreenshotExpired(x.Value, now)).Select(x => x.Key).ToArray())
            _controlSeen.Remove(item);
        foreach (var item in _controlLastInput.Where(x => _screenshotTime.GetElapsedTime(x.Value, now) >= DesktopControlProtocol.InputInterval).Select(x => x.Key).ToArray())
            _controlLastInput.Remove(item);
    }

    public async Task<object> RequestDesktopControlAsync(GatewayHandlerContext ctx, string roomCode, DesktopControlCommand? command)
    {
        var group = $"CTRL_{roomCode}";
        if (command == null || !DesktopControlProtocol.IsValid(command)) return ControlError("invalid_args", "不支持的输入或坐标无效");
        DesktopControlRequest outgoing;
        string target;
        lock (_screenshotGate)
        {
            if (!IsScreenshotRequesterInRoom(group, ctx.ConnectionId)) return ControlError("not_in_room", "不在该控制房间");
            var now = _screenshotTime.GetTimestamp();
            PruneControlRecords(now);
            var seen = (group, ctx.ConnectionId, command.RequestId);
            if (_controlSeen.ContainsKey(seen)) return ControlError("duplicate_request", "该操作已处理，不会再次执行");
            if (_controlSeen.Count >= MaxControlRecords || _controlPending.Count >= MaxControlRecords)
                return ControlError("busy", "控制请求过多，请稍后刷新画面");
            _controlSeen.Add(seen, now);
            var frameKey = (group, ctx.ConnectionId, command.TargetUid);
            if (!_controlFrames.TryGetValue(frameKey, out var frame) || frame.FrameId != command.FrameId)
                return ControlError("frame_unavailable", "画面过期、已使用或对方未允许控制，请刷新画面");
            target = _roomManager.GetConnectionIdByUid(group, command.TargetUid) ?? "";
            if (target.Length == 0 || target != frame.TargetConnection)
                return ControlError("target_offline", "成员连接已变化，请刷新画面");
            if (!HasConfirmedPresence(group, ctx.ConnectionId, command.TargetUid, target, frame.SessionId, command.ControlId, now))
                return ControlError("presence_required", "对方的控制提示尚未确认或已结束，请重新开启控制");
            if (_controlLastInput.ContainsKey(target)) return ControlError("rate_limited", "每个被控端每两秒只能执行一次操作");
            if (_controlPending.ContainsKey((group, command.RequestId))) return ControlError("duplicate_request", "请求编号已被使用");
            _controlFrames.Remove(frameKey); // 一帧只执行一次；所有结果都要求新帧。
            _controlLastInput[target] = now;
            _controlPending.Add((group, command.RequestId), new(group, ctx.ConnectionId, target, frame.SessionId,
                command, now, frame.WindowStarted));
            var remainingWindow = ScreenshotWindow - _screenshotTime.GetElapsedTime(frame.WindowStarted, now);
            var lifetime = remainingWindow < DesktopControlProtocol.RequestLifetime ? remainingWindow : DesktopControlProtocol.RequestLifetime;
            outgoing = new() { Command = command, RoomCode = roomCode, RequesterConnectionId = ctx.ConnectionId,
                ExpiresAtUtc = _screenshotTime.GetUtcNow() + lifetime,
                FrameAgeMilliseconds = _screenshotTime.GetElapsedTime(frame.ReceivedAt, now).TotalMilliseconds,
                LifetimeMilliseconds = lifetime.TotalMilliseconds };
        }
        try
        {
            await _broadcaster.SendEventOnlyToConnectionAsync(target, "desktop.control.requested", outgoing, roomCode);
            return new { accepted = true };
        }
        catch
        {
            lock (_screenshotGate) _controlPending.Remove((group, command.RequestId));
            // 不重发：传输异常不能推断对端一定没有执行。
            return ControlError("transport_unknown", "转发结果未确认，请刷新画面核对；不会自动重试输入");
        }
    }

    public async Task<object> RespondDesktopControlAsync(GatewayHandlerContext ctx, string roomCode, DesktopControlResult? result)
    {
        var group = $"CTRL_{roomCode}";
        if (result == null || !Guid.TryParseExact(result.RequestId, "N", out _)
            || result.Message is not { Length: <= 1000 }
            || result.Status is not ("executed" or "denied" or "error" or "expired" or "busy" or "cancelled"))
            return ControlError("invalid_args", "无效的控制回执");
        ControlPending pending;
        lock (_screenshotGate)
        {
            PruneControlRecords(_screenshotTime.GetTimestamp());
            if (!_controlPending.TryGetValue((group, result.RequestId), out pending!)
                || pending.Target != ctx.ConnectionId || pending.Command.TargetUid != result.TargetUid
                || !IsScreenshotRequesterInRoom(group, ctx.ConnectionId)
                || _roomManager.GetConnectionIdByUid(group, result.TargetUid) != ctx.ConnectionId
                || !IsScreenshotRequesterInRoom(group, pending.Requester))
                return ControlError("invalid_request", "请求已结束或连接身份不符");
            _controlPending.Remove((group, result.RequestId));
        }
        await _broadcaster.SendEventOnlyToConnectionAsync(pending.Requester, "desktop.control.result", result, roomCode);
        return new { accepted = true };
    }

    public async Task<object> CancelDesktopControlAsync(GatewayHandlerContext ctx, string roomCode, string sessionId, string? targetUid, string? controlId, string? requestId = null)
    {
        var group = $"CTRL_{roomCode}";
        if (!Guid.TryParseExact(sessionId, "N", out _) || !Guid.TryParseExact(controlId, "N", out _)
            || (!string.IsNullOrEmpty(requestId) && !Guid.TryParseExact(requestId, "N", out _))
            || targetUid is not { Length: > 0 and <= 128 } || !IsScreenshotRequesterInRoom(group, ctx.ConnectionId))
            return ControlError("invalid_request", "无效的控制会话");
        ControlPending[] cancelled;
        lock (_screenshotGate)
        {
            var now = _screenshotTime.GetTimestamp();
            PruneControlRecords(now);
            if (!string.IsNullOrEmpty(requestId))
            {
                var seen = (group, ctx.ConnectionId, requestId);
                if (!_controlSeen.ContainsKey(seen))
                {
                    if (_controlSeen.Count >= MaxControlRecords) return ControlError("busy", "控制请求过多，请稍后刷新画面");
                    _controlSeen.Add(seen, now); // 取消先到时，迟到的同一输入也不能执行。
                }
            }
            cancelled = _controlPending.Values.Where(x => x.Group == group && x.Requester == ctx.ConnectionId
                && x.SessionId == sessionId && x.Command.TargetUid == targetUid && x.Command.ControlId == controlId
                && (string.IsNullOrEmpty(requestId) || x.Command.RequestId == requestId)).ToArray();
            foreach (var item in cancelled) _controlPending.Remove((item.Group, item.Command.RequestId));
            // 尚未消费的最新帧不属于某个 controlId；旧取消不能移除新会话的截图。
        }
        await NotifyControlCancelledAsync(cancelled);
        return new { accepted = true };
    }

    private async Task ClearDesktopControlConnectionAsync(string connectionId)
    {
        ControlPending[] cancelled;
        lock (_screenshotGate)
        {
            cancelled = _controlPending.Values.Where(x => x.Requester == connectionId || x.Target == connectionId).ToArray();
            foreach (var item in cancelled) _controlPending.Remove((item.Group, item.Command.RequestId));
            foreach (var key in _controlFrames.Where(x => x.Key.Requester == connectionId || x.Value.TargetConnection == connectionId).Select(x => x.Key).ToArray())
                _controlFrames.Remove(key);
            foreach (var key in _controlSeen.Keys.Where(x => x.Requester == connectionId).ToArray()) _controlSeen.Remove(key);
            _controlLastInput.Remove(connectionId);
        }
        await NotifyControlCancelledAsync(cancelled);
    }

    private async Task NotifyControlCancelledAsync(IEnumerable<ControlPending> pending)
    {
        foreach (var item in pending)
        {
            try { await _broadcaster.SendEventOnlyToConnectionAsync(item.Target, "desktop.control.cancelled",
                new { requestId = item.Command.RequestId }, item.Group[5..]); }
            catch (Exception ex) { _logger.LogDebug(ex, "发送控制取消通知失败；短时截止仍生效"); }
        }
    }
}
