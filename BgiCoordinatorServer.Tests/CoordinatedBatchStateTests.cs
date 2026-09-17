using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class CoordinatedBatchStateTests
{
    private readonly DateTime _now = DateTime.UtcNow;
    private CoordinatedBatchState Create() => new(7, new[] { "a", "b", "c", "d" }, _now);
    private CoordinatedBatchSnapshot Send(CoordinatedBatchState s, string uid, string result = "", int index = 0,
        int attempt = 0, int seconds = 0, string layout = "group,group", string? token = null)
        => s.Apply(uid, token ?? uid, s.BatchId, layout, index, attempt, result, _now.AddSeconds(seconds));
    private void Enroll(CoordinatedBatchState s)
    { foreach (var uid in new[] { "a", "b", "c", "d" }) Send(s, uid); }

    [Fact] public void DoesNotStartUntilAllFourEnroll()
    {
        var s = Create();
        foreach (var uid in new[] { "a", "b", "c" }) Assert.Equal("registering", Send(s, uid).Phase);
        Assert.Equal("running", Send(s, "d").Phase);
    }
    [Fact] public void DoesNotAdvanceUntilAllFourSucceed()
    {
        var s = Create(); Enroll(s);
        foreach (var uid in new[] { "a", "b", "c" }) Assert.Equal(0, Send(s, uid, "succeeded").Index);
        Assert.Equal(1, Send(s, "d", "succeeded").Index);
        // Replayed acknowledgements from the preceding group do not complete the next group.
        foreach (var uid in new[] { "a", "b", "c", "d" }) Send(s, uid, "succeeded");
        Assert.Equal("running", s.Phase);
        foreach (var uid in new[] { "a", "b", "c", "d" }) Send(s, uid, "succeeded", index: 1);
        Assert.Equal("completed", s.Phase);
    }
    [Fact] public void RecoveryWaitsForCleanupAndIsSharedAndBounded()
    {
        var s = Create(); Enroll(s);
        Assert.Equal("stopping", Send(s, "a", "failed").Phase);
        Send(s, "b", "stopped"); Send(s, "c", "succeeded");
        Assert.Equal(0, s.Attempt);
        Assert.Equal(1, Send(s, "d", "stopped").Attempt);
        Assert.Equal(0, s.Index);
        // Late failure and duplicate stop cannot poison the new attempt.
        Send(s, "a", "failed"); Send(s, "d", "stopped");
        Assert.Equal("running", s.Phase);
        Send(s, "a", "failed", attempt: 1);
        foreach (var uid in new[] { "b", "c", "d" }) Send(s, uid, "stopped", attempt: 1);
        Assert.Equal("aborted", s.Phase);
        Assert.Equal("recovery_budget_exhausted", s.Reason);
        Assert.Equal(0, s.Index);
    }
    [Theory] [InlineData("cancelled")] [InlineData("unknown")]
    public void CancellationAndUnknownNeverRecoverOrAdvance(string result)
    {
        var s = Create(); Enroll(s); Send(s, "a", result);
        foreach (var uid in new[] { "b", "c", "d" }) Send(s, uid, "succeeded");
        Assert.Equal("aborted", s.Phase); Assert.Equal(0, s.Index); Assert.Equal(0, s.Attempt);
    }
    [Fact] public void RestartedAssistantCannotAdoptOldExecution()
    { var s = Create(); Enroll(s); Assert.Equal("aborted", Send(s, "a", token: "replacement").Phase); }
    [Fact] public void MissingParticipantLeaseAbortsEvenWhenOthersKeepPolling()
    {
        var s = Create(); Enroll(s);
        foreach (var uid in new[] { "a", "b", "c" }) Send(s, uid, seconds: 60);
        Assert.Equal("aborted", Send(s, "a", seconds: 91).Phase);
    }
    [Fact] public void RegistrationHasBound()
    { var s = Create(); Send(s, "a"); Assert.Equal("aborted", Send(s, "a", seconds: 181).Phase); }
    [Fact] public void ConflictingLayoutCannotRunMismatchedGroups()
    { var s = Create(); Send(s, "a"); Assert.Equal("aborted", Send(s, "b", layout: "group").Phase); }
    [Fact] public void UnknownUidAndWrongBatchCannotMutateState()
    {
        var s = Create(); Enroll(s);
        Assert.Throws<InvalidOperationException>(() => Send(s, "intruder", "succeeded"));
        Assert.Throws<InvalidOperationException>(() => s.Apply("a", "a", "old-batch", "group,group", 0, 0, "failed", _now));
        Assert.Equal("running", s.Phase);
    }
    [Fact] public void ConflictingTerminalResultAborts()
    { var s = Create(); Enroll(s); Send(s, "a", "succeeded"); Assert.Equal("aborted", Send(s, "a", "failed").Phase); }
    [Fact] public void CleanupCannotWaitForeverEvenWithLiveParticipants()
    {
        var s = Create(); Enroll(s); Send(s, "a", "failed");
        foreach (var uid in new[] { "a", "b", "c", "d" }) Send(s, uid, seconds: 60);
        Assert.Equal("aborted", Send(s, "a", seconds: 91).Phase);
        Assert.Equal("cleanup_timeout", s.Reason);
    }
}
