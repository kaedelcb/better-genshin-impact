using System.Threading;
using BetterGenshinImpact.Core.Input;
using Fischless.WindowsInput;
using Vanara.PInvoke;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.AutoHoeingTests;

// 驱动生产发送循环。锁屏、前台切换和取消用可控发送与时钟验证，避免向测试机实际注入键鼠。
// 恢复、停止、超时、部分入队均须符合预期，否则不能交付延长容错的改动。
public sealed class RouteInputRetryTests
{
    private static readonly User32.INPUT[] Batch = new User32.INPUT[2];

    [Fact]
    public void AcceptedInputDoesNotWaitOrRepeat()
    {
        var input = new RetryHarness { Send = _ => 2 };
        using var retry = input.Begin();

        input.Dispatch();

        Assert.Equal(new long[] { 0 }, input.SentAt);
        Assert.Equal(0, input.ElapsedMs);
    }

    [Fact]
    public void RecoversAfterOldTenSecondBudget()
    {
        var input = new RetryHarness { Send = now => now >= 20_400 ? 2u : 0u };
        using var retry = input.Begin();

        input.Dispatch();

        Assert.Equal(21_000, input.ElapsedMs);
        Assert.Equal(21_000, input.SentAt[^1]);
    }

    [Fact]
    public void WaitsForBoundForegroundBeforeResending()
    {
        var input = new RetryHarness
        {
            IsBoundForeground = now => now == 0 || now >= 15_000,
            Send = now => now > 0 ? 2u : 0u,
        };
        using var retry = input.Begin();

        input.Dispatch();

        Assert.Equal(new long[] { 0, 15_000 }, input.SentAt);
    }

    [Fact]
    public void DoesNotSendIfOtherWindowOwnsForegroundForEntireBudget()
    {
        var input = new RetryHarness { IsBoundForeground = _ => false, Send = _ => 2 };
        using var retry = input.Begin();

        Assert.Throws<WindowsInputBlockedException>(input.Dispatch);
        Assert.Empty(input.SentAt);
        Assert.Equal(30_000, input.ElapsedMs);
    }

    [Fact]
    public void PermanentFailureStopsAtThirtySeconds()
    {
        var input = new RetryHarness();
        using var retry = input.Begin();

        Assert.Throws<WindowsInputBlockedException>(input.Dispatch);
        Assert.Equal(30_000, input.ElapsedMs);
        Assert.Equal(29_000, input.SentAt[^1]);
    }

    [Fact]
    public void StopWhileWaitingDropsThePendingInput()
    {
        using var stop = new CancellationTokenSource();
        var input = new RetryHarness { AfterWait = stop.Cancel, Send = now => now == 0 ? 0u : 2u };
        using var retry = input.Begin(stop.Token);

        Assert.Throws<OperationCanceledException>(input.Dispatch);
        Assert.Equal(new long[] { 0 }, input.SentAt);
    }

    [Fact]
    public void StopObservedDuringForegroundCheckStillPreventsSending()
    {
        using var stop = new CancellationTokenSource();
        var input = new RetryHarness { IsBoundForeground = _ => { stop.Cancel(); return true; } };
        using var retry = input.Begin(stop.Token);

        Assert.Throws<OperationCanceledException>(input.Dispatch);
        Assert.Empty(input.SentAt);
    }

    [Fact]
    public void PartialSendFailsWithoutReplayingTheBatch()
    {
        var input = new RetryHarness { Send = _ => 1 };
        using var retry = input.Begin();

        Assert.Throws<WindowsInputBlockedException>(input.Dispatch);
        Assert.Equal(new long[] { 0 }, input.SentAt);
        Assert.Equal(0, input.ElapsedMs);
    }

    [Theory]
    [InlineData(false, 0)]
    [InlineData(true, 1)]
    public void CleanupUsesAtMostOneAttemptAndChecksTheBoundWindow(bool foreground, int expectedSends)
    {
        using var stop = new CancellationTokenSource();
        var input = new RetryHarness { IsBoundForeground = _ => foreground };
        using var retry = input.Begin(stop.Token);
        stop.Cancel();

        InputHub.ReleaseRouteInput(input.Dispatch);

        Assert.Equal(expectedSends, input.SentAt.Count);
        Assert.Equal(0, input.ElapsedMs);
    }

    [Fact]
    public async Task WorkInheritedFromAnEndedRouteCannotSend()
    {
        var input = new RetryHarness { Send = _ => 2 };
        var proceed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task task;
        using (input.Begin())
        {
            task = Task.Run(async () =>
            {
                await proceed.Task;
                Assert.Throws<OperationCanceledException>(input.Dispatch);
            });
        }
        proceed.SetResult();
        await task;
        Assert.Empty(input.SentAt);
    }

    private sealed class RetryHarness
    {
        internal long ElapsedMs { get; private set; }
        internal List<long> SentAt { get; } = new();
        internal Func<long, bool> IsBoundForeground { get; init; } = _ => true;
        internal Func<long, uint> Send { get; init; } = _ => 0;
        internal Action? AfterWait { get; init; }

        internal WindowsInputRetryScope Begin(CancellationToken ct = default) => new(
            () => IsBoundForeground(ElapsedMs), ct.ThrowIfCancellationRequested, milliseconds =>
            {
                ElapsedMs += milliseconds;
                AfterWait?.Invoke();
                ct.ThrowIfCancellationRequested();
            });

        internal void Dispatch()
        {
            new WindowsInputMessageDispatcher(_ =>
            {
                SentAt.Add(ElapsedMs);
                return Send(ElapsedMs);
            }, () => ElapsedMs).DispatchInput(Batch);
        }
    }
}
