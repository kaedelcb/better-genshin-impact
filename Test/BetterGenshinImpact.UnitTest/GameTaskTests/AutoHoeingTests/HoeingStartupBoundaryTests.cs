using System;
using System.Collections.Generic;
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
