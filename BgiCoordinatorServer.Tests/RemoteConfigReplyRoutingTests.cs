using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 远程配置编辑回复命令（remote_config.data / push_result）监控模式回投回归测试。
/// 背景：监控（遥控）端连接只登记在 _remoteControlConnections、不入 _controlRooms 成员表，
/// SendRemoteCommand 的 ResolveTargets 匹配不到它 → 监控端发起远程编辑永远等不到回包
/// （回复被误判"目标离线"进缓存，切回执行模式冲刷缓存才可能姗姗来迟——实机"监控模式下
/// 发起修改成员配置完全没反应，切回执行模式立马弹窗"的根因）。
/// 锁定语义：
/// 1) 回复命令额外按 UID 投递给目标 UID 的遥控端连接（修复）；
/// 2) 同 UID 执行端在线时回复双投（双方都能收，非发起方按 CommandId 关联不上会忽略）；
/// 3) 执行类命令维持 FR-3：遥控端不接收，仍走离线缓存（保护既有行为）；
/// 4) 回复命令不进离线缓存（只对实时等待中的会话有意义，缓存冲刷出来的是迟到噪音）。
/// </summary>
public class RemoteConfigReplyRoutingTests
{
    private static (string Room, string Pwd) NewRoom()
        => ("C" + Guid.NewGuid().ToString("N")[..8], "pw");

    private static string Conn(string tag) => $"conn-{tag}-{Guid.NewGuid():N}";

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    private static RemoteCommand NewCmd(string room, string senderUid, string targetUid, string cmd) => new()
    {
        Cmd = cmd,
        RoomCode = room,
        Sender = "成员B",
        SenderUid = senderUid,
        Target = [targetUid],
        CommandId = Guid.NewGuid().ToString("N"),
        Params = new Dictionary<string, object> { ["ok"] = "true" }
    };

    [Fact]
    public async Task RemoteConfigReply_DeliveredToObserverConnection()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connMember = Conn("member");
        var connObs = Conn("obs");
        await h.Ops.JoinControlRoomAsync(Ctx(connMember), room, pwd, "uidB", "成员B");
        // 监控端：isRemote=true，不入 _controlRooms
        await h.Ops.JoinControlRoomAsync(Ctx(connObs), room, pwd, "uidObs", "观察者", isRemote: true);

        // 成员回包 remote_config.data → 目标 uidObs（监控端）
        await h.Ops.SendRemoteCommandAsync(Ctx(connMember), NewCmd(room, "uidB", "uidObs", "remote_config.data"));

        // 修复后：回复必须定向送达监控端连接
        h.LegacyHub.Verify(x => x.Clients.Client(connObs), Times.AtLeastOnce);
        // 回复命令不进离线缓存（发起方 20s 超时后冲刷出来的只是迟到噪音）
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("uidObs"));
    }

    [Fact]
    public async Task RemoteConfigReply_DualDelivery_WhenExecutorWithSameUidOnline()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connMember = Conn("member");
        var connExec = Conn("exec");
        var connObs = Conn("obs");
        await h.Ops.JoinControlRoomAsync(Ctx(connMember), room, pwd, "uidB", "成员B");
        // 同 UID 双端：执行端（入 _controlRooms）+ 监控端（遥控登记）
        await h.Ops.JoinControlRoomAsync(Ctx(connExec), room, pwd, "uidObs", "执行端");
        await h.Ops.JoinControlRoomAsync(Ctx(connObs), room, pwd, "uidObs", "观察者", isRemote: true);

        await h.Ops.SendRemoteCommandAsync(Ctx(connMember), NewCmd(room, "uidB", "uidObs", "remote_config.push_result"));

        // 执行端走 ResolveTargets 正常投递；监控端走新增回投——双投，非发起方忽略
        h.LegacyHub.Verify(x => x.Clients.Client(connExec), Times.AtLeastOnce);
        h.LegacyHub.Verify(x => x.Clients.Client(connObs), Times.AtLeastOnce);
        // 目标 UID 有在线执行端，本就不缓存
        Assert.Empty(h.RoomManager.GetAndClearPendingCommands("uidObs"));
    }

    [Fact]
    public async Task ExecutionCommand_NotDeliveredToObserver_StillCachedOffline()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connMember = Conn("member");
        var connObs = Conn("obs");
        await h.Ops.JoinControlRoomAsync(Ctx(connMember), room, pwd, "uidB", "成员B");
        await h.Ops.JoinControlRoomAsync(Ctx(connObs), room, pwd, "uidObs", "观察者", isRemote: true);

        // 执行类命令（FR-3）：遥控端不接收
        await h.Ops.SendRemoteCommandAsync(Ctx(connMember), NewCmd(room, "uidB", "uidObs", "start_bgi"));

        h.LegacyHub.Verify(x => x.Clients.Client(connObs), Times.Never);
        // 维持既有语义：目标离线进缓存，上线后冲刷下发
        var pending = h.RoomManager.GetAndClearPendingCommands("uidObs");
        Assert.Single(pending);
        Assert.Equal("start_bgi", pending[0].Cmd);
    }
}
