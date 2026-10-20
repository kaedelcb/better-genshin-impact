using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class RunStorePriorityContinuationTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "priority-progress-" + Guid.NewGuid().ToString("N"));
    public void Dispose() { try { Directory.Delete(_root, true); } catch { } }

    private static WorkflowRunRecord Running(RunStore store)
    {
        var run = store.CreateRun("original-plan", "original-revision");
        run.State = WorkflowRunState.Running;
        run.Cursor = new() { NodeId = "B", Attempt = 1 };
        run.CurrentSubmission = new()
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "B", 0, 0, 1),
            NodeId = "B", Attempt = 1, JobId = Guid.NewGuid().ToString("D"), Epoch = "42:99",
            WireRunId = run.WireRunId, SendAttempted = true, Intent = SubmitIntentState.Accepted,
            Fingerprint = "original-request", ExpiresAtUtc = "2030-01-01T00:00:00Z",
        };
        store.Update(run);
        return run;
    }

    [Fact]
    public void RetainedProgressStillRejectsDifferentJobAndNewOverlappingSubmission()
    {
        var store = new RunStore(_root);
        var run = Running(store);
        store.RetainPriorityProgress(run);
        store.PublishFaultForTest = _ => new IOException("unwritable");
        var altered = store.Load(run.RunId)!;
        altered.CurrentSubmission!.JobId = Guid.NewGuid().ToString("D");
        Assert.Throws<RunRecordConflictException>(() => store.Update(altered));
        var overlap = store.Load(run.RunId)!;
        Assert.Throws<InvalidOperationException>(() => store.RecordIntent(overlap,
            new WorkflowSubmission { Key = "another-attempt", NodeId = "B", Attempt = 2 }));
        Assert.Equal(run.CurrentSubmission!.JobId, store.Load(run.RunId)!.CurrentSubmission!.JobId);
        Assert.Equal(run.RecordRevision, store.Load(run.RunId)!.RecordRevision);
    }

    [Fact]
    public void StopAndRevisionRemainAuthoritativeWhilePublishingFails()
    {
        var store = new RunStore(_root);
        var run = Running(store);
        store.RetainPriorityProgress(run);
        store.PublishFaultForTest = _ => new IOException("unwritable");
        var stopped = store.Load(run.RunId)!;
        stopped.StopRequested = true;
        store.Update(stopped);
        run.Note = "stale";
        Assert.Throws<RunRecordConflictException>(() => store.Update(run));
        var clearStop = store.Load(run.RunId)!;
        clearStop.StopRequested = false;
        store.Update(clearStop);
        Assert.True(clearStop.StopRequested);
        Assert.True(store.Load(run.RunId)!.StopRequested);
        store.PublishFaultForTest = null;
        while (store.HasUnpublishedPriorityProgress(run.RunId))
            store.FlushPriorityProgress(run.RunId);
        Assert.True(new RunStore(_root).Load(run.RunId)!.StopRequested);
    }

    [Fact]
    public void ReleasedOwnerCannotUseRetainedProgressToPublishOrAdvance()
    {
        var leaseStore = new ArbitrationLeaseStore(Path.Combine(_root, "lease"));
        var acquired = leaseStore.TryAcquire("original");
        Assert.True(acquired.Success);
        var runs = new RunStore(Path.Combine(_root, "runs"), requireOwnership: true);
        runs.BindOwner(acquired.Ownership!);
        var run = Running(runs);
        runs.RetainPriorityProgress(run);
        runs.PublishFaultForTest = _ => new IOException("unwritable");
        var pending = runs.Load(run.RunId)!;
        pending.Note = "retained by original";
        runs.Update(pending);
        var lease = leaseStore.Read().File!;
        Assert.True(leaseStore.TryRelease(lease.Lease!.LeaseId, lease.Lease.OwnerEpoch, lease.Revision).Success);
        Assert.True(new ArbitrationLeaseStore(Path.Combine(_root, "lease")).TryAcquire("successor").Success);
        var before = File.ReadAllBytes(Path.Combine(_root, "runs", run.RunId + ".run.json"));
        var revoked = runs.Load(run.RunId)!;
        revoked.Note = "must not advance";
        Assert.Throws<RunRecordConflictException>(() => runs.Update(revoked));
        Assert.Throws<RunRecordConflictException>(() => runs.FlushPriorityProgress(run.RunId));
        Assert.Equal(pending.RecordRevision, runs.Load(run.RunId)!.RecordRevision);
        Assert.Equal(before, File.ReadAllBytes(Path.Combine(_root, "runs", run.RunId + ".run.json")));
    }

    [Fact]
    public void BacklogPublishesOneTransitionPerPulseAndAllowsRenewalAndStopBetweenPulses()
    {
        var now = DateTimeOffset.UtcNow;
        var start = now;
        var lease = new ArbitrationLeaseStore(Path.Combine(_root, "lease"), () => now, () => now - start);
        var acquired = lease.TryAcquire("original", ttlSeconds: 15);
        Assert.True(acquired.Success);
        var store = new RunStore(Path.Combine(_root, "runs"), requireOwnership: true);
        store.BindOwner(acquired.Ownership!);
        var run = Running(store);
        store.RetainPriorityProgress(run);
        store.PublishFaultForTest = _ => new IOException("unwritable");
        for (var i = 0; i < 12; i++)
        {
            run = store.Load(run.RunId)!;
            run.Note = "progress-" + i;
            store.Update(run);
        }
        store.PublishFaultForTest = null;
        var writes = 0;
        store.FileOperationFaultForTest = operation =>
        {
            if (operation == "publish") { writes++; now = now.AddSeconds(4); }
            return null;
        };
        for (var pulse = 0; pulse < 30 && store.HasUnpublishedPriorityProgress(run.RunId); pulse++)
        {
            Assert.True(lease.TryRenewLatest(acquired.Ownership!.LeaseId, acquired.Ownership.OwnerEpoch).Success);
            var previousWrites = writes;
            store.FlushPriorityProgress(run.RunId);
            Assert.Equal(previousWrites + 1, writes);
            if (pulse == 2)
            {
                Assert.True(store.UpdateMergingIf(run.RunId, current => { current.StopRequested = true; return true; }, out _));
                Assert.True(store.Load(run.RunId)!.StopRequested);
            }
        }
        Assert.False(store.HasUnpublishedPriorityProgress(run.RunId));
        store.VerifyBoundOwner();
        Assert.True(new RunStore(Path.Combine(_root, "runs")).Load(run.RunId)!.StopRequested);
    }

    [Fact]
    public void PendingProgressStoresOnlyNewFailureFactsInsteadOfRepeatedFullHistory()
    {
        var store = new RunStore(_root);
        var run = Running(store);
        store.RetainPriorityProgress(run);
        store.PublishFaultForTest = _ => new IOException("unwritable");
        long halfway = 0;
        for (var attempt = 0; attempt < 100; attempt++)
        {
            run = store.Load(run.RunId)!;
            run.Note += "failure retained; ";
            run.NodeOutcomes.Add(new() { NodeId = "B", Attempt = attempt, Result = "failed", Reason = new string('x', 200) });
            store.Update(run);
            if (attempt == 49) halfway = store.UnpublishedPriorityPayloadBytes(run.RunId);
        }
        Assert.Equal(100, store.Load(run.RunId)!.NodeOutcomes.Count);
        Assert.InRange(store.UnpublishedPriorityPayloadBytes(run.RunId), halfway, halfway * 23 / 10);
        store.PublishFaultForTest = null;
        for (var pulse = 0; pulse < 105 && store.HasUnpublishedPriorityProgress(run.RunId); pulse++)
            store.FlushPriorityProgress(run.RunId);
        Assert.False(store.HasUnpublishedPriorityProgress(run.RunId));
        var saved = new RunStore(_root).Load(run.RunId)!;
        Assert.Equal(run.RecordRevision, saved.RecordRevision);
        Assert.Equal(100, saved.NodeOutcomes.Count);
        Assert.Equal(run.Note, saved.Note);
    }
}
