using System.Text.Json;
using MultiplayerHoeingAssistant.ViewModels;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class TaskCenterControlRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "control-recovery-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { if (Directory.Exists(_root)) Directory.Delete(_root, true); }

    private TaskCenterHost Host(bool wired, bool f11 = false) => new(
        Path.Combine(_root, "flows"), Path.Combine(_root, "runs"), Path.Combine(_root, "catalog.json"),
        () => null, null, (_, w, r) => new WorkflowRunner(w, r, new Boundary(), new Preparation(), new Terminal()),
        () => (true, null), admissionWired: wired,
        admissionSeams: new TaskCenterAdmissionSeams
        {
            Epoch = "42:100", F11Active = f11, OwnershipTtlSeconds = 1, SuppressLeaseHeartbeatForTest = true,
        });

    private static WorkflowRunRecord Seed(TaskCenterHost host, bool unknown = false)
    {
        var flow = new WorkflowDocument { Name = "孤立运行", Activation = new() { Status = "active" },
            Nodes = [new() { NodeId = "end", Kind = "control.end" }] };
        var revision = host.Workflows.Save(flow, null);
        var run = host.Runs.CreateRun(flow.WorkflowId!, revision, handoff: new HandoffIdentity
        {
            IntentKey = "original-start", ExecutionId = "original-execution", StepId = "start",
            Mode = StartupHandoffModes.Start,
        }, admissionSourceScope: "bgi:local:42:100");
        run.State = unknown ? WorkflowRunState.Unknown : WorkflowRunState.Running;
        run.Cursor = new() { NodeId = "end", Attempt = 1 };
        run.ExtensionData = new() { ["pathLayout"] = JsonSerializer.SerializeToElement("[\"主车道\"]") };
        host.Runs.Update(run);
        return run;
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("resume")]
    [InlineData("reset")]
    public async Task ExpiredOwner_ExplicitControlReacquires_WithoutRevivingOldWriter(string action)
    {
        var host = Host(true);
        try
        {
            host.EnsureRecovered(); // recovery barrier is already complete before the orphan appears
            var oldStore = host.Runs;
            var run = Seed(host);
            await Task.Delay(1200);
            var result = action switch
            {
                "stop" => await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop),
                "resume" => await host.ResumeRunAsync(run.RunId),
                _ => await host.ResetWorkflowExecutionStateAsync(run.WorkflowId),
            };
            Assert.True(result.Ok, result.Message);
            Assert.NotSame(oldStore, host.Runs);
            Assert.Throws<RunRecordConflictException>(() => oldStore.Update(oldStore.Load(run.RunId)!));
            for (var i = 0; i < 200 && host.IsDriving(run.WorkflowId); i++) await Task.Delay(10);
            var saved = host.Runs.Load(run.RunId)!;
            if (action == "reset") Assert.True(TaskCenterHost.IsExecutionResetReleased(saved));
            else Assert.Equal(action == "stop" ? WorkflowRunState.Cancelled : WorkflowRunState.Succeeded, saved.State);
            Assert.Single(host.Runs.List()); // no substitute run and no duplicate task
        }
        finally { await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("stop")]
    [InlineData("resume")]
    public async Task CompletedRecoveryBarrier_DoesNotHideLaterOrphan(string action)
    {
        var host = Host(false);
        try
        {
            host.EnsureRecovered();
            var run = Seed(host);
            var result = action == "stop" ? await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop)
                : await host.ResumeRunAsync(run.RunId);
            Assert.True(result.Ok, result.Message);
            for (var i = 0; i < 200 && host.IsDriving(run.WorkflowId); i++) await Task.Delay(10);
            Assert.Equal(action == "stop" ? WorkflowRunState.Cancelled : WorkflowRunState.Succeeded,
                host.Runs.Load(run.RunId)!.State);
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task UnconfirmedOriginalSend_ResumeKeepsUnknownAndDoesNotSend()
    {
        var host = Host(false);
        try
        {
            host.EnsureRecovered();
            var run = Seed(host);
            run.CurrentSubmission = new() { NodeId = "end", Key = "original", Epoch = "42:100",
                Intent = SubmitIntentState.Accepted, SendAttempted = true, JobId = "original-job" };
            host.Runs.Update(run);
            var result = await host.ResumeRunAsync(run.RunId);
            Assert.False(result.Ok);
            Assert.Equal(WorkflowRunState.Unknown, host.Runs.Load(run.RunId)!.State);
            Assert.Equal("original-job", host.Runs.Load(run.RunId)!.CurrentSubmission!.JobId);
            Assert.False(host.IsDriving(run.WorkflowId));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task F11_ResumeDoesNotAcquireOrNormalize()
    {
        var host = Host(true, f11: true);
        // Seed the preexisting file with the old legacy writer before the managed fence exists.
        var seedHost = Host(false);
        var run = Seed(seedHost);
        try
        {
            var result = await host.ResumeRunAsync(run.RunId);
            Assert.False(result.Ok);
            Assert.Contains("F11", result.Message);
            Assert.False(File.Exists(Path.Combine(_root, "arbitration", "arbitration-lease.json")));
            Assert.Equal(WorkflowRunState.Running, host.Runs.Load(run.RunId)!.State);
        }
        finally { await host.ShutdownAsync(); await seedHost.ShutdownAsync(); }
    }

    [Fact]
    public async Task DifferentHealthyOwner_IsNotTakenOverByStop()
    {
        var host = Host(true);
        using var renewLifetime = new CancellationTokenSource();
        Task? renew = null;
        try
        {
            host.EnsureRecovered();
            var run = Seed(host);
            await Task.Delay(1200);
            var store = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration"));
            var mono = TimeSpan.Zero;
            var observer = new LeaseTakeoverObserver(() => mono);
            Assert.Null(observer.Observe(store.Read()));
            mono = TimeSpan.FromSeconds(2);
            var acquired = store.TryAcquire("another-owner", 15, observer.Observe(store.Read()));
            Assert.True(acquired.Success);
            var bytes = File.ReadAllBytes(Path.Combine(_root, "runs", run.RunId + ".run.json"));
            renew = Task.Run(async () =>
            {
                try
                {
                    while (true)
                    {
                        await Task.Delay(100, renewLifetime.Token);
                        Assert.True(store.TryRenewLatest(acquired.Lease!.LeaseId, "another-owner").Success);
                    }
                }
                catch (OperationCanceledException) { }
            });
            var result = await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop);
            Assert.False(result.Ok);
            Assert.Equal("another-owner", store.Read().File!.Lease!.OwnerEpoch);
            Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(_root, "runs", run.RunId + ".run.json")));
        }
        finally { renewLifetime.Cancel(); if (renew is not null) await renew; await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ExpiredOwner_WithLiveWaitingDriver_StopAndResetCanFinish(bool reset)
    {
        var host = Host(true);
        try
        {
            var flow = new WorkflowDocument { Name = "等待中的原驱动", Activation = new() { Status = "active" },
                Nodes = [new() { NodeId = "end", Kind = "control.end" }],
                Triggers = [new() { Kind = "trigger.time", Params = new()
                {
                    ["time"] = JsonSerializer.SerializeToElement(DateTime.Now.AddHours(1).ToString("HH:mm")),
                    ["missPolicy"] = JsonSerializer.SerializeToElement("nextDay"),
                } }] };
            host.Workflows.Save(flow, null);
            Assert.True((await host.StartWorkflowAsync(flow.WorkflowId!)).Ok);
            for (var i = 0; i < 200 && !host.Runs.List().Any(r => r.State == WorkflowRunState.Waiting); i++) await Task.Delay(10);
            var run = Assert.Single(host.Runs.List());
            Assert.Equal(WorkflowRunState.Waiting, run.State);
            var originalStore = host.Runs;
            await Task.Delay(1200);
            var result = reset ? await host.ResetWorkflowExecutionStateAsync(run.WorkflowId)
                : await host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop);
            Assert.True(result.Ok, result.Message);
            Assert.False(host.IsDriving(run.WorkflowId));
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
            Assert.True(host.Runs.Load(run.RunId)!.StopRequested);
            Assert.NotSame(originalStore, host.Runs);
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task OrphanCardsKeepExplicitControls_ButStoppedRunCannotResume()
    {
        var host = Host(false);
        try
        {
            var run = Seed(host, unknown: true);
            run.CurrentSubmission = new() { Intent = SubmitIntentState.Accepted, SendAttempted = true,
                JobId = "original", AcceptedSendIdentity = "sub:original:1",
                OriginalRequestEvidence = new(1, new string('A', 64), "ext.task.start", null, "rev") };
            Assert.True(ActiveRunVm.Build(run, host).CanStop);
            Assert.True(ActiveRunVm.Build(run, host).CanResume);
            run.StopRequested = true;
            host.Runs.Update(run);
            Assert.False(ActiveRunVm.Build(run, host).CanResume);
            Assert.False((await host.ResumeRunAsync(run.RunId)).Ok);
            Assert.True(host.Runs.Load(run.RunId)!.StopRequested);
        }
        finally { await host.ShutdownAsync(); }
    }

    private sealed class Boundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
            => throw new InvalidOperationException("control.end must never submit a task");
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string id, CancellationToken ct)
            => throw new InvalidOperationException("no task to poll");
        public Task RequestCancelAsync(string id, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Preparation : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct) => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }
    private sealed class Terminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct) => Task.FromResult(TerminalExecutionResult.Executed("terminal"));
    }
}
