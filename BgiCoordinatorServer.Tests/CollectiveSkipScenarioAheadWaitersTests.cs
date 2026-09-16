using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 用户场景实证：3 人跑在 A 前面、在后一个同步点等；A 落后、在前面一个同步点。
/// 目标：用测试把"到底会不会协调跳段"钉死，而不是靠推演。
///
/// 两个阶段的实测：
///   ① A 刚进前面那个同步点（到达记录还在）→ 服务端立刻放行 A 并**删除其到达记录**，
///      且广播里**没有** RequestSkipToProgress（这一轮不跳段）。
///   ② A 被放行后不再挂任何同步点、进度仍停在原处，到达集合稳定 30s（默认 MutualWaitStableSeconds）
///      → 三个条件全中 → **会**给 A 广播 RequestSkipToProgress，同时把手后点的 3 人放行。
/// </summary>
public class CollectiveSkipScenarioAheadWaitersTests
{
    private const long P1 = 1_000_000;      // A 所在的前一个同步点（路线1 段0 路点0）
    private const long P2 = 1_000_500;      // 3 人所在的后一个同步点（路线1 段0 路点500）
    private const long Target = 2_000_000;  // 下一条路线起点 = (maxCurrent/1e6 + 1)*1e6

    private const string SyncP1 = "routeA_tp_0_0";
    private const string SyncP2 = "routeA_tp_0_500";

    private static PlayerInfo Player(string connId, string uid, long progress) => new()
    {
        ConnectionId = connId,
        PlayerId = connId,
        PlayerName = uid,
        PlayerUid = uid,
        Status = PlayerStatus.Pathing,
        LastHeartbeat = DateTime.UtcNow,
        CurrentProgress = progress,
    };

    /// <summary>4 人房间：conn-1 = A（落后、异常、目标承诺到 P2），conn-2/3/4 在 P2 等。</summary>
    private static (GatewayTestHarness Harness, string RoomCode, Room Room) Scenario(bool aAlreadyReleased)
    {
        var h = new GatewayTestHarness();
        var roomCode = h.RoomManager.CreateRoom("conn-1", "Host", null, "uid-1", 4);
        foreach (var i in new[] { 2, 3, 4 })
            h.RoomManager.AddPlayerForTesting(roomCode, Player($"conn-{i}", $"uid-{i}", P2));

        var room = h.RoomManager.GetRoom(roomCode)!;
        room.HostConnectionId = "conn-1";
        room.HostConfig = new RoomConfig
        {
            EnableMutualWaitCollectiveSkip = true,
            MutualWaitMinWaitersRatio = 0.5,
            MutualWaitStableSeconds = 30,
            MaxConsecutiveCollectiveSkips = 3,
        };

        // A：落后 + 异常（承诺会到 P2，所以 P2 必须等它）
        var a = room.Players.First(p => p.ConnectionId == "conn-1");
        a.CurrentProgress = P1;
        a.IsAbnormal = true;
        a.TargetProgress = P2;

        // 3 人在 P2 等待
        room.ArrivalSets[SyncP2] = ["conn-2", "conn-3", "conn-4"];
        room.ArrivalSetProgress[SyncP2] = P2;

        if (aAlreadyReleased)
        {
            // 阶段②起点：A 已从 P1 被放行（到达记录已删），不在任何集合里；
            // 到达集合已稳定 40s（> 默认 30s）
            room.LastArrivalSetsSnapshot = room.ArrivalSets.ToDictionary(kv => kv.Key, kv => new HashSet<string>(kv.Value));
            room.ObservationStartTime = DateTime.UtcNow.AddSeconds(-40);
        }

        return (h, roomCode, room);
    }

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    private static int CountLegacyGroupEvent(GatewayTestHarness h, string eventName)
        => h.LegacyGroupProxy.Invocations.Count(i =>
            i.Method.Name == nameof(IClientProxy.SendCoreAsync)
            && i.Arguments.Count >= 1
            && i.Arguments[0] as string == eventName);

    // =========================================================================
    // 阶段①：A 刚进前面那个同步点（到达记录还在）
    // =========================================================================

    [Fact]
    public async Task Phase1_AArrivesAtEarlierSyncPoint_AIsReleasedImmediately_AndNoSkipIsIssued()
    {
        var (h, roomCode, room) = Scenario(aAlreadyReleased: false);

        // A 上报到达 P1（带真实进度）
        await h.Ops.WaitForAllPlayersAsync(Ctx("conn-1"), SyncP1, P1);

        // ①  A 被立刻放行（孤立落后者放行分支：3 人进度都在它前面 → 定向补发 AllArrived）
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync(
            "AllArrived", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);

        // ②  A 的到达记录被删除 → 它不再"挂在"任何同步点上（这正是阶段②的前提）
        Assert.DoesNotContain(SyncP1, room.ArrivalSets.Keys);
        Assert.DoesNotContain(SyncP1, room.ArrivalSetProgress.Keys);

        // ③  这一轮没有任何跳段命令
        Assert.Equal(0, CountLegacyGroupEvent(h, "RequestSkipToProgress"));
        Assert.Null(room.ActiveCollectiveSkip);
    }

    // =========================================================================
    // 阶段②：A 已不在任何同步点上挂着 + 到达集合稳定 30s
    // =========================================================================

    [Fact]
    public async Task Phase2_ADetachedAndStable30s_CollectiveSkipIsIssuedToA_AndTheThreeAreReleased()
    {
        var (h, roomCode, room) = Scenario(aAlreadyReleased: true);

        // 30s 稳定计时到点 → Timer 回调
        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);

        // ①  判定为落后者的是 A（只有它"不在任何集合 + 进度落后"）
        var active = room.ActiveCollectiveSkip;
        Assert.NotNull(active);
        Assert.Equal(["conn-1"], active!.RequiredConnectionIds);
        Assert.Equal(Target, active.TargetProgress);

        // ②  给 A 广播了跳段命令（带 skipId）
        Assert.Equal(1, CountLegacyGroupEvent(h, "RequestSkipToProgress"));

        // ③  3 人所在的后一个同步点被放行（A 被标记异常、目标抬到远处 → 该点不再等 A）
        Assert.Equal(1, CountLegacyGroupEvent(h, "AllArrived"));
        Assert.DoesNotContain(SyncP2, room.ArrivalSets.Keys);

        // ④  A 的异常目标被抬到下一条路线起点
        var a = room.Players.First(p => p.ConnectionId == "conn-1");
        Assert.True(a.IsAbnormal);
        Assert.Equal(Target, a.TargetProgress);
    }

    // =========================================================================
    // 对照组：A 只是走得慢也一样命中（服务端不检测"是否真的不动"）
    // =========================================================================

    [Fact]
    public async Task Phase2_SlowButHealthyA_AlsoTriggers_ServerHasNoMovementDetection()
    {
        var (h, roomCode, room) = Scenario(aAlreadyReleased: true);
        // A 完全健康（不异常）、只是落后且正在走路：不在任何集合里、进度还没前进
        var a = room.Players.First(p => p.ConnectionId == "conn-1");
        a.IsAbnormal = false;
        a.TargetProgress = -1;

        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);

        // 仍然会触发：判据只有"不在集合 + 进度落后 + 集合 30s 不变"，与 A 是否真的卡住无关
        Assert.NotNull(room.ActiveCollectiveSkip);
        Assert.Equal(["conn-1"], room.ActiveCollectiveSkip!.RequiredConnectionIds);
        Assert.Equal(1, CountLegacyGroupEvent(h, "RequestSkipToProgress"));
    }
}
