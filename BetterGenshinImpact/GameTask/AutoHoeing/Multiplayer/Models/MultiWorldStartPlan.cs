#nullable enable
using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;

/// <summary>本次开队的权威预判快照。空 RemainingHostUids 仅在 NoWork 时表示无需进世界。</summary>
public sealed class MultiWorldStartPlan
{
    public string PlanId { get; set; } = "";
    public string Stage { get; set; } = "Planning";
    public string InitialRoomCode { get; set; } = "";
    public string InitialRoomSessionId { get; set; } = "";
    public DateTime ExpiresAtUtc { get; set; }
    public string InitialHostUid { get; set; } = "";
    public List<string> ParticipantUids { get; set; } = new();
    public List<string> CandidateHostUids { get; set; } = new();
    public List<string> RemainingHostUids { get; set; } = new();
    public RoomConfig? HostConfig { get; set; }
    public List<PlayerInfo> Players { get; set; } = new();
    public bool SourceReleased { get; set; }
    public string? TargetRoomCode { get; set; }
    public string? TargetRoomSessionId { get; set; }
}

/// <summary>每个成员各自的短期查询凭据；不写日志、不借用其他成员的 ticket。</summary>
public sealed class WorldPlanAdmission
{
    public string PlanId { get; set; } = "";
    public string Ticket { get; set; } = "";
    public MultiWorldStartPlan Snapshot { get; set; } = new();
}
