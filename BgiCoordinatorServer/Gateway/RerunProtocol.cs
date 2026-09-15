#nullable enable
using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.Shared.CooperativeRerun;

// 共同重跑的唯一协议来源（客户端与服务器共用同一份文件，客户端通过 csproj Link 编译它）。
//
// 放置位置的两条硬约束（都踩过，别再改）：
// 1) 必须位于 BgiCoordinatorServer 工程目录内。服务端的 Docker 构建上下文就是这个目录
//    （docker-compose `build: .`，Dockerfile 先 COPY csproj 再 COPY . .），
//    任何指向父目录的路径（如原先的 `..\Shared\...`）在容器里不存在 →
//    `CS2001: Source file '/src/../Shared/...' could not be found` 直接构建失败。
// 2) 必须放在**已有的**子目录里，不要为它新建文件夹。部署到服务器时是按目录清单同步的，
//    新出现的顶层文件夹容易被漏掉 → 容器里编译不到这些类型，报一屏 CS0246（"namespace
//    BetterGenshinImpact could not be found"）。放在 Gateway/ 与 GatewayProtocol.cs 同处：
//    能力常量、消息名与网关路由本来就集中在那里。
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
