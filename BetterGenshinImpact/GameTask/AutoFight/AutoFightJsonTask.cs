using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.Common.Job;
using OpenCvSharp;
using BetterGenshinImpact.GameTask.AutoPick.Assets;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;

namespace BetterGenshinImpact.GameTask.AutoFight;

public class AutoFightJsonTask : ISoloTask
{
    public string Name => "自动战斗(JSON策略)";

    private readonly AutoFightParam _taskParam;
    private readonly JsonCombatStrategy _strategy;
    private CancellationToken _ct;

    /// <summary>
    /// YOLO目标检测器（BgiWorld模型），用于战斗结束检测
    /// 当前未使用（战斗结束检测已委托到 AutoFightEndDetection），保留声明以与 TXT 策略保持一致
    /// 初始化条件：_taskParam.FightFinishDetectEnabled == true
    /// </summary>
    private readonly BgiYoloPredictor? _predictor;
    private DateTime _lastFightFlagTime = DateTime.Now;

    private readonly ReturnMainUiTask _returnMainUiTask = new();
    private readonly double _assetScale = TaskContext.Instance().SystemInfo.AssetScale;
    private readonly double _dpi = TaskContext.Instance().DpiScale;

    /// <summary>
    /// 当前队伍中的角色名集合（用于过滤动作节点）
    /// </summary>
    private HashSet<string> _teamCharacterNames = new(StringComparer.OrdinalIgnoreCase);

    // 日志防刷：1秒内同一动作名至多输出一次日志
    private string _lastLoggedActionName = "";
    private DateTime _lastLogTime = DateTime.MinValue;

    /// <summary>
    /// 展开后的优先级动作条目
    /// 每个 JsonAction 展开为 1+N 个条目（1个主条件 + N个 morePriorities）
    /// </summary>
    private class PrioritizedAction
    {
        public JsonAction Action { get; set; }
        public string Expression { get; set; }
        public int Priority { get; set; }
    }

    // 战斗点位
    public static WaypointForTrack? FightWaypoint
    {
        get => AutoFightTask.FightWaypoint;
        set => AutoFightTask.FightWaypoint = value;
    }

    private readonly TaskFightFinishDetectConfig _finishDetectConfig;

    public AutoFightJsonTask(AutoFightParam taskParam)
    {
        _taskParam = taskParam;
        _strategy = JsonCombatStrategyParser.ParseFile(_taskParam.CombatStrategyPath);

        if (_taskParam.FightFinishDetectEnabled)
        {
            _predictor = App.ServiceProvider.GetRequiredService<BgiOnnxFactory>().CreateYoloPredictor(BgiOnnxModel.BgiWorld);
        }

        _finishDetectConfig = new TaskFightFinishDetectConfig(_taskParam.FinishDetectConfig);
    }

    /// <summary>
    /// 获取战斗场景，带重试机制
    /// 最多重试 5 次，每次间隔 1 秒
    /// </summary>
    /// <param name="ct">取消令牌</param>
    /// <returns>初始化完成的战斗场景，失败返回 null</returns>
    public CombatScenes? GetCombatScenesWithRetry(CancellationToken ct = default)
    {
        const int maxRetries = 5;
        var retryDelayMs = 1000;

        for (int attempt = 1; attempt <= maxRetries; attempt++)
        {
            var combatScenes = new CombatScenes().InitializeTeam(CaptureToRectArea());
            if (combatScenes.CheckTeamInitialized())
            {
                return combatScenes;
            }

            if (attempt < maxRetries)
            {
                Thread.Sleep(retryDelayMs);
                ct.ThrowIfCancellationRequested();
            }
        }
        return null;
    }

    /// <summary>
    /// 启动自动战斗（JSON策略模式）
    /// </summary>
    /// <param name="ct">取消令牌</param>
    public async Task Start(CancellationToken ct)
    {
        _ct = ct;

        LogScreenResolution();
        var combatScenes = GetCombatScenesWithRetry();

        // C1 兜底：识别队伍角色失败，启用简化 EQA 循环
        if (combatScenes == null)
        {
            Logger.LogWarning("JSON 策略：识别队伍角色失败，启用 C1 简化 EQA 兜底循环");
            await RunC1FallbackLoopAsync(ct);
            return;
        }

        // 联机锄地双采样稳定性缓冲
        if (PathExecutor.CurrentMultiplayerCoordinator != null)
        {
            combatScenes = AutoFightTask.ApplyStabilityBuffer(combatScenes, ct);
        }

        // 收集当前队伍角色名
        foreach (var avatar in combatScenes.GetAvatars())
        {
            _teamCharacterNames.Add(avatar.Name);
        }
        Logger.LogInformation("JSON 策略：当前队伍角色：{Names}", string.Join(", ", _teamCharacterNames));

        // 过滤可用动作：Character 为空（通用）或在当前队伍中
        var filteredActions = _strategy.Actions
            .Where(a => string.IsNullOrEmpty(a.Character) || _teamCharacterNames.Contains(a.Character))
            .ToList();

        // 展开为优先级条目：每个动作产生 1个主条目 + N个 morePriorities 条目
        var validActions = new List<PrioritizedAction>();
        foreach (var action in filteredActions)
        {
            validActions.Add(new PrioritizedAction
            {
                Action = action,
                Expression = action.Condition.Expression,
                Priority = action.Index
            });

            foreach (var morePriority in action.MorePriorities)
            {
                validActions.Add(new PrioritizedAction
                {
                    Action = action,
                    Expression = morePriority.Expression,
                    Priority = morePriority.Priority
                });
            }
        }

        // 按优先级排序，相同优先级时原动作排在 morePriorities 之前（通过索引辅助排序）
        validActions = validActions
            .OrderBy(p => p.Priority)
            .ThenBy(p => p.Expression == p.Action.Condition.Expression ? 0 : 1)
            .ToList();

        // 无匹配角色的可用动作时注入 EQA 兜底（C2/C3）
        var hasCharacterAction = validActions.Any(p => !string.IsNullOrEmpty(p.Action.Character));
        if (!hasCharacterAction)
        {
            if (validActions.Count == 0)
            {
                Logger.LogWarning("JSON 策略：没有可用的动作节点，跳过战斗");
                return;
            }

            Logger.LogWarning("JSON 策略：无匹配角色的可用动作，注入 EQA 兜底动作");
            // 优先级1：check（since>1&&t>1）
            validActions.Add(new PrioritizedAction
            {
                Action = new JsonAction { Name = "兜底-check", Action = "check", Index = 1 },
                Expression = "since>1&&t>1",
                Priority = 1
            });
            // 优先级2：q（q-ready）
            validActions.Add(new PrioritizedAction
            {
                Action = new JsonAction { Name = "兜底-q", Action = "q", Index = 2 },
                Expression = "q-ready",
                Priority = 2
            });
            // 优先级3：e（e-ready&&since>5）
            validActions.Add(new PrioritizedAction
            {
                Action = new JsonAction { Name = "兜底-e", Action = "e", Index = 3 },
                Expression = "e-ready&&since>5",
                Priority = 3
            });
            // 优先级4：attack(1)（无条件）
            validActions.Add(new PrioritizedAction
            {
                Action = new JsonAction { Name = "兜底-attack(1)", Action = "attack(1)", Index = 4 },
                Expression = "",
                Priority = 4
            });
            // 重新排序
            validActions = validActions.OrderBy(p => p.Priority).ToList();
        }

        Logger.LogInformation("JSON 策略：共 {Total} 个动作，展开为 {Expanded} 个优先级条目",
            _strategy.Actions.Count, validActions.Count);

        // 新的取消token
        var cts2 = new CancellationTokenSource();
        ct.Register(cts2.Cancel);

        combatScenes.BeforeTask(cts2.Token);
        // 设置初始当前角色名（用于无 Character 字段的通用 action 回退）
        CombatScriptParser.CurrentAvatarName = combatScenes.GetAvatars().FirstOrDefault()?.Name ?? CombatScriptParser.CurrentAvatarName;
        TimeSpan fightTimeout = TimeSpan.FromSeconds(_taskParam.Timeout);
        Stopwatch timeoutStopwatch = Stopwatch.StartNew();

        AutoFightSeek.RotationCount = 0;
        AutoFightTask.FightStatusFlag = true;
        AutoFightTask.FightEndTotoly = false; // 重置遗留的战斗结束标志，避免 Avatar 操作短路
        AutoFightTask.FightEndFlag = false;

        // 初始化共享战斗配额结束状态
        _quorumVoted = false;
        _allFightDoneReceived = false;
        var coordinator = PathExecutor.CurrentMultiplayerCoordinator;
        var wp = AutoFightTask.FightWaypoint;
        var routeIndex = coordinator?.CurrentRouteIndex ?? 0;
        _currentFightSyncKey = wp == null
            ? $"{routeIndex}:0:0"
            : $"{routeIndex}:{wp.X:R}:{wp.Y:R}";

        // 订阅全队战斗结束广播
        if (coordinator?.Client != null)
        {
            coordinator.Client.AllFightDone += OnAllFightDone;
            // 上报战斗参与者
            _ = coordinator.ReportFightParticipantAsync(_currentFightSyncKey);
        }

        // 战斗开始后指定时间内不检测结束（EndModel + FightWaitNotEndTime）
        if (_finishDetectConfig.EndModel && _finishDetectConfig.FightWaitNotEndTime > 0)
        {
            _fightDurationExceeded = true;
            Task.Run(async () =>
            {
                await Task.Delay(_finishDetectConfig.FightWaitNotEndTime, cts2.Token);
                _fightDurationExceeded = false;
            }, cts2.Token);
        }
        else
        {
            _fightDurationExceeded = false;
        }

        // 基于经验值的战后拾取检测
        _staticFightEnded = false;
        if (_taskParam.ExpKazuhaPickup) FindExp(cts2.Token);

        var fightEndFlag = false;
        var timeOutFlag = false;
        string lastFightName = "";

        // 初始化条件求值器
        var evaluator = new ConditionEvaluator(combatScenes, () => CaptureToRectArea());

        // 战斗前动作
        await RunPreActions(combatScenes, evaluator);

        // 启动后台回点循环（TXT 一致：独立 Task.Run + 专用 CTS + 战斗结束 join）
        if (_taskParam.KazuhaContinuousReturn)
        {
            _returnLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cts2.Token);
            _kazuhaReturnLoopTask = Task.Run(() => KazuhaReturnLoopAsync(_returnLoopCts.Token), _returnLoopCts.Token);
        }
        else if (_taskParam.FinishDetectConfig.ReturnToFightPointEnabled)
        {
            var rotateFindEnemyEnabled = _taskParam.FinishDetectConfig.RotateFindEnemyEnabled;
            var timeTriggerEnabled = _taskParam.FinishDetectConfig.ReturnToFightPointTimeTriggerEnabled;
            var intervalMs = _taskParam.FinishDetectConfig.ReturnToFightPointIntervalMs;
            var triggerDistance = _taskParam.FinishDetectConfig.ReturnToFightPointTriggerDistance;
            var stopDistance = _taskParam.FinishDetectConfig.ReturnToFightPointStopDistance;
            var timeTriggerSeconds = _taskParam.FinishDetectConfig.ReturnToFightPointTimeTriggerSeconds;

            if (timeTriggerEnabled && !rotateFindEnemyEnabled)
            {
                Logger.LogWarning("[JSON][回点] 时间触发启用但旋转寻敌未启用，时间触发分支跳过；距离触发不受影响");
            }

            if (AutoFightSeekDecisions.IsReturnToFightPointConfigValid(
                    intervalMs, triggerDistance, stopDistance,
                    timeTriggerEnabled, rotateFindEnemyEnabled, timeTriggerSeconds))
            {
                _returnLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cts2.Token);
                _generalReturnLoopTask = Task.Run(() => GeneralReturnLoopAsync(
                    _returnLoopCts.Token, intervalMs, triggerDistance, stopDistance,
                    timeTriggerEnabled, timeTriggerSeconds, rotateFindEnemyEnabled), _returnLoopCts.Token);
            }
        }

        // 战斗操作
        var fightTask = Task.Run(async () =>
        {
            try
            {
                // 战斗开始首帧播种：使用战斗点坐标初始化 Navigation 锚点，
                // 避免沿用上一段移动残留的 stale 坐标导致首帧位置漂移
                var __fightWp = AutoFightTask.FightWaypoint;
                if (__fightWp is not null)
                {
                    var __seed = KazuhaCollectPositionGuardDecisions.ComputeSeedAnchor(__fightWp.X, __fightWp.Y);
                    Navigation.SetPrevPosition((float)__seed.X, (float)__seed.Y);
                }

                JsonAction? lastExecutedAction = null;

                while (!cts2.Token.IsCancellationRequested)
                {
                    if (timeoutStopwatch.Elapsed > fightTimeout)
                    {
                        Logger.LogInformation("战斗超时结束");
                        fightEndFlag = true;
                        timeOutFlag = true;
                        // 联机时通知外部模块战斗已结束（单机不串扰 TXT 的静态状态）
                        if (PathExecutor.CurrentMultiplayerCoordinator != null)
                        {
                            AutoFightTask.FightEndTotoly = true;
                        }
                        break;
                    }

                    // 每次循环开始：截图一次，供所有条件求值复用
                    using var capture = CaptureToRectArea();
                    evaluator.SetCachedCapture(capture);

                    var anyExecuted = false;

                    foreach (var prioritizedAction in validActions)
                        {
                            if (cts2.Token.IsCancellationRequested) break;

                            var action = prioritizedAction.Action;

                            // 求值条件表达式（使用展开后的表达式和优先级）
                            var conditionMet = evaluator.Evaluate(
                                prioritizedAction.Expression,
                                prioritizedAction.Priority,
                                action.Character);

                            if (!conditionMet)
                            {
                                continue;
                            }

                            // 指定角色的动作：执行前确保切换到该角色
                            if (!string.IsNullOrEmpty(action.Character))
                            {
                                var avatar = combatScenes.SelectAvatar(action.Character);
                                if (avatar == null) continue;

                                avatar.Switch();
                                CombatScriptParser.CurrentAvatarName = action.Character;
                            }

                            // 执行动作
                            await ExecuteAction(combatScenes, action);

                            // 确保E技能释放成功
                            if (action.EnsureCast)
                            {
                                var characterName = string.IsNullOrEmpty(action.Character)
                                    ? CombatScriptParser.CurrentAvatarName
                                    : action.Character;
                                var avatar = combatScenes.SelectAvatar(characterName);
                                if (avatar != null)
                                {
                                    var imageAfterAction = CaptureToRectArea();
                                    var retry = 5;
                                    while (!(await AutoFightSkill.AvatarSkillAsync(Logger, avatar, false, 1, _ct, imageAfterAction)) && retry > 0)
                                    {
                                        Logger.LogWarning("{Name} 未检测到技能冷却，重新执行", action.Name);
                                        // 防止在纳塔飞天或爬墙
                                        Simulation.ReleaseAllKey();
                                        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                        Simulation.SendInput.SimulateAction(GIActions.Drop);
                                        await Delay(200, _ct);
                                        // 重新执行整个动作
                                        await ExecuteAction(combatScenes, action);
                                        imageAfterAction = CaptureToRectArea();
                                        await Task.Delay(30, _ct);
                                        retry--;
                                    }
                                    imageAfterAction.Dispose();
                                }
                            }

                            evaluator.UpdateLastExecTime(prioritizedAction.Priority);
                            lastExecutedAction = action;
                            anyExecuted = true;
                            lastFightName = action.Character ?? "";

                            if (_fightEndFlag) break;

                            // 执行完第一个满足条件的动作后重新判断
                            break;
                        }

                    if (fightEndFlag || _fightEndFlag)
                    {
                        // 共享战斗配额：已投票但未收到全队广播时不退出，继续等待
                        if (_quorumVoted && !_allFightDoneReceived)
                        {
                            await Task.Delay(100, cts2.Token);
                            continue;
                        }
                        break;
                    }

                    if (!anyExecuted)
                    {
                        await Delay(200, _ct);
                    }
                }
            }
            catch (Exception e)
            {
                Debug.WriteLine(e.Message);
                Debug.WriteLine(e.StackTrace);
                throw;
            }
            finally
            {
                Simulation.ReleaseAllKey();
                AutoFightTask.FightEndFlag = true;
                AutoFightTask.FightStatusFlag = false;
                // 清理 AllFightDone 订阅
                var c = PathExecutor.CurrentMultiplayerCoordinator?.Client;
                if (c != null)
                {
                    c.AllFightDone -= OnAllFightDone;
                }
            }
        }, cts2.Token);

        await fightTask;

        // 战斗结束：cancel + join 后台回点循环（TXT 一致：先 cancel 再 join，3s 超时 + 异常日志）
        if (_kazuhaReturnLoopTask != null || _generalReturnLoopTask != null)
        {
            try { _returnLoopCts?.Cancel(); } catch { /* 忽略 */ }

            const int returnLoopJoinTimeoutMs = 3000;
            var __tasks = new List<Task>();
            if (_kazuhaReturnLoopTask != null) __tasks.Add(_kazuhaReturnLoopTask);
            if (_generalReturnLoopTask != null) __tasks.Add(_generalReturnLoopTask);
            try
            {
                var __all = Task.WhenAll(__tasks);
                var __winner = await Task.WhenAny(__all, Task.Delay(returnLoopJoinTimeoutMs));
                if (__winner != __all)
                {
                    Logger.LogWarning("[回点][join] 等待后台回点循环结束超时({Timeout}ms)，cancel 已发出，继续战后流程", returnLoopJoinTimeoutMs);
                }
                else if (__all.IsFaulted)
                {
                    Logger.LogWarning(__all.Exception, "[回点][join] 后台回点循环以异常结束，已忽略");
                }
            }
            catch (Exception ex)
            {
                Logger.LogWarning(ex, "[回点][join] join 后台回点循环异常，已忽略并继续");
            }
            finally
            {
                try { _returnLoopCts?.Dispose(); } catch { }
                _returnLoopCts = null;
                _kazuhaReturnLoopTask = null;
                _generalReturnLoopTask = null;
            }
        }

        // 战后拾取
        await PostFightPickup(combatScenes, timeOutFlag, lastFightName);
        _staticFightEnded = true;
    }

    private bool _fightEndFlag;
    // 万叶/通用回点后台任务（TXT 一致：独立 Task.Run + 专用 CTS + 战斗结束 join）
    private CancellationTokenSource? _returnLoopCts;
    private Task? _kazuhaReturnLoopTask;
    private Task? _generalReturnLoopTask;
    // === 共享战斗配额结束同步状态（multiplayer-shared-fight-end-quorum-sync spec）===
    private volatile bool _quorumVoted;
    private volatile bool _allFightDoneReceived;
    private string _currentFightSyncKey = "";
    /// <summary>战斗开始后指定时间内不检测结束（FightWaitNotEndTime）</summary>
    private volatile bool _fightDurationExceeded = false;
    /// <summary>JSON 战斗通用静态标志，用于 FindExp 等静态方法判断战斗是否已结束</summary>
    private static volatile bool _staticFightEnded = false;
    private static readonly object PickLock = new object();
    private static volatile bool _isExperiencePickup = false;

    /// <summary>执行单个 JSON 动作节点</summary>
    private async Task ExecuteAction(CombatScenes combatScenes, JsonAction action)
    {
        try
        {
            var character = string.IsNullOrEmpty(action.Character)
                ? CombatScriptParser.CurrentAvatarName
                : action.Character;

            var commands = CombatScriptParser.ParseLinePart(action.Action, character);

            // 执行前输出日志
            LogActionOnce(action.Name);

            CombatCommand? lastSubCmd = null;
            foreach (var cmd in commands)
            {
                if (_ct.IsCancellationRequested) break;

                cmd.Execute(combatScenes, lastSubCmd);
                lastSubCmd = cmd;

                if (_fightEndFlag) break;

                // 仅由 check 指令触发战斗结束检测
                if (cmd.Method == Method.Check && _taskParam.FightFinishDetectEnabled)
                {
                    if (_finishDetectConfig.RotationMode && _finishDetectConfig.RotateFindEnemyEnabled)
                    {
                        Task.Run(async () =>
                        {
                            _fightEndFlag = await AutoFightEndDetection.CheckFightEnd(
                                _finishDetectConfig,
                                _taskParam.RotaryFactor,
                                _taskParam.FinishDetectConfig.GoDistance,
                                _taskParam.KazuhaContinuousReturn,
                                _fightDurationExceeded,
                                _ct,
                                _ct,
                                null);
                            if (_fightEndFlag)
                            {
                                if (!TryCoordinateSharedFightEnd())
                                {
                                    // 配额系统启用且已投票，继续等待全队广播
                                    _fightEndFlag = false;
                                }
                                else
                                {
                                    Logger.LogInformation("{Name} 检测到战斗结束", action.Name);
                                }
                            }
                        }, _ct);
                    }
                    else
                    {
                        _fightEndFlag = await AutoFightEndDetection.CheckFightEnd(
                            _finishDetectConfig,
                            _taskParam.RotaryFactor,
                            _taskParam.FinishDetectConfig.GoDistance,
                            _taskParam.KazuhaContinuousReturn,
                            _fightDurationExceeded,
                            _ct,
                            _ct,
                            null);
                        if (_fightEndFlag)
                        {
                            if (!TryCoordinateSharedFightEnd())
                            {
                                // 配额系统启用且已投票，继续等待全队广播
                                _fightEndFlag = false;
                            }
                            else
                            {
                                Logger.LogInformation("{Name} 检测到战斗结束", action.Name);
                                break;
                            }
                        }
                    }
                }
            }

            // 更新当前角色名，供后续无指定角色动作使用
            CombatScriptParser.CurrentAvatarName = character;
        }
        catch (Exception e)
        {
            Logger.LogError("自动战斗：{Name} 执行失败：{Msg}", action.Name, e.Message);
        }
        finally
        {
            Simulation.ReleaseAllKey();
        }
    }

    /// <summary>
    /// 共享战斗配额结束协调：返回 true=应立即真结束；false=已投票，继续战斗循环等待 AllFightDone 或超时。
    /// </summary>
    private bool TryCoordinateSharedFightEnd()
    {
        var coordinator = PathExecutor.CurrentMultiplayerCoordinator;
        if (!SharedFightEndQuorumDecisions.IsEnabled(
                coordinator != null,
                coordinator?.IsConnected ?? false,
                coordinator?.EffectiveConfig.SharedFightEndQuorumEnabled ?? false))
        {
            return true;
        }

        if (_allFightDoneReceived) return true;

        if (!_quorumVoted)
        {
            _quorumVoted = true;
            _ = coordinator!.ReportFightDoneAsync(_currentFightSyncKey);
            Logger.LogInformation("[联机][结束配额] JSON 策略本地判定结束，已投票 done，继续战斗等待全队 syncKey={Key}", _currentFightSyncKey);
        }
        return false;
    }

    private void OnAllFightDone(string syncKey)
    {
        if (syncKey == _currentFightSyncKey)
        {
            _allFightDoneReceived = true;
            AutoFightTask.FightEndTotoly = true;
            Logger.LogInformation("[联机][结束配额] JSON 策略收到全队战斗结束广播 syncKey={Key}", syncKey);
        }
    }

    /// <summary>
    /// C1 兜底循环：队伍识别失败时启用简化 EQA 循环。
    /// 固定 E → Q → 普攻×3 → CheckFightFinish 轮询，不依赖角色识别和脚本。
    /// </summary>
    private async Task RunC1FallbackLoopAsync(CancellationToken ct)
    {
        Logger.LogWarning("[兜底][C1] 启用简化 EQA 兜底循环，超时 {Timeout}s", _taskParam.Timeout);

        AutoFightTask.FightStatusFlag = true;
        AutoFightTask.FightEndTotoly = false;
        AutoFightTask.FightEndFlag = false;

        var sw = Stopwatch.StartNew();
        var timeoutMs = (long)_taskParam.Timeout * 1000L;
        var lastCheckMs = -5000L;
        const int checkIntervalMs = 5000;

        try
        {
            while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested && !AutoFightTask.FightEndTotoly)
            {
                // E
                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                await Delay(200, ct);
                if (AutoFightTask.FightEndTotoly || ct.IsCancellationRequested) break;

                // Q
                Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                await Delay(300, ct);
                if (AutoFightTask.FightEndTotoly || ct.IsCancellationRequested) break;

                // 普攻 ×3
                for (var i = 0; i < 3; i++)
                {
                    if (AutoFightTask.FightEndTotoly || ct.IsCancellationRequested) break;
                    Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                    await Delay(250, ct);
                }

                // 周期性检测战斗结束
                if (sw.ElapsedMilliseconds - lastCheckMs >= checkIntervalMs)
                {
                    lastCheckMs = sw.ElapsedMilliseconds;
                    try
                    {
                        var finished = await AutoFightEndDetection.CheckFightEnd(
                            _finishDetectConfig, _taskParam.RotaryFactor,
                            _taskParam.FinishDetectConfig.GoDistance,
                            _taskParam.KazuhaContinuousReturn,
                            false, ct, ct, null);
                        if (finished)
                        {
                            Logger.LogInformation("[兜底][C1] CheckFightFinish 检测到战斗结束，提前退出");
                            break;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "[兜底][C1] CheckFightFinish 异常，忽略并继续");
                    }
                }
            }
        }
        finally
        {
            Simulation.ReleaseAllKey();
            AutoFightTask.FightStatusFlag = false;
            AutoFightTask.FightEndFlag = true;
            AutoFightTask.FightEndTotoly = true;
            Logger.LogInformation("[兜底][C1] 简化兜底结束，耗时 {Elapsed:F1}s", sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// 联机万叶战斗中回点后台循环（TXT 一致：独立 Task.Run + 循环内 Task.Delay + 测距触发）。
    /// 每 1s 检查一次玩家位置到 FightWaypoint 的实时距离，
    /// 距离 > 1.0 时触发一次 MoveCloseTo/MoveTo 拉回到战斗点。
    /// 由 _returnLoopCts 统一控制取消（战斗结束 / FightEndTotoly / 外部取消时停止）。
    /// </summary>
    private async Task KazuhaReturnLoopAsync(CancellationToken token)
    {
        const int intervalMs = 1000;
        const double threshold = 1.0;
        var lastReturnAt = DateTime.MinValue;

        Logger.LogInformation("[JSON][万叶] 持续回点后台任务已启动 (interval={Interval}ms, threshold={Threshold:F1})",
            intervalMs, threshold);

        try
        {
            // 回点首帧播种：使用战斗点坐标初始化 Navigation 锚点
            var __returnWp = AutoFightTask.FightWaypoint;
            if (__returnWp is not null)
            {
                var __seed = KazuhaCollectPositionGuardDecisions.ComputeSeedAnchor(__returnWp.X, __returnWp.Y);
                Navigation.SetPrevPosition((float)__seed.X, (float)__seed.Y);
            }

            while (!token.IsCancellationRequested && !AutoFightTask.FightEndTotoly)
            {
                try
                {
                    await Task.Delay(intervalMs, token);
                }
                catch (OperationCanceledException) { return; }

                if (AutoFightTask.FightEndTotoly || token.IsCancellationRequested) return;

                var fightWaypoint = AutoFightTask.FightWaypoint;
                if (fightWaypoint is null) continue;

                var elapsedSinceLastReturn = (DateTime.UtcNow - lastReturnAt).TotalMilliseconds;
                if (elapsedSinceLastReturn < intervalMs) continue;

                Point2f currentPos;
                try
                {
                    using var image = CaptureToRectArea();
                    currentPos = Navigation.GetPosition(image, fightWaypoint.MapName, fightWaypoint.MapMatchMethod);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "[JSON][万叶] 持续回点位置识别失败，本轮跳过");
                    continue;
                }

                if (currentPos is { X: 0, Y: 0 }) continue;

                // KazuhaReturnReseedGuard：异常坐标重播种 + 重识别有限重试（TXT 一致）
                var __hoeingCfg = TaskContext.Instance().Config.AutoHoeingConfig;
                var __guardResult = await KazuhaReturnReseedGuard.EvaluateAndReseedAsync(
                    currentPos, fightWaypoint.X, fightWaypoint.Y,
                    __hoeingCfg.KazuhaReturnAbnormalCoordThreshold,
                    __hoeingCfg.KazuhaReturnReseedRetryCount,
                    __hoeingCfg.KazuhaReturnZeroCoordStableRetryCount,
                    reseedAnchor: () => Navigation.SetPrevPosition((float)fightWaypoint.X, (float)fightWaypoint.Y),
                    reSample: () =>
                    {
                        using var img = CaptureToRectArea();
                        return Navigation.GetPosition(img, fightWaypoint.MapName, fightWaypoint.MapMatchMethod);
                    },
                    reSampleStable: () =>
                    {
                        using var img = CaptureToRectArea();
                        return Navigation.GetPositionStable(img, fightWaypoint.MapName, fightWaypoint.MapMatchMethod);
                    },
                    delay: t => Task.Delay(KazuhaReturnReseedGuard.ReseedReSampleDelayMs, t),
                    // 画面稳定门控：重识别前先派蒙检测。CaptureToRectArea + Bv.IsInMainUi。
                    isScreenStable: () =>
                    {
                        using var ra = CaptureToRectArea();
                        return Bv.IsInMainUi(ra);
                    },
                    screenStablePollDelay: t => Task.Delay(KazuhaReturnReseedGuard.ScreenStablePollIntervalMs, t),
                    log: m => Logger.LogInformation("[JSON][万叶] 持续回点{Msg}", m),
                    ct: token);

                if (!__guardResult.ShouldMove)
                {
                    Logger.LogDebug(
                        "[JSON][万叶] 持续回点：重播种+重识别 {Retry} 次仍异常，本轮放弃",
                        __guardResult.RetryUsed);
                    continue;
                }
                currentPos = __guardResult.TrustedPos;

                var realtimeDistance = Navigation.GetDistance(fightWaypoint, currentPos);
                if (!AutoFightSeekDecisions.ShouldTriggerContinuousReturn(
                        realtimeDistance, threshold,
                        elapsedSinceLastReturn, intervalMs))
                {
                    continue;
                }

                // 复苏/神像传送进行中：终止本场回点循环
                if (AutoFightSeekDecisions.ShouldStopReturnForTeleport(AutoFightTask.IsTeleportingToStatue))
                {
                    Logger.LogDebug("[JSON][万叶] 复苏/神像传送进行中，终止本场回点循环（距战斗点 {Dist:F1}）", realtimeDistance);
                    return;
                }

                Logger.LogInformation("[JSON][万叶] 持续回点：距战斗点 {Dist:F1}，触发回点", realtimeDistance);

                try
                {
                    fightWaypoint.MoveMode = MoveModeEnum.Walk.Code;
                    var pathExecutor = new PathExecutor(token);

                    if (realtimeDistance > 4.0)
                    {
                        // MoveTo 真寻路 + endWatcher 打断 + 回点状态标记（TXT 一致）
                        using var moveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        using var endWatcher = new CancellationTokenSource();
                        var watcherTask = Task.Run(async () =>
                        {
                            while (!endWatcher.Token.IsCancellationRequested)
                            {
                                if (AutoFightTask.FightEndTotoly)
                                {
                                    try { moveCts.Cancel(); } catch { }
                                    return;
                                }
                                try { await Task.Delay(100, endWatcher.Token); } catch { return; }
                            }
                        }, endWatcher.Token);

                        AutoFightTask.EnterReturnToFightPoint();
                        try
                        {
                            var movePathExecutor = new PathExecutor(moveCts.Token);
                            await movePathExecutor.MoveTo(fightWaypoint, isGetOut: false);
                        }
                        finally
                        {
                            AutoFightTask.ExitReturnToFightPoint();
                            endWatcher.Cancel();
                            try { await watcherTask; } catch { }
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                        }
                    }
                    else
                    {
                        AutoFightTask.EnterReturnToFightPoint();
                        try
                        {
                            await pathExecutor.MoveCloseTo(fightWaypoint);
                        }
                        finally
                        {
                            AutoFightTask.ExitReturnToFightPoint();
                        }
                    }
                    lastReturnAt = DateTime.UtcNow;
                }
                catch (OperationCanceledException) { return; }
                catch (BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException) { return; }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[JSON][万叶] 持续回点移动异常，本轮跳过");
                }
            }
        }
        finally
        {
            Logger.LogDebug("[JSON][万叶] 持续回点后台任务已退出");
        }
    }

    /// <summary>
    /// 通用版战斗中回点后台循环（TXT 一致：独立 Task.Run + 距离/时间双触发 + 距离容差 + BC1 护栏 + endWatcher 打断）。
    /// 与 fightTask 主循环并行，由 _returnLoopCts 统一控制取消（战斗结束 / FightEndTotoly / 外部取消时停止）。
    ///
    /// 每轮先后判定两个触发器（任一满足即触发同一份 MoveTo）：
    ///   1. 距离触发：realtimeDistance > triggerDistance（连续命中 2 次容差才触发）
    ///   2. 时间触发：UtcNow - LastEnemySeenAt > timeTriggerSeconds（需 timeTriggerEnabled && rotateFindEnemyEnabled）
    /// </summary>
    private async Task GeneralReturnLoopAsync(
        CancellationToken token,
        int intervalMs,
        double triggerDistance,
        double stopDistance,
        bool timeTriggerEnabled,
        int timeTriggerSeconds,
        bool rotateFindEnemyEnabled)
    {
        const int distanceTolerance = 2;
        var lastReturnAt = DateTime.MinValue;
        int triggerHitCount = 0;

        Logger.LogInformation("[JSON][回点] 通用版后台任务已启动 (interval={Interval}ms, trigger={Trigger:F1}, stop={Stop:F1}, timeTrigger={TimeEnabled} {TimeSec}s)",
            intervalMs, triggerDistance, stopDistance,
            timeTriggerEnabled && rotateFindEnemyEnabled, timeTriggerSeconds);

        try
        {
            // 回点首帧播种
            var __returnWp = AutoFightTask.FightWaypoint;
            if (__returnWp is not null)
            {
                var __seed = KazuhaCollectPositionGuardDecisions.ComputeSeedAnchor(__returnWp.X, __returnWp.Y);
                Navigation.SetPrevPosition((float)__seed.X, (float)__seed.Y);
            }

            while (!token.IsCancellationRequested && !AutoFightTask.FightEndTotoly)
            {
                try
                {
                    await Task.Delay(intervalMs, token);
                }
                catch (OperationCanceledException) { return; }

                if (AutoFightTask.FightEndTotoly || token.IsCancellationRequested) return;

                var fightWaypoint = AutoFightTask.FightWaypoint;
                if (fightWaypoint is null) continue;

                var elapsedSinceLastReturnMs = (DateTime.UtcNow - lastReturnAt).TotalMilliseconds;

                // 派蒙可见性校验（战斗界面遮挡时跳过）
                using var screen = CaptureToRectArea();
                if (!Bv.IsInMainUi(screen))
                {
                    continue;
                }

                Point2f currentPos;
                try
                {
                    currentPos = Navigation.GetPosition(screen, fightWaypoint.MapName, fightWaypoint.MapMatchMethod);
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "[JSON][回点] 位置识别失败，本轮跳过");
                    continue;
                }

                if (currentPos is { X: 0, Y: 0 }) continue;

                // BC1 护栏复核：异常远点拒绝，不污染 triggerHitCount
                var __guardSeed = KazuhaCollectPositionGuardDecisions.ComputeSeedAnchor(fightWaypoint.X, fightWaypoint.Y);
                if (!KazuhaCollectPositionGuardDecisions.IsRecognizedPositionTrustworthy(
                        currentPos, __guardSeed.X, __guardSeed.Y,
                        KazuhaCollectPositionGuardDecisions.RecognizedPositionGuardThreshold))
                {
                    var __guardDist = Navigation.GetDistance(fightWaypoint, currentPos);
                    Logger.LogDebug("[JSON][回点] 识别坐标距战斗点 {Dist:F1} > {Threshold:F1}，疑似异常远点，本轮拒绝",
                        __guardDist, KazuhaCollectPositionGuardDecisions.RecognizedPositionGuardThreshold);
                    continue;
                }

                var realtimeDistance = Navigation.GetDistance(fightWaypoint, currentPos);

                // 距离触发容差（连续命中 2 次才触发）
                bool distanceTriggered = false;
                if (AutoFightSeekDecisions.ShouldTriggerGeneralDistanceReturn(
                        realtimeDistance, triggerDistance, elapsedSinceLastReturnMs, intervalMs))
                {
                    triggerHitCount++;
                    if (triggerHitCount >= distanceTolerance)
                    {
                        distanceTriggered = true;
                    }
                    else
                    {
                        Logger.LogDebug("[JSON][回点] 距离 {Dist:F1} > {Trigger:F1} 命中 {Hit}/{Tol}，等待二次确认",
                            realtimeDistance, triggerDistance, triggerHitCount, distanceTolerance);
                    }
                }
                else
                {
                    triggerHitCount = 0;
                }

                // 时间触发（仅在距离触发未命中时判定）
                bool timeTriggered = false;
                double elapsedSinceEnemySec = 0;
                if (!distanceTriggered)
                {
                    elapsedSinceEnemySec = (DateTime.UtcNow - AutoFightTask.LastEnemySeenAt).TotalSeconds;
                    timeTriggered = AutoFightSeekDecisions.ShouldTriggerTimeReturn(
                        elapsedSinceEnemySec, timeTriggerSeconds, elapsedSinceLastReturnMs, intervalMs,
                        timeTriggerEnabled, rotateFindEnemyEnabled);
                }

                if (!distanceTriggered && !timeTriggered) continue;

                // 复苏/神像传送进行中：终止本场回点循环
                if (AutoFightSeekDecisions.ShouldStopReturnForTeleport(AutoFightTask.IsTeleportingToStatue))
                {
                    Logger.LogDebug("[JSON][回点] 复苏/神像传送进行中，终止本场回点循环（距战斗点 {Dist:F1}）", realtimeDistance);
                    return;
                }

                triggerHitCount = 0;

                try
                {
                    fightWaypoint.MoveMode = MoveModeEnum.Walk.Code;
                    if (distanceTriggered)
                    {
                        Logger.LogInformation("[JSON][回点] 距战斗点 {Dist:F1} > {Trigger:F1}，触发 MoveTo (stop: {Stop:F1})",
                            realtimeDistance, triggerDistance, stopDistance);
                    }
                    else
                    {
                        Logger.LogInformation("[JSON][回点] 时间触发：{ElapsedSec:F1}s 未发现敌人 > {TimeSec}s，触发 MoveTo (stop: {Stop:F1})",
                            elapsedSinceEnemySec, timeTriggerSeconds, stopDistance);
                    }

                    // endWatcher：轮询 FightEndTotoly 打断进行中的 MoveTo
                    using var moveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    using var endWatcher = new CancellationTokenSource();
                    var watcherTask = Task.Run(async () =>
                    {
                        while (!endWatcher.Token.IsCancellationRequested)
                        {
                            if (AutoFightTask.FightEndTotoly)
                            {
                                try { moveCts.Cancel(); } catch { }
                                return;
                            }
                            try { await Task.Delay(100, endWatcher.Token); } catch { return; }
                        }
                    }, endWatcher.Token);

                    AutoFightTask.EnterReturnToFightPoint();
                    try
                    {
                        var movePathExecutor = new PathExecutor(moveCts.Token);
                        await movePathExecutor.MoveTo(fightWaypoint,
                            isGetOut: false, task: null, nextWaypoint: null, nextDistance: null,
                            retryDis: 4, isPoint: false, closeDistance: stopDistance);
                    }
                    finally
                    {
                        AutoFightTask.ExitReturnToFightPoint();
                        endWatcher.Cancel();
                        try { await watcherTask; } catch { }
                        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                    }

                    lastReturnAt = DateTime.UtcNow;
                    AutoFightTask.LastEnemySeenAt = DateTime.UtcNow;
                }
                catch (OperationCanceledException) { return; }
                catch (BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException) { return; }
                catch (Exception ex)
                {
                    Logger.LogError(ex, "[JSON][回点] MoveTo 异常，本轮跳过");
                }
            }
        }
        finally
        {
            Logger.LogDebug("[JSON][回点] 通用版回点后台任务已退出");
        }
    }

    /// <summary>战斗结束检测</summary>
    private async Task<bool> CheckFightFinish(int delayTime = 1500, int detectDelayTime = 450)
    {
        return await AutoFightEndDetection.CheckFightEnd(
            _finishDetectConfig,
            _taskParam.RotaryFactor,
            _taskParam.FinishDetectConfig.GoDistance,
            _taskParam.KazuhaContinuousReturn,
            _fightDurationExceeded,
            _ct,
            _ct,
            null);
    }



    /// <summary>日志防刷：同一动作名在1秒内至多输出一次日志</summary>
    private void LogActionOnce(string actionName)
    {
        if (actionName == _lastLoggedActionName && (DateTime.Now - _lastLogTime).TotalSeconds < 1)
        {
            return;
        }
        _lastLoggedActionName = actionName;
        _lastLogTime = DateTime.Now;
        Logger.LogInformation("自动战斗：{Name}", actionName);
    }

    /// <summary>执行战斗前动作</summary>
    private async Task RunPreActions(CombatScenes combatScenes, ConditionEvaluator evaluator)
    {
        if (_strategy.Info.PreActions == null || _strategy.Info.PreActions.Count == 0)
            return;

        Logger.LogInformation("JSON 策略：执行战斗前动作");
        using var capture = CaptureToRectArea();
        evaluator.SetCachedCapture(capture);

        foreach (var preAction in _strategy.Info.PreActions)
        {
            if (_ct.IsCancellationRequested) break;

            try
            {
                var firstSpaceIndex = preAction.IndexOf(' ');
                var character = CombatScriptParser.CurrentAvatarName;
                var commands = preAction;
                if (firstSpaceIndex > 0)
                {
                    character = preAction[..firstSpaceIndex];
                    commands = preAction[(firstSpaceIndex + 1)..];
                }

                var cmdList = CombatScriptParser.ParseLineCommands(commands, character);
                foreach (var cmd in cmdList)
                {
                    if (_ct.IsCancellationRequested) break;
                    cmd.Execute(combatScenes);
                    await Delay(300, _ct);
                }

                Logger.LogInformation("战斗前动作：{Action}", preAction);
            }
            catch (Exception e)
            {
                Logger.LogWarning("战斗前动作执行失败：{Action}，{Msg}", preAction, e.Message);
            }
        }
    }

    /// <summary>战后拾取</summary>
    private async Task PostFightPickup(CombatScenes combatScenes, bool timeOutFlag, string lastFightName)
    {
        // 基于经验值检测结果的拾取判断
        if (_taskParam.KazuhaPickupEnabled && _taskParam.ExpKazuhaPickup && !_isExperiencePickup 
            && (combatScenes.GetAvatars().Select(a => a.Name).Contains("枫原万叶") || combatScenes.GetAvatars().Select(a => a.Name).Contains("琴")))
        {
            Logger.LogInformation("基于经验值判断：等待经验值检测结果");
            var waitMs = _taskParam.FinishDetectConfig.RotationMode && _taskParam.FinishDetectConfig.RotateFindEnemyEnabled ? 1800 : 1000;
            while (!_isExperiencePickup && waitMs > 0)
            {
                await Delay(100, _ct);
                waitMs -= 100;
            }
        }

        var shouldPickup = !_taskParam.ExpKazuhaPickup || _isExperiencePickup;
        if (_taskParam.ExpKazuhaPickup)
        {
            Logger.LogInformation("基于经验值判断：{Result} 战后拾取", shouldPickup ? "执行" : "不执行");
        }

        if (_taskParam.KazuhaPickupEnabled && shouldPickup)
        {
            var picker = combatScenes.SelectAvatar("枫原万叶") ?? combatScenes.SelectAvatar("琴");

            string? oldPartyName = null;
            if (RunnerContext.Instance.PartyName is not null)
            {
                oldPartyName = RunnerContext.Instance.PartyName;
            }
            else if (picker is null && !string.IsNullOrEmpty(_taskParam.KazuhaPartyName))
            {
                Logger.LogWarning("换队拾取：当前队伍名称为空，尝试读取！");
                await Delay(1000, _ct);
                await _returnMainUiTask.Start(_ct);

                for (int attempt = 0; attempt < 6; attempt++)
                {
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    var enterGameAppear = await NewRetry.WaitForElementAppear(
                        ElementAssets.Instance.PartyBtnChooseView,
                        () => { },
                        _ct,
                        15,
                        500
                    );
                    if (attempt == 5 && !enterGameAppear)
                    {
                        Logger.LogWarning("换队拾取：读取队伍名称失败，跳过换队拾取步骤");
                        return;
                    }
                }
            }

            if (!string.IsNullOrEmpty(_taskParam.KazuhaPartyName))
            {
                await Delay(1000, _ct);

                var timeWaitStart = 0;
                while (timeWaitStart < 6000)
                {
                    using var ra = CaptureToRectArea();
                    var partyViewBtn = ra.Find(ElementAssets.Instance.PartyBtnChooseView);
                    if (partyViewBtn.IsExist())
                    {
                        var rawPartyName = ra.Find(new RecognitionObject
                        {
                            RecognitionType = RecognitionTypes.Ocr,
                            RegionOfInterest = new Rect(partyViewBtn.Right, partyViewBtn.Top, (int)(350 * _assetScale),
                                partyViewBtn.Height)
                        }).Text;

                        if (string.IsNullOrWhiteSpace(rawPartyName))
                        {
                            oldPartyName = string.Empty;
                        }
                        else
                        {
                            var tempName = rawPartyName
                                .Replace("\"", "")
                                .Replace("\r\n", "")
                                .Replace("\r", "");

                            int firstNewLineIndex = tempName.IndexOf('\n');
                            if (firstNewLineIndex != -1)
                            {
                                tempName = tempName.Substring(0, firstNewLineIndex);
                            }

                            oldPartyName = tempName.Trim();
                        }

                        Logger.LogInformation("换队拾取：当前队伍名称读取为：{oldPartyName}", oldPartyName);
                        Logger.LogDebug("OCR原始识别文本（含转义）：{rawPartyName}", rawPartyName);
                        RunnerContext.Instance.PartyName = oldPartyName;
                        break;
                    }
                    await Delay(200, _ct);
                    timeWaitStart += 200;
                }
            }

            var switchPartyFlag = false;
            if (picker == null && !timeOutFlag && !string.IsNullOrEmpty(_taskParam.KazuhaPartyName) && oldPartyName != _taskParam.KazuhaPartyName)
            {
                try
                {
                    Logger.LogInformation($"切换为拾取队伍：{_taskParam.KazuhaPartyName}");
                    var success = await new SwitchPartyTask().Start(_taskParam.KazuhaPartyName, _ct);
                    if (success)
                    {
                        Logger.LogInformation($"成功切换队伍为{_taskParam.KazuhaPartyName}");
                        switchPartyFlag = true;
                        RunnerContext.Instance.PartyName = _taskParam.KazuhaPartyName;
                        RunnerContext.Instance.ClearCombatScenes();
                        var cs = await RunnerContext.Instance.GetCombatScenes(_ct);
                        picker = cs.SelectAvatar("枫原万叶") ?? cs.SelectAvatar("琴");
                    }
                }
                catch (Exception e)
                {
                    Logger.LogWarning("切换队伍异常，跳过此步骤！{Msg}", e.Message);
                }
            }

            if (picker != null)
            {
                if (picker.Name == "枫原万叶")
                {
                    var time = TimeSpan.FromSeconds(picker.GetSkillCdSeconds());

                    bool shouldSkip = lastFightName == picker.Name && time.TotalSeconds > 3;
                    bool forcePickup = _taskParam.QinDoublePickUp;

                    if (forcePickup || !shouldSkip)
                    {
                        Logger.LogInformation("使用 枫原万叶-长E 拾取掉落物");
                        await Delay(200, _ct);
                        if (picker.TrySwitch(10))
                        {
                            await picker.WaitSkillCd(_ct);
                            await SimulateHoldElementalSkillAsync(800, _ct);
                            await SimulateMouseLeftClickLoopAsync(6, _ct);
                            await Delay(1500, _ct);
                            picker.AfterUseSkill();
                        }
                    }
                    else
                    {
                        Logger.LogInformation("距最近一次万叶出招，时间过短，跳过此次万叶拾取！");
                    }
                }
                else if (picker.Name == "琴")
                {
                    Logger.LogInformation("使用 琴-长E 拾取掉落物");

                    var actionsToUse = PickUpCollectHandler.PickUpActions
                        .Where(action => action.StartsWith("琴-长E" + " ", StringComparison.OrdinalIgnoreCase))
                        .Select(action => action.Replace("琴-长E", "琴", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    var find = _taskParam.QinDoublePickUp;
                    await Delay(150, _ct);
                    if (picker.TrySwitch(10))
                    {
                        foreach (var miningActionStr in actionsToUse)
                        {
                            var pickUpAction = CombatScriptParser.ParseContext(miningActionStr);

                            for (int i = 0; i < 2; i++)
                            {
                                await picker.WaitSkillCd(_ct);
                                foreach (var command in pickUpAction.CombatCommands)
                                {
                                    command.Execute(combatScenes);
                                    Task.Run(() =>
                                    {
                                        if (Monitor.TryEnter(PickLock))
                                        {
                                            try
                                            {
                                                if (find)
                                                {
                                                    using (var imagePick = CaptureToRectArea())
                                                    {
                                                        if (imagePick.Find(AutoPickAssets.Instance.PickRo).IsExist())
                                                        {
                                                            find = false;
                                                        }
                                                    }
                                                }
                                            }
                                            finally
                                            {
                                                Monitor.Exit(PickLock);
                                            }
                                        }
                                    });
                                }

                                if (!find)
                                {
                                    break;
                                }

                                if (i == 0)
                                {
                                    Logger.LogInformation("自动拾取；尝试再次执行 琴-长E 拾取");
                                    picker.AfterUseSkill();
                                }
                                else
                                {
                                    break;
                                }
                            }

                            Simulation.ReleaseAllKey();
                        }
                    }
                }
            }

            if (switchPartyFlag && !string.IsNullOrEmpty(oldPartyName))
            {
                try
                {
                    Logger.LogInformation($"切换为原队伍：{oldPartyName}");
                    var success = await new SwitchPartyTask().Start(oldPartyName, _ct);
                    if (success)
                    {
                        Logger.LogInformation($"切换为原队伍{oldPartyName}");
                        switchPartyFlag = true;
                        RunnerContext.Instance.PartyName = oldPartyName;
                        RunnerContext.Instance.ClearCombatScenes();
                        await RunnerContext.Instance.GetCombatScenes(_ct);
                    }
                }
                catch (Exception e)
                {
                    Logger.LogWarning("恢复原队伍失败，跳过此步骤！{Msg}", e.Message);
                }
            }
        }

        if (_taskParam is { PickDropsAfterFightEnabled: true })
        {
            await new ScanPickTask().Start(_ct);
        }
    }

    /// <summary>
    /// 检查并记录屏幕分辨率
    /// </summary>
    private void LogScreenResolution()
    {
        AssertUtils.CheckGameResolution("自动战斗");
    }

    /// <summary>
    /// 后台检测经验值数字出现
    /// </summary>
    private static Task FindExp(CancellationToken cts2)
    {
        return Task.Run(() =>
        {
            try
            {
                _isExperiencePickup = false;
                var autoFightAssets = AutoFightAssets.Instance;
                var experienceRas = new[]
                {
                   autoFightAssets.InitializeRecognitionObject(60),
                   autoFightAssets.InitializeRecognitionObject(58),
                   autoFightAssets.InitializeRecognitionObject(57),
                };

                while (!_isExperiencePickup && !_staticFightEnded && !cts2.IsCancellationRequested)
                {
                    try
                    {
                        cts2.ThrowIfCancellationRequested();

                        NewRetry.WaitForAction(() =>
                         {
                             using (var ra = CaptureToRectArea())
                             {
                                 _isExperiencePickup = experienceRas.Any(experienceRa =>
                                 {
                                     using var isExist = ra.Find(experienceRa);
                                     if (!isExist.IsExist())
                                     {
                                         return false;
                                     }

                                     var pixelValue1 = ra.SrcMat.At<Vec3b>(isExist.Y, isExist.X - 147);
                                     var expLogo = pixelValue1[0] == 253 && pixelValue1[1] == 247 && pixelValue1[2] == 172;

                                     return expLogo;
                                 });
                             }
                             return _isExperiencePickup;
                         }, cts2, 1, 100).Wait();
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Debug.WriteLine(ex.Message);
                    }

                    if (!_isExperiencePickup && !_staticFightEnded)
                    {
                        Thread.Sleep(200);
                    }
                }
            }
            catch (Exception ex)
            {
                Logger.LogDebug("经验值检测任务异常结束：{Msg}", ex.Message);
            }
        }, cts2);
    }
}
