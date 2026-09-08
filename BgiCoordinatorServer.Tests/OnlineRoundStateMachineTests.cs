using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 上线→就绪→开锄 状态机回归测试（B157 架构评审修复）：
/// 1) threshold 按在线人数封顶：默认 ExpectedHoeingPlayers=4 的 2 人房不再"永远凑不齐"；
/// 2) ConsumeOnlineReady 按本轮参与者 UID 集合消费：高 gen 成员不再残留武装事件（幻影轮次根因）；
/// 3) DisarmOnlineEvents：确认超时耗尽收尾解除武装且不记"已联机记录"；
/// 4) 单人直达 AllReady 定向只发就绪成员：不再全组广播把未上线成员拖进开锄；
/// 5) 多人确认轮端到端：两名成员 gen 不等（minGen=2），全员 ack 后两人都被消费（旧实现只消费 minGen 成员）。
/// </summary>
public class OnlineRoundStateMachineTests
{
    private static (string Room, string Pwd) NewRoom()
        => ("R" + Guid.NewGuid().ToString("N")[..8], "pw");

    private static string Conn(string tag) => $"conn-{tag}-{Guid.NewGuid():N}";

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    private static string Group(string room) => $"CTRL_{room}";

    private static async Task JoinAsync(GatewayTestHarness h, string room, string pwd, string conn, string uid, string name)
        => await h.Ops.JoinControlRoomAsync(Ctx(conn), room, pwd, uid, name);

    private static async Task ReportStatusAsync(GatewayTestHarness h, string room, string conn, int expectedPlayers)
        => await h.Ops.ReportControlStatusAsync(Ctx(conn), new ControlStatus
        {
            RoomCode = room,
            ExpectedHoeingPlayers = expectedPlayers,
        });

    private static ControlRoomPlayer Player(GatewayTestHarness h, string room, string uid)
        => h.RoomManager.GetControlRoomPlayers(Group(room)).Single(p => p.PlayerUid == uid);

    [Fact]
    public async Task ThresholdCap_TwoOnlineWithDefaultFour_CanTransition()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a");
        var connB = Conn("b");
        await JoinAsync(h, room, pwd, connA, "uidA", "成员A");
        await JoinAsync(h, room, pwd, connB, "uidB", "成员B");
        // 双方都用默认 ExpectedHoeingPlayers=4（不改配置的常见场景）
        await ReportStatusAsync(h, room, connA, 4);
        await ReportStatusAsync(h, room, connB, 4);

        h.RoomManager.ReportOnlineEvent(Group(room), connA, 1);
        Assert.False(h.RoomManager.CheckAndTransition(Group(room), out _), "仅 1 人就绪不应触发");

        h.RoomManager.ReportOnlineEvent(Group(room), connB, 1);
        Assert.True(h.RoomManager.CheckAndTransition(Group(room), out var gen),
            "2 人房默认配置 4：threshold 应按在线人数封顶为 2，两人就绪即可触发");
        Assert.Equal(1, gen);
    }

    [Fact]
    public async Task ConsumeOnlineReady_ParticipantSet_ConsumesAllArmedMembers()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a");
        var connB = Conn("b");
        await JoinAsync(h, room, pwd, connA, "uidA", "成员A");
        await JoinAsync(h, room, pwd, connB, "uidB", "成员B");

        // 各成员 generation 独立递增，几乎必然不等：A=5，B=2 → minGen=2
        h.RoomManager.ReportOnlineEvent(Group(room), connA, 5);
        h.RoomManager.ReportOnlineEvent(Group(room), connB, 2);
        Assert.True(h.RoomManager.CheckAndTransition(Group(room), out var gen));
        Assert.Equal(2, gen);

        h.RoomManager.ConsumeOnlineReady(Group(room), gen, new[] { "uidA", "uidB" });

        // 旧实现只消费 gen==minGen 的 B，A 的 gen5 残留武装 → 幻影轮次根因
        Assert.True(Player(h, room, "uidA").OnlineEventConsumed);
        Assert.True(Player(h, room, "uidB").OnlineEventConsumed);
        Assert.False(Player(h, room, "uidA").OnlineReady);
        Assert.Single(Player(h, room, "uidA").OnlineHistory);
        Assert.Single(Player(h, room, "uidB").OnlineHistory);
    }

    [Fact]
    public async Task DisarmOnlineEvents_ClearsArmed_WithoutHistory()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a");
        await JoinAsync(h, room, pwd, connA, "uidA", "成员A");

        h.RoomManager.ReportOnlineEvent(Group(room), connA, 3);
        Assert.False(Player(h, room, "uidA").OnlineEventConsumed);

        h.RoomManager.DisarmOnlineEvents(Group(room), new[] { "uidA" });

        var a = Player(h, room, "uidA");
        Assert.True(a.OnlineEventConsumed);
        Assert.False(a.OnlineReady);
        Assert.Empty(a.OnlineHistory); // 耗尽放弃不算"已联机"，不记记录
    }

    [Fact]
    public async Task TryStartRound_SingleReady_SendsAllReadyOnlyToReadyMember()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a");
        var connB = Conn("b");
        await JoinAsync(h, room, pwd, connA, "uidA", "成员A");
        await JoinAsync(h, room, pwd, connB, "uidB", "成员B");
        // A 配 1 人 → threshold=1，A 单人上报即直达；B 在线但未上线
        await ReportStatusAsync(h, room, connA, 1);
        await ReportStatusAsync(h, room, connB, 4);

        await h.Ops.ReportOnlineEventAsync(Ctx(connA), 7, true);

        // 定向发送：恰好 1 次 AllReady 到客户端连接；组广播通道不得出现 AllReady
        h.LegacyClientProxy.Verify(p => p.SendCoreAsync("AllReady",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Once);
        h.LegacyGroupProxy.Verify(p => p.SendCoreAsync("AllReady",
            It.IsAny<object?[]>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.True(Player(h, room, "uidA").OnlineEventConsumed);
        Assert.True(Player(h, room, "uidB").OnlineEventConsumed == false || Player(h, room, "uidB").OnlineEventGeneration == 0);
    }

    [Fact]
    public async Task ConfirmRound_TwoMembersDifferentGen_AllConfirmedConsumesBoth()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connA = Conn("a");
        var connB = Conn("b");
        await JoinAsync(h, room, pwd, connA, "uidA", "成员A");
        await JoinAsync(h, room, pwd, connB, "uidB", "成员B");
        await ReportStatusAsync(h, room, connA, 2);
        await ReportStatusAsync(h, room, connB, 2);

        // A gen5、B gen2 先后上报 → 凑齐 2 人 → 进入确认阶段（后台 StartConfirmAsync，500ms 轮询）
        await h.Ops.ReportOnlineEventAsync(Ctx(connA), 5, true);
        await h.Ops.ReportOnlineEventAsync(Ctx(connB), 2, true);
        Assert.True(h.RoomManager.IsStateConfirming(Group(room)));

        // 双方确认回执
        await h.Ops.ConfirmAllReadyAsync(Ctx(connA), 2);
        await h.Ops.ConfirmAllReadyAsync(Ctx(connB), 2);

        // 后台确认循环最长 500ms 检测到全员确认 → 消费 + 状态复位 idle
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            if (Player(h, room, "uidA").OnlineEventConsumed && Player(h, room, "uidB").OnlineEventConsumed)
                break;
            await Task.Delay(100);
        }

        // 旧实现只消费 minGen(=2) 的 B，A 的 gen5 残留武装
        Assert.True(Player(h, room, "uidA").OnlineEventConsumed);
        Assert.True(Player(h, room, "uidB").OnlineEventConsumed);
        Assert.False(h.RoomManager.IsStateConfirming(Group(room)));
    }
}
