using BetterGenshinImpact.Shared.CooperativeRerun;

namespace BgiCoordinatorServer.Gateway;

/// <summary>
/// 网关协议常量与映射表（《通信方案》§4.3 65 方法版逐个照搬 + §9.2 按族命名）。
/// 映射表是纯数据：新增消息不改 Hub 签名，服务端路由表驱动。
/// </summary>
public static class GatewayProtocol
{
    /// <summary>信封协议版本，冻结。</summary>
    public const int ProtocolVersion = 3;

    /// <summary>低于此协议版本的客户端 hello 直接拒绝（§4.4 minimumClientProtocol）。</summary>
    public const int MinimumClientProtocol = 2;

    /// <summary>服务端能力清单（§4.4：能力缺省即不支持）。只列本切片真实实现的。</summary>
    public static readonly string[] ServerCapabilities =
        ["gateway.envelope.v3", RerunProtocol.Capability, BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Capability];

    public static class MessageTypes
    {
        public const string Command = "command";
        public const string Event = "event";
        public const string Query = "query";
        public const string Response = "response";
        public const string Hello = "hello";
    }

    /// <summary>错误码（响应 payload.error.code）。</summary>
    public static class ErrorCodes
    {
        public const string UnsupportedOperation = "unsupported_operation";
        public const string HandshakeRequired = "handshake_required";
        public const string WrongChannel = "wrong_channel";
        public const string ProtocolTooOld = "protocol_too_old";
        public const string RoomProtocolMismatch = "room_protocol_mismatch";
        public const string BadRequest = "bad_request";
        public const string InternalError = "internal_error";
    }

    /// <summary>房间协议锁定值（§4.7：同一房间不允许新旧协议混用，按建房者客户端协议锁定）。</summary>
    public static class RoomProtocols
    {
        public const string Legacy = "legacy";
        public const string V3 = "v3";
    }

    /// <summary>消息名常量（§9.2 按族）。</summary>
    public static class Names
    {
        public const string RerunUpdate = RerunProtocol.Update;
        public const string RerunState = RerunProtocol.State;

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
        public const string RoomSetWhitelist = "room.setWhitelist";
        public const string RoomSetHostRouteList = "room.setHostRouteList";
        public const string RoomReportHostReady = "room.reportHostReady";

        public const string RouteReportList = "route.reportList";
        public const string RouteReportVariantSchema = "route.reportVariantSchema";
        public const string RouteReportVerificationDone = "route.reportVerificationDone";

        public const string SyncReportArrival = "sync.reportArrival";
        public const string SyncWaitForAllPlayers = "sync.waitForAllPlayers";

        /// <summary>
        /// 集体跳段执行确认（collective-skip-applied-ack）：客户端按 skipId 回报本地跳段结果。
        /// 纯新增消息名，旧客户端不发送即可，不影响既有协议。
        /// </summary>
        public const string SyncReportCollectiveSkipApplied = "sync.reportCollectiveSkipApplied";

        /// <summary>
        /// 协同中止上报（hoeing-multiplayer-coordinated-abort-restart）：任一成员触发真异常中止时上报，
        /// 服务端幂等广播（首报 wins）让全组同步收口。纯新增消息名，旧客户端不发送即可。
        /// </summary>
        public const string SyncReportCoordinatedAbort = "sync.reportCoordinatedAbort";

        // === 路线边界锚点（route-anchor，独立协议域）===
        // 不复用普通 AllArrived / StartRoute / Abort / CollectiveSkipAppliedAll：
        // 它们分别属于路线内同步、旧重对齐、中断和旧中途集体跳段语义。
        // 说明：常量先于处理器定义（阶段 1 只固化协议面，不接生产路径；
        // 对应处理器与事件映射在阶段 2 随客户端接入一并注册）。
        public const string RouteAnchorEnroll = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Enroll;
        public const string RouteAnchorReport = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Report;
        public const string RouteAnchorPullApplied = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.PullAck;
        /// <summary>失败形态与成功同形（success=false），保留独立名字以便路由别名。</summary>
        public const string RouteAnchorPullFailed = "sync.routeAnchorPullFailed";
        public const string RouteAnchorArrived = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Arrived;
        public const string RouteAnchorCancel = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Cancel;
        public const string RouteAnchorStateQuery = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.State;

        public const string FightReportParticipant = "fight.reportParticipant";
        public const string FightReportDone = "fight.reportDone";

        /// <summary>
        /// §4.3 终态：客户端只报"本局有无经验"、计数上迁服务端（exp.serverSideCount 能力，货冻结未实现）。
        /// 兼容期：payload.kind 区分 5 个旧语义（capReached/capCleared/armed/twoNoExp/twoNoExpCleared），行为与旧方法一致。
        /// </summary>
        public const string ExpReportFightResult = "exp.reportFightResult";

        public const string KazuhaDeclareCapability = "kazuha.declareCapability";
        public const string KazuhaSetPlayer = "kazuha.setPlayer";
        public const string KazuhaNotifyCollectStarted = "kazuha.notifyCollectStarted";

        public const string WorldReportJoined = "world.reportJoined";
        public const string WorldResetJoined = "world.resetJoined";
        public const string WorldResetForNewRound = "world.resetForNewRound";

        public const string AnomalyReportWaitPoint = "anomaly.reportWaitPoint";
        public const string AnomalyReportArrivalAtWaitPoint = "anomaly.reportArrivalAtWaitPoint";
        public const string AnomalyClearStatus = "anomaly.clearStatus";
        public const string AnomalyNotify = "anomaly.notify";
        public const string AnomalyNotifyFightPoint = "anomaly.notifyFightPoint";
        public const string AnomalyRecovered = "anomaly.recovered";
        public const string AnomalyMemberStatusChanged = "anomaly.memberStatusChanged";
        public const string AnomalyReportMemberProgress = "anomaly.reportMemberProgress";
        public const string AnomalyRouteSkipped = "anomaly.routeSkipped";
        public const string AnomalyWaitPointReached = "anomaly.waitPointReached";
        public const string AnomalyFightingStatusChanged = "anomaly.fightingStatusChanged";

        public const string ControlJoinRoom = "control.joinRoom";
        public const string ControlSendCommand = "control.sendCommand";
        public const string ControlReportStatus = "control.reportStatus";
        public const string ControlConfirmAllReady = "control.confirmAllReady";
        public const string ControlReportOnlineEvent = "control.reportOnlineEvent";
        public const string ControlClearOnlineHistory = "control.clearOnlineHistory";

        /// <summary>
        /// 远端任务下发结果回执（target → server）：纯新增消息名，旧客户端不发送即可；
        /// 旧服务端返回 unsupported_operation，新客户端据此降级停发（不影响既有协议）。
        /// </summary>
        public const string ControlReportCommandResult = "control.reportCommandResult";

        public const string LogReportBatch = "log.reportBatch";
        public const string LogSubscribe = "log.subscribe";
        public const string LogUnsubscribe = "log.unsubscribe";
        public const string LogRequestFiles = "log.requestFiles";
        public const string LogReportFiles = "log.reportFiles";
        public const string LogRequestDownload = "log.requestDownload";
        public const string LogReportChunk = "log.reportChunk";

        public const string ScreenshotReport = "screenshot.report";
        public const string ScreenshotRequest = "screenshot.request";
        public const string ScreenshotReportEx = "screenshot.reportEx";
    }

    /// <summary>
    /// 旧 Hub 方法名 → 新消息名（§4.3 65 方法版逐个照搬；聚合族内用 payload 字段区分原方法）。
    /// 仅作路由与核对清单数据，旧 Hub 转发器走强类型直调不经过本表。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyMethodMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 房间生命周期（MarkRoomStartedWithProgress → room.markStarted，completedHostUids 收进 payload）
            ["CreateRoom"] = Names.RoomCreate,
            ["JoinRoom"] = Names.RoomJoin,
            ["LeaveRoom"] = Names.RoomLeave,
            ["CloseRoom"] = Names.RoomClose,
            ["MarkRoomStarted"] = Names.RoomMarkStarted,
            ["MarkRoomStartedWithProgress"] = Names.RoomMarkStarted,
            // 房间查询（全部走 Query；getState 族用 payload.section 区分）
            ["GetOnlineRooms"] = Names.RoomListOnline,
            ["GetRoomConfig"] = Names.RoomGetConfig,
            ["GetRoundHostOrder"] = Names.RoomGetRoundHostOrder,
            ["IsHostReady"] = Names.RoomGetState,
            ["IsHostRouteListUploaded"] = Names.RoomGetState,
            ["GetHostRouteList"] = Names.RoomGetState,
            ["GetHostRouteListStatus"] = Names.RoomGetState,
            ["GetWorldJoinedCount"] = Names.RoomGetState,
            ["GetMemberProgress"] = Names.RoomGetState,
            // 房间配置
            ["SetRoomConfig"] = Names.RoomSetConfig,
            ["UpdateWhitelist"] = Names.RoomSetWhitelist,
            ["SetHostRouteList"] = Names.RoomSetHostRouteList,
            ["ReportHostReady"] = Names.RoomReportHostReady,
            // 路线验证
            ["ReportRouteList"] = Names.RouteReportList,
            ["ReportRouteVariantSchema"] = Names.RouteReportVariantSchema,
            ["ReportRouteVerificationDone"] = Names.RouteReportVerificationDone,
            // 同步点（ReportArrivalWithExpectedCount 的 expectedCount 收进 payload）
            ["ReportArrival"] = Names.SyncReportArrival,
            ["ReportArrivalWithExpectedCount"] = Names.SyncReportArrival,
            ["WaitForAllPlayers"] = Names.SyncWaitForAllPlayers,
            // 战斗
            ["ReportFightParticipant"] = Names.FightReportParticipant,
            ["ReportFightDone"] = Names.FightReportDone,
            // 经验上限（payload.kind 区分，见 Names.ExpReportFightResult 注释）
            ["ReportExpCapReached"] = Names.ExpReportFightResult,
            ["ReportExpCapCleared"] = Names.ExpReportFightResult,
            ["ReportExpArmed"] = Names.ExpReportFightResult,
            ["ReportTwoConsecutiveNoExp"] = Names.ExpReportFightResult,
            ["ReportTwoConsecutiveNoExpCleared"] = Names.ExpReportFightResult,
            // 万叶（SetKazuhaPlayer 已废弃空实现，保留独立消息名作 no-op 路由）
            ["DeclareKazuhaCapability"] = Names.KazuhaDeclareCapability,
            ["SetKazuhaPlayer"] = Names.KazuhaSetPlayer,
            ["NotifyKazuhaCollectStarted"] = Names.KazuhaNotifyCollectStarted,
            // 世界加入
            ["ReportWorldJoined"] = Names.WorldReportJoined,
            ["ResetWorldJoined"] = Names.WorldResetJoined,
            ["ResetForNewWorldRound"] = Names.WorldResetForNewRound,
            // 异常协调 / 重对齐（§4.3 "anomaly.reportWaitPoint 等"，迁移期先进兼容层）
            ["WaitPointReport"] = Names.AnomalyReportWaitPoint,
            ["ReportArrivalAtWaitPoint"] = Names.AnomalyReportArrivalAtWaitPoint,
            ["ClearAbnormalStatus"] = Names.AnomalyClearStatus,
            ["PlayerAnomalyNotify"] = Names.AnomalyNotify,
            ["PlayerAnomalyNotifyFightPoint"] = Names.AnomalyNotifyFightPoint,
            ["PlayerAnomalyRecovered"] = Names.AnomalyRecovered,
            ["MemberStatusChanged"] = Names.AnomalyMemberStatusChanged,
            ["ReportMemberProgress"] = Names.AnomalyReportMemberProgress,
            ["RouteSkipped"] = Names.AnomalyRouteSkipped,
            ["WaitPointReached"] = Names.AnomalyWaitPointReached,
            ["FightingStatusChanged"] = Names.AnomalyFightingStatusChanged,
            // 心跳（HeartbeatWithProgress 的 progress 收进 payload）
            ["Heartbeat"] = Names.SessionHeartbeat,
            ["HeartbeatWithProgress"] = Names.SessionHeartbeat,
            // 控制房间（助手）
            ["JoinControlRoom"] = Names.ControlJoinRoom,
            ["SendRemoteCommand"] = Names.ControlSendCommand,
            ["ReportControlStatus"] = Names.ControlReportStatus,
            ["ConfirmAllReady"] = Names.ControlConfirmAllReady,
            ["ReportOnlineEvent"] = Names.ControlReportOnlineEvent,
            ["ClearOnlineHistory"] = Names.ControlClearOnlineHistory,
            // 日志三件套
            ["ReportMemberLogBatch"] = Names.LogReportBatch,
            ["SubscribeMemberLog"] = Names.LogSubscribe,
            ["UnsubscribeMemberLog"] = Names.LogUnsubscribe,
            ["RequestMemberLogFiles"] = Names.LogRequestFiles,
            ["ReportMemberLogFiles"] = Names.LogReportFiles,
            ["RequestMemberLogDownload"] = Names.LogRequestDownload,
            ["ReportMemberLogChunk"] = Names.LogReportChunk,
            // 截图汇聚
            ["ReportMemberScreenshot"] = Names.ScreenshotReport,
            ["RequestMemberScreenshot"] = Names.ScreenshotRequest,
            ["ReportMemberScreenshotEx"] = Names.ScreenshotReportEx,
        };

    /// <summary>
    /// 旧事件名 → evt 新事件名（双发映射，§4.7）。
    /// 已逐个核对客户端订阅：CoordinatorClient.cs:182-302（23 个）、SignalRClient.cs:104-131（12 个）、
    /// wwwroot/control-room.js（4 个）。服务端广播但现网无订阅的也一并收录（前向兼容）。
    /// </summary>
    public static readonly IReadOnlyDictionary<string, string> LegacyEventMap =
        new Dictionary<string, string>(StringComparer.Ordinal)
        {
            // 锄地房间事件
            ["PlayerListUpdated"] = "room.playerListChanged",
            ["AllArrived"] = "sync.allArrived",
            ["AllFightDone"] = "fight.allDone",
            ["RouteDiffReceived"] = "route.diffReceived",
            ["RouteVerificationPassed"] = "route.verificationPassed",
            ["RouteVariantConsistencyPassed"] = "route.variantConsistencyPassed",
            ["RouteVariantConsistencyFailed"] = "route.variantConsistencyFailed",
            ["RouteVerificationAllDone"] = "route.verificationAllDone",
            ["RoomClosed"] = "room.closed",
            ["VersionCheckRejected"] = "room.versionCheckRejected",
            ["HostReadyChanged"] = "room.hostReadyChanged",
            ["HostRouteListReady"] = "room.hostRouteListReady",
            ["AllWorldJoined"] = "world.allJoined",
            ["AllReachedExpCap"] = "exp.allCapReached",
            ["KazuhaPlayerUpdated"] = "kazuha.playerUpdated",
            ["KazuhaCollectStarted"] = "kazuha.collectStarted",
            ["UnifiedWaitPoint"] = "anomaly.unifiedWaitPoint",
            ["AllPlayersArrived"] = "anomaly.allPlayersArrived",
            ["AbnormalPlayerRecovered"] = "anomaly.playerRecovered",
            ["PlayerAnomalyRecovered"] = "anomaly.playerRecovered",
            ["PlayerAnomalyNotify"] = "anomaly.playerNotified",
            ["PlayerAnomalyNotifyFightPoint"] = "anomaly.fightPointNotified",
            ["MemberStatusChanged"] = "room.memberStatusChanged",
            ["StartRoute"] = "room.startRoute",
            ["RequestSkipToProgress"] = "sync.requestSkipToProgress",
            ["CollectiveSkipDegraded"] = "sync.collectiveSkipDegraded",
            // 集体跳段 Applied 汇总（collective-skip-applied-ack）：全部必要成员回报后才广播。
            // 旧客户端不订阅此事件 → 广播被静默丢弃，行为退化为"只发跳段请求、不等确认"。
            ["CollectiveSkipAppliedAll"] = "sync.collectiveSkipAppliedAll",
            ["RouteEnforceSync"] = "route.enforceSync",
            // 控制房间事件
            ["ControlRoomPlayersUpdated"] = "control.playersUpdated",
            ["RemoteCommand"] = "control.remoteCommand",
            ["RemoteCommandAck"] = "control.remoteCommandAck",
            ["JoinRejected"] = "control.joinRejected",
            ["AllReady"] = "control.allReady",
            ["AllReadyConfirm"] = "control.allReadyConfirm",
            ["AllReadyAbort"] = "control.allReadyAbort",
            // 日志三件套 + 截图
            ["MemberScreenshot"] = "screenshot.member",
            ["MemberScreenshotRequested"] = "screenshot.requested",
            ["MemberLogBatch"] = "log.batch",
            ["MemberLogSubscribersChanged"] = "log.subscribersChanged",
            ["MemberLogFilesRequested"] = "log.filesRequested",
            ["MemberLogFileList"] = "log.fileList",
            ["MemberLogDownloadRequested"] = "log.downloadRequested",
            ["MemberLogFileChunk"] = "log.fileChunk",
        };

    /// <summary>客户端 → 服务端回调名（§4.2：只有两个名字）。</summary>
    public static class Callbacks
    {
        public const string Event = "evt";
        public const string State = "state";
    }

    /// <summary>
    /// 纯新增协议域的事件名（route-anchor）。
    ///
    /// 与 <see cref="LegacyEventMap"/> 刻意分开：映射表承载的是"旧事件名→evt 名"的双发兼容，
    /// 而 route-anchor 是全新域，从来没有旧客户端订阅者，因此只发 evt，
    /// 不进入双发表（避免污染"客户端订阅清单"的严格校验）。
    /// </summary>
    public static class Events
    {
        /// <summary>状态已变化（客户端收到后应立即查询权威快照）。</summary>
        public const string RouteAnchorChanged = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Changed;

        /// <summary>锚点放行：授权进入下一条路线。</summary>
        public const string RouteAnchorReleased = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Released;

        /// <summary>锚点停止：无法确认全员安全收口，整队停止。</summary>
        public const string RouteAnchorStopped = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.Stopped;

        /// <summary>定向 Pull 命令（只发给需要被拉回的成员，不是组广播）。</summary>
        public const string RouteAnchorPull = BetterGenshinImpact.Shared.RouteAnchor.RouteAnchorProtocol.PullEvent;

        /// <summary>
        /// 远端任务下发结果回执（server → 命令发起方定向投递）。
        /// 全新协议域：从来没有旧客户端订阅者，只发 evt，不进 LegacyEventMap 双发表；
        /// 旧发起方（/hub 连接）天然收不到，UI 按 15s 超时降级为"已发送（无回执）"。
        /// </summary>
        public const string ControlRemoteCommandResult = "control.remoteCommandResult";

        /// <summary>
        /// 协同中止广播（hoeing-multiplayer-coordinated-abort-restart）：任一成员上报真异常中止后
        /// 服务端房间级幂等广播（首报 wins），全组秒级同步收口。
        /// 全新协议域：全员强制升级，无旧客户端订阅者，只发 evt，不进 LegacyEventMap 双发表。
        /// </summary>
        public const string SyncCoordinatedAborted = "sync.coordinatedAborted";
    }
}
