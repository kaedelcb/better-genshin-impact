using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 房间生命周期族（自 CoordinatorHub 逐字搬迁：CreateRoom/JoinRoom/LeaveRoom/CloseRoom/
/// MarkRoomStarted(+WithProgress)）。仅做 ctx 参数化与双发改造，业务逻辑不变。
/// 新增：§4.7 房间协议锁定（按建房者客户端协议锁定，join 校验同协议）。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>创建房间，返回房间码与房间协议（§4.7 锁定）。</summary>
    public async Task<(string Code, string Protocol)> CreateRoomAsync(GatewayHandlerContext ctx,
        string playerName = "", List<string>? whitelist = null, string playerUid = "", int expectedPlayerCount = 4, string reportedVersion = "")
    {
        _logger.LogInformation("CreateRoom 收到参数: playerName={Name}, playerUid={Uid}, expectedPlayerCount={Count}, whitelist={WL}",
            playerName, playerUid, expectedPlayerCount, whitelist != null ? string.Join(",", whitelist) : "null");
        // 多世界轮次切换：先离开所有旧 Group，避免旧房间广播串扰
        await LeaveAllGroupsAsync(ctx);
        // version-compatibility-check 改动 5：透传房主上报版本作为房间基准版本
        var code = _roomManager.CreateRoom(ctx.ConnectionId, playerName, whitelist, playerUid, expectedPlayerCount, reportedVersion);
        await _broadcaster.AddToGroupAsync(ctx, code);
        TrackGroup(ctx, code);
        _logger.LogInformation("连接 {ConnId}({Name}) 创建房间 {Code}", ctx.ConnectionId, playerName, code);

        var room = _roomManager.GetRoom(code)!;
        // §4.7 房间协议锁定：按建房者客户端协议锁定，同一房间不允许新旧协议混用
        if (ctx.IsV3)
        {
            room.Protocol = GatewayProtocol.RoomProtocols.V3;
            _logger.LogInformation("[Gateway] 房间 {Code} 协议锁定为 v3（房主为网关客户端）", code);
        }
        ObservePhase(room, code, "room.create");
        await _broadcaster.BroadcastGroupAsync(code, "PlayerListUpdated", new { players = room.Players }, room.Players);
        return (code, room.Protocol);
    }

    /// <summary>加入房间，广播 PlayerListUpdated。返回 (是否成功, 房间协议, 失败原因)。</summary>
    public async Task<(bool Success, string? Protocol, string? Error)> JoinRoomAsync(GatewayHandlerContext ctx,
        string roomCode, string playerName = "", string playerUid = "", string reportedVersion = "")
    {
        var playerId = ctx.ConnectionId;

        var room0 = _roomManager.GetRoom(roomCode);
        if (room0 != null)
        {
            // §4.7 协议锁定校验：同一房间不允许新旧协议混用
            if (ctx.IsV3 && room0.Protocol != GatewayProtocol.RoomProtocols.V3)
            {
                _logger.LogWarning("[Gateway] 连接 {ConnId} 以 v3 协议加入 legacy 房间 {Code}，拒绝（协议锁定）",
                    ctx.ConnectionId, roomCode);
                return (false, room0.Protocol, GatewayProtocol.ErrorCodes.RoomProtocolMismatch);
            }
            if (!ctx.IsV3 && room0.Protocol == GatewayProtocol.RoomProtocols.V3)
            {
                _logger.LogWarning("[Gateway] 连接 {ConnId} 以旧协议加入 v3 房间 {Code}，拒绝（协议锁定）",
                    ctx.ConnectionId, roomCode);
                return (false, room0.Protocol, GatewayProtocol.ErrorCodes.RoomProtocolMismatch);
            }

            // === 版本一致性校验（就地，入房之前）version-compatibility-check R1.1/R6.1 改动 7/14 ===
            // 基准 = 房间内第一个非通配玩家版本（ResolveBaselineVersion），而非固定取房主版本：
            // 否则开发者通配版本当房主时，房主通配 → 全员放行，校验失效（Property 7）。
            List<string> existingVersions;
            lock (room0)
            {
                existingVersions = room0.Players.Select(p => p.ReportedVersion).ToList();
            }
            if (!VersionCompatibilityDecisions.CanJoin(reportedVersion, existingVersions))
            {
                var baseline = VersionCompatibilityDecisions.ResolveBaselineVersion(existingVersions) ?? "";
                var checkResult = BuildVersionCheckResult(reportedVersion, baseline);
                _logger.LogWarning("连接 {ConnId} 版本校验不兼容，阻断加入房间 {Code}：member={Member} baseline={Baseline}",
                    ctx.ConnectionId, roomCode, reportedVersion, baseline);
                // 向该加入者单独回传 Check_Result（向后兼容：旧客户端不订阅此事件即忽略，不影响 bool 返回语义 U4.1）
                await _broadcaster.SendToCallerAsync(ctx, "VersionCheckRejected", checkResult, checkResult);
                return (false, room0.Protocol, "version_incompatible"); // 硬阻断（R5.1），不调用 RoomManager.JoinRoom，成员不入房
            }
        }

        var (success, error) = _roomManager.JoinRoom(roomCode, ctx.ConnectionId, playerId, playerName, playerUid, reportedVersion);

        if (!success)
        {
            _logger.LogWarning("连接 {ConnId} 加入房间 {Code} 失败：{Error}",
                ctx.ConnectionId, roomCode, error);
            return (false, room0?.Protocol, error);
        }

        // 多世界轮次切换：先离开所有旧 Group，避免旧房间广播串扰
        await LeaveAllGroupsAsync(ctx, excludeGroup: roomCode);
        await _broadcaster.AddToGroupAsync(ctx, roomCode);
        TrackGroup(ctx, roomCode);
        _logger.LogInformation("连接 {ConnId} 加入房间 {Code}", ctx.ConnectionId, roomCode);

        var room = _roomManager.GetRoom(roomCode)!;
        ObservePhase(room, roomCode, "room.join");
        await _broadcaster.BroadcastGroupAsync(roomCode, "PlayerListUpdated", new { players = room.Players }, room.Players);
        return (true, room.Protocol, null);
    }

    /// <summary>
    /// 构造版本校验失败的 Check_Result（version-compatibility-check 改动 7 / R5.2–R5.6）。
    /// 含双方版本号、双方是否通配标记、统一版本引导文案。
    /// </summary>
    private static VersionCheckResult BuildVersionCheckResult(string memberVersion, string baselineVersion)
    {
        return new VersionCheckResult
        {
            Compatible = false,
            MemberVersion = memberVersion ?? "",
            BaselineVersion = baselineVersion ?? "",
            MemberIsWildcard = VersionCompatibilityDecisions.IsWildcard(memberVersion),
            BaselineIsWildcard = VersionCompatibilityDecisions.IsWildcard(baselineVersion),
            // R5.6 引导：请将房内所有玩家更新到完全相同的版本后重试
            Hint = "版本不一致，已阻止加入。请将房内所有玩家更新到完全相同的版本后重试。"
        };
    }

    /// <summary>离开房间，广播 PlayerListUpdated</summary>
    public async Task LeaveRoomAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        var affectedCodes = _roomManager.LeaveRoom(ctx.ConnectionId);

        foreach (var code in affectedCodes)
        {
            await _broadcaster.RemoveFromGroupAsync(ctx, code);
            UntrackGroup(ctx, code);
            var updatedRoom = _roomManager.GetRoom(code);
            var players = updatedRoom?.Players ?? [];
            if (updatedRoom != null)
            {
                ObservePhase(updatedRoom, code, "room.leave");
            }
            await _broadcaster.BroadcastGroupAsync(code, "PlayerListUpdated", new { players }, players);
        }

        _logger.LogInformation("连接 {ConnId} 离开房间", ctx.ConnectionId);
    }

    /// <summary>关闭房间（仅房主可操作）</summary>
    public async Task CloseRoomAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[CloseRoom] 连接 {ConnId} 未在任何房间中", ctx.ConnectionId);
            return;
        }

        if (room.HostConnectionId != ctx.ConnectionId)
        {
            _logger.LogWarning("[CloseRoom] 连接 {ConnId} 不是房主，无法关闭房间 {Code}", ctx.ConnectionId, roomCode);
            return;
        }

        _logger.LogInformation("[CloseRoom] 房主 {ConnId} 关闭房间 {Code}", ctx.ConnectionId, roomCode);
        _phaseObserver.Mark(roomCode, RoomPhase.Ended, "room.close");
        _phaseObserver.Forget(roomCode);
        await _broadcaster.BroadcastGroupAsync(roomCode, "RoomClosed", new { reason = "房主已关闭房间" }, "房主已关闭房间");
        // 删除整个房间，防止玩家重连后重新加入已关闭的房间
        _roomManager.DeleteRoom(roomCode);
        await _broadcaster.RemoveFromGroupAsync(ctx, roomCode);
        UntrackGroup(ctx, roomCode);
    }

    /// <summary>
    /// 房主调用此方法把房间标记为已开锄（spec lock-room-after-start §2）。
    /// 服务端从此 JoinRoom 拒绝非重连新玩家、GetOnlineRooms 也不再返回此房间。
    /// 鉴权：ctx.ConnectionId 必须等于 room.HostConnectionId。
    /// 幂等：重复调用直接 return（room.IsStarted 一旦 true 在房间销毁前不复位）。
    /// 非房主调用：LogWarning + return，不抛异常、不修改状态。
    /// hoeing-multiworld-host-restart-resume-round：completedHostUids 非空时排除已完成房主世界（裁剪）。
    /// </summary>
    public Task MarkRoomStartedAsync(GatewayHandlerContext ctx, List<string>? completedHostUids)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null)
        {
            _logger.LogWarning("[MarkRoomStarted] 连接 {ConnId} 未在任何房间中，忽略", ctx.ConnectionId);
            return Task.CompletedTask;
        }
        if (room.HostConnectionId != ctx.ConnectionId)
        {
            _logger.LogWarning("[MarkRoomStarted] 连接 {ConnId} 不是房主，忽略（房间 {Code}）",
                ctx.ConnectionId, roomCode);
            return Task.CompletedTask;
        }
        if (room.IsStarted)
        {
            _logger.LogDebug("[MarkRoomStarted] 房间 {Code} 已经 IsStarted=true，幂等返回", roomCode);
            return Task.CompletedTask;
        }
        room.IsStarted = true;
        _logger.LogInformation("[MarkRoomStarted] 房间 {Code} 已锁定，IsStarted=true", roomCode);
        ObservePhase(room, roomCode, "room.markStarted");

        // multiplayer-server-authoritative-round-order：首轮锁房时全员已在房间
        // （客户端 MarkRoomStarted 在 AllWorldJoined 之后），此刻 Players 是全集，
        // 生成权威轮换序列（首项=首任房主，其余 UID 升序）。整场只生成一次（幂等）。
        // hoeing-multiworld-host-restart-resume-round：completedHostUids 非空时排除已完成房主世界（裁剪）。
        lock (room)
        {
            if (room.RoundHostOrder.Count == 0)
            {
                var hostUid = room.Players.Count > 0 ? room.Players[0].PlayerUid : "";
                IReadOnlySet<string>? exclude =
                    (completedHostUids != null && completedHostUids.Count > 0)
                        ? new HashSet<string>(completedHostUids)
                        : null;
                room.RoundHostOrder = RoundHostOrderDecisions.Build(room.Players, hostUid, exclude);
                _logger.LogInformation("[RoundOrder] 房间 {Code} 生成权威轮换序列（排除 {N} 已完成）：{Order}",
                    roomCode, exclude?.Count ?? 0, string.Join(" -> ", room.RoundHostOrder));
            }
        }
        return Task.CompletedTask;
    }
}
