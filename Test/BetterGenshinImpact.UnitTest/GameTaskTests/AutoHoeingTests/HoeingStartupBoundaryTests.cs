using System;
using System.Collections.Generic;
using System.IO;
using System.Net.WebSockets;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class HoeingStartupBoundaryTests
{
    [Theory]
    [InlineData(false, true, 2, 0)]
    [InlineData(false, true, 2, 1)]
    [InlineData(true, false, 2, 0)]
    public void MissingAuthoritativeRoutesCannotBecomeCompletedWorld(bool cooperative, bool coordinated,
        int authoritativeCount, int localCount)
    {
        using var scope = new BetterGenshinImpact.Service.Execution.CoordinatedHoeingScope(coordinated,
            "batch", new[] { "a", "b" });
        var completedWorld = false;
        var failure = Assert.Throws<HoeingStartupException>(() =>
        {
            HoeingStartupBoundary.RequireRouteCoverage(authoritativeCount, localCount,
                cooperative, CancellationToken.None);
            completedWorld = true;
        });
        Assert.Equal("房主路线映射", failure.Stage);
        Assert.False(completedWorld);
        Assert.Throws<HoeingStartupException>(() =>
            HoeingStartupBoundary.RethrowAfterCleanup(failure, CancellationToken.None));
    }

    [Theory]
    [InlineData(false, true, 0, 0)]
    [InlineData(false, true, 2, 2)]
    [InlineData(true, false, 0, 0)]
    [InlineData(false, false, 2, 0)]
    public void AuthoritativeEmptyOrCompleteRoutesPreserveAdmission(bool cooperative, bool coordinated,
        int authoritativeCount, int localCount)
    {
        using var scope = new BetterGenshinImpact.Service.Execution.CoordinatedHoeingScope(coordinated,
            "batch", new[] { "a", "b" });
        HoeingStartupBoundary.RequireRouteCoverage(authoritativeCount, localCount,
            cooperative, CancellationToken.None);
    }

    [Fact]
    public void UserStopTakesPriorityOverMissingRoutes()
    {
        using var scope = new BetterGenshinImpact.Service.Execution.CoordinatedHoeingScope(true,
            "batch", new[] { "a", "b" });
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        Assert.ThrowsAny<OperationCanceledException>(() =>
            HoeingStartupBoundary.RequireRouteCoverage(2, 0, false, stop.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(null)]
    public void UnconfirmedRouteGateCannotReachFirstRoute(bool? confirmation)
    {
        var firstRouteStarted = false;
        Assert.Throws<HoeingStartupException>(() =>
        {
            HoeingStartupBoundary.RequireConfirmed(confirmation, "route_sync_done", CancellationToken.None);
            firstRouteStarted = true;
        });
        Assert.False(firstRouteStarted);
    }

    [Fact]
    public async Task ClosedRoomDuringRealRouteSyncDoesNotOpenStartupGate()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testSendOverride = (_, _, _) => Task.CompletedTask;
        await using var coordinator = new MultiplayerCoordinator(client, new SyncPointResolver(),
            new BetterGenshinImpact.GameTask.AutoHoeing.AutoHoeingConfig());
        var waiting = coordinator.WaitForAllPlayers("route_sync_done", CancellationToken.None);
        client.DispatchEvt(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Event, Name = "room.closed",
            Payload = GatewayEnvelope.ToPayload(new { reason = "房主已关闭房间" })
        });
        // 原 room.closed 启动门回归：严格同步未确认已直接抛会话终止，首线不可达。
        await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(coordinator.IsExitTriggered);
    }

    [Fact]
    public async Task MissingWorldReadyFailsAndStopsPeriodicReporter()
    {
        var stopped = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        await Assert.ThrowsAsync<HoeingStartupException>(() => HoeingStartupBoundary.WaitForWorldReadyAsync(
            new TaskCompletionSource<bool>(), async token =>
            {
                try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
                finally { stopped.TrySetResult(true); }
            }, TimeSpan.FromMilliseconds(50), CancellationToken.None));
        Assert.True(stopped.Task.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task ReadyDuringReportWaitsForReporterBeforeDisposingItsToken()
    {
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finish = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        bool tokenStillUsable = false;
        var wait = HoeingStartupBoundary.WaitForWorldReadyAsync(ready, async token =>
        {
            entered.SetResult(true);
            await finish.Task;
            // Reproduces a late report continuation after AllWorldJoined, previously reading a disposed CTS.
            using var registration = token.Register(() => { });
            tokenStillUsable = true;
        }, TimeSpan.FromSeconds(5), CancellationToken.None);
        await entered.Task;
        ready.SetResult(true);
        Assert.False(wait.IsCompleted);
        finish.SetResult(true);
        await wait;
        Assert.True(tokenStillUsable);
    }

    [Fact]
    public async Task UserCancellationRemainsCancellationAndStopsReporter()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = false;
        var wait = HoeingStartupBoundary.WaitForWorldReadyAsync(new TaskCompletionSource<bool>(), async token =>
        {
            entered.SetResult(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped = true; }
        }, TimeSpan.FromSeconds(5), stop.Token);
        await entered.Task;
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => wait);
        Assert.True(stopped);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorldReportRecoversOriginalRoomButStillRequiresRealAllWorldJoined(bool ioFailure)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true, _testWaitRetryIntervalMs = 1 };
        await JoinStartupRoomAsync(client);
        var failedSend = Signal();
        var recoveryEntered = Signal();
        var releaseRecovery = Signal();
        var reported = Signal();
        var reports = 0;
        var restores = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            if (request.Name == GatewayProtocol.Names.WorldReportJoined)
            {
                Assert.Equal("startup-room", request.RoomCode);
                if (++reports == 1)
                {
                    await client.OnReconnecting(null);
                    failedSend.TrySetResult(true);
                    if (ioFailure) throw new IOException("world report send interrupted");
                    throw new WebSocketException("world report send interrupted");
                }
                reported.TrySetResult(true);
            }
            if (request.Name == GatewayProtocol.Names.RoomJoin)
            {
                restores++;
                Assert.Equal("startup-room", request.GetString("roomCode"));
                recoveryEntered.TrySetResult(true);
                await releaseRecovery.Task.WaitAsync(token);
            }
            return Response(request, new { success = true });
        };
        var waiting = WaitForRealWorldReadyAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None);
        await failedSend.Task.WaitAsync(TimeSpan.FromSeconds(3));
        var recovery = client.OnReconnected("new-connection");
        await recoveryEntered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(waiting.IsCompleted);
        releaseRecovery.TrySetResult(true);
        await recovery.WaitAsync(TimeSpan.FromSeconds(3));
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, restores);
        Assert.Equal(2, reports);
        Assert.False(waiting.IsCompleted); // Hello + Join + 上报 ACK 均不能伪造全员就绪。
        AllWorldJoined(client);
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
    }

    [Theory]
    [InlineData(false, "not_in_room")]
    [InlineData(true, "not_in_room")]
    [InlineData(false, "handshake_required")]
    [InlineData(true, "handshake_required")]
    public async Task WorldReportBusinessRejectionFailsEvenWhenAllWorldJoinedArrivesTogether(bool readyTogether, string errorCode)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await JoinStartupRoomAsync(client);
        var reports = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal(GatewayProtocol.Names.WorldReportJoined, request.Name);
            reports++;
            if (readyTogether) AllWorldJoined(client);
            return Task.FromResult(Response(request,
                new { error = new { code = errorCode, message = "原房间成员身份被拒绝" } }));
        };
        var failure = await Assert.ThrowsAsync<HoeingStartupException>(() =>
            WaitForRealWorldReadyAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None)
                .WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("全员进入世界上报", failure.Stage);
        Assert.Equal(errorCode, Assert.IsType<GatewayErrorException>(failure.InnerException).Code);
        Assert.Equal(1, reports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task WorldReportCancellationOrPartyTimeoutStopsTheRealPendingReporter(bool userStop)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await JoinStartupRoomAsync(client);
        using var stop = new CancellationTokenSource();
        var entered = Signal();
        var reports = 0;
        var stopped = false;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            reports++;
            entered.TrySetResult(true);
            try { await Task.Delay(Timeout.InfiniteTimeSpan, token); }
            finally { stopped = token.IsCancellationRequested; }
            return Response(request, new { success = true });
        };
        var waiting = WaitForRealWorldReadyAsync(client,
            userStop ? TimeSpan.FromSeconds(5) : TimeSpan.FromMilliseconds(150), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (userStop)
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
        }
        else
        {
            var failure = await Assert.ThrowsAsync<HoeingStartupException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal("全员进入世界", failure.Stage);
        }
        Assert.True(stopped);
        Assert.Equal(1, reports);
    }

    [Fact]
    public async Task PartyTimeoutWhileRecoveringDoesNotRestartBudgetOrSendAnotherWorldReport()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true, _testWaitRetryIntervalMs = 1 };
        await JoinStartupRoomAsync(client);
        var reports = 0;
        var reporterStopped = false;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, _) =>
        {
            reports++;
            await client.OnReconnecting(null);
            throw new WebSocketException("world report send interrupted");
        };
        async Task Report(CancellationToken token)
        {
            try { await client.ReportWorldJoinedAsync(token); }
            finally { reporterStopped = true; }
        }
        var failure = await Assert.ThrowsAsync<HoeingStartupException>(() =>
            HoeingStartupBoundary.WaitForWorldReadyAsync(Signal(), Report,
                TimeSpan.FromMilliseconds(150), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("全员进入世界", failure.Stage);
        Assert.True(reporterStopped);
        Assert.Equal(1, reports);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UserStopDuringReadyReporterDrainWinsOverSuccessOrBusinessFailure(bool rejected)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await JoinStartupRoomAsync(client);
        using var stop = new CancellationTokenSource();
        var entered = Signal();
        var reportCancelled = Signal();
        var response = new TaskCompletionSource<GatewayEnvelope>(TaskCreationOptions.RunContinuationsAsynchronously);
        GatewayEnvelope? pending = null;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            pending = request;
            using var registration = token.Register(() => reportCancelled.TrySetResult(true));
            entered.TrySetResult(true);
            return await response.Task; // 模拟传输完成回调晚于全员就绪及本端取消。
        };
        var waiting = WaitForRealWorldReadyAsync(client, TimeSpan.FromSeconds(5), stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        AllWorldJoined(client);
        await reportCancelled.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(waiting.IsCompleted);
        stop.Cancel();
        response.SetResult(rejected
            ? Response(pending!, new { error = new { code = "not_in_room", message = "late rejection" } })
            : Response(pending!, new { success = true }));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task EndedOrReplacedRoomCannotRedirectPendingWorldReport(bool replaceRoom)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true, _testWaitRetryIntervalMs = 1 };
        await JoinStartupRoomAsync(client);
        var oldEpoch = client.CurrentRoomEpoch;
        var entered = Signal();
        var failSend = Signal();
        var reportRooms = new List<string?>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.WorldReportJoined)
            {
                reportRooms.Add(request.RoomCode);
                entered.TrySetResult(true);
                await failSend.Task;
                throw new WebSocketException("old send completed after room ended");
            }
            return Response(request, new { success = true });
        };
        var waiting = WaitForRealWorldReadyAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
        if (replaceRoom)
            Assert.True(await client.JoinRoomAsync("next-room", "name", "uid"));
        else
            client.EndRoomSession("room-ended");
        failSend.TrySetResult(true);
        await Assert.ThrowsAsync<HoeingStartupException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(new[] { "startup-room" }, reportRooms);
        if (replaceRoom)
        {
            Assert.NotEqual(oldEpoch, client.CurrentRoomEpoch);
            await client.WaitForBusinessReadyAsync(CancellationToken.None);
            Assert.True(client.IsInRoom);
        }
        else Assert.False(client.IsInRoom);
    }

    [Fact]
    public async Task MissingRosterMemberIsReadmittedEvenWhileTransportRemainsConnected()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await JoinStartupRoomAsync(client);
        var epoch = client.CurrentRoomEpoch;
        client.CurrentPlayerList = new(); // 应用心跳清退，不触发传输 Reconnecting。
        var reported = Signal();
        var joins = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.RoomJoin)
            {
                joins++;
                Assert.Equal("startup-room", request.GetString("roomCode"));
                Assert.Equal("uid", request.GetString("playerUid"));
                client.CurrentPlayerList = new() { new() { PlayerUid = "uid", PlayerName = "name" } };
            }
            if (request.Name == GatewayProtocol.Names.WorldReportJoined)
            {
                Assert.Equal(1, joins);
                reported.TrySetResult(true);
            }
            return Task.FromResult(Response(request, new { success = true }));
        };
        var waiting = WaitForRealWorldReadyAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None);
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(waiting.IsCompleted);
        Assert.Equal(epoch, client.CurrentRoomEpoch);
        AllWorldJoined(client);
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(1, joins);
    }

    [Fact]
    public async Task ReportCrossingAnotherDisconnectWaitsForTheNewTransportHandshake()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true, _testWaitRetryIntervalMs = 1 };
        await JoinStartupRoomAsync(client);
        var interrupted = Signal();
        var reported = Signal();
        var reports = 0;
        var hellos = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.SessionHello) hellos++;
            if (request.Name == GatewayProtocol.Names.WorldReportJoined)
            {
                if (++reports == 1)
                {
                    await client.OnReconnecting(null);
                    interrupted.TrySetResult(true);
                    return Response(request, new { error = new { code = "handshake_required", message = "transport replaced" } });
                }
                Assert.Equal(1, hellos);
                reported.TrySetResult(true);
            }
            return Response(request, new { success = true });
        };
        var waiting = WaitForRealWorldReadyAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None);
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(waiting.IsCompleted);
        await client.OnReconnected("new-transport");
        await reported.Task.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.False(waiting.IsCompleted);
        AllWorldJoined(client);
        await waiting.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(2, reports);
    }

    [Fact]
    public async Task RecoveryJoinRefusalFailsStartupWithoutAnotherWorldReport()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true, _testWaitRetryIntervalMs = 1 };
        await JoinStartupRoomAsync(client);
        var interrupted = Signal();
        var reports = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.RoomJoin)
                return Response(request, new { success = false, error = "房间不存在" });
            if (request.Name == GatewayProtocol.Names.WorldReportJoined)
            {
                reports++;
                await client.OnReconnecting(null);
                interrupted.TrySetResult(true);
                throw new WebSocketException("report interrupted");
            }
            return Response(request, new { success = true });
        };
        var waiting = WaitForRealWorldReadyAsync(client, TimeSpan.FromSeconds(5), CancellationToken.None);
        await interrupted.Task.WaitAsync(TimeSpan.FromSeconds(3));
        await client.OnReconnected("refused-connection");
        var failure = await Assert.ThrowsAsync<HoeingStartupException>(() => waiting.WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal("全员进入世界上报", failure.Stage);
        Assert.Equal(1, reports);
        Assert.False(client.IsInRoom);
    }

    private static TaskCompletionSource<bool> Signal()
        => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static GatewayEnvelope Response(GatewayEnvelope request, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Response, Name = request.Name,
        RoomCode = request.RoomCode, Payload = GatewayEnvelope.ToPayload(payload)
    };

    private static async Task JoinStartupRoomAsync(CoordinatorClient client)
    {
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            Task.FromResult(Response(request, new { success = true }));
        Assert.True(await client.JoinRoomAsync("startup-room", "name", "uid"));
        client.CurrentPlayerList = new() { new() { PlayerUid = "uid", PlayerName = "name" } };
    }

    private static void AllWorldJoined(CoordinatorClient client) => client.DispatchEvt(new GatewayEnvelope
    {
        Type = GatewayProtocol.MessageTypes.Event, Name = GatewayProtocol.Events.WorldAllJoined
    });

    private static async Task WaitForRealWorldReadyAsync(CoordinatorClient client, TimeSpan timeout, CancellationToken ct)
    {
        var ready = Signal();
        void OnReady() => ready.TrySetResult(true);
        client.AllWorldJoined += OnReady;
        try
        {
            await HoeingStartupBoundary.WaitForWorldReadyAsync(ready,
                token => client.ReportWorldJoinedAsync(token), timeout, ct, TimeSpan.FromHours(1));
        }
        finally { client.AllWorldJoined -= OnReady; }
    }

    [Theory]
    [InlineData("batch", true, "a,b", "a,b", true)]
    [InlineData("other", true, "a,b", "a,b", false)]
    [InlineData("batch", true, "a", "a", false)]
    [InlineData("batch", true, "a,b", "a", false)]
    [InlineData("batch", false, "a,b", "a,b", false)]
    public async Task AuthorityQueryRequiresSameBatchAndEveryFrozenMember(string batch, bool ready,
        string roster, string joined, bool accepted)
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        var gateway = client.GetOrCreateGatewayForTest();
        string? section = null;
        gateway._testInvokeOverride = (_, request, _) =>
        {
            section = request.GetString("section");
            Assert.Equal("batch", request.GetString("batchId"));
            Assert.Equal(new[] { "a", "b" }, request.Get<List<string>>("participantUids"));
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = request.Name,
                Payload = GatewayEnvelope.ToPayload(new
                {
                    value = new { batchId = batch, participantUids = roster.Split(','), worldJoinedUids = joined.Split(','), ready }
                })
            });
        };
        var verify = client.VerifyHoeingStartupAsync("batch", new[] { "a", "b" }, CancellationToken.None);
        if (accepted) await verify;
        else await Assert.ThrowsAsync<HoeingStartupException>(() => verify);
        Assert.Equal("hoeingStartupReady", section);
    }

    [Fact]
    public void StartupFailureSurvivesCleanupWithoutBecomingCompletedWorld()
    {
        var failure = new HoeingStartupException("路线一致性", "重试仍失败");
        var completedWorld = false;
        var actual = Assert.Throws<HoeingStartupException>(() =>
        {
            HoeingStartupBoundary.RethrowAfterCleanup(failure, CancellationToken.None);
            completedWorld = true;
        });
        Assert.Same(failure, actual);
        Assert.False(completedWorld);
        HoeingStartupBoundary.RethrowAfterCleanup(null, CancellationToken.None); // Authoritative empty round.
    }
}
