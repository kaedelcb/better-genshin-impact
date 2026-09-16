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
/// Pull 执行编排测试（route-anchor · 阶段 4）。
///
/// 这是把"服务端要求跳路线"接进路线循环前必须钉死的顺序契约：
///   目标非法 → 回报失败（不跳）；不可中断 → 回报失败（不跳）；
///   只有中断确认成功后才回报成功，并给出正确的循环变量值。
/// 漏执行路线不会补跑，所以宁可回报失败整队停止，也不能带着不确定继续。
/// </summary>
public class RouteAnchorPullExecutorTests : IDisposable
{
    private const string Plan = "plan-1";
    private const string AnchorId = "route-boundary:server-session:2:plan-1:12:1";

    private readonly CoordinatorClient _client;
    private readonly BgiGatewayClient _gateway;
    private readonly List<GatewayEnvelope> _invoked = new();

    public RouteAnchorPullExecutorTests()
    {
        _client = new CoordinatorClient();
        _client._testIsConnectedOverride = true;
        _gateway = _client.GetOrCreateGatewayForTest();
        _gateway._testInvokeOverride = (_, env, _) =>
        {
            lock (_invoked) _invoked.Add(env);
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = env.Name,
                Payload = GatewayEnvelope.ToPayload(new
                {
                    anchor = new
                    {
                        hasAnchor = true,
                        anchorId = AnchorId,
                        sessionId = "server-session",
                        worldEpoch = 2,
                        planId = Plan,
                        phase = "Pulling",
                        completedRouteIndex = 12,
                        nextRouteIndex = 13,
                        members = Array.Empty<string>(),
                        memberStates = new Dictionary<string, string>(),
                        pullCommandIds = new Dictionary<string, string>(),
                        pullAttempts = new Dictionary<string, int>(),
                        myState = "PullRequested",
                        myPullCommandId = "",
                        released = false,
                        stopped = false,
                        revision = 1,
                    },
                }),
            });
        };
    }

    public void Dispose() => _client.DisposeAsync().AsTask().Wait();

    private static GatewayEnvelope PullEvt(int target, string commandId = "cmd-1") => new()
    {
        Type = GatewayProtocol.MessageTypes.Event,
        Name = GatewayProtocol.Events.SyncRouteAnchorPull,
        Payload = GatewayEnvelope.ToPayload(new
        {
            anchorId = AnchorId,
            sessionId = "server-session",
            worldEpoch = 2,
            planId = Plan,
            commandId,
            targetRouteIndex = target,
            attempt = 1,
        }),
        SentAtUtc = DateTime.UtcNow,
    };

    private async Task<RouteAnchorClient> EnrolledAnchorAsync()
    {
        var anchor = new RouteAnchorClient(_client) { Enabled = true };
        await anchor.EnrollAsync(Plan, 12, 13, CancellationToken.None);
        return anchor;
    }

    private bool AckedSuccess(bool success)
    {
        lock (_invoked)
        {
            var ack = _invoked.FindAll(e => e.Name == GatewayProtocol.Names.RouteAnchorPullAck);
            if (ack.Count == 0) return false;
            var last = ack[^1];
            return last.Payload!["success"]!.GetValue<bool>() == success;
        }
    }

    // =========================================================================
    // 无命令 / 非法目标
    // =========================================================================

    [Fact]
    public async Task NoPendingPull_ReturnsNoCommand_WithoutSendingAck()
    {
        var anchor = await EnrolledAnchorAsync();
        var executor = new RouteAnchorPullExecutor(anchor);

        var result = await executor.ExecuteIfPendingAsync(5, 0, 20, _ => Task.FromResult(true), CancellationToken.None);

        Assert.Equal(RouteAnchorPullExecutionResult.NoCommand, result.Result);
        Assert.False(result.ShouldStopRound);
        lock (_invoked) Assert.Empty(_invoked.FindAll(e => e.Name == GatewayProtocol.Names.RouteAnchorPullAck));
    }

    [Theory]
    [InlineData(5)]     // 等于当前：非法
    [InlineData(3)]     // 向后：非法
    [InlineData(20)]    // 越界：非法
    public async Task InvalidTarget_ReportsFailure_AndNeverAborts(int target)
    {
        var anchor = await EnrolledAnchorAsync();
        _client.DispatchEvt(PullEvt(target));
        var executor = new RouteAnchorPullExecutor(anchor);
        var abortCalled = false;

        var result = await executor.ExecuteIfPendingAsync(5, 0, 20,
            _ => { abortCalled = true; return Task.FromResult(true); }, CancellationToken.None);

        Assert.Equal(RouteAnchorPullExecutionResult.InvalidTarget, result.Result);
        Assert.True(result.ShouldStopRound);
        Assert.False(abortCalled);                 // 非法目标不得先中断当前路线
        Assert.True(AckedSuccess(false));          // 必须如实回报失败
    }

    // =========================================================================
    // 不可中断
    // =========================================================================

    [Fact]
    public async Task AbortFails_ReportsFailure_AndDoesNotClaimApplied()
    {
        var anchor = await EnrolledAnchorAsync();
        _client.DispatchEvt(PullEvt(10));
        var executor = new RouteAnchorPullExecutor(anchor);

        var result = await executor.ExecuteIfPendingAsync(5, 0, 20,
            _ => Task.FromResult(false), CancellationToken.None);

        Assert.Equal(RouteAnchorPullExecutionResult.AbortFailed, result.Result);
        Assert.True(result.ShouldStopRound);
        Assert.True(AckedSuccess(false));
    }

    [Fact]
    public async Task AbortThrows_IsTreatedAsFailure_NotAsApplied()
    {
        var anchor = await EnrolledAnchorAsync();
        _client.DispatchEvt(PullEvt(10));
        var executor = new RouteAnchorPullExecutor(anchor);

        var result = await executor.ExecuteIfPendingAsync(5, 0, 20,
            _ => throw new InvalidOperationException("path executor busy"), CancellationToken.None);

        Assert.Equal(RouteAnchorPullExecutionResult.AbortFailed, result.Result);
        Assert.True(AckedSuccess(false));
    }

    // =========================================================================
    // 成功路径
    // =========================================================================

    [Fact]
    public async Task AbortSucceeds_ReportsApplied_AndReturnsLoopIndexTargetMinusOne()
    {
        var anchor = await EnrolledAnchorAsync();
        _client.DispatchEvt(PullEvt(10));
        var executor = new RouteAnchorPullExecutor(anchor);
        var abortCalled = false;

        var result = await executor.ExecuteIfPendingAsync(5, 0, 20,
            _ => { abortCalled = true; return Task.FromResult(true); }, CancellationToken.None);

        Assert.True(abortCalled);
        Assert.Equal(RouteAnchorPullExecutionResult.Applied, result.Result);
        Assert.False(result.ShouldStopRound);
        Assert.Equal(10, result.TargetRouteIndex);
        Assert.Equal(9, result.RequiredLoopIndex);   // 写 9，for 自增后恰好落到 10
        Assert.True(AckedSuccess(true));
        Assert.Null(anchor.TryGetPullCommand());     // 成功回报后清空
    }

    [Fact]
    public async Task AppliedResult_LoopIndex_LandsExactlyOnTarget()
    {
        var anchor = await EnrolledAnchorAsync();
        _client.DispatchEvt(PullEvt(7));
        var executor = new RouteAnchorPullExecutor(anchor);

        var result = await executor.ExecuteIfPendingAsync(2, 0, 20, _ => Task.FromResult(true), CancellationToken.None);

        var routeIndex = result.RequiredLoopIndex;
        routeIndex++;   // for 的 routeIndex++
        Assert.Equal(7, routeIndex);
    }

    [Fact]
    public async Task CancelledToken_Propagates_WithoutClaimingApplied()
    {
        var anchor = await EnrolledAnchorAsync();
        _client.DispatchEvt(PullEvt(10));
        var executor = new RouteAnchorPullExecutor(anchor);
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            executor.ExecuteIfPendingAsync(5, 0, 20,
                _ => throw new OperationCanceledException(), cts.Token));

        Assert.False(AckedSuccess(true));   // 绝不把取消当成功
    }
}
