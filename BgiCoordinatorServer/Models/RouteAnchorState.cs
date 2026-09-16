namespace BgiCoordinatorServer.Models;

/// <summary>
/// 路线边界锚点阶段（route-anchor）。
///
/// 与普通同步点、旧集体跳段的阶段完全独立：本状态机只负责"上一条路线是否已经全员收口，
/// 能否授权进入下一条路线"。普通 AllArrived / 集体跳段不得改写本状态的推进。
/// </summary>
public enum RouteAnchorPhase
{
    /// <summary>已创建锚点，等待成员提交边界状态（正常完成或被协调处理完）。</summary>
    Collecting = 0,

    /// <summary>部分成员未提交，仍在对应状态的宽限预算内等待。</summary>
    Waiting = 1,

    /// <summary>已向超预算成员下发恢复（Pull）命令，等待其确认已退出旧执行器。</summary>
    Pulling = 2,

    /// <summary>所有被拉成员已确认退出，等待全员进入边界并提交 Ready。</summary>
    WaitingArrival = 3,

    /// <summary>全员 Ready（或按策略允许缺员），已授权进入下一路线。</summary>
    Released = 4,

    /// <summary>无法确认全员安全收口：停止整队，不再授权进入下一路线。</summary>
    Stopped = 5,
}

/// <summary>
/// 单个成员在锚点中的状态（route-anchor）。
///
/// 注意：这是**锚点自己的**成员状态，不修改既有 <see cref="PlayerStatus"/> 与
/// <c>PlayerInfo.IsAbnormal</c>／<c>TargetProgress</c>（正文 9.2 要求：不得为了锚点
/// 全局重定义既有状态或覆盖复苏目标）。
/// </summary>
public enum RouteAnchorMemberState
{
    /// <summary>尚无任何信息。</summary>
    Unknown = 0,

    /// <summary>正常推进中（最近有有效活动）。</summary>
    Pathing = 1,

    /// <summary>正在战斗（由战斗状态观察写入，不能仅凭心跳推断）。</summary>
    Fighting = 2,

    /// <summary>复苏/恢复中。</summary>
    Reviving = 3,

    /// <summary>重连/重新加入中。</summary>
    Rejoining = 4,

    /// <summary>传送中。</summary>
    Teleporting = 5,

    /// <summary>心跳过期/已判离线。</summary>
    Offline = 6,

    /// <summary>已提交本路线边界状态（正常完成/跳过/恢复完成/重跑完成）。</summary>
    Reported = 7,

    /// <summary>已下发 Pull，等待客户端确认旧执行器已退出。</summary>
    PullRequested = 8,

    /// <summary>客户端已确认旧执行器退出并准备好目标游标。</summary>
    PullApplied = 9,

    /// <summary>客户端明确回报 Pull 失败。</summary>
    PullFailed = 10,

    /// <summary>已被移出本锚点（仅在显式允许缺员的策略下参与放行）。</summary>
    Removed = 11,

    /// <summary>已到达共同逻辑边界并提交 Ready（可以开始下一条路线）。</summary>
    Ready = 12,
}

/// <summary>缺席成员的分类结论（route-anchor）。</summary>
public enum RouteAnchorMissingDecision
{
    /// <summary>还在各自状态的宽限预算内，继续等待。</summary>
    Wait = 0,

    /// <summary>超过预算且心跳仍新鲜：下发恢复命令，把它拉回当前边界。</summary>
    Pull = 1,

    /// <summary>无法恢复（心跳过期、Pull 失败、重试耗尽、绝对截止到点）：停止整队。</summary>
    Stop = 2,
}

/// <summary>锚点放行裁决（route-anchor）。</summary>
public enum RouteAnchorReleaseDecision
{
    /// <summary>尚未满足放行条件，继续等待。</summary>
    Wait = 0,

    /// <summary>全员 Ready（或按策略允许缺员且其余全部就绪），授权进入下一路线。</summary>
    Release = 1,

    /// <summary>存在不可恢复成员：停止整队。</summary>
    Stop = 2,
}

/// <summary>成员提交边界的结果类型（route-anchor）。</summary>
public enum RouteAnchorOutcome
{
    Completed = 0,
    Skipped = 1,
    Recovered = 2,
    RerunCompleted = 3,
}

/// <summary>
/// 一次路线边界锚点的运行时状态（route-anchor）。
///
/// 身份字段说明（正文第 3 章）：
///   SessionId  —— 区分房间实例，不能只用可复用的房间码；
///   WorldEpoch —— 世界代际，多世界轮换后旧回调无权改动新会话；
///   PlanId     —— 冻结的共同路线计划标识（协作重跑/变体差异时必须来自该计划）；
///   Sequence   —— 服务端单调编号，保证同一会话内锚点不重复；
///   参与者按 **PlayerUid** 冻结，重连只更新连接绑定，不能让新旧连接算两人。
/// </summary>
public sealed class RouteAnchorState
{
    /// <summary>服务端生成的锚点 ID（见 RouteAnchorDecisions.BuildAnchorId）。</summary>
    public string AnchorId { get; set; } = "";

    /// <summary>会话标识（房间实例级，非房间码）。</summary>
    public string SessionId { get; set; } = "";

    /// <summary>世界代际。</summary>
    public int WorldEpoch { get; set; }

    /// <summary>冻结的路线计划标识。</summary>
    public string PlanId { get; set; } = "";

    /// <summary>锚点单调编号（同一会话内递增）。</summary>
    public int Sequence { get; set; }

    /// <summary>已完成的路线索引（本锚点描述的边界）。</summary>
    public int CompletedRouteIndex { get; set; }

    /// <summary>即将被授权的路线索引。</summary>
    public int NextRouteIndex { get; set; }

    /// <summary>冻结的参与者 UID 集合（不被心跳人数变化缩小）。</summary>
    public HashSet<string> Participants { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → 锚点成员状态。</summary>
    public Dictionary<string, RouteAnchorMemberState> MemberStates { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → 最近一次心跳时间（仅用于判断连接是否存活）。</summary>
    public Dictionary<string, DateTime> LastHeartbeatUtc { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → 最近一次"有效活动"时间（任务确实在推进，与心跳分离）。</summary>
    public Dictionary<string, DateTime> LastActivityUtc { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → 最近一次路线进度上报时间。</summary>
    public Dictionary<string, DateTime> LastRouteProgressUtc { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → 当前 Pull 命令 ID（重发保持同一 ID，客户端据此去重）。</summary>
    public Dictionary<string, string> PullCommandIds { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → 已下发的 Pull 次数。</summary>
    public Dictionary<string, int> PullAttempts { get; set; } = new(StringComparer.Ordinal);

    /// <summary>UID → 提交边界时使用的结果类型。</summary>
    public Dictionary<string, RouteAnchorOutcome> Outcomes { get; set; } = new(StringComparer.Ordinal);

    /// <summary>当前阶段。</summary>
    public RouteAnchorPhase Phase { get; set; } = RouteAnchorPhase.Collecting;

    /// <summary>创建时间（UTC）。</summary>
    public DateTime CreatedAtUtc { get; set; }

    /// <summary>会话级绝对截止时间（UTC）：到点必须 Release 或 Stop，不允许无限等待。</summary>
    public DateTime AbsoluteDeadlineUtc { get; set; }

    /// <summary>最近一次状态变化时间（UTC），用于重复心跳不得无限刷新宽限。</summary>
    public DateTime StateChangedAtUtc { get; set; }

    /// <summary>最近一次失败原因（Pull 失败等），诊断用；空串 = 无。</summary>
    public string LatestFailureReason { get; set; } = "";
}
