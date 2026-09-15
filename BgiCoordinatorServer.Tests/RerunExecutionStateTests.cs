using BetterGenshinImpact.Shared.CooperativeRerun;
using BgiCoordinatorServer.Services;
using Xunit;

namespace BgiCoordinatorServer.Tests;

public class RerunExecutionStateTests
{
    private static readonly DateTime Now = new(2026, 1, 1, 0, 0, 0, DateTimeKind.Utc);
    private readonly RerunExecutionState _state = new("0", 0, new Dictionary<string, string> { ["a"] = "ca", ["b"] = "cb" }, Now);
    private int _sequence;
    private RerunRequest Request(string uid, string op) => new()
    {
        Operation = op, OperationId = (++_sequence).ToString(), Scope = "0",
        SessionId = op == RerunProtocol.Enroll ? "" : _state.SessionId,
        ResumeToken = "secret-" + uid, Routes = Manifest()
    };
    private static List<RerunRouteManifest> Manifest() => new()
    {
        new() { RouteId = "r0", OriginalIndex = 0, Eligible = true, Checkpoints = new()
        {
            new() { Id = "tp0", Kind = "teleport", Segment = 0 },
            new() { Id = "strict_1", Kind = "sync", Segment = 0 },
            new() { Id = "fight1", Kind = "fight", Segment = 0 },
            new() { Id = "tp1", Kind = "teleport", Segment = 1 }
        } },
        new() { RouteId = "r9", OriginalIndex = 9, Eligible = true, Checkpoints = new() { new() { Id = "f", Kind = "fight", Segment = 0 } } }
    };
    private RerunSnapshot Send(string uid, RerunRequest request, DateTime? now = null, string? conn = null)
        => _state.Apply(uid, conn ?? "c" + uid, request, now ?? Now);
    private RerunSnapshot Send(string uid, string op) => Send(uid, Request(uid, op));
    private void Enroll()
    {
        Assert.Equal(RerunStage.Registering, Send("a", RerunProtocol.Enroll).Stage);
        Assert.Equal(RerunStage.Normal, Send("b", RerunProtocol.Enroll).Stage);
    }
    private RerunSnapshot Mark(string uid, string route, string point, bool replay = false)
    {
        var request = Request(uid, RerunProtocol.Mark);
        request.RouteId = route; request.PointId = point; request.IsReplay = replay;
        return Send(uid, request);
    }
    private void Run()
    {
        Enroll(); Mark("a", "r0", "fight1");
        Send("a", RerunProtocol.NormalDone);
        var prepared = Send("b", RerunProtocol.NormalDone);
        foreach (var uid in new[] { "a", "b" })
        {
            var request = Request(uid, RerunProtocol.Prepare); request.PlanHash = prepared.PlanHash;
            Send(uid, request);
        }
    }
    [Fact]
    public void FixedRosterAndEmptyPlan()
    {
        Enroll();
        Assert.Equal(RerunStage.Normal, Send("a", RerunProtocol.NormalDone).Stage);
        var snapshot = Send("b", RerunProtocol.NormalDone);
        Assert.Equal(RerunStage.Completed, snapshot.Stage);
        Assert.Equal(new[] { "a", "b" }, snapshot.Participants);
    }
    [Fact]
    public void MarksAreOrderedDeduplicatedAndPlanUsesOriginalOrder()
    {
        Enroll();
        var request = Request("a", RerunProtocol.Mark); request.RouteId = "r9"; request.PointId = "f";
        Send("a", request); Send("a", request);
        Mark("b", "r0", "fight1");
        Send("a", RerunProtocol.NormalDone);
        var result = Send("b", RerunProtocol.NormalDone);
        Assert.Equal(new[] { "r0", "r9" }, result.Plan);
        Assert.Equal(new long[] { 1, 2 }, result.Marks.Select(m => m.Sequence));
        var bad = Request("a", RerunProtocol.Prepare); bad.PlanHash = "bad";
        Assert.Throws<RerunProtocolException>(() => Send("a", bad));
    }
    [Fact]
    public void CanonicalManifestHasNoPhysicalWaypointIndexAndMismatchRejected()
    {
        Send("a", RerunProtocol.Enroll);
        var request = Request("b", RerunProtocol.Enroll);
        // Both physical variants project to exactly these canonical checkpoints.
        Assert.Equal(RerunStage.Normal, Send("b", request).Stage);
        request = Request("b", RerunProtocol.Enroll); request.Routes[0].Checkpoints[1].Id = "different";
        Assert.Throws<RerunProtocolException>(() => Send("b", request));
    }
    [Fact]
    public void RecoverDoesNotResolveButBypassResolvesOnlyAcknowledgedSegments()
    {
        Run();
        var recovery = Request("a", RerunProtocol.Recover); recovery.RouteId = "r0";
        Assert.Empty(Send("a", recovery).ResolvedPoints);
        var bypass = Request("a", RerunProtocol.Bypass); bypass.RouteId = "r0"; bypass.ThroughSegment = 0;
        Send("a", bypass);
        foreach (var point in new[] { "tp0", "strict_1" })
        {
            var arrival = Request("b", RerunProtocol.Arrive); arrival.RouteId = "r0"; arrival.PointId = point;
            Send("b", arrival);
        }
        var state = Send("b", RerunProtocol.Poll);
        Assert.Contains("tp0", state.ResolvedPoints); Assert.Contains("strict_1", state.ResolvedPoints);
        Assert.DoesNotContain("tp1", state.ResolvedPoints);
        var done = Request("a", RerunProtocol.RouteDone); done.RouteId = "r0"; done.Outcome = RerunRouteOutcome.Completed;
        Assert.Throws<RerunProtocolException>(() => Send("a", done));
    }
    [Fact]
    public void RouteOutcomesAreIdempotentAndFinishNeedsEverybody()
    {
        Run();
        var doneRequests = new List<(string uid, RerunRequest request)>();
        foreach (var uid in new[] { "a", "b" })
        {
            foreach (var point in new[] { "tp0", "strict_1", "tp1" })
            {
                var arrival = Request(uid, RerunProtocol.Arrive); arrival.RouteId = "r0"; arrival.PointId = point;
                Send(uid, arrival);
            }
            var done = Request(uid, RerunProtocol.RouteDone); done.RouteId = "r0"; done.Outcome = RerunRouteOutcome.Completed;
            Send(uid, done); doneRequests.Add((uid, done));
        }
        Assert.Equal(RerunStage.Finishing, Send("a", doneRequests[0].request).Stage);
        Assert.Equal(RerunStage.Finishing, Send("a", RerunProtocol.Finish).Stage);
        Assert.Equal(RerunStage.Completed, Send("b", RerunProtocol.Finish).Stage);
    }
    [Fact]
    public void AbortIsIrreversibleAndFailedOutcomeAborts()
    {
        Run();
        var done = Request("a", RerunProtocol.RouteDone); done.RouteId = "r0"; done.Outcome = RerunRouteOutcome.Failed;
        Assert.Equal(RerunStage.Aborted, Send("a", done).Stage);
        Assert.Equal(RerunStage.Aborted, Send("b", RerunProtocol.Finish).Stage);
    }
    [Fact]
    public void DifferentSessionAndOldConnectionCannotWriteAfterResume()
    {
        Enroll();
        var poll = Request("a", RerunProtocol.Poll); poll.SessionId = "old";
        Assert.Throws<RerunProtocolException>(() => Send("a", poll));
        poll = Request("a", RerunProtocol.Poll);
        Send("a", poll, conn: "new-a");
        Assert.Throws<RerunProtocolException>(() => Send("a", Request("a", RerunProtocol.Poll)));
        poll = Request("a", RerunProtocol.Poll); poll.ResumeToken = "wrong";
        Assert.Throws<RerunProtocolException>(() => Send("a", poll, conn: "new-a"));
    }
    [Fact]
    public void LeaseAndPrepareDeadlineAreBounded()
    {
        Run();
        Assert.Equal(RerunStage.Aborted, Send("a", Request("a", RerunProtocol.Poll), Now.AddSeconds(46)).Stage);
    }
    /// <summary>
    /// 兜底看护：成员都还在轮询（租约被续租、因此不会触发 ParticipantLeaseExpired），
    /// 但阶段长时间没有任何被接受的操作 → 必须统一中止，而不是让全员无限互等。
    /// </summary>
    [Fact]
    public void NoProgressStageIsAbortedByBackstopWatchdog()
    {
        Run();
        // 两端交替轮询保持租约新鲜，但没有任何到达/结算进展，持续超过无进展上界。
        for (var seconds = 30; seconds <= 13 * 60; seconds += 30)
            foreach (var uid in new[] { "a", "b" })
                Send(uid, Request(uid, RerunProtocol.Poll), Now.AddSeconds(seconds));
        var final = Send("a", Request("a", RerunProtocol.Poll), Now.AddSeconds(13 * 60 + 30));
        Assert.Equal(RerunStage.Aborted, final.Stage);
        Assert.Equal("NoProgress", final.Reason);
    }
    /// <summary>
    /// 反向保护：只要有真实进展（这里用到达）就不得被无进展看护误杀。
    /// </summary>
    [Fact]
    public void ProgressResetsTheNoProgressWatchdog()
    {
        Run();
        // 轮询节奏必须快于成员租约（45s），否则触发的是租约中止而不是无进展看护。
        for (var seconds = 30; seconds <= 20 * 60; seconds += 30)
        {
            foreach (var uid in new[] { "a", "b" })
                Send(uid, Request(uid, RerunProtocol.Poll), Now.AddSeconds(seconds));
            var arrive = Request("a", RerunProtocol.Arrive);
            arrive.RouteId = "r0"; arrive.PointId = "tp0";
            Send("a", arrive, Now.AddSeconds(seconds));
        }
        Assert.Equal(RerunStage.Running, Send("a", Request("a", RerunProtocol.Poll), Now.AddSeconds(20 * 60 + 30)).Stage);
    }
    [Fact]
    public void NormalMayRunIndefinitelyWithPollsAndPreparingStillTimesOut()
    {
        Enroll();
        // 正常轮可以长时间没有任何死亡标记：这是常态（正常轮 20–60 分钟），
        // 因此无进展看护绝不能作用于 Normal。这里把轮询拉到 40 分钟验证不会被误判。
        for (var seconds = 30; seconds <= 2400; seconds += 30)
            foreach (var uid in new[] { "a", "b" })
                Assert.Equal(RerunStage.Normal, Send(uid, Request(uid, RerunProtocol.Poll), Now.AddSeconds(seconds)).Stage);
        var mark = Request("a", RerunProtocol.Mark); mark.RouteId = "r0"; mark.PointId = "fight1";
        Send("a", mark, Now.AddSeconds(2400));
        Send("a", Request("a", RerunProtocol.NormalDone), Now.AddSeconds(2400));
        Send("b", Request("b", RerunProtocol.NormalDone), Now.AddSeconds(2400));
        Assert.Equal(RerunStage.Aborted, Send("a", Request("a", RerunProtocol.Poll), Now.AddSeconds(2520)).Stage);
    }
    [Fact]
    public void ReplayMarkDoesNotExtendPlanAndNormalLateMarkIsRejected()
    {
        Run();
        var result = Mark("a", "r0", "fight1", replay: true);
        Assert.Single(result.Plan);
        Assert.True(result.Marks.Last().IsReplay);
        Assert.Throws<RerunProtocolException>(() => Mark("a", "r9", "f", replay: true));
        Assert.Throws<RerunProtocolException>(() => Mark("a", "r0", "fight1"));
    }
    /// <summary>
    /// 孤儿会话回收判据：活跃会话（成员持续轮询续租）绝不能被判成孤儿；
    /// 全体成员都停止活动超过租约窗口后，非终态会话必须可被判为孤儿从而允许新一轮替换。
    /// </summary>
    [Fact]
    public void OrphanedSessionIsReclaimableButActiveSessionIsNot()
    {
        Enroll();
        var active = _state.Apply("a", "ca", Request("a", RerunProtocol.Poll), Now.AddSeconds(30));
        Assert.Equal(RerunStage.Normal, active.Stage);
        Assert.False(_state.IsOrphaned(Now.AddSeconds(30)));

        // 全体成员静默超过租约窗口（无人续租）。
        Assert.True(_state.IsOrphaned(Now.AddSeconds(120)));
        // 只要有一个成员恢复活动，就不再是孤儿。
        _state.Apply("b", "cb", Request("b", RerunProtocol.Poll), Now.AddSeconds(120));
        Assert.False(_state.IsOrphaned(Now.AddSeconds(120)));
        // 终态会话不属于"孤儿"（它本来就是可替换的）。
        _state.Abort("done");
        Assert.True(_state.IsTerminal);
        Assert.False(_state.IsOrphaned(Now.AddSeconds(600)));
    }
    [Fact]
    public void SkipRouteAllowsIncompleteButNotCompletedAndKeepsRoster()
    {
        Run();
        foreach (var uid in new[] { "a", "b" })
        {
            var bypass = Request(uid, RerunProtocol.Bypass); bypass.RouteId = "r0"; bypass.SkipRoute = true;
            Send(uid, bypass);
            var done = Request(uid, RerunProtocol.RouteDone); done.RouteId = "r0"; done.Outcome = RerunRouteOutcome.Incomplete; done.Reason = "anomaly-bypass";
            Send(uid, done);
        }
        var snapshot = Send("a", RerunProtocol.Poll);
        Assert.Equal(RerunStage.Finishing, snapshot.Stage);
        Assert.Equal(2, snapshot.Outcomes.Count);
        Assert.Equal(new[] { "a", "b" }, snapshot.Participants);

        // 测试名承诺的第二半必须真的断言：有豁免时 Completed 会被拒。
        // 服务端要求 Completed 下"所有非战斗规范点都已真实到达"，bypass 不算到达。
        var fresh = new RerunExecutionState("0", 0,
            new Dictionary<string, string> { ["a"] = "ca", ["b"] = "cb" }, Now);
        var sequence = 0;
        RerunRequest Req(string uid, string op) => new()
        {
            Operation = op, OperationId = "x" + (++sequence), Scope = "0",
            SessionId = op == RerunProtocol.Enroll ? "" : fresh.SessionId,
            ResumeToken = "secret-" + uid, Routes = Manifest()
        };
        fresh.Apply("a", "ca", Req("a", RerunProtocol.Enroll), Now);
        fresh.Apply("b", "cb", Req("b", RerunProtocol.Enroll), Now);
        var markRequest = Req("a", RerunProtocol.Mark);
        markRequest.RouteId = "r0"; markRequest.PointId = "fight1";
        fresh.Apply("a", "ca", markRequest, Now);
        fresh.Apply("a", "ca", Req("a", RerunProtocol.NormalDone), Now);
        var prepared = fresh.Apply("b", "cb", Req("b", RerunProtocol.NormalDone), Now);
        foreach (var uid in new[] { "a", "b" })
        {
            var prepare = Req(uid, RerunProtocol.Prepare);
            prepare.PlanHash = prepared.PlanHash;
            fresh.Apply(uid, "c" + uid, prepare, Now);
        }
        var bypassRequest = Req("a", RerunProtocol.Bypass);
        bypassRequest.RouteId = "r0"; bypassRequest.SkipRoute = true;
        fresh.Apply("a", "ca", bypassRequest, Now);
        var completed = Req("a", RerunProtocol.RouteDone);
        completed.RouteId = "r0"; completed.Outcome = RerunRouteOutcome.Completed; completed.Reason = "x";
        Assert.Throws<RerunProtocolException>(() => fresh.Apply("a", "ca", completed, Now));
    }
    [Fact]
    public void ReusingOperationIdentifierWithDifferentPayloadIsRejected()
    {
        Enroll();
        var request = Request("a", RerunProtocol.Mark); request.RouteId = "r0"; request.PointId = "fight1";
        Send("a", request); request.RouteId = "r9"; request.PointId = "f";
        Assert.Throws<RerunProtocolException>(() => Send("a", request));
    }
    [Fact]
    public void SnapshotCannotMutateAuthoritativeState()
    {
        Enroll(); var snapshot = Mark("a", "r0", "fight1");
        snapshot.Marks[0].RouteId = "corrupt"; snapshot.Participants.Clear();
        var actual = Send("a", RerunProtocol.Poll);
        Assert.Equal("r0", actual.Marks[0].RouteId); Assert.Equal(2, actual.Participants.Count);
    }
}
