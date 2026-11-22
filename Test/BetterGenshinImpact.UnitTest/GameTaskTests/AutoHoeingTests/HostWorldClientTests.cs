using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.ViewModel.Pages.View;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

public sealed class HostWorldClientTests
{
    [Fact]
    public void ConfigurationIsOffByDefaultAndOwnedByHost()
    {
        var settings = new Dictionary<string, object?>();
        var config = new AutoHoeingConfig();
        var vm = new MultiplayerHoeingSettingsViewModel(settings, config, ["1"], "1");
        Assert.False(vm.HostProvidesWorldOnly);
        vm.HostProvidesWorldOnly = true;
        vm.WriteMultiplayerSettings(settings);
        Assert.True(Assert.IsType<bool>(settings["hostProvidesWorldOnly"]));
        vm.RoleSelection = "成员（加入房间）";
        vm.HostProvidesWorldOnly = false;
        vm.WriteMultiplayerSettings(settings);
        Assert.True(Assert.IsType<bool>(settings["hostProvidesWorldOnly"]));
    }

    [Theory]
    [InlineData(true, false, false, false, false, true)]
    [InlineData(false, false, false, false, false, false)]
    [InlineData(true, true, false, false, false, false)]
    [InlineData(true, false, true, false, false, false)]
    [InlineData(true, false, false, true, false, false)]
    [InlineData(true, false, false, false, true, false)]
    public void OnlyRegularUnmanagedSingleWorldIsAllowed(bool multiplayer, bool multiWorld, bool debug,
        bool soloDebug, bool managed, bool accepted)
    {
        Assert.Equal(accepted, AutoHoeingTask.HostWorldModeAllowed(multiplayer, multiWorld, debug, soloDebug, managed));
    }

    [Fact]
    public async Task AStandbyRoomJoinReceiptMustNameItsInstance()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testInvokeOverride = (_, request, _) => Task.FromResult(Reply(request, new { success = true, hostProvidesWorldOnly = true }));
        Assert.False(await client.JoinRoomAsync("code", "guest", "guest"));
    }

    [Fact]
    public async Task FrozenExecutorSetDoesNotHideThePhysicalHostOrAcceptADifferentRoster()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await JoinHostWorld(client);
        var state = new HostWorldState("instance", "host", ["guest"], "running", ["route.json"], null, new string('a', 64));
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testInvokeOverride = (_, request, _) =>
        {
            Assert.Equal("code", request.RoomCode);
            Assert.Equal("instance", request.GetString("sessionId"));
            return Task.FromResult(Reply(request, new { state }));
        };
        client.HostPlayerUid = "host";
        client.CurrentRoomPlayerCount = 2;
        Assert.Equal("running", (await client.ReadOrUpdateHostWorldAsync(CancellationToken.None)).Phase);
        Assert.Equal("host", client.HostPlayerUid);
        Assert.Equal(2, client.CurrentRoomPlayerCount);
        Assert.Equal(1, client.FrozenRouteExecutorCount);
        Assert.False(client.IsRouteExecutor("host"));
        Assert.True(client.IsRouteExecutor("guest"));
        state = state with { ExecutorUids = ["guest", "late"] };
        await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() => client.ReadOrUpdateHostWorldAsync(CancellationToken.None));
    }

    [Fact]
    public async Task ClosedRoomNeedsRealTerminalReceiptAndCanReplayItAfterLostAcknowledgement()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await JoinHostWorld(client);
        client.EndRoomSession("closed");
        var state = new HostWorldState("instance", "host", ["guest"], "running", ["route.json"], null, new string('a', 64));
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testInvokeOverride = (_, request, _) => Task.FromResult(Reply(request, new { state }));
        await Assert.ThrowsAsync<MultiplayerSessionFailedException>(() => client.ReadOrUpdateHostWorldAsync(CancellationToken.None));
        state = state with { Phase = "completed" };
        Assert.True((await client.ReadOrUpdateHostWorldAsync(CancellationToken.None, "finish")).IsSuccess);
    }

    [Fact]
    public async Task CancellingARequestWinsOverAReturnedSuccess()
    {
        await using var client = new CoordinatorClient { _testIsConnectedOverride = true };
        await JoinHostWorld(client);
        using var stop = new CancellationTokenSource();
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testInvokeOverride = (_, request, _) =>
        {
            stop.Cancel();
            return Task.FromResult(Reply(request, new { state = new HostWorldState("instance", "host", ["guest"], "noRoutes", [], null) }));
        };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => client.ReadOrUpdateHostWorldAsync(stop.Token));
    }

    [Fact]
    public void AnExplicitEmptyManifestIsDifferentFromAMissingManifestOrNoExecutor()
    {
        var state = new HostWorldState("instance", "host", ["guest"], "noRoutes", [], null);
        state.RequireIdentity("instance", "host", true, "host", null);
        Assert.Throws<MultiplayerSessionFailedException>(() => (state with { RouteNames = null })
            .RequireIdentity("instance", "host", true, "host", null));
        Assert.Throws<MultiplayerSessionFailedException>(() => (state with { ExecutorUids = [] })
            .RequireIdentity("instance", "host", true, "host", null));
    }

    [Fact]
    public void ALocalFallbackFileWithTheSameNameNeedsThePublishedContent()
    {
        var route = Path.Combine(AppContext.BaseDirectory, $"host-world-digest-{Guid.NewGuid():N}.json");
        try
        {
            File.WriteAllText(route, "first route content");
            var original = AutoHoeingTask.ComputeHostWorldManifestDigest([route], CancellationToken.None);
            File.WriteAllText(route, "different route content");
            Assert.NotEqual(original, AutoHoeingTask.ComputeHostWorldManifestDigest([route], CancellationToken.None));
        }
        finally { File.Delete(route); }
    }

    private static async Task JoinHostWorld(CoordinatorClient client)
    {
        var gateway = client.GetOrCreateGatewayForTest();
        gateway._testInvokeOverride = (_, request, _) => Task.FromResult(Reply(request,
            new { success = true, hostProvidesWorldOnly = true, standbySessionId = "instance" }));
        Assert.True(await client.JoinRoomAsync("code", "guest", "guest"));
    }

    private static GatewayEnvelope Reply(GatewayEnvelope request, object payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Response,
        Name = request.Name,
        Payload = GatewayEnvelope.ToPayload(payload),
    };
}
