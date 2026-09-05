using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// collective-stuck-orphan-arrivalset fix 回归测试（2026-09-05 实机踩中）：
/// 晚到补发 / 孤立落后者放行后，ArrivalSets 残留"孤儿到达记录"（该同步点本轮不会再
/// 全量广播，残留永不消）。孤儿集合喂饱集体卡死判定：C1 计数虚高、C2 成员归约 sp 偏大
/// 导致永不满足、C3 残留永不变天然"稳定"→ 30s 后误判集体卡死，ConsecutiveCollectiveSkipCount
/// 无条件 +1，3 次后 CollectiveSkipDegraded 全员协调停止。
/// 修复：① ArrivalSetProgress 存真实全局进度，判定优先使用（孤儿集合可被
/// "CurrentProgress>sp 已穿过豁免"满足 → 自愈广播+清空）；② 补发/放行分支 RemoveArrival
/// 清掉孤儿记录。
/// </summary>
public class OrphanArrivalSetTests
{
    private static PlayerInfo OnlinePlayer(string connId, string uid, long currentProgress = -1) => new()
    {
        ConnectionId = connId,
        PlayerId = connId,
        PlayerName = uid,
        PlayerUid = uid,
        Status = PlayerStatus.Pathing,
        LastHeartbeat = DateTime.UtcNow,
        CurrentProgress = currentProgress,
    };

    private static GatewayTestHarness NewRoomWithPlayers(params string[] connIds)
    {
        var h = new GatewayTestHarness();
        var roomCode = h.RoomManager.CreateRoom(connIds[0], "Host", null, "uid-0", connIds.Length);
        foreach (var connId in connIds.Skip(1))
        {
            h.RoomManager.AddPlayerForTesting(roomCode, OnlinePlayer(connId, $"uid-{connId}"));
        }
        return h;
    }

    private string RoomCodeOf(GatewayTestHarness h, string connId)
        => h.RoomManager.GetRoomByConnectionId(connId).Item2!;

    [Fact]
    public async Task WaitForAllPlayers_LateRebroadcastAfterGroupRelease_LeavesNoOrphanArrival()
    {
        // Arrange：3 人房间，全员到达同一同步点 → 正常广播 + 清空
        var h = NewRoomWithPlayers("conn-1", "conn-2", "conn-3");
        var roomCode = RoomCodeOf(h, "conn-1");
        const string syncId = "routeA_tp_0_0";

        foreach (var connId in new[] { "conn-1", "conn-2", "conn-3" })
        {
            await h.Ops.WaitForAllPlayersAsync(GatewayHandlerContext.Legacy(connId), syncId, 1000000);
        }

        var room = h.RoomManager.GetRoom(roomCode)!;
        Assert.DoesNotContain(syncId, room.ArrivalSets.Keys);
        Assert.Contains(syncId, room.BroadcastedSyncIds);

        // Act：晚到者（已错过组广播）再调同一同步点 → 单独补发 AllArrived
        await h.Ops.WaitForAllPlayersAsync(GatewayHandlerContext.Legacy("conn-1"), syncId, 1000000);

        // Assert：补发后到达记录被 RemoveArrival 清掉，不留孤儿集合/进度快照
        Assert.DoesNotContain(syncId, room.ArrivalSets.Keys);
        Assert.DoesNotContain(syncId, room.ArrivalSetProgress.Keys);
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync(
            "AllArrived", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task WaitForAllPlayers_LaggingCallerRelease_LeavesNoOrphanArrival()
    {
        // Arrange：房主已走到 2000000，落后者还在 1000000 的旧同步点死等
        var h = NewRoomWithPlayers("conn-1", "conn-2");
        var roomCode = RoomCodeOf(h, "conn-1");
        var room = h.RoomManager.GetRoom(roomCode)!;
        room.Players.First(p => p.ConnectionId == "conn-1").CurrentProgress = 2000000;
        const string syncId = "oldSync";

        // Act：落后者调旧同步点 → 触发孤立落后者放行分支（单独补发）
        await h.Ops.WaitForAllPlayersAsync(GatewayHandlerContext.Legacy("conn-2"), syncId, 1000000);

        // Assert：放行后其到达记录被清掉——该集合除 caller 外无人会再到，残留即孤儿
        Assert.DoesNotContain(syncId, room.ArrivalSets.Keys);
        Assert.DoesNotContain(syncId, room.ArrivalSetProgress.Keys);
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync(
            "AllArrived", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task CollectSatisfiedSyncs_OrphanSetWithStoredProgress_SelfHealsViaExemption()
    {
        // Arrange：全员已走过孤儿同步点（Current=2000000 > 该点真实进度 1000000），
        // 孤儿集合挂着 conn-2 的到达记录，ArrivalSetProgress 存了真实进度
        var h = NewRoomWithPlayers("conn-1", "conn-2", "conn-3");
        var roomCode = RoomCodeOf(h, "conn-1");
        var room = h.RoomManager.GetRoom(roomCode)!;
        foreach (var p in room.Players) p.CurrentProgress = 2000000;
        const string orphanSync = "orphanSync";
        h.RoomManager.RecordArrival(roomCode, orphanSync, "conn-2", 0);
        room.ArrivalSetProgress[orphanSync] = 1000000;

        // Act：任意玩家到达新同步点触发全量重评估
        await h.Ops.WaitForAllPlayersAsync(GatewayHandlerContext.Legacy("conn-1"), "newSync", 2000000);

        // Assert：孤儿集合被"CurrentProgress>sp 已穿过豁免"满足 → 广播 AllArrived 并清空（自愈）
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync(
            "AllArrived", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
        Assert.DoesNotContain(orphanSync, room.ArrivalSets.Keys);
        Assert.DoesNotContain(orphanSync, room.ArrivalSetProgress.Keys);
    }

    [Fact]
    public async Task CollectSatisfiedSyncs_OrphanSetWithoutStoredProgress_StaysUnsatisfied()
    {
        // 对照组：没有存储进度时回退成员归约 max（=2000000），
        // Current=2000000 不严格大于 sp → 无人被豁免 → 孤儿集合不满足、不广播。
        // 证明刀 1 的存储进度确实改变了判定结果。
        var h = NewRoomWithPlayers("conn-1", "conn-2", "conn-3");
        var roomCode = RoomCodeOf(h, "conn-1");
        var room = h.RoomManager.GetRoom(roomCode)!;
        foreach (var p in room.Players) p.CurrentProgress = 2000000;
        const string orphanSync = "orphanSync";
        h.RoomManager.RecordArrival(roomCode, orphanSync, "conn-2", 0);

        await h.Ops.WaitForAllPlayersAsync(GatewayHandlerContext.Legacy("conn-1"), "newSync", 2000000);

        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync(
            "AllArrived", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Contains(orphanSync, room.ArrivalSets.Keys);
    }
}
