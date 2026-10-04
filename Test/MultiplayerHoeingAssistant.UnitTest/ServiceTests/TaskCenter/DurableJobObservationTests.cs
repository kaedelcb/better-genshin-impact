using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class DurableJobObservationTests
{
    // Simulated original admission input, shared by the local freeze and the server projection.
    private const string OriginalPayload = """{"taskId":"task-original","expectedConfigRevision":"config-original","idempotencyKey":"key","workflowRunId":"wire","nodeId":"node","occurrence":2,"iteration":3,"attempt":1}""";
    private static FrozenOriginalRequestEvidence OriginalEvidence() => new(1,
        BgiOriginalRequestFingerprint.Compute(BgiExternalClient.ExternalOperations.TaskStart, OriginalPayload),
        BgiExternalClient.ExternalOperations.TaskStart, "task-original", "config-original");
    private sealed class Fixture
    {
        internal readonly string StoreDir = Path.Combine(Path.GetTempPath(), "durable-observation-" + Guid.NewGuid().ToString("N"));
        internal readonly RunStore Store;
        internal readonly WorkflowRunRecord Run;
        internal readonly Port Port;
        internal readonly string Stage;
        internal Fixture(string stage, bool noJob = false)
        {
            Store = new(StoreDir); Stage = stage; Run = Store.CreateRun("wf", "rev"); Run.WireRunId = "wire";
            if (stage == "body") Run.CurrentSubmission = new() { Epoch = "42:99", Key = "key", NodeId = "node", Occurrence = 2,
                LoopIteration = 3, Attempt = 1, JobId = noJob ? null : "job", SendAttempted = true, Fingerprint = "fp", Intent = SubmitIntentState.Accepted,
                OriginalRequestEvidence = OriginalEvidence() };
            else if (stage == "prerequisite") Run.PrerequisiteActions.Add(new() { Epoch = "42:99", IdempotencyKey = "key", WireRunId = "wire",
                NodeId = "node", Occurrence = 2, LoopIteration = 3, Attempt = 1, JobId = noJob ? null : "job", SendAttempted = true, Fingerprint = "fp", State = PrerequisiteActionState.Submitted });
            else Run.PendingCompletion = new() { Epoch = "42:99", IdempotencyKey = "key", WireRunId = "wire",
                JobId = noJob ? null : "job", SendAttempted = true, Fingerprint = "fp", State = "dispatching" };
            Store.Update(Run); Port = new(stage);
        }
        internal (string? Job, string? Raw, bool Exit) Read()
        {
            var r = Store.Load(Run.RunId)!;
            return Stage == "body" ? (r.CurrentSubmission!.JobId, r.CurrentSubmission.ObservedTerminal, r.CurrentSubmission.ExecutionExitConfirmed)
                : Stage == "prerequisite" ? (r.PrerequisiteActions.Single().JobId, r.PrerequisiteActions.Single().ObservedTerminal, r.PrerequisiteActions.Single().ExecutionExitConfirmed)
                : (r.PendingCompletion!.JobId, r.PendingCompletion.ObservedTerminal, r.PendingCompletion.ExecutionExitConfirmed);
        }
        internal async Task Observe(CancellationToken ct = default)
        {
            if (Stage == "body") await new BgiWorkflowExecutionBoundary(Port, Store).AwaitSubmissionExitAsync(Run, Run.CurrentSubmission!, ct);
            else if (Stage == "prerequisite") await new BgiWorkflowPrerequisiteAdapter(Port, Store).ReconcileAsync(Run, Run.PrerequisiteActions.Single(), ct);
            else await new BgiWorkflowTerminalExecutor(Port, Store).ConfirmCancellationAsync(Run, Run.PendingCompletion!, ct);
        }
        internal async Task Cancel()
        {
            if (Stage == "prerequisite") await new BgiWorkflowPrerequisiteAdapter(Port, Store).ConfirmCancellationAsync(Run.PrerequisiteActions.Single(), default);
            else await new BgiWorkflowTerminalExecutor(Port, Store).ConfirmCancellationAsync(Run.PendingCompletion!, default);
        }
    }
    private sealed class Port(string stage) : IBgiExecutionPort
    {
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public BgiEpoch ServerEpoch => new() { ProcessId = 42, StartTicksUtc = 99 };
        internal string Raw = "succeeded";
        internal bool Exit, ThrowOnSecond, WaitForBudgetOnSecond;
        internal int Queries, Cancels, Sends;
        internal Action? AtFirstQuery, AtSecondQuery, AtCancel, AtList;
        internal BgiJobInfo Job() => new() { Epoch = ServerEpoch, JobId = "job", IdempotencyKey = "key", WorkflowRunId = "wire",
            NodeId = stage == "terminal" ? "$flow" : "node", Iteration = stage == "terminal" ? 0 : 3,
            Occurrence = stage == "terminal" ? 0 : 2, Attempt = 1, State = Raw,
            RequestFingerprint = stage == "body" ? BgiOriginalRequestFingerprint.Compute(BgiExternalClient.ExternalOperations.TaskStart, OriginalPayload) : null,
            RequestFingerprintVersion = stage == "body" ? 1 : null,
            RequestOperation = stage == "body" ? BgiExternalClient.ExternalOperations.TaskStart : null,
            TaskId = stage == "body" ? "task-original" : null, ConfigRevision = stage == "body" ? "config-original" : null,
            ExecutionExitConfirmed = Exit, ExecutionExitDisposition = Exit ? "execution_exited" : null };
        public Task<(string?, BgiJobInfo?)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        {
            if (Queries == 0) AtFirstQuery?.Invoke();
            if (++Queries == 2) { AtSecondQuery?.Invoke(); if (ThrowOnSecond) throw new OperationCanceledException("query budget expired"); }
            if (Queries == 2 && WaitForBudgetOnSecond) return WaitForCancellation(ct);
            return Task.FromResult<(string?, BgiJobInfo?)>(("found", Job()));
        }
        private static async Task<(string?, BgiJobInfo?)> WaitForCancellation(CancellationToken ct)
        { await Task.Delay(Timeout.Infinite, ct); return (null, null); }
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
        { AtList?.Invoke(); return Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = ServerEpoch, Jobs = [Job()] }); }
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct)
        { Cancels++; AtCancel?.Invoke(); return Task.CompletedTask; }
        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        { Sends++; throw new InvalidOperationException("observation must not submit"); }
    }

    [Theory]
    [InlineData("body", "succeeded")][InlineData("prerequisite", "succeeded")][InlineData("terminal", "succeeded")]
    [InlineData("body", "failed")][InlineData("prerequisite", "failed")][InlineData("terminal", "failed")]
    public async Task TerminalBeforeExit_IsDurableBeforeNextQueryOce(string stage, string raw)
    {
        var f = new Fixture(stage); f.Port.Raw = raw; f.Port.ThrowOnSecond = true;
        (string? Job, string? Raw, bool Exit) atSecond = default;
        f.Port.AtSecondQuery = () => atSecond = f.Read();
        try { await f.Observe(); } catch (OperationCanceledException) { }
        Assert.Equal(raw, atSecond.Raw); Assert.False(atSecond.Exit);
        Assert.Equal(raw, f.Read().Raw); Assert.False(f.Read().Exit); Assert.Equal(0, f.Port.Sends);
    }

    [Theory][InlineData("body")][InlineData("prerequisite")]
    public async Task DelayCancellationAfterTerminal_PreservesDurableFact(string stage)
    {
        var f = new Fixture(stage); using var cancelled = new CancellationTokenSource();
        f.Port.AtFirstQuery = () => cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Observe(cancelled.Token));
        Assert.Equal("succeeded", f.Read().Raw); Assert.False(f.Read().Exit); Assert.Equal(1, f.Port.Queries);
    }

    [Fact]
    public async Task ActualCleanupBudgetExpiry_PreservesDurableTerminalAndUnresolvedExit()
    {
        var f = new Fixture("terminal"); f.Port.WaitForBudgetOnSecond = true;
        await f.Observe();
        Assert.Equal("succeeded", f.Read().Raw); Assert.False(f.Read().Exit); Assert.Equal(2, f.Port.Queries);
    }

    [Theory][InlineData("prerequisite")][InlineData("terminal")]
    public async Task NoJobHit_IsDurablyBoundBeforeCancelRpc(string stage)
    {
        var f = new Fixture(stage, noJob: true); f.Port.Exit = true;
        string? atCancel = null;
        f.Port.AtCancel = () => atCancel = f.Read().Job;
        await f.Cancel();
        Assert.Equal("job", atCancel);
        Assert.Equal(1, f.Port.Cancels); Assert.Equal("job", f.Read().Job); Assert.Equal(0, f.Port.Sends);
    }

    [Theory][InlineData("prerequisite")][InlineData("terminal")]
    public async Task NoJobHit_PublishFailurePreventsCancelAndFakeBinding(string stage)
    {
        var f = new Fixture(stage, noJob: true); f.Port.Exit = true;
        f.Store.PublishFaultForTest = _ => new IOException("binding publication failed");
        await f.Cancel();
        Assert.Equal(0, f.Port.Cancels); Assert.Null(f.Read().Job); Assert.Equal(0, f.Port.Sends);
    }

    [Theory][InlineData(false)][InlineData(true)]
    public async Task OwnerLookup_UsesOneCompleteSnapshotAndRefusesCorruptRecord(bool corrupt)
    {
        var f = new Fixture("prerequisite", noJob: true); f.Port.Exit = true;
        if (corrupt) File.WriteAllText(Path.Combine(f.StoreDir, "orphan.run.json"), "not-json");
        var scans = 0;
        f.Store.FileOperationFaultForTest = op => { if (op == "enumerate") scans++; return null; };
        await f.Cancel();
        Assert.Equal(1, scans);
        Assert.Equal(corrupt ? 0 : 1, f.Port.Cancels);
        Assert.Equal(0, f.Port.Sends);
    }

    [Theory][InlineData("prerequisite")][InlineData("terminal")]
    public async Task NoJobHit_ConcurrentRevisionPreservesOtherWriterAndStop(string stage)
    {
        var f = new Fixture(stage, noJob: true); f.Port.Exit = true;
        f.Port.AtList = () => { var concurrent = f.Store.Load(f.Run.RunId)!; concurrent.Note = "other writer";
            concurrent.StopRequested = true; f.Store.Update(concurrent); };
        await f.Cancel();
        var saved = f.Store.Load(f.Run.RunId)!;
        Assert.Equal("other writer", saved.Note); Assert.True(saved.StopRequested);
        Assert.Equal("job", f.Read().Job); Assert.Equal(1, f.Port.Cancels);
    }

    [Theory]
    [InlineData("prerequisite", false)][InlineData("terminal", false)]
    [InlineData("prerequisite", true)][InlineData("terminal", true)]
    public async Task NoJobHit_ReplacedOrLocallyChangedFrozenIdentityNeverCancels(string stage, bool memoryOnly)
    {
        var f = new Fixture(stage, noJob: true); f.Port.Exit = true;
        f.Port.AtList = () =>
        {
            var r = memoryOnly ? f.Run : f.Store.Load(f.Run.RunId)!;
            if (stage == "prerequisite") r.PrerequisiteActions.Single().Fingerprint = "replacement";
            else r.PendingCompletion!.Fingerprint = "replacement";
            if (!memoryOnly) f.Store.Update(r);
        };
        await f.Cancel();
        Assert.Equal(0, f.Port.Cancels); Assert.Null(f.Read().Job); Assert.False(f.Read().Exit);
    }

    [Theory][InlineData("body")][InlineData("prerequisite")][InlineData("terminal")]
    public async Task LaterActiveStatusCannotEraseObservedTerminal(string stage)
    {
        var f = new Fixture(stage);
        f.Port.AtSecondQuery = () => f.Port.Raw = "running";
        await f.Observe();
        Assert.Equal("succeeded", f.Read().Raw); Assert.False(f.Read().Exit);
    }

    [Theory]
    [InlineData("body", "raw")][InlineData("prerequisite", "raw")][InlineData("terminal", "raw")]
    [InlineData("body", "exit")][InlineData("prerequisite", "exit")][InlineData("terminal", "exit")]
    [InlineData("body", "job")][InlineData("prerequisite", "job")][InlineData("terminal", "job")]
    [InlineData("body", "fingerprint")][InlineData("prerequisite", "fingerprint")][InlineData("terminal", "fingerprint")]
    [InlineData("body", "wire")][InlineData("prerequisite", "wire")][InlineData("terminal", "wire")]
    public async Task FreshRecordCannotEraseDurableObservationOrFrozenIdentity(string stage, string fault)
    {
        var f = new Fixture(stage); f.Port.Exit = true; await f.Observe();
        var r = f.Store.Load(f.Run.RunId)!;
        if (fault == "wire") r.WireRunId = "replacement";
        else if (stage == "body")
        {
            var a = r.CurrentSubmission!;
            if (fault == "raw") a.ObservedTerminal = null; else if (fault == "exit") a.ExecutionExitConfirmed = false;
            else if (fault == "job") a.JobId = "replacement"; else a.Fingerprint = "replacement";
        }
        else if (stage == "prerequisite")
        {
            var a = r.PrerequisiteActions.Single();
            if (fault == "raw") a.ObservedTerminal = null; else if (fault == "exit") a.ExecutionExitConfirmed = false;
            else if (fault == "job") a.JobId = "replacement"; else a.Fingerprint = "replacement";
        }
        else
        {
            var a = r.PendingCompletion!;
            if (fault == "raw") a.ObservedTerminal = null; else if (fault == "exit") a.ExecutionExitConfirmed = false;
            else if (fault == "job") a.JobId = "replacement"; else a.Fingerprint = "replacement";
        }
        Assert.Throws<RunRecordConflictException>(() => f.Store.Update(r));
        Assert.Equal("succeeded", f.Read().Raw); Assert.True(f.Read().Exit); Assert.Equal("job", f.Read().Job);
    }

    [Fact]
    public async Task CompletionHistoryCannotBeRemovedOrReplacedAfterDischarge()
    {
        var f = new Fixture("terminal"); f.Port.Exit = true; await f.Observe();
        var r = f.Store.Load(f.Run.RunId)!;
        r.CompletionHistory.Add(r.PendingCompletion!); r.PendingCompletion = null; f.Store.Update(r);
        var removed = f.Store.Load(r.RunId)!; removed.CompletionHistory.Clear();
        Assert.Throws<RunRecordConflictException>(() => f.Store.Update(removed));
        var replaced = f.Store.Load(r.RunId)!; replaced.CompletionHistory[0].ObservedTerminal = "cancelled";
        Assert.Throws<RunRecordConflictException>(() => f.Store.Update(replaced));
        Assert.Equal("succeeded", f.Store.Load(r.RunId)!.CompletionHistory.Single().ObservedTerminal);
    }
}
