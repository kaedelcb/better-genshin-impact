using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;

namespace BgiCoordinatorServer.Services;

/// <summary>
/// 路线边界锚点族（route-anchor）· 阶段 2 服务端部分。
///
/// 职责：维护"上一条路线是否全员收口"的权威状态，并在全员就绪时授权进入下一条路线。
///
/// 边界与纪律：
///   1. 只服务新协议（sync.routeAnchor*）。旧客户端从不发送这些命令 → 现网行为零变化。
///   2. 不修改既有 ArrivalSets / AllArrived / 进度豁免 / IsAbnormal / TargetProgress。
///   3. 状态变更一律在 lock(room) 内决策，广播一律在锁外 await（与既有纪律一致）。
///   4. 终态（Released/Stopped）保留在房间里供查询，直到下一个边界到来才被替换。
///   5. 参与者按 PlayerUid 冻结；重连只更新心跳，不因连接变化缩小分母。
/// </summary>
public sealed partial class RoomOperations
{
    /// <summary>参与者冻结规则：仅当锚点仍在 Collecting（尚无任何成员提交边界）时允许补入新成员。</summary>
    private static bool CanJoinParticipantsLocked(RouteAnchorState anchor)
        => anchor.Phase == RouteAnchorPhase.Collecting;

    /// <summary>
    /// 能力门控：当前**在线**成员是否全部宣告了路线锚点能力。
    /// 规则（方案正文第 10 章）：服务器与全部参与客户端都支持才允许激活，禁止半启用。
    /// 未接线（<see cref="_capabilityLookup"/> 为 null）、未握手或旧客户端一律判为不支持。
    /// 只检查在线成员：离线成员不参与本边界，其缺失不阻塞收口。
    /// </summary>
    private bool AllOnlineMembersSupportRouteAnchorLocked(Room room, DateTime nowUtc)
        => _capabilityLookup != null
           && room.Players
                .Where(p => IsAnchorOnline(p, nowUtc))
                .All(p => _capabilityLookup.Supports(p.ConnectionId, BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Capability));

    /// <summary>
    /// 把"成员有效活动 / 战斗状态 / 复苏状态"观察写入**锚点自己的**状态（route-anchor 阶段 5）。
    ///
    /// 纪律（方案 §9.2）：不修改既有 <c>PlayerStatus</c> / <c>IsAbnormal</c> / <c>TargetProgress</c>，
    /// 只在锚点状态里记录；没有活动锚点时是零开销空操作。
    ///
    /// 状态写入规则：已"有结论"的成员（Reported/Ready/PullApplied/PullFailed/Removed）**不被降级**——
    /// 否则一个 Ready 的成员在随后上报"战斗中"时会被误判回未就绪，锚点永远无法放行。
    /// </summary>
    internal static void ObserveRouteAnchorActivityLocked(
        Room room, string uid, RouteAnchorMemberState? state, DateTime nowUtc)
    {
        var anchor = room.ActiveRouteAnchor;
        if (anchor == null) return;
        if (anchor.Phase is RouteAnchorPhase.Released or RouteAnchorPhase.Stopped) return;
        if (string.IsNullOrEmpty(uid) || !anchor.Participants.Contains(uid)) return;

        // 有效活动时间：任何真实上报都刷新（心跳不从这里走，故不会掩盖停滞）
        anchor.LastActivityUtc[uid] = nowUtc;

        if (state == null) return;

        var current = anchor.MemberStates.TryGetValue(uid, out var st) ? st : RouteAnchorMemberState.Unknown;
        var resolved = current is RouteAnchorMemberState.Reported or RouteAnchorMemberState.Ready
            or RouteAnchorMemberState.PullApplied or RouteAnchorMemberState.PullFailed
            or RouteAnchorMemberState.Removed or RouteAnchorMemberState.PullRequested;
        if (resolved) return;

        anchor.MemberStates[uid] = state.Value;
    }

    /// <summary>在线判定与既有逻辑保持一致（心跳 2 分钟内）。</summary>
    private static bool IsAnchorOnline(PlayerInfo p, DateTime nowUtc)
        => nowUtc - p.LastHeartbeat < TimeSpan.FromMinutes(2);

    /// <summary>构造权威快照。必须在 lock(room) 内调用（只读遍历）。</summary>
    private static RouteAnchorSnapshot BuildSnapshot(RouteAnchorState? anchor, string callerUid)
    {
        if (anchor == null) return new RouteAnchorSnapshot { HasAnchor = false };

        var snapshot = new RouteAnchorSnapshot
        {
            HasAnchor = true,
            AnchorId = anchor.AnchorId,
            SessionId = anchor.SessionId,
            WorldEpoch = anchor.WorldEpoch,
            PlanId = anchor.PlanId,
            Phase = anchor.Phase.ToString(),
            CompletedRouteIndex = anchor.CompletedRouteIndex,
            NextRouteIndex = anchor.NextRouteIndex,
            Participants = anchor.Participants.OrderBy(u => u, StringComparer.Ordinal).ToList(),
            AbsoluteDeadlineUtc = anchor.AbsoluteDeadlineUtc,
            Released = anchor.Phase == RouteAnchorPhase.Released,
            Stopped = anchor.Phase == RouteAnchorPhase.Stopped,
            Revision = anchor.Sequence * 1000 + (int)anchor.Phase,
        };

        foreach (var uid in anchor.Participants)
        {
            snapshot.MemberStates[uid] = anchor.MemberStates.TryGetValue(uid, out var s)
                ? s.ToString()
                : RouteAnchorMemberState.Unknown.ToString();
        }

        foreach (var kv in anchor.PullCommandIds) snapshot.PullCommandIds[kv.Key] = kv.Value;
        foreach (var kv in anchor.PullAttempts) snapshot.PullAttempts[kv.Key] = kv.Value;

        if (!string.IsNullOrEmpty(callerUid) && anchor.Participants.Contains(callerUid))
        {
            snapshot.MyState = snapshot.MemberStates.TryGetValue(callerUid, out var mine) ? mine : "";
            snapshot.MyPullCommandId = anchor.PullCommandIds.TryGetValue(callerUid, out var cmd) ? cmd : "";
        }

        return snapshot;
    }

    /// <summary>取调用方在房间内的 PlayerUid；不在房间返回 null。</summary>
    private static string? ResolveCallerUid(Room room, GatewayHandlerContext ctx)
    {
        var uid = room.Players.FirstOrDefault(p => p.ConnectionId == ctx.ConnectionId)?.PlayerUid;
        return string.IsNullOrEmpty(uid) ? null : uid;
    }

    /// <summary>锚点身份是否与请求一致（会话/世界/计划/边界索引）。</summary>
    private static bool IsSameAnchorIdentity(
        RouteAnchorState anchor, string sessionId, int worldEpoch, string planId, int completedRouteIndex)
        => string.Equals(anchor.SessionId, sessionId, StringComparison.Ordinal)
           && anchor.WorldEpoch == worldEpoch
           && string.Equals(anchor.PlanId, planId, StringComparison.Ordinal)
           && anchor.CompletedRouteIndex == completedRouteIndex;

    /// <summary>
    /// 加入/创建路线边界锚点（sync.routeAnchorEnroll）。
    ///
    /// 语义：客户端完成（或跳过）了 route N，声明自己到达边界 N，请求进入边界收口流程。
    /// 服务端首次收到某边界 N 的 enroll 时创建锚点，参与者 = 当时在线成员 UID（冻结）。
    /// </summary>
    public Task<object> EnrollRouteAnchorAsync(
        GatewayHandlerContext ctx, string sessionId, int worldEpoch, string planId, int completedRouteIndex, int nextRouteIndex)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return Task.FromResult<object>(AnchorError("not_in_room"));

        var nowUtc = DateTime.UtcNow;
        RouteAnchorSnapshot snapshot;
        lock (room)
        {
            var uid = ResolveCallerUid(room, ctx);
            if (uid == null) return Task.FromResult<object>(AnchorError("not_in_room"));

            // 协作重跑门控（方案 §9.4：不得同时运行两套路线释放权威）：
            // 重跑 Preparing/Running/Finishing 期间由重跑负责收口，锚点不创建也不推进。
            // 客户端把该拒绝识别为"推进权归重跑"并直接放行（见 RouteAnchorWaitResult.YieldedToRerun），
            // 而不是判失败——否则落后的成员会在重跑窗口内被误停。
            if (room.RerunExecution?.BlocksLegacyAdvancement == true)
            {
                _logger.LogInformation("[RouteAnchor] 拒绝 enroll：房间 {RoomCode} 正处于协作重跑（推进权归重跑）", roomCode);
                return Task.FromResult<object>(AnchorError("rerun_in_progress"));
            }

            // 配置门控：房主未开启（或旧客户端从不发送该字段）时，锚点一律不激活 → 现网行为零变化
            if (room.HostConfig?.EnableRouteAnchor != true)
            {
                _logger.LogInformation("[RouteAnchor] 拒绝 enroll：房间 {RoomCode} 房主未开启 EnableRouteAnchor", roomCode);
                return Task.FromResult<object>(AnchorError("config_disabled"));
            }

            // 能力门控（禁止半启用）：任一在线成员未宣告能力时一律拒绝激活
            if (!AllOnlineMembersSupportRouteAnchorLocked(room, nowUtc))
            {
                _logger.LogWarning("[RouteAnchor] 拒绝 enroll：房间 {RoomCode} 存在未宣告 {Capability} 的在线成员（禁止半启用）",
                    roomCode, BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Capability);
                return Task.FromResult<object>(AnchorError("capability_required"));
            }

            // 计划标识由客户端提供（各成员必须一致，用于识别计划漂移）；
            // 会话语义由服务端权威生成，客户端提供的 sessionId/worldEpoch 仅作日志参考、不参与判定。
            if (string.IsNullOrEmpty(planId))
                return Task.FromResult<object>(AnchorError("missing_identity"));

            var serverSession = room.SessionId;
            var serverEpoch = room.CurrentWorldRound;
            if (!string.Equals(sessionId, serverSession, StringComparison.Ordinal) || worldEpoch != serverEpoch)
            {
                _logger.LogDebug(
                    "[RouteAnchor] 忽略客户端提供的会话身份（服务端权威）：客户端 session={ClientSession} epoch={ClientEpoch}，服务端 session={ServerSession} epoch={ServerEpoch}",
                    sessionId, worldEpoch, serverSession, serverEpoch);
            }

            var existing = room.ActiveRouteAnchor;

            // 1) 同一边界重复 enroll：幂等复用（含已放行之后——不得为同一界再建第二个锚点）
            if (existing != null
                && IsSameAnchorIdentity(existing, serverSession, serverEpoch, planId, completedRouteIndex))
            {
                if (!existing.Participants.Contains(uid))
                {
                    if (!CanJoinParticipantsLocked(existing))
                        return Task.FromResult<object>(AnchorError("not_participant"));
                    existing.Participants.Add(uid);
                    existing.MemberStates[uid] = RouteAnchorMemberState.Pathing;
                    _logger.LogInformation("[RouteAnchor] 成员 {Uid} 在 Collecting 阶段补入锚点 {AnchorId}", uid, existing.AnchorId);
                }

                snapshot = BuildSnapshot(existing, uid);
                return Task.FromResult<object>(new { anchor = snapshot });
            }

            // 2) 已有未终结锚点：不同边界一律拒绝（同房间只能有一个活动边界）
            if (existing != null && existing.Phase is not (RouteAnchorPhase.Released or RouteAnchorPhase.Stopped))
            {
                _logger.LogWarning(
                    "[RouteAnchor] 拒绝 enroll：房间 {RoomCode} 已有活动锚点 {AnchorId}（阶段 {Phase}），请求边界={Boundary}",
                    roomCode, existing.AnchorId, existing.Phase, completedRouteIndex);
                return Task.FromResult<object>(AnchorError("anchor_in_progress"));
            }

            // 3) 过期边界（旧世界/旧轮次客户端迟到）：不重建已放行过的边界
            if (completedRouteIndex < room.LastReleasedRouteBoundary)
            {
                _logger.LogWarning(
                    "[RouteAnchor] 拒绝 enroll：边界 {Boundary} 早于本房间已放行的最高边界 {LastReleased}（房间 {RoomCode}）",
                    completedRouteIndex, room.LastReleasedRouteBoundary, roomCode);
                return Task.FromResult<object>(AnchorError("stale_boundary"));
            }

            // 4) 新建锚点：参与者 = 当时在线成员 UID（冻结）
            room.RouteAnchorSequence += 1;
            var participants = room.Players
                .Where(p => IsAnchorOnline(p, nowUtc) && !string.IsNullOrEmpty(p.PlayerUid))
                .Select(p => p.PlayerUid)
                .Distinct(StringComparer.Ordinal)
                .ToList();

            var anchor = new RouteAnchorState
            {
                AnchorId = RouteAnchorDecisions.BuildAnchorId(serverSession, serverEpoch, planId, completedRouteIndex, room.RouteAnchorSequence),
                SessionId = serverSession,
                WorldEpoch = serverEpoch,
                PlanId = planId,
                Sequence = room.RouteAnchorSequence,
                CompletedRouteIndex = completedRouteIndex,
                NextRouteIndex = nextRouteIndex,
                Phase = RouteAnchorPhase.Collecting,
                CreatedAtUtc = nowUtc,
                StateChangedAtUtc = nowUtc,
                AbsoluteDeadlineUtc = nowUtc + RouteAnchorDecisions.Budgets.Default.AbsoluteTimeout,
            };
            foreach (var p in participants)
            {
                anchor.Participants.Add(p);
                anchor.MemberStates[p] = RouteAnchorMemberState.Pathing;
                anchor.LastHeartbeatUtc[p] = nowUtc;
                anchor.LastActivityUtc[p] = nowUtc;
            }
            room.ActiveRouteAnchor = anchor;

            _logger.LogWarning("[RouteAnchor] 创建锚点 {AnchorId}：房间={RoomCode}, 边界={Boundary}→{Next}, 参与者={Count}",
                anchor.AnchorId, roomCode, completedRouteIndex, nextRouteIndex, participants.Count);

            // 创建即武装兜底评估：真正卡死的房间不会有新消息来驱动对账
            ArmRouteAnchorEvaluateTimerLocked(room, roomCode);

            snapshot = BuildSnapshot(room.ActiveRouteAnchor, uid);
        }

        return Task.FromResult<object>(new { anchor = snapshot });
    }

    /// <summary>
    /// 提交路线边界状态（sync.routeAnchorReport）。
    /// 关键：路线索引必须等于锚点描述的边界，禁止客户端自报推进服务端状态。
    /// </summary>
    public async Task<object> ReportRouteAnchorAsync(
        GatewayHandlerContext ctx, string anchorId, string sessionId, int worldEpoch, string planId,
        int completedRouteIndex, string outcome)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return AnchorError("not_in_room");

        var nowUtc = DateTime.UtcNow;
        RouteAnchorSnapshot snapshot;
        lock (room)
        {
            var uid = ResolveCallerUid(room, ctx);
            if (uid == null) return AnchorError("not_in_room");

            var anchor = room.ActiveRouteAnchor;
            if (anchor == null) return AnchorError("no_anchor");

            var rejection = RouteAnchorDecisions.ValidateReport(
                anchorId, anchor.AnchorId,
                sessionId, worldEpoch, planId,
                anchor.SessionId, anchor.WorldEpoch, anchor.PlanId,
                completedRouteIndex, anchor.CompletedRouteIndex,
                uid, anchor.Participants,
                alreadyReported: anchor.MemberStates.TryGetValue(uid, out var st)
                                 && st is RouteAnchorMemberState.Reported or RouteAnchorMemberState.Ready,
                phase: anchor.Phase);

            if (rejection != RouteAnchorDecisions.ReportRejection.None)
            {
                _logger.LogWarning("[RouteAnchor] 拒绝 Report：房间={RoomCode}, 锚点={AnchorId}, uid={Uid}, 原因={Reason}",
                    roomCode, anchor.AnchorId, uid, rejection);
                return AnchorError(rejection.ToString());
            }

            anchor.MemberStates[uid] = RouteAnchorMemberState.Reported;
            anchor.Outcomes[uid] = ParseOutcome(outcome);
            anchor.LastActivityUtc[uid] = nowUtc;
            anchor.LastRouteProgressUtc[uid] = nowUtc;
            anchor.StateChangedAtUtc = nowUtc;
            anchor.Phase = RouteAnchorDecisions.DerivePhase(
                anchor.Participants, anchor.MemberStates, RouteAnchorReleaseDecision.Wait);

            _logger.LogInformation("[RouteAnchor] 成员提交边界：房间={RoomCode}, 锚点={AnchorId}, uid={Uid}, outcome={Outcome}, 阶段={Phase}",
                roomCode, anchor.AnchorId, uid, anchor.Outcomes[uid], anchor.Phase);

            snapshot = BuildSnapshot(anchor, uid);
        }

        // 报告可能让"最后一个待收口成员"到位 → 驱动对账（可能直接放行，也可能触发对落后者的 Pull）
        await EvaluateRouteAnchorAsync(room, roomCode);

        return new { anchor = snapshot };
    }

    /// <summary>
    /// 提交"已到达边界且可以继续"（sync.routeAnchorArrived → Ready）。
    /// 只有全部参与者 Ready 才会放行；放行结果通过权威快照与事件同时给出。
    /// </summary>
    public async Task<object> ArriveRouteAnchorAsync(
        GatewayHandlerContext ctx, string anchorId, string sessionId, int worldEpoch, string planId)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return AnchorError("not_in_room");

        var nowUtc = DateTime.UtcNow;
        RouteAnchorSnapshot snapshot;
        bool released = false;
        RouteAnchorState? releasedAnchor = null;

        lock (room)
        {
            var uid = ResolveCallerUid(room, ctx);
            if (uid == null) return AnchorError("not_in_room");

            var anchor = room.ActiveRouteAnchor;
            if (anchor == null) return AnchorError("no_anchor");

            if (anchor.Phase is RouteAnchorPhase.Released or RouteAnchorPhase.Stopped)
            {
                // 终态幂等：重复 Ready 返回当前快照，不报错（客户端重试安全）
                snapshot = BuildSnapshot(anchor, uid);
                return new { anchor = snapshot };
            }

            if (!RouteAnchorDecisions.IsSameAnchor(anchorId, anchor.AnchorId)
                || RouteAnchorDecisions.IsStaleGeneration(sessionId, worldEpoch, planId,
                    anchor.SessionId, anchor.WorldEpoch, anchor.PlanId))
            {
                return AnchorError("stale_anchor");
            }

            if (!anchor.Participants.Contains(uid)) return AnchorError("not_participant");

            // 不变量：Ready ⇐ Reported（先提交边界结果，再声明"已到达边界、可以继续"）。
            // 防止"未报告就就绪"让锚点在没有真实完成信息的情况下放行。
            if (!anchor.MemberStates.TryGetValue(uid, out var current)
                || current is not (RouteAnchorMemberState.Reported
                    or RouteAnchorMemberState.PullApplied
                    or RouteAnchorMemberState.Ready))
            {
                return AnchorError("report_required");
            }

            // Ready 必须建立在已提交边界之上（正常完成成员同样先 Report 再 Ready）
            anchor.MemberStates[uid] = RouteAnchorMemberState.Ready;
            anchor.LastActivityUtc[uid] = nowUtc;
            anchor.StateChangedAtUtc = nowUtc;

            var decision = RouteAnchorDecisions.DecideRelease(
                anchor.Phase, anchor.Participants, anchor.MemberStates,
                allowMissingPolicy: false, nowUtc, anchor.AbsoluteDeadlineUtc);

            if (decision == RouteAnchorReleaseDecision.Release)
            {
                anchor.Phase = RouteAnchorPhase.Released;
                // 记录本房间已放行的最高边界：用于拒绝旧世界/旧轮次客户端的迟到 enroll
                if (anchor.CompletedRouteIndex > room.LastReleasedRouteBoundary)
                    room.LastReleasedRouteBoundary = anchor.CompletedRouteIndex;
                // 终态即解除兜底评估（Released 后再评估没有意义）
                room.RouteAnchorEvaluateTimer?.Dispose();
                room.RouteAnchorEvaluateTimer = null;
                released = true;
                releasedAnchor = anchor;
                _logger.LogWarning("[RouteAnchor] 锚点放行：房间={RoomCode}, 锚点={AnchorId}, 边界={Boundary}→{Next}, 参与者={Count}",
                    roomCode, anchor.AnchorId, anchor.CompletedRouteIndex, anchor.NextRouteIndex, anchor.Participants.Count);
            }
            else
            {
                anchor.Phase = RouteAnchorDecisions.DerivePhase(anchor.Participants, anchor.MemberStates, decision);
                _logger.LogInformation("[RouteAnchor] 成员就绪：房间={RoomCode}, 锚点={AnchorId}, uid={Uid}, 阶段={Phase}, 决策={Decision}",
                    roomCode, anchor.AnchorId, uid, anchor.Phase, decision);
            }

            snapshot = BuildSnapshot(anchor, uid);
        }

        if (released && releasedAnchor != null)
        {
            // 锁外广播：evt-only（route-anchor 是全新协议域，不存在旧客户端订阅者）
            await _broadcaster.BroadcastGroupEventOnlyAsync(
                roomCode,
                GatewayProtocol.Events.RouteAnchorReleased,
                new
                {
                    anchorId = releasedAnchor.AnchorId,
                    sessionId = releasedAnchor.SessionId,
                    worldEpoch = releasedAnchor.WorldEpoch,
                    planId = releasedAnchor.PlanId,
                    completedRouteIndex = releasedAnchor.CompletedRouteIndex,
                    nextRouteIndex = releasedAnchor.NextRouteIndex,
                    participants = releasedAnchor.Participants.OrderBy(u => u, StringComparer.Ordinal).ToList(),
                },
                roomCode);
        }

        return new { anchor = snapshot };
    }

    /// <summary>锚点兜底评估周期（秒）。必须显著小于客户端等待上限，保证"卡死也有人评估"。</summary>
    private const int RouteAnchorEvaluateIntervalSeconds = 10;

    /// <summary>
    /// 武装一次性兜底评估定时器。必须在 lock(room) 内调用。
    /// 只有锚点存在且未终结时才武装；已武装则先 Dispose（每次评估后重新计时）。
    /// </summary>
    private void ArmRouteAnchorEvaluateTimerLocked(Room room, string roomCode)
    {
        var anchor = room.ActiveRouteAnchor;
        room.RouteAnchorEvaluateTimer?.Dispose();
        room.RouteAnchorEvaluateTimer = null;

        if (anchor == null) return;
        if (anchor.Phase is RouteAnchorPhase.Released or RouteAnchorPhase.Stopped) return;

        room.RouteAnchorEvaluateTimer = new System.Threading.Timer(
            _ => _ = EvaluateRouteAnchorTimerCallbackAsync(room, roomCode),
            null,
            TimeSpan.FromSeconds(RouteAnchorEvaluateIntervalSeconds),
            Timeout.InfiniteTimeSpan);
    }

    /// <summary>
    /// 兜底评估回调（internal 供测试直接驱动，避免测试等待真实时钟）。
    /// 房间已关闭时直接返回；评估后若仍未终结则续挂下一次。
    /// </summary>
    internal async Task EvaluateRouteAnchorTimerCallbackAsync(Room room, string roomCode)
    {
        if (_roomManager.GetRoom(roomCode) == null) return;

        try
        {
            await EvaluateRouteAnchorAsync(room, roomCode);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[RouteAnchor] 兜底评估回调失败，房间={RoomCode}", roomCode);
        }
    }

    /// <summary>
    /// 锚点对账评估（阶段 4）：把超预算成员分类为 等待 / 拉取 / 停止，并在需要时下发 Pull。
    ///
    /// 纪律：
    ///   · 心跳只判存活；宽限只由"有效活动时间"推进（重复心跳不能刷新宽限，见 RouteAnchorDecisions.ClassifyMissing）；
    ///   · 战斗/复苏使用各自预算，不共用一个阈值；
    ///   · 重试耗尽或任一成员拉取失败 → 整队 Stopped（强兜底不做"静默缺员继续"）；
    ///   · lock 内决策、lock 外发送。
    /// 调用时机：由既有入口（enroll/report/arrive/pull-ack）驱动；周期性驱动（定时器）为后续增量。
    /// </summary>
    internal async Task EvaluateRouteAnchorAsync(Room room, string roomCode)
    {
        var nowUtc = DateTime.UtcNow;
        var budgets = RouteAnchorDecisions.Budgets.Default;

        List<(string ConnectionId, object Payload)> pulls = [];
        RouteAnchorSnapshot? releasedSnapshot = null;
        RouteAnchorSnapshot? stoppedSnapshot = null;

        lock (room)
        {
            var anchor = room.ActiveRouteAnchor;
            if (anchor == null) return;
            if (anchor.Phase is RouteAnchorPhase.Released or RouteAnchorPhase.Stopped) return;

            // 1) 逐个参与者分类
            foreach (var uid in anchor.Participants.ToList())
            {
                // 已离开房间的成员（主动离开 / 心跳判死后被清理）不可恢复：
                // 立即停止整队，而不是把队伍拖到绝对截止（10 分钟）——强兜底要求"有界"。
                if (!room.Players.Any(p => string.Equals(p.PlayerUid, uid, StringComparison.Ordinal)))
                {
                    _logger.LogError("[RouteAnchor] 参与者 {Uid} 已不在房间，锚点停止：房间={RoomCode}, 锚点={AnchorId}",
                        uid, roomCode, anchor.AnchorId);
                    anchor.Phase = RouteAnchorPhase.Stopped;
                    anchor.LatestFailureReason = "member-left-room";
                    anchor.StateChangedAtUtc = nowUtc;
                    stoppedSnapshot = BuildSnapshot(anchor, "");
                    room.RouteAnchorEvaluateTimer?.Dispose();
                    room.RouteAnchorEvaluateTimer = null;
                    break;
                }

                var state = anchor.MemberStates.TryGetValue(uid, out var st)
                    ? st
                    : RouteAnchorMemberState.Unknown;
                if (state is RouteAnchorMemberState.Reported or RouteAnchorMemberState.Ready
                    or RouteAnchorMemberState.PullRequested or RouteAnchorMemberState.PullApplied
                    or RouteAnchorMemberState.PullFailed or RouteAnchorMemberState.Removed)
                {
                    continue;
                }

                var heartbeat = anchor.LastHeartbeatUtc.TryGetValue(uid, out var hb) ? hb : nowUtc;
                var activity = anchor.LastActivityUtc.TryGetValue(uid, out var ac) ? ac : default;

                var decision = RouteAnchorDecisions.ClassifyMissing(nowUtc, heartbeat, activity, state, budgets);

                if (decision == RouteAnchorMissingDecision.Stop)
                {
                    _logger.LogError("[RouteAnchor] 成员 {Uid} 不可恢复（状态={State}, 心跳={Heartbeat:O}），锚点停止：房间={RoomCode}, 锚点={AnchorId}",
                        uid, state, heartbeat, roomCode, anchor.AnchorId);
                    anchor.Phase = RouteAnchorPhase.Stopped;
                    anchor.StateChangedAtUtc = nowUtc;
                    stoppedSnapshot = BuildSnapshot(anchor, "");
                    room.RouteAnchorEvaluateTimer?.Dispose();
                    room.RouteAnchorEvaluateTimer = null;
                    break;
                }

                if (decision != RouteAnchorMissingDecision.Pull) continue;

                var attempts = anchor.PullAttempts.TryGetValue(uid, out var a) ? a : 0;
                if (!RouteAnchorDecisions.CanIssuePull(state, attempts, MaxRouteAnchorPullRetries, nowUtc, heartbeat, budgets))
                {
                    // 不可拉取（重试耗尽/心跳不新鲜/状态不允许）→ 强兜底：整队停止
                    _logger.LogError("[RouteAnchor] 成员 {Uid} 需要拉取但不可下发（状态={State}, 已尝试={Attempts}/{Max}），锚点停止：房间={RoomCode}",
                        uid, state, attempts, MaxRouteAnchorPullRetries, roomCode);
                    anchor.Phase = RouteAnchorPhase.Stopped;
                    anchor.StateChangedAtUtc = nowUtc;
                    stoppedSnapshot = BuildSnapshot(anchor, "");
                    room.RouteAnchorEvaluateTimer?.Dispose();
                    room.RouteAnchorEvaluateTimer = null;
                    break;
                }

                var connectionId = room.Players.FirstOrDefault(p => p.PlayerUid == uid)?.ConnectionId;
                if (string.IsNullOrEmpty(connectionId)) continue;

                var attempt = attempts + 1;
                var commandId = RouteAnchorDecisions.BuildPullCommandId(anchor.AnchorId, uid, attempt);
                anchor.PullCommandIds[uid] = commandId;
                anchor.PullAttempts[uid] = attempt;
                anchor.MemberStates[uid] = RouteAnchorMemberState.PullRequested;
                anchor.StateChangedAtUtc = nowUtc;

                pulls.Add((connectionId, new
                {
                    anchorId = anchor.AnchorId,
                    sessionId = anchor.SessionId,
                    worldEpoch = anchor.WorldEpoch,
                    planId = anchor.PlanId,
                    commandId,
                    targetRouteIndex = anchor.NextRouteIndex,
                    attempt,
                }));

                _logger.LogWarning("[RouteAnchor] 下发 Pull：房间={RoomCode}, 锚点={AnchorId}, uid={Uid}, 目标路线={Target}, 第 {Attempt}/{Max} 次",
                    roomCode, anchor.AnchorId, uid, anchor.NextRouteIndex, attempt, MaxRouteAnchorPullRetries);
            }

            if (stoppedSnapshot == null)
            {
                // 2) 放行裁决
                var decision = RouteAnchorDecisions.DecideRelease(
                    anchor.Phase, anchor.Participants, anchor.MemberStates,
                    allowMissingPolicy: false, nowUtc, anchor.AbsoluteDeadlineUtc);

                if (decision == RouteAnchorReleaseDecision.Release)
                {
                    anchor.Phase = RouteAnchorPhase.Released;
                    if (anchor.CompletedRouteIndex > room.LastReleasedRouteBoundary)
                        room.LastReleasedRouteBoundary = anchor.CompletedRouteIndex;
                    anchor.StateChangedAtUtc = nowUtc;
                    releasedSnapshot = BuildSnapshot(anchor, "");
                    _logger.LogWarning("[RouteAnchor] 对账放行：房间={RoomCode}, 锚点={AnchorId}, 边界={Boundary}→{Next}",
                        roomCode, anchor.AnchorId, anchor.CompletedRouteIndex, anchor.NextRouteIndex);
                }
                else if (decision == RouteAnchorReleaseDecision.Stop)
                {
                    anchor.Phase = RouteAnchorPhase.Stopped;
                    anchor.StateChangedAtUtc = nowUtc;
                    stoppedSnapshot = BuildSnapshot(anchor, "");
                    _logger.LogError("[RouteAnchor] 对账停止（超时或不可恢复）：房间={RoomCode}, 锚点={AnchorId}", roomCode, anchor.AnchorId);
                }
                else
                {
                    anchor.Phase = RouteAnchorDecisions.DerivePhase(anchor.Participants, anchor.MemberStates, decision);
                }
            }
        }

        // 3) lock 外发送
        foreach (var (connectionId, payload) in pulls)
        {
            await _broadcaster.SendEventOnlyToConnectionAsync(
                connectionId, GatewayProtocol.Events.RouteAnchorPull, payload, roomCode);
        }

        if (releasedSnapshot != null)
        {
            await _broadcaster.BroadcastGroupEventOnlyAsync(
                roomCode, GatewayProtocol.Events.RouteAnchorReleased,
                new
                {
                    anchorId = releasedSnapshot.AnchorId,
                    sessionId = releasedSnapshot.SessionId,
                    worldEpoch = releasedSnapshot.WorldEpoch,
                    planId = releasedSnapshot.PlanId,
                    completedRouteIndex = releasedSnapshot.CompletedRouteIndex,
                    nextRouteIndex = releasedSnapshot.NextRouteIndex,
                    participants = releasedSnapshot.Participants,
                },
                roomCode);
        }

        if (stoppedSnapshot != null)
        {
            await _broadcaster.BroadcastGroupEventOnlyAsync(
                roomCode, GatewayProtocol.Events.RouteAnchorStopped,
                new { anchorId = stoppedSnapshot.AnchorId, sessionId = stoppedSnapshot.SessionId, planId = stoppedSnapshot.PlanId },
                roomCode);
        }

        // 未终结 → 续挂下一次兜底评估；已终结 → 定时器在 ArmXxx 内被清掉
        lock (room)
        {
            ArmRouteAnchorEvaluateTimerLocked(room, roomCode);
        }
    }

    /// <summary>
    /// Pull 执行确认（sync.routeAnchorPullApplied / sync.routeAnchorPullFailed）：
    /// 成功 → PullApplied（仍需 Ready 才计入放行）；失败 → PullFailed → 整队停止。
    /// 命令 ID 必须匹配（重复回报幂等，旧/他人命令忽略）。
    /// </summary>
    public async Task<object> ReportRouteAnchorPullAppliedAsync(
        GatewayHandlerContext ctx, string anchorId, string commandId, bool success, string reason)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return AnchorError("not_in_room");

        var nowUtc = DateTime.UtcNow;
        RouteAnchorSnapshot snapshot;
        bool accepted = false;

        lock (room)
        {
            var uid = ResolveCallerUid(room, ctx);
            if (uid == null) return AnchorError("not_in_room");

            var anchor = room.ActiveRouteAnchor;
            if (anchor == null) return AnchorError("no_anchor");
            if (!RouteAnchorDecisions.IsSameAnchor(anchorId, anchor.AnchorId)) return AnchorError("stale_anchor");
            if (!anchor.Participants.Contains(uid)) return AnchorError("not_participant");

            var expected = anchor.PullCommandIds.TryGetValue(uid, out var cmd) ? cmd : "";
            if (!RouteAnchorDecisions.ShouldAcceptPullApplied(commandId, expected))
            {
                _logger.LogWarning("[RouteAnchor] 忽略不匹配的 Pull 确认：房间={RoomCode}, uid={Uid}, 收到={Cmd}, 期望={Expected}",
                    roomCode, uid, commandId, expected);
                return AnchorError("command_mismatch");
            }

            accepted = true;
            if (success)
            {
                anchor.MemberStates[uid] = RouteAnchorMemberState.PullApplied;
                _logger.LogInformation("[RouteAnchor] Pull 已执行：房间={RoomCode}, uid={Uid}, 命令={Cmd}", roomCode, uid, commandId);
            }
            else
            {
                anchor.MemberStates[uid] = RouteAnchorMemberState.PullFailed;
                anchor.LatestFailureReason = reason ?? "";
                _logger.LogError("[RouteAnchor] Pull 执行失败（整队将停止）：房间={RoomCode}, uid={Uid}, 原因={Reason}",
                    roomCode, uid, reason);
            }

            anchor.LastActivityUtc[uid] = nowUtc;
            anchor.StateChangedAtUtc = nowUtc;
            snapshot = BuildSnapshot(anchor, uid);
        }

        if (accepted) await EvaluateRouteAnchorAsync(room, roomCode);
        return new { anchor = snapshot };
    }

    /// <summary>
    /// 取消当前锚点（sync.routeAnchorCancel，阶段 5）。
    ///
    /// 语义：成员主动取消（用户停止 / 任务取消 / 房间关闭前的收尾）时，把锚点置为 Stopped 并广播，
    /// 让所有成员都能立即停止等待，而不是各自等到本地超时。
    /// 约束：只允许**参与者**取消（避免旁观/迟到成员误停整队）；幂等（已终结则返回快照不重复广播）。
    /// </summary>
    public async Task<object> CancelRouteAnchorAsync(GatewayHandlerContext ctx, string anchorId, string reason)
    {
        var (room, roomCode) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null || roomCode == null) return AnchorError("not_in_room");

        var nowUtc = DateTime.UtcNow;
        RouteAnchorSnapshot snapshot;
        bool broadcastStop = false;

        lock (room)
        {
            var uid = ResolveCallerUid(room, ctx);
            if (uid == null) return AnchorError("not_in_room");

            var anchor = room.ActiveRouteAnchor;
            if (anchor == null) return AnchorError("no_anchor");

            if (anchor.Phase is RouteAnchorPhase.Released or RouteAnchorPhase.Stopped)
            {
                // 幂等：已终结（含已放行）时不再改动、不重复广播
                snapshot = BuildSnapshot(anchor, uid);
                return new { anchor = snapshot };
            }

            if (!RouteAnchorDecisions.IsSameAnchor(anchorId, anchor.AnchorId)) return AnchorError("stale_anchor");
            if (!anchor.Participants.Contains(uid)) return AnchorError("not_participant");

            anchor.Phase = RouteAnchorPhase.Stopped;
            anchor.LatestFailureReason = string.IsNullOrEmpty(reason) ? "cancelled" : reason;
            anchor.StateChangedAtUtc = nowUtc;
            room.RouteAnchorEvaluateTimer?.Dispose();
            room.RouteAnchorEvaluateTimer = null;
            broadcastStop = true;

            _logger.LogWarning("[RouteAnchor] 锚点被成员取消：房间={RoomCode}, 锚点={AnchorId}, uid={Uid}, 原因={Reason}",
                roomCode, anchor.AnchorId, uid, anchor.LatestFailureReason);

            snapshot = BuildSnapshot(anchor, uid);
        }

        if (broadcastStop)
        {
            await _broadcaster.BroadcastGroupEventOnlyAsync(
                roomCode, GatewayProtocol.Events.RouteAnchorStopped,
                new
                {
                    anchorId = snapshot.AnchorId,
                    sessionId = snapshot.SessionId,
                    planId = snapshot.PlanId,
                    reason = snapshot.HasAnchor ? "cancelled" : "cancelled",
                },
                roomCode);
        }

        return new { anchor = snapshot };
    }

    /// <summary>Pull 重试上限（强兜底：耗尽即停止整队）。</summary>
    private const int MaxRouteAnchorPullRetries = 2;

    /// <summary>路线锚点权威状态查询（sync.routeAnchorState）。未加入房间返回空快照。</summary>
    public Task<object> QueryRouteAnchorStateAsync(GatewayHandlerContext ctx)
    {
        var (room, _) = _roomManager.GetRoomByConnectionId(ctx.ConnectionId);
        if (room == null) return Task.FromResult<object>(new { anchor = new RouteAnchorSnapshot { HasAnchor = false } });

        RouteAnchorSnapshot snapshot;
        lock (room)
        {
            var uid = ResolveCallerUid(room, ctx) ?? "";
            snapshot = BuildSnapshot(room.ActiveRouteAnchor, uid);
        }

        return Task.FromResult<object>(new { anchor = snapshot });
    }

    private static RouteAnchorOutcome ParseOutcome(string outcome)
        => outcome switch
        {
            "skipped" => RouteAnchorOutcome.Skipped,
            "recovered" => RouteAnchorOutcome.Recovered,
            "rerun-completed" => RouteAnchorOutcome.RerunCompleted,
            _ => RouteAnchorOutcome.Completed,
        };

    /// <summary>
    /// 统一的锚点错误返回（不抛异常，避免被网关吞成 internal_error）。
    /// 除人类可读 message 外，额外给出机器可读的 <c>routeAnchorReason</c>：
    /// 客户端据此区分"重跑窗口让位（rerun_in_progress）"与"真实失败"，避免解析字符串。
    /// </summary>
    private static object AnchorError(string code)
        => new
        {
            error = new
            {
                code = GatewayProtocol.ErrorCodes.BadRequest,
                routeAnchorReason = code,
                message = $"route_anchor:{code}",
            },
        };
}
