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

    // 当前正在执行的 PathExecutor 引用（联机模式下供 AnomalyDetector 信号传递使用）
    private PathExecutor? _activeExecutor;

    // 按线路切角色 Provider（hoeing-multiplayer-per-route-switch-roles）：仅联机 + 配了角色时注入 Hook。
    private PerRouteSwitchRolesProvider? _perRouteSwitchProvider;

    // 反复复苏双层兜底（multi-revival-rapid-recurrence-fallback spec）：
    // 路线生命周期内累计复苏时间戳，OnMultiplayerDefeatedDetected 时调用 Track 决定 escalation 动作；
    // ExecuteRoute 入口 Reset 同时覆盖多世界轮换（design §2.6）。
    private readonly RevivalRecurrenceTracker _revivalTracker = new();

    // 线路重试模式（hoeing-multiplayer-route-retry-mode spec）：ExecuteRoute 入口按线路名 + 白名单算一次，
    // defeat 回调读它决定是否把 escalation 覆盖为 RetrySegment。多世界轮换每次 ExecuteRoute 重算。
    private volatile bool _currentRouteRetryModeEnabled;

    // 线路重试模式 v2（§0）：当前已订阅 PlayerAnomalyNotifyReceived 的 client，供 SetCoordinator 多次调用时取消旧订阅。
    private CoordinatorClient? _anomalySubscribedClient;

    /// <summary>注入按线路切角色 Provider（hoeing-multiplayer-per-route-switch-roles）。null = 不启用。</summary>
    public void SetPerRouteSwitchProvider(PerRouteSwitchRolesProvider? provider) => _perRouteSwitchProvider = provider;

    public void SetCoordinator(MultiplayerCoordinator? coordinator)
    {
        _coordinator = coordinator;
        
        // 设置异常检测器的复苏回调
        if (coordinator != null)
        {
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
            //
            // 线路重试模式 v2（§0）：自己复苏 → 本机 HandleRevivalTrigger（回神像重跑本段）
            //                        + 若命中白名单则广播给队友（复用 PlayerAnomalyNotify 通道，让全员一起重跑）。
            _anomalyDetector.OnMultiplayerDefeatedDetected = () =>
            {
                var executor = _activeExecutor;
                if (executor == null) return;

                bool retryModeRoute = HandleRevivalTrigger(executor, isBroadcastReceived: false);

                // 线路重试模式 v2（EB-v2-1）：命中白名单线路时，广播给房间所有成员，触发全员回神像重跑。
                // 复用 PlayerAnomalyNotify 通道（服务端仅转发 + 写死代码字典，无活跃副作用，见 design §0.4）。
                if (retryModeRoute)
                {
                    var c = _coordinator;
                    if (c != null && c.Client.IsConnected)
                    {
                        var myUid = c.Client.MyPlayerUid;
                        var routeIndex = executor.CurrentRouteIndex;
                        // fire-and-forget 广播；失败静默（不阻塞复苏本地流程）
                        _ = c.Client.ReportAnomalyAsync(myUid, routeIndex, false);
                        Logger.LogWarning("[联机][重试模式v2] 本机复苏，已广播给队友触发全员回神像重跑，uid={Uid}, routeIndex={Idx}", myUid, routeIndex);
                    }
                }
            };

            // 线路重试模式 v2（EB-v2-1）：订阅队友复苏广播。收到后（过滤自己 + retry-route + 有活动执行器）
            // 本机同样 HandleRevivalTrigger（回神像重跑本段），实现"任一成员复苏 → 全员反应"。
            // 先取消旧订阅（SetCoordinator 多世界轮换会多次调用），再订阅新 client。
            if (_anomalySubscribedClient != null)
                _anomalySubscribedClient.PlayerAnomalyNotifyReceived -= OnTeammateRevivalBroadcast;
            coordinator.Client.PlayerAnomalyNotifyReceived += OnTeammateRevivalBroadcast;
            _anomalySubscribedClient = coordinator.Client;
        }
        else
        {
            _anomalyDetector.OnRevivalDetected = null;
            _anomalyDetector.OnMultiplayerDefeatedDetected = null;
            if (_anomalySubscribedClient != null)
            {
                _anomalySubscribedClient.PlayerAnomalyNotifyReceived -= OnTeammateRevivalBroadcast;
                _anomalySubscribedClient = null;
            }
        }
    }

    /// <summary>
    /// 队友复苏广播接收（线路重试模式 v2 / EB-v2-1）。复用 PlayerAnomalyNotify 通道。
    /// 过滤自己（服务端广播含发送方，客户端不自动过滤 Notify）；仅 retry-route + 有活动执行器时反应。
    /// 反应 = 本机同样回神像重跑本段（与自己复苏同源）。不再二次广播（避免风暴）。
    /// </summary>
    private void OnTeammateRevivalBroadcast(string playerUid, int routeIndex, bool passedSyncPoint)
    {
        var c = _coordinator;
        if (c == null) return;
        // 过滤自己：自己复苏走 OnMultiplayerDefeatedDetected，不重复反应
        if (playerUid == c.Client.MyPlayerUid) return;

        var executor = _activeExecutor;
        if (executor == null) return;
        // 仅 retry-route 才全员重跑（白名单房主同步，全员一致）
        if (!_currentRouteRetryModeEnabled) return;

        Logger.LogWarning("[联机][重试模式v2] 收到队友复苏广播（uid={Uid}, routeIndex={Idx}）→ 本机也前往神像回血", playerUid, routeIndex);
        // 线路重试模式 v2（§0）：队友死亡时，本机（包括健康成员）也一起去神像回血，然后全员重跑。
        // 统一调用 HandleRevivalTrigger，让健康队友也走 SignalMultiplayerRevival(RetrySegment) 路径。
        HandleRevivalTrigger(executor, isBroadcastReceived: true);
    }

    /// <summary>
    /// 复苏触发统一处理（线路重试模式 v2 / §0.1）。自己复苏与收到队友广播共用。
    /// 本机 RevivalRecurrenceTracker.Track 计数 → retry-route 时按"只重试一次"覆盖动作：
    ///   Count &lt;= 1 → RetrySegment（回神像重跑本段，正常同步）；Count &gt;= 2 → SkipSegment（跳段，原流程）。
    /// 非 retry-route → 沿用 Track 的原决策动作（multi-revival-rapid-recurrence-fallback 行为不变）。
    /// 通过 executor.SignalMultiplayerRevival(action) 透传到消费点 + catch 块。
    /// </summary>
    /// <returns>本线路是否为 retry-route（供调用方决定是否广播）。</returns>
    private bool HandleRevivalTrigger(PathExecutor executor, bool isBroadcastReceived)
    {
        var action = _revivalTracker.Track(
            DateTime.UtcNow,
            _config.RapidRevivalWindowSeconds,
            _config.RapidRevivalThreshold,
            _config.RouteRevivalCap);

        bool retryModeRoute = _currentRouteRetryModeEnabled;
        if (retryModeRoute)
        {
            // 只重试一次（EB-v2-3）：本段第 1 次复苏（Count<=1）→ RetrySegment（回神像重跑本段）；
            // 第 2 次及以后（Count>=2）→ SkipSegment（跳段，防死循环）。显式 override，不依赖 RRT rapid/cap 阈值。
            action = _revivalTracker.Count <= 1
                ? RevivalEscalationAction.RetrySegment
                : RevivalEscalationAction.SkipSegment;
            Logger.LogWarning("[联机][重试模式v2] {Src}触发，本段复苏计数={Count} → action={Action}",
                isBroadcastReceived ? "队友广播" : "本机复苏", _revivalTracker.Count, action);
        }
        else if (action != RevivalEscalationAction.Continue)
        {
            Logger.LogWarning(
                "[联机] 反复复苏触发升级：count={Count}, action={Action}, window={Win}s, rapid={Rapid}, cap={Cap}",
                _revivalTracker.Count, action,
                _config.RapidRevivalWindowSeconds, _config.RapidRevivalThreshold, _config.RouteRevivalCap);
        }

        executor.SignalMultiplayerRevival(action);
        return retryModeRoute;
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

        // multi-revival-rapid-recurrence-fallback：每条路线开始时清空时间戳列表（OQ-4 = B 多世界轮换自动覆盖）
        _revivalTracker.Reset();

        // 线路重试模式（hoeing-multiplayer-route-retry-mode spec）：按本线路文件名（去扩展名）+ 白名单关键词算一次，
        // 缓存供 defeat 回调读取。多世界轮换每次进入 ExecuteRoute 重算。
        var __retryKeywords = RouteRetryModeDecisions.ParseKeywords(_config.RouteRetryModeKeywords);
        _currentRouteRetryModeEnabled = RouteRetryModeDecisions.IsRetryRoute(
            System.IO.Path.GetFileNameWithoutExtension(route.FileName), __retryKeywords);
        if (_currentRouteRetryModeEnabled)
            Logger.LogInformation("[联机][重试模式] 本线路启用线路重试模式: {Name}", route.FileName);

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
                    executor.PartyConfig = _partyConfig;
                    executor.CurrentJsonRouteIndex = currentJsonRouteIndex;
                    // 线路重试模式 v2（§0）：注入本线路是否命中重试白名单（房主同步，全员一致）。
                    // 仅 true 时 PathExecutor 才启用"段出口屏障"；其他线路整块短路，零影响。
                    executor.RouteRetryModeEnabled = _currentRouteRetryModeEnabled;
                    
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
