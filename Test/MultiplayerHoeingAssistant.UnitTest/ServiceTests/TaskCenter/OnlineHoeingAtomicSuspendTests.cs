using System.Reflection;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests.TaskCenter;

public sealed class OnlineHoeingAtomicSuspendTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "online-atomic-suspend-" + Guid.NewGuid().ToString("N"));

    public void Dispose()
    {
        try { if (Directory.Exists(_root)) Directory.Delete(_root, true); } catch { }
    }

    private sealed record Fixture(TaskCenterHost Host, WorkflowRunRecord Run, Guid OriginalJob)
    {
        internal string Receipt(Guid? job = null) => JsonSerializer.Serialize(new
        {
            status = "suspended", workflowRunId = Run.WireRunId, nodeId = "B",
            originalJobId = job ?? OriginalJob, occurrence = 0, iteration = 0, attempt = 1,
            planCheckpointPhase = "inside", planNextTaskIndex = 24
        });

        internal void Park()
        {
            Assert.True(Host.Runs.UpdateMergingIf(Run.RunId, latest =>
            {
                latest.CurrentSubmission!.ObservedTerminal = "cancelled";
                latest.CurrentSubmission.ExecutionExitConfirmed = true;
                latest.CurrentSubmission.ExecutionExitDisposition = "preempted";
                latest.CurrentSubmission.EffectState = "cancelled";
                latest.State = WorkflowRunState.OnlineHoeingParking;
                latest.OnlineHoeingHandoff!.Phase = "parked";
                return true;
            }, out _));
        }
    }

    private async Task<Fixture> Prepare()
    {
        var workflows = new WorkflowStore(Path.Combine(_root, "flows"));
        var document = new WorkflowDocument
        {
            Name = "采集原节点", Activation = new() { Status = "active" },
            Nodes = [new WorkflowNode
            {
                NodeId = "B", Kind = "resource.configGroup",
                Ref = new() { Config = "采集", ConfigKey = "采集#key", Revision = "rev-1" }
            }]
        };
        workflows.Save(document, null);
        var host = new TaskCenterHost(Path.Combine(_root, "flows"), Path.Combine(_root, "runs"),
            Path.Combine(_root, "catalog.json"), () => null, null, null, () => (true, null),
            onlineEpochProvider: () => new BgiEpoch { ProcessId = 42, StartTicksUtc = 99 });
        var run = host.Runs.CreateRun(document.WorkflowId!, workflows.LoadSnapshot(document.WorkflowId!).Revision);
        var original = Guid.NewGuid();
        run.State = WorkflowRunState.Running;
        run.Cursor = new() { NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1 };
        run.CurrentSubmission = new()
        {
            Key = RunStore.DeriveSubmissionKey(run.RunId, "B", 0, 0, 1),
            NodeId = "B", Occurrence = 0, LoopIteration = 0, Attempt = 1,
            JobId = original.ToString("D"), Intent = SubmitIntentState.Accepted,
            SendAttempted = true, Epoch = "42:99", WireRunId = run.WireRunId,
            Fingerprint = "original-collection", ExpiresAtUtc = DateTimeOffset.UtcNow.AddMinutes(10).ToString("O"),
            RecordedAt = DateTimeOffset.UtcNow
        };
        host.Runs.Update(run);
        await host.CloseOnlineHoeingSendAsync();
        var active = new CommandExecutor.ExecutionIdentity(Guid.NewGuid(), 42, 99, Guid.Parse(run.WireRunId), original);
        var prepared = host.PrepareOnlineHoeing(7, new(true, true, 42, 99, active));
        Assert.True(prepared.Ready); Assert.Equal(run.RunId, prepared.ManagedRunId);
        Assert.Equal("prepared", host.Runs.Load(run.RunId)!.OnlineHoeingHandoff!.Phase);
        return new(host, run, original);
    }

    [Fact]
    public async Task NeverAcceptedPreparation_CanRefreshWithoutAdvancingOrStoppingOriginalPlan()
    {
        var fixture = await Prepare();
        var before = fixture.Host.Runs.Load(fixture.Run.RunId)!;
        Assert.True(fixture.Host.RefreshUnacceptedOnlineHoeingPreparation(7));
        Assert.True(fixture.Host.RefreshUnacceptedOnlineHoeingPreparation(7));
        var after = fixture.Host.Runs.Load(fixture.Run.RunId)!;
        Assert.Null(after.OnlineHoeingHandoff);
        Assert.Equal(fixture.OriginalJob, Guid.Parse(after.CurrentSubmission!.JobId!));
        Assert.Equal(before.CurrentSubmission!.Key, after.CurrentSubmission.Key);
        Assert.Equal(before.Cursor!.NodeId, after.Cursor!.NodeId);
        Assert.Equal(before.Cursor.Attempt, after.Cursor.Attempt);
        Assert.False(after.StopRequested);
    }

    [Fact]
    public async Task ParkedOrAcceptedHandoff_CannotBeDiscardedAsNeverAccepted()
    {
        var fixture = await Prepare();
        fixture.Park();
        Assert.False(fixture.Host.RefreshUnacceptedOnlineHoeingPreparation(7));
        Assert.True(fixture.Host.ConfirmOnlineHoeingSuspend(7, "ticket-7", fixture.Receipt()));
        Assert.False(fixture.Host.RefreshUnacceptedOnlineHoeingPreparation(7));
        Assert.Equal("ticket-7", fixture.Host.Runs.Load(fixture.Run.RunId)!.OnlineHoeingHandoff!.Ticket);
    }

    [Fact]
    public async Task RunnerParkingBetweenReceiptEntryAndAtomicMergePreservesTheSameOriginalAndCheckpoint()
    {
        var f = await Prepare();
        var storeGate = typeof(RunStore).GetField("_gate", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(f.Host.Runs)!;
        using var entered = new ManualResetEventSlim();
        using var finished = new ManualResetEventSlim();
        Exception? error = null;
        bool? confirmed = null;
        var snapshotLoads = 0;
        Thread? receiptThread = null;
        receiptThread = new Thread(() =>
        {
            entered.Set();
            try { confirmed = f.Host.ConfirmOnlineHoeingSuspend(7, "ticket-7", f.Receipt()); }
            catch (Exception ex) { error = ex; }
            finally { finished.Set(); }
        }) { IsBackground = true };
        f.Host.Runs.BeforeLoadForTest = runId =>
        {
            if (Thread.CurrentThread == receiptThread && runId == f.Run.RunId)
                Interlocked.Increment(ref snapshotLoads);
        };
        try
        {
            lock (storeGate)
            {
                receiptThread.Start();
                Assert.True(entered.Wait(TimeSpan.FromSeconds(3)));
                // The old implementation loads prepared outside this gate, then blocks on its merge.
                // The corrected implementation first waits at the merge, then reads the latest parked record.
                Assert.True(SpinWait.SpinUntil(() => (receiptThread.ThreadState & ThreadState.WaitSleepJoin) != 0,
                    TimeSpan.FromSeconds(3)), "receipt must be waiting for the held RunStore merge gate");
                Assert.False(finished.IsSet);
                f.Park(); // Monitor is reentrant: this update wins while receipt merging is held.
            }
            Assert.True(finished.Wait(TimeSpan.FromSeconds(3)));
            Assert.Null(error); Assert.True(confirmed);
            var saved = f.Host.Runs.Load(f.Run.RunId)!;
            Assert.Equal(WorkflowRunState.OnlineHoeingParking, saved.State);
            Assert.Equal("parked", saved.OnlineHoeingHandoff!.Phase);
            Assert.Equal("ticket-7", saved.OnlineHoeingHandoff.Ticket);
            Assert.Equal("inside", saved.OnlineHoeingHandoff.CheckpointPhase);
            Assert.Equal(24, saved.OnlineHoeingHandoff.NextTaskIndex);
            Assert.Equal(f.OriginalJob.ToString("D"), saved.CurrentSubmission!.JobId);
            Assert.Equal("cancelled", saved.CurrentSubmission.ObservedTerminal);
            Assert.True(saved.CurrentSubmission.ExecutionExitConfirmed);
            Assert.Equal(f.Run.CurrentSubmission!.Key, saved.CurrentSubmission.Key);
            Assert.Equal("B", saved.Cursor!.NodeId); Assert.Equal(1, saved.Cursor.Attempt);
            // A repeated receipt cannot roll parked back to suspended or lose the original recovery position.
            Assert.True(f.Host.ConfirmOnlineHoeingSuspend(7, "ticket-7", f.Receipt()));
            var repeated = f.Host.Runs.Load(f.Run.RunId)!.OnlineHoeingHandoff!;
            Assert.Equal("parked", repeated.Phase); Assert.Equal(24, repeated.NextTaskIndex);
            Assert.Equal("inside", repeated.CheckpointPhase); Assert.Equal("ticket-7", repeated.Ticket);
        }
        finally
        {
            f.Host.Runs.BeforeLoadForTest = null;
            receiptThread.Join(TimeSpan.FromSeconds(3));
        }
    }

    [Theory]
    [InlineData("different_job")]
    [InlineData("explicit_stop")]
    [InlineData("different_ticket")]
    public async Task AtomicLatestStateStillRejectsRealIdentityChangesAndExplicitStopWithoutOverwritingEvidence(string reason)
    {
        var f = await Prepare();
        f.Park();
        Assert.True(f.Host.Runs.UpdateMergingIf(f.Run.RunId, latest =>
        {
            latest.OnlineHoeingHandoff!.Ticket = "ticket-7";
            latest.OnlineHoeingHandoff.CheckpointPhase = "inside";
            latest.OnlineHoeingHandoff.NextTaskIndex = 24;
            if (reason == "explicit_stop") latest.StopRequested = true;
            return true;
        }, out _));
        var before = f.Host.Runs.Load(f.Run.RunId)!;
        var receipt = f.Receipt(reason == "different_job" ? Guid.NewGuid() : null);
        Assert.False(f.Host.ConfirmOnlineHoeingSuspend(7, reason == "different_ticket" ? "another-ticket" : "ticket-7", receipt));
        var after = f.Host.Runs.Load(f.Run.RunId)!;
        Assert.Equal(before.RecordRevision, after.RecordRevision);
        Assert.Equal("parked", after.OnlineHoeingHandoff!.Phase);
        Assert.Equal("ticket-7", after.OnlineHoeingHandoff.Ticket);
        Assert.Equal("inside", after.OnlineHoeingHandoff.CheckpointPhase);
        Assert.Equal(24, after.OnlineHoeingHandoff.NextTaskIndex);
        Assert.Equal(before.CurrentSubmission!.JobId, after.CurrentSubmission!.JobId);
        Assert.Equal(before.StopRequested, after.StopRequested);
    }

    [Fact]
    public async Task ResumePreparationReentryAfterLostReplyReusesOriginalKeyFingerprintAttemptAndExpiry()
    {
        var f = await Prepare();
        f.Park();
        Assert.True(f.Host.ConfirmOnlineHoeingSuspend(7, "ticket-7", f.Receipt()));
        var first = await f.Host.PreparePlanResumeAsync(f.Run.RunId, "ticket-7");
        Assert.NotNull(first);
        var saved = f.Host.Runs.Load(f.Run.RunId)!;
        Assert.Equal("resumeRequested", saved.OnlineHoeingHandoff!.Phase);
        Assert.Equal(2, saved.OnlineHoeingHandoff.ResumeAttempt);
        var originalKey = saved.OnlineHoeingHandoff.ResumeKey;
        var originalFingerprint = saved.OnlineHoeingHandoff.ResumeFingerprint;
        var originalExpiry = saved.OnlineHoeingHandoff.ResumeExpiresAtUtc;
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(3));
        var repeated = await f.Host.PreparePlanResumeAsync(f.Run.RunId, "ticket-7", cancellation.Token);
        Assert.NotNull(repeated);
        Assert.Equal(first.OrderBy(p => p.Key).Select(p => (p.Key, JsonSerializer.Serialize(p.Value))),
            repeated.OrderBy(p => p.Key).Select(p => (p.Key, JsonSerializer.Serialize(p.Value))));
        var after = f.Host.Runs.Load(f.Run.RunId)!;
        Assert.Equal(originalKey, after.OnlineHoeingHandoff!.ResumeKey);
        Assert.Equal(originalFingerprint, after.OnlineHoeingHandoff.ResumeFingerprint);
        Assert.Equal(originalExpiry, after.OnlineHoeingHandoff.ResumeExpiresAtUtc);
        Assert.Equal(2, after.OnlineHoeingHandoff.ResumeAttempt);
        Assert.Equal(f.Run.CurrentSubmission!.JobId, after.CurrentSubmission!.JobId);
        Assert.Equal(f.Run.CurrentSubmission.Key, after.CurrentSubmission.Key);
        Assert.Equal(1, after.Cursor!.Attempt);
    }
}
