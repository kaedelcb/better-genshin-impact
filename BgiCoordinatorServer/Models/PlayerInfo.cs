namespace BgiCoordinatorServer.Models;

public class PlayerInfo
{
    public string ConnectionId { get; set; } = "";
    public string PlayerId { get; set; } = "";
    public string PlayerName { get; set; } = "";
    public string PlayerUid { get; set; } = "";
    internal bool NegotiatesStandby { get; init; }
    internal bool StandbyOnly { get; init; }

    /// <summary>该玩家上报的完整 Reported_Version 字符串（version-compatibility-check）。
    /// 空串=旧客户端未上报。仅用于 Check_Result 展示与房间整体判定。</summary>
    public string ReportedVersion { get; set; } = "";
    public PlayerStatus Status { get; set; } = PlayerStatus.Waiting;
    public DateTime LastHeartbeat { get; set; }

    // === 路线进度信息（需求 6）===
    /// <summary>当前路线索引（0-based），-1 表示未上报</summary>
    public int CurrentRouteIndex { get; set; } = -1;
    /// <summary>当前路线开始时间（UTC）</summary>
    public DateTime RouteStartTime { get; set; }
    /// <summary>当前路线预估总时间（秒）</summary>
    public double RouteEstimatedSeconds { get; set; }

    // === 异常玩家状态字段（multiplayer-abnormal-wait-coordination spec）===
    // Validates: Requirements 1.1, 1.4

    /// <summary>
    /// 是否为异常玩家
    /// 当玩家在执行线路过程中遇到错误状态，触发线路跳过时设置为 true
    /// </summary>
    public bool IsAbnormal { get; set; }

    /// <summary>
    /// 当前等待点ID（异常玩家专用）
    /// 异常玩家上报的等待点，必须是 _tp_ 格式的传送点
    /// </summary>
    public string? WaitPointId { get; set; }

    /// <summary>
    /// 异常玩家的目标进度值（异常恢复后会到达的同步点进度）
    /// 用于判定其他玩家在某同步点 X 是否需要等他：
    ///   TargetProgress ≤ X.Progress → 异常玩家会到 X 或之前 → 等他
    ///   TargetProgress > X.Progress → 异常玩家跳过了 X → 不等他
    /// 正常玩家时为 -1
    /// </summary>
    public long TargetProgress { get; set; } = -1;

    /// <summary>
    /// 玩家当前到达的同步点的进度值
    /// 用于服务端判定时知道每个玩家所处的位置
    /// </summary>
    public long CurrentProgress { get; set; } = -1;
}
