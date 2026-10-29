using System.Collections.Concurrent;
using System.Text;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using BgiCoordinatorServer.Gateway;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 网页 → 执行端的槲寄生请求中转。服务端只做送达与回投：
/// 目标按 UID + 助手实例精确定位，回复只认目标连接并只投给发起连接。
/// 是否允许执行（本人凭据、共享开关、修订冲突）全部由执行端判定。
/// </summary>
public sealed partial class RoomOperations
{
    private sealed record MistletoePending(string Group, string Requester, string Target, DateTime Expires);
    private readonly ConcurrentDictionary<string, MistletoePending> _mistletoePending = new(StringComparer.Ordinal);
    private static readonly Regex MistletoeToken = new("^[a-zA-Z0-9_.-]{1,100}$", RegexOptions.CultureInvariant);
    private const int MistletoeRequestBytes = 2 * 1024 * 1024 + 64 * 1024;
    private const int MistletoeReplyBytes = 4 * 1024 * 1024;

    private static object MistletoeError(string code, string message) => new { error = new { code, message } };

    public async Task<object> RequestMistletoeAsync(GatewayHandlerContext ctx, string roomCode, string targetUid,
        string targetInstanceId, string requestId, JsonObject? request)
    {
        var group = $"CTRL_{roomCode}";
        if (!IsInControlRoomOrRemote(ctx, group)) return MistletoeError("not_in_room", "不在该控制房间");
        if (string.IsNullOrWhiteSpace(targetUid) || targetUid.Length > 128
            || string.IsNullOrEmpty(targetInstanceId) || !MistletoeToken.IsMatch(targetInstanceId)
            || string.IsNullOrEmpty(requestId) || !MistletoeToken.IsMatch(requestId)
            || request is null || Encoding.UTF8.GetByteCount(request.ToJsonString()) > MistletoeRequestBytes)
            return MistletoeError("invalid_args", "请求参数无效或过大");
        // 同 UID 可能有多个助手实例（执行端与监控端）；必须按实例精确投递，不能广播。
        var target = _roomManager.GetControlRoomPlayers(group).FirstOrDefault(p => p.Online
            && p.PlayerUid == targetUid && p.ClientInstanceId == targetInstanceId);
        if (target is null || string.IsNullOrEmpty(target.ConnectionId))
            return MistletoeError("target_offline", "目标执行端不在线或实例已变化");

        var now = DateTime.UtcNow;
        foreach (var entry in _mistletoePending)
            if (entry.Value.Expires <= now)
                ((ICollection<KeyValuePair<string, MistletoePending>>)_mistletoePending).Remove(entry);
        if (_mistletoePending.Count >= 512) return MistletoeError("busy", "请求过多，请稍后重试");
        var pending = new MistletoePending(group, ctx.ConnectionId, target.ConnectionId, now.AddSeconds(30));
        if (!_mistletoePending.TryAdd(requestId, pending)) return MistletoeError("duplicate_request", "请求编号已在使用");
        await _broadcaster.SendEventOnlyToConnectionAsync(target.ConnectionId, "mistletoe.requested",
            new { requestId, request, requesterConnectionId = pending.Requester, expiresAtUtc = pending.Expires }, roomCode);
        return new { accepted = true };
    }

    public async Task<object> RespondMistletoeAsync(GatewayHandlerContext ctx, string roomCode, string requestId, JsonObject? reply)
    {
        var group = $"CTRL_{roomCode}";
        if (string.IsNullOrEmpty(requestId) || !IsInControlRoomOrRemote(ctx, group)
            || !_mistletoePending.TryGetValue(requestId, out var pending)
            || pending.Group != group || pending.Target != ctx.ConnectionId || pending.Expires <= DateTime.UtcNow
            || !_roomManager.IsInControlRoom(group, pending.Target)
            || (!_roomManager.IsInControlRoom(group, pending.Requester) && !_roomManager.IsRemoteConnection(group, pending.Requester)))
            return MistletoeError("invalid_request", "请求不存在、已过期或身份不匹配");
        if (reply is null || Encoding.UTF8.GetByteCount(reply.ToJsonString()) > MistletoeReplyBytes)
            return MistletoeError("invalid_args", "回复无效或过大");
        if (!((ICollection<KeyValuePair<string, MistletoePending>>)_mistletoePending)
            .Remove(new KeyValuePair<string, MistletoePending>(requestId, pending)))
            return MistletoeError("invalid_request", "请求已处理");
        await _broadcaster.SendEventOnlyToConnectionAsync(pending.Requester, "mistletoe.result",
            new { requestId, reply }, roomCode);
        return new { accepted = true };
    }
}
