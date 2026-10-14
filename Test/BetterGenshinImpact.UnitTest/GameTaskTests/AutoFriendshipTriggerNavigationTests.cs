using BetterGenshinImpact.GameTask.AutoFriendship;

namespace BetterGenshinImpact.UnitTest.GameTaskTests;

public class AutoFriendshipTriggerNavigationTests
{
    private static TaskCompletionSource<bool> Signal() => new(TaskCreationOptions.RunContinuationsAsynchronously);

    private static Task<bool> Run(
        Func<CancellationToken, Task<bool>> detect,
        Func<CancellationToken, Task> navigate,
        Func<CancellationToken, Task> returnToTrigger,
        CancellationToken ct = default,
        TimeSpan? timeout = null) =>
        AutoFriendshipTask.WaitForTriggerAndNavigationAsync(
            detect, navigate, returnToTrigger, timeout ?? TimeSpan.FromSeconds(10), ct);

    [Fact]
    public async Task TriggerFoundDuringMovement_WaitsForArrivalBeforeAllowingBattle()
    {
        var detection = Signal();
        var movementStarted = Signal();
        var arrival = Signal();
        var returned = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var preparation = Run(
            ct => detection.Task.WaitAsync(ct),
            async ct =>
            {
                movementStarted.SetResult(true);
                await arrival.Task.WaitAsync(ct);
            },
            _ => { returned = true; return Task.CompletedTask; }, stop.Token);

        // 检测尚未完成时已经开始移动。
        await movementStarted.Task.WaitAsync(stop.Token);
        detection.SetResult(true);
        Assert.False(preparation.IsCompleted);
        arrival.SetResult(true);

        Assert.True(await preparation);
        Assert.False(returned);
    }

    [Fact]
    public async Task MovementFinishesFirst_StillRequiresTriggerBeforeAllowingBattle()
    {
        var detectionStarted = Signal();
        var detection = Signal();
        var arrived = Signal();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var preparation = Run(
            async ct =>
            {
                detectionStarted.SetResult(true);
                return await detection.Task.WaitAsync(ct);
            },
            _ => { arrived.SetResult(true); return Task.CompletedTask; },
            _ => throw new InvalidOperationException("检测成功不应返回触发点"), stop.Token);

        await Task.WhenAll(detectionStarted.Task, arrived.Task).WaitAsync(stop.Token);
        Assert.False(preparation.IsCompleted);
        detection.SetResult(true);
        Assert.True(await preparation);
    }

    [Fact]
    public async Task DetectionTimeout_WaitsForMovementToExitAndReturnBeforeReportingNotTriggered()
    {
        var movementStarted = Signal();
        var movementCancelled = Signal();
        var allowMovementExit = Signal();
        var returnStarted = Signal();
        var allowReturn = Signal();
        var movementExited = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var preparation = Run(
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return false;
            },
            async ct =>
            {
                movementStarted.SetResult(true);
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally
                {
                    movementCancelled.SetResult(true);
                    await allowMovementExit.Task.WaitAsync(stop.Token);
                    movementExited = true;
                }
            },
            async ct =>
            {
                Assert.True(movementExited);
                Assert.False(ct.IsCancellationRequested);
                returnStarted.SetResult(true);
                await allowReturn.Task.WaitAsync(ct);
            }, stop.Token, TimeSpan.FromMilliseconds(250));

        await Task.WhenAll(movementStarted.Task, movementCancelled.Task).WaitAsync(stop.Token);
        Assert.False(returnStarted.Task.IsCompleted);
        Assert.False(preparation.IsCompleted);
        allowMovementExit.SetResult(true);
        await returnStarted.Task.WaitAsync(stop.Token);
        Assert.False(preparation.IsCompleted);
        allowReturn.SetResult(true);

        Assert.False(await preparation);
        Assert.False(stop.IsCancellationRequested);
    }

    [Fact]
    public async Task DetectionTimeoutAfterArrival_ReturnsToTriggerInsteadOfAllowingBattle()
    {
        var returned = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var preparation = Run(
            async ct =>
            {
                await Task.Delay(Timeout.Infinite, ct);
                return false;
            },
            _ => Task.CompletedTask,
            _ => { returned = true; return Task.CompletedTask; },
            stop.Token, TimeSpan.FromMilliseconds(100));

        Assert.False(await preparation);
        Assert.True(returned);
    }

    [Fact]
    public async Task TriggerFound_NavigationCanContinuePastDetectionDeadline()
    {
        var detectionDeadline = Signal();
        var arrival = Signal();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        var preparation = Run(
            ct =>
            {
                ct.Register(() => detectionDeadline.TrySetResult(true));
                return Task.FromResult(true);
            },
            ct => arrival.Task.WaitAsync(ct),
            _ => throw new InvalidOperationException("已触发不应返回触发点"),
            stop.Token, TimeSpan.FromMilliseconds(250));

        await detectionDeadline.Task.WaitAsync(stop.Token);
        Assert.False(preparation.IsCompleted);
        arrival.SetResult(true);
        Assert.True(await preparation);
    }

    [Fact]
    public async Task UserCancellation_StopsDetectionAndMovementWithoutReturningOrAllowingBattle()
    {
        var detectionStarted = Signal();
        var movementStarted = Signal();
        var detectionExited = false;
        var movementExited = false;
        var returned = false;
        using var stop = new CancellationTokenSource();

        var preparation = Run(
            async ct =>
            {
                detectionStarted.SetResult(true);
                try { await Task.Delay(Timeout.Infinite, ct); return false; }
                finally { detectionExited = true; }
            },
            async ct =>
            {
                movementStarted.SetResult(true);
                try { await Task.Delay(Timeout.Infinite, ct); }
                finally { movementExited = true; }
            },
            _ => { returned = true; return Task.CompletedTask; }, stop.Token);

        await Task.WhenAll(detectionStarted.Task, movementStarted.Task).WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.True(detectionExited);
        Assert.True(movementExited);
        Assert.False(returned);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task DetectionOrMovementError_StopsOtherOperationWithoutAllowingBattle(bool detectionFails)
    {
        var otherStarted = Signal();
        var otherExited = false;
        var returned = false;
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(5));

        async Task Fail(CancellationToken ct)
        {
            await otherStarted.Task.WaitAsync(ct);
            throw new InvalidOperationException("模拟识别或移动失败");
        }

        async Task WaitUntilCancelled(CancellationToken ct)
        {
            otherStarted.SetResult(true);
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { otherExited = true; }
        }

        var preparation = Run(
            async ct =>
            {
                if (detectionFails) await Fail(ct);
                else await WaitUntilCancelled(ct);
                return false;
            },
            ct => detectionFails ? WaitUntilCancelled(ct) : Fail(ct),
            _ => { returned = true; return Task.CompletedTask; }, stop.Token);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => preparation);
        Assert.Equal("模拟识别或移动失败", error.Message);
        Assert.True(otherExited);
        Assert.False(returned);
    }

    [Fact]
    public async Task CancellationDuringReturn_DoesNotResumeOriginalNotTriggeredBranch()
    {
        using var stop = new CancellationTokenSource();
        var preparation = Run(
            _ => Task.FromResult(false),
            _ => Task.CompletedTask,
            _ =>
            {
                // 返回操作即使正常退出，也不能把用户停止当作普通的“未触发”。
                stop.Cancel();
                return Task.CompletedTask;
            }, stop.Token);

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation.WaitAsync(TimeSpan.FromSeconds(5)));
    }
}
