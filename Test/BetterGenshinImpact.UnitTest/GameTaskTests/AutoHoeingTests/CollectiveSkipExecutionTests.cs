#nullable enable

using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoHoeing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.AutoPathing;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

/// <summary>驱动真实跳段消费入口，覆盖迟到命令、正常跳段和取消边界。</summary>
[Collection("RemoteSkipGateState")]
public sealed class CollectiveSkipExecutionTests : IDisposable
{
    private const long TargetProgress = 45_000_000L;
    private readonly CoordinatorClient _client = new() { _testIsConnectedOverride = true };
    private readonly MultiplayerCoordinator _coordinator;
    private readonly List<GatewayEnvelope> _appliedReports = new();

    public CollectiveSkipExecutionTests()
    {
        RemoteSkipGate.Reset();
        _coordinator = new MultiplayerCoordinator(_client, new SyncPointResolver(), new AutoHoeingConfig());
        _client.GetOrCreateGatewayForTest()._testInvokeOverride = (_, request, _) =>
        {
            if (request.Name == GatewayProtocol.Names.SyncReportCollectiveSkipApplied)
                _appliedReports.Add(request);
            return Task.FromResult(new GatewayEnvelope
            {
                Type = GatewayProtocol.MessageTypes.Response,
                Name = request.Name,
            });
        };
    }

    public void Dispose()
    {
        _coordinator.DisposeAsync().AsTask().GetAwaiter().GetResult();
        _client.DisposeAsync().AsTask().GetAwaiter().GetResult();
        RemoteSkipGate.Reset();
    }

    private void RequestSkip(string? skipId = "skip-incident", long targetProgress = TargetProgress)
        => _client.DispatchEvt(new GatewayEnvelope
        {
            Type = GatewayProtocol.MessageTypes.Event,
            Name = GatewayProtocol.Events.SyncRequestSkipToProgress,
            Payload = GatewayEnvelope.ToPayload(new { skipId, targetProgress }),
            SentAtUtc = DateTime.UtcNow,
        });

    [Theory]
    [InlineData(45_000_000L)] // 事故时序：本地恢复已进入命令要求的下一条路线。
    [InlineData(45_001_003L)]
    [InlineData(46_000_000L)]
    public async Task AlreadyAtOrPastTarget_DoesNotPrepareOrSkip_ReportsActualProgress(long currentProgress)
    {
        RequestSkip();
        var preparations = 0;

        await PathExecutor.HandleCollectiveSkipAsync(_coordinator, false, currentProgress,
            _ => { preparations++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(0, preparations);
        Assert.False(_coordinator.IsExitTriggered);
        var report = Assert.Single(_appliedReports);
        Assert.Equal("skip-incident", report.GetString("skipId"));
        Assert.Equal(currentProgress, report.GetLong("actualProgress"));
        Assert.True(report.GetBool("success"));
        Assert.Equal("already-at-target", report.GetString("reason"));

        RequestSkip(); // 同一命令重播仍不多跳，也不重复报告。
        await PathExecutor.HandleCollectiveSkipAsync(_coordinator, false, currentProgress,
            _ => { preparations++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(0, preparations);
        Assert.Single(_appliedReports);
    }

    [Fact]
    public async Task BehindTarget_PreparesOnceAndUsesExistingSkipException_ReplayDoesNotSkipAgain()
    {
        RequestSkip();
        var preparations = 0;
        Task Prepare(CollectiveSkipRequest request)
        {
            preparations++;
            Assert.Equal(TargetProgress, request.TargetProgress);
            return Task.CompletedTask;
        }

        var skip = await Assert.ThrowsAsync<PathExecutor.CollectiveSkipRetryException>(() =>
            PathExecutor.HandleCollectiveSkipAsync(_coordinator, false, 44_000_000L,
                Prepare, CancellationToken.None));

        Assert.Equal(TargetProgress, skip.TargetProgress);
        Assert.Equal("skip-incident", skip.SkipId);
        Assert.Equal(1, preparations);
        Assert.False(_coordinator.IsExitTriggered);
        Assert.Empty(_appliedReports); // 真正跳段的确认仍由已有跳段分支负责。

        RequestSkip();
        await PathExecutor.HandleCollectiveSkipAsync(_coordinator, false, 44_000_000L,
            Prepare, CancellationToken.None);
        Assert.Equal(1, preparations);
    }

    [Theory]
    [InlineData(false)] // 单机没有协调器。
    [InlineData(true)]  // 共同重跑不消费旧集体跳段。
    public async Task SinglePlayerAndReplay_DoNotPrepareOrConsumeCommand(bool replay)
    {
        RequestSkip();
        var preparations = 0;

        await PathExecutor.HandleCollectiveSkipAsync(replay ? _coordinator : null, replay,
            44_000_000L, _ => { preparations++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(0, preparations);
        Assert.True(_coordinator.TryConsumeCollectiveSkip(out var pending));
        Assert.Equal("skip-incident", pending!.SkipId);
        Assert.Empty(_appliedReports);
        Assert.False(_coordinator.IsExitTriggered);
    }

    [Fact]
    public async Task NoCommand_NormalMultiplayerContinuesWithoutPreparingOrReporting()
    {
        var preparations = 0;

        await PathExecutor.HandleCollectiveSkipAsync(_coordinator, false, 44_000_000L,
            _ => { preparations++; return Task.CompletedTask; }, CancellationToken.None);

        Assert.Equal(0, preparations);
        Assert.Empty(_appliedReports);
        Assert.False(_coordinator.HasActiveCollectiveSkip);
        Assert.False(_coordinator.IsExitTriggered);
    }

    [Theory]
    [InlineData(44_000_000L)]
    [InlineData(45_000_000L)]
    public async Task StoppedBeforeConsumption_CancelsWithoutPreparingOrAcknowledging(long currentProgress)
    {
        RequestSkip();
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        var preparations = 0;

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PathExecutor.HandleCollectiveSkipAsync(_coordinator, false, currentProgress,
                _ => { preparations++; return Task.CompletedTask; }, stop.Token));

        Assert.Equal(stop.Token, cancelled.CancellationToken);
        Assert.Equal(0, preparations);
        Assert.Empty(_appliedReports);
        Assert.True(_coordinator.TryConsumeCollectiveSkip(out _));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedDuringPreparation_PropagatesCancellationInsteadOfAdvancing(bool preparationThrows)
    {
        RequestSkip();
        using var stop = new CancellationTokenSource();
        var preparations = 0;
        Task Prepare(CollectiveSkipRequest _)
        {
            preparations++;
            stop.Cancel();
            if (preparationThrows) stop.Token.ThrowIfCancellationRequested();
            return Task.CompletedTask; // 即使共享动作吞掉取消，返回边界仍须阻止跳段。
        }

        var cancelled = await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            PathExecutor.HandleCollectiveSkipAsync(_coordinator, false, 44_000_000L, Prepare, stop.Token));

        Assert.Equal(stop.Token, cancelled.CancellationToken);
        Assert.Equal(1, preparations);
        Assert.Empty(_appliedReports);
    }

    [Fact]
    public async Task LegacyCommandWithoutSkipId_UsesExistingSkipWithoutAppliedReport()
    {
        RequestSkip(skipId: null);
        var preparations = 0;

        var skip = await Assert.ThrowsAsync<PathExecutor.CollectiveSkipRetryException>(() =>
            PathExecutor.HandleCollectiveSkipAsync(_coordinator, false, 44_000_000L,
                request =>
                {
                    preparations++;
                    Assert.False(request.HasSkipId);
                    return Task.CompletedTask;
                }, CancellationToken.None));

        Assert.Equal(1, preparations);
        Assert.Equal("", skip.SkipId);
        Assert.Equal(TargetProgress, skip.TargetProgress);
        Assert.Empty(_appliedReports);
        Assert.False(_coordinator.IsExitTriggered);
    }
}
