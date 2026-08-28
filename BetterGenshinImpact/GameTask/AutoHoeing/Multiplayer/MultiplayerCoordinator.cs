#nullable enable

using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using Microsoft.AspNetCore.SignalR;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 联机协调器门面类：统一管理联机模式下的所有协调逻辑。
/// 整合了路线同步、异常状态管理、等待点状态跟踪等功能，
/// 替代了之前分散在多个文件中的协调机制。
/// </summary>
public class MultiplayerCoordinator : IAsyncDisposable
{
    private readonly ILogger<MultiplayerCoordinator> _logger = App.GetLogger<MultiplayerCoordinator>();
    private readonly CoordinatorClient _client;
    private readonly AutoHoeingConfig _config;
    private readonly SyncPointResolver _resolver;

    // === 子协调器 ===
    public RouteSyncCoordinator? RouteSyncCoordinator { get; private set; }
    public AbnormalStatusManager? StateManager { get; private set; }
    public WaitPointStateManager? WaitPointStateManager { get; private set; }
    /// <summary>
    /// 万叶聚物同步协调器（multiplayer-kazuha-collect-sync）。
    /// 由 PathExecutor 在战后回点 Delay 处通过 <see cref="KazuhaCollectSyncCoordinator.WaitAtFightPointAsync"/> 调用。
    /// </summary>
    public KazuhaCollectSyncCoordinator? KazuhaCollectSync { get; private set; }

    // === 基础状态 ===
    public bool IsHost { get; private set; }
    public bool IsConnected => _client.IsConnected;
    public bool IsExitTriggered { get; private set; }
    public bool IsAbortRequested { get; private set; }
    public int CurrentRouteIndex => _client.CurrentRouteIndex;
    
    /// <summary>
    /// 当前玩家 UID
    /// </summary>
    public string PlayerUid => _client.PlayerUid;

    /// <summary>
    /// 暴露持有的 AutoHoeingConfig 副本（AutoHoeingTask 在配置组场景下经
    /// JsonSerializer 深拷贝 + line 236-239 FastSync 字段强制重置 + line 243
    /// ApplySettingsOverride() 三步处理后产出的实例）。
    ///
    /// 供 PathExecutor 读取 FastSync 三字段（FastSyncPointEnabled /
    /// FastSyncPathingDistance / FastSyncTeleportLoadingDelayMs）时使用，
    /// 替代 TaskContext.Instance().Config.AutoHoeingConfig（全局未被配置组覆盖）。
    ///
    /// 与 KazuhaCollectSyncCoordinator._config 暴露给 PathExecutor 的模式同构
    /// （详见 .kiro/specs/pathexecutor-fastsync-config-readsource-fix）。
    ///
    /// 本 getter 返回引用，C# 层面调用方可写，但调用纪律保证只读使用
    /// （与 KazuhaCollectSync 现有持有副本不做防护的模式对齐，OQ-5=a 决议）。
    /// </summary>
    public AutoHoeingConfig EffectiveConfig => _config;

    /// <summary>暴露底层 CoordinatorClient 供 AutoFightTask 订阅 AllFightDone 事件（multiplayer-shared-fight-end-quorum-sync spec）。</summary>
    public CoordinatorClient Client => _client;

    // === 连续超时控制（需求 5）===
    private int _consecutiveSyncTimeoutCount;
    private int _consecutiveSkipCount;

    // === 基于经验判断停止锄地（multiplayer-hoeing-exp-cap-stop）===
    /// <summary>本机连续无经验的有效战斗节点计数。</summary>
    private int _consecutiveNoExpCount;
    /// <summary>本机当前是否处于"已上报达上限"态（上报/撤回反复翻转，非一次性闩锁）。</summary>
    private bool _expCapReported;
    /// <summary>本机当前是否处于"已上报连续2场无经验预警"态（exp-cap-prefinal-stop-by-two-noexp）。</summary>
    private bool _twoConsecutiveReported;
    /// <summary>本轮世界本机是否已发送过团队 arming 信号（幂等门控，每轮至多一次）。multiplayer-hoeing-exp-cap-stop R7.1。</summary>
    private bool _expArmedSent;

    // === 跳过同步点标志 ===
    private volatile bool _skipNextSyncPoint;

    // === 集体卡死跳段信号位（multiplayer-mutual-wait-collective-skip spec / OQ-6 A）===
    /// <summary>
    /// 服务端 RequestSkipToProgress 事件触发后置 1，客户端 4 处消费点 CAS 1→0 命中后跳段。
    /// 与 PathExecutor._multiplayerRevivalDetected 完全独立（preservation §3.4）。
    /// 多线程可见性由 Interlocked.Exchange 保证。
    /// </summary>
    private int _remoteSkipRequested = 0;

    /// <summary>
    /// 缓存 RequestSkipToProgress 事件携带的 targetProgress；与 _remoteSkipRequested 配对读写。
    /// </summary>
    private long _remoteSkipTargetProgress = -1;

    // === 待处理等待点 ===
    private PendingWaitPoint? _pendingWaitPoint;

    // === 抢报已记录集合（fastsync-redesign-parameter-passing spec 修复）===
    /// <summary>
    /// 已经通过 FastReportAsync（fire-and-forget）上报过到达的 syncId 集合。
    /// 后续严格路径 WaitForAllPlayers 进入前查此集合：命中 → 直接返回（不阻塞）；
    /// 未命中 → 走原有"订阅 + 上报 + 等 AllArrived"流程。
    ///
    /// 服务端 RecordArrival 是 idempotent，但已经广播过 AllArrived 后 ArrivalSet 会清空，
    /// 严格路径再上报会陷入永久等待——本集合用于让抢报方自己跳过严格等待。
    /// </summary>
    private readonly System.Collections.Concurrent.ConcurrentDictionary<string, byte> _fastReportedSyncIds = new();

    // === 事件 ===
    public event Action<string>? OnDegraded;
    public event Action<bool>? OnConsecutiveSyncTimeoutExceeded;

    // === CancellationTokenSource（需求 2.1）===
    public CancellationTokenSource? StopCts { get; set; }

    public MultiplayerCoordinator(
        CoordinatorClient client,
        SyncPointResolver resolver,
        AutoHoeingConfig? config = null)
    {
        _client = client;
        // 优先使用调用方传入的 config（持有 AutoHoeingTask 拷贝/覆盖后的实例），
        // 否则 fallback 到全局（保持向后兼容）。
        // 配置组（ScriptGroup）启动时 AutoHoeingTask 会在深拷贝上 ApplySettingsOverride，
        // 此时全局未被覆盖，必须由调用方显式传入覆盖后的 _config，否则 KazuhaCollectSync 的
        // EnableKazuhaSync 永远是全局值（多半为 false）→ PathExecutor 跳过聚物分支。
        _config = config ?? TaskContext.Instance().Config.AutoHoeingConfig;
        _resolver = resolver;
        IsHost = client.IsHost;

        // 初始化子协调器
        WaitPointStateManager = new WaitPointStateManager();
        RouteSyncCoordinator = new RouteSyncCoordinator(_client, this, _config);
        StateManager = new AbnormalStatusManager(this, WaitPointStateManager, _config);
        KazuhaCollectSync = new KazuhaCollectSyncCoordinator(_client, _config, this);

        // 集体卡死跳段事件订阅（multiplayer-mutual-wait-collective-skip §8.6）
        _client.RequestSkipToProgressReceived += OnRequestSkipToProgressReceived;
        _client.CollectiveSkipDegradedReceived += OnCollectiveSkipDegradedReceived;

        // 基于经验判断停止锄地：全员达上限广播订阅（multiplayer-hoeing-exp-cap-stop）
        _client.AllReachedExpCap += OnAllReachedExpCap;

        _logger.LogInformation("[联机] 子协调器初始化完成");
    }

    // === 集体卡死跳段事件处理（multiplayer-mutual-wait-collective-skip §8.6）===

    private void OnRequestSkipToProgressReceived(long targetProgress)
    {
        _remoteSkipTargetProgress = targetProgress;
        RemoteSkipGate.TargetProgress = targetProgress;
        // Interlocked.Exchange 保证写入对所有线程立即可见
        Interlocked.Exchange(ref _remoteSkipRequested, 1);
        _logger.LogWarning("[联机] 大部队请求跳段，target={Target}，等待 4 处消费点命中", targetProgress);

        // 唤醒 SyncBarrier.WaitAsync 等待（消费点 4，design §8.7 备选 B 静态 Gate）
        RemoteSkipGate.Cancel();
    }

    private void OnCollectiveSkipDegradedReceived(string reason)
    {
        _logger.LogError("[联机] 集体跳段连续触发达上限，触发协调停止：{Reason}", reason);
        // 走 OnConsecutiveSyncTimeoutExceeded 等价路径（OQ-5 A）
        _ = TriggerCoordinatedStop(IsHost, $"CollectiveSkip:{reason}");
    }

    /// <summary>
    /// CAS 消费 _remoteSkipRequested 信号位（与 PathExecutor.TryConsumeRevivalSignal 同模式但**独立信号位**）。
    /// 命中：返回 true 并通过 out 返回 targetProgress；未命中：返回 false。
    /// 单机模式下调用方已用 <c>MultiplayerCoordinator != null</c> 守卫，本方法不重复短路。
    /// </summary>
    public bool TryConsumeRemoteSkipSignal(out long targetProgress)
    {
        var prev = Interlocked.Exchange(ref _remoteSkipRequested, 0);
        if (prev == 1)
        {
            targetProgress = _remoteSkipTargetProgress;
            _remoteSkipTargetProgress = -1;
            return true;
        }
        targetProgress = -1;
        return false;
    }

    /// <summary>
    /// 降级为单机模式
    /// </summary>
    public void Degrade(string reason)
    {
        _logger.LogWarning("[联机] 降级为单机模式，原因：{Reason}", reason);
        OnDegraded?.Invoke(reason);
    }

    /// <summary>
    /// 触发协调停止（需求 2.2）
    /// </summary>
    public async Task TriggerCoordinatedStop(bool isHost, string reason)
    {
        IsExitTriggered = true;
        _logger.LogWarning("[联机] 触发协调停止，原因：{Reason}", reason);
        
        if (isHost)
        {
            try { await _client.CloseRoomAsync(); } catch { }
        }

        // StopCts 与 AutoHoeingTask._linkedStopCts 是同一实例。任务收尾时
        // AutoHoeingTask 会先 Dispose 该 CTS，若此后（或并发）收到 AllReachedExpCap /
        // CollectiveSkipDegraded 等广播再走到这里，Cancel() 会抛 ObjectDisposedException，
        // 冒泡为未处理异常弹窗并阻塞后续任务。与 AutoHoeingTask 中所有 _linkedStopCts?.Cancel()
        // 调用点一致，吞掉已释放异常（幂等，停止信号已由 IsExitTriggered/其他路径覆盖）。
        try { StopCts?.Cancel(); }
        catch (ObjectDisposedException) { }
        catch (Exception ex) { _logger.LogWarning(ex, "[联机] 触发协调停止时 Cancel 异常（已忽略）"); }
    }

    // === 基于经验判断停止锄地（multiplayer-hoeing-exp-cap-stop）===

    /// <summary>本机是否启用经验上限停止（供 PathExecutor 门控读取）。配置开 ∧ 已连接。</summary>
    public bool IsExpCapStopEnabled =>
        ExpCapDecisions.IsEnabled(_config.EnableExpCapStop, _client.IsConnected);

    /// <summary>经验上限检测模式：true=检测所有经验，false=只检测精英经验（供 PathExecutor 选择检测器）。multiplayer-hoeing-exp-cap-stop</summary>
    public bool ExpCapDetectAllExp => _config.ExpCapDetectAllExp;

    /// <summary>
    /// 服务端广播"全员达经验上限"→ 触发协调停止（fire-and-forget，照搬集体跳段降级停止模式）。
    /// multiplayer-hoeing-exp-cap-stop R5.1。
    /// </summary>
    private void OnAllReachedExpCap()
    {
        _logger.LogWarning("[联机][经验上限] 收到全员达上限广播，触发协调停止");
        _ = TriggerCoordinatedStop(IsHost, "全员达经验上限");
    }

    /// <summary>
    /// PathExecutor 在一个"有效战斗节点"收尾后调用，传入本节点是否检测到经验。
    /// 更新连续无经验计数，按状态机决定上报/撤回/无动作（幂等，仅翻转时通知服务端）。
    /// 仅在 IsExpCapStopEnabled 时被调用（调用方门控）。multiplayer-hoeing-exp-cap-stop R3。
    /// </summary>
    public async Task OnFightNodeExpResultAsync(bool hasExp, CancellationToken ct)
    {
        _consecutiveNoExpCount = ExpCapDecisions.NextCount(_consecutiveNoExpCount, hasExp);
        var action = ExpCapDecisions.NextReportAction(_consecutiveNoExpCount, hasExp, _expCapReported);
        _logger.LogInformation("[联机][经验上限] 本节点{HasExp}经验，连续无经验={Count}/{Th}，动作={Action}",
            hasExp ? "有" : "无", _consecutiveNoExpCount, ExpCapDecisions.ConsecutiveNoExpThreshold, action);

        switch (action)
        {
            case ExpCapReportAction.Report:
                _expCapReported = true;
                await _client.NotifyExpCapReachedAsync(ct);
                break;
            case ExpCapReportAction.Clear:
                _expCapReported = false;
                _logger.LogInformation("[联机][经验上限] 又见经验，撤回上报");
                await _client.NotifyExpCapClearedAsync(ct);
                break;
            default:
                break;
        }

        // === 连续2场无经验预警信号（exp-cap-prefinal-stop-by-two-noexp）===
        // 与 4-threshold 独立并行：count 达 2 时上报预警，有经验时撤回。
        // 4-threshold 正式上报时自动取消预警（下一轮循环中 Clear 路径触发）。
        var twoConsecutiveAction = ExpCapDecisions.NextTwoConsecutiveReportAction(
            _consecutiveNoExpCount, hasExp, _twoConsecutiveReported);
        if (twoConsecutiveAction == ExpCapReportAction.TwoConsecutiveReport)
        {
            _twoConsecutiveReported = true;
            _logger.LogInformation("[联机][经验上限] 连续2场无经验预警，发送预警信号");
            await _client.NotifyTwoConsecutiveNoExpAsync(ct);
        }
        else if (twoConsecutiveAction == ExpCapReportAction.Clear && _twoConsecutiveReported)
        {
            _twoConsecutiveReported = false;
            _logger.LogInformation("[联机][经验上限] 又见经验，撤回2场预警信号");
            await _client.NotifyTwoConsecutiveNoExpClearedAsync(ct);
        }

        // 团队 arming（multiplayer-hoeing-exp-cap-stop R7）：吃到经验、或连续6场无经验兜底 → 发一次 arming（每轮幂等）。
        // 必须放在 switch（含 Clear 上报）之后：保证本机的 ExpCapCleared 先于 ExpArmed 送达服务端，
        // 从而 arming 到达时本机已从 ExpCapReachedSet 移除，不会触发误广播（design §14.3 竞态安全）。
        if (!_expArmedSent && (hasExp || ExpCapDecisions.ShouldForceArm(_consecutiveNoExpCount)))
        {
            _expArmedSent = true;
            _logger.LogInformation("[联机][经验上限] {Reason}，发送团队 arming 信号",
                hasExp ? "本机吃到经验" : $"连续{_consecutiveNoExpCount}场无经验兜底");
            await _client.NotifyExpArmedAsync(ct);
        }
    }

    /// <summary>
    /// 重置连续超时计数（需求 5）
    /// </summary>
    public void ResetSyncTimeoutCount()
    {
        _consecutiveSyncTimeoutCount = 0;
        _consecutiveSkipCount = 0;
    }

    /// <summary>
    /// 增加连续超时计数
    /// </summary>
    public void IncrementSyncTimeoutCount()
    {
        _consecutiveSyncTimeoutCount++;
        _logger.LogWarning("[联机] 连续超时次数: {Count}/{Max}", 
            _consecutiveSyncTimeoutCount, _config.MaxConsecutiveTimeouts);
        
        if (_consecutiveSyncTimeoutCount >= _config.MaxConsecutiveTimeouts)
        {
            OnConsecutiveSyncTimeoutExceeded?.Invoke(IsHost);
        }
    }

    /// <summary>
    /// 重置为新轮次
    /// </summary>
    public void ResetForNewRound()
    {
        _consecutiveSyncTimeoutCount = 0;
        _consecutiveSkipCount = 0;
        _skipNextSyncPoint = false;
        IsExitTriggered = false;
        IsAbortRequested = false;

        // 基于经验判断停止锄地：新轮次复位本机计数与上报态（multiplayer-hoeing-exp-cap-stop R6.1）
        _consecutiveNoExpCount = 0;
        _expCapReported = false;
        _twoConsecutiveReported = false;
        // 团队 arming 幂等门控每轮复位（multiplayer-hoeing-exp-cap-stop R7.6）
        _expArmedSent = false;

        // 集体卡死跳段信号位重置（multiplayer-mutual-wait-collective-skip §8.6 改动 4）
        Interlocked.Exchange(ref _remoteSkipRequested, 0);
        _remoteSkipTargetProgress = -1;
        RemoteSkipGate.Reset();

        RouteSyncCoordinator?.Reset();
        StateManager?.Reset();
        WaitPointStateManager?.ResetCurrentRound();

        // fastsync-claim-short-circuit-premature-release-fix（OQ-3=c→落地清理）：
        // syncId 编码不含世界轮次标识（PathExecutor.BuildSyncPointMap*），同名路线跨轮次复用产生相同 syncId。
        // 不清理则上一轮抢报记录会让本轮 IsFastReported 段内反查误判该点"已抢报"而跳过抢报。
        _fastReportedSyncIds.Clear();

        _logger.LogInformation("[联机] 已重置为新轮次");
    }

    // === 等待点相关 ===

    public bool HasPendingWaitPoint => _pendingWaitPoint != null && !_pendingWaitPoint.IsProcessed;

    public PendingWaitPoint? GetPendingWaitPoint() => _pendingWaitPoint;

    public void SetPendingWaitPoint(PendingWaitPoint point)
    {
        _pendingWaitPoint = point;
        _logger.LogInformation("[联机] 设置待处理等待点: {SyncId}, 强制={Forced}", point.SyncPointId, point.IsForced);
    }

    public void ClearPendingWaitPoint()
    {
        if (_pendingWaitPoint != null)
        {
            _logger.LogInformation("[联机] 清除待处理等待点: {SyncId}", _pendingWaitPoint.SyncPointId);
            _pendingWaitPoint.IsProcessed = true;
            _pendingWaitPoint = null;
        }
    }

    /// <summary>
    /// 检查指定同步点是否为异常等待点
    /// </summary>
    public bool IsAbnormalWaitingAtPoint(string syncPointId)
    {
        // 简化实现：如果有 _pendingWaitPoint 且匹配则返回 IsForced
        return _pendingWaitPoint != null && !_pendingWaitPoint.IsProcessed && _pendingWaitPoint.SyncPointId == syncPointId && _pendingWaitPoint.IsForced;
    }

    public void SetSkipNextSyncPoint()
    {
        _skipNextSyncPoint = true;
        _consecutiveSkipCount++;
        _logger.LogInformation("[联机] 设置跳过下一个同步点，连续跳过次数: {Count}", _consecutiveSkipCount);
    }

    public bool ShouldSkipNextSyncPoint()
    {
        if (_skipNextSyncPoint)
        {
            _skipNextSyncPoint = false;
            return true;
        }
        return false;
    }

    public async Task NotifyRouteSkippedAsync(int routeIndex)
    {
        try
        {
            await _client.ReportRouteSkippedAsync(routeIndex);
            _logger.LogInformation("[联机] 上报路线跳过: {Index}", routeIndex);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 上报路线跳过失败");
        }
    }

    public async Task<bool> ReportWaitPointAsync(string routeId, string syncPointId, int worldRound)
    {
        try
        {
            await _client.ReportWaitPointAsync(syncPointId);
            WaitPointStateManager?.UpdateState(_client.PlayerUid ?? "", new WaitPointState
            {
                PlayerUid = _client.PlayerUid ?? "",
                RouteId = routeId,
                SyncPointId = syncPointId,
                WorldRound = worldRound,
                LastUpdated = DateTime.Now
            });
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 上报等待点失败");
            return false;
        }
    }

    // === 中断重对齐相关 ===

    public int GetAbortTargetRouteIndex()
    {
        return _client.CurrentRouteIndex + 1;
    }

    public bool IsRouteEnforceSyncRequested => false; // 简化实现

    public int GetEnforceTargetRouteIndex()
    {
        return _client.CurrentRouteIndex;
    }

    // === 等待所有玩家 ===

    /// <summary>
    /// 严格等待所有玩家到达指定同步点（multiplayer-hoeing-post-teleport-revival-protection）。
    /// 返回 <see langword="true"/> 仅表示与当前 <paramref name="syncId"/> 匹配的 AllArrived
    /// 已被 <see cref="CoordinatorClient.WaitForAllPlayersAsync"/> 严格等待消费（严格等待完成）；
    /// 返回 <see langword="false"/> 表示未确认完成（取消、超时、房间关闭、断线、未连接或其它异常）。
    /// 调用方只能据此决定是否建立传送后保护窗口，不得把"吞异常后的正常返回"误认成确认完成。
    /// </summary>
    public async Task<bool> WaitForAllPlayers(string syncId, CancellationToken ct, long syncProgress = -1)
    {
        // fastsync-claim-short-circuit-premature-release-fix（OQ-1=a）：
        // 删除「自己已抢报过即短路放行」分支。抢报方真正到达同步点后，照常走严格
        // subscribe-before-action 路径等全员到齐。抢报「让别人早走」能力由 FastReportAsync 保留。
        // 组合 7「服务端已广播 + 已清空 ArrivalSet」竞态由服务端对已放行 syncId 的幂等补发 AllArrived 解决
        // （见 CoordinatorHub.WaitForAllPlayers 补发分支），不再依赖客户端短路规避死等。
        // wasFastReported 仅用于日志文案区分（OQ-4=a / 方案 B），不参与放行决策。
        bool wasFastReported = _fastReportedSyncIds.ContainsKey(syncId);
        try
        {
            // CoordinatorClient 正常返回即表示匹配 AllArrived 已被消费（未连接/未匹配时返回 false），
            // 故此处正常返回可安全映射为 true。取消/超时/关房/断线异常由 catch 统一映射为 false。
            return await _client.WaitForAllPlayersAsync(syncId, ct, syncProgress, wasFastReported);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 等待所有玩家失败: {SyncId}", syncId);
            return false;
        }
    }

    /// <summary>
    /// 抢报专用 fire-and-forget：仅向服务端记录到达，不阻塞调用方
    /// （fastsync-redesign-parameter-passing spec）。
    /// 命中后将 syncId 加入 _fastReportedSyncIds，让后续严格 WaitForAllPlayers 路径
    /// short-circuit 直接返回，避免服务端 ArrivalSet 清空后再订阅事件死等。
    /// </summary>
    public async Task FastReportAsync(string syncId, long syncProgress = -1)
    {
        try
        {
            await _client.FireAndForgetArrivalAsync(syncId, syncProgress);
            _fastReportedSyncIds.TryAdd(syncId, 0);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 抢报失败（已忽略）: {SyncId}", syncId);
        }
    }

    /// <summary>
    /// 查询某 syncId 是否已经通过 FastReportAsync 抢报过。
    /// 调用方（PathExecutor）用此判断在段内寻找"下一个还没抢报的 syncPoint"作为提前距离判定目标。
    /// </summary>
    public bool IsFastReported(string syncId) => _fastReportedSyncIds.ContainsKey(syncId);

    /// <summary>
    /// 段级落后追赶判定（hoeing-multiplayer-lagging-member-catchup spec / 关键问题 1）。
    /// PathExecutor 在段起点传送点正常同步块（WaitForAllPlayers 之前）调用：传入本地实时 mySegProgress，
    /// 本方法读客户端缓存 CurrentPlayerList 归约大部队段级进度（房主优先 / 在线最大），调纯函数判定。
    /// 纯同步读内存、无 await、无网络往返。守卫：开关关闭 / 单机 / 进度不可得 → false。
    /// 房主也参与（D2，hoeing-lagging-catchup-host-synced-setting spec）：房主以在线成员最靠前进度为追赶基准。
    /// </summary>
    public bool TryGetLaggingCatchUpDecision(long mySegProgress)
    {
        if (!EffectiveConfig.MultiplayerEnabled) return false;
        if (!EffectiveConfig.EnableLaggingCatchUp) return false;
        // D2 行为变更（hoeing-lagging-catchup-host-synced-setting spec）：移除 !_client.IsHost 守卫，房主也参与落后追赶判定。
        // 房主自调用时 myUid 跳过自己 → hostSeg=null → ResolveSquadSegmentProgress fallback 到 peerSegs（在线成员）最大值；
        // 房间仅房主一人时 peerSegs 空 → squadSeg=Unavailable(-1) → ShouldCatchUp 返回 false（房主独自不追）。

        var myUid = _client.MyPlayerUid;
        long? hostSeg = null;
        var peerSegs = new System.Collections.Generic.List<long>();
        foreach (var player in _client.CurrentPlayerList)
        {
            if (player.PlayerUid == myUid) continue;
            long seg = player.CurrentProgress;
            if (player.IsHost) hostSeg = seg;
            else peerSegs.Add(seg);
        }
        long squadSeg = LaggingCatchUpDecisions.ResolveSquadSegmentProgress(hostSeg, peerSegs);

        bool shouldCatchUp = LaggingCatchUpDecisions.ShouldCatchUp(
            isMember: true, enabled: true,
            mySegmentProgress: mySegProgress, squadSegmentProgress: squadSeg,
            lagSegmentThreshold: EffectiveConfig.LagSegmentThreshold);

        if (shouldCatchUp)
        {
            _logger.LogWarning("[落后追赶] 段同步点：我 {My} 落后大部队 {Squad}（阈值 {T} 段），跳下一段",
                mySegProgress, squadSeg, EffectiveConfig.LagSegmentThreshold);
        }
        return shouldCatchUp;
    }

    // === 上报状态 ===

    public async Task ReportFightingStatusAsync(bool isFighting)
    {
        try
        {
            await _client.ReportFightingStatusAsync(isFighting);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 上报战斗状态失败");
        }
    }

    /// <summary>上报战斗参与者（multiplayer-shared-fight-end-quorum-sync spec）。</summary>
    public async Task ReportFightParticipantAsync(string syncKey)
    {
        try
        {
            await _client.NotifyFightParticipantAsync(syncKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][结束配额] 上报战斗参与者失败 syncKey={Key}", syncKey);
        }
    }

    /// <summary>上报本地战斗完成投票（multiplayer-shared-fight-end-quorum-sync spec）。</summary>
    public async Task ReportFightDoneAsync(string syncKey)
    {
        try
        {
            await _client.NotifyFightDoneAsync(syncKey);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机][结束配额] 上报战斗完成投票失败 syncKey={Key}", syncKey);
        }
    }

    public async Task ReportMemberStatusAsync(MemberStatus status, long targetProgress = -1)
    {
        try
        {
            await _client.ReportMemberStatusAsync(status, targetProgress);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "[联机] 上报成员状态失败");
        }
    }

    // === 路线验证同步等待 ===

    /// <summary>
    /// 等待路线验证完成
    /// </summary>
    public async Task WaitForRouteVerificationAsync(CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);

        Action? onPassed = () => tcs.TrySetResult(true);
        Action? onTimeout = () =>
        {
            _logger.LogWarning("[联机] 等待路线验证超时（90秒），继续执行");
            tcs.TrySetResult(false);
        };

        _client.RouteVerificationPassed += onPassed;

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(onTimeout);

            await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            _logger.LogWarning("[联机] 等待路线验证被取消");
        }
        finally
        {
            _client.RouteVerificationPassed -= onPassed;
        }
    }

    // === 开始路线指令等待 ===

    /// <summary>
    /// 等待服务器广播的开始路线指令
    /// </summary>
    public async Task<int> WaitForStartRouteAsync(int timeoutSeconds, CancellationToken ct)
    {
        var tcs = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);

        Action<int>? onStartRoute = routeIndex => tcs.TrySetResult(routeIndex);
        Action? onTimeout = () =>
        {
            _logger.LogWarning("[联机] 等待开始路线指令超时（{Timeout}s）", timeoutSeconds);
            tcs.TrySetResult(-1);
        };

        _client.StartRouteReceived += onStartRoute;

        try
        {
            using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(timeoutSeconds));
            using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            using var reg = linkedCts.Token.Register(onTimeout);

            return await tcs.Task;
        }
        catch (OperationCanceledException)
        {
            return -1;
        }
        finally
        {
            _client.StartRouteReceived -= onStartRoute;
        }
    }

    // === 路线变体一致性校验（route-variant-sync-by-logical-id spec / R6 / R8）===

    /// <summary>
    /// 客户端发起 R6 启动期校验（route-variant-sync-by-logical-id spec / R6 / R8.5-7）。
    /// 返回值：
    ///   - true：校验通过 OR 服务端不识别该方法且 items 全空（老路径）
    ///   - false：校验失败 OR 30s 超时 OR ct 取消
    /// 抛异常（caller 应终止本场会话）：
    ///   - InvalidOperationException：服务端不识别新协议但 items 中至少一条非空 LogicalRouteId（R8.7）
    /// </summary>
    public async Task<bool> VerifyRouteVariantSchemaAsync(
        List<RouteVariantSchemaItem> items, CancellationToken ct)
    {
        items ??= new List<RouteVariantSchemaItem>();
        bool selfHasAnyLogicalRouteId = items.Any(i => !string.IsNullOrEmpty(i.LogicalRouteId));

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action? onPassed = null;
        Action<string, Dictionary<string, RouteVariantSchemaItem>>? onFailed = null;
        onPassed = () => tcs.TrySetResult(true);
        onFailed = (logicalId, playerItems) =>
        {
            if (string.IsNullOrEmpty(logicalId))
            {
                _logger.LogWarning("[变体校验] 服务端 30s 超时（视为失败）");
            }
            else
            {
                _logger.LogWarning("[变体校验] LogicalRouteId={LRI} 不一致；玩家文件: {Files}",
                    logicalId, string.Join(", ",
                        playerItems.Select(kv => $"{kv.Key}={kv.Value.ActualVariantFileName}")));
            }
            tcs.TrySetResult(false);
        };
        // subscribe-before-action：先订阅事件再上报，避免服务端在订阅前就广播完成（bgi-config-and-mvvm §5.1）
        _client.RouteVariantConsistencyPassed += onPassed;
        _client.RouteVariantConsistencyFailed += onFailed;
        try
        {
            try
            {
                await _client.ReportRouteVariantSchemaAsync(items, ct);
            }
            catch (HubException ex) when (IsMethodNotFoundException(ex))
            {
                if (selfHasAnyLogicalRouteId)
                {
                    throw new InvalidOperationException(
                        "服务端版本不支持变体功能（ReportRouteVariantSchema 方法不存在），请升级 BgiCoordinatorServer", ex);
                }
                _logger.LogInformation("[变体校验] 服务端不支持新协议且本玩家全员老路径，按老路径执行");
                return true;
            }

            using var localTimeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, localTimeout.Token);
            using var reg = linked.Token.Register(() =>
            {
                if (!selfHasAnyLogicalRouteId)
                {
                    // 全员老线路防御：旧版服务端在"全空"时静默不广播 Passed，
                    // 新客户端会一直等到此超时。本玩家没有任何变体路线 → 没有 schema 可校验，
                    // 超时按"放行"处理（老线路一致性已由 MD5 校验覆盖），避免误判失败卡住联机。
                    // 已升级的服务端会在全空时主动广播 Passed，正常不会走到这里。
                    _logger.LogInformation("[变体校验] 等待超时且本玩家全员老线路，按放行处理（兼容未升级服务端）");
                    tcs.TrySetResult(true);
                }
                else
                {
                    _logger.LogWarning("[变体校验] 客户端等待事件超时 30s，视为失败");
                    tcs.TrySetResult(false);
                }
            });
            return await tcs.Task;
        }
        finally
        {
            _client.RouteVariantConsistencyPassed -= onPassed;
            _client.RouteVariantConsistencyFailed -= onFailed;
        }
    }

    /// <summary>
    /// 识别 HubException 是否为"服务端方法不存在"。
    /// SignalR 调到不存在的方法时抛 HubException，message 形如：
    ///   "Method 'ReportRouteVariantSchema' does not exist"
    ///   "Failed to invoke 'ReportRouteVariantSchema' due to an error on the server. ..."
    /// </summary>
    private static bool IsMethodNotFoundException(Exception ex)
    {
        if (ex == null) return false;
        var msg = ex.Message ?? string.Empty;
        return msg.IndexOf("does not exist", StringComparison.OrdinalIgnoreCase) >= 0
            || (msg.IndexOf("ReportRouteVariantSchema", StringComparison.OrdinalIgnoreCase) >= 0
                && msg.IndexOf("error on the server", StringComparison.OrdinalIgnoreCase) >= 0);
    }

    // === 中断状态清除 ===

    /// <summary>
    /// 清除中断请求状态
    /// </summary>
    public void ClearAbortState()
    {
        IsAbortRequested = false;
        _logger.LogDebug("[联机] 中断状态已清除");
    }

    // === 强制同步状态清除 ===

    /// <summary>
    /// 清除强制线路同步状态
    /// </summary>
    public void ClearRouteEnforceSync()
    {
        // 简化实现，无强制同步状态需要清除
        _logger.LogDebug("[联机] 强制线路同步状态已清除");
    }

    public async ValueTask DisposeAsync()
    {
        // 集体卡死跳段事件取消订阅 + Gate 重置（multiplayer-mutual-wait-collective-skip §8.6 改动 5）
        try
        {
            _client.RequestSkipToProgressReceived -= OnRequestSkipToProgressReceived;
            _client.CollectiveSkipDegradedReceived -= OnCollectiveSkipDegradedReceived;
            // 基于经验判断停止锄地：退订全员达上限广播（multiplayer-hoeing-exp-cap-stop）
            _client.AllReachedExpCap -= OnAllReachedExpCap;
        }
        catch (System.Exception ex)
        {
            _logger.LogWarning(ex, "[联机] DisposeAsync 解绑 RequestSkipToProgress 事件失败（可忽略）");
        }
        RemoteSkipGate.Reset();

        WaitPointStateManager?.Dispose();
        KazuhaCollectSync?.Dispose();
        RouteSyncCoordinator = null;
        StateManager = null;
        WaitPointStateManager = null;
        KazuhaCollectSync = null;
    }
}
