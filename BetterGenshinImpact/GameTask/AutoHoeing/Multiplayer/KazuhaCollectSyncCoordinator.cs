#nullable enable

using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Map;
using Microsoft.Extensions.Logging;
using OpenCvSharp;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 战后回点 → 万叶聚物 → 一起放行 的同步协调器。
/// 由 PathExecutor 在战后回点 Delay 处调用 <see cref="WaitAtFightPointAsync"/>。
///
/// 设计要点（详见 design.md "Components and Interfaces §1"）：
/// - 万叶玩家分支：等所有 Peer 到齐 → 广播 Started → 执行 KazuhaCollectExecutor → 按 Outcome 广播 Finished/Skipped。
/// - 普通玩家分支：subscribe-before-action 订阅 Finished/Skipped → 上报 AtFightPoint → 等待终态事件
///   → 任意终态后统一停 KazuhaSyncWaitSeconds 秒后离开。
/// - 启用门控：<see cref="KazuhaCollectSyncDecisions.IsEnabled"/> 任一项不满足直接 fallback 为原 Delay。
/// - 万叶身份（kazuha-player-auto-detection）：本地缓存 <c>_kazuhaPlayerUid</c>，由服务端广播 KazuhaPlayerUpdated(playerUid) 维护；
///   <c>IsCurrentPlayerKazuha</c> 直接对比 <c>_kazuhaPlayerUid == _client.PlayerUid</c>。替换原 ResolveKazuhaUid + PlayerList 索引方案。
/// - 取消：OperationCanceledException 透传，不广播 Finished/Skipped（requirements 5.6）。
/// </summary>
public sealed class KazuhaCollectSyncCoordinator : IDisposable
{
    private readonly CoordinatorClient _client;
    private readonly AutoHoeingConfig _config;
    private readonly MultiplayerCoordinator _parent;
    private readonly ILogger<KazuhaCollectSyncCoordinator> _logger = App.GetLogger<KazuhaCollectSyncCoordinator>();

    // kazuha-player-auto-detection: 本地缓存最新广播的 KazuhaPlayerUid。
    // 由 _client.KazuhaPlayerUpdated 事件维护；getter 通过 _kazuhaPlayerUid == _client.PlayerUid 判定本玩家是否为 Kazuha。
    private string? _kazuhaPlayerUid;
    private readonly Action<string> _onKazuhaPlayerUpdated;

    // multiplayer-kazuha-collect-point-broadcast: 缓存最近一次本类收到的 KazuhaCollectStarted (syncKey, x, y)。
    // 由构造函数订阅 _client.KazuhaCollectStarted 事件维护；事件回调内仅当 IsValid(x, y) 时更新缓存。
    // TryGetCollectPointForCurrent(syncKey) 在 syncKey 匹配时返回 true，给 PathExecutor 战后非万叶分支
    // 调用以触发二段 MoveCloseTo 精接近聚物点。
    private (string SyncKey, double X, double Y)? _lastCollectPoint;
    // kazuha-collect-min-buffer-before-stay: 记录"收到本周期有效聚物点坐标的时刻"（UTC）。
    // 与 _lastCollectPoint 同生命周期（跨周期保留）；WaitAsNonKazuhaAsync 用它算"距收到坐标已过多久"，
    // 据此补足到 MinBufferBeforeStayMs(1500ms) 再进 KazuhaSyncWaitSeconds 停留。
    // 走向 A/B 判定仍以 syncKey 匹配为准，不单看本字段是否有值（避免误用上一周期旧时间戳）。
    private DateTime _lastCollectPointTimeUtc;
    private readonly Action<string, string, double, double> _onKazuhaCollectStartedCache;

    // fix-kazuha-collect-layer1-dead-code: PathExecutor 在清空 PathingConditionConfig.CombatScenesGoBackUp
    // 之前捕获的战斗快照副本（CaptureCombatSnapshot 写入，每场战斗结束覆盖一次）。
    // 背景缺陷：三层取队第 1 层原设计直读 CombatScenesGoBackUp，但 PathExecutor 战后清空（:1338）
    // 发生在聚物分支（:1362）之前 → 第 1 层永不命中，兜底路径每次都要现场强制重识别（干站数秒）。
    // 改为优先读此副本；共享字段的清空时机不变，AutoFightTask:584 / PathExecutor:1296 等既有消费方零影响。
    private CombatScenes? _capturedCombatSnapshot;

    public KazuhaCollectSyncCoordinator(
        CoordinatorClient client,
        AutoHoeingConfig config,
        MultiplayerCoordinator parent)
    {
        _client = client;
        _config = config;
        _parent = parent;
        CurrentState = KazuhaCollectState.Idle;

        // 订阅 KazuhaPlayerUpdated 事件维护 _kazuhaPlayerUid
        _onKazuhaPlayerUpdated = playerUid =>
        {
            _kazuhaPlayerUid = string.IsNullOrEmpty(playerUid) ? null : playerUid;
        };
        _client.KazuhaPlayerUpdated += _onKazuhaPlayerUpdated;

        // multiplayer-kazuha-collect-point-broadcast: 订阅 KazuhaCollectStarted 维护 _lastCollectPoint。
        // IsValid 守卫过滤 NaN / Inf / (0, 0)（与服务端 Hub IsValid 同语义、与 PBT-4 真值表对齐）。
        _onKazuhaCollectStartedCache = (playerUid, syncKey, collectX, collectY) =>
        {
            if (string.IsNullOrEmpty(syncKey)) return;
            if (!KazuhaCollectPointDecisions.IsValid(collectX, collectY)) return;
            _lastCollectPoint = (syncKey, collectX, collectY);
            // kazuha-collect-min-buffer-before-stay: 与坐标缓存同步记下收到时刻，供停留前补足缓冲计时。
            _lastCollectPointTimeUtc = DateTime.UtcNow;
            _logger.LogDebug("[联机][聚物] 缓存聚物点 syncKey={Key} ({X:F1},{Y:F1})", syncKey, collectX, collectY);
        };
        _client.KazuhaCollectStarted += _onKazuhaCollectStartedCache;
    }

    /// <summary>
    /// 当前玩家是否被指定为万叶玩家。
    /// kazuha-player-auto-detection: 改为读取本地缓存的 <c>_kazuhaPlayerUid</c>（由服务端广播 KazuhaPlayerUpdated 维护），
    /// 替代原 <c>ResolveKazuhaUid + PlayerList</c> 索引方案。
    /// 满足 Property 4 (Uniqueness Across Clients)：所有客户端依据同一服务端广播判定，最多一个客户端 IsCurrentPlayerKazuha == true。
    /// </summary>
    public bool IsCurrentPlayerKazuha
    {
        get
        {
            var uid = _kazuhaPlayerUid;
            if (string.IsNullOrEmpty(uid)) return false;
            return uid == _client.PlayerUid;
        }
    }

    /// <summary>本周期是否启用同步流程（综合 EnableKazuhaSync / 连接状态）。</summary>
    public bool IsEnabled => KazuhaCollectSyncDecisions.IsEnabled(_config, _client.IsConnected);

    /// <summary>
    /// 用户是否在配置中启用了万叶聚物（不感知连接状态）。
    /// 由 PathExecutor 在战后回点判定是否走"回战斗点 → 进入 WaitAtFightPointAsync"分支；
    /// IsConnected==false 时仍进 WaitAtFightPointAsync，由其内部走兜底 Delay。
    /// 必须读此属性（持有 AutoHoeingTask 拷贝/覆盖后的 _config），
    /// 不能读 TaskContext.Instance().Config.AutoHoeingConfig（配置组覆盖未应用到全局）。
    /// </summary>
    public bool IsConfigEnabled => KazuhaCollectSyncDecisions.IsConfigEnabledForPathExecutor(_config);

    /// <summary>
    /// 查询本周期是否已收到有效的聚物点广播。
    /// 当且仅当 _lastCollectPoint.SyncKey == 当前 syncKey 时返回 true，并通过 out 参返回 (x, y)。
    /// 由 PathExecutor 战后非万叶玩家分支在第一段 MoveCloseTo(战斗点) 完成后调用：
    /// 返回 true 时构造临时 WaypointForTrack 做二段 MoveCloseTo(closeDistance:0.5, tailDelayMs:0, maxSteps:5)；
    /// 返回 false 时跳过二段，进 WaitAtFightPointAsync 走原路径。
    /// 内部调 KazuhaCollectPointDecisions.TryMatch 复用 PBT-5 覆盖的纯函数。
    /// </summary>
    public bool TryGetCollectPointForCurrent(string syncKey, out double x, out double y)
    {
        return KazuhaCollectPointDecisions.TryMatch(_lastCollectPoint, syncKey, out x, out y);
    }

    /// <summary>
    /// 阻塞等待本周期聚物点广播到达，最多等 <paramref name="timeoutMs"/> 毫秒。
    /// 已缓存命中（构造函数级订阅已记录）→ 立即返回 true（快速路径）。
    /// 等待期间若万叶上报 → TCS 触发，返回 true。
    /// 超时未到 → 返回 false。
    /// 取消透传 OperationCanceledException（用户取消主任务）。
    ///
    /// 由 PathExecutor 非万叶玩家分支调用：在第一段 MoveCloseTo 之前 kick off（不 await），
    /// 第一段完成后 await 拿结果，让"等广播"与"第一段精接近"时间重叠。
    /// 用 subscribe-before-action 时序（先订阅再检查缓存）避免订阅前事件已到达造成的丢失窗口。
    /// </summary>
    public async Task<bool> TryWaitForCollectPointAsync(string syncKey, int timeoutMs, CancellationToken ct)
    {
        if (string.IsNullOrEmpty(syncKey)) return false;

        // 入口快速路径：构造函数级订阅已记录命中 → 立即返回
        if (TryGetCollectPointForCurrent(syncKey, out _, out _)) return true;

        var tcs = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        Action<string, string, double, double> handler = (uid, evSyncKey, cx, cy) =>
        {
            if (string.IsNullOrEmpty(evSyncKey) || evSyncKey != syncKey) return;
            if (!KazuhaCollectPointDecisions.IsValid(cx, cy)) return;
            tcs.TrySetResult(true);
        };
        _client.KazuhaCollectStarted += handler;
        try
        {
            // subscribe-before-action：订阅后再次检查缓存（订阅与首次检查之间可能错过的事件由此兜底）
            if (TryGetCollectPointForCurrent(syncKey, out _, out _)) return true;

            using var timeoutCts = new CancellationTokenSource(Math.Max(0, timeoutMs));
            using var linked = CancellationTokenSource.CreateLinkedTokenSource(ct, timeoutCts.Token);
            try
            {
                await tcs.Task.WaitAsync(linked.Token);
                return true;
            }
            catch (OperationCanceledException) when (timeoutCts.IsCancellationRequested && !ct.IsCancellationRequested)
            {
                _logger.LogDebug("[联机][聚物] 等聚物点广播超时 {Ms}ms，syncKey={Key}", timeoutMs, syncKey);
                return false;
            }
        }
        finally
        {
            _client.KazuhaCollectStarted -= handler;
        }
    }

    /// <summary>当前周期状态（用于日志/PBT 模型）。</summary>
    public KazuhaCollectState CurrentState { get; private set; }

    /// <summary>
    /// BeginPreparationAsync 的并行预备结果。
    /// SwitchedSuccessfully == true 时 CombatScenes / Kazuha 必非 null，可走 KazuhaCollectExecutor 快速路径；
    /// 否则走原"RunAsKazuhaAsync 内串行 GetCombatScenes + RunAsync 自切人"兜底。
    /// </summary>
    public sealed record PreparationResult(
        CombatScenes? CombatScenes,
        Avatar? Kazuha,
        bool SwitchedSuccessfully)
    {
        public static PreparationResult Skipped { get; } = new(null, null, false);
    }

    /// <summary>
    /// 战后回点 → MoveCloseTo 之前由 PathExecutor 后台 kick-off 的预备任务。
    /// 与 MoveCloseTo（走回战斗点）并行执行：缓存命中时调用 GetCombatScenes → SelectAvatar → Delay(200) → TrySwitch。
    /// 任何"不应预备"的场景（未启用 / 非万叶玩家 / 缓存未命中 / 切人失败 / 异常）一律返回 PreparationResult.Skipped，
    /// 让主流程 RunAsKazuhaAsync 走原兜底路径，绝不抛异常给 PathExecutor（OperationCanceledException 透传除外）。
    /// 关键约束：缓存未命中时不调用 GetCombatScenes，避免走 ReturnMainUiTask 分支按 ESC 中断 MoveCloseTo 走位。
    /// 详见 design.md §3.3。
    /// </summary>
    public Task<PreparationResult> BeginPreparationAsync(CancellationToken ct)
    {
        return Task.Run(async () =>
        {
            try
            {
                if (!KazuhaCollectSyncDecisions.ShouldRunBackgroundPreparation(
                        IsEnabled, IsCurrentPlayerKazuha, RunnerContext.Instance.HasCombatScenesCached))
                {
                    return PreparationResult.Skipped;
                }

                var combatScenes = await RunnerContext.Instance.GetCombatScenes(ct);
                if (combatScenes == null) return PreparationResult.Skipped;

                var kazuha = combatScenes.SelectAvatar("枫原万叶");
                if (kazuha == null) return PreparationResult.Skipped;

                await Task.Delay(200, ct);

                if (!kazuha.TrySwitch(10))
                {
                    _logger.LogDebug("[联机][聚物] BeginPreparationAsync 预切人失败，主流程兜底");
                    return PreparationResult.Skipped;
                }

                _logger.LogDebug("[联机][聚物] BeginPreparationAsync 完成预切人，已就绪");
                return new PreparationResult(combatScenes, kazuha, true);
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[联机][聚物] BeginPreparationAsync 失败，主流程兜底");
                return PreparationResult.Skipped;
            }
        }, ct);
    }

    /// <summary>
    /// 战后到达战斗点后的同步等待入口。返回时已离开战斗点的"等待"阶段。
    /// 未启用门控时（IsConnected==false）直接 fallback 到 <c>Delay(KazuhaSyncWaitSeconds*1000)</c> 行为。
    /// <paramref name="prepTask"/> 为 PathExecutor 在 MoveCloseTo 之前 kick off 的 BeginPreparationAsync 任务，
    /// null 时（PathExecutor 未传 / 兜底）走原"RunAsKazuhaAsync 内串行 GetCombatScenes + RunAsync 自切人"路径。
    /// multiplayer-kazuha-collect-point-broadcast: <paramref name="fightPointWaypoint"/> 类型从 Waypoint
    /// 收紧为 WaypointForTrack，让万叶玩家 onBeforeHoldE hook 闭包能直接拿到 MapName/MapMatchMethod
    /// 调用 Navigation.GetPosition；类型协变兼容（PathExecutor 传入的就是 WaypointForTrack 实例）。
    /// </summary>
    public async Task WaitAtFightPointAsync(WaypointForTrack fightPointWaypoint, Task<PreparationResult>? prepTask, CancellationToken ct)
    {
        ResetForNextCycle();
        CurrentState = KazuhaCollectState.AtFightPoint;

        // 启用门控：!IsEnabled（即 IsConnected==false，因为 EnableKazuhaSync==false 时 PathExecutor 已经不调用本方法）
        // → 用 KazuhaSyncWaitSeconds 作为统一兜底等待（Open Q5: 保留兜底 Delay，时长由 KazuhaSyncWaitSeconds 接管）
        if (!IsEnabled)
        {
            _logger.LogDebug("[联机][聚物] 未启用同步流程（IsConnected==false），按 KazuhaSyncWaitSeconds 兜底等待 {Sec}s",
                _config.KazuhaSyncWaitSeconds);
            try
            {
                await Task.Delay(_config.KazuhaSyncWaitSeconds * 1000, ct);
            }
            finally
            {
                CurrentState = KazuhaCollectState.Skipped;
            }
            return;
        }

        // syncKey: 用当前路线索引 + waypoint X/Y 唯一标识本段战斗点（房主与成员都本地构造，相同输入产生相同 key）。
        // 注意：必须用 X/Y 等内容字段，不能用 fightPointWaypoint.GetHashCode()——object.GetHashCode 默认基于
        // 对象引用，每个客户端从 JSON 反序列化的 Waypoint 实例引用不同，hashcode 必然不一致 → syncKey 跨客户端不一致。
        // X/Y 是 double 类型，不同客户端反序列化结果应完全相同（IEEE754 二进制位字符串化）。
        // 用 InvariantCulture 保证不同 locale 下浮点格式一致（如 "1.5" vs "1,5"）。
        // kazuha-collect-synckey-rerun-isolation：轮末重跑期间追加 RerunSyncIdSuffix（如 "_rerun_r3"），
        // 防止命中上一轮残留的 _lastCollectPoint 缓存导致跳过缓冲早退；后缀由 PathExecutor 同源同步。
        var syncKey = fightPointWaypoint == null
            ? KazuhaSyncKeyBuilder.BuildSyncKey(_client.CurrentRouteIndex, 0, 0, RerunSyncIdSuffix)
            : KazuhaSyncKeyBuilder.BuildSyncKey(_client.CurrentRouteIndex, fightPointWaypoint.X, fightPointWaypoint.Y, RerunSyncIdSuffix);

        // kazuha-player-auto-detection: 删除原"房主侧 kazuha_offline 预检"块。
        // 新机制下房主无法预知谁是 Kazuha（要等客户端声明）；若全员都没声明则 _kazuhaPlayerUid == null
        // 自然走 fallback：IsCurrentPlayerKazuha 返回 false，进入 WaitAsNonKazuhaAsync 等待终态超时后离开。

        try
        {
            if (IsCurrentPlayerKazuha)
            {
                await RunAsKazuhaAsync(fightPointWaypoint, syncKey, prepTask, ct);
            }
            else
            {
                // 非万叶玩家分支不消费 prepTask；BeginPreparationAsync 内部第一行 gate 已对
                // !IsCurrentPlayerKazuha 立即返回 PreparationResult.Skipped，无副作用。
                await WaitAsNonKazuhaAsync(syncKey, ct);
            }
        }
        catch (OperationCanceledException)
        {
            // 取消透传，不广播 Finished/Skipped（requirements 5.6）
            throw;
        }
    }

    /// <summary>
    /// 万叶玩家分支：到点立刻执行聚物（不等其他成员）。
    /// 设计变更：
    /// - 删除"先等所有 Peer 到齐"等待，万叶到达战斗点后立即放 E，缩短整组锄地时长。
    /// - hoeing-kazuha-collect-drop-terminal-signal: 砍终态信号闭环——本分支不再广播
    ///   Finished/Skipped、不再上报 NotifyKazuhaArrivedAtFightPoint；仅在 onBeforeHoldE 内
    ///   fire-and-forget 广播 KazuhaCollectStarted（坐标）。成员只认坐标广播，离开时机由
    ///   "二段完成 + 固定停留 KazuhaSyncWaitSeconds"决定。
    /// - BC3+BC4: 消费 prepTask（已与 MoveCloseTo 并行执行）：成功时走 KazuhaCollectExecutor 快速路径
    ///   (assumeAlreadySwitched=true + preselectedKazuha)；任意失败/兜底回退原"串行 GetCombatScenes + 自切人"路径。
    /// </summary>
    private async Task RunAsKazuhaAsync(WaypointForTrack fightPointWaypoint, string syncKey, Task<PreparationResult>? prepTask, CancellationToken ct)
    {
        var maskedUid = AutoPartyTask.MaskUid(_client.PlayerUid);
        _logger.LogInformation("[联机][聚物] 万叶玩家 {Uid} 到达战斗点，立即执行聚物 (syncKey={Key})", maskedUid, syncKey);

        CurrentState = KazuhaCollectState.Collecting;
        // multiplayer-kazuha-collect-point-broadcast: 删除原"在此处广播 Started 0-参"调用，
        // 改在 KazuhaCollectExecutor.RunAsync 的 onBeforeHoldE hook 闭包内调 4-参版本（含聚物点坐标）。

        // BC3+BC4: 消费 prepTask。OperationCanceledException 透传；其它异常 LogWarning + Skipped 兜底。
        PreparationResult prep;
        if (prepTask != null)
        {
            try
            {
                prep = await prepTask;
            }
            catch (OperationCanceledException)
            {
                throw;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[联机][聚物] 万叶玩家：消费 prepTask 失败，转兜底");
                prep = PreparationResult.Skipped;
            }
        }
        else
        {
            prep = PreparationResult.Skipped;
        }

        CombatScenes? combatScenes;
        Avatar? preKazuha = null;
        bool useFastPath = false;

        if (prep.SwitchedSuccessfully && prep.CombatScenes != null && prep.Kazuha != null)
        {
            combatScenes = prep.CombatScenes;
            preKazuha = prep.Kazuha;
            useFastPath = true;
        }
        else
        {
            // 兜底：缓存未命中 / 预切人失败 / 异常。
            // spec kazuha-collect-team-no-kazuha-false-skip-fix：本机已是万叶（RunAsKazuhaAsync 仅在
            // IsCurrentPlayerKazuha 分支调用），怕假阴性（有万叶却降级卡整房），按三层顺序取万叶。
            combatScenes = null;

            // ---- 第 1 层：复用战斗阶段稳定快照（最治本，零识别零抖动）----
            // fix-kazuha-collect-layer1-dead-code：优先读 PathExecutor 清空前捕获的副本
            // （_capturedCombatSnapshot，每场战斗结束写入）；原直读 CombatScenesGoBackUp 因
            // PathExecutor.cs:1338 清空先于聚物分支而永不命中，保留为次级回读以兼容未捕获的旧流程。
            // 只看万叶在不在，不校验队伍其他角色（Open Question Q9）。本 spec 对该字段只读不写（Preservation 3.3）。
            var snapshot = _capturedCombatSnapshot ?? PathingConditionConfig.CombatScenesGoBackUp;
            bool snapshotHasKazuha = snapshot != null && snapshot.SelectAvatar("枫原万叶") != null;
            if (KazuhaCollectRecognitionDecisions.ShouldUseCombatSnapshot(snapshotHasKazuha))
            {
                combatScenes = snapshot;
                _logger.LogInformation("[联机][聚物] 万叶玩家：第 1 层命中——复用战斗快照聚物（零识别）");
            }
            else
            {
                // ---- 第 2 层：forceRefresh 重试最多 N 次，任一次取到含万叶即停 ----
                int attempt = 0;
                bool gotKazuha = false;
                while (KazuhaCollectRecognitionDecisions.ShouldContinueRetry(
                           attempt, KazuhaCollectRecognitionDecisions.MaxRecognitionRetries, gotKazuha))
                {
                    attempt++;
                    try
                    {
                        var refreshed = await RunnerContext.Instance.GetCombatScenes(ct, forceRefresh: true);
                        if (refreshed != null && refreshed.SelectAvatar("枫原万叶") != null)
                        {
                            combatScenes = refreshed;
                            gotKazuha = true;
                            _logger.LogInformation("[联机][聚物] 万叶玩家：第 2 层第 {Attempt}/{Max} 次重试取到含万叶队伍",
                                attempt, KazuhaCollectRecognitionDecisions.MaxRecognitionRetries);
                        }
                        else
                        {
                            _logger.LogWarning("[联机][聚物] 万叶玩家：第 2 层第 {Attempt}/{Max} 次重试未取到万叶（继续）",
                                attempt, KazuhaCollectRecognitionDecisions.MaxRecognitionRetries);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        // 取消透传（Preservation 3.9）：不当作一次"未取到万叶"，不广播终态。
                        throw;
                    }
                    catch (Exception ex)
                    {
                        // 单次非取消异常：记该次失败，继续下一次重试（不整体降级，除非耗尽）。
                        _logger.LogWarning(ex, "[联机][聚物] 万叶玩家：第 2 层第 {Attempt}/{Max} 次重试 GetCombatScenes 异常（继续）",
                            attempt, KazuhaCollectRecognitionDecisions.MaxRecognitionRetries);
                    }
                }
            }
        }

        // ---- 第 3 层：三层耗尽仍取不到万叶 → 保留原降级语义（终态）----
        if (combatScenes == null)
        {
            _logger.LogWarning("[联机][聚物] 万叶玩家：第 1 层未命中 + 第 2 层重试耗尽，CombatScenes 不可用，本周期不聚物");
            CurrentState = KazuhaCollectState.Skipped;
            return;
        }

        KazuhaCollectExecutor.Outcome outcome;
        try
        {
            outcome = await KazuhaCollectExecutor.RunAsync(
                combatScenes,
                waitSkillCdSeconds: _config.KazuhaWaitSkillCdSeconds,
                // 联机版万叶玩家"下落攻击完成后的拾取等待"复用 KazuhaSyncWaitSeconds（与非万叶玩家停留时长一致），
                // 让万叶/非万叶玩家完成后近乎同步离开战斗点，避免"万叶比别人晚 N 秒离开"的奇怪节奏。
                postPickupWaitMs: Math.Max(0, _config.KazuhaSyncWaitSeconds) * 1000,
                onProgress: stage => _logger.LogInformation("[联机][聚物] 万叶阶段: {Stage}", stage),
                assumeAlreadySwitched: useFastPath,
                preselectedKazuha: preKazuha,
                onBeforeHoldE: hookCt =>
                {
                    // multiplayer-kazuha-collect-point-broadcast: HoldE 起手前算 + 上报聚物点。
                    // 任意环节失败 → 调"无效坐标"分支 (NaN, NaN)，由 **客户端** IsValid 守卫过滤
                    // （CoordinatorClient.NotifyKazuhaCollectStartedAsync 内，spec
                    // kazuha-collect-point-nan-signalr-serialization-fix 引入），再走服务端 IsValid 兜底，
                    // 加非万叶玩家本地订阅 IsValid 兜底，三层防护。最终所有 peer 走原 MoveCloseTo(战斗点) 单段路径，零回归。
                    // 朝向用 CharacterOrientation（角色面向）而不是 CameraOrientation（相机视角）：
                    //   万叶 HoldE 风场聚物中心由角色脚下 + 角色面前定义，与相机视角无关；
                    //   战斗结束后 MoveCloseTo 走回战斗点过程中角色面向已稳定到前进方向，
                    //   此处取得的 angle 才是真正的 HoldE 风场指向。
                    double cx, cy;
                    try
                    {
                        using var ra = TaskControl.CaptureToRectArea();

                        // kazuha-collect-fightpoint-position-misrecognition-fix 方案 A（首帧播种）：
                        // 读坐标算聚物点之前，用战斗点坐标播种 Navigation 单例锚点，避免沿用上一段远处残留 prev 锚错（BC3）。
                        // 仅 SetPrevPosition 覆写 prev，绝不调用 Navigation.Reset()。
                        var seed = KazuhaCollectPositionGuardDecisions.ComputeSeedAnchor(fightPointWaypoint.X, fightPointWaypoint.Y);
                        Navigation.SetPrevPosition((float)seed.X, (float)seed.Y);

                        var pos = Navigation.GetPosition(ra, fightPointWaypoint.MapName, fightPointWaypoint.MapMatchMethod);

                        // 方案 B（读取侧距离护栏）：识别结果距种子锚点（战斗点）超阈值 → 不可信。
                        // B-1：先强制全局重匹配一次（SetPrevPosition(0,0) 让 GetPosition 走全局匹配路径，不调 Reset）。
                        if (pos.X != 0 && pos.Y != 0
                            && !KazuhaCollectPositionGuardDecisions.IsRecognizedPositionTrustworthy(
                                   pos, seed.X, seed.Y, KazuhaCollectPositionGuardDecisions.RecognizedPositionGuardThreshold))
                        {
                            _logger.LogWarning(
                                "[联机][聚物] 聚物点上报：识别结果 ({X:F1},{Y:F1}) 距战斗点 ({SX:F1},{SY:F1}) 超阈值 {Th}，B-1 强制全局重匹配一次",
                                pos.X, pos.Y, seed.X, seed.Y, KazuhaCollectPositionGuardDecisions.RecognizedPositionGuardThreshold);
                            Navigation.SetPrevPosition(0, 0); // 置无效锚点 → 下次 GetPosition 走全局匹配（不调 Reset，副作用最小）
                            pos = Navigation.GetPosition(ra, fightPointWaypoint.MapName, fightPointWaypoint.MapMatchMethod);
                        }

                        if (pos.X == 0 && pos.Y == 0)
                        {
                            _logger.LogDebug("[联机][聚物] 聚物点上报：位置识别失败 (0,0)，调无效坐标分支");
                            cx = double.NaN; cy = double.NaN;
                        }
                        else if (!KazuhaCollectPositionGuardDecisions.IsRecognizedPositionTrustworthy(
                                     pos, seed.X, seed.Y, KazuhaCollectPositionGuardDecisions.RecognizedPositionGuardThreshold))
                        {
                            // B-2：全局重匹配后仍超阈值 → 判失败，置无效坐标，复用既有 IsValid 三层过滤（不广播错误聚物点）。
                            // 让所有 peer 退回原 MoveCloseTo(战斗点) 单段路径（上层兜底），绝不广播错误聚物点。
                            _logger.LogWarning(
                                "[联机][聚物] 聚物点上报：B-1 全局重匹配后 ({X:F1},{Y:F1}) 仍距战斗点超阈值，B-2 判失败（不广播聚物点）",
                                pos.X, pos.Y);
                            cx = double.NaN; cy = double.NaN;
                        }
                        else
                        {
                            // CharacterOrientation 输入是小地图 Mat，从全屏裁出
                            using var miniMap = new OpenCvSharp.Mat(ra.SrcMat, MapAssets.Get(ra).MimiMapRect);
                            var theta = CharacterOrientation.Compute(miniMap); // 角度，0-360（角色面向）
                            (cx, cy) = KazuhaCollectPointDecisions.ComputeCollectPoint(
                                pos.X, pos.Y, theta, forwardDistance: 1.0);
                            _logger.LogDebug(
                                "[联机][聚物] 上报聚物点 syncKey={Key} pos=({X:F1},{Y:F1}) θ={Theta}° (角色面向) → ({CX:F1},{CY:F1})",
                                syncKey, pos.X, pos.Y, theta, cx, cy);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        _logger.LogDebug(ex, "[联机][聚物] 聚物点上报：位置/朝向获取失败，调无效坐标分支");
                        cx = double.NaN; cy = double.NaN;
                    }

                    // fire-and-forget；CoordinatorClient 内部 try/catch + LogWarning + FireAndForget helper 双层兜底
                    FireAndForget(
                        _client.NotifyKazuhaCollectStartedAsync(syncKey, cx, cy),
                        "NotifyKazuhaCollectStarted(syncKey,collectX,collectY)");
                    return Task.CompletedTask;
                },
                ct: ct);
        }
        catch (RetryException)
        {
            _logger.LogWarning("[联机][聚物] 万叶玩家在聚物中触发 RetryException，向上抛");
            CurrentState = KazuhaCollectState.Skipped;
            throw;
        }

        if (outcome.Success)
        {
            _logger.LogInformation("[联机][聚物] 万叶玩家聚物完成");
            CurrentState = KazuhaCollectState.Finished;
        }
        else
        {
            var reason = outcome.SkipReason ?? "unknown";
            _logger.LogWarning("[联机][聚物] 万叶玩家聚物降级，原因={Reason}", reason);
            CurrentState = KazuhaCollectState.Skipped;
        }
    }

    /// <summary>
    /// 普通玩家分支：subscribe-before-action 订阅 Finished/Skipped → 上报 AtFightPoint
    /// → 等待终态事件 → 任意终态后统一停 KazuhaSyncWaitSeconds 秒后离开（不再做 elapsedMs 补足计算）。
    /// </summary>
    private async Task WaitAsNonKazuhaAsync(string syncKey, CancellationToken ct)
    {
        // hoeing-kazuha-collect-drop-terminal-signal: 砍终态信号闭环。
        // 二段精接近由 PathExecutor 在调用本方法之前完成（依赖 KazuhaCollectStarted 坐标广播，与终态无关）。
        // 本方法只剩"统一停 KazuhaSyncWaitSeconds 秒"——不再订阅 Finished/Skipped、不构造 terminalTcs、
        // 不创建 KazuhaSyncTimeoutSeconds 超时 CTS，对终态信号是否到达完全无感（design.md Property 1）。
        CurrentState = KazuhaCollectState.WaitingForKazuha;
        var maskedUid = AutoPartyTask.MaskUid(_client.PlayerUid);

        // kazuha-collect-min-buffer-before-stay: 走向 A（本周期收到有效聚物点坐标）时，
        // 从"收到坐标时刻"算起至少满 MinBufferBeforeStayMs(1500ms) 才进 KazuhaSyncWaitSeconds 停留，
        // 避免成员恰好站在聚物点上 → 二段瞬间完成 → 比万叶物堆成型更早离开、吃不到聚物。
        // 走向 B（未收到坐标 / syncKey 不匹配）→ receivedCollectPoint == false → 补足 0，行为逐字节不变。
        var receivedCollectPoint = TryGetCollectPointForCurrent(syncKey, out _, out _);
        var elapsedMs = receivedCollectPoint
            ? (DateTime.UtcNow - _lastCollectPointTimeUtc).TotalMilliseconds
            : 0.0;
        var bufferRemainMs = KazuhaCollectSyncDecisions.ComputeMinBufferRemainMs(receivedCollectPoint, elapsedMs);
        if (bufferRemainMs > 0)
        {
            _logger.LogInformation(
                "[联机][聚物] 非万叶玩家 {Uid} 二段完成，收到坐标已过 {Elapsed:F0}ms，补足缓冲 {Buffer}ms 后再停留 (syncKey={Key})",
                maskedUid, elapsedMs, bufferRemainMs, syncKey);
            await Task.Delay(bufferRemainMs, ct);
        }

        var waitMs = KazuhaCollectSyncDecisions.ComputePostSecondApproachWaitMs(_config);
        _logger.LogInformation("[联机][聚物] 非万叶玩家 {Uid} 二段完成，统一停留 {Ms}ms 后离开 (syncKey={Key})",
            maskedUid, waitMs, syncKey);
        await Task.Delay(waitMs, ct);
        CurrentState = KazuhaCollectState.Finished;
    }

    /// <summary>每次进入新一段战后回点前调用，重置状态机。</summary>
    public void ResetForNextCycle()
    {
        CurrentState = KazuhaCollectState.Idle;
    }

    /// <summary>
    /// 捕获上一场战斗的稳定快照副本（fix-kazuha-collect-layer1-dead-code）。
    /// 由 PathExecutor 在清空 PathingConditionConfig.CombatScenesGoBackUp 之前调用；
    /// 传入 null 表示上一场无可用快照（识别失败等），三层取队第 1 层自然不命中、走原重试路径。
    /// 不在 ResetForNextCycle 中清空：捕获发生在战斗结束、消费发生在随后的 WaitAtFightPointAsync，
    /// 时序上 Reset 先于消费，清空会误删本场快照。
    /// </summary>
    public void CaptureCombatSnapshot(CombatScenes? snapshot)
    {
        _capturedCombatSnapshot = snapshot;
    }

    /// <summary>
    /// 轮末统一重跑的 syncKey 隔离后缀（multiplayer-hoeing-rerun-sync-isolation 对齐方案，如 "_rerun_r3"）。
    /// 由 PathExecutor 在进战后聚物分支时同步本字段（与 PathExecutor.RerunSyncIdSuffix 同源），
    /// 保证万叶侧广播的 syncKey 与成员侧等待的 syncKey 携带相同后缀。
    /// 首次跑线为 null/空 → syncKey 逐字节不变（零回归）。
    /// </summary>
    public string? RerunSyncIdSuffix { get; set; }

    /// <summary>
    /// 把 Notify*Async 改成 fire-and-forget 时使用的兜底 helper：
    /// 转调独立 <see cref="FireAndForgetHelper.ObserveExceptions"/> 实现，便于 PBT-5 直接覆盖 helper 的语义。
    /// CoordinatorClient 内部已有 try/catch + LogWarning 静默吞 SignalR 异常；
    /// 此处兜底 OperationCanceledException / 其他未预期异常，避免 unobserved task exception 累积。
    /// 不允许使用裸 _ = client.InvokeAsync(...)（异常 sink 会丢失日志）。
    /// </summary>
    private void FireAndForget(Task task, string opLabel)
    {
        _ = FireAndForgetHelper.ObserveExceptions(task, _logger, opLabel);
    }

    /// <summary>
    /// 取消事件订阅。kazuha-player-auto-detection: KazuhaPlayerUpdated 订阅在构造函数中建立，
    /// 这里释放避免重复订阅触发或 _client 生命周期残留引用。
    /// </summary>
    public void Dispose()
    {
        _client.KazuhaPlayerUpdated -= _onKazuhaPlayerUpdated;
        _client.KazuhaCollectStarted -= _onKazuhaCollectStartedCache;
    }
}
