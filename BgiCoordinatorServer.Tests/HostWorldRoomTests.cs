using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class HostWorldRoomTests
{
    private static GatewayHandlerContext Client(string name) => GatewayHandlerContext.V3(name);

    [Theory]
    [InlineData(1)]
    [InlineData(2)]
    public async Task PhysicalPartyIncludesHostButRouteAndFightQuorumsOnlyUseRealExecutors(int executorCount)
    {
        var (h, room) = await Create(executorCount + 1);
        await Join(h, room, "runner1");
        if (executorCount == 2) await Join(h, room, "runner2");
        await h.Ops.ReportWorldJoinedAsync(Client("host"));
        await h.Ops.ReportWorldJoinedAsync(Client("runner1"));
        if (executorCount == 2)
        {
            Assert.Equal("waiting", State(h, room, "host", "start").Phase);
            await h.Ops.ReportWorldJoinedAsync(Client("runner2"));
        }
        Assert.Equal(executorCount + 1, h.RoomManager.GetWorldJoinedCount(room.Code));
        var started = State(h, room, "host", "start");
        Assert.Equal("ready", started.Phase);
        Assert.Equal(executorCount, started.ExecutorUids.Count);
        Assert.DoesNotContain("host", started.ExecutorUids);
        Assert.Equal("running", State(h, room, "host", "publish", ["route.json"]).Phase);

        Assert.False(h.RoomManager.RecordArrival(room.Code, "point", "host", expectedCount: 1));
        Assert.False(h.RoomManager.RecordFightDone(room.Code, "fight", "host"));
        Assert.Equal(executorCount == 1, h.RoomManager.RecordArrival(room.Code, "point", "runner1", expectedCount: 1));
        Assert.Equal(executorCount == 1, h.RoomManager.RecordFightDone(room.Code, "fight", "runner1"));
        if (executorCount == 2)
        {
            Assert.True(h.RoomManager.RecordArrival(room.Code, "point", "runner2", expectedCount: 3));
            Assert.True(h.RoomManager.RecordFightDone(room.Code, "fight", "runner2"));
        }
        Assert.Equal(executorCount, h.RoomManager.GetFightDoneStatus(room.Code, "fight").RequiredDoneCount);
        Assert.Throws<InvalidOperationException>(() => State(h, room, "host", "finish"));
        Assert.Equal(executorCount == 1 ? "completed" : "running", State(h, room, "runner1", "finish").Phase);
        if (executorCount == 2) Assert.Equal("completed", State(h, room, "runner2", "finish").Phase);
        await h.Ops.CloseRoomAsync(Client("host"));
        Assert.Equal("completed", State(h, room, "runner1", "finish").Phase);
        Assert.Equal("completed", State(h, room, "host").Phase);
        Assert.Throws<InvalidOperationException>(() => State(h, room, "unadmitted"));
    }

    [Fact]
    public async Task NoExecutorCannotStartAndAnExplicitEmptyManifestHasItsOwnTerminalState()
    {
        var (h, room) = await Create(2);
        await h.Ops.ReportWorldJoinedAsync(Client("host"));
        Assert.Equal("waiting", h.Ops.HostWorldOperation(Client("host"), room.Code, room.StandbySessionId!,
            "start", allowPartial: true, partyCount: 1).Phase);
        await Join(h, room, "runner");
        await h.Ops.ReportWorldJoinedAsync(Client("runner"));
        Assert.Equal("ready", State(h, room, "host", "start").Phase);
        Assert.Null(State(h, room, "runner").RouteNames);
        Assert.Equal("noRoutes", State(h, room, "host", "publish", []).Phase);
        Assert.Empty(State(h, room, "runner").RouteNames!);
        await h.Ops.CloseRoomAsync(Client("host"));
        Assert.Equal("noRoutes", State(h, room, "runner").Phase);
    }

    [Theory]
    [InlineData("host")]
    [InlineData("runner")]
    public async Task DepartingBeforeFinishIsNotNormalCompletion(string leaving)
    {
        var (h, room) = await Create(2);
        await Join(h, room, "runner");
        await h.Ops.ReportWorldJoinedAsync(Client("host"));
        await h.Ops.ReportWorldJoinedAsync(Client("runner"));
        Assert.Equal("ready", State(h, room, "host", "start").Phase);
        Assert.Equal("running", State(h, room, "host", "publish", ["route.json"]).Phase);
        if (leaving == "host") await h.Ops.HandleDisconnectAsync(Client("host"), null);
        else await h.Ops.LeaveRoomAsync(Client("runner"));
        Assert.Equal("aborted", State(h, room, leaving == "host" ? "runner" : "host").Phase);
    }

    [Fact]
    public async Task JoinRejectsUnsupportedClientAndWrongRoomInstance()
    {
        var (h, room) = await Create(2);
        h.Capabilities.UnsupportedCapabilities.Add(("old", GatewayProtocol.HostProvidesWorldCapability));
        var old = await h.Ops.JoinRoomAsync(Client("old"), room.Code, "old", "old", "1.0.0", acceptsStandby: true);
        Assert.False(old.Success);
        var wrong = await h.Ops.JoinRoomAsync(Client("runner"), room.Code, "runner", "runner", "1.0.0",
            standbySessionId: "different-session", acceptsStandby: true);
        Assert.False(wrong.Success);
        Assert.Single(h.RoomManager.GetRoom(room.Code)!.Players);
    }

    [Fact]
    public async Task ExperienceStopRequiresTheFrozenExecutorsAndSurvivesRoomClose()
    {
        var (h, room) = await Create(3);
        await Join(h, room, "a");
        await Join(h, room, "b");
        await Open(h, room, "a", "b");
        Assert.False(h.RoomManager.RecordExpArmed(room.Code, "host"));
        Assert.False(h.RoomManager.RecordExpCapReached(room.Code, "host"));
        Assert.False(h.RoomManager.RecordExpArmed(room.Code, "a"));
        Assert.False(h.RoomManager.RecordExpCapReached(room.Code, "a"));
        Assert.Equal("running", State(h, room, "host").Phase);
        Assert.True(h.RoomManager.RecordTwoConsecutiveNoExp(room.Code, "b"));
        Assert.Equal("expCap", State(h, room, "host").Phase);
        await h.Ops.CloseRoomAsync(Client("host"));
        Assert.Equal("expCap", State(h, room, "b").Phase);
    }

    [Fact]
    public async Task ReconnectCannotShrinkQuorumOrReplaceTheOriginalIdentity()
    {
        var (h, room) = await Create(3);
        await Join(h, room, "a");
        await Join(h, room, "b");
        await Open(h, room, "a", "b");
        Assert.False(h.RoomManager.RecordArrival(room.Code, "point", "b"));
        var pending = await h.Ops.JoinRoomAsync(Client("replacement"), room.Code, "b", "b", "1.0.0",
            standbySessionId: room.StandbySessionId, acceptsStandby: true);
        Assert.Equal("host_world_reconnect_pending", pending.Error);
        await h.Ops.HandleDisconnectAsync(Client("b"), null);
        Assert.False(h.RoomManager.RecordArrival(room.Code, "another", "a"));
        var joined = await h.Ops.JoinRoomAsync(Client("replacement"), room.Code, "b", "b", "1.0.0",
            standbySessionId: room.StandbySessionId, acceptsStandby: true);
        Assert.True(joined.Success);
        Assert.Equal(new[] { "a", "b" }, State(h, room, "replacement").ExecutorUids);
        Assert.False(h.RoomManager.RecordArrival(room.Code, "point", "b"));
        Assert.True(h.RoomManager.RecordArrival(room.Code, "point", "a"));
    }

    [Fact]
    public async Task HostCannotRepublishDifferentContentOrReleaseTheRouteBarrier()
    {
        var (h, room) = await Create(2);
        await Join(h, room, "a");
        await Open(h, room, "a");
        Assert.Throws<InvalidOperationException>(() => h.Ops.HostWorldOperation(Client("host"), room.Code,
            room.StandbySessionId!, "publish", ["route.json"], manifestDigest: new string('b', 64)));
        Assert.Equal(new string('a', 64), State(h, room, "a").ManifestDigest);
        await h.Ops.WaitForAllPlayersAsync(Client("host"), "route_sync_done", 42);
        Assert.False(h.RoomManager.GetRoom(room.Code)!.ArrivalSets.ContainsKey("route_sync_done"));
        await h.Ops.WaitForAllPlayersAsync(Client("a"), "route_sync_done", -1);
        Assert.Contains("route_sync_done", h.RoomManager.GetRoom(room.Code)!.BroadcastedSyncIds);
    }

    [Fact]
    public async Task ActiveHostAndPassiveGuestChooseIndependently()
    {
        var (h, room) = await Create(2, hostStandby: false);
        await Join(h, room, "idle", standbyOnly: true);
        await Open(h, room, "idle");
        var state = State(h, room, "host");
        Assert.Equal(new[] { "host" }, state.ExecutorUids);
        Assert.Equal(new[] { "idle" }, state.StandbyUids);
        Assert.False(h.RoomManager.RecordArrival(room.Code, "point", "idle"));
        Assert.True(h.RoomManager.RecordArrival(room.Code, "point", "host"));
        Assert.Throws<InvalidOperationException>(() => State(h, room, "idle", "finish"));
        await h.Ops.ReportCoordinatedAbortAsync(Client("idle"), "local idle stop", "idle");
        await h.Ops.LeaveRoomAsync(Client("idle"));
        Assert.Equal("running", State(h, room, "host").Phase);
        Assert.Equal("completed", State(h, room, "host", "finish").Phase);
    }

    [Fact]
    public async Task AllPlayersChoosingStandbyIsNotANormalCompletion()
    {
        var (h, room) = await Create(2);
        await Join(h, room, "idle", standbyOnly: true);
        await h.Ops.ReportWorldJoinedAsync(Client("host"));
        await h.Ops.ReportWorldJoinedAsync(Client("idle"));
        var state = State(h, room, "host", "start");
        Assert.Equal("aborted", state.Phase);
        Assert.Empty(state.ExecutorUids);
        Assert.False(h.RoomManager.GetRoom(room.Code)!.IsStarted);
        Assert.Equal("aborted", State(h, room, "host", "publish", []).Phase);
    }

    [Fact]
    public async Task UncheckedPlayersUseOrdinaryRoomRulesWithAnOlderMember()
    {
        var (h, room) = await Create(3, hostStandby: false);
        var old = await h.Ops.JoinRoomAsync(Client("old"), room.Code, "old", "old", "1.0.0");
        Assert.True(old.Success);
        Assert.Null(old.StandbySessionId);
        var requested = await h.Ops.JoinRoomAsync(Client("idle"), room.Code, "idle", "idle", "1.0.0",
            acceptsStandby: true, standbyOnly: true);
        Assert.Equal("player_standby_peer_unsupported", requested.Error);
        await h.Ops.ReportWorldJoinedAsync(Client("host"));
        await h.Ops.ReportWorldJoinedAsync(Client("old"));
        var regular = h.Ops.HostWorldOperation(Client("host"), room.Code, room.StandbySessionId!, "start", allowPartial: true, partyCount: 2);
        Assert.Equal("regular", regular.Phase);
        Assert.Null(h.RoomManager.GetRoom(room.Code)!.HostWorld);
        await h.Ops.SetHostRouteListAsync(Client("host"), ["route.json"]);
        Assert.True(h.RoomManager.RecordArrival(room.Code, "classic", "host", expectedCount: 1));
    }

    [Fact]
    public async Task ReconnectCannotChangePersonalStandbyChoice()
    {
        var (h, room) = await Create(2, hostStandby: false);
        await Join(h, room, "idle", standbyOnly: true);
        await Open(h, room, "idle");
        await h.Ops.HandleDisconnectAsync(Client("idle"), null);
        var mismatch = await h.Ops.JoinRoomAsync(Client("new"), room.Code, "idle", "idle", "1.0.0",
            standbySessionId: room.StandbySessionId, acceptsStandby: true, standbyOnly: false);
        Assert.Equal("player_standby_identity_mismatch", mismatch.Error);
        var restored = await h.Ops.JoinRoomAsync(Client("new"), room.Code, "idle", "idle", "1.0.0",
            standbySessionId: room.StandbySessionId, acceptsStandby: true, standbyOnly: true);
        Assert.True(restored.Success);
        Assert.Equal(new[] { "host" }, State(h, room, "new").ExecutorUids);
    }

    private static async Task Open(GatewayTestHarness h, RoomCreation room, params string[] runners)
    {
        await h.Ops.ReportWorldJoinedAsync(Client("host"));
        foreach (var runner in runners) await h.Ops.ReportWorldJoinedAsync(Client(runner));
        Assert.Equal("ready", State(h, room, "host", "start").Phase);
        Assert.Equal("running", State(h, room, "host", "publish", ["route.json"]).Phase);
    }

    private static async Task<(GatewayTestHarness Harness, RoomCreation Room)> Create(int playerCount, bool hostStandby = true)
    {
        var h = new GatewayTestHarness();
        var room = await h.Ops.CreateRoomAsync(Client("host"), "host", playerUid: "host",
            expectedPlayerCount: playerCount, reportedVersion: "1.0.0", negotiateStandby: true, standbyOnly: hostStandby);
        await h.Ops.SetRoomConfigAsync(Client("host"), new RoomConfig { MultiWorldEnabled = false });
        await h.Ops.ReportHostReadyAsync(Client("host"));
        Assert.False(string.IsNullOrEmpty(room.StandbySessionId));
        return (h, room);
    }

    private static async Task Join(GatewayTestHarness h, RoomCreation room, string uid, bool standbyOnly = false)
    {
        var join = await h.Ops.JoinRoomAsync(Client(uid), room.Code, uid, uid, "1.0.0",
            acceptsStandby: true, standbyOnly: standbyOnly);
        Assert.True(join.Success);
        Assert.Equal(room.StandbySessionId, join.StandbySessionId);
    }

    private static HostWorldSnapshot State(GatewayTestHarness h, RoomCreation room, string uid,
        string? action = null, List<string>? routes = null)
        => h.Ops.HostWorldOperation(Client(uid), room.Code, room.StandbySessionId!, action, routes,
            manifestDigest: routes is { Count: > 0 } ? new string('a', 64) : null);
}
