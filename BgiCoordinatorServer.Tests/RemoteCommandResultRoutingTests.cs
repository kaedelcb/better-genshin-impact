using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 远端任务下发结果回执（control.reportCommandResult → control.remoteCommandResult 定向回投）测试。
/// 锁定语义：
/// 1) 回执按 SenderUid 回投到发起方的执行端成员连接（GetConnectionIdByUid 路由）；
/// 2) 发起方是遥控/监控端（不入 _controlRooms）时回投到其登记连接（GetRemoteConnectionIdsByUids 路由）；
/// 3) 发起方不在线/不存在时不投递、不抛异常，返回 delivered=0（尽力而为通道，目标不存在安全降级）；
/// 4) 上报方不在控制房间（且非登记遥控连接）时拒绝转发；
/// 5) 投递走 evt-only 定向通道（GatewayHub Client），不触碰旧协议 /hub 路径（LegacyHub 零调用）。
/// </summary>
public class RemoteCommandResultRoutingTests
{
    private static (string Room, string Pwd) NewRoom()
        => ("C" + Guid.NewGuid().ToString("N")[..8], "pw");

    private static string Conn(string tag) => $"conn-{tag}-{Guid.NewGuid():N}";

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    private static RemoteCommandResult NewResult(string room, string senderUid, string targetUid) => new()
    {
        RoomCode = room,
        CommandId = Guid.NewGuid().ToString("N"),
        Cmd = "start_group",
        SenderUid = senderUid,
        TargetUid = targetUid,
        TargetName = "执行机",
        Status = "success",
        Message = "配置组 锄地 已启动",
        TaskName = "锄地"
    };

    [Fact]
    public async Task CommandResult_RoutedToSenderMemberConnection()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connSender = Conn("sender");
        var connTarget = Conn("target");
        await h.Ops.JoinControlRoomAsync(Ctx(connSender), room, pwd, "uidA", "发起方");
        await h.Ops.JoinControlRoomAsync(Ctx(connTarget), room, pwd, "uidB", "执行机");

        var delivered = await h.Ops.ReportCommandResultAsync(Ctx(connTarget), NewResult(room, "uidA", "uidB"));

        Assert.Equal(1, delivered);
        // evt-only 定向通道：发到 GatewayHub 的发起方连接
        h.GatewayHub.Verify(x => x.Clients.Client(connSender), Times.AtLeastOnce);
        // 不投递给无关连接（执行机自己、其他成员）
        h.GatewayHub.Verify(x => x.Clients.Client(connTarget), Times.Never);
    }

    [Fact]
    public async Task CommandResult_RoutedToObserverSenderConnection()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connObs = Conn("obs");
        var connTarget = Conn("target");
        // 监控端发起方：isRemote=true，不入 _controlRooms（监控模式发起下发是实机主场景）
        await h.Ops.JoinControlRoomAsync(Ctx(connObs), room, pwd, "uidA", "观察者", isRemote: true);
        await h.Ops.JoinControlRoomAsync(Ctx(connTarget), room, pwd, "uidB", "执行机");

        var delivered = await h.Ops.ReportCommandResultAsync(Ctx(connTarget), NewResult(room, "uidA", "uidB"));

        Assert.Equal(1, delivered);
        h.GatewayHub.Verify(x => x.Clients.Client(connObs), Times.AtLeastOnce);
    }

    [Fact]
    public async Task CommandResult_SenderOffline_DroppedGracefully()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connTarget = Conn("target");
        await h.Ops.JoinControlRoomAsync(Ctx(connTarget), room, pwd, "uidB", "执行机");

        // 发起方 uidGhost 从未入房（已离场/掉线）：不投递、不抛异常
        var delivered = await h.Ops.ReportCommandResultAsync(Ctx(connTarget), NewResult(room, "uidGhost", "uidB"));

        Assert.Equal(0, delivered);
        h.GatewayHub.Verify(x => x.Clients.Client(It.IsAny<string>()), Times.Never);
        h.LegacyHub.Verify(x => x.Clients.Client(It.IsAny<string>()), Times.Never);
    }

    [Fact]
    public async Task CommandResult_ReporterNotInRoom_Rejected()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connSender = Conn("sender");
        var connOutsider = Conn("outsider");
        await h.Ops.JoinControlRoomAsync(Ctx(connSender), room, pwd, "uidA", "发起方");
        // outsider 未入房（伪造回执）：拒绝转发

        var delivered = await h.Ops.ReportCommandResultAsync(Ctx(connOutsider), NewResult(room, "uidA", "uidX"));

        Assert.Equal(0, delivered);
        h.GatewayHub.Verify(x => x.Clients.Client(It.IsAny<string>()), Times.Never);
    }

    /// <summary>端到端（网关信封 → 路由表 → 回投）：v3 信封 control.reportCommandResult 被正确路由，
    /// 响应 ack 带 delivered；payload.result 缺失时 bad_request。</summary>
    [Fact]
    public async Task Dispatch_ReportCommandResult_RoutesAndAcks()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var connSender = Conn("sender");
        var connTarget = Conn("target");
        await h.Ops.JoinControlRoomAsync(Ctx(connSender), room, pwd, "uidA", "发起方");
        await h.Ops.JoinControlRoomAsync(Ctx(connTarget), room, pwd, "uidB", "执行机");

        // v3 会话登记（hello 时序：握手后才受理业务消息）
        var helloResp = await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(connTarget), GatewayTestHarness.HelloEnvelope());
        Assert.Null(GatewayTestHarness.ErrorCode(helloResp));

        var env = new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Command,
            Name = GatewayProtocol.Names.ControlReportCommandResult,
            Payload = GatewayEnvelope.ToPayload(new { result = NewResult(room, "uidA", "uidB") }),
        };
        var resp = await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(connTarget), env);

        Assert.Null(GatewayTestHarness.ErrorCode(resp));
        h.GatewayHub.Verify(x => x.Clients.Client(connSender), Times.AtLeastOnce);

        // 缺 result：bad_request
        var badEnv = new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Command,
            Name = GatewayProtocol.Names.ControlReportCommandResult,
            Payload = GatewayEnvelope.ToPayload(new { }),
        };
        var badResp = await h.Dispatcher.DispatchAsync(GatewayHandlerContext.V3(connTarget), badEnv);
        Assert.Equal(GatewayProtocol.ErrorCodes.BadRequest, GatewayTestHarness.ErrorCode(badResp));
    }
}
