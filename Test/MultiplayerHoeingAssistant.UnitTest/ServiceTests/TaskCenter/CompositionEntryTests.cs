using System.Collections.Concurrent;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>显式选中的任务就是本次入口；只执行其后确定的路径，不重放被跳过的准备动作。</summary>
public sealed class CompositionEntryTests
{
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task MainlineBetweenPreparationAndLaneRunsSelectedTail(bool yes)
    {
        using var state = new State(yes);
        var doc = Document(); state.Save(doc);

        var run = await state.Runner().StartAsync(doc.WorkflowId!, entryNodeId: "A").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(yes ? new[] { "A", "B1", "B2", "D" } : new[] { "A", "C", "D" }, state.Port.Visited);
        Assert.Equal("A", run.ExplicitEntryNodeId);
        Assert.DoesNotContain(run.NodeOutcomes, o => o.NodeId == "prep");
        Assert.Equal(0, state.Value(1));
        Assert.Equal(1, state.Value(2));
        Assert.Equal(yes ? "branchYes" : "branchNo", run.NodeOutcomes.Single(o => o.NodeId == "lane").Result);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LaneEntryEvaluatesItsOwnConditionAndJoinsMainline(bool yes)
    {
        using var state = new State(yes);
        var doc = Document(); state.Save(doc);

        var run = await state.Runner().StartAsync(doc.WorkflowId!, entryNodeId: "lane").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(yes ? new[] { "B1", "B2", "D" } : new[] { "C", "D" }, state.Port.Visited);
        Assert.Equal("lane", run.ExplicitEntryNodeId);
        Assert.Equal(0, state.Value(1));
        Assert.Equal(1, state.Value(2));
        Assert.Single(run.NodeOutcomes.Where(o => o.NodeId == "D"));
    }

    [Theory]
    [InlineData("B1", false, "B1,B2,D")]
    [InlineData("B2", false, "B2,D")]
    [InlineData("C", true, "C,D")]
    public async Task ExplicitMemberContinuesOnlyThatBranchWithoutReevaluatingDecision(string entry, bool yes, string expected)
    {
        using var state = new State(yes);
        var doc = Document(); state.Save(doc);

        var run = await state.Runner().StartAsync(doc.WorkflowId!, entryNodeId: entry).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(expected.Split(','), state.Port.Visited);
        Assert.Equal(entry, run.ExplicitEntryNodeId);
        Assert.DoesNotContain(run.NodeOutcomes, o => o.NodeId is "prep" or "A" or "lane");
        Assert.Equal(0, state.Value(1));
        Assert.Equal(0, state.Value(2));
    }

    [Fact]
    public async Task SingleMemberStopsBeforeBranchTailAndJoin()
    {
        using var state = new State(false);
        var doc = Document(); state.Save(doc);

        var run = await state.Runner().StartAsync(doc.WorkflowId!, entryNodeId: "B1", stopAfterNodeId: "B1")
            .WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(new[] { "B1" }, state.Port.Visited);
        Assert.Equal("B1", Assert.Single(run.NodeOutcomes).NodeId);
        Assert.Equal(0, state.Value(1));
        Assert.Equal(0, state.Value(2));
    }

    [Fact]
    public async Task MainlineAfterLaneStartsAtSelectedTask()
    {
        using var state = new State(true);
        var doc = Document(); state.Save(doc);

        var run = await state.Runner().StartAsync(doc.WorkflowId!, entryNodeId: "D").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(new[] { "D" }, state.Port.Visited);
        Assert.Equal("D", Assert.Single(run.NodeOutcomes).NodeId);
        Assert.Equal(0, state.Value(1));
        Assert.Equal(0, state.Value(2));
    }

    [Fact]
    public async Task ExplicitEntryDoesNotChangeNextRunsDefaultStart()
    {
        using var state = new State(false);
        var doc = Document(); state.Save(doc);
        var first = await state.Runner().StartAsync(doc.WorkflowId!, entryNodeId: "A").WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(WorkflowRunState.Succeeded, first.State);
        Assert.Equal(0, state.Value(1));
        state.Port.Visited.Clear();

        var next = await state.Runner().StartAsync(doc.WorkflowId!).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowRunState.Succeeded, next.State);
        Assert.Null(next.ExplicitEntryNodeId);
        Assert.Equal("prep", next.NodeOutcomes.First().NodeId);
        Assert.Equal(1, state.Value(1));
        Assert.Equal(new[] { "A", "C", "D" }, state.Port.Visited);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task LegacyLaneEntryRunsItsOriginalPairedDecision(bool yes)
    {
        using var state = new State(yes);
        var doc = Document();
        var lane = doc.Nodes.Single(n => n.NodeId == "lane").Lanes!;
        var decision = lane.Decision!;
        lane.Decision = null; lane.SourceFlowNodeId = "legacy-decision";
        doc.Nodes.Insert(2, new() { NodeId = "legacy-decision", Kind = "control.flow", Flow = decision });
        state.Save(doc);

        var run = await state.Runner().StartAsync(doc.WorkflowId!, entryNodeId: "lane").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal(yes ? new[] { "B1", "B2", "D" } : new[] { "C", "D" }, state.Port.Visited);
        Assert.Equal("legacy-decision", run.ExplicitEntryNodeId);
        Assert.Equal(yes ? "branchYes" : "branchNo", run.NodeOutcomes.Single(o => o.NodeId == "legacy-decision").Result);
        Assert.Equal(0, state.Value(1));
        Assert.Equal(1, state.Value(2));
    }

    [Fact]
    public async Task ExplicitEntryOverridesUnrelatedLegacyWatermarkWithoutReplayingHead()
    {
        using var state = new State(false);
        var doc = Document();
        doc.ExtensionData = new() { ["watermark"] = JsonSerializer.SerializeToElement(new { entryNodeId = "A", once = true }) };
        state.Save(doc);

        var run = await state.Runner().StartAsync(doc.WorkflowId!, entryNodeId: "A").WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Equal("A", run.ExplicitEntryNodeId);
        Assert.Null(run.MigrationEntrySeedKey);
        Assert.Equal(new[] { "A", "C", "D" }, state.Port.Visited);
        Assert.Equal(0, state.Value(1));
    }

    [Fact]
    public async Task DefaultEntryStillRejectsUnsafeLegacyWatermarkBeforeCreatingRun()
    {
        using var state = new State(false);
        var doc = Document();
        doc.ExtensionData = new() { ["watermark"] = JsonSerializer.SerializeToElement(new { entryNodeId = "A", once = true }) };
        state.Save(doc);

        var error = await Assert.ThrowsAsync<InvalidOperationException>(() => state.Runner().StartAsync(doc.WorkflowId!));

        Assert.Contains("迁移入口", error.Message);
        Assert.Empty(state.Runs.List());
        Assert.Empty(state.Port.Visited);
        Assert.Equal(0, state.Value(1));
        Assert.Equal(0, state.Value(2));
    }

    [Fact]
    public async Task AcceptedPlannedEntryIgnoresLegacyWatermarkWhenDrivenThroughStartExisting()
    {
        using var state = new State(false);
        var doc = Document();
        doc.ExtensionData = new() { ["watermark"] = JsonSerializer.SerializeToElement(new { entryNodeId = "A", once = true }) };
        state.Save(doc);
        var snapshot = state.Flows.LoadSnapshot(doc.WorkflowId!);
        var planned = state.Runs.CreateRun(doc.WorkflowId!, snapshot.Revision,
            initialCursor: new WorkflowNodeCursor { NodeId = "A", Occurrence = 0, LoopIteration = 0, Attempt = 1 });
        Assert.Equal(WorkflowRunState.Planned, planned.State);
        Assert.Equal("A", planned.ExplicitEntryNodeId);

        var run = await state.Runner().StartExistingRunAsync(planned.RunId).WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(planned.RunId, run.RunId);
        Assert.Equal(WorkflowRunState.Succeeded, run.State);
        Assert.Null(run.MigrationEntrySeedKey);
        Assert.Equal(new[] { "A", "C", "D" }, state.Port.Visited);
        Assert.Equal(0, state.Value(1));
        Assert.Equal(1, state.Value(2));
    }

    [Theory]
    [InlineData(false, "A", null, "A,C,D")]
    [InlineData(false, "B2", "B2", "B2")]
    [InlineData(true, "A", null, "A,C,D")]
    [InlineData(true, "B2", "B2", "B2")]
    public async Task HostAcceptsMiddleEntryAndSingleLaneMember(bool admissionWired, string entry, string? stopAfter, string expected)
    {
        using var state = new State(false);
        var host = new TaskCenterHost(Path.Combine(state.Root, "flows"), Path.Combine(state.Root, "runs"),
            Path.Combine(state.Root, "catalog.json"), () => null, null,
            (_, flows, runs) => new WorkflowRunner(flows, runs, state.Port, state.Actions, state.Actions,
                new() { FlowRuntime = state.Runtime }), () => (true, null), admissionWired: admissionWired,
            admissionSeams: new() { Epoch = "9:900", F11Active = false, Occupied = false, FactsUnknown = false });
        try
        {
            var doc = Document();
            doc.ExtensionData = new() { ["watermark"] = JsonSerializer.SerializeToElement(new { entryNodeId = "A", once = true }) };
            host.SaveFlow(doc, null);
            var revision = host.LoadFlowSnapshot(doc.WorkflowId!).Revision;

            var action = await host.StartWorkflowAsync(doc.WorkflowId!, entry, revision, stopAfter);

            Assert.True(action.Ok, action.Message);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (host.Runs.List().All(r => !r.IsTerminal) || host.IsDriving(doc.WorkflowId!))
            {
                if (DateTime.UtcNow >= deadline) throw new TimeoutException("中途入口未在隔离宿主中完成。");
                await Task.Delay(20);
            }
            var run = Assert.Single(host.Runs.List());
            Assert.Equal(WorkflowRunState.Succeeded, run.State);
            Assert.Equal(entry, run.ExplicitEntryNodeId);
            Assert.Equal(stopAfter, run.StopAfterNodeId);
            Assert.Equal(expected.Split(','), state.Port.Visited);
            Assert.Equal(0, state.Value(1));
            Assert.Equal(stopAfter is null ? 1 : 0, state.Value(2));
        }
        finally { await host.ShutdownAsync(); }
    }

    private static WorkflowDocument Document()
    {
        var condition = StartupStepKinds.Create(StartupStepKinds.Weekday); condition.Weekdays = [6, 7];
        return new()
        {
            Name = "显式中途入口", Activation = new() { Status = "active" },
            Nodes = [
                new() { NodeId = "prep", Kind = "control.flow", Flow = new() { Steps = [Write(1)] } },
                Resource("A"),
                new() { NodeId = "lane", Kind = "control.lanes", Lanes = new()
                {
                    Decision = new() { Steps = [Write(2), condition], ExitStepId = condition.Id },
                    YesNodeIds = ["B1", "B2"], NoNodeIds = ["C"]
                } },
                Resource("B1"), Resource("B2"), Resource("C"), Resource("D")]
        };
    }

    private static StartupStep Write(int number)
    {
        var step = StartupStepKinds.Create(StartupStepKinds.SetRegister);
        var values = RegisterBank.EmptySelection(); values[number - 1] = 1;
        step.RegisterWrite = new() { Values = values };
        return step;
    }

    private static WorkflowNode Resource(string id) => new()
    { NodeId = id, Kind = "resource.configGroup", Ref = new() { Config = id, Revision = "rev-1" } };

    private sealed class State : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "composition-entry-" + Guid.NewGuid().ToString("N"));
        public WorkflowStore Flows { get; }
        public RunStore Runs { get; }
        public RegisterBankService Registers { get; }
        public TaskFlowRuntime Runtime { get; }
        public Port Port { get; } = new();
        public Actions Actions { get; } = new();
        public State(bool yes)
        {
            Flows = new(Path.Combine(Root, "flows")); Runs = new(Path.Combine(Root, "runs"));
            Registers = new(Path.Combine(Root, "registers.json"), startTimer: false);
            Runtime = new() { Registers = Registers,
                Clock = () => new DateTimeOffset(2026, 11, yes ? 7 : 9, 12, 0, 0, TimeSpan.FromHours(8)) };
        }
        public int Value(int number) => Registers.GetSnapshot().Entries[number - 1].Value;
        public void Save(WorkflowDocument doc) => Flows.Save(doc, null);
        public WorkflowRunner Runner() => new(Flows, Runs, Port, Actions, Actions, new() { FlowRuntime = Runtime });
        public void Dispose()
        {
            Registers.Dispose();
            if (Directory.Exists(Root)) Directory.Delete(Root, true);
        }
    }

    private sealed class Port : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public ConcurrentQueue<string> Visited { get; } = new();
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Visited.Enqueue(request.Occurrence.NodeId); TerminalReleaseFixtureFacts.FreezeBody(request);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Guid.NewGuid().ToString("N")));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string id, CancellationToken ct)
            => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
    }

    private sealed class Actions : IWorkflowPrerequisiteAdapter, IWorkflowTerminalExecutor
    {
        public Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run,
            WorkflowNodeOccurrence occurrence, CancellationToken ct) => Task.FromResult(PrerequisiteResult.ProceedInstance);
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run,
            CancellationToken ct) => Task.FromResult(TerminalExecutionResult.Executed(null));
    }
}
