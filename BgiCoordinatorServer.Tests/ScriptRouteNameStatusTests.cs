using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>
/// 配置组内脚本任务「当前执行线路名」（ControlStatus.CurrentScriptRouteName → ControlRoomPlayer → 增量广播）
/// 的贯通回归。
///
/// 背景：配置组里的 JS 脚本任务，任务名恒为该脚本自身，脚本内部逐条执行地图追踪路线；
/// 联机助手成员状态标签与嘟嘟可「锄地数据」成员墙原先只能显示「配置组 · 任务名」，缺具体线路。
/// 本组测试钉死三件事：
/// 1. 线路名确实被服务端落到成员模型并随广播下发；
/// 2. 同一任务内线路切换（其它字段不变）也必须产生 changed 增量——否则助手端在两条路线之间永远不刷新；
/// 3. 任务停止时服务端复位线路名，避免任务结束后广播里残留上一个脚本的线路。
/// </summary>
public class ScriptRouteNameStatusTests
{
    private static (string Room, string Pwd) NewRoom()
        => ("S" + Guid.NewGuid().ToString("N")[..8], "pw");

    /// <summary>唯一 connectionId（_connectionGroups 是静态表，跨测试复用 connId 会串扰断线清理路径）。</summary>
    private static string Conn(string tag) => $"conn-{tag}-{Guid.NewGuid():N}";

    private static GatewayHandlerContext Ctx(string conn) => GatewayHandlerContext.Legacy(conn);

    /// <summary>构造一份状态上报（BgiStatus=running，与加入时默认 unknown 不同 → 首次上报必产生增量）。</summary>
    private static ControlStatus NewStatus(string room, string uid, string name) => new()
    {
        RoomCode = room,
        PlayerUid = uid,
        PlayerName = name,
        BgiStatus = "running",
    };

    /// <summary>捕获本测试内所有 ControlRoomPlayersUpdated 广播的 update 对象。</summary>
    private static List<ControlRoomPlayersUpdate> CaptureUpdates(GatewayTestHarness h)
    {
        var updates = new List<ControlRoomPlayersUpdate>();
        h.LegacyGroupProxy.Setup(p => p.SendCoreAsync("ControlRoomPlayersUpdated",
                It.IsAny<object?[]>(), It.IsAny<CancellationToken>()))
            .Callback<string, object?[], CancellationToken>((_, args, _) =>
            {
                if (args.Length == 1 && args[0] is ControlRoomPlayersUpdate u) updates.Add(u);
            })
            .Returns(Task.CompletedTask);
        return updates;
    }

    /// <summary>入房 + 基线上报（任务未跑），返回后续上报可直接复用的状态模板。</summary>
    private static async Task<ControlStatus> JoinAndBaselineAsync(GatewayTestHarness h, string conn, string room, string pwd)
    {
        await h.Ops.JoinControlRoomAsync(Ctx(conn), room, pwd, "uidR", "成员R");
        var baseline = NewStatus(room, "uidR", "成员R");
        await h.Ops.ReportControlStatusAsync(Ctx(conn), baseline);
        return baseline;
    }

    [Fact]
    public async Task ScriptRouteName_Reported_StoredAndBroadcast()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var conn = Conn("route1");
        var updates = CaptureUpdates(h);
        await JoinAndBaselineAsync(h, conn, room, pwd);

        var status = NewStatus(room, "uidR", "成员R");
        status.TaskRunning = true;
        status.CurrentTaskGroupName = "小怪-锄地";
        status.CurrentTaskName = "锄地一条龙";
        status.CurrentScriptRouteName = "蒙德城.json";
        await h.Ops.ReportControlStatusAsync(Ctx(conn), status);

        var last = updates[^1];
        Assert.False(last.Full);
        var changed = Assert.Single(last.Changed!);
        Assert.Equal("蒙德城.json", changed.CurrentScriptRouteName);

        // 服务端成员模型（广播之外的查询路径，如 WEB/房主拉取）同样带值
        var stored = Assert.Single(h.RoomManager.GetControlRoomPlayers($"CTRL_{room}"));
        Assert.Equal("蒙德城.json", stored.CurrentScriptRouteName);
        Assert.Equal("小怪-锄地", stored.CurrentTaskGroupName);
    }

    [Fact]
    public async Task ScriptRouteChange_SameTask_StillProducesDelta()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var conn = Conn("route2");
        var updates = CaptureUpdates(h);
        await JoinAndBaselineAsync(h, conn, room, pwd);

        var first = NewStatus(room, "uidR", "成员R");
        first.TaskRunning = true;
        first.CurrentTaskGroupName = "小怪-锄地";
        first.CurrentTaskName = "锄地一条龙";
        first.CurrentScriptRouteName = "蒙德城.json";
        await h.Ops.ReportControlStatusAsync(Ctx(conn), first);
        var afterFirst = updates.Count;

        // 只改线路（任务名/配置组/运行态都不变）：必须仍产生 changed，否则助手端两条路线之间不刷新
        var second = NewStatus(room, "uidR", "成员R");
        second.TaskRunning = true;
        second.CurrentTaskGroupName = "小怪-锄地";
        second.CurrentTaskName = "锄地一条龙";
        second.CurrentScriptRouteName = "璃月港.json";
        await h.Ops.ReportControlStatusAsync(Ctx(conn), second);

        Assert.True(updates.Count > afterFirst, "线路切换（其它字段不变）必须产生一次增量广播");
        var changed = Assert.Single(updates[^1].Changed!);
        Assert.Equal("璃月港.json", changed.CurrentScriptRouteName);
    }

    [Fact]
    public async Task TaskStop_ResetsScriptRouteName()
    {
        var h = new GatewayTestHarness();
        var (room, pwd) = NewRoom();
        var conn = Conn("route3");
        var updates = CaptureUpdates(h);
        await JoinAndBaselineAsync(h, conn, room, pwd);

        var running = NewStatus(room, "uidR", "成员R");
        running.TaskRunning = true;
        running.CurrentTaskGroupName = "小怪-锄地";
        running.CurrentTaskName = "锄地一条龙";
        running.CurrentScriptRouteName = "蒙德城.json";
        await h.Ops.ReportControlStatusAsync(Ctx(conn), running);

        // 任务停止：即便上报里还带着旧线路名，服务端也必须复位（防残留到下一个任务的状态显示）
        var stopped = NewStatus(room, "uidR", "成员R");
        stopped.CurrentScriptRouteName = "蒙德城.json";
        await h.Ops.ReportControlStatusAsync(Ctx(conn), stopped);

        var stored = Assert.Single(h.RoomManager.GetControlRoomPlayers($"CTRL_{room}"));
        Assert.False(stored.TaskRunning);
        Assert.Null(stored.CurrentScriptRouteName);
        Assert.Null(Assert.Single(updates[^1].Changed!).CurrentScriptRouteName);
    }
}
