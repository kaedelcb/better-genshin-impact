using System.Text.Json;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

/// <summary>误开的同身份副本退出后，只恢复仍存活的原连接；不重入房或重发任务。</summary>
public class ControlRoomConnectionRecoveryTests
{
    private static string Room() => "R" + Guid.NewGuid().ToString("N")[..8];
    private static GatewayHandlerContext Client() => GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
    private static GatewayEnvelope Command(string name, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Command, Name = name,
        RequestId = Guid.NewGuid().ToString("N"), Payload = GatewayEnvelope.ToPayload(payload),
    };

    private static async Task Join(GatewayTestHarness h, GatewayHandlerContext client, string room,
        string uid = "u0", string instance = "instance", bool remote = false, string machine = "machine")
    {
        await h.Dispatcher.DispatchAsync(client, GatewayTestHarness.HelloEnvelope());
        Assert.Null(GatewayTestHarness.ErrorCode(await h.Dispatcher.DispatchAsync(client,
            Command(GatewayProtocol.Names.ControlJoinRoom, new
            {
                roomCode = room, password = "pw", playerUid = uid, playerName = uid,
                clientInstanceId = instance, machineId = machine, isRemote = remote,
            }))));
    }

    private static ControlStatus Status(string room, string uid = "u0") => new()
    {
        RoomCode = room, PlayerUid = uid, BgiStatus = "running", TaskStatusAvailable = true,
        TaskRunning = true, CurrentTaskName = "正在跑的路线", ScriptTaskProgress = "第6/10条",
        OnlineHoeingGroupNames = ["锄地"], OnlineHoeingGroupTypes = ["group"], ExpectedHoeingPlayers = 1,
    };

    private static async Task Report(GatewayTestHarness h, GatewayHandlerContext client, ControlStatus status)
        => Assert.Null(GatewayTestHarness.ErrorCode(await h.Dispatcher.DispatchAsync(client,
            Command(GatewayProtocol.Names.ControlReportStatus, new { status }))));

    private static int Events(GatewayTestHarness h, string name)
        => h.GatewayClientProxy.Invocations.Concat(h.GatewayGroupProxy.Invocations).Count(call =>
            call.Arguments.Count >= 2 && call.Arguments[0] as string == GatewayProtocol.Callbacks.Event
            && call.Arguments[1] is object?[] { Length: > 0 } args
            && args[0] is GatewayEnvelope envelope && envelope.Name == name);

    [Fact]
    public async Task DuplicateExits_OriginalHeartbeatRestoresRouteWithoutStartingOrReplayingWork()
    {
        var h = new GatewayTestHarness();
        var room = Room(); var original = Client(); var duplicate = Client(); var remote = Client();
        await Join(h, original, room);
        await Report(h, original, Status(room));
        await Join(h, remote, room, "viewer", remote: true);
        await Join(h, duplicate, room);
        await h.Ops.HandleDisconnectAsync(duplicate, null);
        Assert.False(Assert.Single(h.RoomManager.GetControlRoomPlayers("CTRL_" + room)).Online);

        // 离线期间有人发了启动命令：恢复登记不能顺带冲刷它，避免正在跑的任务重跑。
        var cached = new RemoteCommand { RoomCode = room, Target = ["u0"], Sender = "viewer", Cmd = "start_group" };
        await h.Dispatcher.DispatchAsync(remote, Command(GatewayProtocol.Names.ControlSendCommand, new { command = cached }));
        h.GatewayClientProxy.Invocations.Clear(); h.GatewayGroupProxy.Invocations.Clear();

        await Report(h, original, Status(room));

        var player = Assert.Single(h.RoomManager.GetControlRoomPlayers("CTRL_" + room));
        Assert.True(player.Online);
        Assert.Equal(original.ConnectionId, player.ConnectionId);
        Assert.True(player.TaskRunning);
        Assert.Equal("正在跑的路线", player.CurrentTaskName);
        Assert.Equal("第6/10条", player.ScriptTaskProgress);
        Assert.Equal(0, Events(h, GatewayProtocol.Events.ControlBatchStart));
        Assert.Equal(0, Events(h, "control.allReady"));
        Assert.Equal(0, Events(h, GatewayProtocol.LegacyEventMap["RemoteCommand"]));
        Assert.Single(h.RoomManager.GetAndClearPendingCommands("u0"));

        // 恢复后新的遥控命令只投递到原连接一次。
        h.GatewayHub.Invocations.Clear(); h.GatewayClientProxy.Invocations.Clear();
        var live = new RemoteCommand { RoomCode = room, Target = ["u0"], Sender = "viewer", Cmd = "pause" };
        await h.Dispatcher.DispatchAsync(remote, Command(GatewayProtocol.Names.ControlSendCommand, new { command = live }));
        h.GatewayHub.Verify(hub => hub.Clients.Client(original.ConnectionId), Times.Once);
        h.GatewayHub.Verify(hub => hub.Clients.Client(duplicate.ConnectionId), Times.Never);
        Assert.Equal(1, Events(h, GatewayProtocol.LegacyEventMap["RemoteCommand"]));
    }

    [Fact]
    public void OriginalHeartbeatCannotStealAnOnlineDuplicate()
    {
        var manager = new RoomManager(); var group = "CTRL_" + Room();
        manager.AddToControlRoom(group, "original", "u0", "原助手", "instance", "machine");
        manager.AddToControlRoom(group, "duplicate", "u0", "副本", "instance", "machine");
        manager.UpdateControlStatus(group, "original", Status(group[5..]));
        Assert.Equal("duplicate", Assert.Single(manager.GetControlRoomPlayers(group)).ConnectionId);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("disconnectedOriginal")]
    [InlineData("wrongUid")]
    [InlineData("wrongRoom")]
    [InlineData("differentMachine")]
    [InlineData("missingMachine")]
    public void UntrustedOrExpiredRegistrationCannotRecover(string scenario)
    {
        var manager = new RoomManager(); var room = Room(); var group = "CTRL_" + room;
        var instance = "instance";
        var machine = scenario == "missingMachine" ? "" : "machine";
        manager.AddToControlRoom(group, "original", "u0", "原助手", instance, machine);
        if (scenario == "missingMachine")
        {
            // 旧客户端缺少机器标识时，正常连接的常规状态上报仍然有效。
            manager.UpdateControlStatus(group, "original", Status(room));
            Assert.True(Assert.Single(manager.GetControlRoomPlayers(group)).TaskRunning);
        }
        manager.AddToControlRoom(group, "duplicate", "u0", "副本", instance,
            scenario == "differentMachine" ? "other-machine" : machine);
        if (scenario == "disconnectedOriginal") manager.RemoveFromControlRoom(group, "original");
        manager.RemoveFromControlRoom(group, "duplicate");
        var status = Status(room, scenario == "wrongUid" ? "someone-else" : "u0");
        if (scenario == "wrongRoom")
        {
            var otherGroup = "CTRL_" + Room();
            manager.AddToControlRoom(otherGroup, "other", "u0", "其他房间", instance, "machine");
            manager.RemoveFromControlRoom(otherGroup, "other");
            group = otherGroup;
            status.RoomCode = group[5..];
        }
        manager.UpdateControlStatus(group, scenario == "unknown" ? "stranger" : "original", status);
        Assert.All(manager.GetControlRoomPlayers(group), player => Assert.False(player.Online));
    }

    [Fact]
    public async Task ObserverCannotRecoverOfflineExecutorByReportingItsUid()
    {
        var h = new GatewayTestHarness(); var room = Room(); var executor = Client(); var observer = Client();
        await Join(h, executor, room);
        await Join(h, observer, room, remote: true);
        await h.Ops.HandleDisconnectAsync(executor, null);
        await Report(h, observer, Status(room));
        Assert.False(Assert.Single(h.RoomManager.GetControlRoomPlayers("CTRL_" + room)).Online);
    }

    [Fact]
    public void DifferentUsersAndSameUidDifferentInstancesKeepTheirOwnConnections()
    {
        var manager = new RoomManager(); var room = Room(); var group = "CTRL_" + room;
        manager.AddToControlRoom(group, "original", "u0", "原助手", "instance", "machine");
        manager.AddToControlRoom(group, "other-instance", "u0", "其他实例", "instance-2", "machine");
        manager.AddToControlRoom(group, "other-user", "u1", "其他用户", "instance", "machine");
        manager.AddToControlRoom(group, "duplicate", "u0", "副本", "instance", "machine");
        manager.RemoveFromControlRoom(group, "duplicate");
        manager.UpdateControlStatus(group, "original", Status(room));
        manager.UpdateControlStatus(group, "other-instance", Status(room));
        manager.UpdateControlStatus(group, "other-user", Status(room, "u1"));
        var players = manager.GetControlRoomPlayers(group);
        Assert.Equal(3, players.Count);
        Assert.All(players, player => Assert.True(player.Online));
        Assert.Equal(new[] { "original", "other-instance", "other-user" },
            players.Select(player => player.ConnectionId).ToArray());
    }

    [Fact]
    public void RefreshOrReconnectAndLateOldDisconnectKeepNewConnection()
    {
        var manager = new RoomManager(); var room = Room(); var group = "CTRL_" + room;
        manager.AddToControlRoom(group, "old", "u0", "旧连接", "instance", "machine");
        manager.AddToControlRoom(group, "new", "u0", "新连接", "instance", "machine");
        manager.RemoveFromControlRoom(group, "old");
        manager.UpdateControlStatus(group, "old", Status(room));
        manager.UpdateControlStatus(group, "new", Status(room));
        var current = Assert.Single(manager.GetControlRoomPlayers(group));
        Assert.True(current.Online);
        Assert.Equal("new", current.ConnectionId);
        manager.RemoveFromControlRoom(group, "new");
        manager.UpdateControlStatus(group, "old", Status(room));
        Assert.False(current.Online); // 已断线旧连接的迟到上报也不能复活。
        manager.AddToControlRoom(group, "reconnected", "u0", "正常重连", "instance", "machine");
        Assert.True(current.Online);
        Assert.Equal("reconnected", current.ConnectionId);
    }

    [Fact]
    public void LegacyEmptyInstancesKeepSeparateConnectionsAndCannotReviveAfterDisconnect()
    {
        var manager = new RoomManager(); var room = Room(); var group = "CTRL_" + room;
        manager.AddToControlRoom(group, "a", "u0", "旧版甲");
        manager.AddToControlRoom(group, "b", "u0", "旧版乙");
        manager.RemoveFromControlRoom(group, "b");
        manager.UpdateControlStatus(group, "a", Status(room));
        Assert.Equal("a", Assert.Single(manager.GetControlRoomPlayers(group).Where(p => p.Online)).ConnectionId);
        manager.RemoveFromControlRoom(group, "a");
        manager.UpdateControlStatus(group, "a", Status(room));
        Assert.All(manager.GetControlRoomPlayers(group), player => Assert.False(player.Online));
    }

    [Fact]
    public void SeveralSurvivingOldConnectionsDoNotRaceForRecovery()
    {
        var manager = new RoomManager(); var room = Room(); var group = "CTRL_" + room;
        manager.AddToControlRoom(group, "a", "u0", "甲", "instance", "machine");
        manager.AddToControlRoom(group, "b", "u0", "乙", "instance", "machine");
        manager.AddToControlRoom(group, "c", "u0", "丙", "instance", "machine");
        manager.RemoveFromControlRoom(group, "c");
        Parallel.Invoke(() => manager.UpdateControlStatus(group, "a", Status(room)),
            () => manager.UpdateControlStatus(group, "b", Status(room)));
        Assert.False(Assert.Single(manager.GetControlRoomPlayers(group)).Online);
        manager.RemoveFromControlRoom(group, "b");
        manager.UpdateControlStatus(group, "a", Status(room));
        Assert.Equal("a", Assert.Single(manager.GetControlRoomPlayers(group)).ConnectionId);
    }

    [Fact]
    public async Task OriginalChangingToObserverLosesRecoveryEligibility()
    {
        var h = new GatewayTestHarness(); var room = Room(); var original = Client(); var duplicate = Client();
        await Join(h, original, room);
        await Join(h, duplicate, room);
        await Join(h, original, room, remote: true);
        await h.Ops.HandleDisconnectAsync(duplicate, null);
        await Report(h, original, Status(room));
        Assert.False(Assert.Single(h.RoomManager.GetControlRoomPlayers("CTRL_" + room)).Online);
    }

    [Fact]
    public void OriginalChangingIdentityCannotRecoverItsPreviousIdentity()
    {
        var manager = new RoomManager(); var room = Room(); var group = "CTRL_" + room;
        manager.AddToControlRoom(group, "original", "u0", "原身份", "instance", "machine");
        manager.AddToControlRoom(group, "duplicate", "u0", "副本", "instance", "machine");
        manager.AddToControlRoom(group, "original", "u1", "新身份", "new-instance", "machine");
        manager.RemoveFromControlRoom(group, "duplicate");
        manager.UpdateControlStatus(group, "original", Status(room));
        var players = manager.GetControlRoomPlayers(group);
        Assert.False(Assert.Single(players.Where(p => p.PlayerUid == "u0")).Online);
        Assert.Equal("original", Assert.Single(players.Where(p => p.PlayerUid == "u1")).ConnectionId);
    }

    [Fact]
    public async Task LeavingOldControlGroupRevokesRecoveryEligibility()
    {
        var h = new GatewayTestHarness(); var room = Room(); var original = Client(); var duplicate = Client();
        await Join(h, original, room);
        await Join(h, duplicate, room);
        await h.Ops.CreateRoomAsync(original, playerUid: "u0");
        await h.Ops.HandleDisconnectAsync(duplicate, null);
        await Report(h, original, Status(room));
        Assert.False(Assert.Single(h.RoomManager.GetControlRoomPlayers("CTRL_" + room)).Online);
    }

    [Fact]
    public void RemovedMemberSlotCannotBeReclaimedByOldRegistration()
    {
        var manager = new RoomManager(); var room = Room(); var group = "CTRL_" + room;
        manager.AddToControlRoom(group, "original", "u0", "原助手", "instance", "machine");
        manager.AddToControlRoom(group, "duplicate", "u0", "副本", "instance", "machine");
        manager.RemoveFromControlRoom(group, "duplicate");
        manager.AddToControlRoom(group, "reset", "u0", "重置配置", "other-instance", "machine");
        manager.RemoveFromControlRoom(group, "reset");
        manager.AddToControlRoom(group, "replacement", "u0", "新登记", "instance", "machine");
        manager.RemoveFromControlRoom(group, "replacement");
        manager.UpdateControlStatus(group, "original", Status(room));
        Assert.False(Assert.Single(manager.GetControlRoomPlayers(group)).Online);
    }

    [Fact]
    public async Task RecoveryPreservesRunningBatchIdentityAndDoesNotBroadcastAnotherStart()
    {
        var h = new GatewayTestHarness(); var room = Room(); var original = Client(); var duplicate = Client();
        await Join(h, original, room);
        await Report(h, original, Status(room));
        var response = await h.Dispatcher.DispatchAsync(original,
            Command(GatewayProtocol.Names.ControlBatchBegin, new
            {
                requestId = "run", targetUids = new[] { "u0" }, expiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(2),
            }));
        Assert.Null(GatewayTestHarness.ErrorCode(response));
        var batch = response.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!;
        foreach (var stage in new[] { "registered", "prepared", "takeoverConfirmed" })
        {
            response = await h.Dispatcher.DispatchAsync(original,
                Command(GatewayProtocol.Names.ControlBatchUpdate, new
                {
                    generation = batch.Generation, token = "u0", batchId = batch.BatchId,
                    layout = "group", index = batch.Index, attempt = batch.Attempt, stage, result = "", reason = "",
                }));
            Assert.Null(GatewayTestHarness.ErrorCode(response));
            batch = response.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!;
        }
        Assert.Equal("running", batch.Phase);
        await Join(h, duplicate, room);
        await h.Ops.HandleDisconnectAsync(duplicate, null);
        h.GatewayClientProxy.Invocations.Clear(); h.GatewayGroupProxy.Invocations.Clear();
        var status = Status(room);
        status.HoeingBatchId = batch.BatchId; status.HoeingStartPhase = "running";
        await Report(h, original, status);
        var after = h.Ops.CurrentCoordinatedBatch(original)!;
        Assert.Equal(batch.BatchId, after.BatchId);
        Assert.Equal(batch.Generation, after.Generation);
        Assert.Equal(batch.Index, after.Index);
        Assert.Equal(batch.Attempt, after.Attempt);
        Assert.Equal("running", after.Phase);
        Assert.Equal(0, Events(h, GatewayProtocol.Events.ControlBatchStart));
        Assert.Equal(0, Events(h, "control.allReady"));
        Assert.Equal(0, Events(h, GatewayProtocol.LegacyEventMap["RemoteCommand"]));
    }
}
