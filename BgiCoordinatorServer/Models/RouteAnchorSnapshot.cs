namespace BgiCoordinatorServer.Models;

/// <summary>
/// 路线锚点的权威快照（route-anchor）。
///
/// 设计纪律（正文第 7 章）：状态查询与所有变更响应返回**同一个**权威快照，
/// 事件只负责"尽快查询"，不作为唯一正确性来源。因此所有新旧状态字段都在这里一次给全。
/// 枚举统一用字符串输出（Phase/MemberStates），避免客户端依赖枚举序号。
/// </summary>
public sealed class RouteAnchorSnapshot
{
    /// <summary>是否存在活动锚点（false 时其余字段为默认值）。</summary>
    public bool HasAnchor { get; set; }

    /// <summary>锚点 ID（服务端生成）。</summary>
    public string AnchorId { get; set; } = "";

    /// <summary>会话标识（房间实例级）。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>世界代际。</summary>
    public int WorldEpoch { get; set; }

    /// <summary>冻结的路线计划标识。</summary>
    public string PlanId { get; set; } = "";

    /// <summary>阶段（字符串：Collecting/Waiting/Pulling/WaitingArrival/Released/Stopped）。</summary>
    public string Phase { get; set; } = "";

    /// <summary>已完成路线索引（本锚点描述的边界）。</summary>
    public int CompletedRouteIndex { get; set; }

    /// <summary>即将被授权的路线索引。</summary>
    public int NextRouteIndex { get; set; }

    /// <summary>冻结的参与者 UID。</summary>
    public List<string> Participants { get; set; } = [];

    /// <summary>UID → 成员状态（字符串）。</summary>
    public Dictionary<string, string> MemberStates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → 已下发的 Pull 命令 ID（未下发则不含该键）。</summary>
    public Dictionary<string, string> PullCommandIds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → 已下发 Pull 次数。</summary>
    public Dictionary<string, int> PullAttempts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>调用方自己的状态（字符串；调用方不是参与者时为空串）。</summary>
    public string MyState { get; set; } = "";

    /// <summary>调用方自己的 Pull 命令 ID（需拉取时给出；否则空串）。</summary>
    public string MyPullCommandId { get; set; } = "";

    /// <summary>是否已放行（等价于 Phase == Released）。</summary>
    public bool Released { get; set; }

    /// <summary>是否已停止整队（等价于 Phase == Stopped）。</summary>
    public bool Stopped { get; set; }

    /// <summary>会话绝对截止时间（UTC）。到点必须 Release 或 Stop，不允许无限等待。</summary>
    public DateTime AbsoluteDeadlineUtc { get; set; }

    /// <summary>状态版本号（每次状态变更递增，客户端据此判断快照新旧）。</summary>
    public int Revision { get; set; }
}
