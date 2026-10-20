using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public sealed class CoordinatedStateContinuationTests
{
    private static readonly string[] Uids = ["a", "b", "c", "d"];
    private readonly DateTime _now = new(2026, 10, 20, 1, 0, 0, DateTimeKind.Utc);

    private CoordinatedBatchState Create() => new(7, Uids, _now, stateContinuation: true);

    private CoordinatedBatchSnapshot Send(CoordinatedBatchState state, string uid,
        string stage = "", string result = "", int seconds = 0, int index = 0, int attempt = 0,
        string reason = "")
        => state.Apply(uid, uid, state.BatchId, "group,group", index, attempt, result,
            _now.AddSeconds(seconds), stage, reason);

    private void Enroll(CoordinatedBatchState state)
    {
        foreach (var stage in new[] { "registered", "prepared", "takeoverConfirmed" })
            foreach (var uid in Uids) Send(state, uid, stage);
    }

    [Fact]
    public void RegistrationPreparationTakeoverAndHeartbeatDeadlinesKeepTheSameFourPersonRequest()
    {
        var state = Create();
        var initial = Send(state, "a", "registered");
        state.CheckTimeout(_now.AddSeconds(181));
        Assert.Equal("registering", state.Phase);
        foreach (var uid in Uids.Skip(1)) Send(state, uid, "registered", seconds: 181);
        Assert.Equal("preparing", state.Phase);

        state.CheckTimeout(_now.AddSeconds(400));
        Assert.Equal("preparing", state.Phase);
        foreach (var uid in Uids) Send(state, uid, "prepared", seconds: 400);
        Assert.Equal("takeover", state.Phase);

        state.CheckTimeout(_now.AddSeconds(600));
        Assert.Equal("takeover", state.Phase);
        foreach (var uid in Uids) Send(state, uid, "takeoverConfirmed", seconds: 600);
        Assert.Equal("running", state.Phase);

        state.CheckTimeout(_now.AddHours(8));
        Assert.Equal("running", state.Phase);
        foreach (var uid in Uids.Take(3))
            Assert.Equal(0, Send(state, uid, result: "succeeded", seconds: 28800).Index);
        var next = Send(state, "d", result: "succeeded", seconds: 28800);
        Assert.True(next.StateContinuation);
        Assert.Equal(initial.BatchId, next.BatchId);
        Assert.Equal(initial.Generation, next.Generation);
        Assert.Equal(initial.ParticipantUids, next.ParticipantUids);
        Assert.Equal(1, next.Index);
        Assert.Equal(0, next.Attempt);
    }

    [Fact]
    public void PendingPreparationAndTakeoverReasonsDoNotConfirmReadinessOrCancelTheRoster()
    {
        var state = Create();
        foreach (var uid in Uids) Send(state, uid, "registered");
        var waiting = Send(state, "a", "registered", seconds: 500, reason: "communication_timeout");
        Assert.Equal("preparing", waiting.Phase);
        Assert.Equal("registered", waiting.Members.Single(m => m.Uid == "a").Stage);
        Assert.Equal("communication_timeout", waiting.Members.Single(m => m.Uid == "a").Reason);
        foreach (var uid in Uids) Send(state, uid, "prepared", seconds: 501);
        waiting = Send(state, "a", "prepared", seconds: 1000, reason: "suspend_record_pending");
        Assert.Equal("takeover", waiting.Phase);
        Assert.Equal("prepared", waiting.Members.Single(m => m.Uid == "a").Stage);
        foreach (var uid in Uids.Take(3)) Send(state, uid, "takeoverConfirmed", seconds: 1001);
        Assert.Equal("takeover", state.Phase);
        var running = Send(state, "d", "takeoverConfirmed", seconds: 1002);
        Assert.Equal("running", running.Phase);
        Assert.All(running.Members, m => Assert.Equal("", m.Reason));
        Assert.Equal(waiting.BatchId, running.BatchId);
    }

    [Fact]
    public void UnknownKeepsItsOriginalItemWhilePeersFinishAndCanBeReplacedByActualSuccess()
    {
        var state = Create(); Enroll(state);
        var waiting = Send(state, "a", result: "unknown", reason: "job_record_pending");
        foreach (var uid in Uids.Skip(1)) Send(state, uid, result: "succeeded");
        Assert.Equal("running", state.Phase);
        Assert.Equal(0, state.Index);
        Assert.Equal("unknown", state.Snapshot().Members.Single(m => m.Uid == "a").Result);
        var next = Send(state, "a", result: "succeeded", seconds: 20000);
        Assert.Equal(waiting.BatchId, next.BatchId);
        Assert.Equal(waiting.Attempt, next.Attempt);
        Assert.Equal(1, next.Index);
        Assert.Equal("running", next.Phase);
        var late = Send(state, "a", result: "unknown", index: 0, reason: "old_reply");
        Assert.Equal(next.Revision, late.Revision);
        Assert.All(late.Members, m => Assert.Equal("", m.Result));
    }

    [Fact]
    public void UnknownBeforeTakeoverRequiresTheRealPreparationAndTakeoverReports()
    {
        var state = Create();
        foreach (var uid in Uids) Send(state, uid, "registered");
        Send(state, "a", result: "unknown", reason: "prepare_reply_lost");
        foreach (var uid in Uids.Skip(1)) Send(state, uid, "prepared");
        Assert.Equal("preparing", state.Phase);
        var takeover = Send(state, "a", "prepared", seconds: 3600);
        Assert.Equal("takeover", takeover.Phase);
        Assert.Equal("", takeover.Members.Single(m => m.Uid == "a").Result);
        Send(state, "a", result: "unknown", reason: "suspend_reply_lost", seconds: 3601);
        foreach (var uid in Uids.Skip(1)) Send(state, uid, "takeoverConfirmed", seconds: 3602);
        Assert.Equal("takeover", state.Phase);
        var running = Send(state, "a", "takeoverConfirmed", seconds: 7200);
        Assert.Equal("running", running.Phase);
        Assert.All(running.Members, m => Assert.Equal("", m.Result));
    }

    [Fact]
    public void UnknownDuringRealFailureRecoveryDoesNotStandInForAnExitedParticipant()
    {
        var state = Create(); Enroll(state);
        Send(state, "a", result: "failed");
        var knownFailure = Send(state, "a", result: "unknown", reason: "late_missing_record");
        Assert.Equal("failed", knownFailure.Members.Single(m => m.Uid == "a").Result);
        Send(state, "b", result: "stopped");
        Send(state, "c", result: "stopped");
        Send(state, "d", result: "unknown", reason: "exit_reply_lost");
        state.CheckTimeout(_now.AddHours(2));
        Assert.Equal("stopping", state.Phase);
        Assert.Equal(0, state.Attempt);
        var recovered = Send(state, "d", result: "stopped", seconds: 7200);
        Assert.Equal("running", recovered.Phase);
        Assert.Equal(1, recovered.Attempt);
        Assert.Equal(0, recovered.Index);
        Send(state, "a", result: "failed", attempt: 1, seconds: 7201);
        foreach (var uid in Uids.Skip(1)) Send(state, uid, result: "stopped", attempt: 1, seconds: 7202);
        Assert.Equal("aborted", state.Phase);
        Assert.Equal("recovery_budget_exhausted", state.Reason);
    }

    [Theory]
    [InlineData("manual_stop")]
    [InlineData("manual_reset")]
    public void ExplicitControlCancelsThePendingRequestAndLateTelemetryCannotReviveIt(string reason)
    {
        var state = Create(); Enroll(state);
        Send(state, "a", result: "unknown", reason: "connection_lost");
        state.Abort(reason);
        var stopped = state.Snapshot();
        state.CheckTimeout(_now.AddDays(2));
        foreach (var uid in Uids)
        {
            var late = Send(state, uid, "takeoverConfirmed", result: "succeeded", seconds: 172800);
            Assert.Equal("aborted", late.Phase);
            Assert.Equal(reason, late.Reason);
            Assert.Equal(stopped.BatchId, late.BatchId);
            Assert.Equal(stopped.Index, late.Index);
            Assert.Equal(stopped.Attempt, late.Attempt);
            Assert.False(late.CleanupComplete);
        }
        foreach (var uid in Uids) Send(state, uid, "cleaned", seconds: 172800);
        Assert.True(state.Snapshot().CleanupComplete);
    }

    [Theory]
    [InlineData("preparation_failed", "config_missing", "preparing")]
    [InlineData("takeover_failed", "original_identity_mismatch", "takeover")]
    public void PermanentBusinessFailureIsStillReportedAndNeverPretendsReadiness(string result, string reason, string phase)
    {
        var state = Create();
        foreach (var uid in Uids) Send(state, uid, "registered");
        if (phase == "takeover") foreach (var uid in Uids) Send(state, uid, "prepared");
        var failed = Send(state, "a", result: result, reason: reason);
        Assert.Equal("aborted", failed.Phase);
        Assert.Equal($"participant_{result}:a:{phase}:{reason}", failed.Reason);
        Assert.Equal(0, failed.Index);
        Assert.Equal(0, failed.Attempt);
    }

    [Fact]
    public void ContinuationCannotBypassParticipantTokenBatchOrLayoutIdentity()
    {
        var state = Create(); Enroll(state);
        Send(state, "a", result: "unknown", reason: "connection_lost");
        var before = state.Snapshot();
        Assert.Throws<InvalidOperationException>(() => state.Apply("stranger", "stranger", state.BatchId,
            "group,group", 0, 0, "succeeded", _now));
        Assert.Throws<InvalidOperationException>(() => state.Apply("a", "replacement", state.BatchId,
            "group,group", 0, 0, "succeeded", _now));
        Assert.Throws<InvalidOperationException>(() => state.Apply("a", "a", "another-batch",
            "group,group", 0, 0, "succeeded", _now));
        Assert.Throws<InvalidOperationException>(() => Send(state, "a", "invented-stage"));
        Assert.Throws<InvalidOperationException>(() => Send(state, "a", result: "invented-success"));
        Assert.Equal(before.Revision, state.Revision);
        var mismatched = state.Apply("a", "a", state.BatchId, "group", 0, 0, "", _now);
        Assert.Equal("aborted", mismatched.Phase);
        Assert.Equal("batch_layout_mismatch:a", mismatched.Reason);
    }
    [Fact]
    public void OrdinaryTaskErrorContinuesNextBindingAndCompletesWithErrorFacts()
    {
        var state = Create(); Enroll(state);
        Send(state, "a", result: "local_tasks_failed", reason: "ordinary task failed after hoeing");
        foreach (var uid in Uids.Skip(1)) Send(state, uid, result: "succeeded");
        Assert.Equal("running", state.Phase); Assert.Equal(1, state.Index); Assert.Equal(0, state.Attempt);
        foreach (var uid in Uids) Send(state, uid, result: uid == "b" ? "local_tasks_failed" : "succeeded",
            index: 1, reason: uid == "b" ? "another ordinary failure" : "");
        var done = state.Snapshot();
        Assert.Equal("completed", done.Phase); Assert.Equal(2, done.Index); Assert.Equal(0, done.Attempt);
        Assert.Equal("completed_with_task_errors:a,b", done.Reason);
        Assert.Equal("local_tasks_failed", done.Members.Single(m => m.Uid == "b").Result);
        Assert.Contains("ordinary task failed", done.Members.Single(m => m.Uid == "a").Reason);
    }

}
