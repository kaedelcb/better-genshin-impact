using System.Diagnostics;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class StartupAuxiliaryScopeTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "startup-aux-" + Guid.NewGuid().ToString("N"));
    private readonly BgiExternalClient _client = new();
    private readonly Boundary _boundary = new();
    private readonly TaskCenterHost _host;
    private int _ensureCalls;

    public StartupAuxiliaryScopeTests()
    {
        SetClient(true);
        _host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"), Path.Combine(_root, "catalog.json"),
            () => _client, null, (_, flows, runs) => new WorkflowRunner(flows, runs, _boundary, _boundary, _boundary),
            () => (_client.State == BgiExternalLinkState.Ready, "not ready"), localExecutionCapability: () => true,
            ensureExecutionReady: _ => { _ensureCalls++; return Task.FromResult<string?>("测试中禁止启动真实 BGI"); });
    }

    private Task<WorkflowStopAuthorityRecord?> Check(StartupSourceIntent source, bool required = false)
        => source.GetOrCheckAsync((s, r, ct) => StartupSourceRecovery.RefreshAsync(s,
            token => _host.RefreshStartupStopAuthorityAsync(s, r, token), ct), required, default);

    private void StopPlan(StartupSourceIntent parent)
    {
        var run = _host.Runs.CreateRun("plan", "revision", stopAuthority: parent.Authority);
        run.StopRequested = true;
        run.State = WorkflowRunState.Cancelled;
        _host.Runs.Update(run);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanStop_DoesNotCancelAuxiliary_EvenWhenEarlierParentCallbackObservesStop(bool stopBeforeFork)
    {
        var parent = StartupSourceIntent.Startup();
        await Check(parent, true);
        var auxiliary = stopBeforeFork ? null : parent.ForAuxiliarySteps();
        StopPlan(parent);
        await Assert.ThrowsAsync<StartupPlanStoppedException>(() => Check(parent));
        await Assert.ThrowsAsync<StartupPlanStoppedException>(() => Check(parent));
        Assert.True(parent.Revoked);
        Assert.False(parent.LifetimeToken.IsCancellationRequested);
        auxiliary ??= parent.ForAuxiliarySteps();
        Assert.Equal(parent.Authority, await Check(auxiliary));
        Assert.False(auxiliary.Revoked);
        Assert.Equal(0, _ensureCalls);
        await Assert.ThrowsAsync<InvalidOperationException>(() => _host.RefreshStartupStopAuthorityAsync(auxiliary, true, default));
        parent.Revoke(); // 显式新一轮启动、关闭或切角色仍撤销整组旧来源。
        Assert.True(auxiliary.Revoked);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => Check(auxiliary));
    }

    [Fact]
    public async Task Auxiliary_DoesNotStartBgiWhenOffline_AndRetainsGlobalStopFence()
    {
        var parent = StartupSourceIntent.Startup();
        await Check(parent, true);
        var auxiliary = parent.ForAuxiliarySteps();
        SetClient(false);
        await Assert.ThrowsAsync<StartupSourceUnavailableException>(() => _host.RefreshStartupStopAuthorityAsync(auxiliary, false, default));
        Assert.Equal(0, _ensureCalls);
        Assert.False(auxiliary.Revoked);
        _boundary.Epoch = "43:100";
        SetClient(true);
        Assert.Equal("43:100", (await Check(auxiliary))!.Epoch); // 外部重启后仅接回观察能力。
        Assert.Equal(parent.IntentTimestamp, auxiliary.IntentTimestamp);
        Assert.Equal(0, _ensureCalls);
        _boundary.Version++;
        await Assert.ThrowsAsync<StartupSourceStoppedException>(() => Check(auxiliary));
        Assert.True(auxiliary.StoppedByUser);
        Assert.True(auxiliary.Revoked);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void NextTimerOccurrence_PreservesAuxiliaryLimit_WithNewLifetime(bool stopped)
    {
        var parent = StartupSourceIntent.Startup();
        var auxiliary = parent.ForAuxiliarySteps();
        if (stopped) parent.StopCurrentRun();
        var next = StartupSourceIntent.NextScheduledOccurrence(auxiliary, false);
        Assert.True(next.AuxiliaryOnly);
        Assert.False(next.Revoked);
        Assert.NotEqual(auxiliary.IntentId, next.IntentId);
        Assert.False(next.LifetimeToken.IsCancellationRequested);
        Assert.False(StartupSourceIntent.NextScheduledOccurrence(parent, false).AuxiliaryOnly);
    }

    [Theory]
    [InlineData("timer")]
    [InlineData("watchdog")]
    [InlineData("log")]
    public async Task AuxiliaryCallbacks_CannotStartStopOrReenterBgi_AndNestedTriggerKeepsScope(string triggerKind)
    {
        var parent = StartupSourceIntent.Startup();
        await Check(parent, true);
        var auxiliary = parent.ForAuxiliarySteps();
        var bgiCalls = 0;
        var handoffs = 0;
        StartupSourceIntent? nested = null;
        var runner = new StartupFlowRunner((_, _) => { bgiCalls++; return Task.FromResult(new CommandResult { Status = "success" }); },
            (_, _) => { handoffs++; return Task.FromResult(StartupHandoffResult.Accepted("run", null)); },
            _ => { }, (_, _) => Task.FromResult((true, "ok")), _ => { }, () => null, _ => { },
            sourceAuthorityProvider: (s, r, ct) => _host.RefreshStartupStopAuthorityAsync(s, r, ct),
            armTimerWithSource: (_, source) => nested = source);
        var steps = new List<StartupStep>
        {
            new() { Kind = StartupStepKinds.StartBgi }, new() { Kind = StartupStepKinds.StopBgi },
            new() { Kind = StartupStepKinds.StartGroup, TaskName = "old-plan" },
            new() { Kind = StartupStepKinds.StartOneClick, TaskName = "old-plan" },
            new() { Kind = StartupStepKinds.TimerTrigger, TriggerTime = "23:59", FireSteps = [new() { Kind = StartupStepKinds.EndFlow }] },
            new() { Kind = StartupStepKinds.EnterTaskCenter, TaskCenterFlowId = "old-plan" },
        };
        await runner.RunAsync(steps, default, new StartupTriggerInfo(triggerKind, "aux", "today") { SourceIntent = auxiliary });
        Assert.Equal(0, bgiCalls);
        Assert.Equal(0, handoffs);
        Assert.Same(auxiliary, nested);
        Assert.True(nested!.AuxiliaryOnly);
    }

    [Fact]
    public async Task HandoffWithContinuation_PassesAuxiliarySourceToWatchdog()
    {
        var parent = StartupSourceIntent.Startup();
        await Check(parent, true);
        StartupSourceIntent? mounted = null;
        var runner = new StartupFlowRunner((_, _) => throw new Exception("不得启动真实程序"),
            (_, _) => { StopPlan(parent); parent.StopWithPlan(); return Task.FromResult(StartupHandoffResult.Accepted("run", null)); },
            _ => { }, (_, _) => Task.FromResult((true, "ok")), _ => { }, () => null, _ => { },
            sourceAuthorityProvider: (s, r, ct) => _host.RefreshStartupStopAuthorityAsync(s, r, ct),
            armWatchdogWithSource: (_, source) => mounted = source);
        await runner.RunAsync([new() { Kind = StartupStepKinds.EnterTaskCenter, TaskCenterFlowId = "plan", ContinueAfterTaskCenterHandoff = true },
            new() { Kind = StartupStepKinds.Watchdog, WatchKind = StartupStepKinds.BgiTaskRunning }], default, sourceIntent: parent);
        Assert.NotNull(mounted);
        Assert.True(mounted!.AuxiliaryOnly);
        Assert.False(mounted.Revoked);
        Assert.Equal(parent.Authority, await Check(mounted));
        parent.StopCurrentRun();
        Assert.True(mounted.Revoked);
        Assert.True(mounted.StoppedByUser);
    }

    private void SetClient(bool ready)
    {
        typeof(BgiExternalClient).GetProperty(nameof(BgiExternalClient.State))!.SetValue(_client,
            ready ? BgiExternalLinkState.Ready : BgiExternalLinkState.Down);
        var parts = _boundary.Epoch.Split(':');
        typeof(BgiExternalClient).GetProperty(nameof(BgiExternalClient.ServerEpoch))!.SetValue(_client,
            new BgiEpoch { ProcessId = int.Parse(parts[0]), StartTicksUtc = long.Parse(parts[1]) });
    }

    private sealed class Boundary : IWorkflowExecutionBoundary, IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public string Epoch = "42:99";
        public long Version;
        public bool SingleNativeSupported => false;
        public bool RequiresStopAuthority => true;
        public Task<WorkflowStopAuthorityRecord?> AcquireStopAuthorityAsync(string id, long timestamp, CancellationToken ct)
            => Task.FromResult<WorkflowStopAuthorityRecord?>(new(Epoch, Version, id, timestamp, Stopwatch.Frequency));
        public Task<bool?> InspectStopAuthorityAsync(WorkflowStopAuthorityRecord authority, CancellationToken ct)
            => Task.FromResult<bool?>(authority.Epoch == Epoch && authority.Version == Version);
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct) => throw new Exception("禁止真实任务");
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct) => throw new Exception("禁止真实任务");
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("isolated"));
    }

    public void Dispose()
    {
        _client.Dispose();
        if (Directory.Exists(_root)) Directory.Delete(_root, true);
    }
}
