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
    public async Task SavedDisabledSwitchStillStartsWorldPlan()
    {
        await using var client = await ClientInSourceRoom();
        var task = CreateTask();
        SetField(task, "_settingsOverride", new Dictionary<string, object?>
        {
            ["multiWorldResumeEnabled"] = false
        });
        typeof(AutoHoeingTask).GetMethod("ApplySettingsOverride", BindingFlags.Instance | BindingFlags.NonPublic)!
            .Invoke(task, null);
        var sent = new List<string>();
        SeedNoWorkServer(client, sent, releasedStage: "NoWork", sourceReleased: true);

        Assert.True(await Start(task, client));

        Assert.Equal(GatewayProtocol.Names.WorldPlanBegin, sent[0]);
        Assert.True(Field<bool>(task, "_worldPlanNoWorkConfirmed"));
        Assert.True(Field<bool>(task, "_completedNormally"));
        Assert.False(client.IsInRoom);
    }

    [Fact]
    public async Task ImmediateStartChoosesTheCurrentPartyWithoutSkippingTheirGameAdmission()
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
                Assert.True(request.GetBool("freezeCurrentParty"));
                Assert.True(AutoHoeingTask.IsWaitingForParty);
                return Task.FromResult(Response(request, new
                {
                    planId = "partial-roster-legacy", ticket = "owned-test-ticket",
                    snapshot = new MultiWorldStartPlan { PlanId = "partial-roster-legacy", Stage = "Legacy", CurrentPartyFrozen = true, ParticipantUids = new() { "A" } }
                }));
            };

            Assert.False(await Start(task, client));

            Assert.Equal(new[] { GatewayProtocol.Names.WorldPlanBegin }, sent);
            Assert.False(AutoHoeingTask.IsWaitingForParty);
            Assert.False(AutoHoeingTask.SkipPartyWait); // The request selects this roster; physical arrival is separate.
            Assert.Equal(1, Field<int?>(task, "_legacyPartySize"));
            Assert.Equal(new[] { "A" }, Field<IReadOnlyList<string>>(task, "_manualFrozenParty"));
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

    [Fact]
    public async Task PartialRosterTimeoutSharesItsDeadlineWithGameAssembly()
    {
        await using var client = await ClientInSourceRoom();
        client.CurrentPlayerList.RemoveAll(p => p.PlayerUid == "D");
        var task = CreateTask();
        var config = Field<AutoHoeingConfig>(task, "_config");
        config.PartyTimeoutSeconds = 2;
        config.PartyTimeoutAction = 1;
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal(GatewayProtocol.Names.WorldPlanBegin, request.Name);
            Assert.True(request.GetBool("forceLegacy"));
            Assert.False(request.GetBool("freezeCurrentParty"));
            return Task.FromResult(Response(request, new
            {
                planId = "partial", ticket = "owned-ticket",
                snapshot = new MultiWorldStartPlan { PlanId = "partial", Stage = "Legacy", ParticipantUids = new() { "A", "B", "C" } }
            }));
        };
        Assert.False(await Start(task, client).WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(3, Field<int?>(task, "_legacyPartySize"));
        Assert.Equal(4, (int)typeof(AutoHoeingTask).GetMethod("InitialPartySize", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(task, null)!);
        var seconds = (int)typeof(AutoHoeingTask).GetMethod("RemainingPartyWaitSeconds", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(task, null)!;
        Assert.Equal(1, seconds);
        Assert.False(AutoHoeingTask.SkipPartyWait);
    }

    [Fact]
    public async Task GuardRestartWithUnknownReportLocksFullLegacyOrder()
    {
        await using var client = await ClientInSourceRoom();
        var task = CreateTask();
        SetField(task, "_isGuardRestartRun", true);
        var sent = new List<string>();
        IReadOnlyList<string>? exclusions = new[] { "not-observed" };
        var dir = Directory.CreateTempSubdirectory("worldplan-fallback-");
        try
        {
            var store = Field<BetterGenshinImpact.GameTask.AutoHoeing.Services.MultiWorldProgressStore>(task, "_multiWorldProgressStore");
            store.Load(dir.FullName, "A");
            store.MarkHostCompleted("A", "A|B|C|D");
            client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
            {
                sent.Add(request.Name);
                if (request.Name == GatewayProtocol.Names.RoomMarkStarted)
                {
                    exclusions = request.Get<List<string>>("completedHostUids");
                    return Task.FromResult(Response(request, new { ack = true }));
                }
                var stage = request.Name switch
                {
                    GatewayProtocol.Names.WorldPlanBegin => "Planning",
                    GatewayProtocol.Names.WorldPlanReport => "Prepared",
                    GatewayProtocol.Names.WorldPlanAck => "Legacy",
                    _ => throw new InvalidOperationException(request.Name)
                };
                var snapshot = new MultiWorldStartPlan { PlanId = "guard", Stage = stage,
                    HostConfig = new() { MultiWorldEnabled = true, AutoUpdateRoutes = false } };
                return Task.FromResult(Response(request, new { planId = "guard", ticket = "guard-ticket", snapshot }));
            };
            Assert.False(await Start(task, client));
            await (Task)typeof(AutoHoeingTask).GetMethod("LockCurrentHoeingRoomAsync",
                BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(task, new object[] { client })!;
            Assert.Equal(GatewayProtocol.Names.RoomMarkStarted, sent.Last());
            Assert.True(exclusions == null || exclusions.Count == 0);
            Assert.Null(Field<MultiWorldStartPlan?>(task, "_worldStartPlan"));
        }
        finally { dir.Delete(recursive: true); }
    }

    [Fact]
    public async Task FrozenStartRetriesLostReplyUsingOnlyTheOriginalRoom()
    {
        await using var client = await ClientInSourceRoom();
        var requests = new List<GatewayEnvelope>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            requests.Add(request);
            if (requests.Count == 1) throw new IOException("lost response after server locked the room");
            return Task.FromResult(Response(request, new { ack = true }));
        };
        await client.MarkFrozenRoomStartedAsync(CancellationToken.None);
        Assert.Equal(2, requests.Count);
        Assert.All(requests, r =>
        {
            Assert.Equal(GatewayProtocol.Names.RoomMarkStarted, r.Name);
            Assert.Equal("source-A", r.RoomCode);
            Assert.True(r.GetBool("requireWorldPlanRoster"));
        });
    }

    [Theory]
    [InlineData("B,C,D", "B")]
    [InlineData("C,D", "")]
    [InlineData("A,B,C,D", "A,B")]
    public void OldServerCroppingDoesNotBackfillWorldsOutsideTwoRoundPlan(string reply, string expected)
    {
        var players = new[] { "A", "D", "C", "B" }.Select(u => new PlayerInfo { PlayerUid = u }).ToList();
        var order = BetterGenshinImpact.GameTask.AutoHoeing.Services.RoundHostOrderClientMapping.BuildV1PlayerOrder(
            players, reply.Split(',').ToList(), "A", 2);
        Assert.Equal(expected, string.Join(",", order.Take(2).Select(p => p.PlayerUid)));
    }

    private static AutoHoeingTask CreateTask()
    {
        var task = new AutoHoeingTask();
        SetField(task, "_config", new AutoHoeingConfig
        {
            PlayerUid = "A", PlayerName = "A", ExpectedPlayerCount = 4,
            MultiplayerEnabled = true, MultiplayerRole = "host",
            MultiWorldEnabled = true,
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
                MultiWorldEnabled = true, MultiWorldCount = 2,
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
