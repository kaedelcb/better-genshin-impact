using System.Text.Json;
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
        Assert.Equal("host_world_owner_not_standby", Assert.Throws<InvalidOperationException>(() =>
            h.Ops.HostWorldOperation(Client("host"), room.Code, room.StandbySessionId!, "routeDone",
                routeName: "route.json", durationSeconds: 5, manifestDigest: state.ManifestDigest)).Message);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RoundEndWaitsForThePhysicalPartyBeforeAndAfterRouteExecution(bool publishRoutes)
    {
        var (h, admission) = await Create(3);
        await h.Ops.SetRoomConfigAsync(Client("host"), new RoomConfig { MultiWorldEnabled = true });
        await Join(h, admission, "runner");
        await Join(h, admission, "idle", standbyOnly: true);
        foreach (var uid in new[] { "host", "runner", "idle" })
            await h.Ops.ReportWorldJoinedAsync(Client(uid));
        Assert.Equal("ready", State(h, admission, "host", "start").Phase);
        var room = h.RoomManager.GetRoom(admission.Code)!;
        Assert.False(room.IsStarted);
        Assert.Throws<InvalidOperationException>(() => State(h, admission, "host", "publish", ["route.json"]));
        await h.Ops.MarkRoomStartedAsync(Client("host"), null);

        await h.Ops.ReportArrivalWithExpectedCountAsync(Client("runner"), "round_end_-1", 1);
        await h.Ops.ReportArrivalAsync(Client("host"), "round_end_-1");
        Assert.DoesNotContain("round_end_-1", room.BroadcastedSyncIds);
        await h.Ops.ReportArrivalAsync(Client("idle"), "round_end_-1");
        Assert.Contains("round_end_-1", room.BroadcastedSyncIds);

        var published = State(h, admission, "host", "publish", publishRoutes ? ["route.json"] : []);
        if (publishRoutes)
        {
            foreach (var uid in new[] { "host", "runner", "idle" })
                await h.Ops.ReportArrivalAsync(Client(uid), "round_end_0");
            Assert.DoesNotContain("round_end_0", room.BroadcastedSyncIds);
        }
        var terminal = publishRoutes ? State(h, admission, "runner", "finish") : published;
        foreach (var uid in new[] { "host", "runner", "idle" })
            await h.Ops.ReportArrivalAsync(Client(uid), "round_end_3");
        Assert.DoesNotContain("round_end_3", room.BroadcastedSyncIds);
        Assert.Equal(publishRoutes ? "completed" : "noRoutes", terminal.Phase);
        await h.Ops.HandleDisconnectAsync(Client("idle"), null);
        Assert.True((await h.Ops.JoinRoomAsync(Client("idle-reconnected"), admission.Code, "idle", "idle", "1.0.0",
            standbySessionId: admission.StandbySessionId, acceptsStandby: true, standbyOnly: true)).Success);
        await h.Ops.WaitForAllPlayersAsync(Client("runner"), "round_end_0");
        await h.Ops.WaitForAllPlayersAsync(Client("host"), "round_end_0");
        Assert.DoesNotContain("round_end_0", room.BroadcastedSyncIds);
        await h.Ops.WaitForAllPlayersAsync(Client("idle-reconnected"), "round_end_0", 100);
        Assert.Contains("round_end_0", room.BroadcastedSyncIds);
        Assert.Empty(room.ArrivalSets);
        Assert.All(room.Players, player => Assert.Equal(-1L, player.CurrentProgress));
        Assert.Null(State(h, admission, "host").RouteCompletions);
    }

    [Fact]
    public async Task RegularNegotiationStillUsesTheOrdinaryWhitelistStartupGate()
    {
        var (h, admission) = await Create(3, hostStandby: false);
        var config = new RoomConfig { MultiWorldEnabled = true, MultiWorldCount = 3, RoomWhitelist = "second,host,old" };
        await h.Ops.SetRoomConfigAsync(Client("host"), config);
        await Join(h, admission, "second");
        Assert.True((await h.Ops.JoinRoomAsync(Client("old"), admission.Code, "old", "old", "1.0.0")).Success);
        await h.Ops.ResetForNewWorldRoundAsync(Client("host"), 1);
        foreach (var uid in new[] { "host", "second", "old" })
            await h.Ops.ReportWorldJoinedAsync(Client(uid));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.MarkRoomStartedAsync(Client("host"), null,
            expectedRoomCode: admission.Code, requireRoundOrderStartup: true));
        Assert.Equal("regular", State(h, admission, "host", "start").Phase);
        var room = h.RoomManager.GetRoom(admission.Code)!;
        Assert.False(room.IsStarted);
        Assert.Null(room.HostWorld);

        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.ResetWorldJoinedAsync(Client("host")));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.ResetForNewWorldRoundAsync(Client("host"), 2));
        await h.Ops.MarkRoomStartedAsync(Client("host"), null, expectedRoomCode: room.Code, requireRoundOrderStartup: true);
        var order = new[] { "second", "host", "old" };
        Assert.Equal(order, room.RoundHostOrder);
        var confirmations = order.Select(uid => h.Ops.ConfirmRoundOrderAsync(Client(uid), room.Code, room.SessionId, order, 3));
        Assert.All(await Task.WhenAll(confirmations), ready => Assert.True(ready));
        await h.Ops.SetHostRouteListAsync(Client("host"), ["route.json"]);
        Assert.True(h.RoomManager.RecordArrival(room.Code, "ordinary", "host", expectedCount: 1));
    }

    [Fact]
    public async Task RouteReceiptRequiresAnExecutorAndExactPublishedRouteAndSurvivesAbort()
    {
        var (h, room) = await Create(4);
        await Join(h, room, "runner");
        await Join(h, room, "other-runner");
        await Join(h, room, "idle", standbyOnly: true);
        foreach (var uid in new[] { "host", "runner", "other-runner", "idle" })
            await h.Ops.ReportWorldJoinedAsync(Client(uid));
        Assert.Equal("ready", State(h, room, "host", "start").Phase);
        Assert.Equal("running", State(h, room, "host", "publish", ["route.json", "pending.json"]).Phase);
        await h.Ops.WaitForAllPlayersAsync(Client("runner"), "route_sync_done", -1);
        await h.Ops.WaitForAllPlayersAsync(Client("other-runner"), "route_sync_done", -1);
        Assert.Contains("route_sync_done", h.RoomManager.GetRoom(room.Code)!.BroadcastedSyncIds);
        await h.Ops.HeartbeatWithProgressAsync(Client("runner"), 0, DateTime.UtcNow, 30);
        Assert.Null(State(h, room, "host").RouteCompletions);
        var digest = new string('a', 64);
        HostWorldSnapshot RouteDone(string uid, string route = "route.json", double duration = 12.5, string? manifest = null) =>
            h.Ops.HostWorldOperation(Client(uid), room.Code, room.StandbySessionId!, "routeDone",
                routeName: route, durationSeconds: duration, manifestDigest: manifest ?? digest);
        Assert.Throws<InvalidOperationException>(() => RouteDone("host"));
        Assert.Throws<InvalidOperationException>(() => RouteDone("idle"));
        Assert.Throws<InvalidOperationException>(() => RouteDone("other"));
        Assert.Throws<InvalidOperationException>(() => RouteDone("runner", "Route.json"));
        Assert.Throws<InvalidOperationException>(() => RouteDone("runner", manifest: new string('b', 64)));
        Assert.Throws<InvalidOperationException>(() => RouteDone("runner", duration: double.NaN));
        Assert.Throws<InvalidOperationException>(() => RouteDone("runner", duration: 0));

        await h.Dispatcher.DispatchAsync(Client("runner"), GatewayTestHarness.HelloEnvelope());
        var response = await h.Dispatcher.DispatchAsync(Client("runner"), new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Command,
            Name = GatewayProtocol.Names.RoomHostWorldUpdate,
            RoomCode = room.Code,
            Payload = GatewayEnvelope.ToPayload(new
            {
                action = "routeDone", sessionId = room.StandbySessionId, routeName = "route.json",
                durationSeconds = double.MaxValue, manifestDigest = digest,
            }),
        });
        Assert.Null(GatewayTestHarness.ErrorCode(response));
        var receipt = Assert.Single(response.Payload!["state"]!.Deserialize<HostWorldSnapshot>(GatewayJson.Options)!.RouteCompletions!);
        Assert.Equal("route.json", receipt.RouteName);
        Assert.Equal(TimeSpan.FromHours(6).TotalSeconds, receipt.DurationSeconds);
        Assert.Equal(DateTimeKind.Utc, receipt.CompletedAtUtc.Kind);
        Assert.Equal(receipt, Assert.Single(RouteDone("runner").RouteCompletions!));
        Assert.Equal(receipt, Assert.Single(RouteDone("other-runner", duration: 23).RouteCompletions!));

        await h.Ops.CloseRoomAsync(Client("host"));
        var aborted = State(h, room, "host");
        Assert.Equal("aborted", aborted.Phase);
        Assert.Equal(receipt, Assert.Single(aborted.RouteCompletions!));
        Assert.Equal(receipt, Assert.Single(RouteDone("runner").RouteCompletions!));
        Assert.Throws<InvalidOperationException>(() => RouteDone("runner", "pending.json"));

        var next = await h.Ops.CreateRoomAsync(Client("host"), "host", playerUid: "host", expectedPlayerCount: 3,
            reportedVersion: "1.0.0", negotiateStandby: true, standbyOnly: true);
        Assert.Null(State(h, next, "host").RouteCompletions);
        Assert.Throws<InvalidOperationException>(() => h.Ops.HostWorldOperation(Client("runner"), next.Code,
            room.StandbySessionId!, "routeDone", routeName: "route.json", durationSeconds: 12.5, manifestDigest: digest));
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
