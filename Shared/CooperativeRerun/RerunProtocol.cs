#nullable enable
using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.Shared.CooperativeRerun;

// Shared wire contracts. No game, UI, transport or server dependencies.
public static class RerunProtocol
{
    public const string Capability = "hoeing.rerun.v1";
    public const string Update = "rerun.update";
    public const string State = "rerun.state";
    public const string Enroll = "enroll";
    public const string Mark = "mark";
    public const string NormalDone = "normalDone";
    public const string Prepare = "prepare";
    public const string Arrive = "arrive";
    public const string Recover = "recover";
    public const string Bypass = "bypass";
    public const string RouteDone = "routeDone";
    public const string Finish = "finish";
    public const string Abort = "abort";
    public const string Poll = "poll";
}

public enum RerunStage { Registering, Normal, Preparing, Running, Finishing, Completed, Aborted }
public enum RerunRouteOutcome { None, Completed, Incomplete, Failed, Cancelled }

public sealed class RerunCheckpoint
{
    public string Id { get; set; } = "";
    public int Segment { get; set; }
    // teleport / sync / fight. Order in the manifest is authoritative.
    public string Kind { get; set; } = "";
}

public sealed class RerunRouteManifest
{
    // Stable occurrence identity in the host's final list, not a local filename/index cursor.
    public string RouteId { get; set; } = "";
    public int OriginalIndex { get; set; }
    public bool Eligible { get; set; }
    public List<RerunCheckpoint> Checkpoints { get; set; } = new();
}

public sealed class RerunRequest
{
    public string Operation { get; set; } = RerunProtocol.Poll;
    public string OperationId { get; set; } = "";
    public string SessionId { get; set; } = "";
    // World round in this room. Separate tasks use separate room sessions.
    public string Scope { get; set; } = "";
    // Random client-generated secret retained only by this task; required on every request.
    public string ResumeToken { get; set; } = "";
    public string PlanHash { get; set; } = "";
    public List<RerunRouteManifest> Routes { get; set; } = new();
    public string RouteId { get; set; } = "";
    public string PointId { get; set; } = "";
    public bool IsReplay { get; set; }
    public int ThroughSegment { get; set; } = -1;
    public bool SkipRoute { get; set; }
    public RerunRouteOutcome Outcome { get; set; }
    public string Reason { get; set; } = "";
}

public sealed class RerunMarkEvent
{
    public long Sequence { get; set; }
    public string PlayerUid { get; set; } = "";
    public string RouteId { get; set; } = "";
    public string PointId { get; set; } = "";
    public bool IsReplay { get; set; }
}

public sealed class RerunSnapshot
{
    public string SessionId { get; set; } = "";
    public string Scope { get; set; } = "";
    public RerunStage Stage { get; set; }
    public long Revision { get; set; }
    public string PlanHash { get; set; } = "";
    public List<string> Participants { get; set; } = new();
    public List<string> Plan { get; set; } = new();
    public int CurrentPlanIndex { get; set; }
    public string CurrentRouteId { get; set; } = "";
    public List<RerunMarkEvent> Marks { get; set; } = new();
    public List<string> ResolvedPoints { get; set; } = new();
    // Keys are route occurrence + '/' + participant UID; terminal outcomes never change.
    public Dictionary<string, RerunRouteOutcome> Outcomes { get; set; } = new();
    public string Reason { get; set; } = "";
}
