#nullable enable
using System;
using System.Collections.Generic;
using System.IO;
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

    private static async Task Join(CoordinatorClient client, string room = "A", string uid = "C")
    {
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            Task.FromResult(Response(request, new { success = true }));
        Assert.True(await client.JoinRoomAsync(room, uid, uid));
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

    [Theory]
    [InlineData("C-instance", true)]
    [InlineData(null, false)]
    [InlineData("other-instance", false)]
    public async Task PassiveNewHostMustBindTheSameTargetRoomInstance(string? standbySessionId, bool accepted)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await Join(client);
        using var transfer = client.EnterWorldPlanTransfer("plan-1", "A");
        var admission = Admission();
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testInvokeOverride = (_, request, _) => Task.FromResult(Response(request, new
        {
            snapshot = new MultiWorldStartPlan
            {
                PlanId = admission.PlanId, Stage = "Committed", TargetRoomCode = "C", TargetRoomSessionId = "C-instance",
                TargetStandbySessionId = standbySessionId, RemainingHostUids = new() { "C", "D" },
            },
        }));
        var creation = client.CreateWorldPlanTargetAsync(admission, CancellationToken.None, standbyOnly: true);
        if (!accepted)
        {
            await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() => creation);
            Assert.False(client.IsInRoom);
            return;
        }
        await creation;
        Event(client, "C", GatewayProtocol.Events.RoomPlayerListChanged,
            new { players = new[] { new PlayerInfo { PlayerUid = "C" } } });
        Assert.True(client.NegotiatesPlayerStandby);
        gateway._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal("C-instance", request.GetString("sessionId"));
            return Task.FromResult(Response(request, new
            {
                state = new HostWorldState("C-instance", "C", ["D"], "ready", null, null, StandbyUids: ["C"]),
            }));
        };
        Assert.Equal("ready", (await client.ReadOrUpdateHostWorldAsync(CancellationToken.None)).Phase);
    }

    [Theory]
    [InlineData("C-instance", true)]
    [InlineData("stale-instance", false)]
    public async Task RotationResetRetriesLostReplyButRequiresTheSameRoomEpoch(string responseSession, bool accepted)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await Join(client);
        using var transfer = client.EnterWorldPlanTransfer("plan-1", "A");
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testInvokeOverride = (_, request, _) => Task.FromResult(Response(request, new
        {
            snapshot = new MultiWorldStartPlan
            {
                PlanId = "plan-1", Stage = "Committed", TargetRoomCode = "C", TargetRoomSessionId = "C-instance",
                TargetStandbySessionId = "C-instance", RemainingHostUids = new() { "C", "D" },
            },
        }));
        await client.CreateWorldPlanTargetAsync(Admission(), CancellationToken.None);
        var attempts = 0;
        gateway._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal(GatewayProtocol.Names.WorldResetForNewRound, request.Name);
            Assert.Equal("C", request.RoomCode);
            Assert.Equal("C-instance", request.GetString("sessionId"));
            Assert.Equal(2, request.GetInt("newRound"));
            if (++attempts == 1) throw new IOException("lost receipt");
            return Task.FromResult(Response(request, new { ack = true, worldRound = 2, sessionId = responseSession }));
        };
        var reset = client.ResetStandbyWorldRoundAsync(2, CancellationToken.None);
        if (accepted) await reset;
        else await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() => reset);
        Assert.Equal(2, attempts);
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

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OpeningPlanReadWaitsForTheOriginalRoomDuringAutomaticReconnect(bool stopDuringReconnect)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true, _testConnectionIdOverride = "old" };
        using var stop = new CancellationTokenSource();
        client.BindTaskLifetime(stop.Token);
        await Join(client);
        var admission = Admission();
        var sent = new List<string>();
        var gateway = client.GetOrCreateGatewayForTest();
        var rejoinSent = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var confirmRejoin = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        gateway._testReconnectStartOverride = _ => throw new InvalidOperationException("StartAsync during automatic reconnect");
        gateway._testInvokeOverride = async (_, request, token) =>
        {
            sent.Add(request.Name);
            if (request.Name == GatewayProtocol.Names.RoomJoin)
            {
                Assert.Equal("A", request.GetString("roomCode"));
                rejoinSent.SetResult();
                await confirmRejoin.Task.WaitAsync(TimeSpan.FromSeconds(5), token);
            }
            if (request.Name == GatewayProtocol.Names.WorldPlanGet)
                Assert.Equal(admission.Ticket, request.GetString("ticket"));
            return Response(request, new { success = true, snapshot = admission.Snapshot });
        };

        client._testIsConnectedOverride = false;
        client._testConnectionIdOverride = null;
        await client.OnReconnecting(null);
        var reading = client.ReadWorldPlanAsync(admission, CancellationToken.None);
        Assert.False(reading.IsCompleted);
        Assert.Empty(sent);

        if (stopDuringReconnect)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
            Assert.Empty(sent);
            return;
        }

        client._testConnectionIdOverride = "new";
        client._testIsConnectedOverride = true;
        var recovery = client.OnReconnected("new");
        await rejoinSent.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(reading.IsCompleted);
        confirmRejoin.SetResult();
        await recovery.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(admission.PlanId, (await reading.WaitAsync(TimeSpan.FromSeconds(5))).PlanId);
        Assert.Equal(new[] { GatewayProtocol.Names.SessionHello, GatewayProtocol.Names.RoomJoin,
            GatewayProtocol.Names.WorldPlanGet }, sent);
    }

    [Fact]
    public async Task OpeningPlanCanRereadTheSameTicketWithoutReplayingAnUnconfirmedWrite()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true, _testWaitRetryIntervalMs = 1 };
        await Join(client);
        var admission = Admission();
        var reads = 0;
        var writes = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal(admission.Ticket, request.GetString("ticket"));
            if (request.Name == GatewayProtocol.Names.WorldPlanAck)
            {
                writes++;
                throw new IOException("write response lost");
            }
            Assert.Equal(GatewayProtocol.Names.WorldPlanGet, request.Name);
            if (++reads == 1) throw new IOException("read response lost");
            return Task.FromResult(Response(request, new { snapshot = admission.Snapshot }));
        };

        Assert.Equal(admission.PlanId, (await client.ReadWorldPlanAsync(admission, CancellationToken.None)).PlanId);
        await Assert.ThrowsAsync<IOException>(() => client.AckWorldPlanAsync(admission, CancellationToken.None));
        Assert.Equal(2, reads);
        Assert.Equal(1, writes);
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

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task RosterArrivingDuringReplayIsAlwaysAppliedLast(bool worldPlan, bool createRoom)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        var myUid = createRoom ? "C" : "B";
        if (worldPlan) await Join(client, uid: myUid);
        using var transfer = worldPlan ? client.EnterWorldPlanTransfer("plan-1", "A") : null;
        using var resumeReplay = new ManualResetEventSlim();
        var firstReplay = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var applied = new System.Collections.Concurrent.ConcurrentQueue<string>();
        bool replayTimedOut = false;
        int readyNotifications = 0;
        client.HostReadyChanged += _ => Interlocked.Increment(ref readyNotifications);
        void Roster(params string[] members) => Event(client, "C", GatewayProtocol.Events.RoomPlayerListChanged,
            new { players = Array.ConvertAll(members, uid => new PlayerInfo { PlayerUid = uid }) });

        client.PlayerListUpdated += players =>
        {
            applied.Enqueue(string.Join(",", players.ConvertAll(p => p.PlayerUid)));
            if (firstReplay.TrySetResult())
                replayTimedOut = !resumeReplay.Wait(TimeSpan.FromSeconds(10));
        };
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            Roster("C");
            Roster("C", "B");
            return Task.FromResult(Response(request, request.Name switch
            {
                GatewayProtocol.Names.RoomJoin => (object)new { success = true },
                GatewayProtocol.Names.RoomCreate => new { roomCode = "C" },
                GatewayProtocol.Names.WorldPlanCreateTarget => new { snapshot = new MultiWorldStartPlan
                {
                    PlanId = "plan-1", Stage = "Committed", RemainingHostUids = new() { "C" },
                    TargetRoomCode = "C", TargetRoomSessionId = "target-C",
                } },
                _ => throw new InvalidOperationException(request.Name),
            }));
        };

        var admission = Task.Run(async () =>
        {
            if (!createRoom) Assert.True(await client.JoinRoomAsync("C", myUid, myUid));
            else if (worldPlan) await client.CreateWorldPlanTargetAsync(Admission(), CancellationToken.None);
            else Assert.Equal("C", await client.CreateRoomAsync("C", null, "C", 3));
        });
        try
        {
            await firstReplay.Task.WaitAsync(TimeSpan.FromSeconds(10));
            Roster("C", "B", "D"); // 收到入房回执后，第一份旧名单的回调仍未返回。
            Event(client, "C", GatewayProtocol.Events.RoomHostReadyChanged, new { ready = true });
            Assert.Equal(1, Volatile.Read(ref readyNotifications)); // 非名单通知不被回放阻塞。
        }
        finally { resumeReplay.Set(); }
        await admission.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.False(replayTimedOut);
        Assert.Equal(new[] { "C", "C,B", "C,B,D" }, applied.ToArray());
        Assert.Equal(3, client.CurrentRoomPlayerCount);
        Assert.Equal(new[] { "C", "B", "D" }, client.CurrentPlayerList.ConvertAll(p => p.PlayerUid));
        Assert.Equal(createRoom, client.IsHost);
        Roster("C", "B"); // 真实减员也必须立即生效，不可按“只保留最大人数”处理。
        Assert.Equal(2, client.CurrentRoomPlayerCount);
        Assert.Equal(new[] { "C", "B" }, client.CurrentPlayerList.ConvertAll(p => p.PlayerUid));
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
