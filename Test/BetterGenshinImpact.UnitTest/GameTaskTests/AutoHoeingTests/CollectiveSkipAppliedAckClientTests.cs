#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoPathing;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>
/// 集体跳段 Applied 确认的客户端侧测试（collective-skip-applied-ack）。
///
/// 修复目标：原实现把服务端跳段广播直接变成一个信号位，客户端无 SkipId 概念，
/// 服务端也无从知道谁执行了跳段 → 部分成员照旧走旧路线，各打各的。
///
/// 本文件断言客户端侧契约：
///   1) 带 skipId 的请求可被消费一次，服务端重播同一 skipId 不会重复跳段（幂等）；
///   2) 旧服务端（无 skipId）行为与旧实现一致：能跳段、不回报；
///   3) 消费后回报 Applied，同一 skipId 只回报一次；
///   4) 收到 AppliedAll 才结束活动跳段；过期 skipId 的确认被忽略；
///   5) 新轮次复位不继承旧跳段状态；
///   6) 旧的 RequestSkipToProgressReceived 事件仍照发（旧订阅方零感知）；
///   7) 集体跳段用专用异常类型，与落后追赶/卡死保护的异常类型互不混淆。
/// </summary>
public class CollectiveSkipAppliedAckClientTests : IDisposable
{
    private const string SkipEvent = "sync.requestSkipToProgress";
    private const string AppliedAllEvent = "sync.collectiveSkipAppliedAll";
    private const string AppliedCommand = "sync.reportCollectiveSkipApplied";

    private readonly CoordinatorClient _client;
    private readonly BgiGatewayClient _gateway;
    private readonly List<GatewayEnvelope> _sentEnvelopes = new();

    public CollectiveSkipAppliedAckClientTests()
    {
        RemoteSkipGate.Reset();
        _client = new CoordinatorClient();
        _client._testIsConnectedOverride = true;
        _gateway = _client.GetOrCreateGatewayForTest();
        // 默认拦截所有出站信封（回报 Applied 走 Dispatch）
        _gateway._testSendOverride = (_, env, _) =>
        {
            lock (_sentEnvelopes) _sentEnvelopes.Add(env);
            return Task.CompletedTask;
        };
        _gateway._testInvokeOverride = (_, env, _) =>
        {
            lock (_sentEnvelopes) _sentEnvelopes.Add(env);
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = env.Name,
            });
        };
    }

    public void Dispose()
    {
        RemoteSkipGate.Reset();
        _client.DisposeAsync().AsTask().Wait();
    }

    private MultiplayerCoordinator CreateCoordinator()
        => new(_client, new SyncPointResolver(), new AutoHoeingConfig());

    private static GatewayEnvelope Evt(string name, object? payload) => new()
    {
        Type = GatewayProtocol.MessageTypes.Event,
        Name = name,
        Payload = GatewayEnvelope.ToPayload(payload),
        SentAtUtc = DateTime.UtcNow,
    };

    private List<GatewayEnvelope> SentNamed(string name)
    {
        lock (_sentEnvelopes) return _sentEnvelopes.FindAll(e => e.Name == name);
    }

    // =========================================================================
    // 1) 幂等：同一 skipId 只消费一次（服务端重播不重复跳段）
    // =========================================================================

    [Fact]
    public void RequestWithSkipId_ConsumedOnce_ReplayOfSameSkipIdIgnored()
    {
        var coordinator = CreateCoordinator();
        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-1", targetProgress = 3_000_000L }));

        Assert.True(coordinator.TryConsumeCollectiveSkip(out var request));
        Assert.NotNull(request);
        Assert.Equal("skip-1", request!.SkipId);
        Assert.Equal(3_000_000L, request.TargetProgress);

        // 服务端重播（同一 SkipId）
        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-1", targetProgress = 3_000_000L }));

        Assert.False(coordinator.TryConsumeCollectiveSkip(out var replay));
        Assert.Null(replay);
    }

    [Fact]
    public void RequestWithNewSkipId_WhilePreviousActive_IsAcceptedAsPending_ButNotDoubleConsumed()
    {
        var coordinator = CreateCoordinator();
        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-1", targetProgress = 3_000_000L }));
        Assert.True(coordinator.TryConsumeCollectiveSkip(out _));

        // 新世代 SkipId（服务端清理旧活动跳段后的新命令）应可被受理
        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-2", targetProgress = 4_000_000L }));

        Assert.True(coordinator.TryConsumeCollectiveSkip(out var second));
        Assert.Equal("skip-2", second!.SkipId);
        // 同一条命令不会被消费两次
        Assert.False(coordinator.TryConsumeCollectiveSkip(out _));
    }

    // =========================================================================
    // 2) 旧服务端（无 skipId）：行为与旧实现一致
    // =========================================================================

    [Fact]
    public async Task LegacyServerWithoutSkipId_StillSkips_ButNeverReportsApplied()
    {
        var coordinator = CreateCoordinator();
        _client.DispatchEvt(Evt(SkipEvent, new { targetProgress = 3_000_000L }));

        Assert.True(coordinator.TryConsumeCollectiveSkip(out var request));
        Assert.Equal("", request!.SkipId);
        Assert.False(request.HasSkipId);

        await coordinator.ReportCollectiveSkipAppliedAsync(true, 3_000_000L, "");

        Assert.Empty(SentNamed(AppliedCommand));
    }

    // =========================================================================
    // 3) 回报 Applied：同一 skipId 只回报一次
    // =========================================================================

    [Fact]
    public async Task ReportApplied_SentOncePerSkipId()
    {
        var coordinator = CreateCoordinator();
        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-9", targetProgress = 3_000_000L }));
        Assert.True(coordinator.TryConsumeCollectiveSkip(out _));

        await coordinator.ReportCollectiveSkipAppliedAsync(true, 2_100_000L, "");
        await coordinator.ReportCollectiveSkipAppliedAsync(true, 2_100_000L, "");   // 重复回报

        var sent = SentNamed(AppliedCommand);
        var env = Assert.Single(sent);
        Assert.Equal("skip-9", env.GetString("skipId"));
        Assert.Equal(2_100_000L, env.GetLong("actualProgress"));
        Assert.True(env.GetBool("success"));
    }

    [Fact]
    public async Task ReportApplied_WithoutConsumedSkip_DoesNotSend()
    {
        var coordinator = CreateCoordinator();

        await coordinator.ReportCollectiveSkipAppliedAsync(true, 3_000_000L, "");

        Assert.Empty(SentNamed(AppliedCommand));
    }

    [Fact]
    public async Task ReportApplied_FailureStillSent_WithSuccessFalse()
    {
        var coordinator = CreateCoordinator();
        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-fail", targetProgress = 3_000_000L }));
        Assert.True(coordinator.TryConsumeCollectiveSkip(out _));

        await coordinator.ReportCollectiveSkipAppliedAsync(false, -1, "invalid-target-progress");

        var env = Assert.Single(SentNamed(AppliedCommand));
        Assert.Equal("skip-fail", env.GetString("skipId"));
        Assert.False(env.GetBool("success"));
        Assert.Equal("invalid-target-progress", env.GetString("reason"));
    }

    // =========================================================================
    // 4) AppliedAll：结束活动跳段；过期确认被忽略
    // =========================================================================

    [Fact]
    public void AppliedAll_WithMatchingSkipId_ClearsActiveState()
    {
        var coordinator = CreateCoordinator();
        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-1", targetProgress = 3_000_000L }));
        Assert.True(coordinator.TryConsumeCollectiveSkip(out _));
        Assert.True(coordinator.HasActiveCollectiveSkip);

        _client.DispatchEvt(Evt(AppliedAllEvent, new { skipId = "skip-1", targetProgress = 3_000_000L }));

        Assert.False(coordinator.HasActiveCollectiveSkip);
    }

    [Fact]
    public void AppliedAll_WithStaleSkipId_IsIgnored()
    {
        var coordinator = CreateCoordinator();
        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-2", targetProgress = 4_000_000L }));
        Assert.True(coordinator.TryConsumeCollectiveSkip(out _));

        _client.DispatchEvt(Evt(AppliedAllEvent, new { skipId = "skip-1", targetProgress = 3_000_000L }));

        Assert.True(coordinator.HasActiveCollectiveSkip);
    }

    // =========================================================================
    // 5) 轮次复位 + 旧事件兼容 + Gate 唤醒
    // =========================================================================

    [Fact]
    public void ResetForNewRound_ClearsPendingAndActiveSkip()
    {
        var coordinator = CreateCoordinator();
        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-1", targetProgress = 3_000_000L }));
        Assert.True(coordinator.TryConsumeCollectiveSkip(out _));

        coordinator.ResetForNewRound();

        Assert.False(coordinator.HasActiveCollectiveSkip);
        Assert.False(coordinator.TryConsumeCollectiveSkip(out _));
    }

    [Fact]
    public void LegacyEvent_StillRaised_ForBackwardCompatibility()
    {
        var coordinator = CreateCoordinator();
        long? legacyTarget = null;
        _client.RequestSkipToProgressReceived += v => legacyTarget = v;

        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-1", targetProgress = 3_000_000L }));

        Assert.Equal(3_000_000L, legacyTarget);
        Assert.True(coordinator.TryConsumeCollectiveSkip(out _));   // 同一广播只产生一个待消费请求
    }

    [Fact]
    public void Request_WakesRemoteSkipGate()
    {
        var coordinator = CreateCoordinator();
        Assert.False(RemoteSkipGate.Token.IsCancellationRequested);

        _client.DispatchEvt(Evt(SkipEvent, new { skipId = "skip-1", targetProgress = 3_000_000L }));

        // 唤醒正在 SyncBarrier 等待的流程（消费点 4），否则要等满 60s 超时
        Assert.True(RemoteSkipGate.Token.IsCancellationRequested);
        Assert.True(coordinator.TryConsumeCollectiveSkip(out _));
    }

    // =========================================================================
    // 6) 专用异常：与落后追赶 / 卡死保护的异常互不混淆
    // =========================================================================

    [Fact]
    public void CollectiveSkipRetryException_IsDedicatedType_NotConfusedWithOtherSkipPaths()
    {
        var ex = new PathExecutor.CollectiveSkipRetryException("集体跳段", 3_000_000L, "skip-1");

        Assert.IsAssignableFrom<RetryException>(ex);                        // 仍能被既有的 catch(RetryException) 捕获
        Assert.IsNotType<LaggingCatchUpSkipException>(ex);                  // 不落入落后追赶分支
        Assert.Equal(3_000_000L, ex.TargetProgress);
        Assert.Equal("skip-1", ex.SkipId);
    }
}
