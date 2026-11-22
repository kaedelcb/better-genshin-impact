using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class WhitelistRoundOrderTests
{
    private const string Version = "1.0.0";
    private static GatewayHandlerContext C(string uid) => GatewayHandlerContext.V3(uid);

    [Theory]
    [InlineData(null)]
    [InlineData(" ,， ")]
    [InlineData("未到场，2")]
    public void EmptyOrUnmatchedWhitelistKeepsHostFirstAndSortsTheRest(string? whitelist)
    {
        PlayerInfo[] roster = [Player("9", "房主"), Player("5", "甲"), Player("2", "乙")];
        Assert.Equal(["9", "2", "5"], RoundHostOrderDecisions.Build(roster, "9", whitelist: whitelist));
    }

    [Fact]
    public void MatchesWholeNameWithDunhaoAndBracketAliasesWithoutAddingAbsentOrDuplicateWorlds()
    {
        PlayerInfo[] roster = [Player("9", "茶包"), Player("5", "Dream、渺茫"), Player("2", "大哥"), Player("4", "同名（大哥）"), Player("1", "未匹配")];
        const string whitelist = "未到场，dream、渺茫, 红姐（大哥）， 大哥，茶包, Dream、渺茫";
        Assert.Equal(["5", "2", "4", "9", "1"], RoundHostOrderDecisions.Build(roster, "9", whitelist: whitelist));
        Assert.Equal(["2", "4", "1"], RoundHostOrderDecisions.Build(roster, "9", new HashSet<string> { "5", "9" }, whitelist));
    }

    [Fact]
    public async Task WorldPlanUsesWhitelistBeforeTruncatingAndDoesNotBackfillAnEmptyWorld()
    {
        var (h, _) = await Create(3);
        var a = await h.Ops.BeginWorldPlanAsync(C("A"));
        var b = (await h.Ops.AcquireWorldPlanAsync(C("B")))!;
        var c = (await h.Ops.AcquireWorldPlanAsync(C("C")))!;
        Assert.Equal(["B", "A"], a.Snapshot.CandidateHostUids);
        await h.Ops.ReportWorldPlanAsync(a.PlanId, a.Ticket, true, 4);
        await h.Ops.ReportWorldPlanAsync(a.PlanId, b.Ticket, true, 0);
        await h.Ops.ReportWorldPlanAsync(a.PlanId, c.Ticket, true, 5);
        await h.Ops.AckWorldPlanAsync(a.PlanId, a.Ticket);
        await h.Ops.AckWorldPlanAsync(a.PlanId, b.Ticket);
        var committed = await h.Ops.AckWorldPlanAsync(a.PlanId, c.Ticket);
        Assert.Equal("Committed", committed.Stage);
        Assert.Equal(["A"], committed.RemainingHostUids);
    }

    [Fact]
    public async Task WorldPlanTransfersDirectlyToFirstListedNonHost()
    {
        var (h, source) = await Create(3);
        var a = await h.Ops.BeginWorldPlanAsync(C("A"));
        var b = (await h.Ops.AcquireWorldPlanAsync(C("B")))!;
        var c = (await h.Ops.AcquireWorldPlanAsync(C("C")))!;
        foreach (var admission in new[] { a, b, c })
            await h.Ops.ReportWorldPlanAsync(a.PlanId, admission.Ticket, true, 2);
        await h.Ops.AckWorldPlanAsync(a.PlanId, a.Ticket);
        await h.Ops.AckWorldPlanAsync(a.PlanId, b.Ticket);
        var committed = await h.Ops.AckWorldPlanAsync(a.PlanId, c.Ticket);
        Assert.Equal(["B", "A"], committed.RemainingHostUids);
        Assert.Null(committed.TargetRoomCode);
        await h.Ops.LeaveWorldPlanSourceAsync(C("A"), a.PlanId, a.Ticket);
        await h.Ops.LeaveWorldPlanSourceAsync(C("B"), a.PlanId, b.Ticket);
        await h.Ops.LeaveWorldPlanSourceAsync(C("C"), a.PlanId, c.Ticket);
        Assert.Null(h.RoomManager.GetRoom(source));
        var target = await h.Ops.CreateWorldPlanTargetAsync(C("B"), a.PlanId, b.Ticket);
        Assert.Equal("B", h.RoomManager.GetRoom(target.TargetRoomCode!)?.Players.Single().PlayerUid);
    }

    [Fact]
    public async Task SequentialPathWaitsForEveryFrozenMemberToConfirmSameOrder()
    {
        // This models the automatic partial-party path: actual room is smaller than originally expected.
        var (h, code) = await Create(expected: 4);
        await Mark(h, code);
        var (order, startup) = h.Ops.GetRoundOrderSnapshot(C("A"), code);
        Assert.NotNull(startup);
        Assert.Equal(["B", "A", "C"], order);
        Assert.Equal(2, startup.RoundCount);
        Assert.Equal(new[] { "A", "B", "C" }, startup.ParticipantUids);

        var host = h.Ops.ConfirmRoundOrderAsync(C("A"), code, startup.SessionId, order, 2);
        Assert.False(host.IsCompleted);
        Assert.False(await h.Ops.ConfirmRoundOrderAsync(C("B"), code, startup.SessionId, ["A", "B", "C"], 2));
        Assert.False(await h.Ops.ConfirmRoundOrderAsync(C("B"), code, "old-session", order, 2));
        Assert.False(await h.Ops.ConfirmRoundOrderAsync(C("C"), code, startup.SessionId, order, 3));
        Assert.False(await h.Ops.ConfirmRoundOrderAsync(C("outsider"), code, startup.SessionId, order, 2));
        var member = h.Ops.ConfirmRoundOrderAsync(C("B"), code, startup.SessionId, order, 2);
        Assert.False(host.IsCompleted);
        Assert.False(member.IsCompleted);
        Assert.True(await h.Ops.ConfirmRoundOrderAsync(C("C"), code, startup.SessionId, order, 2));
        Assert.True(await host);
        Assert.True(await member);
    }

    [Fact]
    public async Task AbortedMemberCannotReleaseAlreadyWaitingPlayers()
    {
        var (h, code) = await Create(3);
        await Mark(h, code);
        var (order, startup) = h.Ops.GetRoundOrderSnapshot(C("A"), code);
        var host = h.Ops.ConfirmRoundOrderAsync(C("A"), code, startup!.SessionId, order, 2);
        var member = h.Ops.ConfirmRoundOrderAsync(C("B"), code, startup.SessionId, order, 2);
        await h.Ops.ReportCoordinatedAbortAsync(C("C"), "未能确认顺序", "C");
        Assert.False(await host);
        Assert.False(await member);
        Assert.False(await h.Ops.ConfirmRoundOrderAsync(C("C"), code, startup.SessionId, order, 2));
    }

    [Fact]
    public async Task FrozenRosterDoesNotShrinkAfterMemberLeaves()
    {
        var (h, code) = await Create(3);
        await Mark(h, code);
        var (order, startup) = h.Ops.GetRoundOrderSnapshot(C("A"), code);
        var host = h.Ops.ConfirmRoundOrderAsync(C("A"), code, startup!.SessionId, order, 2);
        Assert.False(host.IsCompleted);
        await h.Ops.LeaveRoomAsync(C("C"));
        Assert.False(await host);
        Assert.False(await h.Ops.ConfirmRoundOrderAsync(C("B"), code, startup.SessionId, order, 2));
    }

    [Fact]
    public async Task GatewayDoesNotReleaseAnOrderAfterTheSharedDeadline()
    {
        var clock = new StartupClock();
        var (h, code) = await Create(3, clock);
        await Mark(h, code);
        foreach (var uid in new[] { "A", "B", "C" })
            await h.Dispatcher.DispatchAsync(C(uid), GatewayTestHarness.HelloEnvelope());
        var reply = await h.Dispatcher.QueryAsync(C("A"), new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Query, Name = GatewayProtocol.Names.RoomGetRoundHostOrder, RoomCode = code,
        });
        Assert.Null(GatewayTestHarness.ErrorCode(reply));
        var startup = reply.Payload!["startup"]!;
        var request = new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Command, Name = GatewayProtocol.Names.RoomConfirmRoundOrder, RoomCode = code,
            Payload = GatewayEnvelope.ToPayload(new
            {
                sessionId = startup["sessionId"]!.GetValue<string>(), order = new[] { "B", "A", "C" }, roundCount = 2,
            }),
        };
        var host = h.Dispatcher.DispatchAsync(C("A"), request);
        Assert.False(host.IsCompleted);
        clock.Now = clock.Now.AddSeconds(31);
        var late = await h.Dispatcher.DispatchAsync(C("B"), request);
        Assert.Null(GatewayTestHarness.ErrorCode(late));
        Assert.False(late.Payload!["ready"]!.GetValue<bool>());
        Assert.False((await host).Payload!["ready"]!.GetValue<bool>());
    }

    private sealed class StartupClock : TimeProvider
    {
        public DateTimeOffset Now { get; set; } = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => Now;
    }

    private static PlayerInfo Player(string uid, string name) => new() { PlayerUid = uid, PlayerName = name };

    private static async Task<(GatewayTestHarness H, string Code)> Create(int expected, TimeProvider? clock = null)
    {
        var h = new GatewayTestHarness(clock);
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "房主", playerUid: "A", expectedPlayerCount: expected, reportedVersion: Version);
        await h.Ops.SetRoomConfigAsync(C("A"), new RoomConfig { MultiWorldEnabled = true, MultiWorldCount = 2, RoomWhitelist = "Dream、渺茫，未到场,房主" });
        Assert.True((await h.Ops.JoinRoomAsync(C("B"), code, "Dream、渺茫", "B", Version)).Success);
        Assert.True((await h.Ops.JoinRoomAsync(C("C"), code, "其他成员", "C", Version)).Success);
        return (h, code);
    }

    private static async Task Mark(GatewayTestHarness h, string code)
    {
        await h.Ops.ReportHostReadyAsync(C("A"));
        foreach (var uid in new[] { "A", "B", "C" }) await h.Ops.ReportWorldJoinedAsync(C(uid));
        await h.Ops.MarkRoomStartedAsync(C("A"), null, expectedRoomCode: code, requireRoundOrderStartup: true);
    }
}
