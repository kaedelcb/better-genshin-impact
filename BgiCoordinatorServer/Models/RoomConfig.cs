namespace BgiCoordinatorServer.Models;

/// <summary>房主锄地配置，同步给所有成员</summary>
public class RoomConfig
{
    public double SyncPointMinDistance { get; set; } = 30;
    public int StartRouteIndex { get; set; } = 0;
    /// <summary>线路关键词过滤（逗号分隔，全角/半角），文件名含任一关键词的线路跳过。默认空=不过滤。hoeing-route-keyword-filter，与客户端 RoomConfig 严格对称</summary>
    public string RouteFilterKeywords { get; set; } = "";
    /// <summary>自动更新线路：执行联机锄地前自动调用一次「更新联机锄地线路」（AutoHoeingUpdater）。默认 true（房主设置，同步给成员）。hoeing-autoupdate-routes，与客户端 RoomConfig 严格对称</summary>
    public bool AutoUpdateRoutes { get; set; } = true;
    /// <summary>线路重试模式关键词（逗号分隔，全角/半角），文件名含任一关键词的线路启用"任一成员复苏→全员回神像重跑本段"。默认空=不启用。hoeing-multiplayer-route-retry-mode §0，房主设置同步给成员，与客户端 RoomConfig 严格对称</summary>
    public string RouteRetryModeKeywords { get; set; } = "";
    public bool UseFixedDebugRoutes { get; set; } = false;
    public string FixedDebugRoutePath { get; set; } = "";
    public bool DebugMode { get; set; } = false;
    /// <summary>
    /// 启用万叶聚物同步流程。默认 false，用户需显式打勾才启用，避免无万叶队伍走无效流程。
    /// 替代旧的 KazuhaPlayerIndex 字段（kazuha-player-auto-detection：从"按索引指定"改为"运行时声明"）。
    /// 启用判定：EnableKazuhaSync ∧ isConnected。
    /// </summary>
    public bool EnableKazuhaSync { get; set; } = false;
    /// <summary>万叶聚物完成后非万叶玩家原地再停留的秒数（让吸过来的物品被己方拾取），范围 [0, 30]，默认 1</summary>
    public int KazuhaSyncWaitSeconds { get; set; } = 1;
    /// <summary>万叶聚物同步流程总超时秒数（在战斗点等待 + 聚物动作的总预算），范围 [5, 120]，默认 20</summary>
    public int KazuhaSyncTimeoutSeconds { get; set; } = 20;
    /// <summary>万叶玩家等待 E 技 CD 的最长上限秒数（超时直接尝试释放，由 OCR + 视觉双判决定成败），范围 [3, 10]，默认 5。需保证小于 KazuhaSyncTimeoutSeconds</summary>
    public int KazuhaWaitSkillCdSeconds { get; set; } = 5;
    /// <summary>拾取前精接近步数（联机万叶聚物），范围 [1, 30]，默认 6（约 0.5s 上限）。multiplayer-kazuha-collect-point-broadcast</summary>
    public int KazuhaSecondApproachMaxSteps { get; set; } = 6;
    public int PartyTimeoutSeconds { get; set; } = 300;
    public bool MultiWorldEnabled { get; set; } = false;
    public int MultiWorldCount { get; set; } = 2;
    public string SelectedBuiltinRoute { get; set; } = "";
    /// <summary>联机模式下的战斗超时时间（秒），由房主设定</summary>
    public int FightTimeoutSeconds { get; set; } = 120;
    /// <summary>战斗额外等待时间（秒），同步点超时后为 Fighting 成员额外等待</summary>
    public int FightExtraWaitSeconds { get; set; } = 60;
    /// <summary>重新加入最大等待时间（秒），同步点超时后为 Rejoining/Reviving 成员额外等待</summary>
    public int RejoinMaxWaitSeconds { get; set; } = 300;
    /// <summary>传送点必同步：所有传送点都作为同步等待点</summary>
    public bool SyncAtEveryTeleport { get; set; } = false;

    // === 集体卡死监测配置（multiplayer-mutual-wait-collective-skip spec）===
    /// <summary>启用集体卡死监测，默认 true。关闭后服务端不创建 MutualWaitMonitor，行为退化到 60s 超时</summary>
    public bool EnableMutualWaitCollectiveSkip { get; set; } = true;
    /// <summary>触发阈值比例：totalWaiters ≥ ⌈online * MutualWaitMinWaitersRatio⌉ 才进入稳定计时，OQ-3 默认 0.5</summary>
    public double MutualWaitMinWaitersRatio { get; set; } = 0.5;
    /// <summary>ArrivalSets 快照保持稳定 N 秒后触发协同跳段，默认 30 秒（保守起步）</summary>
    public int MutualWaitStableSeconds { get; set; } = 30;
    /// <summary>连续触发协同跳段上限，达到后走 OnConsecutiveSyncTimeoutExceeded 类型路径协调停止，默认 3</summary>
    public int MaxConsecutiveCollectiveSkips { get; set; } = 3;

    // === 快速同步点抢报（multiplayer-fast-sync-host-controlled spec, 与客户端 RoomConfig 严格对称）===
    /// <summary>启用快速同步点抢报（主开关），默认 false。关闭时所有抢报路径短路，退化到现有"严格到达后上报"</summary>
    public bool FastSyncPointEnabled { get; set; } = false;
    /// <summary>路径同步点抢报距离阈值（米，原神坐标系），范围 [5.0, 30.0]，默认 10.0</summary>
    public double FastSyncPathingDistance { get; set; } = 10.0;
    /// <summary>传送 loading 命中后到抢报之间的延迟毫秒数，范围 [0, 3000]，默认 0</summary>
    public int FastSyncTeleportLoadingDelayMs { get; set; } = 0;

    // === 共享战斗配额结束同步（multiplayer-shared-fight-end-quorum-sync spec, 与客户端 RoomConfig 严格对称）===
    /// <summary>启用联机共享战斗配额结束同步（主开关），默认 false。关闭时 CheckFightFinish 行为一字不变（零回归）</summary>
    public bool SharedFightEndQuorumEnabled { get; set; } = false;
    /// <summary>配额比例 [0.0,1.0]，默认 0.5。达成条件 doneCount ≥ ⌈participants × ratio⌉</summary>
    public double SharedFightEndQuorumRatio { get; set; } = 0.5;

    // === 落后成员逐段追赶（hoeing-lagging-catchup-host-synced-setting spec, 与客户端 RoomConfig 严格对称）===
    /// <summary>启用落后成员逐段追赶（房主设置，同步成员）。默认 false。</summary>
    public bool EnableLaggingCatchUp { get; set; } = false;
    /// <summary>落后触发段数阈值，范围 [1,3]，默认 1。</summary>
    public int LagSegmentThreshold { get; set; } = 1;

    // === 执行期参数房主同步（hoeing-multiplayer-sync-execution-params spec, 与客户端 RoomConfig 严格对称）===
    /// <summary>组队超时动作：0=停止，1=按现有人数继续。源字段 AutoHoeingConfig.PartyTimeoutAction</summary>
    public int PartyTimeoutAction { get; set; } = 0;
    /// <summary>光柱拾取开关。源字段 AutoFightConfig.PickDropsAfterFightEnabled</summary>
    public bool PickDropsAfterFightEnabled { get; set; } = false;
    /// <summary>光柱拾取时长（秒），clamp ≥0。源字段 AutoFightConfig.PickDropsAfterFightSeconds</summary>
    public int PickDropsAfterFightSeconds { get; set; } = 15;
    /// <summary>删除战后等待。源字段 PathingPartyConfig.QuicklySkip</summary>
    public bool QuicklySkip { get; set; } = false;
    /// <summary>简易等待时间（ms），clamp ≥0。源字段 PathingPartyConfig.CombatScriptEndDelayMs</summary>
    public int CombatScriptEndDelayMs { get; set; } = 900;
    /// <summary>只在传送点时恢复。源字段 PathingPartyConfig.OnlyInTeleportRecover</summary>
    public bool OnlyInTeleportRecover { get; set; } = false;
    /// <summary>游泳检测。源字段 AutoFightConfig.SwimmingEnabled（默认 true）</summary>
    public bool SwimmingEnabled { get; set; } = true;

    // === 基于经验判断停止锄地（multiplayer-hoeing-exp-cap-stop, 与客户端 RoomConfig 严格对称）===
    /// <summary>启用基于经验判断停止锄地（房主设置同步成员）。默认 false。multiplayer-hoeing-exp-cap-stop</summary>
    public bool EnableExpCapStop { get; set; } = false;
    /// <summary>经验上限检测模式：false=只检测精英经验，true=检测所有经验（房主设置同步成员）。默认 false。multiplayer-hoeing-exp-cap-stop</summary>
    public bool ExpCapDetectAllExp { get; set; } = false;

    // === 联机锄地守护自动重开（hoeing-multiplayer-guard-auto-restart, 与客户端 RoomConfig 严格对称）===
    /// <summary>联机锄地守护模式（房主设置同步成员）。默认 true。hoeing-multiplayer-guard-auto-restart</summary>
    public bool HoeingGuardMode { get; set; } = true;
    /// <summary>守护重开触发阈值：未执行线路数 ≥ 此值即重开。默认 3，下限 1。hoeing-multiplayer-guard-auto-restart</summary>
    public int GuardUnexecutedRouteThreshold { get; set; } = 3;

    /// <summary>房间白名单（逗号分隔的玩家名称），由第一任房主设置，同步给所有成员。用于踢人判定白名单统一。spec: sync-roomwhitelist-to-roomconfig</summary>
    public string RoomWhitelist { get; set; } = "";
}
