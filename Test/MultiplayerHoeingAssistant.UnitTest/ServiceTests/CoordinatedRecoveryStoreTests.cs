using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class CoordinatedRecoveryStoreTests
{
    private static OnlineHoeingBatch Batch()
    {
        var start = new CoordinatedBatchSnapshot("recovery-" + Guid.NewGuid().ToString("N"), 18, 0, 0, 0, "running", "")
        {
            Protocol = "hoeing.start.v2", PriorityRecovery = true, StateContinuation = true,
            ParticipantUids = ["self"], Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
                { ["self"] = [new("原配置", "group")] }
        };
        return new(start, "self", new() { Policy = TaskConflictPolicy.RunSpecified, SpecifiedTaskName = "绑定后续" })
        {
            StateContinuationEnabled = true, ExecutionEpoch = new() { ProcessId = 100, StartTicksUtc = 200 },
            StateContinuationStopVersion = 3, TakeoverTicket = "original-ticket", Prepared = true, TakenOver = true,
            RecoveryProgress = new(0, 0, "takeoverConfirmed", "", "", BatchItemState.Submitted,
                Guid.NewGuid().ToString("N"), 0)
        };
    }

    [Fact]
    public async Task RestartReconnectsOriginalJob_WithoutPrepareTakeoverOrSecondSubmit()
    {
        var original = Batch();
        original.FreezeConfigurationRevision("原配置", false, "frozen-revision");
        var directory = Path.Combine(Path.GetTempPath(), "hoeing-recovery", Guid.NewGuid().ToString("N"));
        var store = new CoordinatedRecoveryStore(directory, "same-room-and-instance");
        store.Save(original);
        Assert.Null(store.Load(original.Start!, "self", (_, _) => true));
        Assert.Null(store.Load(original.Start!, "self", (_, _) => null));
        var saved = store.Load(original.Start!, "self", (_, _) => false);
        Assert.NotNull(saved);
        var restored = OnlineHoeingBatch.Restore(saved!);
        Assert.Equal(original.Token, restored.Token);
        Assert.Equal(original.TakeoverTicket, restored.TakeoverTicket);
        Assert.Equal(3, restored.StateContinuationStopVersion);
        Assert.Equal("frozen-revision", restored.ConfigRevisions["group:原配置"]);
        var job = saved!.Progress!.JobId!;
        var key = CoordinatedBatchLoop.RequestKey(restored.BatchId, "self", 0, 0);
        var queries = 0;
        var starts = 0;
        var prepares = 0;
        var takeovers = 0;
        var finalizations = 0;
        var epoch = restored.ExecutionEpoch!;
        var result = await new CoordinatedBatchLoop(restored, new()
        {
            StateContinuationEnabled = true, PollInterval = TimeSpan.FromMilliseconds(1),
            Prepare = _ => { prepares++; return Task.CompletedTask; },
            Takeover = _ => { takeovers++; return Task.CompletedTask; },
            Exchange = (_, result, _, _, _, _) => Task.FromResult(restored.Start! with
                { Phase = result == "succeeded" ? "completed" : "running", Index = result == "succeeded" ? 1 : 0 }),
            ReadJobs = _ => Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = epoch }),
            ReadBoundEpoch = () => epoch,
            ResolveOriginal = (_, _) =>
            {
                queries++;
                return Task.FromResult<BgiTaskQueueStatus?>(new()
                {
                    TaskHandle = job, Epoch = epoch, IdempotencyKey = key, Status = "completed",
                    ExecutionExitConfirmed = true
                });
            },
            ReadTerminal = (_, _) => throw new InvalidOperationException("Must query the retained original key"),
            Submit = (_, _, _) => { starts++; throw new InvalidOperationException("Original job must not be restarted"); },
            CancelOwned = (_, _) => throw new InvalidOperationException("Recovery must not cancel the original job"),
            Acknowledge = (_, _, _, _) => Task.FromResult(true),
            ReleaseTicket = (_, _, _) => Task.FromResult(true),
            FinalizeLocal = (_, _) => { finalizations++; return Task.CompletedTask; },
            OriginalProcessAlive = _ => true
        }).RunAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result);
        Assert.True(queries > 0);
        Assert.Equal(0, starts);
        Assert.Equal(0, prepares);
        Assert.Equal(0, takeovers);
        Assert.Equal(1, finalizations);
    }

    private sealed class TestClock : TimeProvider
    {
        internal long Ticks = 1;
        public override long TimestampFrequency => TimeSpan.TicksPerSecond;
        public override long GetTimestamp() => Ticks;
        internal void Advance(TimeSpan time) => Ticks += time.Ticks;
    }

    [Fact]
    public async Task RestoredPreparedStageMustPerformActualTakeoverExactlyOnce()
    {
        var original = Batch();
        original.TakenOver = false; original.TakeoverTicket = null;
        original.RecoveryProgress = new(0, 0, "prepared", "", "", null, null, 0);
        var batch = OnlineHoeingBatch.Restore(original.CaptureRecovery());
        var epoch = batch.ExecutionEpoch!;
        var rounds = 0; var preparations = 0; var takeovers = 0; var starts = 0;
        string? job = null; string? key = null;
        var result = await new CoordinatedBatchLoop(batch, new()
        {
            StateContinuationEnabled = true, PollInterval = TimeSpan.FromMilliseconds(1),
            Prepare = _ => { preparations++; return Task.CompletedTask; },
            Takeover = _ => { takeovers++; batch.TakeoverTicket = "actual-recovered-takeover"; return Task.CompletedTask; },
            Exchange = (stage, result, _, _, _, _) => Task.FromResult(batch.Start! with
            {
                Phase = result == "succeeded" ? "completed" : ++rounds < 3 ? "preparing"
                    : stage == "takeoverConfirmed" ? "running" : "takeover",
                Index = result == "succeeded" ? 1 : 0
            }),
            ReadJobs = _ => Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = epoch }),
            ReadBoundEpoch = () => epoch,
            ResolveOriginal = (requestKey, _) => Task.FromResult<BgiTaskQueueStatus?>(new()
                { Epoch = epoch, IdempotencyKey = requestKey, TaskHandle = job,
                    Status = job is null ? "not_found" : "completed", ExecutionExitConfirmed = job is not null }),
            ReadTerminal = (_, _) => throw new InvalidOperationException("Use retained key"),
            Submit = (item, _, _) =>
            {
                Assert.Equal("actual-recovered-takeover", batch.TakeoverTicket);
                starts++; key = item.RequestKey; job = Guid.NewGuid().ToString("N");
                return Task.FromResult(new BgiTaskSubmitResult { Success = true, Status = "queued", TaskHandle = job });
            },
            CancelOwned = (_, _) => throw new InvalidOperationException("No cancellation expected"),
            ReleaseTicket = (_, _, _) => Task.FromResult(true), Acknowledge = (_, _, _, _) => Task.FromResult(true),
            OriginalProcessAlive = _ => true, FinalizeLocal = (_, _) => Task.CompletedTask
        }).RunAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result); Assert.Equal(0, preparations); Assert.Equal(1, takeovers); Assert.Equal(1, starts);
        Assert.NotNull(key);
    }

    [Fact]
    public async Task RecoveredObservationRestartsBothContinuousHangBudgets()
    {
        var batch = OnlineHoeingBatch.Restore(Batch().CaptureRecovery());
        var epoch = batch.ExecutionEpoch!;
        var job = batch.RecoveryProgress!.JobId!;
        var key = CoordinatedBatchLoop.RequestKey(batch.BatchId, "self", 0, 0);
        var clock = new TestClock(); var round = 0; var terminations = 0; var resets = 0;
        bool Observed() => round is 3 or >= 6;
        using var monitor = new BgiProcessMonitor("unused");
        var now = DateTime.UtcNow;
        Assert.False(monitor.ObserveCoordinatedWindow(epoch, true, now));
        monitor.ResetCoordinatedHangObservation(epoch);
        Assert.False(monitor.ObserveCoordinatedWindow(epoch, true, now.AddHours(1)));
        Assert.False(monitor.ObserveCoordinatedWindow(epoch, true, now.AddHours(1).AddSeconds(29)));
        Assert.True(monitor.ObserveCoordinatedWindow(epoch, true, now.AddHours(1).AddSeconds(30)));
        var result = await new CoordinatedBatchLoop(batch, new()
        {
            StateContinuationEnabled = true, PollInterval = TimeSpan.FromMilliseconds(1), Clock = clock,
            Prepare = _ => throw new InvalidOperationException(), Takeover = _ => throw new InvalidOperationException(),
            Exchange = (_, result, _, _, _, _) =>
            {
                round++;
                if (round is 3 or 5 or 6) clock.Advance(TimeSpan.FromHours(1));
                return Task.FromResult(batch.Start! with { Phase = result == "succeeded" ? "completed" : "running",
                    Index = result == "succeeded" ? 1 : 0 });
            },
            ReadJobs = _ => Task.FromResult<BgiJobListSnapshot?>(Observed() ? new() { Epoch = epoch } : null),
            ReadBoundEpoch = () => epoch,
            ResolveOriginal = (_, _) => Task.FromResult<BgiTaskQueueStatus?>(Observed() ? new()
                { TaskHandle = job, Epoch = epoch, IdempotencyKey = key,
                    Status = round >= 7 ? "completed" : "running", ExecutionExitConfirmed = round >= 7 } : null),
            ReadTerminal = (_, _) => Task.FromResult<BgiTaskQueueStatus?>(null),
            ExecutorObserved = value => { resets++; monitor.ResetCoordinatedHangObservation(value); },
            TerminateUnresponsiveOwner = (_, _) => { Assert.Equal(5, round); terminations++; return Task.FromResult(false); },
            Submit = (_, _, _) => throw new InvalidOperationException("Original job must not restart"),
            CancelOwned = (_, _) => throw new InvalidOperationException("No cancellation expected"),
            ReleaseTicket = (_, _, _) => Task.FromResult(true), Acknowledge = (_, _, _, _) => Task.FromResult(true),
            OriginalProcessAlive = _ => true, FinalizeLocal = (_, _) => Task.CompletedTask
        }).RunAsync().WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(result); Assert.Equal(1, terminations); Assert.True(resets >= 2);
    }

    [Fact]
    public void StopWithLostCheckpointRemainsStoppedThroughTheSurvivingPeerHandle()
    {
        var batch = Batch();
        batch.EnsureRecoveryStopSignal();
        using var bgi = Mistletoe.Shared.CoordinatedStopSignal.Open(batch.RecoveryStopSignal!.Name);
        Assert.NotNull(bgi);
        var directory = Path.Combine(Path.GetTempPath(), "hoeing-recovery", Guid.NewGuid().ToString("N"));
        var store = new CoordinatedRecoveryStore(directory, "room");
        store.Save(batch); // Durable record still says not stopped, as with a failed later write.
        bgi!.Stop();
        var saved = store.Load(batch.Start!, "self", (_, _) => false)!;
        Assert.False(saved.Stopped);
        var restored = OnlineHoeingBatch.Restore(saved);
        Assert.True(restored.IsCancellationRequested);
        Assert.True(restored.ExplicitStopRequested);
        batch.MarkSettled();
        restored.MarkSettled();
    }

    [Fact]
    public async Task DeadProcessWaitsForSharedAttempt_ThenUsesNewEpochAndGameRoomIdentity()
    {
        var original = Batch();
        var batch = OnlineHoeingBatch.Restore(original.CaptureRecovery());
        var oldEpoch = original.ExecutionEpoch!;
        var newEpoch = new BgiEpoch { ProcessId = 101, StartTicksUtc = 300 };
        var replacementReady = false;
        var recoveries = 0; var starts = 0; var failedReports = 0;
        string? key = null; string? room = null; string? job = null;
        var result = await new CoordinatedBatchLoop(batch, new()
        {
            StateContinuationEnabled = true, PollInterval = TimeSpan.FromMilliseconds(1),
            Prepare = _ => throw new InvalidOperationException("Original stage must not restart"),
            Takeover = _ => throw new InvalidOperationException("Recovery owns replacement preparation"),
            Exchange = (_, result, _, _, attempt, _) =>
            {
                if (result == "failed") failedReports++;
                var completed = result == "succeeded";
                return Task.FromResult(batch.Start! with { Phase = completed ? "completed" : "running",
                    Index = completed ? 1 : 0, Attempt = completed || failedReports >= 3 ? 1 : 0 });
            },
            OriginalProcessAlive = epoch => epoch.ProcessId != oldEpoch.ProcessId,
            RecoverProcess = (previous, _) =>
            {
                Assert.True(failedReports >= 3);
                Assert.Equal(oldEpoch.ProcessId, previous.ProcessId);
                recoveries++; replacementReady = true;
                return Task.FromResult(newEpoch);
            },
            ReadBoundEpoch = () => replacementReady ? newEpoch : oldEpoch,
            ReadJobs = _ => Task.FromResult<BgiJobListSnapshot?>(new() { Epoch = replacementReady ? newEpoch : oldEpoch }),
            ResolveOriginal = (requestKey, _) => Task.FromResult<BgiTaskQueueStatus?>(new()
                { IdempotencyKey = requestKey, TaskHandle = job, Epoch = newEpoch,
                    Status = job is null ? "not_found" : "completed", ExecutionExitConfirmed = job is not null }),
            ReadTerminal = (_, _) => throw new InvalidOperationException("Use original key"),
            Submit = (item, epoch, _) =>
            {
                Assert.True(replacementReady); Assert.Equal(newEpoch.ProcessId, epoch.ProcessId);
                starts++; key = item.RequestKey; room = item.GameBatchId; job = Guid.NewGuid().ToString("N");
                return Task.FromResult(new BgiTaskSubmitResult { Success = true, Status = "queued", TaskHandle = job });
            },
            CancelOwned = (_, _) => throw new InvalidOperationException("Dead process has no live job to cancel"),
            ReleaseTicket = (_, _, _) => Task.FromResult(true),
            Acknowledge = (_, _, _, _) => Task.FromResult(true),
            FinalizeLocal = (_, _) => Task.CompletedTask
        }).RunAsync().WaitAsync(TimeSpan.FromSeconds(3));
        Assert.True(result);
        Assert.Equal(1, starts); Assert.Equal(1, recoveries);
        Assert.Equal(CoordinatedBatchLoop.RequestKey(batch.BatchId, "self", 0, 1), key);
        Assert.Equal(batch.BatchId + "/0/1", room);
    }

    [Fact]
    public void StopIsStoredBeforeReturn_AndCannotRestoreAsAnExecutableBatch()
    {
        var batch = Batch();
        var directory = Path.Combine(Path.GetTempPath(), "hoeing-recovery", Guid.NewGuid().ToString("N"));
        var store = new CoordinatedRecoveryStore(directory, "room");
        batch.PersistRecovery = () => store.Save(batch);
        batch.PersistRecovery();
        batch.Cancel();
        var saved = store.Load(batch.Start!, "self", (_, _) => false);
        Assert.True(saved!.Stopped);
        var restored = OnlineHoeingBatch.Restore(saved);
        Assert.True(restored.ExplicitStopRequested);
        Assert.True(restored.IsCancellationRequested);
        batch.MarkSettled();
        Assert.Null(store.Load(batch.Start!, "self", (_, _) => false));
    }

    [Fact]
    public void MissingJournalRecoversFrozenIdentityFromSurvivingPeer_StillRejectsLiveOwner()
    {
        var batch = Batch();
        var store = new CoordinatedRecoveryStore(Path.Combine(Path.GetTempPath(), "unused-" + Guid.NewGuid()), "room");
        var checkpoint = store.CaptureForPeer(batch);
        var response = "{\"assistantRecovery\":" + checkpoint + "}";
        Assert.Null(store.Load(batch.Start!, "self", (_, _) => false));
        Assert.Null(store.ReadSurvivingPeer(response, batch.Start!, "self", (_, _) => true));
        var restored = OnlineHoeingBatch.Restore(store.ReadSurvivingPeer(response, batch.Start!, "self", (_, _) => false)!);
        Assert.Equal(batch.Token, restored.Token);
        Assert.Equal(batch.Policy!.SpecifiedTaskName, restored.Policy!.SpecifiedTaskName);
        Assert.Equal(batch.RecoveryProgress!.JobId, restored.RecoveryProgress!.JobId);
        Assert.Equal(batch.StateContinuationStopVersion, restored.StateContinuationStopVersion);
    }

    [Fact]
    public void SaveFailureDoesNotCancelLiveBatch_AndCorruptRecordIsNotTreatedAsFreshWork()
    {
        var directory = Path.Combine(Path.GetTempPath(), "hoeing-recovery", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        var obstruction = Path.Combine(directory, "file");
        File.WriteAllText(obstruction, "keep");
        var batch = Batch();
        var store = new CoordinatedRecoveryStore(obstruction, "room");
        store.Save(batch);
        Assert.False(batch.IsCancellationRequested);
        Assert.Equal("keep", File.ReadAllText(obstruction));
        store = new CoordinatedRecoveryStore(Path.Combine(directory, "records"), "room");
        store.Save(batch);
        var path = Directory.GetFiles(Path.Combine(directory, "records"), "*.json").Single();
        File.WriteAllText(path, "{damaged");
        Assert.Null(store.Load(batch.Start!, "self", (_, _) => false));
        Assert.Equal("{damaged", File.ReadAllText(path));
        Assert.Null(new CoordinatedRecoveryStore(Path.Combine(directory, "records"), "other-room")
            .Load(batch.Start!, "self", (_, _) => false));
    }
}
