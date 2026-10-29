using System.Collections.Concurrent;
using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

/// <summary>隔离数据根验证异常中断后的用户重置：保留定义和失败事实，结清后才能新跑或删除。</summary>
public sealed class WorkflowExecutionResetTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "flow-reset-" + Guid.NewGuid().ToString("N"));
    private string Flows => Path.Combine(_root, "flows");
    private string Runs => Path.Combine(_root, "runs");
    private WorkflowStore Workflows => new(Flows);

    public void Dispose()
    {
        if (Directory.Exists(_root)) Directory.Delete(_root, recursive: true);
    }

    private (string Id, string Revision) Seed(string name = "中断计划", bool resourceNodes = false)
    {
        var document = new WorkflowDocument
        {
            Name = name,
            Activation = new WorkflowActivation { Status = "active" },
            Nodes = resourceNodes
                ? [new() { NodeId = "first", Kind = "resource.oneDragonConfig", Ref = new() { Config = "配置一", Revision = "r1" } },
                   new() { NodeId = "second", Kind = "resource.oneDragonConfig", Ref = new() { Config = "配置二", Revision = "r2" } }]
                : [new() { NodeId = "end", Kind = "control.end" }],
        };
        var revision = Workflows.Save(document, null);
        return (document.WorkflowId!, revision);
    }

    private TaskCenterHost Host(RecordingBoundary? boundary = null, bool admissionWired = false,
        TaskCenterAdmissionSeams? seams = null, Func<bool>? capability = null)
        => new(Flows, Runs, Path.Combine(_root, "catalog.json"), () => null, null,
            (_, workflows, runs) => new WorkflowRunner(workflows, runs, boundary ?? new RecordingBoundary(),
                new NoopPrerequisite(), new NoopTerminal()),
            () => (true, null), localExecutionCapability: capability,
            admissionWired: admissionWired, admissionSeams: seams);

    private static WorkflowRunRecord AddRun(TaskCenterHost host, string workflowId, string revision,
        WorkflowRunState state, string? note = null)
        => AddRun(host.Runs, workflowId, revision, state, note);

    private static WorkflowRunRecord AddRun(RunStore runs, string workflowId, string revision,
        WorkflowRunState state, string? note = null)
    {
        var run = runs.CreateRun(workflowId, revision);
        run.State = state;
        run.Note = note;
        runs.Update(run);
        return run;
    }

    private byte[] FlowBytes(string id) => File.ReadAllBytes(Path.Combine(Flows, id + ".flow.json"));
    private byte[] RunBytes(string id) => File.ReadAllBytes(Path.Combine(Runs, id + ".run.json"));

    private static async Task WaitUntilAsync(Func<bool> condition)
    {
        for (var i = 0; i < 1000 && !condition(); i++) await Task.Delay(10);
        Assert.True(condition(), "隔离任务在限定时间内没有到达预期状态。");
    }

    [Fact]
    public async Task Interrupted_ResetPreservesDefinitionAndFailureHistory_AndNewRunStartsAtFirstNode()
    {
        var (id, revision) = Seed(resourceNodes: true);
        var boundary = new RecordingBoundary();
        var host = Host(boundary);
        try
        {
            var failed = AddRun(host, id, revision, WorkflowRunState.Failed, "先前真实失败 InvalidOperationException");
            var failedBytes = RunBytes(failed.RunId);
            var interrupted = AddRun(host, id, revision, WorkflowRunState.Interrupted, "蓝屏前的原始失败说明");
            interrupted.Cursor = new() { NodeId = "second", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
            interrupted.NodeOutcomes.Add(new() { NodeId = "first", Result = "failed", Reason = "保留原失败" });
            host.Runs.Update(interrupted);
            var originalOutcomes = JsonSerializer.Serialize(interrupted.NodeOutcomes);
            var originalCursor = JsonSerializer.Serialize(interrupted.Cursor);
            var definition = FlowBytes(id);

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Effective, reset.Status);
            var abandoned = host.Runs.Load(interrupted.RunId)!;
            Assert.Equal(WorkflowRunState.Interrupted, abandoned.State);
            Assert.NotNull(abandoned.ExecutionResetRelease);
            Assert.True(TaskCenterHost.IsExecutionResetReleased(abandoned));
            Assert.Equal("蓝屏前的原始失败说明", abandoned.Note);
            Assert.Equal(originalOutcomes, JsonSerializer.Serialize(abandoned.NodeOutcomes));
            Assert.Equal(originalCursor, JsonSerializer.Serialize(abandoned.Cursor));
            Assert.Equal(interrupted.WireRunId, abandoned.WireRunId);
            Assert.Equal(interrupted.IdempotencyKey, abandoned.IdempotencyKey);
            Assert.Equal(definition, FlowBytes(id));
            Assert.Equal(failedBytes, RunBytes(failed.RunId));
            Assert.Empty(boundary.Submissions);
            Assert.False((await host.ResumeRunAsync(interrupted.RunId)).Ok);
            var resetHistoryBytes = RunBytes(interrupted.RunId);

            var start = await host.StartWorkflowAsync(id);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            await WaitUntilAsync(() => !host.IsDriving(id) && host.Runs.List().Any(run =>
                run.RunId != interrupted.RunId && run.RunId != failed.RunId && run.State == WorkflowRunState.Succeeded));

            Assert.Equal(new[] { "first", "second" }, boundary.Submissions.ToArray());
            var newRun = Assert.Single(host.Runs.List().Where(run => run.RunId != interrupted.RunId && run.RunId != failed.RunId));
            Assert.Equal(WorkflowRunState.Succeeded, newRun.State);
            Assert.NotEqual(interrupted.WireRunId, newRun.WireRunId);
            Assert.Equal(WorkflowRunState.Interrupted, host.Runs.Load(interrupted.RunId)!.State);
            Assert.DoesNotContain(host.ListActiveRuns(), run => run.RunId == interrupted.RunId);
            Assert.Contains(host.ListHistoryRuns(), run => run.RunId == interrupted.RunId);
            Assert.Equal(resetHistoryBytes, RunBytes(interrupted.RunId));
            Assert.Equal(failedBytes, RunBytes(failed.RunId));
            Assert.Equal(definition, FlowBytes(id));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(WorkflowRunState.Interrupted)]
    [InlineData(WorkflowRunState.Unknown)]
    public async Task NoOutstandingSubmission_ResetAllowsDelete_WithoutRemovingHistory(WorkflowRunState state)
    {
        var (id, revision) = Seed();
        var host = Host();
        try
        {
            var run = AddRun(host, id, revision, state, "原中断或未知说明");
            var definition = FlowBytes(id);
            Assert.False(host.DeleteFlow(id, revision).Ok);

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.True(reset.Ok, reset.Message);
            Assert.Equal(state, host.Runs.Load(run.RunId)!.State);
            Assert.NotNull(host.Runs.Load(run.RunId)!.ExecutionResetRelease);
            Assert.True(TaskCenterHost.IsExecutionResetReleased(host.Runs.Load(run.RunId)!));
            var resetRunBytes = RunBytes(run.RunId);
            Assert.True(host.DeleteFlow(id, revision).Ok);
            Assert.Empty(host.ListFlows());
            var deleted = Assert.Single(host.ListDeletedFlows());
            Assert.Equal(definition, File.ReadAllBytes(deleted.RecoveryFilePath));
            Assert.Equal(resetRunBytes, RunBytes(run.RunId));
            Assert.Contains("原中断或未知说明", host.Runs.Load(run.RunId)!.Note);
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task RepeatedReset_DoesNotRewriteAlreadySettledRunsOrFailureHistory()
    {
        var (id, revision) = Seed();
        var host = Host();
        try
        {
            var run = AddRun(host, id, revision, WorkflowRunState.Interrupted, "原故障");
            var failed = AddRun(host, id, revision, WorkflowRunState.Failed, "历史失败不可洗白");
            var failedBytes = RunBytes(failed.RunId);
            Assert.True((await host.ResetWorkflowExecutionStateAsync(id)).Ok);
            var once = RunBytes(run.RunId);

            var second = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.True(second.Ok, second.Message);
            Assert.Equal(once, RunBytes(run.RunId));
            Assert.Equal(failedBytes, RunBytes(failed.RunId));
            Assert.Equal(2, host.Runs.List().Count);
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task ResetDoesNotRecoverOrRewriteOtherPlans()
    {
        var (id, revision) = Seed();
        var (otherId, otherRevision) = Seed("别的计划");
        var host = Host();
        try
        {
            AddRun(host, id, revision, WorkflowRunState.Interrupted);
            var running = AddRun(host, otherId, otherRevision, WorkflowRunState.Running, "不得被本次恢复扫描改写");
            var unknown = AddRun(host, otherId, otherRevision, WorkflowRunState.Unknown, "别的计划未决责任");
            unknown.CurrentSubmission = new() { Key = "other-send", JobId = "other-job", SendAttempted = true, Intent = SubmitIntentState.Accepted };
            host.Runs.Update(unknown);
            var runningBytes = RunBytes(running.RunId);
            var unknownBytes = RunBytes(unknown.RunId);
            var otherDefinition = FlowBytes(otherId);

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.True(reset.Ok, reset.Message);
            Assert.Equal(runningBytes, RunBytes(running.RunId));
            Assert.Equal(unknownBytes, RunBytes(unknown.RunId));
            Assert.Equal(otherDefinition, FlowBytes(otherId));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(WorkflowRunState.Interrupted)]
    [InlineData(WorkflowRunState.Unknown)]
    public async Task UnconfirmedSubmissionOffline_ResetFailsWithoutDiscardingResponsibility(WorkflowRunState state)
    {
        var (id, revision) = Seed();
        var boundary = new RecordingBoundary();
        var host = Host(boundary);
        try
        {
            var run = AddRun(host, id, revision, state, "原发送结果不可确认");
            run.CurrentSubmission = new()
            {
                Key = "original-submission", NodeId = "original-node", Intent = SubmitIntentState.Accepted,
                JobId = "old-process-job", Epoch = "1:100", SendAttempted = true,
                AcceptedSendIdentity = "sub:old-request:1",
            };
            host.Runs.Update(run);
            var submission = JsonSerializer.Serialize(run.CurrentSubmission);
            var definition = FlowBytes(id);

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Unavailable, reset.Status);
            var remaining = host.Runs.Load(run.RunId)!;
            Assert.False(remaining.IsTerminal);
            Assert.Null(remaining.ExecutionResetRelease);
            Assert.True(RunStore.HasUnresolvedTerminalResponsibility(remaining));
            Assert.Equal(submission, JsonSerializer.Serialize(remaining.CurrentSubmission));
            Assert.Contains("原发送结果不可确认", remaining.Note);
            Assert.Equal(definition, FlowBytes(id));
            Assert.Empty(boundary.Submissions);
            Assert.False(host.DeleteFlow(id, revision).Ok);
            Assert.False((await host.StartWorkflowAsync(id)).Ok);
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task CorruptRunWithUnknownOwnership_ResetIsRefusedAndOriginalFilesRemain()
    {
        var (id, revision) = Seed();
        var host = Host();
        try
        {
            var run = AddRun(host, id, revision, WorkflowRunState.Interrupted, "等待人工重置");
            var runBytes = RunBytes(run.RunId);
            var definition = FlowBytes(id);
            var corruptPath = Path.Combine(Runs, "unreadable.run.json");
            const string corrupt = "{unfinished crash write";
            File.WriteAllText(corruptPath, corrupt);

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Unavailable, reset.Status);
            Assert.Equal(runBytes, RunBytes(run.RunId));
            Assert.Equal(definition, FlowBytes(id));
            Assert.Equal(corrupt, File.ReadAllText(corruptPath));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task MonitoringOnly_ResetRejectedBeforeAnyMutation()
    {
        var (id, revision) = Seed();
        var run = AddRun(new RunStore(Runs), id, revision, WorkflowRunState.Interrupted, "监控端不可重置");
        var original = RunBytes(run.RunId);
        var host = Host(admissionWired: true, seams: new() { Epoch = "1:200" }, capability: () => false);
        try
        {
            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Unavailable, reset.Status);
            Assert.Equal(original, RunBytes(run.RunId));
            Assert.False(Directory.Exists(Path.Combine(_root, "arbitration")));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("process_absent", null)]
    [InlineData("process_id_reused", 638945000000000001L)]
    public async Task OldLocalProcessConfirmedExited_ResetReleasesCrashResponsibilityAndKeepsUnknownFacts(
        string disposition, long? observedStartTicks)
    {
        var (id, revision) = Seed();
        var host = Host();
        try
        {
            var old = AddCrashedLocalRun(host, id, revision);
            var originalSubmission = JsonSerializer.Serialize(old.CurrentSubmission);
            var originalOutcomes = JsonSerializer.Serialize(old.NodeOutcomes);
            var definition = FlowBytes(id);
            var observedEpochs = new List<string>();
            host.ExecutionResetConnectedEpochForTest = _ => Task.FromResult<string?>(NewLocalEpoch);
            host.ExecutionResetProcessObserverForTest = epoch =>
            {
                observedEpochs.Add(epoch);
                return new(disposition, observedStartTicks);
            };

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Effective, reset.Status);
            var released = host.Runs.Load(old.RunId)!;
            Assert.Equal(WorkflowRunState.Unknown, released.State);
            Assert.True(TaskCenterHost.IsExecutionResetReleased(released));
            Assert.Equal("蓝屏后原作业结果未知，不得改为成功或失败", released.Note);
            Assert.Equal(originalSubmission, JsonSerializer.Serialize(released.CurrentSubmission));
            Assert.Equal(originalOutcomes, JsonSerializer.Serialize(released.NodeOutcomes));
            Assert.Equal(OldLocalScope, released.AdmissionSourceScope);
            Assert.Contains(OldLocalEpoch, observedEpochs);
            Assert.Equal(definition, FlowBytes(id));
            Assert.DoesNotContain(host.ListActiveRuns(), run => run.RunId == old.RunId);
            Assert.Contains(host.ListHistoryRuns(), run => run.RunId == old.RunId);
            var releasedHistoryBytes = RunBytes(old.RunId);

            var start = await host.StartWorkflowAsync(id);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            await WaitUntilAsync(() => !host.IsDriving(id) && host.Runs.List().Any(run =>
                run.RunId != old.RunId && run.State == WorkflowRunState.Succeeded));
            Assert.Equal(WorkflowRunState.Succeeded, Assert.Single(host.Runs.List().Where(run => run.RunId != old.RunId)).State);
            Assert.True(host.DeleteFlow(id, revision).Ok);
            Assert.Equal(releasedHistoryBytes, RunBytes(old.RunId));
            Assert.Equal(originalSubmission, JsonSerializer.Serialize(host.Runs.Load(old.RunId)!.CurrentSubmission));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("same_process")]
    [InlineData("unknown")]
    public async Task OriginalLocalProcessNotConfirmedExited_ResetMustNotReleaseOrAllowDelete(string disposition)
    {
        var (id, revision) = Seed();
        var host = Host();
        try
        {
            var old = AddCrashedLocalRun(host, id, revision);
            var submission = JsonSerializer.Serialize(old.CurrentSubmission);
            host.ExecutionResetConnectedEpochForTest = _ => Task.FromResult<string?>(NewLocalEpoch);
            host.ExecutionResetProcessObserverForTest = _ => new(disposition, disposition == "same_process" ? 638945000000000000L : null);

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Unavailable, reset.Status);
            var remaining = host.Runs.Load(old.RunId)!;
            Assert.False(TaskCenterHost.IsExecutionResetReleased(remaining));
            Assert.Equal(WorkflowRunState.Unknown, remaining.State);
            Assert.Equal(submission, JsonSerializer.Serialize(remaining.CurrentSubmission));
            Assert.False(host.DeleteFlow(id, revision).Ok);
            Assert.Contains(host.ListActiveRuns(), run => run.RunId == old.RunId);
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task CurrentLocalEpochStillOriginal_ResetDoesNotTrustContradictoryProcessAbsence()
    {
        var (id, revision) = Seed();
        var host = Host();
        try
        {
            var old = AddCrashedLocalRun(host, id, revision);
            host.ExecutionResetConnectedEpochForTest = _ => Task.FromResult<string?>(OldLocalEpoch);
            host.ExecutionResetProcessObserverForTest = _ => new("process_absent");

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Unavailable, reset.Status);
            Assert.False(TaskCenterHost.IsExecutionResetReleased(host.Runs.Load(old.RunId)!));
            Assert.False(host.DeleteFlow(id, revision).Ok);
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task CurrentExecutorChangesDuringExitProof_ResetDoesNotPublishRelease()
    {
        var (id, revision) = Seed();
        var host = Host();
        try
        {
            var old = AddCrashedLocalRun(host, id, revision);
            var reads = 0;
            host.ExecutionResetConnectedEpochForTest = _ => Task.FromResult<string?>(
                Interlocked.Increment(ref reads) == 1 ? NewLocalEpoch : "43:638947000000000000");
            host.ExecutionResetProcessObserverForTest = _ => new("process_absent");

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Unavailable, reset.Status);
            Assert.True(reads >= 2, "发布释放证据前必须重新确认当前连接身份。");
            Assert.False(TaskCenterHost.IsExecutionResetReleased(host.Runs.Load(old.RunId)!));
            Assert.False(host.DeleteFlow(id, revision).Ok);
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task ResetAwaitingProcessEvidence_RejectsOverlappingResetAndPlanDeletion()
    {
        var (id, revision) = Seed();
        var host = Host();
        var reached = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<string?>(TaskCreationOptions.RunContinuationsAsynchronously);
        try
        {
            var old = AddCrashedLocalRun(host, id, revision);
            host.ExecutionResetConnectedEpochForTest = _ =>
            {
                reached.TrySetResult();
                return release.Task;
            };
            host.ExecutionResetProcessObserverForTest = _ => new("process_absent");

            var first = host.ResetWorkflowExecutionStateAsync(id);
            await reached.Task.WaitAsync(TimeSpan.FromSeconds(5));
            var overlapping = await host.ResetWorkflowExecutionStateAsync(id);
            var aliasedStart = await host.StartWorkflowAsync(id.ToUpperInvariant());

            Assert.Equal(HostActionStatus.Unavailable, overlapping.Status);
            Assert.Equal(HostActionStatus.Unavailable, aliasedStart.Status);
            Assert.Contains("身份", aliasedStart.Message);
            Assert.False(host.DeleteFlow(id, revision).Ok);
            Assert.False(TaskCenterHost.IsExecutionResetReleased(host.Runs.Load(old.RunId)!));
            release.TrySetResult(NewLocalEpoch);
            var completed = await first.WaitAsync(TimeSpan.FromSeconds(10));
            Assert.Equal(HostActionStatus.Effective, completed.Status);
            Assert.True(TaskCenterHost.IsExecutionResetReleased(host.Runs.Load(old.RunId)!));
        }
        finally
        {
            release.TrySetResult(NewLocalEpoch);
            await host.ShutdownAsync();
        }
    }

    [Fact]
    public async Task WiredRecoveryRejectedForStaleEpoch_ResetAbandonsOldRunAndFreshStartUsesCurrentEpoch()
    {
        var (id, revision) = Seed();
        var beforeRestart = new RunStore(Runs);
        var old = beforeRestart.CreateRun(id, revision, note: "旧进程异常退出 InvalidOperationException",
            handoff: new HandoffIdentity
            {
                IntentKey = "fixture:before-crash", ExecutionId = Guid.NewGuid().ToString("N"),
                StepId = "old-step", Mode = StartupHandoffModes.Start,
            }, admissionSourceScope: "bgi:local:1:100");
        old.State = WorkflowRunState.Interrupted;
        beforeRestart.Update(old);
        var boundary = new RecordingBoundary();
        var host = Host(boundary, admissionWired: true, seams: new() { Epoch = "1:200" });
        try
        {
            var definition = FlowBytes(id);

            var resume = await host.ResumeRunAsync(old.RunId);
            Assert.Equal(HostActionStatus.Unavailable, resume.Status);
            Assert.Contains("stale_epoch", resume.Message);
            Assert.Equal(WorkflowRunState.Interrupted, host.Runs.Load(old.RunId)!.State);
            Assert.Empty(boundary.Submissions);

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Effective, reset.Status);
            var abandoned = host.Runs.Load(old.RunId)!;
            Assert.Equal(WorkflowRunState.Interrupted, abandoned.State);
            Assert.NotNull(abandoned.ExecutionResetRelease);
            Assert.True(TaskCenterHost.IsExecutionResetReleased(abandoned));
            Assert.Equal("bgi:local:1:100", abandoned.AdmissionSourceScope);
            Assert.Equal("旧进程异常退出 InvalidOperationException", abandoned.Note);
            Assert.Equal(definition, FlowBytes(id));
            Assert.Empty(boundary.Submissions);

            var start = await host.StartWorkflowAsync(id);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            await WaitUntilAsync(() => !host.IsDriving(id) && host.Runs.List().Any(run =>
                run.RunId != old.RunId && run.State == WorkflowRunState.Succeeded));
            var fresh = Assert.Single(host.Runs.List().Where(run => run.RunId != old.RunId));
            Assert.Equal(WorkflowRunState.Succeeded, fresh.State);
            Assert.NotEqual(old.WireRunId, fresh.WireRunId);
            var arbitration = new ArbitrationLeaseStore(Path.Combine(_root, "arbitration")).Read();
            var operations = arbitration.File!.Handoff!.Operations
                .Concat(arbitration.File.Handoff.ArchivedOperations.Select(archive => archive.Operation));
            var freshStart = Assert.Single(operations.Where(operation => operation.Intent == "start" && operation.RunBinding == fresh.RunId));
            Assert.Equal("1:200", freshStart.TargetEpoch);
            Assert.Equal(definition, FlowBytes(id));
        }
        finally { await host.ShutdownAsync(); }
    }

    [Fact]
    public async Task ColdHostAfterReset_PreservesUnknownHistoryAndAllowsNewStartAndDelete()
    {
        var (id, revision) = Seed();
        var first = Host();
        string runId;
        try
        {
            var old = AddCrashedLocalRun(first, id, revision);
            runId = old.RunId;
            first.ExecutionResetConnectedEpochForTest = _ => Task.FromResult<string?>(NewLocalEpoch);
            first.ExecutionResetProcessObserverForTest = _ => new("process_absent");
            var reset = await first.ResetWorkflowExecutionStateAsync(id);
            Assert.Equal(HostActionStatus.Effective, reset.Status);
        }
        finally { await first.ShutdownAsync(); }

        var history = RunBytes(runId);
        var definition = FlowBytes(id);
        var reopened = Host();
        try
        {
            reopened.EnsureRecovered();
            Assert.Equal(history, RunBytes(runId));
            Assert.Equal(WorkflowRunState.Unknown, reopened.Runs.Load(runId)!.State);
            Assert.True(TaskCenterHost.IsExecutionResetReleased(reopened.Runs.Load(runId)!));
            Assert.DoesNotContain(reopened.ListActiveRuns(), run => run.RunId == runId);
            Assert.Contains(reopened.ListHistoryRuns(), run => run.RunId == runId);
            Assert.False((await reopened.ResumeRunAsync(runId)).Ok);

            var start = await reopened.StartWorkflowAsync(id);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            await WaitUntilAsync(() => !reopened.IsDriving(id) && reopened.Runs.List().Any(run =>
                run.RunId != runId && run.State == WorkflowRunState.Succeeded));
            Assert.Equal(history, RunBytes(runId));
            Assert.Equal(definition, FlowBytes(id));
            Assert.True(reopened.DeleteFlow(id, revision).Ok);
            Assert.Equal(history, RunBytes(runId));
        }
        finally { await reopened.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("completion")]
    [InlineData("prerequisite")]
    [InlineData("online")]
    public async Task SeparateOutstandingResponsibility_ProcessExitCannotClearIt(string responsibility)
    {
        var (id, revision) = Seed();
        var boundary = new RecordingBoundary();
        var host = Host(boundary);
        try
        {
            var old = AddCrashedLocalRun(host, id, revision);
            if (responsibility == "completion")
                old.PendingCompletion = new()
                {
                    ActionId = "original-completion", Kind = "completion.software", Action = "closeSoftware",
                    State = "unknown", SendAttempted = true, Epoch = OldLocalEpoch,
                    JobId = "old-completion-job", WireRunId = old.WireRunId,
                };
            else if (responsibility == "prerequisite")
                old.PrerequisiteActions.Add(new()
                {
                    NodeId = "old-node", Attempt = 1, Kind = "strategy.switchAccount",
                    State = PrerequisiteActionState.Unknown, IdempotencyKey = "original-prerequisite",
                    SendAttempted = true, Epoch = OldLocalEpoch, JobId = "old-prerequisite-job", WireRunId = old.WireRunId,
                });
            else
                old.OnlineHoeingHandoff = new()
                {
                    Generation = 1, Epoch = OldLocalEpoch, NodeId = "old-node", Attempt = 1,
                    Ticket = "fixture-local-ticket", OriginalJobId = old.CurrentSubmission!.JobId,
                    OriginalSubmissionKey = old.CurrentSubmission.Key,
                };
            host.Runs.Update(old);
            var original = RunBytes(old.RunId);
            host.ExecutionResetConnectedEpochForTest = _ => Task.FromResult<string?>(NewLocalEpoch);
            host.ExecutionResetProcessObserverForTest = _ => new("process_absent");

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Unavailable, reset.Status);
            Assert.Equal(original, RunBytes(old.RunId));
            Assert.Null(host.Runs.Load(old.RunId)!.ExecutionResetRelease);
            Assert.False(host.DeleteFlow(id, revision).Ok);
            Assert.Empty(boundary.Submissions);
        }
        finally { await host.ShutdownAsync(); }
    }

    /// <summary>
    /// 真实形态：切换账号前置动作的结果未知、BGI 作业已退出，运行停在 Unknown，没有任务本体提交。
    /// 原 BGI 进程确认退出后允许重置；原进程仍在、其他前置类型或原纪元缺失时照旧拒绝。
    /// </summary>
    private static WorkflowRunRecord AddUnknownAccountSwitchRun(TaskCenterHost host, string workflowId, string revision,
        string kind = "prerequisite.account", string? epoch = OldLocalEpoch, bool foreignWire = false)
    {
        var run = host.Runs.CreateRun(workflowId, revision, note: "前置动作结果不确定，标 Unknown（不推进、不触发收尾、禁止自动重跑）。",
            handoff: new HandoffIdentity
            {
                IntentKey = "fixture:account-switch:" + Guid.NewGuid().ToString("N"),
                ExecutionId = Guid.NewGuid().ToString("N"), StepId = "account-switch", Mode = StartupHandoffModes.Start,
            }, admissionSourceScope: OldLocalScope);
        run.State = WorkflowRunState.Unknown;
        run.PrerequisiteActions.Add(new()
        {
            WireRunId = foreignWire ? Guid.NewGuid().ToString("N") : run.WireRunId, ObservedTerminal = "result_unknown", ExecutionExitConfirmed = true,
            ExecutionExitDisposition = "execution_exited", EffectState = "unknown", NodeId = "account-node",
            Attempt = 1, Kind = kind, AccountKey = "fixture-account", Epoch = epoch,
            IdempotencyKey = "idem-account-" + Guid.NewGuid().ToString("N")[..12], Fingerprint = "fixture-account-payload",
            ExpiresAtUtc = "2030-01-01T00:00:00Z", SendAttempted = true, JobId = "old-account-job",
            State = PrerequisiteActionState.Unknown, Reason = "原始终态或实际效果未知",
        });
        host.Runs.Update(run);
        return run;
    }

    [Theory]
    [InlineData("process_absent", null)]
    [InlineData("process_id_reused", 638945000000000001L)]
    public async Task UnknownAccountSwitch_OldProcessExited_ResetReleasesAndKeepsFacts(string disposition, long? observedTicks)
    {
        var (id, revision) = Seed();
        var boundary = new RecordingBoundary();
        var host = Host(boundary);
        try
        {
            var old = AddUnknownAccountSwitchRun(host, id, revision);
            var crashed = AddCrashedLocalRun(host, id, revision);
            var prerequisite = JsonSerializer.Serialize(old.PrerequisiteActions);
            var definition = FlowBytes(id);
            var observed = new List<string>();
            host.ExecutionResetConnectedEpochForTest = _ => Task.FromResult<string?>(NewLocalEpoch);
            host.ExecutionResetProcessObserverForTest = epoch => { observed.Add(epoch); return new(disposition, observedTicks); };

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Effective, reset.Status);
            foreach (var runId in new[] { old.RunId, crashed.RunId })
            {
                var released = host.Runs.Load(runId)!;
                Assert.Equal(WorkflowRunState.Unknown, released.State);
                Assert.True(TaskCenterHost.IsExecutionResetReleased(released));
                Assert.DoesNotContain(host.ListActiveRuns(), run => run.RunId == runId);
            }
            var kept = host.Runs.Load(old.RunId)!;
            Assert.Equal(prerequisite, JsonSerializer.Serialize(kept.PrerequisiteActions));
            Assert.Equal(PrerequisiteActionState.Unknown, Assert.Single(kept.PrerequisiteActions).State);
            Assert.Equal(OldLocalEpoch, Assert.Single(kept.ExecutionResetRelease!.ProcessExits).OriginalEpoch);
            Assert.Contains(OldLocalEpoch, observed);
            Assert.Equal(definition, FlowBytes(id));
            Assert.Empty(boundary.Submissions);

            var start = await host.StartWorkflowAsync(id);
            Assert.Equal(HostActionStatus.Registered, start.Status);
            await WaitUntilAsync(() => !host.IsDriving(id));
        }
        finally { await host.ShutdownAsync(); }
    }

    /// <summary>BGI 未连接时，重置先走执行环境确保（按需启动 BGI，不发送任务），连上后再核对原进程退出。</summary>
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task BgiOffline_ResetEnsuresBgiFirst_ThenChecksOldProcess(bool ensureSucceeds)
    {
        var (id, revision) = Seed();
        var boundary = new RecordingBoundary();
        var connected = false;
        var ensureCalls = 0;
        var host = new TaskCenterHost(Flows, Runs, Path.Combine(_root, "catalog.json"), () => null, null,
            (_, workflows, runs) => new WorkflowRunner(workflows, runs, boundary, new NoopPrerequisite(), new NoopTerminal()),
            () => connected ? (true, null) : (false, "BGI 离线"),
            ensureExecutionReady: _ =>
            {
                ensureCalls++;
                connected = ensureSucceeds;
                return Task.FromResult<string?>(ensureSucceeds ? null : "测试中 BGI 启动失败");
            });
        try
        {
            var old = AddUnknownAccountSwitchRun(host, id, revision);
            var original = RunBytes(old.RunId);
            host.ExecutionResetConnectedEpochForTest = _ => Task.FromResult<string?>(connected ? NewLocalEpoch : null);
            host.ExecutionResetProcessObserverForTest = _ => new("process_absent");

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(1, ensureCalls);
            Assert.Empty(boundary.Submissions);
            if (ensureSucceeds)
            {
                Assert.Equal(HostActionStatus.Effective, reset.Status);
                Assert.True(TaskCenterHost.IsExecutionResetReleased(host.Runs.Load(old.RunId)!));
            }
            else
            {
                Assert.Equal(HostActionStatus.Unavailable, reset.Status);
                Assert.Contains("测试中 BGI 启动失败", reset.Message);
                Assert.Equal(original, RunBytes(old.RunId));
                Assert.Contains(host.ListActiveRuns(), run => run.RunId == old.RunId);
            }
        }
        finally { await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData("still_running")]
    [InlineData("other_kind")]
    [InlineData("missing_epoch")]
    [InlineData("other_wire")]
    public async Task UnknownAccountSwitch_WithoutExitProof_ResetStillRefused(string shape)
    {
        var (id, revision) = Seed();
        var boundary = new RecordingBoundary();
        var host = Host(boundary);
        try
        {
            var old = AddUnknownAccountSwitchRun(host, id, revision,
                kind: shape == "other_kind" ? "prerequisite.redeemCode" : "prerequisite.account",
                epoch: shape == "missing_epoch" ? null : OldLocalEpoch, foreignWire: shape == "other_wire");
            var original = RunBytes(old.RunId);
            host.ExecutionResetConnectedEpochForTest = _ => Task.FromResult<string?>(NewLocalEpoch);
            host.ExecutionResetProcessObserverForTest = _ => shape == "still_running"
                ? new("same_process", 638945000000000000L) : new("process_absent");

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(HostActionStatus.Unavailable, reset.Status);
            Assert.Equal(original, RunBytes(old.RunId));
            Assert.False(TaskCenterHost.IsExecutionResetReleased(host.Runs.Load(old.RunId)!));
            Assert.Contains(host.ListActiveRuns(), run => run.RunId == old.RunId);
            Assert.Equal(HostActionStatus.Unavailable, (await host.StartWorkflowAsync(id)).Status);
            Assert.False(host.DeleteFlow(id, revision).Ok);
            Assert.Empty(boundary.Submissions);
        }
        finally { await host.ShutdownAsync(); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task ProductionProcessObservation_DistinguishesLiveTestProcessAndExitedOwnedHelper(bool helperExited)
    {
        var (id, revision) = Seed();
        var host = Host();
        Process? ownedHelper = null;
        try
        {
            string originalEpoch;
            if (helperExited)
            {
                // 本测试创建的隐藏命令行只等一行标准输入，随后正常退出；不接触真实 BGI。
                ownedHelper = Process.Start(new ProcessStartInfo
                {
                    FileName = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "cmd.exe"),
                    Arguments = "/d /q /c \"set /p resetHold=&exit /b 0\"",
                    UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    RedirectStandardInput = true,
                }) ?? throw new InvalidOperationException("隔离辅助进程未创建。");
                originalEpoch = ownedHelper.Id + ":" + ownedHelper.StartTime.ToUniversalTime().Ticks;
                await ownedHelper.StandardInput.WriteLineAsync("finish");
                ownedHelper.StandardInput.Close();
                await ownedHelper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
            }
            else
            {
                using var current = Process.GetCurrentProcess();
                originalEpoch = current.Id + ":" + current.StartTime.ToUniversalTime().Ticks;
            }
            var old = AddCrashedLocalRun(host, id, revision, originalEpoch);
            var originalSubmission = JsonSerializer.Serialize(old.CurrentSubmission);
            host.ExecutionResetConnectedEpochForTest = _ => Task.FromResult<string?>(NewLocalEpoch);
            // 不设置 ProcessObserver seam，验证生产 Process.GetProcessById/StartTime 路径。

            var reset = await host.ResetWorkflowExecutionStateAsync(id);

            Assert.Equal(helperExited ? HostActionStatus.Effective : HostActionStatus.Unavailable, reset.Status);
            var observed = host.Runs.Load(old.RunId)!;
            Assert.Equal(helperExited, TaskCenterHost.IsExecutionResetReleased(observed));
            Assert.Equal(WorkflowRunState.Unknown, observed.State);
            Assert.Equal(originalSubmission, JsonSerializer.Serialize(observed.CurrentSubmission));
            if (helperExited)
            {
                var proof = Assert.Single(observed.ExecutionResetRelease!.ProcessExits);
                Assert.Equal(originalEpoch, proof.OriginalEpoch);
                Assert.Contains(proof.Disposition, new[] { "process_absent", "process_id_reused" });
                Assert.True(host.DeleteFlow(id, revision).Ok);
            }
            else Assert.False(host.DeleteFlow(id, revision).Ok);
        }
        finally
        {
            if (ownedHelper is not null)
            {
                if (!ownedHelper.HasExited)
                {
                    ownedHelper.StandardInput.Close();
                    await ownedHelper.WaitForExitAsync().WaitAsync(TimeSpan.FromSeconds(5));
                }
                ownedHelper.Dispose();
            }
            await host.ShutdownAsync();
        }
    }

    private const string OldLocalEpoch = "41:638945000000000000";
    private const string NewLocalEpoch = "42:638946000000000000";
    private const string OldLocalScope = "bgi:local:" + OldLocalEpoch;

    private static WorkflowRunRecord AddCrashedLocalRun(TaskCenterHost host, string workflowId, string revision,
        string? originalEpoch = null)
    {
        originalEpoch ??= OldLocalEpoch;
        var run = host.Runs.CreateRun(workflowId, revision, note: "蓝屏后原作业结果未知，不得改为成功或失败",
            handoff: new HandoffIdentity
            {
                IntentKey = "fixture:crashed-body:" + Guid.NewGuid().ToString("N"),
                ExecutionId = Guid.NewGuid().ToString("N"), StepId = "crashed-body", Mode = StartupHandoffModes.Start,
            }, admissionSourceScope: "bgi:local:" + originalEpoch,
            stopAuthority: new(originalEpoch, 0, "fixture-original-stop-authority", Stopwatch.GetTimestamp(), Stopwatch.Frequency));
        host.Runs.RecordIntentForBoundary(run, new()
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "old-node", 0, 0, 1),
            NodeId = "old-node", Occurrence = 0, LoopIteration = 0, Attempt = 1,
            Epoch = originalEpoch, Fingerprint = "fixture-original-body-payload", ExpiresAtUtc = "2030-01-01T00:00:00Z",
        }, nodeAdmissionRequired: false);
        run.State = WorkflowRunState.Unknown;
        run.CurrentSubmission!.Intent = SubmitIntentState.Accepted;
        run.CurrentSubmission.JobId = "old-process-body-job";
        run.CurrentSubmission.SendAttempted = true;
        run.NodeOutcomes.Add(new() { NodeId = "previous-node", Result = "failed", Reason = "蓝屏前已记录的失败" });
        host.Runs.Update(run);
        return run;
    }

    private sealed class RecordingBoundary : IWorkflowExecutionBoundary
    {
        private readonly ConcurrentQueue<string> _submissions = new();
        public IReadOnlyCollection<string> Submissions => _submissions.ToArray();
        public bool SingleNativeSupported => true;
        public Task<BoundarySubmitResult> SubmitAsync(WorkflowSubmitRequest request, CancellationToken ct)
        {
            TerminalReleaseFixtureFacts.FreezeBody(request);
            _submissions.Enqueue(request.Occurrence.NodeId);
            return Task.FromResult(BoundarySubmitResult.AcceptedWith("fixture-job-" + _submissions.Count));
        }
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
        public Task<TerminalExecutionResult> ExecuteAsync(WorkflowTerminalAction action, WorkflowRunRecord run, CancellationToken ct)
            => Task.FromResult(TerminalExecutionResult.Executed("fixture-no-effect"));
    }
}
