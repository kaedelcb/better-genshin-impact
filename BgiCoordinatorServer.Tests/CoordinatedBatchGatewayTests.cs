using System.Text.Json;
using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class CoordinatedBatchGatewayTests
{
    private static GatewayEnvelope Command(string name, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Command, Name = name,
        RequestId = Guid.NewGuid().ToString("N"), Payload = GatewayEnvelope.ToPayload(payload),
    };

    private static async Task<(GatewayTestHarness H, GatewayHandlerContext[] Clients, string Room)> Setup(int count = 1)
    {
        var h = new GatewayTestHarness();
        var room = "B" + Guid.NewGuid().ToString("N")[..8];
        var clients = Enumerable.Range(0, count).Select(_ => GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"))).ToArray();
        for (var i = 0; i < count; i++)
        {
            await h.Dispatcher.DispatchAsync(clients[i], GatewayTestHarness.HelloEnvelope());
            await h.Ops.JoinControlRoomAsync(clients[i], room, "pw", "u" + i, "name" + i, clientInstanceId: "instance" + i);
            await h.Ops.ReportControlStatusAsync(clients[i], new ControlStatus
            {
                RoomCode = room, PlayerUid = "u" + i, ExpectedHoeingPlayers = count,
                OnlineHoeingGroupNames = ["group" + i], OnlineHoeingGroupTypes = ["group"],
            });
        }
        return (h, clients, room);
    }

    private static CoordinatedBatchSnapshot Snapshot(GatewayEnvelope response)
    {
        Assert.Null(GatewayTestHarness.ErrorCode(response));
        return response.Payload!.Deserialize<CoordinatedBatchSnapshot>(GatewayJson.Options)!;
    }

    private static Task<GatewayEnvelope> Begin(GatewayTestHarness h, GatewayHandlerContext client,
        string request = "request", string[]? targets = null, DateTimeOffset? expiry = null)
        => h.Dispatcher.DispatchAsync(client, Command(GatewayProtocol.Names.ControlBatchBegin,
            new { requestId = request, targetUids = targets ?? ["u0"], expiresAtUtc = expiry ?? DateTimeOffset.UtcNow.AddMinutes(2) }));

    private static Task<GatewayEnvelope> Update(GatewayTestHarness h, GatewayHandlerContext client,
        CoordinatedBatchSnapshot s, string uid = "u0", string stage = "", string result = "", string reason = "",
        string? batchId = null, string? token = null)
        => h.Dispatcher.DispatchAsync(client, Command(GatewayProtocol.Names.ControlBatchUpdate, new
        {
            generation = s.Generation, token = token ?? uid, batchId = batchId ?? s.BatchId,
            layout = "group", index = s.Index, attempt = s.Attempt, stage, result, reason,
        }));

    private static async Task<CoordinatedBatchSnapshot> Run(GatewayTestHarness h, GatewayHandlerContext[] clients, CoordinatedBatchSnapshot state)
    {
        foreach (var stage in new[] { "registered", "prepared", "takeoverConfirmed" })
            for (var i = 0; i < clients.Length; i++) state = Snapshot(await Update(h, clients[i], state, "u" + i, stage));
        Assert.Equal("running", state.Phase);
        return state;
    }

    private static int Events(GatewayTestHarness h, string name) => h.GatewayClientProxy.Invocations.Count(call =>
        call.Arguments.Count >= 2 && call.Arguments[0] as string == GatewayProtocol.Callbacks.Event
        && call.Arguments[1] is object?[] { Length: > 0 } args && args[0] is GatewayEnvelope e && e.Name == name);

    [Fact]
    public async Task AllReadyCreatesAuthorityAndGatewayReturnsRealSnapshot()
    {
        var (h, clients, _) = await Setup();
        Assert.Null((await h.Dispatcher.DispatchAsync(clients[0], Command(GatewayProtocol.Names.ControlBatchCurrent, new { }))).Payload);
        await h.Ops.ReportOnlineEventAsync(clients[0], 7, true);
        var state = Snapshot(await h.Dispatcher.DispatchAsync(clients[0], Command(GatewayProtocol.Names.ControlBatchCurrent, new { })));
        Assert.Equal("registering", state.Phase);
        Assert.True(state.StateContinuation);
        Assert.Contains(GatewayProtocol.HoeingStateContinuationCapability, GatewayProtocol.ServerCapabilities);
        Assert.Equal("online", state.Source);
        Assert.Equal(7, state.IntentGenerations["u0"]);
        Assert.True(state.Generation > 0);
        Assert.Equal(1, Events(h, GatewayProtocol.Events.ControlBatchStart));
        Assert.Equal(0, Events(h, "control.allReady"));
        state = Snapshot(await Update(h, clients[0], state, stage: "registered"));
        Assert.Equal("preparing", state.Phase);
        state = Snapshot(await Update(h, clients[0], state, stage: "prepared"));
        Assert.Equal("takeover", state.Phase);
        Assert.Equal("running", Snapshot(await Update(h, clients[0], state, stage: "takeoverConfirmed")).Phase);
    }

    [Fact]
    public async Task UnregisteredPreviousRound_NextAllReadyKeepsOriginalAuthority()
    {
        var (h, clients, _) = await Setup();
        await h.Ops.ReportOnlineEventAsync(clients[0], 48, true);
        var first = h.Ops.CurrentCoordinatedBatch(clients[0])!;
        await h.Ops.ReportOnlineEventAsync(clients[0], 49, true);
        var current = h.Ops.CurrentCoordinatedBatch(clients[0])!;
        Assert.Equal(first.BatchId, current.BatchId);
        Assert.Equal(first.Revision, current.Revision);
        Assert.Equal("registering", current.Phase);
        Assert.Equal(1, Events(h, GatewayProtocol.Events.ControlBatchStart));
    }

    [Fact]
    public async Task ActivePreviousRound_NextGenerationDoesNotAbortOrBroadcastPhantomStart()
    {
        var (h, clients, _) = await Setup();
        await h.Ops.ReportOnlineEventAsync(clients[0], 48, true);
        var first = await Run(h, clients, h.Ops.CurrentCoordinatedBatch(clients[0])!);
        await h.Ops.ReportOnlineEventAsync(clients[0], 49, true);
        var current = h.Ops.CurrentCoordinatedBatch(clients[0])!;
        Assert.Equal(first.BatchId, current.BatchId);
        Assert.Equal(first.Revision, current.Revision);
        Assert.Equal("running", current.Phase); // 旧f541571e7断言aborted；新开始必须无损拒绝。
        Assert.Equal(1, Events(h, GatewayProtocol.Events.ControlBatchStart));
        Assert.Equal(0, Events(h, GatewayProtocol.Events.ControlBatchState));
    }

    [Fact]
    public async Task ManualReplayReturnsOriginalIdentityWithoutAnotherStart()
    {
        var (h, clients, _) = await Setup();
        var first = Snapshot(await Begin(h, clients[0]));
        var repeat = Snapshot(await Begin(h, clients[0]));
        Assert.Equal(first.BatchId, repeat.BatchId);
        Assert.Equal(first.Generation, repeat.Generation);
        Assert.Equal(1, Events(h, GatewayProtocol.Events.ControlBatchStart));
        Assert.NotNull(GatewayTestHarness.ErrorCode(await Begin(h, clients[0], "another")));
        Assert.Equal(first.BatchId, h.Ops.CurrentCoordinatedBatch(clients[0])!.BatchId);
        Assert.Equal(first.Revision, h.Ops.CurrentCoordinatedBatch(clients[0])!.Revision);
    }

    [Fact]
    public async Task CompletedAndAbortedBatchesKeepOwnershipUntilEveryRegisteredMemberCleans()
    {
        foreach (var result in new[] { "succeeded", "cancelled" })
        {
            var (h, clients, _) = await Setup();
            var state = await Run(h, clients, Snapshot(await Begin(h, clients[0])));
            state = Snapshot(await Update(h, clients[0], state, result: result));
            Assert.False(state.CleanupComplete);
            Assert.NotNull(GatewayTestHarness.ErrorCode(await Begin(h, clients[0], "next")));
            var cleaned = Snapshot(await Update(h, clients[0], state, stage: "cleaned"));
            Assert.True(cleaned.CleanupComplete);
            var next = Snapshot(await Begin(h, clients[0], "next"));
            Assert.NotEqual(state.BatchId, next.BatchId);
            Assert.NotNull(GatewayTestHarness.ErrorCode(await Update(h, clients[0], state, stage: "cleaned")));
            Assert.Equal(next.BatchId, h.Ops.CurrentCoordinatedBatch(clients[0])!.BatchId);
        }
    }

    [Fact]
    public async Task MissingCapabilityRejectsWholeRosterBeforeAnyStartNotification()
    {
        var (h, clients, _) = await Setup(2);
        h.Capabilities.AllowAll = false;
        h.Capabilities.SupportedConnections.Add(clients[0].ConnectionId);
        var rejected = await Begin(h, clients[0], targets: ["u0", "u1"]);
        Assert.NotNull(GatewayTestHarness.ErrorCode(rejected));
        Assert.Contains("hoeing_start_capability_required:u1", rejected.Payload!.ToJsonString());
        Assert.Null(h.Ops.CurrentCoordinatedBatch(clients[0]));
        Assert.Equal(0, Events(h, GatewayProtocol.Events.ControlBatchStart));
        await h.Ops.ReportOnlineEventAsync(clients[0], 1, true);
        await h.Ops.ReportOnlineEventAsync(clients[1], 1, true);
        Assert.Null(h.Ops.CurrentCoordinatedBatch(clients[0]));
        Assert.Equal(0, Events(h, GatewayProtocol.Events.ControlBatchStart));
    }

    [Fact]
    public async Task PreparationFailureImmediatelyNotifiesFixedRosterWithoutTakeover()
    {
        var (h, clients, _) = await Setup(2);
        var state = Snapshot(await Begin(h, clients[0], targets: ["u0", "u1"]));
        state = Snapshot(await Update(h, clients[0], state, stage: "registered"));
        state = Snapshot(await Update(h, clients[1], state, "u1", "registered"));
        var failed = Snapshot(await Update(h, clients[1], state, "u1", result: "preparation_failed", reason: "config_missing"));
        Assert.Equal("aborted", failed.Phase);
        Assert.Contains("u1:preparing:config_missing", failed.Reason);
        Assert.DoesNotContain(failed.Members, m => m.Stage == "takeoverConfirmed");
        Assert.Equal(2, Events(h, GatewayProtocol.Events.ControlBatchState));
    }

    [Fact]
    public async Task KnownParticipantFailureEntersBoundedRecoveryWithOwnerIdentity()
    {
        var (h, clients, _) = await Setup();
        var state = await Run(h, clients, Snapshot(await Begin(h, clients[0])));
        var retry = Snapshot(await Update(h, clients[0], state, result: "failed"));
        Assert.Equal("running", retry.Phase);
        Assert.Equal(1, retry.Attempt);
        Assert.Equal("shared_recovery", retry.Reason);
        Assert.Equal("recovery_budget_exhausted", Snapshot(await Update(h, clients[0], retry, result: "failed")).Reason);
    }

    [Fact]
    public async Task UnknownResultKeepsOriginalRequestUntilTheActualResultArrives()
    {
        var (h, clients, _) = await Setup();
        var state = await Run(h, clients, Snapshot(await Begin(h, clients[0])));
        var unknown = Snapshot(await Update(h, clients[0], state, result: "unknown", reason: "reply_lost"));
        Assert.True(unknown.StateContinuation);
        Assert.Equal("running", unknown.Phase);
        var member = Assert.Single(unknown.Members);
        Assert.Equal("u0", member.Uid);
        Assert.Equal("unknown", member.Result);
        Assert.Equal("reply_lost", member.Reason);
        Assert.Equal(state.BatchId, unknown.BatchId);
        Assert.Equal(state.Index, unknown.Index);
        Assert.Equal(state.Attempt, unknown.Attempt);
        Assert.NotNull(GatewayTestHarness.ErrorCode(await Begin(h, clients[0], "replacement")));
        Assert.NotNull(GatewayTestHarness.ErrorCode(await Update(h, clients[0], unknown, stage: "cleaned")));
        var completed = Snapshot(await Update(h, clients[0], unknown, result: "succeeded"));
        Assert.Equal("completed", completed.Phase);
        Assert.Equal(state.BatchId, completed.BatchId);
        Assert.Equal("succeeded", Assert.Single(completed.Members).Result);
    }

    [Fact]
    public async Task ReplayedResetAndStaleBatchCannotAbortLaterBatch()
    {
        var (h, clients, _) = await Setup();
        var first = await Run(h, clients, Snapshot(await Begin(h, clients[0])));
        var resetCommand = Command(GatewayProtocol.Names.ControlBatchReset,
            new { resetId = "reset", targetUids = new[] { "*" }, expectedGeneration = first.Generation, expectedBatchId = first.BatchId });
        var reset = await h.Dispatcher.DispatchAsync(clients[0], resetCommand);
        Assert.Null(GatewayTestHarness.ErrorCode(reset));
        Assert.Equal(1, Events(h, GatewayProtocol.Events.ControlBatchState));
        Snapshot(await Update(h, clients[0], first, stage: "cleaned"));
        var next = Snapshot(await Begin(h, clients[0], "next"));
        var replay = await h.Dispatcher.DispatchAsync(clients[0], resetCommand);
        Assert.Equal(first.BatchId, replay.Payload!.Deserialize<CoordinatedBatchResetResult>(GatewayJson.Options)!.BatchId);
        var stale = await h.Dispatcher.DispatchAsync(clients[0], Command(GatewayProtocol.Names.ControlBatchReset,
            new { resetId = "stale", targetUids = new[] { "*" }, expectedBatchId = first.BatchId }));
        Assert.Equal("stale_reset", stale.Payload!.Deserialize<CoordinatedBatchResetResult>(GatewayJson.Options)!.Reason);
        Assert.Equal(next.BatchId, h.Ops.CurrentCoordinatedBatch(clients[0])!.BatchId);
        Assert.Equal("registering", h.Ops.CurrentCoordinatedBatch(clients[0])!.Phase);
    }

    [Fact]
    public async Task FrozenBindingAndManualIntentConsumptionOnlyAffectSelectedMembers()
    {
        var (h, clients, room) = await Setup(2);
        await h.Ops.ReportOnlineEventAsync(clients[0], 4, true);
        h.RoomManager.ReportOnlineEvent("CTRL_" + room, clients[1].ConnectionId, 9);
        var state = Snapshot(await Begin(h, clients[0], targets: ["u0"]));
        Assert.True(h.RoomManager.GetControlRoomPlayers("CTRL_" + room).Single(p => p.PlayerUid == "u0").OnlineEventConsumed);
        Assert.False(h.RoomManager.GetControlRoomPlayers("CTRL_" + room).Single(p => p.PlayerUid == "u1").OnlineEventConsumed);
        await h.Ops.ReportControlStatusAsync(clients[0], new ControlStatus
        {
            RoomCode = room, PlayerUid = "u0", OnlineHoeingGroupNames = ["changed"], OnlineHoeingGroupTypes = ["onedragon"],
            HoeingBatchId = state.BatchId, HoeingStartPhase = "preparing", HoeingStartMessage = "检查资源",
        });
        Assert.Equal("group0", h.Ops.CurrentCoordinatedBatch(clients[0])!.Bindings["u0"][0].Name);
        Assert.Equal("group", state.Bindings["u0"][0].Type);
        var player = h.RoomManager.GetControlRoomPlayers("CTRL_" + room).Single(p => p.PlayerUid == "u0");
        Assert.Equal("preparing", player.HoeingStartPhase);
        await h.Ops.ReportControlStatusAsync(clients[0], new ControlStatus { RoomCode = room, PlayerUid = "u0" });
        Assert.Null(player.HoeingBatchId);
        Assert.Null(player.HoeingStartPhase);
        Assert.Null(player.HoeingStartMessage);
    }

    [Fact]
    public async Task ExpiredManualRequestAndReplacedExecutorAreRejectedWithoutMutatingBatch()
    {
        var (h, clients, room) = await Setup();
        Assert.NotNull(GatewayTestHarness.ErrorCode(await Begin(h, clients[0], expiry: DateTimeOffset.UtcNow.AddSeconds(-1))));
        var state = Snapshot(await Begin(h, clients[0]));
        var replacement = GatewayHandlerContext.V3(Guid.NewGuid().ToString("N"));
        await h.Dispatcher.DispatchAsync(replacement, GatewayTestHarness.HelloEnvelope());
        await h.Ops.JoinControlRoomAsync(replacement, room, "pw", "u0", "replacement", clientInstanceId: "other-instance");
        Assert.NotNull(GatewayTestHarness.ErrorCode(await Update(h, replacement, state, stage: "registered")));
        Assert.Equal(state.BatchId, h.Ops.CurrentCoordinatedBatch(clients[0])!.BatchId);
        Assert.Equal("registering", h.Ops.CurrentCoordinatedBatch(clients[0])!.Phase);
    }

    [Fact]
    public async Task FixedRosterWorldReadinessCannotUseOnlyCurrentlyPresentMembers()
    {
        var (h, clients, _) = await Setup(2);
        var batch = await Run(h, clients, Snapshot(await Begin(h, clients[0], targets: ["u0", "u1"])));
        var host = GatewayHandlerContext.V3("game-host-" + Guid.NewGuid().ToString("N"));
        var member = GatewayHandlerContext.V3("game-member-" + Guid.NewGuid().ToString("N"));
        await h.Dispatcher.DispatchAsync(host, GatewayTestHarness.HelloEnvelope());
        await h.Dispatcher.DispatchAsync(member, GatewayTestHarness.HelloEnvelope());
        var create = await h.Dispatcher.DispatchAsync(host, Command(GatewayProtocol.Names.RoomCreate,
            new { playerName = "host", playerUid = "u0", expectedPlayerCount = 1, reportedVersion = "1.0.0",
                hoeingBatchId = batch.BatchId, expectedParticipantUids = batch.ParticipantUids }));
        Assert.Null(GatewayTestHarness.ErrorCode(create));
        var roomCode = create.Payload!["roomCode"]!.GetValue<string>();
        var room = h.RoomManager.GetRoom(roomCode)!;
        Assert.Equal(2, room.ExpectedPlayerCount);
        Assert.False(h.RoomManager.RecordWorldJoined(roomCode, host.ConnectionId));
        Assert.False((await h.Ops.GetHoeingStartupReadyAsync(host, batch.BatchId, batch.ParticipantUids)).Ready);
        var wrongBatch = await h.Ops.JoinRoomAsync(member, roomCode, "member", "u1", hoeingBatchId: "stale-batch");
        Assert.False(wrongBatch.Success);
        Assert.Equal("hoeing_start_identity_mismatch", wrongBatch.Error);
        var rejected = await h.Ops.JoinRoomAsync(member, roomCode, "host", "outsider", reportedVersion: room.Players[0].ReportedVersion, hoeingBatchId: batch.BatchId);
        Assert.False(rejected.Success);
        Assert.Equal("hoeing_start_not_participant", rejected.Error);
        Assert.True((await h.Ops.JoinRoomAsync(member, roomCode, "member", "u1", reportedVersion: room.Players[0].ReportedVersion, hoeingBatchId: batch.BatchId)).Success);
        Assert.False((await h.Ops.GetHoeingStartupReadyAsync(host, batch.BatchId, batch.ParticipantUids)).Ready);
        Assert.True(h.RoomManager.RecordWorldJoined(roomCode, member.ConnectionId));
        var readyQuery = Command(GatewayProtocol.Names.RoomGetState,
            new { section = "hoeingStartupReady", batchId = batch.BatchId, participantUids = batch.ParticipantUids });
        var readyReply = await h.Dispatcher.QueryAsync(host, readyQuery);
        Assert.Null(GatewayTestHarness.ErrorCode(readyReply));
        var ready = readyReply.Payload!["value"]!.Deserialize<HoeingStartupReadySnapshot>(GatewayJson.Options)!;
        Assert.True(ready.Ready);
        Assert.Equal(batch.ParticipantUids.Order(), ready.WorldJoinedUids.Order());
        room.GracePendingMembers[member.ConnectionId] = DateTime.UtcNow.AddSeconds(30);
        Assert.False((await h.Ops.GetHoeingStartupReadyAsync(host, batch.BatchId, batch.ParticipantUids)).Ready);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.GetHoeingStartupReadyAsync(host, "stale", batch.ParticipantUids));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.SetRoomConfigAsync(host,
            new RoomConfig { HoeingBatchId = "another", ExpectedParticipantUids = ["u0"] }));
        await h.Ops.SetRoomConfigAsync(host, new RoomConfig
            { HoeingBatchId = batch.BatchId, ExpectedParticipantUids = batch.ParticipantUids.ToList() });
        Assert.Equal(batch.BatchId, room.HostConfig!.HoeingBatchId);
    }

    [Fact]
    public async Task LegacyQuickCommandCannotBypassCapabilityAndBatchAuthority()
    {
        var (h, clients, room) = await Setup();
        h.Capabilities.AllowAll = false;
        await h.Ops.SendRemoteCommandAsync(clients[0], new RemoteCommand
        {
            RoomCode = room, Cmd = "start_group", CommandId = "old-quick", SenderUid = "u0", Target = ["u0"],
            Params = new Dictionary<string, object> { ["key"] = "一键锄地" },
        });
        Assert.Equal(0, Events(h, "control.remoteCommand"));
        Assert.Equal(0, Events(h, GatewayProtocol.Events.ControlBatchStart));
        Assert.Null(h.Ops.CurrentCoordinatedBatch(clients[0]));
    }

    [Fact]
    public async Task ExplicitResetCleanupCanReleaseLostTokenButCannotTouchSuccessor()
    {
        var (h, clients, _) = await Setup(2);
        var state = await Run(h, clients, Snapshot(await Begin(h, clients[0], targets: ["u0", "u1"])));
        await h.Ops.ResetCoordinatedBatchAsync(clients[0], "physical-reset", ["u0"],
            expectedBatchId: state.BatchId);
        Assert.NotNull(GatewayTestHarness.ErrorCode(await Update(h, clients[0], state, stage: "cleaned", token: "restarted")));
        var denied = await h.Dispatcher.DispatchAsync(clients[1], Command(GatewayProtocol.Names.ControlBatchResetCleaned,
            new { resetId = "physical-reset", batchId = state.BatchId }));
        Assert.NotNull(GatewayTestHarness.ErrorCode(denied));
        var cleaned = Snapshot(await h.Dispatcher.DispatchAsync(clients[0], Command(GatewayProtocol.Names.ControlBatchResetCleaned,
            new { resetId = "physical-reset", batchId = state.BatchId })));
        Assert.False(cleaned.CleanupComplete);
        cleaned = Snapshot(await Update(h, clients[1], state, "u1", stage: "cleaned"));
        Assert.True(cleaned.CleanupComplete);
        var next = Snapshot(await Begin(h, clients[0], "next", ["u0", "u1"]));
        Assert.NotNull(GatewayTestHarness.ErrorCode(await h.Dispatcher.DispatchAsync(clients[0],
            Command(GatewayProtocol.Names.ControlBatchResetCleaned, new { resetId = "physical-reset", batchId = state.BatchId }))));
        Assert.Equal(next.BatchId, h.Ops.CurrentCoordinatedBatch(clients[0])!.BatchId);
        Assert.Equal("registering", h.Ops.CurrentCoordinatedBatch(clients[0])!.Phase);
    }

    [Fact]
    public async Task WebBeginRequiresSuccessfulPasswordJoinAndCannotCrossRoomsBySenderPrefix()
    {
        var (h, clients, room) = await Setup();
        var web = GatewayHandlerContext.V3("web-" + Guid.NewGuid().ToString("N"));
        await h.Dispatcher.DispatchAsync(web, GatewayTestHarness.HelloEnvelope());
        await h.Ops.JoinControlRoomAsync(web, room, "wrong-password", "web_user", "web");
        Assert.NotNull(GatewayTestHarness.ErrorCode(await Begin(h, web)));
        await h.Ops.SendRemoteCommandAsync(web, new RemoteCommand
        {
            RoomCode = room, Cmd = "start_group", SenderUid = "web_fake", Target = ["u0"],
            Params = new Dictionary<string, object> { ["groupName"] = "group0" },
        });
        Assert.Equal(0, Events(h, "control.remoteCommand"));
        Assert.Null(h.Ops.CurrentCoordinatedBatch(clients[0]));

        await h.Ops.JoinControlRoomAsync(web, room, "pw", "web_user", "web");
        var accepted = Snapshot(await Begin(h, web));
        Assert.Equal("manual", accepted.Source);
        Assert.Equal(new[] { "u0" }, accepted.ParticipantUids);

        var otherRoom = "B" + Guid.NewGuid().ToString("N")[..8];
        var other = GatewayHandlerContext.V3("other-" + Guid.NewGuid().ToString("N"));
        await h.Dispatcher.DispatchAsync(other, GatewayTestHarness.HelloEnvelope());
        await h.Ops.JoinControlRoomAsync(other, otherRoom, "other-pw", "other-uid", "other");
        await h.Ops.ReportControlStatusAsync(other, new ControlStatus
            { RoomCode = otherRoom, OnlineHoeingGroupNames = ["other-group"], OnlineHoeingGroupTypes = ["group"] });
        await h.Ops.SendRemoteCommandAsync(web, new RemoteCommand
        {
            RoomCode = otherRoom, Cmd = "start_group", SenderUid = "web_user", Target = ["other-uid"],
            Params = new Dictionary<string, object> { ["key"] = "一键锄地" },
        });
        Assert.Null(h.Ops.CurrentCoordinatedBatch(other));
        Assert.NotNull(GatewayTestHarness.ErrorCode(await Begin(h, web, "wrong-room", ["other-uid"])));
        Assert.Equal(1, Events(h, GatewayProtocol.Events.ControlBatchStart));
    }

    [Fact]
    public async Task SeparateGameCoordinatorFreezesContractWithoutLocalControlBatch()
    {
        var h = new GatewayTestHarness();
        var host = GatewayHandlerContext.V3("host-" + Guid.NewGuid().ToString("N"));
        var created = await h.Ops.CreateRoomAsync(host, playerUid: "host", expectedPlayerCount: 1,
            hoeingBatchId: "external-control-batch", expectedParticipantUids: ["host"]);
        Assert.True(h.RoomManager.RecordWorldJoined(created.Code, host.ConnectionId));
        Assert.True((await h.Ops.GetHoeingStartupReadyAsync(host, "external-control-batch", ["host"])).Ready);
    }
}
