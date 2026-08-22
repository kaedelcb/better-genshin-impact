using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Services;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Exceptions;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.GameTask.Shell;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;


namespace BetterGenshinImpact.GameTask.AutoHoeing;

/// <summary>
/// 锄地一条龙 - BetterGI原生C#独立任务
/// 将JS脚本"锄地一条龙"的全部功能转写为原生任务
/// </summary>
public class AutoHoeingTask : ISoloTask
{
    public string Name => "锄地一条龙";

    private readonly ILogger<AutoHoeingTask> _logger = App.GetLogger<AutoHoeingTask>();
    private AutoHoeingConfig _config = null!;
    private CancellationToken _ct;

    /// <summary>
    /// 配置组传入的地图追踪配置（含队伍、战斗策略等），为null时使用默认配置
    /// </summary>
    private readonly PathingPartyConfig? _partyConfig;

    /// <summary>
    /// 配置组传入的独立任务配置覆盖，为null时使用全局AutoHoeingConfig
    /// </summary>
    private readonly Dictionary<string, object?>? _settingsOverride;

    /// <summary>
    /// 配置组名称
    /// </summary>
    private readonly string? _groupName;

    // 数据目录（路线文件、怪物信息、运行记录等）
    private string _dataDir = "";

    // 服务
    private readonly MonsterInfoRepository _monsterRepo = new();
    private readonly CdManager _cdManager = new();
    private readonly MultiWorldProgressStore _multiWorldProgressStore = new();
    private bool _isInitialHost; // round 0 落定：本机是否为本场发起房主（hoeing-multiworld-host-restart-resume-round）
    private readonly RouteSelector _routeSelector = new();
    private readonly TimeRestrictionChecker _timeChecker = new();
    private readonly TemplatePickupService _pickupService = new();
    private readonly AnomalyDetector _anomalyDetector = new();
    private readonly DumperService _dumperService = new();
    private readonly BlacklistManager _blacklistManager = new();
    private readonly CookingService _cookingService = new();
    private RouteConsistencyChecker? _consistencyChecker;
    private CoordinatorClient? _coordinatorClientRef;
    private RouteExecutionEngine? _executionEngine;
    private MultiplayerCoordinator? _multiplayerCoordinator;
    private WorldStateMonitor? _worldStateMonitor;
    private bool _shouldSwitchFurina;
    
    private volatile bool _sessionTerminated;
    private string? _stopReason;
    private CancellationTokenSource? _linkedStopCts;

    // 基于经验判断停止锄地：本次结束是否由"全员达经验上限"触发（multiplayer-hoeing-exp-cap-stop）。
    // 仅此路径在退世界后额外传送七天神像（任务结束时可能停在怪堆/危险地形，去神像收尾更安全）；
    // 其它停止路径（超时/被踢/掉房/正常跑完）不触发，保持原行为。
    private volatile bool _expCapStopTriggered;

    // 基于经验判断停止锄地：保存最后一轮（被中断时）的统计信息，供 Start.finally 补输出"本轮锄地结束统计"格式日志。
    // 仅经验上限触发（_expCapStopTriggered）且确实进入过 ProcessRoutesByGroup（_expCapTotalRoutes > 0）时使用。
    private string? _expCapRoundPrefix;         // 最后一轮的 roundPrefix
    private int _expCapCompletedCount;           // 最后一轮的 completedCount
    private int _expCapSkippedCount;             // 最后一轮的 skippedCount
    private int _expCapTotalRoutes;              // 最后一轮的 groupRoutes.Count
    private DateTime _expCapGroupStartTime;      // 最后一轮的 groupStartTime

    // === 联机锄地守护自动重开（hoeing-multiplayer-guard-auto-restart）===
    // 本次运行是否由守护重开产生（新实例置 true）。true 时结束后不再触发重开（次数上限 1，R3）。
    private bool _isGuardRestartRun;
    // 本次任务累计"计划应跑线路总数"与"已执行到的线路数"（跨组/跨多世界轮累加）。
    // 未执行数 = _guardPlannedRouteCount − _guardExecutedRouteCount（HoeingGuardDecisions.ComputeUnexecutedCount）。
    private int _guardPlannedRouteCount;
    private int _guardExecutedRouteCount;
    // 本次运行是否到达过"正常完成点"（hoeing-guard-false-restart-on-normal-close 防线 B）。
    // 仅在多世界"跑满全部轮次"一处置 true（由 RunMultiWorldAsync 的 allRoundsCompleted 严格把关）；
    // 中途异常（取消/掉线/会话终止 break/本轮初始化失败 break）均不置位。
    // 单世界路径刻意不置位——那条路径有多个"正常 return 但未跑完"的分支无法区分（见缺陷 2 注释），
    // 其正常完成由防线 A（未执行线路数为 0）识别。
    // 守护据此区分"正常收尾关房"与"房主掉线关房"——二者 _stopReason 文字相同，无法用文字判别。
    // volatile：赋值发生在任务线程，读取发生在 Start 尾部守护块，保证可见性。
    private volatile bool _completedNormally;

    // 本次任务开始时间（用于上限退出时输出总用时汇总）。在 Start() 入口设置。
    private DateTime _taskStartTime = DateTime.MinValue;

    // 联机组队是否达到"可开锄"状态。仅在 InitializeMultiplayerAsync 成功走到末尾时置 true；
    // 任何失败/降级路径（连不上服务器、加不进房间、拿不到房主 UID、房主未就绪超时、
    // 加入世界失败、等待组队超时、初始化异常）都会提前 return，标志保持 false。
    // RunTask 据此守卫：false 且非单人调试模式 → 不进入锄地，杜绝成员在自己世界单跑。
    private bool _multiplayerPartyReady;
    // 本次运行组队阶段是否失败（hoeing-multiplayer-party-fail-restart）。
    // 组队失败时尚未开锄，_guardPlannedRouteCount 恒为 0，未执行线路数为 0 会触发
    // HoeingGuardDecisions 防线 A 短路（误判为"全跑完了"）。组队失败路径由 MarkPartyFailed
    // 同时写入 _stopReason 并置本标志，Start 尾部守护块据此以 partyFailed 豁免防线 A，
    // 使组队失败也能触发重开。volatile：赋值发生在任务线程，读取发生在 Start 尾部守护块。
    private volatile bool _partyFailed;

    // hoeing-multiplayer-sync-execution-params §C2 通道 C：成员回填时暂存被覆盖的全局
    // PickDropsAfterFightSeconds 原值，finally（§C4）恢复，保证单机 ScanPickTask 零感知。
    // 用 ??= 只在首次回填记录，多世界多次回填不覆盖真正原值。
    private int? _savedGlobalPickDropsAfterFightSeconds;
    
    private bool _teamAlreadySwitched = false;
    private bool _worldPermissionSet = false;
    // 开锄前换武器仅首轮执行一次的标志（hoeing-multiplayer-preswitch-weapon）。
    // 注意：绝不在多世界轮换重置点（_teamAlreadySwitched = false 处）一起重置，保证仅首轮换武器一次。
    private bool _preSwitchWeaponDone = false;
    // 解析后的两行换武器配置（来自配置组 settings['preSwitchWeaponRows']），ApplyPreSwitchWeaponRowsOverride 内填充。
    private List<Multiplayer.PreSwitchWeaponRow> _preSwitchWeaponRows = new();
    // 解析后的按线路切角色映射（来自配置组 settings['perRouteSwitchRoles']），ApplyPerRouteSwitchRolesOverride 内填充。
    private Dictionary<string, Multiplayer.RouteRoleEntry> _perRouteSwitchRoles = new();
    // 按线路切角色 Provider（封装 RouteId 推导 + Hook 构造），注入 RouteExecutionEngine。
    private Multiplayer.PerRouteSwitchRolesProvider? _perRouteSwitchProvider;

    /// <summary>
    /// 隐藏服务器地址的前半部分（隐私保护）
    /// </summary>
    private static string MaskServerUrl(string? url)
    {
        if (string.IsNullOrEmpty(url)) return url ?? "";
        try
        {
            // 例如 http://121.4.78.52:8080/hub -> http://***:****/***
            var uri = new Uri(url);
            var maskedPath = string.IsNullOrEmpty(uri.AbsolutePath) || uri.AbsolutePath == "/" ? "" : "/***";
            return $"{uri.Scheme}://***:****{maskedPath}";
        }
        catch
        {
            return url;
        }
    }

    /// <summary>
    /// route-variant-sync-by-logical-id spec / R14：对单条计划路线执行
    /// LogicalRouteId 加载 + 变体偏好查询 + 必要时切换文件 + fallback 处理。
    /// 返回 (实际加载的 PathingTask, schemaItem)。
    /// schemaItem.LogicalRouteId 为空字符串时表示老路径，调用方不应纳入 R6 上报比较。
    /// </summary>
    private (PathingTask? actualTask, RouteVariantSchemaItem schemaItem) ResolveAndLoadActualVariant(
        string hostFileName, string hostFullPath, string pathingDir)
    {
        PathingTask? loaded;
        try
        {
            loaded = PathingTask.BuildFromFilePath(hostFullPath);
        }
        catch (InvalidRouteException ex)
        {
            // 代表文件本身就坏（重复 SyncPointId）→ 让调用方拿到 null 后跳过/报错
            _logger.LogError(ex, "[变体] 代表路线 {Host} 加载失败（InvalidRouteException）", hostFileName);
            return (null, new RouteVariantSchemaItem { ActualVariantFileName = hostFileName });
        }
        catch (Exception ex)
        {
            // hoeing-variant-route-empty-json-crash-and-discovery-fix / EB-A：
            // 空文件 / 不含合法 JSON token / 反序列化失败（如历史遗留的占位 JSON）
            // 会让 STJ 抛 JsonException。此处兜底跳过该路线（返回 null）、
            // 由调用方剔除出执行/上报列表并继续其余路线，不终止整个一条龙任务。
            _logger.LogWarning(ex, "[变体] 代表路线 {Host} 加载失败（空/损坏 JSON），跳过该路线", hostFileName);
            return (null, new RouteVariantSchemaItem { ActualVariantFileName = hostFileName });
        }
        if (loaded == null)
        {
            return (null, new RouteVariantSchemaItem { ActualVariantFileName = hostFileName });
        }

        // route-variant-sync-by-logical-id spec / §15.5：文件夹式变体派生身份。
        // 代表文件所在变体文件夹 → 基名（= 配对键 = syncId 命名空间）；其总文件夹名 → 偏好 key。
        var repFolder = RouteVariantNaming.TryGetVariantFolder(hostFullPath);
        var baseName = repFolder == null ? null : RouteVariantNaming.StripBaseName(hostFileName, repFolder);
        var topFolderName = RouteVariantNaming.TryGetTopFolderName(hostFullPath);

        // 老路径（非变体布局）OR 单机模式 → 直接返回，不进入替换分支（R15.4）
        if (string.IsNullOrEmpty(baseName) || _multiplayerCoordinator == null)
        {
            return (loaded, BuildSchemaItem(loaded, loaded.FileName));
        }

        // 加载后强制进手动同步模式：syncId 命名空间 = 基名（即使没替换，代表也得进手动模式才能跨变体对齐）
        loaded.LogicalRouteId = baseName;

        // topDir = 变体子文件夹的父目录（总线路文件夹，如 传奇）
        var topDir = Path.GetDirectoryName(Path.GetDirectoryName(hostFullPath)) ?? pathingDir;
        var scan = RouteVariantScanner.ScanVariants(topDir, forceRefresh: false);
        var prefs = _config.VariantPreferences ?? new Dictionary<string, string>();

        var result = RouteVariantResolver.Resolve(
            representativeFileName: hostFileName,
            representativeAbsolutePath: hostFullPath,
            baseName: baseName,
            topFolderName: topFolderName,
            variantPreferences: prefs,
            scanByBaseName: scan);

        if (result.Reason != RouteVariantResolver.FallbackReason.None)
        {
            _logger.LogWarning("[变体] 偏好不可用，回退到代表 {Host}（原因: {Reason}, 总文件夹={Top}, 基名={Base}）",
                hostFileName, result.Reason, topFolderName, baseName);
        }

        PathingTask actualTask = loaded;
        if (!string.Equals(result.ActualAbsolutePath, hostFullPath, StringComparison.OrdinalIgnoreCase))
        {
            try
            {
                var reloaded = PathingTask.BuildFromFilePath(result.ActualAbsolutePath);
                if (reloaded == null)
                {
                    _logger.LogWarning("[变体] 偏好文件 {Pref} 加载返回 null，回退到代表 {Host}",
                        result.ActualFileName, hostFileName);
                    actualTask = loaded;
                }
                else
                {
                    // 偏好变体同样强制进手动模式，命名空间 = 同一基名（保证与代表/其他变体对齐）
                    reloaded.LogicalRouteId = baseName;
                    actualTask = reloaded;
                }
            }
            catch (InvalidRouteException ex)
            {
                _logger.LogWarning(ex, "[变体] 偏好文件 {Pref} 加载抛 InvalidRouteException，回退到代表 {Host}",
                    result.ActualFileName, hostFileName);
                actualTask = loaded;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[变体] 偏好文件 {Pref} 加载失败（FilePreferenceUnreadable），回退到代表 {Host}",
                    result.ActualFileName, hostFileName);
                actualTask = loaded;
            }
        }

        _logger.LogInformation("[变体] 实际执行 {Actual}（代表={Host}, 基名={Base}）",
            actualTask.FileName, hostFileName, baseName);

        return (actualTask, BuildSchemaItem(actualTask, actualTask.FileName));
    }

    /// <summary>
    /// 从加载完成的 PathingTask 提取 SyncPointList + TeleportSyncPointSequence 用于 R6 上报。
    /// 段切分必须与 PathExecutor.ConvertWaypointsForTrack 完全一致
    /// （teleport 遇到且当前段非空时起新段、第一个若是 teleport 则为段0 wpIdx0）。
    /// </summary>
    private static RouteVariantSchemaItem BuildSchemaItem(PathingTask task, string actualFileName)
    {
        var item = new RouteVariantSchemaItem
        {
            LogicalRouteId = task.LogicalRouteId ?? string.Empty,
            ActualVariantFileName = actualFileName,
            SyncPointList = new List<string>(),
            TeleportSyncPointSequence = new List<int[]>()
        };
        if (string.IsNullOrEmpty(task.LogicalRouteId)) return item;

        // 复刻 ConvertWaypointsForTrack 的段切分
        var segments = new List<List<Waypoint>>();
        var temp = new List<Waypoint>();
        foreach (var wp in task.Positions)
        {
            if (wp.Type == "teleport" && temp.Count > 0)
            {
                segments.Add(temp);
                temp = new List<Waypoint>();
            }
            temp.Add(wp);
        }
        segments.Add(temp);

        for (int listIdx = 0; listIdx < segments.Count; listIdx++)
        {
            var seg = segments[listIdx];
            for (int wpIdx = 0; wpIdx < seg.Count; wpIdx++)
            {
                var wp = seg[wpIdx];
                if (!string.IsNullOrEmpty(wp.SyncPointId))
                {
                    item.SyncPointList.Add(wp.SyncPointId);
                }
                if (wp.Type == "teleport")
                {
                    item.TeleportSyncPointSequence.Add(new[] { listIdx, wpIdx });
                }
            }
        }
        return item;
    }

    /// <summary>
    /// 把 KazuhaSyncWaitSeconds 钳到合法范围 [0, 30]。纯函数，PBT 友好。
    /// </summary>
    private static int ClampSyncWait(int value) => Math.Clamp(value, 0, 30);

    /// <summary>
    /// 把 KazuhaSyncTimeoutSeconds 钳到合法范围 [5, 120]。纯函数，PBT 友好。
    /// </summary>
    private static int ClampSyncTimeout(int value) => Math.Clamp(value, 5, 120);

    /// <summary>
    /// 把 KazuhaWaitSkillCdSeconds 钳到合法范围 [3, 10]。纯函数，PBT 友好。
    /// </summary>
    private static int ClampWaitCd(int value) => Math.Clamp(value, 3, 10);

    /// <summary>
    /// 把 KazuhaSecondApproachMaxSteps 钳到合法范围 [1, 30]。纯函数，PBT 友好。
    /// multiplayer-kazuha-collect-point-broadcast: 二段精接近步数上限，每步约 80ms。
    /// </summary>
    private static int ClampSecondApproachMaxSteps(int value) => Math.Clamp(value, 1, 30);

    /// <summary>
    /// 客户端识别本地联机队伍是否含万叶，含则向服务端声明候选身份（kazuha-player-auto-detection）。
    /// 调用时机：
    ///   1. 房主路径：联机队伍切换完成（MultiplayerPartyName/MultiplayerStartAvatarName）后、第一条路线开锄前
    ///   2. 成员路径：JoinHostWorldAsync 完成后、第一条路线开锄前
    ///   3. 多世界第 N 轮：新房间 SetRoomConfigAsync 完成后再次调用
    /// 失败静默：识别失败/SignalR 失败仅 LogWarning 不阻塞主任务。
    /// </summary>
    private async Task DeclareKazuhaCapabilityIfPresentAsync(CancellationToken ct)
    {
        // 守卫语义：声明能否进行只取决于联机连接状态，不再依赖 _multiplayerCoordinator 是否已构造
        // kazuha-declare-after-team-switch: 删除原 if (_multiplayerCoordinator == null) return; 这一行
        if (!_config.MultiplayerEnabled) return;       // 单机路径不感知
        if (!_config.EnableKazuhaSync) return;          // 用户未开启同步不声明

        var client = _coordinatorClientRef;
        if (client == null || !client.IsConnected) return;  // SignalR 未就绪不声明

        // kazuha-declare-multi-recognition-vote:
        // 把"信一次识别"升级为"3 次完整识别 + 严格多数表决"，拦截单次识别抖动导致的假阳性。
        const int sampleCount = 3; // Sample_Count，硬编码 3（不做配置项）
        var votes = new List<bool>(sampleCount);

        for (var i = 0; i < sampleCount; i++)
        {
            BetterGenshinImpact.GameTask.AutoFight.Model.CombatScenes? combatScenes;
            try
            {
                // 3 次均 forceRefresh: true（每次回主界面重新识别，最大化去抖）
                combatScenes = await RunnerContext.Instance.GetCombatScenes(ct, forceRefresh: true);
            }
            catch (OperationCanceledException)
            {
                // Requirement 4：取消必须透传，绝不降级为 false 票、绝不声明
                throw;
            }
            catch (Exception ex)
            {
                // 非取消异常：沿用现有"静默兜底"语义，但降级为该次记 false 票后继续，而非整体 return
                _logger.LogWarning(ex, "[联机][聚物] 第 {Index}/{Total} 次识别队伍场景失败，记为不含万叶一票", i + 1, sampleCount);
                votes.Add(false);
                continue;
            }

            if (combatScenes == null)
            {
                // Requirement 1.3：识别返回 null 记 false 票（保守），保留原"CombatScenes 不可用"日志
                _logger.LogInformation("[联机][聚物] 第 {Index}/{Total} 次 CombatScenes 不可用，记为不含万叶一票", i + 1, sampleCount);
                votes.Add(false);
                continue;
            }

            votes.Add(combatScenes.SelectAvatar("枫原万叶") != null);
        }

        var hasKazuha = KazuhaTeamDetectionDecisions.ShouldDeclareKazuha(votes);

        // 票型诊断日志（事后排查抖动用）
        _logger.LogInformation(
            "[联机][聚物] 万叶识别表决: 票型={Votes} 结论={Result}",
            string.Join(",", votes.Select(v => v ? "T" : "F")),
            hasKazuha ? "含" : "不含");

        if (hasKazuha)
        {
            await client.DeclareKazuhaCapabilityAsync();
            _logger.LogInformation("[联机][聚物] 本地联机队伍含万叶，已向服务端声明候选身份");
        }
        else
        {
            _logger.LogInformation("[联机][聚物] 本地联机队伍不含万叶，跳过声明");
        }
    }

    /// <summary>
    /// 保证 KazuhaWaitSkillCdSeconds &lt; KazuhaSyncTimeoutSeconds 的派生约束。
    /// 当违例时把 KazuhaSyncTimeoutSeconds 提升为 KazuhaWaitSkillCdSeconds + 5。
    /// </summary>
    private static void NormalizeKazuhaTimeoutOrder(AutoHoeingConfig config)
    {
        if (config.KazuhaWaitSkillCdSeconds >= config.KazuhaSyncTimeoutSeconds)
        {
            config.KazuhaSyncTimeoutSeconds = config.KazuhaWaitSkillCdSeconds + 5;
        }
    }

    /// <summary>
    /// 多世界模式下，保存第一任房主的配置，后续轮次都使用这个配置
    /// </summary>
    private RoomConfig? _firstHostConfig;
    
    public static volatile bool SkipPartyWait = false; // 房主点击"立即开始"时设为 true
    public static volatile bool IsWaitingForParty = false; // 是否正在等待组队（进入F2页面）

    public AutoHoeingTask(PathingPartyConfig? partyConfig = null, Dictionary<string, object?>? settings = null, string? groupName = null)
    {
        _partyConfig = partyConfig;
        _settingsOverride = settings;
        _groupName = groupName;
    }

    public async Task Start(CancellationToken ct)
    {
        _taskStartTime = DateTime.Now;
        _ct = ct;
        _config = TaskContext.Instance().Config.AutoHoeingConfig;

        // 如果有配置组传入的覆盖配置，在全局配置的深拷贝上应用，避免污染全局状态
        if (_settingsOverride != null && _settingsOverride.Count > 0)
        {
            var json = JsonSerializer.Serialize(_config);
            _config = JsonSerializer.Deserialize<AutoHoeingConfig>(json) ?? _config;

            // 重置所有可能被其他配置组污染的字段为默认值
            // 这些字段若配置组 settings 里有显式配置，ApplySettingsOverride 会覆盖；
            // 若没有配置，则保持干净的默认值，不受上一次执行的残留影响
            _config.MultiplayerEnabled = false;
            _config.DebugMode = false;
            _config.StartRouteIndex = 0;
            _config.RouteFilterKeywords = "";
            _config.SyncPointMinDistance = 30.0;
            _config.EnableKazuhaSync = false;
            _config.MultiplayerUseFixedFightStrategy = true;  // 默认 ON（保持现有固定策略行为）
            _config.KazuhaSyncWaitSeconds = 1;
            _config.KazuhaSyncTimeoutSeconds = 20;
            _config.KazuhaWaitSkillCdSeconds = 5;
            _config.KazuhaSecondApproachMaxSteps = 6;
            _config.MultiWorldEnabled = false;
            _config.MultiWorldCount = 2;
            _config.FightExtraWaitSeconds = 60;
            _config.RejoinMaxWaitSeconds = 300;

            // === 集体卡死监测重置（multiplayer-mutual-wait-collective-skip §3.10）===
            _config.EnableMutualWaitCollectiveSkip = true;
            _config.MutualWaitMinWaitersRatio = 0.5;
            _config.MutualWaitStableSeconds = 30;
            _config.MaxConsecutiveCollectiveSkips = 3;

            // === 快速同步点抢报重置（multiplayer-fast-sync-host-controlled spec FR4）===
            _config.FastSyncPointEnabled = false;
            _config.FastSyncPathingDistance = 10.0;
            _config.FastSyncTeleportLoadingDelayMs = 0;

            // === 共享战斗配额结束同步重置（multiplayer-shared-fight-end-quorum-sync spec）===
            _config.SharedFightEndQuorumEnabled = false;
            _config.SharedFightEndQuorumRatio = 0.5;

            // === 落后追赶重置（hoeing-lagging-catchup-host-synced-setting spec，对称房主下发字段）===
            _config.EnableLaggingCatchUp = false;
            _config.LagSegmentThreshold = 1;

            // === 基于经验判断停止锄地重置（multiplayer-hoeing-exp-cap-stop）===
            _config.EnableExpCapStop = false;
            _config.ExpCapDetectAllExp = false;

            // === 守护模式重置（hoeing-multiplayer-guard-auto-restart）默认开启===
            _config.HoeingGuardMode = true;
            _config.GuardUnexecutedRouteThreshold = 3;
            // 自动更新线路默认开启
            _config.AutoUpdateRoutes = true;

            // === 单人调试模式重置（hoeing-multiplayer-solo-debug-mode，纯本地）===
            _config.SoloDebugMode = false;

            // === 按周期吃食物重置（multiplayer-hoeing-auto-eat-food-by-period，纯本地）===
            _config.MedicineFoodSlot1 = 1;
            _config.MedicineFoodSlot2 = 2;
            _config.MedicineFoodSlot3 = 3;
            _config.MedicineFoodSlot4 = 4;
            _config.MedicineFoodPeriod1 = 0;
            _config.MedicineFoodPeriod2 = 0;
            _config.MedicineFoodPeriod3 = 0;
            _config.MedicineFoodPeriod4 = 0;
            _config.MedicineFoodRouteKeywords1 = "";
            _config.MedicineFoodRouteKeywords2 = "";
            _config.MedicineFoodRouteKeywords3 = "";
            _config.MedicineFoodRouteKeywords4 = "";
            // 线路重试模式（hoeing-multiplayer-route-retry-mode spec，纯本地）
            _config.RouteRetryModeKeywords = "";

            ApplySettingsOverride();
        }

        // 数据目录：使用JS脚本原有的pathing目录
        _dataDir = Path.Combine(
            AppContext.BaseDirectory,
            "User", "JsScript", "AutoHoeingOneDragon");

        // 检查JS脚本资源是否存在
        if (!Directory.Exists(_dataDir) || !Directory.Exists(Path.Combine(_dataDir, "pathing")))
        {
            _logger.LogError("锄地一条龙资源目录不存在: {Dir}", _dataDir);
            _logger.LogError("请先在「脚本仓库」中订阅并下载「AutoHoeingOneDragon」JS脚本，独立任务依赖该脚本的路线和资源文件");

            // 在UI线程弹窗提示
            await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
            {
                Wpf.Ui.Violeta.Controls.Toast.Warning(
                    "锄地一条龙资源未找到，请先在「脚本仓库」中订阅并下载「AutoHoeingOneDragon」脚本");
            });
            return;
        }

        if (!string.IsNullOrEmpty(_groupName))
        {
            _logger.LogInformation("锄地一条龙任务启动 [配置组: {Group}]，数据目录: {Dir}", _groupName, _dataDir);
        }
        else
        {
            _logger.LogInformation("锄地一条龙任务启动，数据目录: {Dir}", _dataDir);
        }

        try
        {
            // 清空联机锄地队伍识别稳定性缓冲（每次任务启动重置一次）
            // 详见 .kiro/specs/combat-scenes-recognition-stability-buffer/design.md §2.3
            BetterGenshinImpact.GameTask.AutoFight.AutoFightTask.ResetRecognitionStabilityBuffer();

            // 自动更新线路：如果启用了 AutoUpdateRoutes，在开始执行前先调用一次更新联机锄地线路
            if (_config.MultiplayerEnabled && _config.AutoUpdateRoutes)
            {
                await RunAutoUpdateRoutesAsync();
            }

            await RunTask();
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("锄地一条龙任务被取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "锄地一条龙任务异常终止");
        }
        finally
        {
            // 清除全局联机锄地进度信号
            AutoHoeingProgress.Clear();

            // 通道 C 恢复（hoeing-multiplayer-sync-execution-params §C4）：还原成员回填时覆盖的
            // 全局 PickDropsAfterFightSeconds，保证单机 ScanPickTask 后续读到原值。无条件恢复（HasValue 守护）。
            if (_savedGlobalPickDropsAfterFightSeconds.HasValue)
            {
                TaskContext.Instance().Config.AutoFightConfig.PickDropsAfterFightSeconds = _savedGlobalPickDropsAfterFightSeconds.Value;
                _savedGlobalPickDropsAfterFightSeconds = null;
            }
            // 清除联机战斗超时覆盖值
            PathingConditionConfig.MultiplayerFightTimeoutOverride = null;
            // multiplayer-hoeing-selectable-fight-strategy §C5: 复位战斗策略开关静态信号为默认 true（保持现有固定行为）
            PathingConditionConfig.MultiplayerUseFixedFightStrategyOverride = true;
            PathExecutor.CurrentWorldStateMonitor = null;
            PathExecutor.CurrentMultiplayerCoordinator = null;
            // 第2层（hoeing-multiplayer-otherworld-teammate-avatar-misrecognition-fix）：
            // 成对清空 provider，确保单机/下次任务零残留。
            PathingConditionConfig.AuthoritativePlayerCountProvider = null;

            // 房主兜底：确保关闭房间通知已发送
            if (_multiplayerCoordinator != null && _multiplayerCoordinator.IsHost)
            {
                try { await _coordinatorClientRef!.CloseRoomAsync(); }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[联机] finally 中房主发送 CloseRoom 失败（静默忽略）");
                }
            }

            // 停止世界状态监测（需求 2）
            if (_worldStateMonitor != null)
            {
                await _worldStateMonitor.StopAsync();
                _worldStateMonitor = null;
            }

            // 先 dispose coordinator（取消事件订阅），再 dispose linkedStopCts
            // 避免 dispose 后 RoomClosed 事件触发 TriggerCoordinatedStop 时 Cancel 已 dispose 的 CTS
            if (_multiplayerCoordinator != null)
            {
                await _multiplayerCoordinator.DisposeAsync();
                _multiplayerCoordinator = null;
            }

            if (_linkedStopCts != null)
            {
                _linkedStopCts.Dispose();
                _linkedStopCts = null;
            }
            // 多世界模式下 _coordinatorClientRef 由 RunTask 管理，这里只做兜底清理
            if (_coordinatorClientRef != null)
            {
                await _coordinatorClientRef.DisposeAsync();
                _coordinatorClientRef = null;
            }

            // 联机中断时通知用户并退出世界
            if (!string.IsNullOrEmpty(_stopReason) && _config.MultiplayerEnabled)
            {
                // 房主和成员都退出联机世界回到单人世界
                try
                {
                    _logger.LogInformation("[联机] ===== 联机锄地退出（原因: {Reason}）=====", _stopReason);
                    _logger.LogInformation("[联机] {Role}退出联机世界",
                        _config.MultiplayerRole == "member" ? "成员" : "房主");
                    // 使用 60 秒超时，给 LeaveWorldAsync 的 5 次重试留出充足时间。
                    // 每次重试约需 25-30 秒（回主界面 10s + 开F2 + 弹窗 8s + 等待加载 10s + 复核 1.5s），
                    // 60 秒足够 2 次完整重试。15 秒太紧，一次弹窗处理稍慢就超时，后续重试全浪费。
                    using var leaveWorldCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                    var autoParty = new AutoPartyTask();
                    var left = await autoParty.LeaveWorldAsync(leaveWorldCts.Token);
                    if (!left)
                        _logger.LogWarning("[联机] 退出世界未确认成功");

                    // === 基于经验判断停止锄地：仅此路径退世界后额外传送七天神像（multiplayer-hoeing-exp-cap-stop）===
                    // 任务在此结束时角色可能停在怪堆/危险地形，回到自己世界后传送到最近七天神像收尾更安全。
                    // 仅 _expCapStopTriggered 时执行；超时/被踢/掉房/正常跑完等其它停止路径不受影响（零回归）。
                    if (_expCapStopTriggered)
                    {
                        try
                        {
                            _logger.LogInformation("[联机][经验上限] 退世界完成，传送最近七天神像收尾");
                            using var tpStatueCts = new CancellationTokenSource(TimeSpan.FromSeconds(60));
                            await new AutoTrackPath.TpTask(tpStatueCts.Token).TpToStatueOfTheSeven();
                        }
                        catch (Exception exTp)
                        {
                            _logger.LogWarning(exTp, "[联机][经验上限] 传送七天神像失败（忽略）");
                        }

                        // === 基于经验判断停止锄地：输出完整汇总日志（multiplayer-hoeing-exp-cap-stop）===
                        // 经验上限触发的退出在 ProcessRoutesByGroup 中被 OperationCanceledException 中断，
                        // 路线循环末尾的汇总日志（完成数/跳过数/用时）不会被执行。
                        // 这里在 finally 块中补输出，确保用户能看到本次锄地的完整信息。
                        var taskElapsed = DateTime.Now - _taskStartTime;
                        _logger.LogInformation(
                            "[联机][经验上限] ===== 本次锄地汇总（经验上限退出）=====");
                        _logger.LogInformation(
                            "[联机][经验上限] 总用时: {H}时{Min}分{S}秒",
                            (int)taskElapsed.TotalHours, taskElapsed.Minutes, taskElapsed.Seconds);
                        _logger.LogInformation(
                            "[联机][经验上限] 停止原因: {Reason}", _stopReason);
                        _logger.LogInformation(
                            "[联机][经验上限] 角色: {Role}",
                            _config.MultiplayerRole == "member" ? "成员" : "房主");
                        if (_config.MultiWorldEnabled)
                        {
                            _logger.LogInformation(
                                "[联机][经验上限] 多世界模式: 启用（配置 {Count} 轮）",
                                _config.MultiWorldCount);
                        }

                        // 补输出被中断那轮的"本轮锄地结束统计"格式日志（含完成数/跳过数），
                        // 与正常轮次的日志格式一致，方便用户了解最后一轮的执行情况。
                        // 仅当确实进入过 ProcessRoutesByGroup 时（_expCapTotalRoutes > 0）才输出。
                        if (_expCapTotalRoutes > 0)
                        {
                            var roundElapsed = DateTime.Now - _expCapGroupStartTime;
                            var prefix = _expCapRoundPrefix ?? "";
                            _logger.LogInformation(
                                "{Prefix}本轮锄地结束统计（经验上限退出）：用时 {H}时{Min}分{S}秒，完成 {Done} 条 / 跳过 {Skip} 条（计划共 {Total} 条）",
                                prefix,
                                (int)roundElapsed.TotalHours, roundElapsed.Minutes, roundElapsed.Seconds,
                                _expCapCompletedCount, _expCapSkippedCount, _expCapTotalRoutes);
                        }

                        _logger.LogInformation(
                            "[联机][经验上限] ==========================================");
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[联机] 退出世界失败（忽略）");
                }

                var reason = _stopReason;
                await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                {
                    Wpf.Ui.Violeta.Controls.Toast.Warning($"联机中断：{reason}");
                });
            }
        }

        // === 联机锄地守护自动重开（hoeing-multiplayer-guard-auto-restart R2/R3）===
        // 放在 finally 之后：首次运行已完全收尾（连接/监测/CTS 已释放，异常已退世界回到自己世界）。
        // 用 Start 的原始外部 ct（未被 RunTask 内替换污染的局部参数）判定手动停止；满足全部条件则新建实例重开一次。
        var guardUnexecuted = Multiplayer.HoeingGuardDecisions.ComputeUnexecutedCount(
            _guardPlannedRouteCount, _guardExecutedRouteCount);
        var guardShouldRestart = Multiplayer.HoeingGuardDecisions.ShouldRestart(
            guardMode: _config.HoeingGuardMode,
            multiplayerEnabled: _config.MultiplayerEnabled,
            stopReason: _stopReason,
            unexecutedCount: guardUnexecuted,
            threshold: Multiplayer.HoeingGuardDecisions.ClampThreshold(_config.GuardUnexecutedRouteThreshold),
            userCancelled: ct.IsCancellationRequested,   // 原始外部令牌（Start 参数，非字段 _ct）
            expCapStopTriggered: _expCapStopTriggered,
            isGuardRestartRun: _isGuardRestartRun,
            completedNormally: _completedNormally,       // 防线 B（hoeing-guard-false-restart-on-normal-close）
            partyFailed: _partyFailed);                  // 组队失败豁免防线 A（hoeing-multiplayer-party-fail-restart）

        if (guardShouldRestart)
        {
            _logger.LogWarning(
                "[联机][守护] 本次锄地未正常完成（原因: {Reason}，未执行线路数: {Unexec}），触发守护自动重开一次",
                string.IsNullOrEmpty(_stopReason) ? "未执行数达阈值" : _stopReason, guardUnexecuted);
            // 整块包 try/catch：构造/重开若抛异常不得逸出 Start，与首次运行"所有异常被 try/catch 消化、
            // Start 正常返回"的既有契约一致。OCE（重开期间手动停止）与首次运行一样仅记日志。
            try
            {
                try
                {
                    await System.Windows.Application.Current.Dispatcher.InvokeAsync(() =>
                        Wpf.Ui.Violeta.Controls.Toast.Information("锄地守护模式：本次未跑完，正在自动重开一次"));
                }
                catch { /* toast 失败不影响重开 */ }

                var restart = new AutoHoeingTask(_partyConfig, _settingsOverride, _groupName)
                {
                    _isGuardRestartRun = true
                };
                await restart.Start(ct);   // 复用原始外部 ct；重开的运行 isGuardRestartRun=true → 结束时不再重开（R3）
            }
            catch (OperationCanceledException)
            {
                _logger.LogInformation("[联机][守护] 守护重开期间任务被取消");
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[联机][守护] 守护重开执行异常（忽略，不影响本次任务收尾）");
            }
        }
    }

    /// <summary>
    /// 标记组队阶段失败（hoeing-multiplayer-party-fail-restart）：
    /// 写入 _stopReason（重开判定的异常输入）并置 _partyFailed。
    /// 组队失败时 _guardPlannedRouteCount 恒为 0（尚未开锄），未执行线路数为 0 会触发
    /// HoeingGuardDecisions 防线 A 短路；Start 尾部守护块以 _partyFailed 豁免该短路，
    /// 使组队失败（只要有异常原因）也能触发守护重开，保证全队重新组队跑完。
    /// </summary>
    private void MarkPartyFailed(string reason)
    {
        _stopReason = reason;
        _partyFailed = true;
    }

    private async Task InitializeMultiplayerAsync()
    {
        // 进入即置 false；仅在成功走到方法末尾时置 true（见字段注释）。
        _multiplayerPartyReady = false;
        try
        {
            var client = new CoordinatorClient();
            // 隐藏服务器地址前半部分
            var maskedUrl = MaskServerUrl(_config.CoordinatorServerUrl);
            _logger.LogInformation("[联机] 开始连接协调服务器: {Url}", maskedUrl);
            var connected = await client.ConnectAsync(_config.CoordinatorServerUrl, _ct);
            if (!connected)
            {
                _logger.LogWarning("[联机] 连接协调服务器失败，降级为单机模式");
                MarkPartyFailed("连接协调服务器失败");
                return;
            }
            _logger.LogInformation("[联机] 连接成功");

            // 解析白名单
            var whitelist = string.IsNullOrEmpty(_config.RoomWhitelist)
                ? null
                : new List<string>(_config.RoomWhitelist.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));

            // 根据角色决定创建或加入房间
            var isMember = _config.MultiplayerRole == "member";

            if (isMember)
            {
                // 成员模式：根据加入方式获取房间码
                string? roomCodeToJoin = null;

                // 指定房主名为空时，自动降级为随机加入
                if (_config.MemberJoinMode != "random" && string.IsNullOrEmpty(_config.TargetHostName))
                {
                    _logger.LogWarning("[联机] 成员模式指定房主名为空，自动切换为随机加入模式");
                    _config.MemberJoinMode = "random";
                }

                if (_config.MemberJoinMode == "random")
                {
                    // 随机加入：轮询房间列表，逐个尝试有空位的房间，加入失败则换下一个
                    _logger.LogInformation("[联机] 成员模式，随机加入现有房间（超时 {T}s）...", _config.PartyTimeoutSeconds);
                    var deadline = DateTime.UtcNow.AddSeconds(_config.PartyTimeoutSeconds);
                    int attempt = 0;
                    while (DateTime.UtcNow < deadline)
                    {
                        attempt++;
                        var rooms = await client.GetOnlineRoomsAsync();
                        var candidates = rooms.Where(r => r.PlayerCount < r.MaxPlayers).ToList();
                        if (candidates.Count == 0)
                        {
                            _logger.LogInformation("[联机] 暂无可用房间，等待中...（第{N}次）", attempt);
                            await Task.Delay(5000, _ct);
                            continue;
                        }

                        bool joined = false;
                        foreach (var room in candidates)
                        {
                            _logger.LogInformation("[联机] 尝试加入房间: {Code}（房主: {Host}，{N}/{Max}人）",
                                room.Code, room.HostName, room.PlayerCount, room.MaxPlayers);
                            var ok = await client.JoinRoomAsync(room.Code, _config.PlayerName, _config.PlayerUid);
                            if (ok)
                            {
                                roomCodeToJoin = room.Code;
                                _logger.LogInformation("[联机] 成功加入房间: {Code}", room.Code);
                                joined = true;
                                break;
                            }
                            _logger.LogWarning("[联机] 加入房间 {Code} 失败（可能满员或白名单限制），尝试下一个", room.Code);
                        }

                        if (joined) break;

                        _logger.LogInformation("[联机] 本轮所有候选房间均加入失败，5秒后重试（第{N}次）", attempt);
                        await Task.Delay(5000, _ct);
                    }
                    if (roomCodeToJoin == null)
                    {
                        _logger.LogError("[联机] 等待超时（{T}s），未能加入任何可用房间，停止联机锄地", _config.PartyTimeoutSeconds);
                        MarkPartyFailed("加入房间超时（随机模式）");
                        await client.DisposeAsync();
                        return;
                    }
                }
                else
                {
                    // 指定房主名：轮询房间列表，找到对应房主的房间
                    var targetHost = _config.TargetHostName;
                    _logger.LogInformation("[联机] 成员模式，等待房主 [{Host}] 的房间（超时 {T}s）...", targetHost, _config.PartyTimeoutSeconds);
                    var deadline = DateTime.UtcNow.AddSeconds(_config.PartyTimeoutSeconds);
                    int attempt = 0;
                    while (DateTime.UtcNow < deadline)
                    {
                        attempt++;
                        var rooms = await client.GetOnlineRoomsAsync();
                        var target = rooms.FirstOrDefault(r =>
                            r.HostName.Equals(targetHost, StringComparison.OrdinalIgnoreCase) &&
                            r.PlayerCount < r.MaxPlayers);
                        if (target != null)
                        {
                            roomCodeToJoin = target.Code;
                            _logger.LogInformation("[联机] 找到房主 [{Host}] 的房间: {Code}", targetHost, target.Code);
                            break;
                        }
                        _logger.LogInformation("[联机] 未找到房主 [{Host}] 的房间，等待中...（第{N}次）", targetHost, attempt);
                        await Task.Delay(5000, _ct);
                    }
                    if (roomCodeToJoin == null)
                    {
                        _logger.LogError("[联机] 等待超时（{T}s），未找到房主 [{Host}] 的房间，停止联机锄地", _config.PartyTimeoutSeconds, targetHost);
                        MarkPartyFailed("加入房间超时（指定房主）");
                        await client.DisposeAsync();
                        return;
                    }
                }

                // 加入找到的房间（带重试）
                // 注意：随机加入模式下，JoinRoomAsync 已在轮询循环中成功调用，无需重复加入
                _config.CurrentRoomCode = roomCodeToJoin;
                bool joinedRoom = _config.MemberJoinMode == "random"; // 随机模式已在轮询中加入成功
                bool roomClosed = false;
                Action<string> roomClosedHandler = _ => roomClosed = true;
                client.RoomClosed += roomClosedHandler;
                try
                {
                    if (!joinedRoom)
                    {
                        for (int retry = 1; retry <= 10; retry++)
                        {
                            if (roomClosed) { _logger.LogWarning("[联机] 房间已被关闭，停止加入"); break; }
                            _logger.LogInformation("[联机] 尝试加入房间: {Code}（第{N}次）", roomCodeToJoin, retry);
                            joinedRoom = await client.JoinRoomAsync(roomCodeToJoin, _config.PlayerName, _config.PlayerUid);
                            if (joinedRoom) { _logger.LogInformation("[联机] 成功加入房间: {Code}", roomCodeToJoin); break; }
                            _logger.LogWarning("[联机] 加入房间 {Code} 失败，3秒后重试", roomCodeToJoin);
                            await Task.Delay(3000, _ct);
                        }
                    }
                }
                finally
                {
                    client.RoomClosed -= roomClosedHandler;
                }
                if (!joinedRoom)
                {
                    _logger.LogError("[联机] 无法加入房间 {Code}（{Reason}），停止联机锄地",
                        roomCodeToJoin, roomClosed ? "房间已关闭" : "重试耗尽");
                    MarkPartyFailed("无法加入房间");
                    await client.DisposeAsync();
                    return;
                }
                // 加入房间后立刻发心跳，确保服务器知道成员在线
                await client.SendHeartbeatAsync();
            }
            else
            {
                // 房主模式：始终创建新房间（避免重启后尝试加入已不存在的旧房间）
                if (!string.IsNullOrEmpty(_config.CurrentRoomCode))
                {
                    _logger.LogInformation("[联机] 清除旧房间码 {Code}，创建新房间", _config.CurrentRoomCode);
                    _config.CurrentRoomCode = "";
                }
                _logger.LogInformation("[联机] 无房间码，创建新房间");
                var newCode = await client.CreateRoomAsync(_config.PlayerName, whitelist, _config.PlayerUid, _config.ExpectedPlayerCount);
                if (newCode != null)
                {
                    _config.CurrentRoomCode = newCode;
                    _logger.LogInformation("[联机] 已创建房间: {Code}", newCode);
                }
                else
                {
                    _logger.LogWarning("[联机] 创建房间失败，降级为单机模式");
                    MarkPartyFailed("创建房间失败");
                    await client.DisposeAsync();
                    return;
                }
            }

            // 房主/成员标识（用于后续配置同步）
            var isHost = !isMember;
            // kazuha-declare-after-team-switch: 删除此处旧调用——
            // 此位置早于 _multiplayerCoordinator 构造（line 582）且早于 PrepareMultiplayerPartyAndAvatar 完成，
            // 调用窗口非法。新调用点已移至 RunTask 内 InitializeMultiplayerAsync 返回之后。

            // 配置同步：房主上传，成员拉取
            if (isHost)
            {
                var roomConfig = new Multiplayer.Models.RoomConfig
                {
                    SyncPointMinDistance = _config.SyncPointMinDistance,
                    StartRouteIndex = _config.StartRouteIndex,
                    RouteFilterKeywords = _config.RouteFilterKeywords,
                    // 线路重试模式关键词房主同步（hoeing-multiplayer-route-retry-mode §0 / EB-v2-4）
                    RouteRetryModeKeywords = _config.RouteRetryModeKeywords,
                    UseFixedDebugRoutes = _config.UseFixedDebugRoutes,
                    FixedDebugRoutePath = _config.FixedDebugRoutePath,
                    DebugMode = _config.DebugMode,
                    EnableKazuhaSync = _config.EnableKazuhaSync,
                    KazuhaSyncWaitSeconds = _config.KazuhaSyncWaitSeconds,
                    KazuhaSyncTimeoutSeconds = _config.KazuhaSyncTimeoutSeconds,
                    KazuhaWaitSkillCdSeconds = _config.KazuhaWaitSkillCdSeconds,
                    KazuhaSecondApproachMaxSteps = _config.KazuhaSecondApproachMaxSteps,
                    PartyTimeoutSeconds = _config.PartyTimeoutSeconds,
                    MultiWorldEnabled = _config.MultiWorldEnabled,
                    MultiWorldCount = _config.MultiWorldCount,
                    SelectedBuiltinRoute = _config.SelectedBuiltinRoute,
                    FightTimeoutSeconds = _config.FightTimeoutSeconds,
                    FightExtraWaitSeconds = _config.FightExtraWaitSeconds,
                    RejoinMaxWaitSeconds = _config.RejoinMaxWaitSeconds,
                    // === 集体卡死监测（multiplayer-mutual-wait-collective-skip §8.8）===
                    EnableMutualWaitCollectiveSkip = _config.EnableMutualWaitCollectiveSkip,
                    MutualWaitMinWaitersRatio = _config.MutualWaitMinWaitersRatio,
                    MutualWaitStableSeconds = _config.MutualWaitStableSeconds,
                    MaxConsecutiveCollectiveSkips = _config.MaxConsecutiveCollectiveSkips,
                    // === 快速同步点抢报上传（multiplayer-fast-sync-host-controlled spec FR4）===
                    FastSyncPointEnabled = _config.FastSyncPointEnabled,
                    FastSyncPathingDistance = _config.FastSyncPathingDistance,
                    FastSyncTeleportLoadingDelayMs = _config.FastSyncTeleportLoadingDelayMs,
                    // === 共享战斗配额结束同步上传（multiplayer-shared-fight-end-quorum-sync spec）===
                    SharedFightEndQuorumEnabled = _config.SharedFightEndQuorumEnabled,
                    SharedFightEndQuorumRatio = _config.SharedFightEndQuorumRatio,
                    // === 落后追赶上传（hoeing-lagging-catchup-host-synced-setting spec）===
                    EnableLaggingCatchUp = _config.EnableLaggingCatchUp,
                    LagSegmentThreshold = Math.Clamp(_config.LagSegmentThreshold, 1, 3),
                    // === 基于经验判断停止锄地上传（multiplayer-hoeing-exp-cap-stop）===
                    EnableExpCapStop = _config.EnableExpCapStop,
                    ExpCapDetectAllExp = _config.ExpCapDetectAllExp,
                    // === 守护模式上传（hoeing-multiplayer-guard-auto-restart）===
                    HoeingGuardMode = _config.HoeingGuardMode,
                    GuardUnexecutedRouteThreshold = Multiplayer.HoeingGuardDecisions.ClampThreshold(_config.GuardUnexecutedRouteThreshold),
                    // === 自动更新线路上传（hoeing-autoupdate-routes）===
                    AutoUpdateRoutes = _config.AutoUpdateRoutes,
                    // === 执行期参数房主同步（hoeing-multiplayer-sync-execution-params spec §C1）===
                    // 上传源统一取房主当前配置组 _partyConfig（房主锄地实际生效实例，OQ-1）；
                    // _partyConfig 为 null 时退回全局 AutoFightConfig（AutoFight 类）或字段默认。
                    PartyTimeoutAction = _config.PartyTimeoutAction,
                    PickDropsAfterFightEnabled = _partyConfig?.AutoFightConfig?.PickDropsAfterFightEnabled
                        ?? TaskContext.Instance().Config.AutoFightConfig.PickDropsAfterFightEnabled,
                    PickDropsAfterFightSeconds = SyncExecParamsDecisions.ClampSeconds(
                        _partyConfig?.AutoFightConfig?.PickDropsAfterFightSeconds
                        ?? TaskContext.Instance().Config.AutoFightConfig.PickDropsAfterFightSeconds),
                    QuicklySkip = _partyConfig?.QuicklySkip ?? false,
                    CombatScriptEndDelayMs = SyncExecParamsDecisions.ClampDelayMs(
                        _partyConfig?.CombatScriptEndDelayMs ?? 900),
                    OnlyInTeleportRecover = _partyConfig?.OnlyInTeleportRecover ?? false,
                    SwimmingEnabled = _partyConfig?.AutoFightConfig?.SwimmingEnabled
                        ?? TaskContext.Instance().Config.AutoFightConfig.SwimmingEnabled,
                };
                
                // 房主上传配置，带重试机制（最多3次）
                bool uploadSuccess = false;
                for (int retry = 1; retry <= 3; retry++)
                {
                    try
                    {
                        await client.SetRoomConfigAsync(roomConfig);
                        uploadSuccess = true;
                        _logger.LogInformation("[联机] 房主配置已上传到服务器（第{N}次尝试）", retry);
                        break;
                    }
                    catch (Exception ex)
                    {
                        if (retry < 3)
                        {
                            _logger.LogWarning(ex, "[联机] 上传房主配置失败（第{N}次尝试），2秒后重试", retry);
                            await Task.Delay(2000, _ct);
                        }
                        else
                        {
                            _logger.LogError(ex, "[联机] 上传房主配置失败（重试3次后仍失败）");
                        }
                    }
                }
                
                // 多世界模式下配置上传失败是严重错误，必须终止
                if (!uploadSuccess && _config.MultiWorldEnabled)
                {
                    _logger.LogError("[联机] 多世界模式：房主配置上传失败，终止多世界模式");
                    MarkPartyFailed("房主配置上传失败");
                    await client.DisposeAsync();
                    _multiplayerCoordinator = null;
                    return;
                }
                
                // 多世界模式：保存第一任房主的配置
                if (_config.MultiWorldEnabled && uploadSuccess)
                {
                    _firstHostConfig = roomConfig;
                    _logger.LogInformation("[联机] 多世界模式：已锁定第一任房主配置");
                }
            }

            var resolver = new SyncPointResolver();
            _multiplayerCoordinator = new MultiplayerCoordinator(client, resolver, _config);

            _logger.LogInformation("[联机] MultiplayerCoordinator 初始化完成");

            // 联机模式：设置战斗超时覆盖值（不修改原始配置，通过 PathingConditionConfig 传递给 AutoFightHandler）
            PathingConditionConfig.MultiplayerFightTimeoutOverride = _config.FightTimeoutSeconds;
            // multiplayer-hoeing-selectable-fight-strategy §C5: 纯本地开关，读本机 _config（非 hostConfig）
            PathingConditionConfig.MultiplayerUseFixedFightStrategyOverride = _config.MultiplayerUseFixedFightStrategy;

            // 打印所有房主同步的参数
            _logger.LogInformation("[联机] ===== 当前联机参数（房主同步）=====");
            _logger.LogInformation("[联机] 集合点最小距离={MinDist}", _config.SyncPointMinDistance);
            _logger.LogInformation("[联机] 战斗超时={FightTimeout}s，启用万叶聚物同步={Sync}，从第{Start}条路线开始",
                _config.FightTimeoutSeconds, _config.EnableKazuhaSync, _config.StartRouteIndex);
            _logger.LogInformation("[联机] 调试模式={Debug}", _config.DebugMode);
            _logger.LogInformation("[联机] 组队超时={PartyTimeout}s，多世界={MultiWorld}（{Rounds}轮），内置线路={Route}",
                _config.PartyTimeoutSeconds, _config.MultiWorldEnabled, _config.MultiWorldCount, _config.SelectedBuiltinRoute);
            _logger.LogInformation("[联机] 战斗额外等待={FightExtra}s，重新加入最大等待={RejoinMax}s，传送必同步（默认）",
                _config.FightExtraWaitSeconds, _config.RejoinMaxWaitSeconds);
            _logger.LogInformation("[联机] 集体卡死监测：启用={E}, 阈值比例={R:F2}, 稳定秒={S}, 上限={M}",
                _config.EnableMutualWaitCollectiveSkip, _config.MutualWaitMinWaitersRatio,
                _config.MutualWaitStableSeconds, _config.MaxConsecutiveCollectiveSkips);
            _logger.LogInformation("[联机] 快速同步点抢报：启用={E}, 路径距离阈值={D:F1}米, 传送延迟={T}ms",
                _config.FastSyncPointEnabled, _config.FastSyncPathingDistance, _config.FastSyncTeleportLoadingDelayMs);
            _logger.LogInformation("[联机] 共享战斗配额结束同步：启用={E}, 配额比例={R:F2}",
                _config.SharedFightEndQuorumEnabled, _config.SharedFightEndQuorumRatio);
            _logger.LogInformation("[联机] 落后追赶：启用={E}, 触发阈值={T}段（房主同步，房主与成员都参与）",
                _config.EnableLaggingCatchUp, _config.LagSegmentThreshold);
            _logger.LogInformation("[联机] 基于经验判断停止锄地：启用={E}，检测模式={M}（房主同步，全员连续2场无经验后统一结束）",
                _config.EnableExpCapStop, _config.ExpCapDetectAllExp ? "所有经验" : "仅精英经验");
            _logger.LogInformation("[联机] =====================================");

            _multiplayerCoordinator.OnDegraded += reason =>
            {
                _logger.LogWarning("[联机] 已降级为单机模式，原因：{Reason}", reason);
            };

            // 连续超时退出处理（需求 5）
            _multiplayerCoordinator.OnConsecutiveSyncTimeoutExceeded += async (isHost) =>
            {
                if (isHost)
                {
                    _logger.LogError("[联机] 房主连续超时达到上限，广播关闭房间并停止");
                    try { await _coordinatorClientRef!.CloseRoomAsync(); } catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[联机] 广播关闭房间失败（静默忽略，成员靠心跳超时感知）");
                    }
                }
                else
                {
                    _logger.LogError("[联机] 成员连续超时达到上限，上报 Offline 并退出");
                    try { await _coordinatorClientRef!.ReportMemberStatusAsync(MemberStatus.Offline); } catch (Exception ex)
                    {
                        _logger.LogWarning(ex, "[联机] 上报 Offline 失败（静默忽略）");
                    }
                }
                // 降级停止后续同步等待
                _multiplayerCoordinator?.Degrade("连续超时达到上限");
            };

            // 路线一致性验证（延迟到路线筛选后执行，只验证本次要跑的路线）
            _consistencyChecker = new RouteConsistencyChecker();
            _coordinatorClientRef = client;

            // 启动世界状态监测（需求 2）
            // 注入单人调试模式标志（纯本地，显式依赖）：为 true 时 WorldStateMonitor 跳过
            // connected-but-not-in-game 被踢出终止判定，供单人世界调试线路。hoeing-multiplayer-solo-debug-mode。
            _worldStateMonitor = new WorldStateMonitor(client, _config.PlayerUid, _ct, _config.SoloDebugMode, _config.HoeingGuardMode);
            client.WorldStateMonitor = _worldStateMonitor;
            _worldStateMonitor.OnExitConfirmed += async (isHost, reason) =>
            {
                _stopReason = reason;
                if (!isHost) _sessionTerminated = true;
                // 直接 cancel linkedStopCts，确保 _ct 被取消（不依赖 TriggerCoordinatedStop 的 _stopCts）
                try { _linkedStopCts?.Cancel(); }
                catch (ObjectDisposedException) { }
                catch { }
                await _multiplayerCoordinator!.TriggerCoordinatedStop(isHost, reason);
            };
            _worldStateMonitor.OnDroppedFromRoom += async () =>
            {
                _stopReason = "掉出房间且重试失败";
                if (!_multiplayerCoordinator!.IsHost) _sessionTerminated = true;
                try { _linkedStopCts?.Cancel(); }
                catch (ObjectDisposedException) { }
                catch { }
                var isHost = _multiplayerCoordinator!.IsHost;
                await _multiplayerCoordinator.TriggerCoordinatedStop(isHost, "掉出房间且重试失败");
            };
            // 创建 linked CTS，协调停止时通过 Cancel 传播停止信号（需求 2.1）
            _linkedStopCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            _multiplayerCoordinator.StopCts = _linkedStopCts;
            // 关键：将 _ct 替换为 linked token，这样 _linkedStopCts.Cancel() 能取消所有使用 _ct 的操作
            _ct = _linkedStopCts.Token;

            // 成员侧：监听 RoomClosed 事件，设置 _sessionTerminated 确保多世界模式不继续下一轮
            // 房主多世界轮次切换会主动调用 CloseRoomAsync，会触发 SignalR 回环 RoomClosed 广播；
            // 通过 ConsumeSelfClosingRoomFlag 识别为自触发并跳过取消，避免误终止后续轮次。
            // 多世界轮次切换期间（成员尚未离开旧 Group），旧房主关旧房间也会广播 RoomClosed，
            // 必须用 WorldStateMonitor.IsRoundSwitching 抑制，否则成员会被误终止整个任务。
            client.RoomClosed += reason =>
            {
                if (client.ConsumeSelfClosingRoomFlag())
                {
                    _logger.LogInformation("[联机] 收到 RoomClosed（自触发关房）: {Reason}，不取消任务（多世界轮次切换）", reason);
                    return;
                }
                if (_worldStateMonitor != null && _worldStateMonitor.IsRoundSwitching)
                {
                    _logger.LogInformation("[联机] 收到 RoomClosed（轮次切换中）: {Reason}，不取消任务（旧房间关闭广播）", reason);
                    return;
                }
                _stopReason = $"房间已关闭: {reason}";
                _sessionTerminated = true;
                try { _linkedStopCts?.Cancel(); }
                catch (ObjectDisposedException) { }
                catch { }
            };

            // === 基于经验判断停止锄地：全员达上限广播 → 设 _stopReason 走退世界流程（multiplayer-hoeing-exp-cap-stop）===
            // 关键：MultiplayerCoordinator.OnAllReachedExpCap 只做 TriggerCoordinatedStop（置 IsExitTriggered + 房主关房 + Cancel），
            // 不设 _stopReason。这里补一处 task 级订阅，保证房主与成员收到广播后 finally 都能走 LeaveWorldAsync 退回单人世界，
            // 而不是把角色停在房主世界里。与 RoomClosed 处理器同构（设 _stopReason + _sessionTerminated + Cancel，幂等）。
            client.AllReachedExpCap += () =>
            {
                _stopReason = "全员达经验上限，自动结束联机锄地";
                _sessionTerminated = true;
                _expCapStopTriggered = true; // 标记此路径，退世界后额外去七天神像收尾
                try { _linkedStopCts?.Cancel(); }
                catch (ObjectDisposedException) { }
                catch { }
            };

            _worldStateMonitor.Start();
            _logger.LogInformation("[联机] 路线一致性验证将在路线筛选后执行");

            // 自动组队：游戏内加入/等待
            _worldStateMonitor.IsPartyPhase = true;
            var autoParty = new AutoPartyTask();
            if (isHost)
            {
                // 上报房主就绪，通知成员可以开始申请
                await client.ReportHostReadyAsync();

                var hotkeyHint = string.IsNullOrEmpty(TaskContext.Instance().Config.HotKeyConfig.SkipPartyWaitHotkey)
                    ? "（可在快捷键设置中配置快捷键）"
                    : $"（可按快捷键 {TaskContext.Instance().Config.HotKeyConfig.SkipPartyWaitHotkey} 立即开始）";
                _logger.LogInformation("[联机] 当前为房主，已上报就绪，等待成员加入世界 {Hint}", hotkeyHint);
                var partyWhitelist = string.IsNullOrEmpty(_config.RoomWhitelist)
                    ? null
                    : _config.RoomWhitelist.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
                var actualCount = await autoParty.WaitForMembersAsync(
                    _config.ExpectedPlayerCount, partyWhitelist, client, _config.PartyTimeoutSeconds, _ct);

                if (actualCount < 0)
                {
                    // 初始化失败（未检测到主界面等）
                    _logger.LogError("[联机] 自动组队初始化失败，停止联机锄地");
                    _worldStateMonitor.IsPartyPhase = false;
                    MarkPartyFailed("自动组队初始化失败");
                    await client.DisposeAsync();
                    _multiplayerCoordinator = null;
                    return;
                }
                else if (actualCount == 0)
                {
                    // 超时
                    if (_config.PartyTimeoutAction == 1)
                    {
                        // 现有人数锄地
                        var currentCount = client.CurrentRoomPlayerCount;
                        _logger.LogWarning("[联机] 组队超时，以当前 {N} 人开始锄地", currentCount);
                        _config.ExpectedPlayerCount = currentCount > 0 ? currentCount : 1;
                    }
                    else
                    {
                        _logger.LogError("[联机] 组队超时，停止联机锄地");
                        _worldStateMonitor.IsPartyPhase = false;
                        MarkPartyFailed("组队超时");
                        await client.DisposeAsync();
                        _multiplayerCoordinator = null;
                        return;
                    }
                }
                else if (actualCount < _config.ExpectedPlayerCount)
                {
                    // 房主手动跳过（ESC），以实际人数开始
                    _logger.LogInformation("[联机] 房主跳过等待，以实际 {N} 人开始锄地", actualCount);
                    _config.ExpectedPlayerCount = actualCount;
                }
                else
                {
                    _logger.LogInformation("[联机] 人齐，共 {N} 人，开始锄地", actualCount);
                }

                // 房主也上报 WorldJoined，触发 AllWorldJoined 广播给成员
                // 先注册监听再上报，避免信号在上报和等待之间丢失
                var hostAllJoinedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action hostAllJoinedHandler = () => hostAllJoinedTcs.TrySetResult(true);
                client.AllWorldJoined += hostAllJoinedHandler;
                try
                {
                    await client.ReportWorldJoinedAsync();
                    _logger.LogInformation("[联机] 房主已上报就绪，等待所有成员就绪...");

                    using var allJoinedCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.PartyTimeoutSeconds));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_ct, allJoinedCts.Token);
                    using var reg = linkedCts.Token.Register(() => hostAllJoinedTcs.TrySetResult(false));
                    var allJoined = await hostAllJoinedTcs.Task;
                    if (allJoined)
                        _logger.LogInformation("[联机] 所有成员已就绪，开始锄地");
                    else
                        _logger.LogWarning("[联机] 等待所有成员就绪超时，继续执行");
                }
                finally
                {
                    client.AllWorldJoined -= hostAllJoinedHandler;
                }
                _worldStateMonitor.IsPartyPhase = false;
            }
            else
            {
                // 从服务器获取房主 UID（PlayerList 第一个玩家）
                // 等待 PlayerListUpdated 到达（最多 10 秒超时）
                if (string.IsNullOrEmpty(client.HostPlayerUid))
                {
                    _logger.LogInformation("[联机] 等待 PlayerListUpdated 事件到达...");
                    var playerListTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Action<List<PlayerInfo>> playerListHandler = _ => playerListTcs.TrySetResult(true);
                    client.PlayerListUpdated += playerListHandler;
                    try
                    {
                        using var playerListCts = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_ct, playerListCts.Token);
                        using var reg = linkedCts.Token.Register(() => playerListTcs.TrySetResult(false));
                        
                        var received = await playerListTcs.Task;
                        if (!received)
                        {
                            _logger.LogWarning("[联机] 等待 PlayerListUpdated 超时，HostPlayerUid 可能为空");
                        }
                        else
                        {
                            _logger.LogInformation("[联机] PlayerListUpdated 已到达");
                        }
                    }
                    finally
                    {
                        client.PlayerListUpdated -= playerListHandler;
                    }
                }
                
                var hostUid = client.HostPlayerUid;
                if (string.IsNullOrEmpty(hostUid))
                {
                    // 成员拿不到房主 UID = 组队失败，不能在自己世界开锄，提前终止（_multiplayerPartyReady 保持 false）
                    _logger.LogError("[联机] 无法获取房主 UID，组队失败，停止联机锄地");
                    _worldStateMonitor.IsPartyPhase = false;
                    MarkPartyFailed("无法获取房主 UID");
                    await client.DisposeAsync();
                    _multiplayerCoordinator = null;
                    return;
                }
                else
                {
                    // 等待房主就绪后再申请加入
                    _logger.LogInformation("[联机] 等待房主进入就绪状态...");
                    var hostReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                    Action<bool> readyHandler = ready => { if (ready) hostReadyTcs.TrySetResult(true); };
                    client.HostReadyChanged += readyHandler;
                    try
                    {
                        // 先查一次当前状态
                        if (await client.IsHostReadyAsync())
                        {
                            _logger.LogInformation("[联机] 房主已就绪");
                        }
                        else
                        {
                            using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.PartyTimeoutSeconds));
                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_ct, readyCts.Token);
                            using var reg = linkedCts.Token.Register(() => hostReadyTcs.TrySetResult(false));

                            // 同时启动轮询，防止事件推送丢失（服务器可能不持久化就绪状态）
                            _ = Task.Run(async () =>
                            {
                                while (!hostReadyTcs.Task.IsCompleted)
                                {
                                    try
                                    {
                                        await Task.Delay(3000, linkedCts.Token);
                                        if (await client.IsHostReadyAsync())
                                            hostReadyTcs.TrySetResult(true);
                                    }
                                    catch { break; }
                                }
                            }, linkedCts.Token);

                            var ready = await hostReadyTcs.Task;
                            if (!ready)
                            {
                                _logger.LogError("[联机] 等待房主就绪超时，停止联机锄地");
                                _worldStateMonitor.IsPartyPhase = false;
                                MarkPartyFailed("等待房主就绪超时");
                                await client.DisposeAsync();
                                _multiplayerCoordinator = null;
                                return;
                            }
                            _logger.LogInformation("[联机] 房主已就绪");
                        }
                    }
                    finally
                    {
                        client.HostReadyChanged -= readyHandler;
                    }

                    // 房主就绪后拉取配置（确保房主已上传配置）
                    RoomConfig? hostConfig = null;
                    for (int retry = 1; retry <= 3; retry++)
                    {
                        hostConfig = await client.GetRoomConfigAsync();
                        if (hostConfig != null)
                        {
                            _logger.LogInformation("[联机] 成功拉取房主配置（第{N}次尝试）", retry);
                            break;
                        }
                        if (retry < 3)
                        {
                            _logger.LogWarning("[联机] 拉取房主配置失败（第{N}次尝试），2秒后重试", retry);
                            await Task.Delay(2000, _ct);
                        }
                    }

                    if (hostConfig != null)
                    {
                        _config.SyncPointMinDistance = hostConfig.SyncPointMinDistance;
                        _config.StartRouteIndex = hostConfig.StartRouteIndex;
                        _config.RouteFilterKeywords = hostConfig.RouteFilterKeywords;
                        // 线路重试模式关键词房主同步（hoeing-multiplayer-route-retry-mode §0 / EB-v2-4）
                        _config.RouteRetryModeKeywords = hostConfig.RouteRetryModeKeywords;
                        _config.UseFixedDebugRoutes = hostConfig.UseFixedDebugRoutes;
                        _config.FixedDebugRoutePath = hostConfig.FixedDebugRoutePath;
                        _config.DebugMode = hostConfig.DebugMode;
                        _config.EnableKazuhaSync = hostConfig.EnableKazuhaSync;
                        _config.KazuhaSyncWaitSeconds = hostConfig.KazuhaSyncWaitSeconds;
                        _config.KazuhaSyncTimeoutSeconds = hostConfig.KazuhaSyncTimeoutSeconds;
                        _config.KazuhaWaitSkillCdSeconds = hostConfig.KazuhaWaitSkillCdSeconds;
                        _config.KazuhaSecondApproachMaxSteps = hostConfig.KazuhaSecondApproachMaxSteps;
                        _config.PartyTimeoutSeconds = hostConfig.PartyTimeoutSeconds;
                        _config.MultiWorldEnabled = hostConfig.MultiWorldEnabled;
                        _config.MultiWorldCount = hostConfig.MultiWorldCount;
                        _config.SelectedBuiltinRoute = hostConfig.SelectedBuiltinRoute;
                        _config.FightExtraWaitSeconds = hostConfig.FightExtraWaitSeconds;
                        _config.RejoinMaxWaitSeconds = hostConfig.RejoinMaxWaitSeconds;
                        // === 集体卡死监测同步（multiplayer-mutual-wait-collective-skip §8.8）===
                        _config.EnableMutualWaitCollectiveSkip = hostConfig.EnableMutualWaitCollectiveSkip;
                        _config.MutualWaitMinWaitersRatio = hostConfig.MutualWaitMinWaitersRatio;
                        _config.MutualWaitStableSeconds = hostConfig.MutualWaitStableSeconds;
                        _config.MaxConsecutiveCollectiveSkips = hostConfig.MaxConsecutiveCollectiveSkips;
                        // === 快速同步点抢报同步（multiplayer-fast-sync-host-controlled spec FR5）===
                        _config.FastSyncPointEnabled = hostConfig.FastSyncPointEnabled;
                        _config.FastSyncPathingDistance = FastSyncDecisions.ClampPathingDistance(hostConfig.FastSyncPathingDistance);
                        _config.FastSyncTeleportLoadingDelayMs = FastSyncDecisions.ClampTeleportDelay(hostConfig.FastSyncTeleportLoadingDelayMs);
                        // === 共享战斗配额结束同步同步（multiplayer-shared-fight-end-quorum-sync spec）===
                        _config.SharedFightEndQuorumEnabled = hostConfig.SharedFightEndQuorumEnabled;
                        _config.SharedFightEndQuorumRatio = SharedFightEndQuorumDecisions.ClampRatio(hostConfig.SharedFightEndQuorumRatio);
                        // === 落后追赶同步（hoeing-lagging-catchup-host-synced-setting spec，成员回填房主下发值）===
                        _config.EnableLaggingCatchUp = hostConfig.EnableLaggingCatchUp;
                        _config.LagSegmentThreshold = Math.Clamp(hostConfig.LagSegmentThreshold, 1, 3);
                        // === 基于经验判断停止锄地同步（multiplayer-hoeing-exp-cap-stop，成员回填房主下发值）===
                        _config.EnableExpCapStop = hostConfig.EnableExpCapStop;
                        _config.ExpCapDetectAllExp = hostConfig.ExpCapDetectAllExp;
                        // === 守护模式同步（hoeing-multiplayer-guard-auto-restart，成员回填房主下发值）===
                        _config.HoeingGuardMode = hostConfig.HoeingGuardMode;
                        _config.GuardUnexecutedRouteThreshold = Multiplayer.HoeingGuardDecisions.ClampThreshold(hostConfig.GuardUnexecutedRouteThreshold);
                        // === 自动更新线路同步（hoeing-autoupdate-routes，成员回填房主下发值）===
                        _config.AutoUpdateRoutes = hostConfig.AutoUpdateRoutes;

                        // === 执行期参数房主同步（hoeing-multiplayer-sync-execution-params §C2）===
                        ApplyHostExecParams(hostConfig);

                        // 联机模式：设置战斗超时覆盖值（不修改原始配置）
                        PathingConditionConfig.MultiplayerFightTimeoutOverride = hostConfig.FightTimeoutSeconds;
                        // multiplayer-hoeing-selectable-fight-strategy §C5: 战斗策略开关纯本地，读本机 _config（不读 hostConfig）
                        PathingConditionConfig.MultiplayerUseFixedFightStrategyOverride = _config.MultiplayerUseFixedFightStrategy;

                        // 多世界模式：保存第一任房主的配置
                        if (_config.MultiWorldEnabled)
                        {
                            _firstHostConfig = hostConfig;
                            _logger.LogInformation("[联机] 多世界模式：已保存第一任房主配置");
                        }

                        _logger.LogInformation(
                            "[联机] 已同步房主配置：最小距离={Dist}，快速同步点抢报启用={FastEn}，路径距离阈值={FastDist:F1}米，传送延迟={FastDelay}ms",
                            hostConfig.SyncPointMinDistance,
                            _config.FastSyncPointEnabled, _config.FastSyncPathingDistance, _config.FastSyncTeleportLoadingDelayMs);
                    }
                    else
                    {
                        // 多世界模式下配置同步失败是严重错误，必须终止
                        if (_config.MultiWorldEnabled)
                        {
                            _logger.LogError("[联机] 多世界模式：无法获取房主配置（重试3次后仍失败），终止多世界模式");
                            _worldStateMonitor.IsPartyPhase = false;
                            MarkPartyFailed("无法获取房主配置");
                            await client.DisposeAsync();
                            _multiplayerCoordinator = null;
                            return;
                        }
                        _logger.LogWarning("[联机] 无法获取房主配置，使用本地配置");
                    }

                    // 加入世界前先确认自己在房间花名册，掉了就用房间码重新加入（网络抖动容错）
                    await client.EnsureInRoomAsync(_ct);

                    _logger.LogInformation("[联机] 当前为成员，尝试加入房主世界，房主 UID: {Uid}", AutoPartyTask.MaskUid(hostUid));
                    var joinOk = await autoParty.JoinHostWorldAsync(hostUid, _ct);
                    if (joinOk)
                    {
                        _logger.LogInformation("[联机] 已进入房主世界，等待所有人就绪...");

                        // 先注册 AllWorldJoined 监听，再上报，避免信号在上报和等待之间丢失
                        var waitTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                        Action handler = () => waitTcs.TrySetResult(true);
                        client.AllWorldJoined += handler;
                        try
                        {
                            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.PartyTimeoutSeconds));
                            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_ct, timeoutCts.Token);
                            using var reg = linkedCts.Token.Register(() =>
                            {
                                if (_ct.IsCancellationRequested)
                                    waitTcs.TrySetCanceled(_ct);
                                else
                                    waitTcs.TrySetResult(false);
                            });

                            // 周期性确认在房 + 重报已加入世界（每 5s），杜绝单次上报因网络抖动丢失。
                            // 循环内首次立即执行一次（无初始延迟），收到 AllWorldJoined 或超时/取消即停止。
                            _ = Task.Run(async () =>
                            {
                                try
                                {
                                    while (!waitTcs.Task.IsCompleted && !linkedCts.Token.IsCancellationRequested)
                                    {
                                        await client.EnsureInRoomAsync(linkedCts.Token);
                                        await client.ReportWorldJoinedAsync();
                                        await Task.Delay(5000, linkedCts.Token);
                                    }
                                }
                                catch (OperationCanceledException) { /* 正常结束（就绪或超时） */ }
                                catch (Exception ex) { _logger.LogWarning(ex, "[联机] 周期上报已加入世界异常（忽略）"); }
                            }, linkedCts.Token);

                            var allReady = await waitTcs.Task;
                            if (allReady)
                            {
                                _logger.LogInformation("[联机] 所有人已就绪，开始锄地");
                            }
                            else
                            {
                                _logger.LogWarning("[联机] 等待组队超时，退出房间，结束任务");
                                _worldStateMonitor.IsPartyPhase = false;
                                MarkPartyFailed("等待组队超时");
                                _multiplayerCoordinator?.Degrade("组队超时");
                                return;
                            }
                        }
                        finally
                        {
                            client.AllWorldJoined -= handler;
                        }
                        _worldStateMonitor.IsPartyPhase = false;
                    }
                    else
                    {
                        _logger.LogError("[联机] 加入房主世界失败，停止联机锄地");
                        _worldStateMonitor.IsPartyPhase = false;
                        MarkPartyFailed("加入房主世界失败");
                        _multiplayerCoordinator?.Degrade("加入房主世界失败");
                        return;
                    }
                }
            }

            // 注入到执行引擎
            _executionEngine?.SetCoordinator(_multiplayerCoordinator);
            _executionEngine?.SetWorldStateMonitor(_worldStateMonitor);
            _logger.LogInformation("[联机] 初始化完成，房间码：{Code}", _config.CurrentRoomCode);

            // 房主路径：开锄前锁定房间，从此服务端拒绝非重连新玩家（spec lock-room-after-start §4.2 / bugfix §2.8）
            // 成员路径不调（成员无房主权限，调了服务端也会拒）
            if (client.IsHost)
            {
                // 重开续跑：算出已完成房主 UID 上报，服务端裁剪权威序列，全组跳过已完成世界。
                var completed = ComputeCompletedHostUidsForReport(client);
                await client.MarkRoomStartedAsync(completed);
            }

            // 只有走到这里（所有组队/加入/等待环节均未提前 return）才算组队成功，允许后续开锄。
            // 任何失败/降级/超时路径都在上方提前 return，本标志保持 false，RunTask 据此拦截开锄。
            _multiplayerPartyReady = true;

            // 锄地房间信号：已成功加入联机锄地房间（IsInRoom=true）即视为"锄地中"，
            // 立即同步全局进度载体，供 IPC task.status 读取。离开房间由 Start finally 的 Clear() 复位。
            lock (AutoHoeingProgress.Sync)
            {
                AutoHoeingProgress.IsRunning = true;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[联机] 初始化异常，降级为单机模式");
            if (_worldStateMonitor != null) _worldStateMonitor.IsPartyPhase = false;
            // 手动停止（OperationCanceledException）不标记组队失败——Start 尾部 userCancelled 会兜底不重开。
            if (ex is not OperationCanceledException)
                MarkPartyFailed("初始化异常");
            _multiplayerCoordinator = null;
        }
    }

    /// <summary>
    /// 尝试启动联机助手（MultiplayerHoeingAssistant.exe）。启动失败不抛异常，仅记日志。
    /// </summary>
    private void TryLaunchAssistant()
    {
        try
        {
            var assistantPath = Path.Combine(AppContext.BaseDirectory, @"Tools\MultiplayerHoeingAssistant\MultiplayerHoeingAssistant.exe");
            if (File.Exists(assistantPath))
            {
                var process = new System.Diagnostics.Process
                {
                    StartInfo = new System.Diagnostics.ProcessStartInfo
                    {
                        FileName = assistantPath,
                        Arguments = "--minimized",
                        UseShellExecute = true
                    }
                };
                process.Start();
                _logger.LogInformation("[联机] 已自动启动联机助手（最小化）");
            }
            else
            {
                _logger.LogWarning("[联机] 联机助手不存在，无法自动启动: {Path}", assistantPath);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 自动启动联机助手失败");
        }
    }

    /// <summary>
    /// 联机前准备：切换队伍和角色
    /// </summary>
    private async Task PrepareMultiplayerPartyAndAvatar()
    {
        if (!_config.MultiplayerEnabled)
        {
            return;
        }
        
        _logger.LogInformation("[联机] 开始准备联机队伍和角色");

        // 0a. 设置世界权限为确认后才能加入
        await SetWorldPermissionToConfirmJoin();
        
        // 1. 切换队伍
        if (!string.IsNullOrEmpty(_config.MultiplayerPartyName))
        {
            await SwitchMultiplayerParty();
        }
        
        // 2. 切换角色
        if (!string.IsNullOrEmpty(_config.MultiplayerStartAvatarName))
        {
            await SwitchMultiplayerAvatar();
        }

        // 2.5 开锄前换武器（hoeing-multiplayer-preswitch-weapon）：仅首轮、切队+切角色之后执行。
        // 多世界轮换后续轮次 _preSwitchWeaponDone 已为真 → 跳过（不重复换武器）。
        if (!_preSwitchWeaponDone)
        {
            await ExecutePreSwitchWeaponAsync();
            _preSwitchWeaponDone = true; // 无论是否有可执行行都置真，避免后续轮次重复判定
        }

        _logger.LogInformation("[联机] 队伍和角色准备完成");
    }

    /// <summary>
    /// 切换联机队伍（带重试）
    /// </summary>
    private async Task SwitchMultiplayerParty()
    {
        // 避免重复切换队伍
        if (_teamAlreadySwitched)
        {
            _logger.LogInformation("[联机] 队伍已切换过，跳过重复切换");
            return;
        }
        
        bool switchSuccess = false;
        
        for (int retry = 1; retry <= 5; retry++)
        {
            bool attemptFailed = false;
            try
            {
                switchSuccess = await new SwitchPartyTask().Start(_config.MultiplayerPartyName, _ct);
                if (switchSuccess)
                {
                    _logger.LogInformation("[联机] 切换队伍成功（第{N}次尝试）", retry);
                    _teamAlreadySwitched = true; // 标记已切换
                    break;
                }

                // Start() 返回 false 也算本次失败
                attemptFailed = true;
            }
            catch (Exception ex)
            {
                // 战斗中打不开队伍界面会抛 PartySetupFailedException，
                // 与返回 false 统一当作"本次换队失败"处理，走同一兜底分支
                attemptFailed = true;
                _logger.LogWarning(ex, "[联机] 切换队伍异常（第{N}次尝试）", retry);
            }

            // 本次失败（无论 false 还是抛异常）且仍有重试机会：
            // 第 1 次失败起，每次失败都先传送七天神像再重试（传送时世界时停，战斗不阻挡传送）
            if (SwitchPartyRetryDecisions.ShouldTeleportBeforeRetry(retry, attemptFailed))
            {
                _logger.LogWarning("[联机] 切换队伍失败/异常（第{N}次尝试），传送七天神像后重试", retry);
                try
                {
                    await new TpTask(_ct).TpToStatueOfTheSeven(requireLoadingScreen: true);
                    await Task.Delay(1000, _ct);
                }
                catch (Exception ex)
                {
                    // 传送失败仅告警，不中断；靠后续重试 + 战斗自然结束兜底
                    _logger.LogWarning(ex, "[联机] 传送到七天神像失败，继续重试");
                }
            }
        }
        
        if (!switchSuccess)
        {
            _logger.LogWarning("[联机] 切换队伍失败（重试5次后仍失败），将使用当前队伍继续联机");
        }
        else
        {
            await Task.Delay(500, _ct); // 等待队伍切换完成
        }
    }

    /// <summary>
    /// 切换联机角色（带重试）
    /// </summary>
    private async Task SwitchMultiplayerAvatar()
    {
        bool avatarSwitchSuccess = false;
        
        for (int retry = 1; retry <= 3; retry++)
        {
            try
            {
                // 获取当前战斗场景
                using var ra = CaptureToRectArea();
                var combatScenes = new CombatScenes().InitializeTeam(ra);
                
                if (combatScenes.CheckTeamInitialized())
                {
                    // 通过角色名称查找角色
                    var targetAvatar = combatScenes.SelectAvatar(_config.MultiplayerStartAvatarName);
                    
                    if (targetAvatar != null)
                    {
                        // 尝试切换到该角色
                        if (targetAvatar.TrySwitch(10))
                        {
                            avatarSwitchSuccess = true;
                            _logger.LogInformation("[联机] 切换到角色[{Name}]成功（第{R}次尝试）", 
                                _config.MultiplayerStartAvatarName, retry);
                            break;
                        }
                    }
                    else
                    {
                        // 当前队伍没有该角色，切换到1号位
                        _logger.LogWarning("[联机] 当前队伍没有角色[{Name}]，切换到1号位角色", 
                            _config.MultiplayerStartAvatarName);
                        var firstAvatar = combatScenes.SelectAvatar(1);
                        if (firstAvatar.TrySwitch(10))
                        {
                            avatarSwitchSuccess = true;
                            _logger.LogInformation("[联机] 切换到1号位角色[{Name}]成功", firstAvatar.Name);
                            break;
                        }
                    }
                }
                
                if (retry < 3)
                {
                    _logger.LogWarning("[联机] 切换角色失败（第{N}次尝试），1秒后重试", retry);
                    await Task.Delay(1000, _ct);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[联机] 切换角色异常（第{N}次尝试）", retry);
                if (retry < 3)
                {
                    await Task.Delay(1000, _ct);
                }
            }
        }
        
        if (!avatarSwitchSuccess)
        {
            _logger.LogWarning("[联机] 切换角色失败（重试3次后仍失败），将使用当前角色继续联机");
        }
    }

    /// <summary>
    /// 开锄前换武器编排（hoeing-multiplayer-preswitch-weapon）：
    /// Row1 -> Row2 顺序，仅处理 Row_Enabled 行（勾选且 Character/Weapon 非空，不去重）；
    /// 每行用其 6 参数作 settings 覆盖构造 OcrSwitchWeaponTask 并 Start(ct)；
    /// 全部完成后切回 MultiplayerStartAvatarName（仅当至少执行过一行）。
    /// 单行非取消异常记 LogWarning 不阻断后续行与开锄；OperationCanceledException 透传不吞。
    /// </summary>
    private async Task ExecutePreSwitchWeaponAsync()
    {
        var enabledRows = _preSwitchWeaponRows
            .Where(r => Multiplayer.PreSwitchWeaponDecisions.IsRowEnabled(r))
            .ToList();
        if (enabledRows.Count == 0)
        {
            _logger.LogInformation("[开锄前换武器] 无启用行，跳过");
            return; // 无行执行 → 不额外切回起始角色
        }

        bool anyExecuted = false;
        foreach (var row in enabledRows)
        {
            _ct.ThrowIfCancellationRequested(); // 取消透传，停止后续行
            try
            {
                var rowSettings = Multiplayer.PreSwitchWeaponDecisions.BuildRowSettingsDict(row);
                _logger.LogInformation("[开锄前换武器] 为角色[{Char}]换武器[{Weapon}]", row.Character, row.Weapon);
                await new OcrSwitchWeapon.OcrSwitchWeaponTask(_partyConfig, rowSettings, _groupName).Start(_ct);
                anyExecuted = true;
            }
            catch (OperationCanceledException)
            {
                throw; // 取消异常不吞，透传中断
            }
            catch (Exception ex)
            {
                // 单行换武器失败记结构化警告日志，不阻断后续行与开锄流程
                _logger.LogWarning(ex, "[开锄前换武器] 角色[{Char}]换武器失败，跳过该行继续", row.Character);
            }
        }

        // 至少执行过一行 → 切回联机起始角色（复用现有联机切角色能力，含重试/兜底）
        if (anyExecuted && !string.IsNullOrEmpty(_config.MultiplayerStartAvatarName))
        {
            try
            {
                _ct.ThrowIfCancellationRequested();
                await SwitchMultiplayerAvatar();
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                // 切回起始角色非取消异常记日志后继续后续联机流程（开锄阶段本身也有角色识别兜底）
                _logger.LogWarning(ex, "[开锄前换武器] 切回起始角色失败，继续后续联机流程");
            }
        }
    }

    private async Task RunTask()
    {
        // 1. 加载配置
        var accountName = string.IsNullOrEmpty(_config.AccountName) ? "默认账户" : _config.AccountName;
        LoadGroupSettings(accountName, _config.MultiplayerEnabled);

        // hoeing-multiplayer-account-name-config：任务启动即确保账户 CD 记录文件存在。
        // 无论单机/联机、房主/成员，新账户名一经运行即在 records 目录生成 {账户名}.json，
        // 不再依赖"某条路线成功跑完才写盘"（联机被踢/中断时那条路径不会触发）。
        _cdManager.Load(_dataDir, accountName);
        _cdManager.EnsureFileExists();

        // 2. 解析时间限制
        _timeChecker.ParseRestrictions(_config.NoRunPeriod);

        // 3. 加载怪物信息
        var monsterInfoPath = Path.Combine(_dataDir, "assets", "monsterInfo.json");
        _monsterRepo.Load(monsterInfoPath);

        // 3.5 初始化并发服务
        var assetsDir = Path.Combine(_dataDir, "assets");
        _pickupService.LoadTemplates(assetsDir, _config.PickupMode);
        _anomalyDetector.LoadTemplates(assetsDir);
        _blacklistManager.Load(_dataDir, accountName);
        _blacklistManager.LoadItemFullTemplate(assetsDir);
        _cookingService.LoadTemplates(assetsDir);
        _executionEngine = new RouteExecutionEngine(
            _pickupService, _anomalyDetector, _dumperService, _blacklistManager, _config, _partyConfig);

        // 按线路切角色（hoeing-multiplayer-per-route-switch-roles）：构造 Provider 并注入 engine。
        // Provider 封装 RouteId 推导 + Hook 构造；仅联机 + 配了角色时生效（engine 内门控）。
        _perRouteSwitchProvider = new Multiplayer.PerRouteSwitchRolesProvider(
            _perRouteSwitchRoles, _partyConfig, _groupName, _logger);
        _executionEngine.SetPerRouteSwitchProvider(_perRouteSwitchProvider);

        // 联机模式初始化
        if (_config.MultiplayerEnabled)
        {
            // 联机锄地第一时间：自动启动联机助手（如果配置了自动启动）
            if (_config.AutoLaunchAssistant)
            {
                TryLaunchAssistant();
            }

            // 联机模式下禁用自动领取派遣，避免打断锄地流程
            // （_partyConfig 为 null 时由 RouteExecutionEngine 在每条路线执行前处理）
            if (_partyConfig != null)
                _partyConfig.DisableAutoFetchDispatch = true;

            // 任务最开始：检测当前是否在多人联机世界（前一次任务残留），如是则先退出。
            // 必须在所有联机准备步骤之前一次性做完，且只做一次——
            // 否则放在 PrepareMultiplayerPartyAndAvatar 里会被多世界每一轮重复调用，
            // 把刚刚 WaitForMembersAsync 拉进来的成员误当残留踢出去。
            await EnsureInOwnWorldBeforeMultiplayerAsync();

            // 联机前准备：切换队伍和角色
            await PrepareMultiplayerPartyAndAvatar();
            
            await InitializeMultiplayerAsync();

            // 组队守卫（方案A）：只要联机组队没有达到"可开锄"状态（连接失败 / 加入房间失败 /
            // 拿不到房主 UID / 房主未就绪超时 / 加入房主世界失败 / 等待全员就绪超时 / 初始化异常等），
            // 一律不进入锄地，杜绝成员在自己世界单跑、房主未满员被动开跑。
            // 例外：单人调试模式（SoloDebugMode）是开发者故意独自跑线路，组队本就不会满员，必须放行。
            // 注意：房主主动"立即开始(当前人数)" / ESC 跳过 / 超时按现有人数锄地 这三条属于人为选择，
            // 它们走的是 WaitForMembersAsync 正常返回 + 成功走到 InitializeMultiplayerAsync 末尾，
            // _multiplayerPartyReady 为 true，不受本守卫影响。
            if (!_multiplayerPartyReady && !_config.SoloDebugMode)
            {
                _logger.LogError("[联机] 组队未成功（未达到满员/就绪条件），停止锄地，不在自己世界单独运行");
                if (_worldStateMonitor != null) _worldStateMonitor.IsPartyPhase = false;
                try
                {
                    if (_coordinatorClientRef != null)
                    {
                        await _coordinatorClientRef.DisposeAsync();
                        _coordinatorClientRef = null;
                    }
                }
                catch (Exception ex)
                {
                    // 清理连接失败可恢复：任务即将结束，连接终会随进程释放，仅记日志不阻断退出。
                    _logger.LogWarning(ex, "[联机] 组队失败后释放协调连接异常（忽略）");
                }
                _multiplayerCoordinator = null;
                return;
            }

            // kazuha-declare-after-team-switch: 合法声明窗口
            // 时序保证：PrepareMultiplayerPartyAndAvatar 已返回（tTeam ≤ tCall）
            //         + InitializeMultiplayerAsync 内 _multiplayerCoordinator 已构造（tCoord ≤ tCall）
            //         + 第一条路线尚未开锄（tCall < tRoute）
            // 多世界第 1 轮幂等：本调用 + RunMultiWorldAsync round==0 内同名调用，由服务端 Hub 幂等检查保证安全
            await DeclareKazuhaCapabilityIfPresentAsync(_ct);

            // 多世界模式：循环执行多轮
            if (_config.MultiWorldEnabled && _coordinatorClientRef != null)
            {
                // 在进入多世界循环前先加载 CD 记录（单世界路径在下面加载，多世界需要在这里加载）
                _cdManager.Load(_dataDir, accountName);
                await RunMultiWorldAsync(accountName);
                return;
            }
        }

        // 4. 加载CD记录
        _cdManager.Load(_dataDir, accountName);

        // 5. 构建分组标签
        var groupTags = BuildGroupTags();

        await RunSingleWorldAsync(accountName, groupTags);

        // 防线 B 不在此置位（hoeing-guard-false-restart-on-normal-close 缺陷 2）：
        // RunSingleWorldAsync / ProcessRoutesByGroup 内有多条"正常 return 但并未跑完"的路径
        // （成员等房主路线列表超时 90s、路线一致性校验失败、变体 schema 校验失败、
        //  服务端不支持变体、线路同步协调 Abort、固定调试线路无文件……），
        // 它们均不抛异常，方法照常返回，此处无法区分"跑完"与"中途放弃"，置位必然误发完工单。
        // 单世界正常跑完时未执行线路数天然为 0，防线 A 已足够识别，无需完工单。

        // 联机模式任务结束：成员退回自己的世界
        if (_config.MultiplayerEnabled && _coordinatorClientRef != null)
        {
            var isMember = _config.MultiplayerRole == "member";
            if (isMember)
            {
                _logger.LogInformation("[联机] 任务结束，成员退回自己的世界");
                try
                {
                    var autoParty = new AutoPartyTask();
                    var left = await autoParty.LeaveWorldAsync(_ct);
                    if (!left)
                        _logger.LogWarning("[联机] 退出世界未确认成功");
                }
                catch (Exception ex) { _logger.LogWarning(ex, "[联机] 退出世界失败，忽略"); }
            }
        }
    }

    /// <summary>
    /// 多世界连续锄地：按玩家列表顺序，每轮换一个房主，共 MultiWorldCount 轮
    /// </summary>
    private async Task RunMultiWorldAsync(string accountName)
    {
        var client = _coordinatorClientRef!;

        // 多世界轮换序列：优先采用服务端权威序列（首任房主 MarkRoomStarted 时生成，各端一致），
        // 旧服务端 / 未填 UID / 查询失败 → 降级本地 CurrentPlayerList 快照（现状行为）。
        // multiplayer-server-authoritative-round-order §B2。
        var snapshot = new List<PlayerInfo>(client.CurrentPlayerList);
        List<string>? authoritativeUids = null;
        if (!string.IsNullOrEmpty(_config.PlayerUid))
        {
            // 短重试给房主 MarkRoomStarted 留时间（成员可能早于房主生成序列），最多 ~3s。
            for (int i = 0; i < 6; i++)
            {
                authoritativeUids = await client.GetRoundHostOrderAsync();
                if (authoritativeUids != null && authoritativeUids.Count > 0) break;
                await Delay(500, _ct);
            }
        }

        List<PlayerInfo> playerOrder = RoundHostOrderClientMapping.BuildPlayerOrder(snapshot, authoritativeUids);
        if (RoundHostOrderClientMapping.UsedAuthoritative(authoritativeUids))
        {
            _logger.LogInformation("[多世界] 采用服务端权威轮换序列（{N} 项）: {Order}",
                playerOrder.Count, string.Join(" → ", playerOrder.Select(p => p.PlayerName)));
        }
        else
        {
            // 降级：旧服务端 / 未填 UID / 未生成 → 本地快照（后续掉线不影响已锁定顺序）。
            _logger.LogWarning("[多世界] 服务端未返回轮换序列，降级本地 CurrentPlayerList 快照（{N} 人）", playerOrder.Count);
        }

        if (playerOrder.Count == 0)
        {
            _logger.LogWarning("[多世界] 玩家列表为空，仅执行第1轮");
            await RunSingleWorldCoreAsync(accountName);
            return;
        }

        // 轮数 = min(配置轮数, 实际玩家数)，超出玩家数的轮数没有意义
        var totalRounds = Math.Min(_config.MultiWorldCount, playerOrder.Count);
        if (totalRounds < _config.MultiWorldCount)
            _logger.LogWarning("[多世界] 配置轮数({Cfg})超过实际玩家数({N})，实际执行 {Total} 轮",
                _config.MultiWorldCount, playerOrder.Count, totalRounds);

        _logger.LogInformation("[多世界] 共 {Total} 轮，轮换顺序: {Players}",
            totalRounds, string.Join(" → ", playerOrder.Select(p => p.PlayerName)));

        // 重开续跑：加载本地进度 + 计算当前轮换序列签名（hoeing-multiworld-host-restart-resume-round）
        _multiWorldProgressStore.Load(_dataDir, accountName);
        var resumeOrderSignature = MultiWorldResumeDecisions.ComputeOrderSignature(
            playerOrder.Select(p => p.PlayerUid));

        bool lastRoundAmIHost = false;
        // 是否跑满全部轮次（hoeing-guard-false-restart-on-normal-close 防线 B）。
        // 循环内任一 break（会话终止 / 本轮初始化失败）都置 false —— break 只跳出 for，
        // 不跳出方法，若不区分则异常中止也会流到循环外的"完工单"处被误判为正常完成。
        bool allRoundsCompleted = true;
        for (int round = 0; round < totalRounds; round++)
        {
            if (_sessionTerminated || _multiplayerCoordinator?.IsExitTriggered == true)
            {
                _logger.LogWarning("[多世界] 会话已终止（sessionTerminated={ST}, exitTriggered={ET}），跳过剩余轮次",
                    _sessionTerminated, _multiplayerCoordinator?.IsExitTriggered);
                allRoundsCompleted = false;   // 会话中途终止，未跑满，不发完工单
                break;
            }

            _ct.ThrowIfCancellationRequested();

            var roundHostPlayer = playerOrder[round];
            // 身份判断优先级：UID > 名称 > 列表位置（都为空时用位置，第0个是第1轮房主）
            bool amIHost;
            if (!string.IsNullOrEmpty(_config.PlayerUid))
                amIHost = roundHostPlayer.PlayerUid == _config.PlayerUid;
            else if (!string.IsNullOrEmpty(_config.PlayerName))
                amIHost = roundHostPlayer.PlayerName == _config.PlayerName;
            else
            {
                // UID 和名称都未填，无法可靠判断，降级：只有第1轮的第1个玩家是房主
                amIHost = round == 0 && _config.MultiplayerRole == "host";
                _logger.LogWarning("[多世界] 玩家 UID 和名称均未填写，身份判断可能不准确，建议填写 UID");
            }
            lastRoundAmIHost = amIHost;
            if (round == 0) _isInitialHost = amIHost; // 本场发起房主身份（续跑进度只由发起房主维护）

            _logger.LogInformation("[多世界] 第 {Round}/{Total} 轮，房主: {Host}，我是{Role}",
                round + 1, totalRounds, roundHostPlayer.PlayerName, amIHost ? "房主" : "成员");

            if (round > 0)
            {
                // 注意：第 1 轮结束后房主和成员都已通过 LeaveCurrentWorldAsync 主动回到自己世界，
                // 因此本轮开始时 IsInMultiGame 必然为 false，不能在此处用"是否在联机世界"做兜底检测。
                // 成员被踢/掉线的异常场景由 WorldStateMonitor.OnDroppedFromRoom 和 RoomClosed 事件兜底。

                // === 在自己世界内强制切回 MultiplayerStartAvatarName ===
                // 游戏机制：访客在房主世界只能操控进世界瞬间的活跃角色。前一轮锄完时活跃角色
                // 可能不是 MultiplayerStartAvatarName（典型为辅助角色），导致本轮做客时该角色不在场。
                // 必须在 SetupNextRoundAsync 加入新房主世界之前，在自己世界内强制切回 MultiplayerStartAvatarName。
                // 详见 design.md §1 / bugfix.md 2.1 / 2.2 / 2.7
                _teamAlreadySwitched = false;
                _logger.LogInformation("[多世界] 第 {Round} 轮入口：在自己世界内重新切换队伍 + 切到 MultiplayerStartAvatarName", round + 1);
                await PrepareMultiplayerPartyAndAvatar();

                // 非第一轮：需要重新组队进入新房主的世界，并重新加载 CD
                // 抑制墙钟兜底按集合点超时联动（PartyTimeoutSeconds + 余量，Resolve 内叠加）：
                // SetupNextRoundAsync 内部多处等待（WaitForMembers / AllJoined / HostReady）均以
                // PartyTimeoutSeconds 为单次超时，故兜底以同一量级 + 余量作上界，避免误杀重组队长等待。
                // 但 PartyTimeoutSeconds 可能被用户设得很长（如 3600s），钳到 120s 安全下限，保证
                // 墙钟兜底至少能覆盖 WaitForMembers 的 120s 最小超时，同时避免 3660s 的不合理兜底。
                var roundSwitchTimeout = Math.Max(120, _config.PartyTimeoutSeconds);
                _worldStateMonitor?.BeginRoundSwitch(roundSwitchTimeout);
                var setupGroupTags = BuildGroupTags();
                var setupOutcome = await SetupNextRoundAsync(round, roundHostPlayer, amIHost, client, playerOrder.Count, setupGroupTags);
                _worldStateMonitor?.EndRoundSwitch();

                _multiplayerCoordinator?.ResetForNewRound();
                if (setupOutcome == RoundSetupOutcome.Abort || _multiplayerCoordinator == null)
                {
                    _logger.LogError("[多世界] 第 {Round} 轮初始化失败，停止后续轮次", round + 1);
                    // 尝试回到自己世界，避免卡在别人世界
                    try { await LeaveCurrentWorldAsync(amIHost, client); }
                    catch (Exception ex) { _logger.LogWarning(ex, "[多世界] 离开世界失败，忽略"); }
                    allRoundsCompleted = false;   // 本轮初始化失败，未跑满，不发完工单
                    break;
                }
                _cdManager.Load(_dataDir, accountName);
            }

            // 第 1 轮：在 if (round > 0) 分支没走过 Prepare，由这里调（在自己世界内）
            // 第 N+1 轮（round > 0）：if (round > 0) 分支开头已经调过 Prepare，这里跳过避免重复
            //                       —— 重复调会在房主世界内尝试切换（无效且会触发游戏内 UI 干扰）
            // 详见 design.md §2 / bugfix.md 2.4
            if (round == 0)
            {
                await PrepareMultiplayerPartyAndAvatar();
            }

            // kazuha-declare-after-team-switch: 合法声明窗口（每轮所有客户端各自识别）
            // 时序保证：本轮 PrepareMultiplayerPartyAndAvatar 已返回（tTeam ≤ tCall）
            //         + _multiplayerCoordinator 已就绪（round==0 由 InitializeMultiplayerAsync 构造，
            //           round>0 由 SetupNextRoundAsync 末尾重建，均早于本调用，tCoord ≤ tCall）
            //         + RunSingleWorldCoreAsync 尚未开锄（tCall < tRoute）
            // 第 1 轮 2 次声明（RunTask 内 + 此处 round==0）由服务端 Hub 幂等检查保证安全
            await DeclareKazuhaCapabilityIfPresentAsync(_ct);

            // 执行本轮锄地
            var groupTags = BuildGroupTags();
            var roundContext = new MultiWorldRoundContext
            {
                Round1Based = round + 1,
                TotalRounds = totalRounds,
                HostPlayerName = roundHostPlayer.PlayerName,
            };
            await RunSingleWorldCoreAsync(accountName, groupTags, roundContext);

            // 重开续跑：本轮锄地核心已完成，发起房主记录该轮房主 UID 并落盘（绝不预写）。
            // 放在 if (round < totalRounds-1) 之前，确保最后一轮也被标记。
            if (_isInitialHost && roundHostPlayer.PlayerUid is { Length: > 0 } completedHostUid)
                _multiWorldProgressStore.MarkHostCompleted(completedHostUid, resumeOrderSignature);

            // 本轮结束：全员同步后离开世界（最后一轮不需要离开）
            if (round < totalRounds - 1)
            {
                _logger.LogInformation("[多世界] 第 {Round} 轮锄地完成，等待全员同步", round + 1);
                // 轮次切换期间忽略 WorldStateMonitor 的 IsInMultiGame 检测（需求 2）
                // SyncRoundEndAsync 用固定 120s 构造 SyncBarrier（轮次同步只需等所有人跑完当前轮，
                // 不应使用 PartyTimeoutSeconds 组队超时，用户可能设得很长如 3600s）。
                // 墙钟兜底同样以 120s 为基准 + 60s 余量 = 180s，确保兜底 > 集合点超时。
                _worldStateMonitor?.BeginRoundSwitch(120);
                await SyncRoundEndAsync(round, client);

                // 离开世界：成员先主动离开，房主等待后再关闭房间
                // 这样避免房主关闭房间时成员还在执行 LeaveWorldAsync 的竞态
                await LeaveCurrentWorldAsync(amIHost, client);

                // 清理本轮的 coordinator，下一轮重新建
                if (_multiplayerCoordinator != null)
                {
                    _multiplayerCoordinator.OnDegraded -= _ => { };
                    _multiplayerCoordinator = null;
                }
                _executionEngine?.SetCoordinator(null);
                _executionEngine?.SetWorldStateMonitor(null);
            }
        }

        // 防线 B：仅"跑满全部轮次"才声明正常完成（hoeing-guard-false-restart-on-normal-close）。
        // break 只跳出 for 不跳出方法，故必须用 allRoundsCompleted 区分：
        //   - 跑满 totalRounds 轮自然结束 → true  → 发完工单
        //   - 会话终止 / 初始化失败 break → false → 不发（守护据未执行线路数正常判定重开）
        //   - 取消异常（掉线触发）        → 直接跳出方法，连这里都到不了 → 不发
        // 必须置于收尾 LeaveCurrentWorldAsync 之前——收尾会触发关房广播写入 _stopReason，
        // 而本标志正是用来让守护无视该 _stopReason 的。
        if (allRoundsCompleted)
        {
            _logger.LogInformation("[多世界] 全部 {Total} 轮锄地完成", totalRounds);
            _completedNormally = true;

            // 重开续跑：整场全部完成，清空本地进度，避免下一整场首启被旧进度污染误跳。
            if (_isInitialHost) _multiWorldProgressStore.Clear();
        }
        else
        {
            _logger.LogWarning("[多世界] 未跑满 {Total} 轮即中止，不标记正常完成（守护将按未执行线路数判定是否重开）",
                totalRounds);
        }

        // 任务结束：所有人退回自己的世界
        _logger.LogInformation("[多世界] 任务结束，退回自己的世界");
        try { await LeaveCurrentWorldAsync(lastRoundAmIHost, client); }
        catch (Exception ex) { _logger.LogWarning(ex, "[多世界] 任务结束退出世界失败，忽略"); }
    }

    /// <summary>
    /// 房主重开上报前算出 Completed_Host_Uids：读本地进度 + 开关 + 签名，委托纯函数决策。
    /// 开关关 / 数据不可信 / 无进度 → 空集合（全量序列，回退现状）。
    /// hoeing-multiworld-host-restart-resume-round Req 1.4 / 2.4 / 5.1。
    /// </summary>
    private IReadOnlySet<string> ComputeCompletedHostUidsForReport(CoordinatorClient client)
    {
        try
        {
            // 上报点 A 早于 RunMultiWorldAsync，playerOrder 尚未构造，用 CurrentPlayerList 快照算签名。
            // ComputeOrderSignature 对集合无序签名，只要全员集合一致即与写入时签名一致。
            var hostUids = client.CurrentPlayerList.Select(p => p.PlayerUid);
            var signature = MultiWorldResumeDecisions.ComputeOrderSignature(hostUids);
            var completed = MultiWorldResumeDecisions.ComputeCompletedHostUids(
                _multiWorldProgressStore.Current, _config.MultiWorldResumeEnabled, signature);
            _logger.LogInformation(
                "[多世界续跑] 上报已完成房主：{N} 个 [{Uids}]（开关={On}，签名匹配={Match}）",
                completed.Count, string.Join(",", completed),
                _config.MultiWorldResumeEnabled,
                _multiWorldProgressStore.Current.OrderSignature == signature);
            return completed;
        }
        catch (Exception ex)
        {
            // 计算失败按空集合处理：上报空 → 全量序列（安全降级，Req 2.4）。不静默吞。
            _logger.LogWarning(ex, "[多世界续跑] 计算 Completed_Host_Uids 失败，降级上报空集合");
            return new HashSet<string>();
        }
    }

    /// <summary>SetupNextRoundAsync 的结果：决定多世界主循环后续走向。</summary>
    private enum RoundSetupOutcome
    {
        /// <summary>正常完成建房/加入，进入本轮锄地。</summary>
        Proceed,
        /// <summary>建房/配置等致命失败 → 主循环 break（沿用原 _multiplayerCoordinator==null 语义）。</summary>
        Abort,
    }

    /// <summary>
    /// 设置下一轮：新房主创建房间，成员加入
    /// </summary>
    private async Task<RoundSetupOutcome> SetupNextRoundAsync(int round, PlayerInfo roundHostPlayer, bool amIHost,
        CoordinatorClient client, int playerCount, List<List<string>> groupTags)
    {
        try
        {
            var autoParty = new AutoPartyTask();
            var whitelist = string.IsNullOrEmpty(_config.RoomWhitelist)
                ? null
                : _config.RoomWhitelist.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

            // 多世界模式下：无论用户是否配置 _config.RoomWhitelist，都必须把"上一轮 client.CurrentPlayerList
            // 中所有非空 PlayerName"自动注入新房间白名单——否则用户白名单为空时新房间裸奔，
            // 任何陌生人都能在 GetOnlineRooms 看到新房间并 JoinRoom（spec lock-room-after-start §5 / bugfix §1.6 / §2.10）。
            // 用户原始白名单（whitelist）取并集保留，确保用户已配置但不在 CurrentPlayerList 中
            // 的玩家不丢失（bugfix §3.13）。
            var allPlayerNames = client.CurrentPlayerList
                .Select(p => p.PlayerName)
                .Where(n => !string.IsNullOrEmpty(n));
            List<string> multiWorldWhitelist = whitelist != null
                ? whitelist.Union(allPlayerNames).ToList()
                : allPlayerNames.ToList();
            _logger.LogInformation("[多世界] 第 {Round} 轮白名单（含所有参与玩家）: {List}",
                round + 1, string.Join(", ", multiWorldWhitelist));

            if (amIHost)
            {
                // 我是本轮房主：创建新房间，带重试机制（最多3次）
                _logger.LogInformation("[多世界] 第 {Round} 轮我是房主，创建新房间", round + 1);
                string? newCode = null;
                for (int retry = 1; retry <= 3; retry++)
                {
                    newCode = await client.CreateRoomAsync(_config.PlayerName, multiWorldWhitelist, _config.PlayerUid, playerCount);
                    if (newCode != null)
                    {
                        _logger.LogInformation("[多世界] 第 {Round} 轮创建房间成功（第{N}次尝试）: {Code}", round + 1, retry, newCode);
                        break;
                    }
                    if (retry < 3)
                    {
                        _logger.LogWarning("[多世界] 第 {Round} 轮创建房间失败（第{N}次尝试），2秒后重试", round + 1, retry);
                        await Task.Delay(2000, _ct);
                    }
                }
                
                if (newCode == null)
                {
                    _logger.LogError("[多世界] 第 {Round} 轮创建房间失败（重试3次后仍失败），终止多世界模式", round + 1);
                    MarkPartyFailed("创建房间失败");
                    return RoundSetupOutcome.Abort;
                }
                _config.CurrentRoomCode = newCode;
                _logger.LogInformation("[多世界] 新房间码: {Code}", newCode);

                // 重置 WorldJoinedSet，确保新轮次独立计数
                await client.ResetWorldJoinedAsync();
                _logger.LogInformation("[多世界] 第 {Round} 轮 WorldJoinedSet 已重置", round + 1);

                // 重置多轮世界等待点状态（skip-route-wait-point-report 修复）
                await client.ResetForNewWorldRoundAsync(round + 1);
                _logger.LogInformation("[多世界] 第 {Round} 轮等待点状态已重置", round + 1);

                // 上传配置：使用第一任房主的配置（已在第1轮保存）
                if (_firstHostConfig != null)
                {
                    // 房主上传配置，带重试机制（最多3次）
                    bool uploadSuccess = false;
                    for (int retry = 1; retry <= 3; retry++)
                    {
                        try
                        {
                            await client.SetRoomConfigAsync(_firstHostConfig);
                            uploadSuccess = true;
                            _logger.LogInformation("[多世界] 第 {Round} 轮已上传第一任房主的配置（第{N}次尝试）", round + 1, retry);

                            // kazuha-declare-after-team-switch: 删除此处旧调用——
                            // 此位置只在房主分支触发（成员永远不调用），且早于 line 1647 的 _multiplayerCoordinator 重建。
                            // 新调用点已移至 RunMultiWorldAsync 主循环内 RunSingleWorldCoreAsync 之前，每轮所有客户端都触发。
                            break;
                        }
                        catch (Exception ex)
                        {
                            if (retry < 3)
                            {
                                _logger.LogWarning(ex, "[多世界] 第 {Round} 轮上传配置失败（第{N}次尝试），2秒后重试", round + 1, retry);
                                await Task.Delay(2000, _ct);
                            }
                            else
                            {
                                _logger.LogError(ex, "[多世界] 第 {Round} 轮上传配置失败（重试3次后仍失败），终止多世界模式", round + 1);
                            }
                        }
                    }
                    
                    if (!uploadSuccess)
                    {
                        MarkPartyFailed("上传配置失败");
                        _multiplayerCoordinator = null;
                        return RoundSetupOutcome.Abort;
                    }
                }
                else
                {
                    // 多世界模式下_firstHostConfig为null是严重错误，必须终止
                    _logger.LogError("[多世界] 第 {Round} 轮房主配置丢失（_firstHostConfig为null），这表明第1轮配置保存失败，终止多世界模式", round + 1);
                    MarkPartyFailed("房主配置丢失");
                    _multiplayerCoordinator = null;
                    return RoundSetupOutcome.Abort;
                }

                await client.ReportHostReadyAsync();

                var actualCount = await autoParty.WaitForMembersAsync(
                    playerCount, whitelist, client, _config.PartyTimeoutSeconds, _ct);

                if (actualCount <= 0)
                {
                    _logger.LogError("[多世界] 第 {Round} 轮等待成员超时", round + 1);
                    MarkPartyFailed("等待成员超时");
                    return RoundSetupOutcome.Abort;
                }

                // 先注册监听再上报，避免信号在上报和等待之间丢失
                var hostAllJoinedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action hostAllJoinedHandler = () => hostAllJoinedTcs.TrySetResult(true);
                client.AllWorldJoined += hostAllJoinedHandler;
                try
                {
                    await client.ReportWorldJoinedAsync();
                    _logger.LogInformation("[多世界] 第 {Round} 轮房主已上报就绪，等待所有成员就绪...", round + 1);

                    using var allJoinedCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.PartyTimeoutSeconds));
                    using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_ct, allJoinedCts.Token);
                    using var reg = linkedCts.Token.Register(() => hostAllJoinedTcs.TrySetResult(false));
                    var allJoined = await hostAllJoinedTcs.Task;
                    if (allJoined)
                    {
                        _logger.LogInformation("[多世界] 第 {Round} 轮所有成员已就绪", round + 1);
                    }
                    else
                    {
                        // 组队守卫（方案A）：本轮未达全员就绪，不开锄，终止多世界。
                        // （事件反注册由下方 finally 统一处理，此处不重复 -=）
                        _logger.LogError("[多世界] 第 {Round} 轮等待所有成员就绪超时，未满员，终止本轮不开锄", round + 1);
                        MarkPartyFailed("等待全员就绪超时");
                        return RoundSetupOutcome.Abort;
                    }
                }
                finally
                {
                    client.AllWorldJoined -= hostAllJoinedHandler;
                }
            }
            else
            {
                // 我是成员：先找到新房主的房间并加入，再等待就绪
                _logger.LogInformation("[多世界] 第 {Round} 轮我是成员，等待房主 [{Host}] 创建房间",
                    round + 1, roundHostPlayer.PlayerName);

                // 轮询找到新房主的房间并加入
                string? newRoomCode = null;
                var deadline = DateTime.UtcNow.AddSeconds(_config.PartyTimeoutSeconds);
                int attempt = 0;
                while (DateTime.UtcNow < deadline)
                {
                    attempt++;
                    var rooms = await client.GetOnlineRoomsAsync();
                    var target = rooms.FirstOrDefault(r =>
                        (!string.IsNullOrEmpty(roundHostPlayer.PlayerUid) && r.HostUid == roundHostPlayer.PlayerUid) ||
                        r.HostName.Equals(roundHostPlayer.PlayerName, StringComparison.OrdinalIgnoreCase));
                    if (target != null)
                    {
                        _logger.LogInformation("[多世界] 找到新房主 [{Host}] 的房间: {Code}", roundHostPlayer.PlayerName, target.Code);
                        var joined = await client.JoinRoomAsync(target.Code, _config.PlayerName, _config.PlayerUid);
                        if (joined)
                        {
                            newRoomCode = target.Code;
                            _logger.LogInformation("[多世界] 成功加入新房间: {Code}", target.Code);
                            break;
                        }
                        _logger.LogWarning("[多世界] 加入房间 {Code} 失败，重试", target.Code);
                    }
                    else
                    {
                        _logger.LogInformation("[多世界] 未找到新房主 [{Host}] 的房间，等待中...（第{N}次）", roundHostPlayer.PlayerName, attempt);
                    }
                    await Task.Delay(3000, _ct);
                }

                if (newRoomCode == null)
                {
                    _logger.LogError("[多世界] 等待超时，未找到新房主 [{Host}] 的房间，终止多世界模式", roundHostPlayer.PlayerName);
                    MarkPartyFailed("未找到新房主房间");
                    _multiplayerCoordinator = null;
                    return RoundSetupOutcome.Abort;
                }

                // 加入房间后立刻发心跳，确保服务器知道成员在线（避免 AllOnlineMembersReported 提前触发）
                await client.SendHeartbeatAsync();
                _logger.LogInformation("[多世界] 第 {Round} 轮成员已发送心跳", round + 1);

                // 等待房主就绪
                var hostReadyTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<bool> readyHandler = ready => { if (ready) hostReadyTcs.TrySetResult(true); };
                client.HostReadyChanged += readyHandler;
                try
                {
                    if (!await client.IsHostReadyAsync())
                    {
                        using var readyCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.PartyTimeoutSeconds));
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, readyCts.Token);
                        using var reg = linked.Token.Register(() => hostReadyTcs.TrySetResult(false));
                        if (!await hostReadyTcs.Task)
                        {
                            _logger.LogError("[多世界] 等待房主就绪超时");
                            MarkPartyFailed("等待房主就绪超时");
                            return RoundSetupOutcome.Abort;
                        }
                    }
                }
                finally { client.HostReadyChanged -= readyHandler; }

                // 拉取新配置，带重试机制（最多3次）
                RoomConfig? hostConfig = null;
                for (int retry = 1; retry <= 3; retry++)
                {
                    hostConfig = await client.GetRoomConfigAsync();
                    if (hostConfig != null)
                    {
                        _logger.LogInformation("[多世界] 第 {Round} 轮成功拉取房主配置（第{N}次尝试）", round + 1, retry);
                        break;
                    }
                    if (retry < 3)
                    {
                        _logger.LogWarning("[多世界] 第 {Round} 轮拉取房主配置失败（第{N}次尝试），2秒后重试", round + 1, retry);
                        await Task.Delay(2000, _ct);
                    }
                }
                
                if (hostConfig != null)
                {
                    _config.SyncPointMinDistance = hostConfig.SyncPointMinDistance;
                    _config.StartRouteIndex = hostConfig.StartRouteIndex;
                    _config.RouteFilterKeywords = hostConfig.RouteFilterKeywords;
                    // 线路重试模式关键词房主同步（hoeing-multiplayer-route-retry-mode §0 / EB-v2-4）
                    _config.RouteRetryModeKeywords = hostConfig.RouteRetryModeKeywords;
                    _config.UseFixedDebugRoutes = hostConfig.UseFixedDebugRoutes;
                    _config.FixedDebugRoutePath = hostConfig.FixedDebugRoutePath;
                    _config.DebugMode = hostConfig.DebugMode;
                    _config.EnableKazuhaSync = hostConfig.EnableKazuhaSync;
                    _config.KazuhaSyncWaitSeconds = hostConfig.KazuhaSyncWaitSeconds;
                    _config.KazuhaSyncTimeoutSeconds = hostConfig.KazuhaSyncTimeoutSeconds;
                    _config.KazuhaWaitSkillCdSeconds = hostConfig.KazuhaWaitSkillCdSeconds;
                    _config.KazuhaSecondApproachMaxSteps = hostConfig.KazuhaSecondApproachMaxSteps;
                    _config.PartyTimeoutSeconds = hostConfig.PartyTimeoutSeconds;
                    _config.MultiWorldEnabled = hostConfig.MultiWorldEnabled;
                    _config.MultiWorldCount = hostConfig.MultiWorldCount;
                    _config.SelectedBuiltinRoute = hostConfig.SelectedBuiltinRoute;
                    _config.FightExtraWaitSeconds = hostConfig.FightExtraWaitSeconds;
                    _config.RejoinMaxWaitSeconds = hostConfig.RejoinMaxWaitSeconds;
                    // === 集体卡死监测同步（multiplayer-mutual-wait-collective-skip §8.8）===
                    _config.EnableMutualWaitCollectiveSkip = hostConfig.EnableMutualWaitCollectiveSkip;
                    _config.MutualWaitMinWaitersRatio = hostConfig.MutualWaitMinWaitersRatio;
                    _config.MutualWaitStableSeconds = hostConfig.MutualWaitStableSeconds;
                    _config.MaxConsecutiveCollectiveSkips = hostConfig.MaxConsecutiveCollectiveSkips;
                    // === 快速同步点抢报同步（multiplayer-fast-sync-host-controlled spec FR5）===
                    _config.FastSyncPointEnabled = hostConfig.FastSyncPointEnabled;
                    _config.FastSyncPathingDistance = FastSyncDecisions.ClampPathingDistance(hostConfig.FastSyncPathingDistance);
                    _config.FastSyncTeleportLoadingDelayMs = FastSyncDecisions.ClampTeleportDelay(hostConfig.FastSyncTeleportLoadingDelayMs);
                    // === 共享战斗配额结束同步同步（multiplayer-shared-fight-end-quorum-sync spec，多世界轮换块）===
                    _config.SharedFightEndQuorumEnabled = hostConfig.SharedFightEndQuorumEnabled;
                    _config.SharedFightEndQuorumRatio = Multiplayer.SharedFightEndQuorumDecisions.ClampRatio(hostConfig.SharedFightEndQuorumRatio);
                    // === 落后追赶同步（hoeing-lagging-catchup-host-synced-setting spec，多世界轮换块）===
                    _config.EnableLaggingCatchUp = hostConfig.EnableLaggingCatchUp;
                    _config.LagSegmentThreshold = Math.Clamp(hostConfig.LagSegmentThreshold, 1, 3);
                    // === 守护模式同步（hoeing-multiplayer-guard-auto-restart，多世界轮换块，成员回填房主下发值）===
                    _config.HoeingGuardMode = hostConfig.HoeingGuardMode;
                    _config.GuardUnexecutedRouteThreshold = Multiplayer.HoeingGuardDecisions.ClampThreshold(hostConfig.GuardUnexecutedRouteThreshold);
                    // === 自动更新线路同步（hoeing-autoupdate-routes，多世界轮换块，成员回填房主下发值）===
                    _config.AutoUpdateRoutes = hostConfig.AutoUpdateRoutes;

                    // === 执行期参数房主同步（hoeing-multiplayer-sync-execution-params §C2，多世界轮换块，R6）===
                    ApplyHostExecParams(hostConfig);

                    // 联机模式：设置战斗超时覆盖值（不修改原始配置）
                    PathingConditionConfig.MultiplayerFightTimeoutOverride = hostConfig.FightTimeoutSeconds;
                    // multiplayer-hoeing-selectable-fight-strategy §C5: 战斗策略开关纯本地，读本机 _config（不读 hostConfig）
                    PathingConditionConfig.MultiplayerUseFixedFightStrategyOverride = _config.MultiplayerUseFixedFightStrategy;

                    _logger.LogInformation(
                        "[多世界] 第 {Round} 轮成员已同步房主配置：最小距离={Dist}，快速同步点抢报启用={FastEn}，路径距离阈值={FastDist:F1}米，传送延迟={FastDelay}ms",
                        round + 1, hostConfig.SyncPointMinDistance,
                        _config.FastSyncPointEnabled, _config.FastSyncPathingDistance, _config.FastSyncTeleportLoadingDelayMs);
                }
                else
                {
                    // 多世界模式下配置同步失败是严重错误，必须终止
                    _logger.LogError("[多世界] 第 {Round} 轮成员无法获取房主配置（重试3次后仍失败），终止多世界模式", round + 1);
                    MarkPartyFailed("无法获取房主配置");
                    _multiplayerCoordinator = null;
                    return RoundSetupOutcome.Abort;
                }

                // 加入世界前先确认自己在房间花名册，掉了就用房间码重新加入（网络抖动容错）
                await client.EnsureInRoomAsync(_ct);

                // 加入新房主世界
                var joinOk = await autoParty.JoinHostWorldAsync(roundHostPlayer.PlayerUid, _ct);
                if (!joinOk)
                {
                    _logger.LogError("[多世界] 加入第 {Round} 轮房主世界失败", round + 1);
                    MarkPartyFailed("加入房主世界失败");
                    return RoundSetupOutcome.Abort;
                }

                // 先注册 AllWorldJoined 监听，再上报，避免信号在上报和等待之间丢失
                var allJoinedTcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action allJoinedHandler = () => allJoinedTcs.TrySetResult(true);
                client.AllWorldJoined += allJoinedHandler;
                try
                {
                    using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.PartyTimeoutSeconds));
                    using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, timeoutCts.Token);
                    using var reg = linked.Token.Register(() => allJoinedTcs.TrySetResult(false));

                    // 周期性确认在房 + 重报已加入世界（每 5s），杜绝单次上报因网络抖动丢失。
                    // 循环内首次立即执行一次（无初始延迟），收到 AllWorldJoined 或超时/取消即停止。
                    _ = Task.Run(async () =>
                    {
                        try
                        {
                            while (!allJoinedTcs.Task.IsCompleted && !linked.Token.IsCancellationRequested)
                            {
                                await client.EnsureInRoomAsync(linked.Token);
                                await client.ReportWorldJoinedAsync();
                                await Task.Delay(5000, linked.Token);
                            }
                        }
                        catch (OperationCanceledException) { /* 正常结束（就绪或超时） */ }
                        catch (Exception ex) { _logger.LogWarning(ex, "[联机] 周期上报已加入世界异常（忽略）"); }
                    }, linked.Token);

                    if (!await allJoinedTcs.Task)
                    {
                        // 组队守卫（方案A）：本轮未达全员就绪，成员不在自己/房主世界单跑，终止本轮。
                        // （finally 会 -= allJoinedHandler，此处不重复解绑）
                        _logger.LogError("[多世界] 等待全员就绪超时，未满员，终止本轮不开锄");
                        MarkPartyFailed("等待全员就绪超时");
                        return RoundSetupOutcome.Abort;
                    }
                }
                finally { client.AllWorldJoined -= allJoinedHandler; }
            }

            // 重建 coordinator
            var resolver = new SyncPointResolver();
            _multiplayerCoordinator = new MultiplayerCoordinator(client, resolver, _config);
            // 将当前 _linkedStopCts 赋给新 coordinator，确保协调停止能传播到 _ct
            _multiplayerCoordinator.StopCts = _linkedStopCts;

            _multiplayerCoordinator.OnDegraded += reason =>
                _logger.LogWarning("[联机] 已降级为单机模式，原因：{Reason}", reason);
            // 连续超时退出处理（需求 5）— 多世界模式每轮重建时也需要注册
            _multiplayerCoordinator.OnConsecutiveSyncTimeoutExceeded += async (isHost) =>
            {
                if (isHost)
                {
                    _logger.LogError("[联机] 房主连续超时达到上限，广播关闭房间并停止");
                    try { await _coordinatorClientRef!.CloseRoomAsync(); } catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "[联机] 广播关闭房间失败（静默忽略，成员靠心跳超时感知）");
                    }
                }
                else
                {
                    _logger.LogError("[联机] 成员连续超时达到上限，上报 Offline 并退出");
                    try { await _coordinatorClientRef!.ReportMemberStatusAsync(MemberStatus.Offline); } catch (Exception ex2)
                    {
                        _logger.LogWarning(ex2, "[联机] 上报 Offline 失败（静默忽略）");
                    }
                }
                _multiplayerCoordinator?.Degrade("连续超时达到上限");
            };
            _consistencyChecker = new RouteConsistencyChecker();
            _executionEngine?.SetCoordinator(_multiplayerCoordinator);
            _executionEngine?.SetWorldStateMonitor(_worldStateMonitor);
            // 重置路线进度信息（需求 6），避免上一轮的旧进度在新轮次心跳中上报
            _coordinatorClientRef?.UpdateRouteProgress(-1, DateTime.UtcNow, 0);
            // 轮次切换完成，恢复 WorldStateMonitor 检测（需求 2）
            _worldStateMonitor?.EndRoundSwitch();
            _logger.LogInformation("[多世界] 第 {Round} 轮初始化完成", round + 1);

            // 多世界轮换：新房主路径锁定新房间（spec lock-room-after-start §4.3 / bugfix §2.9）
            // 新房间天然 IsStarted=false，需要重新调一次 MarkRoomStartedAsync
            // 成员路径不调（amIHost==client.IsHost；成员无房主权限）
            if (amIHost)
            {
                // 重开续跑边界：第 N 轮新房主重建房间的序列无人再查询（死数据），不裁剪。
                // 全组跳过的唯一入口是首轮 InitializeMultiplayerAsync 的上报点 A。保持无参（全量序列）。
                await client.MarkRoomStartedAsync();
            }

            return RoundSetupOutcome.Proceed;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[多世界] 第 {Round} 轮初始化异常", round + 1);
            // 手动停止（OperationCanceledException）不标记组队失败——Start 尾部 userCancelled 会兜底不重开。
            if (ex is not OperationCanceledException)
                MarkPartyFailed("轮次初始化异常");
            _multiplayerCoordinator = null;
            // 初始化失败也要恢复 WorldStateMonitor 检测（需求 2）
            _worldStateMonitor?.EndRoundSwitch();
            return RoundSetupOutcome.Abort;
        }
    }

    /// <summary>全员同步本轮结束</summary>
    private async Task SyncRoundEndAsync(int round, CoordinatorClient client)
    {
        // 协调停止已触发，跳过轮次同步等待（避免等 120s 超时）
        if (_sessionTerminated || _multiplayerCoordinator?.IsExitTriggered == true)
        {
            _logger.LogWarning("[多世界] 协调停止已触发，跳过轮次结束同步");
            return;
        }

        try
        {
            var syncId = $"round_end_{round}";
            _logger.LogInformation("[多世界] 同步轮次结束: {SyncId}", syncId);
            // 用固定 120s 作为超时：轮次同步只需等所有人跑完本轮路线，不应使用
            // PartyTimeoutSeconds（组队超时，用户可能设得很长如 3600s）。
            var barrier = new SyncBarrier(client, 120);
            await barrier.WaitAsync(syncId, _ct);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[多世界] 轮次结束同步异常，继续");
        }
    }

    /// <summary>离开当前世界（成员退出，房主关闭房间）</summary>
    private async Task LeaveCurrentWorldAsync(bool amIHost, CoordinatorClient client)
    {
        var autoParty = new AutoPartyTask();
        if (amIHost)
        {
            // 房主：等待成员有足够时间主动离开（LeaveWorldAsync 约需 5-20 秒）
            // 然后关闭房间，被动踢出还未离开的成员
            // 等待时间 = min(PartyTimeoutSeconds/10, 15) 秒，默认 15 秒
            var waitMs = Math.Min(_config.PartyTimeoutSeconds / 10 * 1000, 15000);
            _logger.LogInformation("[多世界] 房主等待成员离开（最多 {T}s，踢出按钮归零即提前退出）后关闭房间", waitMs / 1000);

            // 方向 B：进入等待前建立一次稳定起点（回主界面，关闭可能的菜单/弹窗），
            // F2 在首轮 CountKickButtonsInF2Async 内打开一次，之后停留 F2 每轮仅截图+计数。
            try
            {
                await new BetterGenshinImpact.GameTask.Common.Job.ReturnMainUiTask().Start(_ct);
                await Task.Delay(500, _ct);
            }
            catch (OperationCanceledException) { throw; }
            catch (Exception ex)
            {
                // 可恢复：建立起点失败按"首轮检测自动开 F2"处理，不中断退世界流程
                _logger.LogWarning(ex, "[多世界] 进入等待阶段回主界面异常，首轮将自动打开 F2");
            }

            // 轮询提前退出：停留 F2 每轮截图→数踢出按钮，连续 3 次为 0 即确认无残留成员，提前继续。
            // 真实墙钟累计（含检测耗时），到 waitMs 兜底强制继续。间隔固定 1000ms。
            long fallbackMs = waitMs;
            var swStart = Environment.TickCount;
            int consecutiveZero = 0;
            while (true)
            {
                _ct.ThrowIfCancellationRequested();
                long elapsed = Environment.TickCount - swStart;
                int kickCount = await autoParty.CountKickButtonsInF2Async(_ct);
                consecutiveZero = HostLeaveEarlyExitDecisions.NextConsecutiveZero(consecutiveZero, kickCount);
                var leaveDecision = HostLeaveEarlyExitDecisions.Decide(kickCount, consecutiveZero, elapsed, fallbackMs, 3);
                if (leaveDecision == HostLeaveWaitDecision.EarlyExit)
                {
                    _logger.LogInformation("[多世界] 房主检测到成员已全部离开（连续 {N} 次 0 踢出按钮），提前退出等待", consecutiveZero);
                    break;
                }
                if (leaveDecision == HostLeaveWaitDecision.FallbackForceExit)
                {
                    _logger.LogInformation("[多世界] 房主等待达兜底上限（{T}s），强制继续（残留踢出按钮={Kick}）", fallbackMs / 1000, kickCount);
                    break;
                }
                await Task.Delay(1000, _ct);
            }

            await client.CloseRoomAsync();
            _logger.LogInformation("[多世界] 房主已关闭房间");
            await Task.Delay(2000, _ct);
            // 房主也需要退出多人世界，回到自己的世界
            _logger.LogInformation("[多世界] 房主退出多人世界");
            var left = await autoParty.LeaveWorldAsync(_ct);
            if (!left)
                _logger.LogWarning("[多世界] 房主退出多人世界未确认成功，继续下一轮");
            await Task.Delay(1000, _ct);
        }
        else
        {
            // 成员：主动离开，回到自己的世界
            _logger.LogInformation("[多世界] 成员离开当前世界");
            var left = await autoParty.LeaveWorldAsync(_ct);
            if (!left)
                _logger.LogWarning("[多世界] 离开世界操作未确认成功，继续下一轮");
            await Task.Delay(1000, _ct);
        }
    }

    /// <summary>单世界锄地核心（从 RunTask 提取，供多世界循环复用）</summary>
    private async Task RunSingleWorldCoreAsync(string accountName, List<List<string>>? groupTags = null, MultiWorldRoundContext? roundContext = null)
    {
        groupTags ??= BuildGroupTags();
        await RunSingleWorldAsync(accountName, groupTags, roundContext);
    }

    private async Task RunSingleWorldAsync(string accountName, List<List<string>> groupTags, MultiWorldRoundContext? roundContext = null)
    {
        // 根据操作模式执行
        var operationMode = _config.OperationMode;

        if (operationMode == "启用仅指定怪物模式")
        {
            await RunTargetMonsterMode(accountName, groupTags, roundContext);
        }
        else if (_config.UseFixedDebugRoutes)
        {
            // 固定调试线路模式：使用三级优先级逻辑加载路线
            _logger.LogInformation("[固定调试线路] 开始加载路线");
            var fixedRoutes = LoadRoutesBasedOnConfig();
            if (fixedRoutes.Count == 0)
            {
                _logger.LogWarning("[固定调试线路] 没有找到可用的路线文件");
                return;
            }
            _logger.LogInformation("[固定调试线路] 共加载 {Count} 条路线，按文件名顺序执行", fixedRoutes.Count);

            // 队伍校验
            ValidateTeam();

            await ProcessRoutesByGroup(fixedRoutes, accountName, roundContext);
        }
        else
        {
            // 预处理路线
            var pathingDir = Path.Combine(_dataDir, "pathing");
            var routes = RouteInfoLoader.LoadRoutes(
                pathingDir, _monsterRepo, _config.IgnoreRate, groupTags[0]);

            // 初始化CD和运行记录
            foreach (var route in routes)
                _cdManager.InitializeRoute(route);

            // 自我优化
            SelfOptimizer.Apply(routes, _config.DisableSelfOptimization, _config.CuriosityFactor);

            // 标记过滤
            var priorityTags = ParseChineseTags(_config.PriorityTags);
            var excludeTags = ParseChineseTags(_config.ExcludeTags);
            if (!_config.PickupMode.Contains("模板匹配") && !excludeTags.Contains("沙暴"))
                excludeTags.Add("沙暴");

            RouteMarker.MarkRoutes(routes, groupTags, priorityTags, excludeTags);

            // 路线选择优化
            var targetElite = Math.Max(0, _config.TargetEliteNum) + 5;
            var targetMonster = Math.Max(0, _config.TargetMonsterNum) + 25;
            _routeSelector.SelectOptimalRoutes(
                routes, _config.EfficiencyIndex, targetElite, targetMonster, _config.SortMode);

            // 分组分配
            RouteGroupAssigner.AssignGroups(routes, groupTags);

            if (operationMode == "调试路线分配")
            {
                RouteGroupAssigner.PrintGroupSummary(routes, _config, _dataDir);
                _cdManager.UpdateAllRecords(routes);
            }
            else if (operationMode == "运行锄地路线")
            {
                // 队伍校验
                ValidateTeam();

                _logger.LogInformation("开始运行锄地路线");
                _cdManager.UpdateAllRecords(routes);
                await ProcessRoutesByGroup(routes, accountName, roundContext);
            }
            else // 强制刷新所有运行记录
            {
                _logger.LogInformation("强制刷新所有运行记录");
                _cdManager.ClearAll();
                // 同时清除内存中路线对象上的CD和运行记录
                foreach (var route in routes)
                {
                    route.CdTime = DateTime.MinValue;
                    route.Records.Clear();
                }
                _cdManager.UpdateAllRecords(routes);
            }
        }
    }

    private async Task RunTargetMonsterMode(string accountName, List<List<string>> groupTags, MultiWorldRoundContext? roundContext = null)
    {
        var targetMonsters = ParseChineseTags(_config.TargetMonsters);
        if (targetMonsters.Count == 0)
        {
            _logger.LogError("目标怪物为空，请检查配置");
            return;
        }

        _logger.LogInformation("目标怪物模式：{Monsters}", string.Join("、", targetMonsters));

        var pathingDir = Path.Combine(_dataDir, "pathing");
        var fakeGroupTags = Enumerable.Range(0, 10).Select(_ => new List<string>()).ToList();
        var routes = RouteInfoLoader.LoadRoutes(pathingDir, _monsterRepo, _config.IgnoreRate, fakeGroupTags[0]);

        foreach (var route in routes)
            _cdManager.InitializeRoute(route);

        // 逐路线匹配
        foreach (var route in routes)
        {
            var textToSearch = (route.FullPath ?? "") + " " +
                string.Join(" ", route.MonsterInfo.Keys);
            route.Selected = targetMonsters.Any(m => textToSearch.Contains(m));
            route.Group = route.Selected ? 1 : 0;
        }

        var selectedCount = routes.Count(p => p.Selected);
        _logger.LogInformation("目标怪物模式：共找到 {Count} 条相关路线", selectedCount);

        _cdManager.UpdateAllRecords(routes);
        await ProcessRoutesByGroup(routes, accountName, roundContext);
    }

    private async Task ProcessRoutesByGroup(List<RouteInfo> routes, string accountName, MultiWorldRoundContext? roundContext = null)
    {
        var targetGroup = _config.GroupIndex;
        var groupRoutes = routes.Where(r => r.Group == targetGroup && r.Selected).ToList();

        // === 路线跳过对齐修复（sync-point-route-skip-alignment）===
        // 每轮开始时重置成员路线进度缓存，防止跨轮误判
        if (_coordinatorClientRef != null)
        {
            _coordinatorClientRef.ResetMemberProgressCache();
            _logger.LogDebug("[联机] 新一轮开始：成员路线进度缓存已重置");
        }
        
        // 连续不跳过重试计数（防止无限循环）
        int consecutiveNoSkipRetryCount = 0;
        const int MaxNoSkipRetries = 3;
        
        // 智能跳过决策辅助方法
        bool ShouldSkipRoute(int currentRouteIndex)
        {
            if (_multiplayerCoordinator == null || _coordinatorClientRef == null)
                return true; // 单机模式或无协调器时无条件跳过
            
            // 获取所有对方玩家中最小路线索引
            int? minPeerRouteIndex = GetMinPeerRouteIndex();
            
            // 查询失败兜底：返回 true（无条件跳过）
            if (minPeerRouteIndex == null)
            {
                _logger.LogWarning("[联机] 智能跳过决策：查询对方进度失败，兜底为无条件跳过");
                return true;
            }
            
            // 目标路线索引 = 当前路线索引 + 1（跳过后将进入的路线）
            int targetRouteIndex = currentRouteIndex + 1;
            
            // 对方路线索引 <= 目标路线索引 → 不跳过（对方还在目标路线或更早）
            if (minPeerRouteIndex <= targetRouteIndex)
            {
                _logger.LogInformation("[联机] 智能跳过决策：不跳过路线 {Current}（对方进度 {Peer} <= 目标路线 {Target}）",
                    currentRouteIndex, minPeerRouteIndex, targetRouteIndex);
                return false;
            }
            
            // 对方已超前 → 跳过
            _logger.LogInformation("[联机] 智能跳过决策：跳过路线 {Current}（对方进度 {Peer} > 目标路线 {Target}）",
                currentRouteIndex, minPeerRouteIndex, targetRouteIndex);
            return true;
        }
        
        // 获取所有对方玩家中最小路线索引
        int? GetMinPeerRouteIndex()
        {
            if (_coordinatorClientRef == null || _coordinatorClientRef.CurrentPlayerList == null)
                return null;
            
            // 过滤掉自己
            var myUid = _config.PlayerUid;
            var peerPlayers = _coordinatorClientRef.CurrentPlayerList
                .Where(p => p.PlayerUid != myUid)
                .ToList();
            
            if (peerPlayers.Count == 0)
            {
                _logger.LogDebug("[联机] 无对方玩家，返回 null");
                return null;
            }
            
            // 获取所有对方玩家的路线索引，取最小值（最落后玩家）
            int? minIndex = null;
            foreach (var player in peerPlayers)
            {
                var peerIndex = _coordinatorClientRef.GetPeerRouteIndex(player.PlayerUid);
                if (peerIndex.HasValue)
                {
                    minIndex = minIndex.HasValue ? Math.Min(minIndex.Value, peerIndex.Value) : peerIndex.Value;
                    _logger.LogDebug("[联机] 玩家 {Uid} 路线索引: {Index}", player.PlayerUid, peerIndex.Value);
                }
                else
                {
                    _logger.LogDebug("[联机] 玩家 {Uid} 路线索引: 未缓存", player.PlayerUid);
                }
            }
            
            _logger.LogDebug("[联机] 最小对方路线索引: {Index}", minIndex?.ToString() ?? "null");
            return minIndex;
        }
        // === 路线跳过对齐修复结束 ===

        // 步骤1：联机模式路线列表同步（必须在验证之前，确保两端用相同路线集合验证）
        if (_config.MultiplayerEnabled && _coordinatorClientRef != null)
        {
            // 房主身份判断：优先使用 UID 匹配，仅在 PlayerUid 为空时使用 MultiplayerRole 兜底
            bool isHost = !string.IsNullOrEmpty(_config.PlayerUid)
                ? _coordinatorClientRef.HostPlayerUid == _config.PlayerUid
                : _config.MultiplayerRole == "host";

            if (isHost || string.IsNullOrEmpty(_coordinatorClientRef.HostPlayerUid))
            {
                // 房主：CD 过滤后上传最终路线文件名列表
                var keywordsHost = RouteKeywordFilterDecisions.ParseKeywords(_config.RouteFilterKeywords);
                var hostRouteNames = groupRoutes
                    .Where(r => _config.StartRouteIndex > 0 || !_cdManager.IsOnCooldown(r))
                    .Where(r =>
                    {
                        var nameNoExt = Path.GetFileNameWithoutExtension(r.FileName);
                        if (RouteKeywordFilterDecisions.ShouldSkip(nameNoExt, keywordsHost))
                        {
                            var hit = RouteKeywordFilterDecisions.MatchedKeyword(nameNoExt, keywordsHost);
                            _logger.LogInformation("[联机] 房主关键词过滤跳过线路 {Name}（命中关键词：{Keyword}）", r.FileName, hit);
                            return false;
                        }
                        return true;
                    })
                    .Select(r => r.FileName)
                    .ToList();
                await _coordinatorClientRef.SetHostRouteListAsync(hostRouteNames);
                _logger.LogInformation("[联机] 房主已上传路线列表，共 {Count} 条（CD+关键词过滤后）", hostRouteNames.Count);

                // 用过滤后的列表替换 groupRoutes
                var hostRouteSet = new HashSet<string>(hostRouteNames);
                groupRoutes = groupRoutes.Where(r => hostRouteSet.Contains(r.FileName)).ToList();
            }
            else
            {
                // 成员：等待房主路线列表就绪事件，或轮询兜底
                var routeListTcs = new TaskCompletionSource<List<string>>(TaskCreationOptions.RunContinuationsAsynchronously);
                Action<List<string>> routeListHandler = names => routeListTcs.TrySetResult(names);
                _coordinatorClientRef.HostRouteListReady += routeListHandler;
                List<string> hostRouteNames;
                try
                {
                    // 先尝试直接拉取（房主可能已经上传了）。
                    // 原子查询 (uploaded, names) 同源，消除 TOCTOU：避免房主在两次独立查询之间上传非空列表，
                    // 导致拿到 uploaded=true + count=0 误判跳过（multiplayer-member-skip-round-stuck-roundend-sync-fix）。
                    var (hostUploadedNow, existing) = await _coordinatorClientRef.GetHostRouteListStatusAsync();
                    if (existing.Count > 0)
                    {
                        hostRouteNames = existing;
                        _logger.LogInformation("[联机] 直接获取到房主路线列表，共 {Count} 条", hostRouteNames.Count);
                    }
                    else
                    {
                        // 拿到空列表：可能是"房主还没上传"，也可能是"房主上传了空列表（CD全过滤）"。
                        // hostUploadedNow 与 existing 来自同一原子快照：uploaded=true 且 count=0 才是真·空列表。
                        // 不能用 IsHostReadyAsync 代理：房主在组队阶段就上报就绪（早于路线上传），会误判。
                        // multiplayer-host-empty-route-member-wait-timeout-fix（方案A）+ multiplayer-member-skip-round-stuck-roundend-sync-fix（原子快照）。
                        if (HostRouteListDecisions.ClassifyMemberRouteListResult(hostUploadedNow, 0, timedOut: false)
                            == HostRouteListDecisions.MemberRouteListOutcome.SkipRoundEmpty)
                        {
                            _logger.LogWarning("[联机] 房主已上传且路线列表为空（可能全部在CD中），跳过本轮执行");
                            return;
                        }

                        // 房主尚未上传：等待推送事件，最多90秒
                        _logger.LogInformation("[联机] 等待房主上传路线列表...");
                        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
                        using var linked = CancellationTokenSource.CreateLinkedTokenSource(_ct, timeoutCts.Token);
                        using var reg = linked.Token.Register(() => routeListTcs.TrySetResult([]));
                        hostRouteNames = await routeListTcs.Task;
                        if (hostRouteNames.Count > 0)
                        {
                            _logger.LogInformation("[联机] 收到房主路线列表推送，共 {Count} 条", hostRouteNames.Count);
                        }
                        else
                        {
                            // 等待结束仍为空：再查一次服务端原子快照，区分"房主路线为空"与"纯超时未收到"。
                            var (hostUploadedAfter, namesAfter) = await _coordinatorClientRef.GetHostRouteListStatusAsync();
                            // 极端情况：等待期间推送丢失但服务端已有非空列表 → 用快照内容兜底正常继续。
                            if (namesAfter.Count > 0)
                            {
                                hostRouteNames = namesAfter;
                                _logger.LogInformation("[联机] 二次确认获取到房主路线列表，共 {Count} 条", hostRouteNames.Count);
                            }
                            else
                            {
                                var outcome = HostRouteListDecisions.ClassifyMemberRouteListResult(hostUploadedAfter, namesAfter.Count, timedOut: true);
                                if (outcome == HostRouteListDecisions.MemberRouteListOutcome.SkipRoundEmpty)
                                {
                                    _logger.LogWarning("[联机] 房主已上传且路线列表为空（可能全部在CD中），跳过本轮执行");
                                    return;
                                }

                                _logger.LogError("[联机] 等待房主上传路线列表超时（90秒，房主未上传），停止本轮锄地");
                                return;
                            }
                        }
                    }
                }
                finally
                {
                    _coordinatorClientRef.HostRouteListReady -= routeListHandler;
                }

                // 先从完整路线列表查找（不限于当前 group 过滤后的结果）
                // 对同名变体（A变体/B变体... 同名 json）按代表 A→B→C→D 收敛到单一份，
                // 使本行对重复 FileName 绝对安全（不依赖上游是否已去重），避免 ToDictionary 撞键崩溃。
                // 选代表与房主、与一致性校验比对的文件一致（成员要跑的变体由下游 ResolveAndLoadActualVariant 解析替换）。
                var allRoutesDict = routes
                    .GroupBy(r => r.FileName)
                    .ToDictionary(g => g.Key, g => g
                        .OrderBy(r =>
                        {
                            var folder = RouteVariantNaming.TryGetVariantFolder(r.FullPath);
                            var idx = folder == null ? -1 : Array.IndexOf(RouteVariantNaming.VariantFolders, folder);
                            return idx < 0 ? int.MaxValue : idx;
                        })
                        .First());
                var found = hostRouteNames
                    .Where(name => allRoutesDict.ContainsKey(name))
                    .Select(name => allRoutesDict[name])
                    .ToList();

                _logger.LogDebug("[联机] 第一处映射: routes {Routes} 条, hostRouteNames {Host} 条, 命中 found {Found} 条",
                    routes.Count, hostRouteNames.Count, found.Count);

                // 如果有路线在本地找不到，尝试从 pathing 目录重新加载全量路线
                if (found.Count < hostRouteNames.Count)
                {
                    _logger.LogDebug("[联机] 第一处映射命中 {Found}/{Host} 条，进入兜底重载",
                        found.Count, hostRouteNames.Count);

                    var pathingDir = Path.Combine(_dataDir, "pathing");
                    var allRoutes = RouteInfoLoader.LoadRoutes(pathingDir, _monsterRepo, _config.IgnoreRate, new List<string>());

                    // 兜底重载按变体「代表」(A→B→C→D) 收敛到单一文件，
                    // 避免 A变体/B变体 同名文件平铺导致 ToDictionary(r => r.FileName) 撞键崩溃。
                    // 与 LoadFixedDebugRoutes 一致用代表（不传偏好）：保证此处映射到的文件与一致性校验
                    // 比对的代表文件相同；成员要跑的变体在校验后由 ResolveAndLoadActualVariant 解析替换。
                    var allFullPaths = allRoutes
                        .Where(r => !string.IsNullOrEmpty(r.FullPath))
                        .Select(r => r.FullPath!)
                        .ToArray();
                    var hasVariantLayout = allFullPaths.Any(f => RouteVariantNaming.TryGetVariantFolder(f) != null);

                    IEnumerable<RouteInfo> convergedRoutes = allRoutes;
                    if (hasVariantLayout)
                    {
                        var repPaths = SelectVariantRepresentatives(pathingDir, allFullPaths);
                        var repSet = new HashSet<string>(repPaths, StringComparer.OrdinalIgnoreCase);
                        convergedRoutes = allRoutes.Where(r =>
                            !string.IsNullOrEmpty(r.FullPath) && repSet.Contains(r.FullPath!)).ToList();

                        var dupNames = allRoutes
                            .GroupBy(r => r.FileName)
                            .Where(g => g.Count() > 1)
                            .Select(g => g.Key)
                            .ToList();
                        if (dupNames.Count > 0)
                            _logger.LogInformation("[联机] 兜底重载存在同名多变体 {Count} 项，已按变体代表(A→B→C→D)收敛到单一文件: {Files}",
                                dupNames.Count, string.Join(", ", dupNames));

                        _logger.LogDebug("[联机] 兜底重载收敛: 原 {Before} 条 → 收敛后 {After} 条（hasVariantLayout={HasVariant}）",
                            allRoutes.Count, ((List<RouteInfo>)convergedRoutes).Count, hasVariantLayout);
                    }

                    var fullDict = convergedRoutes.ToDictionary(r => r.FileName);
                    found = hostRouteNames
                        .Where(name => fullDict.ContainsKey(name))
                        .Select(name => fullDict[name])
                        .ToList();

                    var missing = hostRouteNames.Where(name => !fullDict.ContainsKey(name)).ToList();
                    if (missing.Count > 0)
                        _logger.LogWarning("[联机] 成员本地缺少以下路线文件（路线一致性验证将检测到差异）: {Files}", string.Join(", ", missing));
                }
                groupRoutes = found;
                _logger.LogInformation("[联机] 成员已同步房主路线列表，共 {Count} 条", groupRoutes.Count);
            }
        }

        // 步骤1.5：路线同步完成后的同步点，确保房主和成员同时进入验证阶段
        if (_multiplayerCoordinator != null && _config.MultiplayerEnabled)
        {
            // 路线为0时直接跳过后续流程
            if (groupRoutes.Count == 0)
            {
                _logger.LogWarning("[联机] 路线列表为空（可能全部在CD中），跳过本轮执行");
                return;
            }
            try
            {
                _logger.LogInformation("[联机] 等待所有玩家完成路线同步...");
                await _multiplayerCoordinator.WaitForAllPlayers("route_sync_done", _ct);
                _logger.LogInformation("[联机] 所有玩家路线同步完成，开始验证");
            }
            catch (OperationCanceledException)
            {
                if (_ct.IsCancellationRequested) throw;
                _logger.LogWarning("[联机] 路线同步等待超时，继续执行");
            }
        }

        // 步骤2：路线一致性验证（在同步后的路线集合上验证）
        if (_multiplayerCoordinator != null && _consistencyChecker != null && _coordinatorClientRef != null)
        {
            if (_config.DebugMode)
            {
                _logger.LogInformation("[联机] 调试模式：跳过路线一致性验证，共 {Count} 条路线", groupRoutes.Count);
            }
            else
            {
                _logger.LogInformation("[联机] 开始路线一致性验证，本次路线数量: {Count}", groupRoutes.Count);
                using var verifyCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                verifyCts.CancelAfter(TimeSpan.FromSeconds(120));
                try
                {
                    var verified = await _consistencyChecker.VerifyRoutesAsync(
                        _coordinatorClientRef,
                        groupRoutes.Select(r => r.FullPath),
                        verifyCts.Token);
                    if (verified == false)
                    {
                        _logger.LogError("[联机] 路线一致性验证失败，两端路线不一致，尝试更新线路后重试...");
                        await RunAutoUpdateRoutesAsync(waitMs: 5000);
                        _logger.LogInformation("[联机] 线路已更新，重新进行路线一致性验证...");
                        using var retryVerifyCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
                        retryVerifyCts.CancelAfter(TimeSpan.FromSeconds(120));
                        try
                        {
                            var retryVerified = await _consistencyChecker.VerifyRoutesAsync(
                                _coordinatorClientRef,
                                groupRoutes.Select(r => r.FullPath),
                                retryVerifyCts.Token);
                            if (retryVerified == false)
                            {
                                _logger.LogError("[联机] 重试后路线一致性验证仍失败，两端路线不一致，停止锄地");
                                return;
                            }
                            else if (retryVerified == null)
                                _logger.LogWarning("[联机] 重试后路线一致性验证超时，继续执行");
                            else
                                _logger.LogInformation("[联机] 重试后路线一致性验证通过");
                        }
                        catch (OperationCanceledException)
                        {
                            if (_ct.IsCancellationRequested) throw;
                            _logger.LogWarning("[联机] 重试后路线一致性验证超时，继续执行");
                        }
                    }
                    else if (verified == null)
                        _logger.LogWarning("[联机] 路线一致性验证超时，继续执行");
                    else
                        _logger.LogInformation("[联机] 路线一致性验证通过");
                }
                catch (OperationCanceledException)
                {
                    if (_ct.IsCancellationRequested) throw;
                    _logger.LogWarning("[联机] 路线一致性验证超时，继续执行");
                }
            }
        }

        // 步骤3：路线验证同步等待（等待所有玩家完成验证后再开始执行）
        // 注意：RouteVerificationPassed 事件已在步骤2的 VerifyRoutesAsync 中被消费，
        // 当 VerifyRoutesAsync 返回时，说明服务器已收到所有玩家的路线上报并完成了对比，
        // 因此无需再次等待。跳过此步骤避免竞态条件（事件在订阅前已触发）。
        if (_multiplayerCoordinator != null && _config.MultiplayerEnabled)
        {
            // 验证已在步骤2完成（服务器收到所有玩家上报后才广播 RouteVerificationPassed）
            // 无需额外等待
            _logger.LogDebug("[联机] 路线验证已在步骤2完成，跳过额外同步等待");
        }

        // === route-variant-sync-by-logical-id spec / R6 + R14：变体解析 + 启动期 schema 校验 ===
        // R6.9：仅启动期一次性触发，执行过程不再校验。
        if (_multiplayerCoordinator != null && _config.MultiplayerEnabled)
        {
            var variantSchemaItems = new List<RouteVariantSchemaItem>();
            var failedRoutes = new List<RouteInfo>();
            foreach (var route in groupRoutes)
            {
                if (string.IsNullOrEmpty(route.FullPath)) continue;
                var pathingDir = Path.GetDirectoryName(route.FullPath) ?? "";
                var (actualTask, schemaItem) = ResolveAndLoadActualVariant(route.FileName, route.FullPath, pathingDir);

                // hoeing-variant-route-empty-json-crash-and-discovery-fix / EB-A 2.2：
                // 加载失败（空/损坏 JSON）→ 该路线既不纳入执行列表也不上报 R6 校验，
                // 跳过它继续其余路线，避免空占位 JSON 终止整任务。
                if (actualTask == null)
                {
                    _logger.LogWarning("[变体] 路线 {File} 加载失败，已从本场执行列表剔除", route.FileName);
                    failedRoutes.Add(route);
                    continue;
                }

                // R14：若解析出的实际变体与 Host 默认不同，就地替换 route 指向变体文件，
                // 使 ExecuteRoute 自然加载变体（actualTask 非空且 FullPath 改变时）。
                if (!string.IsNullOrEmpty(actualTask.FullPath)
                    && !string.Equals(actualTask.FullPath, route.FullPath, StringComparison.OrdinalIgnoreCase))
                {
                    route.FullPath = actualTask.FullPath;
                    route.FileName = actualTask.FileName;
                }
                variantSchemaItems.Add(schemaItem);
            }

            if (failedRoutes.Count > 0)
            {
                groupRoutes.RemoveAll(r => failedRoutes.Contains(r));
            }

            // 调试模式跳过网络校验（与 MD5 校验的 DebugMode 跳过一致），但变体替换已完成
            if (_config.DebugMode)
            {
                _logger.LogInformation("[变体校验] 调试模式：跳过跨玩家 schema 网络校验（变体替换已完成）");
            }
            else
            {
                bool variantOk;
                try
                {
                    variantOk = await _multiplayerCoordinator.VerifyRouteVariantSchemaAsync(variantSchemaItems, _ct);
                }
                catch (InvalidOperationException ex)   // R8.7：服务端不支持变体功能
                {
                    _logger.LogError(ex, "[变体校验] 服务端版本不支持变体功能，停止本场联机锄地");
                    try { Wpf.Ui.Violeta.Controls.Toast.Warning("服务端版本不支持变体功能，请升级 BgiCoordinatorServer"); } catch { }
                    return;
                }
                if (!variantOk)
                {
                    _logger.LogError("[变体校验] 跨玩家 schema 校验失败，停止本场联机锄地");
                    return;
                }
                _logger.LogInformation("[变体校验] 跨玩家 schema 校验通过");
            }
        }

        // 路线为0时直接返回，避免卡住
        if (groupRoutes.Count == 0)
        {
            _logger.LogWarning("路径组{G} 没有可执行的路线（可能全部在CD中或未选中），跳过", targetGroup);
            return;
        }

        // 计算组内总计信息
        var totalElites = groupRoutes.Sum(r => r.EliteMonsterCount);
        var totalMonsters = groupRoutes.Sum(r => r.NormalMonsterCount);
        var totalGain = groupRoutes.Sum(r => r.EliteMoraGain + r.NormalMoraGain);
        var totalEstimatedTime = groupRoutes.Sum(r => r.AdjustedTime);

        var tsTotal = TimeSpan.FromSeconds(totalEstimatedTime);
        _logger.LogInformation("当前组 路径组{G} 共 {Count} 条路线，精英{E}，小怪{M}，预计用时 {H}时{Min}分{S}秒",
            targetGroup, groupRoutes.Count, totalElites, totalMonsters,
            (int)tsTotal.TotalHours, tsTotal.Minutes, tsTotal.Seconds);

        // 切换队伍
        if (!string.IsNullOrEmpty(_config.PartyName))
        {
            if (_config.MultiplayerEnabled)
            {
                _logger.LogInformation("[联机] 联机模式下队伍已在准备阶段切换，跳过重复切换");
            }
            else
            {
                _logger.LogInformation("切换至配置队伍: {Name}", _config.PartyName);
                var switchSuccess = await new SwitchPartyTask().Start(_config.PartyName, _ct);
                if (!switchSuccess)
                {
                    _logger.LogWarning("切换队伍失败: {Name}，继续执行", _config.PartyName);
                }
                await Delay(500, _ct);
            }
        }
        else
        {
            _logger.LogInformation("[DEBUG] 未配置队伍名称，跳过切换队伍");
        }

        int count = 0;
        int consecutiveSkipCount = 0; // 联机模式：连续跳过路线计数（需求 1）
        int completedCount = 0; // 本轮成功完整执行的线路数（用于轮结束统计日志）
        int skippedCount = 0;   // 本轮被跳过的线路数（联机跳过/CD跳过，用于轮结束统计日志）
        var groupStartTime = DateTime.Now;
        double remainingEstimatedTime = totalEstimatedTime;
        double skippedTime = 0;

        // 调试用：从指定索引开始（1-based，0表示从头）
        var startIndex = Math.Max(0, _config.StartRouteIndex - 1);
        if (startIndex > 0 && startIndex < groupRoutes.Count)
        {
            _logger.LogInformation("[调试] 从第 {Start} 条路线开始执行（跳过前 {Skip} 条）",
                startIndex + 1, startIndex);
        }

        var roundPrefix = MultiWorldRoundLogPrefix.Format(roundContext);
        _logger.LogInformation("[DEBUG] 开始遍历路线，共 {Count} 条，startIndex={Start}", groupRoutes.Count, startIndex);

        // === 守护自动重开：累计本组计划应跑线路数（扣除 StartRouteIndex 调试前缀）===
        // hoeing-multiplayer-guard-auto-restart：未执行数 = 计划数 − 已执行数。此处在 groupRoutes 最终确定
        // （CD/关键词/房主列表/变体过滤完）且 startIndex 已算出后累加，跨组/跨多世界轮只增不减。
        _guardPlannedRouteCount += Math.Max(0, groupRoutes.Count - startIndex);

        foreach (var route in groupRoutes.Skip(startIndex))
        {
            // === 守护自动重开：循环走到即算"已执行到"（无论后续完成/跳过/未完整/异常）===
            _guardExecutedRouteCount++;

            // === 中断重对齐检查（multiplayer-abort-and-realign spec）===
            if (_multiplayerCoordinator?.IsAbortRequested == true)
            {
                var targetRoute = _multiplayerCoordinator.GetAbortTargetRouteIndex();
                _logger.LogWarning("[联机] 收到中断指令，跳转到目标路线 {TargetRoute}", targetRoute);
                
                // 等待服务器广播的 StartRoute 指令（带超时）
                var startRoute = await _multiplayerCoordinator.WaitForStartRouteAsync(30, _ct);
                if (startRoute >= 0)
                {
                    // 清除中断状态
                    _multiplayerCoordinator.ClearAbortState();
                    
                    // 检查目标路线是否有效
                    if (startRoute >= 0 && startRoute < groupRoutes.Count)
                    {
                        // 跳转到目标路线
                        count = startRoute - startIndex;
                        if (count < 0) count = 0; // 目标路线已经过了，从头开始
                        
                        _logger.LogInformation("[联机] 跳转到路线 {TargetRoute}", startRoute);
                        continue; // 重新开始循环，跳转到目标路线
                    }
                }
                else
                {
                    _logger.LogWarning("[联机] 等待开始路线指令超时，继续执行当前路线");
                    _multiplayerCoordinator.ClearAbortState();
                }
            }
            
            // 每条路线开始时重置连续超时计数（需求 5），避免跨路线累积误触发
            _multiplayerCoordinator?.ResetSyncTimeoutCount();

            // 当前路线索引
            int currentRouteIndex = startIndex + count;
            
            // === 强制线路同步检查（multiplayer-route-enforcement spec）===
            if (_multiplayerCoordinator?.IsRouteEnforceSyncRequested == true)
            {
                var enforceTarget = _multiplayerCoordinator.GetEnforceTargetRouteIndex();
                _logger.LogWarning("[联机] 收到强制线路同步指令，需要跳转到目标线路 {TargetRoute}，当前线路 {CurrentRoute}",
                    enforceTarget, currentRouteIndex);
                
                // 清除强制同步状态
                _multiplayerCoordinator.ClearRouteEnforceSync();
                
                // 检查目标线路是否有效且在当前线路之前
                if (enforceTarget >= 0 && enforceTarget < currentRouteIndex && enforceTarget < groupRoutes.Count)
                {
                    // 计算跳转后的 count 值
                    count = enforceTarget - startIndex;
                    if (count < 0) count = 0; // 目标线路已经过了，从头开始
                    
                    _logger.LogInformation("[联机] 强制线路同步：跳转到线路 {TargetRoute}", enforceTarget);
                    continue; // 重新开始循环，跳转到目标线路
                }
                else
                {
                    _logger.LogInformation("[联机] 目标线路 {TargetRoute} 不在当前线路 {CurrentRoute} 之前，无需跳转",
                        enforceTarget, currentRouteIndex);
                }
            }
            
            // === 线路同步检查点（需求 10）：路线开始时检查同步 ===
            if (_multiplayerCoordinator?.RouteSyncCoordinator != null && _config.MultiplayerEnabled)
            {
                try
                {
                    var syncDecision = await _multiplayerCoordinator.RouteSyncCoordinator.CheckSyncAtRouteStart(currentRouteIndex, _ct);
                    
                    if (syncDecision == RouteSyncDecision.SkipToTarget)
                    {
                        // 正常玩家落后，需要追赶异常玩家
                        _logger.LogInformation("[联机] 线路同步检查：需要跳到目标线路追赶");
                        
                        // 刷新进度缓存
                        _multiplayerCoordinator.RouteSyncCoordinator.RefreshCache();
                        
                        // 上报 Rejoining 状态，触发路线跳过流程追赶
                        _logger.LogInformation("[联机] 上报 Rejoining 状态，触发路线跳过流程追赶");
                        await _coordinatorClientRef.ReportMemberStatusAsync(MemberStatus.Rejoining);
                        
                        // 继续执行当前路线，在路线执行时会检测到 Rejoining 状态并触发跳过
                    }
                    else if (syncDecision == RouteSyncDecision.Abort)
                    {
                        // 协调失败，需要结束锄地
                        _logger.LogWarning("[联机] 线路同步检查：协调失败，需要结束锄地");
                        return;
                    }
                    // RouteSyncDecision.Proceed: 正常执行
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[联机] 线路同步检查异常，继续执行当前路线");
                }
            }

            // 上报当前路线进度信息（需求 6），下次心跳时自动携带
            _coordinatorClientRef?.UpdateRouteProgress(currentRouteIndex, DateTime.UtcNow, route.AdjustedTime);

            _logger.LogInformation("[DEBUG] 进入路线循环，count={Count}，route={Name}", count + 1, route.FileName);
            _ct.ThrowIfCancellationRequested();
            // 联机协调停止检查（需求 2.2）
            if (_multiplayerCoordinator?.IsExitTriggered == true)
            {
                _logger.LogWarning("[联机] 协调停止已触发，退出路线循环");
                break;
            }
            count++;

            // 时间限制检查
            if (_timeChecker.IsInRestrictedPeriod() || _timeChecker.IsApproachingRestriction())
            {
                _logger.LogWarning("接近或处于限制时间，停止执行");
                break;
            }

            // CD检查（StartRouteIndex > 0 时跳过CD检查，强制执行；填0则正常检测CD）
            // 联机模式下成员不检测CD，由房主路线列表控制
            if (!_config.MultiplayerEnabled && _config.StartRouteIndex <= 0 && _cdManager.IsOnCooldown(route))
            {
                _logger.LogInformation("路线 {Name} 未刷新，跳过", route.FileName);
                skippedTime += route.AdjustedTime;
                remainingEstimatedTime -= route.AdjustedTime;
                skippedCount++;
                continue;
            }

            // 关键词过滤（与 CD 过滤独立叠加，Req 4.5）。
            // 联机由房主上传前过滤，成员不二次过滤（决策 e）：仅单机生效。
            if (!_config.MultiplayerEnabled)
            {
                var keywords = RouteKeywordFilterDecisions.ParseKeywords(_config.RouteFilterKeywords);
                var nameNoExt = Path.GetFileNameWithoutExtension(route.FileName);
                if (RouteKeywordFilterDecisions.ShouldSkip(nameNoExt, keywords))
                {
                    var hit = RouteKeywordFilterDecisions.MatchedKeyword(nameNoExt, keywords);
                    _logger.LogInformation("路线 {Name} 命中关键词「{Keyword}」，跳过", route.FileName, hit);
                    skippedTime += route.AdjustedTime;
                    remainingEstimatedTime -= route.AdjustedTime;
                    skippedCount++;
                    continue;
                }
            }

            var tsRouteEstimate = TimeSpan.FromSeconds(Math.Max(0, route.AdjustedTime));
            var tsRoundRemain = TimeSpan.FromSeconds(Math.Max(0, remainingEstimatedTime));
            _logger.LogInformation(
                "{Prefix}当前进度：开始第 {N}/{T} 条线路: {Name}，本线路预计用时 {EH}时{EMin}分{ES}秒，本轮预计剩余 {H}时{Min}分{S}秒",
                roundPrefix, startIndex + count, groupRoutes.Count, route.FileName,
                (int)tsRouteEstimate.TotalHours, tsRouteEstimate.Minutes, tsRouteEstimate.Seconds,
                (int)tsRoundRemain.TotalHours, tsRoundRemain.Minutes, tsRoundRemain.Seconds);

            // 写入全局进度载体（供 IPC task.status 读取）
            lock (AutoHoeingProgress.Sync)
            {
                AutoHoeingProgress.IsRunning = true;
                AutoHoeingProgress.RoundPrefix = roundPrefix;
                AutoHoeingProgress.CurrentRouteIndex = startIndex + count;
                AutoHoeingProgress.TotalRoutes = groupRoutes.Count;
                AutoHoeingProgress.RouteFileName = route.FileName;
                AutoHoeingProgress.RouteEstimatedSeconds = route.AdjustedTime;
                AutoHoeingProgress.RoundRemainingSeconds = remainingEstimatedTime;
            }

            // 白芙切换
            if (_shouldSwitchFurina)
            {
                _logger.LogInformation("上条路线检测到白芙，执行强制黑芙切换");
                _shouldSwitchFurina = false;
                var switchPath = Path.Combine(_dataDir, "assets", "强制黑芙.json");
                if (File.Exists(switchPath))
                {
                    var switchTask = PathingTask.BuildFromFilePath(switchPath);
                    if (switchTask != null)
                    {
                        var executor = new PathExecutor(_ct);
                        executor.PartyConfig = _partyConfig;
                        await executor.Pathing(switchTask);
                    }
                }
            }

            // 料理buff
            _logger.LogInformation("[DEBUG] 开始料理buff检查");
            await _cookingService.TryUseCooking(_config.CookingNames, _ct);
            _logger.LogInformation("[DEBUG] 料理buff检查完成，准备执行路线");

            var sw = Stopwatch.StartNew();

            try
            {
                if (_executionEngine != null)
                {
                    var execResult = await _executionEngine.ExecuteRoute(route, _ct, currentRouteIndex);
                    _shouldSwitchFurina = execResult.ShouldSwitchFurina;
                    sw.Stop();
                    var duration = execResult.ActualDuration;

                    // 更新剩余时间
                    remainingEstimatedTime -= route.AdjustedTime;

                    // 联机模式：检测路线跳过（需求 1）
                    if (execResult.SkipRouteRequested && _multiplayerCoordinator != null && _coordinatorClientRef != null)
                    {
                        consecutiveSkipCount++;
                        _logger.LogWarning("[联机] 路线 {Name} 被跳过（原因: {Reason}），连续跳过: {Count}/{Max}",
                            route.FileName, execResult.SkipRouteReason, consecutiveSkipCount, _config.MaxConsecutiveSkips);

                        // 不在这里上报 Normal — 保持 IsAbnormal 状态，
                        // 让 MemberStatusChanged 重评估能放行正在等的正常玩家。
                        // Normal 会在下一个同步点等待前上报（PathExecutor 中 _needReportNormalBeforeSync）。

                        // === 路线跳过对齐修复（sync-point-route-skip-alignment）===
                        // 智能跳过决策与进度广播
                        if (_multiplayerCoordinator != null)
                        {
                            // 获取当前路线索引（使用外部作用域的 currentRouteIndex）
                            // count 已递增，需要减1
                            int routeIdx = startIndex + count - 1;
                            
                            // 智能跳过决策
                            bool shouldSkip = ShouldSkipRoute(routeIdx);
                            
                            if (!shouldSkip)
                            {
                                // 不跳过路线：递增连续不跳过重试计数
                                consecutiveNoSkipRetryCount++;
                                _logger.LogInformation("[联机] 智能跳过决策：不跳过路线 {Index}（对方进度 <= 目标路线），重试计数: {Retry}/{Max}",
                                    currentRouteIndex, consecutiveNoSkipRetryCount, MaxNoSkipRetries);
                                
                                if (consecutiveNoSkipRetryCount >= MaxNoSkipRetries)
                                {
                                    // 达到上限，强制跳过
                                    _logger.LogWarning("[联机] 连续不跳过重试达到上限（{Max}次），强制跳过路线 {Index}",
                                        MaxNoSkipRetries, currentRouteIndex);
                                    shouldSkip = true;
                                    consecutiveNoSkipRetryCount = 0;
                                }
                                else
                                {
                                    // 继续重试当前路线
                                    _logger.LogInformation("[联机] 继续重试当前路线 {Index}（新 PathExecutor 实例）", currentRouteIndex);
                                    continue; // 重新执行当前路线
                                }
                            }
                            
                            if (shouldSkip)
                            {
                                // 确认跳过：重置重试计数，广播进度，通知对方
                                consecutiveNoSkipRetryCount = 0;
                                
                                // 立即广播新进度（解决进度广播时机窗口期）
                                await _coordinatorClientRef.SendMemberProgressAsync(currentRouteIndex + 1);
                                _logger.LogDebug("[联机] 已广播跳过后进度: 路线 {Index}", currentRouteIndex + 1);
                                
                                // === 等待点上报修复（skip-route-wait-point-report）===
                                // 先发送 RouteSkipped（立即放行）
                                await _multiplayerCoordinator.NotifyRouteSkippedAsync(currentRouteIndex);
                                
                                // 再发送 WaitPointReport（等待点对齐）
                                // 获取当前世界轮次
                                int worldRound = GetCurrentWorldRound();
                                // 获取路线ID
                                string routeId = currentRouteIndex.ToString();
                                // 获取下一个同步点（优先选择"传送必同步"的同步点）
                                string nextSyncPointId = await GetNextSyncPointForWaitPointAsync(currentRouteIndex, groupRoutes);
                                
                                if (!string.IsNullOrEmpty(nextSyncPointId))
                                {
                                    try
                                    {
                                        // 上报等待点（带容错处理）
                                        bool reportSuccess = await _multiplayerCoordinator.ReportWaitPointAsync(routeId, nextSyncPointId, worldRound);
                                        
                                        if (reportSuccess)
                                        {
                                            _logger.LogInformation("[联机] 等待点上报成功: Route={RouteId}, SyncPoint={SyncPointId}, Round={WorldRound}", 
                                                routeId, nextSyncPointId, worldRound);
                                        }
                                        else
                                        {
                                            // 上报失败，记录日志但不阻塞执行
                                            _logger.LogWarning("[联机] 等待点上报失败，已回退到RouteSkipped机制");
                                        }
                                    }
                                    catch (Exception ex)
                                    {
                                        // 异常处理：记录日志但不阻塞执行
                                        _logger.LogError(ex, "[联机] 等待点上报时发生异常，已回退到RouteSkipped机制");
                                    }
                                }
                                else
                                {
                                    _logger.LogWarning("[联机] 无法获取下一个同步点，跳过等待点上报");
                                }
                                // === 等待点上报修复结束 ===
                                
                                // 设置跳过下一个同步点标志
                                _multiplayerCoordinator.SetSkipNextSyncPoint();
                            }
                        }
                        // === 路线跳过对齐修复结束 ===

                        // 检查连续跳过是否达到上限
                        if (consecutiveSkipCount >= _config.MaxConsecutiveSkips)
                        {
                            _logger.LogError("[联机] 连续跳过达到上限（{Max}次），触发退出", _config.MaxConsecutiveSkips);
                            if (_multiplayerCoordinator.IsHost)
                            {
                                try { await _coordinatorClientRef.CloseRoomAsync(); } catch (Exception ex2)
                                {
                                    _logger.LogWarning(ex2, "[联机] 广播关闭房间失败（静默忽略）");
                                }
                            }
                            else
                            {
                                try { await _coordinatorClientRef.ReportMemberStatusAsync(MemberStatus.Offline); } catch (Exception ex2)
                                {
                                    _logger.LogWarning(ex2, "[联机] 上报 Offline 失败（静默忽略）");
                                }
                            }
                            _multiplayerCoordinator.Degrade("连续跳过路线达到上限");
                            break; // 退出路线循环
                        }

                        skippedCount++;
                        continue; // 跳到下一条路线，不记录 CD
                    }

                    if (execResult.FullyCompleted)
                    {
                        consecutiveSkipCount = 0; // 正常完成，归零连续跳过计数
                        consecutiveNoSkipRetryCount = 0; // 路线跳过对齐修复：正常完成时重置不跳过重试计数
                        // 联机模式：路线正常完成，确保状态为 Normal（可能之前是 Rejoining/Reviving）
                        if (_coordinatorClientRef != null && _multiplayerCoordinator != null)
                        {
                            try { await _coordinatorClientRef.ReportMemberStatusAsync(MemberStatus.Normal); } catch { }
                        }
                        
                        // === 线路同步检查点（需求 10）：路线完成时上报进度 ===
                        if (_multiplayerCoordinator?.RouteSyncCoordinator != null && _config.MultiplayerEnabled)
                        {
                            try
                            {
                                await _multiplayerCoordinator.RouteSyncCoordinator.ReportRouteCompletion(currentRouteIndex);
                            }
                            catch (Exception ex)
                            {
                                _logger.LogWarning(ex, "[联机] 路线完成进度上报异常");
                            }
                        }

                        // 联机模式下只有房主记录 CD（刷的是房主家的怪，成员只是协助）。
                        // 成员记 CD 会导致：第二轮自己当房主时上传给服务器的"非 CD 路线"少一大块，
                        // 自己家的怪没被刷却被记成已完成（测试反馈：4p 做地主只刷第 8 条，2/3p 做地主一条都不刷）。
                        // hoeing-multiplayer-solo-debug-mode Req5：单人调试模式恒不记 CD（无论单机/联机、房主/成员），
                        // 避免调试跑把线路标记为已完成，污染当天/CD 周期内的正常锄地。决策抽成纯函数（PBT 守护，Property 3）。
                        bool shouldRecordCd = SoloDebugDecisions.ShouldRecordCd(
                            _config.MultiplayerEnabled,
                            _multiplayerCoordinator?.IsHost ?? false,
                            _config.SoloDebugMode);
                        if (shouldRecordCd)
                        {
                            _cdManager.RecordCompletion(route, duration);
                            _cdManager.UpdateAllRecords(routes);
                        }
                        else if (_config.SoloDebugMode)
                        {
                            _logger.LogDebug("[单人调试模式] 路线 {Name} 正常完成，单人调试模式下不记本地 CD（避免污染当天正常锄地）", route.FileName);
                        }
                        else
                        {
                            _logger.LogDebug("[联机] 成员模式：路线 {Name} 正常完成，但刷的是房主世界，不记本地 CD", route.FileName);
                        }
                    }
                    else
                    {
                        _logger.LogWarning("路线 {Name} 未完整执行（中断/异常），不记录CD", route.FileName);
                    }

                    // 计算预计剩余时间
                    var actualUsedTime = (DateTime.Now - groupStartTime).TotalSeconds;
                    var consumedEstimated = totalEstimatedTime - remainingEstimatedTime - skippedTime;
                    var predictRemaining = consumedEstimated > 0
                        ? remainingEstimatedTime * actualUsedTime / consumedEstimated
                        : remainingEstimatedTime;
                    var tsRemain = TimeSpan.FromSeconds(Math.Max(0, predictRemaining));

                    _logger.LogInformation(
                        "{Prefix}完成第 {N}/{T} 条线路: {Name}，该组预计剩余: {H}时{Min}分{S}秒",
                        roundPrefix, startIndex + count, groupRoutes.Count, route.FileName,
                        (int)tsRemain.TotalHours, tsRemain.Minutes, tsRemain.Seconds);
                    completedCount++;
                }
            }
            catch (Exception ex)
            {
                _logger.LogError("执行路线 {Name} 出错: {Msg}", route.FileName, ex.Message);
            }
        }

        // 本轮锄地结束统计：用时 + 完成/跳过线路数（需求：每轮结束输出统计信息）
        // 使用 try-catch 确保即使被取消（如经验上限触发）也能输出汇总日志。
        try
        {
            var roundElapsed = DateTime.Now - groupStartTime;
            _logger.LogInformation(
                "{Prefix}本轮锄地结束统计：用时 {H}时{Min}分{S}秒，完成 {Done} 条 / 跳过 {Skip} 条（计划共 {Total} 条）",
                roundPrefix,
                (int)roundElapsed.TotalHours, roundElapsed.Minutes, roundElapsed.Seconds,
                completedCount, skippedCount, groupRoutes.Count);
        }
        catch (OperationCanceledException)
        {
            // 取消异常：汇总日志已输出，继续向上传播取消信号
            var roundElapsed = DateTime.Now - groupStartTime;
            _logger.LogInformation(
                "{Prefix}本轮锄地结束统计（被中断）：用时 {H}时{Min}分{S}秒，完成 {Done} 条 / 跳过 {Skip} 条（计划共 {Total} 条）",
                roundPrefix,
                (int)roundElapsed.TotalHours, roundElapsed.Minutes, roundElapsed.Seconds,
                completedCount, skippedCount, groupRoutes.Count);

            // 保存被中断这轮的统计信息，供经验上限退出时在 Start.finally 补输出"本轮锄地结束统计"格式日志。
            // （该格式只在此 catch 块输出一次；Start.finally 的 [联机][经验上限] 汇总不含完成数/跳过数。）
            _expCapRoundPrefix = roundPrefix;
            _expCapCompletedCount = completedCount;
            _expCapSkippedCount = skippedCount;
            _expCapTotalRoutes = groupRoutes.Count;
            _expCapGroupStartTime = groupStartTime;
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "{Prefix}输出本轮锄地汇总日志时异常（忽略）", roundPrefix);
        }
    }

    /// <summary>
    /// route-variant-sync-by-logical-id spec：解析锄地线路"所有可能的来源目录"。
    /// 供 UI 变体偏好面板扫描用——用户可能用普通模式或固定调试线路模式，
    /// 这里返回所有候选目录（存在的），UI 全部扫一遍合并，避免漏掉用户实际放变体的目录。
    /// 与 LoadRoutesBasedOnConfig 的三级优先级目录保持一致。
    /// </summary>
    /// <summary>
    /// route-variant-sync-by-logical-id spec：解析锄地线路"所有可能的来源目录"。
    /// 供 UI 变体偏好面板扫描用——用户可能用普通模式或固定调试线路模式，
    /// 这里返回所有候选目录（存在的），UI 全部扫一遍合并，避免漏掉用户实际放变体的目录。
    /// 与 LoadRoutesBasedOnConfig 的三级优先级目录保持一致，并额外纳入整个 Assets 根目录
    /// （递归扫描所有内置线路子目录，不依赖 SelectedBuiltinRoute 当前值）。
    /// </summary>
    /// <summary>
    /// 执行"更新联机锄地线路"（AutoHoeingUpdater）。
    /// 如果 waitMs > 0，则在更新完成后额外等待指定毫秒数（用于校验失败重试时等待更新落盘生效）。
    /// 更新失败不影响调用方继续执行（仅记日志）。
    /// </summary>
    private async Task RunAutoUpdateRoutesAsync(int waitMs = 0)
    {
        _logger.LogInformation("[自动更新线路] 开始执行更新联机锄地线路...");
        try
        {
            var updateTask = new ShellTask(ShellTaskParam.BuildFromConfig(
                @"Tools\AutoHoeingUpdater\AutoHoeingUpdater.exe --silent --target ""%CD%"" --force-download",
                new ShellConfig { Timeout = 120, NoWindow = true, Output = true }));
            await updateTask.Start(_ct);
            _logger.LogInformation("[自动更新线路] 更新完成");
            if (waitMs > 0)
            {
                _logger.LogInformation("[自动更新线路] 等待 {WaitMs}ms 让更新落盘生效...", waitMs);
                await Task.Delay(waitMs, _ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[自动更新线路] 更新失败（不影响后续任务继续执行）");
        }
    }

    public static List<string> ResolveAllHoeingRouteDirs(AutoHoeingConfig config)
    {
        var dirs = new List<string>();
        var baseDir = AppContext.BaseDirectory;

        // 1. 普通模式目录
        var normalPathing = Path.Combine(baseDir, "User", "JsScript", "AutoHoeingOneDragon", "pathing");
        if (Directory.Exists(normalPathing)) dirs.Add(normalPathing);

        // 2. 整个 Assets 根目录：RouteVariantScanner.ScanVariants 递归扫子目录，
        //    一次性覆盖「传奇 / +6 / -6(3人) / DebugRoutes / ...」等所有内置线路子目录，
        //    不依赖 SelectedBuiltinRoute 当前选了哪个（否则用户没选某目录就扫不到其中的变体）。
        var assetsRoot = Path.Combine(baseDir, "GameTask", "AutoHoeing", "Assets");
        if (Directory.Exists(assetsRoot)) dirs.Add(assetsRoot);

        // 3. 用户手填的自定义调试路径（可能在 Assets 之外）
        if (config != null
            && !string.IsNullOrWhiteSpace(config.FixedDebugRoutePath)
            && Directory.Exists(config.FixedDebugRoutePath))
        {
            dirs.Add(config.FixedDebugRoutePath);
        }

        // 去重（按绝对路径，忽略大小写）
        return dirs
            .Select(d => Path.GetFullPath(d))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .ToList();
    }

    /// <summary>
    /// 根据配置加载路线，实现三级优先级逻辑
    /// 优先级 1: 手动输入的 FixedDebugRoutePath
    /// 优先级 2: 选中的 SelectedBuiltinRoute
    /// 优先级 3: 默认行为（DebugRoutes 或正常路线选择）
    /// </summary>
    private List<RouteInfo> LoadRoutesBasedOnConfig()
    {
        // 优先级 1: 手动输入的调试目录
        if (!string.IsNullOrWhiteSpace(_config.FixedDebugRoutePath))
        {
            _logger.LogInformation("[线路加载] 使用手动指定路径: {Path}", _config.FixedDebugRoutePath);
            return LoadFixedDebugRoutes(_config.FixedDebugRoutePath);
        }

        // 优先级 2: 选中的内置线路
        if (!string.IsNullOrWhiteSpace(_config.SelectedBuiltinRoute))
        {
            var assetsPath = Path.Combine(AppContext.BaseDirectory, "GameTask", "AutoHoeing", "Assets");
            var selectedPath = Path.Combine(assetsPath, _config.SelectedBuiltinRoute);

            if (Directory.Exists(selectedPath))
            {
                _logger.LogInformation("[线路加载] 使用内置线路: {Route}", _config.SelectedBuiltinRoute);
                return LoadFixedDebugRoutes(selectedPath);
            }
            else
            {
                _logger.LogWarning("[线路加载] 选中的内置线路不存在: {Route}，回退到默认行为", _config.SelectedBuiltinRoute);
            }
        }

        // 优先级 3: 默认行为（DebugRoutes 目录）
        var defaultPath = Path.Combine(AppContext.BaseDirectory, "GameTask", "AutoHoeing", "Assets", "DebugRoutes");
        _logger.LogInformation("[线路加载] 使用默认 DebugRoutes 目录");
        return LoadFixedDebugRoutes(defaultPath);
    }

    /// <summary>
    /// 从固定目录加载调试线路，按文件名排序，全部标记为 Selected 且 Group=目标组
    /// </summary>
    private List<RouteInfo> LoadFixedDebugRoutes(string dirPath)
    {
        var routes = new List<RouteInfo>();
        if (!Directory.Exists(dirPath))
        {
            _logger.LogError("[固定调试线路] 目录不存在: {Dir}", dirPath);
            return routes;
        }

        // route-variant-sync-by-logical-id spec / §15.4 / R15.3：
        // 若目录下存在变体子文件夹（A变体/B变体/C变体/D变体），递归扫描并按基名去重，
        // 每个基名只产出一个代表（A→B→C→D 第一个存在的变体），避免同一逻辑线路的
        // 多个变体（_a/_b...）被全部跑一遍。非变体布局保持原"仅扫顶层 *.json"行为。
        var allJson = Directory.GetFiles(dirPath, "*.json", SearchOption.AllDirectories);
        var hasVariantLayout = allJson.Any(f => RouteVariantNaming.TryGetVariantFolder(f) != null);

        string[] files;
        if (!hasVariantLayout)
        {
            // 老布局：仅顶层 *.json，行为与改前完全一致
            files = Directory.GetFiles(dirPath, "*.json")
                .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        else
        {
            // 变体布局：加载阶段统一选「代表」(A→B→C→D 第一个存在)，使房主/成员加载与一致性校验
            // 比对的都是同一代表文件，MD5 校验照常通过。成员各自要跑的变体在校验之后由
            // ResolveAndLoadActualVariant 按 _config.VariantPreferences 解析替换（不在此处提前换，
            // 否则校验阶段会比到不同变体内容导致误判差异）。
            files = SelectVariantRepresentatives(dirPath, allJson);
        }

        for (int i = 0; i < files.Length; i++)
        {
            routes.Add(new RouteInfo
            {
                FileName = Path.GetFileName(files[i]),
                FullPath = files[i],
                Index = i + 1,
                Selected = true,
                Available = true,
                Group = _config.GroupIndex,
                EstimatedTime = 60,
                AdjustedTime = 60,
            });
        }

        return routes;
    }

    /// <summary>
    /// route-variant-sync-by-logical-id spec / §15.4：变体布局下选代表。
    /// - 变体子文件夹（A变体..D变体）内的文件：按基名分组，每基名取 A→B→C→D 第一个存在的代表。
    /// - 不在变体子文件夹里的普通文件：原样保留。
    /// 返回排序后的代表绝对路径数组（确定性，跨玩家一致）。
    /// </summary>
    private static string[] SelectVariantRepresentatives(string dirPath, string[] allJson, IReadOnlyDictionary<string, string>? variantPreferences = null)
    {
        // 基名 → (变体文件夹 → 绝对路径)
        var byBase = new Dictionary<string, Dictionary<string, string>>(StringComparer.Ordinal);
        var nonVariant = new List<string>();

        foreach (var path in allJson)
        {
            var folder = RouteVariantNaming.TryGetVariantFolder(path);
            if (folder == null)
            {
                nonVariant.Add(path);
                continue;
            }
            var baseName = RouteVariantNaming.StripBaseName(Path.GetFileName(path), folder);
            if (string.IsNullOrEmpty(baseName)) { nonVariant.Add(path); continue; }

            if (!byBase.TryGetValue(baseName, out var folderMap))
            {
                folderMap = new Dictionary<string, string>(StringComparer.Ordinal);
                byBase[baseName] = folderMap;
            }
            // 同一基名同一变体文件夹理论上只有一个文件；若重复以先扫到的为准
            if (!folderMap.ContainsKey(folder))
                folderMap[folder] = path;
        }

        var representatives = new List<string>();
        foreach (var (_, folderMap) in byBase)
        {
            // 偏好 key = 总文件夹名（从该基名任一变体文件路径派生，folderMap 各路径同属一个总文件夹）
            string? preferredFolder = null;
            if (variantPreferences != null)
            {
                var anyPath = folderMap.Values.FirstOrDefault();
                var topFolder = anyPath == null ? null : RouteVariantNaming.TryGetTopFolderName(anyPath);
                if (!string.IsNullOrEmpty(topFolder)
                    && variantPreferences.TryGetValue(topFolder, out var pref))
                {
                    preferredFolder = pref;
                }
            }
            var repFolder = RouteVariantNaming.PickPreferredFolder(folderMap.Keys, preferredFolder);
            if (repFolder != null && folderMap.TryGetValue(repFolder, out var repPath))
                representatives.Add(repPath);
        }
        representatives.AddRange(nonVariant);

        return representatives
            .OrderBy(f => Path.GetFileName(f), StringComparer.OrdinalIgnoreCase)
            .ToArray();
    }

    private void ValidateTeam()
    {
        if (_config.SkipValidation)
        {
            _logger.LogWarning("已跳过校验阶段");
            return;
        }

        // 基本校验（窗口分辨率等）
        var gameInfo = TaskContext.Instance().SystemInfo;
        if (gameInfo.CaptureAreaRect.Width != 1920 || gameInfo.CaptureAreaRect.Height != 1080)
        {
            _logger.LogWarning("游戏窗口非 1920×1080，可能导致图像识别失败");
        }
    }

    private void LoadGroupSettings(string accountName, bool isMultiplayer = false)
    {
        if (_config.GroupIndex == 1)
        {
            // 路径组一：保存配置
            var settings = new AccountGroupSettings
            {
                TagsForGroup1 = _config.TagsForGroup1,
                TagsForGroup2 = _config.TagsForGroup2,
                TagsForGroup3 = _config.TagsForGroup3,
                TagsForGroup4 = _config.TagsForGroup4,
                TagsForGroup5 = _config.TagsForGroup5,
                TagsForGroup6 = _config.TagsForGroup6,
                TagsForGroup7 = _config.TagsForGroup7,
                TagsForGroup8 = _config.TagsForGroup8,
                TagsForGroup9 = _config.TagsForGroup9,
                TagsForGroup10 = _config.TagsForGroup10,
                DisableSelfOptimization = _config.DisableSelfOptimization,
                EfficiencyIndex = _config.EfficiencyIndex,
                CuriosityFactor = _config.CuriosityFactor.ToString(),
                IgnoreRate = _config.IgnoreRate,
                TargetEliteNum = _config.TargetEliteNum,
                TargetMonsterNum = _config.TargetMonsterNum,
                PriorityTags = _config.PriorityTags,
                ExcludeTags = _config.ExcludeTags
            };
            var filePath = Path.Combine(_dataDir, "settings", $"{accountName}.json");
            settings.SaveToFile(filePath);
        }
        else
        {
            // 非路径组一：加载配置
            var filePath = Path.Combine(_dataDir, "settings", $"{accountName}.json");
            var settings = AccountGroupSettings.LoadFromFile(filePath);
            if (settings != null)
            {
                _config.TagsForGroup1 = settings.TagsForGroup1;
                _config.TagsForGroup2 = settings.TagsForGroup2;
                _config.TagsForGroup3 = settings.TagsForGroup3;
                _config.TagsForGroup4 = settings.TagsForGroup4;
                _config.TagsForGroup5 = settings.TagsForGroup5;
                _config.TagsForGroup6 = settings.TagsForGroup6;
                _config.TagsForGroup7 = settings.TagsForGroup7;
                _config.TagsForGroup8 = settings.TagsForGroup8;
                _config.TagsForGroup9 = settings.TagsForGroup9;
                _config.TagsForGroup10 = settings.TagsForGroup10;
                _config.EfficiencyIndex = settings.EfficiencyIndex;
                _config.IgnoreRate = settings.IgnoreRate;
                _config.TargetEliteNum = settings.TargetEliteNum;
                _config.TargetMonsterNum = settings.TargetMonsterNum;
                _config.PriorityTags = settings.PriorityTags;
                _config.ExcludeTags = settings.ExcludeTags;
            }
            else
            {
                // hoeing-multiplayer-account-name-config R3：联机路径缺失 settings 文件静默走默认设置继续，
                // 不记录阻塞错误；单机路径保留现状 LogError（R3.4 / R4）。
                if (LoadGroupSettingsDecisions.ShouldLogMissingSettingsError(isMultiplayer))
                {
                    _logger.LogError("配置文件不存在，请先在路径组一运行一次");
                }
            }
        }
    }

    private List<List<string>> BuildGroupTags()
    {
        var groupSettings = new[]
        {
            _config.TagsForGroup1, _config.TagsForGroup2, _config.TagsForGroup3,
            _config.TagsForGroup4, _config.TagsForGroup5, _config.TagsForGroup6,
            _config.TagsForGroup7, _config.TagsForGroup8, _config.TagsForGroup9,
            _config.TagsForGroup10
        };

        var groupTags = groupSettings
            .Select(s => ParseChineseTags(s))
            .ToList();

        // 第0组 = 所有组标签的并集去重
        groupTags[0] = groupTags.SelectMany(t => t).Distinct().ToList();

        return groupTags;
    }

    private static List<string> ParseChineseTags(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new();
        return input.Split('，')
            .Select(s => s.Trim())
            .Where(s => s.Length > 0)
            .ToList();
    }

    /// <summary>
    /// hoeing-multiplayer-sync-execution-params §C2：成员回填房主同步的 6 类执行期参数。
    /// 单世界（~1140-1192）与多世界轮换（~2080-2115）两处回填共用，避免遗漏（DRY）。
    /// 通道 D：PartyTimeoutAction 回填 _config（仅回填，成员超时行为不变，见 §C3）。
    /// 通道 B：QuicklySkip/OnlyInTeleportRecover/CombatScriptEndDelayMs 写成员 _partyConfig（PathExecutor 读 PartyConfig.*）。
    /// 通道 B'：PickDropsAfterFightEnabled/SwimmingEnabled/PickDropsAfterFightSeconds 写 _partyConfig.AutoFightConfig。
    /// 通道 C：全局 PickDropsAfterFightSeconds（ScanPickTask 绕过 taskParams 读全局），存原值→覆盖，finally 恢复。
    /// </summary>
    private void ApplyHostExecParams(Multiplayer.Models.RoomConfig hostConfig)
    {
        // 通道 D：仅回填 _config，不改变成员超时行为
        _config.PartyTimeoutAction = hostConfig.PartyTimeoutAction;

        // 通道 B / B'：写成员本机 _partyConfig（及其下挂 AutoFightConfig）
        if (_partyConfig != null)
        {
            _partyConfig.QuicklySkip = hostConfig.QuicklySkip;
            _partyConfig.CombatScriptEndDelayMs = SyncExecParamsDecisions.ClampDelayMs(hostConfig.CombatScriptEndDelayMs);
            _partyConfig.OnlyInTeleportRecover = hostConfig.OnlyInTeleportRecover;
            if (_partyConfig.AutoFightConfig != null)
            {
                _partyConfig.AutoFightConfig.PickDropsAfterFightEnabled = hostConfig.PickDropsAfterFightEnabled;
                _partyConfig.AutoFightConfig.PickDropsAfterFightSeconds = SyncExecParamsDecisions.ClampSeconds(hostConfig.PickDropsAfterFightSeconds);
                _partyConfig.AutoFightConfig.SwimmingEnabled = hostConfig.SwimmingEnabled;
            }
        }

        // 通道 C：ScanPickTask 读全局 AutoFightConfig.PickDropsAfterFightSeconds，绕过 taskParams。
        // 存原值（仅首次）→ 覆盖，finally（§C4）恢复。
        _savedGlobalPickDropsAfterFightSeconds ??=
            TaskContext.Instance().Config.AutoFightConfig.PickDropsAfterFightSeconds;
        TaskContext.Instance().Config.AutoFightConfig.PickDropsAfterFightSeconds =
            SyncExecParamsDecisions.ClampSeconds(hostConfig.PickDropsAfterFightSeconds);
    }

    /// <summary>
    /// 将配置组传入的覆盖值应用到当前配置
    /// </summary>
    private void ApplySettingsOverride()
    {
        if (_settingsOverride == null) return;

        T Get<T>(string key, T fallback)
        {
            if (_settingsOverride.TryGetValue(key, out var val) && val != null)
            {
                try
                {
                    // 处理 JsonElement 类型（System.Text.Json 反序列化 Dictionary<string, object?> 时的默认类型）
                    if (val is JsonElement jsonElement)
                    {
                        val = ConvertJsonElement(jsonElement);
                        if (val == null) return fallback;
                    }

                    // 处理 double -> int 转换时的 NaN/Infinity 和精度问题
                    if (typeof(T) == typeof(int) && val is double d)
                    {
                        // 处理 NaN/Infinity：直接返回 fallback，不做类型转换
                        if (double.IsNaN(d) || double.IsInfinity(d))
                            return fallback;
                        // 处理浮点数精度问题：如果值接近整数，进行取整
                        if (Math.Abs(d - Math.Round(d)) < 1e-9)
                            return (T)(object)Convert.ToInt32(Math.Round(d));
                        return (T)(object)Convert.ToInt32(d);
                    }
                    return (T)Convert.ChangeType(val, typeof(T));
                }
                catch { return fallback; }
            }
            return fallback;
        }

        // 将 JsonElement 转换为实际的值类型
        static object? ConvertJsonElement(JsonElement element)
        {
            return element.ValueKind switch
            {
                JsonValueKind.String => element.GetString(),
                JsonValueKind.Number => element.TryGetInt64(out var l) ? l : element.GetDouble(),
                JsonValueKind.True => true,
                JsonValueKind.False => false,
                JsonValueKind.Null => null,
                _ => element.GetRawText()
            };
        }

        // groupIndex: 下拉框值是"路径组一"~"路径组十"，需要转换为数字1-10
        var groupIndexStr = Get("groupIndex", "");
        if (!string.IsNullOrEmpty(groupIndexStr))
        {
            var groupMap = new Dictionary<string, int>
            {
                ["路径组一"] = 1, ["路径组二"] = 2, ["路径组三"] = 3, ["路径组四"] = 4, ["路径组五"] = 5,
                ["路径组六"] = 6, ["路径组七"] = 7, ["路径组八"] = 8, ["路径组九"] = 9, ["路径组十"] = 10
            };
            if (groupMap.TryGetValue(groupIndexStr, out var idx))
                _config.GroupIndex = idx;
        }

        _config.OperationMode = Get("operationMode", _config.OperationMode);
        _config.PartyName = Get("partyName", _config.PartyName);
        _config.SortMode = Get("sortMode", _config.SortMode);
        _config.PickupMode = Get("pickupMode", _config.PickupMode);
        _config.UseRouteRelatedMaterialsOnly = Get("useRouteRelatedMaterialsOnly", _config.UseRouteRelatedMaterialsOnly);
        _config.DisableSecondaryValidation = Get("disableSecondaryValidation", _config.DisableSecondaryValidation);
        _config.DumperCharacters = Get("dumperCharacters", _config.DumperCharacters);
        _config.CookingNames = Get("cookingNames", _config.CookingNames);
        _config.NoRunPeriod = Get("noRunPeriod", _config.NoRunPeriod);
        _config.FindFInterval = Get("findFInterval", _config.FindFInterval);
        _config.PickupDelay = Get("pickupDelay", _config.PickupDelay);
        _config.RollingDelay = Get("rollingDelay", _config.RollingDelay);
        _config.ScrollCycle = Get("scrollCycle", _config.ScrollCycle);
        _config.LogMonsterCount = Get("logMonsterCount", _config.LogMonsterCount);
        _config.DisableAsync = Get("disableAsync", _config.DisableAsync);
        _config.EnableCoordinateCheck = Get("enableCoordinateCheck", _config.EnableCoordinateCheck);
        _config.SkipValidation = Get("skipValidation", _config.SkipValidation);
        _config.AccountName = Get("accountName", _config.AccountName);
        _config.TagsForGroup1 = Get("tagsForGroup1", _config.TagsForGroup1);
        _config.TagsForGroup2 = Get("tagsForGroup2", _config.TagsForGroup2);
        _config.TagsForGroup3 = Get("tagsForGroup3", _config.TagsForGroup3);
        _config.TagsForGroup4 = Get("tagsForGroup4", _config.TagsForGroup4);
        _config.TagsForGroup5 = Get("tagsForGroup5", _config.TagsForGroup5);
        _config.TagsForGroup6 = Get("tagsForGroup6", _config.TagsForGroup6);
        _config.TagsForGroup7 = Get("tagsForGroup7", _config.TagsForGroup7);
        _config.TagsForGroup8 = Get("tagsForGroup8", _config.TagsForGroup8);
        _config.TagsForGroup9 = Get("tagsForGroup9", _config.TagsForGroup9);
        _config.TagsForGroup10 = Get("tagsForGroup10", _config.TagsForGroup10);
        _config.DisableSelfOptimization = Get("disableSelfOptimization", _config.DisableSelfOptimization);
        _config.EfficiencyIndex = Get("efficiencyIndex", _config.EfficiencyIndex);
        _config.CuriosityFactor = Get("curiosityFactor", _config.CuriosityFactor);
        _config.IgnoreRate = Get("ignoreRate", _config.IgnoreRate);
        _config.TargetEliteNum = Get("targetEliteNum", _config.TargetEliteNum);
        _config.TargetMonsterNum = Get("targetMonsterNum", _config.TargetMonsterNum);
        _config.PriorityTags = Get("priorityTags", _config.PriorityTags);
        _config.ExcludeTags = Get("excludeTags", _config.ExcludeTags);
        _config.TargetMonsters = Get("targetMonsters", _config.TargetMonsters);

        // 联机配置
        _config.MultiplayerEnabled = Get("multiplayerEnabled", _config.MultiplayerEnabled);
        _config.MultiplayerPartyName = Get("multiplayerPartyName", _config.MultiplayerPartyName);
        _config.MultiplayerStartAvatarName = Get("multiplayerStartAvatarName", _config.MultiplayerStartAvatarName);
        _config.MultiplayerRole = _settingsOverride.ContainsKey("multiplayerRole")
            ? Get("multiplayerRole", _config.MultiplayerRole)
            : _config.MultiplayerRole;
        _config.MemberJoinMode = _settingsOverride.ContainsKey("memberJoinMode")
            ? Get("memberJoinMode", _config.MemberJoinMode)
            : _config.MemberJoinMode;
        _config.TargetHostName = Get("targetHostName", _config.TargetHostName);
        _config.CoordinatorServerUrl = Get("coordinatorServerUrl", _config.CoordinatorServerUrl);
        _config.PlayerName = Get("playerName", _config.PlayerName);
        _config.PlayerUid = Get("playerUid", _config.PlayerUid);
        _config.ExpectedPlayerCount = Get("expectedPlayerCount", _config.ExpectedPlayerCount);
        _config.RoomWhitelist = Get("roomWhitelist", _config.RoomWhitelist);
        _config.PartyTimeoutSeconds = Get("partyTimeoutSeconds", _config.PartyTimeoutSeconds);
        _config.PartyTimeoutAction = Get("partyTimeoutAction", _config.PartyTimeoutAction);

        if (_config.MultiplayerEnabled)
        {
            // 联机模式：应用联机专属字段
            _config.SyncPointMinDistance = Get("syncPointMinDistance", _config.SyncPointMinDistance);
            _config.EnableKazuhaSync = Get("enableKazuhaSync", _config.EnableKazuhaSync);
            _config.MultiplayerUseFixedFightStrategy = Get("multiplayerUseFixedFightStrategy", _config.MultiplayerUseFixedFightStrategy);
            _config.KazuhaSyncWaitSeconds = ClampSyncWait(Get("kazuhaSyncWaitSeconds", _config.KazuhaSyncWaitSeconds));
            _config.KazuhaSyncTimeoutSeconds = ClampSyncTimeout(Get("kazuhaSyncTimeoutSeconds", _config.KazuhaSyncTimeoutSeconds));
            _config.KazuhaWaitSkillCdSeconds = ClampWaitCd(Get("kazuhaWaitSkillCdSeconds", _config.KazuhaWaitSkillCdSeconds));
            _config.KazuhaSecondApproachMaxSteps = ClampSecondApproachMaxSteps(Get("kazuhaSecondApproachMaxSteps", _config.KazuhaSecondApproachMaxSteps));
            // 万叶回点异常坐标重播种重识别修复：纯本地调试参数回读（阈值 double，次数 int；Get 泛型推断同 SyncPointMinDistance 的 double 用法）
            _config.KazuhaReturnAbnormalCoordThreshold = Get("kazuhaReturnAbnormalCoordThreshold", _config.KazuhaReturnAbnormalCoordThreshold);
            _config.KazuhaReturnReseedRetryCount = Get("kazuhaReturnReseedRetryCount", _config.KazuhaReturnReseedRetryCount);
            _config.KazuhaReturnPreDistanceZeroRetryTimeoutMs = Get("kazuhaReturnPreDistanceZeroRetryTimeoutMs", _config.KazuhaReturnPreDistanceZeroRetryTimeoutMs);
            // hoeing-multiplayer-lagging-member-catchup：落后追赶调试参数回读（纯本地，配置组场景必须经此 override 才能传到运行时 EffectiveConfig）
            _config.EnableLaggingCatchUp = Get("enableLaggingCatchUp", _config.EnableLaggingCatchUp);
            _config.LagSegmentThreshold = Get("lagSegmentThreshold", _config.LagSegmentThreshold);
            // hoeing-multiplayer-solo-debug-mode：单人调试模式回读（纯本地，配置组场景必须经此 override 才能传到运行时 EffectiveConfig）
            _config.SoloDebugMode = Get("soloDebugMode", _config.SoloDebugMode);
            _config.AutoLaunchAssistant = Get("autoLaunchAssistant", _config.AutoLaunchAssistant);
            NormalizeKazuhaTimeoutOrder(_config);
            _config.FightTimeoutSeconds = Get("fightTimeoutSeconds", _config.FightTimeoutSeconds);
            // === 集体卡死监测（multiplayer-mutual-wait-collective-skip §8.8 / OQ-1~OQ-5 默认值）===
            _config.EnableMutualWaitCollectiveSkip = Get("enableMutualWaitCollectiveSkip", _config.EnableMutualWaitCollectiveSkip);
            _config.MutualWaitMinWaitersRatio = Get("mutualWaitMinWaitersRatio", _config.MutualWaitMinWaitersRatio);
            _config.MutualWaitStableSeconds = Get("mutualWaitStableSeconds", _config.MutualWaitStableSeconds);
            _config.MaxConsecutiveCollectiveSkips = Get("maxConsecutiveCollectiveSkips", _config.MaxConsecutiveCollectiveSkips);
            // === 快速同步点抢报（multiplayer-fast-sync-host-controlled spec FR4 / FR25 / FR26）===
            _config.FastSyncPointEnabled = Get("fastSyncPointEnabled", _config.FastSyncPointEnabled);
            _config.FastSyncPathingDistance = FastSyncDecisions.ClampPathingDistance(
                Get("fastSyncPathingDistance", _config.FastSyncPathingDistance));
            _config.FastSyncTeleportLoadingDelayMs = FastSyncDecisions.ClampTeleportDelay(
                Get("fastSyncTeleportLoadingDelayMs", _config.FastSyncTeleportLoadingDelayMs));
            // === 共享战斗配额结束同步（multiplayer-shared-fight-end-quorum-sync spec）===
            _config.SharedFightEndQuorumEnabled = Get("sharedFightEndQuorumEnabled", _config.SharedFightEndQuorumEnabled);
            _config.SharedFightEndQuorumRatio = Multiplayer.SharedFightEndQuorumDecisions.ClampRatio(
                Get("sharedFightEndQuorumRatio", _config.SharedFightEndQuorumRatio));
            // === 基于经验判断停止锄地（multiplayer-hoeing-exp-cap-stop）：配置组场景必须经此 override 才能传到运行时 EffectiveConfig ===
            _config.EnableExpCapStop = Get("enableExpCapStop", _config.EnableExpCapStop);
            _config.ExpCapDetectAllExp = Get("expCapDetectAllExp", _config.ExpCapDetectAllExp);
            // === 守护模式（hoeing-multiplayer-guard-auto-restart）：配置组场景必须经此 override 才能传到运行时 EffectiveConfig ===
            _config.HoeingGuardMode = Get("hoeingGuardMode", _config.HoeingGuardMode);
            _config.GuardUnexecutedRouteThreshold = Multiplayer.HoeingGuardDecisions.ClampThreshold(
                Get("guardUnexecutedRouteThreshold", _config.GuardUnexecutedRouteThreshold));
            _config.AutoUpdateRoutes = Get("autoUpdateRoutes", _config.AutoUpdateRoutes);

            // === 按周期吃食物（multiplayer-hoeing-auto-eat-food-by-period，纯本地）===
            _config.MedicineFoodSlot1 = Get("medicineFoodSlot1", _config.MedicineFoodSlot1);
            _config.MedicineFoodSlot2 = Get("medicineFoodSlot2", _config.MedicineFoodSlot2);
            _config.MedicineFoodSlot3 = Get("medicineFoodSlot3", _config.MedicineFoodSlot3);
            _config.MedicineFoodSlot4 = Get("medicineFoodSlot4", _config.MedicineFoodSlot4);
            _config.MedicineFoodPeriod1 = Get("medicineFoodPeriod1", _config.MedicineFoodPeriod1);
            _config.MedicineFoodPeriod2 = Get("medicineFoodPeriod2", _config.MedicineFoodPeriod2);
            _config.MedicineFoodPeriod3 = Get("medicineFoodPeriod3", _config.MedicineFoodPeriod3);
            _config.MedicineFoodPeriod4 = Get("medicineFoodPeriod4", _config.MedicineFoodPeriod4);
            _config.MedicineFoodRouteKeywords1 = Get("medicineFoodRouteKeywords1", _config.MedicineFoodRouteKeywords1);
            _config.MedicineFoodRouteKeywords2 = Get("medicineFoodRouteKeywords2", _config.MedicineFoodRouteKeywords2);
            _config.MedicineFoodRouteKeywords3 = Get("medicineFoodRouteKeywords3", _config.MedicineFoodRouteKeywords3);
            _config.MedicineFoodRouteKeywords4 = Get("medicineFoodRouteKeywords4", _config.MedicineFoodRouteKeywords4);
            // 线路重试模式（hoeing-multiplayer-route-retry-mode spec，纯本地）
            _config.RouteRetryModeKeywords = Get("routeRetryModeKeywords", _config.RouteRetryModeKeywords);
        }
        else
        {
            // 单机模式：重置真正的联机专属字段为安全默认值，避免全局配置残留影响
            _config.SyncPointMinDistance = 30.0;
            _config.EnableKazuhaSync = false;
            _config.MultiplayerUseFixedFightStrategy = true;  // 单机模式重置为默认，避免全局残留影响
            _config.KazuhaSyncWaitSeconds = 1;
            _config.KazuhaSyncTimeoutSeconds = 20;
            _config.KazuhaWaitSkillCdSeconds = 5;
            _config.KazuhaSecondApproachMaxSteps = 6;
            _config.FightTimeoutSeconds = 120;
            // === 集体卡死监测（单机模式重置为默认值，preservation §3.11）===
            _config.EnableMutualWaitCollectiveSkip = true;
            _config.MutualWaitMinWaitersRatio = 0.5;
            _config.MutualWaitStableSeconds = 30;
            _config.MaxConsecutiveCollectiveSkips = 3;
            // === 快速同步点抢报（单机重置为默认值，preservation UB1）===
            _config.FastSyncPointEnabled = false;
            _config.FastSyncPathingDistance = 10.0;
            _config.FastSyncTeleportLoadingDelayMs = 0;
            // === 共享战斗配额结束同步（单机重置为默认值）===
            _config.SharedFightEndQuorumEnabled = false;
            _config.SharedFightEndQuorumRatio = 0.5;
            // === 落后追赶（单机重置为默认值，hoeing-lagging-catchup-host-synced-setting spec）===
            _config.EnableLaggingCatchUp = false;
            _config.LagSegmentThreshold = 1;
            // === 基于经验判断停止锄地（单机重置为默认值，multiplayer-hoeing-exp-cap-stop）===
            _config.EnableExpCapStop = false;
            _config.ExpCapDetectAllExp = false;

            // 单机模式：重置固定调试线路字段，避免联机全局配置残留影响
            // 如果 settings 显式包含这些键，后续 ContainsKey 逻辑会覆盖回来
            if (!_settingsOverride!.ContainsKey("useFixedDebugRoutes"))
                _config.UseFixedDebugRoutes = false;
            if (!_settingsOverride.ContainsKey("fixedDebugRoutePath"))
                _config.FixedDebugRoutePath = "";
            if (!_settingsOverride.ContainsKey("selectedBuiltinRoute"))
                _config.SelectedBuiltinRoute = "";
        }

        // 单机和联机均支持的字段
        _config.StartRouteIndex = Get("startRouteIndex", _config.StartRouteIndex);
        _config.RouteFilterKeywords = Get("routeFilterKeywords", _config.RouteFilterKeywords);
        _config.DebugMode = Get("debugMode", _config.DebugMode);
        if (_settingsOverride.ContainsKey("useFixedDebugRoutes"))
            _config.UseFixedDebugRoutes = Get("useFixedDebugRoutes", _config.UseFixedDebugRoutes);
        if (_settingsOverride.ContainsKey("fixedDebugRoutePath"))
            _config.FixedDebugRoutePath = Get("fixedDebugRoutePath", _config.FixedDebugRoutePath);
        if (_settingsOverride.ContainsKey("selectedBuiltinRoute"))
            _config.SelectedBuiltinRoute = Get("selectedBuiltinRoute", _config.SelectedBuiltinRoute);

        _config.MultiWorldEnabled = Get("multiWorldEnabled", _config.MultiWorldEnabled);
        _config.MultiWorldCount = Get("multiWorldCount", _config.MultiWorldCount);

        // route-variant-sync-by-logical-id spec / §15.7 / R15.5：
        // 配置组级变体偏好（key=线路基名, value=变体文件夹名）合并到 _config.VariantPreferences，
        // 配置组键覆盖全局键。_config 已是全局深拷贝，可安全 mutate（不污染全局单例）。
        ApplyVariantPreferencesOverride();
        ApplyPreSwitchWeaponRowsOverride();
        ApplyPerRouteSwitchRolesOverride();
    }

    /// <summary>
    /// 从配置组 settings['preSwitchWeaponRows'] 解析两行换武器配置（hoeing-multiplayer-preswitch-weapon）。
    /// 缺失（raw==null）→ 保持空列表（不执行换武器）；否则委托纯函数 ParseRows（内部异常兜底为默认两行）。
    /// 不写入 OcrSwitchWeaponConfig 全局（每行通过 settings 覆盖在 OcrSwitchWeaponTask 内部深拷贝上应用）。
    /// </summary>
    private void ApplyPreSwitchWeaponRowsOverride()
    {
        if (_settingsOverride == null) { _preSwitchWeaponRows = new(); return; }
        object? raw = _settingsOverride.TryGetValue("preSwitchWeaponRows", out var v) ? v : null;
        if (raw == null) { _preSwitchWeaponRows = new(); return; }
        _preSwitchWeaponRows = Multiplayer.PreSwitchWeaponDecisions.ParseRows(raw);
    }

    /// <summary>
    /// 从配置组 settings['perRouteSwitchRoles'] 解析按线路切角色映射（hoeing-multiplayer-per-route-switch-roles）。
    /// 缺失（raw==null）→ 保持空映射（不触发任何线路切角色）；否则委托纯函数 ParseRoutes（内部异常兜底为空映射，R3）。
    /// </summary>
    private void ApplyPerRouteSwitchRolesOverride()
    {
        if (_settingsOverride == null) { _perRouteSwitchRoles = new(); return; }
        object? raw = _settingsOverride.TryGetValue("perRouteSwitchRoles", out var v) ? v : null;
        if (raw == null) { _perRouteSwitchRoles = new(); return; }
        _perRouteSwitchRoles = Multiplayer.PerRouteSwitchRolesDecisions.ParseRoutes(raw);
    }
    /// _config.VariantPreferences。配置组键覆盖全局键；缺失或解析失败则保持全局值（可恢复）。
    /// </summary>
    private void ApplyVariantPreferencesOverride()
    {
        if (_settingsOverride == null) return;
        if (!_settingsOverride.TryGetValue("variantPreferences", out var raw) || raw == null) return;

        var parsed = new Dictionary<string, string>(StringComparer.Ordinal);
        try
        {
            if (raw is JsonElement je && je.ValueKind == JsonValueKind.Object)
            {
                foreach (var prop in je.EnumerateObject())
                {
                    if (prop.Value.ValueKind == JsonValueKind.String)
                    {
                        var v = prop.Value.GetString();
                        if (!string.IsNullOrEmpty(prop.Name) && !string.IsNullOrEmpty(v))
                            parsed[prop.Name] = v!;
                    }
                }
            }
            else if (raw is IDictionary<string, object?> dict)
            {
                foreach (var (k, v) in dict)
                    if (!string.IsNullOrEmpty(k) && v is string s && !string.IsNullOrEmpty(s))
                        parsed[k] = s;
            }
            else if (raw is Dictionary<string, string> sd)
            {
                foreach (var (k, v) in sd)
                    if (!string.IsNullOrEmpty(k) && !string.IsNullOrEmpty(v))
                        parsed[k] = v;
            }
        }
        catch (Exception ex)
        {
            // 配置组变体偏好解析失败属可恢复异常：保持全局偏好不变，不阻塞任务启动。
            _logger.LogWarning(ex, "[变体] 配置组变体偏好解析失败，沿用全局偏好");
            return;
        }

        if (parsed.Count == 0) return;

        // 在全局深拷贝基础上叠加配置组键（覆盖同名 key）
        var merged = new Dictionary<string, string>(_config.VariantPreferences ?? new(), StringComparer.Ordinal);
        foreach (var (k, v) in parsed) merged[k] = v;
        _config.VariantPreferences = merged;
        _logger.LogInformation("[变体] 已应用配置组变体偏好 {Count} 条（覆盖全局）", parsed.Count);
    }

    /// <summary>
    /// 获取可配置参数定义（供UI编辑使用），顺序和说明与JS settings.json一致
    /// </summary>
    public static List<SoloTaskSettingItem> GetSettingDefinitions()
    {
        var config = TaskContext.Instance().Config.AutoHoeingConfig;
        // groupIndex: 数字转为下拉框选项
        var groupNames = new[] { "路径组一","路径组二","路径组三","路径组四","路径组五","路径组六","路径组七","路径组八","路径组九","路径组十" };
        var currentGroup = config.GroupIndex >= 1 && config.GroupIndex <= 10 ? groupNames[config.GroupIndex - 1] : "路径组一";

        return new List<SoloTaskSettingItem>
        {
            // ===== 第一部分：路径组执行配置 =====
            new() { Name = "operationMode", Label = "执行模式", Type = "select", DefaultValue = config.OperationMode,
                Options = new() { "运行锄地路线", "调试路线分配", "强制刷新所有运行记录", "启用仅指定怪物模式" } },
            new() { Name = "groupIndex", Label = "选择执行第几个路径组", Type = "select", DefaultValue = currentGroup,
                Options = new(groupNames) },
            new() { Name = "partyName", Label = "本路径组使用配队名称\n【注意】请只在这里填写要使用的配队，配置组中配队项留空", Type = "text", DefaultValue = config.PartyName },
            new() { Name = "sortMode", Label = "组内路线排序模式", Type = "select", DefaultValue = config.SortMode,
                Options = new() { "原文件顺序", "效率降序", "高收益优先" } },
            new() { Name = "pickupMode", Label = "拾取模式\n【注意】bgi原版拾取性能开销大，准确低，尽量不要使用", Type = "select", DefaultValue = config.PickupMode,
                Options = new() { "模板匹配拾取狗粮和怪物材料", "模板匹配仅拾取狗粮", "BGI原版拾取", "不拾取" } },
            new() { Name = "useRouteRelatedMaterialsOnly", Label = "只使用路线相关怪物材料进行识别，提高性能\n仅在选择模板匹配拾取狗粮和怪物材料时生效\n推荐先不勾选运行一段时间获取历史数据后勾选", Type = "bool", DefaultValue = config.UseRouteRelatedMaterialsOnly },
            new() { Name = "disableSecondaryValidation", Label = "禁用识别到物品后的二次校验，可能增加误捡概率", Type = "bool", DefaultValue = config.DisableSecondaryValidation },
            new() { Name = "dumperCharacters", Label = "泥头车模式，将在接近战斗点前提前释放部分角色E技能\n需要启用时填写角色在队伍中的编号，多个用中文逗号分隔\n【注意】精英路线启用泥头车将有可能导致狗粮损失", Type = "text", DefaultValue = config.DumperCharacters },
            new() { Name = "cookingNames", Label = "使用料理名称，将在路线之间尝试使用对应名称的料理\n多个料理名称之间使用中文逗号分隔，使用间隔为300秒", Type = "text", DefaultValue = config.CookingNames },
            new() { Name = "noRunPeriod", Label = "不运行时段\n示例：单个小时：8  连续区间：8-11 或 23:11-23:55\n多项用中文逗号分隔，留空=全天可运行", Type = "text", DefaultValue = config.NoRunPeriod },
            new() { Name = "findFInterval", Label = "识别间隔(ms)，两次检测F图标之间等待时间，建议10-200", Type = "number", DefaultValue = config.FindFInterval },
            new() { Name = "pickupDelay", Label = "拾取后延时(ms)，连续拾取相同物品时建议调大，建议32-200", Type = "number", DefaultValue = config.PickupDelay },
            new() { Name = "rollingDelay", Label = "滚动后延时(ms)，拾取错误时建议调大，建议16-100", Type = "number", DefaultValue = config.RollingDelay },
            new() { Name = "scrollCycle", Label = "单次滚动周期(ms)，上下滚动不全时建议调大，建议800-2000", Type = "number", DefaultValue = config.ScrollCycle },
            new() { Name = "logMonsterCount", Label = "运行路线时输出交互或拾取精英和小怪数量，便于在日志分析中比对", Type = "bool", DefaultValue = config.LogMonsterCount },
            new() { Name = "disableAsync", Label = "禁用异步操作，设备性能过低换队受影响时选择性勾选", Type = "bool", DefaultValue = config.DisableAsync },
            new() { Name = "enableCoordinateCheck", Label = "路线结尾时进行坐标检查\n用于在路线出现卡死等放弃时不记录CD信息", Type = "bool", DefaultValue = config.EnableCoordinateCheck },
            new() { Name = "skipValidation", Label = "跳过校验阶段\n确认跳过校验阶段，任何包括但不限于漏怪、卡死、不拾取等问题均由自己配置引起", Type = "bool", DefaultValue = config.SkipValidation },

            // ===== 第二部分：路线选择与分组配置 =====
            new() { Name = "accountName", Label = "账户名称\n用于多用户运行时区分不同账户的记录，单用户请勿修改", Type = "text", DefaultValue = config.AccountName },
            new() { Name = "tagsForGroup1", Label = "路径组一要【排除】的标签\n允许使用的标签：水免，次数盾，高危，传奇，蕈兽，小怪，沙暴，狭窄地形，环境伤害\n多个标签使用中文逗号分隔", Type = "text", DefaultValue = config.TagsForGroup1 },
            new() { Name = "tagsForGroup2", Label = "路径组二要【选择】的标签", Type = "text", DefaultValue = config.TagsForGroup2 },
            new() { Name = "tagsForGroup3", Label = "路径组三要【选择】的标签", Type = "text", DefaultValue = config.TagsForGroup3 },
            new() { Name = "tagsForGroup4", Label = "路径组四要【选择】的标签", Type = "text", DefaultValue = config.TagsForGroup4 },
            new() { Name = "disableSelfOptimization", Label = "禁用根据运行记录优化路线选择的功能\n完全使用路线原有信息", Type = "bool", DefaultValue = config.DisableSelfOptimization },
            new() { Name = "efficiencyIndex", Label = "摩拉/耗时权衡因数，填0及以上的数字\n越大越倾向于花费较多时间提高总收益\n含义为愿意为1摩拉多花多少秒", Type = "number", DefaultValue = config.EfficiencyIndex },
            new() { Name = "curiosityFactor", Label = "好奇系数，缺少记录的路线预期用时将被削减对应比例\n填0-1之间的数", Type = "number", DefaultValue = config.CuriosityFactor },
            new() { Name = "ignoreRate", Label = "小怪数量/精英数量大于该值的路线将被视为纯小怪路线\n忽略其中包含的精英", Type = "number", DefaultValue = config.IgnoreRate },
            new() { Name = "targetEliteNum", Label = "目标精英数量", Type = "number", DefaultValue = config.TargetEliteNum },
            new() { Name = "targetMonsterNum", Label = "目标小怪数量", Type = "number", DefaultValue = config.TargetMonsterNum },
            new() { Name = "priorityTags", Label = "优先关键词，含关键词的路线会被视为最高效率\n不同关键词使用中文逗号分隔\n仅优先选择，不影响路线排序", Type = "text", DefaultValue = config.PriorityTags },
            new() { Name = "excludeTags", Label = "排除关键词，含关键词的路线会被完全排除\n不同关键词使用中文逗号分隔", Type = "text", DefaultValue = config.ExcludeTags },
            new() { Name = "tagsForGroup5", Label = "路径组五要【选择】的标签", Type = "text", DefaultValue = config.TagsForGroup5 },
            new() { Name = "tagsForGroup6", Label = "路径组六要【选择】的标签", Type = "text", DefaultValue = config.TagsForGroup6 },
            new() { Name = "tagsForGroup7", Label = "路径组七要【选择】的标签", Type = "text", DefaultValue = config.TagsForGroup7 },
            new() { Name = "tagsForGroup8", Label = "路径组八要【选择】的标签", Type = "text", DefaultValue = config.TagsForGroup8 },
            new() { Name = "tagsForGroup9", Label = "路径组九要【选择】的标签", Type = "text", DefaultValue = config.TagsForGroup9 },
            new() { Name = "tagsForGroup10", Label = "路径组十要【选择】的标签", Type = "text", DefaultValue = config.TagsForGroup10 },

            // ===== 第三部分：仅指定怪物模式 =====
            new() { Name = "targetMonsters", Label = "目标怪物\n建议按照怪物图鉴中的名字填写，有多个目标时使用中文逗号分隔", Type = "text", DefaultValue = config.TargetMonsters },

            // ===== 第四部分：联机队伍和角色准备 =====
            new() { Name = "multiplayerPartyName", Label = "联机队伍名称\n留空则使用当前队伍", Type = "text", DefaultValue = config.MultiplayerPartyName },
            new() { Name = "multiplayerStartAvatarName", Label = "联机起始角色名称\n留空则使用当前角色，如：钟离、纳西妲", Type = "text", DefaultValue = config.MultiplayerStartAvatarName },

            // ===== 第五部分：联机角色配置 =====
            new() { Name = "multiplayerRole", Label = "联机角色", Type = "select", DefaultValue = config.MultiplayerRole,
                Options = new() { "host", "member" } },
            new() { Name = "memberJoinMode", Label = "成员加入方式", Type = "select", DefaultValue = config.MemberJoinMode,
                Options = new() { "byHostName", "random" } },
            new() { Name = "targetHostName", Label = "目标房主玩家名称\n仅在成员模式且加入方式为byHostName时生效", Type = "text", DefaultValue = config.TargetHostName },

            // ===== 第六部分：联机服务器和同步配置 =====
            new() { Name = "coordinatorServerUrl", Label = "协调服务器地址", Type = "text", DefaultValue = config.CoordinatorServerUrl },
            new() { Name = "playerName", Label = "玩家名(必填)\n联机时显示给其他玩家", Type = "text", DefaultValue = config.PlayerName },
            new() { Name = "playerUid", Label = "玩家UID(必填)\n用于进入世界和多世界切换", Type = "text", DefaultValue = config.PlayerUid },
            new() { Name = "expectedPlayerCount", Label = "房间期望人数（2-4）\n用于判断人齐条件", Type = "number", DefaultValue = config.ExpectedPlayerCount },
            new() { Name = "roomWhitelist", Label = "房间白名单\n逗号分隔的玩家名称", Type = "text", DefaultValue = config.RoomWhitelist },
            new() { Name = "partyTimeoutSeconds", Label = "组队等待超时（秒）\n超时后根据超时动作处理", Type = "number", DefaultValue = config.PartyTimeoutSeconds },
            new() { Name = "partyTimeoutAction", Label = "组队超时动作", Type = "select", DefaultValue = config.PartyTimeoutAction.ToString(),
                Options = new() { "0", "1" } },
            new() { Name = "enableKazuhaSync", Label = "启用万叶聚物同步\n勾选后战后回点时由声明顺序首位含万叶的玩家自动放 E 聚物", Type = "bool", DefaultValue = config.EnableKazuhaSync },
            new() { Name = "multiplayerUseFixedFightStrategy", Label = "固定使用联机战斗策略\n联机战斗策略为针对联机优化过的，大部分角色的普通战斗策略和联机可能不一样，建议使用联机战斗策略", Type = "bool", DefaultValue = config.MultiplayerUseFixedFightStrategy },
            new() { Name = "kazuhaSyncWaitSeconds", Label = "万叶聚物完成后非万叶玩家停留（秒）\n0-30，默认1，仅在指定万叶玩家+启用走回战斗点时生效", Type = "number", DefaultValue = config.KazuhaSyncWaitSeconds },
            new() { Name = "kazuhaSyncTimeoutSeconds", Label = "万叶聚物同步总超时（秒）\n5-120，默认20，所有玩家在战斗点等待万叶完成的最长时间", Type = "number", DefaultValue = config.KazuhaSyncTimeoutSeconds },
            new() { Name = "kazuhaWaitSkillCdSeconds", Label = "万叶 E 技 CD 等待上限（秒）\n3-10，默认5，超时后直接尝试释放 E 技", Type = "number", DefaultValue = config.KazuhaWaitSkillCdSeconds },
            new() { Name = "kazuhaSecondApproachMaxSteps", Label = "拾取前精接近步数（联机万叶聚物）\n1-30，默认6（约0.5s上限），战后回点完成第一段后再做二段精接近的最大步数", Type = "number", DefaultValue = config.KazuhaSecondApproachMaxSteps },
            new() { Name = "kazuhaReturnAbnormalCoordThreshold", Label = "回点异常坐标阈值\n默认50，战后回点/持续回点读到的坐标距战斗点超过此值视为识别漂移，触发重播种+重识别", Type = "number", DefaultValue = config.KazuhaReturnAbnormalCoordThreshold },
            new() { Name = "kazuhaReturnReseedRetryCount", Label = "回点重识别重试次数\n默认3，命中异常阈值后重播种战斗点锚点并重识别的最大重试次数", Type = "number", DefaultValue = config.KazuhaReturnReseedRetryCount },
            new() { Name = "kazuhaReturnPreDistanceZeroRetryTimeoutMs", Label = "回点距离预判(0,0)重试窗口(ms)\n默认2000，战后回点距离预判帧识别失败(0,0)时 GetPositionStable 全局匹配重试的总时长上限", Type = "number", DefaultValue = config.KazuhaReturnPreDistanceZeroRetryTimeoutMs },
            new() { Name = "syncPointMinDistance", Label = "集合点与战斗点的最小距离阈值\n小于此距离的点不作为集合点", Type = "number", DefaultValue = config.SyncPointMinDistance },

            // ===== 联机战斗配置 =====
            new() { Name = "fightTimeoutSeconds", Label = "联机战斗超时时间（秒）\n由房主设定并同步给所有成员，覆盖各自的自动战斗超时", Type = "number", DefaultValue = config.FightTimeoutSeconds },

            // ===== 集体卡死监测（multiplayer-mutual-wait-collective-skip spec）=====
            new() { Name = "enableMutualWaitCollectiveSkip", Label = "启用集体卡死监测\n服务端检测到多人在不同同步点互锁后，主动协调全员跳段，无需等到客户端 60s 超时", Type = "bool", DefaultValue = config.EnableMutualWaitCollectiveSkip },
            new() { Name = "mutualWaitMinWaitersRatio", Label = "触发阈值比例（0.5=半数）\ntotalWaiters ≥ ⌈online * Ratio⌉ 才进入稳定计时；0.5 表示 4 人房 2 人到达就开始计时", Type = "number", DefaultValue = config.MutualWaitMinWaitersRatio },
            new() { Name = "mutualWaitStableSeconds", Label = "稳定时长（秒）\nArrivalSets 快照保持稳定 N 秒后触发协同跳段，默认 30 秒", Type = "number", DefaultValue = config.MutualWaitStableSeconds },
            new() { Name = "maxConsecutiveCollectiveSkips", Label = "连续触发上限\n连续触发 N 次集体跳段后协调停止，默认 3", Type = "number", DefaultValue = config.MaxConsecutiveCollectiveSkips },

            // ===== 快速同步点抢报（multiplayer-fast-sync-host-controlled spec, host-controlled）=====
            new() { Name = "fastSyncPointEnabled", Label = "启用快速同步点抢报\n开启后路径同步点距离 ≤ 阈值 / 传送 loading 命中即抢先上报，减少队伍互等时间。包括战斗点（O1=A）", Type = "bool", DefaultValue = config.FastSyncPointEnabled },
            new() { Name = "fastSyncPathingDistance", Label = "路径抢报距离阈值（米，原神坐标系）\n距 waypoint ≤ 阈值时抢报，范围 5-30，默认 10。低值更保守、高值更激进", Type = "number", DefaultValue = config.FastSyncPathingDistance },
            new() { Name = "fastSyncTeleportLoadingDelayMs", Label = "传送 loading 抢报延迟（毫秒）\nloading 命中后延迟多少毫秒抢报，范围 0-3000，默认 0。高网络延迟环境可上调", Type = "number", DefaultValue = config.FastSyncTeleportLoadingDelayMs },

            // ===== 共享战斗配额结束同步（multiplayer-shared-fight-end-quorum-sync spec, host-controlled）=====
            new() { Name = "sharedFightEndQuorumEnabled", Label = "启用共享战斗配额结束同步（房主设置，同步成员）\n开启后联机共享战斗中本地判定结束改为投票，参与者中 done 数 ≥ ⌈人数×比例⌉ 时全队一起结束，避免无仇恨玩家提前离队", Type = "bool", DefaultValue = config.SharedFightEndQuorumEnabled },
            new() { Name = "sharedFightEndQuorumRatio", Label = "配额比例（0-1，默认 0.5 过半）\n达成条件 doneCount ≥ ⌈participants × ratio⌉", Type = "number", DefaultValue = config.SharedFightEndQuorumRatio },

            // ===== 落后成员逐段追赶（hoeing-lagging-catchup-host-synced-setting spec, host-controlled）=====
            new() { Name = "enableLaggingCatchUp", Label = "启用落后成员逐段追赶（房主设置，同步成员）\n开启后段级落后大部队达阈值的玩家逐段快速跳进对齐，房主与成员都参与追赶", Type = "bool", DefaultValue = config.EnableLaggingCatchUp },
            new() { Name = "lagSegmentThreshold", Label = "落后触发阈值（落后多少段触发，范围 1-3，默认 1）", Type = "number", DefaultValue = config.LagSegmentThreshold },

            // ===== 第七部分：多世界连续锄地配置 =====
            new() { Name = "multiWorldEnabled", Label = "启用多世界连续锄地\n房主设定，完成一个世界后轮换到下一个玩家的世界", Type = "bool", DefaultValue = config.MultiWorldEnabled },
            new() { Name = "multiWorldCount", Label = "多世界锄地轮数（1-4）\n由房主设定，按加入顺序依次成为房主", Type = "number", DefaultValue = config.MultiWorldCount },

            // ===== 线路关键词过滤（hoeing-route-keyword-filter spec，单机和联机均支持）=====
            new() { Name = "routeFilterKeywords", Label = "线路关键词过滤\n逗号分隔（支持全角，/半角,），文件名含任一关键词的线路跳过；留空=不过滤", Type = "text", DefaultValue = config.RouteFilterKeywords },
        };
    }

    /// <summary>
    /// 联机前检查：若当前已在多人联机世界，则先退出回到自己世界。
    /// 流程：返回主界面 → 截图判断 IsInMultiGame（带 1 次重试）→ 若是则调 LeaveWorldAsync 退出。
    /// 任何异常都不阻断后续流程，仅记日志，由后续步骤继续兜底。
    /// </summary>
    private async Task EnsureInOwnWorldBeforeMultiplayerAsync()
    {
        try
        {
            // 1. 回到主界面（DetectedMultiGameStatus 依赖右侧 HUD / 左上角图标可见）
            try
            {
                await new ReturnMainUiTask().Start(_ct);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[联机] 检测多人世界前回到主界面失败，继续尝试检测");
            }
            await Task.Delay(500, _ct);

            // 2. 检测多人状态（最多 2 次，避免单次模板匹配漏识造成误判）
            bool isInMultiGame = false;
            for (int attempt = 1; attempt <= 2; attempt++)
            {
                try
                {
                    using var ra = CaptureToRectArea();
                    var status = PartyAvatarSideIndexHelper.DetectedMultiGameStatus(ra);
                    if (status.IsInMultiGame)
                    {
                        _logger.LogInformation("[联机] 检测到当前处于多人联机世界（第{N}次检测，房主={IsHost}，人数={Count}），先退出多人世界",
                            attempt, status.IsHost, status.PlayerCount);
                        isInMultiGame = true;
                        break;
                    }
                }
                catch (Exception ex)
                {
                    _logger.LogDebug(ex, "[联机] 多人世界检测异常（第{N}次），重试", attempt);
                }
                if (attempt < 2)
                    await Task.Delay(500, _ct);
            }

            if (!isInMultiGame)
            {
                _logger.LogDebug("[联机] 未检测到多人联机世界，跳过退出步骤");
                return;
            }

            // 3. 退出多人世界（带 60s 兜底超时，避免极端情况下卡死整个任务）
            using var leaveCts = CancellationTokenSource.CreateLinkedTokenSource(_ct);
            leaveCts.CancelAfter(TimeSpan.FromSeconds(60));
            try
            {
                var autoParty = new AutoPartyTask();
                var left = await autoParty.LeaveWorldAsync(leaveCts.Token);
                if (left)
                    _logger.LogInformation("[联机] 已退出多人世界，回到自己的世界");
                else
                    _logger.LogWarning("[联机] 退出多人世界未确认成功，后续步骤将继续尝试");
            }
            catch (OperationCanceledException) when (!_ct.IsCancellationRequested)
            {
                _logger.LogWarning("[联机] 退出多人世界超时（60s），后续步骤将继续尝试");
            }
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 联机前多人世界检查异常，忽略并继续");
        }
    }

    /// <summary>
    /// 设置世界权限为确认后才能加入
    /// 参考 AutoPermission JS 脚本的点击坐标（基于1920x1080）
    /// </summary>
    private async Task SetWorldPermissionToConfirmJoin()
    {
        if (_worldPermissionSet)
        {
            _logger.LogInformation("[联机] 世界权限已设置过，跳过");
            return;
        }
        try
        {
            // 返回主界面再操作
            var returnMainUiTask = new ReturnMainUiTask();
            await returnMainUiTask.Start(_ct);
            
            _logger.LogInformation("[联机] 开始设置世界权限为确认后才能加入");
            // 按F2打开联机界面
            Simulation.SendInput.SimulateAction(GIActions.OpenCoOpScreen);
            await Task.Delay(1500, _ct);
            
            // 点击"世界权限"选项（坐标来自 AutoPermission JS 脚本）
            GameCaptureRegion.GameRegion1080PPosClick(330, 1010);
            await Task.Delay(800, _ct);
            
            // 点击"确认后可加入"选项
            GameCaptureRegion.GameRegion1080PPosClick(330, 960);
            await Task.Delay(500, _ct);
            
            // 按ESC关闭联机界面
            Simulation.SendInput.SimulateAction(GIActions.OpenCoOpScreen);
            await Task.Delay(800, _ct);
            
            _logger.LogInformation("[联机] 世界权限已设置为确认后才能加入");
            _worldPermissionSet = true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 设置世界权限时发生异常，将继续执行");
        }
    }

    /// <summary>
    /// 获取当前世界轮次（skip-route-wait-point-report 修复）
    /// </summary>
    private int GetCurrentWorldRound()
    {
        try
        {
            // 从WaitPointStateManager获取当前轮次
            if (_multiplayerCoordinator != null)
            {
                // 通过状态统计获取当前轮次
                var stats = _multiplayerCoordinator.StateManager.GetStatistics();
                int currentRound = stats.CurrentWorldRound;
                _logger.LogDebug("[联机] 获取当前世界轮次: {Round}", currentRound);
                return currentRound;
            }
            
            // 默认返回1（第一轮）
            return 1;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[联机] 获取当前世界轮次时发生异常");
            return 1; // 异常时返回默认值
        }
    }

    /// <summary>
    /// 获取下一个同步点用于等待点上报（skip-route-wait-point-report 修复）
    /// 优先选择"传送必同步"的同步点
    /// 使用实际执行的路线列表（groupRoutes）而非配置的路线列表
    /// </summary>
    private async Task<string> GetNextSyncPointForWaitPointAsync(int currentRouteIndex, List<RouteInfo> actualRoutes)
    {
        try
        {
            // 获取下一个路线的索引
            int nextRouteIndex = currentRouteIndex + 1;
            
            // 使用实际执行的路线列表
            var routes = actualRoutes;
            if (nextRouteIndex >= routes.Count)
            {
                _logger.LogWarning("[联机] 下一个路线索引 {NextIndex} 超出范围（总路线数: {Total}）", 
                    nextRouteIndex, routes.Count);
                return string.Empty;
            }
            
            var nextRoute = routes[nextRouteIndex];
            _logger.LogDebug("[联机] 获取下一个同步点：路线索引={Index}, 文件名={FileName}", 
                nextRouteIndex, nextRoute.FileName);
            
            // 传送必同步：始终选择第一个传送点作为同步点
            // 同步点ID格式：{FileName}_tp_{listIdx}_{wpIdx}
            _logger.LogInformation("[联机] 传送必同步（默认启用），选择传送点作为等待点");
            return $"{nextRoute.FileName}_tp_0_0"; // 第一个路线的第一个传送点
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "[联机] 获取下一个同步点时发生异常");
            return string.Empty;
        }
    }
}