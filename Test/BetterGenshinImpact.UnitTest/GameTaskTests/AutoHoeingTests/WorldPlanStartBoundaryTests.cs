using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>Exercises the real startup method through confirmed and rejected no-work teardown.</summary>
[Collection("WorldPlanStartupState")]
public sealed class WorldPlanStartBoundaryTests
{
    static WorldPlanStartBoundaryTests()
    {
        var configField = typeof(BetterGenshinImpact.Service.ConfigService).GetField(
            "<Config>k__BackingField", BindingFlags.Static | BindingFlags.NonPublic);
        if (configField?.GetValue(null) == null) configField?.SetValue(null, new AllConfig());
    }

    [Fact]
    public async Task ConfirmedNoWorkCompletesOnlyAfterSourceReleaseWithoutFictitiousWorldReadiness()
    {
        await using var client = await ClientInSourceRoom();
        var task = CreateTask();
        var sent = new List<string>();
        SeedNoWorkServer(client, sent, releasedStage: "NoWork", sourceReleased: true);

        Assert.True(await Start(task, client));

        Assert.True(Field<bool>(task, "_worldPlanNoWorkConfirmed"));
        Assert.True(Field<bool>(task, "_completedNormally"));
        Assert.True(Field<bool>(task, "_mainExecutionReturned"));
        Assert.False(Field<bool>(task, "_multiplayerPartyReady"));
        Assert.False(client.IsInRoom);
        Assert.Equal(new[]
        {
            GatewayProtocol.Names.WorldPlanBegin,
            GatewayProtocol.Names.WorldPlanReport,
            GatewayProtocol.Names.WorldPlanAck,
            GatewayProtocol.Names.WorldPlanLeaveSource
        }, sent);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task AbortedNoWorkSourceReleaseNeverMarksCompletion_AndAbortsOwnedPlan(bool sourceReleased)
    {
        await using var client = await ClientInSourceRoom();
        var task = CreateTask();
        var sent = new List<string>();
        SeedNoWorkServer(client, sent, releasedStage: "Aborted", sourceReleased);

        await Assert.ThrowsAnyAsync<Exception>(() => Start(task, client));

        Assert.False(Field<bool>(task, "_worldPlanNoWorkConfirmed"));
        Assert.False(Field<bool>(task, "_completedNormally"));
        Assert.False(Field<bool>(task, "_mainExecutionReturned"));
        Assert.False(Field<bool>(task, "_multiplayerPartyReady"));
        Assert.Equal(new[]
        {
            GatewayProtocol.Names.WorldPlanBegin,
            GatewayProtocol.Names.WorldPlanReport,
            GatewayProtocol.Names.WorldPlanAck,
            GatewayProtocol.Names.WorldPlanLeaveSource,
            GatewayProtocol.Names.SessionHello,
            GatewayProtocol.Names.WorldPlanAbort
        }, sent);
    }

    [Fact]
    public async Task DisabledHostSwitchReturnsToTheOldStartupWithoutSendingAnyPlanRequest()
    {
        await using var client = await ClientInSourceRoom();
        var task = CreateTask();
        Field<AutoHoeingConfig>(task, "_config").MultiWorldResumeEnabled = false;
        var sent = new List<string>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            sent.Add(request.Name);
            throw new InvalidOperationException("Disabled preview must not send a request");
        };

        Assert.False(await Start(task, client));

        Assert.Empty(sent);
        Assert.False(Field<bool>(task, "_worldPlanNoWorkConfirmed"));
        Assert.False(Field<bool>(task, "_completedNormally"));
        Assert.False(Field<bool>(task, "_multiplayerPartyReady"));
        Assert.True(client.IsInRoom);
    }

    [Fact]
    public async Task ImmediateStartWithPartialOrdinaryRosterPublishesLegacyAndPreservesTheOldPartySignal()
    {
        await using var client = await ClientInSourceRoom();
        client.CurrentPlayerList = new() { new PlayerInfo { PlayerUid = "A", PlayerName = "A" } };
        var task = CreateTask();
        var sent = new List<string>();
        var previousSkip = AutoHoeingTask.SkipPartyWait;
        var previousWaiting = AutoHoeingTask.IsWaitingForParty;
        try
        {
            AutoHoeingTask.SkipPartyWait = true;
            AutoHoeingTask.IsWaitingForParty = false;
            client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            {
                sent.Add(request.Name);
                Assert.Equal(GatewayProtocol.Names.WorldPlanBegin, request.Name);
                Assert.True(request.GetBool("forceLegacy"));
                Assert.True(AutoHoeingTask.IsWaitingForParty);
                return Task.FromResult(Response(request, new
                {
                    planId = "partial-roster-legacy", ticket = "owned-test-ticket",
                    snapshot = new MultiWorldStartPlan { PlanId = "partial-roster-legacy", Stage = "Legacy" }
                }));
            };

            Assert.False(await Start(task, client));

            Assert.Equal(new[] { GatewayProtocol.Names.WorldPlanBegin }, sent);
            Assert.False(AutoHoeingTask.IsWaitingForParty);
            Assert.True(AutoHoeingTask.SkipPartyWait); // The original AutoPartyTask must consume it later.
            Assert.False(Field<bool>(task, "_worldPlanNoWorkConfirmed"));
            Assert.False(Field<bool>(task, "_completedNormally"));
            Assert.True(client.IsInRoom);
        }
        finally
        {
            AutoHoeingTask.SkipPartyWait = previousSkip;
            AutoHoeingTask.IsWaitingForParty = previousWaiting;
        }
    }

    private static AutoHoeingTask CreateTask()
    {
        var task = new AutoHoeingTask();
        SetField(task, "_config", new AutoHoeingConfig
        {
            PlayerUid = "A", PlayerName = "A", ExpectedPlayerCount = 4,
            MultiplayerEnabled = true, MultiplayerRole = "host",
            MultiWorldEnabled = true, MultiWorldResumeEnabled = true,
            MultiWorldCount = 2, PartyTimeoutSeconds = 30
        });
        // A is not a candidate in this focused startup fixture, so no account or resource is read.
        SetField(task, "_dataDir", Path.Combine(Path.GetTempPath(), "not-created-worldplan-start-boundary"));
        SetField(task, "_ct", CancellationToken.None);
        return task;
    }

    private static async Task<CoordinatorClient> ClientInSourceRoom()
    {
        var client = new CoordinatorClient { _testIsConnectedOverride = true };
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testInvokeOverride = (_, request, _) => Task.FromResult(Response(request, new { success = true }));
        Assert.True(await client.JoinRoomAsync("source-A", "A", "A"));
        typeof(BgiGatewayClient).GetProperty(nameof(BgiGatewayClient.ServerCapabilities))!
            .SetValue(gateway, new[] { GatewayProtocol.WorldPlanCapability });
        client.CurrentPlayerList = new[] { "A", "B", "C", "D" }
            .Select(uid => new PlayerInfo { PlayerUid = uid, PlayerName = uid }).ToList();
        client.HostPlayerUid = "A";
        return client;
    }

    private static void SeedNoWorkServer(CoordinatorClient client, List<string> sent,
        string releasedStage, bool sourceReleased)
    {
        MultiWorldStartPlan Snapshot(string stage, bool released = false) => new()
        {
            PlanId = "owned-no-work-plan", Stage = stage,
            InitialHostUid = "A", InitialRoomCode = "source-A", InitialRoomSessionId = "source-A-instance",
            ExpiresAtUtc = DateTime.UtcNow.AddMinutes(5), SourceReleased = released,
            ParticipantUids = new() { "A", "B", "C", "D" },
            CandidateHostUids = new() { "B" }, RemainingHostUids = new(),
            Players = client.CurrentPlayerList,
            HostConfig = new RoomConfig
            {
                MultiWorldEnabled = true, MultiWorldResumeEnabled = true, MultiWorldCount = 2,
                PartyTimeoutSeconds = 30, AutoUpdateRoutes = false
            }
        };
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            sent.Add(request.Name);
            if (request.Name == GatewayProtocol.Names.WorldPlanBegin)
                return Task.FromResult(Response(request, new
                {
                    planId = "owned-no-work-plan", ticket = "owned-test-ticket", snapshot = Snapshot("Planning")
                }));
            if (request.Name == GatewayProtocol.Names.SessionHello)
                return Task.FromResult(Response(request, new { capabilities = new[] { GatewayProtocol.WorldPlanCapability } }));
            Assert.Equal("owned-no-work-plan", request.GetString("planId"));
            Assert.Equal("owned-test-ticket", request.GetString("ticket"));
            var snapshot = request.Name switch
            {
                GatewayProtocol.Names.WorldPlanReport => Snapshot("Prepared"),
                GatewayProtocol.Names.WorldPlanAck => Snapshot("NoWork"),
                GatewayProtocol.Names.WorldPlanLeaveSource => Snapshot(releasedStage, sourceReleased),
                GatewayProtocol.Names.WorldPlanAbort => Snapshot("Aborted"),
                _ => throw new InvalidOperationException("Unexpected startup side effect: " + request.Name)
            };
            if (request.Name == GatewayProtocol.Names.WorldPlanReport)
            {
                Assert.True(request.GetBool("known"));
                Assert.Equal(0, request.GetInt("remainingCount"));
            }
            return Task.FromResult(Response(request, new { snapshot }));
        };
    }

    private static GatewayEnvelope Response(GatewayEnvelope request, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Response, Name = request.Name,
        Payload = GatewayEnvelope.ToPayload(payload)
    };

    private static Task<bool> Start(AutoHoeingTask task, CoordinatorClient client)
        => (Task<bool>)typeof(AutoHoeingTask).GetMethod("TryPrepareWorldStartAsync",
            BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(task, new object[] { client, true })!;

    private static T Field<T>(AutoHoeingTask task, string name)
        => (T)typeof(AutoHoeingTask).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(task)!;

    private static void SetField(AutoHoeingTask task, string name, object value)
        => typeof(AutoHoeingTask).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(task, value);
}

// The startup UI exposes process-wide waiting/skip flags; run these tests without other collections in flight.
[CollectionDefinition("WorldPlanStartupState", DisableParallelization = true)]
public sealed class WorldPlanStartupStateCollection { }
