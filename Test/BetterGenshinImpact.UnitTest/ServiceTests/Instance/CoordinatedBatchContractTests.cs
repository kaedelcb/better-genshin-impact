using BgiCoordinatorServer.Services;
using MultiplayerHoeingAssistant.Services;

namespace BetterGenshinImpact.UnitTest.ServiceTests.Instance;

public class CoordinatedBatchContractTests
{
    [Fact]
    public void FourMembersMustFinishRecoveryBeforeMobGroupCanStart()
    {
        var now = DateTime.UtcNow;
        var uids = new[] { "host", "a", "b", "c" };
        var team = new CoordinatedBatchState(42, uids, now);
        foreach (var uid in uids) team.Apply(uid, uid, "", "group,group", 0, 0, "", now);
        // First client completed, second encountered the world-3 failure, two are still running.
        string Outcome(string status, string? code = null) => CoordinatedBatchOutcome.Classify(
            new BgiTaskQueueStatus { Status = status, ErrorCode = code }, team.Phase == "stopping");
        team.Apply("host", "host", team.BatchId, "group,group", 0, 0, Outcome("completed"), now);
        team.Apply("a", "a", team.BatchId, "group,group", 0, 0, Outcome("failed", "hoeing_incomplete"), now);
        Assert.Equal("stopping", team.Phase);
        team.Apply("b", "b", team.BatchId, "group,group", 0, 0, Outcome("failed", "task_cancelled"), now);
        team.Apply("c", "c", team.BatchId, "group,group", 0, 0, Outcome("running"), now);
        Assert.Equal(0, team.Attempt);
        team.Apply("c", "c", team.BatchId, "group,group", 0, 0, Outcome("queueCancelled"), now);
        Assert.Equal(1, team.Attempt);
        Assert.Equal(0, team.Index);
        // A delayed success from the first attempt does not count for the retry.
        foreach (var uid in uids) team.Apply(uid, uid, team.BatchId, "group,group", 0, 0, "succeeded", now);
        foreach (var uid in uids.Take(3)) team.Apply(uid, uid, team.BatchId, "group,group", 0, 1, Outcome("completed"), now);
        Assert.Equal(0, team.Index);
        team.Apply("c", "c", team.BatchId, "group,group", 0, 1, Outcome("completed"), now);
        Assert.Equal(1, team.Index);
        Assert.Equal(0, team.Attempt);
    }

    [Theory]
    [InlineData("not_found", false)]
    [InlineData("completed", true)]
    public void MissingTaskOrUserCancellationAbortsInsteadOfStartingMob(string status, bool cancelled)
    {
        var now = DateTime.UtcNow;
        var team = new CoordinatedBatchState(1, new[] { "a" }, now);
        team.Apply("a", "a", "", "group,group", 0, 0, "", now);
        var outcome = CoordinatedBatchOutcome.Classify(new BgiTaskQueueStatus { Status = status, Cancelled = cancelled }, false);
        team.Apply("a", "a", team.BatchId, "group,group", 0, 0, outcome, now);
        Assert.Equal("aborted", team.Phase);
        Assert.Equal(0, team.Index);
        Assert.Equal(0, team.Attempt);
    }
}
