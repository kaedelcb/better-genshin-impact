using BetterGenshinImpact.Service;
using Xunit;

namespace BetterGenshinImpact.UnitTest.ServiceTests;

public class GameTaskStartupGateTests
{
    private sealed class Clock : TimeProvider
    {
        private long _ticks;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => _ticks;
        public Task Delay(TimeSpan duration, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            _ticks += duration.Ticks;
            return Task.CompletedTask;
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ColdOrAlreadyBound_WaitsThroughLoginLoadingAndRewardsBeforeBody(bool runtimeRunning)
    {
        var clock = new Clock();
        var frames = new Queue<string>(["login", "loading", "welkin", "primogem", "main"]);
        var events = new List<string>();
        var starts = 0;
        var expectedStarts = runtimeRunning ? 0 : 1;
        await GameTaskStartupGate.PrepareAsync(true,
            _ =>
            {
                if (!runtimeRunning) { starts++; runtimeRunning = true; }
                return Task.FromResult(true);
            }, _ =>
            {
                var frame = frames.Dequeue();
                events.Add(frame);
                return frame == "main";
            }, CancellationToken.None, delay: (duration, token) =>
            {
                events.Add("wait");
                return clock.Delay(duration, token);
            }, timeProvider: clock);
        events.Add("body");
        Assert.True(runtimeRunning);
        Assert.Equal(expectedStarts, starts);
        Assert.Equal(["login", "wait", "loading", "wait", "welkin", "wait", "primogem", "wait", "main", "body"], events);
    }

    [Fact]
    public async Task CancelledColdStartThenRestart_RechecksScreenAlthoughRuntimeRemainsBound()
    {
        using var first = new CancellationTokenSource();
        var running = false;
        var starts = 0;
        var reads = 0;
        Task<bool> EnsureRuntime(CancellationToken _)
        {
            if (!running) { running = true; starts++; }
            return Task.FromResult(true);
        }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameTaskStartupGate.PrepareAsync(true,
            EnsureRuntime, _ => { reads++; return false; }, first.Token,
            delay: (_, token) => { first.Cancel(); token.ThrowIfCancellationRequested(); return Task.CompletedTask; }));
        Assert.True(running);
        Assert.Equal(1, reads);

        var clock = new Clock();
        var restartReads = 0;
        await GameTaskStartupGate.PrepareAsync(true, EnsureRuntime,
            _ => ++restartReads == 3, CancellationToken.None, delay: clock.Delay, timeProvider: clock);
        Assert.Equal(1, starts);
        Assert.Equal(3, restartReads);
    }

    [Fact]
    public async Task AlreadyReady_DoesNotAddFixedDelay()
    {
        await GameTaskStartupGate.PrepareAsync(true, _ => Task.FromResult(true), _ => true,
            CancellationToken.None, delay: (_, _) => throw new Exception("Already ready must not wait"));
    }

    [Fact]
    public async Task SpecialTask_PreparesRuntimeButDoesNotInspectOrClickScreen()
    {
        var starts = 0;
        await GameTaskStartupGate.PrepareAsync(false, _ => { starts++; return Task.FromResult(true); },
            _ => throw new Exception("Special task must bypass the screen gate"), CancellationToken.None,
            delay: (_, _) => throw new Exception("Special task must not wait"));
        Assert.Equal(1, starts);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RuntimeStartFailed_NeverInspectsScreenOrRunsBody(bool waitForMainUi)
    {
        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => GameTaskStartupGate.PrepareAsync(
            waitForMainUi, _ => Task.FromResult(false), _ => throw new Exception("Must not inspect"), CancellationToken.None));
        Assert.Contains("运行环境未就绪", error.Message);
    }

    [Fact]
    public async Task CancelledBeforeStarting_DoesNotLaunchGame()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameTaskStartupGate.PrepareAsync(true,
            _ => throw new Exception("Must not start runtime"), _ => throw new Exception("Must not inspect"), stop.Token));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task StoppedDuringRuntimeBinding_DoesNotContinueEvenForSpecialTask(bool waitForMainUi)
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameTaskStartupGate.PrepareAsync(waitForMainUi,
            _ => { stop.Cancel(); return Task.FromResult(true); },
            _ => throw new Exception("Must not inspect after stop"), stop.Token));
    }

    [Fact]
    public async Task StoppedDuringRecognition_DoesNotAcceptReadyFrame()
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameTaskStartupGate.PrepareAsync(true,
            _ => Task.FromResult(true), _ => { stop.Cancel(); return true; }, stop.Token));
    }

    [Fact]
    public async Task StopInterruptsAnInFlightDelay_WithoutWaitingForAnotherFrame()
    {
        using var stop = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var reads = 0;
        var preparation = GameTaskStartupGate.PrepareAsync(true,
            _ => Task.FromResult(true), _ => { reads++; return false; }, stop.Token,
            delay: (_, token) =>
            {
                waiting.SetResult();
                return Task.Delay(Timeout.InfiniteTimeSpan, token);
            });
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => preparation.WaitAsync(TimeSpan.FromSeconds(5)));
        Assert.Equal(1, reads);
    }

    [Fact]
    public async Task ReplacementToken_DoesNotReviveCancelledWaitOrSendAnotherClick()
    {
        using var original = new CancellationTokenSource();
        using var replacement = new CancellationTokenSource();
        var globalToken = original.Token;
        var clicks = 0;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameTaskStartupGate.PrepareAsync(true,
            _ => Task.FromResult(true), token => { token.ThrowIfCancellationRequested(); clicks++; return false; }, globalToken,
            delay: (_, token) =>
            {
                original.Cancel();
                globalToken = replacement.Token;
                return Task.CompletedTask; // 即使等待实现忽略取消，下一帧仍必须检查原令牌。
            }));
        Assert.False(globalToken.IsCancellationRequested);
        Assert.Equal(1, clicks);
    }

    [Fact]
    public async Task OwnerLostDuringRecognition_DoesNotAcceptReadyFrame()
    {
        var owned = true;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameTaskStartupGate.PrepareAsync(true,
            _ => Task.FromResult(true), _ => { owned = false; return true; }, CancellationToken.None,
            verifyOwnership: () => { if (!owned) throw new OperationCanceledException(); }));
    }

    [Fact]
    public async Task LoadingNeverCompletes_TimesOutWithoutRunningBody()
    {
        var clock = new Clock();
        var reads = 0;
        var error = await Assert.ThrowsAsync<TimeoutException>(() => GameTaskStartupGate.PrepareAsync(true,
            _ => Task.FromResult(true), _ => { reads++; return false; }, CancellationToken.None,
            delay: clock.Delay, timeProvider: clock, timeout: TimeSpan.FromSeconds(3)));
        Assert.Equal(3, reads);
        Assert.Contains("未开始执行任务", error.Message);
    }

    [Fact]
    public async Task RecognitionFailure_PropagatesInsteadOfTreatingUnknownScreenAsReady()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => GameTaskStartupGate.PrepareAsync(true,
            _ => Task.FromResult(true), _ => throw new InvalidOperationException("capture lost"), CancellationToken.None));
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StopAndRuntimeOrCaptureFailureArriveTogether_CancellationWins(bool duringBinding)
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => GameTaskStartupGate.PrepareAsync(true,
            _ =>
            {
                if (duringBinding)
                {
                    stop.Cancel();
                    throw new InvalidOperationException("window closed during binding");
                }
                return Task.FromResult(true);
            }, _ =>
            {
                stop.Cancel();
                throw new InvalidOperationException("capture disposed by stop");
            }, stop.Token));
    }
}
