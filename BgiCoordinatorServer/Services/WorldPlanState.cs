using System.Security.Cryptography;
using System.Text.Json;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>进游戏世界前的一次计划。源房销毁后仍可凭各自票据查询；不继承上次开队状态。</summary>
public sealed class WorldPlanState
{
    public const string Capability = "hoeing.worldPlan.v1";
    public object Sync { get; } = new();
    public SemaphoreSlim TransferGate { get; } = new(1, 1);
    public string Id { get; } = Guid.NewGuid().ToString("N");
    public string InitialRoomCode { get; }
    public string InitialRoomSessionId { get; }
    public string InitialHostUid { get; }
    public DateTime ExpiresAtUtc { get; }
    public RoomConfig Config { get; }
    public List<PlayerInfo> Players { get; }
    public List<string> Participants { get; }
    public List<string> Candidates { get; }
    private readonly Dictionary<string, string> _tickets;
    private readonly HashSet<string> _acquired = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (bool Known, int Count)> _reports = new(StringComparer.Ordinal);
    private readonly HashSet<string> _acks = new(StringComparer.Ordinal);
    private readonly HashSet<string> _sourceReady = new(StringComparer.Ordinal);
    private string _stage;
    private string? _targetCode;
    private string? _targetSession;
    private bool _sourceReleased;

    public WorldPlanState(Room room, bool capable, DateTime now)
    {
        InitialRoomCode = room.Code;
        InitialRoomSessionId = room.SessionId;
        Config = Clone(room.HostConfig ?? throw new InvalidOperationException("world_plan_config_missing"));
        Players = Clone(room.Players);
        InitialHostUid = Players.Single(p => p.ConnectionId == room.HostConnectionId).PlayerUid;
        Participants = Players.Select(p => p.PlayerUid).ToList();
        if (Participants.Count is < 1 or > 4 || capable && (Participants.Any(string.IsNullOrWhiteSpace)
            || Participants.Distinct(StringComparer.Ordinal).Count() != Participants.Count))
            throw new InvalidOperationException("world_plan_roster_invalid");
        Candidates = RoundHostOrderDecisions.Build(Players, InitialHostUid)
            .Take(Math.Clamp(Config.MultiWorldCount, 1, 4)).ToList();
        ExpiresAtUtc = now.AddSeconds(Math.Max(300L, (long)Math.Clamp(Config.PartyTimeoutSeconds, 1, 86400) * 3 + 120));
        _tickets = Participants.Distinct(StringComparer.Ordinal).ToDictionary(uid => uid,
            _ => Convert.ToHexString(RandomNumberGenerator.GetBytes(32)), StringComparer.Ordinal);
        _stage = capable ? "Planning" : "Legacy";
    }

    private static T Clone<T>(T value) => JsonSerializer.Deserialize<T>(JsonSerializer.Serialize(value))!;

    public WorldPlanAdmission Acquire(string uid, DateTime now)
    {
        lock (Sync)
        {
            if (!_tickets.TryGetValue(uid, out var ticket))
            {
                // Legacy keeps the original open-room admission rules; a later ordinary member also learns the same fallback.
                if (_stage != "Legacy") throw new InvalidOperationException("world_plan_not_participant");
                _tickets[uid] = ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
            }
            _acquired.Add(uid);
            return new(Id, ticket, Snapshot(now));
        }
    }

    public string Authenticate(string ticket)
    {
        lock (Sync)
        {
            if (string.IsNullOrEmpty(ticket)) throw new InvalidOperationException("world_plan_ticket_invalid");
            // Tickets are random bearer credentials, never inferred from the caller's self-reported UID.
            return _tickets.FirstOrDefault(p => p.Value == ticket).Key
                ?? throw new InvalidOperationException("world_plan_ticket_invalid");
        }
    }

    private void Expire(DateTime now)
    {
        if (now >= ExpiresAtUtc) _stage = "Aborted";
    }

    public WorldPlanSnapshot Report(string ticket, bool known, int count, DateTime now)
    {
        lock (Sync)
        {
            var uid = Authenticate(ticket);
            Expire(now);
            if (count < 0) throw new InvalidOperationException("world_plan_count_invalid");
            if (_stage == "Planning")
            {
                // A retry cannot revise a report after other clients have acknowledged the selection.
                if (_reports.TryGetValue(uid, out var prior) && prior != (known, count))
                    throw new InvalidOperationException("world_plan_report_changed");
                _reports[uid] = (known, count);
                if (_reports.Count == Participants.Count) _stage = "Prepared";
            }
            return Snapshot(now);
        }
    }

    public WorldPlanSnapshot Ack(string ticket, DateTime now)
    {
        lock (Sync)
        {
            var uid = Authenticate(ticket);
            Expire(now);
            if (_stage == "Prepared")
            {
                _acks.Add(uid);
                if (_acks.Count == Participants.Count && _acquired.Count == Participants.Count)
                {
                    _stage = _reports.Values.Any(r => !r.Known) ? "Legacy"
                        : Remaining().Count == 0 ? "NoWork" : "Committed";
                    if (_stage == "Committed" && Remaining()[0] == InitialHostUid)
                        SetTarget(InitialRoomCode, InitialRoomSessionId);
                }
            }
            return Snapshot(now);
        }
    }

    public WorldPlanSnapshot Abort(string ticket, DateTime now)
    {
        lock (Sync)
        {
            Authenticate(ticket);
            _stage = "Aborted";
            return Snapshot(now);
        }
    }

    public string RequireTarget(string ticket, DateTime now)
    {
        lock (Sync)
        {
            var uid = Authenticate(ticket);
            Expire(now);
            if (_stage != "Committed" || Remaining().FirstOrDefault() != uid)
                throw new InvalidOperationException("world_plan_target_not_committed");
            return uid;
        }
    }

    public void SetTarget(string code, string session)
    {
        lock (Sync)
        {
            if (_targetCode != null && (_targetCode != code || _targetSession != session))
                throw new InvalidOperationException("world_plan_target_changed");
            _targetCode = code;
            _targetSession = session;
        }
    }

    /// <summary>每一端先建立本地旧房事件隔离，再登记。提交计划本身不能代替这一步。</summary>
    public bool RegisterSourceReady(string ticket, DateTime now)
    {
        lock (Sync)
        {
            var uid = Authenticate(ticket);
            Expire(now);
            if (_stage is not ("Committed" or "NoWork"))
                throw new InvalidOperationException("world_plan_not_committed");
            if (_stage == "Committed" && Remaining()[0] == InitialHostUid)
                throw new InvalidOperationException("world_plan_source_is_target");
            _sourceReady.Add(uid);
            return _sourceReady.Count == Participants.Count;
        }
    }

    public void ReleaseSource() { lock (Sync) _sourceReleased = true; }

    private List<string> Remaining() => Candidates.Where(uid => _reports.TryGetValue(uid, out var report)
        && report.Known && report.Count > 0).ToList();

    public WorldPlanSnapshot Snapshot(DateTime now)
    {
        lock (Sync)
        {
            Expire(now);
            return new()
            {
                PlanId = Id, Stage = _stage, InitialHostUid = InitialHostUid,
                InitialRoomCode = InitialRoomCode, InitialRoomSessionId = InitialRoomSessionId,
                ExpiresAtUtc = ExpiresAtUtc, ParticipantUids = [.. Participants], CandidateHostUids = [.. Candidates],
                RemainingHostUids = Remaining(), HostConfig = Clone(Config), Players = Clone(Players),
                TargetRoomCode = _targetCode, TargetRoomSessionId = _targetSession,
                SourceReleased = _sourceReleased,
            };
        }
    }
}
