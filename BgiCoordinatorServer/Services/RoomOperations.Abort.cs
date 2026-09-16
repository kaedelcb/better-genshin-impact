using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 协同中止族（hoeing-multiplayer-coordinated-abort-restart）。
/// 任一成员触发真异常中止（掉出房间、成员 Offline、锚点失败等）时上报，
/// 服务端做房间级幂等广播，让全组秒级同步收口，避免各端靠 30s 视觉窗各自超时。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>
    /// 上报协同中止：校验调用连接确为房内玩家（reporterUid 非空时比对 PlayerUid，防冒名），
    /// 房间级幂等（首报 wins，多端近同时上报只广播一次，防风暴）后全组广播 CoordinatedAborted
    /// （含上报者自身，沿用全组广播惯例）。
    /// 不在房间（房间已关闭/旧端迟到上报）→ 仅记日志忽略。
    /// </summary>
    public async Task ReportCoordinatedAbortAsync(GatewayHandlerContext ctx, string? reason, string? reporterUid)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[CoordinatedAbort] 连接 {ConnId} 不在任何房间，协同中止上报被忽略（原因={Reason}）",
                ctx.ConnectionId, reason);
            return;
        }

        lock (room)
        {
            // 鉴权：reporterUid 非空时，禁止以他人身份上报（仿 ReportMemberProgressAsync）
            if (!string.IsNullOrEmpty(reporterUid))
            {
                var player = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId);
                if (player == null || player.PlayerUid != reporterUid)
                {
                    _logger.LogWarning("[CoordinatedAbort] 鉴权失败：连接 {ConnId} 上报 reporterUid={ReportedUid} 与本连接玩家不一致，忽略",
                        ctx.ConnectionId, reporterUid);
                    return;
                }
            }

            // 幂等：已广播过直接返回（4 端近同时上报只广播一次）
            if (room.CoordinatedAbortBroadcasted)
            {
                _logger.LogDebug("[CoordinatedAbort] 房间 {Code} 已广播过协同中止，重复上报忽略（reporter={ReporterUid}）",
                    roomCode, reporterUid);
                return;
            }
            room.CoordinatedAbortBroadcasted = true;
        }

        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        _logger.LogInformation("房间 {Code} 收到协同中止上报（reporter={ReporterUid}），广播终止：{Reason}",
            roomCode, reporterUid, reason);
        // evt-only 组广播（全员强制升级，无旧客户端订阅者，只发 evt 信封）
        await _broadcaster.BroadcastGroupEventOnlyAsync(
            roomCode,
            GatewayProtocol.Events.SyncCoordinatedAborted,
            new { reason, reporterUid },
            roomCode);
    }
}
