namespace BgiCoordinatorServer.Models;

/// <summary>同次开队的线路预判；空计划是明确终态，不能当作尚未获得轮换序列。</summary>
public sealed class WorldPlanSnapshot
{
    public string PlanId { get; init; } = "";
    public string Stage { get; init; } = "Planning";
    public string InitialHostUid { get; init; } = "";
    public string InitialRoomCode { get; init; } = "";
    public string InitialRoomSessionId { get; init; } = "";
    public DateTime ExpiresAtUtc { get; init; }
    public List<string> ParticipantUids { get; init; } = [];
    public List<string> CandidateHostUids { get; init; } = [];
    public List<string> RemainingHostUids { get; init; } = [];
    public RoomConfig HostConfig { get; init; } = new();
    public List<PlayerInfo> Players { get; init; } = [];
    public string? TargetRoomCode { get; init; }
    public string? TargetRoomSessionId { get; init; }
    public bool SourceReleased { get; init; }
    public bool CurrentPartyFrozen { get; init; }
}

/// <summary>票据只发给对应源房成员，不通过房间广播共享。</summary>
public sealed record WorldPlanAdmission(string PlanId, string Ticket, WorldPlanSnapshot Snapshot);
