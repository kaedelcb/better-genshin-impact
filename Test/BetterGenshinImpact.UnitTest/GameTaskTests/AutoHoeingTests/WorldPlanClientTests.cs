#nullable enable
using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class WorldPlanClientTests
{
    private static GatewayEnvelope Response(GatewayEnvelope request, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Response, Name = request.Name,
        Payload = GatewayEnvelope.ToPayload(payload),
    };

    private static void Event(CoordinatorClient client, string room, string name, object payload)
        => client.DispatchEvt(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Event, RoomCode = room, Name = name,
            Payload = GatewayEnvelope.ToPayload(payload),
        });

    private static WorldPlanAdmission Admission() => new()
    {
        PlanId = "plan-1", Ticket = "private-test-ticket",
        Snapshot = new MultiWorldStartPlan { PlanId = "plan-1", Stage = "Committed" },
    };

    private static async Task Join(CoordinatorClient client, string room = "A")
    {
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            Task.FromResult(Response(request, new { success = true }));
        Assert.True(await client.JoinRoomAsync(room, "C", "C"));
    }

    [Fact]
    public async Task LateSourceEventsCannotChangeTargetHostOrReleaseTargetReadiness()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await Join(client);
        using var transfer = client.EnterWorldPlanTransfer("plan-1", "A");
        await Join(client, "C");
        Event(client, "C", GatewayProtocol.Events.RoomPlayerListChanged,
            new { players = new[] { new PlayerInfo { PlayerUid = "C" } } });
        var ready = 0; var joined = 0; var aborted = 0; var closed = 0;
        client.HostReadyChanged += _ => ready++;
        client.AllWorldJoined += () => joined++;
        client.CoordinatedAbortReceived += (_, _) => aborted++;
        client.RoomClosed += _ => closed++;
        Event(client, "A", GatewayProtocol.Events.RoomPlayerListChanged,
            new { players = new[] { new PlayerInfo { PlayerUid = "A" } } });
        Event(client, "A", GatewayProtocol.Events.RoomHostReadyChanged, new { ready = true });
        Event(client, "A", GatewayProtocol.Events.WorldAllJoined, new { });
        Event(client, "A", GatewayProtocol.Events.SyncCoordinatedAborted, new { reason = "old", reporterUid = "A" });
        Event(client, "A", GatewayProtocol.Events.RoomClosed, new { reason = "old" });
        client.DispatchEvt(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Event, Name = GatewayProtocol.Events.WorldAllJoined,
            Payload = GatewayEnvelope.ToPayload(new { }),
        }); // 新流程不能用没有房间身份的迟到消息放行。
        Assert.Equal("C", client.HostPlayerUid);
        Assert.Equal(0, ready); Assert.Equal(0, joined); Assert.Equal(0, aborted); Assert.Equal(0, closed);
        Event(client, "C", GatewayProtocol.Events.RoomClosed, new { reason = "real failure" });
        Assert.Equal(1, closed);
        Assert.False(client.IsInRoom);
    }

    [Fact]
    public async Task TargetCreationRetainsPlayerListThatArrivesBeforeCreateResponse()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await Join(client);
        using var transfer = client.EnterWorldPlanTransfer("plan-1", "A");
        var admission = Admission();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal(GatewayProtocol.Names.WorldPlanCreateTarget, request.Name);
            Event(client, "C", GatewayProtocol.Events.RoomPlayerListChanged,
                new { players = new[] { new PlayerInfo { PlayerUid = "C" } } });
            return Task.FromResult(Response(request, new
            {
                snapshot = new MultiWorldStartPlan
                {
                    PlanId = admission.PlanId, Stage = "Committed", TargetRoomCode = "C",
                    TargetRoomSessionId = "C-instance", RemainingHostUids = new() { "C", "D" },
                },
            }));
        };
        await client.CreateWorldPlanTargetAsync(admission, CancellationToken.None);
        Assert.Equal("C", client.HostPlayerUid);
        Assert.True(client.IsInRoom);
        Assert.True(client.IsHost);
    }

    [Fact]
    public async Task TransferReconnectOnlyHellosAndReadsOriginalTicketWithoutRejoiningSource()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await Join(client);
        using var transfer = client.EnterWorldPlanTransfer("plan-1", "A");
        var admission = Admission();
        var sent = new List<string>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, token) =>
        {
            Assert.False(token.IsCancellationRequested); // 原 A RoomSession 已结束，不得使用其取消令牌。
            sent.Add(request.Name);
            if (request.Name == GatewayProtocol.Names.SessionHello)
                return Task.FromResult(Response(request, new { capabilities = Array.Empty<string>() }));
            Assert.Equal("private-test-ticket", request.GetString("ticket"));
            return Task.FromResult(Response(request, new { snapshot = admission.Snapshot }));
        };
        await client.OnReconnected("new-connection");
        var snapshot = await client.ReadWorldPlanAsync(admission, CancellationToken.None);
        Assert.Equal("plan-1", snapshot.PlanId);
        Assert.Equal(new[] { GatewayProtocol.Names.SessionHello, GatewayProtocol.Names.WorldPlanGet }, sent);
    }

    [Fact]
    public async Task StopCancelsPendingPlanReadAndForbidsTargetCreateEvenWithUncancelledCallerToken()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        using var stop = new CancellationTokenSource();
        client.BindTaskLifetime(stop.Token);
        await Join(client);
        using var transfer = client.EnterWorldPlanTransfer("plan-1", "A");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(request, new { snapshot = Admission().Snapshot });
        };
        var reading = client.ReadWorldPlanAsync(Admission(), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.CreateWorldPlanTargetAsync(Admission(), CancellationToken.None));
    }

    [Fact]
    public async Task ExplicitJoinBuffersItsEarlyEventsButRejectsUnrelatedRooms()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await Join(client);
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            Event(client, "UNRELATED", GatewayProtocol.Events.RoomPlayerListChanged,
                new { players = new[] { new PlayerInfo { PlayerUid = "intruder" } } });
            Event(client, "C", GatewayProtocol.Events.RoomPlayerListChanged,
                new { players = new[] { new PlayerInfo { PlayerUid = "C" } } });
            return Task.FromResult(Response(request, new { success = true }));
        };
        Assert.True(await client.JoinRoomAsync("C", "C", "C"));
        Assert.Equal("C", client.HostPlayerUid);
    }

    [Fact]
    public async Task StopStillAllowsOnlyTheOwnedPlanCleanupAbort()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        using var stop = new CancellationTokenSource();
        client.BindTaskLifetime(stop.Token);
        await Join(client);
        stop.Cancel();
        var sent = new List<string>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            sent.Add(request.Name);
            return Task.FromResult(Response(request, request.Name == GatewayProtocol.Names.SessionHello
                ? (object)new { capabilities = Array.Empty<string>() }
                : new { snapshot = new MultiWorldStartPlan { PlanId = "plan-1", Stage = "Aborted" } }));
        };
        var snapshot = await client.AbortWorldPlanAsync(Admission(), CancellationToken.None);
        Assert.Equal("Aborted", snapshot.Stage);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.AckWorldPlanAsync(Admission(), CancellationToken.None));
        Assert.Equal(new[] { GatewayProtocol.Names.SessionHello, GatewayProtocol.Names.WorldPlanAbort }, sent);
    }

    [Fact]
    public async Task NoWorkTeardownDoesNotReviveSourceEventsAfterTransferScopeEnds()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await Join(client);
        var closed = 0; var aborted = 0;
        client.RoomClosed += _ => closed++;
        client.CoordinatedAbortReceived += (_, _) => aborted++;
        client.EnterWorldPlanTransfer("plan-1", "A").Dispose();
        Event(client, "A", GatewayProtocol.Events.RoomClosed, new { reason = "late NoWork close" });
        Event(client, "A", GatewayProtocol.Events.SyncCoordinatedAborted, new { reason = "late abort", reporterUid = "A" });
        Assert.Equal(0, closed);
        Assert.Equal(0, aborted);
        Assert.False(client.IsInRoom);
    }

    [Fact]
    public async Task CrossRoomTransportFailureRetriesTheSameTicketWithoutRejoiningSource()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true, _testWaitRetryIntervalMs = 1 };
        await Join(client);
        using var transfer = client.EnterWorldPlanTransfer("plan-1", "A");
        var attempts = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal(GatewayProtocol.Names.WorldPlanGet, request.Name);
            Assert.Equal("private-test-ticket", request.GetString("ticket"));
            if (++attempts == 1) throw new System.IO.IOException("connection changing");
            return Task.FromResult(Response(request, new { snapshot = Admission().Snapshot }));
        };
        var snapshot = await client.ReadWorldPlanAsync(Admission(), CancellationToken.None);
        Assert.Equal("plan-1", snapshot.PlanId);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task CurrentRoomArrivalReplayReleasesAfterMigrationWhileOldRoomReplayIsIgnored()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await Join(client);
        using var transfer = client.EnterWorldPlanTransfer("plan-1", "A");
        await Join(client, "C");
        var arrivals = new List<string>();
        var failures = new List<string>();
        client.AllArrived += syncId => arrivals.Add(syncId);
        client.SyncBarrierFailedReceived += (syncId, _) => failures.Add(syncId);
        Event(client, "A", GatewayProtocol.Events.SyncAllArrived, new { syncPointId = "old" });
        Event(client, "A", GatewayProtocol.Events.SyncBarrierFailed, new { syncPointId = "old", reason = "old failure" });
        Event(client, "C", GatewayProtocol.Events.SyncAllArrived, new { syncPointId = "current" });
        Event(client, "C", GatewayProtocol.Events.SyncBarrierFailed, new { syncPointId = "current-gate", reason = "current failure" });
        Assert.Equal(new[] { "current" }, arrivals);
        Assert.Equal(new[] { "current-gate" }, failures);
    }

    [Fact]
    public async Task LostCreateAcknowledgementRetriesWithinTotalBudgetAndAdoptsSameTargetRoom()
    {
        await using var client = new CoordinatorClient
        {
            _testIsConnectedOverride = true, _testWaitRetryIntervalMs = 1,
            _testAuxiliaryBudget = TimeSpan.FromMilliseconds(60),
        };
        await Join(client);
        using var transfer = client.EnterWorldPlanTransfer("plan-1", "A");
        var sends = 0;
        var target = new MultiWorldStartPlan
        {
            PlanId = "plan-1", Stage = "Committed", RemainingHostUids = new() { "C" },
            TargetRoomCode = "C", TargetRoomSessionId = "only-C-instance",
        };
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            Assert.Equal(GatewayProtocol.Names.WorldPlanCreateTarget, request.Name);
            Assert.Equal("private-test-ticket", request.GetString("ticket"));
            if (++sends == 1) await Task.Delay(Timeout.InfiniteTimeSpan, token); // Server succeeded, reply lost.
            Event(client, "C", GatewayProtocol.Events.RoomPlayerListChanged,
                new { players = new[] { new PlayerInfo { PlayerUid = "C" } } });
            return Response(request, new { snapshot = target });
        };
        var snapshot = await client.CreateWorldPlanTargetAsync(Admission(), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, sends);
        Assert.Equal("only-C-instance", snapshot.TargetRoomSessionId);
        Assert.Equal("C", client.HostPlayerUid);
        Assert.True(client.IsInRoom);
    }

    [Fact]
    public async Task TargetConfirmationUsesServerConnectionValidationRatherThanIndependentSnapshotQuery()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await Join(client, "C");
        var sent = new List<string>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            sent.Add(request.Name);
            Assert.Equal("private-test-ticket", request.GetString("ticket"));
            return Task.FromResult(Response(request, new { snapshot = Admission().Snapshot }));
        };
        await client.ConfirmWorldPlanTargetAsync(Admission(), CancellationToken.None);
        Assert.Equal(new[] { GatewayProtocol.Names.WorldPlanValidateTarget }, sent);
    }
}
