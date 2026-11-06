using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class WorldPlanGatewayTests
{
    private const string Version = "1.0.0";
    private static GatewayHandlerContext C(string uid) => GatewayHandlerContext.V3(uid);

    private static async Task<(GatewayTestHarness H, string Code, WorldPlanAdmission[] Admissions)> Start(
        int rounds = 4, bool mixed = false, TimeProvider? clock = null, bool fixedRoster = false)
    {
        var h = new GatewayTestHarness(clock);
        var batch = fixedRoster ? "world-plan-test-batch" : null;
        var participants = fixedRoster ? new List<string> { "A", "B", "C", "D" } : null;
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "A", playerUid: "A", expectedPlayerCount: 4,
            reportedVersion: Version, hoeingBatchId: batch, expectedParticipantUids: participants);
        await h.Ops.SetRoomConfigAsync(C("A"), new RoomConfig
        {
            HoeingBatchId = batch, ExpectedParticipantUids = participants ?? [],
            MultiWorldEnabled = true, MultiWorldResumeEnabled = true, MultiWorldCount = rounds,
            SelectedBuiltinRoute = "原房主线路", RouteFilterKeywords = "排除", PartyTimeoutSeconds = 60,
        });
        foreach (var uid in new[] { "B", "C", "D" })
            Assert.True((await h.Ops.JoinRoomAsync(C(uid), code, uid, uid, reportedVersion: Version, hoeingBatchId: batch)).Success);
        if (mixed) h.Capabilities.UnsupportedCapabilities.Add(("D", WorldPlanState.Capability));
        var admissions = new List<WorldPlanAdmission> { await h.Ops.BeginWorldPlanAsync(C("A")) };
        foreach (var uid in new[] { "B", "C", "D" }) admissions.Add((await h.Ops.AcquireWorldPlanAsync(C(uid)))!);
        return (h, code, admissions.ToArray());
    }

    private static async Task<WorldPlanSnapshot> Commit(GatewayTestHarness h, WorldPlanAdmission[] clients, params int[] counts)
    {
        WorldPlanSnapshot s = clients[0].Snapshot;
        for (var i = 0; i < clients.Length; i++)
            s = await h.Ops.ReportWorldPlanAsync(clients[i].PlanId, clients[i].Ticket, counts[i] >= 0, Math.Max(0, counts[i]));
        Assert.Equal("Prepared", s.Stage);
        for (var i = 0; i < clients.Length; i++)
        {
            s = await h.Ops.AckWorldPlanAsync(clients[i].PlanId, clients[i].Ticket);
            if (i < clients.Length - 1) Assert.Equal("Prepared", s.Stage);
        }
        return s;
    }

    private static async Task Release(GatewayTestHarness h, WorldPlanAdmission[] clients)
    {
        for (var i = 0; i < clients.Length; i++)
        {
            var s = await h.Ops.LeaveWorldPlanSourceAsync(C(new[] { "A", "B", "C", "D" }[i]), clients[i].PlanId, clients[i].Ticket);
            Assert.Equal(i == clients.Length - 1, s.SourceReleased);
        }
    }

    [Fact]
    public async Task FourMembersSkipAB_KeepAllParticipants_AndTargetCReusesOriginalConfig()
    {
        var (h, old, a) = await Start();
        var plan = await Commit(h, a, 0, 0, 2, 5);
        Assert.Equal("Committed", plan.Stage);
        Assert.Equal(new[] { "C", "D" }, plan.RemainingHostUids);
        Assert.Equal(new[] { "A", "B", "C", "D" }, plan.ParticipantUids);
        await Release(h, a);
        Assert.Null(h.RoomManager.GetRoom(old));
        var target = await h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket);
        var room = h.RoomManager.GetRoom(target.TargetRoomCode!)!;
        Assert.Equal("C", Assert.Single(room.Players).PlayerUid);
        Assert.Equal(4, room.ExpectedPlayerCount);
        Assert.Equal("原房主线路", room.HostConfig!.SelectedBuiltinRoute);
        Assert.Equal("排除", room.HostConfig.RouteFilterKeywords);
        foreach (var uid in new[] { "A", "B", "D" })
            Assert.True((await h.Ops.JoinRoomAsync(C(uid), room.Code, uid, uid, reportedVersion: Version)).Success);
        Assert.Equal(4, room.Players.Count);
        Assert.False(room.HostReady);
        Assert.Equal(0, h.RoomManager.GetWorldJoinedCount(room.Code));
    }

    [Fact]
    public async Task LostTargetResponse_RetryReturnsSameRoom_AndLateSourceLeaveCannotDeleteIt()
    {
        var (h, _, a) = await Start();
        await Commit(h, a, 0, 0, 2, 5);
        await Release(h, a);
        h.GatewayGroups.Setup(g => g.AddToGroupAsync("C", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new IOException("模拟创建成功后的组订阅回包丢失"));
        await Assert.ThrowsAsync<IOException>(() => h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket));
        var allocated = await h.Ops.GetWorldPlanAsync(a[2].PlanId, a[2].Ticket);
        Assert.NotNull(allocated.TargetRoomCode);
        h.GatewayGroups.Setup(g => g.AddToGroupAsync("C", It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(Task.CompletedTask);
        var retry = await h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket);
        Assert.Equal(allocated.TargetRoomCode, retry.TargetRoomCode);
        Assert.Equal(allocated.TargetRoomSessionId, retry.TargetRoomSessionId);
        Assert.Single(h.RoomManager.GetOnlineRooms());
        await h.Ops.LeaveWorldPlanSourceAsync(C("C"), a[2].PlanId, a[2].Ticket);
        Assert.Equal(retry.TargetRoomCode, h.RoomManager.GetRoomByConnectionId("C").RoomCode);
    }

    [Fact]
    public async Task MemberCanRecoverPlanAfterSourceGone_OnlyWithItsPrivateTicket()
    {
        var (h, _, a) = await Start();
        await Commit(h, a, 0, 0, 2, 5);
        await Release(h, a);
        var recovered = await h.Ops.GetWorldPlanAsync(a[1].PlanId, a[1].Ticket);
        Assert.Equal("Committed", recovered.Stage);
        Assert.True(recovered.SourceReleased);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.GetWorldPlanAsync(a[1].PlanId, "B"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.CreateWorldPlanTargetAsync(C("B"), a[1].PlanId, a[1].Ticket));
    }

    [Fact]
    public async Task TwoRoundPlanDoesNotFillSkippedWorldsWithCD_AndNoWorkIsExplicit()
    {
        var (h, _, a) = await Start(2);
        var plan = await Commit(h, a, 0, 0, 20, 20);
        Assert.Equal("NoWork", plan.Stage);
        Assert.Equal(new[] { "A", "B" }, plan.CandidateHostUids);
        Assert.Empty(plan.RemainingHostUids);
        await Release(h, a);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket));
    }

    [Fact]
    public async Task UnknownReportChoosesOneLegacyResult_NotEmptySuccess()
    {
        var (h, _, a) = await Start();
        var plan = await Commit(h, a, 0, -1, 0, 0);
        Assert.Equal("Legacy", plan.Stage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket));
    }

    [Fact]
    public async Task MixedCapabilityReturnsLegacyWithoutWaitingForOldClientAcknowledgement()
    {
        var (_, _, a) = await Start(mixed: true);
        Assert.All(a, x => Assert.Equal("Legacy", x.Snapshot.Stage));
    }

    [Fact]
    public async Task StopAfterCommitCannotBeRevivedByLateAckOrTargetCreation()
    {
        var (h, _, a) = await Start();
        await Commit(h, a, 0, 0, 2, 5);
        await h.Ops.AbortWorldPlanAsync(a[1].PlanId, a[1].Ticket);
        Assert.Equal("Aborted", (await h.Ops.AckWorldPlanAsync(a[0].PlanId, a[0].Ticket)).Stage);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket));
    }

    [Fact]
    public async Task NoSourceReleaseUntilAllClientsHaveInstalledTransferProtection()
    {
        var (h, old, a) = await Start();
        await Commit(h, a, 0, 0, 2, 5);
        await h.Ops.LeaveWorldPlanSourceAsync(C("A"), a[0].PlanId, a[0].Ticket);
        Assert.NotNull(h.RoomManager.GetRoom(old));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket));
        Assert.False((await h.Ops.GetWorldPlanAsync(a[1].PlanId, a[1].Ticket)).SourceReleased);
    }

    [Fact]
    public async Task FirstTargetAReusesSourceRoom_AndBeginRetryKeepsPlanIdentity()
    {
        var (h, old, a) = await Start();
        Assert.Equal(a[0].PlanId, (await h.Ops.BeginWorldPlanAsync(C("A"))).PlanId);
        var plan = await Commit(h, a, 1, 0, 2, 0);
        Assert.Equal(old, plan.TargetRoomCode);
        var again = await h.Ops.CreateWorldPlanTargetAsync(C("A"), a[0].PlanId, a[0].Ticket);
        Assert.Equal(old, again.TargetRoomCode);
        Assert.Equal(4, h.RoomManager.GetRoom(old)!.Players.Count);
        Assert.Empty(h.RoomManager.GetOnlineRooms()); // Full rooms are intentionally absent from discovery.
    }

    [Fact]
    public void ExpirationStopsPlan_AndSnapshotsCannotMutateFrozenConfig()
    {
        var now = DateTime.UtcNow;
        var room = new Room { Code = "one", SessionId = "instance", HostConnectionId = "a", HostConfig = new() { MultiWorldCount = 1 },
            Players = [new() { ConnectionId = "a", PlayerUid = "A" }] };
        var state = new WorldPlanState(room, true, now);
        var a = state.Acquire("A", now);
        a.Snapshot.HostConfig.MultiWorldCount = 4;
        a.Snapshot.CandidateHostUids.Clear();
        state.Report(a.Ticket, true, 1, now);
        Assert.Equal("Committed", state.Ack(a.Ticket, now).Stage);
        Assert.Equal(new[] { "A" }, state.Snapshot(now).CandidateHostUids);
        Assert.Equal(1, state.Snapshot(now).HostConfig.MultiWorldCount);
        Assert.Equal("Aborted", state.Snapshot(state.ExpiresAtUtc).Stage);
        Assert.Throws<InvalidOperationException>(() => state.RequireTarget(a.Ticket, state.ExpiresAtUtc));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    public async Task MissingOrDuplicateUidUsesOneLegacyDecision(string uid)
    {
        var h = new GatewayTestHarness();
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "A", playerUid: "A", expectedPlayerCount: 2, reportedVersion: Version);
        await h.Ops.SetRoomConfigAsync(C("A"), new RoomConfig { MultiWorldEnabled = true });
        Assert.True((await h.Ops.JoinRoomAsync(C("B"), code, "B", uid, reportedVersion: Version)).Success);
        Assert.Equal("Legacy", (await h.Ops.BeginWorldPlanAsync(C("A"))).Snapshot.Stage);
        Assert.Equal("Legacy", (await h.Ops.AcquireWorldPlanAsync(C("B")))!.Snapshot.Stage);
    }

    [Fact]
    public async Task ExternalSourceClosureBeforeProtectionBarrierAbortsTransfer()
    {
        var (h, old, a) = await Start();
        await Commit(h, a, 0, 0, 2, 5);
        h.RoomManager.DeleteRoom(old);
        foreach (var admission in a.Take(3))
            await h.Ops.LeaveWorldPlanSourceAsync(C("unused"), admission.PlanId, admission.Ticket);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.LeaveWorldPlanSourceAsync(C("D"), a[3].PlanId, a[3].Ticket));
        Assert.Equal("Aborted", (await h.Ops.GetWorldPlanAsync(a[0].PlanId, a[0].Ticket)).Stage);
    }

    [Fact]
    public async Task OldHostNewMemberAcquireLegacyWithoutBegin()
    {
        var h = new GatewayTestHarness();
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "A", playerUid: "A", expectedPlayerCount: 4, reportedVersion: Version);
        await h.Ops.SetRoomConfigAsync(C("A"), new RoomConfig { MultiWorldEnabled = true });
        h.Capabilities.UnsupportedCapabilities.Add(("A", WorldPlanState.Capability));
        Assert.True((await h.Ops.JoinRoomAsync(C("B"), code, "B", "B", reportedVersion: Version)).Success);
        var b = (await h.Ops.AcquireWorldPlanAsync(C("B")))!;
        Assert.Equal("Legacy", b.Snapshot.Stage);
        Assert.True((await h.Ops.JoinRoomAsync(C("C"), code, "C", "C", reportedVersion: Version)).Success);
        var c = (await h.Ops.AcquireWorldPlanAsync(C("C")))!;
        Assert.Equal("Legacy", c.Snapshot.Stage);
        Assert.Equal(b.PlanId, c.PlanId);
        Assert.True((await h.Ops.JoinRoomAsync(C("D"), code, "D", "D", reportedVersion: Version)).Success);
        var d = (await h.Ops.AcquireWorldPlanAsync(C("D")))!;
        Assert.Equal("Legacy", d.Snapshot.Stage);
        Assert.Equal(b.PlanId, d.PlanId);
        Assert.Equal(4, h.RoomManager.GetRoom(code)!.Players.Count);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.GetWorldPlanAsync(d.PlanId, "D"));
    }

    [Fact]
    public async Task CurrentPartyLegacyDecisionAllowsLateOrdinaryMembersWithoutExpandingExecutablePlan()
    {
        var h = new GatewayTestHarness();
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "A", playerUid: "A", expectedPlayerCount: 4, reportedVersion: Version);
        await h.Ops.SetRoomConfigAsync(C("A"), new RoomConfig { MultiWorldEnabled = true });
        var a = await h.Ops.BeginWorldPlanAsync(C("A"), forceLegacy: true);
        Assert.Equal("Legacy", a.Snapshot.Stage);
        foreach (var uid in new[] { "B", "C", "D" })
        {
            Assert.True((await h.Ops.JoinRoomAsync(C(uid), code, uid, uid, reportedVersion: Version)).Success);
            var member = (await h.Ops.AcquireWorldPlanAsync(C(uid)))!;
            Assert.Equal(a.PlanId, member.PlanId);
            Assert.Equal("Legacy", member.Snapshot.Stage);
            await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.CreateWorldPlanTargetAsync(C(uid), member.PlanId, member.Ticket));
        }
        Assert.Single((await h.Ops.GetWorldPlanAsync(a.PlanId, a.Ticket)).ParticipantUids);
    }

    [Fact]
    public async Task AbortRevokesRegisteredC_WhilePreservingSuccessorDRoomAndMappings()
    {
        var (h, _, a) = await Start();
        await Commit(h, a, 0, 0, 2, 5);
        await Release(h, a);
        var target = await h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket);
        Assert.True((await h.Ops.JoinRoomAsync(C("B"), target.TargetRoomCode!, "B", "B", reportedVersion: Version)).Success);
        Assert.Equal(target.TargetRoomCode,
            (await h.Ops.ValidateWorldPlanTargetAsync(C("B"), a[1].PlanId, a[1].Ticket)).TargetRoomCode);
        var (next, _) = await h.Ops.CreateRoomAsync(C("D"), "D", playerUid: "D", expectedPlayerCount: 4, reportedVersion: Version);
        Assert.True((await h.Ops.JoinRoomAsync(C("B"), next, "B", "B", reportedVersion: Version)).Success);
        await h.Ops.AbortWorldPlanAsync(a[0].PlanId, a[0].Ticket);
        Assert.Null(h.RoomManager.GetRoom(target.TargetRoomCode!));
        Assert.NotNull(h.RoomManager.GetRoom(next));
        Assert.Equal(next, h.RoomManager.GetRoomByConnectionId("B").RoomCode);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.ValidateWorldPlanTargetAsync(C("B"), a[1].PlanId, a[1].Ticket));
        await h.Ops.AbortWorldPlanAsync(a[0].PlanId, a[0].Ticket);
        Assert.NotNull(h.RoomManager.GetRoom(next));
    }

    [Fact]
    public async Task ValidateTargetRejectsWrongRoomWrongMemberAndReusedRoomInstance()
    {
        var (h, _, a) = await Start();
        await Commit(h, a, 0, 0, 2, 5);
        await Release(h, a);
        var target = await h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.ValidateWorldPlanTargetAsync(C("B"), a[1].PlanId, a[1].Ticket));
        Assert.True((await h.Ops.JoinRoomAsync(C("D"), target.TargetRoomCode!, "D", "D", reportedVersion: Version)).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.ValidateWorldPlanTargetAsync(C("D"), a[1].PlanId, a[1].Ticket));
        var room = h.RoomManager.GetRoom(target.TargetRoomCode!)!;
        Assert.Equal("Committed", (await h.Ops.ValidateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket)).Stage);
        room.SessionId = "后来的同房码实例";
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.ValidateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket));
        await h.Ops.AbortWorldPlanAsync(a[0].PlanId, a[0].Ticket);
        Assert.Same(room, h.RoomManager.GetRoom(target.TargetRoomCode!));
    }

    [Fact]
    public async Task FrozenTwoMemberSourceRejectsLateNewUid_ButKeepsReconnectAndTruthfulWorldReadiness()
    {
        var h = new GatewayTestHarness();
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "A", playerUid: "A", expectedPlayerCount: 2, reportedVersion: Version);
        await h.Ops.SetRoomConfigAsync(C("A"), new RoomConfig { MultiWorldEnabled = true });
        Assert.True((await h.Ops.JoinRoomAsync(C("B"), code, "B", "B", reportedVersion: Version)).Success);
        var a = await h.Ops.BeginWorldPlanAsync(C("A"));
        var room = h.RoomManager.GetRoom(code)!;
        Assert.Equal("Planning", a.Snapshot.Stage);
        Assert.False(room.IsStarted);
        Assert.Equal(0, h.RoomManager.GetWorldJoinedCount(code));
        Assert.False((await h.Ops.JoinRoomAsync(C("C"), code, "C", "C", reportedVersion: Version)).Success);
        Assert.False((await h.Ops.JoinRoomAsync(C("duplicate-A"), code, "另一个A", "A", reportedVersion: Version)).Success);
        room.GracePendingMembers["B"] = DateTime.UtcNow.AddMinutes(1);
        Assert.True((await h.Ops.JoinRoomAsync(C("reconnected-B"), code, "B", "B", reportedVersion: Version)).Success);
        var b = (await h.Ops.AcquireWorldPlanAsync(C("reconnected-B")))!;
        Assert.Equal(a.PlanId, b.PlanId);
        Assert.Equal(2, room.Players.Count);
        Assert.Equal(new[] { "A", "B" }, room.WorldPlanParticipantUids);
        var legacy = await Commit(h, new[] { a, b }, 0, -1);
        Assert.Equal("Legacy", legacy.Stage);
        Assert.Null(room.WorldPlanParticipantUids);
        // The fallback removes the new freeze but preserves the original room-capacity rule.
        room.ExpectedPlayerCount = 3;
        Assert.True((await h.Ops.JoinRoomAsync(C("C"), code, "C", "C", reportedVersion: Version)).Success);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task FrozenRosterCannotStartWithOnlyABC_AndDRejoinStillMustReportActualWorldArrival(bool fixedRoster, bool grace)
    {
        var (h, _, a) = await Start(fixedRoster: fixedRoster);
        await Commit(h, a, 0, 0, 2, 5);
        await Release(h, a);
        var target = await h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket);
        var room = h.RoomManager.GetRoom(target.TargetRoomCode!)!;
        foreach (var uid in new[] { "A", "B" })
            Assert.True((await h.Ops.JoinRoomAsync(C(uid), room.Code, uid, uid, Version, room.HoeingBatchId)).Success);
        if (grace)
        {
            Assert.True((await h.Ops.JoinRoomAsync(C("D"), room.Code, "D", "D", Version, room.HoeingBatchId)).Success);
            room.GracePendingMembers["D"] = DateTime.UtcNow.AddSeconds(30);
        }
        await h.Ops.ReportHostReadyAsync(C("C"));
        foreach (var uid in new[] { "A", "B", "C" })
            Assert.False(h.RoomManager.RecordWorldJoined(room.Code, uid));
        Assert.Equal(3, h.RoomManager.GetWorldJoinedCount(room.Code));
        var incomplete = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.MarkRoomStartedAsync(C("C"), null));
        Assert.Equal("world_plan_roster_not_ready", incomplete.Message);
        Assert.False(room.IsStarted);
        Assert.Empty(room.RoundHostOrder);
        var dConnection = grace ? "reconnected-D" : "D";
        Assert.True((await h.Ops.JoinRoomAsync(C(dConnection), room.Code, "D", "D", Version, room.HoeingBatchId)).Success);
        Assert.Equal(3, h.RoomManager.GetWorldJoinedCount(room.Code));
        Assert.False(room.WorldJoinedSet.Contains(dConnection));
        Assert.True(h.RoomManager.RecordWorldJoined(room.Code, dConnection));
        await h.Ops.MarkRoomStartedAsync(C("C"), null);
        Assert.True(room.IsStarted);
    }

    [Fact]
    public async Task FrozenRosterNetworkReconnectPreservesOnlyPriorConfirmedWorldAdmission()
    {
        var (h, _, a) = await Start();
        await Commit(h, a, 0, 0, 2, 5);
        await Release(h, a);
        var target = await h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket);
        var room = h.RoomManager.GetRoom(target.TargetRoomCode!)!;
        foreach (var uid in new[] { "A", "B", "D" })
            Assert.True((await h.Ops.JoinRoomAsync(C(uid), room.Code, uid, uid, Version)).Success);
        foreach (var uid in new[] { "A", "B", "C", "D" }) h.RoomManager.RecordWorldJoined(room.Code, uid);
        room.GracePendingMembers["D"] = DateTime.UtcNow.AddSeconds(30);
        Assert.False(h.RoomManager.RecordWorldJoined(room.Code, "C"));
        Assert.True((await h.Ops.JoinRoomAsync(C("D-returned"), room.Code, "D", "D", Version)).Success);
        Assert.DoesNotContain("D", room.WorldJoinedSet);
        Assert.Contains("D-returned", room.WorldJoinedSet);
        Assert.Equal(4, h.RoomManager.GetWorldJoinedCount(room.Code));
        Assert.True(h.RoomManager.RecordWorldJoined(room.Code, "C"));
        var notReady = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.MarkRoomStartedAsync(C("C"), null));
        Assert.Equal("world_plan_roster_not_ready", notReady.Message); // HostReady is independent of world admission.
        Assert.False(room.IsStarted);
        await h.Ops.ReportHostReadyAsync(C("C"));
        await h.Ops.MarkRoomStartedAsync(C("C"), null);
        Assert.True(room.IsStarted);
    }

    private sealed class PlanClock : TimeProvider
    {
        private DateTimeOffset _now = DateTimeOffset.UtcNow;
        public override DateTimeOffset GetUtcNow() => _now;
        public void Advance(TimeSpan elapsed) => _now += elapsed;
    }

    [Fact]
    public async Task ExpiredStartupTicketCannotAbortAnUnstartedRegisteredTarget()
    {
        var clock = new PlanClock();
        var (h, _, a) = await Start(clock: clock);
        await Commit(h, a, 0, 0, 2, 5);
        await Release(h, a);
        var target = await h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket);
        var room = h.RoomManager.GetRoom(target.TargetRoomCode!)!;
        clock.Advance(TimeSpan.FromMinutes(20));
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.AbortWorldPlanAsync(a[1].PlanId, a[1].Ticket));
        Assert.Equal("world_plan_ticket_expired", error.Message);
        Assert.Same(room, h.RoomManager.GetRoom(room.Code));
        Assert.False(room.IsStarted);
        await h.Ops.CloseRoomAsync(C("C"));
        Assert.Null(h.RoomManager.GetRoom(room.Code));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RealStartRetiresTicketAbort_ForDirectAAndTransferredC_OrdinaryStopStillWorks(bool directA)
    {
        var clock = new PlanClock();
        var (h, source, a) = await Start(clock: clock);
        await Commit(h, a, directA ? 1 : 0, 0, 2, 5);
        var host = directA ? "A" : "C";
        var code = source;
        if (!directA)
        {
            await Release(h, a);
            code = (await h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket)).TargetRoomCode!;
            foreach (var uid in new[] { "A", "B", "D" })
                Assert.True((await h.Ops.JoinRoomAsync(C(uid), code, uid, uid, Version)).Success);
        }
        var room = h.RoomManager.GetRoom(code)!;
        await h.Ops.ReportHostReadyAsync(C(host));
        foreach (var uid in new[] { "A", "B", "C", "D" }) h.RoomManager.RecordWorldJoined(code, uid);
        await h.Ops.MarkRoomStartedAsync(C(host), null);
        Assert.True(room.IsStarted);
        var retired = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.AbortWorldPlanAsync(a[1].PlanId, a[1].Ticket));
        Assert.Equal("world_plan_startup_retired", retired.Message);
        Assert.Same(room, h.RoomManager.GetRoom(code));
        clock.Advance(TimeSpan.FromMinutes(20));
        var expired = await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.AbortWorldPlanAsync(a[1].PlanId, a[1].Ticket));
        Assert.Equal("world_plan_ticket_expired", expired.Message);
        Assert.Equal("Committed", (await h.Ops.GetWorldPlanAsync(a[1].PlanId, a[1].Ticket)).Stage);
        await h.Ops.MarkRoomStartedAsync(C(host), null); // Reconnect locking remains idempotent after retirement/expiry.
        await h.Ops.CloseRoomAsync(C(host));
        Assert.Null(h.RoomManager.GetRoom(code));
    }

    [Fact]
    public async Task LaterWorldCreateInheritsFrozenRoster_WithoutExtendingFirstWorldStartupTicket()
    {
        var h = new GatewayTestHarness();
        var uids = new List<string> { "A", "B", "C", "D" };
        var (code, _) = await h.Ops.CreateRoomAsync(C("D"), "D", playerUid: "D", expectedPlayerCount: 4,
            reportedVersion: Version, worldPlanParticipantUids: uids);
        var room = h.RoomManager.GetRoom(code)!;
        Assert.Null(room.HoeingBatchId);
        Assert.Null(room.WorldPlanId);
        Assert.Equal(uids, room.WorldPlanParticipantUids);
        Assert.False((await h.Ops.JoinRoomAsync(C("stranger"), code, "stranger", "stranger", Version)).Success);
        foreach (var uid in new[] { "A", "B" })
            Assert.True((await h.Ops.JoinRoomAsync(C(uid), code, uid, uid, Version)).Success);
        foreach (var uid in new[] { "A", "B", "D" }) Assert.False(h.RoomManager.RecordWorldJoined(code, uid));
        await h.Ops.ReportHostReadyAsync(C("D"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.MarkRoomStartedAsync(C("D"), null));
        Assert.True((await h.Ops.JoinRoomAsync(C("C"), code, "C", "C", Version)).Success);
        Assert.True(h.RoomManager.RecordWorldJoined(code, "C"));
        await h.Ops.MarkRoomStartedAsync(C("D"), null);
        Assert.True(room.IsStarted);
    }

    [Fact]
    public async Task StrictStartGatewayOnlyAcknowledgesTheFrozenHostRoomAfterRealArrival()
    {
        var (h, _, a) = await Start();
        await Commit(h, a, 0, 0, 2, 5);
        await Release(h, a);
        var target = await h.Ops.CreateWorldPlanTargetAsync(C("C"), a[2].PlanId, a[2].Ticket);
        var room = h.RoomManager.GetRoom(target.TargetRoomCode!)!;
        foreach (var uid in new[] { "A", "B", "D" })
            Assert.True((await h.Ops.JoinRoomAsync(C(uid), room.Code, uid, uid, Version)).Success);
        await h.Dispatcher.DispatchAsync(C("C"), GatewayTestHarness.HelloEnvelope());
        await h.Dispatcher.DispatchAsync(C("B"), GatewayTestHarness.HelloEnvelope());
        GatewayEnvelope Mark(string code) => new()
        {
            Type = GatewayProtocol.MessageTypes.Command, Name = GatewayProtocol.Names.RoomMarkStarted,
            RoomCode = code, Payload = GatewayEnvelope.ToPayload(new { requireWorldPlanRoster = true }),
        };
        Assert.NotNull(GatewayTestHarness.ErrorCode(await h.Dispatcher.DispatchAsync(C("C"), Mark(room.Code))));
        Assert.False(room.IsStarted);
        await h.Ops.ReportHostReadyAsync(C("C"));
        foreach (var uid in new[] { "A", "B", "C", "D" }) h.RoomManager.RecordWorldJoined(room.Code, uid);
        Assert.NotNull(GatewayTestHarness.ErrorCode(await h.Dispatcher.DispatchAsync(C("B"), Mark(room.Code))));
        Assert.NotNull(GatewayTestHarness.ErrorCode(await h.Dispatcher.DispatchAsync(C("C"), Mark("wrong-old-room"))));
        Assert.False(room.IsStarted);
        var success = await h.Dispatcher.DispatchAsync(C("C"), Mark(room.Code));
        Assert.Null(GatewayTestHarness.ErrorCode(success));
        Assert.True(success.Payload!["ack"]!.GetValue<bool>());
        Assert.True(room.IsStarted);
    }

    [Fact]
    public async Task StrictStartCannotClaimLegacyRoomOrMissingRoom_OldMarkRemainsCompatible()
    {
        var h = new GatewayTestHarness();
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "A", playerUid: "A", expectedPlayerCount: 4, reportedVersion: Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.MarkRoomStartedAsync(C("A"), null, true, code));
        await Assert.ThrowsAsync<InvalidOperationException>(() => h.Ops.MarkRoomStartedAsync(C("absent"), null, true, code));
        Assert.False(h.RoomManager.GetRoom(code)!.IsStarted);
        await h.Ops.MarkRoomStartedAsync(C("A"), null);
        Assert.True(h.RoomManager.GetRoom(code)!.IsStarted);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenRoomIsPublishedWithProtocolRosterAndFirstPlanBeforeGroupSubscription(bool firstTarget)
    {
        GatewayTestHarness h;
        WorldPlanAdmission[]? admissions = null;
        if (firstTarget)
        {
            var started = await Start();
            h = started.H;
            admissions = started.Admissions;
            await Commit(h, admissions, 0, 0, 2, 5);
            await Release(h, admissions);
        }
        else h = new GatewayTestHarness();
        var host = firstTarget ? "C" : "D";
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        h.GatewayGroups.Setup(g => g.AddToGroupAsync(host, It.IsAny<string>(), It.IsAny<CancellationToken>()))
            .Returns(async () => { entered.TrySetResult(true); await release.Task; });
        Task creating = firstTarget
            ? h.Ops.CreateWorldPlanTargetAsync(C(host), admissions![2].PlanId, admissions[2].Ticket)
            : h.Ops.CreateRoomAsync(C(host), host, playerUid: host, expectedPlayerCount: 4,
                reportedVersion: Version, worldPlanParticipantUids: ["A", "B", "C", "D"]);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var summary = Assert.Single(h.RoomManager.GetOnlineRooms());
            var room = h.RoomManager.GetRoom(summary.Code)!;
            Assert.Equal(GatewayProtocol.RoomProtocols.V3, room.Protocol);
            Assert.Equal(new[] { "A", "B", "C", "D" }, room.WorldPlanParticipantUids);
            Assert.Equal(firstTarget ? admissions![0].PlanId : null, room.WorldPlanId);
            if (firstTarget) Assert.Equal("原房主线路", room.HostConfig!.SelectedBuiltinRoute);
            Assert.False((await h.Ops.JoinRoomAsync(C("stranger"), room.Code, "stranger", "stranger", Version)).Success);
        }
        finally { release.TrySetResult(true); }
        await creating;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task FrozenReconnectUsesUidInsteadOfCrossUidName_AndKeepsTheHostFirst(bool fixedRoster)
    {
        var h = new GatewayTestHarness();
        var uids = new List<string> { "A", "B", "C", "D" };
        var batch = fixedRoster ? "fixed-reconnect-test" : null;
        var (code, _) = await h.Ops.CreateRoomAsync(C("C"), "C", playerUid: "C", expectedPlayerCount: 4,
            reportedVersion: Version, hoeingBatchId: batch, expectedParticipantUids: fixedRoster ? uids : null,
            worldPlanParticipantUids: uids);
        var room = h.RoomManager.GetRoom(code)!;
        Assert.True((await h.Ops.JoinRoomAsync(C("B"), code, "B", "B", Version, batch)).Success);
        room.GracePendingMembers["C"] = DateTime.UtcNow.AddMinutes(1);
        Assert.True((await h.Ops.JoinRoomAsync(C("A"), code, "C", "A", Version, batch)).Success);
        Assert.Equal("C", room.HostConnectionId);
        Assert.True(room.GracePendingMembers.ContainsKey("C"));
        Assert.Equal(3, room.Players.Count);
        Assert.Equal("A", Assert.Single(room.Players.Where(p => p.ConnectionId == "A")).PlayerUid);
        Assert.True((await h.Ops.JoinRoomAsync(C("C-returned"), code, "B", "C", Version, batch)).Success);
        Assert.Equal("C-returned", room.HostConnectionId);
        Assert.Equal("C", room.Players[0].PlayerUid);
        Assert.Equal("C-returned", room.Players[0].ConnectionId);
        room.Players.Single(p => p.PlayerUid == "B").LastHeartbeat = DateTime.UtcNow.AddMinutes(-3);
        Assert.True((await h.Ops.JoinRoomAsync(C("B-returned"), code, "C", "B", Version, batch)).Success);
        Assert.Equal("C-returned", room.HostConnectionId);
        Assert.Equal("B-returned", room.Players.Single(p => p.PlayerUid == "B").ConnectionId);
        Assert.Equal("C", Assert.Single(h.RoomManager.GetOnlineRooms()).HostUid);
        Assert.True((await h.Ops.JoinRoomAsync(C("D"), code, "D", "D", Version, batch)).Success);
        room.Players[0].LastHeartbeat = DateTime.UtcNow.AddMinutes(-3);
        Assert.True((await h.Ops.JoinRoomAsync(C("C-returned-again"), code, "A", "C", Version, batch)).Success);
        Assert.Equal("C-returned-again", room.HostConnectionId);
        Assert.Equal("C", room.Players[0].PlayerUid);
        Assert.Equal(room.HostConnectionId, room.Players[0].ConnectionId);
        Assert.Empty(h.RoomManager.GetOnlineRooms()); // Still a full four-member room after replacing its host.
        Assert.Equal(4, room.Players.Select(p => p.PlayerUid).Distinct().Count());
    }

    private static async Task<(GatewayTestHarness H, string Code)> UnplannedFourPersonTwoWorldRoom()
    {
        var h = new GatewayTestHarness();
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "A", playerUid: "A", expectedPlayerCount: 4, reportedVersion: Version);
        await h.Ops.SetRoomConfigAsync(C("A"), new RoomConfig { MultiWorldEnabled = true, MultiWorldCount = 2 });
        foreach (var uid in new[] { "B", "C", "D" })
            Assert.True((await h.Ops.JoinRoomAsync(C(uid), code, uid, uid, Version)).Success);
        return (h, code);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task V1HostCompletedPayloadCannotClipTwoWorldBoundaryForV2Peer(bool acquiredLegacy)
    {
        var (h, code) = await UnplannedFourPersonTwoWorldRoom();
        foreach (var uid in new[] { "A", "C", "D" })
            h.Capabilities.UnsupportedCapabilities.Add((uid, WorldPlanState.Capability));
        if (acquiredLegacy)
            Assert.Equal("Legacy", (await h.Ops.AcquireWorldPlanAsync(C("B")))!.Snapshot.Stage);
        var v1Hello = GatewayTestHarness.HelloEnvelope() with
        {
            Payload = GatewayEnvelope.ToPayload(new ClientHello { ClientKind = "bgi", ClientVersion = Version,
                ProtocolVersion = 3, Capabilities = ["hoeing.worldPlan.v1"] }),
        };
        await h.Dispatcher.DispatchAsync(C("A"), v1Hello);
        await h.Dispatcher.DispatchAsync(C("B"), GatewayTestHarness.HelloEnvelope() with
        {
            Payload = GatewayEnvelope.ToPayload(new ClientHello { ClientKind = "bgi", ClientVersion = Version,
                ProtocolVersion = 3, Capabilities = [WorldPlanState.Capability] }),
        });
        await h.Ops.ReportHostReadyAsync(C("A"));
        foreach (var uid in new[] { "A", "B", "C", "D" }) await h.Ops.ReportWorldJoinedAsync(C(uid));
        var mark = await h.Dispatcher.DispatchAsync(C("A"), new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Command, Name = GatewayProtocol.Names.RoomMarkStarted, RoomCode = code,
            Payload = GatewayEnvelope.ToPayload(new { completedHostUids = new[] { "A" } }),
        });
        Assert.Null(GatewayTestHarness.ErrorCode(mark));
        var queried = await h.Dispatcher.QueryAsync(C("B"), new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Query, Name = GatewayProtocol.Names.RoomGetRoundHostOrder, RoomCode = code,
        });
        Assert.Null(GatewayTestHarness.ErrorCode(queried));
        var order = queried.Payload!["order"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Assert.Equal(new[] { "A", "B", "C", "D" }, order);
        Assert.Equal(new[] { "A", "B" }, order.Take(h.RoomManager.GetRoom(code)!.HostConfig!.MultiWorldCount));
        Assert.DoesNotContain("C", order.Take(2));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExplicitLegacyDecisionKeepsFullOrderEvenAfterCapabilityLookupNoLongerHasV2(bool unknownReport)
    {
        GatewayTestHarness h;
        string code;
        if (unknownReport)
        {
            var started = await Start(rounds: 2);
            h = started.H;
            code = started.Code;
            Assert.Equal("Legacy", (await Commit(h, started.Admissions, 0, -1, 20, 20)).Stage);
        }
        else
        {
            (h, code) = await UnplannedFourPersonTwoWorldRoom();
            Assert.Equal("Legacy", (await h.Ops.BeginWorldPlanAsync(C("A"), forceLegacy: true)).Snapshot.Stage);
        }
        foreach (var uid in new[] { "A", "B", "C", "D" })
            h.Capabilities.UnsupportedCapabilities.Add((uid, WorldPlanState.Capability));
        await h.Ops.MarkRoomStartedAsync(C("A"), ["A"]);
        var room = h.RoomManager.GetRoom(code)!;
        Assert.True(room.UseLegacyWorldPlanOrder);
        Assert.Equal(new[] { "A", "B", "C", "D" }, room.RoundHostOrder);
        Assert.Equal(new[] { "A", "B" }, room.RoundHostOrder.Take(room.HostConfig!.MultiWorldCount));
    }

    [Fact]
    public async Task UninvolvedAllOldRoomStillAcceptsItsOriginalCompletedHostPayload()
    {
        var (h, code) = await UnplannedFourPersonTwoWorldRoom();
        foreach (var uid in new[] { "A", "B", "C", "D" })
            h.Capabilities.UnsupportedCapabilities.Add((uid, WorldPlanState.Capability));
        await h.Ops.MarkRoomStartedAsync(C("A"), ["A"]);
        var room = h.RoomManager.GetRoom(code)!;
        Assert.False(room.UseLegacyWorldPlanOrder);
        Assert.Equal(new[] { "B", "C", "D" }, room.RoundHostOrder);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ManualCurrentPartyFreezesABC_WhileAutomaticLegacyFallbackKeepsDAdmissionOpen(bool manual)
    {
        var h = new GatewayTestHarness();
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "A", playerUid: "A", expectedPlayerCount: 4, reportedVersion: Version);
        await h.Ops.SetRoomConfigAsync(C("A"), new RoomConfig { MultiWorldEnabled = true, MultiWorldCount = 2 });
        foreach (var uid in new[] { "B", "C" })
            Assert.True((await h.Ops.JoinRoomAsync(C(uid), code, uid, uid, Version)).Success);
        var a = await h.Ops.BeginWorldPlanAsync(C("A"), forceLegacy: true, freezeCurrentParty: manual);
        Assert.Equal("Legacy", a.Snapshot.Stage);
        Assert.Equal(manual, a.Snapshot.CurrentPartyFrozen);
        var room = h.RoomManager.GetRoom(code)!;
        Assert.Equal(manual ? 3 : 4, room.ExpectedPlayerCount);
        Assert.Null(room.WorldPlanId);
        if (manual) Assert.Equal(new[] { "A", "B", "C" }, room.WorldPlanParticipantUids);
        else Assert.Null(room.WorldPlanParticipantUids);
        var b = (await h.Ops.AcquireWorldPlanAsync(C("B")))!;
        Assert.Equal(manual, b.Snapshot.CurrentPartyFrozen);
        await h.Ops.AckWorldPlanAsync(b.PlanId, b.Ticket);
        if (manual) Assert.NotNull(room.WorldPlanParticipantUids); // A legacy ACK cannot unfreeze this explicit manual choice.
        Assert.Equal(!manual, (await h.Ops.JoinRoomAsync(C("D"), code, "D", "D", Version)).Success);
        await h.Ops.ReportHostReadyAsync(C("A"));
        var allJoined = false;
        foreach (var uid in new[] { "A", "B", "C" }) allJoined = h.RoomManager.RecordWorldJoined(code, uid);
        Assert.Equal(manual, allJoined);
        if (!manual) Assert.True(h.RoomManager.RecordWorldJoined(code, "D"));
        await h.Ops.MarkRoomStartedAsync(C("A"), ["A"], requireWorldPlanRoster: manual, expectedRoomCode: code);
        Assert.True(room.IsStarted);
        Assert.Equal(new[] { "A", "B" }, room.RoundHostOrder.Take(2));
    }

    [Theory]
    [InlineData("")]
    [InlineData("A")]
    public async Task ManualPartyWithoutReliableUidRestrictsCapacityWithoutInventingAFrozenRoster(string uid)
    {
        var h = new GatewayTestHarness();
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "A", playerUid: "A", expectedPlayerCount: 4, reportedVersion: Version);
        await h.Ops.SetRoomConfigAsync(C("A"), new RoomConfig { MultiWorldEnabled = true });
        Assert.True((await h.Ops.JoinRoomAsync(C("B"), code, "B", uid, Version)).Success);
        var plan = await h.Ops.BeginWorldPlanAsync(C("A"), forceLegacy: true, freezeCurrentParty: true);
        Assert.Equal("Legacy", plan.Snapshot.Stage);
        Assert.False(plan.Snapshot.CurrentPartyFrozen);
        var room = h.RoomManager.GetRoom(code)!;
        Assert.Equal(2, room.ExpectedPlayerCount);
        Assert.Null(room.WorldPlanParticipantUids);
        Assert.False((await h.Ops.JoinRoomAsync(C("D"), code, "D", "D", Version)).Success);
    }
}
