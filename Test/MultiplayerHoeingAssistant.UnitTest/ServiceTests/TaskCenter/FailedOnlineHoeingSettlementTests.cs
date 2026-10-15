using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

[Collection("TaskCenterPipeTransport")]
public sealed class FailedOnlineHoeingSettlementTests : IAsyncLifetime
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "failed-hoeing-plan-" + Guid.NewGuid().ToString("N"));
    private readonly BgiInstancePipeDouble _pipe = new();
    private string? _oldPipe;
    private readonly List<TaskCenterHost> _hosts = [];

    public Task InitializeAsync()
    {
        _oldPipe = BgiExternalClient.PipeNameOverrideForTest;
        BgiExternalClient.PipeNameOverrideForTest = _pipe.PipeName;
        _pipe.Start();
        return Task.CompletedTask;
    }

    public async Task DisposeAsync()
    {
        foreach (var host in _hosts) await host.ShutdownAsync();
        BgiExternalClient.PipeNameOverrideForTest = _oldPipe;
        await _pipe.DisposeAsync();
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private async Task<(TaskCenterHost Host, WorkflowRunRecord Run)> Prepared(BgiExternalClient? client = null)
    {
        var folder = Path.Combine(_root, Guid.NewGuid().ToString("N"));
        var epoch = client?.ServerEpoch ?? new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 };
        var host = new TaskCenterHost(Path.Combine(folder, "flows"), Path.Combine(folder, "runs"),
            Path.Combine(folder, "catalog.json"), () => client, null, null, () => (true, null),
            onlineEpochProvider: () => epoch);
        _hosts.Add(host);
        var workflows = new WorkflowStore(Path.Combine(folder, "flows"));
        var document = new WorkflowDocument
        {
            Name = "原计划", Activation = new WorkflowActivation { Status = "active" },
            Nodes = [new WorkflowNode { NodeId = "B", Kind = "resource.configGroup",
                Ref = new WorkflowResourceRef { Config = "原配置组", ConfigKey = "B#key", Revision = "body-r1" } }]
        };
        workflows.Save(document, null);
        var run = host.Runs.CreateRun(document.WorkflowId!, workflows.LoadSnapshot(document.WorkflowId!).Revision);
        run.State = WorkflowRunState.Running;
        run.Cursor = new WorkflowNodeCursor { NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        run.CurrentSubmission = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "B", 0, 0, 1),
            JobId = Guid.NewGuid().ToString("D"), NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1,
            Epoch = WorkflowStopAuthority.Epoch(epoch), WireRunId = run.WireRunId,
            Intent = SubmitIntentState.Accepted, SendAttempted = true,
            Fingerprint = "original-body", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O")
        };
        host.Runs.Update(run);
        await host.CloseOnlineHoeingSendAsync();
        var preparation = host.PrepareOnlineHoeing(7, new CommandExecutor.ExecutionBoundarySnapshot(true, true,
            epoch.ProcessId, epoch.StartTicksUtc,
            new CommandExecutor.ExecutionIdentity(Guid.NewGuid(), epoch.ProcessId, epoch.StartTicksUtc,
                Guid.Parse(run.WireRunId), Guid.Parse(run.CurrentSubmission.JobId))));
        Assert.True(preparation.Ready, preparation.Reason);
        Assert.Equal(run.RunId, preparation.ManagedRunId);
        return (host, host.Runs.Load(run.RunId)!);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task PreparedRollbackRequiresAuthoritativeSameOriginalJobStillRunning(bool sameOriginal)
    {
        using var client = new BgiExternalClient();
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BgiExternalLinkState.Ready, client.State);
        var (host, run) = await Prepared(client);
        var epoch = client.ServerEpoch!;
        _pipe.BodyForTest = (operation, _) => operation == "ext.job.status"
            ? (true, null, null, new
            {
                status = "running", bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
                job = new
                {
                    jobId = sameOriginal ? Guid.Parse(run.CurrentSubmission!.JobId!).ToString("N") : Guid.NewGuid().ToString("N"),
                    state = "running", idempotencyKey = run.CurrentSubmission!.Key, workflowRunId = run.WireRunId,
                    nodeId = "B", occurrence = 0, iteration = 0, attempt = 1, exitConfirmed = false
                }
            }) : null;

        var settled = await host.SettleFailedOnlineHoeingAsync(7, run.RunId, true, "队友准备失败");
        Assert.Equal(sameOriginal, settled);
        var saved = host.Runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Running, saved.State);
        Assert.False(saved.StopRequested);
        Assert.Equal(run.CurrentSubmission.JobId, saved.CurrentSubmission!.JobId);
        if (sameOriginal) Assert.Null(saved.OnlineHoeingHandoff);
        else Assert.Equal("prepared", saved.OnlineHoeingHandoff!.Phase);
        Assert.Equal(0, _pipe.CountOf("ext.task.cancel"));
        Assert.Equal(0, _pipe.CountOf("ext.task.start"));
        Assert.Equal(1, _pipe.CountOf("ext.job.status"));
    }

    [Fact]
    public async Task MissingPhysicalCleanupKeepsExactHandoffUnknownAndSendGateClosed()
    {
        var (host, run) = await Prepared();
        Assert.False(await host.SettleFailedOnlineHoeingAsync(7, run.RunId, false, "票据释放未知"));
        var saved = host.Runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Unknown, saved.State);
        Assert.Equal(7, saved.OnlineHoeingHandoff!.Generation);
        Assert.Equal("unknown", saved.OnlineHoeingHandoff.Phase);
        Assert.False(host.CanOpenOnlineSendAfterBatch);
        Assert.Empty(_pipe.Received);
    }

    [Fact]
    public async Task OldGenerationCannotFailOrStopCurrentPreparation()
    {
        var (host, run) = await Prepared();
        Assert.False(await host.SettleFailedOnlineHoeingAsync(6, run.RunId, false, "迟到失败"));
        Assert.False(await host.SettleFailedOnlineHoeingAsync(7, "different-run", true, "迟到失败"));
        var saved = host.Runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Running, saved.State);
        Assert.Equal("prepared", saved.OnlineHoeingHandoff!.Phase);
        Assert.False(saved.StopRequested);
        Assert.Empty(_pipe.Received);
    }

    [Theory]
    [InlineData(WorkflowRunState.Cancelled, true)]
    [InlineData(WorkflowRunState.OnlineHoeingParking, false)]
    [InlineData(WorkflowRunState.Unknown, true)]
    public async Task ConfirmedStoppedOriginalIsFinalizedWithoutCreatingAResumeJob(WorkflowRunState initialState, bool alreadyStopped)
    {
        var (host, run) = await Prepared();
        Assert.True(host.ConfirmOnlineHoeingSuspend(7, "owned-ticket", JsonSerializer.Serialize(new
        {
            status = "suspended", workflowRunId = run.WireRunId, nodeId = "B",
            originalJobId = run.CurrentSubmission!.JobId, occurrence = 0, iteration = 0, attempt = 1
        })));
        var cancelled = host.Runs.Load(run.RunId)!;
        cancelled.OnlineHoeingHandoff!.Phase = initialState == WorkflowRunState.Unknown ? "unknown" : "parked";
        cancelled.State = initialState;
        cancelled.StopRequested = alreadyStopped;
        cancelled.CurrentSubmission!.ObservedTerminal = "cancelled";
        cancelled.CurrentSubmission.ExecutionExitConfirmed = true;
        cancelled.CurrentSubmission.ExecutionExitDisposition = "execution_exited";
        cancelled.CurrentSubmission.EffectState = "cancelled";
        host.Runs.Update(cancelled);

        Assert.True(await host.SettleFailedOnlineHoeingAsync(7, run.RunId, true, "批次取消"));
        var saved = host.Runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Cancelled, saved.State);
        Assert.True(saved.StopRequested);
        Assert.Null(saved.OnlineHoeingHandoff);
        var history = Assert.Single(saved.OnlineHoeingHistory);
        Assert.Equal("stopped", history.Phase);
        Assert.Null(history.ResumeJobId);
        Assert.Null(history.Ticket);
        Assert.NotEmpty(history.TicketHash!);
        Assert.True(host.CanOpenOnlineSendAfterBatch);
        Assert.True(TerminalReleaseEvidence.RunSettled(saved));
        Assert.Empty(_pipe.Received);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, true)]
    public async Task UndrivenParkingRetainsUnconfirmedExitOrExistingResumeResponsibility(bool exitConfirmed, bool hasResume)
    {
        var (host, run) = await Prepared();
        var parked = host.Runs.Load(run.RunId)!;
        parked.State = WorkflowRunState.OnlineHoeingParking;
        parked.OnlineHoeingHandoff!.Phase = "parked";
        parked.OnlineHoeingHandoff.Ticket = "owned-ticket";
        parked.OnlineHoeingHandoff.ResumeJobId = hasResume ? Guid.NewGuid().ToString("N") : null;
        parked.CurrentSubmission!.ObservedTerminal = "cancelled";
        parked.CurrentSubmission.ExecutionExitConfirmed = exitConfirmed;
        parked.CurrentSubmission.ExecutionExitDisposition = exitConfirmed ? "execution_exited" : null;
        parked.CurrentSubmission.EffectState = "cancelled";
        host.Runs.Update(parked);

        Assert.False(await host.SettleFailedOnlineHoeingAsync(7, run.RunId, true, "批次取消"));
        var saved = host.Runs.Load(run.RunId)!;
        Assert.NotEqual(WorkflowRunState.Cancelled, saved.State);
        Assert.NotNull(saved.OnlineHoeingHandoff);
        Assert.Empty(saved.OnlineHoeingHistory);
        Assert.False(host.CanOpenOnlineSendAfterBatch);
    }

    [Fact]
    public async Task UnknownPreparedOriginalNeverGetsStoppedAsACleanupShortcut()
    {
        var (host, run) = await Prepared();
        Assert.False(await host.SettleFailedOnlineHoeingAsync(7, run.RunId, true, "准备中断"));
        var saved = host.Runs.Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Running, saved.State);
        Assert.False(saved.StopRequested);
        Assert.Equal("prepared", saved.OnlineHoeingHandoff!.Phase);
        Assert.False(host.CanOpenOnlineSendAfterBatch);
    }

    [Theory]
    [InlineData("cancelled", WorkflowRunState.OnlineHoeingParking)]
    [InlineData("succeeded", WorkflowRunState.OnlineHoeingParking)]
    [InlineData("succeeded", WorkflowRunState.Succeeded)]
    public async Task AdoptedResumeIsArchivedWithItsRealTerminalAndRetainsCompletedPlanFacts(string rawTerminal, WorkflowRunState planState)
    {
        using var client = new BgiExternalClient();
        await client.StartAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(BgiExternalLinkState.Ready, client.State);
        var (host, run) = await Prepared(client);
        Assert.True(host.ConfirmOnlineHoeingSuspend(7, "owned-ticket", JsonSerializer.Serialize(new
        {
            status = "suspended", workflowRunId = run.WireRunId, nodeId = "B",
            originalJobId = run.CurrentSubmission!.JobId, occurrence = 0, iteration = 0, attempt = 1
        })));
        var parked = host.Runs.Load(run.RunId)!;
        parked.State = WorkflowRunState.OnlineHoeingParking;
        parked.OnlineHoeingHandoff!.Phase = "parked";
        parked.CurrentSubmission!.ObservedTerminal = "cancelled";
        parked.CurrentSubmission.ExecutionExitConfirmed = true;
        parked.CurrentSubmission.ExecutionExitDisposition = "execution_exited";
        parked.CurrentSubmission.EffectState = "cancelled";
        host.Runs.Update(parked);
        var continuation = await host.PreparePlanResumeAsync(run.RunId, "owned-ticket");
        Assert.NotNull(continuation);
        var resumed = Guid.NewGuid().ToString("D");
        Assert.True(host.AcceptPlanResumeJob(run.RunId, "owned-ticket", resumed));
        if (planState == WorkflowRunState.Succeeded)
        {
            var completed = host.Runs.Load(run.RunId)!;
            completed.State = WorkflowRunState.Succeeded;
            completed.StopRequested = false;
            host.Runs.Update(completed);
        }
        var accepted = host.Runs.Load(run.RunId)!.OnlineHoeingHandoff!;
        var epoch = client.ServerEpoch!;
        _pipe.BodyForTest = (operation, _) => operation switch
        {
            "ext.job.status" => (true, null, null, new
            {
                status = rawTerminal, bgiEpoch = new { processId = epoch.ProcessId, startTicksUtc = epoch.StartTicksUtc },
                job = new
                {
                    jobId = Guid.Parse(resumed).ToString("N"), state = rawTerminal, exitConfirmed = true,
                    exitDisposition = "execution_exited", idempotencyKey = accepted.ResumeKey,
                    workflowRunId = run.WireRunId, nodeId = "B", occurrence = 0, iteration = 0, attempt = 2
                }
            }),
            _ => null
        };

        Assert.True(await host.SettleCancelledOnlinePolicyAsync(7, run.RunId).WaitAsync(TimeSpan.FromSeconds(10)));
        var settled = host.Runs.Load(run.RunId)!;
        Assert.Equal(planState == WorkflowRunState.Succeeded ? WorkflowRunState.Succeeded : WorkflowRunState.Cancelled, settled.State);
        Assert.Equal(planState != WorkflowRunState.Succeeded, settled.StopRequested);
        Assert.Null(settled.OnlineHoeingHandoff);
        var history = Assert.Single(settled.OnlineHoeingHistory);
        Assert.Equal("terminal", history.Phase);
        Assert.Equal(resumed, history.ResumeJobId); // Exact resume identity is retained, never erased for cleanup.
        Assert.Equal(rawTerminal, history.ResumeTerminal);
        Assert.Equal(run.CurrentSubmission!.JobId, Assert.Single(settled.SubmissionHistory).JobId);
        Assert.Equal(resumed, settled.CurrentSubmission!.JobId);
        Assert.Equal(rawTerminal, settled.CurrentSubmission.ObservedTerminal);
        Assert.True(TerminalReleaseEvidence.ValidOnlineHoeingHandoff(settled, history));
        Assert.True(TerminalReleaseEvidence.RunSettled(settled));
        Assert.True(host.CanOpenOnlineSendAfterBatch);
        Assert.Equal(0, _pipe.CountOf("ext.task.start"));
        Assert.Equal(0, _pipe.CountOf("task.stop"));
        if (planState == WorkflowRunState.Succeeded)
        {
            var runFile = Assert.Single(Directory.GetFiles(_root, run.RunId + ".run.json", SearchOption.AllDirectories));
            var before = File.ReadAllBytes(runFile);
            Assert.True(await host.SettleCancelledOnlinePolicyAsync(7, run.RunId));
            Assert.Equal(before, File.ReadAllBytes(runFile));
            var next = host.PrepareOnlineHoeing(8, new CommandExecutor.ExecutionBoundarySnapshot(true, false,
                epoch.ProcessId, epoch.StartTicksUtc, null));
            Assert.True(next.Ready, next.Reason);
            Assert.Null(next.ManagedRunId);
        }
    }
}
