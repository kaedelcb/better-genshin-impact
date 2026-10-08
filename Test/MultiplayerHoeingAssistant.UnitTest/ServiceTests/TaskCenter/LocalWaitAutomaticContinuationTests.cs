using System.Collections.Concurrent;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

[Collection("TaskCenterHeavyE2E")]
public sealed class LocalWaitAutomaticContinuationTests
{
    private sealed class Fixture
    {
        public readonly string Root = Path.Combine(Path.GetTempPath(), "automatic-wait-" + Guid.NewGuid().ToString("N"));
        public readonly ConcurrentQueue<string> Sends = new();
        public bool Park = true;
        public TaskCenterAdmissionSeams Facts = new() { Epoch = "42:99", Occupied = false, FactsUnknown = false };
        public TaskCenterHost Host { get; private set; } = null!;
        public Fixture() => Host = MakeHost();
        public TaskCenterHost MakeHost()
        {
            TaskCenterHost? owner = null;
            owner = new TaskCenterHost(Path.Combine(Root, "flows"), Path.Combine(Root, "runs"), Path.Combine(Root, "catalog.json"),
                () => null, log: null, runnerFactory: (_, flows, runs) => new WorkflowRunner(flows, runs, new Boundary(Sends),
                    new Prerequisite(), new Terminal(), localWaitQueue: owner!.LocalWaitQueue,
                    localWaitAdmissionScopeProvider: run => owner.AdmissionParentForTest(run.RunId, run.WorkflowId)?.Scope,
                    localWaitPrerequisiteReferenceProvider: (run, occ) => "taskcenter-localwait/run/" + Uri.EscapeDataString(run.RunId)
                        + "/workflow/" + Uri.EscapeDataString(run.WorkflowId) + "/node/" + Uri.EscapeDataString(occ.NodeId)
                        + "/occurrence/" + occ.Occurrence + "/loop/" + occ.LoopIteration,
                    waitDecisionSource: new WaitDecisionSource(request =>
                    {
                        if (!Park) return new() { Kind = LocalWaitDecisionKind.ContinueAdmission, Reason = "fixture idle" };
                        Facts.FactsUnknown = true; // keep the host from automatically resuming before the test releases facts
                        var parent = owner.AdmissionParentForTest(request.RunId, request.WorkflowId)!.Value;
                        var candidate = TaskCenterHost.BuildSuccessorIdentityCandidate(parent.Scope, request.WorkflowId, request.RunId,
                            request.NodeId, request.Occurrence, request.LoopIteration, request.Attempt,
                            request.NodePriority, request.Tier, request.ScheduledAt);
                        var identity = LocalWaitIdentityTranslation.BuildAdmissionIdentity(candidate);
                        return new()
                        {
                            Kind = LocalWaitDecisionKind.Wait, NoSendConfirmed = true, Reason = "fixture accepted local wait",
                            Context = new()
                            {
                                RunId = request.RunId, WorkflowId = request.WorkflowId, WorkflowRevision = request.WorkflowRevision,
                                RecordRevision = request.RecordRevision, NodeId = request.NodeId, SequenceIndex = request.SequenceIndex,
                                CursorNodeId = request.CursorNodeId, CursorOccurrence = request.CursorOccurrence,
                                CursorLoopIteration = request.CursorLoopIteration, Occurrence = request.Occurrence,
                                LoopIteration = request.LoopIteration, Attempt = request.Attempt,
                                SourceKind = LocalWaitSourceKind.PanelFlowRegistration, SourceIdentity = parent.RequestIdentity,
                                Scope = parent.Scope, CandidateId = identity.CandidateId, AdmissionIdentity = identity.AdmissionIdentity,
                                Tier = request.Tier, Priority = request.NodePriority, HasTrustedRankingFacts = true,
                            },
                        };
                    })), readinessOverride: () => (true, null), admissionWired: true, admissionSeams: Facts,
                successorAdmissionWired: true);
            return owner;
        }
        public async Task<WorkflowRunRecord> ParkFlowAsync(string name, int priority = 0)
        {
            var doc = new WorkflowDocument
            {
                Name = name, Activation = new() { Status = "active" },
                Nodes = [new() { NodeId = "body", Kind = "resource.oneDragonConfig", Ref = new() { Config = name, Revision = "rev" },
                    Strategies = [new() { Kind = "schedule.priority", Params = new() { ["priority"] = JsonSerializer.SerializeToElement(priority) } }] }],
            };
            Host.Workflows.Save(doc, null);
            Facts.FactsUnknown = false;
            var started = await Host.StartWorkflowAsync(doc.WorkflowId!);
            Assert.Equal(HostActionStatus.Registered, started.Status);
            await WaitUntil(() => !Host.IsDriving(doc.WorkflowId!));
            var run = Assert.Single(Host.Runs.List().Where(r => r.WorkflowId == doc.WorkflowId));
            Assert.Equal(WorkflowRunState.LocalWaitParking, run.State);
            Assert.NotNull(run.LocalWaitDecision?.Binding);
            return run;
        }
    }
    private sealed class Boundary(ConcurrentQueue<string> sends) : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            TerminalReleaseFixtureFacts.FreezeBody(request);
            sends.Enqueue(request.Run.WorkflowId);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + request.Run.RunId));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        public Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }
    private sealed class Prerequisite : IWorkflowPrerequisiteAdapter
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct) => Task.FromResult(PrerequisiteResult.ProceedInstance);
    }
    private sealed class Terminal : IWorkflowTerminalExecutor
    {
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct) => Task.FromResult(TerminalExecutionResult.Executed(null));
    }
    private static async Task WaitUntil(Func<bool> condition)
    {
        for (var i = 0; i < 1000; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "automatic continuation did not converge");
    }

    [Fact]
    public async Task OccupancyEnd_SelectsHighestPriority_AndCompletesOriginalRunsOnce()
    {
        var fixture = new Fixture();
        try
        {
            var low = await fixture.ParkFlowAsync("low", 1);
            var high = await fixture.ParkFlowAsync("high", 9);
            Assert.Empty(fixture.Sends);
            fixture.Park = false;
            fixture.Facts.FactsUnknown = false;
            await fixture.Host.ReevaluateLocalWaitForTestAsync(LocalWaitReevaluationTriggerPoint.OccupancyEnded);
            await WaitUntil(() => fixture.Host.Runs.List().All(r => r.IsTerminal));
            Assert.Equal(new[] { high.WorkflowId, low.WorkflowId }, fixture.Sends);
            Assert.Equal(new[] { low.RunId, high.RunId }.OrderBy(x => x), fixture.Host.Runs.List().Select(r => r.RunId).OrderBy(x => x));
            Assert.All(fixture.Host.Runs.List(), r => Assert.Equal(WorkflowRunState.Succeeded, r.State));
            Assert.All(fixture.Host.LocalWaitQueue.Load(), i => Assert.Equal(LocalWaitItemState.Cancelled, i.State));
        }
        finally { await fixture.Host.ShutdownAsync(); }
    }

    [Fact]
    public async Task UnknownFactsAndQueuePayloadDrift_DoNotResume()
    {
        var fixture = new Fixture();
        try
        {
            var parked = await fixture.ParkFlowAsync("wait");
            fixture.Park = false;
            await fixture.Host.ReevaluateLocalWaitForTestAsync(LocalWaitReevaluationTriggerPoint.SafetyNet);
            Assert.Empty(fixture.Sends);
            var file = fixture.Host.LocalWaitQueue.FilePath;
            var queue = JsonNode.Parse(File.ReadAllText(file))!;
            queue["items"]![0]!["priority"] = 100;
            File.WriteAllText(file, queue.ToJsonString());
            fixture.Facts.FactsUnknown = false;
            await fixture.Host.ReevaluateLocalWaitForTestAsync(LocalWaitReevaluationTriggerPoint.OccupancyEnded);
            Assert.Empty(fixture.Sends);
            Assert.Equal(WorkflowRunState.LocalWaitParking, fixture.Host.Runs.Load(parked.RunId)!.State);
        }
        finally { await fixture.Host.ShutdownAsync(); }
    }

    [Fact]
    public async Task StartupRecovery_ContinuesAcceptedZeroSendWait_WithSameRunIdentity()
    {
        var fixture = new Fixture();
        var original = await fixture.ParkFlowAsync("restart");
        await fixture.Host.ShutdownAsync();
        fixture.Park = false;
        fixture.Facts.FactsUnknown = false;
        var cold = fixture.MakeHost();
        try
        {
            cold.EnsureRecovered();
            await WaitUntil(() => cold.Runs.Load(original.RunId)?.State == WorkflowRunState.Succeeded);
            Assert.Single(fixture.Sends);
            Assert.Equal(original.RunId, Assert.Single(cold.Runs.List()).RunId);
        }
        finally { await cold.ShutdownAsync(); }
    }

    [Fact]
    public async Task ManualPauseAndStop_AreNotAutomaticExecutionIntent()
    {
        var fixture = new Fixture();
        try
        {
            var paused = await fixture.ParkFlowAsync("paused");
            paused.State = WorkflowRunState.Paused;
            fixture.Host.Runs.Update(paused);
            fixture.Park = false;
            fixture.Facts.FactsUnknown = false;
            await fixture.Host.ReevaluateLocalWaitForTestAsync(LocalWaitReevaluationTriggerPoint.OccupancyEnded);
            Assert.Empty(fixture.Sends);
            Assert.Equal(WorkflowRunState.Paused, fixture.Host.Runs.Load(paused.RunId)!.State);
            var stop = await fixture.Host.RequestRunActionAsync(paused.RunId, WorkflowRunAction.Stop);
            Assert.True(stop.Ok, stop.Message);
            await fixture.Host.ReevaluateLocalWaitForTestAsync(LocalWaitReevaluationTriggerPoint.SafetyNet);
            Assert.Empty(fixture.Sends);
            Assert.True(fixture.Host.Runs.Load(paused.RunId)!.StopRequested);
        }
        finally { await fixture.Host.ShutdownAsync(); }
    }
}
