#nullable enable
using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.Shared.CooperativeRerun;

// 共同重跑的唯一协议来源（客户端与服务器共用）。
//
// 为什么放在服务端工程目录内，而不是仓库根的独立 Shared 目录：
// 服务端的 Docker 构建上下文就是 BgiCoordinatorServer 目录（docker-compose 的 `build: .`，
// 且 Dockerfile 先 `COPY ["BgiCoordinatorServer.csproj", "."]` 再 `COPY . .`），
// 任何指向父目录的文件（如 `..\Shared\...`）在容器里都不存在 —— 会以
// `CS2001: Source file '/src/../Shared/...' could not be found` 直接构建失败。
// 客户端通过 BetterGenshinImpact.csproj 的 <Compile Include ... Link> 链接同一个文件，
// 因此仍然是单一来源，不存在两份拷贝漂移的风险。
//
// 约束：本文件只能依赖 BCL，不得引用游戏、UI、传输或服务端类型（两端都要能编译它）。

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
