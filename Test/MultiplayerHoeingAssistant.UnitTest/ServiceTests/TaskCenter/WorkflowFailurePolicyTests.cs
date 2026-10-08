using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class WorkflowFailurePolicyTests
{
    private sealed class Boundary(params string[] terminals) : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public List<(string Node, int Attempt, string Key)> Sends { get; } = [];
        public Action? AfterFirstFailure { get; set; }
        public bool ExitConfirmed { get; set; } = true;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            TerminalReleaseFixtureFacts.FreezeBody(request);
            var sub = request.Run.CurrentSubmission!;
            Sends.Add((request.Node.NodeId, sub.Attempt, sub.Key));
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Sends.Count));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            var terminal = terminals[Math.Min(Sends.Count - 1, terminals.Length - 1)];
            if (Sends.Count == 1 && terminal == "failed") AfterFirstFailure?.Invoke();
            return Task.FromResult(terminal == "unknown"
                ? BoundaryTerminalResult.UncertainWith("unknown fixture")
                : BoundaryTerminalResult.Observed(terminal, exitConfirmed: ExitConfirmed));
        }
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
    private static WorkflowDocument Document(int retries, string mode = "stop", bool second = false)
    {
        var doc = new WorkflowDocument
        {
            Name = "失败策略", Activation = new() { Status = "active" },
            Execution = new() { OnNodeFailure = mode, MaxNodeRetries = retries },
            Nodes = [new() { NodeId = "one", Kind = "resource.oneDragonConfig", Ref = new() { Config = "cfg", Revision = "revision" } }],
        };
        if (second) doc.Nodes.Add(new() { NodeId = "two", Kind = "resource.oneDragonConfig", Ref = new() { Config = "cfg2", Revision = "revision" } });
        return doc;
    }
    private static (WorkflowRunner Runner, WorkflowStore Flows, RunStore Runs, string Id) Fixture(WorkflowDocument doc, Boundary boundary)
    {
        var root = Path.Combine(Path.GetTempPath(), "failure-policy-" + Guid.NewGuid().ToString("N"));
        var flows = new WorkflowStore(Path.Combine(root, "flows"));
        var runs = new RunStore(Path.Combine(root, "runs"));
        flows.Save(doc, null);
        return (new(flows, runs, boundary, new Prerequisite(), new Terminal()), flows, runs, doc.WorkflowId!);
    }

    [Fact]
    public async Task FailedResource_RetriesWithDistinctAttempt_AndKeepsOriginalFailure()
    {
        var boundary = new Boundary("failed", "succeeded");
        var fixture = Fixture(Document(1), boundary);
        var run = await fixture.Runner.StartAsync(fixture.Id);
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(new[] { 1, 2 }, boundary.Sends.Select(s => s.Attempt));
        Assert.Equal(2, boundary.Sends.Select(s => s.Key).Distinct().Count());
        Assert.Equal("failed", run.NodeOutcomes[0].Result);
        Assert.Equal(2, run.NodeOutcomes[0].RetryNextAttempt);
        Assert.Equal("succeeded", run.NodeOutcomes[1].Result);
        Assert.Equal(1, Assert.Single(run.SubmissionHistory).Attempt);
    }

    [Theory]
    [InlineData(0, 1)]
    [InlineData(3, 4)]
    public async Task FailedResource_UsesBoundedBudget(int retries, int expectedSends)
    {
        var boundary = new Boundary("failed");
        var fixture = Fixture(Document(retries), boundary);
        var run = await fixture.Runner.StartAsync(fixture.Id);
        Assert.Equal(WorkflowRunState.Failed, run.State);
        Assert.Equal(expectedSends, boundary.Sends.Count);
        Assert.All(run.NodeOutcomes, o => Assert.Equal("failed", o.Result));
    }

    [Theory]
    [InlineData("stop", 1)]
    [InlineData("continue", 2)]
    public async Task FailureMode_DeterminesWhetherNextResourceRuns(string mode, int expectedSends)
    {
        var boundary = new Boundary("failed", "succeeded");
        var fixture = Fixture(Document(0, mode, second: true), boundary);
        var run = await fixture.Runner.StartAsync(fixture.Id);
        Assert.Equal(expectedSends, boundary.Sends.Count);
        Assert.Equal(WorkflowRunState.Failed, run.State); // a different node's success never erases the failure
    }

    [Theory]
    [InlineData("unknown", WorkflowRunState.Unknown)]
    [InlineData("cancelled", WorkflowRunState.Cancelled)]
    public async Task UnknownOrCancelled_NeverRetries(string terminal, WorkflowRunState state)
    {
        var boundary = new Boundary(terminal);
        var fixture = Fixture(Document(3, "continue"), boundary);
        var run = await fixture.Runner.StartAsync(fixture.Id);
        Assert.Single(boundary.Sends);
        Assert.Equal(state, run.State);
    }

    [Fact]
    public async Task FailedWithoutConfirmedExit_PreservesResponsibilityWithoutRetry()
    {
        var boundary = new Boundary("failed") { ExitConfirmed = false };
        var fixture = Fixture(Document(3), boundary);
        var run = await fixture.Runner.StartAsync(fixture.Id);
        Assert.Single(boundary.Sends);
        Assert.False(run.CurrentSubmission!.ExecutionExitConfirmed);
        Assert.Null(Assert.Single(run.NodeOutcomes).RetryNextAttempt);
        Assert.True(RunStore.HasUnresolvedExternalFact(run));
    }

    [Fact]
    public async Task PauseAtRetryBoundary_ReopensWithPersistedNextAttempt()
    {
        var boundary = new Boundary("failed", "succeeded");
        var fixture = Fixture(Document(1), boundary);
        boundary.AfterFirstFailure = () =>
        {
            var run = Assert.Single(fixture.Runs.List());
            fixture.Runner.RequestAction(run.RunId, WorkflowRunAction.Pause);
        };
        var parked = await fixture.Runner.StartAsync(fixture.Id);
        Assert.Equal(WorkflowRunState.Paused, parked.State);
        Assert.Equal(2, parked.Cursor!.Attempt);
        var reopened = new WorkflowRunner(fixture.Flows, fixture.Runs, boundary, new Prerequisite(), new Terminal());
        var completed = await reopened.ResumeAsync(parked.RunId);
        Assert.Equal(WorkflowRunState.Succeeded, completed.State);
        Assert.Equal(new[] { 1, 2 }, boundary.Sends.Select(s => s.Attempt));
        Assert.Equal(2, completed.NodeOutcomes.Count);
    }

    [Fact]
    public void EditorPolicy_PersistsOverrides_AndPreservesUntouchedParameters()
    {
        var doc = Document(0);
        var catalog = new ResourceCatalogService(() => null, Path.Combine(Path.GetTempPath(), "failure-catalog-" + Guid.NewGuid().ToString("N")));
        var edit = new WorkflowEditVm(doc, "revision", catalog) { FailureModeIndex = 1, MaxRetriesText = "2" };
        edit.Nodes[0].UsePlanFailurePolicy = false;
        edit.Nodes[0].FailureModeIndex = 0;
        edit.Nodes[0].MaxRetriesText = "1";
        var copy = edit.BuildSubmissionCopy();
        var roundtrip = JsonSerializer.Deserialize<WorkflowDocument>(JsonSerializer.Serialize(copy))!;
        Assert.Equal(new WorkflowFailurePolicy(false, 1), WorkflowFailurePolicy.Resolve(roundtrip, roundtrip.Nodes[0]));
        Assert.Equal("continue", roundtrip.Execution!.OnNodeFailure);
        Assert.Equal(2, roundtrip.Execution.MaxNodeRetries);
        Assert.Empty(WorkflowFailurePolicy.Validate(roundtrip));
    }
}
