using System.Reflection;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.Service;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

[Collection("WorldPlanStartupState")]
public sealed class MultiWorldConfigLockingBugTests
{
    private const string RoutePin = "11111111111111111111111111111111";

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FirstHostPinsTheAcknowledgedConfigOnlyInMultiWorld(bool multiWorld)
    {
        await using var client = ConnectedClient();
        var task = CreateTask(multiWorld);
        var room = Room(multiWorld);
        var requests = CaptureGatewayRequests(client);

        Assert.True(await task.UploadFirstHostConfigAsync(client, room));
        Assert.Equal(3, ReadConfigUpload(Assert.Single(requests)).MultiWorldCount);
        if (multiWorld)
            Assert.Same(room, Field("_firstHostConfig").GetValue(task));
        else
            Assert.Null(Field("_firstHostConfig").GetValue(task));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MemberAppliesFirstHostConfigAndPinsItOnlyInMultiWorld(bool multiWorld)
    {
        await using var client = ConnectedClient();
        var task = CreateTask(multiWorld: false);
        var room = Room(multiWorld);
        var installs = new List<string?>();
        task.UpdateHoeingRoutes = (pin, _, _) =>
        {
            installs.Add(pin);
            return Task.FromResult(RoutePin);
        };

        client.GetOrCreateGatewayForTest()._testInvokeOverride = (method, request, _) =>
        {
            Assert.Equal(GatewayProtocol.HubMethods.Query, method);
            Assert.Equal(GatewayProtocol.Names.RoomGetConfig, request.Name);
            return Task.FromResult(Response(request, new { config = room }));
        };

        var configProperty = typeof(ConfigService).GetProperty(nameof(ConfigService.Config))!;
        var originalConfig = ConfigService.Config;
        var originalTimeout = PathingConditionConfig.MultiplayerFightTimeoutOverride;
        var originalStrategy = PathingConditionConfig.MultiplayerUseFixedFightStrategyOverride;
        configProperty.SetValue(null, new AllConfig());
        try
        {
            Assert.True(await task.SyncFirstHostConfigAsync(client));
            Assert.Equal(RoutePin, Assert.Single(installs));
            var local = Assert.IsType<AutoHoeingConfig>(Field("_config").GetValue(task));
            Assert.Equal(3, local.MultiWorldCount);
            Assert.Equal(multiWorld, local.MultiWorldEnabled);
            if (multiWorld)
                Assert.Equal(RoutePin, Assert.IsType<RoomConfig>(Field("_firstHostConfig").GetValue(task)).RoutePackageId);
            else
                Assert.Null(Field("_firstHostConfig").GetValue(task));
        }
        finally
        {
            PathingConditionConfig.MultiplayerFightTimeoutOverride = originalTimeout;
            PathingConditionConfig.MultiplayerUseFixedFightStrategyOverride = originalStrategy;
            configProperty.SetValue(null, originalConfig);
        }
    }

    [Fact]
    public async Task MultiWorldMemberStopsAfterThreeMissingConfigs()
    {
        await using var client = ConnectedClient();
        await using var monitor = new WorldStateMonitor(client, "member") { IsPartyPhase = true };
        var task = CreateTask(multiWorld: true);
        Field("_worldStateMonitor").SetValue(task, monitor);
        var requests = new List<string>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            requests.Add(request.Name);
            return Task.FromResult(Response(request, new { config = (RoomConfig?)null }));
        };

        Assert.False(await task.SyncFirstHostConfigAsync(client));
        Assert.Equal(Enumerable.Repeat(GatewayProtocol.Names.RoomGetConfig, 3), requests);
        Assert.False(monitor.IsPartyPhase);
        Assert.True(Assert.IsType<bool>(Field("_partyFailed").GetValue(task)));
        Assert.Null(Field("_firstHostConfig").GetValue(task));
    }

    [Fact]
    public async Task LaterHostWithoutPinnedConfigStopsBeforeUploadingAnyFallback()
    {
        await using var client = ConnectedClient();
        var task = CreateTask(multiWorld: true);
        var requests = CaptureGatewayRequests(client);
        Field("_multiplayerCoordinator").SetValue(task, new MultiplayerCoordinator(
            client, new SyncPointResolver(), new AutoHoeingConfig()));

        Assert.False(await task.UploadLaterRoundHostConfigAsync(client, round: 1));
        Assert.Empty(requests);
        Assert.True(Assert.IsType<bool>(Field("_partyFailed").GetValue(task)));
        Assert.Null(Field("_multiplayerCoordinator").GetValue(task));
    }

    [Fact]
    public async Task LaterHostUploadsTheFirstHostConfigInsteadOfItsLocalSettings()
    {
        await using var client = ConnectedClient();
        var task = CreateTask(multiWorld: true);
        var requests = CaptureGatewayRequests(client);
        Assert.True(await task.UploadFirstHostConfigAsync(client, Room(multiWorld: true)));
        var local = Assert.IsType<AutoHoeingConfig>(Field("_config").GetValue(task));
        local.MultiWorldCount = 4;
        local.StartRouteIndex = 5;

        Assert.True(await task.UploadLaterRoundHostConfigAsync(client, round: 1));
        Assert.Equal(2, requests.Count);
        Assert.All(requests.Select(ReadConfigUpload), config =>
        {
            Assert.Equal(3, config.MultiWorldCount);
            Assert.Equal(2, config.StartRouteIndex);
            Assert.Equal(RoutePin, config.RoutePackageId);
        });
    }

    private static CoordinatorClient ConnectedClient() => new() { _testIsConnectedOverride = true };

    private static RoomConfig Room(bool multiWorld) => new()
    {
        MultiWorldEnabled = multiWorld,
        MultiWorldCount = 3,
        StartRouteIndex = 2,
        RoutePackageId = RoutePin
    };

    private static AutoHoeingTask CreateTask(bool multiWorld)
    {
        var task = new AutoHoeingTask();
        Field("_config").SetValue(task, new AutoHoeingConfig { MultiplayerEnabled = true, MultiWorldEnabled = multiWorld });
        return task;
    }

    private static List<(string Method, GatewayEnvelope Request)> CaptureGatewayRequests(CoordinatorClient client)
    {
        var calls = new List<(string Method, GatewayEnvelope Request)>();
        client.GetOrCreateGatewayForTest()._testInvokeOverride = (method, request, _) =>
        {
            calls.Add((method, request));
            return Task.FromResult(Response(request, new { }));
        };
        return calls;
    }

    private static RoomConfig ReadConfigUpload((string Method, GatewayEnvelope Request) call)
    {
        Assert.Equal(GatewayProtocol.HubMethods.Dispatch, call.Method);
        Assert.Equal(GatewayProtocol.Names.RoomSetConfig, call.Request.Name);
        return Assert.IsType<RoomConfig>(call.Request.Get<RoomConfig>("config"));
    }

    private static GatewayEnvelope Response(GatewayEnvelope request, object payload) => new()
    {
        Name = request.Name,
        Type = GatewayProtocol.MessageTypes.Response,
        Payload = GatewayEnvelope.ToPayload(payload)
    };

    private static FieldInfo Field(string name)
        => typeof(AutoHoeingTask).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!;
}
