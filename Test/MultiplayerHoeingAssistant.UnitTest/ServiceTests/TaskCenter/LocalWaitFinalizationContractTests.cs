using System;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>SB21-4 BO-13: terminal handling for a parked run stays in the current host session.</summary>
[Collection("LocalWaitSnapshotProbe")]
public sealed class LocalWaitFinalizationContractTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sb21-4-bo13-" + Guid.NewGuid().ToString("N"));
    private readonly string _flowsDir;
    private readonly string _runsDir;
    private readonly WorkflowStore _workflows;
    private readonly RunStore _runs;

    public LocalWaitFinalizationContractTests()
    {
        _flowsDir = Path.Combine(_root, "flows");
        _runsDir = Path.Combine(_root, "runs");
        _workflows = new WorkflowStore(_flowsDir);
        _runs = new RunStore(_runsDir);
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private sealed class FakeBoundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => false;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
            => Task.FromResult(BoundarySubmitResult.AcceptedWith("job-bo13"));
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        public Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class NoopPrerequisite : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }

    private sealed class NoopTerminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("job-terminal"));
    }

    private string SeedWorkflow()
    {
        var flow = new WorkflowDocument
        {
            Name = "SB21-4 parked stop",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes =
            [
                new WorkflowNode
                {
                    NodeId = "n1", Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "cfg", Revision = "rev-1" },
                },
            ],
        };
        _workflows.Save(flow, null);
        return flow.WorkflowId!;
    }

    private TaskCenterHost MakeHost()
    {
        var boundary = new FakeBoundary();
        return new TaskCenterHost(_flowsDir, _runsDir, Path.Combine(_root, "catalog-cache.json"),
            () => null, null,
            (_, workflows, runs) => new WorkflowRunner(workflows, runs, boundary,
                new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null));
    }

    private TaskCenterHost MakeWaitParkingHost(bool admissionWired = false, Action<int>? runnerFactoryEntered = null,
        Action<string>? log = null)
    {
        TaskCenterHost? host = null;
        var runnerFactoryCalls = 0;
        const string scope = "bgi:local:sb21-4-test-epoch";
        var decisionSource = new WaitDecisionSource(request => new LocalWaitDecisionRecord
        {
            Kind = LocalWaitDecisionKind.Wait,
            Context = MakeValidContext(request, scope, "request-" + request.RunId),
            Reason = "SB21-4 recovery/repark fixture",
            NoSendConfirmed = true,
        });
        var seams = admissionWired
            ? new TaskCenterAdmissionSeams { Epoch = "9:900", Occupied = false, FactsUnknown = false }
            : null;
        host = new TaskCenterHost(_flowsDir, _runsDir, Path.Combine(_root, "catalog-cache.json"),
            () => null, log,
            (_, workflows, runs) =>
            {
                runnerFactoryEntered?.Invoke(Interlocked.Increment(ref runnerFactoryCalls));
                return new WorkflowRunner(workflows, runs, new FakeBoundary(),
                    new NoopPrerequisite(), new NoopTerminal(), localWaitQueue: host!.LocalWaitQueue,
                    localWaitPrerequisiteReferenceProvider: (_, _) => "wf/n1/0/0@sb21-4-test",
                    localWaitAdmissionScopeProvider: _ => scope,
                    waitDecisionSource: decisionSource);
            },
            () => (true, null), admissionWired: admissionWired,
            arbitrationDir: admissionWired ? Path.Combine(_root, "arbitration") : null,
            admissionSeams: seams);
        return host;
    }

    private async Task<(TaskCenterHost Host, string WorkflowId, WorkflowRunRecord Run)> StartAdmissionWiredParkedRun(
        Action<string>? log = null)
    {
        var workflowId = SeedWorkflow();
        var host = MakeWaitParkingHost(admissionWired: true, log: log);
        var start = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, start.Status);
        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(10)));
        var run = Assert.Single(host.Runs.List());
        Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
        return (host, workflowId, run);
    }

    private List<OperationRecord> ReadAdmissionOperationsForRun(string runId)
        => new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read()
            .File?.Handoff?.Operations?.Where(op => op.RunBinding == runId).ToList() ?? [];

    private static LocalWaitDecisionContext MakeValidContext(WaitDecisionRequest request, string scope, string sourceIdentity)
    {
        var candidate = TaskCenterHost.BuildSuccessorIdentityCandidate(scope, request.WorkflowId, request.RunId,
            request.NodeId, request.Occurrence, request.LoopIteration, request.Attempt);
        var (admissionIdentity, candidateId) = LocalWaitIdentityTranslation.BuildAdmissionIdentity(candidate);
        return new LocalWaitDecisionContext
        {
            RunId = request.RunId,
            WorkflowId = request.WorkflowId,
            WorkflowRevision = request.WorkflowRevision,
            RecordRevision = request.RecordRevision,
            CursorNodeId = request.CursorNodeId,
            CursorOccurrence = request.CursorOccurrence,
            CursorLoopIteration = request.CursorLoopIteration,
            NodeId = request.NodeId,
            SequenceIndex = request.SequenceIndex,
            Occurrence = request.Occurrence,
            LoopIteration = request.LoopIteration,
            Attempt = request.Attempt,
            SourceKind = LocalWaitSourceKind.PanelFlowRegistration,
            SourceIdentity = sourceIdentity,
            Scope = scope,
            CandidateId = candidateId,
            AdmissionIdentity = admissionIdentity,
            Tier = candidate.Tier,
            Priority = candidate.Priority,
            HasTrustedRankingFacts = true,
        };
    }

    private WorkflowRunRecord SeedParkedRun(TaskCenterHost host, string workflowId,
        LocalWaitDecisionKind decisionKind, bool createQueueItem, bool unresolvedExternalFact = false,
        bool driftQueuePayload = false, LocalWaitSourceKind sourceKind = LocalWaitSourceKind.PanelFlowRegistration)
    {
        const string scope = "bgi:local:test-epoch";
        var handoff = sourceKind == LocalWaitSourceKind.StartupHandoff
            ? new HandoffIdentity
            {
                IntentKey = "sb21-4-startup-handoff-" + Guid.NewGuid().ToString("N"),
                ExecutionId = "sb21-4-execution",
                StepId = "sb21-4-step",
                Mode = StartupHandoffModes.Start,
            }
            : null;
        var run = _runs.CreateRun(workflowId, "revision-sb21-4", handoff: handoff,
            admissionSourceScope: handoff is null ? null : scope);
        const string nodeId = "n1";
        const int occurrence = 2;
        const int loopIteration = 1;
        const int attempt = 3;
        var stableIdentity = $"{run.RunId}|{nodeId}|{occurrence}|{loopIteration}";
        var itemId = LocalWaitQueuePolicy.DeriveItemId(stableIdentity);
        var queuedAt = DateTimeOffset.UtcNow;
        var candidate = TaskCenterHost.BuildSuccessorIdentityCandidate(scope, workflowId, run.RunId,
            nodeId, occurrence, loopIteration, attempt);
        var (admissionIdentity, candidateId) = LocalWaitIdentityTranslation.BuildAdmissionIdentity(candidate);
        var binding = new LocalWaitBinding
        {
            ItemId = itemId,
            StableIdentity = stableIdentity,
            CandidateId = candidateId,
            AdmissionIdentity = admissionIdentity,
            Namespace = workflowId,
            WorkflowId = workflowId,
            SourceKind = sourceKind,
            SourceIdentity = sourceKind == LocalWaitSourceKind.StartupHandoff ? run.RunId : "request-" + run.RunId,
            RunId = run.RunId,
            Scope = scope,
            WorkflowRevision = run.WorkflowRevision,
            NodeId = nodeId,
            SequenceIndex = 0,
            RecordRevision = run.RecordRevision,
            CursorNodeId = nodeId,
            CursorOccurrence = occurrence,
            CursorLoopIteration = loopIteration,
            Occurrence = occurrence,
            LoopIteration = loopIteration,
            Attempt = attempt,
            Tier = ArbitrationTier.Plan,
            Priority = 0,
            IsHoeingHighest = false,
            ScheduledAt = null,
            PrerequisiteReference = "wf/n1/2/1@bo13-test",
            EnqueuedAtUtc = queuedAt,
        };

        run.State = WorkflowRunState.LocalWaitParking;
        run.Cursor = new WorkflowNodeCursor
        {
            NodeId = nodeId, Occurrence = occurrence, LoopIteration = loopIteration, Attempt = attempt,
        };
        run.NodeOutcomes.Add(new WorkflowNodeOutcome
        {
            NodeId = nodeId, SequenceIndex = 0, Occurrence = 1, LoopIteration = loopIteration,
            Result = "succeeded", RawTerminal = "succeeded",
        });
        run.CurrentSubmission = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, nodeId, occurrence, loopIteration, attempt),
            NodeId = nodeId,
            Occurrence = occurrence,
            LoopIteration = loopIteration,
            Attempt = attempt,
            Intent = unresolvedExternalFact ? SubmitIntentState.Accepted : SubmitIntentState.LocalWaitDeferred,
            JobId = unresolvedExternalFact ? "job-unresolved" : null,
            SendAttempted = unresolvedExternalFact,
            AcceptedSendIdentity = unresolvedExternalFact ? "send-unresolved" : null,
            ObservedTerminal = null,
        };
        var context = new LocalWaitDecisionContext
        {
            RunId = run.RunId,
            WorkflowId = workflowId,
            WorkflowRevision = run.WorkflowRevision,
            RecordRevision = run.RecordRevision,
            CursorNodeId = nodeId,
            CursorOccurrence = occurrence,
            CursorLoopIteration = loopIteration,
            NodeId = nodeId,
            SequenceIndex = 0,
            Occurrence = occurrence,
            LoopIteration = loopIteration,
            Attempt = attempt,
            SourceKind = sourceKind,
            SourceIdentity = binding.SourceIdentity,
            Scope = binding.Scope,
            CandidateId = binding.CandidateId,
            AdmissionIdentity = binding.AdmissionIdentity,
            Tier = ArbitrationTier.Plan,
            Priority = 0,
            HasTrustedRankingFacts = true,
        };
        run.LocalWaitDecision = new LocalWaitDecisionRecord
        {
            Kind = decisionKind,
            Context = context,
            Binding = decisionKind == LocalWaitDecisionKind.Wait ? binding : null,
            Reason = decisionKind == LocalWaitDecisionKind.Wait
                ? "确定零发送等待：SB21-4 fixture"
                : "持久拒登：SB21-4 fixture",
            NoSendConfirmed = true,
        };
        run.Note = "SB21-4 fixture run note";
        _runs.Update(run);

        if (createQueueItem)
        {
            var item = binding.ToQueueItem();
            if (driftQueuePayload) item.CandidateId += "-drift";
            host.LocalWaitQueue.Upsert(item);
        }
        return _runs.Load(run.RunId)!;
    }

    [Fact]
    public async Task LocalWaitParkingStop_FinalizesAndTombstonesItsBoundQueueItem()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var unrelated = new LocalWaitItem
        {
            ItemId = "unrelated-wait",
            StableIdentity = "another-run|n9|0|0",
            CandidateId = "candidate-other",
            Namespace = "another-flow",
            WorkflowId = "another-flow",
            Tier = ArbitrationTier.Plan,
            Priority = 4,
            PrerequisiteReference = "wf/another-flow/n9@fixture",
            EnqueuedAtUtc = DateTimeOffset.UtcNow,
        };
        host.LocalWaitQueue.Upsert(unrelated);
        var itemsBefore = host.LocalWaitQueue.Load().ToList();
        var before = Assert.Single(itemsBefore, item => item.WorkflowId == workflowId);
        var unrelatedBefore = Assert.Single(itemsBefore, item => item.ItemId == unrelated.ItemId);
        var beforeRevision = run.RecordRevision;
        using var queueJsonBefore = JsonDocument.Parse(File.ReadAllText(host.LocalWaitQueue.FilePath));
        var generationHighWaterBefore = queueJsonBefore.RootElement.GetProperty("generationHighWater").GetInt64();

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.True(result.Status == HostActionStatus.Effective, result.Message);
        var persisted = new RunStore(_runsDir).Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Cancelled, persisted.State);
        Assert.Equal(run.RunId, persisted.RunId);
        Assert.Equal(run.WorkflowId, persisted.WorkflowId);
        Assert.Equal(run.Cursor!.NodeId, persisted.Cursor!.NodeId);
        Assert.Equal(run.Cursor.Occurrence, persisted.Cursor.Occurrence);
        Assert.Equal(run.Cursor.LoopIteration, persisted.Cursor.LoopIteration);
        Assert.Equal(run.Cursor.Attempt, persisted.Cursor.Attempt);
        Assert.Equal(run.CurrentSubmission!.Key, persisted.CurrentSubmission!.Key);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, persisted.CurrentSubmission.Intent);
        Assert.Equal(run.NodeOutcomes.Select(outcome => new
            {
                outcome.NodeId, outcome.SequenceIndex, outcome.Occurrence, outcome.LoopIteration,
                outcome.Result, outcome.RawTerminal, outcome.SubmissionKey, outcome.Attempt,
                outcome.AcceptedSendIdentity, outcome.Reason,
            }), persisted.NodeOutcomes.Select(outcome => new
            {
                outcome.NodeId, outcome.SequenceIndex, outcome.Occurrence, outcome.LoopIteration,
                outcome.Result, outcome.RawTerminal, outcome.SubmissionKey, outcome.Attempt,
                outcome.AcceptedSendIdentity, outcome.Reason,
            }));
        Assert.Equal(run.LocalWaitDecision, persisted.LocalWaitDecision);
        Assert.True(persisted.RecordRevision > beforeRevision);

        var persistedQueueItems = new LocalWaitQueueStore(_runsDir).Load();
        Assert.Equal(itemsBefore.Count, persistedQueueItems.Count);
        var persistedQueueItem = Assert.Single(persistedQueueItems, item => item.ItemId == before.ItemId);
        Assert.Equal(before.ItemId, persistedQueueItem.ItemId);
        Assert.Equal(before.StableIdentity, persistedQueueItem.StableIdentity);
        Assert.Equal(before.Generation, persistedQueueItem.Generation);
        Assert.Equal(LocalWaitItemState.Cancelled, persistedQueueItem.State);
        var unrelatedAfter = Assert.Single(persistedQueueItems, item => item.ItemId == unrelated.ItemId);
        Assert.Equal(unrelatedBefore.Generation, unrelatedAfter.Generation);
        Assert.Equal(LocalWaitItemState.Waiting, unrelatedAfter.State);
        Assert.Equal(unrelatedBefore.CandidateId, unrelatedAfter.CandidateId);
        Assert.Equal(unrelatedBefore.StableIdentity, unrelatedAfter.StableIdentity);

        using var queueJsonAfter = JsonDocument.Parse(File.ReadAllText(host.LocalWaitQueue.FilePath));
        Assert.Equal(generationHighWaterBefore, queueJsonAfter.RootElement.GetProperty("generationHighWater").GetInt64());
        var queueBytesAfterStop = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        var recordRevisionAfterStop = persisted.RecordRevision;
        var repeated = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
        Assert.Equal(HostActionStatus.Unavailable, repeated.Status);
        Assert.Contains("终态", repeated.Message);
        Assert.Equal(queueBytesAfterStop, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
        Assert.Equal(recordRevisionAfterStop, new RunStore(_runsDir).Load(run.RunId)!.RecordRevision);

        var newRun = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, newRun.Status);
        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        Assert.Contains(new RunStore(_runsDir).List(), next => next.RunId != run.RunId
            && next.WorkflowId == workflowId && next.State == WorkflowRunState.Succeeded);
    }

    [Fact]
    public void LocalWaitParkingStop_RefusesToCancelQueueItemWhosePayloadDrifted()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait,
            createQueueItem: true, driftQueuePayload: true);
        var queueBefore = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var recordBefore = File.ReadAllBytes(runPath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(WorkflowRunState.LocalWaitParking, new RunStore(_runsDir).Load(run.RunId)!.State);
        Assert.Equal(queueBefore, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
        Assert.Equal(recordBefore, File.ReadAllBytes(runPath));
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(host.LocalWaitQueue.Load()).State);
    }

    [Fact]
    public void LocalWaitParkingStop_CorruptQueueRefusesTerminalizationAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var invalidBytes = System.Text.Encoding.UTF8.GetBytes("{ definitely-not-json");
        File.WriteAllBytes(host.LocalWaitQueue.FilePath, invalidBytes);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(WorkflowRunState.LocalWaitParking, new RunStore(_runsDir).Load(run.RunId)!.State);
        Assert.Equal(invalidBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public async Task ResumeThatReparks_RemainsStoppableAndReleasesSameFlowForNewRun()
    {
        var workflowId = SeedWorkflow();
        var host = MakeWaitParkingHost();

        var firstStart = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, firstStart.Status);
        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        var firstRun = Assert.Single(new RunStore(_runsDir).List());
        Assert.Equal(WorkflowRunState.LocalWaitParking, firstRun.State);
        var firstBinding = firstRun.LocalWaitDecision?.Binding;
        var firstContextRevision = firstRun.LocalWaitDecision!.Context.RecordRevision;
        Assert.NotNull(firstBinding);
        var firstQueueItem = Assert.Single(host.LocalWaitQueue.Load());
        Assert.Equal(LocalWaitItemState.Waiting, firstQueueItem.State);
        using var queueBeforeResume = JsonDocument.Parse(File.ReadAllText(host.LocalWaitQueue.FilePath));
        var generationHighWaterBeforeResume = queueBeforeResume.RootElement.GetProperty("generationHighWater").GetInt64();

        var resume = await host.ResumeRunAsync(firstRun.RunId);
        Assert.Equal(HostActionStatus.Registered, resume.Status);
        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        var reparking = new RunStore(_runsDir).Load(firstRun.RunId)!;
        Assert.Equal(WorkflowRunState.LocalWaitParking, reparking.State);
        Assert.Equal(firstRun.Cursor!.NodeId, reparking.Cursor!.NodeId);
        Assert.Equal(firstRun.Cursor.Occurrence, reparking.Cursor.Occurrence);
        Assert.Equal(firstRun.Cursor.LoopIteration, reparking.Cursor.LoopIteration);
        Assert.Equal(firstRun.Cursor.Attempt, reparking.Cursor.Attempt);
        Assert.Equal(firstRun.CurrentSubmission!.Key, reparking.CurrentSubmission!.Key);
        Assert.Equal(firstBinding, reparking.LocalWaitDecision!.Binding);
        Assert.True(reparking.LocalWaitDecision.Context.RecordRevision > firstContextRevision,
            "same-process Resume must refresh the decision snapshot while retaining the original queue binding");
        Assert.True(reparking.LocalWaitDecision.Binding!.RecordRevision <= reparking.LocalWaitDecision.Context.RecordRevision,
            "valid repark keeps the immutable queue binding at or before its refreshed decision snapshot");
        Assert.True(reparking.LocalWaitDecision.Context.RecordRevision < reparking.RecordRevision,
            "valid repark refreshes the decision snapshot before the run record advances again");
        var queueAfterRepark = Assert.Single(host.LocalWaitQueue.Load());
        Assert.Equal(firstBinding!.ItemId, queueAfterRepark.ItemId);
        Assert.Equal(firstQueueItem.Generation, queueAfterRepark.Generation);
        using var queueAfterReparkJson = JsonDocument.Parse(File.ReadAllText(host.LocalWaitQueue.FilePath));
        Assert.Equal(generationHighWaterBeforeResume,
            queueAfterReparkJson.RootElement.GetProperty("generationHighWater").GetInt64());

        var stop = host.RequestRunAction(firstRun.RunId, WorkflowRunAction.Stop);
        Assert.True(stop.Status == HostActionStatus.Effective, stop.Message);
        Assert.Equal(WorkflowRunState.Cancelled, new RunStore(_runsDir).Load(firstRun.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);

        var secondStart = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, secondStart.Status);
        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        var runs = new RunStore(_runsDir).List().ToList();
        Assert.Equal(2, runs.Count);
        Assert.Contains(runs, run => run.RunId != firstRun.RunId
            && run.WorkflowId == workflowId && run.State == WorkflowRunState.LocalWaitParking);
    }

    [Fact]
    public async Task RestartRecoveryThenResumeReparksRetainsQueueGenerationAndAllowsExplicitStop()
    {
        var workflowId = SeedWorkflow();
        var firstHost = MakeWaitParkingHost();
        var firstStart = await firstHost.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, firstStart.Status);
        Assert.True(SpinWait.SpinUntil(() => !firstHost.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        var originalRun = Assert.Single(firstHost.Runs.List());
        Assert.Equal(WorkflowRunState.LocalWaitParking, originalRun.State);
        var originalBinding = originalRun.LocalWaitDecision!.Binding!;
        var originalItem = Assert.Single(firstHost.LocalWaitQueue.Load());
        using var beforeRestartQueue = JsonDocument.Parse(File.ReadAllText(firstHost.LocalWaitQueue.FilePath));
        var originalHighWater = beforeRestartQueue.RootElement.GetProperty("generationHighWater").GetInt64();
        await firstHost.ShutdownAsync();

        var recoveredHost = MakeWaitParkingHost();
        try
        {
            recoveredHost.EnsureRecovered();
            var recovered = recoveredHost.Runs.Load(originalRun.RunId)!;
            Assert.Equal(WorkflowRunState.Interrupted, recovered.State);
            Assert.True(recovered.RecordRevision > originalRun.RecordRevision,
                "startup recovery must persist the LocalWaitParking to Interrupted transition before explicit Resume");
            Assert.Equal(originalBinding, recovered.LocalWaitDecision!.Binding);
            Assert.Equal(originalRun.Cursor!.NodeId, recovered.Cursor!.NodeId);
            Assert.Equal(originalRun.Cursor.Occurrence, recovered.Cursor.Occurrence);
            Assert.Equal(originalRun.Cursor.LoopIteration, recovered.Cursor.LoopIteration);
            Assert.Equal(originalRun.Cursor.Attempt, recovered.Cursor.Attempt);
            Assert.Equal(originalRun.LocalWaitDecision!.Context.RecordRevision,
                recovered.LocalWaitDecision.Context.RecordRevision);

            var resume = await recoveredHost.ResumeRunAsync(originalRun.RunId);
            Assert.Equal(HostActionStatus.Registered, resume.Status);
            Assert.True(SpinWait.SpinUntil(() => !recoveredHost.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
            var reparking = recoveredHost.Runs.Load(originalRun.RunId)!;
            Assert.Equal(WorkflowRunState.LocalWaitParking, reparking.State);
            Assert.Equal(originalBinding, reparking.LocalWaitDecision!.Binding);
            Assert.True(reparking.LocalWaitDecision.Context.RecordRevision > recovered.LocalWaitDecision!.Context.RecordRevision,
                "explicit Resume must refresh the decision snapshot while retaining the prior immutable binding");
            Assert.True(reparking.LocalWaitDecision.Binding!.RecordRevision <= reparking.LocalWaitDecision.Context.RecordRevision);
            Assert.True(reparking.LocalWaitDecision.Context.RecordRevision < reparking.RecordRevision);
            var reparkedItem = Assert.Single(recoveredHost.LocalWaitQueue.Load());
            Assert.Equal(originalItem.Generation, reparkedItem.Generation);
            using var afterRestartQueue = JsonDocument.Parse(File.ReadAllText(recoveredHost.LocalWaitQueue.FilePath));
            Assert.Equal(originalHighWater, afterRestartQueue.RootElement.GetProperty("generationHighWater").GetInt64());

            var stop = recoveredHost.RequestRunAction(originalRun.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Effective, stop.Status);
            Assert.Equal(WorkflowRunState.Cancelled, recoveredHost.Runs.Load(originalRun.RunId)!.State);
            Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(recoveredHost.LocalWaitQueue.Load()).State);
            using var afterStopQueue = JsonDocument.Parse(File.ReadAllText(recoveredHost.LocalWaitQueue.FilePath));
            Assert.Equal(originalHighWater, afterStopQueue.RootElement.GetProperty("generationHighWater").GetInt64());

            var nextStart = await recoveredHost.StartWorkflowAsync(workflowId);
            Assert.Equal(HostActionStatus.Registered, nextStart.Status);
            Assert.True(SpinWait.SpinUntil(() => !recoveredHost.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
            Assert.Contains(recoveredHost.Runs.List(), candidate => candidate.RunId != originalRun.RunId
                && candidate.WorkflowId == workflowId && candidate.State == WorkflowRunState.LocalWaitParking);
        }
        finally { await recoveredHost.ShutdownAsync(); }
    }

    [Fact]
    public void LocalWaitHoldStop_TerminatesPersistedRejectedRegistrationWithoutBinding()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Hold, createQueueItem: false);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.True(result.Status == HostActionStatus.Effective, result.Message);
        var persisted = new RunStore(_runsDir).Load(run.RunId)!;
        Assert.Equal(WorkflowRunState.Cancelled, persisted.State);
        Assert.Equal(LocalWaitDecisionKind.Hold, persisted.LocalWaitDecision!.Kind);
        Assert.Null(persisted.LocalWaitDecision.Binding);
        Assert.Contains("持久拒登", persisted.LocalWaitDecision.Reason);
        Assert.Empty(new LocalWaitQueueStore(_runsDir).Load());
        Assert.Empty(host.ListActiveRuns());
        Assert.Contains(host.ListHistoryRuns(), history => history.RunId == run.RunId);
    }

    [Fact]
    public void LocalWaitParkingStop_WithUnresolvedExternalFactDoesNotFinalizeOrCleanQueue()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait,
            createQueueItem: true, unresolvedExternalFact: true);
        var queueItem = Assert.Single(host.LocalWaitQueue.Load());

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(WorkflowRunState.LocalWaitParking, new RunStore(_runsDir).Load(run.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(new LocalWaitQueueStore(_runsDir).Load()).State);
        Assert.Equal(queueItem.Generation, Assert.Single(host.LocalWaitQueue.Load()).Generation);
    }

    [Fact]
    public void LocalWaitParkingStop_WithUnresolvedPrerequisiteResponsibilityPreservesRunAndQueue()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
        {
            NodeId = run.Cursor!.NodeId,
            Occurrence = run.Cursor.Occurrence,
            LoopIteration = run.Cursor.LoopIteration,
            Attempt = run.Cursor.Attempt,
            StrategyIndex = 1,
            Kind = "account.switch",
            IdempotencyKey = "prereq-unknown-" + run.RunId,
            Fingerprint = "fixture",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            SendAttempted = true,
            JobId = "prereq-job-unknown",
            State = PrerequisiteActionState.Unknown,
        });
        _runs.Update(run);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Theory]
    [InlineData(PrerequisiteActionState.Intent)]
    [InlineData(PrerequisiteActionState.Submitted)]
    [InlineData(PrerequisiteActionState.Unknown)]
    [InlineData((PrerequisiteActionState)12345)]
    public void LocalWaitParkingStop_IndependentlyRefusesEachUnresolvedPrerequisiteState(
        PrerequisiteActionState state)
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
        {
            NodeId = run.Cursor!.NodeId,
            Occurrence = run.Cursor.Occurrence,
            LoopIteration = run.Cursor.LoopIteration,
            Attempt = run.Cursor.Attempt,
            StrategyIndex = 1,
            Kind = "account.switch",
            IdempotencyKey = "prereq-state-" + state + "-" + run.RunId,
            Fingerprint = "fixture",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            State = state,
        });
        _runs.Update(run);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Theory]
    [InlineData(true, null)]
    [InlineData(false, "known-job-with-unresolved-state")]
    public void LocalWaitParkingStop_UnknownPrerequisiteRemainsUnresolvedForEachPersistedSendFact(
        bool sendAttempted, string? jobId)
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
        {
            NodeId = run.Cursor!.NodeId,
            Occurrence = run.Cursor.Occurrence,
            LoopIteration = run.Cursor.LoopIteration,
            Attempt = run.Cursor.Attempt,
            StrategyIndex = 1,
            Kind = "account.switch",
            IdempotencyKey = "prereq-unknown-fact-" + run.RunId,
            Fingerprint = "fixture",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            SendAttempted = sendAttempted,
            JobId = jobId,
            State = PrerequisiteActionState.Unknown,
        });
        _runs.Update(run);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Theory]
    [InlineData(PrerequisiteActionState.Succeeded)]
    [InlineData(PrerequisiteActionState.Failed)]
    [InlineData(PrerequisiteActionState.Cancelled)]
    public void LocalWaitParkingStop_AllowsKnownTerminalPrerequisiteResponsibilities(
        PrerequisiteActionState state)
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
        {
            NodeId = run.Cursor!.NodeId,
            Occurrence = run.Cursor.Occurrence,
            LoopIteration = run.Cursor.LoopIteration,
            Attempt = run.Cursor.Attempt,
            StrategyIndex = 1,
            Kind = "account.switch",
            IdempotencyKey = "prereq-terminal-" + state + "-" + run.RunId,
            Fingerprint = "fixture",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            State = state,
        });
        _runs.Update(run);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Effective, result.Status);
        Assert.Equal(WorkflowRunState.Cancelled, new RunStore(_runsDir).Load(run.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
    }

    [Fact]
    public void LocalWaitParkingStop_DoesNotTreatTerminalSendAttemptAsUnresolvedResponsibility()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
        {
            NodeId = run.Cursor!.NodeId,
            Occurrence = run.Cursor.Occurrence,
            LoopIteration = run.Cursor.LoopIteration,
            Attempt = run.Cursor.Attempt,
            StrategyIndex = 1,
            Kind = "account.switch",
            IdempotencyKey = "prereq-complete-send-" + run.RunId,
            Fingerprint = "fixture",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            SendAttempted = true,
            State = PrerequisiteActionState.Succeeded,
        });
        _runs.Update(run);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Effective, result.Status);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
    }

    [Fact]
    public void LocalWaitParkingStop_DoesNotTreatTerminalJobIdAsUnresolvedResponsibility()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.PrerequisiteActions.Add(new PrerequisiteActionRecord
        {
            NodeId = run.Cursor!.NodeId,
            Occurrence = run.Cursor.Occurrence,
            LoopIteration = run.Cursor.LoopIteration,
            Attempt = run.Cursor.Attempt,
            StrategyIndex = 1,
            Kind = "account.switch",
            IdempotencyKey = "prereq-complete-job-" + run.RunId,
            Fingerprint = "fixture",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(1).ToString("O"),
            JobId = "known-terminal-job",
            State = PrerequisiteActionState.Succeeded,
        });
        _runs.Update(run);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Effective, result.Status);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsDecisionWhoseCursorSnapshotDisagrees()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.LocalWaitDecision = run.LocalWaitDecision! with
        {
            Context = run.LocalWaitDecision.Context with { CursorOccurrence = run.Cursor!.Occurrence + 1 },
        };
        _runs.Update(run);
        var runBytes = File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json"));
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json")));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsDecisionNotBoundToDeferredSubmission()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.CurrentSubmission!.Key = "different-submission-key";
        _runs.Update(run);
        var runBytes = File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json"));
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json")));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsMissingCanonicalAdmissionIdentity()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        run.LocalWaitDecision = run.LocalWaitDecision! with
        {
            Context = run.LocalWaitDecision.Context with { AdmissionIdentity = null },
        };
        _runs.Update(run);
        var runBytes = File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json"));
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(Path.Combine(_runsDir, run.RunId + ".run.json")));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsStartupHandoffIdentityBorrowedFromOtherRunAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait,
            createQueueItem: true, sourceKind: LocalWaitSourceKind.StartupHandoff);
        var otherRun = _runs.CreateRun(workflowId, "revision-sb21-4-other");
        var decision = run.LocalWaitDecision!;
        run.LocalWaitDecision = decision with
        {
            Binding = decision.Binding! with { SourceIdentity = otherRun.RunId },
            Context = decision.Context with { SourceIdentity = otherRun.RunId },
        };
        _runs.Update(run);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
        Assert.Equal(WorkflowRunState.LocalWaitParking, new RunStore(_runsDir).Load(run.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(host.LocalWaitQueue.Load()).State);
    }

    [Fact]
    public void LocalWaitParkingStop_AcceptsStartupHandoffRunIdentityAndFinalizesExactBinding()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait,
            createQueueItem: true, sourceKind: LocalWaitSourceKind.StartupHandoff);
        Assert.Equal(run.RunId, run.LocalWaitDecision!.Binding!.SourceIdentity);
        Assert.Equal(run.RunId, run.LocalWaitDecision.Context.SourceIdentity);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Effective, result.Status);
        Assert.Equal(WorkflowRunState.Cancelled, new RunStore(_runsDir).Load(run.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsCanonicalAdmissionIdentityFromWrongCandidateNamespace()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var decision = run.LocalWaitDecision!;
        var binding = decision.Binding!;
        var wrongCandidate = TaskCenterHost.BuildSuccessorIdentityCandidate(binding.Scope, run.WorkflowId,
            run.RunId, run.Cursor!.NodeId, run.Cursor.Occurrence, run.Cursor.LoopIteration, run.Cursor.Attempt);
        wrongCandidate.Namespace = "manual";
        var (wrongAdmissionIdentity, wrongCandidateId) = LocalWaitIdentityTranslation.BuildAdmissionIdentity(wrongCandidate);
        var wrongBinding = binding with
        {
            AdmissionIdentity = wrongAdmissionIdentity,
            CandidateId = wrongCandidateId,
        };
        run.LocalWaitDecision = decision with
        {
            Binding = wrongBinding,
            Context = decision.Context with
            {
                AdmissionIdentity = wrongAdmissionIdentity,
                CandidateId = wrongCandidateId,
            },
        };
        _runs.Update(run);

        var queueJson = JsonNode.Parse(File.ReadAllText(host.LocalWaitQueue.FilePath))!.AsObject();
        var queuedItem = queueJson["items"]!.AsArray()
            .Single(item => item!["itemId"]!.GetValue<string>() == binding.ItemId)!.AsObject();
        queuedItem["admissionIdentity"] = wrongAdmissionIdentity;
        queuedItem["candidateId"] = wrongCandidateId;
        File.WriteAllText(host.LocalWaitQueue.FilePath,
            queueJson.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsRunIdMismatchBeforeTouchingEitherRecordOrQueue()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var runA = _runs.CreateRun(workflowId, "revision-sb21-4-A");
        var runB = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var pathA = Path.Combine(_runsDir, runA.RunId + ".run.json");
        var pathB = Path.Combine(_runsDir, runB.RunId + ".run.json");
        var runAOriginalBytes = File.ReadAllBytes(pathA);
        File.WriteAllBytes(pathA, File.ReadAllBytes(pathB));
        var runAWithMisboundBytes = File.ReadAllBytes(pathA);
        var runBBytes = File.ReadAllBytes(pathB);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(runA.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.NotEqual(runAOriginalBytes, runAWithMisboundBytes);
        Assert.Equal(runAWithMisboundBytes, File.ReadAllBytes(pathA));
        Assert.Equal(runBBytes, File.ReadAllBytes(pathB));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void RequestRunAction_RejectsEmptyRunIdBeforeReadingOrTouchingStores()
    {
        var host = MakeHost();
        var result = host.RequestRunAction(" ", WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Contains("runId 为空", result.Message);
        Assert.Empty(host.Runs.List());
        Assert.False(File.Exists(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_FinalReadRejectsConcurrentRunIdMisbindAndPreservesQueueBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var runA = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var runB = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var pathA = Path.Combine(_runsDir, runA.RunId + ".run.json");
        var pathB = Path.Combine(_runsDir, runB.RunId + ".run.json");
        var runABytes = File.ReadAllBytes(pathA);
        var runBBytes = File.ReadAllBytes(pathB);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        var loadCount = 0;
        host.Runs.BeforeLoadForTest = id =>
        {
            if (id == runA.RunId && Interlocked.Increment(ref loadCount) == 2)
                File.WriteAllBytes(pathA, runBBytes);
        };

        HostActionResult? action = null;
        Exception? thrown;
        try { thrown = Record.Exception(() => action = host.RequestRunAction(runA.RunId, WorkflowRunAction.Stop)); }
        finally { host.Runs.BeforeLoadForTest = null; }

        Assert.Null(thrown);
        Assert.Equal(2, loadCount);
        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
        Assert.NotEqual(runABytes, runBBytes);
        Assert.Equal(runBBytes, File.ReadAllBytes(pathA));
        Assert.Equal(runBBytes, File.ReadAllBytes(pathB));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_FinalReadMalformedRecordIsUnavailableAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var path = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(path);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        var malformedBytes = System.Text.Encoding.UTF8.GetBytes("{ malformed-final-read");
        var loadCount = 0;
        host.Runs.BeforeLoadForTest = id =>
        {
            if (id == run.RunId && Interlocked.Increment(ref loadCount) == 2)
                File.WriteAllBytes(path, malformedBytes);
        };

        HostActionResult? action = null;
        Exception? thrown;
        try { thrown = Record.Exception(() => action = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop)); }
        finally { host.Runs.BeforeLoadForTest = null; }

        Assert.Null(thrown);
        Assert.Equal(2, loadCount);
        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
        Assert.Contains("停驻运行记录复核失败", action.Message);
        Assert.Equal(malformedBytes, File.ReadAllBytes(path));
        Assert.NotEqual(runBytes, malformedBytes);
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_FinalReadExceptionIsUnavailableAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var path = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(path);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        var loadCount = 0;
        host.Runs.BeforeLoadForTest = id =>
        {
            if (id == run.RunId && Interlocked.Increment(ref loadCount) == 2)
                throw new IOException("SB21-4 injected final run read failure");
        };

        HostActionResult? action = null;
        Exception? thrown;
        try { thrown = Record.Exception(() => action = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop)); }
        finally { host.Runs.BeforeLoadForTest = null; }

        Assert.Null(thrown);
        Assert.Equal(2, loadCount);
        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
        Assert.Contains("停驻运行记录复核失败", action.Message);
        Assert.Equal(runBytes, File.ReadAllBytes(path));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_RejectsDecisionSnapshotOlderThanItsBindingAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var decision = run.LocalWaitDecision!;
        var binding = decision.Binding!;
        var newerBinding = binding with { RecordRevision = binding.RecordRevision + 1 };
        run.LocalWaitDecision = decision with
        {
            Binding = newerBinding,
        };
        host.Runs.Update(run);
        host.LocalWaitQueue.Upsert(newerBinding.ToQueueItem());

        var path = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(path);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
        Assert.Equal(runBytes, File.ReadAllBytes(path));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Theory]
    [InlineData("binding-zero")]
    [InlineData("decision-zero")]
    [InlineData("both-snapshots-future")]
    public void LocalWaitParkingStop_RejectsNonpositiveOrFutureBindingSnapshotsAndPreservesBytes(string invalidRevision)
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var decision = run.LocalWaitDecision!;
        var binding = decision.Binding!;
        switch (invalidRevision)
        {
            case "binding-zero":
                binding = binding with { RecordRevision = 0 };
                decision = decision with { Binding = binding };
                break;
            case "decision-zero":
                binding = binding with { RecordRevision = 0 };
                decision = decision with
                {
                    Binding = binding,
                    Context = decision.Context! with { RecordRevision = 0 },
                };
                break;
            case "both-snapshots-future":
                var futureRevision = run.RecordRevision + 3;
                binding = binding with { RecordRevision = futureRevision };
                decision = decision with
                {
                    Binding = binding,
                    Context = decision.Context! with { RecordRevision = futureRevision },
                };
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(invalidRevision), invalidRevision, null);
        }
        run.LocalWaitDecision = decision;
        host.Runs.Update(run);
        host.LocalWaitQueue.Upsert(binding.ToQueueItem());

        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public void LocalWaitParkingStop_QueuePublishFailurePreservesBytesAndCanRetry()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        LocalWaitQueueStore.BeforeTemporaryFileWriteProbe = (tmp, _) =>
        {
            if (string.Equals(Path.GetDirectoryName(tmp), _runsDir, StringComparison.OrdinalIgnoreCase))
                throw new IOException("SB21-4 injected queue publish failure");
        };

        HostActionResult result;
        try { result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop); }
        finally { LocalWaitQueueStore.BeforeTemporaryFileWriteProbe = null; }

        Assert.Equal(HostActionStatus.Unavailable, result.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(host.LocalWaitQueue.Load()).State);

        var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
        Assert.Equal(HostActionStatus.Effective, retry.Status);
        Assert.Equal(WorkflowRunState.Cancelled, new RunStore(_runsDir).Load(run.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
    }

    [Fact]
    public void LocalWaitParkingStop_RunPublishFailureKeepsQueueTombstoneAndRetryDoesNotRewriteIt()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytes = File.ReadAllBytes(runPath);
        host.Runs.PublishFaultForTest = updated => updated.State == WorkflowRunState.Cancelled
            ? new IOException("SB21-4 injected run publish failure") : null;

        var failed = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Unavailable, failed.Status);
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
        Assert.Equal(LocalWaitItemState.Cancelled, Assert.Single(host.LocalWaitQueue.Load()).State);
        var tombstoneBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        host.Runs.PublishFaultForTest = null;

        var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

        Assert.Equal(HostActionStatus.Effective, retry.Status);
        Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
        Assert.Equal(tombstoneBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public async Task LocalWaitParkingStop_DuringResumeReservationIsUnavailable()
    {
        var workflowId = SeedWorkflow();
        using var enteredFactory = new ManualResetEventSlim();
        using var releaseFactory = new ManualResetEventSlim();
        var host = MakeWaitParkingHost(runnerFactoryEntered: call =>
        {
            if (call != 2) return;
            enteredFactory.Set();
            if (!releaseFactory.Wait(TimeSpan.FromSeconds(10)))
                throw new TimeoutException("resume factory barrier timed out");
        });
        var started = await host.StartWorkflowAsync(workflowId);
        Assert.Equal(HostActionStatus.Registered, started.Status);
        Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(5)));
        var run = Assert.Single(host.Runs.List());

        var resumeTask = Task.Run(() => host.ResumeRunAsync(run.RunId));
        Assert.True(enteredFactory.Wait(TimeSpan.FromSeconds(5)), "resume runner factory did not reach reserved window");
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var runBytesWhileReserved = File.ReadAllBytes(runPath);
        var queueBytesWhileReserved = File.ReadAllBytes(host.LocalWaitQueue.FilePath);
        var stop = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
        Assert.Equal(runBytesWhileReserved, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytesWhileReserved, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
        releaseFactory.Set();
        var resume = await resumeTask;

        Assert.Equal(HostActionStatus.Unavailable, stop.Status);
        Assert.Contains("启动或驱动", stop.Message);
        Assert.Equal(HostActionStatus.Registered, resume.Status);
        await host.ShutdownAsync();
    }

    [Fact]
    public void LocalWaitParkingStop_RunReadFailureReturnsStructuredUnavailableAndPreservesBytes()
    {
        var workflowId = SeedWorkflow();
        var host = MakeHost();
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Wait, createQueueItem: true);
        var runPath = Path.Combine(_runsDir, run.RunId + ".run.json");
        var corruptBytes = System.Text.Encoding.UTF8.GetBytes("{ corrupted-run-record");
        File.WriteAllBytes(runPath, corruptBytes);
        var queueBytes = File.ReadAllBytes(host.LocalWaitQueue.FilePath);

        HostActionResult? action = null;
        var exception = Record.Exception(() => action = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop));

        Assert.Null(exception);
        Assert.Equal(HostActionStatus.Unavailable, action!.Status);
        Assert.Equal(corruptBytes, File.ReadAllBytes(runPath));
        Assert.Equal(queueBytes, File.ReadAllBytes(host.LocalWaitQueue.FilePath));
    }

    [Fact]
    public async Task LocalWaitParkingStop_TerminalizesAdmissionWiredAcceptedRegistration()
    {
        var workflowId = SeedWorkflow();
        var host = MakeWaitParkingHost(admissionWired: true);
        try
        {
            var start = await host.StartWorkflowAsync(workflowId);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(workflowId), TimeSpan.FromSeconds(10)));
            var run = Assert.Single(host.Runs.List());
            Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
            var before = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read();
            Assert.Contains(before.File!.Handoff!.Operations!, op => op.RunBinding == run.RunId
                && op.RequestState == OperationRequestState.Accepted);

            var stop = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Effective, stop.Status);
            Assert.True(SpinWait.SpinUntil(() =>
            {
                try
                {
                    return new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read()
                        .File?.Handoff?.Operations?.Any(op => op.RunBinding == run.RunId
                            && op.RequestState == OperationRequestState.TerminalCompleted) == true;
                }
                catch (IOException) { return false; }
            }, TimeSpan.FromSeconds(5)), "accepted admission registration did not reach terminal state");
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_OverlappingRetriesWriteOneTerminalTransitionAndIsolateOtherRun()
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        try
        {
            var otherWorkflowId = SeedWorkflow();
            var otherStart = await host.StartWorkflowAsync(otherWorkflowId);
            Assert.Equal(HostActionStatus.Registered, otherStart.Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(otherWorkflowId), TimeSpan.FromSeconds(10)));
            var otherRun = Assert.Single(host.Runs.List().Where(candidate => candidate.WorkflowId == otherWorkflowId));

            var admissionPath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
            var store = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration"));
            var before = store.Read().File!;
            var target = Assert.Single(before.Handoff!.Operations!.Where(op => op.RunBinding == run.RunId));
            Assert.Equal(OperationRequestState.Accepted, target.RequestState);
            var unrelatedBefore = JsonSerializer.SerializeToUtf8Bytes(
                Assert.Single(before.Handoff.Operations, op => op.RunBinding == otherRun.RunId));

            host.AdmissionTerminalWriteFaultForTest = (_, _) => new IOException("seed pending admission retry");
            var initialStop = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Unavailable, initialStop.Status);
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
            Assert.Equal(OperationRequestState.Accepted,
                Assert.Single(ReadAdmissionOperationsForRun(run.RunId)).RequestState);
            host.AdmissionTerminalWriteFaultForTest = null;
            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromMilliseconds(50);

            var arrivalCount = 0;
            var reconciliationCompletionCount = 0;
            using var bothReconciliationsCompleted = new ManualResetEventSlim();
            host.AdmissionTerminalReconciliationCompletedForTest = () =>
            {
                if (Interlocked.Increment(ref reconciliationCompletionCount) >= 2)
                    bothReconciliationsCompleted.Set();
            };
            var terminalIdentityCalls = new System.Collections.Concurrent.ConcurrentQueue<string>();
            using var bothAtTerminalWrite = new Barrier(2);
            host.AdmissionTerminalResultForTest = identity =>
            {
                terminalIdentityCalls.Enqueue(identity);
                Interlocked.Increment(ref arrivalCount);
                if (!bothAtTerminalWrite.SignalAndWait(TimeSpan.FromSeconds(10)))
                    throw new TimeoutException("overlapping Stop calls did not reach the same terminal-write boundary");
                return null;
            };

            var first = Task.Run(() => host.RequestRunAction(run.RunId, WorkflowRunAction.Stop));
            var timedOut = await first;
            Assert.Equal(HostActionStatus.Unavailable, timedOut.Status);
            Assert.Contains("再次执行 Stop 重试", timedOut.Message);

            var retry = await Task.Run(() => host.RequestRunAction(run.RunId, WorkflowRunAction.Stop));

            Assert.Equal(HostActionStatus.Effective, retry.Status);
            Assert.True(bothReconciliationsCompleted.Wait(TimeSpan.FromSeconds(5)),
                "both the timed-out first worker and the retry reconciliation must exit before inspecting final bytes");
            host.AdmissionTerminalReconciliationCompletedForTest = null;
            Assert.True(reconciliationCompletionCount >= 2);
            var after = store.Read().File!;
            var terminal = Assert.Single(after.Handoff!.Operations!, op => op.RunBinding == run.RunId);
            Assert.Equal(OperationRequestState.TerminalCompleted, terminal.RequestState);
            Assert.Equal(unrelatedBefore, JsonSerializer.SerializeToUtf8Bytes(
                Assert.Single(after.Handoff.Operations, op => op.RunBinding == otherRun.RunId)));
            Assert.All(terminalIdentityCalls, identity => Assert.Equal(target.RequestIdentity, identity));
            Assert.Equal(before.Revision + 1, after.Revision);
            Assert.Equal(after.Revision, terminal.UpdatedRevision);
            Assert.Equal(2, arrivalCount);

            var completedLeaseBytes = File.ReadAllBytes(admissionPath);
            var repeated = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Effective, repeated.Status);
            Assert.Equal(completedLeaseBytes, File.ReadAllBytes(admissionPath));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_ReadTimeoutIsVisibleAndSameSessionStopRetriesToTerminal()
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        try
        {
            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromMilliseconds(60);
            host.AdmissionTerminalReadFaultForTest = _ => new IOException("injected admission read outage");

            var first = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Unavailable, first.Status);
            Assert.Contains("再次执行 Stop 重试", first.Message);
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
            Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.Accepted);

            host.AdmissionTerminalReadFaultForTest = null;
            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromSeconds(2);
            var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, retry.Status);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
            var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
            var terminalBytes = File.ReadAllBytes(leasePath);
            var repeated = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Effective, repeated.Status);
            Assert.Equal(terminalBytes, File.ReadAllBytes(leasePath));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_LogicalAdmissionRejectionRemainsVisibleAndCanRetry()
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        try
        {
            host.AdmissionTerminalResultForTest = identity => AdmissionResult.Of(
                AdmissionResultKind.Error, "injected_logical_rejection", "terminal write refused", identity);

            var rejected = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Unavailable, rejected.Status);
            Assert.Contains("再次执行 Stop 重试", rejected.Message);
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
            Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.Accepted);

            host.AdmissionTerminalResultForTest = null;
            var retried = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, retried.Status);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_WriteExhaustionProcessesSiblingAndRetryDoesNotRewriteTerminalSibling()
    {
        var loggerFailures = 0;
        var (host, _, run) = await StartAdmissionWiredParkedRun(message =>
        {
            if (!message.Contains("终局回写被拒", StringComparison.Ordinal)) return;
            Interlocked.Increment(ref loggerFailures);
            throw new InvalidOperationException("injected terminal reconciliation logger failure");
        });
        try
        {
            // Recovery admission creates a second legitimate operation bound to this same parked run.
            var resumed = await host.ResumeRunAsync(run.RunId);
            Assert.Equal(HostActionStatus.Registered, resumed.Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(run.WorkflowId), TimeSpan.FromSeconds(10)),
                "resume fixture must settle back into LocalWaitParking before explicit Stop begins");
            Assert.Equal(WorkflowRunState.LocalWaitParking, host.Runs.Load(run.RunId)!.State);
            var accepted = ReadAdmissionOperationsForRun(run.RunId)
                .Where(op => op.RequestState == OperationRequestState.Accepted).OrderBy(op => op.RequestIdentity).ToList();
            Assert.True(accepted.Count >= 2, "the fixture must expose multiple Accepted registrations for one run");
            var failingIdentity = accepted[0].RequestIdentity;
            var writeAttempts = 0;
            host.AdmissionTerminalWriteFaultForTest = (identity, attempt) =>
            {
                if (!string.Equals(identity, failingIdentity, StringComparison.Ordinal)) return null;
                Interlocked.Increment(ref writeAttempts);
                return new IOException("injected sibling terminal write outage " + attempt);
            };

            var first = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Unavailable, first.Status);
            Assert.Equal(5, writeAttempts);
            Assert.Equal(1, loggerFailures);
            var afterPartial = ReadAdmissionOperationsForRun(run.RunId);
            var terminalSibling = Assert.Single(afterPartial.Where(op => op.RequestIdentity != failingIdentity));
            Assert.Equal(OperationRequestState.TerminalCompleted, terminalSibling.RequestState);
            Assert.Equal(OperationRequestState.Accepted, Assert.Single(afterPartial, op => op.RequestIdentity == failingIdentity).RequestState);
            var terminalRevision = terminalSibling.UpdatedRevision;
            var terminalUpdatedAt = terminalSibling.UpdatedAtUtc;

            host.AdmissionTerminalWriteFaultForTest = null;
            var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, retry.Status);
            var afterRetry = ReadAdmissionOperationsForRun(run.RunId);
            Assert.All(afterRetry, op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
            var unchangedSibling = Assert.Single(afterRetry, op => op.RequestIdentity != failingIdentity);
            Assert.Equal(terminalRevision, unchangedSibling.UpdatedRevision);
            Assert.Equal(terminalUpdatedAt, unchangedSibling.UpdatedAtUtc);
            var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
            var completedBytes = File.ReadAllBytes(leasePath);

            var repeated = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
            Assert.Equal(HostActionStatus.Effective, repeated.Status);
            Assert.Equal(completedBytes, File.ReadAllBytes(leasePath));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_PendingAdmissionTerminalizationCanRecoverAfterHostRestart()
    {
        var (firstHost, _, run) = await StartAdmissionWiredParkedRun();
        firstHost.AdmissionTerminalWriteFaultForTest = (_, _) => new IOException("injected persistent write outage");
        var first = firstHost.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
        Assert.Equal(HostActionStatus.Unavailable, first.Status);
        Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.Accepted);
        await firstHost.ShutdownAsync();

        var restarted = MakeWaitParkingHost(admissionWired: true);
        try
        {
            var recovered = restarted.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, recovered.Status);
            Assert.Equal(WorkflowRunState.Cancelled, restarted.Runs.Load(run.RunId)!.State);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
        }
        finally { await restarted.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_FinalAdmissionReadFailureIsVisibleAndRetryCompletes()
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        try
        {
            host.AdmissionTerminalReadFaultForTest = attempt => attempt == 2
                ? new IOException("injected post-write confirmation failure") : null;

            var first = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Unavailable, first.Status);
            Assert.Contains(ReadAdmissionOperationsForRun(run.RunId), op => op.RequestState == OperationRequestState.TerminalCompleted);
            host.AdmissionTerminalReadFaultForTest = null;

            var retry = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, retry.Status);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("corrupt")]
    [InlineData("unsupported")]
    public async Task LocalWaitParkingStop_UnreadableAdmissionStateIsNotTreatedAsNoMapping(string failureKind)
    {
        var (host, _, run) = await StartAdmissionWiredParkedRun();
        var leasePath = Path.Combine(_root, "arbitration", "arbitration-lease.json");
        var originalLeaseBytes = File.ReadAllBytes(leasePath);
        byte[] unavailableLeaseBytes;
        if (failureKind == "unsupported")
        {
            var leaseDocument = JsonNode.Parse(originalLeaseBytes)!.AsObject();
            leaseDocument["version"] = ArbitrationLeaseStore.SupportedVersion + 1;
            unavailableLeaseBytes = JsonSerializer.SerializeToUtf8Bytes(leaseDocument);
        }
        else
        {
            unavailableLeaseBytes = System.Text.Encoding.UTF8.GetBytes("{ malformed admission bytes");
        }

        try
        {
            File.WriteAllBytes(leasePath, unavailableLeaseBytes);
            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromMilliseconds(60);

            var blocked = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Unavailable, blocked.Status);
            Assert.Contains("再次执行 Stop 重试", blocked.Message);
            Assert.Equal(unavailableLeaseBytes, File.ReadAllBytes(leasePath));
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);

            File.WriteAllBytes(leasePath, originalLeaseBytes);
            host.AdmissionTerminalReconciliationTimeoutForTest = TimeSpan.FromSeconds(2);
            var retried = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, retried.Status);
            Assert.All(ReadAdmissionOperationsForRun(run.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task PersistentHoldStopWithAdmissionWiredConfirmsNoMappingAndReachesCancelled()
    {
        var workflowId = SeedWorkflow();
        var host = MakeWaitParkingHost(admissionWired: true);
        var run = SeedParkedRun(host, workflowId, LocalWaitDecisionKind.Hold, createQueueItem: false);

        try
        {
            var result = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, result.Status);
            Assert.Equal(WorkflowRunState.Cancelled, host.Runs.Load(run.RunId)!.State);
            Assert.Empty(ReadAdmissionOperationsForRun(run.RunId));
            Assert.Empty(host.LocalWaitQueue.Load());
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task LocalWaitParkingStop_TerminalizesOnlyTheRegistrationBoundToItsRun()
    {
        var firstWorkflowId = SeedWorkflow();
        var host = MakeWaitParkingHost(admissionWired: true);
        try
        {
            var firstStart = await host.StartWorkflowAsync(firstWorkflowId);
            Assert.Equal(HostActionStatus.Registered, firstStart.Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(firstWorkflowId), TimeSpan.FromSeconds(10)));
            var firstRun = Assert.Single(host.Runs.List().Where(run => run.WorkflowId == firstWorkflowId));
            var secondWorkflowId = SeedWorkflow();
            var secondStart = await host.StartWorkflowAsync(secondWorkflowId);
            Assert.Equal(HostActionStatus.Registered, secondStart.Status);
            Assert.True(SpinWait.SpinUntil(() => !host.IsDriving(secondWorkflowId), TimeSpan.FromSeconds(10)));
            var secondRun = Assert.Single(host.Runs.List().Where(run => run.WorkflowId == secondWorkflowId));
            var secondOperationBefore = Assert.Single(ReadAdmissionOperationsForRun(secondRun.RunId));
            Assert.Equal(OperationRequestState.Accepted, secondOperationBefore.RequestState);
            var secondOperationBytes = JsonSerializer.SerializeToUtf8Bytes(secondOperationBefore);
            var terminalWriteAttempts = new System.Collections.Concurrent.ConcurrentQueue<string>();
            host.AdmissionTerminalResultForTest = identity =>
            {
                terminalWriteAttempts.Enqueue(identity);
                return null;
            };

            var stopped = host.RequestRunAction(firstRun.RunId, WorkflowRunAction.Stop);

            Assert.Equal(HostActionStatus.Effective, stopped.Status);
            Assert.All(ReadAdmissionOperationsForRun(firstRun.RunId), op => Assert.Equal(OperationRequestState.TerminalCompleted, op.RequestState));
            Assert.Equal(ReadAdmissionOperationsForRun(firstRun.RunId).Select(op => op.RequestIdentity), terminalWriteAttempts);
            var secondOperationAfter = Assert.Single(ReadAdmissionOperationsForRun(secondRun.RunId));
            Assert.Equal(secondOperationBytes, JsonSerializer.SerializeToUtf8Bytes(secondOperationAfter));
        }
        finally { await host.ShutdownAsync(); }
    }
}
