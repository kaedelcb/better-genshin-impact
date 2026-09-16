using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 极限场景实证：A 战斗中死亡复苏并跳段、B 正常打完、C 刚开打、D 在战斗前的同步点等。
///
/// 位置关系（单条路线内递增）：D 等在战斗前的同步点 Pd=1_000_000；
/// 战斗点在其后；下一段起点/战后同步点 Pns=1_001_000。
///
/// 各端状态（与服务端实际已知信息一致）：
///   A：复苏后异常，承诺目标 = Pns（TargetProgress=Pns）；CurrentProgress 仍为 Pd（进度只在到达同步点时更新）；
///      已跳段、正赶往 Pns，不在任何等待集合里。
///   B：打完战斗，已到战后同步点 Pns 并在那里等（在 ArrivalSets 里）。
///   C：刚开打，最后一次上报仍是 Pd，不在任何等待集合里。
///   D：在战斗前同步点 Pd 等（在 ArrivalSets 里）。
///
/// 本文件用测试把"现在会如何处理"钉死，而不是靠推演。
/// </summary>
public class CollectiveSkipScenarioRevivedAndFightingTests
{
    private const long Pd = 1_000_000;    // 战斗前同步点（D 在等）
    private const long Pns = 1_001_000;   // 下一段起点 = 战后同步点（B 在等）
    private const long Target = 2_000_000; // 集体跳段目标 = 下一条路线起点

    private const string SyncPreFight = "routeA_tp_0_0";
    private const string SyncPostFight = "routeA_tp_0_1000";

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

    /// <param name="bWaitingAtPostFight">B 是否还在战后同步点等（true=两组等待者；false=只有 D 一个等待者）</param>
    private static (GatewayTestHarness Harness, string RoomCode, Room Room) Scenario(bool bWaitingAtPostFight)
    {
        var h = new GatewayTestHarness();
        var roomCode = h.RoomManager.CreateRoom("conn-A", "Host", null, "uid-A", 4);
        foreach (var n in new[] { "B", "C", "D" })
            h.RoomManager.AddPlayerForTesting(roomCode, Player($"conn-{n}", $"uid-{n}", Pd));

        var room = h.RoomManager.GetRoom(roomCode)!;
        room.HostConnectionId = "conn-A";
        room.HostConfig = new RoomConfig
        {
            EnableMutualWaitCollectiveSkip = true,
            MutualWaitMinWaitersRatio = 0.5,
            MutualWaitStableSeconds = 30,
            MaxConsecutiveCollectiveSkips = 3,
        };

        var a = room.Players.First(p => p.ConnectionId == "conn-A");
        var b = room.Players.First(p => p.ConnectionId == "conn-B");
        var c = room.Players.First(p => p.ConnectionId == "conn-C");
        var d = room.Players.First(p => p.ConnectionId == "conn-D");

        // A：复苏异常 + 承诺到 Pns；进度仍停在 Pd（跳段途中）
        a.IsAbnormal = true;
        a.TargetProgress = Pns;
        a.CurrentProgress = Pd;

        // B：打完并到战后同步点（进度已推进到 Pns）
        b.CurrentProgress = Pns;

        // C：刚开打，最后一次上报仍是 Pd
        c.CurrentProgress = Pd;

        // D：在战斗前同步点等
        d.CurrentProgress = Pd;

        room.ArrivalSets[SyncPreFight] = ["conn-D"];
        room.ArrivalSetProgress[SyncPreFight] = Pd;

        if (bWaitingAtPostFight)
        {
            room.ArrivalSets[SyncPostFight] = ["conn-B"];
            room.ArrivalSetProgress[SyncPostFight] = Pns;
        }

        room.LastArrivalSetsSnapshot = room.ArrivalSets.ToDictionary(
            kv => kv.Key, kv => new HashSet<string>(kv.Value));
        room.ObservationStartTime = DateTime.UtcNow.AddSeconds(-40);
        return (h, roomCode, room);
    }

    private static int CountLegacyGroupEvent(GatewayTestHarness h, string eventName)
        => h.LegacyGroupProxy.Invocations.Count(i =>
            i.Method.Name == nameof(IClientProxy.SendCoreAsync)
            && i.Arguments.Count >= 1
            && i.Arguments[0] as string == eventName);

    // =========================================================================
    // 情形 1：两组等待者（D 在战斗前点、B 在战后点）→ 卡死判定成立
    // =========================================================================

    [Fact]
    public async Task TwoWaitingGroups_CollectiveSkipFires_AndFlagsBothRevivedAAndFightingC()
    {
        var (h, roomCode, room) = Scenario(bWaitingAtPostFight: true);

        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);

        var active = room.ActiveCollectiveSkip;
        Assert.NotNull(active);
        Assert.Equal(Target, active!.TargetProgress);

        // ★ 关键实测：被判为"落后者"的是【不在任何等待集合里】的 A 和 C
        //   —— A 是刚复苏跳段的、C 是正在战斗的。两者都被标记、都被要求跳段。
        Assert.Contains("conn-A", active.RequiredConnectionIds);
        Assert.Contains("conn-C", active.RequiredConnectionIds);
        Assert.DoesNotContain("conn-B", active.RequiredConnectionIds);   // B 在等待集合里
        Assert.DoesNotContain("conn-D", active.RequiredConnectionIds);   // D 在等待集合里

        // A 的"承诺目标"被覆盖：从 Pns 抬到下一条路线起点
        var a = room.Players.First(p => p.ConnectionId == "conn-A");
        Assert.Equal(Target, a.TargetProgress);
        // C（战斗中）被标成异常
        var c = room.Players.First(p => p.ConnectionId == "conn-C");
        Assert.True(c.IsAbnormal);
        Assert.Equal(Target, c.TargetProgress);

        // 放行情况（实测）：
        //   · D 所在的战斗前点立刻放行（A 被豁免、B 进度已在前）
        //   · B 所在的战后点【仍然要等 D】（D 进度落后且在放行后继续前进）→ 所以只放行 1 个点
        Assert.Equal(1, CountLegacyGroupEvent(h, "AllArrived"));
        Assert.DoesNotContain(SyncPreFight, room.ArrivalSets.Keys);
        Assert.Contains(SyncPostFight, room.ArrivalSets.Keys);

        // 跳段命令已广播（A 与 C 都会收到并各自执行）
        Assert.Equal(1, CountLegacyGroupEvent(h, "RequestSkipToProgress"));
    }

    /// <summary>
    /// 同上场景，但补一步：D 走到战后点后 B 才被放行（证明"只放行 1 个点"不是死锁）。
    /// </summary>
    [Fact]
    public async Task AfterCollectiveSkip_BStillWaitsForD_AndIsReleasedWhenDArrives()
    {
        var (h, roomCode, room) = Scenario(bWaitingAtPostFight: true);
        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);
        Assert.Contains(SyncPostFight, room.ArrivalSets.Keys);

        // D（已被放行）走到战后同步点并上报到达
        await h.Ops.WaitForAllPlayersAsync(GatewayHandlerContext.Legacy("conn-D"), SyncPostFight, Pns);

        Assert.DoesNotContain(SyncPostFight, room.ArrivalSets.Keys);
    }

    // =========================================================================
    // 情形 2：只有 D 一个等待者（B 已继续前进）
    // =========================================================================

    [Fact]
    public async Task SingleWaitingGroup_BelowWaiterThreshold_NoCollectiveSkipAtAll()
    {
        var (h, roomCode, room) = Scenario(bWaitingAtPostFight: false);

        await h.Ops.EvaluateCollectiveStuckTimerCallbackAsync(room, roomCode);

        // 4 人房间阈值 = ⌈4×0.5⌉ = 2，只有 1 个等待者 → C1 不成立 → 集体跳段完全不介入
        Assert.Null(room.ActiveCollectiveSkip);
        Assert.Equal(0, CountLegacyGroupEvent(h, "RequestSkipToProgress"));
        Assert.Equal(0, CountLegacyGroupEvent(h, "AllArrived"));

        // D 保持等待；A（异常、目标 Pns）在 D 的点上被豁免，不拖住 D
        Assert.Contains(SyncPreFight, room.ArrivalSets.Keys);
    }

    // =========================================================================
    // 情形 3：A 的承诺点（Pns）在 D 的点被豁免 —— 异常玩家的"跳过即豁免"语义
    // =========================================================================

    [Fact]
    public async Task RevivedA_IsExemptAtThePointItSkipped_SoItDoesNotBlockOthers()
    {
        var (h, roomCode, room) = Scenario(bWaitingAtPostFight: false);
        var room2 = room;

        // 让 D 的点满足放行条件：除 D 外的正常玩家进度都推到 D 之后（B、C 已过该点）
        room2.Players.First(p => p.ConnectionId == "conn-B").CurrentProgress = Pns;
        room2.Players.First(p => p.ConnectionId == "conn-C").CurrentProgress = Pns;

        await h.Ops.WaitForAllPlayersAsync(GatewayHandlerContext.Legacy("conn-D"), SyncPreFight, Pd);

        // D 被放行：A 是异常玩家且目标(Pns)≠该点进度(Pd) → 豁免；B/C 进度在前 → 豁免
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync(
            "AllArrived", It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.AtLeastOnce);
        Assert.DoesNotContain(SyncPreFight, room2.ArrivalSets.Keys);
    }
}
