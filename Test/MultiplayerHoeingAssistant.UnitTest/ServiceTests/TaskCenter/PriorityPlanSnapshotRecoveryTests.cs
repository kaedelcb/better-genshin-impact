using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class PriorityPlanSnapshotRecoveryTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "priority-plan-snapshot-" + Guid.NewGuid().ToString("N"));
    private readonly List<OnlineHoeingBatch> _batches = [];
    private readonly List<TaskCenterHost> _hosts = [];
    private TaskCenterHost Host()
    {
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null));
        _hosts.Add(host);
        return host;
    }
    private (TaskCenterHost Host, OnlineHoeingBatch Batch, WorkflowRunRecord Run, byte[] Workflow) Capture()
    {
        var host = Host();
        var workflow = new WorkflowDocument
        {
            Name = "原计划快照", Nodes = [new() { NodeId = "B", Kind = "resource.configGroup",
                Ref = new() { Config = "B", ConfigKey = "B#k", Revision = "original-resource" } }],
        };
        host.Workflows.Save(workflow, null);
        var flow = host.Workflows.LoadSnapshot(workflow.WorkflowId!);
        var run = host.Runs.CreateRun(workflow.WorkflowId!, flow.Revision,
            stopAuthority: new("42:99", 0, "original-intent", 10, Stopwatch.Frequency));
        run.CurrentSubmission = new()
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "B", 0, 0, 1), NodeId = "B", Attempt = 1,
            Epoch = "42:99", WireRunId = run.WireRunId, JobId = Guid.NewGuid().ToString("D"),
            Intent = SubmitIntentState.Accepted, SendAttempted = true,
            Fingerprint = "frozen-original", ExpiresAtUtc = "2030-01-01T00:00:00Z",
        };
        run.Cursor = new() { NodeId = "B", Attempt = 1 };
        run.State = WorkflowRunState.OnlineHoeingParking;
        run.OnlineHoeingHandoff = new()
        {
            StateContinuation = true, PriorityRecovery = true, Generation = 7, Ticket = "original-ticket",
            Epoch = "42:99", NodeId = "B", Attempt = 1, Phase = "parked",
            OriginalJobId = run.CurrentSubmission.JobId, OriginalSubmissionKey = run.CurrentSubmission.Key,
        };
        host.Runs.Update(run);
        var batch = new OnlineHoeingBatch(new("snapshot-batch", 7, 0, 0, 1, "completed", "")
        {
            Protocol = "hoeing.start.v2", PriorityRecovery = true, StateContinuation = true,
            ParticipantUids = ["self"], Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                { ["self"] = [new("original", "group")] },
        }, "self", new() { Policy = TaskConflictPolicy.Resume })
        {
            ManagedRunId = run.RunId, TakeoverTicket = "original-ticket", StateContinuationEnabled = true,
            StateContinuationStopVersion = 0, ExecutionEpoch = new() { ProcessId = 42, StartTicksUtc = 99 },
        };
        _batches.Add(batch);
        batch.EnsureRecoveryStopSignal();
        host.CapturePriorityPlanRecovery(batch);
        Assert.NotNull(batch.ManagedPlanSnapshot);
        return (host, batch, run, File.ReadAllBytes(Path.Combine(host.Workflows.RootDirectory, workflow.WorkflowId + ".flow.json")));
    }
    private OnlineHoeingBatch Restore(OnlineHoeingBatch original, bool livingOwner = false)
    {
        using var owner = Process.GetCurrentProcess();
        var saved = original.CaptureRecovery() with
        {
            OwnerPid = livingOwner ? owner.Id : int.MaxValue,
            OwnerStartTicks = livingOwner ? owner.StartTime.ToUniversalTime().Ticks : 1,
            OwnerSession = owner.SessionId,
        };
        // Exercise the same serialized batch payload used by the file and surviving-BGI copies.
        var batch = OnlineHoeingBatch.Restore(JsonSerializer.Deserialize<CoordinatedRecoverySnapshot>(JsonSerializer.Serialize(saved))!);
        _batches.Add(batch);
        return batch;
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LostOrCorruptProgressRestoresOriginalIdentityAndPreservesBadBytes(bool corrupt)
    {
        var captured = Capture();
        var runPath = Path.Combine(_root, "runs", captured.Run.RunId + ".run.json");
        var flowPath = Path.Combine(_root, "flows", captured.Run.WorkflowId + ".flow.json");
        var badRun = new byte[] { 0x7b, 0x22, 0xff };
        var badFlow = new byte[] { 0x7b, 0x22, 0x66 };
        if (corrupt) { File.WriteAllBytes(runPath, badRun); File.WriteAllBytes(flowPath, badFlow); }
        else { File.Delete(runPath); File.Delete(flowPath); }
        var batch = Restore(captured.Batch);
        var successor = Host();
        Assert.True(await successor.RestorePriorityPlanRecoveryAsync(batch));
        var restored = successor.Runs.Load(captured.Run.RunId)!;
        Assert.Equal(captured.Run.RunId, restored.RunId);
        Assert.Equal(captured.Run.WireRunId, restored.WireRunId);
        Assert.Equal(captured.Run.StopAuthority, restored.StopAuthority);
        Assert.Equal(captured.Run.CurrentSubmission!.Key, restored.CurrentSubmission!.Key);
        Assert.Equal(captured.Run.CurrentSubmission.JobId, restored.CurrentSubmission.JobId);
        Assert.Equal(captured.Run.WorkflowRevision, successor.Workflows.LoadSnapshot(restored.WorkflowId).Revision);
        Assert.Equal(captured.Workflow, File.ReadAllBytes(flowPath));
        Assert.Single(successor.Runs.List());
        if (corrupt)
        {
            Assert.Contains(Directory.GetFiles(Path.Combine(_root, "runs", "_backup")), path => File.ReadAllBytes(path).SequenceEqual(badRun));
            Assert.Contains(Directory.GetFiles(Path.Combine(_root, "flows", "_backup")), path => File.ReadAllBytes(path).SequenceEqual(badFlow));
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task LivingOriginalOwnerOrStoppedSignalCannotRestoreMissingProgress(bool livingOwner)
    {
        var captured = Capture();
        var batch = Restore(captured.Batch, livingOwner);
        File.Delete(Path.Combine(_root, "runs", captured.Run.RunId + ".run.json"));
        if (!livingOwner) captured.Batch.RecoveryStopSignal!.Stop();
        Assert.False(await Host().RestorePriorityPlanRecoveryAsync(batch));
        Assert.False(File.Exists(Path.Combine(_root, "runs", captured.Run.RunId + ".run.json")));
    }

    [Fact]
    public async Task DurableStoppedRecordWinsOverOldBatchSnapshot()
    {
        var captured = Capture();
        captured.Host.Runs.UpdateMergingIf(captured.Run.RunId, run => { run.StopRequested = true; return true; }, out _);
        var batch = Restore(captured.Batch);
        var successor = Host();
        Assert.False(await successor.RestorePriorityPlanRecoveryAsync(batch));
        Assert.True(batch.IsCancellationRequested);
        Assert.True(successor.Runs.Load(captured.Run.RunId)!.StopRequested);
    }

    [Fact]
    public async Task NewValidWorkflowIsNotOverwrittenWithCapturedContent()
    {
        var captured = Capture();
        var changed = captured.Host.Workflows.LoadSnapshot(captured.Run.WorkflowId);
        changed.Document.Name = "用户新内容";
        captured.Host.Workflows.Save(changed.Document, changed.Revision);
        var path = Path.Combine(_root, "flows", captured.Run.WorkflowId + ".flow.json");
        var before = File.ReadAllBytes(path);
        File.Delete(Path.Combine(_root, "runs", captured.Run.RunId + ".run.json"));
        await Assert.ThrowsAsync<WorkflowRevisionConflictException>(() => Host().RestorePriorityPlanRecoveryAsync(Restore(captured.Batch)));
        Assert.Equal(before, File.ReadAllBytes(path));
    }

    [Fact]
    public async Task IntactRecordProgressAdvanceKeepsItsCurrentPositionAndRevision()
    {
        var captured = Capture();
        var pinned = captured.Batch.ManagedPlanSnapshot!;
        captured.Host.Runs.UpdateMergingIf(captured.Run.RunId,
            run => { run.Note = "current progress survives"; return true; }, out var advanced);
        captured.Host.CapturePriorityPlanRecovery(captured.Batch);
        var refreshed = JsonSerializer.Deserialize<WorkflowRunRecord>(captured.Batch.ManagedPlanSnapshot!.RunJson)!;
        var original = JsonSerializer.Deserialize<WorkflowRunRecord>(pinned.RunJson)!;
        Assert.Equal(advanced!.RecordRevision, refreshed.RecordRevision);
        Assert.Equal("current progress survives", refreshed.Note);
        Assert.Equal(original.WireRunId, refreshed.WireRunId);
        Assert.Equal(original.WorkflowId, refreshed.WorkflowId);
        Assert.Equal(original.StopAuthority, refreshed.StopAuthority);
        Assert.Equal(original.WorkflowRevision, refreshed.WorkflowRevision);
        var successor = Host();
        Assert.True(await successor.RestorePriorityPlanRecoveryAsync(Restore(captured.Batch)));
        var current = successor.Runs.Load(captured.Run.RunId)!;
        Assert.Equal(advanced!.RecordRevision, current.RecordRevision);
        Assert.Equal("current progress survives", current.Note);
        Assert.Equal(captured.Run.CurrentSubmission!.Key, current.CurrentSubmission!.Key);
    }

    [Theory]
    [InlineData("wire")]
    [InlineData("workflow")]
    [InlineData("authority")]
    [InlineData("workflow_revision")]
    public async Task IntactRecordCannotReplacePinnedIdentityOrOriginalArrangement(string changed)
    {
        var captured = Capture();
        var pinned = captured.Batch.ManagedPlanSnapshot;
        var current = captured.Host.Runs.Load(captured.Run.RunId)!;
        if (changed == "wire") current.WireRunId = Guid.NewGuid().ToString("N");
        if (changed == "workflow") current.WorkflowId = "wf-different-arrangement";
        if (changed == "authority") current.StopAuthority = current.StopAuthority! with { IntentId = "different-intent" };
        if (changed == "workflow_revision")
        {
            var flow = captured.Host.Workflows.LoadSnapshot(current.WorkflowId);
            flow.Document.Name = "new arrangement with matching run revision";
            current.WorkflowRevision = captured.Host.Workflows.Save(flow.Document, flow.Revision);
        }
        var runPath = Path.Combine(_root, "runs", captured.Run.RunId + ".run.json");
        var flowPath = Path.Combine(_root, "flows", captured.Run.WorkflowId + ".flow.json");
        // Fixture models an externally replaced record; normal RunStore writers cannot change frozen identities.
        File.WriteAllText(runPath, JsonSerializer.Serialize(current));
        var runBytes = File.ReadAllBytes(runPath);
        var flowBytes = File.ReadAllBytes(flowPath);
        captured.Host.CapturePriorityPlanRecovery(captured.Batch);
        Assert.Equal(pinned, captured.Batch.ManagedPlanSnapshot);
        await Assert.ThrowsAsync<InvalidDataException>(() => Host().RestorePriorityPlanRecoveryAsync(Restore(captured.Batch)));
        Assert.Equal(runBytes, File.ReadAllBytes(runPath));
        Assert.Equal(flowBytes, File.ReadAllBytes(flowPath));
    }

    [Theory]
    [InlineData("future_schema")]
    [InlineData("uncommitted_migration")]
    public async Task ValidJsonWithUnknownSchemaOrUncommittedMigrationIsPreserved(string changed)
    {
        var captured = Capture();
        var flowPath = Path.Combine(_root, "flows", captured.Run.WorkflowId + ".flow.json");
        var json = System.Text.Json.Nodes.JsonNode.Parse(File.ReadAllText(flowPath))!;
        if (changed == "future_schema") json["schemaVersion"] = WorkflowDocumentSchema.Version + 1;
        else json["nativeMigrationTransactionId"] = "uncommitted-original-transaction";
        File.WriteAllText(flowPath, json.ToJsonString());
        var before = File.ReadAllBytes(flowPath);
        File.Delete(Path.Combine(_root, "runs", captured.Run.RunId + ".run.json"));
        await Assert.ThrowsAsync<WorkflowRevisionConflictException>(() => Host().RestorePriorityPlanRecoveryAsync(Restore(captured.Batch)));
        Assert.Equal(before, File.ReadAllBytes(flowPath));
        Assert.False(File.Exists(Path.Combine(_root, "runs", captured.Run.RunId + ".run.json")));
    }

    public void Dispose()
    {
        foreach (var batch in _batches) batch.RecoveryStopSignal?.Dispose();
        foreach (var host in _hosts) host.ShutdownAsync().GetAwaiter().GetResult();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }
}
