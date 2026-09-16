#nullable enable

using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;

/// <summary>
/// 路线边界锚点权威快照（route-anchor）· 客户端侧 DTO。
///
/// 与服务端 <c>RouteAnchorSnapshot</c> 逐字段对应（服务器为唯一权威，改动须双向同步）。
/// 设计纪律：查询与所有变更响应返回同一形状；枚举以字符串给出，客户端不依赖枚举序号。
/// </summary>
public sealed class RouteAnchorSnapshotDto
{
    /// <summary>是否存在活动锚点。</summary>
    public bool HasAnchor { get; set; }

    /// <summary>锚点 ID（服务端生成）。</summary>
    public string AnchorId { get; set; } = "";

    /// <summary>会话标识（服务端生成；客户端只回带，不生成）。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>世界代际（服务端当前世界轮次）。</summary>
    public int WorldEpoch { get; set; }

    /// <summary>冻结的路线计划标识（客户端提供、服务端记录）。</summary>
    public string PlanId { get; set; } = "";

    /// <summary>阶段字符串：Collecting/Waiting/Pulling/WaitingArrival/Released/Stopped。</summary>
    public string Phase { get; set; } = "";

    /// <summary>已完成路线索引（本锚点描述的边界）。</summary>
    public int CompletedRouteIndex { get; set; }

    /// <summary>即将被授权的路线索引。</summary>
    public int NextRouteIndex { get; set; }

    /// <summary>冻结的参与者 UID。</summary>
    public List<string> Participants { get; set; } = [];

    /// <summary>UID → 成员状态字符串。</summary>
    public Dictionary<string, string> MemberStates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → Pull 命令 ID。</summary>
    public Dictionary<string, string> PullCommandIds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → Pull 已下发次数。</summary>
    public Dictionary<string, int> PullAttempts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>调用方自己的成员状态字符串。</summary>
    public string MyState { get; set; } = "";

    /// <summary>调用方自己的 Pull 命令 ID（需拉取时给出，否则空串）。</summary>
    public string MyPullCommandId { get; set; } = "";

    /// <summary>是否已放行（授权进入下一条路线）。</summary>
    public bool Released { get; set; }

    /// <summary>是否已停止整队。</summary>
    public bool Stopped { get; set; }

    /// <summary>会话绝对截止时间（UTC）。</summary>
    public DateTime AbsoluteDeadlineUtc { get; set; }

    /// <summary>状态版本号。</summary>
    public int Revision { get; set; }

    /// <summary>是否已经终结（放行或停止）。</summary>
    public bool IsTerminal => Released || Stopped;

    public override string ToString()
        => $"RouteAnchor[Id={AnchorId}, Phase={Phase}, Boundary={CompletedRouteIndex}→{NextRouteIndex}, Released={Released}, Stopped={Stopped}]";
}
