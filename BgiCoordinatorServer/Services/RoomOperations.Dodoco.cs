using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BgiCoordinatorServer.Gateway;

namespace BgiCoordinatorServer.Services;

public sealed partial class RoomOperations
{
    private sealed record DodocoPending(string Group, string Requester, string Target, DateTime Expires);
    private readonly ConcurrentDictionary<string, DodocoPending> _dodocoPending = new(StringComparer.Ordinal);
    private static readonly Regex DodocoToken = new("^[a-zA-Z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);
    private static readonly HashSet<string> DodocoOperations = new(StringComparer.Ordinal)
    {
        "watch.read", "watch.records", "watch.rule.upsert", "watch.rule.delete", "watch.settings",
        "watch.clear", "watch.incidents", "watch.frame", "watch.diagnostic.start", "watch.diagnostic.chunk",
        "report.dates", "report.day"
    };

    private static object DodocoError(string code, string message) => new { error = new { code, message } };

    public async Task<object> RequestDodocoAsync(GatewayHandlerContext ctx, string roomCode, string targetUid,
        string requestId, string operation, JsonObject? args)
    {
        var group = $"CTRL_{roomCode}";
        if (!IsInControlRoomOrRemote(ctx, group)) return DodocoError("not_in_room", "不在该控制房间");
        if (string.IsNullOrWhiteSpace(targetUid) || targetUid.Length > 128 ||
            string.IsNullOrEmpty(requestId) || !DodocoToken.IsMatch(requestId) ||
            string.IsNullOrEmpty(operation) || !DodocoOperations.Contains(operation) ||
            args is null || Encoding.UTF8.GetByteCount(args.ToJsonString()) > 16384)
            return DodocoError("invalid_args", "请求参数无效或过大");
        var target = _roomManager.GetConnectionIdByUid(group, targetUid);
        if (string.IsNullOrEmpty(target) || !_roomManager.GetControlRoomPlayers(group)
                .Any(p => p.ConnectionId == target && p.PlayerUid == targetUid && p.Online))
            return DodocoError("target_offline", "目标成员不在线");

        var now = DateTime.UtcNow;
        foreach (var entry in _dodocoPending)
            if (entry.Value.Expires <= now)
                ((ICollection<KeyValuePair<string, DodocoPending>>)_dodocoPending).Remove(entry);
        if (_dodocoPending.Count >= 512) return DodocoError("busy", "请求过多，请稍后重试");
        var pending = new DodocoPending(group, ctx.ConnectionId, target, now.AddSeconds(30));
        if (!_dodocoPending.TryAdd(requestId, pending)) return DodocoError("duplicate_request", "请求编号已在使用");
        await _broadcaster.SendEventOnlyToConnectionAsync(target, "dodoco.requested",
            new { requestId, operation, args, requesterConnectionId = pending.Requester, expiresAtUtc = pending.Expires }, roomCode);
        return new { accepted = true };
    }

    public async Task<object> RespondDodocoAsync(GatewayHandlerContext ctx, string roomCode, string requestId,
        string status, string errorCode, string message, JsonObject? data)
    {
        var group = $"CTRL_{roomCode}";
        if (string.IsNullOrEmpty(requestId) || !IsInControlRoomOrRemote(ctx, group) ||
            !_dodocoPending.TryGetValue(requestId, out var pending) ||
            pending.Group != group || pending.Target != ctx.ConnectionId || pending.Expires <= DateTime.UtcNow ||
            !_roomManager.IsInControlRoom(group, pending.Target) ||
            (!_roomManager.IsInControlRoom(group, pending.Requester) && !_roomManager.IsRemoteConnection(group, pending.Requester)))
            return DodocoError("invalid_request", "请求不存在、已过期或身份不匹配");
        if (status is not ("ok" or "denied" or "error") || (errorCode?.Length ?? 0) > 100 ||
            (message?.Length ?? 0) > 1000 || (data != null && Encoding.UTF8.GetByteCount(data.ToJsonString()) > 512 * 1024))
            return DodocoError("invalid_args", "响应参数无效或过大");
        if (!((ICollection<KeyValuePair<string, DodocoPending>>)_dodocoPending)
            .Remove(new KeyValuePair<string, DodocoPending>(requestId, pending)))
            return DodocoError("invalid_request", "请求已处理");
        await _broadcaster.SendEventOnlyToConnectionAsync(pending.Requester, "dodoco.result",
            new { requestId, status, errorCode, message, data }, roomCode);
        return new { accepted = true };
    }
}
