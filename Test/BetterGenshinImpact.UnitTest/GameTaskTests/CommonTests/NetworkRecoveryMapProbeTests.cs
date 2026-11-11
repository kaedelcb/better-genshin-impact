using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonTests;

// 验证“游戏已恢复但关图动画/截图迟到”不会再因500ms单帧失败而重跑恢复；
// 同时验证不连按Esc、不因单帧闪现放行、停止优先。这些测试不启动游戏或发送真实输入。
public class NetworkRecoveryMapProbeTests
{
    [Fact]
    public async Task SlowCloseAnimationSucceedsWithoutPressingEscapeAgain()
    {
        var elapsed = 0;
        var closes = 0;
        var result = await NetworkRecoveryMapProbe.CloseAsync(
            () => elapsed >= 1700, () => { closes++; return true; },
            (ms, _) => { elapsed += ms; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(result);
        Assert.Equal(1, closes);
        Assert.Equal(1800, elapsed);
    }

    [Fact]
    public async Task AlreadyClosedMapDoesNotOpenPaimonMenu()
    {
        var elapsed = 0;
        var result = await NetworkRecoveryMapProbe.CloseAsync(
            () => true, () => throw new Exception("不能再次按Esc"),
            (ms, _) => { elapsed += ms; return Task.CompletedTask; }, CancellationToken.None);
        Assert.True(result);
        Assert.Equal(600, elapsed);
    }

    [Fact]
    public async Task OneTransientMainUiFrameCannotReleasePause()
    {
        var samples = new Queue<bool>(new[] { false, true, false });
        var elapsed = 0;
        var closes = 0;
        var result = await NetworkRecoveryMapProbe.CloseAsync(
            () => samples.Count > 0 && samples.Dequeue(), () => { closes++; return true; },
            (ms, _) => { elapsed += ms; return Task.CompletedTask; }, CancellationToken.None);
        Assert.False(result);
        Assert.Equal(1, closes);
        Assert.Equal(3000, elapsed);
    }

    [Fact]
    public async Task MissingMainUiRemainsFailureWithBoundedObservation()
    {
        var captures = 0;
        var closes = 0;
        var result = await NetworkRecoveryMapProbe.CloseAsync(
            () => { captures++; return false; }, () => { closes++; return true; },
            (_, _) => Task.CompletedTask, CancellationToken.None);
        Assert.False(result);
        Assert.Equal(27, captures);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task RejectedInputTargetKeepsRecoveryPendingWithoutObservation()
    {
        var result = await NetworkRecoveryMapProbe.CloseAsync(
            () => false, () => false,
            (_, _) => throw new Exception("未发送关图输入不应等待成功"), CancellationToken.None);
        Assert.False(result);
    }

    [Fact]
    public async Task CancellationBeforeEntryDoesNotCaptureOrSendInput()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkRecoveryMapProbe.CloseAsync(
            () => throw new Exception("不应截图"), () => throw new Exception("不应按键"),
            (_, _) => throw new Exception("不应延时"), stop.Token));
    }

    [Fact]
    public async Task StopDuringCloseWaitDoesNotContinueProbing()
    {
        using var stop = new CancellationTokenSource();
        var captures = 0;
        var closes = 0;
        var task = NetworkRecoveryMapProbe.CloseAsync(
            () => { captures++; return false; }, () => { closes++; return true; },
            (_, ct) => Task.Delay(Timeout.Infinite, ct), stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, captures);
        Assert.Equal(1, closes);
    }

    [Fact]
    public async Task StopDuringSuccessfulRecognitionWinsOverSuccess()
    {
        using var stop = new CancellationTokenSource();
        var captures = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkRecoveryMapProbe.CloseAsync(
            () => { if (++captures == 3) stop.Cancel(); return captures > 1; },
            () => true, (_, _) => Task.CompletedTask, stop.Token));
        Assert.Equal(3, captures);
    }

    [Fact]
    public async Task StopDuringInitialCaptureDoesNotSendEscape()
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => NetworkRecoveryMapProbe.CloseAsync(
            () => { stop.Cancel(); return false; }, () => throw new Exception("不应按键"),
            (_, _) => Task.CompletedTask, stop.Token));
    }
}
