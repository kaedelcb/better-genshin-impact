using System.IO;
using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>验证同机更新协作文件：发起后为“准备”，成功写“完成”并带重启时间，失败写“失败”且不重启助手，结果只写一次。</summary>
public class BgiUpdateCoordinationTests : IDisposable
{
    private readonly string _dir = Path.Combine(AppContext.BaseDirectory, "coordination-test-" + Guid.NewGuid().ToString("N"));

    public BgiUpdateCoordinationTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public async Task Begin_WritesPreparing_ThenComplete_WritesDoneWithRestart()
    {
        var session = await BgiUpdateCoordination.BeginAsync(_dir, closeBgi: true, null);
        var preparing = BgiUpdateCoordination.Read(_dir);
        Assert.Equal(BgiUpdateCoordination.Preparing, preparing?.Phase);
        Assert.Equal(session.Id, preparing?.Id);

        session.Complete(TimeSpan.FromSeconds(6));
        var done = BgiUpdateCoordination.Read(_dir);
        Assert.Equal(BgiUpdateCoordination.Done, done?.Phase);
        Assert.True(done!.RestartAssistants);
        Assert.NotNull(done.RestartAfterUtc);
    }

    [Fact]
    public async Task Fail_WritesFailedWithoutRestart_AndResultIsWrittenOnce()
    {
        var session = await BgiUpdateCoordination.BeginAsync(_dir, closeBgi: true, null);
        session.Fail();
        session.Complete(TimeSpan.Zero); // 已结束，不得改写为“完成”
        var state = BgiUpdateCoordination.Read(_dir);
        Assert.Equal(BgiUpdateCoordination.Failed, state?.Phase);
        Assert.False(state!.RestartAssistants);
    }

    // 原有状态文件测试无法发现“已关闭但启动来源检查又拉起”的竞争。
    // 以下验证在 watcher 尚未处理准备通知时，实际启动门也已拒绝；结束后正常恢复，且不影响另一目录。
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparing_BlocksLaunchBeforeWatcher_AndTerminalStateReleasesIt(bool complete)
    {
        var executable = Path.Combine(_dir, "BetterGI.exe");
        var launches = 0;
        var session = await BgiUpdateCoordination.BeginAsync(_dir, closeBgi: true, null);

        Assert.False(BgiPackageUpdateService.TryLaunchWhileNotInstalling(executable, () => launches++));
        Assert.Equal(0, launches);
        Assert.True(BgiPackageUpdateService.TryLaunchWhileNotInstalling(
            Path.Combine(_dir, "other-installation", "BetterGI.exe"), () => launches++));
        if (complete) session.Complete(TimeSpan.Zero); else session.Fail();
        Assert.True(BgiPackageUpdateService.TryLaunchWhileNotInstalling(executable, () => launches++));
        Assert.Equal(2, launches);
    }

    [Fact]
    public void ExpiredPreparing_DoesNotBlockRecoveryForever()
    {
        File.WriteAllText(BgiUpdateCoordination.PathFor(_dir), JsonSerializer.Serialize(
            new BgiUpdateCoordinationState("expired", BgiUpdateCoordination.Preparing,
                DateTimeOffset.UtcNow.AddSeconds(-1), null, false)));
        var launched = false;
        Assert.True(BgiPackageUpdateService.TryLaunchWhileNotInstalling(
            Path.Combine(_dir, "BetterGI.exe"), () => launched = true));
        Assert.True(launched);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Preparing_BlocksMonitorStart_WithoutClearingExistingStopHold(bool held)
    {
        var launches = 0;
        var startedEvents = 0;
        using var monitor = new BgiProcessMonitor(Path.Combine(_dir, "BetterGI.exe"),
            launchGuard: (_, _) => { launches++; return true; });
        monitor.OnBgiStarted += () => startedEvents++;
        if (held) monitor.HoldAutomaticRestartForUserStop();
        var session = await BgiUpdateCoordination.BeginAsync(_dir, closeBgi: true, null);
        Assert.False(monitor.RestartBgi());
        Assert.Equal(held, monitor.IsAutomaticRestartHeld);
        Assert.Equal(0, launches);
        Assert.Equal(0, startedEvents);
        session.Fail();
        Assert.True(monitor.RestartBgi());
        Assert.Equal(1, launches);
        Assert.Equal(1, startedEvents);
    }

    [Fact]
    public void LaunchGuardRejection_DoesNotClearStopHold()
    {
        using var monitor = new BgiProcessMonitor(Path.Combine(_dir, "BetterGI.exe"),
            launchGuard: (_, _) => false);
        monitor.HoldAutomaticRestartForUserStop();
        Assert.False(monitor.RestartBgi());
        Assert.True(monitor.IsAutomaticRestartHeld);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task TaskCenterReadiness_DuringUpdateReturnsRecoverableWait_RegardlessOfGuardian(bool guardBgi)
    {
        var config = new AssistConfig { BgiPath = Path.Combine(_dir, "BetterGI.exe"), GuardBgi = guardBgi };
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, config);
        var method = typeof(MainViewModel).GetMethod("EnsureTaskCenterExecutionReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var session = await BgiUpdateCoordination.BeginAsync(_dir, closeBgi: true, null);
        try
        {
            var result = await ((Task<string?>)method.Invoke(vm, [CancellationToken.None])!).WaitAsync(TimeSpan.FromSeconds(2));
            Assert.Equal(BgiUpdateCoordination.PreparingMessage, result);
            using var canceled = new CancellationTokenSource();
            canceled.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
                (Task<string?>)method.Invoke(vm, [canceled.Token])!);
        }
        finally { session.Fail(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task UpdateWait_KeepsStartupSource_AndCanBeStopped(bool stop)
    {
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm,
            new AssistConfig { BgiPath = Path.Combine(_dir, "BetterGI.exe"), GuardBgi = false });
        var method = typeof(MainViewModel).GetMethod("EnsureTaskCenterExecutionReadyAsync", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var source = StartupSourceIntent.Startup();
        var waiting = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var session = await BgiUpdateCoordination.BeginAsync(_dir, closeBgi: true, null);
        try
        {
            var recovery = StartupSourceRecovery.RefreshAsync(source, async ct =>
            {
                if (BgiUpdateCoordination.IsPreparing(_dir))
                {
                    var reason = await (Task<string?>)method.Invoke(vm, [ct])!;
                    Assert.Equal(BgiUpdateCoordination.PreparingMessage, reason);
                    waiting.TrySetResult();
                    // TaskCenterHost 将环境未就绪原因交给这一可恢复异常，来源恢复循环继续等待。
                    throw new StartupSourceUnavailableException(reason!);
                }
                return new WorkflowStopAuthorityRecord("test-epoch", 0, source.IntentId,
                    source.IntentTimestamp, System.Diagnostics.Stopwatch.Frequency);
            }, CancellationToken.None, retryDelay: TimeSpan.FromMilliseconds(10));
            await waiting.Task.WaitAsync(TimeSpan.FromSeconds(2));
            Assert.False(recovery.IsCompleted);
            Assert.False(source.Revoked);
            if (stop)
            {
                source.StopCurrentRun();
                await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recovery.WaitAsync(TimeSpan.FromSeconds(2)));
            }
            else
            {
                session.Fail();
                var authority = await recovery.WaitAsync(TimeSpan.FromSeconds(2));
                Assert.Equal(source.IntentId, authority!.IntentId);
                Assert.False(source.Revoked);
            }
        }
        finally { session.Fail(); }
    }

    [Theory]
    [InlineData(true, true, 0)]
    [InlineData(false, true, 1)]
    [InlineData(null, false, 0)]
    public void UpdateRestore_DoesNotRelaunchExistingOrUncertainTarget(bool? running, bool expected, int expectedLaunches)
    {
        var launches = 0;
        var result = BgiUpdateCoordination.RestoreBgiIfNeededCore(() => running, () => { launches++; return true; });
        Assert.Equal(expected, result);
        Assert.Equal(expectedLaunches, launches);
    }

    [Fact]
    public void UpdateRestore_ReusesProcessAlreadyStartedDuringCompletionDelay()
    {
        var running = false;
        var launches = 0;
        var kept = 0;
        bool Restore() => BgiUpdateCoordination.RestoreBgiIfNeededCore(() => running,
            () => { launches++; running = true; return true; }, () => kept++);
        Assert.True(Restore());
        Assert.True(Restore());
        Assert.Equal(1, launches);
        Assert.Equal(1, kept);
    }

    [Fact]
    public async Task UnsafeLauncher_BlocksFailureRecovery_AndOnlyANewUpdateReleasesIt()
    {
        var session = await BgiUpdateCoordination.BeginAsync(_dir, closeBgi: true, null);
        session.BlockRestart();
        session.Fail();
        session.Complete(TimeSpan.Zero);
        Assert.Equal(BgiUpdateCoordination.Blocked, BgiUpdateCoordination.Read(_dir)!.Phase);
        var executable = Path.Combine(_dir, "BetterGI.exe");
        Assert.False(BgiPackageUpdateService.TryLaunchWhileNotInstalling(executable,
            () => throw new Exception("启动文件未确认时不得恢复进程")));
        using var monitor = new BgiProcessMonitor(executable);
        var vm = new MainViewModel();
        typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm,
            new AssistConfig { BgiPath = executable, GuardBgi = true });
        typeof(MainViewModel).GetField("_processMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vm, monitor);
        typeof(MainViewModel).GetMethod("CheckBgiUpdateCoordination", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(vm, null);
        Assert.True(monitor.IsAutomaticRestartHeld);
        Assert.False(monitor.RestartBgi());

        var retry = await BgiUpdateCoordination.BeginAsync(_dir, closeBgi: true, null);
        retry.BeginInstallation();
        retry.Complete(TimeSpan.Zero);
        Assert.True(BgiPackageUpdateService.TryLaunchWhileNotInstalling(executable, () => { }));
    }

    [Fact]
    public void BlockedLauncher_DoesNotAutomaticallyRecoverWhenPrepareLifetimeExpires()
    {
        File.WriteAllText(BgiUpdateCoordination.PathFor(_dir), JsonSerializer.Serialize(
            new BgiUpdateCoordinationState("blocked", BgiUpdateCoordination.Blocked,
                DateTimeOffset.UtcNow.AddDays(-1), null, false)));
        Assert.True(BgiUpdateCoordination.IsLaunchBlocked(_dir));
    }

    [Fact]
    public async Task InstallationAndDesktopConfirmation_StayProtectedUntilExplicitCompletion()
    {
        var session = await BgiUpdateCoordination.BeginAsync(_dir, true, null);
        session.BeginInstallation();
        Assert.Equal(DateTimeOffset.MaxValue, BgiUpdateCoordination.Read(_dir)!.ExpiresUtc);
        Assert.True(BgiUpdateCoordination.IsLaunchBlocked(_dir));
        Assert.False(BgiUpdateCoordination.Read(_dir)!.RestartAssistants);
        session.Complete(TimeSpan.FromSeconds(1)); // 电脑端在完成提示返回后调用。
        Assert.Equal(BgiUpdateCoordination.Done, BgiUpdateCoordination.Read(_dir)!.Phase);
    }

    [Fact]
    public async Task ConfirmationWithoutCopiedFiles_RemainsProtected_ButCannotCountAsRepair()
    {
        var session = await BgiUpdateCoordination.BeginAsync(_dir, true, null);
        session.HoldUntilConfirmation();
        Assert.Equal(DateTimeOffset.MaxValue, BgiUpdateCoordination.Read(_dir)!.ExpiresUtc);
        Assert.True(BgiUpdateCoordination.IsLaunchBlocked(_dir));
        session.BlockRestart();
        var retry = await BgiUpdateCoordination.BeginAsync(_dir, true, null);
        retry.HoldUntilConfirmation();
        Assert.Throws<IOException>(() => retry.Complete(TimeSpan.Zero));
        retry.Fail();
        Assert.Equal(BgiUpdateCoordination.Blocked, BgiUpdateCoordination.Read(_dir)!.Phase);
    }

    [Fact]
    public async Task FailedRetry_PreservesPreviousBlockedState_AndCannotCompleteWithoutRepair()
    {
        var first = await BgiUpdateCoordination.BeginAsync(_dir, true, null);
        first.BlockRestart();
        var retry = await BgiUpdateCoordination.BeginAsync(_dir, true, null);
        Assert.Equal(DateTimeOffset.MaxValue, BgiUpdateCoordination.Read(_dir)!.ExpiresUtc);
        Assert.Throws<IOException>(() => retry.Complete(TimeSpan.Zero));
        retry.Fail();
        Assert.Equal(BgiUpdateCoordination.Blocked, BgiUpdateCoordination.Read(_dir)!.Phase);
        Assert.True(BgiUpdateCoordination.IsLaunchBlocked(_dir));
    }

    [Fact]
    public async Task TerminalWriteFailure_LeavesDurableInstallationProtection_AndCanRetryTheWrite()
    {
        var session = await BgiUpdateCoordination.BeginAsync(_dir, true, null);
        session.BeginInstallation();
        using (var held = new FileStream(BgiUpdateCoordination.PathFor(_dir), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var error = Record.Exception(() => session.BlockRestart());
            Assert.True(error is IOException or UnauthorizedAccessException, error?.ToString());
        }
        Assert.Equal(DateTimeOffset.MaxValue, BgiUpdateCoordination.Read(_dir)!.ExpiresUtc);
        Assert.True(BgiUpdateCoordination.IsLaunchBlocked(_dir));
        session.Fail();
        Assert.Equal(BgiUpdateCoordination.Blocked, BgiUpdateCoordination.Read(_dir)!.Phase);
    }

    [Fact]
    public async Task ProtectionWriteFailure_StopsBeforeAnyProductFileIsOverwritten()
    {
        var executable = Path.Combine(_dir, "BetterGI.exe");
        File.WriteAllText(executable, "original-launcher");
        var fixture = Path.Combine(AppContext.BaseDirectory, "TestData", "BetterGI_v0.64.2+lcb.22.7-NexusBGI-fix13.7z");
        var session = await BgiUpdateCoordination.BeginAsync(_dir, true, null);
        using (var held = new FileStream(BgiUpdateCoordination.PathFor(_dir), FileMode.Open, FileAccess.Read, FileShare.Read))
        {
            var result = BgiPackageUpdateService.Apply(fixture, _dir, ["User"], [], null,
                beforeOverwrite: session.BeginInstallation);
            Assert.False(result.Success);
            Assert.Equal(0, result.ExtractedFiles);
            Assert.Equal("original-launcher", File.ReadAllText(executable));
        }
        session.Fail();
        Assert.Equal(BgiUpdateCoordination.Failed, BgiUpdateCoordination.Read(_dir)!.Phase);
    }

    [Fact]
    public void Read_IgnoresMissingOrCorruptFile()
    {
        Assert.Null(BgiUpdateCoordination.Read(_dir));
        File.WriteAllText(BgiUpdateCoordination.PathFor(_dir), "{not json");
        Assert.Null(BgiUpdateCoordination.Read(_dir));
    }
}
