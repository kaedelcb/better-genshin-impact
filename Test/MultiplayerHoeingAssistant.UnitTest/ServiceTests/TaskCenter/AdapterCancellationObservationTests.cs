using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class AdapterCancellationObservationTests
{
    [Theory][InlineData("occurrence")][InlineData("attempt")]
    public async Task BodyNoJob_WrongAppearanceCannotBindOriginalResponsibility(string fault)
    {
        var port = new Port(false, "succeeded", fault);
        var runs = new RunStore(Path.Combine(Path.GetTempPath(), "body-readonly-" + Guid.NewGuid().ToString("N")));
        var run = runs.CreateRun("wf", "rev");
        run.WireRunId = "wire-original";
        run.CurrentSubmission = new() { Key = "key-original", NodeId = "node", Occurrence = 2, LoopIteration = 3, Attempt = 1,
            Epoch = "42:99", SendAttempted = true, Intent = SubmitIntentState.Submitted };
        runs.Update(run);
        var result = await new BgiWorkflowExecutionBoundary(port, runs).ReconcileSubmissionAsync(run, run.CurrentSubmission, default);
        Assert.True(result.Uncertain);
        Assert.Null(runs.Load(run.RunId)!.CurrentSubmission!.JobId);
        Assert.Equal(0, port.Sends);
        Assert.Equal(0, port.Cancels);
    }

    private sealed class Port(bool terminal, string raw, string listFault) : IBgiExecutionPort
    {
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public BgiEpoch ServerEpoch => new() { ProcessId = 42, StartTicksUtc = listFault == "epoch" ? 100 : 99 };
        public int Sends, Cancels, Queries, Lists;
        private BgiJobInfo Job(bool list = false) => new() { Epoch = ServerEpoch, JobId = "job-original",
            IdempotencyKey = "key-original", WorkflowRunId = "wire-original", NodeId = terminal ? "$flow" : "node",
            Occurrence = list && listFault == "occurrence" ? 99 : terminal ? 0 : 2,
            Iteration = list && listFault == "iteration" ? 99 : terminal ? 0 : 3,
            Attempt = list && listFault == "attempt" ? 99 : 1,
            State = raw, WasCancelled = true, ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited" };
        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        { Sends++; throw new InvalidOperationException("cleanup must never submit"); }
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
        {
            Lists++;
            return Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = ServerEpoch,
                Jobs = listFault == "missing" ? [] : listFault == "duplicate" ? [Job(true), Job(true)] : [Job(true)] });
        }
        public Task<(string?, BgiJobInfo?)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        { Queries++; return Task.FromResult<(string?, BgiJobInfo?)>(("found", Job())); }
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct)
        { Cancels++; Assert.False(ct.IsCancellationRequested); throw new IOException("cancel_transport_failed"); }
    }

    [Theory]
    [InlineData(false)][InlineData(true)]
    public async Task WrongEpochCleanup_NeverCancelsDifferentProcess(bool terminal)
    {
        var port = new Port(terminal, "succeeded", "epoch");
        var runs = new RunStore(Path.Combine(Path.GetTempPath(), "cleanup-readonly-" + Guid.NewGuid().ToString("N")));
        var run = runs.CreateRun("wf", "rev"); run.WireRunId = "wire-original";
        if (terminal)
        {
            var c = new PendingCompletionRecord { Epoch = "42:99", WireRunId = "wire-original", IdempotencyKey = "key-original", SendAttempted = true, State = "submitted", JobId = "job-original" };
            run.PendingCompletion = c; runs.Update(run);
            Assert.Equal("unknown", (await new BgiWorkflowTerminalExecutor(port, runs).ConfirmCancellationAsync(c, default)).State);
        }
        else
        {
            var a = new PrerequisiteActionRecord { Epoch = "42:99", WireRunId = "wire-original", IdempotencyKey = "key-original", NodeId = "node", Occurrence = 2, LoopIteration = 3, Attempt = 1, SendAttempted = true, JobId = "job-original" };
            run.PrerequisiteActions.Add(a); runs.Update(run);
            Assert.Equal(PrerequisiteStatus.Unknown, (await new BgiWorkflowPrerequisiteAdapter(port, runs).ConfirmCancellationAsync(a, default)).Status);
        }
        Assert.Equal(0, port.Cancels);
        Assert.Equal(0, port.Sends);
    }

    [Theory]
    [InlineData(false, "succeeded", false)][InlineData(false, "succeeded", true)]
    [InlineData(false, "failed", false)][InlineData(false, "failed", true)]
    [InlineData(false, "cancelled", false)][InlineData(false, "rejected", false)]
    [InlineData(true, "succeeded", false)][InlineData(true, "succeeded", true)]
    [InlineData(true, "cancelled", false)][InlineData(true, "rejected", true)]
    [InlineData(true, "result_unknown", false)]
    public async Task CancelTransportFailure_StillObservesOriginalJobAndPreservesRawResult(bool terminal, string raw, bool noJob)
    {
        var port = new Port(terminal, raw, "");
        var runs = new RunStore(Path.Combine(Path.GetTempPath(), "cleanup-readonly-" + Guid.NewGuid().ToString("N")));
        var run = runs.CreateRun("wf", "rev"); run.WireRunId = "wire-original";
        if (terminal)
        {
            var c = new PendingCompletionRecord { Epoch = "42:99", WireRunId = "wire-original", IdempotencyKey = "key-original",
                JobId = noJob ? null : "job-original", SendAttempted = true, State = "dispatching", Fingerprint = "fp" };
            run.PendingCompletion = c; runs.Update(run);
            var result = await new BgiWorkflowTerminalExecutor(port, runs).ConfirmCancellationAsync(c, new CancellationToken(true));
            Assert.Equal(raw == "succeeded" ? "executed" : raw == "cancelled" ? "cancelled" : raw == "rejected" ? "rejected" : "unknown", result.State);
            Assert.Equal(raw, c.ObservedTerminal); Assert.True(c.ExecutionExitConfirmed);
            Assert.Equal("job-original", c.JobId);
            if (raw == "result_unknown") Assert.Equal("unknown", c.EffectState);
        }
        else
        {
            var a = new PrerequisiteActionRecord { Epoch = "42:99", WireRunId = "wire-original", IdempotencyKey = "key-original",
                NodeId = "node", LoopIteration = 3, Occurrence = 2, Attempt = 1, JobId = noJob ? null : "job-original", SendAttempted = true };
            run.PrerequisiteActions.Add(a); runs.Update(run);
            var result = await new BgiWorkflowPrerequisiteAdapter(port, runs).ConfirmCancellationAsync(a, new CancellationToken(true));
            Assert.Equal(raw == "succeeded" ? PrerequisiteStatus.Proceed : raw == "cancelled" ? PrerequisiteStatus.Cancelled
                : raw == "rejected" ? PrerequisiteStatus.Rejected : PrerequisiteStatus.Failed, result.Status);
            Assert.Equal(raw, a.ObservedTerminal); Assert.True(a.ExecutionExitConfirmed); Assert.Equal("job-original", a.JobId);
        }
        Assert.Equal(0, port.Sends); Assert.Equal(1, port.Cancels); Assert.Equal(1, port.Queries);
        Assert.Equal(noJob ? 1 : 0, port.Lists);
    }

    [Theory]
    [InlineData(false, "missing")][InlineData(true, "missing")]
    [InlineData(false, "duplicate")][InlineData(true, "duplicate")]
    [InlineData(false, "occurrence")][InlineData(true, "occurrence")]
    [InlineData(false, "iteration")][InlineData(true, "iteration")]
    [InlineData(false, "attempt")][InlineData(true, "attempt")]
    public async Task NoJobReadback_MissingAmbiguousOrWrongOccurrenceNeverBindsOrResubmits(bool terminal, string fault)
    {
        var port = new Port(terminal, "succeeded", fault);
        var runs = new RunStore(Path.Combine(Path.GetTempPath(), "cleanup-readonly-" + Guid.NewGuid().ToString("N")));
        var run = runs.CreateRun("wf", "rev"); run.WireRunId = "wire-original";
        if (terminal)
        {
            var c = new PendingCompletionRecord { Epoch = "42:99", WireRunId = "wire-original", IdempotencyKey = "key-original", SendAttempted = true, State = "dispatching" };
            run.PendingCompletion = c; runs.Update(run);
            Assert.Equal("unknown", (await new BgiWorkflowTerminalExecutor(port, runs).ConfirmCancellationAsync(c, default)).State);
            Assert.Null(c.JobId); Assert.False(c.ExecutionExitConfirmed);
        }
        else
        {
            var a = new PrerequisiteActionRecord { Epoch = "42:99", WireRunId = "wire-original", IdempotencyKey = "key-original",
                NodeId = "node", LoopIteration = 3, Occurrence = 2, Attempt = 1, SendAttempted = true };
            run.PrerequisiteActions.Add(a); runs.Update(run);
            Assert.Equal(PrerequisiteStatus.Unknown, (await new BgiWorkflowPrerequisiteAdapter(port, runs).ConfirmCancellationAsync(a, default)).Status);
            Assert.Null(a.JobId); Assert.False(a.ExecutionExitConfirmed);
        }
        Assert.Equal(0, port.Sends); Assert.Equal(0, port.Cancels); Assert.Equal(0, port.Queries);
    }
}
