using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Input;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common;
using Xunit;

public sealed class PathEnvironmentRecoveryTests : IDisposable
{
    private const string SuspendKey = "寻路环境门控";

    public PathEnvironmentRecoveryTests()
    {
        TaskContext.Instance().Config.TpConfig.UseOfficialTeleport = false;
        TaskControl.IsSuspendedByNetwork = false;
        TaskControl.IsSuspendedByWindow = false;
        RunnerContext.Instance.IsSuspend = false;
        TpTaskFastDrag.ProbeForTest = null;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task OrdinaryPathFailurePreservesExistingRetryWithoutProbing(bool afterVerifiedNetworkRecovery)
    {
        if (afterVerifiedNetworkRecovery)
        {
            TaskControl.BeginEnvironmentRecovery(CancellationToken.None);
            try
            {
                var version = TaskControl.NetworkRecoveryVersion;
                TaskControl.IsSuspendedByNetwork = true;
                await TaskControl.RunNetworkRecoveryForTest(CancellationToken.None);
                Assert.Equal(version + 1, TaskControl.NetworkRecoveryVersion);
            }
            finally { await TaskControl.EndEnvironmentRecoveryAsync(); }
        }

        TpTaskFastDrag.ProbeForTest = _ => throw new InvalidOperationException("普通寻路卡住不应探测地图");
        var state = new Suspendable();
        RunnerContext.Instance.SuspendableDictionary[SuspendKey] = state;
        var probes = TpTaskFastDrag.ProbeCalls;
        var releases = InputHub.Releases;
        var pickPauses = RunnerContext.Instance.AutoPickTriggerStopCount;

        var recovery = TaskControl.TryRecoverPathEnvironmentAsync(CancellationToken.None);

        Assert.True(recovery.IsCompletedSuccessfully);
        Assert.False(await recovery); // 调用方继续既有 RetryException / HandledException 处理。
        Assert.Equal(probes, TpTaskFastDrag.ProbeCalls);
        Assert.Equal(releases, InputHub.Releases);
        Assert.Equal(pickPauses, RunnerContext.Instance.AutoPickTriggerStopCount);
        Assert.Equal(0, state.Suspends);
        Assert.Equal(0, state.Resumes);
        Assert.False(RunnerContext.Instance.IsSuspend);
    }

    [Theory]
    [InlineData(true, false)]
    [InlineData(false, true)]
    public async Task DetectedNetworkOrWindowFailureStillHoldsPathUntilRecoveryOrStop(bool network, bool window)
    {
        TaskControl.IsSuspendedByNetwork = network;
        TaskControl.IsSuspendedByWindow = window;
        using var stop = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        TpTaskFastDrag.ProbeForTest = async ct =>
        {
            entered.TrySetResult();
            await Task.Delay(Timeout.Infinite, ct);
            return false;
        };
        var state = new Suspendable();
        RunnerContext.Instance.SuspendableDictionary[SuspendKey] = state;
        var pickPauses = RunnerContext.Instance.AutoPickTriggerStopCount;
        var recovery = TaskControl.TryRecoverPathEnvironmentAsync(stop.Token);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.False(recovery.IsCompleted);
            Assert.Equal(pickPauses + 1, RunnerContext.Instance.AutoPickTriggerStopCount);
            Assert.Equal(1, state.Suspends);
            Assert.Equal(0, state.Resumes);
            Assert.Equal(network, TaskControl.IsSuspendedByNetwork);
            Assert.Equal(window, TaskControl.IsSuspendedByWindow);
        }
        finally
        {
            stop.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery);
        }
        Assert.Equal(pickPauses, RunnerContext.Instance.AutoPickTriggerStopCount);
        Assert.Equal(1, state.Resumes);
    }

    public void Dispose()
    {
        TpTaskFastDrag.ProbeForTest = null;
        TaskControl.IsSuspendedByNetwork = false;
        TaskControl.IsSuspendedByWindow = false;
        RunnerContext.Instance.IsSuspend = false;
        RunnerContext.Instance.SuspendableDictionary.Remove(SuspendKey);
    }
}
