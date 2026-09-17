namespace BgiCoordinatorServer.Services;

public sealed record CoordinatedBatchSnapshot(string BatchId, int Generation, int Index, int Attempt,
    long Revision, string Phase, string Reason);

/// <summary>Control-room lifetime, independent of gameplay rooms. Caller serializes access.</summary>
public sealed class CoordinatedBatchState
{
    private sealed class Member(DateTime now)
    {
        public string Token = "";
        public string Result = "";
        public DateTime LastSeen = now;
    }
    private readonly Dictionary<string, Member> _members;
    private readonly DateTime _created;
    private DateTime _stopping;
    private string? _layout;
    private int _count;
    public string BatchId { get; } = Guid.NewGuid().ToString("N");
    public int Generation { get; }
    public int Index { get; private set; }
    public int Attempt { get; private set; }
    public long Revision { get; private set; }
    public string Phase { get; private set; } = "registering";
    public string Reason { get; private set; } = "";
    public bool IsTerminal => Phase is "completed" or "aborted";

    public CoordinatedBatchState(int generation, IEnumerable<string> uids, DateTime now)
    {
        Generation = generation;
        _created = now;
        _members = uids.Distinct(StringComparer.Ordinal).ToDictionary(u => u, _ => new Member(now));
        if (_members.Count is < 1 or > 4 || _members.Keys.Any(string.IsNullOrWhiteSpace))
            throw new ArgumentException("Invalid fixed batch roster");
    }

    public CoordinatedBatchSnapshot Snapshot() => new(BatchId, Generation, Index, Attempt, Revision, Phase, Reason);

    public void Abort(string reason)
    {
        if (IsTerminal) return;
        Phase = "aborted";
        Reason = reason;
        Revision++;
    }

    public CoordinatedBatchSnapshot Apply(string uid, string token, string batchId, string layout,
        int index, int attempt, string result, DateTime now)
    {
        if (!_members.TryGetValue(uid, out var member)) throw new InvalidOperationException("batch_not_participant");
        if (string.IsNullOrWhiteSpace(token) || token.Length > 128) throw new InvalidOperationException("batch_token");
        if (batchId.Length > 0 && batchId != BatchId) throw new InvalidOperationException("batch_identity");
        // A restarted assistant must not silently adopt an old attempt.
        if (member.Token.Length > 0 && member.Token != token) { Abort("participant_restarted"); return Snapshot(); }
        if (result is not ("" or "succeeded" or "failed" or "stopped" or "cancelled" or "unknown"))
            throw new InvalidOperationException("batch_result");
        if (layout.Length is < 1 or > 1024 || layout.Split(',').Any(x => x is not ("group" or "onedragon")))
            throw new InvalidOperationException("batch_layout");
        if (_layout != null && _layout != layout) { Abort("batch_layout_mismatch"); return Snapshot(); }
        if (IsTerminal) return Snapshot();
        if (Phase == "registering" && now - _created > TimeSpan.FromMinutes(3)) Abort("registration_timeout");
        if (Phase != "registering" && _members.Values.Any(m => now - m.LastSeen > TimeSpan.FromSeconds(90)))
            Abort("participant_lease_expired");
        if (Phase == "stopping" && now - _stopping > TimeSpan.FromSeconds(90)) Abort("cleanup_timeout");
        if (IsTerminal) return Snapshot();
        _layout ??= layout;
        _count = layout.Split(',').Length;
        member.Token = token;
        member.LastSeen = now;
        if (Phase == "registering")
        {
            if (result is "cancelled" or "unknown") { Abort(result); return Snapshot(); }
            if (result != "") throw new InvalidOperationException("batch_not_started");
            if (_members.Values.All(m => m.Token.Length > 0)) { Phase = "running"; Revision++; }
            return Snapshot();
        }
        // Late responses from prior items/attempts cannot mutate the new execution.
        if (index != Index || attempt != Attempt) return Snapshot();
        if (result is "cancelled" or "unknown") { Abort(result); return Snapshot(); }
        if (result.Length > 0)
        {
            if (member.Result.Length > 0 && member.Result != result)
            {
                Abort("conflicting_terminal_result");
                return Snapshot();
            }
            if (result == "stopped" && Phase != "stopping") { Abort("unexpected_stop"); return Snapshot(); }
            member.Result = result;
            if (result == "failed" && Phase == "running")
            {
                Phase = "stopping";
                _stopping = now;
                Reason = "participant_failed";
                Revision++;
            }
        }
        if (Phase == "running" && _members.Values.All(m => m.Result == "succeeded"))
        {
            Index++;
            Attempt = 0;
            foreach (var m in _members.Values) m.Result = "";
            Phase = Index == _count ? "completed" : "running";
            Revision++;
        }
        else if (Phase == "stopping" && _members.Values.All(m => m.Result.Length > 0))
        {
            if (Attempt >= 1) Abort("recovery_budget_exhausted");
            else
            {
                Attempt++;
                foreach (var m in _members.Values) m.Result = "";
                Phase = "running";
                Reason = "shared_recovery";
                Revision++;
            }
        }
        return Snapshot();
    }
}
