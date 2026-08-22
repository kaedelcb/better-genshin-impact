using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;

namespace BetterGenshinImpact.GameTask.AutoHoeing;

/// <summary>
/// 锄地一条龙配置类
/// </summary>
[Serializable]
public partial class AutoHoeingConfig : ObservableObject
{
    // ========== 第一部分：执行配置 ==========

    /// <summary>
    /// 执行模式：运行锄地路线、调试路线分配、强制刷新所有运行记录、启用仅指定怪物模式
    /// </summary>
    [ObservableProperty]
    private string _operationMode = "运行锄地路线";

    /// <summary>
    /// 选择执行第几个路径组（1-10）
    /// </summary>
    [ObservableProperty]
    private int _groupIndex = 1;

    /// <summary>
    /// 本路径组使用配队名称
    /// </summary>
    [ObservableProperty]
    private string _partyName = "";

    /// <summary>
    /// 组内路线排序模式：原文件顺序、效率降序、高收益优先
    /// </summary>
    [ObservableProperty]
    private string _sortMode = "高收益优先";

    /// <summary>
    /// 拾取模式（默认改为「模板匹配仅拾取狗粮」以减少新用户首跑的拾取干扰）
    /// </summary>
    [ObservableProperty]
    private string _pickupMode = "模板匹配仅拾取狗粮";

    /// <summary>
    /// 仅使用路线相关怪物材料进行识别
    /// </summary>
    [ObservableProperty]
    private bool _useRouteRelatedMaterialsOnly;

    /// <summary>
    /// 禁用识别到物品后的二次校验
    /// </summary>
    [ObservableProperty]
    private bool _disableSecondaryValidation;

    /// <summary>
    /// 泥头车角色编号（中文逗号分隔，如"1，3"）
    /// </summary>
    [ObservableProperty]
    private string _dumperCharacters = "";

    /// <summary>
    /// 使用料理名称（中文逗号分隔）
    /// </summary>
    [ObservableProperty]
    private string _cookingNames = "";

    /// <summary>
    /// 不运行时段
    /// </summary>
    [ObservableProperty]
    private string _noRunPeriod = "";

    /// <summary>
    /// 识别间隔(毫秒)
    /// </summary>
    [ObservableProperty]
    private int _findFInterval = 100;

    /// <summary>
    /// 拾取后延时(毫秒)
    /// </summary>
    [ObservableProperty]
    private int _pickupDelay = 50;

    /// <summary>
    /// 滚动后延时(毫秒)
    /// </summary>
    [ObservableProperty]
    private int _rollingDelay = 32;

    /// <summary>
    /// 单次滚动周期(毫秒)
    /// </summary>
    [ObservableProperty]
    private int _scrollCycle = 1000;

    /// <summary>
    /// 运行路线时输出怪物数量日志
    /// </summary>
    [ObservableProperty]
    private bool _logMonsterCount;

    /// <summary>
    /// 禁用异步操作
    /// </summary>
    [ObservableProperty]
    private bool _disableAsync;

    /// <summary>
    /// 路线结尾时进行坐标检查
    /// </summary>
    [ObservableProperty]
    private bool _enableCoordinateCheck;

    /// <summary>
    /// 跳过校验阶段
    /// </summary>
    [ObservableProperty]
    private bool _skipValidation;

    // ========== 第二部分：路线选择与分组配置 ==========

    /// <summary>
    /// 账户名称
    /// </summary>
    [ObservableProperty]
    private string _accountName = "默认账户";

    /// <summary>
    /// 路径组一要排除的标签
    /// </summary>
    [ObservableProperty]
    private string _tagsForGroup1 = "蕈兽，传奇，狭窄地形";

    [ObservableProperty] private string _tagsForGroup2 = "";
    [ObservableProperty] private string _tagsForGroup3 = "";
    [ObservableProperty] private string _tagsForGroup4 = "";
    [ObservableProperty] private string _tagsForGroup5 = "";
    [ObservableProperty] private string _tagsForGroup6 = "";
    [ObservableProperty] private string _tagsForGroup7 = "";
    [ObservableProperty] private string _tagsForGroup8 = "";
    [ObservableProperty] private string _tagsForGroup9 = "";
    [ObservableProperty] private string _tagsForGroup10 = "";

    /// <summary>
    /// 禁用根据运行记录优化路线选择
    /// </summary>
    [ObservableProperty]
    private bool _disableSelfOptimization;

    /// <summary>
    /// 摩拉/耗时权衡因数
    /// </summary>
    [ObservableProperty]
    private double _efficiencyIndex = 0.25;

    /// <summary>
    /// 好奇系数（0-1）
    /// </summary>
    [ObservableProperty]
    private double _curiosityFactor;

    /// <summary>
    /// 小怪/精英忽略比例
    /// </summary>
    [ObservableProperty]
    private int _ignoreRate = 100;

    /// <summary>
    /// 目标精英数量
    /// </summary>
    [ObservableProperty]
    private int _targetEliteNum = 400;

    /// <summary>
    /// 目标小怪数量
    /// </summary>
    [ObservableProperty]
    private int _targetMonsterNum = 2000;

    /// <summary>
    /// 优先关键词（中文逗号分隔）
    /// </summary>
    [ObservableProperty]
    private string _priorityTags = "";

    /// <summary>
    /// 排除关键词（中文逗号分隔）
    /// </summary>
    [ObservableProperty]
    private string _excludeTags = "";

    // ========== 第三部分：仅指定怪物模式 ==========

    /// <summary>
    /// 目标怪物（中文逗号分隔）
    /// </summary>
    [ObservableProperty]
    private string _targetMonsters = "";

    // ========== 第四部分：联机配置 ==========

    /// <summary>
    /// 启用联机模式（默认开启：新配置组默认进入联机锄地界面）
    /// </summary>
    [ObservableProperty]
    private bool _multiplayerEnabled = true;

    /// <summary>
    /// 联机队伍名称（为空则不切换）
    /// </summary>
    [ObservableProperty]
    private string _multiplayerPartyName = "";

    /// <summary>
    /// 联机起始角色名称（为空则不切换）
    /// </summary>
    [ObservableProperty]
    private string _multiplayerStartAvatarName = "";

    /// <summary>
    /// 协调服务器地址
    /// </summary>
    [ObservableProperty]
    private string _coordinatorServerUrl = "举例：https://bgi-sync.example.com(需部署自建服务)";

    /// <summary>
    /// 当前房间码（运行时状态，不持久化）
    /// </summary>
    [System.Text.Json.Serialization.JsonIgnore]
    public string CurrentRoomCode { get; set; } = "";

    /// <summary>
    /// 从第几条路线开始执行（1-based，0表示从头开始），用于调试续点
    /// </summary>
    [ObservableProperty]
    private int _startRouteIndex = 0;

    /// <summary>
    /// 线路关键词过滤：逗号分隔（支持全角「，」/半角「,」）的关键词列表。
    /// 文件名（不含路径、不含 .json 扩展名）包含任一关键词的线路将被跳过。
    /// 默认空字符串 = 不过滤（旧配置零感知）。匹配不区分大小写。
    /// 单机和联机均支持；联机由房主上传前过滤，成员不二次过滤。
    /// hoeing-route-keyword-filter spec。
    /// </summary>
    [ObservableProperty]
    private string _routeFilterKeywords = "";

    /// <summary>
    /// 自动更新线路：执行联机锄地前自动调用一次「更新联机锄地线路」（AutoHoeingUpdater）。
    /// 默认 true（房主设置，同步给成员）。hoeing-autoupdate-routes spec。
    /// </summary>
    [ObservableProperty]
    private bool _autoUpdateRoutes = true;

    /// <summary>
    /// 玩家名称，联机时显示给其他玩家
    /// </summary>
    [ObservableProperty]
    private string _playerName = "";

    /// <summary>
    /// 玩家 UID，用于进入世界和多世界切换
    /// </summary>
    [ObservableProperty]
    private string _playerUid = "";

    /// <summary>
    /// 调试模式：跳过路线一致性验证，方便单人调试
    /// </summary>
    [ObservableProperty]
    private bool _debugMode = false;

    /// <summary>
    /// 使用固定调试线路：启用后从指定目录按文件名顺序加载路线，跳过正常路线选择逻辑
    /// </summary>
    [ObservableProperty]
    private bool _useFixedDebugRoutes = true;

    /// <summary>
    /// 固定调试线路目录路径，默认为内置 DebugRoutes 目录，可自定义
    /// </summary>
    [ObservableProperty]
    private string _fixedDebugRoutePath = "";

    /// <summary>
    /// 选中的内置线路文件夹名称（为空表示未选择）
    /// </summary>
    [ObservableProperty]
    private string _selectedBuiltinRoute = "";

    /// <summary>
    /// 集合点与战斗点的最小距离阈值，小于此距离的点不作为集合点，默认30
    /// </summary>
    [ObservableProperty]
    private double _syncPointMinDistance = 30.0;

    /// <summary>
    /// 联机模式战斗超时时间（秒），由房主设定并同步给所有成员，覆盖各自的自动战斗超时配置。默认 120
    /// </summary>
    [ObservableProperty]
    private int _fightTimeoutSeconds = 50;

    /// <summary>
    /// 联机锄地是否强制使用固定的"联机战斗策略"文件（User\AutoFight\联机战斗策略.txt）。
    /// 默认 true（保持现有行为，老用户升级零感知）。
    /// false 时联机锄地沿用配置组/全局 AutoFightConfig.StrategyName 解析出的策略。
    /// 纯本地配置（OQ-A 方案 A）：不进 SignalR / 两处 RoomConfig，每个玩家各自决定。
    /// </summary>
    [ObservableProperty]
    private bool _multiplayerUseFixedFightStrategy = true;

    /// <summary>
    /// 战斗额外等待时间（秒），同步点超时后为 Fighting 成员额外等待，默认 60
    /// </summary>
    [ObservableProperty]
    private int _fightExtraWaitSeconds = 60;

    /// <summary>
    /// 重新加入最大等待时间（秒），同步点超时后为 Rejoining/Reviving 成员额外等待，默认 300
    /// </summary>
    [ObservableProperty]
    private int _rejoinMaxWaitSeconds = 300;

    /// <summary>
    /// 最大连续跳过路线次数，达到上限后退出联机锄地，默认 3
    /// </summary>
    [ObservableProperty]
    private int _maxConsecutiveSkips = 3;

    /// <summary>
    /// 最大连续同步超时次数，达到上限后退出联机锄地，默认 3
    /// </summary>
    [ObservableProperty]
    private int _maxConsecutiveTimeouts = 3;

    /// <summary>
    /// 最大路线滞后容忍数量，超过此数量的成员被视为落后过多，默认 2
    /// </summary>
    [ObservableProperty]
    private int? _maxRouteLag = 2;

    // === 集体卡死监测（multiplayer-mutual-wait-collective-skip spec / OQ-1~OQ-8 全部默认值）===
    /// <summary>启用集体卡死监测，默认 true。关闭后服务端不创建 MutualWaitMonitor，行为退化到 60s 超时</summary>
    [ObservableProperty]
    private bool _enableMutualWaitCollectiveSkip = true;
    /// <summary>触发阈值比例：totalWaiters ≥ ⌈online * MutualWaitMinWaitersRatio⌉ 才进入稳定计时，OQ-3 默认 0.5</summary>
    [ObservableProperty]
    private double _mutualWaitMinWaitersRatio = 0.5;
    /// <summary>ArrivalSets 快照保持稳定 N 秒后触发协同跳段，默认 30 秒（保守起步）</summary>
    [ObservableProperty]
    private int _mutualWaitStableSeconds = 30;
    /// <summary>连续触发协同跳段上限，达到后走 OnConsecutiveSyncTimeoutExceeded 类型路径协调停止，默认 3</summary>
    [ObservableProperty]
    private int _maxConsecutiveCollectiveSkips = 3;

    // === 快速同步点抢报（multiplayer-fast-sync-host-controlled spec, host-controlled 三处对称）===
    /// <summary>
    /// 启用快速同步点抢报（主开关）。默认 false。
    /// 关闭时所有抢报路径短路，行为退化为现有"严格到达后上报"。
    /// 详见 .kiro/specs/multiplayer-fast-sync-host-controlled/。
    /// </summary>
    [ObservableProperty]
    private bool _fastSyncPointEnabled = true;

    /// <summary>
    /// 路径同步点抢报距离阈值（米，原神坐标系）。范围 [5.0, 30.0]，默认 10.0。
    /// 距 waypoint 距离 ≤ 阈值时触发 OR 门抢报；持久化加载时由
    /// FastSyncDecisions.ClampPathingDistance 兜底 clamp。
    /// </summary>
    [ObservableProperty]
    private double _fastSyncPathingDistance = 30.0;

    /// <summary>
    /// 传送 loading 命中后到抢报上报之间的延迟毫秒数。范围 [0, 3000]，默认 0。
    /// 高网络延迟环境可上调。持久化加载时由 FastSyncDecisions.ClampTeleportDelay 兜底 clamp。
    /// </summary>
    [ObservableProperty]
    private int _fastSyncTeleportLoadingDelayMs = 0;

    // === 共享战斗配额结束同步（multiplayer-shared-fight-end-quorum-sync spec, host-controlled 三处对称）===
    /// <summary>
    /// 启用联机共享战斗"配额结束同步"（主开关）。默认 false。
    /// 关闭时 CheckFightFinish 行为一字不变（零回归）。开启后：本地判定结束改为上报投票，
    /// 战斗参与者中 done 数 ≥ ⌈participants × ratio⌉ 时服务端广播 AllFightDone 强制全队结束。
    /// 房主设置，随 RoomConfig 同步给成员。详见 .kiro/specs/multiplayer-shared-fight-end-quorum-sync/。
    /// </summary>
    [ObservableProperty]
    private bool _sharedFightEndQuorumEnabled = false;

    /// <summary>
    /// 配额比例。范围 [0.0, 1.0]，默认 0.5（过半）。达成条件 doneCount ≥ ⌈participants × ratio⌉。
    /// 加载/同步时由 SharedFightEndQuorumDecisions.ClampRatio 兜底（NaN → 0.5）。
    /// </summary>
    [ObservableProperty]
    private double _sharedFightEndQuorumRatio = 0.5;

    /// <summary>
    /// 启用"基于经验判断停止锄地"（房主设置，同步成员）。默认 false。
    /// 开启后联机锄地每个有效战斗节点检测怪物经验，连续 2 场无经验判本机达上限；
    /// 全员达上限后服务端广播 AllReachedExpCap，各端优雅停止。零回归：false 时所有路径短路。
    /// multiplayer-hoeing-exp-cap-stop。
    /// </summary>
    [ObservableProperty]
    private bool _enableExpCapStop = false;

    /// <summary>
    /// 经验上限检测模式（房主设置，同步成员）。默认 false。multiplayer-hoeing-exp-cap-stop。
    /// - false（默认）：只检测精英经验（57/58/60 数字模板），小怪经验不计入 → 精英上限即判达上限。
    /// - true：检测所有经验（复用好感任务 exp.png 通用图标），精英+小怪任一掉经验都算"有经验"。
    /// 依附于 EnableExpCapStop：仅在其为 true 时有意义。
    /// </summary>
    [ObservableProperty]
    private bool _expCapDetectAllExp = false;

    /// <summary>
    /// 启用万叶聚物同步流程。默认 false，用户需显式打勾才启用，避免无万叶队伍走无效流程。
    /// 替代旧的 KazuhaPlayerIndex 字段（kazuha-player-auto-detection：从"按索引指定"改为"运行时声明"）。
    /// 启用判定：EnableKazuhaSync ∧ isConnected。
    /// </summary>
    [ObservableProperty]
    private bool _enableKazuhaSync = false;

    /// <summary>
    /// 万叶聚物完成后非万叶玩家原地再停留的秒数（让吸过来的物品被己方拾取），范围 [0, 30]，默认 1
    /// </summary>
    [ObservableProperty]
    private int _kazuhaSyncWaitSeconds = 0;

    /// <summary>
    /// 万叶聚物同步流程总超时秒数（在战斗点等待 + 聚物动作的总预算），范围 [5, 120]，默认 20
    /// </summary>
    [ObservableProperty]
    private int _kazuhaSyncTimeoutSeconds = 5;

    /// <summary>
    /// 万叶玩家等待 E 技 CD 的最长上限秒数（超时直接尝试释放，由 OCR + 视觉双判决定成败），范围 [3, 10]，默认 5。需保证小于 KazuhaSyncTimeoutSeconds
    /// </summary>
    [ObservableProperty]
    private int _kazuhaWaitSkillCdSeconds = 5;

    /// <summary>
    /// 拾取前精接近步数（联机万叶聚物）：
    /// 非万叶玩家在战后回点完成第一段粗接近后，再做"二段精接近"到万叶上报的聚物点 / 兜底接近战斗点。
    /// 步数越大上限耗时越长（每步 ~80ms），范围 [1, 30]，默认 6（约 0.5s 上限）。
    /// 仅联机万叶聚物分支生效，单机零回归（multiplayer-kazuha-collect-point-broadcast）。
    /// </summary>
    [ObservableProperty]
    private int _kazuhaSecondApproachMaxSteps = 6;

    /// <summary>
    /// 万叶回点异常坐标判定阈值（小地图坐标单位）。
    /// 战后回点 / 持续回点读到的坐标距战斗点超过此值，视为识别漂移 garbage 远点，
    /// 触发"重新播种战斗点锚点 + 重识别"重试，而不是直接朝该点移动。
    /// 默认 50（替代旧硬编码 180）。纯本地调试参数，不同步房主→成员、不进 RoomConfig 协议。
    /// 仅联机万叶两条回点路径（持续回点后台循环 / 战后聚物分支）读取，单机零回归。
    /// 详见 .kiro/specs/hoeing-kazuha-return-abnormal-coord-reseed-moveto-fix。
    /// </summary>
    [ObservableProperty]
    private double _kazuhaReturnAbnormalCoordThreshold = 50.0;

    /// <summary>
    /// 万叶回点异常坐标的重新播种 + 重识别最大重试次数。
    /// 命中异常阈值后，重播种战斗点锚点并重识别（每次间隔约 100ms），最多重试此次数；
    /// 仍异常则放弃本轮移动（绝不以异常坐标 MoveTo / MoveCloseTo）。
    /// 默认 3。纯本地调试参数，不同步、不进 RoomConfig 协议。
    /// 详见 .kiro/specs/hoeing-kazuha-return-abnormal-coord-reseed-moveto-fix。
    /// </summary>
    [ObservableProperty]
    private int _kazuhaReturnReseedRetryCount = 3;

    /// <summary>
    /// 万叶回点 (0,0) 识别失败时 GetPositionStable 全局匹配重试上限。
    /// 当回点重识别（GetPosition 局部匹配）返回 (0,0)（小地图图像层面识别不出）时，
    /// 改走更鲁棒的 GetPositionStable（全局匹配）重试，最多此次数；任一次返回非 (0,0)
    /// 且落入异常阈值内即采纳，仍全部 (0,0) 则放弃本轮移动。
    /// 与 KazuhaReturnReseedRetryCount（漂移远点重试）分开，便于分别调优。
    /// 默认 3。纯本地调试参数，不进 RoomConfig 协议、不碰 SignalR。
    /// 详见 .kiro/specs/hoeing-kazuha-return-minimap-recognition-fail-getpositionstable-retry-fix。
    /// </summary>
    [ObservableProperty]
    private int _kazuhaReturnZeroCoordStableRetryCount = 3;

    /// <summary>
    /// 万叶战后回点"距离预判帧" GetPosition 返回 (0,0) 时，进入 GetPositionStable（全局匹配）
    /// 有限重试的总时长窗口上限（毫秒）。窗口内按 ReseedReSampleDelayMs（约 100ms）分多次重试，
    /// 任一次拿到非 (0,0) 有效坐标即采纳并走既有 MoveTo 二段式接近；窗口耗尽仍 (0,0) 则退化到
    /// 现状等价行为（跳过 MoveTo、第一段 MoveCloseTo、进 WaitAtFightPointAsync），不崩溃、不死循环。
    /// 默认 2000（约 2 秒）。纯本地调试参数，不进 RoomConfig 协议、不碰 SignalR。
    /// 仅联机 PathExecutor 战后聚物回点分支（路径 B）读取，单机零回归。
    /// 详见 .kiro/specs/hoeing-kazuha-return-predistance-zero-coord-skip-moveto-fix。
    /// </summary>
    [ObservableProperty]
    private int _kazuhaReturnPreDistanceZeroRetryTimeoutMs = 2000;

    /// <summary>
    /// 联机调试：启用落后成员逐段追赶。默认 false（骨架阶段手动开启，实测稳定后再考虑默认开）。
    /// 仅联机模式 + 成员侧生效，单机零感知。
    /// </summary>
    [ObservableProperty]
    private bool _enableLaggingCatchUp = false;

    /// <summary>
    /// 联机调试：落后追赶触发阈值（落后多少段才触发逐段跳进），默认 1，下限 1。
    /// 真段级语义：落后 ≥ N 段触发（数据源 = CurrentPlayerList 各玩家段级 CurrentProgress）。
    /// </summary>
    [ObservableProperty]
    private int _lagSegmentThreshold = 1;

    /// <summary>
    /// 联机锄地守护模式（房主设置，同步成员）。默认 true。
    /// 开启后：本次联机锄地因异常（掉线/被踢/掉房/未执行数达阈值）未正常跑完时，自动完整重开一次
    /// （等价手动重开，靠 CD 记录跳过已完成线路）。手动停止 / 经验上限正常停止 / 单机模式不触发。
    /// 重开仅 1 次。hoeing-multiplayer-guard-auto-restart。
    /// </summary>
    [ObservableProperty]
    private bool _hoeingGuardMode = true;

    /// <summary>
    /// 守护重开触发阈值：未执行线路数（=计划应跑总数−已执行数）≥ 此值即视为未跑完（房主设置，同步成员）。
    /// 默认 3，下限 1。仅 HoeingGuardMode 为 true 时有意义。hoeing-multiplayer-guard-auto-restart。
    /// </summary>
    [ObservableProperty]
    private int _guardUnexecutedRouteThreshold = 3;

    /// <summary>
    /// 单人调试模式：纯本地调试开关，默认 false。
    /// 开启后联机锄地在单人世界下绕过 WorldStateMonitor 的"被踢出联机世界"终止判定
    /// （IsInMultiGame=false + SignalR 正常的 connected-but-not-in-game 累计与据其触发的 ConfirmExitAsync），
    /// 使任务持续运行供开发者单人调试整条锄地线路。
    /// 纯本地：不进 Multiplayer/Models/RoomConfig.cs 与 BgiCoordinatorServer/Models/RoomConfig.cs、
    /// 不碰 SignalR、不做房主→成员下发，每个玩家各自决定。
    /// 仅用于调试，不保证依赖真实多人世界的下游环节在单人下完整可用。
    /// 详见 .kiro/specs/hoeing-multiplayer-solo-debug-mode。
    /// </summary>
    [ObservableProperty]
    private bool _soloDebugMode = false;

    /// <summary>
    /// 联机锄地启动后自动启动联机助手（MultiplayerHoeingAssistant.exe）。纯本地设置，不同步。
    /// </summary>
    [ObservableProperty]
    private bool _autoLaunchAssistant = false;

    /// <summary>
    /// 房间白名单，逗号分隔的玩家名称
    /// </summary>
    [ObservableProperty]
    private string _roomWhitelist = "";

    /// <summary>
    /// 房间期望人数（2-4），用于判断人齐条件
    /// </summary>
    [ObservableProperty]
    private int _expectedPlayerCount = 4;

    /// <summary>
    /// 组队等待超时（秒），超时后停止联机锄地
    /// </summary>
    [ObservableProperty]
    private int _partyTimeoutSeconds = 600;

    /// <summary>
    /// 组队超时动作：0=结束任务，1=现有人数锄地
    /// </summary>
    [ObservableProperty]
    private int _partyTimeoutAction = 0;

    // ========== 第五部分：联机角色配置（配置组专用） ==========

    /// <summary>
    /// 联机角色：host=房主，member=成员
    /// </summary>
    [ObservableProperty]
    private string _multiplayerRole = "host";

    /// <summary>
    /// 成员加入方式：byHostName=指定玩家名称，random=随机加入现有房间
    /// </summary>
    [ObservableProperty]
    private string _memberJoinMode = "random";

    /// <summary>
    /// 成员加入时指定的房主玩家名称
    /// </summary>
    [ObservableProperty]
    private string _targetHostName = "";

    // ========== 第六部分：多世界连续锄地配置 ==========

    /// <summary>
    /// 启用多世界连续锄地（房主设定，完成一个世界后轮换到下一个玩家的世界）
    /// </summary>
    [ObservableProperty]
    private bool _multiWorldEnabled = true;

    /// <summary>
    /// 多世界锄地轮数（1-4），由房主设定，按加入顺序依次成为房主
    /// </summary>
    [ObservableProperty]
    private int _multiWorldCount = 4;

    /// <summary>
    /// 多世界重开续跑：房主重开时把已完成房主 UID 上报服务端裁剪权威序列，全组跳过已完成世界。
    /// 默认 true。房主侧配置（不进 RoomConfig、不下发成员）；效果通过裁剪序列全局生效。
    /// 关闭 → 上报空集合 → 全量序列 → 逐轮试探（现状）。单机零感知。
    /// hoeing-multiworld-host-restart-resume-round Req 5。
    /// </summary>
    [ObservableProperty]
    private bool _multiWorldResumeEnabled = true;

    // === 反复复苏双层兜底（multi-revival-rapid-recurrence-fallback spec, OQ-1 / OQ-2 默认值）===

    /// <summary>
    /// 反复复苏滑动窗口（秒）：在此窗口内累计复苏次数达 RapidRevivalThreshold 即触发"跳本段 + 神像回血"。
    /// 默认 60 秒，覆盖实测日志最坏间隔（29s）。范围 [10, 300]。
    /// </summary>
    [ObservableProperty]
    private int _rapidRevivalWindowSeconds = 60;

    /// <summary>
    /// 反复复苏触发阈值（次）：滑动窗口内累计达此次数即触发"跳本段 + 神像回血"。
    /// 默认 2 次（即"窗口内出现第 2 次复苏立即升级"）。范围 [2, 10]。
    /// </summary>
    [ObservableProperty]
    private int _rapidRevivalThreshold = 2;

    /// <summary>
    /// 单条路线复苏次数上限：路线累计达此次数即触发"跳整路线 + 神像回血"（防死循环）。
    /// 默认 3 次，与 MaxConsecutiveSkips 保持一致。范围 [2, 10]。
    /// </summary>
    [ObservableProperty]
    private int _routeRevivalCap = 3;

    // ========== 按周期自动吃食物（纯本地：不同步给其他玩家，不写入任何 RoomConfig / SignalR） ==========
    // multiplayer-hoeing-auto-eat-food-by-period spec。参考 MultiplayerUseFixedFightStrategy 的"纯本地"先例。
    // 每个玩家各自的食物/CD，不由房主下发。

    /// <summary>吃食物第1行：食物页固定格子序号（1~4）</summary>
    [ObservableProperty] private int _medicineFoodSlot1 = 1;
    /// <summary>吃食物第2行：食物页固定格子序号（1~4）</summary>
    [ObservableProperty] private int _medicineFoodSlot2 = 2;
    /// <summary>吃食物第3行：食物页固定格子序号（1~4）</summary>
    [ObservableProperty] private int _medicineFoodSlot3 = 3;
    /// <summary>吃食物第4行：食物页固定格子序号（1~4）</summary>
    [ObservableProperty] private int _medicineFoodSlot4 = 4;

    /// <summary>吃食物第1行：执行周期（秒），0 表示该行不启用</summary>
    [ObservableProperty] private int _medicineFoodPeriod1 = 0;
    /// <summary>吃食物第2行：执行周期（秒），0 表示该行不启用</summary>
    [ObservableProperty] private int _medicineFoodPeriod2 = 0;
    /// <summary>吃食物第3行：执行周期（秒），0 表示该行不启用</summary>
    [ObservableProperty] private int _medicineFoodPeriod3 = 0;
    /// <summary>吃食物第4行：执行周期（秒），0 表示该行不启用</summary>
    [ObservableProperty] private int _medicineFoodPeriod4 = 0;

    /// <summary>吃食物第1格：吃药线路关键词（逗号分隔，全/半角均可）。当前线路名含任一关键词即吃该格，无视周期/CD。空=线路维度不启用。</summary>
    [ObservableProperty] private string _medicineFoodRouteKeywords1 = "";
    /// <summary>吃食物第2格：吃药线路关键词（逗号分隔）。含任一关键词即吃该格，无视周期/CD。空=不启用。</summary>
    [ObservableProperty] private string _medicineFoodRouteKeywords2 = "";
    /// <summary>吃食物第3格：吃药线路关键词（逗号分隔）。含任一关键词即吃该格，无视周期/CD。空=不启用。</summary>
    [ObservableProperty] private string _medicineFoodRouteKeywords3 = "";
    /// <summary>吃食物第4格：吃药线路关键词（逗号分隔）。含任一关键词即吃该格，无视周期/CD。空=不启用。</summary>
    [ObservableProperty] private string _medicineFoodRouteKeywords4 = "";

    // === 线路重试模式 v2（hoeing-multiplayer-route-retry-mode §0，房主设置同步给成员，进 RoomConfig）===
    /// <summary>
    /// 线路重试模式关键词（逗号分隔，全/半角）。线路名含任一关键词的线路启用：任一成员复苏 → 全员停战斗+跳拾取+回神像+重跑本段（正常同步），只重试一次。
    /// 空=不启用任何线路。由房主设置并通过 RoomConfig 同步给所有成员（成员本地填写会被房主同步覆盖）；UI 绑定本字段。
    /// </summary>
    [ObservableProperty] private string _routeRetryModeKeywords = "";

    // === 路线变体偏好（route-variant-sync-by-logical-id spec / R13）===

    /// <summary>
    /// 路线变体偏好字典：key = LogicalRouteId, value = 玩家选中的变体文件名。
    /// 全局存储（落盘到 User/config.json），不进 RoomConfig（不同步给其他玩家——
    /// 不同玩家独立选自己跑哪个变体是核心价值）。
    /// 默认空字典；老用户升级后所有路线走"无偏好"分支，行为完全不变。
    ///
    /// ⚠️ 直接 mutate 字典（如 VariantPreferences[k] = v）不会触发 PropertyChanged，
    ///    持久化会丢失。请使用 SetVariantPreference / RemoveVariantPreference 方法。
    /// </summary>
    [ObservableProperty]
    private Dictionary<string, string> _variantPreferences = new();

    /// <summary>
    /// 设置某 LogicalRouteId 的变体偏好（R13.5）。
    /// 类内显式 OnPropertyChanged，避开 [ObservableProperty] 字典 mutate 不触发的陷阱。
    /// </summary>
    public void SetVariantPreference(string logicalRouteId, string fileName)
    {
        if (string.IsNullOrEmpty(logicalRouteId)) return;
        _variantPreferences[logicalRouteId] = fileName ?? string.Empty;
        OnPropertyChanged(nameof(VariantPreferences));
    }

    /// <summary>
    /// 移除某 LogicalRouteId 的变体偏好。
    /// </summary>
    public void RemoveVariantPreference(string logicalRouteId)
    {
        if (string.IsNullOrEmpty(logicalRouteId)) return;
        if (_variantPreferences.Remove(logicalRouteId))
            OnPropertyChanged(nameof(VariantPreferences));
    }
}
