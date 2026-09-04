using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 万叶族（自 CoordinatorHub 逐字搬迁：SetKazuhaPlayer（已废弃空实现）/
/// DeclareKazuhaCapability/NotifyKazuhaCollectStarted）。
/// 仅做 ctx 参数化与双发改造，业务逻辑不变。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>
    /// （已废弃，保留空实现）旧客户端调用此方法时仅记 deprecated 警告，不影响协议兼容。
    /// kazuha-player-auto-detection: 替换为运行时声明协议 DeclareKazuhaCapability，由各客户端各自识别本地联机队伍是否含万叶并主动声明。
    /// </summary>
    public Task SetKazuhaPlayerAsync(GatewayHandlerContext ctx, int index = 0)
    {
        _logger.LogWarning("[SetKazuhaPlayer] 调用方使用了已废弃的 Hub 方法（kazuha-player-auto-detection 已替换为 DeclareKazuhaCapability），index={Index}", index);
        return Task.CompletedTask;
    }

    /// <summary>
    /// 客户端声明本地联机队伍含万叶（kazuha-player-auto-detection）。
    /// 幂等：同一 ConnectionId 重复调用直接 return（lock 内做 Any 检查）。
    /// 选举：第一个声明者自动成为 KazuhaConnectionId，触发 KazuhaPlayerUpdated(playerUid) 广播。
    /// 后续声明者仅入候选列表，断线时按列表顺序顶替。
    /// </summary>
    public async Task DeclareKazuhaCapabilityAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return;
        ObservePhase(room, roomCode, "kazuha.declareCapability");

        bool shouldBroadcast = false;
        string broadcastUid = "";
        lock (room)
        {
            var player = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId);
            if (player == null) return;

            // 幂等检查：同一 ConnectionId 重复声明直接 return
            if (room.KazuhaCandidates.Any(c => c.ConnectionId == ctx.ConnectionId))
            {
                _logger.LogDebug("[DeclareKazuhaCapability] 重复声明，忽略 connId={ConnId}", ctx.ConnectionId);
                return;
            }

            room.KazuhaCandidates.Add(new KazuhaCandidate
            {
                ConnectionId = ctx.ConnectionId,
                PlayerUid = player.PlayerUid
            });

            // 第一个声明者自动成为当前 Kazuha
            if (room.KazuhaCollect.KazuhaConnectionId == null)
            {
                room.KazuhaCollect.KazuhaConnectionId = ctx.ConnectionId;
                broadcastUid = player.PlayerUid;
                shouldBroadcast = true;
            }
        }

        if (shouldBroadcast)
        {
            _logger.LogInformation("[DeclareKazuhaCapability] 房间 {Code} 选出第一位 Kazuha: {Uid}",
                roomCode, broadcastUid);
            await _broadcaster.BroadcastGroupAsync(roomCode, "KazuhaPlayerUpdated", new { playerUid = broadcastUid }, broadcastUid);
        }
    }

    // ====== 万叶聚物同步（multiplayer-kazuha-collect-sync）======

    /// <summary>
    /// 万叶玩家广播"开始执行聚物动作"。仅记录 + 广播，不做终态守卫。
    /// multiplayer-kazuha-collect-point-broadcast: 增加 syncKey + 聚物点 (collectX, collectY) 三参。
    /// hoeing-kazuha-collect-drop-terminal-signal: 不再写 room.KazuhaCollect.CurrentCollectPoint（字段已删）；
    /// 广播始终携带 4 参，无效坐标用 NaN 透传，由客户端 IsValid 守卫过滤。
    /// 注意：SignalR 不支持 hub 方法重载，老客户端调 0-参 InvokeAsync 会因 routing 失败
    /// 抛 HubException → 客户端 try/catch 静默 → 走退化路径（不上报聚物点）。
    /// 部署顺序：先服务端、后客户端，最大化平滑过渡。
    /// </summary>
    public async Task NotifyKazuhaCollectStartedAsync(GatewayHandlerContext ctx, string syncKey, double collectX, double collectY)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return;

        // 鉴权：必须是当前周期的万叶玩家
        lock (room)
        {
            if (room.KazuhaCollect.KazuhaConnectionId != ctx.ConnectionId)
            {
                _logger.LogWarning("[KazuhaCollect] NotifyKazuhaCollectStarted 鉴权失败：调用方 {ConnId} 不是万叶 {KazuhaId}",
                    ctx.ConnectionId, room.KazuhaCollect.KazuhaConnectionId);
                return;
            }
        }

        // IsValid: NaN / Inf / (0, 0) 全部判无效（与 KazuhaCollectPointDecisions.IsValid 同语义）
        bool collectPointValid = !double.IsNaN(collectX) && !double.IsNaN(collectY)
                              && !double.IsInfinity(collectX) && !double.IsInfinity(collectY)
                              && !(collectX == 0.0 && collectY == 0.0);

        var playerUid = "";
        lock (room)
        {
            playerUid = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId)?.PlayerUid ?? "";
        }

        // hoeing-kazuha-collect-drop-terminal-signal: 删 CurrentSyncKey fallback 与 CurrentCollectPoint 写入
        // （这两个字段随终态状态机一并删除）。syncKey 由万叶客户端直接传入，恒非空（design.md Property 2 守住）。
        _logger.LogInformation(
            "[KazuhaCollect] 房间 {Code} 万叶 {Uid} 开始聚物 syncKey={Key} collectPoint=({X},{Y}) valid={Valid}",
            roomCode, playerUid, syncKey, collectX, collectY, collectPointValid);

        // 始终广播 4-参（无效坐标用 NaN 透传给客户端，客户端 IsValid 守卫会过滤）
        await _broadcaster.BroadcastGroupAsync(roomCode, "KazuhaCollectStarted",
            new { playerUid, syncKey, collectX, collectY },
            playerUid, syncKey ?? "", collectX, collectY);
    }
}
