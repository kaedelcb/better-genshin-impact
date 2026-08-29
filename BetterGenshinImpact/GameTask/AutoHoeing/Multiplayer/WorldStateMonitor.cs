#nullable enable

using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer.Models;
using BetterGenshinImpact.GameTask.Common.Job;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;

namespace BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

/// <summary>
/// 世界状态周期性监测器（重设计版）。
/// 后台 Task 周期性检测当前玩家是否仍在房主世界及房间中。
/// 使用信号融合（截图 + SignalR 连接状态）、恢复窗口、传送抑制消除误报。
/// </summary>
public class WorldStateMonitor : IAsyncDisposable
{
    private readonly ILogger<WorldStateMonitor> _logger = App.GetLogger<WorldStateMonitor>();
    private readonly CoordinatorClient _client;
    private readonly string _playerUid;
    // 单人调试模式（纯本地，构造时注入；遵循显式依赖，不读全局静态）。
    // 为 true 时跳过 connected-but-not-in-game 被踢出终止判定。详见 hoeing-multiplayer-solo-debug-mode。
    private readonly bool _soloDebugMode;
    private readonly CancellationTokenSource _cts;
    private Task? _monitorTask;

    // === 截图失败计数 ===
    private int _consecutiveScreenshotFailures;
    private const int ScreenshotFailureThreshold = 3;
    private const int ScreenshotFailurePauseMs = 30_000;

    // === 信号融合（需求 1）===
    private int _connectedButNotInGame;
    private const int ConnectedNotInGameThreshold = 7; // 约 21 秒（3s 间隔 × 7 次）

    // === 传送抑制（需求 4）===
    private volatile bool _isTeleportSuppressed;
    private DateTime _teleportSuppressionStart;
    private const int TeleportSuppressionTimeoutSeconds = 40;

    // === 恢复窗口（需求 3）===
    private bool _isInRecoveryWindow;
    private DateTime _recoveryWindowStart;
    private DateTime _recoveryWindowAbsoluteStart; // 绝对起始时间，防止无限延长（EC-06）
    private const int RecoveryWindowSeconds = 30;
    private const int RecoveryWindowAbsoluteMaxSeconds = 120; // 绝对最大时长
    private const int RecoveryCheckIntervalMs = 3_000;
    private const int NormalCheckIntervalMs = 10_000;

    // === 组队阶段抑制（保留之前的 bugfix）===
    /// <summary>自动组队阶段为 true，忽略 IsInMultiGame == false</summary>
    public volatile bool IsPartyPhase;

    // === R5 队友掉线协同中止检测（hoeing-multiplayer-guard-auto-restart §10）===
    // 仅 HoeingGuardMode 开启时启用（构造注入）。执行期房间人数低于基准 → 判定队友掉线 → 协同中止重开。
    private readonly bool _peerDropGuardEnabled;
    private int _peerBaselineCount = -1;   // 执行期基准人数（-1 = 未捕获）
    private int _peerBelowStreak;          // 连续低于基准次数（去抖，达 PeerDropConfirmChecks 触发）
    private const int PeerDropConfirmChecks = 2;

    // === 视觉失明检测（锄地必须全员）===
    private bool _visualMismatchInProgress;   // 是否正处于"视觉<协调器"持续观察中
    private DateTime _visualMismatchStart;    // 首次观察到不一致的时刻（墙钟起点）
    private const double VisualMismatchExitWindowSeconds = 30; // 持续观察墙钟窗口（秒）

    // === 轮次切换抑制（需求 7）===
    private volatile bool _isRoundSwitching;
    private DateTime _roundSwitchStart;
    // 轮次切换墙钟兜底超时（秒）。不再写死 120：由 BeginRoundSwitch 注入运行时值
    // （= PartyTimeoutSeconds + 余量），经 RoundSwitchTimeoutDecisions.Resolve 钳制。
    // 初值为安全下限，保证 BeginRoundSwitch 调用前/单机无注入时行为不短于原 120s。
    private int _roundSwitchTimeoutSeconds = RoundSwitchTimeoutDecisions.SafeFloorSeconds;

    /// <summary>
    /// 多世界轮次是否正在切换中（公开只读访问，供 AutoHoeingTask 的事件处理器使用）。
    /// 切换期间需要忽略服务端的 RoomClosed 广播，避免旧房间关闭误终止整个多世界任务。
    /// </summary>
    public bool IsRoundSwitching => _isRoundSwitching;

    // === 换角色抑制（hoeing-perroute-switchroles-worldmonitor-falsekick-fix）===
    // 按线路切换角色期间会打开配对/队伍/角色选择界面，IsInMultiGame=false 但 SignalR 正常，
    // 属合法窗口。与轮次切换同模式：抑制期忽略 IsInMultiGame=false，墙钟兜底防卡死。
    private volatile bool _isRoleSwitching;
    private DateTime _roleSwitchStart;
    private const int RoleSwitchSafeFloorSeconds = 120; // OQ-1：固定兜底 120s
    private int _roleSwitchTimeoutSeconds = RoleSwitchSafeFloorSeconds;

    /// <summary>是否正在按线路换角色中（公开只读，供诊断/未来事件处理器使用）。</summary>
    public bool IsRoleSwitching => _isRoleSwitching;

    // === 吃药抑制（multiplayer-hoeing-auto-eat-food-by-period）===
    // 吃药打开背包食物页浮层期间 IsInMultiGame=false 但 SignalR 正常，属合法窗口。
    // 与换角色/轮次切换同模式：抑制期忽略 IsInMultiGame=false，墙钟兜底防卡死。
    private volatile bool _isEatingMedicine;
    private DateTime _eatingMedicineStart;
    private const int EatingMedicineSafeFloorSeconds = 60; // 吃药耗时远小于此，兜底防永久关闭被踢检测

    /// <summary>是否正在按周期吃食物中（公开只读，供诊断使用）。</summary>
    public bool IsEatingMedicine => _isEatingMedicine;

    // === 心跳失败独立退出路径（EC-03）===
    // 判定改用"真实墙钟窗口"而非"失败次数"：SignalR 重连结束瞬间，重连窗口内排队的多个
    // InvokeAsync 会在同一时刻批量抛异常，若按次数累计会瞬间打满阈值导致误退（把"批量爆发"
    // 误当成"连续 30 秒失败"）。改为记录首次失败时刻，用真实流逝时间判定，天然免疫批量爆发；
    // 同时保留最小失败次数守护，避免单次偶发失败在窗口边界误触发。详见方案二分析。
    private int _consecutiveHeartbeatFailures;
    private DateTime _firstHeartbeatFailureTime;
    private const int HeartbeatFailureExitWindowSeconds = 30; // 真实墙钟窗口：连续失败持续满 30s 才退出
    private const int HeartbeatFailureMinCount = 2; // 最小失败次数守护，防单次偶发失败在窗口边界误触发

    // === 事件 ===
    /// <summary>确认退出世界，触发协调停止。参数 (isHost, reason)。</summary>
    public event Func<bool, string, Task>? OnExitConfirmed;

    /// <summary>掉出房间且重试全部失败。</summary>
    public event Func<Task>? OnDroppedFromRoom;

    public WorldStateMonitor(CoordinatorClient client, string playerUid, CancellationToken externalCt = default, bool soloDebugMode = false, bool peerDropGuardEnabled = false)
    {
        _client = client;
        _playerUid = playerUid;
        _soloDebugMode = soloDebugMode;
        _peerDropGuardEnabled = peerDropGuardEnabled;
        // EC-01: 使用 linked CTS，外部取消时后台 Task 也自动停止
        _cts = externalCt == default
            ? new CancellationTokenSource()
            : CancellationTokenSource.CreateLinkedTokenSource(externalCt);
    }

    // === 公开方法 ===

    /// <summary>启动后台监测循环。</summary>
    public void Start()
    {
        _monitorTask = Task.Run(MonitorLoopAsync);
        _logger.LogInformation("[WorldStateMonitor] 已启动");
    }

    /// <summary>停止后台监测（同步，兼容旧调用）。</summary>
    public void Stop()
    {
        _cts.Cancel();
        _logger.LogInformation("[WorldStateMonitor] 已停止（同步）");
    }

    /// <summary>停止后台监测，等待后台 Task 完成（最多 3 秒）。</summary>
    public async Task StopAsync()
    {
        _cts.Cancel();
        if (_monitorTask != null)
        {
            try
            {
                await _monitorTask.WaitAsync(TimeSpan.FromSeconds(3));
            }
            catch (TimeoutException)
            {
                _logger.LogWarning("[WorldStateMonitor] 后台 Task 3 秒内未完成，强制继续");
            }
            catch (OperationCanceledException) { }
        }
        _logger.LogInformation("[WorldStateMonitor] 已停止");
    }

    /// <summary>PathExecutor 传送前调用，进入传送抑制期。</summary>
    public void BeginTeleportSuppression()
    {
        _teleportSuppressionStart = DateTime.UtcNow;
        _isTeleportSuppressed = true;
        _logger.LogDebug("[WorldStateMonitor] 进入传送抑制期");
    }

    /// <summary>PathExecutor 传送完成后调用，解除传送抑制期。</summary>
    public void EndTeleportSuppression()
    {
        _isTeleportSuppressed = false;
        _logger.LogDebug("[WorldStateMonitor] 传送抑制期结束");
    }

    /// <summary>
    /// 传送失败重试时调用：仅当仍处于抑制期时，刷新抑制起始时刻（重置墙钟超时窗口）。
    /// 由 TpTask.Tp 的重试 catch 分支经 PathExecutor.CurrentWorldStateMonitor?. 调用。
    /// 非抑制态调用为 no-op（不创建新抑制态、不动任何计数）。单机模式 CurrentWorldStateMonitor==null
    /// 时为 null-conditional no-op，零感知。详见 design.md §Correctness Properties Property 3/4。
    /// </summary>
    public void RefreshTeleportSuppression()
    {
        if (!_isTeleportSuppressed) return;
        _teleportSuppressionStart = DateTime.UtcNow;
        _logger.LogDebug("[WorldStateMonitor] 传送失败重试，刷新传送抑制计时");
    }

    /// <summary>
    /// 多世界轮次切换开始，暂停所有检测。
    /// </summary>
    /// <param name="suppressionTimeoutSeconds">
    /// 本次抑制的墙钟兜底超时（秒），由调用方按 config.PartyTimeoutSeconds + 余量 传入。
    /// 经 RoundSwitchTimeoutDecisions.Resolve 钳制后写入 _roundSwitchTimeoutSeconds，
    /// 取代原写死的 120s，使兜底永远晚于集合点等待超时。详见 design.md §Fix Implementation。
    /// </summary>
    public void BeginRoundSwitch(int suppressionTimeoutSeconds)
    {
        _roundSwitchStart = DateTime.UtcNow;
        _roundSwitchTimeoutSeconds = RoundSwitchTimeoutDecisions.Resolve(suppressionTimeoutSeconds);
        _isRoundSwitching = true;
        _logger.LogInformation("[WorldStateMonitor] 进入轮次切换状态（墙钟兜底 {Timeout}s）",
            _roundSwitchTimeoutSeconds);
    }

    /// <summary>多世界轮次切换完成，恢复检测并重置状态。</summary>
    public void EndRoundSwitch()
    {
        _isRoundSwitching = false;
        ResetInternalState();
        _logger.LogInformation("[WorldStateMonitor] 轮次切换完成，已重置内部状态");
    }

    /// <summary>
    /// 按线路换角色开始，暂停被踢出（connected-but-not-in-game）检测。
    /// 换角色界面下 IsInMultiGame=false 但 SignalR 正常，属合法窗口，不应被判被踢出。
    /// </summary>
    /// <param name="suppressionTimeoutSeconds">墙钟兜底超时（秒）；&lt;=0 时回落安全下限 120s。</param>
    public void BeginRoleSwitch(int suppressionTimeoutSeconds = RoleSwitchSafeFloorSeconds)
    {
        _roleSwitchStart = DateTime.UtcNow;
        _roleSwitchTimeoutSeconds = suppressionTimeoutSeconds > 0
            ? suppressionTimeoutSeconds
            : RoleSwitchSafeFloorSeconds;
        _isRoleSwitching = true;
        _logger.LogInformation("[WorldStateMonitor] 进入换角色抑制状态（墙钟兜底 {Timeout}s）",
            _roleSwitchTimeoutSeconds);
    }

    /// <summary>按线路换角色完成，恢复检测并重置内部累计状态。</summary>
    public void EndRoleSwitch()
    {
        _isRoleSwitching = false;
        ResetInternalState();
        _logger.LogInformation("[WorldStateMonitor] 换角色完成，已重置内部状态");
    }

    /// <summary>PathExecutor 吃药打开食物页前调用，进入吃药抑制期。</summary>
    public void BeginMedicineEat()
    {
        _eatingMedicineStart = DateTime.UtcNow;
        _isEatingMedicine = true;
        _logger.LogDebug("[WorldStateMonitor] 进入吃药抑制状态");
    }

    /// <summary>吃药完成，恢复检测并重置内部累计状态。</summary>
    public void EndMedicineEat()
    {
        _isEatingMedicine = false;
        ResetInternalState();
        _logger.LogDebug("[WorldStateMonitor] 吃药完成，已重置内部状态");
    }

    /// <summary>
    /// 心跳失败时由 CoordinatorClient 调用。用真实墙钟窗口判定退出：
    /// 仅当"连续失败持续时长 >= HeartbeatFailureExitWindowSeconds 且失败次数 >= HeartbeatFailureMinCount"
    /// 时才触发退出。首次失败（count 从 0→1）记录起始时刻，成功一次即清零并重置计时。
    /// 这样重连结束瞬间批量抛出的多个失败因 elapsed≈0 不会误触发，只有真实持续的网络故障才退出。
    /// </summary>
    public void NotifyHeartbeatFailure()
    {
        if (_consecutiveHeartbeatFailures == 0)
        {
            _firstHeartbeatFailureTime = DateTime.UtcNow; // 记录首次失败时刻，作为墙钟窗口起点
        }
        _consecutiveHeartbeatFailures++;

        var elapsed = (DateTime.UtcNow - _firstHeartbeatFailureTime).TotalSeconds;
        if (elapsed >= HeartbeatFailureExitWindowSeconds
            && _consecutiveHeartbeatFailures >= HeartbeatFailureMinCount)
        {
            _logger.LogError("[WorldStateMonitor] 心跳连续失败持续 {Sec:F0}s（{Count} 次），触发退出",
                elapsed, _consecutiveHeartbeatFailures);
            _consecutiveHeartbeatFailures = 0;
            // 异步触发退出，不阻塞心跳回调
            _ = Task.Run(async () =>
            {
                try { await ConfirmExitAsync("心跳连续失败超过阈值"); }
                catch (Exception ex) { _logger.LogWarning(ex, "[WorldStateMonitor] 心跳失败触发退出异常"); }
            });
        }
    }

    /// <summary>心跳成功时重置失败计数（count 归 0 隐式使下次失败重新记录墙钟起点）。</summary>
    public void NotifyHeartbeatSuccess()
    {
        _consecutiveHeartbeatFailures = 0;
    }

    /// <summary>重置所有内部计数器和状态。</summary>
    public void ResetInternalState()
    {
        _consecutiveScreenshotFailures = 0;
        _connectedButNotInGame = 0;
        _consecutiveHeartbeatFailures = 0;
        _isInRecoveryWindow = false;
        _isTeleportSuppressed = false;
    }

    public async ValueTask DisposeAsync()
    {
        await StopAsync();
        _cts.Dispose();
    }

    // === 内部方法 ===

    private async Task MonitorLoopAsync()
    {
        while (!_cts.Token.IsCancellationRequested)
        {
            try
            {
                var interval = (_isInRecoveryWindow || _connectedButNotInGame > 0)
                    ? RecoveryCheckIntervalMs
                    : NormalCheckIntervalMs;
                await Task.Delay(interval, _cts.Token);
                await CheckOnceAsync();
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[WorldStateMonitor] 检测循环异常，继续下一轮");
            }
        }
    }

    private async Task CheckOnceAsync()
    {
        // 暂停态优先级最高：用户按快捷键暂停（如调整角色装备）期间，IsInMultiGame=false 是预期行为，
        // 不应被计入"被踢出联机世界"的 ConnectedNotInGame 阈值。同时清零相关计数与恢复窗口，
        // 避免恢复后立即被旧累计触发协调停止。
        if (BetterGenshinImpact.GameTask.RunnerContext.Instance.IsSuspend)
        {
            if (_connectedButNotInGame > 0 || _isInRecoveryWindow)
            {
                _logger.LogInformation("[WorldStateMonitor] 检测到任务暂停，重置异常计数与恢复窗口");
                _connectedButNotInGame = 0;
                _isInRecoveryWindow = false;
            }
            else
            {
                _logger.LogDebug("[WorldStateMonitor] 任务暂停中，跳过本轮检测");
            }
            return;
        }

        // === R5 队友掉线协同中止检测（hoeing-multiplayer-guard-auto-restart §10）===
        // 与自身 IsInMultiGame 流程正交：只比房间人数、不截图。仅"干净执行阶段"生效，
        // 抑制窗口（组队/轮换/换角色/吃药/传送/暂停）一律复位基准，等下个执行阶段重新捕获。
        // 仅 HoeingGuardMode 开启才启用；关闭时整块跳过，历史行为（剩下人继续跑）逐字节不变。
        if (_peerDropGuardEnabled)
        {
            bool inSuppressedWindow =
                IsPartyPhase || _isRoundSwitching || _isRoleSwitching || _isEatingMedicine
                || _isTeleportSuppressed
                || BetterGenshinImpact.GameTask.RunnerContext.Instance.IsSuspend;

            if (inSuppressedWindow)
            {
                // 合法人数波动窗口：分两类处理（guard-multiplayer-peerdrop-visual-blind 边界1）
                // - 组队阶段 / 轮换切换：房间语义重置（人未齐/房间重建），复位基准，等下阶段重新捕获。
                // - 传送 / 换角色 / 吃药 / 暂停：房间未变，协调器人数不该变 → 保留基准，
                //   使抑制窗口后协调器人数下降（真掉线）能被 R5 感知（修复"人数降了客户端不知道"）。
                if (IsPartyPhase || _isRoundSwitching)
                {
                    _peerBaselineCount = -1;
                }
                _peerBelowStreak = 0;
            }
            else
            {
                var currentCount = _client.CurrentRoomPlayerCount;
                // 人数 <= 0 视为列表尚未就绪（滞后/断连瞬间），跳过本帧不误判
                if (currentCount > 0)
                {
                    var (newBaseline, below) = HoeingGuardDecisions.UpdatePeerBaseline(_peerBaselineCount, currentCount);
                    _peerBaselineCount = newBaseline;
                    if (below)
                    {
                        _peerBelowStreak++;
                        // 增强可观测性：打印当前在线成员 UID+名字 和 自己 UID，便于定位具体掉线者
                        var onlineMembers = _client.CurrentPlayerList?
                            .Select(p => $"{p.PlayerUid}({p.PlayerName})")
                            .ToList() ?? new List<string>();
                        var onlineDisplay = string.Join(", ", onlineMembers);
                        var myUid = _client.PlayerUid;
                        _logger.LogWarning("[WorldStateMonitor] 执行期房间人数 {Cur} 低于基准 {Base}（{Streak}/{Need}），疑似队友掉线 | 当前在线=[{Online}] | 自己={Self}",
                            currentCount, newBaseline, _peerBelowStreak, PeerDropConfirmChecks,
                            onlineDisplay, myUid);
                        if (_peerBelowStreak >= PeerDropConfirmChecks)
                        {
                            _peerBelowStreak = 0;
                            var confirmedOnlineMembers = _client.CurrentPlayerList?
                                .Select(p => $"{p.PlayerUid}({p.PlayerName})")
                                .ToList() ?? new List<string>();
                            _logger.LogError("[WorldStateMonitor] 确认队友掉线（人数 {Cur} < 基准 {Base}），触发协同中止重开 | 当前在线=[{Online}] | 自己={Self}",
                                currentCount, newBaseline,
                                string.Join(", ", confirmedOnlineMembers), _client.PlayerUid);
                            await ConfirmExitAsync("检测到队友掉线，协同中止重开");
                            return;
                        }
                    }
                    else
                    {
                        _peerBelowStreak = 0;
                    }

                    // === 视觉失明检测（锄地必须全员）===
                    // 视觉真实人数（P 模板）< 协调器花名册人数且持续墙钟 30s → 判定有人退世界/掉线
                    // 未被协调器感知（CurrentRoomPlayerCount 只跟踪房间花名册，不跟踪"是否在世界"）。
                    // 真实墙钟 + 最小帧数防抖，免疫战斗中瞬间漏识别（复用 EC-03 免疫批量爆发模式）。
                    int visualCount;
                    try
                    {
                        using var region = CaptureToRectArea();
                        var visualStatus = PartyAvatarSideIndexHelper.DetectedMultiGameStatus(
                            region, applyAuthoritativeCrossValidation: false);
                        visualCount = visualStatus.PlayerCount;
                    }
                    catch
                    {
                        visualCount = 0; // 截图失败视为识别不到，不触发失明
                    }

                    var coordCount = _client.CurrentRoomPlayerCount;
                    var mismatch = visualCount < coordCount;
                    if (mismatch)
                    {
                        if (!_visualMismatchInProgress)
                        {
                            _visualMismatchInProgress = true;
                            _visualMismatchStart = DateTime.UtcNow;
                        }
                        var mismatchElapsed = (DateTime.UtcNow - _visualMismatchStart).TotalSeconds;
                        if (HoeingGuardDecisions.ShouldTriggerVisualMismatchExit(
                                _peerDropGuardEnabled, inSuppressedWindow,
                                visualCount, coordCount, mismatchElapsed, VisualMismatchExitWindowSeconds))
                        {
                            _visualMismatchInProgress = false;
                            _logger.LogError(
                                "[WorldStateMonitor] 视觉真实人数 {VC} 持续低于协调器花名册 {CC} 满 {Win}s，判定成员退世界/掉线失明，触发协同中止重开",
                                visualCount, coordCount, mismatchElapsed);
                            await ConfirmExitAsync("检测到成员退出世界（视觉人数持续低于协调器），协同中止重开");
                            return;
                        }
                    }
                    else
                    {
                        _visualMismatchInProgress = false; // 视觉一致/回升，重置观察
                    }
                }
            }
        }

        // 0. 传送抑制超时自动解除（RC-01: 使用局部变量快照）
        var teleportSuppressed = _isTeleportSuppressed;
        var teleportStart = _teleportSuppressionStart;
        if (teleportSuppressed &&
            (DateTime.UtcNow - teleportStart).TotalSeconds > TeleportSuppressionTimeoutSeconds)
        {
            _isTeleportSuppressed = false;
            teleportSuppressed = false;
            _connectedButNotInGame = 0; // 墙钟兜底解除后，累计从 0 干净起算，不携带传送前陈旧计数（OQ-2）
            _logger.LogWarning("[WorldStateMonitor] 传送抑制期超过 {Timeout}s 未解除，自动解除",
                TeleportSuppressionTimeoutSeconds);
        }

        // 0b. 轮次切换超时自动解除（兜底超时为实例字段，已联动集合点等待超时 + 余量）
        if (_isRoundSwitching &&
            (DateTime.UtcNow - _roundSwitchStart).TotalSeconds > _roundSwitchTimeoutSeconds)
        {
            _isRoundSwitching = false;
            _logger.LogError("[WorldStateMonitor] 轮次切换超过 {Timeout}s 未解除，触发协调停止",
                _roundSwitchTimeoutSeconds);
            await ConfirmExitAsync("轮次切换超时");
            return;
        }

        // 0c. 换角色抑制超时自动解除（兜底防换角色卡死永久关闭被踢检测）
        if (_isRoleSwitching &&
            (DateTime.UtcNow - _roleSwitchStart).TotalSeconds > _roleSwitchTimeoutSeconds)
        {
            _isRoleSwitching = false;
            _logger.LogError("[WorldStateMonitor] 换角色超过 {Timeout}s 未解除，触发协调停止",
                _roleSwitchTimeoutSeconds);
            await ConfirmExitAsync("换角色超时");
            return;
        }

        // 0d. 吃药抑制超时自动解除（兜底防吃药卡死永久关闭被踢检测）。
        // 与换角色不同：吃药超时只解除抑制 + 清零累计，不终止任务（吃药是小动作，不值得因它退联机）。
        if (_isEatingMedicine &&
            (DateTime.UtcNow - _eatingMedicineStart).TotalSeconds > EatingMedicineSafeFloorSeconds)
        {
            _isEatingMedicine = false;
            _connectedButNotInGame = 0;
            _logger.LogWarning("[WorldStateMonitor] 吃药抑制超过 {Timeout}s 未解除，自动解除",
                EatingMedicineSafeFloorSeconds);
        }

        // 1. 截图检测 IsInMultiGame
        bool isInMultiGame;
        try
        {
            using var region = CaptureToRectArea();
            // applyAuthoritativeCrossValidation: false —— 掉线检测只信纯视觉。真掉出房间时协调器名单
            // 滞后仍报多人，交叉校验覆盖会把"已掉出"(false) 翻回 true 导致漏报掉线。
            var status = PartyAvatarSideIndexHelper.DetectedMultiGameStatus(
                region, applyAuthoritativeCrossValidation: false);
            isInMultiGame = status.IsInMultiGame;
            _consecutiveScreenshotFailures = 0;
        }
        catch (Exception ex)
        {
            // 截图本身失败（异常），不计入退出判定（需求 4.6）
            _consecutiveScreenshotFailures++;
            _logger.LogDebug(ex, "[WorldStateMonitor] 截图/识别失败（连续{Count}次）",
                _consecutiveScreenshotFailures);

            if (_consecutiveScreenshotFailures >= ScreenshotFailureThreshold)
            {
                _consecutiveScreenshotFailures = 0;
                await Task.Delay(ScreenshotFailurePauseMs, _cts.Token);
            }
            return;
        }

        // 2. IsInMultiGame == true → 一切正常
        if (isInMultiGame)
        {
            if (_isInRecoveryWindow)
            {
                _logger.LogInformation("[WorldStateMonitor] 恢复窗口内检测到正常状态，恢复成功");
                _isInRecoveryWindow = false;
            }
            _connectedButNotInGame = 0;
            return;
        }

        // 3. IsInMultiGame == false — 检查抑制状态
        // 3a. 组队阶段忽略
        if (IsPartyPhase)
        {
            _connectedButNotInGame = 0;
            _logger.LogDebug("[WorldStateMonitor] 组队阶段中，忽略 IsInMultiGame=false");
            return;
        }

        // 3b. 传送抑制期忽略（需求 4.2）
        if (teleportSuppressed)
        {
            _logger.LogDebug("[WorldStateMonitor] 传送抑制期中，忽略 IsInMultiGame=false");
            return;
        }

        // 3c. 轮次切换中忽略（需求 7.2）
        if (_isRoundSwitching)
        {
            _logger.LogDebug("[WorldStateMonitor] 轮次切换中，忽略 IsInMultiGame=false");
            return;
        }

        // 3d. 换角色中忽略（hoeing-perroute-switchroles-worldmonitor-falsekick-fix，与轮次切换同语义）
        if (_isRoleSwitching)
        {
            _logger.LogDebug("[WorldStateMonitor] 换角色中，忽略 IsInMultiGame=false");
            return;
        }

        // 3e. 吃药中忽略（multiplayer-hoeing-auto-eat-food-by-period，与换角色同语义）
        if (_isEatingMedicine)
        {
            _logger.LogDebug("[WorldStateMonitor] 吃药中，忽略 IsInMultiGame=false");
            return;
        }

        // 4. 信号融合：检查 SignalR 连接状态（需求 1.2）
        var isConnected = _client.IsConnected;

        if (isConnected)
        {
            // 单人调试模式：单人世界恒 IsInMultiGame=false + SignalR 正常，会持续累计 connected-but-not-in-game
            // 并在阈值处被踢出。开启单人调试模式时跳过这条累计与据其触发的 ConfirmExitAsync（需求 2.1/2.2），
            // 输出含"单人调试模式"标识的说明日志（需求 2.4）。其余分支（截图失败/恢复窗口/轮次切换/心跳）不受影响。
            if (SoloDebugDecisions.ShouldBypassConnectedNotInGameExit(_soloDebugMode))
            {
                if (_connectedButNotInGame != 0) _connectedButNotInGame = 0;
                _logger.LogDebug("[WorldStateMonitor] 单人调试模式已开启，跳过被踢出（connected-but-not-in-game）终止判定");
                return;
            }

            // IsInMultiGame=false + SignalR 正常 → 可能是瞬态干扰，也可能是被踢出
            _connectedButNotInGame++;
            _isInRecoveryWindow = false; // 不进入恢复窗口（OQ-4：保持不变）

            // 回主页自愈：计数命中 {3,5,7} 时先尝试回到主界面（决策器纯函数，design §Components 1）。
            // 第 3/5 次给一次自愈机会；第 7 次为退出前最后一次自愈。
            if (ReturnMainUiRecoveryDecisions.ShouldAttemptReturnMainUi(_connectedButNotInGame))
            {
                _logger.LogWarning("[WorldStateMonitor] IsInMultiGame=false 但 SignalR 正常（{Count}/{Threshold}），尝试回主页自愈",
                    _connectedButNotInGame, ConnectedNotInGameThreshold);
                // 取消异常向上传播（让 MonitorLoopAsync 结束）；其他异常仅 LogWarning 后继续。
                await TryReturnMainUiForRecoveryAsync();
            }

            if (ReturnMainUiRecoveryDecisions.ShouldConfirmExit(_connectedButNotInGame))
            {
                // 连续达到阈值 → 判定为被踢出（需求 1.3 / 1.5）。
                // count==7 时此处紧跟在上面的回主页之后执行：先回主页后退出。
                _logger.LogError("[WorldStateMonitor] 连续 {Count} 次 IsInMultiGame=false 且 SignalR 正常，判定为被踢出",
                    _connectedButNotInGame);
                await ConfirmExitAsync("被踢出联机世界（SignalR正常但截图连续失败）");
            }
            else if (!ReturnMainUiRecoveryDecisions.ShouldAttemptReturnMainUi(_connectedButNotInGame))
            {
                // count ∈ {1,2,4,6}：既不回主页也不退出，仅记录瞬态干扰（需求 1.4）
                _logger.LogDebug("[WorldStateMonitor] IsInMultiGame=false 但 SignalR 正常（{Count}/{Threshold}），判定为瞬态干扰",
                    _connectedButNotInGame, ConnectedNotInGameThreshold);
            }
            return;
        }

        // 5. IsInMultiGame=false + SignalR 断开 → 进入/继续恢复窗口（需求 1.4, 3.1）
        _connectedButNotInGame = 0;

        if (!_isInRecoveryWindow)
        {
            _isInRecoveryWindow = true;
            _recoveryWindowStart = DateTime.UtcNow;
            _recoveryWindowAbsoluteStart = DateTime.UtcNow;
            _logger.LogWarning("[WorldStateMonitor] 进入恢复窗口（IsInMultiGame=false + SignalR断开），持续 {Sec}s",
                RecoveryWindowSeconds);
            return;
        }

        // 恢复窗口中：检查是否超时
        // EC-06: 绝对最大时长检查，防止无限延长
        var absoluteElapsed = (DateTime.UtcNow - _recoveryWindowAbsoluteStart).TotalSeconds;
        if (absoluteElapsed >= RecoveryWindowAbsoluteMaxSeconds)
        {
            _logger.LogError("[WorldStateMonitor] 恢复窗口绝对超时（{Elapsed:F0}s），确认退出", absoluteElapsed);
            await ConfirmExitAsync("恢复窗口绝对超时（截图失败+SignalR断开持续超过120秒）");
            return;
        }

        // 如果正在重连，延长恢复窗口（需求 3.6）
        if (_client.IsReconnecting)
        {
            _logger.LogDebug("[WorldStateMonitor] 恢复窗口中，SignalR 正在重连，延长窗口");
            _recoveryWindowStart = DateTime.UtcNow;
            return;
        }

        // 如果传送抑制期激活，延长恢复窗口（需求 3.7）
        if (_isTeleportSuppressed)
        {
            _logger.LogDebug("[WorldStateMonitor] 恢复窗口中，传送抑制期激活，延长窗口");
            _recoveryWindowStart = DateTime.UtcNow;
            return;
        }

        var elapsed = (DateTime.UtcNow - _recoveryWindowStart).TotalSeconds;
        if (elapsed >= RecoveryWindowSeconds)
        {
            _logger.LogError("[WorldStateMonitor] 恢复窗口超时（{Elapsed:F0}s），确认退出", elapsed);
            await ConfirmExitAsync("恢复窗口超时（截图失败+SignalR断开持续30秒）");
        }
    }

    /// <summary>
    /// 回主页自愈尝试：调用 ReturnMainUiTask 尝试回到主界面（需求 3）。
    /// - 传入 _cts.Token 作为取消令牌（需求 3.1）。
    /// - OperationCanceledException：监测被取消，向上传播至 MonitorLoopAsync 的
    ///   catch(OperationCanceledException) 结束循环——禁止吞掉（需求 3.3）。
    /// - 其他 Exception：回主页尽力而为，失败不应让监测崩溃，记录 LogWarning 后继续，
    ///   由调用点决定是否继续退出判定（需求 3.2 / 3.4）。
    /// </summary>
    private async Task TryReturnMainUiForRecoveryAsync()
    {
        try
        {
            await new ReturnMainUiTask().Start(_cts.Token);
        }
        catch (OperationCanceledException)
        {
            // 监测取消信号：不吞，向上传播让 MonitorLoopAsync 正常结束（需求 3.3）。
            throw;
        }
        catch (Exception ex)
        {
            // 回主页失败（非取消）：尽力而为，记录后继续；不改计数（OQ-5），
            // 计数清零交由后续轮次 IsInMultiGame==true（需求 4.1）。不静默吞异常（需求 3.4）。
            _logger.LogWarning(ex, "[WorldStateMonitor] 回主页自愈尝试失败（{Count}/{Threshold}），继续后续判定",
                _connectedButNotInGame, ConnectedNotInGameThreshold);
        }
    }

    /// <summary>确认退出世界，触发协调停止回调。</summary>
    private async Task ConfirmExitAsync(string reason)
    {
        var isHost = !string.IsNullOrEmpty(_client.HostPlayerUid)
            && _client.HostPlayerUid == _playerUid;

        _logger.LogError("[WorldStateMonitor] 确认退出世界，原因: {Reason}，角色: {Role}",
            reason, isHost ? "房主" : "成员");

        if (OnExitConfirmed != null)
        {
            try { await OnExitConfirmed.Invoke(isHost, reason); }
            catch (Exception ex) { _logger.LogWarning(ex, "[WorldStateMonitor] OnExitConfirmed 回调异常"); }
        }

        Stop();
    }

    /// <summary>
    /// 掉出房间后指数退避重试加入：2 轮 × 3 次（立即 → 5s → 15s），轮间等待 30s。
    /// 重试期间如果 IsInMultiGame 变 false，中止重试转入退出世界流程。
    /// </summary>
    private async Task<bool> TryRejoinRoomAsync()
    {
        var joinDelays = new[] { 0, 5000, 15000 };

        for (int round = 1; round <= 2; round++)
        {
            if (round > 1)
            {
                _logger.LogInformation("[WorldStateMonitor] 重新加入房间第{Round}轮前等待30秒...", round);
                await Task.Delay(30000, _cts.Token);
            }

            for (int attempt = 0; attempt < joinDelays.Length; attempt++)
            {
                if (_cts.Token.IsCancellationRequested) return false;

                if (joinDelays[attempt] > 0)
                    await Task.Delay(joinDelays[attempt], _cts.Token);

                // 重试期间检查 IsInMultiGame
                try
                {
                    using var region = CaptureToRectArea();
                    // applyAuthoritativeCrossValidation: false —— 重试期"已掉出"判定只信纯视觉，
                    // 不被滞后协调器翻回 true。
                    var status = PartyAvatarSideIndexHelper.DetectedMultiGameStatus(
                        region, applyAuthoritativeCrossValidation: false);
                    if (!status.IsInMultiGame)
                    {
                        _logger.LogWarning("[WorldStateMonitor] 重试加入房间期间 IsInMultiGame 变 false，中止重试");
                        return false;
                    }
                }
                catch { /* 截图失败，继续尝试加入 */ }

                // 检查是否正在重连（避免与 OnConnectionClosed 竞态）
                if (_client.IsReconnecting)
                {
                    _logger.LogInformation("[WorldStateMonitor] 正在重连中，跳过本次加入尝试");
                    return false;
                }

                try
                {
                    _logger.LogInformation("[WorldStateMonitor] 重新加入房间尝试（第{Round}轮第{Attempt}次）",
                        round, attempt + 1);
                    var joined = await _client.RejoinCurrentRoomAsync();
                    if (joined)
                    {
                        _logger.LogInformation("[WorldStateMonitor] 重新加入房间成功（第{Round}轮第{Attempt}次）",
                            round, attempt + 1);
                        return true;
                    }
                    _logger.LogWarning("[WorldStateMonitor] 重新加入房间失败（第{Round}轮第{Attempt}次）",
                        round, attempt + 1);
                }
                catch (Exception ex)
                {
                    _logger.LogWarning(ex, "[WorldStateMonitor] 重新加入房间异常（第{Round}轮第{Attempt}次）",
                        round, attempt + 1);
                }
            }
        }

        return false;
    }
}
