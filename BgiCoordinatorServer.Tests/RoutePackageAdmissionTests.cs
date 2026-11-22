using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.Extensions.Logging;
using Moq;
using Xunit;

namespace BgiCoordinatorServer.Tests;

// Uses the real RoomOperations called by both /hub and /gateway, not a second admission implementation.
public sealed class RoutePackageAdmissionTests
{
    private static RoomOperations Operations(GatewayTestHarness h, RoutePackageStore store)
        => new(h.RoomManager, Mock.Of<ILogger<RoomOperations>>(),
            new GatewayBroadcaster(h.LegacyHub.Object, h.GatewayHub.Object, h.Tracker, Mock.Of<ILogger<GatewayBroadcaster>>()),
            new RoomPhaseObserver(Mock.Of<ILogger<RoomPhaseObserver>>()), h.Capabilities,
            routePackagePolicy: new RoutePackagePolicy(store, h.Tracker));

    private static GatewayHandlerContext Hello(GatewayTestHarness h, string id, string? version = null, bool legacy = false)
    {
        if (legacy) return GatewayHandlerContext.Legacy(id);
        h.Tracker.CompleteHello(id, new ClientHello { ClientKind = "bgi", ClientVersion = version ?? RoutePackageTestData.Version, ProtocolVersion = 3 });
        return GatewayHandlerContext.V3(id);
    }

    [Theory]
    [InlineData("")]
    [InlineData("1.0.0-alpha")]
    [InlineData("0.57.0")]
    [InlineData("0.57.0+lcb.20261116")]
    [InlineData("0.57.0+lcb.20261117 ")]
    public async Task ProductionCreateRejectsEmptyWildcardOldOrNonExactVersion(string version)
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var draft = await data.Upload(store);
        await store.PublishAsync(draft.Id, 0, "首版", "admin");
        var h = new GatewayTestHarness();
        var ops = Operations(h, store);
        foreach (var legacy in new[] { false, true })
        {
            var ctx = Hello(h, legacy ? "legacy" : "v3", version, legacy);
            await Assert.ThrowsAsync<InvalidOperationException>(() => ops.CreateRoomAsync(ctx, "房主", reportedVersion: version));
        }
        Assert.Empty(h.RoomManager.GetOnlineRooms());
    }

    [Theory]
    [InlineData("alpha")]
    public async Task InvalidPolicyVersionBlocksHoeingButNotControlRoomOrStatus(string? required)
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, required);
        var h = new GatewayTestHarness();
        var ops = Operations(h, store);
        var ctx = Hello(h, "host");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.CreateRoomAsync(ctx, reportedVersion: RoutePackageTestData.Version));
        await ops.JoinControlRoomAsync(ctx, "CONTROL", "pw", "uid", "控制成员", clientInstanceId: "instance");
        await ops.ReportControlStatusAsync(ctx, new ControlStatus { RoomCode = "CONTROL", PlayerUid = "uid", BgiVersion = "old", QuickCommands = new() { ["一键自定义"] = "GROUP:普通任务" } });
        Assert.Single(h.RoomManager.GetControlRoomPlayers("CTRL_CONTROL"));
        var quick = await ops.BeginManualCoordinatedBatchAsync(ctx, "quick", ["uid"], null, quickCommandKey: "一键自定义");
        Assert.Equal("一键自定义", quick.QuickCommandKey);
    }

    [Fact]
    public async Task UnpinnedVersionAllowsAnyClientOnceAPackageIsPublished()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, null);
        var h = new GatewayTestHarness();
        var ops = Operations(h, store);
        var closed = await Assert.ThrowsAsync<InvalidOperationException>(() => ops.CreateRoomAsync(Hello(h, "host"), reportedVersion: "9.9.9"));
        Assert.Equal("hoeing_route_package_uninitialized", closed.Message);
        var draft = await data.Upload(store);
        Assert.Equal("", draft.RequiredClientVersion);
        await store.PublishAsync(draft.Id, 0, "日常更新", "admin");
        var (code, _) = await ops.CreateRoomAsync(Hello(h, "host2"), "房主", playerUid: "A", expectedPlayerCount: 2, reportedVersion: "9.9.9");
        Assert.False(string.IsNullOrEmpty(code));
    }

    [Fact]
    public async Task ExactVersionWithoutInitialPublicationIsStillClosed()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var h = new GatewayTestHarness();
        var ops = Operations(h, store);
        var ctx = Hello(h, "host");
        var draft = await data.Upload(store);
        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() => ops.CreateRoomAsync(ctx, reportedVersion: RoutePackageTestData.Version));
        Assert.Equal("hoeing_route_package_uninitialized", ex.Message);
        Assert.False(store.IsPublishedAvailable(draft.Id));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PinIsRequiredFrozenAndRetainedAcrossPublicationAndReconnect(bool legacy)
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var a = await data.Upload(store);
        await store.PublishAsync(a.Id, 0, "首版", "admin");
        var h = new GatewayTestHarness();
        var ops = Operations(h, store);
        var host = Hello(h, "host", legacy: legacy);
        var member = Hello(h, "member", legacy: legacy);
        var (code, _) = await ops.CreateRoomAsync(host, "房主", playerUid: "A", expectedPlayerCount: 2, reportedVersion: RoutePackageTestData.Version);
        Assert.False((await ops.JoinRoomAsync(member, code, "成员", "B", RoutePackageTestData.Version)).Success);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.SetHostRouteListAsync(host, ["a"]));
        await ops.SetRoomConfigAsync(host, new RoomConfig { RoutePackageId = a.Id });
        Assert.True((await ops.JoinRoomAsync(member, code, "成员", "B", RoutePackageTestData.Version)).Success);
        var b = await data.Upload(store);
        await store.PublishAsync(b.Id, 1, "当前版前移", "admin");
        var change = await Assert.ThrowsAsync<InvalidOperationException>(() => ops.SetRoomConfigAsync(host, new RoomConfig { RoutePackageId = b.Id }));
        Assert.Equal("hoeing_route_package_frozen", change.Message);
        await ops.SetHostRouteListAsync(host, ["a"]);
        await ops.ReportHostReadyAsync(host);
        await ops.MarkRoomStartedAsync(host, null);
        Assert.Equal(a.Id, h.RoomManager.GetRoom(code)!.HostConfig!.RoutePackageId);
        var wrong = Hello(h, "member-new", "1.0.0", legacy);
        Assert.False((await ops.JoinRoomAsync(wrong, code, "成员", "B", "1.0.0")).Success);
        var recovered = Hello(h, "member-new", legacy: legacy);
        h.Tracker.Remove("member");
        Assert.True((await ops.JoinRoomAsync(recovered, code, "成员", "B", RoutePackageTestData.Version)).Success);
        Assert.Equal(a.Id, h.RoomManager.GetRoom(code)!.HostConfig!.RoutePackageId);
        if (!legacy)
        {
            h.Tracker.CompleteHello(host.ConnectionId, new ClientHello { ClientKind = "bgi", ClientVersion = "1.0.0", ProtocolVersion = 3 });
            await Assert.ThrowsAsync<InvalidOperationException>(() => ops.MarkRoomStartedAsync(host, null));
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("11111111111111111111111111111111")]
    public async Task LocalRouteFallbackAdmitsTheRoomAndFreezesTheModeAndId(string localId)
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var published = await data.Upload(store);
        await store.PublishAsync(published.Id, 0, "首版", "admin");
        var h = new GatewayTestHarness();
        var ops = Operations(h, store);
        var host = Hello(h, "host");
        var member = Hello(h, "member");
        var (code, _) = await ops.CreateRoomAsync(host, "房主", playerUid: "A", expectedPlayerCount: 2,
            reportedVersion: RoutePackageTestData.Version);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.SetRoomConfigAsync(host, new RoomConfig { RoutePackageId = localId }));
        await ops.SetRoomConfigAsync(host, new RoomConfig
        {
            RoutePackageId = localId, AutoUpdateRoutes = false, UseLocalRouteFallback = true
        });
        Assert.True((await ops.JoinRoomAsync(member, code, "成员", "B", RoutePackageTestData.Version)).Success);
        await ops.SetHostRouteListAsync(host, ["本地线路"]);
        await ops.ReportHostReadyAsync(host);
        var changed = await Assert.ThrowsAsync<InvalidOperationException>(() => ops.SetRoomConfigAsync(host,
            new RoomConfig { RoutePackageId = published.Id }));
        Assert.Equal("hoeing_route_package_frozen", changed.Message);
        var wrongVersion = Hello(h, "wrong", "1.0.0");
        Assert.False((await ops.JoinRoomAsync(wrongVersion, code, "错误版本", "C", "1.0.0")).Success);
    }

    [Fact]
    public async Task DraftAndCorruptedPinCannotBeInstalledAsHostConfig()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var first = await data.Upload(store);
        await store.PublishAsync(first.Id, 0, "首版", "admin");
        var draft = await data.Upload(store);
        var h = new GatewayTestHarness();
        var ops = Operations(h, store);
        var host = Hello(h, "host");
        var (code, _) = await ops.CreateRoomAsync(host, reportedVersion: RoutePackageTestData.Version);
        foreach (var id in new[] { "", "../escape", draft.Id })
            await Assert.ThrowsAsync<InvalidOperationException>(() => ops.SetRoomConfigAsync(host, new RoomConfig { RoutePackageId = id }));
        Assert.Null(h.RoomManager.GetRoom(code)!.HostConfig);
        await ops.SetRoomConfigAsync(host, new RoomConfig { RoutePackageId = first.Id });
    }

    [Fact]
    public async Task ControlHoeingChecksReportedBgiVersionRatherThanAssistantHelloVersion()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var draft = await data.Upload(store);
        await store.PublishAsync(draft.Id, 0, "首版", "admin");
        var h = new GatewayTestHarness();
        var ops = Operations(h, store);
        var ctx = GatewayHandlerContext.V3("assistant");
        h.Tracker.CompleteHello(ctx.ConnectionId, new ClientHello { ClientKind = "assistant", ClientVersion = "1.2.3", ProtocolVersion = 3 });
        await ops.JoinControlRoomAsync(ctx, "CONTROL", "pw", "uid", "成员", clientInstanceId: "instance");
        await ops.ReportControlStatusAsync(ctx, new ControlStatus { RoomCode = "CONTROL", PlayerUid = "uid", BgiVersion = "old", OnlineHoeingGroupNames = ["联机组"] });
        var oldSnapshot = Assert.Single(h.RoomManager.GetCoordinatedStartPlayers("CTRL_CONTROL"));
        Assert.Equal("old", oldSnapshot.BgiVersion);
        var rejected = await Assert.ThrowsAsync<InvalidOperationException>(() => ops.BeginManualCoordinatedBatchAsync(ctx, "bad", ["uid"], null));
        Assert.Equal("hoeing_required_version_mismatch", rejected.Message);
        await ops.ReportControlStatusAsync(ctx, new ControlStatus { RoomCode = "CONTROL", PlayerUid = "uid", BgiVersion = RoutePackageTestData.Version, OnlineHoeingGroupNames = ["联机组"] });
        Assert.Equal("old", oldSnapshot.BgiVersion); // 已获取的快照不随下一次上报改变。
        Assert.Equal(RoutePackageTestData.Version, Assert.Single(h.RoomManager.GetCoordinatedStartPlayers("CTRL_CONTROL")).BgiVersion);
        Assert.Equal("registering", (await ops.BeginManualCoordinatedBatchAsync(ctx, "good", ["uid"], null)).Phase);
    }

    [Fact]
    public async Task WorldPlanTargetAllocationAndItsRetryRevalidateHelloAndPreserveTheOriginalPin()
    {
        using var data = new RoutePackageTestData();
        using var store = new RoutePackageStore(data.StoreRoot, RoutePackageTestData.Version);
        var a = await data.Upload(store);
        await store.PublishAsync(a.Id, 0, "首版", "admin");
        var h = new GatewayTestHarness();
        var ops = Operations(h, store);
        var host = Hello(h, "host"); var member = Hello(h, "member");
        await ops.CreateRoomAsync(host, "房主", playerUid: "A", expectedPlayerCount: 2, reportedVersion: RoutePackageTestData.Version);
        var source = h.RoomManager.GetRoomByConnectionId(host.ConnectionId).Room!;
        await ops.SetRoomConfigAsync(host, new RoomConfig { RoutePackageId = a.Id, MultiWorldEnabled = true, MultiWorldCount = 2 });
        Assert.True((await ops.JoinRoomAsync(member, source.Code, "成员", "B", RoutePackageTestData.Version)).Success);
        var hostAdmission = await ops.BeginWorldPlanAsync(host);
        var memberAdmission = (await ops.AcquireWorldPlanAsync(member))!;
        await ops.ReportWorldPlanAsync(hostAdmission.PlanId, hostAdmission.Ticket, true, 0);
        await ops.ReportWorldPlanAsync(memberAdmission.PlanId, memberAdmission.Ticket, true, 1);
        await ops.AckWorldPlanAsync(hostAdmission.PlanId, hostAdmission.Ticket);
        await ops.AckWorldPlanAsync(memberAdmission.PlanId, memberAdmission.Ticket);
        await ops.LeaveWorldPlanSourceAsync(host, hostAdmission.PlanId, hostAdmission.Ticket);
        await ops.LeaveWorldPlanSourceAsync(member, memberAdmission.PlanId, memberAdmission.Ticket);
        var b = await data.Upload(store);
        await store.PublishAsync(b.Id, 1, "新包不能改变既定轮次", "admin");
        var reconnect = Hello(h, "member-reconnect", "1.0.0");
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.CreateWorldPlanTargetAsync(reconnect, memberAdmission.PlanId, memberAdmission.Ticket));
        Assert.Empty(h.RoomManager.GetOnlineRooms());
        reconnect = Hello(h, "member-reconnect");
        var target = await ops.CreateWorldPlanTargetAsync(reconnect, memberAdmission.PlanId, memberAdmission.Ticket);
        Assert.Equal(a.Id, h.RoomManager.GetRoom(target.TargetRoomCode!)!.HostConfig!.RoutePackageId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.SetRoomConfigAsync(reconnect, new RoomConfig { RoutePackageId = b.Id }));
        h.Tracker.Remove(reconnect.ConnectionId);
        await Assert.ThrowsAsync<InvalidOperationException>(() => ops.CreateWorldPlanTargetAsync(reconnect, memberAdmission.PlanId, memberAdmission.Ticket));
        Assert.Equal(target.TargetRoomCode, h.RoomManager.GetRoomByConnectionId(reconnect.ConnectionId).RoomCode);
    }
}
