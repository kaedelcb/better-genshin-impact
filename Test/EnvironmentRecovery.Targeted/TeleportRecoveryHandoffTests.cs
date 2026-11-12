using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.Common.Job;
using Xunit;

// 使用生产恢复编排和步骤边界。回归2026-11-12：恢复改变界面后，不能继续旧点击或重置普通重试额度。
public sealed class TeleportRecoveryHandoffTests : IDisposable
{
    private int _closes;

    public TeleportRecoveryHandoffTests()
    {
        TaskContext.Instance().Config.TpConfig.UseOfficialTeleport = false;
        RunnerContext.Instance.IsSuspend = false;
        TaskControl.IsSuspendedByNetwork = false;
        TaskControl.IsSuspendedByWindow = false;
        Screen.IsMapForTest = true;
        Bv.MainUiForTest = () => !Screen.IsMapForTest;
        InputHub.Keyboard.PressForTest = () => { _closes++; Screen.IsMapForTest = false; };
        TpTaskFastDrag.ProbeForTest = _ => { Screen.IsMapForTest = true; return Task.FromResult(true); };
        TaskControl.DelayForTest = (_, ct) => { ct.ThrowIfCancellationRequested(); return Task.CompletedTask; };
    }

    [Theory]
    [InlineData("Teyvat")]
    [InlineData("SeaOfBygoneEras")]
    public async Task RecoveryLeavesMapAndRestartsBeforeInterruptedClick(string mapName)
    {
        var attempts = 0;
        var clicks = new List<int>();
        var releases = InputHub.Releases;
        var result = await TaskControl.RunTeleportStepAsync(async () =>
        {
            var coordinateFromThisAttempt = ++attempts;
            if (attempts == 1)
            {
                TaskControl.IsSuspendedByNetwork = true;
                await TaskControl.RunNetworkRecoveryForTest(CancellationToken.None);
                // 生产TrySuspend和CaptureGameImage使用同一检查点。
                TaskControl.ThrowIfTeleportStepInterrupted();
            }
            Assert.True(Screen.IsMapForTest);
            clicks.Add(coordinateFromThisAttempt);
            return "原目标传送点";
        }, mapName, CancellationToken.None);

        Assert.Equal(mapName, TpTaskFastDrag.LastProbeMapNameForTest);
        Assert.Equal("原目标传送点", result);
        Assert.Equal(2, attempts);
        Assert.Equal(new[] { 2 }, clicks); // 第一轮计算出的坐标从未用于点击。
        Assert.Equal(0, _closes);
        Assert.True(InputHub.Releases > releases);
        Assert.False(TaskControl.IsSuspendedByNetwork);
    }

    [Fact]
    public async Task NestedMapCheckRestartsWholeOuterStep()
    {
        var outerAttempts = 0;
        var staleContinuations = 0;
        await TaskControl.RunTeleportStepAsync(async () =>
        {
            outerAttempts++;
            await TaskControl.RunTeleportStepAsync(async () =>
            {
                if (outerAttempts == 1)
                {
                    TaskControl.IsSuspendedByNetwork = true;
                    await TaskControl.RunNetworkRecoveryForTest(CancellationToken.None);
                }
                return true;
            }, "Teyvat", CancellationToken.None);
            if (outerAttempts == 1) staleContinuations++;
            return true;
        }, "Teyvat", CancellationToken.None);
        Assert.Equal(2, outerAttempts);
        Assert.Equal(0, staleContinuations);
        Assert.Equal(0, _closes);
    }

    [Fact]
    public async Task MissingMapHoldsStepUntilProbeAndThenRebuildsCoordinates()
    {
        var attempts = 0;
        var probeEntered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var responsive = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clicked = false;
        TpTaskFastDrag.ProbeForTest = async ct =>
        {
            probeEntered.TrySetResult();
            return await responsive.Task.WaitAsync(ct);
        };
        var operation = TaskControl.RunTeleportStepAsync(() =>
        {
            if (++attempts == 1) throw new MapUiUnavailableException("缩放读取失败");
            clicked = true;
            return Task.FromResult(42);
        }, "Teyvat", CancellationToken.None);
        await probeEntered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(clicked);
        Assert.False(operation.IsCompleted);
        responsive.SetResult(true);
        Assert.Equal(42, await operation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(2, attempts);
        Assert.Equal(0, _closes);
    }

    [Fact]
    public async Task RecoveryDoesNotResetOrdinaryFailureBudget()
    {
        var attempts = new List<int>();
        var recovered = false;
        for (var ordinaryAttempt = 0; ordinaryAttempt < 3; ordinaryAttempt++)
        {
            await Assert.ThrowsAsync<InvalidOperationException>(() => TaskControl.RunTeleportStepAsync<int>(async () =>
            {
                attempts.Add(ordinaryAttempt);
                if (ordinaryAttempt == 1 && !recovered)
                {
                    recovered = true;
                    TaskControl.IsSuspendedByNetwork = true;
                    await TaskControl.RunNetworkRecoveryForTest(CancellationToken.None);
                    TaskControl.ThrowIfTeleportStepInterrupted();
                }
                throw new InvalidOperationException("普通点位失败，仍由外层计次");
            }, "Teyvat", CancellationToken.None));
        }
        Assert.Equal(new[] { 0, 1, 1, 2 }, attempts);
    }

    [Fact]
    public async Task StaleSuccessfulResultCannotEscapeStepBoundary()
    {
        var attempts = 0;
        var result = await TaskControl.RunTeleportStepAsync(async () =>
        {
            var value = ++attempts;
            if (attempts == 1)
            {
                TaskControl.IsSuspendedByNetwork = true;
                await TaskControl.RunNetworkRecoveryForTest(CancellationToken.None);
            }
            return value; // 即使业务未再截图/等待，也不能把恢复前的结果当作成功返回。
        }, "Teyvat", CancellationToken.None);
        Assert.Equal(2, result);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task OtherPausedWorkCannotAuthorizeInputBeforeTeleportPauses()
    {
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        var probes = 0;
        TpTaskFastDrag.ProbeForTest = _ => { probes++; return Task.FromResult(true); };
        var attempts = 0;
        await TaskControl.RunTeleportStepAsync(async () =>
        {
            if (++attempts == 1)
            {
                TaskControl.IsSuspendedByNetwork = true;
                var recovery = TaskControl.RunNetworkRecoveryForTest(stop.Token, acknowledgeTeleport: false);
                Assert.Equal(0, probes);
                Assert.False(recovery.IsCompleted);
                TaskControl.PauseTeleportForTest(true);
                await recovery;
                TaskControl.ThrowIfTeleportStepInterrupted(stop.Token);
            }
            return true;
        }, "Teyvat", stop.Token);
        Assert.Equal(1, probes);
        Assert.Equal(2, attempts);
    }

    [Fact]
    public async Task UserStopWinsOverRecoveryAndReleasesStepForNextTask()
    {
        using var stop = new CancellationTokenSource();
        var attempts = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => TaskControl.RunTeleportStepAsync(async () =>
        {
            attempts++;
            TaskControl.IsSuspendedByNetwork = true;
            await TaskControl.RunNetworkRecoveryForTest(stop.Token);
            stop.Cancel();
            TaskControl.ThrowIfTeleportStepInterrupted(stop.Token);
            return true;
        }, "Teyvat", stop.Token));
        Assert.Equal(1, attempts);
        Assert.True(await TaskControl.RunTeleportStepAsync(() => Task.FromResult(true), "Teyvat", CancellationToken.None));
        Assert.Equal(0, RunnerContext.Instance.AutoPickTriggerStopCount);
    }

    [Fact]
    public async Task StopWhileMapIsUnavailableReleasesRecoveryAndTeleportOwnership()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TpTaskFastDrag.ProbeForTest = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        };
        var operation = TaskControl.RunTeleportStepAsync<int>(
            () => throw new MapUiUnavailableException("地图仍不可用"), "Teyvat", stop.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        Assert.Equal(0, RunnerContext.Instance.AutoPickTriggerStopCount);
        Assert.Equal(7, await TaskControl.RunTeleportStepAsync(
            () => Task.FromResult(7), "Teyvat", CancellationToken.None));
        await TaskControl.WaitForEnvironmentRecoveryAsync(
            _ => Task.FromResult(true), CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task FollowingPathRecoveryReturnsToMainUiAfterTeleportScopeEnds()
    {
        await TaskControl.RunTeleportStepAsync(() => Task.FromResult(true), "Teyvat", CancellationToken.None);
        TaskControl.IsSuspendedByNetwork = true;
        await TaskControl.RunNetworkRecoveryForTest(CancellationToken.None);
        Assert.Equal(1, _closes);
        Assert.False(Screen.IsMapForTest);
        Assert.False(TaskControl.IsSuspendedByNetwork);
    }

    public void Dispose()
    {
        Screen.IsMapForTest = false;
        Bv.MainUiForTest = () => true;
        InputHub.Keyboard.PressForTest = null;
        TpTaskFastDrag.ProbeForTest = null;
        TaskControl.DelayForTest = null;
        TaskControl.IsSuspendedByNetwork = false;
        TaskControl.IsSuspendedByWindow = false;
        NetworkRecovery.RecoveryNetworkDone = false;
        RunnerContext.Instance.IsSuspend = false;
    }
}
