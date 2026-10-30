using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common;
using Microsoft.Extensions.Time.Testing;
using Xunit;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

public class EnvironmentRecoveryIntegrationTests
{
    [Theory]
    [InlineData("独立地图追踪")]
    [InlineData("配置组")]
    [InlineData("JS宿主调用")]
    public async Task SharedOperationCannotReturnOrAdvanceCallerWhileUnresponsive(string entry)
    {
        var time = new FakeTimeProvider();
        var state = new Suspendable();
        RunnerContext.Instance.SuspendableDictionary[entry] = state;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        int advances = 0;
        async Task Caller()
        {
            await TaskControl.WaitForEnvironmentRecoveryAsync(async ct =>
            {
                entered.TrySetResult();
                return await response.Task.WaitAsync(ct);
            }, CancellationToken.None, new EnvironmentRecoveryWindow(time));
            advances++;
        }
        var caller = Caller();
        await entered.Task;
        Assert.Equal(0, advances);
        Assert.Equal(1, RunnerContext.Instance.AutoPickTriggerStopCount);
        Assert.Equal(1, state.Suspends);
        response.SetResult(true);
        await caller;
        Assert.Equal(1, advances);
        Assert.Equal(0, RunnerContext.Instance.AutoPickTriggerStopCount);
        Assert.Equal(1, state.Resumes);
        RunnerContext.Instance.SuspendableDictionary.Clear();
    }

    [Fact]
    public async Task CancelledRecoveryReleasesInputGateAndSuspendCounter()
    {
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var task = TaskControl.WaitForEnvironmentRecoveryAsync(async ct =>
        {
            entered.SetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        }, stop.Token);
        await entered.Task;
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        Assert.Equal(0, RunnerContext.Instance.AutoPickTriggerStopCount);
        // 第二次恢复能取得同一把门，证明取消路径没有泄漏互斥锁。
        await TaskControl.WaitForEnvironmentRecoveryAsync(_ => Task.FromResult(true), CancellationToken.None)
            .WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task EndingTaskCancelsAndJoinsOldMonitorBeforeNextTask()
    {
        TaskContext.Instance().Config.TpConfig.UseOfficialTeleport = false;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var exited = false;
        TaskControl.Monitor = async ct =>
        {
            entered.SetResult();
            try { await Task.Delay(Timeout.Infinite, ct); }
            finally { exited = true; }
        };
        TaskControl.BeginEnvironmentRecovery(CancellationToken.None);
        TaskControl.TriggerMonitor();
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await TaskControl.EndEnvironmentRecoveryAsync().WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(exited);
        Assert.False(TaskControl.IsSuspendedByNetwork);
        TaskControl.Monitor = _ => Task.CompletedTask;
    }

    [Fact]
    public async Task TimeoutUsesExistingPauseAndResumeWithoutCompletingCurrentCall()
    {
        var time = new FakeTimeProvider();
        using var stop = new CancellationTokenSource();
        bool responsive = false;
        var task = TaskControl.WaitForEnvironmentRecoveryAsync(async ct =>
        {
            if (responsive) return true;
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        }, stop.Token, new EnvironmentRecoveryWindow(time));
        time.Advance(TimeSpan.FromSeconds(60));
        await WaitUntil(() => RunnerContext.Instance.IsSuspend);
        Assert.False(task.IsCompleted);
        Assert.Equal(1, RunnerContext.Instance.AutoPickTriggerStopCount);
        responsive = true;
        RunnerContext.Instance.IsSuspend = false; // 与现有暂停快捷键相同入口。
        await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(0, RunnerContext.Instance.AutoPickTriggerStopCount);
    }

    [Fact]
    public async Task SecondRecoveryCannotIssueInputsWhileFirstOwnsGate()
    {
        var response = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var first = TaskControl.WaitForEnvironmentRecoveryAsync(_ => response.Task, CancellationToken.None);
        using var stop = new CancellationTokenSource();
        int secondProbes = 0;
        var second = TaskControl.WaitForEnvironmentRecoveryAsync(_ =>
        {
            secondProbes++;
            return Task.FromResult(true);
        }, stop.Token);
        Assert.Equal(0, secondProbes);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => second);
        response.SetResult(true);
        await first;
        Assert.Equal(0, secondProbes);
        Assert.Equal(0, RunnerContext.Instance.AutoPickTriggerStopCount);
    }

    [Fact]
    public async Task RootStopCancelsEvenCallerWithNoneToken()
    {
        using var stop = new CancellationTokenSource();
        TaskControl.BeginEnvironmentRecovery(stop.Token);
        var task = TaskControl.WaitForEnvironmentRecoveryAsync(async ct =>
        {
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        }, CancellationToken.None);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        await TaskControl.EndEnvironmentRecoveryAsync();
        Assert.Equal(0, RunnerContext.Instance.AutoPickTriggerStopCount);
    }

    private static async Task WaitUntil(Func<bool> condition)
    {
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        while (!condition()) await Task.Delay(10, timeout.Token);
    }

    [Fact]
    public async Task OfficialTeleportDoesNotEnterNewProtection()
    {
        TaskContext.Instance().Config.TpConfig.UseOfficialTeleport = true;
        TaskControl.IsSuspendedByNetwork = true;
        try
        {
            TaskControl.BeginEnvironmentRecovery(CancellationToken.None);
            Assert.True(TaskControl.IsSuspendedByNetwork); // 不初始化/替换公版已有网络状态。
            Assert.False(await TaskControl.TryRecoverPathEnvironmentAsync(CancellationToken.None));
            Assert.Equal(0, RunnerContext.Instance.AutoPickTriggerStopCount);
            await TaskControl.EndEnvironmentRecoveryAsync();
        }
        finally
        {
            TaskControl.IsSuspendedByNetwork = false;
            TaskContext.Instance().Config.TpConfig.UseOfficialTeleport = false;
        }
    }

    [Fact]
    public async Task VerifiedNetworkRecoveryMarksInterruptedAttemptsAndSuppressesPingOnlyFalseAlarms()
    {
        TaskControl.BeginEnvironmentRecovery(CancellationToken.None);
        try
        {
            var before = TaskControl.NetworkRecoveryVersion;
            Assert.False(TaskControl.HasRecentVerifiedGameResponse);
            TaskControl.IsSuspendedByNetwork = true;
            await TaskControl.RunNetworkRecoveryForTest(CancellationToken.None);
            Assert.Equal(before + 1, TaskControl.NetworkRecoveryVersion);
            Assert.False(TaskControl.IsSuspendedByNetwork);
            Assert.True(TaskControl.HasRecentVerifiedGameResponse);
        }
        finally { await TaskControl.EndEnvironmentRecoveryAsync(); }
    }

    [Fact]
    public async Task CancelledNetworkRecoveryCannotPublishVerifiedResponse()
    {
        using var stop = new CancellationTokenSource();
        TaskControl.BeginEnvironmentRecovery(stop.Token);
        try
        {
            var before = TaskControl.NetworkRecoveryVersion;
            var task = TaskControl.RunNetworkRecoveryForTest(stop.Token);
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
            Assert.Equal(before, TaskControl.NetworkRecoveryVersion);
            Assert.False(TaskControl.HasRecentVerifiedGameResponse);
        }
        finally { await TaskControl.EndEnvironmentRecoveryAsync(); }
    }

    [Fact]
    public async Task CompletedTaskCannotStartNewUnownedMonitor()
    {
        int calls = 0;
        TaskControl.Monitor = _ => { Interlocked.Increment(ref calls); return Task.CompletedTask; };
        TaskControl.BeginEnvironmentRecovery(CancellationToken.None);
        await TaskControl.EndEnvironmentRecoveryAsync();
        TaskControl.TriggerMonitor();
        await Task.Delay(50);
        Assert.Equal(0, calls);
        TaskControl.Monitor = _ => Task.CompletedTask;
    }

    [Fact]
    public async Task LateProbeCannotClearNetworkPauseBeforeBudgetAcceptsIt()
    {
        var time = new FakeTimeProvider();
        using var stop = new CancellationTokenSource();
        TaskControl.IsSuspendedByNetwork = true;
        var task = TaskControl.WaitForEnvironmentRecoveryAsync(_ =>
        {
            time.Advance(TimeSpan.FromSeconds(61));
            return Task.FromResult(true);
        }, stop.Token, new EnvironmentRecoveryWindow(time));
        await WaitUntil(() => RunnerContext.Instance.IsSuspend);
        Assert.True(TaskControl.IsSuspendedByNetwork);
        Assert.False(task.IsCompleted);
        stop.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => task);
        RunnerContext.Instance.IsSuspend = false;
        TaskControl.IsSuspendedByNetwork = false;
    }
}
