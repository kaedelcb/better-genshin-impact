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

    private static async Task<(GatewayTestHarness H, string Code, WorldPlanAdmission[] Admissions)> Start(int rounds = 4, bool mixed = false)
    {
        var h = new GatewayTestHarness();
        var (code, _) = await h.Ops.CreateRoomAsync(C("A"), "A", playerUid: "A", expectedPlayerCount: 4, reportedVersion: Version);
        await h.Ops.SetRoomConfigAsync(C("A"), new RoomConfig
        {
            MultiWorldEnabled = true, MultiWorldResumeEnabled = true, MultiWorldCount = rounds,
            SelectedBuiltinRoute = "原房主线路", RouteFilterKeywords = "排除", PartyTimeoutSeconds = 60,
        });
        foreach (var uid in new[] { "B", "C", "D" })
            Assert.True((await h.Ops.JoinRoomAsync(C(uid), code, uid, uid, reportedVersion: Version)).Success);
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
}
