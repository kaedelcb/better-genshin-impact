using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class OnlineHoeingContinuationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "online-plan-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    [Fact]
    public void FailedPlanResumeWithoutWaiter_DoesNotCreateUnobservedFaultedTask()
    {
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null));
        var run = host.Runs.CreateRun("flow-B", "rev-1");
        run.State = WorkflowRunState.Running;
        run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord { Phase = "suspended", Ticket = "ticket-7" };
        host.Runs.Update(run);

        host.FailPlanResume(run.RunId, "计划表停止终态未确认，不启动完成后指定任务");

        var signals = typeof(TaskCenterHost).GetField("_onlineResumeSignals",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host)!;
        var entry = ((System.Collections.IEnumerable)signals).Cast<object>().Single();
        var completion = entry.GetType().GetProperty("Value")!.GetValue(entry)!;
        var task = (Task)completion.GetType().GetProperty("Task")!.GetValue(completion)!;
        Assert.True(task.IsCompletedSuccessfully);
        Assert.Equal(WorkflowRunState.Unknown, host.Runs.Load(run.RunId)!.State);
    }

    [Fact]
    public async Task ManualResetGate_BlocksOldBatchReopenUntilRunCleanupCompletes()
    {
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null));
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(5));
        await host.BeginManualResetAsync(budget.Token);
        Assert.False(host.CanOpenOnlineSendAfterBatch);

        var result = await host.ForceAbandonOnlineHoeingForManualResetAsync("测试人工重置", openSendGate: true);

        Assert.True(result.SendGateOpened);
        Assert.True(host.CanOpenOnlineSendAfterBatch);
    }

    private sealed class ResumePort : IBgiExecutionPort
    {
        public BgiJobInfo? Resumed { get; set; }
        public BgiJobInfo? PendingExit { get; set; }
        public int PendingExitQueries { get; set; }
        public bool NotFound { get; set; }
        private int _queryCount;
        public int QueryCount => Volatile.Read(ref _queryCount);
        public string? LastQueryJobId { get; private set; }
        public bool IsReady => true;
        public bool HasCapability(string name) => true;
        public BgiEpoch? ServerEpoch => new() { ProcessId = 42, StartTicksUtc = 99 };
        public Task<BgiExternalResponse> SendCommandAsync(string operation, object? payload, CancellationToken ct)
            => throw new InvalidOperationException("恢复观察不可再次发送");
        public Task<BgiJobListSnapshot?> QueryJobListAsync(CancellationToken ct)
            => Task.FromResult<BgiJobListSnapshot?>(null);
        public Task<(string? Status, BgiJobInfo? Job)> QueryJobStatusAsync(string jobId, CancellationToken ct)
        {
            var query = Interlocked.Increment(ref _queryCount);
            LastQueryJobId = jobId;
            if (NotFound) return Task.FromResult<(string?, BgiJobInfo?)>(("not_found", null));
            var observed = PendingExit is not null && query <= PendingExitQueries ? PendingExit : Resumed;
            return Task.FromResult((observed?.State, observed));
        }
        public Task CancelOwnedTaskAsync(string jobId, CancellationToken ct) => Task.CompletedTask;
    }

    private sealed class PlanBoundary : IWorkflowExecutionBoundary
    {
        private readonly RunStore _runs;
        public PlanBoundary(RunStore runs) => _runs = runs;
        public readonly List<string> Submitted = [];
        public readonly List<(string NodeId, string? Revision)> Revisions = [];
        public bool SingleNativeSupported => false;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            Submitted.Add(request.Occurrence.NodeId);
            Revisions.Add((request.Occurrence.NodeId, request.Node.Ref?.Revision));
            TerminalReleaseFixtureFacts.FreezeBody(request);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("job-" + Submitted.Count));
        }
        public Task<BoundaryTerminalResult> AwaitTerminalAsync(string jobId, CancellationToken ct)
        {
            if (jobId != "job-2") return Task.FromResult(BoundaryTerminalResult.Observed("succeeded"));
            // 模拟宿主在 Runner 等待期间另行落盘接管意图；Runner 的内存副本保持旧值。
            var saved = _runs.List().Single(r => r.CurrentSubmission?.JobId == jobId);
            saved.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
            {
                Generation = 7, Ticket = "ticket-7", Epoch = "42:99", NodeId = "B",
                Occurrence = saved.CurrentSubmission!.Occurrence,
                LoopIteration = saved.CurrentSubmission.LoopIteration,
                Attempt = saved.CurrentSubmission.Attempt,
                OriginalSubmissionKey = saved.CurrentSubmission.Key,
                OriginalJobId = jobId, Phase = "suspended",
            };
            _runs.Update(saved);
            return Task.FromResult(BoundaryTerminalResult.Observed("cancelled", errorCode: "preempted"));
        }
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
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action,
            WorkflowRunRecord run, CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("unused"));
    }

    [Fact]
    public async Task PrepareSuspend_BindsExactActivePlanJobAndRejectsDifferentJob()
    {
        var flows = Path.Combine(_root, "flows");
        var runs = Path.Combine(_root, "runs");
        var workflow = new WorkflowStore(flows);
        var document = new WorkflowDocument
        {
            Name = "计划B",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes = [new WorkflowNode
            {
                NodeId = "B", Kind = "resource.configGroup",
                Ref = new WorkflowResourceRef { Config = "配置组B", ConfigKey = "B#k", Revision = "rev-1" },
            }],
        };
        workflow.Save(document, null);
        var host = new TaskCenterHost(flows, runs, Path.Combine(_root, "catalog.json"),
            () => null, null, null, () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
        var run = host.Runs.CreateRun(document.WorkflowId!, workflow.LoadSnapshot(document.WorkflowId!).Revision);
        var original = Guid.NewGuid();
        run.State = WorkflowRunState.Running;
        run.Cursor = new WorkflowNodeCursor { NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        run.CurrentSubmission = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "B", 0, 0, 1),
            NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1,
            JobId = original.ToString("D"), Intent = SubmitIntentState.Accepted,
        };
        host.Runs.Update(run);
        await host.CloseOnlineHoeingSendAsync();
        var active = new CommandExecutor.ExecutionIdentity(Guid.NewGuid(), 42, 99,
            Guid.Parse(run.WireRunId), original);
        var prepared = host.PrepareOnlineHoeing(7,
            new CommandExecutor.ExecutionBoundarySnapshot(true, true, 42, 99, active));
        Assert.True(prepared.Ready);
        Assert.Equal(run.RunId, prepared.ManagedRunId);
        Assert.False(host.ConfirmOnlineHoeingSuspend(7, "ticket-7",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "suspended", workflowRunId = run.WireRunId,
                nodeId = "B", originalJobId = Guid.NewGuid(),
                occurrence = 0, iteration = 0, attempt = 1,
            })));
        Assert.True(host.ConfirmOnlineHoeingSuspend(7, "ticket-7",
            System.Text.Json.JsonSerializer.Serialize(new
            {
                status = "suspended", workflowRunId = run.WireRunId,
                nodeId = "B", originalJobId = original,
                occurrence = 0, iteration = 0, attempt = 1,
                planCheckpointPhase = "inside", planNextTaskIndex = 2,
            })));
        var saved = host.Runs.Load(run.RunId)!.OnlineHoeingHandoff!;
        Assert.Equal("suspended", saved.Phase);
        Assert.Equal("ticket-7", saved.Ticket);
        Assert.Equal(2, saved.NextTaskIndex);
    }

    [Fact]
    public async Task PreemptedBJob_BindsOneResumeJobWithoutOverwritingOriginal()
    {
        var flows = Path.Combine(_root, "flows");
        var runs = Path.Combine(_root, "runs");
        var workflow = new WorkflowStore(flows);
        var document = new WorkflowDocument
        {
            Name = "B 节点续跑",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes = [new WorkflowNode
            {
                NodeId = "B", Kind = "resource.configGroup",
                Ref = new WorkflowResourceRef { Config = "配置组B", ConfigKey = "B#k", Revision = "rev-1" },
            }],
        };
        workflow.Save(document, null);
        var host = new TaskCenterHost(flows, runs, Path.Combine(_root, "catalog.json"),
            () => null, null, null, () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
        var run = host.Runs.CreateRun(document.WorkflowId!, workflow.LoadSnapshot(document.WorkflowId!).Revision);
        var oldJob = Guid.NewGuid().ToString("D");
        var oldKey = RunStore.DeriveSubmissionKey(run.RunId, "B", 0, 0, 1);
        run.State = WorkflowRunState.Running;
        run.Cursor = new WorkflowNodeCursor { NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        run.CurrentSubmission = new WorkflowSubmission
        {
            Key = oldKey, NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1,
            Intent = SubmitIntentState.Accepted, JobId = oldJob, SendAttempted = true,
            Epoch = "42:99", WireRunId = run.WireRunId,
            Fingerprint = "original-fingerprint", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
            RecordedAt = DateTimeOffset.UtcNow,
        };
        run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Generation = 7, Ticket = "ticket-7", Epoch = "42:99", NodeId = "B",
            Occurrence = 0, LoopIteration = 0, Attempt = 1,
            OriginalJobId = oldJob, OriginalSubmissionKey = oldKey, Phase = "suspended",
        };
        host.Runs.Update(run);
        var port = new ResumePort();
        var boundary = new BgiWorkflowExecutionBoundary(port, host.Runs);
        var continuing = host.AwaitOnlineHoeingContinuationAsync(boundary, run, run.CurrentSubmission,
            BoundaryTerminalResult.Observed("cancelled", errorCode: "preempted"), CancellationToken.None);

        for (var i = 0; i < 40 && host.Runs.Load(run.RunId)?.State != WorkflowRunState.OnlineHoeingParking; i++)
            await Task.Delay(25);
        Assert.Equal(WorkflowRunState.OnlineHoeingParking, host.Runs.Load(run.RunId)!.State);
        var plan = await host.PreparePlanResumeAsync(run.RunId, "ticket-7");
        Assert.NotNull(plan);
        var resumedJob = Guid.NewGuid().ToString("D");
        port.Resumed = new BgiJobInfo
        {
            Epoch = new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 },
            JobId = Guid.Parse(resumedJob).ToString("N"), State = "succeeded", Source = "Resume",
            WorkflowRunId = run.WireRunId, NodeId = "B", Occurrence = 0,
            Iteration = 0, Attempt = 2, IdempotencyKey = (string)plan!["idempotencyKey"]!,
            ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited",
        };
        Assert.True(host.AcceptPlanResumeJob(run.RunId, "ticket-7", resumedJob));
        Assert.True(host.AcceptPlanResumeJob(run.RunId, "ticket-7", resumedJob));
        Assert.False(host.AcceptPlanResumeJob(run.RunId, "ticket-7", Guid.NewGuid().ToString("D")));
        var terminal = await continuing.WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal("succeeded", terminal!.Terminal);
        var saved = host.Runs.Load(run.RunId)!;
        Assert.Single(saved.SubmissionHistory);
        Assert.Equal(oldJob, saved.SubmissionHistory[0].JobId);
        Assert.Equal("cancelled", saved.SubmissionHistory[0].ObservedTerminal);
        Assert.Equal(resumedJob, saved.CurrentSubmission!.JobId);
        Assert.Equal(2, saved.Cursor!.Attempt);
        Assert.Single(saved.NodeOutcomes.Where(o => o.Reason == "online_hoeing_preempted"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task PlanA_BF_G_C_ContinuesCOnlyAfterResumedBJobSucceeds(bool resumedJobNotFound)
    {
        var flows = Path.Combine(_root, "flows");
        var runs = Path.Combine(_root, "runs");
        var workflow = new WorkflowStore(flows);
        var document = new WorkflowDocument
        {
            Name = "ABC",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes = new[] { "A", "B", "C" }.Select(id => new WorkflowNode
            {
                NodeId = id, Kind = "resource.configGroup",
                Ref = new WorkflowResourceRef { Config = "配置" + id, ConfigKey = id + "#k", Revision = "rev-1" },
            }).ToList(),
        };
        workflow.Save(document, null);
        var host = new TaskCenterHost(flows, runs, Path.Combine(_root, "catalog.json"),
            () => null, null, null, () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
        var port = new ResumePort();
        var resumeBoundary = new BgiWorkflowExecutionBoundary(port, host.Runs);
        var body = new PlanBoundary(host.Runs);
        var runner = new WorkflowRunner(workflow, host.Runs, body, new NoopPrerequisite(), new NoopTerminal(),
            new WorkflowRunnerOptions
            {
                OnlineHoeingContinuation = (run, sub, terminal, ct)
                    => host.AwaitOnlineHoeingContinuationAsync(resumeBoundary, run, sub, terminal, ct),
                ResolveResourceRevisionAsync = (node, _) =>
                    Task.FromResult(node.NodeId == "B" ? "rev-current" : "rev-1"),
            });
        var driving = runner.StartAsync(document.WorkflowId!);
        WorkflowRunRecord? parked = null;
        for (var i = 0; i < 80; i++)
        {
            parked = host.Runs.List().FirstOrDefault(r => r.State == WorkflowRunState.OnlineHoeingParking);
            if (parked is not null) break;
            await Task.Delay(25);
        }
        Assert.True(parked is not null,
            "submitted=" + string.Join(",", body.Submitted)
            + " states=" + string.Join(",", host.Runs.List().Select(r => r.State + "/"
                + (r.OnlineHoeingHandoff?.Phase ?? "none") + "/" + r.Note)));
        Assert.Equal(new[] { "A", "B" }, body.Submitted);
        var request = await host.PreparePlanResumeAsync(parked!.RunId, "ticket-7");
        Assert.NotNull(request);
        var resumedJob = Guid.NewGuid().ToString("D");
        var resumedJobWire = Guid.Parse(resumedJob).ToString("N");
        port.PendingExit = new BgiJobInfo
        {
            Epoch = new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 },
            JobId = resumedJobWire, State = "succeeded", Source = "Resume",
            WorkflowRunId = parked.WireRunId, NodeId = "B", Occurrence = 0,
            Iteration = 0, Attempt = 2, IdempotencyKey = (string)request!["idempotencyKey"]!,
            ExecutionExitConfirmed = false,
        };
        port.PendingExitQueries = 1;
        port.Resumed = new BgiJobInfo
        {
            Epoch = new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 },
            JobId = resumedJobWire, State = "succeeded", Source = "Resume",
            WorkflowRunId = parked.WireRunId, NodeId = "B", Occurrence = 0,
            Iteration = 0, Attempt = 2, IdempotencyKey = (string)request!["idempotencyKey"]!,
            ExecutionExitConfirmed = true, ExecutionExitDisposition = "execution_exited",
        };
        port.NotFound = resumedJobNotFound;
        Assert.True(host.AcceptPlanResumeJob(parked.RunId, "ticket-7", resumedJob));
        var finished = await driving.WaitAsync(TimeSpan.FromSeconds(3));
        if (resumedJobNotFound)
        {
            Assert.Equal(WorkflowRunState.Unknown, finished.State);
            Assert.Equal(new[] { "A", "B" }, body.Submitted);
            Assert.Equal("observing", finished.OnlineHoeingHandoff?.Phase);
            Assert.Contains("作业不存在", finished.NodeOutcomes.Last().Reason);
            return;
        }
        Assert.Equal(WorkflowRunState.Succeeded, finished.State);
        Assert.Equal(new[] { "A", "B", "C" }, body.Submitted);
        Assert.Equal(new[] { ("A", "rev-1"), ("B", "rev-current"), ("C", "rev-1") }, body.Revisions);
        Assert.True(port.QueryCount >= 2, "必须等待恢复作业的执行退出凭证，而非仅看到业务终态");
        Assert.Equal(resumedJobWire, port.LastQueryJobId);
        Assert.Single(finished.OnlineHoeingHistory);
        Assert.Null(finished.OnlineHoeingHistory[0].Ticket);
        Assert.NotNull(finished.OnlineHoeingHistory[0].TicketHash);
        Assert.True(TerminalReleaseEvidence.RunSettled(finished));
    }

    [Fact]
    public async Task BetweenBAndC_NoBgiContext_ResumesCWithoutReplayingB()
    {
        var flows = Path.Combine(_root, "flows");
        var runs = Path.Combine(_root, "runs");
        var workflow = new WorkflowStore(flows);
        var document = new WorkflowDocument
        {
            Name = "ABC 节点空隙",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes = new[] { "A", "B", "C" }.Select(id => new WorkflowNode
            {
                NodeId = id, Kind = "resource.configGroup",
                Ref = new WorkflowResourceRef { Config = "配置" + id, ConfigKey = id + "#k", Revision = "rev-1" },
            }).ToList(),
        };
        workflow.Save(document, null);
        var body = new PlanBoundary(new RunStore(runs));
        var host = new TaskCenterHost(flows, runs, Path.Combine(_root, "catalog.json"),
            () => null, null,
            (_, w, r) => new WorkflowRunner(w, r, body, new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
        var run = host.Runs.CreateRun(document.WorkflowId!, workflow.LoadSnapshot(document.WorkflowId!).Revision);
        run.State = WorkflowRunState.LocalWaitParking;
        run.TriggerConsumed = true;
        run.Cursor = new WorkflowNodeCursor { NodeId = "C", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        run.OnlineHoeingGateParked = true;
        run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Generation = 7, Ticket = "ticket-7", Epoch = "42:99", NodeId = "C",
            Occurrence = 0, LoopIteration = 0, Attempt = 1, Phase = "gap",
        };
        foreach (var (id, index) in new[] { ("A", 0), ("B", 1) })
            run.NodeOutcomes.Add(new WorkflowNodeOutcome
            {
                NodeId = id, SequenceIndex = index, Occurrence = 0, LoopIteration = 0,
                Result = "succeeded",
            });
        host.Runs.Update(run);
        await host.CloseOnlineHoeingSendAsync();
        var released = await host.FinishOnlineGapAfterReleaseAsync(run.RunId, "ticket-7");
        Assert.True(released, "state=" + host.Runs.Load(run.RunId)?.State
            + " note=" + host.Runs.Load(run.RunId)?.Note
            + " handoff=" + host.Runs.Load(run.RunId)?.OnlineHoeingHandoff?.Phase);
        WorkflowRunRecord? finished = null;
        for (var i = 0; i < 80; i++)
        {
            finished = host.Runs.Load(run.RunId);
            if (finished?.State == WorkflowRunState.Succeeded) break;
            await Task.Delay(25);
        }
        Assert.Equal(WorkflowRunState.Succeeded, finished!.State);
        Assert.Equal(new[] { "C" }, body.Submitted);
        Assert.Null(finished.OnlineHoeingHandoff);
        Assert.Single(finished.OnlineHoeingHistory);
    }

    [Fact]
    public void ExplicitStop_PreservesCancelledOriginalAndNeverCreatesResumeJob()
    {
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null));
        var run = host.Runs.CreateRun("flow-B", "rev-1");
        var old = Guid.NewGuid().ToString("D");
        run.State = WorkflowRunState.Cancelled;
        run.StopRequested = true;
        run.CurrentSubmission = new WorkflowSubmission
        {
            Key = "old-key", NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1,
            JobId = old, Intent = SubmitIntentState.Accepted, SendAttempted = true,
            Epoch = "42:99", WireRunId = run.WireRunId,
            ObservedTerminal = "cancelled", ExecutionExitConfirmed = true,
            ExecutionExitDisposition = "execution_exited", EffectState = "cancelled",
            Fingerprint = "original", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
        };
        run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Generation = 7, Ticket = "ticket-7", Epoch = "42:99", NodeId = "B",
            OriginalJobId = old, OriginalSubmissionKey = "old-key", Phase = "parked",
        };
        host.Runs.Update(run);
        Assert.True(host.FinalizeStoppedOnlineHandoff(run.RunId));
        var saved = host.Runs.Load(run.RunId)!;
        Assert.Null(saved.OnlineHoeingHandoff);
        Assert.Single(saved.OnlineHoeingHistory);
        Assert.Null(saved.OnlineHoeingHistory[0].ResumeJobId);
    }

    [Fact]
    public void AssistantRestart_UnsettledOnlineHandoffBecomesUnknownWithoutResend()
    {
        var runs = Path.Combine(_root, "runs");
        var store = new RunStore(runs);
        var run = store.CreateRun("flow-B", "rev-1");
        run.State = WorkflowRunState.OnlineHoeingParking;
        run.Cursor = new WorkflowNodeCursor { NodeId = "B", Attempt = 1 };
        run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Generation = 7, Ticket = "ticket-7", Epoch = "42:99", NodeId = "B",
            OriginalJobId = Guid.NewGuid().ToString("D"), Phase = "resumeRequested",
        };
        store.Update(run);
        var recovered = new RunStore(runs).RecoverOnStart().Single(r => r.RunId == run.RunId);
        Assert.Equal(WorkflowRunState.Unknown, recovered.State);
        Assert.Equal("resumeRequested", recovered.OnlineHoeingHandoff!.Phase);
        Assert.False(recovered.StopRequested);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task RestartAfterParkedOnlineHandoff_AutomaticallySettlesOldRunBeforeNextBatch(
        bool recoveryAlreadyCompleted)
    {
        var runs = Path.Combine(_root, "runs");
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), runs,
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 100 },
            onlineHandoffProcessAlive: (pid, ticks) =>
            {
                Assert.Equal(41, pid);
                Assert.Equal(99, ticks);
                return false;
            });
        if (recoveryAlreadyCompleted) host.EnsureRecovered();
        var old = host.Runs.CreateRun("daily-plan", "rev-1");
        var job = Guid.NewGuid().ToString("D");
        var key = RunStore.DeriveSubmissionKey(old.RunId, "B", 0, 0, 1);
        old.State = WorkflowRunState.OnlineHoeingParking;
        old.Cursor = new WorkflowNodeCursor { NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        old.CurrentSubmission = new WorkflowSubmission
        {
            Key = key, NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1,
            JobId = job, Intent = SubmitIntentState.Accepted, SendAttempted = true,
            AcceptedSendIdentity = "sub:" + Guid.NewGuid().ToString("N") + ":1",
            Epoch = "41:99", WireRunId = old.WireRunId, Fingerprint = "original",
            ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
            ObservedTerminal = "cancelled", ExecutionExitConfirmed = true,
            ExecutionExitDisposition = "execution_exited", EffectState = "cancelled",
        };
        old.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Generation = 7, Ticket = "ticket-7", Epoch = "41:99", NodeId = "B",
            Occurrence = 0, LoopIteration = 0, Attempt = 1,
            OriginalJobId = job, OriginalSubmissionKey = key, Phase = "parked",
        };
        host.Runs.Update(old);

        var recovery = await host.RecoverOnlineHandoffsBeforeStartAsync();

        var settled = host.Runs.Load(old.RunId)!;
        Assert.Equal(1, recovery.SettledRuns);
        Assert.Equal(0, recovery.RemainingHandoffs);
        Assert.Equal(WorkflowRunState.Cancelled, settled.State);
        Assert.True(settled.StopRequested);
        Assert.Null(settled.OnlineHoeingHandoff);
        Assert.Single(settled.OnlineHoeingHistory);
        Assert.Equal("stopped", settled.OnlineHoeingHistory[0].Phase);
        Assert.Null(settled.OnlineHoeingHistory[0].Ticket);
        Assert.NotNull(settled.OnlineHoeingHistory[0].TicketHash);
        Assert.Equal("cancelled", settled.CurrentSubmission?.ObservedTerminal);
        Assert.True(TerminalReleaseEvidence.RunSettled(settled));
        Assert.False(host.HasUnsettledOnlineHoeingHandoff);
        var bytes = File.ReadAllBytes(Path.Combine(runs, old.RunId + ".run.json"));
        await host.RecoverOnlineHandoffsBeforeStartAsync();
        Assert.Equal(bytes, File.ReadAllBytes(Path.Combine(runs, old.RunId + ".run.json")));

        await host.CloseOnlineHoeingSendAsync();
        var next = host.PrepareOnlineHoeing(8,
            new CommandExecutor.ExecutionBoundarySnapshot(true, false, 42, 100, null));
        Assert.True(next.Ready, next.Reason);
    }

    [Fact]
    public async Task DeadOldEpochWithUnconfirmedResume_PreservesUnknownAndOriginalEvidence()
    {
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 100 },
            onlineHandoffProcessAlive: (_, _) => false);
        var old = host.Runs.CreateRun("daily-plan", "rev-1");
        old.State = WorkflowRunState.OnlineHoeingParking;
        old.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Epoch = "41:99", Phase = "resumeRequested", Ticket = "original-ticket",
            ResumeJobId = Guid.NewGuid().ToString("D"),
        };
        host.Runs.Update(old);

        var recovery = await host.RecoverOnlineHandoffsBeforeStartAsync();

        var unresolved = host.Runs.Load(old.RunId)!;
        Assert.Equal(0, recovery.SettledRuns);
        Assert.Equal(1, recovery.RemainingHandoffs);
        Assert.Equal(WorkflowRunState.Unknown, unresolved.State);
        Assert.Equal("resumeRequested", unresolved.OnlineHoeingHandoff?.Phase);
        Assert.Equal("original-ticket", unresolved.OnlineHoeingHandoff?.Ticket);
        Assert.False(unresolved.StopRequested);
    }

    [Fact]
    public async Task DeadOldUnknownHandoff_DoesNotBlockColdBgiStart()
    {
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null),
            onlineEpochProvider: () => null,
            onlineHandoffProcessAlive: (_, _) => false);
        var old = host.Runs.CreateRun("daily-plan", "rev-1");
        old.State = WorkflowRunState.OnlineHoeingParking;
        old.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Epoch = "41:99", Phase = "resumeRequested", Ticket = "original-ticket",
            ResumeJobId = Guid.NewGuid().ToString("D"),
        };
        host.Runs.Update(old);

        await host.RecoverOnlineHandoffsBeforeStartAsync();

        Assert.Equal(WorkflowRunState.Unknown, host.Runs.Load(old.RunId)!.State);
        Assert.False(host.HasUnsettledOnlineHoeingHandoff);
        Assert.True(host.CanOpenOnlineSendAfterBatch);
    }

    [Fact]
    public async Task LivingOriginalProcess_IsNeverAutoSettled()
    {
        var runs = Path.Combine(_root, "runs");
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), runs,
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 100 },
            onlineHandoffProcessAlive: (_, _) => true);
        var old = host.Runs.CreateRun("daily-plan", "rev-1");
        old.State = WorkflowRunState.OnlineHoeingParking;
        old.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Epoch = "41:99", Phase = "parked", Ticket = "original-ticket",
        };
        host.Runs.Update(old);
        var path = Path.Combine(runs, old.RunId + ".run.json");
        var before = File.ReadAllBytes(path);

        var recovery = await host.RecoverOnlineHandoffsBeforeStartAsync();

        Assert.Equal(before, File.ReadAllBytes(path));
        Assert.Equal(0, recovery.SettledRuns);
        Assert.Equal(1, recovery.RemainingHandoffs);
        Assert.True(host.HasUnsettledOnlineHoeingHandoff);
    }

    [Fact]
    public async Task RemoteGroup_ReconcilesOldOnlineRunBeforeBatchBusyGuard()
    {
        var busy = true;
        var recovered = 0;
        var admitted = 0;
        var executor = new CommandExecutor(null!, "unused",
            isBatchInFlight: () => busy,
            externalStartAdmission: (_, _) =>
            {
                admitted++;
                return Task.FromResult(new ExternalStartAdmissionOutcome(
                    ExternalStartAdmissionStatus.Rejected, "test_rejected", "准入测试拒绝"));
            },
            recoverOnlineHandoff: () =>
            {
                recovered++;
                busy = false;
                return Task.CompletedTask;
            });

        var result = await executor.ExecuteAsync(new RemoteCommand
        {
            Cmd = "start_group",
            Params = new Dictionary<string, object> { ["groupName"] = "联机-传奇-锄地" },
        });

        Assert.Equal(1, recovered);
        Assert.Equal(1, admitted);
        Assert.Equal("test_rejected", result.ErrorCode);
    }

    [Fact]
    public void StopRacingResumeReceipt_PersistsLateJobForCancellationInsteadOfLosingIt()
    {
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null));
        var run = host.Runs.CreateRun("flow-B", "rev-1");
        run.State = WorkflowRunState.OnlineHoeingParking;
        run.StopRequested = true;
        run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Ticket = "ticket-7", Phase = "resumeRequested", ResumeKey = "resume-key",
        };
        host.Runs.Update(run);
        var lateJob = Guid.NewGuid().ToString("D");
        Assert.False(host.AcceptPlanResumeJob(run.RunId, "ticket-7", lateJob));
        var saved = host.Runs.Load(run.RunId)!.OnlineHoeingHandoff!;
        Assert.Equal("stopPending", saved.Phase);
        Assert.Equal(lateJob, saved.ResumeJobId);
    }

    [Fact]
    public void ParkedPlanUi_ShowsWaitingAndStopWithoutGenericResume()
    {
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null));
        var run = host.Runs.CreateRun("flow-B", "rev-1");
        run.State = WorkflowRunState.OnlineHoeingParking;
        run.Cursor = new WorkflowNodeCursor { NodeId = "B", Attempt = 1 };
        run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord { Ticket = "ticket-7", Phase = "parked" };
        var vm = ActiveRunVm.Build(run, host);
        Assert.Contains("联机锄地", vm.StateText);
        Assert.True(vm.CanStop);
        Assert.False(vm.CanResume);
        Assert.True(vm.BlocksNewStart);
    }

    [Fact]
    public async Task GapCursorChanged_DoesNotRetryOrClaimPlanSuccess()
    {
        var flows = Path.Combine(_root, "flows");
        var runs = Path.Combine(_root, "runs");
        var workflow = new WorkflowStore(flows);
        var document = new WorkflowDocument
        {
            Name = "空隙游标失效",
            Activation = new WorkflowActivation { Status = "active" },
            Nodes = [new WorkflowNode
            {
                NodeId = "C", Kind = "resource.configGroup",
                Ref = new WorkflowResourceRef { Config = "配置C", ConfigKey = "C#k", Revision = "rev-1" },
            }],
        };
        workflow.Save(document, null);
        var host = new TaskCenterHost(flows, runs, Path.Combine(_root, "catalog.json"),
            () => null, null, null, () => (true, null));
        var run = host.Runs.CreateRun(document.WorkflowId!, workflow.LoadSnapshot(document.WorkflowId!).Revision);
        run.State = WorkflowRunState.LocalWaitParking;
        run.Cursor = new WorkflowNodeCursor { NodeId = "removed", Attempt = 1 };
        run.OnlineHoeingGateParked = true;
        run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Generation = 7, Ticket = "ticket-7", Epoch = "42:99", NodeId = "removed", Phase = "gap",
        };
        host.Runs.Update(run);
        await host.CloseOnlineHoeingSendAsync();
        Assert.False(await host.FinishOnlineGapAfterReleaseAsync(run.RunId, "ticket-7"));
        Assert.Equal(WorkflowRunState.Unknown, host.Runs.Load(run.RunId)!.State);
    }

    [Fact]
    public void PendingUnknownHandoff_KeepsPlanSendGateClosedAcrossAnotherBatch()
    {
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null));
        var run = host.Runs.CreateRun("flow-B", "rev-1");
        run.State = WorkflowRunState.Unknown;
        run.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Ticket = "ticket-7", Phase = "unknown",
        };
        host.Runs.Update(run);
        Assert.True(host.HasUnsettledOnlineHoeingHandoff);
        Assert.False(host.CanOpenOnlineSendAfterBatch);
        run.OnlineHoeingHandoff.Phase = "resumeAccepted";
        host.Runs.Update(run);
        Assert.True(host.CanOpenOnlineSendAfterBatch);
    }

    [Fact]
    public async Task DeadOldEpochUnknownHandoff_DoesNotBlockNewPlanBatchOrChangeOldRecord()
    {
        var runs = Path.Combine(_root, "runs");
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), runs,
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 100 },
            onlineHandoffProcessAlive: (pid, ticks) =>
            {
                Assert.Equal(41, pid);
                Assert.Equal(99, ticks);
                return false;
            });
        var old = host.Runs.CreateRun("same-flow", "rev-1");
        old.State = WorkflowRunState.Unknown;
        old.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Epoch = "41:99", Phase = "unknown", ResumeJobId = Guid.NewGuid().ToString("D"),
        };
        host.Runs.Update(old);
        var oldFile = Path.Combine(runs, old.RunId + ".run.json");
        var oldBytes = File.ReadAllBytes(oldFile);

        var current = host.Runs.CreateRun("same-flow", "rev-1");
        var jobId = Guid.NewGuid();
        current.State = WorkflowRunState.Running;
        current.Cursor = new WorkflowNodeCursor { NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        current.CurrentSubmission = new WorkflowSubmission
        {
            Key = RunStore.DeriveSubmissionKey(current.RunId, "B", 0, 0, 1),
            NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1,
            JobId = jobId.ToString("D"), Intent = SubmitIntentState.Accepted,
        };
        host.Runs.Update(current);
        Assert.False(host.HasUnsettledOnlineHoeingHandoff);
        Assert.True(host.CanOpenOnlineSendAfterBatch);

        await host.CloseOnlineHoeingSendAsync();
        var active = new CommandExecutor.ExecutionIdentity(Guid.NewGuid(), 42, 100,
            Guid.Parse(current.WireRunId), jobId);
        var prepared = host.PrepareOnlineHoeing(8,
            new CommandExecutor.ExecutionBoundarySnapshot(true, true, 42, 100, active));
        Assert.True(prepared.Ready, prepared.Reason);
        Assert.Equal(current.RunId, prepared.ManagedRunId);
        Assert.Equal("prepared", host.Runs.Load(current.RunId)!.OnlineHoeingHandoff?.Phase);
        Assert.True(host.HasUnsettledOnlineHoeingHandoff);
        Assert.False(host.CanOpenOnlineSendAfterBatch);
        Assert.Equal(oldBytes, File.ReadAllBytes(oldFile));
    }

    [Theory]
    [InlineData("41:99", true, WorkflowRunState.Unknown)]
    [InlineData("41:99", null, WorkflowRunState.Unknown)]
    [InlineData("42:100", false, WorkflowRunState.Unknown)]
    [InlineData("invalid", false, WorkflowRunState.Unknown)]
    [InlineData("41:99", false, WorkflowRunState.OnlineHoeingParking)]
    public async Task UnprovenOldHandoff_StillBlocksNewBatch(
        string oldEpoch, bool? oldProcessAlive, WorkflowRunState oldState)
    {
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 100 },
            onlineHandoffProcessAlive: (_, _) => oldProcessAlive);
        var old = host.Runs.CreateRun("flow-old", "rev-1");
        old.State = oldState;
        old.OnlineHoeingHandoff = new OnlineHoeingHandoffRecord
        {
            Epoch = oldEpoch, Phase = "unknown", ResumeJobId = Guid.NewGuid().ToString("D"),
        };
        host.Runs.Update(old);
        Assert.True(host.HasUnsettledOnlineHoeingHandoff);
        Assert.False(host.CanOpenOnlineSendAfterBatch);
        await host.CloseOnlineHoeingSendAsync();
        var prepared = host.PrepareOnlineHoeing(8,
            new CommandExecutor.ExecutionBoundarySnapshot(true, false, 42, 100, null));
        Assert.False(prepared.Ready);
        Assert.Contains("上一轮计划接管", prepared.Reason);
        Assert.Equal("unknown", host.Runs.Load(old.RunId)!.OnlineHoeingHandoff?.Phase);
    }

    [Fact]
    public void OriginalProcessProbe_DistinguishesLiveProcessFromReusedPid()
    {
        using var self = System.Diagnostics.Process.GetCurrentProcess();
        var ticks = self.StartTime.ToUniversalTime().Ticks;
        Assert.True(TaskCenterHost.ProbeOriginalBgiProcess(self.Id, ticks));
        Assert.False(TaskCenterHost.ProbeOriginalBgiProcess(self.Id, ticks - 1));
    }
}
