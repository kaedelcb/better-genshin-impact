#nullable enable

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Gateway;

/// <summary>
/// 网关协议常量（BGI 客户端侧）。
/// 数据抄自 BgiCoordinatorServer/Gateway/GatewayProtocol.cs（**以服务器为唯一权威，改动须双向同步**）；
/// 只收录 BGI 锄地房间平面用到的消息名与事件名（控制房间/日志三件套/截图属助手平面，不收）。
/// </summary>
public static class GatewayProtocol
{
    /// <summary>信封协议版本，冻结。</summary>
    public const int ProtocolVersion = 3;

    public static class MessageTypes
    {
        public const string Command = "command";
        public const string Event = "event";
        public const string Query = "query";
        public const string Response = "response";
        public const string Hello = "hello";
    }

    /// <summary>服务器 → 客户端回调名（§4.2：只有两个，本切片只用 evt）。</summary>
    public static class Callbacks
    {
        public const string Event = "evt";
        public const string State = "state";
    }

    /// <summary>Hub 入口方法名（/gateway 上仅有的两个服务端方法）。</summary>
    public static class HubMethods
    {
        public const string Dispatch = "Dispatch";
        public const string Query = "Query";
    }

    /// <summary>响应 error.code（镜像服务器 GatewayProtocol.ErrorCodes，本切片实际用到的子集）。</summary>
    public static class ErrorCodes
    {
        /// <summary>未知消息名——v3 下"服务端无此功能"的表现形式（对齐旧协议 HubException "does not exist"）。</summary>
        public const string UnsupportedOperation = "unsupported_operation";
    }

    /// <summary>客户端 → 服务端消息名（§9.2 按族，本切片实际用到的子集）。</summary>
    public static class Names
    {
        public const string SessionHello = "session.hello";
        public const string SessionHeartbeat = "session.heartbeat";

        public const string RoomCreate = "room.create";
        public const string RoomJoin = "room.join";
        public const string RoomLeave = "room.leave";
        public const string RoomClose = "room.close";
        public const string RoomMarkStarted = "room.markStarted";
        public const string RoomGetState = "room.getState";
        public const string RoomListOnline = "room.listOnline";
        public const string RoomGetConfig = "room.getConfig";
        public const string RoomGetRoundHostOrder = "room.getRoundHostOrder";
        public const string RoomSetConfig = "room.setConfig";
        public const string RoomSetHostRouteList = "room.setHostRouteList";
        public const string RoomReportHostReady = "room.reportHostReady";

        public const string RouteReportList = "route.reportList";
        public const string RouteReportVariantSchema = "route.reportVariantSchema";

        public const string SyncReportArrival = "sync.reportArrival";
        public const string SyncWaitForAllPlayers = "sync.waitForAllPlayers";

        /// <summary>
        /// 集体跳段执行确认（collective-skip-applied-ack）：按 skipId 回报本地跳段结果。
        /// 纯新增消息名；旧服务端不认识它，客户端仅在收到带 skipId 的跳段请求时才发送。
        /// </summary>
        public const string SyncReportCollectiveSkipApplied = "sync.reportCollectiveSkipApplied";

        /// <summary>
        /// 上报协同中止（hoeing-multiplayer-coordinated-abort-restart）：任一端真异常中止时通知
        /// 服务器广播全房间（sync.coordinatedAborted）。纯新增消息名；旧服务端不认识它返回 error，
        /// 客户端 catch 降级为纯本地判定（不更糟）。
        /// </summary>
        public const string SyncReportCoordinatedAbort = "sync.reportCoordinatedAbort";

        public const string FightReportParticipant = "fight.reportParticipant";
        public const string FightReportDone = "fight.reportDone";

        /// <summary>payload.kind 区分 5 个旧语义：capReached/capCleared/armed/twoNoExp/twoNoExpCleared。</summary>
        public const string ExpReportFightResult = "exp.reportFightResult";

        public const string KazuhaDeclareCapability = "kazuha.declareCapability";
        public const string KazuhaNotifyCollectStarted = "kazuha.notifyCollectStarted";

        public const string WorldReportJoined = "world.reportJoined";
        public const string WorldResetJoined = "world.resetJoined";
        public const string WorldResetForNewRound = "world.resetForNewRound";

        public const string AnomalyNotify = "anomaly.notify";
        public const string AnomalyNotifyFightPoint = "anomaly.notifyFightPoint";
        public const string AnomalyRecovered = "anomaly.recovered";
        public const string AnomalyMemberStatusChanged = "anomaly.memberStatusChanged";
        public const string AnomalyReportMemberProgress = "anomaly.reportMemberProgress";
        public const string AnomalyRouteSkipped = "anomaly.routeSkipped";
        public const string AnomalyWaitPointReached = "anomaly.waitPointReached";
        public const string AnomalyFightingStatusChanged = "anomaly.fightingStatusChanged";

        // === 路线边界锚点（route-anchor，独立协议域）===
        // 与普通同步（sync.waitForAllPlayers / AllArrived）、旧集体跳段完全独立：
        // 前者管路线内部，锚点管"上一条路线是否全员收口、能否进入下一条"。
        public const string RouteAnchorEnroll = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Enroll;
        public const string RouteAnchorReport = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Report;
        public const string RouteAnchorArrived = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Arrived;
        public const string RouteAnchorCancel = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Cancel;
        public const string RouteAnchorStateQuery = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.State;
        public const string RouteAnchorPullAck = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.PullAck;
    }

    /// <summary>room.getState 的 section 取值（§4.3 分组收敛，6 个旧查询聚合）。</summary>
    public static class StateSections
    {
        public const string HostReady = "hostReady";
        public const string HostRouteListUploaded = "hostRouteListUploaded";
        public const string HostRouteList = "hostRouteList";
        public const string HostRouteListStatus = "hostRouteListStatus";
        public const string MemberProgress = "memberProgress";
    }

    /// <summary>exp.reportFightResult 的 kind 取值（与旧 5 个 Hub 方法一一对应）。</summary>
    public static class ExpKinds
    {
        public const string CapReached = "capReached";
        public const string CapCleared = "capCleared";
        public const string Armed = "armed";
        public const string TwoNoExp = "twoNoExp";
        public const string TwoNoExpCleared = "twoNoExpCleared";
    }

    /// <summary>
    /// 服务端 → 客户端 evt 事件名（对应服务器 LegacyEventMap 的映射目标值）。
    /// 客户端 DispatchEvt 按这些名字分发到 CoordinatorClient 的各 C# 事件。
    /// </summary>
    public static class Events
    {
        public const string RoomPlayerListChanged = "room.playerListChanged";       // ← PlayerListUpdated
        public const string SyncAllArrived = "sync.allArrived";                     // ← AllArrived
        public const string FightAllDone = "fight.allDone";                         // ← AllFightDone
        public const string RouteDiffReceived = "route.diffReceived";               // ← RouteDiffReceived
        public const string RouteVerificationPassed = "route.verificationPassed";   // ← RouteVerificationPassed
        public const string RouteVariantConsistencyPassed = "route.variantConsistencyPassed";
        public const string RouteVariantConsistencyFailed = "route.variantConsistencyFailed";
        public const string RoomClosed = "room.closed";                             // ← RoomClosed
        public const string RoomVersionCheckRejected = "room.versionCheckRejected"; // ← VersionCheckRejected
        public const string RouteVerificationAllDone = "route.verificationAllDone"; // ← RouteVerificationAllDone
        public const string ExpAllCapReached = "exp.allCapReached";                 // ← AllReachedExpCap
        public const string KazuhaPlayerUpdated = "kazuha.playerUpdated";           // ← KazuhaPlayerUpdated
        public const string KazuhaCollectStarted = "kazuha.collectStarted";         // ← KazuhaCollectStarted
        public const string WorldAllJoined = "world.allJoined";                     // ← AllWorldJoined
        public const string RoomHostReadyChanged = "room.hostReadyChanged";         // ← HostReadyChanged
        public const string RoomHostRouteListReady = "room.hostRouteListReady";     // ← HostRouteListReady
        public const string AnomalyPlayerNotified = "anomaly.playerNotified";       // ← PlayerAnomalyNotify
        public const string AnomalyFightPointNotified = "anomaly.fightPointNotified"; // ← PlayerAnomalyNotifyFightPoint
        public const string AnomalyPlayerRecovered = "anomaly.playerRecovered";     // ← PlayerAnomalyRecovered / AbnormalPlayerRecovered（两旧名同名映射）
        public const string RoomMemberStatusChanged = "room.memberStatusChanged";   // ← MemberStatusChanged
        public const string RoomStartRoute = "room.startRoute";                     // ← StartRoute
        public const string SyncRequestSkipToProgress = "sync.requestSkipToProgress"; // ← RequestSkipToProgress
        public const string SyncCollectiveSkipDegraded = "sync.collectiveSkipDegraded"; // ← CollectiveSkipDegraded
        public const string SyncCollectiveSkipAppliedAll = "sync.collectiveSkipAppliedAll"; // ← CollectiveSkipAppliedAll（collective-skip-applied-ack）

        /// <summary>协同中止广播（hoeing-multiplayer-coordinated-abort-restart）：任一端上报后由服务器广播全房间。载荷：reason, reporterUid。</summary>
        public const string SyncCoordinatedAborted = "sync.coordinatedAborted";

        // === 路线边界锚点事件（route-anchor）===
        // 事件只负责"尽快查询权威快照"，不作为唯一正确性来源（方案正文第 7 章）。
        /// <summary>锚点状态变化（收到后应立即查询 sync.routeAnchorState）。</summary>
        public const string SyncRouteAnchorChanged = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Changed;

        /// <summary>锚点放行：服务端授权进入下一条路线。</summary>
        public const string SyncRouteAnchorReleased = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Released;

        /// <summary>锚点停止：无法确认全员安全收口，整队停止。</summary>
        public const string SyncRouteAnchorStopped = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Stopped;

        /// <summary>服务端定向下发"把他拉回当前边界"（只发给需要被拉回的成员）。</summary>
        public const string SyncRouteAnchorPull = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.PullEvent;
    }
}
