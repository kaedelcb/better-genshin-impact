#nullable enable

namespace BetterGenshinImpact.Shared.RouteAnchor;

// 路线边界锚点的唯一协议来源（客户端与服务器共用同一份文件，客户端通过 csproj Link 编译它）。
//
// 放置位置的两条硬约束与 RerunProtocol 完全相同（都踩过）：
// 1) 必须位于 BgiCoordinatorServer 工程目录内（服务端 Docker 构建上下文只有该目录）；
// 2) 必须放在**已有的**子目录里（Gateway/），不要为它新建文件夹。
//
// 约束：本文件只能依赖 BCL，不得引用游戏、UI、传输或服务端类型（两端都要能编译它）。

/// <summary>
/// 路线边界锚点（route-anchor）共享协议常量。
///
/// 用途：能力协商 + 消息名 + 事件名的单一来源，避免两端各写一份导致漂移。
/// 能力语义：只有**服务器与全部参与客户端**都宣告该能力时才允许激活锚点（禁止半启用）。
/// </summary>
public static class RouteAnchorProtocol
{
    /// <summary>能力标识。服务器放进 hello 响应的 capabilities；客户端放进 hello 请求的 capabilities。</summary>
    public const string Capability = "hoeing.routeAnchor.v1";

    // 客户端 → 服务端
    public const string Enroll = "sync.routeAnchorEnroll";
    public const string Report = "sync.routeAnchorReport";
    public const string Arrived = "sync.routeAnchorArrived";
    public const string Cancel = "sync.routeAnchorCancel";

    /// <summary>服务端 → 客户端：把超预算成员拉回当前边界（定向发送，载荷带 commandId）。</summary>
    public const string Pull = "sync.routeAnchorPull";

    /// <summary>客户端 → 服务端：Pull 执行确认（成败同一条命令，用 success 区分）。</summary>
    public const string PullAck = "sync.routeAnchorPullApplied";

    // 查询（权威事实来源）
    public const string State = "sync.routeAnchorState";

    // 服务端 → 客户端事件（只作"尽快查询"的提示，不承载最终正确性）
    public const string Changed = "sync.routeAnchorChanged";
    public const string Released = "sync.routeAnchorReleased";
    public const string Stopped = "sync.routeAnchorStopped";

    /// <summary>定向 Pull 命令事件名（与 Pull 命令同值，客户端按 evt 名识别）。</summary>
    public const string PullEvent = "sync.routeAnchorPull";

    /// <summary>
    /// 轮末边界哨兵：最后一条路线完成时提交的 "nextRouteIndex"。
    /// 语义 = 本轮结束（Finished），仍需全员收口，不允许提前关房或进入重跑。
    /// </summary>
    public const int RoundEndNextRouteIndex = -1;

    // 边界提交结果类型
    public const string OutcomeCompleted = "completed";
    public const string OutcomeSkipped = "skipped";
    public const string OutcomeRecovered = "recovered";
    public const string OutcomeRerunCompleted = "rerun-completed";

    /// <summary>锚点路径上的成员状态字符串（与枚举名一致，避免客户端依赖枚举序号）。</summary>
    public const string MemberStateReady = "Ready";
    public const string MemberStateReported = "Reported";
    public const string MemberStatePathing = "Pathing";
    public const string MemberStateFighting = "Fighting";
    public const string MemberStateReviving = "Reviving";
    public const string MemberStateOffline = "Offline";
    public const string MemberStatePullRequested = "PullRequested";
    public const string MemberStatePullApplied = "PullApplied";
    public const string MemberStatePullFailed = "PullFailed";
    public const string MemberStateRemoved = "Removed";

    /// <summary>阶段字符串。</summary>
    public const string PhaseCollecting = "Collecting";
    public const string PhaseWaiting = "Waiting";
    public const string PhasePulling = "Pulling";
    public const string PhaseWaitingArrival = "WaitingArrival";
    public const string PhaseReleased = "Released";
    public const string PhaseStopped = "Stopped";
}
