namespace BgiCoordinatorServer.Models;

/// <summary>
/// 集体跳段阶段（collective-skip-applied-ack）。
///
/// 语义：一次集体跳段 = 一个 SkipId。服务端广播请求后进入 Requested，
/// 等全部必要成员回报 Applied（客户端确认已按目标跳段）后才进入 Applied 并广播
/// CollectiveSkipAppliedAll。整个过程中同一房间至多存在一个活动跳段。
/// </summary>
public enum CollectiveSkipPhase
{
    /// <summary>已广播跳段请求，等待必要成员回报 Applied。</summary>
    Requested = 0,

    /// <summary>全部必要成员已回报 Applied，已广播 CollectiveSkipAppliedAll（状态随即被清除）。</summary>
    Applied = 1,
}

/// <summary>
/// 一次集体跳段的运行时状态（collective-skip-applied-ack）。
///
/// 背景：原实现只广播 targetProgress（一次性瞬时事件），服务端无法知道客户端是否收到、
/// 是否真正执行，导致部分成员照旧走旧路线 → 各打各的。本状态把"跳段"从一次广播
/// 升级为"请求 → 客户端回报 Applied → 服务端确认"的闭环（仍然复用既有同步点完成最终汇合，
/// 不新增第二套集合点判定）。
///
/// 仅服务端运行时状态：不进入 RoomConfig、不进 RoomSummary、不跨轮次保留
/// （ResetForNewWorldRound 清除）。
/// </summary>
public sealed class CollectiveSkipState
{
    /// <summary>本次跳段的唯一标识（房间码 + 世代 + GUID）。客户端按此幂等去重。</summary>
    public string SkipId { get; set; } = "";

    /// <summary>目标进度（下一条路线起点，编码 路线×1e6 + 段×1e3 + 路点）。</summary>
    public long TargetProgress { get; set; }

    /// <summary>必须回报 Applied 的连接集合（创建时被判定为落后的在线玩家）。</summary>
    public HashSet<string> RequiredConnectionIds { get; set; } = [];

    /// <summary>已回报 Applied 成功的连接集合。</summary>
    public HashSet<string> AppliedConnectionIds { get; set; } = [];

    /// <summary>已回报 Applied 失败的连接集合（失败者从 Required 移出，不阻塞放行）。</summary>
    public HashSet<string> FailedConnectionIds { get; set; } = [];

    /// <summary>创建时刻（UTC），用于诊断与时序判定。</summary>
    public DateTime CreatedAt { get; set; }

    /// <summary>当前阶段。</summary>
    public CollectiveSkipPhase Phase { get; set; } = CollectiveSkipPhase.Requested;

    /// <summary>已广播次数（首次 =1；重播上限见 RoomOperations.MaxCollectiveSkipBroadcastCount）。</summary>
    public int BroadcastCount { get; set; }

    /// <summary>最近一次失败原因（诊断用，可空）。</summary>
    public string LatestFailureReason { get; set; } = "";
}
