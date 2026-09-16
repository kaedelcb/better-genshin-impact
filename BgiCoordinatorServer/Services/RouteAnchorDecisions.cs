using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 路线边界锚点的纯决策函数集合（route-anchor）。
///
/// 与仓库既有模式一致（参见 LaggingMemberReleaseDecisions / RoomPhaseDecisions）：
/// 无 logger、无 room、无 SignalR 依赖，输入输出纯粹，便于单元测试直接撒输入验证性质。
///
/// 本文件只做"判定"，不做"执行"：不创建锚点、不发广播、不改房间状态。
/// 阶段 1 不接入任何生产调用路径（正文第 12 节阶段 1 要求）。
/// </summary>
public static class RouteAnchorDecisions
{
    /// <summary>锚点等待预算（route-anchor）。</summary>
    public readonly record struct Budgets(
        TimeSpan HeartbeatFreshness,
        TimeSpan PathingGrace,
        TimeSpan FightingGrace,
        TimeSpan RecoveryGrace,
        TimeSpan TeleportGrace,
        TimeSpan AbsoluteTimeout)
    {
        /// <summary>
        /// 保守默认值：均为**初始建议值**，实机验证后按正文第 8 节调整；
        /// 战斗预算必须与房主实际 FightTimeoutSeconds 兼容（此处取不短于常见战斗的取值）。
        /// </summary>
        public static Budgets Default => new(
            HeartbeatFreshness: TimeSpan.FromSeconds(30),
            PathingGrace: TimeSpan.FromSeconds(90),
            FightingGrace: TimeSpan.FromSeconds(120),
            RecoveryGrace: TimeSpan.FromSeconds(180),
            TeleportGrace: TimeSpan.FromSeconds(60),
            AbsoluteTimeout: TimeSpan.FromMinutes(10));
    }

    /// <summary>
    /// 构造锚点 ID。必须包含会话、世界代际、计划与已完成路线索引，避免重跑/多世界/同名路线互相污染。
    /// </summary>
    public static string BuildAnchorId(string sessionId, int worldEpoch, string planId, int completedRouteIndex, int sequence)
        => $"route-boundary:{sessionId}:{worldEpoch}:{planId}:{completedRouteIndex}:{sequence}";

    /// <summary>收到的消息是否属于当前活动锚点。空 ID（旧消息/无锚点）一律不算同一锚点。</summary>
    public static bool IsSameAnchor(string? receivedAnchorId, string? currentAnchorId)
        => !string.IsNullOrEmpty(receivedAnchorId)
           && !string.IsNullOrEmpty(currentAnchorId)
           && string.Equals(receivedAnchorId, currentAnchorId, StringComparison.Ordinal);

    /// <summary>代际（会话/世界/计划）是否已过期。过期消息应被拒绝，但不因此停止当前正常会话。</summary>
    public static bool IsStaleGeneration(
        string? receivedSessionId, int receivedWorldEpoch, string? receivedPlanId,
        string? currentSessionId, int currentWorldEpoch, string? currentPlanId)
        => !string.Equals(receivedSessionId, currentSessionId, StringComparison.Ordinal)
           || receivedWorldEpoch != currentWorldEpoch
           || !string.Equals(receivedPlanId, currentPlanId, StringComparison.Ordinal);

    /// <summary>边界报告被拒的原因（供日志与测试断言）。</summary>
    public enum ReportRejection
    {
        None = 0,
        NotSameAnchor = 1,
        StaleGeneration = 2,
        NotParticipant = 3,
        Duplicate = 4,
        WrongRouteIndex = 5,
        AlreadyTerminal = 6,
    }

    /// <summary>
    /// 是否接受成员提交的路线边界报告。
    /// 关键约束：路线索引必须等于本锚点描述的"已完成路线"，不允许由客户端自报推进服务端状态。
    /// </summary>
    public static ReportRejection ValidateReport(
        string? receivedAnchorId, string? currentAnchorId,
        string? receivedSessionId, int receivedWorldEpoch, string? receivedPlanId,
        string? currentSessionId, int currentWorldEpoch, string? currentPlanId,
        int reportedCompletedRouteIndex, int expectedCompletedRouteIndex,
        string playerUid, IReadOnlyCollection<string> participants,
        bool alreadyReported, RouteAnchorPhase phase)
    {
        if (phase is RouteAnchorPhase.Released or RouteAnchorPhase.Stopped)
            return ReportRejection.AlreadyTerminal;
        if (!IsSameAnchor(receivedAnchorId, currentAnchorId))
            return ReportRejection.NotSameAnchor;
        if (IsStaleGeneration(receivedSessionId, receivedWorldEpoch, receivedPlanId,
                currentSessionId, currentWorldEpoch, currentPlanId))
            return ReportRejection.StaleGeneration;
        if (!participants.Contains(playerUid))
            return ReportRejection.NotParticipant;
        if (alreadyReported)
            return ReportRejection.Duplicate;
        if (reportedCompletedRouteIndex != expectedCompletedRouteIndex)
            return ReportRejection.WrongRouteIndex;
        return ReportRejection.None;
    }

    /// <summary>该成员状态对应的宽限预算。</summary>
    public static TimeSpan ResolveGrace(RouteAnchorMemberState state, Budgets budgets)
        => state switch
        {
            RouteAnchorMemberState.Fighting => budgets.FightingGrace,
            RouteAnchorMemberState.Reviving => budgets.RecoveryGrace,
            RouteAnchorMemberState.Rejoining => budgets.RecoveryGrace,
            RouteAnchorMemberState.Teleporting => budgets.TeleportGrace,
            _ => budgets.PathingGrace,
        };

    /// <summary>
    /// 对单个未提交成员做"等待 / 拉取 / 停止"分类。
    ///
    /// 关键纪律：
    ///   1) 心跳只证明连接存活，**不**延长宽限；宽限只由"有效活动时间"推进，
    ///      避免重复心跳无限刷新（正文第 8 节）。
    ///   2) 战斗/复苏各自使用自己的预算，不共用一个 30 秒阈值。
    ///   3) 心跳过期、Pull 失败、已移除成员一律不参与放行。
    /// </summary>
    public static RouteAnchorMissingDecision ClassifyMissing(
        DateTime nowUtc,
        DateTime lastHeartbeatUtc,
        DateTime lastActivityUtc,
        RouteAnchorMemberState state,
        Budgets budgets)
    {
        if (state is RouteAnchorMemberState.Offline or RouteAnchorMemberState.PullFailed)
            return RouteAnchorMissingDecision.Stop;

        // 心跳不新鲜：无法与本机通信，视为不可恢复。
        if (nowUtc - lastHeartbeatUtc > budgets.HeartbeatFreshness)
            return RouteAnchorMissingDecision.Stop;

        // Pull 已在途：等待其确认退出，不重复下发。
        if (state is RouteAnchorMemberState.PullRequested or RouteAnchorMemberState.PullApplied)
            return RouteAnchorMissingDecision.Wait;

        // 已上报/就绪/已移除：不属于"缺席待处理"，由放行裁决统一处理。
        if (state is RouteAnchorMemberState.Reported or RouteAnchorMemberState.Ready or RouteAnchorMemberState.Removed)
            return RouteAnchorMissingDecision.Wait;

        var grace = ResolveGrace(state, budgets);

        // 有效活动不新鲜（与心跳无关）→ 允许拉取。
        // lastActivityUtc 为默认值(0001-01-01)时视为从未有活动：直接进入拉取。
        if (lastActivityUtc == default || nowUtc - lastActivityUtc > grace)
            return RouteAnchorMissingDecision.Pull;

        return RouteAnchorMissingDecision.Wait;
    }

    /// <summary>
    /// 锚点放行裁决。
    ///
    /// 规则：
    ///   - 存在 Pull 失败成员 → Stop（强兜底不允许把失败当成缺员继续）。
    ///   - 参与者全部 Ready → Release。
    ///   - 绝对截止时间已到且未全员 Ready → Stop（不允许无限等待）。
    ///   - <paramref name="allowMissingPolicy"/> 为 true 时，Removed 成员可被排除后放行；
    ///     该策略**不是**"保持全员统一"，必须在配置层显式提示（正文 10.1）。
    /// </summary>
    public static RouteAnchorReleaseDecision DecideRelease(
        RouteAnchorPhase phase,
        IReadOnlyCollection<string> participants,
        IReadOnlyDictionary<string, RouteAnchorMemberState> memberStates,
        bool allowMissingPolicy,
        DateTime nowUtc,
        DateTime absoluteDeadlineUtc)
    {
        if (phase is RouteAnchorPhase.Released or RouteAnchorPhase.Stopped)
            return phase == RouteAnchorPhase.Released
                ? RouteAnchorReleaseDecision.Release
                : RouteAnchorReleaseDecision.Stop;

        if (participants.Count == 0)
            return RouteAnchorReleaseDecision.Stop;

        bool anyPullFailed = participants.Any(uid =>
            memberStates.TryGetValue(uid, out var s) && s == RouteAnchorMemberState.PullFailed);
        if (anyPullFailed)
            return RouteAnchorReleaseDecision.Stop;

        bool allReady = participants.All(uid =>
            memberStates.TryGetValue(uid, out var s) && s == RouteAnchorMemberState.Ready);
        if (allReady)
            return RouteAnchorReleaseDecision.Release;

        if (allowMissingPolicy)
        {
            bool anyRemoved = participants.Any(uid =>
                memberStates.TryGetValue(uid, out var s) && s == RouteAnchorMemberState.Removed);
            if (anyRemoved)
            {
                bool allOthersReady = participants
                    .Where(uid => !(memberStates.TryGetValue(uid, out var s) && s == RouteAnchorMemberState.Removed))
                    .All(uid => memberStates.TryGetValue(uid, out var s) && s == RouteAnchorMemberState.Ready);
                if (allOthersReady)
                    return RouteAnchorReleaseDecision.Release;
            }
        }

        if (nowUtc >= absoluteDeadlineUtc)
            return RouteAnchorReleaseDecision.Stop;

        return RouteAnchorReleaseDecision.Wait;
    }

    /// <summary>
    /// 由当前成员状态推导锚点阶段（纯函数，不修改输入）。
    /// Collecting 与 Waiting 的区分：是否已有任意成员提交边界。
    /// </summary>
    public static RouteAnchorPhase DerivePhase(
        IReadOnlyCollection<string> participants,
        IReadOnlyDictionary<string, RouteAnchorMemberState> memberStates,
        RouteAnchorReleaseDecision releaseDecision)
    {
        if (releaseDecision == RouteAnchorReleaseDecision.Release)
            return RouteAnchorPhase.Released;
        if (releaseDecision == RouteAnchorReleaseDecision.Stop)
            return RouteAnchorPhase.Stopped;

        if (participants.Count == 0)
            return RouteAnchorPhase.Collecting;

        bool anyReported = participants.Any(uid =>
            memberStates.TryGetValue(uid, out var s) && s is RouteAnchorMemberState.Reported
                or RouteAnchorMemberState.Ready or RouteAnchorMemberState.PullApplied);
        if (!anyReported)
            return RouteAnchorPhase.Collecting;

        bool anyPullInFlight = participants.Any(uid =>
            memberStates.TryGetValue(uid, out var s) && s is RouteAnchorMemberState.PullRequested
                or RouteAnchorMemberState.PullApplied);
        if (anyPullInFlight)
            return RouteAnchorPhase.Pulling;

        bool allReportedButNotReady = participants.All(uid =>
            memberStates.TryGetValue(uid, out var s) && s is RouteAnchorMemberState.Reported
                or RouteAnchorMemberState.PullApplied or RouteAnchorMemberState.Ready);
        if (allReportedButNotReady)
            return RouteAnchorPhase.WaitingArrival;

        return RouteAnchorPhase.Waiting;
    }

    /// <summary>
    /// 是否接受 PullApplied 回报。
    /// 必须严格匹配命令：同一 commandId 重复回报幂等接受（可重复补发）；
    /// 不匹配的 commandId 一律忽略（旧命令/他人命令不得推进状态）。
    /// </summary>
    public static bool ShouldAcceptPullApplied(string? reportedCommandId, string? expectedCommandId)
        => !string.IsNullOrEmpty(reportedCommandId)
           && !string.IsNullOrEmpty(expectedCommandId)
           && string.Equals(reportedCommandId, expectedCommandId, StringComparison.Ordinal);

    /// <summary>
    /// 是否可以对该成员下发 Pull。
    /// 条件：不在途、未达重试上限、心跳新鲜、状态可拉（非 Offline/PullFailed/已 Ready）。
    /// </summary>
    public static bool CanIssuePull(
        RouteAnchorMemberState state,
        int attemptsSoFar,
        int maxAttempts,
        DateTime nowUtc,
        DateTime lastHeartbeatUtc,
        Budgets budgets)
    {
        if (attemptsSoFar >= maxAttempts) return false;
        if (nowUtc - lastHeartbeatUtc > budgets.HeartbeatFreshness) return false;
        return state is RouteAnchorMemberState.Unknown
            or RouteAnchorMemberState.Pathing
            or RouteAnchorMemberState.Fighting
            or RouteAnchorMemberState.Reviving
            or RouteAnchorMemberState.Rejoining
            or RouteAnchorMemberState.Teleporting;
    }

    /// <summary>
    /// 构造 Pull 命令 ID。同一成员的同一锚点同一尝试序号 → 同一 ID（重发必须复用，客户端据此去重）。
    /// </summary>
    public static string BuildPullCommandId(string anchorId, string playerUid, int attempt)
        => $"anchor-pull:{anchorId}:{playerUid}:{attempt}";
}
