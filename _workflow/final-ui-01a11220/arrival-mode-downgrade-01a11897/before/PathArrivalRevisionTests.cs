using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class PathArrivalRevisionTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RevisionSavedDuringNodeWait_AppliesBeforeConditionChoosesSuccessor(bool explicitReload)
    {
        var fixture = new Fixture(Document(Condition("gate", "end", "unselected", "10:00"), End(), Condition("unselected", "$end", "$end")));
        string? revision = null;
        fixture.OnDelay = () =>
        {
            revision = fixture.Edit(doc =>
            {
                doc.Nodes[0].Path!.Yes = "inserted";
                doc.Nodes.Add(Condition("inserted", "end", "$end"));
            });
            if (explicitReload) fixture.Request(WorkflowRunAction.ReloadDefinition);
        };
        var run = await fixture.Run();
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(new[] { "gate", "inserted", "end" }, run.NodeOutcomes.Select(o => o.NodeId));
        Assert.Equal(new[] { "branchYes", "branchYes", "succeeded" }, run.NodeOutcomes.Select(o => o.Result));
        Assert.Equal(revision, run.WorkflowRevision);
        Assert.Empty(fixture.Boundary.Sent);
        Assert.Empty(run.SubmissionHistory);
        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(fixture.Delays));
        Assert.Null(run.Wait);
        Assert.True(TerminalReleaseEvidence.RunSettled(fixture.Runs.Load(run.RunId)!));
    }

    [Fact]
    public async Task ReloadDuringSelectedSuccessorWait_DoesNotReevaluateCompletedCondition()
    {
        var selected = new WorkflowNode { NodeId = "selected", Kind = "resource.oneDragonConfig", Ref = new() { Config = "selected" }, Path = new() { Next = "end" }, Strategies = [Route(), Time("10:00")] };
        var fixture = new Fixture(Document(Condition("gate", "selected", "unselected"), selected, End(), Condition("unselected", "$end", "$end")));
        fixture.OnDelay = () =>
        {
            fixture.Edit(doc => { doc.Nodes[0].Path!.Condition!.Value = false; doc.Nodes[0].Path!.Yes = "unselected"; });
            fixture.Request(WorkflowRunAction.ReloadDefinition);
        };
        var run = await fixture.Run();
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(new[] { "gate", "selected", "end" }, run.NodeOutcomes.Select(o => o.NodeId));
        Assert.Equal("branchYes", run.NodeOutcomes[0].Result);
        Assert.Equal("selected", Assert.Single(fixture.Boundary.Sent));
    }

    [Fact]
    public async Task ReloadDeletingWaitingPathNode_DoesNotGuessDifferentArrival()
    {
        var fixture = new Fixture(Document(Condition("gate", "end", "$end", "10:00"), End()));
        fixture.OnDelay = () =>
        {
            fixture.Edit(doc => doc.Nodes.RemoveAt(0));
            fixture.Request(WorkflowRunAction.ReloadDefinition);
        };
        var run = await fixture.Run();
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(new[] { "gate", "end" }, run.NodeOutcomes.Select(o => o.NodeId));
        Assert.Empty(fixture.Boundary.Sent);
    }

    [Fact]
    public async Task StopDuringNodeWait_WithPendingRevisionCannotExecuteAnyNode()
    {
        var fixture = new Fixture(Document(Condition("gate", "end", "$end", "10:00"), End()));
        fixture.OnDelay = () =>
        {
            fixture.Edit(doc => doc.Nodes.Add(Condition("inserted", "$end", "$end")));
            fixture.Request(WorkflowRunAction.ReloadDefinition);
            fixture.Request(WorkflowRunAction.Stop);
        };
        var run = await fixture.Run();
        Assert.Equal(WorkflowRunState.Cancelled, run.State);
        Assert.Empty(run.NodeOutcomes);
        Assert.Empty(fixture.Boundary.Sent);
        Assert.Empty(run.SubmissionHistory);
    }

    [Fact]
    public async Task DeadlineAtWaitArrival_WithPendingRevisionStillPreventsAllNodeActions()
    {
        var doc = Document(Condition("gate", "end", "$end", "10:00"), End());
        doc.Loop = new() { Mode = "immediate", Params = new() { ["deadline"] = JsonSerializer.SerializeToElement("09:30") } };
        var fixture = new Fixture(doc);
        fixture.OnDelay = () =>
        {
            fixture.Edit(current => { current.Nodes[0].Path!.Yes = "inserted"; current.Nodes.Add(Condition("inserted", "end", "$end")); });
            fixture.Request(WorkflowRunAction.ReloadDefinition);
        };
        var run = await fixture.Run();
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(TimeSpan.FromMinutes(30), Assert.Single(fixture.Delays));
        Assert.Empty(run.NodeOutcomes);
        Assert.Empty(fixture.Boundary.Sent);
        Assert.Null(run.Wait);
        Assert.True(run.TailReached);
    }

    private static WorkflowDocument Document(params WorkflowNode[] nodes) => new() { Name = "path-arrival-revision", Nodes = [.. nodes] };
    private static WorkflowStrategy Route() => new() { Kind = "flow.route" };
    private static WorkflowStrategy Time(string time) => new() { Kind = "schedule.time", Params = new() { ["time"] = JsonSerializer.SerializeToElement(time), ["mode"] = JsonSerializer.SerializeToElement("sequence") } };
    private static WorkflowNode Condition(string id, string yes, string no, string? time = null) => new()
    {
        NodeId = id, Kind = "control.condition", Path = new() { Yes = yes, No = no, Condition = new() { Kind = "constant", Value = true } },
        Strategies = time is null ? [Route()] : [Route(), Time(time)],
    };
    private static WorkflowNode End() => new() { NodeId = "end", Kind = "control.end", Strategies = [Route()] };

    private sealed class Fixture
    {
        private DateTimeOffset _now = Day;
        private readonly WorkflowStore _flows;
        private readonly WorkflowRunner _runner;
        private readonly string _workflowId, _runId;
        public readonly RunStore Runs;
        public readonly Boundary Boundary = new();
        public readonly List<TimeSpan> Delays = [];
        public Action? OnDelay;
        public string Revision { get; private set; }
        public Fixture(WorkflowDocument doc)
        {
            var root = Path.Combine(Path.GetTempPath(), "path-arrival-revision-" + Guid.NewGuid().ToString("N"));
            _flows = new(Path.Combine(root, "flows")); Runs = new(Path.Combine(root, "runs"));
            Revision = _flows.Save(doc, null); _workflowId = doc.WorkflowId!;
            var run = Runs.CreateRun(_workflowId, Revision); run.CreatedAt = Day; Runs.Update(run); _runId = run.RunId;
            var actions = new Actions();
            _runner = new(_flows, Runs, Boundary, actions, actions, new()
            {
                Clock = () => _now, DelayAsync = (delay, ct) =>
                {
                    ct.ThrowIfCancellationRequested(); Delays.Add(delay); _now += delay;
                    var callback = OnDelay; OnDelay = null; callback?.Invoke();
                    ct.ThrowIfCancellationRequested(); return Task.CompletedTask;
                },
            });
        }
        public string Edit(Action<WorkflowDocument> edit)
        {
            var snapshot = _flows.LoadSnapshot(_workflowId); edit(snapshot.Document);
            return Revision = _flows.Save(snapshot.Document, snapshot.Revision);
        }
        public void Request(WorkflowRunAction action) => _runner.RequestAction(_runId, action);
        public Task<WorkflowRunRecord> Run() => _runner.StartExistingRunAsync(_runId).WaitAsync(TimeSpan.FromSeconds(10));
    }
    private sealed class Boundary : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public readonly List<string> Sent = [];
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Sent.Add(request.Occurrence.NodeId); TerminalReleaseFixtureFacts.FreezeBody(request);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Sent.Count));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
    }
    private sealed class Actions : IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
            => Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed(null));
    }
}
