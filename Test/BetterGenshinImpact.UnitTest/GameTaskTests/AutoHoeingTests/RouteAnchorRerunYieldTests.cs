#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 协作重跑窗口内锚点让位（方案 §9.4：不得同时运行两套路线释放权威）。
///
/// 背景：领先成员跑完循环 → Finished 收口 → 进入重跑（服务端 BlocksLegacyAdvancement=true）；
/// 落后成员仍在正常循环里到达边界并 Enroll。若不处理，两套推进机制同时活跃。
/// 修法：服务端在重跑窗口拒绝 Enroll（rerun_in_progress）；客户端把该拒绝识别为"让位并继续"。
///
/// 本文件同时锁定**放行集合的窄化边界**——只新增 YieldedToRerun 这一例，其余一律不放行。
/// </summary>
public class RouteAnchorRerunYieldTests : IDisposable
{
    private const string Plan = "plan-1";

    private readonly CoordinatorClient _client;
    private readonly BgiGatewayClient _gateway;

    public RouteAnchorRerunYieldTests()
    {
        _client = new CoordinatorClient();
        _client._testIsConnectedOverride = true;
        _gateway = _client.GetOrCreateGatewayForTest();
    }

    public void Dispose() => _client.DisposeAsync().AsTask().Wait();

    private void FakeServerRejectsEnrollWith(string errorCode)
    {
        _gateway._testInvokeOverride = (_, env, _) => Task.FromResult(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Response,
            Name = env.Name,
            Payload = GatewayEnvelope.ToPayload(new
            {
                error = new { code = "bad_request", routeAnchorReason = errorCode, message = $"route_anchor:{errorCode}" },
            }),
        });
    }

    // =========================================================================
    // 放行集合：只多一例，其余一律不放行
    // =========================================================================

    [Fact]
    public void AllowsNextRoute_AllowsExactlyReleasedAndYieldedToRerun()
    {
        Assert.True(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.Released));
        Assert.True(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.YieldedToRerun));

        // 窄化边界：其余结果必须**全部**仍为不放行
        Assert.False(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.Stopped));
        Assert.False(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.Failed));
        Assert.False(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.Cancelled));
        Assert.False(RouteAnchorActivationDecisions.AllowsNextRoute(RouteAnchorWaitResult.Disabled));

        // 枚举全量核对：放行集合恰好 2 个取值（防止将来新增取值时被默认放行）
        var allowed = new List<RouteAnchorWaitResult>();
        foreach (var value in Enum.GetValues<RouteAnchorWaitResult>())
        {
            if (RouteAnchorActivationDecisions.AllowsNextRoute(value)) allowed.Add(value);
        }
        Assert.Equal(new[] { RouteAnchorWaitResult.Released, RouteAnchorWaitResult.YieldedToRerun }, allowed);
    }

    // =========================================================================
    // 重跑窗口：让位并继续（不判失败）
    // =========================================================================

    [Fact]
    public async Task CompleteBoundary_RerunInProgress_YieldsAndContinues()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        FakeServerRejectsEnrollWith("rerun_in_progress");

        var result = await anchor.CompleteBoundaryAsync(Plan, 12, 13, "completed", CancellationToken.None);

        Assert.Equal(RouteAnchorWaitResult.YieldedToRerun, result);
        Assert.True(RouteAnchorActivationDecisions.AllowsNextRoute(result));   // 允许继续本轮
        Assert.Equal("rerun_in_progress", _client.LastRouteAnchorErrorCode);
    }

    [Theory]
    [InlineData("[gateway:bad_request] route_anchor:rerun_in_progress", "rerun_in_progress")]
    [InlineData("[gateway:bad_request] route_anchor:config_disabled", "config_disabled")]
    [InlineData("[gateway:bad_request] route_anchor:capability_required", "capability_required")]
    [InlineData("route_anchor:stale_boundary", "stale_boundary")]
    [InlineData("完全无关的错误消息", "")]
    [InlineData("", "")]
    [InlineData(null, "")]
    public void ExtractAnchorErrorReason_OnlyAcceptsKnownPrefix(string? message, string expected)
        => Assert.Equal(expected, CoordinatorClient.ExtractAnchorErrorReason(message));

    [Fact]
    public async Task CompleteBoundary_OtherRejection_StillFails()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        FakeServerRejectsEnrollWith("config_disabled");

        var result = await anchor.CompleteBoundaryAsync(Plan, 12, 13, "completed", CancellationToken.None);

        Assert.Equal(RouteAnchorWaitResult.Failed, result);
        Assert.False(RouteAnchorActivationDecisions.AllowsNextRoute(result));
    }

    [Fact]
    public async Task CompleteBoundary_CapabilityRejection_StillFails()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        FakeServerRejectsEnrollWith("capability_required");

        var result = await anchor.CompleteBoundaryAsync(Plan, 12, 13, "completed", CancellationToken.None);

        Assert.Equal(RouteAnchorWaitResult.Failed, result);
    }

    [Fact]
    public async Task LastRouteAnchorErrorCode_IsClearedOnSuccess()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        FakeServerRejectsEnrollWith("rerun_in_progress");
        await anchor.CompleteBoundaryAsync(Plan, 12, 13, "completed", CancellationToken.None);
        Assert.Equal("rerun_in_progress", _client.LastRouteAnchorErrorCode);

        // 重跑结束后恢复正常：成功调用必须清掉上一次的错误码，避免"粘住"导致后续误让位
        _gateway._testInvokeOverride = (_, env, _) => Task.FromResult(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Response,
            Name = env.Name,
            Payload = GatewayEnvelope.ToPayload(new
            {
                anchor = new
                {
                    hasAnchor = true,
                    anchorId = "route-boundary:server-session:2:plan-1:12:1",
                    sessionId = "server-session",
                    worldEpoch = 2,
                    planId = Plan,
                    phase = "Collecting",
                    completedRouteIndex = 12,
                    nextRouteIndex = 13,
                    members = Array.Empty<string>(),
                    memberStates = new Dictionary<string, string>(),
                    pullCommandIds = new Dictionary<string, string>(),
                    pullAttempts = new Dictionary<string, int>(),
                    myState = "Pathing",
                    myPullCommandId = "",
                    released = false,
                    stopped = false,
                    revision = 1,
                },
            }),
        });

        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);

        Assert.Equal("", _client.LastRouteAnchorErrorCode);
    }
}
