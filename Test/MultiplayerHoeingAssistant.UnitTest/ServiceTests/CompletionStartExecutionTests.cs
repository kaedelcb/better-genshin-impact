using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

// These cases decide whether a saved starting point may execute or must be rejected. In
// particular, an invalid stable ID must never turn into index zero or the saved cursor.
public sealed class CompletionStartExecutionTests
{
    private static readonly BgiEpoch Epoch = new() { ProcessId = 41, StartTicksUtc = 410 };
    private static TaskConflictPolicySettings Policy(string type = "group", string mode = "node") => new()
    {
        Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskType = type, SpecifiedTaskName = "后续任务",
        SpecifiedStartMode = mode, SpecifiedStartTaskId = mode == "node" ? "stable-b" : "",
        SpecifiedStartTaskName = "第二任务", SpecifiedConfigRevision = "r1", SpecifiedWorkflowId = type == "workflow" ? "flow-test" : "",
    };
    private static JsonElement Description(string revision = "r1") => JsonSerializer.SerializeToElement(new
    {
        configRevision = revision,
        tasks = new[]
        {
            new { taskId = "disabled-a", name = "未启用", enabled = false, legacyIndex = 1 },
            new { taskId = "stable-b", name = "重名", enabled = true, legacyIndex = 4 },
            new { taskId = "stable-c", name = "重名", enabled = true, legacyIndex = 7 },
        },
    });

    [Theory]
    [InlineData("group", "beginning", 4, null)]
    [InlineData("group", "node", 4, null)]
    [InlineData("onedragon", "beginning", 0, "stable-b")]
    [InlineData("onedragon", "node", 0, "stable-b")]
    public void ExplicitStartsUseFirstEnabledOrStableIdentity(string type, string mode, int index, string? taskId)
    {
        var start = CommandExecutor.ResolveCompletionStart(Policy(type, mode), Description());
        Assert.Equal(index, start.Index);
        Assert.Equal(taskId, start.TaskId);
        Assert.Equal("r1", start.Revision);
    }

    [Theory]
    [InlineData("missing", "r1", "specified_task_missing")]
    [InlineData("disabled-a", "r1", "specified_task_missing")]
    [InlineData("stable-b", "old", "configuration_revision_mismatch")]
    public void MissingDisabledOrStaleNodeIsRejected(string id, string revision, string error)
    {
        var policy = Policy(); policy.SpecifiedStartTaskId = id; policy.SpecifiedConfigRevision = revision;
        var failure = Assert.Throws<CommandExecutor.CompletionStartException>(() => CommandExecutor.ResolveCompletionStart(policy, Description()));
        Assert.Equal(error, failure.Code);
    }

    [Fact]
    public void DuplicateTaskNamesDoNotChangeSelectedStableId()
    {
        var policy = Policy("onedragon"); policy.SpecifiedStartTaskId = "stable-c";
        Assert.Equal("stable-c", CommandExecutor.ResolveCompletionStart(policy, Description()).TaskId);
    }

    [Fact]
    public void RecoveryMaterializedLongKeepsFrozenGroupIndex()
    {
        var start = CommandExecutor.ResolveCompletionStart(Policy(), Description());
        var json = JsonSerializer.Serialize(CommandExecutor.FreezeCompletionStart(start));
        var recovered = CoordinatedRecoveryStore.Materialize(JsonSerializer.Deserialize<Dictionary<string, object?>>(json))!;
        Assert.IsType<long>(recovered["startFromIndex"]);
        Assert.Equal(start, CommandExecutor.ReadFrozenCompletionStart(recovered));
        recovered["startFromIndex"] = (long)int.MaxValue + 1;
        Assert.Throws<CommandExecutor.CompletionStartException>(() => CommandExecutor.ReadFrozenCompletionStart(recovered));
    }

    [Fact]
    public void LegacyDefaultRetainsLegacyCursorSemantics()
    {
        var policy = Policy(mode: "default"); policy.SpecifiedConfigRevision = "";
        var start = CommandExecutor.ResolveCompletionStart(policy, Description());
        Assert.Equal(0, start.Index); Assert.Null(start.TaskId);
    }

    [Fact]
    public void BatchSnapshotAndRecoveryKeepEveryStartField()
    {
        var policy = Policy("workflow");
        var batch = Batch(policy);
        policy.SpecifiedStartTaskId = "changed-after-snapshot";
        var saved = batch.CaptureRecovery();
        var restored = OnlineHoeingBatch.Restore(saved);
        Assert.Equal("stable-b", restored.Policy!.SpecifiedStartTaskId);
        Assert.Equal("第二任务", restored.Policy.SpecifiedStartTaskName);
        Assert.Equal("r1", restored.Policy.SpecifiedConfigRevision);
        Assert.Equal("flow-test", restored.Policy.SpecifiedWorkflowId);
        Assert.Equal("node", restored.Policy.SpecifiedStartMode);
        Assert.Equal(batch.CompletionIntentTimestamp, restored.CompletionIntentTimestamp);
    }

    private static OnlineHoeingBatch Batch(TaskConflictPolicySettings? policy = null)
    {
        var batch = new OnlineHoeingBatch(new("completion-start", 8, 0, 0, 1, "completed", "")
        {
            Protocol = "hoeing.start.v2", ParticipantUids = ["self"],
            Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>> { ["self"] = [new("锄地", "group")] },
        }, "self", policy ?? Policy("workflow")) { ExecutionEpoch = Epoch, StateContinuationEnabled = true };
        batch.MarkLocalSuccess(Epoch);
        return batch;
    }

    [Fact]
    public async Task WorkflowRegistrationWaitsForEntireRunAndNeverUsesGuidJobIdentity()
    {
        var batch = Batch(); var reads = 0; var starts = 0;
        const string runId = "run-stable123456";
        await MainViewModel.RunCompletionContinuationAsync(batch, new()
        {
            WorkflowRun = true, RegisteredJob = false, RequireAuthority = () => { },
            Prepare = _ => Task.FromResult(true), Reconcile = _ => Task.FromResult(true),
            Start = _ =>
            {
                starts++;
                return Task.FromResult(new CommandResult { Status = "success", JobId = runId,
                    TargetProcessId = Epoch.ProcessId, TargetStartTicksUtc = Epoch.StartTicksUtc.ToString() });
            },
            Query = _ =>
            {
                reads++;
                Assert.False(batch.LocalExecutionReleased);
                Assert.Equal(runId, batch.PolicyExecution!.WorkflowRunId);
                return Task.FromResult<BgiTaskQueueStatus?>(new()
                {
                    TaskHandle = runId, Epoch = Epoch, IdempotencyKey = batch.PolicyRequestKey,
                    Status = reads == 3 ? "completed" : "running", ExecutionExitConfirmed = reads == 3,
                });
            },
            Acknowledge = (_, _) => Task.FromResult(true), ExpectedRequestKey = () => batch.PolicyRequestKey,
            Delay = _ => Task.CompletedTask,
        }, CancellationToken.None).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(1, starts); Assert.Equal(3, reads); Assert.True(batch.LocalExecutionReleased);
    }

    [Fact]
    public void StructuralWorkflowBeginningDoesNotStartAtFirstStoredLaneMember()
    {
        var document = new WorkflowDocument
        {
            WorkflowId = "flow-test", Name = "双车道", Nodes =
            [
                new() { NodeId = "side", Kind = "resource.oneDragonConfig", Path = new() { Next = "$end" },
                    ExtensionData = new() { ["scheduleLane"] = JsonSerializer.SerializeToElement(1) } },
                new() { NodeId = "head", Kind = "resource.oneDragonConfig", Path = new() { Next = "$end" } },
            ],
            ExtensionData = new() { ["scheduleLanes"] = JsonSerializer.SerializeToElement(new[] { "主", "侧" }) },
        };
        var cursor = TaskCenterHost.ResolveCompletionWorkflowCursor(Policy("workflow", "beginning"), new(document, "r1"));
        Assert.Equal("head", cursor.NodeId);
    }

    [Theory]
    [InlineData("missing", "r1", "specified_task_missing")]
    [InlineData("stable-b", "old", "configuration_revision_mismatch")]
    public void WorkflowInvalidSelectionNeverFallsBackToHead(string nodeId, string revision, string error)
    {
        var policy = Policy("workflow"); policy.SpecifiedStartTaskId = nodeId; policy.SpecifiedConfigRevision = revision;
        var document = new WorkflowDocument { WorkflowId = "flow-test", Nodes = [new() { NodeId = "stable-b", Kind = "resource.oneDragonConfig" }] };
        Assert.Equal(error, Assert.Throws<CommandExecutor.CompletionStartException>(() =>
            TaskCenterHost.ResolveCompletionWorkflowCursor(policy, new(document, "r1"))).Code);
    }

    [Theory]
    [InlineData(WorkflowRunState.Planned, false, "queued")]
    [InlineData(WorkflowRunState.Running, false, "running")]
    [InlineData(WorkflowRunState.Unknown, false, "result_unknown")]
    [InlineData(WorkflowRunState.Interrupted, false, "interrupted")]
    [InlineData(WorkflowRunState.Completing, false, "completing")]
    [InlineData(WorkflowRunState.Succeeded, true, "completed")]
    [InlineData(WorkflowRunState.Failed, true, "failed")]
    [InlineData(WorkflowRunState.Cancelled, true, "queueCancelled")]
    public void WorkflowTerminalRequiresWholeRunExit(WorkflowRunState state, bool exited, string status)
    {
        var result = TaskCenterHost.CompletionWorkflowStatus(new() { RunId = "run-whole", State = state }, "request");
        Assert.Equal(exited, result.ExecutionExitConfirmed);
        Assert.Equal(status, result.Status);
    }
}
