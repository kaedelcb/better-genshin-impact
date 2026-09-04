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
    public static readonly string[] ServerCapabilities = ["gateway.envelope.v3"];

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

        public const string LogReportBatch = "log.reportBatch";
        public const string LogSubscribe = "log.subscribe";
        public const string LogUnsubscribe = "log.unsubscribe";
        public const string LogRequestFiles = "log.requestFiles";
        public const string LogReportFiles = "log.reportFiles";
        public const string LogRequestDownload = "log.requestDownload";
        public const string LogReportChunk = "log.reportChunk";

        public const string ScreenshotReport = "screenshot.report";
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
            ["RouteEnforceSync"] = "route.enforceSync",
            // 控制房间事件
            ["ControlRoomPlayersUpdated"] = "control.playersUpdated",
            ["RemoteCommand"] = "control.remoteCommand",
            ["RemoteCommandAck"] = "control.remoteCommandAck",
            ["JoinRejected"] = "control.joinRejected",
            ["AllReady"] = "control.allReady",
            ["AllReadyConfirm"] = "control.allReadyConfirm",
            // 日志三件套 + 截图
            ["MemberScreenshot"] = "screenshot.member",
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
}
