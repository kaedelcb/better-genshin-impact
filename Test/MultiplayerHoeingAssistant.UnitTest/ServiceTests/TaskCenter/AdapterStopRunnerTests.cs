using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class AdapterStopRunnerTests
{
    private sealed class RebasingPrerequisites(AdapterStopRunnerTests owner) : IWorkflowPrerequisiteAdapter
    {
        public async Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
        {
            var a = run.PrerequisiteActions.Single();
            a.SendAttempted = true; a.State = PrerequisiteActionState.Unknown; a.WireRunId = run.WireRunId;
            a.Epoch = "42:99"; a.IdempotencyKey = "first"; a.Fingerprint = "fp-first"; a.JobId = "job-first";
            run.PrerequisiteActions.Add(new() { NodeId = "earlier", Occurrence = 1, LoopIteration = 0, Attempt = 1,
                WireRunId = run.WireRunId, Epoch = "42:99", IdempotencyKey = "second", Fingerprint = "fp-second",
                JobId = "job-second", SendAttempted = true, State = PrerequisiteActionState.Unknown });
            owner._runs.Update(run); owner._entered.TrySetResult(run.RunId);
            await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException();
        }
        public Task<PrerequisiteResult> ConfirmCancellationAsync(WorkflowRunRecord run, PrerequisiteActionRecord a, CancellationToken ct)
        {
            var binding = BgiWorkflowObservationPersistence.Binding.Freeze(a);
            var job = new BgiJobInfo { Epoch = new() { ProcessId = 42, StartTicksUtc = 99 }, JobId = a.JobId,
                IdempotencyKey = a.IdempotencyKey, WorkflowRunId = a.WireRunId, NodeId = a.NodeId,
                Occurrence = a.Occurrence, Iteration = a.LoopIteration, Attempt = a.Attempt,
                State = "succeeded", ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited" };
            BgiWorkflowObservationPersistence.Save(owner._runs, run, a, binding, a.JobId, a.JobId!, job);
            return Task.FromResult(new PrerequisiteResult(PrerequisiteStatus.Proceed, null, a.JobId));
        }
    }

    [Fact]
    public async Task Stop_MultipleRebasedPrerequisitesPreserveEarlierDurableOutcome()
    {
        var flow = new WorkflowDocument { Name = "multi-observation", Activation = new() { Status = "active" },
            Nodes = [new() { NodeId = "node", Kind = "resource.oneDragonConfig", Ref = new() { Config = "config" },
                Strategies = [new() { Kind = "prerequisite.account", Params = new() { ["uid"] = JsonSerializer.SerializeToElement("123") } }] }] };
        _flows.Save(flow, null); var boundary = new Boundary(this);
        var runner = new WorkflowRunner(_flows, _runs, boundary, new RebasingPrerequisites(this), new Terminal(this));
        var task = runner.StartAsync(flow.WorkflowId!); var id = await _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.RequestAction(id, WorkflowRunAction.Stop); var final = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(WorkflowRunState.Cancelled, final.State);
        var saved = _runs.Load(id)!; Assert.Equal(2, saved.PrerequisiteActions.Count);
        Assert.All(saved.PrerequisiteActions, a => { Assert.Equal(PrerequisiteActionState.Succeeded, a.State);
            Assert.Equal("succeeded", a.ObservedTerminal); Assert.True(a.ExecutionExitConfirmed); });
        Assert.Equal(0, boundary.Sends);
    }

    private readonly RunStore _runs = new(Path.Combine(Path.GetTempPath(), "adapter-stop-" + Guid.NewGuid().ToString("N"), "runs"));
    private readonly WorkflowStore _flows = new(Path.Combine(Path.GetTempPath(), "adapter-stop-" + Guid.NewGuid().ToString("N"), "flows"));
    private readonly TaskCompletionSource<string> _entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private string _raw = "succeeded";
    private string _stage = "prerequisite";
    private int _observations;

    private sealed class Boundary(AdapterStopRunnerTests owner) : IWorkflowExecutionBoundary
    {
        public bool SingleNativeSupported => true;
        public int Sends;
        public WorkflowRunRecord? Run;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest r, CancellationToken ct)
        {
            TerminalReleaseFixtureFacts.FreezeBody(r);
            Sends++; Run = r.Run;
            r.Run.CurrentSubmission!.SendAttempted = true;
            owner._runs.Update(r.Run);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("body-job"));
        }
        public async Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            if (owner._stage == "body")
            {
                if (++owner._observations == 1)
                {
                    Run!.CurrentSubmission!.ObservedTerminal = "succeeded";
                    Run.CurrentSubmission.ExecutionExitConfirmed = false;
                    owner._runs.Update(Run);
                    owner._entered.TrySetResult(Run.RunId);
                    await Task.Delay(Timeout.Infinite, ct);
                }
            }
            return BoundaryTerminalResult.Observed("succeeded");
        }
    }

    private sealed class Prerequisite(AdapterStopRunnerTests owner) : IWorkflowPrerequisiteAdapter
    {
        public async Task<PrerequisiteResult> ExecuteAsync(WorkflowStrategy strategy, WorkflowRunRecord run, WorkflowNodeOccurrence occurrence, CancellationToken ct)
        {
            if (owner._stage != "prerequisite") return PrerequisiteResult.ProceedInstance;
            var a = run.PrerequisiteActions.Last();
            a.SendAttempted = true; a.JobId = "prerequisite-job"; a.State = PrerequisiteActionState.Submitted;
            a.WireRunId = run.WireRunId; a.Epoch = "42:99"; a.IdempotencyKey = "original";
            owner._runs.Update(run); owner._entered.TrySetResult(run.RunId);
            await Task.Delay(Timeout.Infinite, ct);
            throw new InvalidOperationException();
        }
        public Task<PrerequisiteResult> ConfirmCancellationAsync(PrerequisiteActionRecord a, CancellationToken ct)
        {
            Assert.False(ct.IsCancellationRequested);
            owner._observations++;
            a.ObservedTerminal = owner._raw; a.ExecutionExitConfirmed = true;
            a.ExecutionExitDisposition = "execution_exited"; a.EffectState = owner._raw;
            return Task.FromResult(new PrerequisiteResult(owner._raw == "succeeded" ? PrerequisiteStatus.Proceed
                : owner._raw == "cancelled" ? PrerequisiteStatus.Cancelled : PrerequisiteStatus.Failed, null, a.JobId));
        }
    }

    private sealed class Terminal(AdapterStopRunnerTests owner) : IWorkflowTerminalExecutor
    {
        public int Sends;
        public async Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
        {
            Sends++;
            var c = run.PendingCompletion!; c.State = "submitted"; c.SendAttempted = true;
            c.JobId = "terminal-job"; c.Fingerprint = "frozen"; c.WireRunId = run.WireRunId;
            c.Epoch = "42:99"; c.IdempotencyKey = "original"; c.ExpiresAtUtc = "2030-01-01T00:00:00Z";
            owner._runs.Update(run); owner._entered.TrySetResult(run.RunId);
            await Task.Delay(Timeout.Infinite, ct); throw new InvalidOperationException();
        }
        public Task<TerminalExecutionResult> ConfirmCancellationAsync(PendingCompletionRecord c, CancellationToken ct)
        {
            Assert.False(ct.IsCancellationRequested); owner._observations++;
            c.ObservedTerminal = owner._raw; c.ExecutionExitConfirmed = true;
            c.ExecutionExitDisposition = "execution_exited"; c.EffectState = owner._raw;
            return Task.FromResult(owner._raw == "succeeded" ? TerminalExecutionResult.Executed(c.JobId)
                : owner._raw == "cancelled" ? TerminalExecutionResult.CancelledWith(c.JobId, null)
                : TerminalExecutionResult.UnknownWith(c.JobId, "effect_unknown"));
        }
    }

    [Theory]
    [InlineData("prerequisite", "succeeded", WorkflowRunState.Cancelled)]
    [InlineData("prerequisite", "failed", WorkflowRunState.Cancelled)]
    [InlineData("prerequisite", "cancelled", WorkflowRunState.Cancelled)]
    [InlineData("terminal", "succeeded", WorkflowRunState.Cancelled)]
    [InlineData("terminal", "cancelled", WorkflowRunState.Cancelled)]
    [InlineData("terminal", "unknown", WorkflowRunState.Unknown)]
    [InlineData("body", "succeeded", WorkflowRunState.Cancelled)]
    public async Task Stop_ObservesEveryOutstandingStageAndPreservesActualFacts(string stage, string raw, WorkflowRunState expected)
    {
        _stage = stage; _raw = raw;
        var flow = new WorkflowDocument { Name = "all-stage-stop", Activation = new() { Status = "active" },
            Nodes = [new() { NodeId = "node", Kind = "resource.oneDragonConfig", Ref = new() { Config = "config" },
                Strategies = stage == "prerequisite" ? [new() { Kind = "prerequisite.account", Params = new() { ["uid"] = JsonSerializer.SerializeToElement("123") } }] : [] }],
            Terminal = [new() { Kind = "terminal.completionAction", Params = new() { ["action"] = JsonSerializer.SerializeToElement("closeGame") } }] };
        _flows.Save(flow, null);
        var boundary = new Boundary(this); var terminal = new Terminal(this);
        var runner = new WorkflowRunner(_flows, _runs, boundary, new Prerequisite(this), terminal);
        var task = runner.StartAsync(flow.WorkflowId!);
        var id = await _entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        runner.RequestAction(id, WorkflowRunAction.Stop);
        var run = await task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(stage == "body" ? 2 : 1, _observations);
        Assert.Equal(expected, run.State);
        Assert.True(_runs.Load(id)!.StopRequested);
        if (stage == "prerequisite") { Assert.Equal(raw, run.PrerequisiteActions.Single().ObservedTerminal); Assert.Equal(0, boundary.Sends); Assert.Equal(0, terminal.Sends); }
        if (stage == "body") Assert.Equal(raw, run.CurrentSubmission!.ObservedTerminal);
        if (stage == "terminal")
        {
            using var json = JsonDocument.Parse(JsonSerializer.Serialize(_runs.Load(id)));
            var records = json.RootElement.GetProperty("completionHistory");
            if (expected == WorkflowRunState.Cancelled) Assert.Equal(raw, records[0].GetProperty("observedTerminal").GetString());
            else Assert.Equal(raw, run.PendingCompletion!.ObservedTerminal);
            Assert.Equal(1, terminal.Sends);
        }
    }
}
