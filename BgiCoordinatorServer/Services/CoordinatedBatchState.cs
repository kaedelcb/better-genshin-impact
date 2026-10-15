namespace BgiCoordinatorServer.Services;

public sealed record CoordinatedBatchBinding(string Name, string Type);
public sealed record CoordinatedBatchMemberSnapshot(string Uid, string Stage, string Reason);

public sealed record CoordinatedBatchSnapshot(string BatchId, int Generation, int Index, int Attempt,
    long Revision, string Phase, string Reason)
{
    public string Protocol { get; init; } = "hoeing.start.v2";
    public string Source { get; init; } = "online";
    public string RequestId { get; init; } = "";
    public IReadOnlyList<string> ParticipantUids { get; init; } = [];
    public IReadOnlyDictionary<string, int> IntentGenerations { get; init; } = new Dictionary<string, int>();
    public IReadOnlyDictionary<string, IReadOnlyList<CoordinatedBatchBinding>> Bindings { get; init; }
        = new Dictionary<string, IReadOnlyList<CoordinatedBatchBinding>>();
    public IReadOnlyList<CoordinatedBatchMemberSnapshot> Members { get; init; } = [];
    public IReadOnlyList<string> MissingRegistrationUids { get; init; } = [];
    public bool CleanupComplete { get; init; }
}

public sealed record CoordinatedBatchResetResult
{
    public string ResetId { get; init; } = "";
    public bool Found { get; init; }
    public string BatchId { get; init; } = "";
    public int Generation { get; init; }
    public string Phase { get; init; } = "none";
    public string Reason { get; init; } = "";
    public IReadOnlyList<string> TargetUids { get; init; } = [];
    public IReadOnlyList<string> MissingRegistrationUids { get; init; } = [];
}

/// <summary>控制房间拥有开始、全队推进与清理权限；调用者串行访问。</summary>
public sealed class CoordinatedBatchState
{
    private sealed class Member(DateTime now)
    {
        public string Token = "";
        public string Result = "";
        public string Stage = "pending";
        public string Reason = "";
        public DateTime LastSeen = now;
    }
    private readonly Dictionary<string, Member> _members;
    private readonly DateTime _created;
    private DateTime _phaseStarted;
    private string? _layout;
    private int _count;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<CoordinatedBatchBinding>> _bindings;
    private readonly IReadOnlyDictionary<string, int> _intentGenerations;
    private readonly IReadOnlyDictionary<string, string> _executorInstances;
    public string BatchId { get; } = Guid.NewGuid().ToString("N");
    public int Generation { get; }
    public string Source { get; }
    public string RequestId { get; }
    public int Index { get; private set; }
    public int Attempt { get; private set; }
    public long Revision { get; private set; }
    public string Phase { get; private set; } = "registering";
    public string Reason { get; private set; } = "";
    public bool ExecutionEnded => Phase is "completed" or "aborted";
    public bool IsTerminal => ExecutionEnded && _members.Values.All(m => m.Stage == "cleaned");
    public IReadOnlyList<string> ParticipantUids => _members.Keys.ToArray();

    public CoordinatedBatchState(int generation, IEnumerable<string> uids, DateTime now,
        string source = "online", string requestId = "",
        IReadOnlyDictionary<string, IReadOnlyList<CoordinatedBatchBinding>>? bindings = null,
        IReadOnlyDictionary<string, int>? intentGenerations = null,
        IReadOnlyDictionary<string, string>? executorInstances = null)
    {
        Generation = generation;
        Source = source;
        RequestId = requestId;
        _created = _phaseStarted = now;
        _members = uids.Distinct(StringComparer.Ordinal).ToDictionary(u => u, _ => new Member(now), StringComparer.Ordinal);
        if (_members.Count is < 1 or > 4 || _members.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid fixed batch roster");
        _bindings = bindings?.ToDictionary(p => p.Key,
            p => (IReadOnlyList<CoordinatedBatchBinding>)p.Value.ToArray(), StringComparer.Ordinal)
            ?? new Dictionary<string, IReadOnlyList<CoordinatedBatchBinding>>();
        _intentGenerations = intentGenerations?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, int>();
        _executorInstances = executorInstances?.ToDictionary(p => p.Key, p => p.Value, StringComparer.Ordinal)
            ?? new Dictionary<string, string>();
        if (_bindings.Count > 0)
        {
            _layout = string.Join(",", _bindings[_members.Keys.First()].Select(i => i.Type));
            _count = _bindings[_members.Keys.First()].Count;
        }
    }

    public bool MatchesExecutor(string uid, string instanceId)
        => !_executorInstances.TryGetValue(uid, out var expected) || expected == instanceId;

    public CoordinatedBatchSnapshot Snapshot() => new(BatchId, Generation, Index, Attempt, Revision, Phase, Reason)
    {
        Source = Source,
        RequestId = RequestId,
        ParticipantUids = ParticipantUids,
        IntentGenerations = _intentGenerations,
        Bindings = _bindings,
        Members = _members.Select(p => new CoordinatedBatchMemberSnapshot(p.Key, p.Value.Stage, p.Value.Reason)).ToArray(),
        MissingRegistrationUids = Reason == "registration_timeout"
            ? _members.Where(p => p.Value.Token.Length == 0).Select(p => p.Key).ToArray() : [],
        CleanupComplete = IsTerminal,
    };

    public void Abort(string reason)
    {
        if (ExecutionEnded) return;
        Phase = "aborted";
        Reason = reason;
        // 未登记成员没有获得接管权；迟到通知只能读到终态，不能再执行准备或接管。
        foreach (var member in _members.Values.Where(m => m.Token.Length == 0)) member.Stage = "cleaned";
        Revision++;
    }

    public void CheckTimeout(DateTime now)
    {
        if (ExecutionEnded) return;
        if (Phase == "registering" && now - _created > TimeSpan.FromMinutes(3)) Abort("registration_timeout");
        else if (Phase == "preparing" && now - _phaseStarted > TimeSpan.FromMinutes(3)) Abort("preparation_timeout");
        else if (Phase == "takeover" && now - _phaseStarted > TimeSpan.FromSeconds(90)) Abort("takeover_timeout");
        else if (Phase == "stopping" && now - _phaseStarted > TimeSpan.FromSeconds(90)) Abort("cleanup_timeout");
        else if ((Phase is "running" or "stopping") && _members.Values.Any(m => now - m.LastSeen > TimeSpan.FromSeconds(90)))
            Abort("participant_lease_expired");
    }

    public CoordinatedBatchSnapshot ConfirmResetCleaned(string uid)
    {
        if (!ExecutionEnded || !_members.TryGetValue(uid, out var member))
            throw new InvalidOperationException("batch_reset_cleanup_not_authorized");
        if (member.Stage != "cleaned") { member.Stage = "cleaned"; Revision++; }
        return Snapshot();
    }

    public CoordinatedBatchSnapshot Apply(string uid, string token, string batchId, string layout,
        int index, int attempt, string result, DateTime now, string stage = "", string reason = "")
    {
        if (!_members.TryGetValue(uid, out var member)) throw new InvalidOperationException("batch_not_participant");
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) throw new InvalidOperationException("batch_token");
        if (batchId != BatchId) throw new InvalidOperationException("batch_identity");
        if (member.Token.Length > 0 && member.Token != token) throw new InvalidOperationException("batch_participant_token_mismatch");
        if (reason.Length > 512) throw new InvalidOperationException("batch_reason");
        if (result is not ("" or "succeeded" or "failed" or "stopped" or "cancelled" or "unknown" or "preparation_failed" or "takeover_failed"))
            throw new InvalidOperationException("batch_result");
        if (stage is not ("" or "registered" or "prepared" or "takeoverConfirmed" or "cleaned"))
            throw new InvalidOperationException("batch_stage");
        CheckTimeout(now);
        if (ExecutionEnded)
        {
            if (stage == "cleaned" && member.Token == token && member.Stage != "cleaned")
            { member.Stage = "cleaned"; Revision++; }
            return Snapshot();
        }
        if (stage == "cleaned") throw new InvalidOperationException("batch_cleanup_before_terminal");
        if (layout.Length is < 1 or > 1024 || layout.Split(',').Any(x => x is not ("group" or "onedragon")))
            throw new InvalidOperationException("batch_layout");
        if (_layout != null && _layout != layout)
        {
            member.Token = token;
            member.Stage = "registered";
            Abort("batch_layout_mismatch:" + uid);
            return Snapshot();
        }
        _layout ??= layout;
        _count = layout.Split(',').Length;
        if (member.Token.Length == 0)
        {
            if (stage != "registered") throw new InvalidOperationException("batch_registration_required");
            member.Token = token;
            member.Stage = "registered";
            Revision++;
        }
        member.LastSeen = now;
        if ((Phase is "running" or "stopping") && (index != Index || attempt != Attempt)) return Snapshot();
        if (result is "cancelled" or "unknown" or "preparation_failed" or "takeover_failed")
        {
            member.Reason = reason;
            Abort($"participant_{result}:{uid}:{Phase}" + (reason.Length > 0 ? ":" + reason : ""));
            return Snapshot();
        }
        if (Phase is "registering" or "preparing" or "takeover")
        {
            if (result.Length > 0) throw new InvalidOperationException("batch_not_started");
            if (stage == "prepared" && member.Stage == "registered")
            {
                if (Phase == "takeover") throw new InvalidOperationException("batch_preparation_phase");
                member.Stage = "prepared"; Revision++;
            }
            if (stage == "takeoverConfirmed" && member.Stage != "takeoverConfirmed")
            {
                if (Phase != "takeover" || member.Stage != "prepared") throw new InvalidOperationException("batch_takeover_not_authorized");
                member.Stage = "takeoverConfirmed"; Revision++;
            }
            if (Phase == "registering" && _members.Values.All(m => m.Token.Length > 0)) SetPhase("preparing", now);
            if (Phase == "preparing" && _members.Values.All(m => m.Stage == "prepared")) SetPhase("takeover", now);
            if (Phase == "takeover" && _members.Values.All(m => m.Stage == "takeoverConfirmed")) SetPhase("running", now);
            return Snapshot();
        }
        // 旧项/旧attempt回执不能污染后继项。重复准备消息也不能回退状态。
        if (index != Index || attempt != Attempt) return Snapshot();
        if (result.Length > 0)
        {
            if (member.Result.Length > 0 && member.Result != result)
            { Abort("conflicting_terminal_result"); return Snapshot(); }
            if (result == "stopped" && Phase != "stopping") { Abort("unexpected_stop"); return Snapshot(); }
            member.Result = result;
            if (result == "failed" && Phase == "running")
            {
                Reason = "participant_failed:" + uid;
                SetPhase("stopping", now);
            }
        }
        if (Phase == "running" && _members.Values.All(m => m.Result == "succeeded"))
        {
            Index++;
            Attempt = 0;
            foreach (var m in _members.Values) m.Result = "";
            SetPhase(Index == _count ? "completed" : "running", now);
        }
        else if (Phase == "stopping" && _members.Values.All(m => m.Result.Length > 0))
        {
            if (Attempt >= 1) Abort("recovery_budget_exhausted");
            else
            {
                Attempt++;
                foreach (var m in _members.Values) m.Result = "";
                Reason = "shared_recovery";
                SetPhase("running", now);
            }
        }
        return Snapshot();
    }

    private void SetPhase(string phase, DateTime now)
    {
        Phase = phase;
        _phaseStarted = now;
        Revision++;
    }
}
