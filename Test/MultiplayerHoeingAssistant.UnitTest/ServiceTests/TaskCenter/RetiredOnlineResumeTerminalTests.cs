using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class RetiredOnlineResumeTerminalTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "retired-resume-terminal-" + Guid.NewGuid().ToString("N"));
    private TaskCenterHost? _host;
    private readonly BgiExternalClient _client = new();
    private sealed class Port : IBgiExecutionPort
    {
        private BgiJobInfo? _job;
        internal int Starts;
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public BgiEpoch ServerEpoch { get; } = new() { ProcessId = 123, StartTicksUtc = 456 };
        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
        {
            if (operation == WorkflowStopAuthority.Operation)
                return Task.FromResult(new BgiExternalResponse { Success = true, Data = JsonSerializer.Serialize(new
                { bgiEpoch = new { processId = 123, startTicksUtc = 456 }, stopVersion = 0, monotonicFrequency = Stopwatch.Frequency }) });
            Assert.Equal(BgiExternalClient.ExternalOperations.TaskStart, operation);
            Interlocked.Increment(ref Starts);
            var json = JsonSerializer.Serialize(payload);
            var data = JsonSerializer.Deserialize<JsonElement>(json);
            var job = Guid.NewGuid().ToString("N");
            _job = new()
            {
                JobId = job, Epoch = ServerEpoch, State = "result_unknown", ExecutionExitConfirmed = false,
                IdempotencyKey = data.GetProperty("idempotencyKey").GetString(), WorkflowRunId = data.GetProperty("workflowRunId").GetString(),
                NodeId = data.GetProperty("nodeId").GetString(), Occurrence = data.GetProperty("occurrence").GetInt32(),
                Iteration = data.GetProperty("iteration").GetInt32(), Attempt = data.GetProperty("attempt").GetInt32(),
                RequestFingerprintVersion = 1, RequestFingerprint = BgiOriginalRequestFingerprint.Compute(operation, json),
                RequestOperation = operation, ConfigRevision = data.GetProperty("expectedConfigRevision").GetString(),
            };
            return Task.FromResult(new BgiExternalResponse { Success = true,
                Data = JsonSerializer.Serialize(new { status = "queued", taskHandle = job }) });
        }
        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
            => Task.FromResult((_job?.State, _job));
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
            => Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = ServerEpoch, Jobs = _job is null ? [] : [_job] });
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct)
            => throw new InvalidOperationException("terminal reconciliation must never restart or cancel another task");
    }

    private LeaseReadResult ReadLease() => new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read();
    private async Task<(WorkflowRunRecord Run, Port Port, RetiredUnknownOperationRecord[] Retired)> PrepareRetiredResumeShape()
    {
        var port = new Port();
        _host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"), Path.Combine(_root, "catalog.json"),
            () => _client, null, null, () => (true, null), localExecutionCapability: () => true,
            statusSnapshotProvider: () => new ControlStatus { TaskStatusAvailable = true, TaskStatusBgiEpoch = "123:456",
                TaskStatusObservedAtUtc = DateTimeOffset.UtcNow, TaskRunning = false },
            admissionWired: true, successorAdmissionWired: true,
            admissionSeams: new() { Epoch = "123:456", F11Active = false, Occupied = false, FactsUnknown = false,
                ProductionBoundaryFactory = (_, runs) => new BgiWorkflowExecutionBoundary(port, runs) });
        var flow = new WorkflowDocument { Name = "原计划与恢复取消", Activation = new() { Status = "active" },
            Nodes = [new() { NodeId = "B", Kind = "resource.configGroup", Ref = new() { Config = "原配置", ConfigKey = "original#key", Revision = "rev-1" } }] };
        _host.Workflows.Save(flow, null);
        Assert.True((await _host.StartWorkflowAsync(flow.WorkflowId!)).Ok);
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        WorkflowRunRecord? original;
        while ((original = _host.Runs.List().SingleOrDefault()) is not { State: WorkflowRunState.Unknown }
            || ReadLease().File?.Handoff?.RetiredUnknownOperations.Count(r => r.RunId == original.RunId) != 2)
            await Task.Delay(10, budget.Token);
        var retired = ReadLease().File!.Handoff!.RetiredUnknownOperations.Where(r => r.RunId == original.RunId).ToArray();
        Assert.Equal(2, retired.Length);
        Assert.Contains(retired, r => r.Operation.OperationType == OperationType.FlowRegistration);
        Assert.Contains(retired, r => r.Operation.OperationType == OperationType.NodeExecution);

        // Exact incident input after physical cleanup: preserve the real admitted original identities,
        // then model the stored original cancellation + retained-resume cancellation already observed.
        // This targets terminal writeback; creating the resume itself has separate transport coverage.
        var old = original.CurrentSubmission!;
        old.ObservedTerminal = "cancelled"; old.ExecutionExitConfirmed = true;
        old.ExecutionExitDisposition = "execution_exited"; old.EffectState = "cancelled";
        var resume = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(original.RunId, "B", 0, 0, 2), NodeId = "B", Attempt = 2,
            JobId = Guid.NewGuid().ToString("D"), Intent = SubmitIntentState.Accepted, SendAttempted = true,
            Epoch = old.Epoch, WireRunId = original.WireRunId, Fingerprint = "retained-resume-original",
            ExpiresAtUtc = old.ExpiresAtUtc, ObservedTerminal = "cancelled", ExecutionExitConfirmed = true,
            ExecutionExitDisposition = "execution_exited", EffectState = "cancelled",
        };
        Assert.Null(resume.OriginalRequestEvidence); // task.resume must not invent an ext.task.start payload.
        original.SubmissionHistory = [old]; original.CurrentSubmission = resume;
        original.Cursor = new() { NodeId = "B", Attempt = 2 };
        original.State = WorkflowRunState.Cancelled; original.StopRequested = true;
        original.OnlineHoeingHandoff = null;
        original.OnlineHoeingHistory = [new OnlineHoeingHandoffRecord
        {
            StateContinuation = true, PriorityRecovery = true, Generation = 7, Ticket = "original-ticket", Epoch = old.Epoch!,
            NodeId = "B", Attempt = 1, OriginalJobId = old.JobId, OriginalSubmissionKey = old.Key, Phase = "terminal",
            ResumeJobId = resume.JobId, ResumeKey = resume.Key, ResumeAttempt = 2, ResumeFingerprint = resume.Fingerprint,
            ResumeExpiresAtUtc = resume.ExpiresAtUtc, ResumeTerminal = "cancelled",
        }.Archived()];
        original.NodeOutcomes =
        [
            new() { NodeId = "B", Attempt = 1, Result = "cancelled", RawTerminal = "cancelled",
                SubmissionKey = old.Key, AcceptedSendIdentity = old.AcceptedSendIdentity,
                Reason = "online_hoeing_preempted", RetryNextAttempt = 2 },
            new() { NodeId = "B", Attempt = 2, Result = "cancelled", RawTerminal = "cancelled", SubmissionKey = resume.Key },
        ];
        Assert.True(TerminalReleaseEvidence.ValidOnlineHoeingHandoff(original, original.OnlineHoeingHistory[0]));
        Assert.True(TerminalReleaseEvidence.RunSettled(original));
        return (original, port, retired);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("resume_exit_missing")]
    [InlineData("original_mapping_mismatch")]
    public async Task RetiredOriginalOperationsCloseOnlyAfterExactResumeAndExitFacts(string? fault)
    {
        var prepared = await PrepareRetiredResumeShape();
        var run = prepared.Run;
        if (fault == "resume_exit_missing") run.CurrentSubmission!.ExecutionExitConfirmed = false;
        File.WriteAllText(Path.Combine(_root, "runs", run.RunId + ".run.json"), JsonSerializer.Serialize(run));
        if (fault == "original_mapping_mismatch")
        {
            var path = Path.Combine(_root, "arbitration", "arbitration-lease.json");
            var file = JsonSerializer.Deserialize<LogicalOwnerLeaseFile>(File.ReadAllText(path))!;
            file.Handoff!.RetiredUnknownOperations.Single(r => r.RunId == run.RunId
                && r.Operation.OperationType == OperationType.NodeExecution).Operation.WireSubmitKey = "different-original-key";
            File.WriteAllText(path, JsonSerializer.Serialize(file));
        }
        var result = await _host!.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop);
        var after = _host.Runs.Load(run.RunId)!;
        var retired = ReadLease().File!.Handoff!.RetiredUnknownOperations.Where(r => r.RunId == run.RunId).ToArray();
        Assert.Equal(1, prepared.Port.Starts);
        Assert.Equal(2, retired.Length);
        if (fault is not null)
        {
            Assert.Equal(HostActionStatus.Unavailable, result.Status);
            Assert.Null(after.TerminalRelease);
            Assert.All(retired, r => Assert.Null(r.Operation.TerminalReleaseEvidence));
            return;
        }
        Assert.Equal(HostActionStatus.Effective, result.Status);
        Assert.True(TerminalReleaseEvidence.ValidRunSeal(after));
        Assert.All(retired, current =>
        {
            var before = prepared.Retired.Single(r => r.Operation.RequestIdentity == current.Operation.RequestIdentity);
            Assert.Equal(before.Reason, current.Reason); Assert.Equal(before.RetiredAtUtc, current.RetiredAtUtc);
            Assert.Equal(OperationRequestState.Accepted, current.Operation.RequestState);
            var seal = current.Operation.OperationType == OperationType.NodeExecution
                ? TerminalReleaseEvidence.NodeSeal(after, current.Operation) : after.TerminalRelease;
            Assert.Equal("runstore-seal:" + seal!.Id, current.Operation.TerminalReleaseEvidence);
            current.Operation.TerminalReleaseEvidence = null;
            Assert.Equal(JsonSerializer.Serialize(before.Operation), JsonSerializer.Serialize(current.Operation));
        });
        Assert.DoesNotContain(ReadLease().File!.Handoff!.Operations, op => op.RunBinding == run.RunId);
        var revision = after.RecordRevision;
        Assert.Equal(HostActionStatus.Effective, (await _host.RequestRunActionAsync(run.RunId, WorkflowRunAction.Stop)).Status);
        Assert.Equal(revision, _host.Runs.Load(run.RunId)!.RecordRevision);
        Assert.Equal(1, prepared.Port.Starts);
    }

    public void Dispose()
    {
        _host?.ShutdownAsync().GetAwaiter().GetResult(); _client.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
