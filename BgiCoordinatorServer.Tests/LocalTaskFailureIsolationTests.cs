using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class LocalTaskFailureIsolationTests
{
    private static readonly string[] Uids = ["a", "b", "c", "d"];
    private readonly DateTime _now = DateTime.UtcNow;
    private CoordinatedBatchSnapshot Send(CoordinatedBatchState state, string uid, string result = "",
        int index = 0, int attempt = 0, int seconds = 0, string stage = "", string reason = "") =>
        state.Apply(uid, uid, state.BatchId, "group,group", index, attempt, result, _now.AddSeconds(seconds), stage, reason);

    private CoordinatedBatchState Running()
    {
        var state = new CoordinatedBatchState(15, Uids, _now);
        foreach (var stage in new[] { "registered", "prepared", "takeoverConfirmed" })
            foreach (var uid in Uids) Send(state, uid, stage: stage);
        Assert.Equal("running", state.Phase);
        return state;
    }

    [Fact]
    public void FailedStatueDoesNotStopPeersAndOnlyEndsBatchAfterEveryCurrentGroupExits()
    {
        var state = Running();
        var local = Send(state, "a", "local_tasks_failed", reason: "回神像失败");
        Assert.Equal("running", local.Phase);
        Assert.Equal("回神像失败", local.Members.Single(m => m.Uid == "a").Reason);
        Assert.Equal("local_tasks_failed", local.Members.Single(m => m.Uid == "a").Result);
        Send(state, "b", "succeeded"); // This member has no statue task.
        Send(state, "c");             // This member still has two local tasks.
        var waiting = Send(state, "d", "succeeded");
        Assert.Equal("running", waiting.Phase);
        Assert.Equal(0, waiting.Index);
        Assert.Equal(0, waiting.Attempt);
        Assert.Throws<InvalidOperationException>(() => Send(state, "a", stage: "cleaned"));

        var ended = Send(state, "c", "succeeded");
        Assert.Equal("aborted", ended.Phase);
        Assert.Equal("local_tasks_failed:a", ended.Reason);
        Assert.Equal(0, ended.Index); // The second binding must not start after a failed first group.
        Assert.Equal(0, ended.Attempt);
        Assert.False(ended.CleanupComplete);
        Assert.Equal("succeeded", ended.Members.Single(m => m.Uid == "c").Result);
        foreach (var uid in Uids) ended = Send(state, uid, stage: "cleaned");
        Assert.True(ended.CleanupComplete);
    }

    [Fact]
    public void FinishedFailedMemberMayDisconnectWithoutExpiringPeersThatAreStillDoingLocalTasks()
    {
        var state = Running();
        Send(state, "a", "local_tasks_failed");
        foreach (var uid in Uids.Skip(1)) Send(state, uid, seconds: 60);
        Assert.Equal("running", Send(state, "b", seconds: 100).Phase);
        foreach (var uid in Uids.Skip(1)) Send(state, uid, "succeeded", seconds: 101);
        Assert.Equal("local_tasks_failed:a", state.Reason);
    }

    [Fact]
    public void StillRunningMembersContinueToRequireALease()
    {
        var state = Running();
        Send(state, "a", "local_tasks_failed");
        Send(state, "b", seconds: 60);
        Send(state, "c", seconds: 60);
        Assert.Equal("participant_lease_expired", Send(state, "b", seconds: 100).Reason);
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("cancelled")]
    [InlineData("failed")]
    [InlineData("succeeded")]
    [InlineData("local_failed")]
    public void LateObservationCannotRewriteASealedLocalFailure(string late)
    {
        var state = Running();
        Send(state, "a", "local_tasks_failed");
        var snapshot = Send(state, "a", late);
        Assert.Equal("running", snapshot.Phase);
        Assert.Equal("local_tasks_failed", snapshot.Members.Single(m => m.Uid == "a").Result);
    }

    [Theory]
    [InlineData("local_failed")]
    [InlineData("unknown")]
    [InlineData("cancelled")]
    public void GenuineHoeingFailureOrUnknownKeepsTheExistingCoordinatedAbort(string failure)
    {
        var state = Running();
        Send(state, "a", "local_tasks_failed");
        Assert.Equal("aborted", Send(state, "b", failure).Phase);
        Assert.Equal(0, state.Attempt);
    }

    [Fact]
    public void NativeFailureAfterAnIsolatedLocalFailureCannotReplayCompletedHoeing()
    {
        var state = Running();
        Send(state, "a", "local_tasks_failed");
        Assert.Equal("aborted", Send(state, "b", "failed").Phase);
        Assert.Equal(0, state.Attempt);
    }

    [Fact]
    public void ALocalFailureArrivingDuringRealFailureStoppingStillPreventsSharedReplay()
    {
        var state = Running();
        Assert.Equal("stopping", Send(state, "b", "failed").Phase);
        Send(state, "a", "local_tasks_failed");
        Send(state, "c", "stopped");
        Assert.Equal("partial_completion_no_replay", Send(state, "d", "stopped").Reason);
        Assert.Equal(0, state.Attempt);
    }

    [Theory]
    [InlineData("manual_reset")]
    [InlineData("user_stop_all")]
    public void ExplicitControlDoesNotWaitForOtherMembersLocalTasks(string reason)
    {
        var state = Running();
        Send(state, "a", "local_tasks_failed");
        state.Abort(reason);
        Assert.Equal("aborted", state.Phase);
        Assert.Equal(reason, state.Reason);
        Assert.False(state.IsTerminal);
    }

    [Fact]
    public void AllSuccessfulGroupsAdvanceNormallyAndOldLocalFailureCannotPoisonNextBinding()
    {
        var state = Running();
        foreach (var uid in Uids) Send(state, uid, "succeeded");
        Assert.Equal(1, state.Index);
        Assert.Equal("running", Send(state, "a", "local_tasks_failed", index: 0).Phase);
        Assert.All(state.Snapshot().Members, m => Assert.Equal("", m.Result));
        foreach (var uid in Uids) Send(state, uid, "succeeded", index: 1);
        Assert.Equal("completed", state.Phase);
    }

    [Fact]
    public void OrdinaryFailureCannotBeReportedBeforeTheBindingWasStarted()
    {
        var state = new CoordinatedBatchState(16, Uids, _now);
        Send(state, "a", stage: "registered");
        Assert.Throws<InvalidOperationException>(() => Send(state, "a", "local_tasks_failed"));
        Assert.Equal("registering", state.Phase);
    }
}
