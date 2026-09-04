using BgiCoordinatorServer.Gateway;
using BgiCoordinatorServer.Models;
using BgiCoordinatorServer.Services;
using Microsoft.AspNetCore.SignalR;

namespace BgiCoordinatorServer.Hubs;

/// <summary>
/// 旧协调 Hub（/hub）。迁移期双轨（《通信方案》§4.7）：65 个公开方法全部瘦身为
/// 3-5 行转发器，业务逻辑在 RoomOperations 共享路径（GatewayHub 路由同用）。
/// 方法签名、返回语义、事件名逐字节不变，旧客户端零感知。
/// </summary>
public class CoordinatorHub : Hub
{
    private readonly RoomOperations _ops;

    public CoordinatorHub(RoomOperations ops)
    {
        _ops = ops;
    }

    /// <summary>创建房间，返回房间码</summary>
    public async Task<string> CreateRoom(string playerName = "", List<string>? whitelist = null, string playerUid = "", int expectedPlayerCount = 4, string reportedVersion = "")
    {
        var (code, _) = await _ops.CreateRoomAsync(GatewayHandlerContext.Legacy(Context.ConnectionId),
            playerName, whitelist, playerUid, expectedPlayerCount, reportedVersion);
        return code;
    }

    /// <summary>加入房间，广播 PlayerListUpdated</summary>
    public async Task<bool> JoinRoom(string roomCode, string playerName = "", string playerUid = "", string reportedVersion = "")
    {
        var (success, _, _) = await _ops.JoinRoomAsync(GatewayHandlerContext.Legacy(Context.ConnectionId),
            roomCode, playerName, playerUid, reportedVersion);
        return success;
    }

    /// <summary>离开房间，广播 PlayerListUpdated</summary>
    public Task LeaveRoom()
        => _ops.LeaveRoomAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>上报路线清单，所有成员上报后对比 MD5，广播差异或验证通过</summary>
    public Task ReportRouteList(List<RouteHash> routes)
        => _ops.ReportRouteListAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), routes);

    /// <summary>
    /// 上报本玩家计划要执行的所有路线的变体 schema（route-variant-sync-by-logical-id spec / R6）。
    /// 服务端按 LogicalRouteId 分组比对所有玩家的 SyncPointList + TeleportSyncPointSequence。
    /// 全部一致 → 广播 RouteVariantConsistencyPassed；任一不一致 / 30s 超时 → 广播 RouteVariantConsistencyFailed。
    /// 全员 LogicalRouteId 均为空 → 跳过校验、不广播（老路径零回归 R6.7）。
    /// </summary>
    public Task ReportRouteVariantSchema(List<RouteVariantSchemaItem> items)
        => _ops.ReportRouteVariantSchemaAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), items);

    /// <summary>上报到达集合点，全员到达时广播 AllArrived</summary>
    public Task ReportArrival(string syncPointId)
        => _ops.ReportArrivalAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncPointId);

    /// <summary>
    /// 上报到达集合点（带预期人数），指定人数到达时广播 AllArrived
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="expectedCount">预期到达人数，0表示使用房间总人数</param>
    public Task ReportArrivalWithExpectedCount(string syncPointId, int expectedCount)
        => _ops.ReportArrivalWithExpectedCountAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncPointId, expectedCount);

    /// <summary>上报战斗完成，全员完成时广播 AllFightDone</summary>
    public Task ReportFightDone(string syncPointId)
        => _ops.ReportFightDoneAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncPointId);

    /// <summary>上报战斗参与者（multiplayer-shared-fight-end-quorum-sync spec，配额分母）</summary>
    public Task ReportFightParticipant(string syncKey)
        => _ops.ReportFightParticipantAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncKey);

    /// <summary>心跳，更新 LastHeartbeat</summary>
    public Task Heartbeat()
        => _ops.HeartbeatAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>带路线进度信息的心跳（需求 6）</summary>
    public Task HeartbeatWithProgress(int routeIndex, DateTime routeStartTime, double routeEstimatedSeconds)
        => _ops.HeartbeatWithProgressAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), routeIndex, routeStartTime, routeEstimatedSeconds);



    /// <summary>查询指定成员的路线进度（需求 6）</summary>
    public Task<MemberProgress?> GetMemberProgress(string playerUid)
        => _ops.GetMemberProgressAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), playerUid);



    /// <summary>关闭房间（仅房主可操作）</summary>
    public Task CloseRoom()
        => _ops.CloseRoomAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// （已废弃，保留空实现）旧客户端调用此方法时仅记 deprecated 警告，不影响协议兼容。
    /// kazuha-player-auto-detection: 替换为运行时声明协议 DeclareKazuhaCapability，由各客户端各自识别本地联机队伍是否含万叶并主动声明。
    /// </summary>
    public Task SetKazuhaPlayer(int index = 0)
        => _ops.SetKazuhaPlayerAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), index);

    /// <summary>
    /// 客户端声明本地联机队伍含万叶（kazuha-player-auto-detection）。
    /// 幂等：同一 ConnectionId 重复调用直接 return（lock 内做 Any 检查）。
    /// 选举：第一个声明者自动成为 KazuhaConnectionId，触发 KazuhaPlayerUpdated(playerUid) 广播。
    /// 后续声明者仅入候选列表，断线时按列表顺序顶替。
    /// </summary>
    public Task DeclareKazuhaCapability()
        => _ops.DeclareKazuhaCapabilityAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    // ====== 万叶聚物同步（multiplayer-kazuha-collect-sync）======

    /// <summary>
    /// 万叶玩家广播"开始执行聚物动作"。仅记录 + 广播，不做终态守卫。
    /// multiplayer-kazuha-collect-point-broadcast: 增加 syncKey + 聚物点 (collectX, collectY) 三参。
    /// hoeing-kazuha-collect-drop-terminal-signal: 不再写 room.KazuhaCollect.CurrentCollectPoint（字段已删）；
    /// 广播始终携带 4 参，无效坐标用 NaN 透传，由客户端 IsValid 守卫过滤。
    /// 注意：SignalR 不支持 hub 方法重载，老客户端调 0-参 InvokeAsync 会因 routing 失败
    /// 抛 HubException → 客户端 try/catch 静默 → 走退化路径（不上报聚物点）。
    /// 部署顺序：先服务端、后客户端，最大化平滑过渡。
    /// </summary>
    public Task NotifyKazuhaCollectStarted(string syncKey, double collectX, double collectY)
        => _ops.NotifyKazuhaCollectStartedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncKey, collectX, collectY);

    /// <summary>上报路线验证完成，全员完成时广播 RouteVerificationAllDone</summary>
    public Task ReportRouteVerificationDone()
        => _ops.ReportRouteVerificationDoneAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>上报本机达经验上限，全员达上限时广播 AllReachedExpCap。multiplayer-hoeing-exp-cap-stop</summary>
    public Task ReportExpCapReached()
        => _ops.ReportExpCapReachedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>撤回本机达经验上限（又见经验）。multiplayer-hoeing-exp-cap-stop</summary>
    public Task ReportExpCapCleared()
        => _ops.ReportExpCapClearedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>成员上报团队 arming（本机吃到经验，或连续 5 场无经验兜底）。置 ExpCapArmed=true；
    /// 若 arming 后已满足全员上报（全员满级兜底场景）则补广播 AllReachedExpCap。multiplayer-hoeing-exp-cap-stop R7</summary>
    public Task ReportExpArmed()
        => _ops.ReportExpArmedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// 上报"连续2场无经验预警"（exp-cap-prefinal-stop-by-two-noexp）。
    /// 客户端连续 2 场无经验时调用，服务端将 connectionId 加入 TwoConsecutiveNoExpSet。
    /// 若 arming ∧ 全员 ∈ (ExpCapReachedSet ∪ TwoConsecutiveNoExpSet) → 广播 AllReachedExpCap。
    /// 旧服务端无此方法 → 客户端 HubException 被静默吞掉 → 退化为 4-threshold 行为。
    /// </summary>
    public Task ReportTwoConsecutiveNoExp()
        => _ops.ReportTwoConsecutiveNoExpAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// 撤回"连续2场无经验预警"（又见经验）。exp-cap-prefinal-stop-by-two-noexp。
    /// </summary>
    public Task ReportTwoConsecutiveNoExpCleared()
        => _ops.ReportTwoConsecutiveNoExpClearedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>更新白名单（仅房主）</summary>
    public Task UpdateWhitelist(List<string>? whitelist = null)
        => _ops.UpdateWhitelistAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), whitelist);

    /// <summary>获取在线房间列表</summary>
    public Task<List<RoomSummary>> GetOnlineRooms()
        => _ops.GetOnlineRoomsAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>房主上传锄地配置</summary>
    public Task SetRoomConfig(RoomConfig config)
        => _ops.SetRoomConfigAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), config);

    /// <summary>成员拉取房主锄地配置</summary>
    public Task<RoomConfig?> GetRoomConfig()
        => _ops.GetRoomConfigAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>房主上报已进入等待状态</summary>
    public Task ReportHostReady()
        => _ops.ReportHostReadyAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>查询房主是否就绪</summary>
    public Task<bool> IsHostReady()
        => _ops.IsHostReadyAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// 房主调用此方法把房间标记为已开锄（spec lock-room-after-start §2）。
    /// 服务端从此 JoinRoom 拒绝非重连新玩家、GetOnlineRooms 也不再返回此房间。
    /// 鉴权：Context.ConnectionId 必须等于 room.HostConnectionId。
    /// 幂等：重复调用直接 return（room.IsStarted 一旦 true 在房间销毁前不复位）。
    /// 非房主调用：LogWarning + return，不抛异常、不修改状态。
    /// </summary>
    public Task MarkRoomStarted()
        => _ops.MarkRoomStartedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), null);

    /// <summary>
    /// 房主重开续跑：上报已完成房主 UID 集合，服务端据此裁剪权威轮换序列。
    /// 旧服务端无此方法 → 客户端 HubException 降级 → 等价 MarkRoomStarted()（全量序列）。
    /// hoeing-multiworld-host-restart-resume-round Req 1.1 / 6.1。
    /// </summary>
    public Task MarkRoomStartedWithProgress(List<string> completedHostUids)
        => _ops.MarkRoomStartedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), completedHostUids);

    /// <summary>返回本房间权威轮换序列（UID 列表）。未生成 / 房间不存在 → 空列表。</summary>
    public Task<List<string>> GetRoundHostOrder()
        => _ops.GetRoundHostOrderAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>房主上传最终路线列表，并广播通知成员</summary>
    public Task SetHostRouteList(List<string> routeNames)
        => _ops.SetHostRouteListAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), routeNames);

    /// <summary>成员拉取房主路线列表</summary>
    public Task<List<string>> GetHostRouteList()
        => _ops.GetHostRouteListAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// 查询房主是否已上传过路线列表（含上传空列表）。
    /// multiplayer-host-empty-route-member-wait-timeout-fix：成员据此区分
    /// "房主从未上传"（false → 继续等待）与"房主上传了空列表"（true + 列表空 → 优雅跳过本轮）。
    /// </summary>
    public Task<bool> IsHostRouteListUploaded()
        => _ops.IsHostRouteListUploadedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// 原子返回房主路线列表状态：(Uploaded, RouteNames) 同一时刻快照。
    /// multiplayer-member-skip-round-stuck-roundend-sync-fix：取代成员侧
    /// GetHostRouteList + IsHostRouteListUploaded 两次独立查询，消除 TOCTOU 竞态
    /// （房主在两次查询之间 SetHostRouteList(非空) 导致成员拿到 uploaded=true+count=0 误判跳过）。
    /// lock(room) 与 SetHostRouteList 写侧互斥，并复制列表快照，确保读到的 Uploaded 与 RouteNames
    /// 来自同一时刻、且返回后不被房主并发改动。
    /// </summary>
    public Task<HostRouteListStatus> GetHostRouteListStatus()
        => _ops.GetHostRouteListStatusAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>上报已加入世界，全员加入时广播 AllWorldJoined</summary>
    public Task ReportWorldJoined()
        => _ops.ReportWorldJoinedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>获取已加入世界的人数</summary>
    public Task<int> GetWorldJoinedCount()
        => _ops.GetWorldJoinedCountAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>重置已加入世界的记录（多世界模式新轮次开始时调用）</summary>
    public Task ResetWorldJoined()
        => _ops.ResetWorldJoinedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));



    public override async Task OnDisconnectedAsync(Exception? exception)
    {
        // 断线清理与 GatewayHub 共用同一实现（RoomOperations.HandleDisconnectAsync）：
        // 宽限期/房主关房/同步点重评估/万叶顶替/控制房间清理/日志订阅清理
        await _ops.HandleDisconnectAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), exception);
        await base.OnDisconnectedAsync(exception);
    }

    /// <summary>
    /// 等待点上报（multiplayer-abnormal-wait-coordination 重构）
    /// 玩家跳过线路并在同步点等待时调用
    /// 服务端验证等待点格式、计算统一等待点、广播给所有正常玩家
    /// </summary>
    /// <param name="routeId">路线ID</param>
    /// <param name="syncPointId">同步点ID</param>
    /// <param name="worldRound">世界轮次</param>
    public Task WaitPointReport(string routeId, string syncPointId, int worldRound)
        => _ops.WaitPointReportAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), routeId, syncPointId, worldRound);

    /// <summary>
    /// 多轮世界重置（multiplayer-abnormal-wait-coordination 重构）
    /// 多轮世界新轮次开始时调用，清理所有等待点状态和异常状态
    /// </summary>
    public Task ResetForNewWorldRound(int newRound)
        => _ops.ResetForNewWorldRoundAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), newRound);

    /// <summary>
    /// 到达等待点上报（multiplayer-abnormal-wait-coordination 需求 5）
    /// 正常玩家到达统一等待点时调用，服务端记录到达状态并在全员到达时广播
    /// </summary>
    /// <param name="syncPointId">同步点ID</param>
    public Task ReportArrivalAtWaitPoint(string syncPointId)
        => _ops.ReportArrivalAtWaitPointAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncPointId);

    /// <summary>
    /// 清除异常状态（需求 5.3, 5.5）
    /// 异常玩家恢复正常后调用，服务端更新状态并广播
    /// </summary>
    public Task ClearAbnormalStatus()
        => _ops.ClearAbnormalStatusAsync(GatewayHandlerContext.Legacy(Context.ConnectionId));

    /// <summary>
    /// 接收玩家异常通知并广播给房间内其他玩家（multiplayer-abnormal-sync-server spec）
    /// Validates: Requirements REQ-1.1, REQ-1.2, REQ-1.3, REQ-1.4, REQ-3.2, REQ-3.3
    /// </summary>
    public Task PlayerAnomalyNotify(string playerUid, int routeIndex, bool passedSyncPoint)
        => _ops.PlayerAnomalyNotifyAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), playerUid, routeIndex, passedSyncPoint);

    /// <summary>
    /// <summary>
    /// 接收"复苏者附带战斗点"的异常通知并广播（hoeing-route-retry-round-end-refactor v3）。
    /// 纯透传：不解析 fightPointId、不进 AbnormalPlayerInfos（区别于既有 PlayerAnomalyNotify）。
    /// 供客户端做"只跳过复苏那一个战斗点"（requirements.md §9 EB-v3-1 / design.md §9.1）。
    /// </summary>
    public Task PlayerAnomalyNotifyFightPoint(string playerUid, int routeIndex, int fightPointId)
        => _ops.PlayerAnomalyNotifyFightPointAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), playerUid, routeIndex, fightPointId);

    /// 接收玩家异常恢复通知并广播给房间内其他玩家（multiplayer-abnormal-sync-server spec）
    /// Validates: Requirements REQ-2.1, REQ-2.2, REQ-3.4
    /// </summary>
    public Task PlayerAnomalyRecovered(string playerUid)
        => _ops.PlayerAnomalyRecoveredAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), playerUid);

    /// <summary>
    /// 更新成员状态。
    /// 当玩家上报 Reviving/Rejoining 时，标记为异常并重新评估 ArrivalSets；
    /// 当玩家上报 Normal 时，清除异常标记。
    /// targetProgress：异常玩家的目标进度值，用于判定其他玩家在某同步点是否需要等他。
    /// </summary>
    public Task MemberStatusChanged(string playerUid, string status, long targetProgress = -1)
        => _ops.MemberStatusChangedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), playerUid, status, targetProgress);

    /// <summary>
    /// 客户端在跳路线后立即广播自己的新进度（multiplayer-sync-skip-by-progress §2.4）。
    /// 服务端更新对应玩家的 CurrentProgress = routeIndex * 1_000_000，
    /// 并触发对房间所有 ArrivalSets 的全量重评估（与 MemberStatusChanged / WaitForAllPlayers 同一机制）。
    ///
    /// 鉴权（OQ-2 方案 A）：用 Context.ConnectionId 定位本连接对应的玩家，
    ///   校验 player.PlayerUid == playerUid（playerUid 非空时）。不一致直接 LogWarning + return。
    /// 兼容性：旧客户端不调用此方法即可，新增 Hub 方法不破坏旧协议。
    /// </summary>
    public Task ReportMemberProgress(string playerUid, int routeIndex)
        => _ops.ReportMemberProgressAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), playerUid, routeIndex);

    /// <summary>
    /// 记录路线跳过
    /// </summary>
    public Task RouteSkipped(string playerUid, int routeIndex)
        => _ops.RouteSkippedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), playerUid, routeIndex);

    /// <summary>
    /// 记录等待点到达
    /// </summary>
    public Task WaitPointReached(string playerUid, string syncPointId)
        => _ops.WaitPointReachedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), playerUid, syncPointId);

    /// <summary>
    /// 更新战斗状态
    /// </summary>
    public Task FightingStatusChanged(string playerUid, bool isFighting)
        => _ops.FightingStatusChangedAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), playerUid, isFighting);

    /// <summary>
    /// 等待所有玩家到达指定同步点（非阻塞模式：记录到达 → 检查条件 → 广播 → 立即返回）
    /// 客户端通过本地 TCS + AllArrived 事件等待，服务端不阻塞 SignalR 连接。
    /// 
    /// 判定规则（基于全局进度值）：
    ///   对每个异常玩家 P：
    ///     P.TargetProgress == syncProgress → P 正要去 X → 等他
    ///     P.TargetProgress != syncProgress → P 跳过了 X 或不会到 X → 不等他
    ///   对每个正常玩家 P（multiplayer-sync-skip-by-progress §2.1）：
    ///     P.CurrentProgress > syncProgress → P 已穿过此同步点 → 不等他
    ///     否则 → 等他
    ///
    /// 进度更新后回头重评估（multiplayer-sync-skip-by-progress §2.3）：
    ///   syncProgress >= 0 时 caller.CurrentProgress 被刷新，房间内其他历史 ArrivalSets
    ///   可能因 caller 被新豁免逻辑剔除而满足放行条件，需用 CollectSatisfiedSyncsLocked
    ///   全量评估后批量广播 AllArrived。
    /// </summary>
    public Task WaitForAllPlayers(string syncId, long syncProgress = -1)
        => _ops.WaitForAllPlayersAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), syncId, syncProgress);

    // =========================================================================
    // 控制房间（multiplayer-hoeing-assistant）— 远程控制
    // =========================================================================

    /// <summary>
    /// 加入控制房间。校验密码 + UID 白名单，成功后加入 CTRL_{roomCode} Group
    /// </summary>
    public Task JoinControlRoom(string roomCode, string password, string playerUid, string playerName, List<string>? allowedUids = null, bool isRemote = false, string clientInstanceId = "")
        => _ops.JoinControlRoomAsync(GatewayHandlerContext.Legacy(Context.ConnectionId),
            roomCode, password, playerUid, playerName, allowedUids, isRemote, clientInstanceId);

    /// <summary>
    /// 向控制房间成员转发远程命令。目标离线时缓存，上线后自动下发。
    /// </summary>
    public Task SendRemoteCommand(RemoteCommand command)
        => _ops.SendRemoteCommandAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), command);

    /// <summary>
    /// 成员上报自身 BGI 状态与可用配置列表，服务端更新后广播最新成员列表。
    /// </summary>
    public Task ReportControlStatus(ControlStatus status)
        => _ops.ReportControlStatusAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), status);

    /// <summary>
    /// 成员截图汇聚（嘟嘟可 P5 / 远程成员巡检墙）：成员端助手每 10s 上报一帧 JPEG 缩略图（base64），
    /// 服务端校验连接确实在对应 CTRL_ 控制房间后纯转发给房间内所有成员（不做服务端存储）。
    /// 限流：同 uid 10 秒内只转发一帧，超出丢弃。
    /// </summary>
    public Task ReportMemberScreenshot(string roomCode, string uid, string jpegBase64, int width, int height, DateTime capturedAt)
        => _ops.ReportMemberScreenshotAsync(GatewayHandlerContext.Legacy(Context.ConnectionId),
            roomCode, uid, jpegBase64, width, height, capturedAt);

    /// <summary>
    /// 房间实时日志汇聚：成员端助手每 500ms 合批上报本机 BGI 实时日志行（已渲染为文本行），
    /// 服务端校验连接确实在对应 CTRL_ 控制房间后纯转发给房间内所有成员（不做服务端存储/不过滤内容）。
    /// 限流：同 uid 每秒最多 4 批（正常节奏 500ms 一批，留一倍余量）；负载上限：单批 500 行 / 256KB 字符。
    /// infoOnly：发送端开启省流（仅 INF+）的标志，透传给观看端做状态提示。
    /// </summary>
    public Task ReportMemberLogBatch(string roomCode, string uid, string senderName, List<string> lines, bool infoOnly)
        => _ops.ReportMemberLogBatchAsync(GatewayHandlerContext.Legacy(Context.ConnectionId),
            roomCode, uid, senderName, lines, infoOnly);

    /// <summary>
    /// 订阅某成员的实时日志流（房间日志汇聚·观众驱动：没人订阅时目标端零上报零流量）。
    /// 目标端收到 MemberLogSubscribersChanged(count)，只需要知道"有几个人在看"。幂等。
    /// </summary>
    public Task SubscribeMemberLog(string roomCode, string targetUid)
        => _ops.SubscribeMemberLogAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), roomCode, targetUid);

    /// <summary>退订某成员的实时日志流。未订阅过时静默。</summary>
    public Task UnsubscribeMemberLog(string roomCode, string targetUid)
        => _ops.UnsubscribeMemberLogAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), roomCode, targetUid);

    // ========== 远程成员完整日志下载（按需请求-应答，纯转发不存储） ==========

    /// <summary>
    /// 观众端请求目标成员的日志文件列表 → 单播目标端 MemberLogFilesRequested(requesterUid, requestId)。
    /// 目标不在线时静默丢弃（观众端超时兜底提示）。
    /// </summary>
    public Task RequestMemberLogFiles(string roomCode, string targetUid, string requestId)
        => _ops.RequestMemberLogFilesAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), roomCode, targetUid, requestId);

    /// <summary>
    /// 目标端应答日志文件列表 → 按 requestId 映射<b>单播</b>请求方（转发后即删映射）。
    /// 项数超上限截断；文件名不合白名单的项剔除（双保险，助手端已校验）。映射不存在（过期/伪造）直接丢弃。
    /// </summary>
    public Task ReportMemberLogFiles(string roomCode, string uid, string requestId, List<MemberLogFileDescriptor> files)
        => _ops.ReportMemberLogFilesAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), roomCode, uid, requestId, files);

    /// <summary>
    /// 观众端请求下载目标成员的某个日志文件 → 单播目标端 MemberLogDownloadRequested(requesterUid, requestId, fileName)。
    /// fileName 必须过白名单（防目录穿越），否则直接拒绝。
    /// </summary>
    public Task RequestMemberLogDownload(string roomCode, string targetUid, string requestId, string fileName)
        => _ops.RequestMemberLogDownloadAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), roomCode, targetUid, requestId, fileName);

    /// <summary>
    /// 目标端分块上行日志文件（gzip+base64）→ 按 requestId 映射<b>单播</b>请求方（映射不存在=过期/伪造，丢弃）。
    /// 限流：数据块同 uid 每秒 ≤30；忙标记块（totalChunks=0 且 done=true）独立阈值同 uid 每秒 ≤5，防洪泛。
    /// 总块数 ≤400。done=true 块转发后删除映射（下载结束清理）。
    /// </summary>
    public Task ReportMemberLogChunk(string roomCode, string uid, string requestId,
        int seq, int totalChunks, string chunkBase64, string fileName, bool done)
        => _ops.ReportMemberLogChunkAsync(GatewayHandlerContext.Legacy(Context.ConnectionId),
            roomCode, uid, requestId, seq, totalChunks, chunkBase64, fileName, done);

    /// <summary>清除指定成员的 OnlineHistory（已联机记录）。由本人或房主调用。</summary>
    public Task ClearOnlineHistory(string targetUid)
        => _ops.ClearOnlineHistoryAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), targetUid);

    /// <summary>上报上线事件（带 generation 代序号）。由 ReportOnlineEvent 端点统一处理就绪检查。</summary>
    public Task ReportOnlineEvent(int generation, bool isOnlineReady)
        => _ops.ReportOnlineEventAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), generation, isOnlineReady);

    /// <summary>客户端确认收到 AllReadyConfirm。由客户端收到 AllReadyConfirm 事件后调用。</summary>
    public Task ConfirmAllReady(int generation)
        => _ops.ConfirmAllReadyAsync(GatewayHandlerContext.Legacy(Context.ConnectionId), generation);
}
