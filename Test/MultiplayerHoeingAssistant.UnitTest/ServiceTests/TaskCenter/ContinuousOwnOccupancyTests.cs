using System.Collections.Concurrent;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>
/// Exercises the production wait decision and automatic continuation with a delayed BGI status snapshot.
/// Only node execution is simulated; handoff registration, arbitration recovery, run persistence and the
/// local wait queue/monitor use their real implementation against an isolated temporary data root.
/// </summary>
[Collection("TaskCenterHeavyE2E")]
public sealed class ContinuousOwnOccupancyTests
{
    private enum StatusAfterFirstNode
    {
        OwnRun,
        Unknown,
        Stale,
        OtherRun,
    }

    private sealed class Fixture
    {
        private const string Epoch = "42:99";
        private readonly object _statusGate = new();
        private readonly StatusAfterFirstNode _afterFirstNode;
        private bool _busy;
        private string? _wireRunId;
        private string? _jobId;
        private readonly Guid _otherRunId = Guid.NewGuid();
        private readonly Guid _executionId = Guid.NewGuid();
        private readonly BgiExternalClient _client;

        internal string Root { get; } = Path.Combine(Path.GetTempPath(), "continuous-own-occupancy-" + Guid.NewGuid().ToString("N"));
        internal ConcurrentQueue<string> Sends { get; } = new();
        internal ConcurrentQueue<string> Logs { get; } = new();
        internal TaskCenterHost Host { get; }
        internal string WorkflowId { get; }
        internal string OldUnknownRunId { get; }
        internal byte[] OldUnknownBytes { get; }
        internal string OldUnknownPath => Path.Combine(Root, "runs", OldUnknownRunId + ".run.json");

        internal Fixture(StatusAfterFirstNode afterFirstNode)
        {
            _afterFirstNode = afterFirstNode;
            var workflows = new WorkflowStore(Path.Combine(Root, "flows"));
            var document = new WorkflowDocument
            {
                Name = "测试",
                Activation = new() { Status = "active" },
                Nodes =
                [
                    new() { NodeId = "mail", Kind = "resource.singleTask", Ref = new() { Config = "领取邮件", TaskId = "mail", Revision = "rev-mail" } },
                    new() { NodeId = "close-stream", Kind = "resource.configGroup", Ref = new() { Config = "关直播", Revision = "rev-close" } },
                ],
            };
            workflows.Save(document, null);
            WorkflowId = document.WorkflowId!;

            // Keep the already isolated historical Unknown in the same workflow. It has no current
            // submission/operation and belongs to the old BGI epoch, so it must not block a new run.
            var legacyRuns = new RunStore(Path.Combine(Root, "runs"));
            var old = legacyRuns.CreateRun(WorkflowId, workflows.LoadSnapshot(WorkflowId).Revision,
                admissionSourceScope: "bgi:local:41:98", note: "旧纪元 Unknown 历史，当前发送责任已隔离");
            old.State = WorkflowRunState.Unknown;
            legacyRuns.Update(old);
            OldUnknownRunId = old.RunId;
            OldUnknownBytes = File.ReadAllBytes(OldUnknownPath);

            // Do not connect to a real pipe. The epoch is only an identity fixture; leaving admission
            // seams null is essential so CurrentArbitrationFacts reads the production status provider.
            _client = new BgiExternalClient(Root);
            typeof(BgiExternalClient).GetField("<ServerEpoch>k__BackingField", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(_client, new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });

            TaskCenterHost? owner = null;
            owner = new TaskCenterHost(Path.Combine(Root, "flows"), Path.Combine(Root, "runs"), Path.Combine(Root, "catalog.json"),
                clientAccessor: () => _client, log: Logs.Enqueue,
                runnerFactory: (_, flows, runs) => new WorkflowRunner(flows, runs, new Boundary(this),
                    new Prerequisite(), new Terminal(), localWaitQueue: owner!.LocalWaitQueue,
                    localWaitAdmissionScopeProvider: run => owner.AdmissionParentForTest(run.RunId, run.WorkflowId)?.Scope,
                    localWaitPrerequisiteReferenceProvider: PrerequisiteReference,
                    waitDecisionSource: owner.WaitDecisionSource),
                readinessOverride: () => (true, null), localExecutionCapability: () => true,
                statusSnapshotProvider: Status, admissionWired: true, successorAdmissionWired: true);
            Host = owner;
        }

        internal async Task<HandoffRegisterResult> StartAsync()
            => await Host.RegisterHandoffAsync(new StartupHandoffRequest
            {
                WorkflowId = WorkflowId,
                ExecutionId = Guid.NewGuid().ToString("N"),
                IntentKey = "manual:continuous-own-" + Guid.NewGuid().ToString("N"),
                StepId = "enter-task-center",
                Mode = StartupHandoffModes.Start,
            });

        internal void MakeIdle()
        {
            lock (_statusGate) _busy = false;
        }

        private ControlStatus Status()
        {
            bool busy;
            string? wireRunId;
            string? jobId;
            lock (_statusGate)
            {
                busy = _busy;
                wireRunId = _wireRunId;
                jobId = _jobId;
            }
            var status = new ControlStatus
            {
                TaskRunning = busy,
                TaskStatusAvailable = !busy || _afterFirstNode != StatusAfterFirstNode.Unknown,
                TaskStatusBgiEpoch = Epoch,
                TaskStatusObservedAtUtc = busy && _afterFirstNode == StatusAfterFirstNode.Stale
                    ? DateTimeOffset.UtcNow - ControlStatus.TaskStatusFreshnessWindow - TimeSpan.FromSeconds(1)
                    : DateTimeOffset.UtcNow,
                CurrentTaskName = busy ? "领取邮件" : null,
            };
            if (busy && wireRunId is not null)
                status.CurrentExecution = new TaskExecutionIdentitySnapshot(_executionId, 1,
                    _afterFirstNode == StatusAfterFirstNode.OtherRun ? _otherRunId : Guid.Parse(wireRunId),
                    jobId is null ? null : Guid.Parse(jobId), "OneDragon", "Ext", "领取邮件", false);
            return status;
        }

        private void RecordSubmit(WorkflowSubmitRequest request, string jobId)
        {
            Sends.Enqueue(request.Occurrence.NodeId);
            if (request.Occurrence.NodeId != "mail") return;
            lock (_statusGate)
            {
                _wireRunId = request.Run.WireRunId;
                _jobId = jobId;
                _busy = true;
            }
        }

        internal async Task CloseAsync()
        {
            MakeIdle();
            await Host.ShutdownAsync();
            _client.Dispose();
            try { Directory.Delete(Root, recursive: true); } catch { }
        }

        private sealed class Boundary(Fixture fixture) : IWorkflowExecutionBoundary
        {
            public bool SingleNativeSupported => true;
            public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
            {
                TerminalReleaseFixtureFacts.FreezeBody(request);
                var jobId = Guid.NewGuid().ToString("N");
                fixture.RecordSubmit(request, jobId);
                return Task.FromResult(BoundarySubmitResult.AcceptedWith(jobId));
            }
            public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
                => Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
            public Task RequestCancelAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
        }
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

    private static string PrerequisiteReference(WorkflowRunRecord run, WorkflowNodeOccurrence occurrence)
        => "taskcenter-localwait/run/" + Uri.EscapeDataString(run.RunId)
           + "/workflow/" + Uri.EscapeDataString(run.WorkflowId)
           + "/node/" + Uri.EscapeDataString(occurrence.NodeId)
           + "/occurrence/" + occurrence.Occurrence
           + "/loop/" + occurrence.LoopIteration;

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1500; i++)
        {
            if (condition()) return;
            await Task.Delay(10);
        }
        Assert.True(condition(), "continuous plan did not reach its expected state");
    }

    [Fact]
    public async Task Handoff_OwnPreviousNodeStillInStatus_WaitsThenAutomaticallyCompletesSecondNodeOnce()
    {
        var fixture = new Fixture(StatusAfterFirstNode.OwnRun);
        try
        {
            var accepted = await fixture.StartAsync();
            Assert.Equal(HandoffOutcome.Accepted, accepted.Outcome);
            Assert.NotNull(accepted.RunId);
            await WaitUntilAsync(() => !fixture.Host.IsDriving(fixture.WorkflowId)
                && fixture.Host.Runs.Load(accepted.RunId!)?.State == WorkflowRunState.LocalWaitParking);

            var parked = fixture.Host.Runs.Load(accepted.RunId!)!;
            Assert.Equal("close-stream", parked.Cursor?.NodeId);
            Assert.Equal(LocalWaitDecisionKind.Wait, parked.LocalWaitDecision?.Kind);
            Assert.True(parked.LocalWaitDecision?.NoSendConfirmed);
            var binding = Assert.IsType<LocalWaitBinding>(parked.LocalWaitDecision?.Binding);
            Assert.Equal(LocalWaitSourceKind.StartupHandoff, binding.SourceKind);
            Assert.Equal(accepted.RunId, binding.RunId);
            Assert.Equal("close-stream", binding.NodeId);
            Assert.Equal("bgi:local:42:99", binding.Scope);
            Assert.Equal(new[] { "mail" }, fixture.Sends.ToArray());
            var queued = Assert.Single(fixture.Host.LocalWaitQueue.Load());
            Assert.Equal(LocalWaitItemState.Waiting, queued.State);
            Assert.Equal(binding.ItemId, queued.ItemId);

            // No explicit resume or test reevaluation call: the real monitor must observe idle and
            // resume the original run at the second node instead of creating/replaying the first.
            fixture.MakeIdle();
            await WaitUntilAsync(() => fixture.Host.Runs.Load(accepted.RunId!)?.State == WorkflowRunState.Succeeded
                && !fixture.Host.IsDriving(fixture.WorkflowId));

            Assert.Equal(new[] { "mail", "close-stream" }, fixture.Sends.ToArray());
            var completed = fixture.Host.Runs.Load(accepted.RunId!)!;
            Assert.Equal(WorkflowRunState.Succeeded, completed.State);
            Assert.Single(completed.NodeOutcomes.Where(outcome => outcome.NodeId == "mail" && outcome.Result == "succeeded"));
            Assert.Single(completed.NodeOutcomes.Where(outcome => outcome.NodeId == "close-stream" && outcome.Result == "succeeded"));
            Assert.Equal(2, fixture.Host.Runs.List().Count);
            Assert.Equal(WorkflowRunState.Unknown, fixture.Host.Runs.Load(fixture.OldUnknownRunId)!.State);
            Assert.Equal(fixture.OldUnknownBytes, File.ReadAllBytes(fixture.OldUnknownPath));
            Assert.All(fixture.Host.LocalWaitQueue.Load(), item => Assert.Equal(LocalWaitItemState.Cancelled, item.State));
            // The resumed drive may finish before its admission call returns and writes this diagnostic.
            await WaitUntilAsync(() => fixture.Logs.Any(line => line.Contains("已按优先级自动接续等待运行", StringComparison.Ordinal)));
        }
        finally { await fixture.CloseAsync(); }
    }

    [Theory]
    [InlineData("Unknown")]
    [InlineData("Stale")]
    [InlineData("OtherRun")]
    public async Task Handoff_UntrustedOrOtherOccupant_HoldsSecondNodeWithoutSending(string statusAfterFirst)
    {
        var fixture = new Fixture(Enum.Parse<StatusAfterFirstNode>(statusAfterFirst));
        try
        {
            var accepted = await fixture.StartAsync();
            Assert.Equal(HandoffOutcome.Accepted, accepted.Outcome);
            await WaitUntilAsync(() => !fixture.Host.IsDriving(fixture.WorkflowId)
                && fixture.Host.Runs.Load(accepted.RunId!)?.State == WorkflowRunState.LocalWaitParking);

            var parked = fixture.Host.Runs.Load(accepted.RunId!)!;
            Assert.Equal("close-stream", parked.Cursor?.NodeId);
            Assert.Equal(LocalWaitDecisionKind.Hold, parked.LocalWaitDecision?.Kind);
            Assert.Null(parked.LocalWaitDecision?.Binding);
            Assert.Equal(SubmitIntentState.LocalWaitDeferred, parked.CurrentSubmission?.Intent);
            Assert.False(parked.CurrentSubmission!.SendAttempted);
            Assert.Equal(new[] { "mail" }, fixture.Sends.ToArray());
            Assert.Empty(fixture.Host.LocalWaitQueue.Load());

            // Hold has no trusted binding and must not turn into automatic execution just because a
            // later status says idle. Only the own-run zero-send Wait is an accepted continuation.
            fixture.MakeIdle();
            await fixture.Host.ReevaluateLocalWaitForTestAsync(LocalWaitReevaluationTriggerPoint.OccupancyEnded);
            Assert.Equal(new[] { "mail" }, fixture.Sends.ToArray());
            Assert.Equal(WorkflowRunState.LocalWaitParking, fixture.Host.Runs.Load(accepted.RunId!)!.State);
        }
        finally { await fixture.CloseAsync(); }
    }
}
