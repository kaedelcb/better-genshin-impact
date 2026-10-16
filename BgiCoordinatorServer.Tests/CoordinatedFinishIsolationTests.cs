using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class CoordinatedFinishIsolationTests
{
    private readonly DateTime _now = DateTime.UtcNow;
    private static readonly string[] Uids = ["a", "b", "c", "d"];
    private CoordinatedBatchState Create()
    {
        var state = new CoordinatedBatchState(8, Uids, _now);
        foreach (var stage in new[] { "registered", "prepared", "takeoverConfirmed" })
            foreach (var uid in Uids) Send(state, uid, stage: stage);
        return state;
    }
    private CoordinatedBatchSnapshot Send(CoordinatedBatchState state, string uid, string result = "",
        int seconds = 0, int index = 0, int attempt = 0, string stage = "", string reason = "") =>
        state.Apply(uid, uid, state.BatchId, "group", index, attempt, result,
            _now.AddSeconds(seconds), stage, reason);

    [Theory]
    [InlineData("unknown")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("local_failed")]
    public void LateTelemetryCannotRevokeTheSameMembersConfirmedSuccess(string late)
    {
        var state = Create();
        Send(state, "a", "succeeded");
        var snapshot = Send(state, "a", late, reason: "old-process-unavailable");
        Assert.Equal("running", snapshot.Phase);
        Assert.Equal("succeeded", snapshot.Members.Single(m => m.Uid == "a").Result);
        Assert.Equal("old-process-unavailable", snapshot.Members.Single(m => m.Uid == "a").Reason);
        foreach (var uid in Uids.Skip(1)) snapshot = Send(state, uid, "succeeded");
        Assert.Equal("completed", snapshot.Phase);
    }

    [Fact]
    public void CompletedMemberMayDisconnectWhilePeersFinishTheirLocalTail()
    {
        var state = Create();
        Send(state, "a", "succeeded");
        foreach (var uid in Uids.Skip(1)) Send(state, uid, seconds: 60);
        var snapshot = Send(state, "b", seconds: 100);
        Assert.Equal("running", snapshot.Phase);
        foreach (var uid in Uids.Skip(1)) snapshot = Send(state, uid, "succeeded", seconds: 101);
        Assert.Equal("completed", snapshot.Phase);
        Assert.False(snapshot.CleanupComplete); // Execution success is not a fabricated cleanup ACK.
    }

    [Fact]
    public void UnfinishedDisconnectedMemberStillAbortsTheJointExecution()
    {
        var state = Create();
        foreach (var uid in Uids.Skip(1)) Send(state, uid, seconds: 60);
        Assert.Equal("participant_lease_expired", Send(state, "b", seconds: 100).Reason);
    }

    [Fact]
    public void PostOrPartialFailureNeverRestartsCompletedNativeWork()
    {
        var state = Create();
        Send(state, "a", "succeeded");
        var snapshot = Send(state, "b", "local_failed", reason: "post-task-failed-after-native");
        Assert.Equal("aborted", snapshot.Phase);
        Assert.StartsWith("participant_local_failed:b:", snapshot.Reason);
        Assert.Equal(0, snapshot.Attempt);
        Assert.Equal("succeeded", snapshot.Members.Single(m => m.Uid == "a").Result);
    }

    [Fact]
    public void FailureAfterAPeerSuccessDoesNotRequestSharedReplay()
    {
        var state = Create();
        Send(state, "a", "succeeded");
        var snapshot = Send(state, "b", "failed");
        Assert.Equal("aborted", snapshot.Phase);
        Assert.Equal("participant_failed_after_peer_completed:b", snapshot.Reason);
    }

    [Fact]
    public void SuccessArrivingDuringStoppingAlsoPreventsReplay()
    {
        var state = Create();
        Send(state, "a", "failed");
        Send(state, "b", "stopped");
        Send(state, "c", "succeeded");
        var snapshot = Send(state, "d", "stopped");
        Assert.Equal("aborted", snapshot.Phase);
        Assert.Equal("partial_completion_no_replay", snapshot.Reason);
        Assert.Equal(0, snapshot.Attempt);
    }

    [Fact]
    public void RecoveryRemainsBoundedWhenNobodyHasCompleted()
    {
        var state = Create();
        Send(state, "a", "failed");
        foreach (var uid in Uids.Skip(1)) Send(state, uid, "stopped");
        Assert.Equal("running", state.Phase);
        Assert.Equal(1, state.Attempt);
        Send(state, "a", "failed", attempt: 1);
        foreach (var uid in Uids.Skip(1)) Send(state, uid, "stopped", attempt: 1);
        Assert.Equal("recovery_budget_exhausted", state.Reason);
    }

    [Fact]
    public void ExplicitUserControlStillEndsTheOldBatchWithoutInventingCleanup()
    {
        var state = Create();
        Send(state, "a", "succeeded");
        state.Abort("user_stop_all");
        Assert.Equal("aborted", state.Phase);
        Assert.False(state.Snapshot().CleanupComplete);
        Assert.Equal("succeeded", state.Snapshot().Members.Single(m => m.Uid == "a").Result);
    }
}
