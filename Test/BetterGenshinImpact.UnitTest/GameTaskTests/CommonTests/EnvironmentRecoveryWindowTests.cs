using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Time.Testing;
using Xunit;

namespace BetterGenshinImpact.UnitTest.GameTaskTests.CommonTests;

public class EnvironmentRecoveryWindowTests
{
    [Fact]
    public async Task ActualResponseContinuesWithoutWaitingForNetworkProbe()
    {
        var time = new FakeTimeProvider();
        int manual = 0, probes = 0;
        await new EnvironmentRecoveryWindow(time).WaitAsync(
            _ => { probes++; return Task.FromResult(true); },
            _ => { manual++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, probes);
        Assert.Equal(0, manual);
    }

    [Fact]
    public async Task TemporaryFreezeKeepsCallerPendingUntilFreshProbeSucceeds()
    {
        var time = new FakeTimeProvider();
        int probes = 0, nextTask = 0;
        async Task RunCurrentRoute()
        {
            await new EnvironmentRecoveryWindow(time).WaitAsync(
                _ => Task.FromResult(++probes == 2),
                _ => throw new InvalidOperationException("不应超时"), CancellationToken.None);
            nextTask++;
        }
        var task = RunCurrentRoute();
        Assert.False(task.IsCompleted);
        Assert.Equal(0, nextTask);
        time.Advance(TimeSpan.FromSeconds(1));
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, probes);
        Assert.Equal(1, nextTask);
    }

    [Fact]
    public async Task TimeoutHoldsCurrentInvocationUntilExplicitResumeThenRechecks()
    {
        var time = new FakeTimeProvider();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int probes = 0;
        var task = new EnvironmentRecoveryWindow(time).WaitAsync(async ct =>
        {
            if (++probes > 1) return true;
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        }, async ct => { waiting.SetResult(); await resume.Task.WaitAsync(ct); }, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(60));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(task.IsCompleted);
        Assert.Equal(1, probes);
        time.Advance(TimeSpan.FromMinutes(10));
        Assert.Equal(1, probes); // 超时后不再后台无限探测、按键。
        resume.SetResult();
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task ReopeningMapDoesNotRenewBudgetForRepeatedTeleportFailure()
    {
        var time = new FakeTimeProvider();
        var window = new EnvironmentRecoveryWindow(time);
        int probes = 0, manual = 0;
        Task<bool> Probe(CancellationToken _) { probes++; return Task.FromResult(true); }
        Task Resume(CancellationToken _) { manual++; return Task.CompletedTask; }
        await window.WaitAsync(Probe, Resume, CancellationToken.None);
        time.Advance(TimeSpan.FromSeconds(59));
        await window.WaitAsync(Probe, Resume, CancellationToken.None);
        Assert.Equal(0, manual);
        time.Advance(TimeSpan.FromSeconds(1));
        await window.WaitAsync(Probe, Resume, CancellationToken.None);
        Assert.Equal(1, manual);
        Assert.Equal(3, probes);
    }

    [Fact]
    public async Task LateSuccessfulProbeCannotReleaseTimedOutOperation()
    {
        var time = new FakeTimeProvider();
        int probes = 0, manual = 0;
        await new EnvironmentRecoveryWindow(time).WaitAsync(_ =>
        {
            if (++probes == 1) time.Advance(TimeSpan.FromSeconds(61));
            return Task.FromResult(true);
        }, _ => { manual++; return Task.CompletedTask; }, CancellationToken.None);
        Assert.Equal(1, manual);
        Assert.Equal(2, probes);
    }

    [Fact]
    public async Task StopDuringProbePropagatesWithoutManualWaitOrSuccess()
    {
        using var stop = new CancellationTokenSource();
        int manual = 0;
        var task = new EnvironmentRecoveryWindow().WaitAsync(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return true;
        }, _ => { manual++; return Task.CompletedTask; }, stop.Token);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, manual);
    }

    [Fact]
    public async Task StopWinsOverSimultaneousSuccess()
    {
        using var stop = new CancellationTokenSource();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EnvironmentRecoveryWindow().WaitAsync(
            _ => { stop.Cancel(); return Task.FromResult(true); },
            _ => throw new Exception("不应等待"), stop.Token));
    }

    [Fact]
    public async Task StopWhileAwaitingUserDoesNotRestartProbe()
    {
        var time = new FakeTimeProvider();
        using var stop = new CancellationTokenSource();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        int probes = 0;
        var task = new EnvironmentRecoveryWindow(time).WaitAsync(async ct =>
        {
            probes++;
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        }, async ct => { waiting.SetResult(); await Task.Delay(Timeout.Infinite, ct); }, stop.Token);
        time.Advance(TimeSpan.FromSeconds(60));
        await waiting.Task.WaitAsync(TimeSpan.FromSeconds(5));
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(1, probes);
    }

    [Fact]
    public async Task OrdinaryProbeBugIsNotConvertedIntoInfiniteRecovery()
    {
        await Assert.ThrowsAsync<InvalidOperationException>(() => new EnvironmentRecoveryWindow().WaitAsync(
            _ => throw new InvalidOperationException("非环境错误"),
            _ => throw new Exception("不应等待"), CancellationToken.None));
    }

    [Fact]
    public async Task CancellationBeforeEntryDoesNotProbeOrPause()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new EnvironmentRecoveryWindow().WaitAsync(
            _ => throw new Exception("不应探测"), _ => throw new Exception("不应暂停"), stop.Token));
    }

    [Fact]
    public async Task IndependentOperationsDoNotInheritPreviousFailureBudget()
    {
        var time = new FakeTimeProvider();
        await new EnvironmentRecoveryWindow(time).WaitAsync(_ => Task.FromResult(true),
            _ => throw new Exception("不应暂停"), CancellationToken.None);
        time.Advance(TimeSpan.FromMinutes(5));
        await new EnvironmentRecoveryWindow(time).WaitAsync(_ => Task.FromResult(true),
            _ => throw new Exception("不应暂停"), CancellationToken.None);
    }
}
