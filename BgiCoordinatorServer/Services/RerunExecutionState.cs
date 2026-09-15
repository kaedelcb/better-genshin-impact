using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using BetterGenshinImpact.Shared.CooperativeRerun;

namespace BgiCoordinatorServer.Services;

public sealed class RerunProtocolException(string code, string message) : Exception(message)
{
    public string Code { get; } = code;
}

// All access is serialized by the owning room lock. No transport or wall-clock dependencies.
public sealed class RerunExecutionState
{
    private sealed class Member(string connection, DateTime now)
    {
        public string Connection = connection;
        public string Token = "";
        public DateTime LastSeen = now;
        public HashSet<string> RetiredConnections { get; } = new(StringComparer.Ordinal);
        public Dictionary<string, string> Operations { get; } = new(StringComparer.Ordinal);
    }
    private readonly Dictionary<string, Member> _members;
    private List<RerunRouteManifest> _routes = new();
    private string _manifest = "";
    private readonly HashSet<string> _markedRoutes = new(StringComparer.Ordinal);
    private readonly List<RerunMarkEvent> _marks = new();
    private readonly HashSet<string> _normalDone = new(StringComparer.Ordinal);
    private readonly HashSet<string> _prepared = new(StringComparer.Ordinal);
    private readonly HashSet<string> _finished = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _arrivals = new(StringComparer.Ordinal);
    private readonly Dictionary<string, HashSet<string>> _bypasses = new(StringComparer.Ordinal);
    private readonly Dictionary<string, RerunRouteOutcome> _outcomes = new(StringComparer.Ordinal);
    private List<string> _plan = new();
    private DateTime _stageStarted;
    private int _current;
    private long _revision;
    /// <summary>最近一次被接受的操作时间，供"阶段无进展"兜底看护计时。</summary>
    private DateTime _lastProgressUtc;
    private string _planHash = "";
    private string _reason = "";

    public string SessionId { get; } = Guid.NewGuid().ToString("N");
    public string Scope { get; }
    public int WorldRound { get; }
    public RerunStage Stage { get; private set; } = RerunStage.Registering;
    public bool BlocksLegacyAdvancement => Stage is RerunStage.Preparing or RerunStage.Running or RerunStage.Finishing;

    /// <summary>
    /// 会话是否已到终态（Completed/Aborted）。终态不承载待完成语义，可被同一世界轮的下一次
    /// Enroll 安全替换；进行中的会话绝不可被替换。
    /// </summary>
    public bool IsTerminal => Stage is RerunStage.Completed or RerunStage.Aborted;

    /// <summary>
    /// 会话是否已成孤儿：非终态，但**没有任何成员**在租约窗口内活动过。
    /// 客户端每 750ms 轮询会持续续租，因此活跃会话永远不会被判成孤儿；
    /// 只有"起过一轮、相关进程全没了、房间却还活着"的残留会话会命中。
    /// 它必须可被回收，否则下一次 Enroll 会因为 token 不匹配被永久拒绝。
    /// </summary>
    public bool IsOrphaned(DateTime now)
        => !IsTerminal && _members.Values.All(m => now - m.LastSeen >= TimeSpan.FromSeconds(45));

    public RerunExecutionState(string scope, int worldRound, IReadOnlyDictionary<string, string> rosterUidToConnection, DateTime now)
    {
        Require(rosterUidToConnection.Count > 0 && rosterUidToConnection.Count <= 4, "rerun_roster", "Invalid participant count.");
        Require(rosterUidToConnection.All(p => ValidId(p.Key) && !string.IsNullOrWhiteSpace(p.Value))
            && rosterUidToConnection.Values.Distinct(StringComparer.Ordinal).Count() == rosterUidToConnection.Count,
            "rerun_roster", "Participants must have unique identities and connections.");
        Require(!string.IsNullOrWhiteSpace(scope), "rerun_scope", "Task scope is required.");
        Scope = scope;
        WorldRound = worldRound;
        _members = rosterUidToConnection.OrderBy(p => p.Key, StringComparer.Ordinal)
            .ToDictionary(p => p.Key, p => new Member(p.Value, now), StringComparer.Ordinal);
        _stageStarted = now;
        _lastProgressUtc = now;
    }

    public RerunSnapshot Apply(string uid, string connectionId, RerunRequest request, DateTime now)
    {
        Require(_members.TryGetValue(uid, out var member), "rerun_identity", "Not a participant.");
        Require(request.Scope == Scope, "rerun_scope", "World scope differs.");
        Require(!string.IsNullOrWhiteSpace(request.ResumeToken) && request.ResumeToken.Length <= 512,
            "rerun_token", "Resume token required.");
        bool initialEnroll = request.Operation == RerunProtocol.Enroll && string.IsNullOrEmpty(request.SessionId);
        Require(initialEnroll || request.SessionId == SessionId, "rerun_session", "Session differs.");
        Require(!member!.RetiredConnections.Contains(connectionId), "rerun_binding", "Connection has been superseded.");
        if (member.Token.Length == 0)
        {
            Require(initialEnroll && member.Connection == connectionId, "rerun_identity", "Initial enrollment must use the admitted connection.");
        }
        else
        {
            Require(member.Token == request.ResumeToken, "rerun_token", "Resume token differs.");
            if (member.Connection != connectionId)
            {
                Require(!initialEnroll, "rerun_session", "Resuming requires the session identifier.");
                member.RetiredConnections.Add(member.Connection);
                member.Connection = connectionId;
                _revision++;
            }
        }
        EvaluateDeadline(now);
        member.LastSeen = now;
        if (Stage == RerunStage.Aborted) return Snapshot();
        if (request.Operation == RerunProtocol.Poll) return Snapshot();
        Require(!string.IsNullOrWhiteSpace(request.OperationId) && request.OperationId.Length <= 128,
            "rerun_operation_id", "Stable operation identifier required.");
        var fingerprint = Hash(JsonSerializer.Serialize(request));
        if (member.Operations.TryGetValue(request.OperationId, out var existing))
        {
            Require(existing == fingerprint, "rerun_operation_conflict", "Operation identifier reused with another payload.");
            return Snapshot();
        }
        if (request.Operation == RerunProtocol.Abort)
        {
            Abort(string.IsNullOrWhiteSpace(request.Reason) ? "ParticipantCancelled" : request.Reason);
            member.Operations.Add(request.OperationId, fingerprint);
            return Snapshot();
        }
        if (Stage == RerunStage.Completed) return Snapshot();
        Require(member.Operations.Count < 100_000, "rerun_limit", "Too many operations.");

        switch (request.Operation)
        {
            case RerunProtocol.Enroll:
                Enroll(member, request, now);
                break;
            case RerunProtocol.Mark:
                Mark(uid, request);
                break;
            case RerunProtocol.NormalDone:
                Require(Stage == RerunStage.Normal, "rerun_stage", "Normal execution is not active.");
                _normalDone.Add(uid);
                if (_normalDone.Count == _members.Count)
                {
                    _plan = _routes.Where(r => _markedRoutes.Contains(r.RouteId)).Select(r => r.RouteId).ToList();
                    _planHash = Hash(_manifest + JsonSerializer.Serialize(_plan));
                    Transition(_plan.Count == 0 ? RerunStage.Completed : RerunStage.Preparing, now);
                }
                break;
            case RerunProtocol.Prepare:
                Require(Stage == RerunStage.Preparing, "rerun_stage", "Plan is not preparing.");
                Require(request.PlanHash == _planHash, "rerun_plan_hash", "Frozen plan hash differs.");
                _prepared.Add(uid);
                if (_prepared.Count == _members.Count)
                {
                    Transition(RerunStage.Running, now);
                    foreach (var participant in _members.Values) participant.LastSeen = now;
                }
                break;
            case RerunProtocol.Arrive:
                var route = CurrentRoute(request);
                Require(route.Checkpoints.Any(p => p.Id == request.PointId), "rerun_point", "Unknown checkpoint.");
                Require(!_outcomes.ContainsKey(OutcomeKey(route.RouteId, uid)), "rerun_outcome", "Route outcome already submitted.");
                Set(_arrivals, PointKey(route.RouteId, request.PointId)).Add(uid);
                break;
            case RerunProtocol.Recover:
                CurrentRoute(request); // Recovery does not prove arrival and never clears bypass evidence.
                break;
            case RerunProtocol.Bypass:
                Bypass(uid, request);
                break;
            case RerunProtocol.RouteDone:
                RouteDone(uid, request, now);
                break;
            case RerunProtocol.Finish:
                Require(Stage == RerunStage.Finishing, "rerun_stage", "Final cleanup is not active.");
                _finished.Add(uid);
                if (_finished.Count == _members.Count) Transition(RerunStage.Completed, now);
                break;
            default:
                throw new RerunProtocolException("rerun_operation", "Unknown operation.");
        }
        member.Operations.Add(request.OperationId, fingerprint);
        _revision++;
        // 任何被接受的操作都算"有进展"（到达/豁免/线路结算/阶段确认）；无进展看护据此计时。
        _lastProgressUtc = now;
        return Snapshot();
    }

    private void Enroll(Member member, RerunRequest request, DateTime now)
    {
        var routes = ValidateManifest(request.Routes);
        var manifest = JsonSerializer.Serialize(routes);
        Require(_manifest.Length == 0 || manifest == _manifest, "rerun_manifest", "Canonical manifest differs.");
        Require(Stage == RerunStage.Registering || member.Token.Length > 0, "rerun_stage", "Enrollment is closed.");
        if (_manifest.Length == 0) { _manifest = manifest; _routes = routes; }
        member.Token = request.ResumeToken;
        if (Stage == RerunStage.Registering && _members.Values.All(m => m.Token.Length > 0))
        {
            Transition(RerunStage.Normal, now);
            foreach (var participant in _members.Values) participant.LastSeen = now;
        }
    }

    private void Mark(string uid, RerunRequest request)
    {
        Require(Stage is RerunStage.Normal or RerunStage.Running, "rerun_stage", "Marks are not accepted in this stage.");
        Require(request.IsReplay == (Stage == RerunStage.Running), "rerun_stage", "Mark execution phase differs.");
        Require(Stage != RerunStage.Normal || !_normalDone.Contains(uid), "rerun_normal_done", "Normal mark queue has already been closed.");
        var route = _routes.FirstOrDefault(r => r.RouteId == request.RouteId);
        Require(route != null && route.Eligible && route.Checkpoints.Any(p => p.Id == request.PointId && p.Kind == "fight"),
            "rerun_mark", "Only eligible fight checkpoints can be marked.");
        if (request.IsReplay) CurrentRoute(request);
        Require(_marks.Count < 100_000, "rerun_limit", "Too many mark events.");
        _marks.Add(new RerunMarkEvent { Sequence = _marks.Count + 1L, PlayerUid = uid, RouteId = request.RouteId, PointId = request.PointId, IsReplay = request.IsReplay });
        if (!request.IsReplay) _markedRoutes.Add(request.RouteId);
    }

    private void Bypass(string uid, RerunRequest request)
    {
        var route = CurrentRoute(request);
        Require(!_outcomes.ContainsKey(OutcomeKey(route.RouteId, uid)), "rerun_outcome", "Route outcome already submitted.");
        Require(request.SkipRoute || (request.ThroughSegment >= 0 && route.Checkpoints.Any(p => p.Segment == request.ThroughSegment)),
            "rerun_segment", "Bypass must name an acknowledged segment.");
        foreach (var point in route.Checkpoints.Where(p => request.SkipRoute || p.Segment <= request.ThroughSegment))
        {
            var key = PointKey(route.RouteId, point.Id);
            if (!Contains(_arrivals, key, uid)) Set(_bypasses, key).Add(uid);
        }
    }

    private void RouteDone(string uid, RerunRequest request, DateTime now)
    {
        var outcomeKey = OutcomeKey(request.RouteId, uid);
        if (_outcomes.TryGetValue(outcomeKey, out var previous))
        {
            Require(previous == request.Outcome, "rerun_outcome", "Terminal outcome cannot change.");
            return;
        }
        var route = CurrentRoute(request);
        Require(request.Outcome is RerunRouteOutcome.Completed or RerunRouteOutcome.Incomplete or RerunRouteOutcome.Failed or RerunRouteOutcome.Cancelled,
            "rerun_outcome", "Terminal outcome required.");
        if (request.Outcome is RerunRouteOutcome.Failed or RerunRouteOutcome.Cancelled)
        {
            _outcomes.Add(outcomeKey, request.Outcome);
            Abort(string.IsNullOrWhiteSpace(request.Reason) ? "Route" + request.Outcome : request.Reason);
            return;
        }
        if (request.Outcome == RerunRouteOutcome.Incomplete)
            Require(!string.IsNullOrWhiteSpace(request.Reason), "rerun_incomplete_reason", "Incomplete outcome requires an explicit exit reason.");
        foreach (var point in route.Checkpoints.Where(p => p.Kind != "fight"))
        {
            var key = PointKey(route.RouteId, point.Id);
            Require(Contains(_arrivals, key, uid) || (request.Outcome == RerunRouteOutcome.Incomplete && Contains(_bypasses, key, uid)),
                "rerun_unresolved", "Route checkpoints are not resolved for this participant.");
        }
        _outcomes.Add(outcomeKey, request.Outcome);
        if (_members.Keys.All(id => _outcomes.ContainsKey(OutcomeKey(route.RouteId, id))))
        {
            _current++;
            if (_current == _plan.Count) Transition(RerunStage.Finishing, now);
        }
    }

    private RerunRouteManifest CurrentRoute(RerunRequest request)
    {
        Require(Stage == RerunStage.Running && _current < _plan.Count && _plan[_current] == request.RouteId,
            "rerun_route", "Only the currently committed route may advance.");
        return _routes.First(r => r.RouteId == request.RouteId);
    }

    public void Abort(string reason)
    {
        if (Stage is RerunStage.Aborted or RerunStage.Completed) return;
        Stage = RerunStage.Aborted; _reason = reason.Length > 512 ? reason[..512] : reason; _revision++;
    }

    private void EvaluateDeadline(DateTime now)
    {
        if (Stage is RerunStage.Completed or RerunStage.Aborted) return;
        if (Stage is RerunStage.Registering or RerunStage.Preparing)
        {
            if (now - _stageStarted >= TimeSpan.FromSeconds(120)) Abort("PreparationTimeout");
            return;
        }

        // 成员租约：任何阶段都适用（轮询会续租，掉线/进程消失才会计满）。
        if (_members.Values.Any(m => now - m.LastSeen >= TimeSpan.FromSeconds(45)))
        {
            Abort("ParticipantLeaseExpired");
            return;
        }

        // 无进展兜底看护**只对"重跑进行中"的阶段生效**。
        // 正常轮（Normal）唯一可被接受的业务操作是"某白名单线路战斗点死亡标记"——正常轮动辄 20–60 分钟
        // 而没有一次死亡是完全正常的，因此绝不能把无进展判据套到 Normal：那会把大多数正常轮误判成失败
        // 并连带终止整个会话（这正是本看护必须限定阶段的原因）。
        if (Stage is RerunStage.Running or RerunStage.Finishing && now - _lastProgressUtc >= NoProgressBound)
        {
            Abort("NoProgress");
        }
    }

    /// <summary>
    /// 阶段无进展上界。远大于正常跨端等待（单条线路的完整遍历），仅用于把"无限互等"变成显式中止。
    /// 必须大于客户端单点等待上界（8 分钟），避免正常等待被判成无进展。
    /// </summary>
    private static readonly TimeSpan NoProgressBound = TimeSpan.FromMinutes(12);

    private void Transition(RerunStage stage, DateTime now) { Stage = stage; _stageStarted = now; }
    private RerunSnapshot Snapshot()
    {
        var currentRoute = _current < _plan.Count ? _plan[_current] : "";
        return new RerunSnapshot
        {
            SessionId = SessionId, Scope = Scope, Stage = Stage, Revision = _revision, PlanHash = _planHash,
            Participants = _members.Keys.ToList(), Plan = _plan.ToList(), CurrentPlanIndex = _current, CurrentRouteId = currentRoute,
            Marks = _marks.Select(m => new RerunMarkEvent { Sequence = m.Sequence, PlayerUid = m.PlayerUid, RouteId = m.RouteId, PointId = m.PointId, IsReplay = m.IsReplay }).ToList(),
            ResolvedPoints = currentRoute.Length == 0 ? new() : _routes.First(r => r.RouteId == currentRoute).Checkpoints
                .Where(p => _members.Keys.All(uid => Contains(_arrivals, PointKey(currentRoute, p.Id), uid) || Contains(_bypasses, PointKey(currentRoute, p.Id), uid)))
                .Select(p => p.Id).ToList(),
            Outcomes = new Dictionary<string, RerunRouteOutcome>(_outcomes, StringComparer.Ordinal), Reason = _reason
        };
    }
    private static List<RerunRouteManifest> ValidateManifest(List<RerunRouteManifest>? routes)
    {
        Require(routes != null && routes.Count <= 10_000, "rerun_manifest", "Invalid manifest.");
        var ids = new HashSet<string>(StringComparer.Ordinal);
        var indices = new HashSet<int>();
        foreach (var route in routes!)
        {
            Require(route != null && ValidId(route.RouteId) && route.OriginalIndex >= 0 && ids.Add(route.RouteId) && indices.Add(route.OriginalIndex),
                "rerun_manifest", "Route occurrence identity must be unique.");
            Require(route!.Checkpoints != null && route.Checkpoints.Count <= 10_000, "rerun_manifest", "Invalid checkpoints.");
            var points = new HashSet<string>(StringComparer.Ordinal);
            var segment = -1;
            foreach (var point in route.Checkpoints!)
            {
                Require(point != null && ValidId(point.Id) && points.Add(point.Id) && point.Segment >= segment && point.Segment >= 0
                    && point.Kind is "teleport" or "sync" or "fight", "rerun_manifest", "Invalid canonical checkpoint order.");
                segment = point!.Segment;
            }
        }
        // Detach mutable input lists; physical indices are absent from this contract.
        return routes!.OrderBy(r => r.OriginalIndex).Select(r => new RerunRouteManifest
        {
            RouteId = r.RouteId, OriginalIndex = r.OriginalIndex, Eligible = r.Eligible,
            Checkpoints = r.Checkpoints.Select(p => new RerunCheckpoint { Id = p.Id, Segment = p.Segment, Kind = p.Kind }).ToList()
        }).ToList();
    }
    private static bool ValidId(string? id) => !string.IsNullOrWhiteSpace(id) && id.Length <= 512 && !id.Contains('/') && !id.Contains('\0');
    private static string PointKey(string route, string point) => route + "/" + point;
    private static string OutcomeKey(string route, string uid) => route + "/" + uid;
    private static HashSet<string> Set(Dictionary<string, HashSet<string>> sets, string key)
    {
        if (!sets.TryGetValue(key, out var set)) sets[key] = set = new(StringComparer.Ordinal);
        return set;
    }
    private static bool Contains(Dictionary<string, HashSet<string>> sets, string key, string uid) => sets.TryGetValue(key, out var set) && set.Contains(uid);
    private static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private static void Require(bool condition, string code, string message)
    {
        if (!condition) throw new RerunProtocolException(code, message);
    }
}
