using System.Diagnostics;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class StartupWatchdogCompletedExitTests
{
    private static StartupStep Dog() => new()
    {
        Kind = StartupStepKinds.Watchdog, WatchKind = StartupStepKinds.BgiTaskRunning,
        ExpectRunning = false, StatusSource = StartupStatusSource.CurrentSession,
        WatchIntervalSeconds = 60, WatchConfirmSeconds = 6, WatchConfirmTimes = 10,
        FireSteps = [new() { Kind = StartupStepKinds.Wait, WaitSeconds = 0 }],
    };

    private static StartupSourceIntent Parent() => StartupSourceIntent.Inherit(
        new("42:99", 0, "mount", 123, Stopwatch.Frequency));

    private static WorkflowRunRecord Completed(StartupSourceIntent source) => new()
    {
        RunId = "run", State = WorkflowRunState.Succeeded, StopAuthority = source.Authority,
        CompletionHistory = [new()
        {
            Kind = "terminal.completionAction", Action = "closeGameAndSoftware", State = "executed",
            Epoch = source.Authority!.Epoch, BodyCompletedBeforeTerminal = true,
            ObservedTerminal = "succeeded", ExecutionExitConfirmed = true, EffectState = "succeeded",
        }],
    };

    [Fact]
    public void MountedIdleWatchdog_HasPrivateScope_DoesNotAlterDebounceOrSiblingTriggers()
    {
        var parent = Parent();
        var auxiliary = parent.ForAuxiliarySteps("run");
        var step = Dog();
        var dog = auxiliary.ForWatchdog(step);
        Assert.True(dog.AllowsCompletedBgiExit);
        Assert.False(parent.AllowsCompletedBgiExit);
        Assert.False(auxiliary.AllowsCompletedBgiExit);
        Assert.False(StartupSourceIntent.NextScheduledOccurrence(dog, false).AllowsCompletedBgiExit);
        Assert.Equal("run", dog.HandoffRunId);
        Assert.Equal(parent.Authority, dog.Authority);
        Assert.Equal(60, step.WatchIntervalSeconds);
        Assert.Equal(6, step.WatchConfirmSeconds);
        Assert.Equal(10, step.WatchConfirmTimes);
        step.FireSteps.Add(new() { Kind = StartupStepKinds.StartGroup });
        Assert.False(dog.AllowsCompletedBgiExit); // 挂载后编辑也不能把离线许可扩大为任务执行。
    }

    [Theory]
    [InlineData("other-user")]
    [InlineData("startup-order")]
    [InlineData("running")]
    [InlineData("task-name")]
    [InlineData("timer")]
    [InlineData("nested-trigger")]
    [InlineData("start-bgi")]
    [InlineData("stop-bgi")]
    [InlineData("task")]
    [InlineData("handoff")]
    [InlineData("condition")]
    public void OtherTargetsAndActions_DoNotGainOfflinePermission(string change)
    {
        var step = Dog();
        switch (change)
        {
            case "other-user": step.StatusSource = StartupStatusSource.UserName; step.StatusTargetUser = "YS"; break;
            case "startup-order": step.StatusSource = StartupStatusSource.StartupOrder; break;
            case "running": step.ExpectRunning = true; break;
            case "task-name": step.WatchKind = StartupStepKinds.BgiTaskName; break;
            case "timer": step.Kind = StartupStepKinds.TimerTrigger; break;
            case "nested-trigger": step.FireSteps.Add(new() { Kind = StartupStepKinds.Watchdog }); break;
            case "start-bgi": step.FireSteps.Add(new() { Kind = StartupStepKinds.StartBgi }); break;
            case "stop-bgi": step.FireSteps.Add(new() { Kind = StartupStepKinds.StopBgi }); break;
            case "task": step.FireSteps.Add(new() { Kind = StartupStepKinds.StartGroup }); break;
            case "handoff": step.FireSteps.Add(new() { Kind = StartupStepKinds.EnterTaskCenter }); break;
            case "condition": step.FireSteps[0].NodeType = "condition"; break;
        }
        Assert.False(Parent().ForAuxiliarySteps("run").ForWatchdog(step).AllowsCompletedBgiExit);
        Assert.False(Parent().ForWatchdog(Dog()).AllowsCompletedBgiExit);
        Assert.False(Parent().ForAuxiliarySteps().ForWatchdog(Dog()).AllowsCompletedBgiExit);
    }

    [Theory]
    [InlineData("none", true)]
    [InlineData("close-software", true)]
    [InlineData("stopped", false)]
    [InlineData("cancelled", false)]
    [InlineData("failed", false)]
    [InlineData("unknown", false)]
    [InlineData("pending", false)]
    [InlineData("other-intent", false)]
    [InlineData("other-epoch", false)]
    [InlineData("changed-stop-version", false)]
    [InlineData("body-not-done", false)]
    [InlineData("unconfirmed-exit", false)]
    [InlineData("submitted", false)]
    [InlineData("effect-unknown", false)]
    [InlineData("terminal-unknown", false)]
    [InlineData("close-game-only", false)]
    [InlineData("no-history", false)]
    public void OnlyMatchingSuccessfulSoftwareExit_IsCompletionEvidence(string change, bool expected)
    {
        var source = Parent();
        var run = Completed(source);
        var effect = run.CompletionHistory[0];
        switch (change)
        {
            case "close-software": effect.Action = "closeSoftware"; break;
            case "stopped": run.StopRequested = true; break;
            case "cancelled": run.State = WorkflowRunState.Cancelled; break;
            case "failed": run.State = WorkflowRunState.Failed; break;
            case "unknown": run.State = WorkflowRunState.Unknown; break;
            case "pending": run.PendingCompletion = new(); break;
            case "other-intent": run.StopAuthority = run.StopAuthority! with { IntentId = "other" }; break;
            case "other-epoch": effect.Epoch = "43:100"; break;
            case "changed-stop-version": run.StopAuthority = run.StopAuthority! with { Version = 1 }; break;
            case "body-not-done": effect.BodyCompletedBeforeTerminal = false; break;
            case "unconfirmed-exit": effect.ExecutionExitConfirmed = false; break;
            case "submitted": effect.State = "submitted"; break;
            case "effect-unknown": effect.EffectState = "unknown"; break;
            case "terminal-unknown": effect.ObservedTerminal = null; break;
            case "close-game-only": effect.Action = "closeGame"; break;
            case "no-history": run.CompletionHistory.Clear(); break;
        }
        Assert.Equal(expected, TaskCenterHost.HasCompletedBgiExit(run, source.Authority!));
        Assert.False(TaskCenterHost.HasCompletedBgiExit(null, source.Authority!));
    }

    [Theory]
    [InlineData("idle", true)]
    [InlineData("running", false)]
    [InlineData("unavailable", null)]
    [InlineData("expired", null)]
    [InlineData("old-process", null)]
    [InlineData("missing", null)]
    public void LiveTaskReading_RequiresFreshMatchingStatus(string kind, bool? expected)
    {
        var source = Parent().ForAuxiliarySteps("run").ForWatchdog(Dog());
        var status = new ControlStatus
        {
            TaskStatusAvailable = true, TaskStatusBgiEpoch = source.Authority!.Epoch,
            TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = kind == "running",
        };
        if (kind == "unavailable") status.TaskStatusAvailable = false;
        if (kind == "expired") status.TaskStatusObservedAtUtc = DateTimeOffset.UtcNow.AddMinutes(-1);
        if (kind == "old-process") status.TaskStatusBgiEpoch = "41:98";
        var actual = MistletoeViewModel.ReadAuxiliaryIdleWatchdog(source, kind == "missing" ? null : status, false);
        Assert.Equal(expected, actual.reading);
        Assert.True(MistletoeViewModel.ReadAuxiliaryIdleWatchdog(source, null, true).reading);
    }

    [Theory]
    [InlineData("f11")]
    [InlineData("all-stop")]
    [InlineData("new-start")]
    public async Task StopOrReplaceParent_CancelsOfflineWatchdogBeforeExternalAction(string stop)
    {
        var parent = Parent();
        var dog = parent.ForAuxiliarySteps("run").ForWatchdog(Dog());
        if (stop == "new-start") parent.Revoke(); else parent.StopCurrentRun();
        Assert.True(dog.Revoked);
        Assert.Null(MistletoeViewModel.ReadAuxiliaryIdleWatchdog(dog, null, true).reading);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => dog.GetOrCheckAsync(
            (_, _, _) => throw new Exception("已停止来源不得再求值"), false, default));
    }

    [Fact]
    public async Task OfflineTrigger_RechecksSameAuthorityBeforeEachExternalStep_AndCannotCommitBgiTask()
    {
        var dog = Parent().ForAuxiliarySteps("run").ForWatchdog(Dog());
        var run = Completed(dog);
        var checkedCount = 0;
        var actionCount = 0;
        var launched = 0;
        var runner = new StartupFlowRunner((_, _) => throw new Exception("不得执行 BGI"),
            (_, _) => throw new Exception("不得重新移交"), _ => { }, (_, _) => Task.FromResult((true, "ok")),
            _ => { }, () => null, _ => { }, sourceAuthorityProvider: (source, required, _) =>
            {
                Assert.Same(dog, source);
                Assert.False(required);
                Assert.True(source.AllowsCompletedBgiExit);
                Assert.True(TaskCenterHost.HasCompletedBgiExit(run, source.Authority!));
                checkedCount++;
                return Task.FromResult(source.Authority);
            }, userProgramLauncher: (_, _, _, ct) =>
            {
                ct.ThrowIfCancellationRequested();
                launched++;
                return Task.FromResult("isolated external action");
            });
        runner.NodeStateSink = (_, state, _) => { if (state == NodeRunState.Success) actionCount++; };
        await runner.RunAsync([new() { Kind = StartupStepKinds.Wait, WaitSeconds = 0 },
            new() { Kind = StartupStepKinds.StartProgram, Path = "isolated.cmd", WindowsUserName = "test" }], default,
            new StartupTriggerInfo("watchdog", "dog", "today") { SourceIntent = dog });
        Assert.Equal(2, checkedCount);
        Assert.Equal(2, actionCount);
        Assert.Equal(1, launched);
        await Assert.ThrowsAsync<InvalidOperationException>(() => dog.GetOrCheckAsync(
            (_, _, _) => throw new Exception("辅助来源不得申请任务权限"), true, default));
    }

    [Fact]
    public async Task AcceptedHandoff_KeepsExactRunForMountedWatchdog()
    {
        var parent = Parent();
        StartupSourceIntent? mounted = null;
        var runner = new StartupFlowRunner((_, _) => throw new Exception("不得执行 BGI"),
            (_, _) => Task.FromResult(StartupHandoffResult.Accepted("run", null)), _ => { },
            (_, _) => Task.FromResult((true, "ok")), _ => { }, () => null, _ => { },
            sourceAuthorityProvider: (source, _, _) => Task.FromResult(source.Authority),
            armWatchdogWithSource: (step, source) => mounted = source.ForWatchdog(step));
        await runner.RunAsync([new() { Kind = StartupStepKinds.EnterTaskCenter,
            TaskCenterFlowId = "plan", ContinueAfterTaskCenterHandoff = true }, Dog()], default, sourceIntent: parent);
        Assert.NotNull(mounted);
        Assert.Equal("run", mounted!.HandoffRunId);
        Assert.True(mounted.AllowsCompletedBgiExit);
        Assert.Equal(parent.Authority, mounted.Authority);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task CancelDuringPreActionCheck_NeverLaunchesExternalProgram(bool stopParent)
    {
        var parent = Parent();
        var dog = parent.ForAuxiliarySteps("run").ForWatchdog(Dog());
        using var withdrawn = new CancellationTokenSource();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var launched = false;
        var runner = new StartupFlowRunner((_, _) => throw new Exception("不得执行 BGI"),
            (_, _) => throw new Exception("不得移交"), _ => { }, (_, _) => Task.FromResult((true, "ok")),
            _ => { }, () => null, _ => { }, sourceAuthorityProvider: async (source, _, ct) =>
            {
                entered.TrySetResult();
                await release.Task.WaitAsync(ct);
                return source.Authority;
            }, userProgramLauncher: (_, _, _, _) => { launched = true; return Task.FromResult("unexpected"); });
        var execution = runner.RunAsync([new() { Kind = StartupStepKinds.StartProgram,
            Path = "isolated.cmd", WindowsUserName = "test" }], withdrawn.Token,
            new StartupTriggerInfo("watchdog", "dog", "today") { SourceIntent = dog });
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        if (stopParent) parent.StopCurrentRun(); else withdrawn.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.WaitAsync(TimeSpan.FromSeconds(2)));
        Assert.False(launched);
    }

    [Fact]
    public void StrictSessionProbe_OtherUserDoesNotHideExit_AndUnreadableIdentityIsUnknown()
    {
        using var other = new Process();
        Assert.True(BgiProcessMonitor.TryGetCurrentSessionBgiProcessesStrictCore([other], 4,
            _ => 1, _ => false, out var selected, out _, _ => { }));
        Assert.Empty(selected);
        Assert.False(BgiProcessMonitor.TryGetCurrentSessionBgiProcessesStrictCore([other], 4,
            _ => throw new System.ComponentModel.Win32Exception(), _ => false, out _, out _, _ => { }));
        Assert.True(BgiProcessMonitor.TryGetCurrentSessionBgiProcessesStrictCore([other], 4,
            _ => 4, _ => false, out selected, out _, _ => { }));
        Assert.Single(selected); // 本会话重启的 BGI 必须阻断退出后的离线放行。
    }
}
