using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.Service.Execution;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

[Collection("WorldPlanStartupState")]
public sealed class WorldPlanMemberNegotiationTests
{
    static WorldPlanMemberNegotiationTests()
    {
        var config = typeof(BetterGenshinImpact.Service.ConfigService).GetField(
            "<Config>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        if (config?.GetValue(null) == null) config?.SetValue(null, new AllConfig());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ConfigAndReadinessQueriesPassCancellationToTheTransport(bool readiness)
    {
        await using var client = await Member();
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            Assert.Equal(readiness ? GatewayProtocol.Names.RoomGetState : GatewayProtocol.Names.RoomGetConfig, request.Name);
            Assert.Equal(stop.Token, token);
            entered.TrySetResult(true);
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(request, null);
        };
        Task reading = readiness ? client.IsHostReadyAsync(stop.Token) : client.GetRoomConfigAsync(stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => reading);
    }

    [Fact]
    public async Task OrdinarySingleWorldWithReadyHostAndThreeConfigFailuresReturnsToLegacyStartup()
    {
        await using var client = await Member();
        var task = CreateTask();
        var configs = 0;
        var acquired = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            switch (request.Name)
            {
                case GatewayProtocol.Names.WorldPlanAcquire:
                    acquired++;
                    return System.Threading.Tasks.Task.FromResult(Response(request, null));
                case GatewayProtocol.Names.RoomGetConfig:
                    configs++;
                    throw new IOException("transient config failure");
                case GatewayProtocol.Names.RoomGetState:
                    return System.Threading.Tasks.Task.FromResult(Response(request, new { value = true }));
                default:
                    throw new InvalidOperationException("Unexpected game/start side effect: " + request.Name);
            }
        };

        Assert.False(await Start(task, client).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Equal(3, configs);
        Assert.Equal(6, acquired); // Each readiness fact is followed by a fresh no-plan query.
        Assert.Null(Field<WorldPlanAdmission?>(task, "_worldPlanAdmission"));
        Assert.False(Field<bool>(task, "_multiplayerPartyReady"));
        Assert.True(client.IsInRoom);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task MissingConfigNeverBypassesMultiWorldOrFixedBatchAdmission(bool multiWorld, bool fixedBatch)
    {
        await using var client = await Member();
        var task = CreateTask(multiWorld);
        using var scope = fixedBatch ? new CoordinatedHoeingScope(true, "fixed-test", new[] { "A", "B" }) : null;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            System.Threading.Tasks.Task.FromResult(Response(request, request.Name switch
            {
                GatewayProtocol.Names.WorldPlanAcquire => null,
                GatewayProtocol.Names.RoomGetConfig => new { config = (RoomConfig?)null },
                GatewayProtocol.Names.RoomGetState => new { value = true },
                _ => throw new InvalidOperationException("Unexpected startup side effect: " + request.Name)
            }));

        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(task, client).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.Null(Field<WorldPlanAdmission?>(task, "_worldPlanAdmission"));
        Assert.False(Field<bool>(task, "_multiplayerPartyReady"));
    }

    [Fact]
    public async Task KnownMultiWorldHostCannotUseHostReadyAsPermissionToEnterWithoutItsPlan()
    {
        await using var client = await Member();
        var task = CreateTask(); // Local single-world defaults cannot override the host's stated intent.
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            System.Threading.Tasks.Task.FromResult(Response(request, request.Name switch
            {
                GatewayProtocol.Names.WorldPlanAcquire => null,
                GatewayProtocol.Names.RoomGetConfig => new { config = Plan("Prepared").HostConfig },
                GatewayProtocol.Names.RoomGetState => new { value = true },
                _ => throw new InvalidOperationException("Unexpected startup side effect: " + request.Name)
            }));
        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(task, client));
        Assert.Null(Field<WorldPlanAdmission?>(task, "_worldPlanAdmission"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExistingPlanWinsEvenWhenHostIsReadyAndLocalDefaultsAreSingleWorld(bool arrivesAfterReadiness)
    {
        await using var client = await Member();
        var task = CreateTask();
        var acquired = 0;
        var configs = 0;
        var readiness = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            object? payload;
            switch (request.Name)
            {
                case GatewayProtocol.Names.WorldPlanAcquire:
                    acquired++;
                    payload = arrivesAfterReadiness && acquired == 1 ? null : new
                    {
                        planId = "first-world-A", ticket = "B-owned-ticket", snapshot = Plan("Prepared")
                    };
                    break;
                case GatewayProtocol.Names.RoomGetConfig:
                    configs++;
                    payload = new { config = (RoomConfig?)null };
                    break;
                case GatewayProtocol.Names.RoomGetState:
                    readiness++;
                    Assert.True(arrivesAfterReadiness);
                    payload = new { value = true };
                    break;
                case GatewayProtocol.Names.WorldPlanReport:
                    payload = new { snapshot = Plan("Prepared") };
                    break;
                case GatewayProtocol.Names.WorldPlanAck:
                    payload = new { snapshot = Plan("Committed") };
                    break;
                default:
                    throw new InvalidOperationException("Unexpected game/start side effect: " + request.Name);
            }
            return System.Threading.Tasks.Task.FromResult(Response(request, payload));
        };

        Assert.False(await Start(task, client)); // First world is A; normal game assembly follows, with the plan retained.
        Assert.Equal(arrivesAfterReadiness ? 2 : 1, acquired);
        Assert.Equal(1, configs);
        Assert.Equal(arrivesAfterReadiness ? 1 : 0, readiness);
        Assert.Equal("first-world-A", Field<WorldPlanAdmission>(task, "_worldPlanAdmission").PlanId);
        Assert.Equal("Committed", Field<MultiWorldStartPlan>(task, "_worldStartPlan").Stage);
        Assert.True(Field<AutoHoeingConfig>(task, "_config").MultiWorldEnabled);
        Assert.False(Field<bool>(task, "_multiplayerPartyReady"));
    }

    [Fact]
    public async Task ProductionMemberStartupStopsWhileItsConfigQueryIsStillPending()
    {
        await using var client = await Member();
        using var stop = new CancellationTokenSource();
        var task = CreateTask(cancellation: stop.Token);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        client.GetOrCreateGatewayForTest()._testInvokeOverride = async (_, request, token) =>
        {
            if (request.Name == GatewayProtocol.Names.WorldPlanAcquire) return Response(request, null);
            Assert.Equal(GatewayProtocol.Names.RoomGetConfig, request.Name);
            Assert.True(token.CanBeCanceled);
            entered.TrySetResult(true);
            await System.Threading.Tasks.Task.Delay(Timeout.InfiniteTimeSpan, token);
            return Response(request, null);
        };
        var starting = Start(task, client);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => starting.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(Field<bool>(task, "_multiplayerPartyReady"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task MalformedAdmissionResponseCannotBeInterpretedAsNoPlan(bool explicitNullSnapshot)
    {
        await using var client = await Member();
        var task = CreateTask();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.RoomGetConfig)
                return System.Threading.Tasks.Task.FromResult(Response(request, null));
            Assert.Equal(GatewayProtocol.Names.WorldPlanAcquire, request.Name);
            object malformed = explicitNullSnapshot ? new { snapshot = (MultiWorldStartPlan?)null } : new { };
            return System.Threading.Tasks.Task.FromResult(Response(request, malformed));
        };
        await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() => Start(task, client));
        Assert.Null(Field<WorldPlanAdmission?>(task, "_worldPlanAdmission"));
        Assert.False(Field<bool>(task, "_multiplayerPartyReady"));
    }

    [Theory]
    [InlineData(false, true)]
    [InlineData(true, false)]
    public async Task ExplicitSingleWorldOrDisabledHostConfigDoesNotCallThePlanEndpoint(bool multiWorld, bool resume)
    {
        await using var client = await Member();
        var task = CreateTask(multiWorld: true);
        var sent = new List<string>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            sent.Add(request.Name);
            Assert.Equal(GatewayProtocol.Names.RoomGetConfig, request.Name);
            return System.Threading.Tasks.Task.FromResult(Response(request, new
            {
                config = new RoomConfig { MultiWorldEnabled = multiWorld, MultiWorldResumeEnabled = resume }
            }));
        };

        Assert.False(await Start(task, client));
        Assert.Equal(new[] { GatewayProtocol.Names.RoomGetConfig }, sent);
        Assert.Null(Field<WorldPlanAdmission?>(task, "_worldPlanAdmission"));
        Assert.True(client.IsInRoom);
    }

    [Fact]
    public async Task DisabledHostConfigStillRequiresTheFixedBatchIdentityBeforeLegacyAdmission()
    {
        await using var client = await Member();
        using var scope = new CoordinatedHoeingScope(true, "expected-batch", new[] { "A", "B" });
        var task = CreateTask();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal(GatewayProtocol.Names.RoomGetConfig, request.Name);
            return System.Threading.Tasks.Task.FromResult(Response(request, new
            {
                config = new RoomConfig { MultiWorldEnabled = false, HoeingBatchId = "wrong-batch" }
            }));
        };
        await Assert.ThrowsAnyAsync<Exception>(() => Start(task, client));
        Assert.False(Field<bool>(task, "_multiplayerPartyReady"));
    }

    [Fact]
    public async Task PreviouslyObservedEnabledHostCannotFallBackAfterSubsequentConfigFailures()
    {
        await using var client = await Member();
        var task = CreateTask();
        var configs = 0;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            object? payload = request.Name switch
            {
                GatewayProtocol.Names.RoomGetConfig => ++configs == 1
                    ? new { config = Plan("Prepared").HostConfig } : null,
                GatewayProtocol.Names.WorldPlanAcquire => null,
                GatewayProtocol.Names.RoomGetState => new { value = configs > 1 },
                _ => throw new InvalidOperationException("Unexpected startup side effect: " + request.Name)
            };
            return System.Threading.Tasks.Task.FromResult(Response(request, payload));
        };

        await Assert.ThrowsAsync<InvalidOperationException>(() => Start(task, client).WaitAsync(TimeSpan.FromSeconds(3)));
        Assert.True(configs > 1);
        Assert.True(Field<AutoHoeingConfig>(task, "_config").MultiWorldEnabled);
        Assert.Null(Field<WorldPlanAdmission?>(task, "_worldPlanAdmission"));
        Assert.False(Field<bool>(task, "_multiplayerPartyReady"));
    }

    [Fact]
    public async Task NegotiationDeadlineStopsTheMemberUpdateWithoutCancellingTheWholeTask()
    {
        using var budget = new CancellationTokenSource();
        var task = CreateTask();
        var started = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        task.UpdateHoeingRoutes = async (_, token) =>
        {
            started.TrySetResult();
            await Task.Delay(Timeout.InfiniteTimeSpan, token);
        };
        await using var client = await Member();
        var config = Plan("Planning").HostConfig!;
        config.AutoUpdateRoutes = true;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal(GatewayProtocol.Names.RoomGetConfig, request.Name);
            return Task.FromResult(Response(request, new { config }));
        };
        var negotiate = (Task)typeof(AutoHoeingTask).GetMethod("NegotiateMemberWorldPlanAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(task, new object[] { client, budget })!;
        await started.Task.WaitAsync(TimeSpan.FromSeconds(2));
        budget.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => negotiate.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(Field<CancellationToken>(task, "_ct").IsCancellationRequested);
    }

    private static AutoHoeingTask CreateTask(bool multiWorld = false, CancellationToken cancellation = default)
    {
        var task = new AutoHoeingTask();
        Set(task, "_config", new AutoHoeingConfig
        {
            PlayerUid = "B", PlayerName = "B", MultiplayerRole = "member", MultiplayerEnabled = true,
            MultiWorldEnabled = multiWorld, MultiWorldResumeEnabled = true, PartyTimeoutSeconds = 30,
            AutoUpdateRoutes = false
        });
        Set(task, "_ct", cancellation);
        return task;
    }

    private static MultiWorldStartPlan Plan(string stage) => new()
    {
        PlanId = "first-world-A", Stage = stage, InitialHostUid = "A", InitialRoomCode = "source-A",
        InitialRoomSessionId = "source-A-instance", ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5),
        ParticipantUids = new() { "A", "B" }, CandidateHostUids = new() { "A" }, RemainingHostUids = new() { "A" },
        Players = new() { new PlayerInfo { PlayerUid = "A" }, new PlayerInfo { PlayerUid = "B" } },
        HostConfig = new RoomConfig
        {
            MultiWorldEnabled = true, MultiWorldResumeEnabled = true, MultiWorldCount = 1,
            PartyTimeoutSeconds = 30, AutoUpdateRoutes = false
        }
    };

    private static async Task<CoordinatorClient> Member()
    {
        var client = new CoordinatorClient { _testIsConnectedOverride = true };
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            System.Threading.Tasks.Task.FromResult(Response(request, new { success = true }));
        Assert.True(await client.JoinRoomAsync("source-A", "B", "B"));
        typeof(BgiGatewayClient).GetProperty(nameof(BgiGatewayClient.ServerCapabilities))!
            .SetValue(client.GetOrCreateGatewayForTest(), new[] { GatewayProtocol.WorldPlanCapability });
        client.CurrentPlayerList = new() { new PlayerInfo { PlayerUid = "A" }, new PlayerInfo { PlayerUid = "B" } };
        client.HostPlayerUid = "A";
        return client;
    }

    private static GatewayEnvelope Response(GatewayEnvelope request, object? payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Response, Name = request.Name, Payload = GatewayEnvelope.ToPayload(payload)
    };

    private static Task<bool> Start(AutoHoeingTask task, CoordinatorClient client)
        => (Task<bool>)typeof(AutoHoeingTask).GetMethod("TryPrepareWorldStartAsync", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(task, new object[] { client, false })!;

    private static T Field<T>(AutoHoeingTask task, string name)
        => (T)typeof(AutoHoeingTask).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(task)!;

    private static void Set(AutoHoeingTask task, string name, object value)
        => typeof(AutoHoeingTask).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(task, value);
}
