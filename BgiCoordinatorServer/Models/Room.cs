namespace BgiCoordinatorServer.Models;

public class Room
{
    public string Code { get; set; } = "";
    public string HostConnectionId { get; set; } = "";

    /// <summary>房间基准版本：房主 CreateRoom 时上报的完整 Reported_Version 字符串。
    /// 加入校验以此为参照（version-compatibility-check）。空串=房主旧客户端未上报。</summary>
    public string HostBaselineVersion { get; set; } = "";

    public List<PlayerInfo> Players { get; set; } = [];
    public DateTime CreatedAt { get; set; }

    /// <summary>断线宽限期：connectionId → 过期时间。成员 SignalR 断线后不立即删人，宽限期内重连复用。</summary>
    public Dictionary<string, DateTime> GracePendingMembers { get; set; } = new();

    /// <summary>syncPointId → 已到达的 connectionId 集合</summary>
    public Dictionary<string, HashSet<string>> ArrivalSets { get; set; } = [];

    /// <summary>
    /// 本轮已广播过 AllArrived 的 syncId 集合（fastsync-claim-short-circuit-premature-release-fix / OQ-1=a）。
    /// 每次广播 AllArrived 并 ClearArrivalSet 时加入；当某玩家调 WaitForAllPlayers(syncId) 而该 syncId
    /// 已在此集合中，说明该 syncId 本轮确已全员放行过，对该调用方单独补发 AllArrived 解锁
    /// （晚到抢报方错过了 Clients.Group 广播）。
    /// per-room 运行时状态（非配置，不进 RoomConfig）；多世界轮换在 ResetForNewWorldRound 清空，
    /// 避免下一轮同名 syncId 残留误放。
    /// </summary>
    public HashSet<string> BroadcastedSyncIds { get; set; } = [];

    /// <summary>syncPointId → 已完成战斗的 connectionId 集合</summary>
    public Dictionary<string, HashSet<string>> FightDoneSets { get; set; } = [];

    /// <summary>syncKey → 战斗参与者 connectionId 集合（multiplayer-shared-fight-end-quorum-sync spec，配额分母）</summary>
    public Dictionary<string, HashSet<string>> FightParticipantSets { get; set; } = [];

    /// <summary>
    /// 已广播过 AllFightDone 的 syncKey 集合（multiplayer-shared-fight-end-quorum-sync spec design §11.3，轮终标志）。
    /// 广播达成时加入；下一拨用同一战斗点的玩家上报参与者/投票时，发现含此 syncKey 即触发
    /// FightParticipantSets/FightDoneSets 周期清空，开启新一轮，消除上一轮残留污染（D2）。
    /// 多世界轮换 ResetForNewWorldRound 一并清空。
    /// </summary>
    public HashSet<string> FightDoneBroadcasted { get; set; } = [];

    /// <summary>
    /// 已上报"达经验上限"的 connectionId 集合（multiplayer-hoeing-exp-cap-stop）。
    /// 可增可减：ReportExpCapReached 加入、ReportExpCapCleared（撤回）移除。
    /// 与 RouteVerificationDoneSet 同构的在线清理。多世界轮换 ResetForNewWorldRound 清空。
    /// </summary>
    public HashSet<string> ExpCapReachedSet { get; set; } = [];

    /// <summary>
    /// 本轮世界是否已广播过 AllReachedExpCap（幂等标志，multiplayer-hoeing-exp-cap-stop）。
    /// ResetForNewWorldRound 复位 false。
    /// </summary>
    public bool ExpCapBroadcasted { get; set; } = false;

    /// <summary>
    /// 本轮世界团队是否已 arming（任意成员吃到经验，或连续 5 场无经验兜底触发）。
    /// 广播 AllReachedExpCap 的必要条件之一：仅当 ExpCapArmed==true 且全员上报达上限才广播。
    /// 防"重启空线路误停"（全员没吃过经验就连续无经验 → 未 arming → 不广播）。
    /// ResetForNewWorldRound 复位 false。multiplayer-hoeing-exp-cap-stop R7.3。
    /// </summary>
    public bool ExpCapArmed { get; set; } = false;
    /// <summary>控制房间是否已广播过 AllReady（幂等标志）。主机游戏确认后置 true，有人下线后复位 false。</summary>
    public bool AllReadyBroadcasted { get; set; } = false;

    /// <summary>
    /// 已上报"连续2场无经验预警"的 connectionId 集合（exp-cap-prefinal-stop-by-two-noexp）。
    /// 与 ExpCapReachedSet 同构：可增可减（Report 加入 / Clear 移除）。
    /// 广播条件：ExpCapArmed ∧ 全员 ∈ (ExpCapReachedSet ∪ TwoConsecutiveNoExpSet)。
    /// ResetForNewWorldRound 清空。
    /// </summary>
    public HashSet<string> TwoConsecutiveNoExpSet { get; set; } = [];

    /// <summary>房主筛选后的最终路线文件名列表（按执行顺序）</summary>
    public List<string> HostRouteList { get; set; } = [];

    /// <summary>
    /// 房主是否已上传过路线列表（含上传空列表的情况）。
    /// multiplayer-host-empty-route-member-wait-timeout-fix：用于区分
    /// "房主从未上传"（默认 false）与"房主上传了空列表（CD全过滤）"（true + HostRouteList 为空）。
    /// 成员据此在收到空列表时判断应优雅跳过本轮还是继续等待。
    /// SetHostRouteList 置 true；多世界轮换 ResetForNewWorldRound 重置为 false。
    /// </summary>
    public bool HostRouteListUploaded { get; set; } = false;

    /// <summary>房间白名单</summary>
    public List<string> Whitelist { get; set; } = [];

    /// <summary>已完成路线验证的 connectionId 集合</summary>
    public HashSet<string> RouteVerificationDoneSet { get; set; } = [];

    /// <summary>已加入世界的 connectionId 集合</summary>
    public HashSet<string> WorldJoinedSet { get; set; } = [];

    /// <summary>房间期望人数</summary>
    public int ExpectedPlayerCount { get; set; } = 4;

    /// <summary>房主锄地配置</summary>
    public RoomConfig? HostConfig { get; set; }

    /// <summary>房主是否已进入等待状态</summary>
    public bool HostReady { get; set; } = false;

    /// <summary>
    /// 房间是否已开锄。房主调用 MarkRoomStarted 后置 true，从此 JoinRoom 拒绝非重连新玩家。
    /// 一旦 true 在房间销毁前不复位（多世界轮换由新房间天然 IsStarted=false 承担解锁）。
    /// 不进入 RoomSummary（GetOnlineRooms 已在服务端做完过滤，前端无须感知）。
    /// 详见 spec lock-room-after-start。
    /// </summary>
    public bool IsStarted { get; set; } = false;

    /// <summary>当前世界轮次（多轮世界支持）</summary>
    public int CurrentWorldRound { get; set; } = 0;

    /// <summary>
    /// 多世界权威轮换序列：UID 列表，第 i 项 = 第 i 轮房主 UID。
    /// 首任房主 MarkRoomStarted 时（首轮全员已在房间）基于 Players 生成一次
    /// （首项=首任房主，其余 UID 升序），整场只确定一次，房间销毁随之消失。
    /// 空 = 未生成（单世界 / 旧流程 / 非首轮房间未触发）。
    /// 客户端 RunMultiWorldAsync 查询此序列构造 playerOrder，保证各端轮换序列一致。
    /// multiplayer-server-authoritative-round-order。
    /// </summary>
    public List<string> RoundHostOrder { get; set; } = [];

    /// <summary>玩家等待点上报缓存：playerUid → WaitPointReport</summary>
    public Dictionary<string, WaitPointReport> WaitPoints { get; set; } = [];

    /// <summary>协调后的统一等待点</summary>
    public CoordinatedWaitPoint? CoordinatedWaitPoint { get; set; }

    // === 异常等待协调机制字段（multiplayer-abnormal-wait-coordination spec）===
    // Validates: Requirements 1.1, 1.3, 1.4

    /// <summary>
    /// 玩家异常状态：playerUid → AbnormalPlayerState
    /// 服务端维护所有玩家的异常状态，用于计算统一等待点
    /// </summary>
    public Dictionary<string, AbnormalPlayerState> AbnormalPlayerStates { get; set; } = new();

    /// <summary>
    /// 当前统一等待点（服务端计算）
    /// 指示正常玩家应在何处等待异常玩家
    /// </summary>
    public UnifiedWaitPoint? CurrentUnifiedWaitPoint { get; set; }

    /// <summary>
    /// 等待点到达记录：syncPointId → 已到达的 playerUid 集合
    /// 用于追踪哪些玩家已到达统一等待点
    /// </summary>
    public Dictionary<string, HashSet<string>> WaitPointArrivals { get; set; } = new();

    // === 异常中断重对齐机制字段（multiplayer-abort-and-realign spec）===

    /// <summary>
    /// 当前重对齐流程（null 表示没有进行中的重对齐）
    /// 当检测到异常玩家时创建，所有玩家对齐完成后清除
    /// </summary>
    public RealignProcess? CurrentRealignProcess { get; set; }
    
    // === 联机锄地异常同步机制字段（multiplayer-abnormal-sync-server spec）===
    // Validates: Requirements REQ-3.1

    /// <summary>
    /// 异常玩家状态：playerUid → AbnormalPlayerInfo
    /// 用于联机锄地场景下追踪异常玩家状态
    /// </summary>
    public Dictionary<string, AbnormalPlayerInfo> AbnormalPlayerInfos { get; set; } = new();
    
    // === 强制线路同步机制字段（multiplayer-route-enforcement spec）===
    
    /// <summary>
    /// 是否启用强制线路同步（默认启用）
    /// 启用后服务器会定期检测线路偏差并强制同步
    /// </summary>
    public bool RouteEnforcementEnabled { get; set; } = true;
    
    /// <summary>
    /// 线路偏差阈值（默认 1）
    /// 当玩家之间线路索引差异超过此阈值时触发强制同步
    /// </summary>
    public int RouteEnforcementThreshold { get; set; } = 1;

    // === 万叶聚物同步机制字段（multiplayer-kazuha-collect-sync + kazuha-player-auto-detection）===
    /// <summary>
    /// 万叶聚物候选玩家列表，按 SignalR 调用到达顺序保存所有声明过候选身份的玩家。
    /// 由 CoordinatorHub.DeclareKazuhaCapability 追加；OnDisconnectedAsync 移除断线者。
    /// 第一个候选默认成为当前 Kazuha；当前 Kazuha 断线时按列表顺序选下一个仍在线者接管。
    /// </summary>
    public List<KazuhaCandidate> KazuhaCandidates { get; set; } = [];

    /// <summary>
    /// 万叶聚物同步房间状态：跟踪当前周期内已到达战斗点的玩家、终态广播守卫等。
    /// 设计见 design.md "Data Models §2 服务端房间维度状态"。
    /// </summary>
    public KazuhaCollectRoomState KazuhaCollect { get; set; } = new();

    // === 集体卡死监测字段（multiplayer-mutual-wait-collective-skip spec）===
    // Validates: Requirements 2.1 / 2.8 / 3.10

    /// <summary>
    /// 当前 ArrivalSets 快照（深拷贝），由 MutualWaitMonitor 在 piggyback 评估时
    /// 与 room.ArrivalSets 比对：相同则保持 ObservationStartTime 不动，不同则刷新快照与时刻。
    /// 多世界轮换 ResetForNewWorldRound 内置 null。
    /// </summary>
    public Dictionary<string, HashSet<string>>? LastArrivalSetsSnapshot { get; set; }

    /// <summary>
    /// 当前 ArrivalSets 快照开始稳定的 UTC 时刻，配合 LastArrivalSetsSnapshot 使用。
    /// EvaluateCollectiveStuckTimerCallbackAsync 中检查 (Now - ObservationStartTime) >= MutualWaitStableSeconds。
    /// </summary>
    public DateTime ObservationStartTime { get; set; }

    /// <summary>
    /// per-room 倒计时 Timer（OQ-2 C 混合方案的兜底定时器）。
    /// 在 LastArrivalSetsSnapshot 刷新时 Dispose+重建，到期后调 EvaluateCollectiveStuckTimerCallbackAsync。
    /// 多世界轮换重置时 Dispose + 置 null。
    /// </summary>
    public System.Threading.Timer? CollectiveSkipTimer { get; set; }

    /// <summary>
    /// 连续触发协同跳段计数器；每次成功触发 EvaluateCollectiveStuckTimerCallbackAsync + 广播后 +1。
    /// 达到 MaxConsecutiveCollectiveSkips 触发降级（OQ-5 A）。
    /// 多世界轮换 ResetForNewWorldRound 内归 0。
    /// </summary>
    public int ConsecutiveCollectiveSkipCount { get; set; } = 0;
}

/// <summary>
/// 万叶聚物候选玩家（kazuha-player-auto-detection）。
/// 客户端识别本地联机队伍含万叶后，调用 DeclareKazuhaCapability 追加到 Room.KazuhaCandidates。
/// </summary>
public class KazuhaCandidate
{
    public string ConnectionId { get; set; } = "";
    public string PlayerUid { get; set; } = "";
}

/// <summary>
/// 单个房间内"万叶聚物同步"的服务端状态。
/// hoeing-kazuha-collect-drop-terminal-signal: 砍终态信号闭环后，本类只剩 KazuhaConnectionId 一个字段——
/// 用于 NotifyKazuhaCollectStarted 坐标广播鉴权（仅当前万叶可广播聚物点）。
/// 原终态守卫字段（CurrentCycleId / ArrivedAtFightPoint / TerminalBroadcasted / TerminalKind /
/// CurrentSyncKey / CurrentCollectPoint）已随 NotifyKazuhaArrivedAtFightPoint / Finished / Skipped 一并删除。
/// </summary>
public class KazuhaCollectRoomState
{
    /// <summary>
    /// 当前周期"万叶玩家"的 ConnectionId。
    /// kazuha-player-auto-detection: 由 KazuhaCandidates 按声明先后顺序选第一个在线者填充；
    /// 当前 Kazuha 断线时切换到下一个在线候选；候选耗尽时置 null。
    /// hoeing-kazuha-collect-drop-terminal-signal: 砍终态信号闭环后，本类只剩此字段——
    /// 用于 NotifyKazuhaCollectStarted 坐标广播鉴权（仅当前万叶可广播聚物点）。
    /// </summary>
    public string? KazuhaConnectionId { get; set; }
}
