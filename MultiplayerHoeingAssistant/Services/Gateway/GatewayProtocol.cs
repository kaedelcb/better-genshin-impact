namespace MultiplayerHoeingAssistant.Services.Gateway;

/// <summary>
/// 网关协议常量（MHA 客户端侧）。
/// 数据抄自 BgiCoordinatorServer/Gateway/GatewayProtocol.cs（**以服务器为唯一权威，改动须双向同步**）；
/// 只收录助手控制房间平面用到的消息名与事件名（锄地房间平面属 BGI 侧，不收）。
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
        public const string ScreenshotRequest = "screenshot.request";
        public const string ScreenshotReportEx = "screenshot.reportEx";
    }

    /// <summary>
    /// 服务端 → 客户端 evt 事件名（对应服务器 LegacyEventMap 的映射目标值）。
    /// SignalRClient.DispatchEvt 按这些名字分发到 13 个 C# 事件。
    /// </summary>
    public static class Events
    {
        public const string ControlPlayersUpdated = "control.playersUpdated";       // ← ControlRoomPlayersUpdated
        public const string ControlRemoteCommand = "control.remoteCommand";         // ← RemoteCommand
        public const string ControlJoinRejected = "control.joinRejected";           // ← JoinRejected
        public const string ControlAllReady = "control.allReady";                   // ← AllReady
        public const string ControlAllReadyConfirm = "control.allReadyConfirm";     // ← AllReadyConfirm

        public const string ScreenshotMember = "screenshot.member";                 // ← MemberScreenshot
        public const string ScreenshotRequested = "screenshot.requested";           // ← MemberScreenshotRequested

        public const string LogBatch = "log.batch";                                 // ← MemberLogBatch
        public const string LogSubscribersChanged = "log.subscribersChanged";       // ← MemberLogSubscribersChanged
        public const string LogFilesRequested = "log.filesRequested";               // ← MemberLogFilesRequested
        public const string LogFileList = "log.fileList";                           // ← MemberLogFileList
        public const string LogDownloadRequested = "log.downloadRequested";         // ← MemberLogDownloadRequested
        public const string LogFileChunk = "log.fileChunk";                         // ← MemberLogFileChunk
    }
}
