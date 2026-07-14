using BetterGenshinImpact.Core.Recognition.ONNX;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.GameTask.AutoFight.Model;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.Model.Area;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Recognition;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.Common.Job;
using OpenCvSharp;
using BetterGenshinImpact.Helpers;
using Vanara;
using Microsoft.Extensions.DependencyInjection;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.AutoFight.Assets;
using BetterGenshinImpact.View.Drawable;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using Vanara.PInvoke;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoPick.Assets;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoPathing.Model.Enum;
using System.Text.RegularExpressions;
using BetterGenshinImpact.Core.Script.Dependence;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoHoeing.Multiplayer;

namespace BetterGenshinImpact.GameTask.AutoFight;

public class AutoFightTask : ISoloTask
{
    public string Name => "自动战斗";

    private readonly AutoFightParam _taskParam;

    private readonly CombatScriptBag _combatScriptBag;
    
    private readonly CombatScriptBag _combatScriptBagSecond;

    private CancellationToken _ct;

    private readonly BgiYoloPredictor _predictor;

    private DateTime _lastFightFlagTime = DateTime.UtcNow; // 战斗标志最近一次出现的时间

    private readonly double _dpi = TaskContext.Instance().DpiScale;

    private static OtherConfig Config { get; set; } = TaskContext.Instance().Config.OtherConfig;
    
    private static AutoFightConfig FightConfig { get; set; } = TaskContext.Instance().Config.AutoFightConfig;
    
    public static bool FightStatusFlag = false;
    
    public static int SwitchTryCount = 0;
    
    public static volatile  bool FightEndFlag = false;
    
    private static volatile bool _isExperiencePickup = false;

    public static bool IsTpForRecover {get; set;} = false;
    
    public static volatile  bool FightEndTotoly = false;

    // 战斗点位
    public static WaypointForTrack? FightWaypoint  {get; set;} = null;

    /// <summary>
    /// "正在前往七天神像传送回血"标志（return-to-point-suspend-during-revival-teleport spec）。
    /// 语义：true = 复苏/神像传送进行中，两条回点后台循环应 return 终止本场回点循环
    ///   （终止 ≠ 暂停一轮——传送后角色必定不回战斗点，本循环已无意义，直接终止最干净；
    ///    回点能力由下一场战斗重新启动的新循环恢复）。
    ///
    /// 写者（唯一）：PathExecutor.TpStatueOfTheSeven 的 try-finally —— 入口置 true，finally 复位 false，
    ///   保证任何退出路径（正常 / RetryException / 取消）都复位，不会永久悬挂。
    /// 读者：KazuhaContinuousReturnLoopAsync（MoveCloseTo 前）/ GeneralReturnToFightPointLoopAsync（MoveTo 前），
    ///   命中即 return 终止本场回点循环。
    /// 跨线程：PathExecutor 传送线程写、两个回点后台循环线程读，单调置位/复位无复合判断，volatile 足够。
    ///
    /// 严禁与以下信号混用 / 合并 / 重命名（语义完全不同）：
    ///   - TpTask.SuppressAutoRevivalClick（抑制 AnomalyDetector 自动点击复苏）
    ///   - PathExecutor._multiplayerRevivalDetected（待消费的复苏事件信号位）
    ///   - IsSuspend / IsSuspendedByCapture（用户主动暂停 / 截图暂停）
    /// </summary>
    public static volatile bool IsTeleportingToStatue = false;

    /// <summary>
    /// "战斗中回点移动进行中"引用计数（fight-return-to-point-seek-rotation-conflict-fix spec）。
    /// 唯一语义：当前有至少一个回点发起方正在执行移动/回点（count &gt; 0）。
    ///
    /// 写者（成对）：三个回点发起方在真正执行移动前后调用 EnterReturnToFightPoint() / ExitReturnToFightPoint()：
    ///   - B：KazuhaContinuousReturnLoopAsync（MoveCloseTo 前后）
    ///   - C：GeneralReturnToFightPointLoopAsync（MoveTo 前后）
    ///   - E：AutoFightSeek.SeekAndFightAsync 内置回点分支（MoveTo 前后）
    /// 读者（唯一）：AutoFightSeek.SeekAndFightAsync 的两处 MoveMouseBy 前，经
    ///   AutoFightSeekDecisions.ShouldSkipSeekRotation(IsReturningToFightPoint) 判定是否跳过甩鼠标。
    ///
    /// 用引用计数（而非单一 bool）的原因：万叶玩家场景下 B（后台循环）与 E（寻敌内置回点）
    ///   可能并发置位（同源 _taskParam.KazuhaContinuousReturn），单一 bool 存在
    ///   "一方复位掩盖另一方仍在进行"的风险；引用计数保证嵌套/重叠安全。
    /// 跨线程：Interlocked 增减 + Volatile.Read，无复合判断，线程安全。
    ///
    /// 严禁与以下信号合并 / 混用 / 重命名（语义完全不同）：
    ///   - IsTeleportingToStatue（神像传送进行中 → 回点循环 return 终止）
    ///   - IsSuspend / IsSuspendedByCapture（用户主动暂停 / 截图暂停）
    /// </summary>
    private static int _returnToFightPointDepth;

    /// <summary>回点移动是否进行中（引用计数 &gt; 0）。读者：SeekAndFightAsync 的 MoveMouseBy 门控。</summary>
    public static bool IsReturningToFightPoint => System.Threading.Volatile.Read(ref _returnToFightPointDepth) > 0;

    /// <summary>进入回点移动（计数 +1）。回点发起方在真正执行移动前调用，必须与 ExitReturnToFightPoint 成对（try-finally）。</summary>
    public static void EnterReturnToFightPoint() => System.Threading.Interlocked.Increment(ref _returnToFightPointDepth);

    /// <summary>退出回点移动（计数 -1）。必须在 finally 中调用，保证任何退出路径都复位。</summary>
    public static void ExitReturnToFightPoint() => System.Threading.Interlocked.Decrement(ref _returnToFightPointDepth);

    /// <summary>
    /// 最近一次"看到敌人"的时间戳。
    /// 由 AutoFightSeek.SeekAndFightAsync 在 4 处 return false 之前同步赋值；
    /// 由 GeneralReturnToFightPointLoopAsync 时间触发判据读取；
    /// 万叶专属循环 KazuhaContinuousReturnLoopAsync 不感知此字段（§3.13 强约束）。
    ///
    /// 战斗开始（fightTask 静态状态重置块）SHALL 重置为 DateTime.UtcNow，
    /// 避免跨战斗轮次读到 stale 值。
    ///
    /// 线程安全：DateTime 不能直接 volatile，通过 long ticks + Volatile.Read/Write 实现。
    /// 详见 .kiro/specs/fight-return-to-point-revamp/design.md §2.3
    /// </summary>
    private static long _lastEnemySeenAtTicks = DateTime.UtcNow.Ticks;

    public static DateTime LastEnemySeenAt
    {
        get => new DateTime(System.Threading.Volatile.Read(ref _lastEnemySeenAtTicks), DateTimeKind.Utc);
        set => System.Threading.Volatile.Write(ref _lastEnemySeenAtTicks, value.Ticks);
    }

    /// <summary>
    /// 联机锄地稳定性缓冲：上次成功识别的角色名集合 fingerprint
    /// （OrderBy + Ordinal + "|" 拼接，顺序无关）。
    /// 由 AutoHoeingTask.RunTask 入口设为 null 清空，仅联机锄地路径读写。
    /// 详见 .kiro/specs/combat-scenes-recognition-stability-buffer/design.md §2.1。
    /// </summary>
    private static volatile string? _lastRecognizedAvatarFingerprint;

    /// <summary>清空联机锄地稳定性缓冲（由 AutoHoeingTask 入口调用）。</summary>
    public static void ResetRecognitionStabilityBuffer()
    {
        _lastRecognizedAvatarFingerprint = null;
    }
    
    private static readonly object PickLock = new object(); 
    
    private static readonly object ZLock = new object(); 
    
    private readonly double _assetScale = TaskContext.Instance().SystemInfo.AssetScale;
    
    private readonly ReturnMainUiTask _returnMainUiTask = new();
    
    private class TaskFightFinishDetectConfig
    {
        public int DelayTime = 1500;
        public int DetectDelayTime = 450;
        public int FastCheckDelay = 100;
        public Dictionary<string, int> DelayTimes = new();
        public double CheckTime = 5;
        public List<string> CheckNames = new();
        public bool FastCheckEnabled;
        public bool RotateFindEnemyEnabled = false;

        public TaskFightFinishDetectConfig(AutoFightParam.FightFinishDetectConfig finishDetectConfig)
        {
            FastCheckEnabled = finishDetectConfig.FastCheckEnabled;
            ParseCheckTimeString(finishDetectConfig.FastCheckParams, out CheckTime, CheckNames);
            ParseFastCheckEndDelayString(finishDetectConfig.CheckEndDelay, out DelayTime, DelayTimes);
            BattleEndProgressBarColor =
                ParseStringToTuple(finishDetectConfig.BattleEndProgressBarColor, (95, 235, 255));
            BattleEndProgressBarColorTolerance =
                ParseSingleOrCommaSeparated(finishDetectConfig.BattleEndProgressBarColorTolerance, (6, 6, 6));
            DetectDelayTime = (int)((double.TryParse(finishDetectConfig.BeforeDetectDelay, out var result) ? result : 0.45) * 1000);
            FastCheckDelay = (int)Math.Round(finishDetectConfig.FastCheckDelay * 1000);
            RotateFindEnemyEnabled = finishDetectConfig.RotateFindEnemyEnabled;
        }

        public (int, int, int) BattleEndProgressBarColor { get; }
        public (int, int, int) BattleEndProgressBarColorTolerance { get; }

        public static void ParseCheckTimeString(
            string input,
            out double checkTime,
            List<string> names)
        {
            checkTime = 5;
            if (string.IsNullOrEmpty(input))
            {
                return; // 直接返回
            }

            var uniqueNames = new HashSet<string>(); // 用于临时去重的集合

            // 按分号分割字符串
            var segments = input.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                var trimmedSegment = segment.Trim();

                // 如果是纯数字部分
                if (double.TryParse(trimmedSegment, NumberStyles.Float, CultureInfo.InvariantCulture,
                        out double number))
                {
                    checkTime = number; // 更新 CheckTime
                }
                else if (!uniqueNames.Contains(trimmedSegment)) // 如果是非数字且不重复
                {
                    uniqueNames.Add(trimmedSegment); // 添加到集合
                }
            }

            names.AddRange(uniqueNames); // 将集合转换为列表
        }

        public static void ParseFastCheckEndDelayString(
            string input,
            out int delayTime,
            Dictionary<string, int> nameDelayMap)
        {
            delayTime = 1500;

            if (string.IsNullOrEmpty(input))
            {
                return; // 直接返回
            }

            // 分割字符串，以分号为分隔符
            var segments = input.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var segment in segments)
            {
                var parts = segment.Split(',');

                // 如果是纯数字部分
                if (parts.Length == 1)
                {
                    if (double.TryParse(parts[0].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                            out double number))
                    {
                        delayTime = (int)(number * 1000); // 更新 delayTime
                    }
                }
                // 如果是名字,数字格式
                else if (parts.Length == 2)
                {
                    string name = parts[0].Trim();
                    if (double.TryParse(parts[1].Trim(), NumberStyles.Float, CultureInfo.InvariantCulture,
                            out double value))
                    {
                        nameDelayMap[name] = (int)(value * 1000); // 更新字典，取最后一个值
                    }
                }
                // 其他格式，跳过不处理
            }
        }


        static bool IsSingleNumber(string input, out int result)
        {
            return int.TryParse(input, out result);
        }

        static (int, int, int) ParseSingleOrCommaSeparated(string input, (int, int, int) defaultValue)
        {
            // 如果是单个数字
            if (IsSingleNumber(input, out var singleNumber))
            {
                return (singleNumber, singleNumber, singleNumber);
            }

            return ParseStringToTuple(input, defaultValue);
        }

        static (int, int, int) ParseStringToTuple(string input, (int, int, int) defaultValue)
        {
            // 尝试按逗号分割字符串
            var parts = input.Split(',');
            if (parts.Length == 3 &&
                int.TryParse(parts[0], out var num1) &&
                int.TryParse(parts[1], out var num2) &&
                int.TryParse(parts[2], out var num3))
            {
                return (num1, num2, num3);
            }

            // 如果解析失败，返回默认值
            return defaultValue;
        }
    }

    private TaskFightFinishDetectConfig _finishDetectConfig;

    /// <summary>
    /// 联机锄地 C1 兜底信号位：A1（GetCombatScenesWithRetry）侧探测到队伍识别失败时设为 true，
    /// Start 入口据此调用 RunC1FallbackLoopAsync 走简化兜底循环。
    /// 单机路径下永远为 false。
    /// 详见 fight-strategy-fallback-use-real-flow/design.md §2.1 / §2.6。
    /// </summary>
    private bool _useC1Fallback;

    // fight-end-return-loop-not-joined-movement-overlap-fix:
    // 保存后台回点循环 Task 引用 + 专用 CTS，战斗结束时 cancel + join 消除移动重叠窗口。
    private Task? _kazuhaReturnLoopTask;
    private Task? _generalReturnLoopTask;
    private CancellationTokenSource? _returnLoopCts;
    private const int ReturnLoopJoinTimeoutMs = 3000;

    public AutoFightTask(AutoFightParam taskParam)
    {
        _taskParam = taskParam;
        
        var combatScriptBagAll = CombatScriptParser.ReadAndParse(_taskParam.CombatStrategyPath);
        
        _combatScriptBagSecond= combatScriptBagAll;
        
        #region 指定国家战斗脚本解析

        var isAutoSelectTeam = FightConfig.StrategyName.Contains("根据队伍自动选择");
        
        var isSelectAuto = _taskParam.CountryName.Contains("自动");
        
        if (isAutoSelectTeam)
        {
            var countryNamesList = FightConfig.CountryNamesList;
            
            // 对combatScriptBagAll进行重新排序，把含国家脚步名称排后面
            combatScriptBagAll.CombatScripts = combatScriptBagAll.CombatScripts
                .OrderBy(script => countryNamesList.Any(country => script.Name.Contains(country)))
                .ThenBy(script => countryNamesList.FirstOrDefault(country => script.Name.Contains(country)) ?? "")
                .ToList();
            
            var filteredCombatScripts = combatScriptBagAll.CombatScripts
                .Where(script => 
                    _taskParam.CountryName.Length >= 2 
                        ? _taskParam.CountryName.All(country => country != null && script.Name.Contains(country))
                        : _taskParam.CountryName.Any(country => country != null && script.Name.Contains(country)))
                .ToList();
            
            if (filteredCombatScripts.Count == 0)
            {
                //可能在 _taskParam.CountryName.Length >= 2 可能是因为没有符合条件的脚本，尝试Any
                filteredCombatScripts = combatScriptBagAll.CombatScripts
                    .Where(script => _taskParam.CountryName.Any(country => country != "精英" && country != "小怪" && script.Name.Contains(country)))
                    .ToList();
                if (filteredCombatScripts.Count == 0)
                {
                    filteredCombatScripts = combatScriptBagAll.CombatScripts
                        .Where(script => _taskParam.CountryName.Any(country => country != null && script.Name.Contains(country)))
                        .ToList();
                }
            }
            
            // 如果没有找到对应国家的脚本，则使用所有脚本
            if (filteredCombatScripts.Count == 0 && isAutoSelectTeam && isSelectAuto)
            {
                TaskControl.Logger.LogWarning("没有找到符合 {CountryName} 的战斗脚本，将使用所有策略进行匹配", string.Join(", ", _taskParam.CountryName));
                filteredCombatScripts = combatScriptBagAll.CombatScripts;
            }
            
            var combatScriptBagByCountry = new CombatScriptBag(filteredCombatScripts.Count == 0 ?combatScriptBagAll.CombatScripts : filteredCombatScripts);
            
            _combatScriptBag = isSelectAuto || combatScriptBagAll.CombatScripts.Count <= 1 ? combatScriptBagAll : combatScriptBagByCountry;
            
        }
        #endregion

        else
        {
            _combatScriptBag = combatScriptBagAll;
        }

        if (_taskParam.FightFinishDetectEnabled)
        {
            _predictor = App.ServiceProvider.GetRequiredService<BgiOnnxFactory>().CreateYoloPredictor(BgiOnnxModel.BgiWorld);
        }

        _finishDetectConfig = new TaskFightFinishDetectConfig(_taskParam.FinishDetectConfig);
        
    }
    public CombatScenes GetCombatScenesWithRetry(CancellationToken ct = default)
    {
        var first = TryInitializeTeamWithRetry(ct);

        if (first != null)
        {
            // 联机锄地：叠加双采样稳定性比较
            if (PathingConditionConfig.MultiplayerFightTimeoutOverride.HasValue)
            {
                first = ApplyStabilityBuffer(first, ct);
            }
            return first;
        }

        ct.ThrowIfCancellationRequested();

        // CustomAvatar 兜底优先（preservation §3.3）
        if (Config.CustomAvatarConfigOut.CustomAvatarEnabled)
        {
            return new CombatScenes().InitializeTeamForced(Config.CustomAvatarConfigOut.CustomAvatarForceUseList);
        }

        // 联机锄地 C1 兜底分支：不抛异常，标记 _useC1Fallback 让 Start 走 RunC1FallbackLoopAsync
        // 单机路径维持原 throw（preservation §3.1）
        if (FightStrategyFallbackDecisions.ShouldUseFallback(
                isMultiplayerHoeing: PathingConditionConfig.MultiplayerFightTimeoutOverride.HasValue,
                teamRecognized: false,
                customAvatarEnabled: false,
                matchedScriptCount: 0,
                availableAvatarCount: 0))
        {
            _useC1Fallback = true;
            // 返回未初始化的占位 CombatScenes，Start 通过 _useC1Fallback 信号位绕过后续流程
            return new CombatScenes();
        }

        throw new Exception("识别队伍角色失败（已重试 5 次）");
    }

    /// <summary>
    /// 5 次重试的纯识别函数：返回首次 CheckTeamInitialized 通过的 CombatScenes，全部失败返回 null。
    /// 详见 .kiro/specs/combat-scenes-recognition-stability-buffer/design.md §2.2。
    /// </summary>
    private static CombatScenes? TryInitializeTeamWithRetry(CancellationToken ct)
    {
        const int maxRetries = 5;
        const int retryDelayMs = 1000;

        for (var attempt = 1; attempt <= maxRetries; attempt++)
        {
            ct.ThrowIfCancellationRequested();
            using var ra = CaptureToRectArea();
            var scenes = new CombatScenes().InitializeTeam(ra);
            if (scenes.CheckTeamInitialized())
            {
                return scenes;
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
    /// 联机锄地双采样稳定性缓冲：与 _lastRecognizedAvatarFingerprint 比较，不一致则再识别一次。
    /// 详见 .kiro/specs/combat-scenes-recognition-stability-buffer/design.md §2.2 决策表。
    /// </summary>
    internal static CombatScenes ApplyStabilityBuffer(CombatScenes first, CancellationToken ct)
    {
        var firstFp = ComputeAvatarFingerprint(first);
        var prev = _lastRecognizedAvatarFingerprint;

        if (prev == null || prev == firstFp)
        {
            _lastRecognizedAvatarFingerprint = firstFp;
            return first;
        }

        Logger.LogWarning(
            "[联机][稳定性] 识别 fingerprint 与缓冲不一致，触发二次校验（prev={Prev} cur={Cur}）",
            prev, firstFp);

        var second = TryInitializeTeamWithRetry(ct);
        if (second == null)
        {
            Logger.LogWarning(
                "[联机][稳定性] 二次识别失败，沿用第一次结果并更新缓冲（{Cur}）",
                firstFp);
            _lastRecognizedAvatarFingerprint = firstFp;
            return first;
        }

        var secondFp = ComputeAvatarFingerprint(second);

        if (secondFp == prev)
        {
            Logger.LogInformation(
                "[联机][稳定性] 二次识别恢复一致（{Prev}），丢弃第一次误识别",
                prev);
            first.Dispose();
            return second;
        }

        if (secondFp == firstFp)
        {
            Logger.LogWarning(
                "[联机][稳定性] 二次确认队伍变更（{New}），更新缓冲",
                firstFp);
            _lastRecognizedAvatarFingerprint = firstFp;
            second.Dispose();
            return first;
        }

        Logger.LogWarning(
            "[联机][稳定性] 三次结果均不同（prev={Prev} first={First} second={Second}），使用最新二次结果",
            prev, firstFp, secondFp);
        _lastRecognizedAvatarFingerprint = secondFp;
        first.Dispose();
        return second;
    }

    /// <summary>
    /// 计算队伍 fingerprint：角色名集合，OrderBy(StringComparer.Ordinal) 后用 "|" 拼接。
    /// 顺序无关（{A,B,C,D} == {D,C,B,A}）。
    /// </summary>
    internal static string ComputeAvatarFingerprint(CombatScenes scenes)
    {
        if (scenes is null) return string.Empty;
        var names = scenes.GetAvatars()
            .Select(a => a.Name)
            .Where(n => !string.IsNullOrEmpty(n))
            .OrderBy(s => s, StringComparer.Ordinal);
        return string.Join("|", names);
    }
    
    //添加一个计时器，设定一个标志位，1000Ms内为true，超过1000Ms为false，战斗结束后重置计时器和标志位
    private volatile bool _fightDurationExceeded = true;
    
    //战斗跳过标记位
    private volatile bool _skipFlag = false;
    
    // 方法1：判断是否是单个数字

    /*public int delayTime=1500;
    public Dictionary<string, int> delayTimes = new();
    public double checkTime = 5;
    public List<string> checkNames = new();*/
    public async Task Start(CancellationToken ct)
    {
        _ct = ct;

        LogScreenResolution();

        // 点位配置同时供正常战斗流程和 C1 兜底流程使用，整场战斗保持不变。
        var rewardEndDetection = _taskParam.RewardEndDetection;
        LogRewardEndDetectionEnabled(rewardEndDetection);

        // 重置兜底信号位（每个 fight waypoint 独立判定）
        _useC1Fallback = false;

        var combatScenes = GetCombatScenesWithRetry(ct);

        // ─── A1 出口：GetCombatScenesWithRetry 已设置 _useC1Fallback 表示队伍识别失败 ───
        // 立即委派给简化 EQA 循环，不进入后续脚本匹配 / 战斗循环
        if (_useC1Fallback)
        {
            await RunC1FallbackLoopAsync(ct, rewardEndDetection);
            return;
        }

        if (_taskParam.AutoCombatEq && PathingConditionConfig.CombatScenesGoBackUp is not null && 
            PathingConditionConfig.CombatScenesGoBackUp.Avatars.Select(avatar => avatar.Name).ToArray()
                .SequenceEqual(combatScenes.Avatars.Select(a => a.Name).ToArray()))
        {
            Logger.LogInformation("自动战斗：继承地图追踪队伍Cd信息...");
            combatScenes = PathingConditionConfig.CombatScenesGoBackUp;
            // foreach (var avatar in combatScenes.GetAvatars())
            // {
            //     Logger.LogInformation("队伍角色 {Name} 当前剩余E技能CD：{Cd} 秒",
            //         avatar.Name,
            //         Math.Round(avatar.GetSkillCdSeconds(), 2));
            // }
        }
        
        /*var combatScenes = new CombatScenes().InitializeTeam(CaptureToRectArea());
        if (!combatScenes.CheckTeamInitialized())
        {
            throw new Exception("识别队伍角色失败");
        }*/

        // ─── A2 注入点：脚本匹配 ───
        // 单机路径仍调用原 FindCombatScript，行为不变（preservation §3.4-3.6）
        // 联机锄地下用 TryFindCombatScript 拿无异常信号，false → 走兜底
        var isMultiplayerHoeing = PathingConditionConfig.MultiplayerFightTimeoutOverride.HasValue;
        List<CombatCommand>? combatCommands;
        var matchedScriptCount = 0;

        if (isMultiplayerHoeing)
        {
            // 第一轮：仅在"根据队伍自动选择"时启用 isFirstRound（与原逻辑一致）
            var firstRoundResult = _combatScriptBag.FindCombatScript(
                combatScenes.GetAvatars(),
                FightConfig.StrategyName.Contains("根据队伍自动选择"));

            if (firstRoundResult != null)
            {
                combatCommands = firstRoundResult;
                matchedScriptCount = 1;
            }
            else
            {
                // 第二轮：用无异常重载
                if (!_combatScriptBagSecond.TryFindCombatScript(
                        combatScenes.GetAvatars(),
                        out combatCommands,
                        out matchedScriptCount))
                {
                    // matchedScriptCount==0 → C2 命中 → 捏造 EQA 序列继续主循环
                    if (FightStrategyFallbackDecisions.ShouldUseFallback(
                            isMultiplayerHoeing: true,
                            teamRecognized: true,
                            customAvatarEnabled: Config.CustomAvatarConfigOut.CustomAvatarEnabled,
                            matchedScriptCount: 0,
                            availableAvatarCount: 0))
                    {
                        Logger.LogWarning(
                            "[联机][兜底][C2] 无匹配战斗脚本，捏造 EQA 序列继续战斗（avatars={Names}）",
                            string.Join(",", combatScenes.GetAvatars().Select(a => a.Name)));
                        var avatarNamesForFallback =
                            combatScenes.GetAvatars().Select(a => a.Name).ToList();
                        combatCommands = BuildSyntheticEqaCommands(avatarNamesForFallback);
                        matchedScriptCount = 1; // 防 A3 误触
                    }
                    else
                    {
                        // 决策返回 false（理论不应发生，因为 isMultiplayerHoeing=true ∧ matched=0 必触发 C2）
                        // 兜底安全网：维持原 Exception 行为
                        throw new Exception("未匹配到任何战斗脚本");
                    }
                }
            }
        }
        else
        {
            // 单机路径：保留原表达式不变，FindCombatScript 在第二轮 MatchCount==0 时仍抛 Exception
            combatCommands = _combatScriptBag.FindCombatScript(combatScenes.GetAvatars(),
                FightConfig.StrategyName.Contains("根据队伍自动选择")) ??
                             _combatScriptBagSecond.FindCombatScript(combatScenes.GetAvatars());
        }
        
        var bandList = (_taskParam.AutoCombatEq && !string.IsNullOrWhiteSpace(_taskParam.UseEqList))
            ? _taskParam.UseEqList.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Where(s => s.Contains("C", StringComparison.OrdinalIgnoreCase)) // 检查是否包含'C'
                .Select(s => int.TryParse(s.TrimEnd('C'), out var n) ? n : 0) // 去掉'C'并尝试解析数字
                .Where(n => n >= 1 && n <= combatScenes.GetAvatars().Count) // 保证序号在队伍
                .ToList()
            : new List<int>(); // 如果没有指定AutoCombatEq，默认情况下bandList为空

        var bandAvatarsName = _taskParam.AutoCombatEq ? combatScenes.GetAvatars().Where(a => bandList.Contains(a.Index)).Select(a => a.Name).ToList() : new List<string>();
        // Logger.LogError("当前禁用角色：{CombatScriptName}", bandAvatarsName);
        
        // 命令用到的角色名 筛选交集
        var commandAvatarNames = combatCommands.Select(c => c.Name).Distinct()
            .Select(n => combatScenes.SelectAvatar(n)?.Name)
            .WhereNotNull().ToList();
        commandAvatarNames = commandAvatarNames.Except(bandAvatarsName).ToList();
        
        // 过滤不可执行的脚本，Task里并不支持"当前角色"。
        combatCommands = combatCommands 
            .Where(c => commandAvatarNames.Contains(c.Name))
            .ToList();
        
        if (commandAvatarNames.Count <= 0)
        {
            // ─── A3 注入点：联机锄地下捏造 EQA 序列继续主循环（C3） ───
            if (FightStrategyFallbackDecisions.ShouldUseFallback(
                    isMultiplayerHoeing: isMultiplayerHoeing,
                    teamRecognized: true,
                    customAvatarEnabled: Config.CustomAvatarConfigOut.CustomAvatarEnabled,
                    matchedScriptCount: Math.Max(1, matchedScriptCount),
                    availableAvatarCount: 0))
            {
                // 用真实队伍 - ban 列表作为捏脚本对象
                var avatarsForFallback = combatScenes.GetAvatars()
                    .Select(a => a.Name)
                    .Except(bandAvatarsName)
                    .ToList();
                if (avatarsForFallback.Count == 0)
                {
                    // ban 列表覆盖整队的极端边界：回退到全队（至少打一段）
                    avatarsForFallback = combatScenes.GetAvatars().Select(a => a.Name).ToList();
                }
                Logger.LogWarning(
                    "[联机][兜底][C3] 无可用战斗脚本，捏造 EQA 序列继续战斗（avatars={Names}）",
                    string.Join(",", avatarsForFallback));
                combatCommands = BuildSyntheticEqaCommands(avatarsForFallback);
                commandAvatarNames = avatarsForFallback;
            }
            else
            {
                // 单机路径维持原 Exception
                throw new Exception("没有可用战斗脚本");
            }
        }

        // 新的取消token
        var cts2 = new CancellationTokenSource();
        ct.Register(cts2.Cancel);

        combatScenes.BeforeTask(cts2.Token);
        // 注入阿蕾奇诺红血门控开关到每个角色（让 Avatar.KeyPress/UseBurst 读到当前配置组/独立任务的开关值，
        // 而非全局 AutoFightConfig；修复"配置组开关失效"问题）。
        foreach (var avatarToInit in combatScenes.GetAvatars())
        {
            avatarToInit.ArlecchinoBurstLowHpGateEnabled = _taskParam.ArlecchinoBurstLowHpGateEnabled;
            avatarToInit.MavuikaMotorcycleCheckEnabled = _taskParam.MavuikaMotorcycleCheckEnabled;
            avatarToInit.ArlecchinoAutoEnabled = _taskParam.ArlecchinoAutoEnabled;
            avatarToInit.QiKong = _taskParam.QiKong;
        }
        TimeSpan fightTimeout = TimeSpan.FromSeconds(_taskParam.Timeout); // 战斗超时时间
        Stopwatch timeoutStopwatch = Stopwatch.StartNew();

        Stopwatch checkFightFinishStopwatch = Stopwatch.StartNew();
        TimeSpan checkFightFinishTime = TimeSpan.FromSeconds(_finishDetectConfig.CheckTime); //检查战斗超时时间的超时时间


        //战斗前检查，可做成配置
        // if (await CheckFightFinish()) {
        //     return;
        // }
        // var FightEndFlag = false;
        FightEndFlag = false;
        SwitchTryCount = 0;
        var fightEndFlag = false;
        var timeOutFlag = false;
        // 后台识别任务写、主流程读；命中后复用原超时结束的战后处理分支。
        var rewardTimeoutEndFlag = 0;
        Task? rewardTimeoutEndTask = null;
        string lastFightName = "";

        //统计切换人打架次数
        var countFight = 0;
        
        // 可以跳过的角色名,配置中有的和命令中有的取交
        var canBeSkippedAvatarNames = combatScenes.UpdateActionSchedulerByCd(_taskParam.ActionSchedulerByCd)
            .Where(s => commandAvatarNames.Contains(s)).WhereNotNull().ToList();
        
        //所有角色是否都可被跳过
        var allCanBeSkipped = commandAvatarNames.All(a => canBeSkippedAvatarNames.Contains(a));
        
        var delayTime = _finishDetectConfig.DelayTime;
        var detectDelayTime = (_taskParam.FinishDetectConfig.EndModel&& _taskParam.FinishDetectConfig.RotateFindEnemyEnabled) || _taskParam.FinishDetectConfig.PaimonEndModel ? _finishDetectConfig.FastCheckDelay : _finishDetectConfig.DetectDelayTime;

        Avatar? guardianAvatar = null;
        if (!string.IsNullOrWhiteSpace(_taskParam.GuardianAvatar))
        {
            // Logger.LogInformation("盾奶优先功能角色预处理开始..{aq}-{aa}.",_taskParam.GuardianAvatar,combatScenes.GetAvatars().Count);
            if (int.Parse(_taskParam.GuardianAvatar) <= combatScenes.GetAvatars().Count) //确保序号在队伍内
            {
                guardianAvatar = combatScenes.SelectAvatar(int.Parse(_taskParam.GuardianAvatar));
            }
            else
            {
                Logger.LogWarning("盾奶优先功能角色预处理失败，请检查盾奶优先功能角色配置是否正确。");
                if (combatScenes.SelectAvatar(_taskParam.GuardianAvatar) is not null)
                {
                    guardianAvatar = combatScenes.SelectAvatar(int.Parse(_taskParam.GuardianAvatar));
                }
            }
        }

        AutoFightSeek.RotationCount= 0; // 重置旋转次数

        ImageRegion image = null;

        var useEqList = (_taskParam.AutoCombatEq && !string.IsNullOrWhiteSpace(_taskParam.UseEqList))
            ? _taskParam.UseEqList.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(s => int.TryParse(s.Trim(), out var n) ? n : 0)
                .Where(n => n >= 1 && n <= combatScenes.GetAvatars().Count) // 保证序号在队伍
                .ToList()
            : new List<int> { 1, 2, 3, 4 }
                .Where(n => n >= 1 && n <= combatScenes.GetAvatars().Count) // 添加此行以处理默认值
                .ToList();
        
        var useSkillList = new List<int>();
        var useSkillListWithH = new List<int>();
        var useSkillListWithF = 0;
        var useSkillListWithA = new Dictionary<int, int>();

        if (_taskParam.AutoCombatEq && !string.IsNullOrWhiteSpace(_taskParam.UseSkillList))
        {
            var skillParts = _taskParam.UseSkillList.Split(new[] { ',', '，' }, StringSplitOptions.RemoveEmptyEntries);

            foreach (var part in skillParts)
            {
                var trimmedPart = part.Trim();
                // 使用正则表达式移除A及其后面的括号和内容
                var skillNumberStr = trimmedPart.Replace("H", "").Replace("F", "").Trim();
                var match = Regex.Match(skillNumberStr, @"(\d+)A(\(\d+\))?");
                skillNumberStr = System.Text.RegularExpressions.Regex.Replace(skillNumberStr, @"A\(\d+\)|A", "");
                
                if (match.Success)
                {
                    // 提取以A结尾的数字前面的数字
                    if (int.TryParse(match.Groups[1].Value, out int skillNumber2))
                    {
                        // 提取括号中的数字，如果存在的话
                        if (match.Groups[2].Success && int.TryParse(match.Groups[2].Value.Trim('(', ')'), out int value))
                        {
                            useSkillListWithA.Add(skillNumber2, value);
                        }
                        else
                        {
                            useSkillListWithA.Add(skillNumber2,600);
                        }
                    }
                }
                
                var skillNumber = int.TryParse(skillNumberStr, out var n) ? n : 0;

                if (skillNumber >= 1 && skillNumber <= combatScenes.GetAvatars().Count) //保证序号在队伍
                {
                    useSkillList.Add(skillNumber); // 添加到全部技能列表

                    if (trimmedPart.Contains('H'))
                    {
                        useSkillListWithH.Add(skillNumber); // 添加到带H的技能列表
                    }
                    if (trimmedPart.Contains('F') && useSkillListWithF == 0) // 只记录第一个F
                    {
                        useSkillListWithF = skillNumber; // 记录第一个带F的技能序号
                    }
                }
            }
            // foreach (var kvp in useSkillListWithA)
            // {
            //     Logger.LogError($"{{ {kvp.Key}, {kvp.Value} }}");
            // }
        }
        else
        {
            useSkillList = new List<int> { 1, 2, 3, 4 };
            useSkillListWithH = new List<int>();
            // useSkillListWithF = 0;
        }

        var predefinedlist = new List<string>() { "枫原万叶" ,"希诺宁"};
        
        //旋转次数
        var rotationLimit = _taskParam.RotaryFactor == 1 ? 500 : _taskParam.FinishDetectConfig.RotationMode && _taskParam.FinishDetectConfig.RotateFindEnemyEnabled ? 50 : 6;

        var (__quorumCoordinator, __onAllFightDone) = StartSharedFightEndCoordination();

        // 战斗操作
        var fightTask = Task.Run(async () =>
        {
            // 重置静态战斗状态：必须在任何后台子任务（FindExp / TakeMedicineAsync / KazuhaContinuousReturnLoopAsync / GeneralReturnToFightPointLoopAsync）
            // 启动之前完成，否则子任务在启动同步段就会读到上一场战斗遗留的 stale 标志（特别是 FightEndTotoly=true），
            // 导致 while 循环条件立即为 true 而退出（联机锄地连续战斗下尤其明显——日志只剩
            // "自动吃药：检测间隔..." 一行，主检测循环根本没进入）。
            FightStatusFlag = true;
            FightEndTotoly = false;
            _totolyEndCount = 0;
            _2ndEndFlag = false;
            LastEnemySeenAt = DateTime.UtcNow;
            // 与原 CheckFightFinish 并行运行，不受 FightFinishDetectEnabled 开关影响。
            if (rewardEndDetection is not null)
            {
                rewardTimeoutEndTask = StartRewardTimeoutEndLoop(cts2.Token, rewardEndDetection, _ =>
                {
                    // 联机配额模式可能不会立即退出，也必须保留本机已命中的结果。
                    Interlocked.Exchange(ref rewardTimeoutEndFlag, 1);
                    HandleRewardEndDetected(rewardEndDetection);
                });
            }

            // return-to-point-stale-prev-position-drift-fix (b) 战斗开始首帧播种：
            // 进入战斗、任何后台子任务（持续回点循环 / SeekAndFightAsync）首次 GetPosition 之前，
            // 用开战点（FightWaypoint）坐标播种 Navigation 单例锚点，避免沿用上一段移动残留的
            // _prevX/_prevY 导致首帧识别失败时局部匹配锚错（BC1）。验证"用开战点做前一个有效坐标"的推测。
            // 仅 SetPrevPosition 覆写 prev，绝不 Navigation.Reset()（进程级共享单例）。
            // 单机/联机共用同一战斗路径，FightWaypoint 单机也有值；识别成功立即用真值刷新 → 单机零回归。
            // fightTask 任务体每场战斗只执行一次 → 天然"只播一次"。
            var __fightWp = FightWaypoint;
            if (__fightWp is not null)
            {
                var __seed = KazuhaCollectPositionGuardDecisions.ComputeSeedAnchor(__fightWp.X, __fightWp.Y);
                Navigation.SetPrevPosition((float)__seed.X, (float)__seed.Y);
            }

            #region 基于战斗检测经验值开关万叶拾取功能同步任务
            
            if (_taskParam.ExpKazuhaPickup) FindExp(cts2.Token);
            
            #endregion
            
            #region 自动吃药功能同步任务

            if (_taskParam.TakeMedicineEnabled)
            {
                IsTpForRecover = true;
                _ = TakeMedicineAsync(cts2.Token);
            }
            else
            {
                IsTpForRecover = false;
            }
            
            #endregion
            
            try
            {
                // multiplayer-kazuha-pre-cast-positioning EB2: 联机锄地 + 万叶玩家场景下，启动独立"持续回点"后台任务。
                // 必须放在 FightEndTotoly = false 之后，否则会读到上一场战斗结束遗留的 stale true，循环立即退出。
                // 与 fightTask 主循环并行运行，绑定同一 cts2.Token（战斗结束/取消时统一终止）。
                // 注意：不能依赖 SeekAndFightAsync 内部的持续回点，因为 SeekAndFightAsync 仅在用户开启
                // RotateFindEnemyEnabled 时才会被调用——大多数用户默认 false。本独立任务确保万叶玩家
                // 战斗中持续回点功能在所有联机锄地场景下都能生效。
                if (_taskParam.KazuhaContinuousReturn)
                {
                    // 万叶专属循环（最高优先级，§3.11 一行不动）
                    // fight-end-return-loop-not-joined-movement-overlap-fix:
                    // 用专用回点 CTS（linked cts2）派生 token，保存 Task 引用供战斗结束 join。
                    _returnLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cts2.Token);
                    _kazuhaReturnLoopTask = Task.Run(() => KazuhaContinuousReturnLoopAsync(_returnLoopCts.Token), _returnLoopCts.Token);
                }
                else if (_taskParam.FinishDetectConfig.ReturnToFightPointEnabled)
                {
                    // 通用版循环：距离触发 + 时间触发并存
                    var rotateFindEnemyEnabled = _taskParam.FinishDetectConfig.RotateFindEnemyEnabled;
                    var timeTriggerEnabled = _taskParam.FinishDetectConfig.ReturnToFightPointTimeTriggerEnabled;
                    var intervalMs = _taskParam.FinishDetectConfig.ReturnToFightPointIntervalMs;
                    var triggerDistance = _taskParam.FinishDetectConfig.ReturnToFightPointTriggerDistance;
                    var stopDistance = _taskParam.FinishDetectConfig.ReturnToFightPointStopDistance;
                    var timeTriggerSeconds = _taskParam.FinishDetectConfig.ReturnToFightPointTimeTriggerSeconds;

                    if (timeTriggerEnabled && !rotateFindEnemyEnabled)
                    {
                        TaskControl.Logger.LogWarning(
                            "[AutoFight][回点] 时间触发启用但旋转寻敌未启用，时间触发分支跳过；距离触发不受影响");
                    }

                    if (AutoFightSeekDecisions.IsReturnToFightPointConfigValid(
                            intervalMs, triggerDistance, stopDistance,
                            timeTriggerEnabled, rotateFindEnemyEnabled, timeTriggerSeconds))
                    {
                        // fight-end-return-loop-not-joined-movement-overlap-fix:
                        // 用专用回点 CTS（linked cts2）派生 token，保存 Task 引用供战斗结束 join。
                        _returnLoopCts = CancellationTokenSource.CreateLinkedTokenSource(cts2.Token);
                        _generalReturnLoopTask = Task.Run(() => GeneralReturnToFightPointLoopAsync(
                            _returnLoopCts.Token, intervalMs, triggerDistance, stopDistance,
                            timeTriggerEnabled, timeTriggerSeconds, rotateFindEnemyEnabled), _returnLoopCts.Token);
                    }
                    else
                    {
                        TaskControl.Logger.LogWarning(
                            "[AutoFight][回点] 配置非法（trigger={Trigger:F1}, stop={Stop:F1}, interval={Interval}ms, timeSec={TimeSec}），本次任务不启用通用版回点循环",
                            triggerDistance, stopDistance, intervalMs, timeTriggerSeconds);
                    }
                }
                // else 单机/联机非万叶 + 总开关 false 默认不启动任何回点循环（§3.1 单机零行为变化）

                // 进入战斗后，不检查战斗结束的判断
                if (_taskParam.FinishDetectConfig.EndModel && _taskParam.FinishDetectConfig.FightWaitNotEndTime > 0)
                {
                    Task.Run(async () => {
                        _fightDurationExceeded = true;
                        await Task.Delay(_taskParam.FinishDetectConfig.FightWaitNotEndTime, cts2.Token);
                        _fightDurationExceeded = false;
                    }, cts2.Token); 
                }
                else
                {
                    _fightDurationExceeded = false;
                }
                
                while (!cts2.Token.IsCancellationRequested && !FightEndTotoly)
                {
                    if (_skipFlag)
                    { 
                        await Task.Delay(100, cts2.Token);
                        //Logger.LogWarning("二次检测等待1");
                        continue; 
                    }
                    
                    if(FightEndTotoly) break;
                    // 所有战斗角色都可以被取消
                    #region 本次战斗的跳过战斗判定

                    //如果所有角色都可以被跳过，且没有任何一个cd大于0的(技能都还没好)
                    //则强制等待，因为不等待的话什么都不能做，而且会造成刷屏
                    if (allCanBeSkipped)
                    {
                        //获取最低cd
                        var minCoolDown = commandAvatarNames.Select(a => combatScenes.SelectAvatar(a)).WhereNotNull()
                            .Select(a => a.GetSkillCdSeconds()).Min();
                        if (minCoolDown > 0)
                        {
                            TaskControl.Logger.LogInformation("队伍中所有角色的技能都在冷却中,等待{MinCoolDown}秒后继续。", Math.Round(minCoolDown, 2));
                            await Delay((int)Math.Ceiling(minCoolDown * 1000), cts2.Token);
                        }
                    }

                    var skipFightName = "";

                    #endregion
                    
                    for (var i = 0; i < combatCommands.Count; i++)
                    {
                        if (_skipFlag)
                        { 
                            await Task.Delay(100, cts2.Token);
                            //Logger.LogWarning("二次检测等待2");
                            continue; 
                        }
                        
                        var command = combatCommands[i];
                        var lastCommand = i == 0 ? command : combatCommands[i - 1];
                        
                        #region 盾奶位技能优先和自动EQ功能
                        
                        // var skipModel = guardianAvatar != null && ((lastFightName != command.Name) || (guardianAvatar.IsSkillReady()));
                        
                        if (guardianAvatar is not null && (lastFightName != command.Name || combatScenes.GetAvatars().Count <=2)) {
                            
                            image = CaptureToRectArea();
                            
                            await AutoFightSkill.EnsureGuardianSkill(guardianAvatar,lastCommand,lastFightName,
                            _taskParam.GuardianAvatar,_taskParam.GuardianAvatarHold,5,cts2.Token,_taskParam.GuardianCombatSkip,_taskParam.BurstEnabled);
                            
                            if (_taskParam.AutoCombatEq && guardianAvatar.ManualSkillCd == 0 && !cts2.Token.IsCancellationRequested)
                            {
                                if (timeoutStopwatch.Elapsed > fightTimeout)
                                {
                                    fightEndFlag = true;
                                    timeOutFlag = true;
                                    FightEndTotoly  = true;
                                    break;
                                }

                                if(i>0)i--;
                                continue;     
                                
                            }

                            if (_taskParam.AutoCombatEq)
                            {
                                var useEq = new List<int>();
                                for (var h = 1; h <= combatScenes.GetAvatars().Count; h++)
                                {
                                    if (!combatScenes.SelectAvatar(h).IsActive(image))
                                    {
                                        continue;
                                    }

                                    try
                                    {
                                        useEq = await AutoFightSkill.AvatarQSkillAsync(image, useEqList, h);
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.LogError("自动EQ战斗：角色 {name} 识别异常 {ex}", h, ex.Message);
                                        fightEndFlag = true;
                                        FightEndTotoly  = true;
                                        throw;
                                    }
                                    
                                    break;
                                }
                                
                                if (useSkillListWithF>0 && combatScenes.SelectAvatar(useSkillListWithF).IsSkillReady()) //自定义序号首位先放E，只执行一次
                                {
                                    if (_taskParam.FinishDetectConfig.RotationMode &&
                                        _taskParam.FinishDetectConfig.RotateFindEnemyEnabled &&
                                        CheckFightFinish(0, detectDelayTime, cts2.Token).Result)
                                    {
                                        FightEndTotoly  = true;
                                        fightEndFlag = true;
                                        break;
                                    }
                                    Logger.LogInformation("自动EQ战斗：执行序号 {name} 首E技能", useSkillListWithF);
                                    var avatarFirst = combatScenes.SelectAvatar(useSkillListWithF);
                                
                                    // 先尝试切换角色，成功再单独 await 技能执行结果
                                    if (avatarFirst.TrySwitch(15))
                                    {
                                        var skillSucceeded = await AutoFightSkill.AvatarSkillAsync(Logger, avatarFirst, false, 1, cts2.Token);
                                        if (!skillSucceeded)
                                        {
                                            // 原有在条件不满足时的处理逻辑
                                            avatarFirst.UseSkill(useSkillListWithH.Contains(useSkillListWithF), 1);
                                            var useA = useSkillListWithA.ContainsKey(useSkillListWithF) && useSkillListWithA[useSkillListWithF] > 0;
                                            if (useA)
                                            {
                                                Logger.LogInformation("自动EQ战斗：执行序号 {name} 角色首E技能后普攻 {time} ms", useSkillListWithF, useSkillListWithA[useSkillListWithF]);
                                                avatarFirst.Attack(useSkillListWithA[useSkillListWithF]); 
                                            }
                                        }
                                    }
                                    useSkillListWithF = 0;
                                }

                                if (useEq.Count > 0)
                                {
                                    foreach (var num in useEq) 
                                    {
                                        if (_skipFlag)
                                        {
                                            break; 
                                        }
                                        
                                        Logger.LogInformation("自动EQ战斗：使用序号 {name} 角色技能", num);
                                        var avatarQ = combatScenes.SelectAvatar(num);
                                        var useE = useSkillList.Contains(num);
                                        var avatarQHold = useSkillListWithH.Contains(num);
                                        var usePre = predefinedlist.Contains(avatarQ.Name);
                                        var useAContainsKey = useSkillListWithA.ContainsKey(num);
                                        var useA = (useAContainsKey && useSkillListWithA[num] > 0) || usePre;

                                        if (_taskParam.FinishDetectConfig.RotationMode &&
                                            _taskParam.FinishDetectConfig.RotateFindEnemyEnabled &&
                                            CheckFightFinish(0, detectDelayTime, cts2.Token).Result)
                                        {
                                            FightEndTotoly  = true;
                                            fightEndFlag = true;
                                            break;
                                        }
                                        
                                        if (avatarQ.TrySwitch(15))
                                        {
                                            lastFightName = avatarQ.Name;
                                            countFight++;
                                            if (useE && !await AutoFightSkill.AvatarSkillAsync(Logger, avatarQ, false, 1, cts2.Token))
                                            {
                                                avatarQ.UseSkill(avatarQHold);
                                                if (useA)
                                                {
                                                    if (!useAContainsKey)
                                                    {
                                                        useSkillListWithA.Add(num,avatarQHold?700:600);
                                                    }
                                                    Logger.LogInformation("自动EQ战斗：执行序号 {name} 角色普攻 {time} ms", num, useSkillListWithA[num]);
                                                    avatarQ.Attack(useSkillListWithA[num]); 
                                                }
                                                
                                                var imageAfterUseSkill = CaptureToRectArea();
                                                var retry = 30;
                                                try
                                                {
                                                    while (!await AutoFightSkill.AvatarSkillAsync(Logger, avatarQ,
                                                               false, 1, cts2.Token, imageAfterUseSkill) && retry > 0)
                                                    {
                                                        Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                                        Simulation.ReleaseAllKey();

                                                        // 防止在纳塔飞天或爬墙
                                                        if (retry % 4 == 0)
                                                        {
                                                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                                                            Simulation.SendInput.SimulateAction(GIActions.Drop);
                                                        }

                                                        // 释放旧的截图资源
                                                        imageAfterUseSkill.Dispose();

                                                        // 获取新的截图
                                                        imageAfterUseSkill = CaptureToRectArea();

                                                        await Task.Delay(30, cts2.Token);
                                                        retry -= 1;
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Logger.LogError("自动EQ战斗：角色 {name} 释放技能异常 {ex}", avatarQ.Name, ex.Message);
                                                    fightEndFlag = true;
                                                    FightEndTotoly  = true;
                                                    throw;
                                                }
                                                finally
                                                {
                                                    imageAfterUseSkill.Dispose();
                                                }
                                            }
                                            
                                            if (_skipFlag)
                                            {
                                                break; 
                                            }
                                            
                                            fightEndFlag = await CheckFightFinish(0, detectDelayTime, cts2.Token,avatarQ) || FightEndTotoly;
                                            if (!fightEndFlag)
                                            { 
                                                Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                                                var imageAfterBurst = CaptureToRectArea();
                                                var ms = 30; // 初始化计数器

                                                try
                                                {
                                                    while (imageAfterBurst.Find(ElementAssets.Instance.PaimonMenuRo).IsExist() && ms > 0)
                                                    {
                                                        var skillSucceeded = await AutoFightSkill.AvatarSkillAsync(Logger, avatarQ, true, 1, cts2.Token, imageAfterBurst, false);

                                                        if (skillSucceeded)
                                                        {
                                                            break;
                                                        }

                                                        // 原逻辑：触发一次大招并等待，再更新截图重试
                                                        if (_skipFlag)
                                                        {
                                                            break; 
                                                        }
                                                        
                                                        if (_taskParam.FinishDetectConfig.RotationMode &&
                                                            _taskParam.FinishDetectConfig.RotateFindEnemyEnabled)
                                                        {
                                                            var aa = CheckFightFinish(0, detectDelayTime, cts2.Token).Result;
                                                            if (aa)
                                                            {
                                                                FightEndTotoly  = true;
                                                                fightEndFlag = true;
                                                                break;
                                                            }
                                                        }
                                                        Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                                                        await Task.Delay(50, cts2.Token);

                                                        imageAfterBurst.Dispose();
                                                        imageAfterBurst = CaptureToRectArea();

                                                        ms -= 1;
                                                    }
                                                }
                                                catch (Exception ex)
                                                {
                                                    Logger.LogError("自动EQ战斗：角色 {name} 释放技能异常 {ex}", avatarQ.Name, ex.Message);
                                                    fightEndFlag = true;
                                                    FightEndTotoly  = true;
                                                    throw;
                                                }
                                                finally
                                                {
                                                    // 确保最终释放资源
                                                    imageAfterBurst.Dispose();
                                                    if (_taskParam.FinishDetectConfig.RotationMode &&
                                                        _taskParam.FinishDetectConfig.RotateFindEnemyEnabled &&
                                                        CheckFightFinish(0, detectDelayTime, cts2.Token).Result)
                                                    {
                                                        FightEndTotoly  = true;
                                                        fightEndFlag = true;
                                                    }
                                                }
                                            }
                                            else
                                            {
                                                FightEndTotoly  = true;
                                                break;
                                            }
                                            if (guardianAvatar.IsSkillReady())
                                            {
                                                break;
                                            }
                                        }
                                    }
                                    if (_skipFlag) continue;
                                }
                                useEq.Clear(); 
                                if (guardianAvatar.IsSkillReady() && !cts2.Token.IsCancellationRequested)
                                {
                                    if(i>0)i--;
                                    continue;
                                }
                            }
                            image.Dispose();
                        }
                        
                        if (fightEndFlag)break;
                        
                        var avatar = combatScenes.SelectAvatar(command.Name);
                        
                        #endregion
                        
                        #region 初始寻敌处理

                        if ( _finishDetectConfig.RotateFindEnemyEnabled && i == 0 && _taskParam.IsFirstCheck)
                        {
                            try
                            {
                                await AutoFightSeek.SeekAndFightAsync(TaskControl.Logger, detectDelayTime, delayTime,
                                    cts2.Token, true, _taskParam.RotaryFactor,avatar,_taskParam.FinishDetectConfig.GoDistance,_taskParam.FinishDetectConfig.EndModel,_taskParam.FinishDetectConfig.RotationMode,
                                    kazuhaContinuousReturn: _taskParam.KazuhaContinuousReturn,
                                    returnIntervalMs: 1000,
                                    returnDistanceThreshold: 1.0);
                            }
                            catch (Exception ex)
                            {
                                fightEndFlag = true;
                                FightEndTotoly  = true;
                                Logger.LogError("初始寻敌异常 {ex}", ex.Message);
                                throw;
                            }
                        }
                        
                        #endregion
                        
                        if (avatar is null || (avatar.Name == guardianAvatar?.Name && (_taskParam.GuardianCombatSkip || _taskParam.BurstEnabled)))
                        {
                            Logger.LogDebug("跳过角色{command.Name} - {avatar.Name}", command.Name,avatar?.Name);
                            continue;
                        }

                        if (_taskParam.AutoCombatEq)
                        {
                            if (_taskParam.FinishDetectConfig.RotationMode &&
                                _taskParam.FinishDetectConfig.RotateFindEnemyEnabled &&
                                CheckFightFinish(0, detectDelayTime, cts2.Token).Result)
                            {
                                FightEndTotoly  = true;
                                fightEndFlag = true;
                                break;
                            }
                            avatar?.TrySwitch(15);
                        }
                        #region 每个命令的跳过战斗判定

                        // 判断是否满足跳过条件:
                        // 1.上一次成功执行命令的最后执行角色不是这次的执行角色
                        // 2.这次执行的角色包含在可跳过的角色列表中
                        if (!
                                //上次命令的执行角色和这次相同
                                (lastFightName == command.Name &&
                                 // 且未跳过(成功执行)了,则不进行跳过判定
                                 skipFightName == "")
                            &&
                            // 且这次执行的角色包含在可跳过的角色列表中
                            (allCanBeSkipped || canBeSkippedAvatarNames.Contains(command.Name))
                           )
                        {
                            var cd = avatar.GetSkillCdSeconds();
                            if (cd > 0)
                            {
                                // 如果上一次该角色已经被跳过，则不进行log输出，以免刷屏
                                if (skipFightName != command.Name)
                                {
                                    var manualSkillCd = avatar.ManualSkillCd;
                                    if (manualSkillCd > 0)
                                    {
                                        TaskControl.Logger.LogInformation("{commandName}cd冷却为{skillCd}秒,剩余{Cd}秒,跳过此次行动",
                                            command.Name,
                                            manualSkillCd, Math.Round(cd, 2));
                                    }
                                    else
                                    {
                                        TaskControl.Logger.LogInformation("{CommandName}cd冷却剩余{Cd}秒,跳过此次行动", command.Name,
                                            Math.Round(cd, 2));
                                    }
                                }

                                // 避免重复log提示
                                skipFightName = command.Name;
                                continue;
                            }

                            // 表示这次执行命令没有跳过
                            skipFightName = "";
                        }

                        #endregion
                        
                        if (timeoutStopwatch.Elapsed > fightTimeout || AutoFightSeek.RotationCount >= rotationLimit)
                        {
                            TaskControl.Logger.LogInformation(AutoFightSeek.RotationCount >= rotationLimit ? "旋转次数达到上限，战斗结束" : "战斗超时结束");
                            fightEndFlag = true;
                            timeOutFlag = true;
                            FightEndTotoly  = true;
                            break;
                        }

                        #region Q前寻敌处理
                        if (_finishDetectConfig.RotateFindEnemyEnabled && _taskParam.CheckBeforeBurst && (command.Method == Method.Burst || command.Args.Contains("q") || command.Args.Contains("Q")))
                        {
                            if (_taskParam.FinishDetectConfig.RotationMode &&
                                _taskParam.FinishDetectConfig.RotateFindEnemyEnabled)
                            {
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        if (CheckFightFinish(0, detectDelayTime, cts2.Token, avatar).Result)
                                        {
                                            FightEndTotoly = true;
                                            fightEndFlag = true;
                                        }
                                    }
                                    catch (ObjectDisposedException)
                                    {
                                        // Token已释放，忽略
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        // 任务已取消，忽略
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.LogWarning(ex, "异步战斗结束检测异常");
                                    }
                                });
                                if(FightEndTotoly)break;
                            }
                            else
                            {
                                fightEndFlag = await CheckFightFinish(delayTime, detectDelayTime,cts2.Token,avatar);
                            }
                        }
                        #endregion
                        
                        if (_skipFlag)
                        {
                            continue; 
                        }
                        
                        Task.Run(() =>
                        {
                            try
                            {
                                if (_taskParam.FinishDetectConfig.RotationMode &&
                                    _taskParam.FinishDetectConfig.RotateFindEnemyEnabled &&
                                    CheckFightFinish(0, detectDelayTime, cts2.Token, avatar).Result||FightEndTotoly)
                                {
                                    FightEndTotoly = true;
                                    fightEndFlag = true;
                                }
                            }
                            catch (Exception ex)
                            {
                                Logger.LogWarning(ex, "异步战斗结束检测异常");
                            }
                        });
                        
                        //如果当前角色是玛薇卡，检测是否为摩托状态，如果是摩托状态且动作不是重击，则等待摩托状态结束，摩托状态结束后继续执行动作
                        Task.Run(() =>{
                            if (_taskParam.MavuikaMotorcycleCheckEnabled && avatar?.Name == "玛薇卡" && !command.Args.Contains("重击"))
                            {
                                //摩托状态才执行
                                using var region = CaptureToRectArea();
                                var pos = region.SrcMat.At<Vec3b>(991, 1678);
                                var pos2 = region.SrcMat.At<Vec3b>(991, 1728);
                                double colorDifference = Math.Sqrt(
                                    Math.Pow(pos.Item0 - pos2.Item0, 2) + // 蓝通道差值的平方
                                    Math.Pow(pos.Item1 - pos2.Item1, 2) + // 绿通道差值的平方
                                    Math.Pow(pos.Item2 - pos2.Item2, 2) // 红通道差值的平方
                                );
                                // Logger.LogInformation("玛薇卡蓄力颜色差值:{ColorDifference}", Math.Round(colorDifference, 2));
                                if (colorDifference < 15 && avatar.IsActive(region)) // 这个数值是通过观察大量截图得来的，摩托状态下差值一般在10-15之间，非摩托状态一般在20以上
                                {
                                    Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                                    // Logger.LogWarning("检测到玛薇卡处于摩托状态，等待摩托状态结束");
                                }
                            }
                        });
                        command.Execute(combatScenes, lastCommand);
                        //统计战斗人次
                        if (i == combatCommands.Count - 1 || command.Name != combatCommands[i + 1].Name)
                        {
                            countFight++;
                        }

                        #region check动作触发战斗结束检测
                        if (command.Method == Method.Check && _taskParam.FightFinishDetectEnabled)
                        {
                            if ((_taskParam.FinishDetectConfig.RotationMode &&
                             _taskParam.FinishDetectConfig.RotateFindEnemyEnabled) || _taskParam.FinishDetectConfig.PaimonEndModel)
                            {
                                Task.Run(() =>
                                {
                                    try
                                    {
                                        if (CheckFightFinish(0, detectDelayTime, cts2.Token, avatar).Result)
                                        {
                                            FightEndTotoly = true;
                                            fightEndFlag = true;
                                        }
                                    }
                                    catch (ObjectDisposedException)
                                    {
                                        // Token已释放，忽略
                                    }
                                    catch (OperationCanceledException)
                                    {
                                        // 任务已取消，忽略
                                    }
                                    catch (Exception ex)
                                    {
                                        Logger.LogWarning(ex, "异步战斗结束检测异常");
                                    }
                                });
                                if(FightEndTotoly)break;
                            }
                            else
                            {
                                fightEndFlag = await CheckFightFinish(delayTime, detectDelayTime,cts2.Token,avatar);
                            }
                        }
                        #endregion

                        lastFightName = command.Name;
                        if (!fightEndFlag && _taskParam is { FightFinishDetectEnabled: true })
                        {
                            //处于最后一个位置，或者当前执行人和下一个人名字不一样的情况，满足一定条件(开启快速检查，并且检查时间大于0或人名存在配置)检查战斗
                            if (i == combatCommands.Count - 1
                                || (
                                    _finishDetectConfig.FastCheckEnabled &&
                                    command.Name != combatCommands[i + 1].Name &&
                                    ((_finishDetectConfig.CheckTime > 0 &&
                                      checkFightFinishStopwatch.Elapsed > checkFightFinishTime)
                                     || _finishDetectConfig.CheckNames.Contains(command.Name))
                                ))
                            {
                                checkFightFinishStopwatch.Restart();
                               
                                if (_finishDetectConfig.DelayTimes.TryGetValue(command.Name, out var time))
                                {
                                    delayTime = time;
                                    // Logger.LogInformation($"{command.Name}结束后，延时检查为{delayTime}毫秒");
                                }
                                else
                                {
                                    // Logger.LogInformation($"延时检查为{delayTime}毫秒");
                                }

                                
                                if (_taskParam.FinishDetectConfig.RotationMode &&
                                    _taskParam.FinishDetectConfig.RotateFindEnemyEnabled)
                                {
                                    Task.Run(() =>
                                    {
                                        try
                                        {
                                            if (CheckFightFinish(0, detectDelayTime, cts2.Token, avatar).Result)
                                            {
                                                FightEndTotoly = true;
                                                fightEndFlag = true;
                                            }
                                        }
                                        catch (ObjectDisposedException)
                                        {
                                            // Token已释放，忽略
                                        }
                                        catch (OperationCanceledException)
                                        {
                                            // 任务已取消，忽略
                                        }
                                        catch (Exception ex)
                                        {
                                            Logger.LogWarning(ex, "异步战斗结束检测异常");
                                        }
                                    });
                                    if(FightEndTotoly)break;
                                }
                                else
                                {
                                    fightEndFlag = await CheckFightFinish(delayTime, detectDelayTime,cts2.Token,avatar);
                                }
                                
                            }
                        }

                        if (fightEndFlag)
                        {
                            FightEndTotoly  = true;
                            break;
                        }
                    }


                    if (fightEndFlag)
                    {
                        FightEndTotoly  = true;
                        break;
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
                FightStatusFlag = false;
                FightEndTotoly  = true;
                image?.Dispose();
                // 移除原 GC.Collect() + GC.WaitForPendingFinalizers() 同步阻塞续命：
                // 该续命补丁是为了兜底回收 ImageRegion 未释放的原生 Mat（旧 bug：ImageRegion.Dispose
                // 用 new 隐藏导致 using/接口/基类引用释放时走空的 Region.Dispose，SrcMat 永不释放）。
                // 根因已修（Region.Dispose 改 virtual、ImageRegion.Dispose 改 override），
                // SrcMat 现在随 using 正常释放，无需在战斗结束关键路径上同步阻塞等终结器（消除 2~3 秒停顿的 GC 部分）。
                Dispatcher.IsCustomCts = false;
            }
        }, cts2.Token);

        await fightTask;
        await WaitForRewardEndDetectionTaskAsync(rewardTimeoutEndTask);

        // 奖励识别结束与战斗超时共用 timeOutFlag 行为，例如不执行换队拾取。
        if (Volatile.Read(ref rewardTimeoutEndFlag) == 1)
        {
            timeOutFlag = true;
        }

        StopSharedFightEndCoordination(__quorumCoordinator, __onAllFightDone);

        // fight-end-return-loop-not-joined-movement-overlap-fix:
        // 战斗结束：先 cancel 回点 CTS 立即打断进行中的 MoveCloseTo(万叶)/MoveTo(通用)，
        // 再 join 两条后台回点循环，确保进入 PathExecutor 战后走回点流程前无并行移动子系统。
        // 无循环（单机/联机非万叶+总开关关）时两 Task 引用均 null → 整段跳过（单机零回归）。
        if (ReturnLoopJoinDecisions.ShouldJoin(_kazuhaReturnLoopTask != null, _generalReturnLoopTask != null))
        {
            try { _returnLoopCts?.Cancel(); }
            catch (Exception ex) { Logger.LogWarning(ex, "[回点][join] cancel 回点 CTS 异常，忽略并继续 join"); }

            var __returnLoopTasks = new List<Task>();
            if (_kazuhaReturnLoopTask != null) __returnLoopTasks.Add(_kazuhaReturnLoopTask);
            if (_generalReturnLoopTask != null) __returnLoopTasks.Add(_generalReturnLoopTask);
            try
            {
                var __all = Task.WhenAll(__returnLoopTasks);
                var __winner = await Task.WhenAny(__all, Task.Delay(ReturnLoopJoinTimeoutMs));
                if (__winner != __all)
                {
                    Logger.LogWarning("[回点][join] 等待后台回点循环结束超时({Timeout}ms)，cancel 已发出，继续战后流程", ReturnLoopJoinTimeoutMs);
                }
                else if (__all.IsFaulted)
                {
                    // 循环内部已 catch OperationCanceledException return，正常不抛；此处兜底意外异常
                    Logger.LogWarning(__all.Exception, "[回点][join] 后台回点循环以异常结束，已忽略");
                }
            }
            catch (Exception ex)
            {
                // join 自身不可抛出致 Start 崩；循环内异常 / cancel 引发的 OCE 在此兜底吞掉并记录
                Logger.LogWarning(ex, "[回点][join] join 后台回点循环异常，已忽略并继续");
            }
            finally
            {
                try { _returnLoopCts?.Dispose(); } catch { /* CTS 已 dispose 可恢复，忽略 */ }
                _returnLoopCts = null;
                _kazuhaReturnLoopTask = null;
                _generalReturnLoopTask = null;
            }
        }

        if (_taskParam.KazuhaPickupEnabled && _taskParam.ExpKazuhaPickup && !_isExperiencePickup && (combatScenes.GetAvatars().Select( a => a.Name).Contains("枫原万叶") || combatScenes.GetAvatars().Select( a => a.Name).Contains("琴")))
        {
            TaskControl.Logger.LogInformation("基于怪物经验判断：{text} 经验值显示","等待");

            var ms = _taskParam.FinishDetectConfig.RotationMode && _taskParam.FinishDetectConfig.RotateFindEnemyEnabled ? 1800:1000;
            while (!_isExperiencePickup && ms > 0)
            {
                // Logger.LogError("战斗人次低于配置人次，且未检测到经验值显示，继续等待经验值显示，剩余等待时间{ms}ms-11", ms);
                ms -= 100;
                await Delay(100, ct);
            }
        }
        FightEndFlag = true; 

        if ((_taskParam.BattleThresholdForLoot >= 2 && countFight < _taskParam.BattleThresholdForLoot) && (!_taskParam.ExpKazuhaPickup || !_isExperiencePickup))
        {
            TaskControl.Logger.LogInformation($"战斗人次（{countFight}）低于配置人次（{_taskParam.BattleThresholdForLoot}），跳过此次拾取！");
            
            if (_taskParam.EndBloodCheackEnabled)
            {
                //防止检测战斗结束时，派蒙头冠消失
                using var ra = CaptureToRectArea();
                var pixelValue = ra.SrcMat.At<Vec3b>(32, 67);
                ra.Dispose();
                // 检查每个通道的值是否在允许的范围内
                if (!(Math.Abs(pixelValue[0] - 143) <= 10 &&
                      Math.Abs(pixelValue[1] - 196) <= 10 &&
                      Math.Abs(pixelValue[2] - 233) <= 10))
                {
                    await Delay(1000, ct);
                }
            
                await EndBloodCheck(ct,combatScenes);
            }
            
            return;
        }
      
        if(_taskParam.KazuhaPickupEnabled && _taskParam.ExpKazuhaPickup) TaskControl.Logger.LogInformation("基于怪物经验判断：{text} 万叶拾取", _isExperiencePickup? "执行" : "不执行");
        
        if (_taskParam.KazuhaPickupEnabled && (!_taskParam.ExpKazuhaPickup || _isExperiencePickup))
        {
            // Logger.LogInformation("开始 _isExperiencePickup：{_isExperiencePickup}",_isExperiencePickup);
            // 队伍中存在万叶的时候使用一次长E
            var picker = combatScenes.SelectAvatar("枫原万叶") ?? combatScenes.SelectAvatar("琴");
            
            string? oldPartyName = null;
            if (RunnerContext.Instance.PartyName is not null)
            {
                 oldPartyName = RunnerContext.Instance.PartyName;
            }
            else if(picker is null && !string.IsNullOrEmpty(_taskParam.KazuhaPartyName))
            {
                Logger.LogWarning("换队拾取：当前队伍名称为空，尝试读取");
                await Delay(1000, ct);
                await _returnMainUiTask.Start(ct);

                for( int attempt = 0; attempt < 6; attempt++)
                {
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    var enterGameAppear = await NewRetry.WaitForElementAppear(
                        ElementAssets.Instance.PartyBtnChooseView,
                        () => { },
                        ct,
                        15,
                        500
                    );
                    if (enterGameAppear)
                    {
                        Logger.LogInformation("换队拾取：成功打开队伍界面");
                        break;
                    }
                    
                    if(attempt == 5 && !enterGameAppear)
                    {
                        Logger.LogWarning("换队拾取：读取队伍名称失败，跳过换队拾取步骤");
                    }
                }
                
                await Delay(1000, ct);
                
                //等待寻找2秒队伍按钮出现
                var timeWaitStart = 0;
                while(timeWaitStart < 6000)
                {
                    using var ra = CaptureToRectArea();
                    var partyViewBtn = ra.Find(ElementAssets.Instance.PartyBtnChooseView);
                    if (partyViewBtn.IsExist())
                    {
                        // OCR 当前队伍名称（无法单字，中间禁止空格）
                      // 读取OCR原始识别文本
                      var rawPartyName = ra.Find(new RecognitionObject
                      {
                          RecognitionType = RecognitionTypes.Ocr,
                          RegionOfInterest = new Rect(partyViewBtn.Right, partyViewBtn.Top, (int)(350 * _assetScale),
                              partyViewBtn.Height)
                      }).Text;
                      
                      // 核心处理逻辑：1.空值兜底 2.去首尾空白 3.移除末尾的“口”字（仅最后一个是口才删）
                      if (string.IsNullOrWhiteSpace(rawPartyName))
                      {
                          oldPartyName = string.Empty;
                      }
                      else
                      {
                          var tempName = rawPartyName
                              .Replace("\"", "")        // 移除所有双引号（核心新增，解决日志里的""问题）
                              .Replace("\r\n", "")      // 清理Windows换行符
                              .Replace("\r", "");   // 先清理所有双引号，避免引号干扰后续处理
                              
                              // 核心逻辑：找到第一个换行符(\n)的位置，截断并删除换行+后面所有字符
                              int firstNewLineIndex = tempName.IndexOf('\n');
                              if (firstNewLineIndex != -1) // 存在换行符，截取到换行符前
                              {
                                  tempName = tempName.Substring(0, firstNewLineIndex);
                              }
                          
                              // 最后统一去首尾所有空白（空格、制表符、回车符\r等），得到纯净队伍名
                              oldPartyName = tempName.Trim();
                      }
                      
                      // 后续原有逻辑不变
                      Logger.LogInformation("换队拾取：当前队伍名称读取为：{oldPartyName}", oldPartyName);
                      // 加在rawPartyName赋值后，打印原始文本的“原始形态”（转义符会显示）
                      Logger.LogDebug("OCR原始识别文本（含转义）：{rawPartyName}", rawPartyName);
                      RunnerContext.Instance.PartyName = oldPartyName;
                        // await _returnMainUiTask.Start(ct);
                        break;
                    }
                    await Delay(200, ct);
                    timeWaitStart += 200;
                }
            }
            
            var switchPartyFlag = false;
            if (picker == null && !timeOutFlag &&!string.IsNullOrEmpty(_taskParam.KazuhaPartyName) && oldPartyName != _taskParam.KazuhaPartyName)
            {
                try
                {
                    TaskControl.Logger.LogInformation($"切换为拾取队伍：{_taskParam.KazuhaPartyName}");
                    var success = await new SwitchPartyTask().Start(_taskParam.KazuhaPartyName, ct);
                    if (success)
                    {
                        TaskControl.Logger.LogInformation($"成功切换队伍为{_taskParam.KazuhaPartyName}");
                        switchPartyFlag = true;
                        RunnerContext.Instance.PartyName = _taskParam.KazuhaPartyName;
                        RunnerContext.Instance.ClearCombatScenes();
                        var cs = await RunnerContext.Instance.GetCombatScenes(ct);
                        picker = cs.SelectAvatar("枫原万叶") ?? cs.SelectAvatar("琴");
                    }
                }
                catch (Exception e)
                {
                    TaskControl.Logger.LogInformation("切换队伍异常，跳过此步骤！");
                }

            }
            
            if (picker != null)
            {

                var ms = 2000;
                while (!_2ndEndFlag && ms > 0)
                {
                    // Logger.LogWarning("等待万叶/琴技能CD-999999999，剩余等待时间{ms}ms", ms);
                    ms -= 100;
                    await Delay(100, ct);
                }
                
                if (picker.Name == "枫原万叶")
                {
                    var time = TimeSpan.FromSeconds(picker.GetSkillCdSeconds());
                    // 如果配置了二次拾取，或者不满足跳过条件（上次是万叶且冷却时间>3秒），则执行拾取
                    bool shouldSkip = lastFightName == picker.Name && time.TotalSeconds > 3;
                    bool forcePickup = _taskParam.QinDoublePickUp;
                    
                    if (forcePickup || !shouldSkip)
                    {
                        TaskControl.Logger.LogInformation("使用 枫原万叶-长E 拾取掉落物");
                        await Delay(50, ct);
                        if (picker.TrySwitch(20))
                        {
                            await Delay(50, ct);
                            if (await AutoFightSkill.AvatarSkillAsync(Logger, picker, false, 1, ct))
                            {
                                await picker.WaitSkillCd(ct);
                            }
                            //判断万叶是否出战，如果出战了才执行后续操作
                            using var ra = CaptureToRectArea();
                            if (!picker.IsActive(ra))
                            {
                                picker.TrySwitch(20);
                            }
                            picker.UseSkill(true);
                            await Delay(50, ct);
                            Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                            if (!await AutoFightSkill.AvatarSkillAsync(Logger, picker, false, 1, ct))
                            {
                                Logger.LogWarning("万叶长E技能未成功释放，尝试再次释放");
                                picker.TrySwitch(20);
                                await Delay(50, ct);
                                picker.UseSkill(true);
                                await Delay(50, ct);
                                // 调用统一的辅助方法，模拟万叶长按 E 的输入序列：
                                // 包含释放鼠标左键前摇防卡键 -> E 键 KeyDown -> 延时 800ms -> E 键 KeyUp -> 延时 50ms
                                await SimulateHoldElementalSkillAsync(800, ct);    
                            
                                // 调用统一的辅助方法，模拟 6 次鼠标左键连续点击：
                                // 配合万叶长 E 的滞空特性执行下落攻击，内部包含 try/finally 以保证取消任务时安全释放左键
                                await SimulateMouseLeftClickLoopAsync(6, ct);      

                            }
                            await Delay(_taskParam.KazuhaTime, ct);
                            picker.AfterUseSkill();
                        }
                    }
                    else
                    {
                        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                        picker.TrySwitch(20);
                        TaskControl.Logger.LogInformation("距最近一次万叶出招，时间过短，跳过此次万叶拾取！");
                        if (!await AutoFightSkill.AvatarSkillAsync(Logger, picker, false, 1, ct))
                        {
                            // Logger.LogWarning("11111111");
                            picker.UseSkill(true);
                        }
                        else
                        {
                            using var ra = CaptureToRectArea();
                            if (!picker.IsActive(ra))
                            {
                                picker.TrySwitch(20);
                            }

                            // Logger.LogWarning("222222");
                            picker.UseSkill(true);
                        }
                        Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                    }
                }
                else if (picker.Name == "琴")
                {
                    TaskControl.Logger.LogInformation("使用 琴-长E 拾取掉落物");
                    
                    var actionsToUse = PickUpCollectHandler.PickUpActions
                        .Where(action => action.StartsWith("琴-长E" + " ", StringComparison.OrdinalIgnoreCase))
                        .Select(action => action.Replace("琴-长E","琴", StringComparison.OrdinalIgnoreCase))
                        .ToArray();

                    var find = _taskParam.QinDoublePickUp;
                    await Delay(150, ct);
                    if (picker.TrySwitch(10))
                    {
                        if (await AutoFightSkill.AvatarSkillAsync(Logger, picker, false, 1, ct))//有祭礼情况下可能CD已经好了
                        {
                            await picker.WaitSkillCd(ct);
                        }
                        foreach (var miningActionStr in actionsToUse)
                        {
                            var pickUpAction = CombatScriptParser.ParseContext(miningActionStr);

                            for (int i = 0; i < 2; i++)
                            {
                                foreach (var command in pickUpAction.CombatCommands)
                                {
                                    command.Execute(combatScenes);
                                    //异步执行，防止卡顿
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
                                            catch (Exception e)
                                            {
                                                Logger.LogError(e, "琴拾取物品异常");
                                                find = false;
                                                throw;
                                            }
                                            finally
                                            {
                                                GC.Collect();//释放内存
                                                GC.WaitForPendingFinalizers();//释放内存
                                                Monitor.Exit(PickLock);
                                            }
                                        }
                                        // 后面没代码了，不用写return？
                                    });
                                }

                                if (!find)
                                {
                                    break;
                                }

                                if (i == 0)
                                {
                                    Logger.LogInformation("自动拾取；尝试再次执行 琴-长E 拾取");
                                    await picker.WaitSkillCd(ct);
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
            //切换过队伍的，需要再切回来
            if (switchPartyFlag && !string.IsNullOrEmpty(oldPartyName))
            {
                try
                {
                    TaskControl.Logger.LogInformation($"切换为原队伍：{oldPartyName}");
                    var success = await new SwitchPartyTask().Start(oldPartyName, ct);
                    if (success)
                    {
                        TaskControl.Logger.LogInformation($"切换为原队伍{oldPartyName}");
                        switchPartyFlag = true;
                        RunnerContext.Instance.PartyName = oldPartyName;
                        RunnerContext.Instance.ClearCombatScenes();
                        await RunnerContext.Instance.GetCombatScenes(ct);
    
                    }
                }
                catch (Exception e)
                {
                    TaskControl.Logger.LogInformation("恢复原队伍失败，跳过此步骤！");
                }
                    
            }
        }
        
        if (_taskParam is { PickDropsAfterFightEnabled: true } )
        {
            // 执行扫描掉落物光柱并靠近的功能
            await new ScanPickTask().Start(ct);
        }

        if (_taskParam.EndBloodCheackEnabled)
        {
            // if(!Bv.IsInBigMapUi(CaptureToRectArea()))
            //防止检测战斗结束时，派蒙头冠消失
            using var ra = CaptureToRectArea();
            var pixelValue = ra.SrcMat.At<Vec3b>(32, 67);
            // 检查每个通道的值是否在允许的范围内
            if (!(Math.Abs(pixelValue[0] - 143) <= 10 &&
                  Math.Abs(pixelValue[1] - 196) <= 10 &&
                  Math.Abs(pixelValue[2] - 233) <= 10))
            {
                await Delay(1000, ct);
            }
            
            await EndBloodCheck(ct,combatScenes);

            PathingConditionConfig.CombatScenesGoBackUp = combatScenes;
            Simulation.ReleaseAllKey(); 
            
        }
    }

    private void LogScreenResolution()
    {
        AssertUtils.CheckGameResolution("自动战斗");
    }

    static bool AreDifferencesWithinBounds((int, int, int) a, (int, int, int) b, (int, int, int) c)
    {
        // 计算每个位置的差值绝对值并进行比较
        return Math.Abs(a.Item1 - b.Item1) < c.Item1 &&
               Math.Abs(a.Item2 - b.Item2) < c.Item2 &&
               Math.Abs(a.Item3 - b.Item3) < c.Item3;
    }
    
    private volatile bool _totolyFlag = false;
    
    private volatile int _totolyEndCount = 0;
    
    private volatile bool _2ndEndFlag = false;

    // === 共享战斗配额结束同步状态（multiplayer-shared-fight-end-quorum-sync spec）===
    // 仅当 IsEnabled（联机+连接+房主开关）时启用；单机/开关关时三字段保持默认，CheckFightFinish 行为一字不变。
    private volatile bool _quorumVoted;            // 本地是否已投过 done 票（每场战斗一次）
    private volatile bool _allFightDoneReceived;   // 是否已收到本场 syncKey 的 AllFightDone 广播
    private string _currentFightSyncKey = "";      // 本场战斗 syncKey（routeIndex:X:Y）

    // === 共享战斗配额结束同步：订阅 AllFightDone + 上报参与者（subscribe-before-action）===
    // 仅当 IsEnabled（联机+连接+房主开关）时启用；否则三字段保持默认，全程零回归。
    private (MultiplayerCoordinator? Coordinator, Action<string>? AllFightDoneHandler) StartSharedFightEndCoordination()
    {
        _quorumVoted = false;
        _allFightDoneReceived = false;
        _currentFightSyncKey = "";

        var coordinator = PathExecutor.CurrentMultiplayerCoordinator;
        if (!SharedFightEndQuorumDecisions.IsEnabled(
                coordinator != null,
                coordinator?.IsConnected ?? false,
                coordinator?.EffectiveConfig.SharedFightEndQuorumEnabled ?? false))
        {
            return (coordinator, null);
        }

        var waypoint = FightWaypoint;
        var routeIndex = coordinator!.CurrentRouteIndex;
        _currentFightSyncKey = waypoint == null
            ? $"{routeIndex}:0:0"
            : $"{routeIndex}:{waypoint.X.ToString("R", CultureInfo.InvariantCulture)}:{waypoint.Y.ToString("R", CultureInfo.InvariantCulture)}";

        Action<string> onAllFightDone = key =>
        {
            if (key != _currentFightSyncKey)
            {
                return;
            }

            _allFightDoneReceived = true;
            FightEndTotoly = true; // 立即打断仍在酣战的主循环
            Logger.LogInformation("[联机][结束配额] 收到全队战斗结束广播，结束本场战斗 syncKey={Key}", _currentFightSyncKey);
        };

        coordinator.Client.AllFightDone += onAllFightDone;
        // 先订阅再上报参与者（配额分母）；与投票同源 syncKey 保证一致
        _ = coordinator.ReportFightParticipantAsync(_currentFightSyncKey);
        Logger.LogInformation("[联机][结束配额] 已启用，订阅 AllFightDone 并上报参与者 syncKey={Key}", _currentFightSyncKey);
        return (coordinator, onAllFightDone);
    }

    // === 共享战斗配额结束同步：解订阅 AllFightDone（subscribe-before-action 配对清理）===
    private static void StopSharedFightEndCoordination(
        MultiplayerCoordinator? coordinator,
        Action<string>? onAllFightDone)
    {
        if (coordinator != null && onAllFightDone != null)
        {
            coordinator.Client.AllFightDone -= onAllFightDone;
        }
    }

    /// <summary>
    /// 共享战斗配额结束协调：返回 true=应立即真结束（维持原语义 / 已收到全队广播）；
    /// false=已投票，继续战斗循环等待 AllFightDone 或超时（multiplayer-shared-fight-end-quorum-sync spec）。
    /// 未启用（单机/断连/开关关）时恒返回 true → 调用方走原"立即结束"逻辑（零回归）。
    /// </summary>
    private bool TryCoordinateSharedFightEnd()
    {
        var coordinator = BetterGenshinImpact.GameTask.AutoPathing.PathExecutor.CurrentMultiplayerCoordinator;
        if (!SharedFightEndQuorumDecisions.IsEnabled(
                coordinator != null,
                coordinator?.IsConnected ?? false,
                coordinator?.EffectiveConfig.SharedFightEndQuorumEnabled ?? false))
        {
            return true; // 未启用：维持原立即结束语义
        }

        if (_allFightDoneReceived) return true; // 已收到全队广播 → 真结束

        if (!_quorumVoted)
        {
            _quorumVoted = true;
            _ = coordinator!.ReportFightDoneAsync(_currentFightSyncKey); // fire-and-forget，内部静默失败
            Logger.LogInformation("[联机][结束配额] 本地判定结束，已投票 done，继续战斗等待全队 syncKey={Key}", _currentFightSyncKey);
        }
        return false; // 继续战斗，不离开战斗点
    }

    public async Task<bool> CheckFightFinish(int delayTime = 1500, int detectDelayTime = 450,CancellationToken ct = default,Avatar? avatar = null)
    {
        if (_totolyFlag || _fightDurationExceeded)
        {
            return false;
        }

        // 共享战斗配额结束（multiplayer-shared-fight-end-quorum-sync spec, design §11.4）：
        // 已投票 → 不再按 L 做视觉检测（OpenPartySetupScreen），无论是否已收到广播（根除 D3 的"结束前多 L 一次"）。
        //   - 已收到全队 AllFightDone 广播（_allFightDoneReceived=true）→ return true 真结束；
        //   - 未收到广播 → return false 继续战斗输出，仅等广播（handler 置 FightEndTotoly）或战斗超时兜底。
        // _quorumVoted 仅在功能启用时被置 true，单机/开关关时恒 false → 零回归。
        if (_quorumVoted)
        {
            return _allFightDoneReceived;
        }

        if(_totolyEndCount >= 1)
        {
            // 共享战斗配额结束门控：未启用走原逻辑；启用则投票后继续战斗直到广播/超时
            if (!TryCoordinateSharedFightEnd())
            {
                _totolyFlag = false;
                return false;
            }
            Logger.LogWarning("二次检查：战斗结束。");
            _2ndEndFlag = true;
            FightEndTotoly = true;
            _totolyFlag = false;
            return true;
        }
        
        _totolyFlag = true;

        var doubleEndLogo = true;
        using var captureToRectArea = CaptureToRectArea();
        var pixelValue = captureToRectArea.SrcMat.At<Vec3b>(32, 67); 
        var paiMon = (Math.Abs(pixelValue[0] - 143) <= 10 &&
                      Math.Abs(pixelValue[1] - 196) <= 10 &&
                      Math.Abs(pixelValue[2] - 233) <= 10);
        if (!paiMon)
        {
            _totolyFlag = false;
            return false;
        }

        if (Dispatcher.IsCustomCts)
        {
            _totolyFlag = false;
            return false;
        }
        if (_finishDetectConfig.RotateFindEnemyEnabled)
        {
            bool? result = null;
            try
            {
                if (_taskParam.FinishDetectConfig.RotationMode&& _taskParam.FinishDetectConfig.RotateFindEnemyEnabled)
                {
                    Task.Run(async () =>
                    {
                        result = await AutoFightSeek.SeekAndFightAsync(TaskControl.Logger, detectDelayTime, delayTime, ct,false,_taskParam.RotaryFactor,avatar,_taskParam.FinishDetectConfig.GoDistance,_taskParam.FinishDetectConfig.EndModel,_taskParam.FinishDetectConfig.RotationMode,
                            kazuhaContinuousReturn: _taskParam.KazuhaContinuousReturn,
                            returnIntervalMs: 1000,
                            returnDistanceThreshold: 1.0); 
                        AutoFightSeek.RotationCount = (result == null) ? 
                            AutoFightSeek.RotationCount + 1 :  0;
                    }, ct);  
                    
                }
                else
                {
                    result = await AutoFightSeek.SeekAndFightAsync(TaskControl.Logger, detectDelayTime,  delayTime, ct,false,_taskParam.RotaryFactor,avatar,_taskParam.FinishDetectConfig.GoDistance,_taskParam.FinishDetectConfig.PaimonEndModel? _taskParam.FinishDetectConfig.PaimonEndModel:_taskParam.FinishDetectConfig.EndModel,_taskParam.FinishDetectConfig.RotationMode,
                        kazuhaContinuousReturn: _taskParam.KazuhaContinuousReturn,
                        returnIntervalMs: 1000,
                        returnDistanceThreshold: 1.0); 
                    AutoFightSeek.RotationCount = (result == null) ? 
                        AutoFightSeek.RotationCount + 1 :  0;
                }
            }
            catch (Exception ex)
            {
                TaskControl.Logger.LogError(ex, "SeekAndFightAsync 方法发生异常");
                _totolyFlag = false;
                return true;
            }
            
            if (result != null)
            {
                _totolyFlag = false;
                return result.Value;
            }
        }

        if (_finishDetectConfig.RotateFindEnemyEnabled && !_taskParam.FinishDetectConfig.EndModel)await Delay(delayTime, _ct);
        
        // Logger.LogInformation("打开编队界面检查战斗是否结束{detectDelayTime} {delayTime}",detectDelayTime,delayTime);

        for (int i = 0; i < 2; i++)
        {
            if (i == 1)
            {
                using var captureToRectArea2 = CaptureToRectArea();
                var pixelValue22 = captureToRectArea.SrcMat.At<Vec3b>(32, 67); 
                var paiMon22 = (Math.Abs(pixelValue22[0] - 143) <= 10 &&
                              Math.Abs(pixelValue22[1] - 196) <= 10 &&
                              Math.Abs(pixelValue22[2] - 233) <= 10);
                if (!paiMon22)
                {
                    _totolyEndCount = 0;
                    _totolyFlag = false;
                    return false;
                }
            }
            
            Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
            await Delay(detectDelayTime, _ct);

            // await Delay(80, _ct);
            using var ra = CaptureToRectArea();
            Simulation.SendInput.SimulateAction(GIActions.Drop);

            Vec3b pixelValue2;
            var paiMon2 = false;
            if ((_taskParam.FinishDetectConfig.EndModel && _taskParam.FinishDetectConfig.RotateFindEnemyEnabled) ||
                _taskParam.FinishDetectConfig.PaimonEndModel)
            {
                pixelValue2 = ra.SrcMat.At<Vec3b>(32, 67); //派蒙
                paiMon2 = (Math.Abs(pixelValue2[0] - 143) <= 10 &&
                           Math.Abs(pixelValue2[1] - 196) <= 10 &&
                           Math.Abs(pixelValue2[2] - 233) <= 10);
            }
            else
            {
                pixelValue2 = ra.SrcMat.At<Vec3b>(50, 790); //进度条颜色
                var whiteTile = ra.SrcMat.At<Vec3b>(50, 768); //白块
                paiMon2 = !(IsWhite(whiteTile.Item2, whiteTile.Item1, whiteTile.Item0) &&
                            IsYellow(pixelValue2.Item2, pixelValue2.Item1,
                                pixelValue2.Item0));
            }

            var aa = AutoFightSkill.MedicinalCdAsync(Logger, true, 1, ct).Result;
            
            if (!paiMon2 && !aa)
            {
                // 优先检测复活弹窗，避免弹窗滤镜导致派蒙像素不匹配而误判战斗结束
                using var popupCheck = CaptureToRectArea();
                var reviveConfirmRa = popupCheck.Find(AutoFightAssets.Instance.ConfirmRa);
                if (reviveConfirmRa.IsExist())
                {
                    TaskControl.Logger.LogInformation("派蒙模式：检测到复活弹窗，主动处理");
                    await Delay(100, _ct);
                    reviveConfirmRa.Click(); // 点击确认（尝试复活）
                    await Delay(300, _ct);

                    // 检测弹窗是否仍在（复活药CD时确认无效，弹窗不会关闭）
                    using var popupCheck2 = CaptureToRectArea();
                    var reviveExitRa = popupCheck2.Find(AutoFightAssets.Instance.ExitRa);
                    if (reviveExitRa.IsExist())
                    {
                        reviveExitRa.Click(); // 点击取消关闭弹窗
                        TaskControl.Logger.LogInformation("派蒙模式：复活药可能在CD，点击取消关闭弹窗");
                        await Delay(200, _ct);
                        reviveExitRa.ClickTo(-150,0);
                    }

                    _totolyEndCount = 0;
                    _totolyFlag = false;
                    return false; // 战斗未结束
                }

                if (_taskParam.FinishDetectConfig.PaimonEndModel && _taskParam.FinishDetectConfig.DoubleEndEnbled && doubleEndLogo)
                {
                    _skipFlag = true;
                    FightEndTotoly = false;
                    Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                    Logger.LogInformation("派蒙模式：进行二次检测，延时 {doubleEndDelay} ms", _taskParam.FinishDetectConfig.DoubleEndDelay);
                    doubleEndLogo = false;
                    _totolyEndCount = _totolyEndCount + 1;
                    await Delay(_taskParam.FinishDetectConfig.DoubleEndDelay, _ct);
                    _skipFlag = false;
                    continue;
                }

                using var bitmap = CaptureToRectArea();
                var confirmRa = bitmap.Find(AutoFightAssets.Instance.ConfirmRa);
                if (confirmRa.IsExist())
                {
                    TaskControl.Logger.LogInformation("识别到确认界面，可能是误判，继续战斗");
                    _totolyEndCount = 0;
                    return false;
                }

                TaskControl.Logger.LogInformation("{t}：识别到战斗结束",
                    _taskParam.FinishDetectConfig.EndModel && _taskParam.FinishDetectConfig.RotateFindEnemyEnabled
                        ? "派蒙模式"
                        : "默认模式");
                // 共享战斗配额结束门控：未启用走原逻辑；启用则投票后继续战斗直到广播/超时
                if (!TryCoordinateSharedFightEnd())
                {
                    _totolyFlag = false;
                    return false;
                }
                //取消正在进行的换队
                _2ndEndFlag = true;
                FightEndTotoly = true;
                _totolyEndCount = _totolyEndCount + 1;
                Simulation.SendInput.SimulateAction(GIActions.OpenPartySetupScreen);
                _totolyFlag = false;
                return true;
            }

            if ((_taskParam.RotaryFactor != 1 && !_taskParam.FinishDetectConfig.EndModel))
                Logger.LogInformation("{t}：未识别到战斗结束",
                    _taskParam.FinishDetectConfig.EndModel && _taskParam.FinishDetectConfig.RotateFindEnemyEnabled
                        ? "快速模式"
                        : "默认模式");

            if (_finishDetectConfig.RotateFindEnemyEnabled && _taskParam.RotaryFactor != 1)
            {
                try
                {
                    Task.Run(() =>
                    {
                        Scalar bloodLower = new Scalar(255, 90, 90);
                        MoveForwardTask.MoveForwardAsync(bloodLower, bloodLower, TaskControl.Logger, _ct,
                            _taskParam.FinishDetectConfig.GoDistance);
                    }, _ct);
                }
                catch (Exception ex)
                {
                    TaskControl.Logger.LogError($"任务运行时发生异常: {ex.Message}");
                }
            }

            _lastFightFlagTime = DateTime.UtcNow;
            _totolyEndCount = 0;
            _totolyFlag = false;
            return false;
        }
        _totolyEndCount = 0;
        return false;
    }

    bool IsYellow(int r, int g, int b)
    {
        //Logger.LogInformation($"IsYellow({r},{g},{b})");
        // 黄色范围：R高，G高，B低
        return (r >= 200 && r <= 255) &&
               (g >= 200 && g <= 255) &&
               (b >= 0 && b <= 100);
    }

    bool IsWhite(int r, int g, int b)
    {
        //Logger.LogInformation($"IsWhite({r},{g},{b})");
        // 白色范围：R高，G高，B低
        return (r >= 240 && r <= 255) &&
               (g >= 240 && g <= 255) &&
               (b >= 240 && b <= 255);
    }

    private static string GetRewardEndDetectionTypeName(RewardEndDetectionType rewardType)
    {
        return rewardType == RewardEndDetectionType.Experience ? "经验值" : "摩拉";
    }

    private static string GetRewardEndDetectionModeName(RewardEndDetectionType rewardType)
    {
        return rewardType == RewardEndDetectionType.Experience ? "经验" : "摩拉";
    }

    private static void LogRewardEndDetectionEnabled(RewardEndDetectionConfig? config)
    {
        if (config is null)
        {
            return;
        }

        var valueDescription = config.Type == RewardEndDetectionType.Mora && config.MoraValues is not null
            ? $"（{string.Join("/", config.MoraValues.Order())}）"
            : "";
        Logger.LogInformation("当前地图追踪战斗点启用{RewardType}{RewardValues}识别结束战斗",
            GetRewardEndDetectionTypeName(config.Type),
            valueDescription);
    }

    private void HandleRewardEndDetected(RewardEndDetectionConfig config)
    {
        TaskControl.Logger.LogInformation("{RewardType}检测模式：识别到战斗结束",
            GetRewardEndDetectionModeName(config.Type));
        // 单机/配额关闭时立即结束；配额开启时只投票，等待 AllFightDone 广播统一结束。
        if (TryCoordinateSharedFightEnd())
        {
            FightEndTotoly = true;
        }
    }

    private static async Task WaitForRewardEndDetectionTaskAsync(Task? rewardEndDetectionTask)
    {
        if (rewardEndDetectionTask is null)
        {
            return;
        }

        // 战斗结束后最多等待 500ms 收尾，不能让后台识别阻塞后续地图追踪。
        var waitTask = await Task.WhenAny(rewardEndDetectionTask, Task.Delay(500));
        if (waitTask == rewardEndDetectionTask && rewardEndDetectionTask.IsFaulted)
        {
            Logger.LogWarning(rewardEndDetectionTask.Exception, "奖励后台识别任务异常，已忽略");
        }
    }

    private Task StartRewardTimeoutEndLoop(CancellationToken token, RewardEndDetectionConfig config, Action<int> onFound)
    {
        return Task.Run(async () =>
        {
            // 首次命中即退出；未命中时按 200ms 间隔检测，直到战斗结束或任务取消。
            while (!token.IsCancellationRequested && !FightEndTotoly)
            {
                try
                {
                    if (CheckRewardForTimeoutEnd(config, out var rewardValue))
                    {
                        onFound(rewardValue);
                        return;
                    }

                    await Task.Delay(200, token);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (ObjectDisposedException)
                {
                    return;
                }
                catch (Exception ex)
                {
                    Logger.LogDebug(ex, "奖励后台识别异常");
                    await Task.Delay(200, token);
                }
            }
        }, token);
    }

    private bool CheckRewardForTimeoutEnd(RewardEndDetectionConfig config, out int rewardValue)
    {
        rewardValue = 0;

        return config.Type switch
        {
            RewardEndDetectionType.Experience => CheckExperienceRewardByTemplate(out rewardValue),
            _ => CheckMoraRewardByTemplate(config, out rewardValue)
        };
    }

    private bool CheckMoraRewardByTemplate(RewardEndDetectionConfig config, out int mora)
    {
        mora = 0;

        try
        {
            using var ra = CaptureToRectArea();
            foreach (var moraRewardRa in AutoFightAssets.Instance.MoraRewardRas)
            {
                if (!int.TryParse(moraRewardRa.Name?.Replace("mora_", ""), out var templateMora) ||
                    !config.IsMoraValueEnabled(templateMora))
                {
                    continue;
                }

                var found = ra.Find(moraRewardRa);
                if (!found.IsExist())
                {
                    continue;
                }

                mora = templateMora;
                return true;
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "摩拉奖励模板识别异常");
        }

        return false;
    }

    private bool CheckExperienceRewardByTemplate(out int experience)
    {
        experience = 0;

        try
        {
            using var ra = CaptureToRectArea();
            foreach (var experienceRa in AutoFightAssets.Instance.ExperienceRewardRas)
            {
                var found = ra.Find(experienceRa);
                if (!found.IsExist())
                {
                    continue;
                }

                var iconX = found.X - 147;
                if (iconX < 0)
                {
                    continue;
                }

                // 同时校验数值左侧经验图标的特征色，避免把其他白色 UI 数字当成经验值。
                var pixelValue = ra.SrcMat.At<Vec3b>(found.Y, iconX);
                if (pixelValue[0] != 253 || pixelValue[1] != 247 || pixelValue[2] != 172)
                {
                    continue;
                }

                return int.TryParse(experienceRa.Name, out experience);
            }
        }
        catch (Exception ex)
        {
            Logger.LogDebug(ex, "经验值奖励模板识别异常");
        }

        return false;
    }
    
    //基于万叶经验值判断是否拾取
    private static Task FindExp(CancellationToken cts2)
    {
        var autoFightAssets = AutoFightAssets.Instance;

        try  
        {
            Task.Run(() =>
            {
                _isExperiencePickup = false;
                var expLogo = false;
                
                var experienceRas = new[]
                {
                   autoFightAssets.InitializeRecognitionObject(60), 
                   autoFightAssets.InitializeRecognitionObject(58), 
                   autoFightAssets.InitializeRecognitionObject(57),
                };
                
                while (!(_isExperiencePickup || FightEndFlag) && !cts2.IsCancellationRequested)
                {
                    try
                    {
                        cts2.ThrowIfCancellationRequested();

                        var result = NewRetry.WaitForAction(() =>
                        {
                            using (var ra = CaptureToRectArea())
                            {
                                _isExperiencePickup = experienceRas.Any(experienceRa => 
                                {
                                    var isExist = ra.Find(experienceRa);
                                    if (!isExist.IsExist())
                                    {
                                        return false;
                                    }
                
                                    var pixelValue1 = ra.SrcMat.At<Vec3b>(isExist.Y, isExist.X - 147); //经验值图标，在2K以上时匹配度0.6，这个经验值颜色尤为重要
                                    expLogo = pixelValue1[0] == 253 && pixelValue1[1] == 247 && pixelValue1[2] == 172;

                                    return expLogo;
                                });
                            }
                            return _isExperiencePickup;
                        }, cts2, 1, 100).Result;
                    }
                    catch (OperationCanceledException ex)
                    {
                        Console.WriteLine($"检测经验发生异常: {ex.Message}");
                    }
                    catch (Exception ex)
                    {
                        // Console.WriteLine($"检测怪物经验发生异常: {ex.Message}");
                    }
                    
                    if (_isExperiencePickup) Logger.LogInformation("基于怪物经验判断：识别到 {text1} 经验值，{text2} 万叶拾取","精英","启用" );

                }
                
                cts2.ThrowIfCancellationRequested();
                
            }, cts2); 
        }
        catch (OperationCanceledException ex)
        {
            Console.WriteLine($"检测经验发生异常: {ex.Message}");
        }
        catch (Exception ex)
        {
            Console.WriteLine($"检测怪物经验发生异常: {ex.Message}");
        }
        finally
        {
            VisionContext.Instance().DrawContent.ClearAll();
            GC.Collect();//释放内存
            GC.WaitForPendingFinalizers();//释放内存
            // FightEndFlag = true; 
        }
        
        return Task.CompletedTask;
    }
    
    private static readonly MedicineState _medicineState = new();
    
    /// <summary>
    /// 向后兼容：外部模块通过此属性访问复活次数
    /// </summary>
    public static int RecoverCount
    {
        get => _medicineState.ReviveCount;
        set
        {
            // 外部设置时重置整个状态
            if (value == 0) _medicineState.Reset();
            else if (value >= 3) _medicineState.Reset(); // 外部设置为3表示禁用
        }
    }

    /// <summary>
    /// 战斗中自动吃药（异步方法，可正确 await）
    /// </summary>
    private async Task TakeMedicineAsync(CancellationToken ct, bool endBloodCheck = false)
    {
        _medicineState.Reset();
        _medicineState.EnterMedicineScope();
        var greenBloodCount = 0;
        var reviveCooldownTime = DateTime.MinValue;
        const int reviveCooldownSeconds = 20;
        var lastReviveTime = DateTime.MinValue; // 死亡槽位检测吃药独立计时，不影响 LastEatTime
        const int reviveRetryIntervalMs = 1500; // 死亡吃药重试间隔
        var cdRetryCount = 0; // CD重试计数器
        const int maxCdRetries = 5; // CD最多重试5次（约2.5秒），超过后计为失败

        try
        {
            // 检测营养袋
            using (var ra = CaptureToRectArea())
            {
                if (!CombatHealthDetector.HasNutritionBag(ra))
                {
                    Logger.LogInformation("自动吃药：未发现营养袋，自动吃药关闭");
                    return;
                }

                if (!endBloodCheck)
                {
                    Logger.LogInformation("自动吃药：检测间隔{checkInterval}，吃药间隔{medicineInterval}，吃药上限{recoverMaxCount}",
                        _taskParam.CheckInterval, _taskParam.MedicineInterval, _taskParam.RecoverMaxCount);
                }
            }

            // 主检测循环
            while (!FightEndFlag && !ct.IsCancellationRequested&&!FightEndTotoly)
            {
                ct.ThrowIfCancellationRequested();

                try
                {
                    var needHeal = false;
                    var needRevive = false;
                    var isResurrectionDrug = false;

                    using (var ra = CaptureToRectArea())
                    {
                        // 先检测复活界面（优先级最高，因为复活界面弹出时派蒙不可见）
                        var confirmRa = ra.Find(AutoFightAssets.Instance.ConfirmRa);
                        if (confirmRa.IsExist())
                        {
                            // 先点确认（尝试使用复活药）
                            confirmRa.Click();
                            _medicineState.IncrementRevive();
                            Logger.LogInformation("自动吃药：检测到复活界面，点击确认（第{count}次）", _medicineState.ReviveCount);
                            await Task.Delay(300, ct);
                            // 无论确认是否关闭了弹窗，都点一次取消位置（复活药CD时确认无效，需要取消关闭弹窗）
                            using var ra2 = CaptureToRectArea();
                            var exitRa = ra2.Find(AutoFightAssets.Instance.ExitRa);
                            if (exitRa.IsExist())
                            {
                                exitRa.Click();
                                Logger.LogDebug("自动吃药：点击取消关闭复活弹窗");
                                await Task.Delay(200, ct);
                            }

                            if (_medicineState.IsReviveOverLimit())
                            {
                                Logger.LogInformation("自动吃药：复活次数达到上限({count}次)，退出吃药，启用外部复活检测",
                                    _medicineState.ReviveCount);
                                _medicineState.ExitMedicineScope(shouldEnableReviveCheck: true);
                                return;
                            }
                            continue;
                        }

                        // 派蒙不可见时跳过血量检测（可能在放大招或其他界面）
                        if (!CombatHealthDetector.IsPaimonVisible(ra))
                        {
                            await Task.Delay(Math.Max(_taskParam.CheckInterval - 150, 100), ct);
                            continue;
                        }

                        // 检测是否为复活药（白色图标）
                        isResurrectionDrug = CombatHealthDetector.IsResurrectionDrug(ra);

                        // 死亡检测
                        var deadSlots = CombatHealthDetector.GetDeadCharacterSlots(ra);
                        if (deadSlots.Count > 0)
                        {
                            needRevive = true;
                        }

                        // 红血检测
                        if (!needRevive)
                        {
                            var isRed = CombatHealthDetector.IsRedBlood(ra);
                            var isGreen = CombatHealthDetector.IsGreenBlood(ra);
                            
                            // // 输出实际像素值用于调试
                            // var bloodPixel = ra.SrcMat.At<Vec3b>(1009, 808);
                            // var greenPixel = ra.SrcMat.At<Vec3b>(1010, 814);
                            // Logger.LogDebug("[吃药检测] isRed={isRed}, isGreen={isGreen}, bloodBGR=({b},{g},{r}), greenBGR=({gb},{gg},{gr}), greenCount={gc}",
                            //     isRed, isGreen, bloodPixel[0], bloodPixel[1], bloodPixel[2],
                            //     greenPixel[0], greenPixel[1], greenPixel[2], greenBloodCount);
                            
                            if (isRed)
                            {
                                if (isResurrectionDrug)
                                {
                                    needHeal = false;
                                }
                                else
                                {
                                    needHeal = true;
                                    Logger.LogDebug("[吃药检测] 判定：红血");
                                }
                            }
                            else if (!isGreen)
                            {
                                // 非绿血也非红血，可能是丝血或其他状态
                                greenBloodCount++;
                                Logger.LogDebug("[吃药检测] 非红非绿，greenCount累积到{gc}", greenBloodCount);
                                if (greenBloodCount > 5 || (endBloodCheck && greenBloodCount > 1))
                                {
                                    if (isResurrectionDrug)
                                    {
                                        needHeal = false;
                                    }
                                    else
                                    {
                                        using var bloodRect = ra.DeriveCrop(808, 1009, 3, 3);
                                        if (!CombatHealthDetector.IsPixelSimilar(
                                                bloodRect.SrcMat.At<Vec3b>(1, 1),
                                                bloodRect.SrcMat.At<Vec3b>(2, 2)))
                                        {
                                            needHeal = true;
                                            Logger.LogDebug("[吃药检测] 判定：丝血（非红非绿累积超阈值）");
                                        }
                                        else
                                        {
                                            Logger.LogDebug("[吃药检测] 血条像素一致，跳过");
                                        }
                                    }
                                }
                            }
                            else
                            {
                                greenBloodCount = 0;
                            }
                        }

                        // 复活药自动使用（角色死亡时）
                        if (isResurrectionDrug && !needRevive &&
                            (DateTime.UtcNow - reviveCooldownTime).TotalSeconds > reviveCooldownSeconds)
                        {
                            if (!await AutoFightSkill.MedicinalCdAsync(Logger, false, 1, ct))
                            {
                                reviveCooldownTime = DateTime.UtcNow;
                                Logger.LogInformation("自动吃药：发现复活药，使用小道具");
                                Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                                Simulation.ReleaseAllKey();
                            }
                        }
                    }

                    // 执行吃药/复活
                    if ((needHeal &&
                         (DateTime.UtcNow - PathingConditionConfig.LastEatTime).TotalMilliseconds >
                         Math.Max(_taskParam.MedicineInterval, 1500)) ||
                        (needRevive &&
                         (DateTime.UtcNow - lastReviveTime).TotalMilliseconds > reviveRetryIntervalMs))
                    {
                        var canHeal = needHeal && !_medicineState.IsHealOverLimit(_taskParam.RecoverMaxCount);
                        // 死亡槽位检测触发的复活不受上限限制，上限由复活弹窗确认计数控制
                        var canRevive = needRevive && isResurrectionDrug; // 复活药CD时快捷键会变回恢复药，此时不按

                        if (canHeal || canRevive)
                        {
                            var isMedicineOnCd = await AutoFightSkill.MedicinalCdAsync(Logger, false, 1, ct);
                            if (isMedicineOnCd)
                            {
                                cdRetryCount++;
                                if (cdRetryCount >= maxCdRetries)
                                {
                                    // CD等待超限，计为一次失败的吃药尝试（死亡路径不计复活次数）
                                    if (needHeal) _medicineState.IncrementHeal();
                                    Logger.LogWarning("自动吃药：药物冷却等待超限({count}次)，计为失败尝试，{reason}", cdRetryCount, needRevive ? "复活" : "回复");
                                    cdRetryCount = 0;
                                    PathingConditionConfig.LastEatTime = DateTime.UtcNow;
                                }
                                else
                                {
                                    Logger.LogDebug("自动吃药：药物冷却中({count}/{max})，等待下一轮重试", cdRetryCount, maxCdRetries);
                                }
                                await Task.Delay(500, ct);
                                continue;
                            }
                            
                            cdRetryCount = 0;
                            
                            using var ra = CaptureToRectArea();
                            if (CombatHealthDetector.HasNutritionBag(ra))
                            {
                                Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                            }
                            else
                            {
                                Logger.LogWarning("自动吃药：未发现营养袋，无法使用小道具");
                            }
                            
                            Simulation.ReleaseAllKey();

                            // 死亡检测触发的吃药不计数，复活次数以复活弹窗确认为准（避免倒下动画期间重复计数）
                            if (needHeal)
                            {
                                _medicineState.IncrementHeal();
                                PathingConditionConfig.LastEatTime = DateTime.UtcNow;
                            }
                            if (needRevive) lastReviveTime = DateTime.UtcNow;
                            Logger.LogInformation("自动吃药：{reason}，使用小道具", needHeal ? "发现红血" : "发现角色死亡");

                            if (endBloodCheck && _medicineState.TotalCount >= 1)
                                return; // 单次检测复用
                        }
                        else
                        {
                            // 真正超额：heal超限且没有死亡需要处理
                            // 复活药CD时 canRevive=false 但 needRevive=true，不应退出，等复活药CD好
                            if (!needRevive && needHeal)
                            {
                                Logger.LogInformation("自动吃药：吃药数量超额退出");
                                _medicineState.ExitMedicineScope(shouldEnableReviveCheck: true);
                                return;
                            }
                            // 复活药CD中，等待下一轮检测
                            Logger.LogDebug("自动吃药：复活药CD中或无可执行操作，等待下一轮");
                        }
                    }

                    await Task.Delay(Math.Max(_taskParam.CheckInterval - 100, 100), ct);
                }
                catch (OperationCanceledException)
                {
                    break;
                }
                catch (Exception ex)
                {
                    Logger.LogWarning(ex, "自动吃药检测异常");
                }
            }
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "自动吃药异常");
        }
        finally
        {
            // 退出时检查复活界面
            try
            {
                using var bitmap = CaptureToRectArea();
                var confirmRa = bitmap.Find(AutoFightAssets.Instance.ConfirmRa);
                if (confirmRa.IsExist())
                {
                    // 先点确认尝试复活，再点取消关闭弹窗
                    confirmRa.Click();
                    await Task.Delay(300, ct);
                    using var bitmap2 = CaptureToRectArea();
                    var exitRa = bitmap2.Find(AutoFightAssets.Instance.ExitRa);
                    if (exitRa.IsExist())
                    {
                        exitRa.Click();
                        await Task.Delay(200, ct);
                    }
                    if (!await AutoFightSkill.MedicinalCdAsync(Logger, false, 1, ct))
                    {
                        Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                        Simulation.ReleaseAllKey();
                    }
                }
            }
            catch
            {
                // 退出清理不应抛异常
            }
        }
    }

    /// <summary>
    /// C1 兜底循环：队伍完全识别失败（5 次重试都没拿到 Avatar）时调用。
    /// 不切人、不重击，仅 E → Q → 普攻 ×3 循环，每 ~5s 调用一次 CheckFightFinish(null) 检测战斗结束。
    /// 沿用 _taskParam.Timeout 作为兜底超时上限。
    /// 详见 .kiro/specs/fight-strategy-fallback-use-real-flow/design.md §2.6。
    /// </summary>
    private async Task RunC1FallbackLoopAsync(
        CancellationToken ct,
        RewardEndDetectionConfig? rewardEndDetection)
    {
        Logger.LogWarning(
            "[联机][兜底][C1] 队伍识别失败，启用简化 EQA 循环 + CheckFightFinish 检测，超时 {Timeout}s",
            _taskParam.Timeout);

        // 重置 CheckFightFinish 依赖的状态字段（与主循环重置块语义一致，避免读到上一场战斗 stale 值）
        FightStatusFlag = true;
        FightEndTotoly = false;
        _totolyEndCount = 0;
        _2ndEndFlag = false;
        _fightDurationExceeded = false;
        _totolyFlag = false;

        // C1 会绕过正常战斗主循环，因此在此单独建立联机配额和奖励识别生命周期。
        var (quorumCoordinator, onAllFightDone) = StartSharedFightEndCoordination();
        using var rewardEndDetectionCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
        Task? rewardEndDetectionTask = null;
        if (rewardEndDetection is not null)
        {
            rewardEndDetectionTask = StartRewardTimeoutEndLoop(
                rewardEndDetectionCts.Token,
                rewardEndDetection,
                _ => HandleRewardEndDetected(rewardEndDetection));
        }

        var sw = Stopwatch.StartNew();
        var timeoutMs = (long)_taskParam.Timeout * 1000L;
        var lastCheckMs = -5000L; // 让首轮也能尽快检测一次
        const int checkIntervalMs = 5000;

        try
        {
            while (sw.ElapsedMilliseconds < timeoutMs && !ct.IsCancellationRequested && !FightEndTotoly)
            {
                // E
                Simulation.SendInput.SimulateAction(GIActions.ElementalSkill);
                await Delay(200, ct);
                if (FightEndTotoly || ct.IsCancellationRequested) break;

                // Q
                Simulation.SendInput.SimulateAction(GIActions.ElementalBurst);
                await Delay(300, ct);
                if (FightEndTotoly || ct.IsCancellationRequested) break;

                // 普攻 ×3
                for (var i = 0; i < 3; i++)
                {
                    if (FightEndTotoly || ct.IsCancellationRequested) break;
                    Simulation.SendInput.SimulateAction(GIActions.NormalAttack);
                    await Delay(250, ct);
                }

                // 周期性检测战斗结束（avatar=null，CheckFightFinish 内异常会被捕获 return true 提前结束）
                if (sw.ElapsedMilliseconds - lastCheckMs >= checkIntervalMs)
                {
                    lastCheckMs = sw.ElapsedMilliseconds;
                    try
                    {
                        var finished = await CheckFightFinish(0, 450, ct, null);
                        if (finished)
                        {
                            Logger.LogInformation(
                                "[联机][兜底][C1] CheckFightFinish 检测到战斗结束，提前退出兜底");
                            break;
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "[联机][兜底][C1] CheckFightFinish 异常，忽略并继续");
                    }
                }
            }
        }
        finally
        {
            FightEndTotoly = true;
            // 与正常流程一致：停止奖励识别并解除本场 AllFightDone 订阅。
            rewardEndDetectionCts.Cancel();
            await WaitForRewardEndDetectionTaskAsync(rewardEndDetectionTask);
            StopSharedFightEndCoordination(quorumCoordinator, onAllFightDone);
            Simulation.ReleaseAllKey();
            FightStatusFlag = false;
            Logger.LogInformation(
                "[联机][兜底][C1] 简化兜底结束，耗时 {Elapsed:F1}s",
                sw.Elapsed.TotalSeconds);
        }
    }

    /// <summary>
    /// 为 C2/C3 捏造的 EQA 序列：每个角色一组 skill → burst → attack(0.5)，不含 charge。
    /// 由原 AutoFightTask 主循环按角色顺序逐条 Execute，自动获得 CheckFightFinish / AnomalyDetector 等全部副作用。
    /// </summary>
    private static List<CombatCommand> BuildSyntheticEqaCommands(IEnumerable<string> avatarNames)
    {
        var cmds = new List<CombatCommand>();
        foreach (var name in avatarNames)
        {
            cmds.Add(new CombatCommand(name, "skill"));
            cmds.Add(new CombatCommand(name, "burst"));
            cmds.Add(new CombatCommand(name, "attack(0.5)"));
        }
        return cmds;
    }

    //定义按键，用于结束吃药的切换人
    private static readonly GIActions[] MemberActions = new GIActions[]
    {
        GIActions.SwitchMember1,
        GIActions.SwitchMember2,
        GIActions.SwitchMember3,
        GIActions.SwitchMember4
    };

    private async Task EndBloodCheck(CancellationToken ct, CombatScenes? combatScenes = null)
    {
        _medicineState.Reset(); // 战斗结束吃药独立计算
        _medicineState.EnterMedicineScope();
        var ms = 2500;
        var useMedicine = new List<int> { 1, 2, 3, 4 };
        var hasRechecked = false;

        try
        {
            await TakeMedicineAsync(ct, true); // 尝试吃药和复活角色

            while (ms > 0)
            {
                using (var ra = CaptureToRectArea())
                {
                    // 检测是否为复活药
                    if (CombatHealthDetector.IsResurrectionDrug(ra))
                    {
                        if (!_taskParam.QRecoverAvatar) return;
                        Logger.LogInformation("自动结束吃药：检测到复活药，不执行结束吃恢复药");
                        Logger.LogInformation("自动结束吃药：尝试执行技能恢复");
                    }
                    else
                    {
                        // 非复活药前提下检测营养袋
                        if (!CombatHealthDetector.HasNutritionBag(ra))
                        {
                            Logger.LogInformation("自动结束吃药：未发现营养袋，结束吃药关闭");
                            return;
                        }
                    }

                    // 检查4个角色槽位的血量
                    for (var h = 0; h < 4; h++)
                    {
                        var hasGreenBlood = CombatHealthDetector.IsSlotRedBlood(ra, h);
                        var isActive = CombatHealthDetector.IsSlotActive(ra, h);

                        // 有绿血或非出战状态（可能死亡）的角色需要吃药
                        if (hasGreenBlood || !isActive)
                        {
                            ms = 1;
                            useMedicine.Remove(h + 1);
                        }
                    }
                }

                // 发现红血角色，可能因为游泳等误判，进行复检
                if (useMedicine.Count > 0 && !hasRechecked)
                {
                    hasRechecked = true;
                    Logger.LogInformation("自动结束吃药：检测到红血角色 {slots}，进行复检", useMedicine);
                    ms = 100;
                    useMedicine = new List<int> { 1, 2, 3, 4 };
                    await Task.Delay(500, ct);
                }

                await Task.Delay(100, ct);
                ms -= 95;
            }

            using var swimming = CaptureToRectArea();
            if (useMedicine.Count > 0 && !Avatar.SwimmingConfirm(swimming))
            {
                // 优先使用技能恢复
                if (_taskParam.QRecoverAvatar && PathingConditionConfig.PartyConfigBackUp.RecoverAvatarIndex is not null)
                {
                    var pathExecutor = new PathExecutor(ct);
                    Logger.LogWarning("自动结束吃药：执行技能恢复 {slots} {avatar}", useMedicine, PathingConditionConfig.PartyConfigBackUp.RecoverAvatarIndex);
                    await pathExecutor.TryPartyHealing(combatScenes, PathingConditionConfig.PartyConfigBackUp);
                    return;
                }

                // 等待吃药冷却
                var timeSinceLastEat = (DateTime.UtcNow - PathingConditionConfig.LastEatTime).TotalMilliseconds;
                if (timeSinceLastEat < 1500)
                {
                    await Task.Delay(1500 - (int)timeSinceLastEat, ct);
                }

                PathingConditionConfig.LastEatTime = DateTime.UtcNow;
                Logger.LogInformation("自动结束吃药：发现红血角色，执行吃药 {slots} 编号", useMedicine);

                // 切换角色并吃药
                foreach (var num in useMedicine)
                {
                    Simulation.ReleaseAllKey();
                    await Task.Delay(700, ct);
                    Simulation.SendInput.SimulateAction(MemberActions[num - 1]);
                    await Task.Delay(800, ct);

                    using (var bitmap = CaptureToRectArea())
                    {
                        if (Bv.IsInRevivePrompt(bitmap))
                        {
                            // 先点确认尝试复活，再点取消关闭弹窗
                            var confirmArea = bitmap.Find(AutoFightAssets.Instance.ConfirmRa);
                            if (confirmArea.IsExist())
                            {
                                confirmArea.Click();
                            }
                            await Task.Delay(300, ct);
                            using var bitmap2 = CaptureToRectArea();
                            var exitArea = bitmap2.Find(AutoFightAssets.Instance.ExitRa);
                            if (exitArea.IsExist())
                            {
                                exitArea.Click();
                                await Task.Delay(200, ct);
                            }
                        }
                    }

                    try
                    {
                        if (!await AutoFightSkill.MedicinalCdAsync(Logger, false, 1, ct))
                        {
                            Simulation.SendInput.SimulateAction(GIActions.QuickUseGadget);
                        }
                    }
                    catch (OperationCanceledException)
                    {
                        break;
                    }
                    catch (Exception ex)
                    {
                        Logger.LogWarning(ex, "自动结束吃药异常");
                    }

                    await Task.Delay(700, ct);
                }
            }
            else
            {
                Logger.LogInformation("自动结束吃药：检测未发现红血角色，不执行结束吃药");
            }

            _medicineState.ExitMedicineScope(shouldEnableReviveCheck: true);
        }
        catch (OperationCanceledException)
        {
            // 正常取消
        }
        catch (Exception ex)
        {
            Logger.LogWarning(ex, "战斗结束血量检测异常");
        }
        finally
        {
            // 确保状态恢复
            _medicineState.ExitMedicineScope(shouldEnableReviveCheck: true);
        }
    }

    static double FindMax(double[] numbers)
    {
        if (numbers == null || numbers.Length == 0)
        {
            throw new ArgumentException("The array is empty or null.");
        }

        double max = numbers[0] > 10000 ? 0 : numbers[0];
        foreach (var num in numbers)
        {
            var cpnum = numbers[0] > 10000 ? 0 : num;
            max = Math.Max(max, num);
        }

        return max;
    }

    [Obsolete]
    private static Dictionary<string, double> ParseStringToDictionary(string input, double defaultValue = -1)
    {
        var dictionary = new Dictionary<string, double>();

        if (string.IsNullOrEmpty(input))
        {
            return dictionary; // 返回空字典
        }

        string[] pairs = input.Split(';', StringSplitOptions.RemoveEmptyEntries);

        foreach (var pair in pairs)
        {
            var parts = pair.Split(',', StringSplitOptions.TrimEntries);

            if (parts.Length > 0)
            {
                string name = parts[0];
                double value = defaultValue;

                if (parts.Length > 1 && double.TryParse(parts[1], out var parsedValue))
                {
                    value = parsedValue;
                }

                dictionary[name] = value;
            }
        }

        return dictionary;
    }

    private bool HasFightFlagByYolo(ImageRegion imageRegion)
    {
        // if (RuntimeHelper.IsDebug)
        // {
        //     imageRegion.SrcMat.SaveImage(Global.Absolute(@"log\fight\" + $"{DateTime.UtcNow:yyyyMMdd_HHmmss_ffff}.png"));
        // }
        var dict = _predictor.Detect(imageRegion);
        return dict.ContainsKey("health_bar") || dict.ContainsKey("enemy_identify");
    }

    /// <summary>
    /// 联机锄地 + 万叶玩家专用："持续回点"独立后台循环。
    /// 与 fightTask 主循环并行：每秒检查一次玩家位置到 FightWaypoint 的实时距离，
    /// 距离 > 1.0 即调一次 await pathExecutor.MoveCloseTo（小碎步精确接近）拉回到战斗点；
    /// 由 cts2.Token 统一控制取消（战斗结束 / FightEndTotoly / 外部取消时停止）。
    ///
    /// 仅在 _taskParam.KazuhaContinuousReturn == true 时被启动；
    /// 单机 / 联机非万叶玩家场景该字段保持 false，本方法不会被调用。
    /// 详见 .kiro/specs/multiplayer-kazuha-pre-cast-positioning/design.md §3.2
    /// （原设计放在 SeekAndFightAsync 内部，但 SeekAndFightAsync 仅在 RotateFindEnemyEnabled
    /// 启用时调用，对默认用户无效；故搬出独立后台 Task 保证全场景覆盖）。
    /// </summary>
    private async Task KazuhaContinuousReturnLoopAsync(CancellationToken token)
    {
        const int returnIntervalMs = 1000;
        const double returnDistanceThreshold = 1.0;
        var lastReturnAt = DateTime.MinValue;
        var pathExecutor = new PathExecutor(token);

        try
        {
            TaskControl.Logger.LogInformation("[联机][万叶] 持续回点后台任务已启动 (interval={Interval}ms, threshold={Threshold:F1})",
                returnIntervalMs, returnDistanceThreshold);

            // return-to-point-stale-prev-position-drift-fix (c) 回点首帧播种（循环启动一次，Q8）：
            // 回点循环首轮 GetPosition 之前用战斗点坐标播种，避免战后被怪推开残留导致
            // "距战斗点 381 > 4.0" 类异常大距离（BC2）。只在 while 之前播一次，循环体每轮不覆写
            // （不压制局部匹配连续帧累积）。与 PathExecutor.cs:838-846 联机万叶分支时序/调用栈不同，无重复。
            var __returnWp = FightWaypoint;
            if (__returnWp is not null)
            {
                var __seed = KazuhaCollectPositionGuardDecisions.ComputeSeedAnchor(__returnWp.X, __returnWp.Y);
                Navigation.SetPrevPosition((float)__seed.X, (float)__seed.Y);
            }

            while (!token.IsCancellationRequested && !FightEndTotoly)
            {
                try
                {
                    await Task.Delay(returnIntervalMs, token);
                }
                catch (OperationCanceledException) { return; }

                if (FightEndTotoly || token.IsCancellationRequested) return;

                var fightWaypoint = FightWaypoint;
                if (fightWaypoint is null) continue;

                var elapsedSinceLastReturn = (DateTime.UtcNow - lastReturnAt).TotalMilliseconds;
                if (elapsedSinceLastReturn < returnIntervalMs) continue;

                Point2f currentPos;
                try
                {
                    using var image = CaptureToRectArea();
                    currentPos = Navigation.GetPosition(image, fightWaypoint.MapName, fightWaypoint.MapMatchMethod);
                }
                catch (Exception ex)
                {
                    TaskControl.Logger.LogDebug(ex, "[联机][万叶] 持续回点位置识别失败，本轮跳过");
                    continue;
                }

                if (currentPos is { X: 0, Y: 0 }) continue;

                // hoeing-kazuha-return-abnormal-coord-reseed-moveto-fix 路径 A：
                // 旧护栏命中只 continue、不重播种 → 漂移的陈旧锚点残留、帧帧误匹配无法恢复。
                // 改为调用与路径 B 同一的 KazuhaReturnReseedGuard：异常时重播种(SetPrevPosition 战斗点)
                // + 重识别重试（每次间隔 100ms），阈值/次数读 AutoHoeingConfig（替代硬编码 180）。
                // 仍异常则放弃本轮 continue（已尝试重播种恢复）；可信则用修正坐标继续后续分流。
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
                    // hoeing-kazuha-return-minimap-recognition-fail-getpositionstable-retry-fix：
                    // (0,0) 识别失败时改走 GetPositionStable 全局匹配重试。
                    reSampleStable: () =>
                    {
                        using var img = CaptureToRectArea();
                        return Navigation.GetPositionStable(img, fightWaypoint.MapName, fightWaypoint.MapMatchMethod);
                    },
                    delay: t => Task.Delay(KazuhaReturnReseedGuard.ReseedReSampleDelayMs, t),
                    // 画面稳定门控（本次修复）：重识别前先派蒙检测。CaptureToRectArea + Bv.IsInMainUi。
                    isScreenStable: () =>
                    {
                        using var ra = CaptureToRectArea();
                        return Bv.IsInMainUi(ra);
                    },
                    screenStablePollDelay: t => Task.Delay(KazuhaReturnReseedGuard.ScreenStablePollIntervalMs, t),
                    log: m => TaskControl.Logger.LogInformation("[联机][万叶] 持续回点{Msg}", m),
                    ct: token);

                if (!__guardResult.ShouldMove)
                {
                    TaskControl.Logger.LogDebug(
                        "[联机][万叶] 持续回点：重播种+重识别 {Retry} 次仍异常，本轮放弃",
                        __guardResult.RetryUsed);
                    continue;
                }
                currentPos = __guardResult.TrustedPos;

                var realtimeDistance = Navigation.GetDistance(fightWaypoint, currentPos);
                if (!AutoFightSeekDecisions.ShouldTriggerContinuousReturn(
                        realtimeDistance, returnDistanceThreshold,
                        elapsedSinceLastReturn, returnIntervalMs))
                {
                    continue;
                }

                // 复苏/神像传送进行中：终止本场回点循环，避免把刚传送到神像的角色又拉回战斗点。
                // 终止 ≠ 暂停一轮——传送后角色必定不回战斗点，本循环已无意义，return 退出最干净；
                // return 会走方法末尾的 finally（打 "持续回点后台任务已退出" 日志），是干净退出。
                // 此处 return 之前 pathExecutor 在 while 外创建、无 using，与既有 return 路径
                //（OperationCanceledException / FightEndTotoly）一致，不持有需手动释放的资源。
                // 回点能力由下一场战斗重新启动的新循环恢复。
                // 详见 return-to-point-suspend-during-revival-teleport/design.md 改动 3 / Property 1。
                if (AutoFightSeekDecisions.ShouldStopReturnForTeleport(AutoFightTask.IsTeleportingToStatue))
                {
                    TaskControl.Logger.LogDebug("[联机][万叶] 复苏/神像传送进行中，终止本场回点循环（距战斗点 {Dist:F1}）", realtimeDistance);
                    return;
                }

                try
                {
                    fightWaypoint.MoveMode = MoveModeEnum.Walk.Code;
                    // BC2 距离自适应（改动 1b）：复用 PathExecutorDecisions.ShouldPreMoveTo(realtimeDistance, 4.0)
                    // 分流——远距离（> 4.0）用 MoveTo 真寻路拉回，近距离（<= 4.0）保持原 MoveCloseTo 精接近。
                    // 阈值 4.0 对齐 PathExecutor 战后聚物分支 ShouldPreMoveTo(preDistance, 4.0)（Q1）。
                    if (PathExecutorDecisions.ShouldPreMoveTo(realtimeDistance, 4.0))
                    {
                        TaskControl.Logger.LogInformation("[联机][万叶] 持续回点：距战斗点 {Dist:F1} > 4.0，触发 MoveTo 真寻路",
                            realtimeDistance);
                        // 远距离 MoveTo 分支（Q4）：每轮新建 PathExecutor(moveCts.Token) + endWatcher 监测 FightEndTotoly
                        // 立即打断，finally 释放 W 键，对齐通用循环 GeneralReturnToFightPointLoopAsync 的 MoveTo 用法。
                        // isGetOut:false（BC3 调用点②）关闭脱困，避免战斗场景抢镜头/抢移动。
                        using var moveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                        using var endWatcher = new CancellationTokenSource();
                        var watcherTask = Task.Run(async () =>
                        {
                            while (!endWatcher.Token.IsCancellationRequested)
                            {
                                if (FightEndTotoly)
                                {
                                    try { moveCts.Cancel(); } catch { /* already disposed */ }
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
                                retryDis: 4, isPoint: false, closeDistance: returnDistanceThreshold);
                        }
                        finally
                        {
                            AutoFightTask.ExitReturnToFightPoint();
                            endWatcher.Cancel();
                            try { await watcherTask; } catch { /* ignore */ }
                            Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                        }
                    }
                    else
                    {
                        TaskControl.Logger.LogInformation("[联机][万叶] 持续回点：距战斗点 {Dist:F1} <= 4.0，触发 MoveCloseTo",
                            realtimeDistance);
                        // 近距离 MoveCloseTo 分支保持现状（Q4）：while 外单实例 pathExecutor，耗时短。
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
                // NormalEndException（取消自动任务）继承自 System.Exception 而非 OperationCanceledException，
                // 属任务取消信号，应干净退出循环而非误报为"移动异常本轮跳过"。
                catch (BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException) { return; }
                catch (Exception ex)
                {
                    TaskControl.Logger.LogError(ex, "[联机][万叶] 持续回点移动异常，本轮跳过");
                }
            }
        }
        finally
        {
            TaskControl.Logger.LogDebug("[联机][万叶] 持续回点后台任务已退出");
        }
    }

    /// <summary>
    /// 通用版"战斗中回点"独立后台循环。
    /// 与 fightTask 主循环并行，由 cts2.Token 统一控制取消（战斗结束 / FightEndTotoly / 外部取消时停止）。
    ///
    /// 仅在以下三态分支启用：
    ///   - _taskParam.KazuhaContinuousReturn == false（万叶专属循环优先）
    ///   - _taskParam.FinishDetectConfig.ReturnToFightPointEnabled == true
    ///   - AutoFightSeekDecisions.IsReturnToFightPointConfigValid(...) == true
    ///
    /// 每轮先后判定两个触发器（任一满足即触发同一份 MoveTo）：
    ///   1. 距离触发：realtimeDistance > triggerDistance
    ///   2. 时间触发：UtcNow - LastEnemySeenAt > timeTriggerSeconds（需 timeTriggerEnabled && rotateFindEnemyEnabled）
    ///
    /// 详见 .kiro/specs/fight-return-to-point-revamp/design.md §2.4
    /// </summary>
    private async Task GeneralReturnToFightPointLoopAsync(
        CancellationToken token,
        int intervalMs,
        double triggerDistance,
        double stopDistance,
        bool timeTriggerEnabled,
        int timeTriggerSeconds,
        bool rotateFindEnemyEnabled)
    {
        const int distanceTolerance = 2;  // 触发 / 停止判定连续命中次数容差，过滤位置识别瞬时误差
        var lastReturnAt = DateTime.MinValue;
        int triggerHitCount = 0;  // 连续 distance > triggerDistance 命中次数

        try
        {
            TaskControl.Logger.LogInformation(
                "[AutoFight][回点] 通用版后台任务已启动 (interval={Interval}ms, trigger={Trigger:F1}, stop={Stop:F1}, timeTrigger={TimeEnabled} {TimeSec}s)",
                intervalMs, triggerDistance, stopDistance,
                timeTriggerEnabled && rotateFindEnemyEnabled, timeTriggerSeconds);

            // return-to-point-stale-prev-position-drift-fix (c) 回点首帧播种（循环启动一次，Q8）：
            // 单机 / 联机未开万叶聚物的通用回点缺口——首轮测距前用战斗点坐标播种，避免沿用战后残留。
            // 只在 while 之前播一次，循环体每轮不覆写。
            var __returnWp = FightWaypoint;
            if (__returnWp is not null)
            {
                var __seed = KazuhaCollectPositionGuardDecisions.ComputeSeedAnchor(__returnWp.X, __returnWp.Y);
                Navigation.SetPrevPosition((float)__seed.X, (float)__seed.Y);
            }

            while (!token.IsCancellationRequested && !FightEndTotoly)
            {
                try
                {
                    await Task.Delay(intervalMs, token);
                }
                catch (OperationCanceledException) { return; }

                if (FightEndTotoly || token.IsCancellationRequested) return;

                var fightWaypoint = FightWaypoint;
                if (fightWaypoint is null) continue;

                var elapsedSinceLastReturnMs = (DateTime.UtcNow - lastReturnAt).TotalMilliseconds;

                // 派蒙可见性校验：战斗激烈时派蒙图标可能被遮挡 → GetPosition 不可靠 → 跳过本轮。
                // 不污染 triggerHitCount（连续计数仅在能可靠测量距离时才累加）。
                using var screen = CaptureToRectArea();
                if (!Bv.IsInMainUi(screen))
                {
                    TaskControl.Logger.LogDebug("[AutoFight][回点] 派蒙不可见（战斗界面遮挡），本轮跳过测距");
                    continue;
                }

                OpenCvSharp.Point2f currentPos;
                try
                {
                    currentPos = Navigation.GetPosition(screen, fightWaypoint.MapName, fightWaypoint.MapMatchMethod);
                }
                catch (Exception ex)
                {
                    TaskControl.Logger.LogDebug(ex, "[AutoFight][回点] 位置识别失败，本轮跳过");
                    continue;
                }

                if (currentPos is { X: 0, Y: 0 }) continue;

                // BC1 护栏复核（kazuha-continuous-return-abnormal-coord-and-moveto-distance-fix 改动 2a / Q8）：
                // 通用循环同样只挡 (0,0)、不挡 garbage 远点。护栏放在 (0,0) 过滤之后、realtimeDistance 计算 +
                // triggerHitCount 容差累加之前，故护栏拒绝（continue）天然不污染 triggerHitCount
                // （与派蒙不可见 continue 对称，Preservation 3.11）。seed 用循环体内重算 ComputeSeedAnchor
                // （恒等返回战斗点，无副作用，不 SetPrevPosition 覆写——遵守循环体不覆写约束）。
                var __guardSeed = KazuhaCollectPositionGuardDecisions.ComputeSeedAnchor(fightWaypoint.X, fightWaypoint.Y);
                if (!KazuhaCollectPositionGuardDecisions.IsRecognizedPositionTrustworthy(
                        currentPos, __guardSeed.X, __guardSeed.Y,
                        KazuhaCollectPositionGuardDecisions.RecognizedPositionGuardThreshold))
                {
                    var __guardDist = Navigation.GetDistance(fightWaypoint, currentPos);
                    TaskControl.Logger.LogDebug(
                        "[AutoFight][回点] 识别坐标距战斗点 {Dist:F1} > {Threshold:F1}，疑似异常远点，本轮拒绝（不污染 triggerHitCount）",
                        __guardDist, KazuhaCollectPositionGuardDecisions.RecognizedPositionGuardThreshold);
                    continue;
                }

                var realtimeDistance = Navigation.GetDistance(fightWaypoint, currentPos);

                // 距离触发容差：连续 distanceTolerance 次 distance > triggerDistance 才算真正触发；
                // 一旦命中失败立即清零（断序即重置），避免间歇抖动累计成误触发
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
                        TaskControl.Logger.LogDebug("[AutoFight][回点] 距离 {Dist:F1} > {Trigger:F1} 命中 {Hit}/{Tol}，等待二次确认",
                            realtimeDistance, triggerDistance, triggerHitCount, distanceTolerance);
                    }
                }
                else
                {
                    triggerHitCount = 0;
                }

                // 再判时间触发（仅在距离触发未命中时判定，节流共享）
                bool timeTriggered = false;
                double elapsedSinceEnemySec = 0;
                if (!distanceTriggered)
                {
                    elapsedSinceEnemySec = (DateTime.UtcNow - LastEnemySeenAt).TotalSeconds;
                    timeTriggered = AutoFightSeekDecisions.ShouldTriggerTimeReturn(
                        elapsedSinceEnemySec, timeTriggerSeconds, elapsedSinceLastReturnMs, intervalMs,
                        timeTriggerEnabled, rotateFindEnemyEnabled);
                }

                if (!distanceTriggered && !timeTriggered) continue;

                // 复苏/神像传送进行中：终止本场回点循环。
                // 终止 ≠ 暂停一轮——传送后角色必定不回战斗点，本循环已无意义，return 退出最干净。
                // 此处 return 在 endWatcher / moveCts 创建之前（那些在下方 try 块内），W 键也未按下
                //（SimulateAction(MoveForward, KeyUp) 只在 MoveTo 的 finally 里，未进 MoveTo 就没按下），
                // 故 return 不持有需手动释放的资源；return 会走方法末尾的 finally（打 "通用版回点后台任务已退出" 日志）。
                // 回点能力由下一场战斗重新启动的新循环恢复。
                // 详见 return-to-point-suspend-during-revival-teleport/design.md 改动 4 / Property 1。
                if (AutoFightSeekDecisions.ShouldStopReturnForTeleport(AutoFightTask.IsTeleportingToStatue))
                {
                    TaskControl.Logger.LogDebug("[AutoFight][回点] 复苏/神像传送进行中，终止本场回点循环（距战斗点 {Dist:F1}）", realtimeDistance);
                    return;
                }

                // 触发后重置容差计数器
                triggerHitCount = 0;

                try
                {
                    fightWaypoint.MoveMode = MoveModeEnum.Walk.Code;
                    if (distanceTriggered)
                    {
                        TaskControl.Logger.LogInformation(
                            "[AutoFight][回点] 距战斗点 {Dist:F1} > {Trigger:F1}，触发 MoveTo (stop: {Stop:F1})",
                            realtimeDistance, triggerDistance, stopDistance);
                    }
                    else
                    {
                        TaskControl.Logger.LogInformation(
                            "[AutoFight][回点] 时间触发：{ElapsedSec:F1}s 未发现敌人 > {TimeSec}s，触发 MoveTo (stop: {Stop:F1})",
                            elapsedSinceEnemySec, timeTriggerSeconds, stopDistance);
                    }

                    // 通用版用 MoveTo（PATH 模式真寻路 + closeDistance 精确停止）
                    // §Q1=B 决议：MoveTo 签名末尾新增 closeDistance: stopDistance 实参
                    // isGetOut: false 关闭"卡死脱困"分支（PathExecutor.cs §2776 处的脱困触发器）：
                    // 战斗中走回战斗点不应被脱困逻辑接管，避免与战斗主循环抢镜头/抢移动
                    //
                    // 战斗结束（FightEndTotoly=true）时立即打断进行中的 MoveTo：
                    //   - cts2.Token 在 fightTask finally 之后才会被取消，期间 MoveTo 仍在按 W 键往前
                    //   - PathExecutor ct 是构造时一次性传入的，必须每轮新建 + 接 linked CTS 才能中途取消
                    //   - 后台轮询监测 FightEndTotoly，一旦战斗结束立即取消 linked CTS，PathExecutor 内部循环退出
                    //   - finally 释放 W 键，避免战斗结束后角色继续往战斗点抖动
                    using var moveCts = CancellationTokenSource.CreateLinkedTokenSource(token);
                    using var endWatcher = new CancellationTokenSource();
                    var watcherTask = Task.Run(async () =>
                    {
                        while (!endWatcher.Token.IsCancellationRequested)
                        {
                            if (FightEndTotoly)
                            {
                                try { moveCts.Cancel(); } catch { /* already disposed */ }
                                return;
                            }
                            try { await Task.Delay(100, endWatcher.Token); } catch { return; }
                        }
                    }, endWatcher.Token);

                    // 标记回点移动进行中：让 D（SeekAndFightAsync 两处 MoveMouseBy）让位。
                    // Enter 放在 try 第一行、Exit 放在既有 finally 内，与 W 键释放一同执行，保证任何退出路径都复位。
                    AutoFightTask.EnterReturnToFightPoint();
                    try
                    {
                        // 每轮新建 PathExecutor，传入 linked CTS Token 以便 FightEndTotoly 时立即打断
                        var movePathExecutor = new PathExecutor(moveCts.Token);
                        await movePathExecutor.MoveTo(fightWaypoint,
                            isGetOut: false, task: null, nextWaypoint: null, nextDistance: null,
                            retryDis: 4, isPoint: false, closeDistance: stopDistance);
                    }
                    finally
                    {
                        AutoFightTask.ExitReturnToFightPoint();
                        endWatcher.Cancel();
                        try { await watcherTask; } catch { /* ignore */ }
                        // 兜底释放 W 键，避免战斗结束 / 取消时角色继续前进
                        Simulation.SendInput.SimulateAction(GIActions.MoveForward, KeyType.KeyUp);
                    }

                    // §Q7 T5：两个触发器后置都同步重置 lastReturnAt + LastEnemySeenAt
                    lastReturnAt = DateTime.UtcNow;
                    LastEnemySeenAt = DateTime.UtcNow;
                }
                catch (OperationCanceledException) { return; }
                // NormalEndException（取消自动任务）继承自 System.Exception 而非 OperationCanceledException，
                // 属任务取消信号，应干净退出循环而非误报为"MoveTo 异常本轮跳过"。
                catch (BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception.NormalEndException) { return; }
                catch (Exception ex)
                {
                    TaskControl.Logger.LogError(ex, "[AutoFight][回点] MoveTo 异常，本轮跳过");
                }
            }
        }
        finally
        {
            TaskControl.Logger.LogDebug("[AutoFight][回点] 通用版回点后台任务已退出");
        }
    }

    // 无用
    // [Obsolete]
    // private bool HasFightFlagByGadget(ImageRegion imageRegion)
    // {
    //     // 小道具位置 1920-133,800,60,50
    //     var gadgetMat = imageRegion.DeriveCrop(AutoFightAssets.Instance.GadgetRect).SrcMat;
    //     var list = ContoursHelper.FindSpecifyColorRects(gadgetMat, new Scalar(225, 220, 225), new Scalar(255, 255, 255));
    //     // 要大于 gadgetMat 的 1/2
    //     return list.Any(r => r.Width > gadgetMat.Width / 2 && r.Height > gadgetMat.Height / 2);
    // }
}
