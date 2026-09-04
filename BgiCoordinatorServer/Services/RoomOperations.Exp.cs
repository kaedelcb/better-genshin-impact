using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 经验上限族（自 CoordinatorHub 逐字搬迁：ReportExpCapReached/ReportExpCapCleared/
/// ReportExpArmed/ReportTwoConsecutiveNoExp/ReportTwoConsecutiveNoExpCleared）。
/// 仅做 ctx 参数化与双发改造，业务逻辑不变。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>上报本机达经验上限，全员达上限时广播 AllReachedExpCap。multiplayer-hoeing-exp-cap-stop</summary>
    public async Task ReportExpCapReachedAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return;
        ObservePhase(room, roomCode, "exp.reportFightResult");

        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        var allReached = _roomManager.RecordExpCapReached(roomCode, ctx.ConnectionId);

        if (allReached)
        {
            _logger.LogInformation("房间 {Code} 全员达经验上限，广播终止", roomCode);
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllReachedExpCap", null);
        }
    }

    /// <summary>撤回本机达经验上限（又见经验）。multiplayer-hoeing-exp-cap-stop</summary>
    public Task ReportExpCapClearedAsync(GatewayHandlerContext ctx)
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return Task.CompletedTask;

        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        _roomManager.RecordExpCapCleared(roomCode, ctx.ConnectionId);
        return Task.CompletedTask;
    }

    /// <summary>成员上报团队 arming（本机吃到经验，或连续 5 场无经验兜底）。置 ExpCapArmed=true；
    /// 若 arming 后已满足全员上报（全员满级兜底场景）则补广播 AllReachedExpCap。multiplayer-hoeing-exp-cap-stop R7</summary>
    public async Task ReportExpArmedAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return;
        ObservePhase(room, roomCode, "exp.reportFightResult");

        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        var allReached = _roomManager.RecordExpArmed(roomCode, ctx.ConnectionId);

        if (allReached)
        {
            _logger.LogInformation("房间 {Code} 团队 arming 后全员达经验上限，广播终止", roomCode);
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllReachedExpCap", null);
        }
    }

    /// <summary>
    /// 上报"连续2场无经验预警"（exp-cap-prefinal-stop-by-two-noexp）。
    /// 客户端连续 2 场无经验时调用，服务端将 connectionId 加入 TwoConsecutiveNoExpSet。
    /// 若 arming ∧ 全员 ∈ (ExpCapReachedSet ∪ TwoConsecutiveNoExpSet) → 广播 AllReachedExpCap。
    /// 旧服务端无此方法 → 客户端 HubException 被静默吞掉 → 退化为 4-threshold 行为。
    /// </summary>
    public async Task ReportTwoConsecutiveNoExpAsync(GatewayHandlerContext ctx)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return;
        ObservePhase(room, roomCode, "exp.reportFightResult");

        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        var allReached = _roomManager.RecordTwoConsecutiveNoExp(roomCode, ctx.ConnectionId);

        if (allReached)
        {
            _logger.LogInformation("房间 {Code} 连续2场无经验预警触发全员覆盖，广播终止", roomCode);
            await _broadcaster.BroadcastGroupAsync(roomCode, "AllReachedExpCap", null);
        }
    }

    /// <summary>
    /// 撤回"连续2场无经验预警"（又见经验）。exp-cap-prefinal-stop-by-two-noexp。
    /// </summary>
    public Task ReportTwoConsecutiveNoExpClearedAsync(GatewayHandlerContext ctx)
    {
        var (_, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (roomCode == null) return Task.CompletedTask;

        _roomManager.UpdateHeartbeat(ctx.ConnectionId);
        _roomManager.RecordTwoConsecutiveNoExpCleared(roomCode, ctx.ConnectionId);
        return Task.CompletedTask;
    }
}
