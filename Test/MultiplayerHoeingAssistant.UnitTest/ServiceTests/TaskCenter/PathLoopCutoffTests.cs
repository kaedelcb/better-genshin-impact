using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public class PathLoopCutoffTests
{
    private static readonly DateTimeOffset Day = new(2026, 10, 8, 9, 0, 0, TimeSpan.FromHours(8));

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public void StructuralLoop_RestartsPrimaryEntryAfterEarlierSecondaryNode(bool explicitEnd)
    {
        var plan = new WorkflowPlan(PathDocument(explicitEnd));
        var first = plan.FirstOccurrence()!;
        Assert.Equal("primary", first.NodeId);
        Assert.Equal(1, first.SequenceIndex);
        Assert.Equal(0, first.PathLane);
        var next = plan.Next(first)!;
        Assert.Equal(first.NodeId, next.NodeId);
        Assert.Equal(first.Occurrence, next.Occurrence);
        Assert.Equal(first.SequenceIndex, next.SequenceIndex);
        Assert.Equal(0, next.PathLane);
        Assert.Equal(1, next.LoopIteration);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task StructuralLoop_TwoRoundsSubmitOnlySelectedPrimaryWithFreshIdentity(bool explicitEnd)
    {
        var fixture = new Fixture(PathDocument(explicitEnd));
        fixture.Boundary.OnSend = _ =>
        {
            if (fixture.Boundary.Sent.Count == 2) fixture.Pause();
        };
        var run = await fixture.Run();
        Assert.Equal(WorkflowRunState.Paused, run.State);
        Assert.Equal(new[] { "primary", "primary" }, fixture.Boundary.Sent.Select(s => s.Occurrence.NodeId));
        Assert.Equal(new[] { 0, 1 }, fixture.Boundary.Sent.Select(s => s.Occurrence.LoopIteration));
        Assert.All(fixture.Boundary.Sent, s => Assert.Equal(0, s.Occurrence.PathLane));
        Assert.Equal(2, fixture.Boundary.Sent.Select(s => s.Key).Distinct().Count());
    }

    [Fact]
    public async Task ScheduledLoop_NonzeroPrimaryEntryStillWaitsForNextRound()
    {
        var doc = PathDocument(true);
        doc.Loop = ScheduledLoop("10:00");
        var fixture = new Fixture(doc);
        fixture.Boundary.OnTerminal = () => fixture.Now = fixture.Now.AddMinutes(1);
        fixture.Boundary.OnSend = _ =>
        {
            if (fixture.Boundary.Sent.Count == 2) fixture.Pause();
        };
        var run = await fixture.Run();
        Assert.Equal(WorkflowRunState.Paused, run.State);
        Assert.Equal(new[] { "primary", "primary" }, fixture.Boundary.Sent.Select(s => s.Occurrence.NodeId));
        Assert.Equal(Day, fixture.Boundary.Sent[0].At);
        Assert.Equal(Day.AddHours(1), fixture.Boundary.Sent[1].At);
        Assert.Contains(TimeSpan.FromMinutes(59), fixture.Delays);
        Assert.Equal(1, run.LastScheduledRoundWait);
    }

    [Theory]
    [InlineData("sequence", false)]
    [InlineData("fixed", false)]
    [InlineData("sequence", true)]
    public async Task TimedNode_CutoffConvergesBeforePrerequisiteAndSubmission(string mode, bool overshoot)
    {
        var fixture = new Fixture(new()
        {
            Name = "timed-cutoff", Nodes = [Timed("late", "11:00", mode, prerequisite: true)],
            Loop = DeadlineLoop("10:00"),
        });
        fixture.OnDelay = _ => { if (overshoot) fixture.Now = Day.AddHours(2); };
        var run = await fixture.Run();
        Assert.Equal(0, fixture.Actions.PrerequisiteCalls);
        Assert.Empty(fixture.Boundary.Sent);
        Assert.Equal(TimeSpan.FromHours(1), Assert.Single(fixture.Delays));
        Assert.Equal(Day.AddHours(1), run.LoopDeadlineAt);
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.True(run.TailReached);
        Assert.Null(run.Wait);
        Assert.False(run.StopRequested);
        Assert.True(TerminalReleaseEvidence.RunSettled(fixture.Runs.Load(run.RunId)!));
    }

    [Fact]
    public async Task FlexibleNode_IdleOnlyAfterCutoffDoesNotStart()
    {
        var node = Timed("flex", "09:00", "flexible", prerequisite: true);
        node.Strategies[0].Params!["until"] = JsonSerializer.SerializeToElement("11:00");
        var fixture = new Fixture(new() { Name = "flex-cutoff", Nodes = [node], Loop = DeadlineLoop("10:00") });
        fixture.Facts = () => new() { ExecutionOccupied = fixture.Now < Day.AddHours(1).AddSeconds(1) };
        // One finite clock jump models a wakeup after the deadline; no polling/stress run.
        fixture.OnDelay = _ => fixture.Now = Day.AddHours(1).AddSeconds(1);
        var run = await fixture.Run();
        Assert.Equal(0, fixture.Actions.PrerequisiteCalls);
        Assert.Empty(fixture.Boundary.Sent);
        Assert.Single(fixture.Delays);
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.True(run.TailReached);
        Assert.False(run.StopRequested);
    }

    [Fact]
    public async Task StartedNodeMayFinishAfterCutoffButSuccessorDoesNotStart()
    {
        var fixture = new Fixture(new()
        {
            Name = "started-cutoff", Nodes = [Timed("started", "09:00", "sequence"), Timed("later", "09:00", "sequence", prerequisite: true)],
            Loop = DeadlineLoop("10:00"),
        });
        fixture.Boundary.OnTerminal = () => fixture.Now = Day.AddHours(2);
        var run = await fixture.Run();
        Assert.Equal("started", Assert.Single(fixture.Boundary.Sent).Occurrence.NodeId);
        Assert.Equal("succeeded", Assert.Single(run.NodeOutcomes).Result);
        Assert.Equal(0, fixture.Actions.PrerequisiteCalls);
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.True(run.TailReached);
    }

    [Fact]
    public async Task ScheduledRound_TimedWaitCannotRunExpiredRemainder()
    {
        var fixture = new Fixture(new()
        {
            Name = "expired-round", Nodes = [Timed("late", "11:00", "sequence", prerequisite: true)],
            Loop = ScheduledLoop("10:00"),
        });
        fixture.OnDelay = _ =>
        {
            if (fixture.Delays.Count == 2) fixture.Pause();
        };
        var run = await fixture.Run();
        Assert.Empty(fixture.Boundary.Sent);
        Assert.Equal(0, fixture.Actions.PrerequisiteCalls);
        Assert.Equal("skippedFilter", Assert.Single(run.NodeOutcomes).Result);
        Assert.Equal(0, run.NodeOutcomes[0].LoopIteration);
        Assert.Equal(TimeSpan.FromHours(1), fixture.Delays[0]);
        Assert.Equal(WorkflowRunState.Paused, run.State);
        Assert.Equal(1, run.Cursor!.LoopIteration);
    }

    private static WorkflowDocument PathDocument(bool explicitEnd)
    {
        var secondary = Timed("secondary", "07:00", "sequence");
        secondary.ExtensionData = new() { ["scheduleLane"] = JsonSerializer.SerializeToElement(1) };
        var primary = Timed("primary", "08:00", "sequence");
        primary.Strategies.Add(new() { Kind = "flow.route" });
        if (explicitEnd) primary.Path = new() { Next = "$end" };
        return new()
        {
            Name = "primary-loop", Nodes = [secondary, primary], Loop = new() { Mode = "immediate" },
            ExtensionData = new() { ["scheduleLanes"] = JsonSerializer.SerializeToElement(new[] { "主车道", "支线" }) },
        };
    }

    private static WorkflowLoop DeadlineLoop(string deadline) => new()
    {
        Mode = "immediate", Params = new() { ["deadline"] = JsonSerializer.SerializeToElement(deadline) },
    };
    private static WorkflowLoop ScheduledLoop(string time) => new()
    {
        Mode = "scheduled", Params = new()
        {
            ["time"] = JsonSerializer.SerializeToElement(time), ["skipAcrossDays"] = JsonSerializer.SerializeToElement(true),
        },
    };
    private static WorkflowNode Timed(string id, string time, string mode, bool prerequisite = false)
    {
        var node = new WorkflowNode
        {
            NodeId = id, Kind = "resource.oneDragonConfig", Ref = new() { Config = id },
            Strategies = [new() { Kind = "schedule.time", Params = new()
            {
                ["time"] = JsonSerializer.SerializeToElement(time), ["mode"] = JsonSerializer.SerializeToElement(mode),
            } }],
        };
        if (prerequisite) node.Strategies.Add(new() { Kind = "prerequisite.account", Params = new() { ["uid"] = JsonSerializer.SerializeToElement("100000001") } });
        return node;
    }

    private sealed class Fixture
    {
        public DateTimeOffset Now = Day;
        public readonly List<TimeSpan> Delays = [];
        public Action<TimeSpan>? OnDelay;
        public Func<FlexibleWindowFacts> Facts = () => new();
        public readonly Boundary Boundary;
        public readonly Actions Actions = new();
        public readonly RunStore Runs;
        private readonly WorkflowRunner _runner;
        private readonly string _runId;
        public Fixture(WorkflowDocument doc)
        {
            var root = Path.Combine(Path.GetTempPath(), "path-loop-cutoff-" + Guid.NewGuid().ToString("N"));
            var flows = new WorkflowStore(Path.Combine(root, "flows"));
            Runs = new RunStore(Path.Combine(root, "runs"));
            var saved = flows.Save(doc, null);
            var run = Runs.CreateRun(doc.WorkflowId!, saved);
            run.CreatedAt = Now;
            Runs.Update(run);
            _runId = run.RunId;
            Boundary = new(() => Now);
            _runner = new(flows, Runs, Boundary, Actions, Actions, new()
            {
                Clock = () => Now, FlexibleFactsProvider = () => Facts(), DelayAsync = (delay, ct) =>
                {
                    ct.ThrowIfCancellationRequested();
                    Delays.Add(delay);
                    Now += delay;
                    OnDelay?.Invoke(delay);
                    ct.ThrowIfCancellationRequested();
                    return Task.CompletedTask;
                },
            });
        }
        public void Pause() => _runner.RequestAction(_runId, WorkflowRunAction.Pause);
        public Task<WorkflowRunRecord> Run() => _runner.StartExistingRunAsync(_runId).WaitAsync(TimeSpan.FromSeconds(10));
    }

    private sealed class Boundary(Func<DateTimeOffset> clock) : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public readonly List<(WorkflowNodeOccurrence Occurrence, string Key, DateTimeOffset At)> Sent = [];
        public Action<WorkflowSubmitRequest>? OnSend;
        public Action? OnTerminal;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Sent.Add((request.Occurrence, request.Run.CurrentSubmission!.Key, clock()));
            TerminalReleaseFixtureFacts.FreezeBody(request);
            OnSend?.Invoke(request);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Sent.Count));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            ct.ThrowIfCancellationRequested();
            OnTerminal?.Invoke();
            return Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
        }
    }
    private sealed class Actions : IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public int PrerequisiteCalls;
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
        {
            PrerequisiteCalls++;
            return Task.FromResult(PrerequisiteResult.ProceedInstance);
        }
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed(null));
    }
}
