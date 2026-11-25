using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

/// <summary>Only exact native/whole-job success and the same batch's cleaned authority may dispatch the new action.</summary>
public sealed class RoomOrchestrationCompletionEvidenceTests
{
    private static readonly BgiEpoch Epoch = new() { ProcessId = 10, StartTicksUtc = 700 };

    private static OnlineHoeingBatch Batch()
    {
        var items = new CoordinatedHoeingBinding[] { new("native", "group"), new("tail", "group") };
        var start = new CoordinatedBatchSnapshot("batch-room", 19, 0, 0, 1, "registering", "")
        {
            Protocol = "hoeing.start.v2", Source = "online", StateContinuation = true, PriorityRecovery = true,
            ParticipantUids = ["self", "peer"], Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
            { ["self"] = items, ["peer"] = items }
        };
        var result = new OnlineHoeingBatch(start, "self", new()
        {
            Policy = TaskConflictPolicy.Stop,
            RoomSwitch = new("alpha", "https://alpha.example", "ALPHA1", "beta", "https://beta.example", "BETAA1", "乙")
        });
        result.ExecutionEpoch = Epoch;
        result.StateContinuationStopVersion = 7;
        result.TakenOver = true;
        return result;
    }

    private static void ObserveJob(OnlineHoeingBatch batch, int index, int native, bool exact = true, int attempt = 0)
    {
        var id = Guid.NewGuid().ToString("N");
        var key = CoordinatedBatchLoop.RequestKey(batch.BatchId, batch.SelfUid, index, attempt);
        batch.RecordRoomBindingSuccess(index, attempt, id, key, Epoch, new()
        {
            TaskHandle = id, IdempotencyKey = exact ? key : "other", Epoch = Epoch,
            Status = "completed", ExecutionExitConfirmed = true, HoeingEvidence = new()
            { Started = native, Completed = native, Sealed = true, JobResult = "Ran" }
        });
    }

    private static void CompleteLocal(OnlineHoeingBatch batch)
    {
        batch.MarkLocalSuccess(Epoch);
        batch.RecordRoomReleaseBoundary();
        batch.MarkLocalExecutionReleased();
        batch.LocalCleanupConfirmed = true;
        batch.PolicyTicketReleased = true;
    }

    private static CoordinatedBatchSnapshot Authority(OnlineHoeingBatch batch, string reason = "", string stage = "cleaned")
        => batch.Start! with
        {
            Phase = "completed", Reason = reason, Revision = 90, Index = batch.Bindings.Count,
            Members = [new("self", stage, "") { Result = "succeeded" }, new("peer", "takeoverConfirmed", "") { Result = "succeeded" }]
        };

    [Theory]
    [InlineData("")]
    [InlineData("shared_recovery")]
    public void WholeBatchAndActualNativeSuccessClaimOnceEvenBeforeMainLoopSetsSucceeded(string reason)
    {
        var batch = Batch();
        ObserveJob(batch, 0, 2);
        ObserveJob(batch, 1, 0);
        CompleteLocal(batch);
        batch.ObserveRoomCompletionAuthority(Authority(batch, reason, stage: "takeoverConfirmed"));
        Assert.False(batch.TryClaimRoomCompletion()); // The main loop's terminal isn't a cleaned receipt.
        batch.ObserveRoomCompletionAuthority(Authority(batch, reason) with { Revision = 91 });
        Assert.False(batch.CoordinatedSucceeded); // Cleanup callback can precede the original execution loop's flag.
        Assert.True(batch.TryClaimRoomCompletion());
        Assert.False(batch.TryClaimRoomCompletion());
    }

    [Theory]
    [InlineData("ordinaryOnly")]
    [InlineData("wrongJobIdentity")]
    [InlineData("missingBinding")]
    [InlineData("peerErrors")]
    [InlineData("stopped")]
    [InlineData("superseded")]
    [InlineData("retryWithoutNewEvidence")]
    public void CannotClaimOnNoNativeErrorsMissingIdentityOrRevokedAuthority(string scenario)
    {
        var batch = Batch();
        ObserveJob(batch, 0, scenario == "ordinaryOnly" ? 0 : 1, exact: scenario != "wrongJobIdentity");
        if (scenario != "missingBinding") ObserveJob(batch, 1, 0);
        if (scenario == "retryWithoutNewEvidence") batch.ResetRoomBindingEvidence(0);
        CompleteLocal(batch);
        batch.ObserveRoomCompletionAuthority(Authority(batch, scenario == "peerErrors" ? "completed_with_task_errors:peer" : ""));
        if (scenario == "stopped") batch.Cancel();
        if (scenario == "superseded") batch.RevokeRoomCompletion();
        Assert.False(batch.TryClaimRoomCompletion());
    }

    [Fact]
    public void IncomingOrchestrationBatchKeepsItsOriginalStopFenceWhileWaitingForRegistration()
    {
        var snapshot = new CoordinatedBatchSnapshot("incoming", 13, 0, 0, 1, "registering", "")
        {
            Protocol = "hoeing.start.v2", Source = "online", StateContinuation = true, PriorityRecovery = true,
            ParticipantUids = ["self"], Bindings = new Dictionary<string, IReadOnlyList<CoordinatedHoeingBinding>>
            { ["self"] = [new("乙房任务", "group")] }
        };
        var batch = new OnlineHoeingBatch(snapshot, "self", new());
        batch.FreezeIncomingRoomOrchestrationAuthority("room-orch-a", Epoch, 7);
        Assert.False(batch.Prepared);
        Assert.True(batch.MatchesIncomingRoomOrchestrationFence(Epoch, 7));
        Assert.False(batch.MatchesIncomingRoomOrchestrationFence(Epoch, 8)); // F11 arrived before preparation.
        Assert.False(batch.MatchesIncomingRoomOrchestrationFence(new BgiEpoch { ProcessId = 11, StartTicksUtc = 701 }, 7));
        Assert.Throws<InvalidOperationException>(() => batch.FreezeIncomingRoomOrchestrationAuthority("room-orch-a", Epoch, 8));
        var restored = OnlineHoeingBatch.Restore(batch.CaptureRecovery());
        Assert.Equal("room-orch-a", restored.IncomingRoomOrchestrationIntentId);
        Assert.False(restored.MatchesIncomingRoomOrchestrationFence(Epoch, 8));
    }

    [Fact]
    public void RecoveryPreservesEarlierNativeReceiptAndDoesNotReplaceClaimedAction()
    {
        var batch = Batch();
        ObserveJob(batch, 0, 2);
        ObserveJob(batch, 1, 0);
        CompleteLocal(batch);
        var snapshot = batch.CaptureRecovery();
        var restored = OnlineHoeingBatch.Restore(snapshot);
        restored.ObserveRoomCompletionAuthority(Authority(restored));
        Assert.True(restored.TryClaimRoomCompletion());
        var claimed = OnlineHoeingBatch.Restore(restored.CaptureRecovery());
        claimed.ObserveRoomCompletionAuthority(Authority(claimed));
        Assert.False(claimed.TryClaimRoomCompletion());
    }
}
