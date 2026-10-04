using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// SB21-3 / BO-4: facade wait results must retain their wait state through the actual consumers.
/// This is component/runtime evidence only; production wiring and BGI execution stay closed.
/// </summary>
public sealed class FacadeWaitLocallyConsumerTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "sb21-3-bo4-" + Guid.NewGuid().ToString("N"));

    public FacadeWaitLocallyConsumerTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    [Fact]
    public async Task FacadeWaitLocally_IsConsumedAsPersistedParkingForSameOccurrence_WithoutSend()
    {
        var flowsDir = Path.Combine(_root, "flows");
        var runsDir = Path.Combine(_root, "runs");
        var queuePath = Path.Combine(_root, "local-waits.json");
        var workflows = new WorkflowStore(flowsDir);
        var runs = new RunStore(runsDir);
        var panelRequestIdentity = Guid.NewGuid().ToString("N");
        var flow = new WorkflowDocument
        {
            Name = "BO-4 facade wait",
            Nodes = new List<WorkflowNode>
            {
                new()
                {
                    NodeId = "n1",
                    Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "c-n1", ConfigKey = "c-n1#k", Revision = "rev-1" },
                },
                new()
                {
                    NodeId = "n2",
                    Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "c-n2", ConfigKey = "c-n2#k", Revision = "rev-1" },
                },
            },
        };
        flow.Activation = new WorkflowActivation { Status = "active" };
        workflows.Save(flow, null);

        var sourceRequests = new List<WaitDecisionRequest>();
        var sourceDecisions = new List<LocalWaitDecisionRecord>();
        var decisionSource = new WaitDecisionSource(request =>
        {
            sourceRequests.Add(request);
            var kind = sourceRequests.Count == 1
                ? LocalWaitDecisionKind.ContinueAdmission
                : LocalWaitDecisionKind.Wait;
            // Feed the same registered panel RequestIdentity through the production parent resolver,
            // then into the runner test seam. Admission itself remains injected test-only.
            var panelParent = ResolvePanelFlowParent(panelRequestIdentity, request);
            Assert.Equal(panelRequestIdentity, panelParent.RequestIdentity);
            Assert.Equal("bgi:local:e1", panelParent.Scope);
            var decision = DecisionFor(request, kind, panelParent.RequestIdentity);
            sourceDecisions.Add(decision);
            return decision;
        });
        var inner = new NeverSendBoundary();
        BoundarySubmitResult? facadeMapped = null;
        WorkflowSubmitRequest? facadeRequest = null;
        var boundary = new ArbitrationWorkflowExecutionBoundary(inner, (request, _) =>
        {
            facadeRequest = request;
            facadeMapped = TaskCenterHost.MapAdmissionResultToBoundary(new AdmissionResult
            {
                Kind = AdmissionResultKind.WaitLocally,
                ReasonCode = "local_wait",
                Detail = "facade returned a determined local wait",
            });
            return Task.FromResult(facadeMapped);
        });
        var queue = new LocalWaitQueueStore(queuePath);
        var runner = new WorkflowRunner(workflows, runs, boundary, new NoopPrerequisite(), new NoopTerminal(),
            new WorkflowRunnerOptions(),
            localWaitQueue: queue,
            localWaitPrerequisiteReferenceProvider: (_, _) => "wf-x/n1/0/0@ticket-bo4",
            localWaitAdmissionScopeProvider: _ => "bgi:local:e1",
            waitDecisionSource: decisionSource);

        var run = await runner.StartAsync(flow.WorkflowId!);

        Assert.Equal(BoundarySubmitKind.Wait, facadeMapped?.Kind);
        Assert.NotNull(facadeRequest);
        Assert.Equal("n1", facadeRequest!.Occurrence.NodeId);
        Assert.Equal(0, facadeRequest.Occurrence.Occurrence);
        Assert.Equal(0, facadeRequest.Occurrence.LoopIteration);
        Assert.Equal(2, sourceRequests.Count); // Continue before admission; identify the facade wait afterward.
        Assert.All(sourceRequests, request =>
        {
            Assert.Equal(run.RunId, request.RunId);
            Assert.Equal(flow.WorkflowId, request.WorkflowId);
            Assert.Equal(run.WorkflowRevision, request.WorkflowRevision);

            Assert.Equal("n1", request.CursorNodeId);
            Assert.Equal(0, request.CursorOccurrence);
            Assert.Equal(0, request.CursorLoopIteration);
            Assert.Equal(0, request.SequenceIndex);
            Assert.Equal("n1", request.NodeId);
            Assert.Equal(0, request.Occurrence);
            Assert.Equal(0, request.LoopIteration);
            Assert.Equal(1, request.Attempt);
        });

        Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
        Assert.Equal("n1", run.Cursor?.NodeId);
        Assert.Equal(0, run.Cursor?.Occurrence);
        Assert.Equal(0, run.Cursor?.LoopIteration);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, run.CurrentSubmission?.Intent);
        Assert.False(run.CurrentSubmission?.SendAttempted ?? true);
        Assert.Null(run.CurrentSubmission?.JobId);
        Assert.Null(run.CurrentSubmission?.AcceptedSendIdentity);
        Assert.Null(run.CurrentSubmission?.ObservedTerminal);
        Assert.Equal(0, inner.SubmitCalls);
        Assert.Equal(0, inner.AwaitCalls);

        var binding = Assert.IsType<LocalWaitBinding>(run.LocalWaitDecision?.Binding);
        Assert.Equal(run.RunId, binding.RunId);
        Assert.Equal(run.WorkflowRevision, binding.WorkflowRevision);
        Assert.Equal(sourceDecisions[1].Context.SourceKind, binding.SourceKind);
        Assert.Equal(sourceDecisions[1].Context.SourceIdentity, binding.SourceIdentity);
        Assert.Equal(panelRequestIdentity, sourceDecisions[1].Context.SourceIdentity);
        Assert.Equal(LocalWaitSourceKind.PanelFlowRegistration, binding.SourceKind);
        Assert.Equal(panelRequestIdentity, binding.SourceIdentity);
        Assert.NotEqual(run.RunId, binding.SourceIdentity);

        Assert.Equal("n1", binding.CursorNodeId);
        Assert.Equal(0, binding.CursorOccurrence);
        Assert.Equal(0, binding.CursorLoopIteration);
        Assert.Equal(0, binding.SequenceIndex);
        Assert.Equal($"{run.RunId}|n1|0|0", binding.StableIdentity);
        Assert.DoesNotContain("facade returned", binding.StableIdentity, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(flow.WorkflowId, binding.WorkflowId);
        Assert.Equal("n1", binding.NodeId);
        Assert.Equal(0, binding.Occurrence);
        Assert.Equal(0, binding.LoopIteration);
        Assert.Equal(1, binding.Attempt);
        Assert.Equal("bgi:local:e1", binding.Scope);
        Assert.Equal(sourceRequests[1].RecordRevision, binding.RecordRevision);

        Assert.NotEqual(sourceRequests[0].RecordRevision, sourceRequests[1].RecordRevision);

        var reloadedRun = new RunStore(runsDir).Load(run.RunId!);
        Assert.Equal(WorkflowRunState.LocalWaitParking, reloadedRun?.State);
        Assert.Equal("n1", reloadedRun?.Cursor?.NodeId);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, reloadedRun?.CurrentSubmission?.Intent);
        var commitBoundaryDecision = Assert.IsType<LocalWaitDecisionRecord>(run.LocalWaitDecision);
        var persistedDecision = Assert.IsType<LocalWaitDecisionRecord>(reloadedRun?.LocalWaitDecision);
        Assert.Equal(commitBoundaryDecision.Kind, persistedDecision.Kind);
        Assert.Equal(commitBoundaryDecision.Reason, persistedDecision.Reason);
        Assert.Equal(commitBoundaryDecision.NoSendConfirmed, persistedDecision.NoSendConfirmed);
        AssertDecisionContextFieldsEqual(commitBoundaryDecision.Context, persistedDecision.Context);
        Assert.Equal(LocalWaitSourceKind.PanelFlowRegistration, persistedDecision.Context.SourceKind);
        Assert.Equal(panelRequestIdentity, persistedDecision.Context.SourceIdentity);
        var persistedBinding = Assert.IsType<LocalWaitBinding>(persistedDecision.Binding);
        AssertBindingFieldsEqual(binding, persistedBinding);
        Assert.Equal(LocalWaitSourceKind.PanelFlowRegistration, persistedBinding.SourceKind);
        Assert.Equal(panelRequestIdentity, persistedBinding.SourceIdentity);
        var persistedItem = Assert.Single(new LocalWaitQueueStore(queuePath).Load());
        Assert.Equal(persistedBinding.ItemId, persistedItem.ItemId);
        Assert.Equal(persistedBinding.StableIdentity, persistedItem.StableIdentity);
        Assert.Equal(LocalWaitItemState.Waiting, persistedItem.State);
        Assert.False(persistedItem.HasTrustedIdentity);
        // LocalWaitItem is a scheduling mirror; the complete source binding is owned by RunStore.
    }

    [Fact]
    public async Task FacadeWaitLocally_WithStaleCursorSnapshot_ParksWithoutQueueBindingOrSend()
    {
        var flowsDir = Path.Combine(_root, "stale-flows");
        var runsDir = Path.Combine(_root, "stale-runs");
        var queuePath = Path.Combine(_root, "stale-local-waits.json");
        var workflows = new WorkflowStore(flowsDir);
        var runs = new RunStore(runsDir);
        var panelRequestIdentity = Guid.NewGuid().ToString("N");
        var flow = new WorkflowDocument
        {
            Name = "BO-4 stale facade wait",
            Nodes = new List<WorkflowNode>
            {
                new()
                {
                    NodeId = "n1",
                    Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "c-n1", ConfigKey = "c-n1#k", Revision = "rev-1" },
                },
                new()
                {
                    NodeId = "n2",
                    Kind = "resource.oneDragonConfig",
                    Ref = new WorkflowResourceRef { Config = "c-n2", ConfigKey = "c-n2#k", Revision = "rev-1" },
                },
            },
        };
        flow.Activation = new WorkflowActivation { Status = "active" };
        workflows.Save(flow, null);

        var sourceRequests = new List<WaitDecisionRequest>();
        var sourceDecisions = new List<LocalWaitDecisionRecord>();
        var decisionSource = new WaitDecisionSource(request =>
        {
            sourceRequests.Add(request);
            var kind = sourceRequests.Count == 1
                ? LocalWaitDecisionKind.ContinueAdmission
                : LocalWaitDecisionKind.Wait;
            var panelParent = ResolvePanelFlowParent(panelRequestIdentity, request);
            var decision = DecisionFor(request, kind, panelParent.RequestIdentity);
            if (kind == LocalWaitDecisionKind.Wait)
            {
                decision = decision with
                {
                    Context = decision.Context with { CursorNodeId = "stale-n1" },
                };
            }
            sourceDecisions.Add(decision);
            return decision;
        });
        var inner = new NeverSendBoundary();
        BoundarySubmitResult? facadeMapped = null;
        var boundary = new ArbitrationWorkflowExecutionBoundary(inner, (_, _) =>
        {
            facadeMapped = TaskCenterHost.MapAdmissionResultToBoundary(new AdmissionResult
            {
                Kind = AdmissionResultKind.WaitLocally,
                ReasonCode = "local_wait",
                Detail = "facade returned a determined local wait",
            });
            return Task.FromResult(facadeMapped);
        });
        var queue = new LocalWaitQueueStore(queuePath);
        var runner = new WorkflowRunner(workflows, runs, boundary, new NoopPrerequisite(), new NoopTerminal(),
            new WorkflowRunnerOptions(),
            localWaitQueue: queue,
            localWaitPrerequisiteReferenceProvider: (_, _) => "wf-x/n1/0/0@ticket-bo4",
            localWaitAdmissionScopeProvider: _ => "bgi:local:e1",
            waitDecisionSource: decisionSource);

        var run = await runner.StartAsync(flow.WorkflowId!);

        Assert.Equal(BoundarySubmitKind.Wait, facadeMapped?.Kind);
        Assert.Equal(2, sourceRequests.Count);
        Assert.Equal("n1", sourceRequests[1].CursorNodeId);
        Assert.Equal("stale-n1", sourceDecisions[1].Context.CursorNodeId);
        Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
        Assert.Equal("n1", run.Cursor?.NodeId);
        Assert.Equal(SubmitIntentState.LocalWaitDeferred, run.CurrentSubmission?.Intent);
        Assert.False(run.CurrentSubmission?.SendAttempted ?? true);
        Assert.Null(run.CurrentSubmission?.JobId);
        Assert.Null(run.CurrentSubmission?.AcceptedSendIdentity);
        Assert.Null(run.CurrentSubmission?.ObservedTerminal);
        Assert.Equal(0, inner.SubmitCalls);
        Assert.Equal(0, inner.AwaitCalls);

        Assert.Equal(LocalWaitDecisionKind.Hold, run.LocalWaitDecision?.Kind);
        Assert.Null(run.LocalWaitDecision?.Binding);
        Assert.Contains("身份与当前运行/游标不一致", run.LocalWaitDecision?.Reason, StringComparison.Ordinal);
        Assert.Empty(queue.Load());

        var reloadedRun = new RunStore(runsDir).Load(run.RunId!);
        Assert.Equal(WorkflowRunState.LocalWaitParking, reloadedRun?.State);
        Assert.Equal("n1", reloadedRun?.Cursor?.NodeId);
        Assert.Equal(LocalWaitDecisionKind.Hold, reloadedRun?.LocalWaitDecision?.Kind);
        Assert.Null(reloadedRun?.LocalWaitDecision?.Binding);
        Assert.Empty(new LocalWaitQueueStore(queuePath).Load());
    }

    [Fact]
    public async Task CommandExecutor_RecognizesFacadeWaitAsNonterminalLocalWaitWithoutJob()
    {
        var mappedStatus = TaskCenterHost.MapAdmissionResultToExternalStartStatus(new AdmissionResult
        {
            Kind = AdmissionResultKind.WaitLocally,
            ReasonCode = "local_wait",
            Detail = "facade returned a determined local wait",
        });
        var executor = new CommandExecutor(null!, "unused", externalStartAdmission: (_, _) =>
            Task.FromResult(new ExternalStartAdmissionOutcome(mappedStatus, "local_wait", "已登记本地持久等待")));

        var result = await executor.StartSpecifiedTaskViaAdmissionAsync(new TaskConflictPolicySettings
        {
            SpecifiedTaskType = "group",
            SpecifiedTaskName = "BO-4 test group",
            SpecifiedTaskPriority = 1,
        });

        Assert.Equal("failed", result.Status); // Existing wire status; local_wait remains the discriminating code.
        Assert.Equal("local_wait", result.ErrorCode);
        Assert.Contains("已登记本地持久等待", result.Message);
        Assert.False(result.IsTerminal);
        Assert.Null(result.JobId);
        Assert.Equal(ExecutionDisposition.None, result.ExecutionDisposition);
    }

    private static AdmissionParentSource ResolvePanelFlowParent(
        string requestIdentity, WaitDecisionRequest request)
    {
        var parent = new OperationRecord
        {
            RequestIdentity = requestIdentity,
            CandidateId = "candidate-" + requestIdentity,
            Candidate = new ArbitrationCandidate
            {
                Scope = "bgi:local:e1",
                WorkflowId = request.WorkflowId,
                NodeId = "",
            },
            RunBinding = request.RunId,
            RequestState = OperationRequestState.Queued,
            OperationType = OperationType.FlowRegistration,
            Intent = "start",
            ResourceRef = "flow:" + request.WorkflowId,
        };
        return TaskCenterHost.ResolveAdmissionParent([parent], null, request.RunId, request.WorkflowId)
            ?? throw new InvalidOperationException("Registered test panel parent was not resolved.");
    }

    private static LocalWaitDecisionRecord DecisionFor(
        WaitDecisionRequest request, LocalWaitDecisionKind kind, string sourceIdentity)
        => new()
        {
            Kind = kind,
            Context = new LocalWaitDecisionContext
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
                Scope = "bgi:local:e1",
                Tier = ArbitrationTier.Plan,
                Priority = 0,
                HasTrustedRankingFacts = true,
            },
            Reason = "BO-4 consumer wait decision",
            NoSendConfirmed = kind != LocalWaitDecisionKind.ContinueAdmission,
        };

    private static void AssertDecisionContextFieldsEqual(
        LocalWaitDecisionContext expected, LocalWaitDecisionContext actual)
    {
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.WorkflowId, actual.WorkflowId);
        Assert.Equal(expected.WorkflowRevision, actual.WorkflowRevision);
        Assert.Equal(expected.RecordRevision, actual.RecordRevision);
        Assert.Equal(expected.CursorNodeId, actual.CursorNodeId);
        Assert.Equal(expected.CursorOccurrence, actual.CursorOccurrence);
        Assert.Equal(expected.CursorLoopIteration, actual.CursorLoopIteration);
        Assert.Equal(expected.NodeId, actual.NodeId);
        Assert.Equal(expected.SequenceIndex, actual.SequenceIndex);
        Assert.Equal(expected.Occurrence, actual.Occurrence);
        Assert.Equal(expected.LoopIteration, actual.LoopIteration);
        Assert.Equal(expected.Attempt, actual.Attempt);
        Assert.Equal(expected.SourceKind, actual.SourceKind);
        Assert.Equal(expected.SourceIdentity, actual.SourceIdentity);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.CandidateId, actual.CandidateId);
        Assert.Equal(expected.AdmissionIdentity, actual.AdmissionIdentity);
        Assert.Equal(expected.Tier, actual.Tier);
        Assert.Equal(expected.Priority, actual.Priority);
        Assert.Equal(expected.IsHoeingHighest, actual.IsHoeingHighest);
        Assert.Equal(expected.HasTrustedRankingFacts, actual.HasTrustedRankingFacts);
    }

    private static void AssertBindingFieldsEqual(LocalWaitBinding expected, LocalWaitBinding actual)
    {
        Assert.Equal(expected.ItemId, actual.ItemId);
        Assert.Equal(expected.StableIdentity, actual.StableIdentity);
        Assert.Equal(expected.CandidateId, actual.CandidateId);
        Assert.Equal(expected.AdmissionIdentity, actual.AdmissionIdentity);
        Assert.Equal(expected.Namespace, actual.Namespace);
        Assert.Equal(expected.WorkflowId, actual.WorkflowId);
        Assert.Equal(expected.SourceKind, actual.SourceKind);
        Assert.Equal(expected.SourceIdentity, actual.SourceIdentity);
        Assert.Equal(expected.RunId, actual.RunId);
        Assert.Equal(expected.Scope, actual.Scope);
        Assert.Equal(expected.WorkflowRevision, actual.WorkflowRevision);
        Assert.Equal(expected.NodeId, actual.NodeId);
        Assert.Equal(expected.SequenceIndex, actual.SequenceIndex);
        Assert.Equal(expected.RecordRevision, actual.RecordRevision);
        Assert.Equal(expected.CursorNodeId, actual.CursorNodeId);
        Assert.Equal(expected.CursorOccurrence, actual.CursorOccurrence);
        Assert.Equal(expected.CursorLoopIteration, actual.CursorLoopIteration);
        Assert.Equal(expected.Occurrence, actual.Occurrence);
        Assert.Equal(expected.LoopIteration, actual.LoopIteration);
        Assert.Equal(expected.Attempt, actual.Attempt);
        Assert.Equal(expected.Tier, actual.Tier);
        Assert.Equal(expected.Priority, actual.Priority);
        Assert.Equal(expected.IsHoeingHighest, actual.IsHoeingHighest);
        Assert.Equal(expected.ScheduledAt, actual.ScheduledAt);
        Assert.Equal(expected.PrerequisiteReference, actual.PrerequisiteReference);
        Assert.Equal(expected.EnqueuedAtUtc, actual.EnqueuedAtUtc);
    }

    private sealed class NeverSendBoundary : IWorkflowExecutionBoundary
    {
        public int SubmitCalls { get; private set; }
        public int AwaitCalls { get; private set; }
        public bool SingleNativeSupported => false;

        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            SubmitCalls++;
            throw new InvalidOperationException("Wait must not fall through to the execution boundary.");
        }

        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            AwaitCalls++;
            return Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        }
    }

    private sealed class NoopPrerequisite : IWorkflowPrerequisiteAdapter
    {
        public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string> { "prerequisite.account", "prerequisite.redeem", "prerequisite.wait" };
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }

    private sealed class NoopTerminal : IWorkflowTerminalExecutor
    {
        public IReadOnlySet<string> SupportedKinds { get; } = new HashSet<string> { "terminal.notify" };
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed(null));
    }
}
