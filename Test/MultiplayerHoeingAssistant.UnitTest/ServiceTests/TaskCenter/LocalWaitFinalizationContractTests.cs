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

    private TaskCenterHost MakeWaitParkingHost(bool admissionWired = false, Action<int>? runnerFactoryEntered = null)
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
            () => null, null,
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
        bool driftQueuePayload = false)
    {
        var run = _runs.CreateRun(workflowId, "revision-sb21-4");
        const string nodeId = "n1";
        const int occurrence = 2;
        const int loopIteration = 1;
        const int attempt = 3;
        var stableIdentity = $"{run.RunId}|{nodeId}|{occurrence}|{loopIteration}";
        var itemId = LocalWaitQueuePolicy.DeriveItemId(stableIdentity);
        var queuedAt = DateTimeOffset.UtcNow;
        const string scope = "bgi:local:test-epoch";
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
            SourceKind = LocalWaitSourceKind.PanelFlowRegistration,
            SourceIdentity = "request-" + run.RunId,
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
            SourceKind = LocalWaitSourceKind.PanelFlowRegistration,
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
        Assert.NotNull(firstBinding);
        Assert.Equal(LocalWaitItemState.Waiting, Assert.Single(host.LocalWaitQueue.Load()).State);

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
        Assert.Equal(firstBinding!.ItemId, Assert.Single(host.LocalWaitQueue.Load()).ItemId);

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
        var stop = host.RequestRunAction(run.RunId, WorkflowRunAction.Stop);
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
}
