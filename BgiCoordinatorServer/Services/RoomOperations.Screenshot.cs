namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    // screen:{GuidN}:{GuidN} 共 72 字符；日志传输仍使用原 64 字符上限。
    private const int MaxScreenshotRequestIdLength = 72;
    private const int MaxScreenshotViewers = 1024;
    private const int MaxScreenshotSessionsPerViewer = 128;
    private const int MaxPendingScreenshotRequests = 8192;
    private static readonly TimeSpan ScreenshotWindow = TimeSpan.FromMinutes(3);
    private readonly TimeProvider _screenshotTime;
    private readonly object _screenshotGate = new();
    private readonly Dictionary<(string Group, string Connection), ScreenshotViewer> _screenshotViewers = new();
    private readonly Dictionary<(string Group, string RequestId), ScreenshotRequest> _screenshotRequests = new();

    private sealed class ScreenshotViewer
    {
        // 保留最近会话的固定起点；仅真正的新 session 才淘汰最早历史，连续取帧不会清理/续期。
        // 旧协议的空 session 永不淘汰，避免兼容连接自动续期。
        public Dictionary<string, long> Sessions { get; } = new(StringComparer.Ordinal);
        public Queue<string> SessionOrder { get; } = new();
        public Dictionary<string, long> LastRequestByTarget { get; } = new(StringComparer.Ordinal);
    }

    private sealed record ScreenshotRequest(string RequesterConnectionId, string TargetConnectionId,
        string TargetUid, string SessionId, long StartedAt);

    private static bool TryParseScreenshotSession(string requestId, out string sessionId)
    {
        sessionId = ""; // 旧客户端：同一连接所有无格式 requestId 共用一次窗口。
        if (string.IsNullOrEmpty(requestId) || requestId.Length > MaxScreenshotRequestIdLength) return false;
        if (!requestId.StartsWith("screen:", StringComparison.Ordinal))
            return requestId.Length <= MaxRequestIdLength;
        if (requestId.Length != MaxScreenshotRequestIdLength || requestId[39] != ':'
            || !Guid.TryParseExact(requestId.AsSpan(7, 32), "N", out var session)
            || !Guid.TryParseExact(requestId.AsSpan(40, 32), "N", out _)) return false;
        sessionId = session.ToString("N");
        return true;
    }

    private bool ScreenshotExpired(long startedAt, long now)
        => _screenshotTime.GetElapsedTime(startedAt, now) >= ScreenshotWindow;

    private bool RegisterScreenshotRequest(string group, string requestId, string sessionId,
        string requesterConnection, string? targetConnection, string targetUid)
    {
        lock (_screenshotGate)
        {
            // 与断线清理共用锁并再次核对身份，避免入口校验后断线又留下新状态。
            if (!IsScreenshotRequesterInRoom(group, requesterConnection)) return false;
            var now = _screenshotTime.GetTimestamp();
            var key = (group, requesterConnection);
            if (!_screenshotViewers.TryGetValue(key, out var viewer))
            {
                if (_screenshotViewers.Count >= MaxScreenshotViewers) return false;
                _screenshotViewers.Add(key, viewer = new());
            }
            if (!viewer.Sessions.TryGetValue(sessionId, out var startedAt))
            {
                if (viewer.Sessions.Count >= MaxScreenshotSessionsPerViewer)
                {
                    var oldest = viewer.SessionOrder.Dequeue();
                    viewer.Sessions.Remove(oldest);
                    foreach (var pending in _screenshotRequests.Where(x => x.Key.Group == group
                                 && x.Value.RequesterConnectionId == requesterConnection && x.Value.SessionId == oldest)
                                 .Select(x => x.Key).ToArray())
                        _screenshotRequests.Remove(pending);
                }
                viewer.Sessions.Add(sessionId, startedAt = now);
                if (sessionId.Length > 0) viewer.SessionOrder.Enqueue(sessionId);
            }
            if (ScreenshotExpired(startedAt, now) || string.IsNullOrEmpty(targetConnection)) return false;

            // 按连接而非 UID 限流，多个观看者（包括共享 UID 的遥控端）互不抢额度。
            foreach (var target in viewer.LastRequestByTarget
                         .Where(x => _screenshotTime.GetElapsedTime(x.Value, now) >= TimeSpan.FromSeconds(1))
                         .Select(x => x.Key).ToArray())
                viewer.LastRequestByTarget.Remove(target);
            if (viewer.LastRequestByTarget.ContainsKey(targetUid) || viewer.LastRequestByTarget.Count >= 64) return false;

            foreach (var pending in _screenshotRequests.Where(x => ScreenshotExpired(x.Value.StartedAt, now)).Select(x => x.Key).ToArray())
                _screenshotRequests.Remove(pending);
            if (_screenshotRequests.Count >= MaxPendingScreenshotRequests
                || !_screenshotRequests.TryAdd((group, requestId), new(requesterConnection, targetConnection, targetUid, sessionId, startedAt)))
                return false;
            viewer.LastRequestByTarget[targetUid] = now;
            return true;
        }
    }

    private bool TryTakeScreenshotRequest(string group, string requestId, string targetConnection,
        string targetUid, out ScreenshotRequest request)
    {
        lock (_screenshotGate)
        {
            request = null!;
            if (!_screenshotRequests.TryGetValue((group, requestId), out var pending)) return false;
            if (ScreenshotExpired(pending.StartedAt, _screenshotTime.GetTimestamp()))
            {
                _screenshotRequests.Remove((group, requestId));
                return false;
            }
            // 伪造应答不能消费真正目标的请求。
            if (pending.TargetConnectionId != targetConnection || pending.TargetUid != targetUid) return false;
            _screenshotRequests.Remove((group, requestId));
            if (!IsScreenshotRequesterInRoom(group, pending.RequesterConnectionId)) return false;
            request = pending;
            return true;
        }
    }

    private bool IsScreenshotRequesterInRoom(string group, string connectionId)
        => _roomManager.GetUidByConnectionId(group, connectionId) != null
            || _roomManager.GetRemoteUidByConnectionId(group, connectionId) != null;

    private void ClearScreenshotConnection(string connectionId)
    {
        lock (_screenshotGate)
        {
            foreach (var key in _screenshotViewers.Keys.Where(x => x.Connection == connectionId).ToArray())
                _screenshotViewers.Remove(key);
            foreach (var key in _screenshotRequests.Where(x => x.Value.RequesterConnectionId == connectionId
                         || x.Value.TargetConnectionId == connectionId).Select(x => x.Key).ToArray())
                _screenshotRequests.Remove(key);
        }
    }
}
