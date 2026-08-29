using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoHoeing.Models;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Services;

/// <summary>
/// 路线执行引擎：调用PathExecutor执行地图追踪，并发运行拾取/异常检测/泥头车子任务
/// </summary>
public class RouteExecutionEngine
{
    private static readonly ILogger Logger = App.GetLogger<RouteExecutionEngine>();

    private readonly TemplatePickupService _pickupService;
    private readonly AnomalyDetector _anomalyDetector;
    private readonly DumperService _dumperService;
    private readonly BlacklistManager _blacklistManager;
    private readonly AutoHoeingConfig _config;
    private readonly PathingPartyConfig? _partyConfig;

    private volatile bool _running;
    private MultiplayerCoordinator? _coordinator;
    private WorldStateMonitor? _worldStateMonitor;
    private volatile bool _currentRouteRetryModeEnabled;
    private CoordinatorClient? _anomalySubscribedClient;

    // 当前正在执行的 PathExecutor 引用（联机模式下供 AnomalyDetector 信号传递使用）
    private PathExecutor? _activeExecutor;

    // 按线路切角色 Provider（hoeing-multiplayer-per-route-switch-roles）：仅联机 + 配了角色时注入 Hook。
    private PerRouteSwitchRolesProvider? _perRouteSwitchProvider;

    // 反复复苏双层兜底（multi-revival-rapid-recurrence-fallback spec）：
    // 路线生命周期内累计复苏时间戳，OnMultiplayerDefeatedDetected 时调用 Track 决定 escalation 动作；
    // ExecuteRoute 入口 Reset 同时覆盖多世界轮换（design §2.6）。
    private readonly RevivalRecurrenceTracker _revivalTracker = new();

    // 线路级：本轮有哪些 route index 在战斗中触发复苏（轮末统一重跑）。
    private readonly HashSet<int> _routeRerunMarkSet = new();
    // 线路级：本轮已经重跑过的 route index（只重跑一次）。
    private readonly HashSet<int> _routeRerunDoneSet = new();

    /// <summary>注入按线路切角色 Provider（hoeing-multiplayer-per-route-switch-roles）。null = 不启用。</summary>
    public void SetPerRouteSwitchProvider(PerRouteSwitchRolesProvider? provider) => _perRouteSwitchProvider = provider;

    public void SetCoordinator(MultiplayerCoordinator? coordinator)
    {
        if (_anomalySubscribedClient != null)
        {
            _anomalySubscribedClient.PlayerAnomalyNotifyReceived -= OnTeammateRevivalBroadcast;
            _anomalySubscribedClient.PlayerAnomalyNotifyFightPointReceived -= OnTeammateRevivalFightPointBroadcast;
            _anomalySubscribedClient = null;
        }

        _coordinator = coordinator;
        
        // 设置异常检测器的复苏回调
        if (coordinator != null)
        {
            _anomalySubscribedClient = coordinator.Client;
            _anomalySubscribedClient.PlayerAnomalyNotifyReceived += OnTeammateRevivalBroadcast;
            _anomalySubscribedClient.PlayerAnomalyNotifyFightPointReceived += OnTeammateRevivalFightPointBroadcast;

            _anomalyDetector.OnRevivalDetected = async () =>
            {
                // 不在这里上报 Reviving，避免覆盖 targetProgress 为 -1。
                // 复苏后会触发 RetryException，由 PathExecutor 的 catch 块上报带正确 targetProgress 的 Reviving。
                Logger.LogInformation("[联机] 检测到复苏，等待 RetryException 路径上报 Reviving");
                await Task.CompletedTask;
            };

            // 联机模式专用：色块检测到"已倒下"时，向当前 PathExecutor 发信号，
            // 让其在主循环抛 RetryException，进入"同步点前/后"统一异常处理流程。
            // 注意：这个回调只对联机的色块检测生效（IsMultiplayerDefeated），
            // 单机的模板匹配复苏走的是另一条 OnRevivalDetected 回调，不受影响。
            _anomalyDetector.OnMultiplayerDefeatedDetected = () =>
            {
                var executor = _activeExecutor;
                if (executor == null) return;
                if (!_currentRouteRetryModeEnabled) return;

                // === 传送后复苏保护短路（multiplayer-hoeing-post-teleport-revival-protection）===
                // 命中保护窗口 → 仅本机处理，不进入 tracker / 路线标记 / 异常上报（requirements 2.3, 3.6）。
                // 消费由 PathExecutor.catch(RetryException) 保护分支（Task 4.3）完成，此处只只读判定短路，
                // 不做任何 CAS 消费，避免双重消费。未命中则完全走下方既有回调逻辑。
                if (executor.IsPostTeleportRevivalProtectionHit())
                {
                    Logger.LogInformation("[联机] 传送后复苏保护命中，短路团队副作用，仅本机从当前段起点重跑");
                    return;
                }

                HandleRevivalTrigger(executor);
                // 标记/上报必须用"线路在组内的索引"（CurrentJsonRouteIndex），而非"当前段在路线内的索引"
                // （CurrentRouteIndex）。误用段索引会导致：从第 N 条线路开始时，段0=0 被错记成线路0，
                // 轮末重跑取 groupRoutes[0] 重跑第一条线路，而非真正发生复苏的线路（重跑错误线路 bugfix）。
                var routeIndex = executor.CurrentJsonRouteIndex;
                if (RouteRerunDecisions.ShouldMarkRerun(routeIndex, _routeRerunMarkSet, _routeRerunDoneSet))
                {
                    _routeRerunMarkSet.Add(routeIndex);
                    Logger.LogWarning("[联机][重试模式] 本线路有成员复苏，标记需重跑: routeIdx={Idx}", routeIndex);
                }

                var c = _coordinator;
                if (c != null && c.Client.IsConnected)
                {
                    // v3（同伴战斗点级跳过）：附带复苏者正在战斗的战斗点（segIdx*10000+wpIdx），
                    // 供其他成员做战斗点级跳过（不是 v2 的集体回神像重跑）。
                    // 既有 3 参 ReportAnomalyAsync 保留作 fallback（老客户端走线路级）。
                    var fightPointId = executor.CurWaypoints.Item1 * 10000 + executor.CurWaypoint.Item1;
                    _ = c.Client.ReportAnomalyWithFightPointAsync(c.Client.MyPlayerUid, routeIndex, fightPointId);
                }
            };
        }
        else
        {
            _anomalyDetector.OnRevivalDetected = null;
            _anomalyDetector.OnMultiplayerDefeatedDetected = null;
        }
    }

    // 广播中的 routeIndex 是复苏成员所在的权威线路；接收方当前线路仅用于决定是否跳过当前战斗。
    private void OnTeammateRevivalBroadcast(string playerUid, int routeIndex, bool passedSyncPoint)
    {
        var c = _coordinator;
        if (c != null && playerUid == c.Client.MyPlayerUid) return;
        if (routeIndex < 0) return;

        var executor = _activeExecutor;
        if (executor == null) return;

        if (RouteRerunDecisions.ShouldMarkRerun(routeIndex, _routeRerunMarkSet, _routeRerunDoneSet))
        {
            _routeRerunMarkSet.Add(routeIndex);
            Logger.LogWarning(
                "[联机][重试模式] 收到队友复苏广播，标记权威线路需重跑: playerUid={PlayerUid}, broadcastRouteIdx={BroadcastIdx}, currentRouteIdx={CurrentIdx}, passedSyncPoint={Passed}",
                playerUid, routeIndex, executor.CurrentJsonRouteIndex, passedSyncPoint);
        }
        else
        {
            Logger.LogInformation(
                "[联机][重试模式] 收到队友复苏广播，线路已标记或已重跑: playerUid={PlayerUid}, broadcastRouteIdx={BroadcastIdx}, currentRouteIdx={CurrentIdx}, passedSyncPoint={Passed}",
                playerUid, routeIndex, executor.CurrentJsonRouteIndex, passedSyncPoint);
        }

        if (executor.CurrentJsonRouteIndex == routeIndex)
        {
            executor.WantsSkipCurrentFight = true;
            Logger.LogInformation(
                "[联机][重试模式] 当前正在广播线路，跳过当前战斗: broadcastRouteIdx={BroadcastIdx}, currentRouteIdx={CurrentIdx}",
                routeIndex, executor.CurrentJsonRouteIndex);
        }
    }

    // === 联机 v3：同伴复苏广播（带战斗点）三态处理 ===
    // 复苏成员通过 ReportAnomalyWithFightPointAsync 广播其正在战斗的战斗点（segIdx*10000+wpIdx）。
    // 本成员收到后，按 design.md §9.3 三态处理：
    //   情况1（自己正处在该战斗点/正在打）→ 掐断当前战斗（只停当下，不去神像、不跳段）。
    //   情况3（自己尚未到达该战斗点）→ 记录待跳过点，走到该点时 continue 跳过。
    //   情况2（已越过该点）→ 段范围校验忽略，不置任何标志。
    // 复苏者本人路径（v2 OnTeammateRevivalBroadcast）逐字节不变，作为 fallback 通道。
    private void OnTeammateRevivalFightPointBroadcast(string playerUid, int routeIndex, int fightPointId)
    {
        var c = _coordinator;
        if (c != null && playerUid == c.Client.MyPlayerUid) return;   // 过滤自己
        if (routeIndex < 0 || fightPointId < 0) return;

        var executor = _activeExecutor;
        if (executor == null) return;

        // 照常标记权威线路需重跑（与既有 OnTeammateRevivalBroadcast 行为一致）。
        if (RouteRerunDecisions.ShouldMarkRerun(routeIndex, _routeRerunMarkSet, _routeRerunDoneSet))
        {
            _routeRerunMarkSet.Add(routeIndex);
            Logger.LogWarning(
                "[联机][重试模式] 收到队友复苏广播(带战斗点)，标记权威线路需重跑: playerUid={PlayerUid}, broadcastRouteIdx={BroadcastIdx}, currentRouteIdx={CurrentIdx}",
                playerUid, routeIndex, executor.CurrentJsonRouteIndex);
        }

        if (executor.CurrentJsonRouteIndex != routeIndex) return;      // 不是本线路，忽略（情况2隐含：本线路才处理）

        var selfFp = executor.CurWaypoints.Item1 * 10000 + executor.CurWaypoint.Item1;   // 自己当前所处点
        if (selfFp == fightPointId)
        {
            // 情况1：自己正处在复苏战斗点（正在打/即将进入）→ 掐断战斗（只停当下）
            executor.WantsSkipCurrentFight = true;
            Logger.LogInformation(
                "[联机][重试模式] 当前正处在复苏战斗点，掐断当前战斗: broadcastRouteIdx={BroadcastIdx}, selfFp={SelfFp}",
                routeIndex, selfFp);
        }
        else if (FightPointSkipDecisions.ShouldRecordPendingSkip(fightPointId, executor.CurWaypoints.Item1))
        {
            // 情况3：自己尚未到达复苏战斗点（或已在别处）→ 记录待跳过点，走到时才消费
            executor.SkipRevivalFightPointId = fightPointId;
            Logger.LogInformation(
                "[联机][重试模式] 记录待跳过复苏战斗点: broadcastRouteIdx={BroadcastIdx}, fightPointId={Fp}, selfFp={SelfFp}",
                routeIndex, fightPointId, selfFp);
        }
        // 情况2：已越过该点（段范围校验在 ShouldRecordPendingSkip 内处理）→ 忽略
    }

    /// <summary>
    /// 复苏触发统一处理。保留 RevivalRecurrenceTracker 的原始升级决策，
    /// 由 PathExecutor 按 Continue/SkipSegment/SkipRoute 原流程消费。
    /// </summary>
    private void HandleRevivalTrigger(PathExecutor executor)
    {
        var action = _revivalTracker.Track(
            DateTime.UtcNow,
            _config.RapidRevivalWindowSeconds,
            _config.RapidRevivalThreshold,
            _config.RouteRevivalCap);
        if (action != RevivalEscalationAction.Continue)
        {
            Logger.LogWarning(
                "[联机] 反复复苏触发升级：count={Count}, action={Action}, window={Win}s, rapid={Rapid}, cap={Cap}",
                _revivalTracker.Count, action,
                _config.RapidRevivalWindowSeconds, _config.RapidRevivalThreshold, _config.RouteRevivalCap);
        }

        executor.SignalMultiplayerRevival(action);
    }

    public HashSet<int> TakeRouteRerunMarkSet()
    {
        var result = new HashSet<int>(_routeRerunMarkSet);
        _routeRerunMarkSet.Clear();
        return result;
    }

    public bool IsRouteRerunDone(int routeIndex) => _routeRerunDoneSet.Contains(routeIndex);

    public void MarkRouteRerunDone(int routeIndex) => _routeRerunDoneSet.Add(routeIndex);

    public void ClearRerunRoundState()
    {
        _routeRerunMarkSet.Clear();
        _routeRerunDoneSet.Clear();
    }
    public void SetWorldStateMonitor(WorldStateMonitor? monitor) => _worldStateMonitor = monitor;

    public RouteExecutionEngine(
        TemplatePickupService pickupService,
        AnomalyDetector anomalyDetector,
        DumperService dumperService,
        BlacklistManager blacklistManager,
        AutoHoeingConfig config,
        PathingPartyConfig? partyConfig = null)
    {
        _pickupService = pickupService;
        _anomalyDetector = anomalyDetector;
        _dumperService = dumperService;
        _blacklistManager = blacklistManager;
        _config = config;
        _partyConfig = partyConfig;
    }

    /// <summary>
    /// 执行单条路线，并发启动所有子任务
    /// </summary>
    public async Task<RouteExecutionResult> ExecuteRoute(
        RouteInfo route, CancellationToken ct, int currentJsonRouteIndex = 0)
    {
        var result = new RouteExecutionResult();
        _running = true;
        _anomalyDetector.ShouldSwitchFurina = false;
        _currentRouteRetryModeEnabled = RouteRetryModeDecisions.IsRetryRoute(
            System.IO.Path.GetFileNameWithoutExtension(route.FileName),
            RouteRetryModeDecisions.ParseKeywords(_config.RouteRetryModeKeywords));

        // multi-revival-rapid-recurrence-fallback：每条路线开始时清空时间戳列表（OQ-4 = B 多世界轮换自动覆盖）
        _revivalTracker.Reset();

        // 设置路线相关材料过滤
        if (_config.UseRouteRelatedMaterialsOnly)
            _pickupService.SetRouteRelatedMaterials(route.MonsterInfo, route.PickupHistory);
        else
            _pickupService.ResetAllEnabled();

        var sw = Stopwatch.StartNew();

        using var cts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var linkedCt = cts.Token;

        bool IsRunning() => _running && !linkedCt.IsCancellationRequested;

        bool pathingFullyCompleted = false;
        bool skipRouteRequested = false;
        string? skipRouteReason = null;

        // 主路线执行任务
        var pathingTask = Task.Run(async () =>
        {
            try
            {
                Logger.LogInformation("开始执行地图追踪任务: {Name}", route.FileName);
                var task = PathingTask.BuildFromFilePath(route.FullPath);
                if (task != null)
                {
                    var executor = new PathExecutor(ct);
                    executor.WantsSkipCurrentFight = false;
                    executor.SkipRevivalFightPointId = -1;   // v3：每条线路入口复位待跳过战斗点
                    executor.PartyConfig = _partyConfig;
                    executor.CurrentJsonRouteIndex = currentJsonRouteIndex;
                    
                    // 联机模式：注入 MultiplayerCoordinator，并禁用自动领取派遣
                    if (_config.MultiplayerEnabled && _coordinator != null)
                    {
                        executor.MultiplayerCoordinator = _coordinator;
                        executor.WorldStateMonitor = _worldStateMonitor;
                        PathExecutor.CurrentWorldStateMonitor = _worldStateMonitor;
                        PathExecutor.CurrentMultiplayerCoordinator = _coordinator;
                        // 第2层（hoeing-multiplayer-otherworld-teammate-avatar-misrecognition-fix）：
                        // 注入"读实时协调器权威人数"的委托，供 DetectedMultiGameStatus 交叉校验。
                        // 委托每次调用实时读 _coordinator，连接断开自动返回 Available=false 退回纯视觉。
                        PathingConditionConfig.AuthoritativePlayerCountProvider = () =>
                        {
                            var c = _coordinator;
                            if (c == null || !c.IsConnected) return (false, 0, false);
                            return (true, c.Client.CurrentRoomPlayerCount, c.Client.IsHost);
                        };
                        executor.PartyConfig.DisableAutoFetchDispatch = true;
                        Logger.LogInformation("[联机] 已注入 MultiplayerCoordinator 到 PathExecutor，路线: {Name}", route.FileName);
                    }
                    else
                    {
                        Logger.LogDebug("[联机] MultiplayerEnabled={Enabled}，coordinator={HasCoord}，单机模式执行",
                            _config.MultiplayerEnabled, _coordinator != null);
                    }
                    
                    // 按线路切角色（hoeing-multiplayer-per-route-switch-roles）：仅联机 + 配了角色时注入 Hook
                    if (_config.MultiplayerEnabled && _coordinator != null && _perRouteSwitchProvider != null)
                    {
                        var perRouteHook = _perRouteSwitchProvider.BuildHookForRoute(route);
                        if (perRouteHook != null)
                        {
                            executor.PerRouteSwitchHook = perRouteHook;
                            Logger.LogInformation("[联机][按线路切角色] 线路 {Name} 已注入切角色 Hook", route.FileName);
                        }
                    }
                    
                    // 注册当前 executor，供 AnomalyDetector 异步信号使用
                    _activeExecutor = executor;
                    try
                    {
                        Logger.LogInformation("[DEBUG] 开始调用 executor.Pathing，路线: {Name}", route.FileName);
                        await executor.Pathing(task);
                        Logger.LogInformation("[DEBUG] executor.Pathing 完成，SuccessEnd={End}，路线: {Name}", executor.SuccessEnd, route.FileName);
                        pathingFullyCompleted = executor.SuccessEnd;

                        // 联机模式：传递路线跳过标志位（需求 1）
                        if (executor.SkipRouteRequested)
                        {
                            skipRouteRequested = true;
                            skipRouteReason = executor.SkipRouteReason;
                            pathingFullyCompleted = false; // 跳过的路线不算完整完成
                            Logger.LogInformation("[联机] 路线 {Name} 被标记为跳过: {Reason}", route.FileName, skipRouteReason);
                        }
                    }
                    finally
                    {
                        // 路线结束（含异常路径）解除引用，避免下一条路线之前 AnomalyDetector 误信号到旧 executor
                        if (ReferenceEquals(_activeExecutor, executor))
                        {
                            _activeExecutor = null;
                        }
                    }
                }
                else
                {
                    Logger.LogWarning("[DEBUG] BuildFromFilePath 返回 null，路线: {Name}", route.FileName);
                }
            }
            catch (OperationCanceledException)
            {
                throw; // 让取消异常穿透，不吞掉
            }
            catch (Exception ex)
            {
                Logger.LogError("执行地图追踪出错: {Msg}", ex.Message);
            }
            finally
            {
                _running = false;
                // 补打线路结束日志，使 LogParse 能将每条独立任务版锄地线路闭合为 ConfigTask。
                // 复用解析器已识别的格式 → 脚本执行结束: "xxx"（LogParse.cs:161 现有判定）。
                // name 必须与开始日志的 route.FileName 完全一致，否则无法配对。
                // 放在最外层 finally：正常完成 / 异常 / 取消 / BuildFromFilePath 返回 null 都能闭合。
                Logger.LogInformation("→ 脚本执行结束: {Name}", route.FileName);
            }
        }, linkedCt);

        // 并发子任务列表
        var tasks = new List<Task> { pathingTask };

        // 模板匹配拾取
        if (_config.PickupMode.Contains("模板匹配"))
        {
            tasks.Add(Task.Run(() => _pickupService.RunPickupLoop(
                IsRunning, _blacklistManager.Blacklist,
                _config.PickupDelay, _config.RollingDelay,
                _config.ScrollCycle, _config.FindFInterval,
                linkedCt), linkedCt));
        }

        // 异常状态检测
        tasks.Add(Task.Run(() => _anomalyDetector.RunDetectionLoop(IsRunning, linkedCt), linkedCt));

        // 黑名单检测
        if (_config.PickupMode.Contains("模板匹配"))
        {
            tasks.Add(Task.Run(() => _blacklistManager.RunDetectionLoop(
                IsRunning, _pickupService.TargetItems.ToList(), linkedCt), linkedCt));
        }

        // 泥头车
        var dumperChars = ParseDumperCharacters(_config.DumperCharacters);
        if (dumperChars.Count > 0)
        {
            var pathingData = PathingTask.BuildFromFilePath(route.FullPath);
            if (pathingData != null)
            {
                CombatScenes? combatScenes = null;
                try
                {
                    using var region = CaptureToRectArea();
                    combatScenes = new CombatScenes().InitializeTeam(region);
                    if (!combatScenes.CheckTeamInitialized())
                    {
                        Logger.LogWarning("泥头车队伍识别失败，跳过泥头车功能");
                        combatScenes.Dispose();
                        combatScenes = null;
                    }
                }
                catch (Exception ex)
                {
                    Logger.LogWarning("泥头车CombatScenes初始化异常: {Msg}", ex.Message);
                    combatScenes?.Dispose();
                    combatScenes = null;
                }

                if (combatScenes != null)
                {
                    var cs = combatScenes;
                    tasks.Add(Task.Run(async () =>
                    {
                        try
                        {
                            await _dumperService.RunDumperLoop(
                                pathingData.Positions, dumperChars, route.MapName,
                                cs, IsRunning, linkedCt);
                        }
                        finally
                        {
                            cs.Dispose();
                        }
                    }, linkedCt));
                }
            }
        }

        // 等待所有任务完成
        try
        {
            await Task.WhenAll(tasks);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            Logger.LogDebug("并发任务异常: {Msg}", ex.Message);
        }

        sw.Stop();
        result.ActualDuration = sw.Elapsed.TotalSeconds;
        result.ShouldSwitchFurina = _anomalyDetector.ShouldSwitchFurina;
        result.Success = true;
        result.FullyCompleted = pathingFullyCompleted;
        result.SkipRouteRequested = skipRouteRequested;
        result.SkipRouteReason = skipRouteReason;

        return result;
    }

    private static List<int> ParseDumperCharacters(string input)
    {
        if (string.IsNullOrWhiteSpace(input)) return new();
        return input.Split('，')
            .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
            .Where(n => n >= 1 && n <= 4)
            .ToList();
    }
}
