using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.GameTask.AutoFight;
using OfficialAutoFightRouter = BetterGenshinImpact.GameTask.AutoFightOfficial.OfficialAutoFightRouter;
using OfficialParamAdapter = BetterGenshinImpact.GameTask.AutoFightOfficial.OfficialParamAdapter;
using OfficialFightTask = BetterGenshinImpact.GameTask.AutoFightOfficial.AutoFightTask;
using BetterGenshinImpact.GameTask.AutoFriendship.Assets;
using BetterGenshinImpact.GameTask.AutoFriendship.Model;
using BetterGenshinImpact.GameTask.AutoPathing;
using BetterGenshinImpact.GameTask.AutoPathing.Model;
using BetterGenshinImpact.GameTask.AutoTrackPath;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Common.Map.Maps;
using BetterGenshinImpact.GameTask.Common.Map.Maps.Base;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.View.Windows;
using Microsoft.Extensions.Logging;
using OpenCvSharp;
using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Recognition.OCR;
using BetterGenshinImpact.Core.Simulator;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.Core.Script.Dependence;

namespace BetterGenshinImpact.GameTask.AutoFriendship;

/// <summary>
/// 好感任务自动完成 - 严格复刻 JS 脚本 main.js 的逻辑流程
/// </summary>
public partial class AutoFriendshipTask : ISoloTask, IDisposable
{
    private readonly ILogger<AutoFriendshipTask> _logger;
    private AutoFriendshipConfig _config = null!;
    private CancellationToken _ct;

    // 状态变量（精确对应 JS 全局变量）
    private bool _detectedExpOrMora = true;
    private int _noExpOrMoraCount = 0;
    private bool _running = true;
    private bool _fighting = false;
    private int _consecutiveMaxRetryCount = 0;

    // 战斗点坐标（用于离开区域判断，对应 JS 的 battlePointCoords 全局变量）
    private (double X, double Y)? _battlePointCoords;

    // OCR 关键词
    private List<string> _ocrKeywords = new();

    // 经验/摩拉模板
    private Mat? _expTemplate;
    private Mat? _moraTemplate;

    // 游戏区域截图缓存管理器（对应 JS gameRegionManager）
    private readonly List<ImageRegion> _gameRegionCache = new();
    private DateTime _lastCaptureTime = DateTime.MinValue;
    private bool _isDisposing = false;
    private bool _isCapturing = false;
    private const int GameRegionCacheSize = 3;

    public string Name => "好感任务自动完成";

    /// <summary>
    /// official-autofight-parallel-engine spec §4.3(E7)：按全局开关创建公版或茶包版战斗任务。
    /// 全局开关读 Config.AutoFightConfig（R3.6，非联机）；公版专属字段取全局 AutoFightOfficialConfig。
    /// AutoFriendship 自身维护的静态信号（如 AutoFightTask.FightEndTotoly）仍走茶包版语义，不受影响。
    /// </summary>
    private ISoloTask CreateFriendshipFightTask(AutoFightParam fightParam)
    {
        if (OfficialAutoFightRouter.UseOfficial(TaskContext.Instance().Config.AutoFightConfig, false))
        {
            var officialParam = OfficialParamAdapter.FromTeapot(fightParam, TaskContext.Instance().Config.AutoFightOfficialConfig);
            return new OfficialFightTask(officialParam);
        }
        return new AutoFightTask(fightParam);
    }

    /// <summary>
    /// 配置组传入的独立任务配置覆盖，为null时使用全局AutoFriendshipConfig
    /// </summary>
    private readonly Dictionary<string, object?>? _settingsOverride;

    /// <summary>
    /// 配置组传入的路径队伍配置，包含战斗和地图追踪参数
    /// </summary>
    private readonly PathingPartyConfig? _partyConfig;

    /// <summary>
    /// 独立任务页面传入的自动战斗配置，为null时使用全局AutoFightConfig
    /// </summary>
    private readonly AutoFightConfig? _autoFightConfig;

    public AutoFriendshipTask(AutoFriendshipConfig config, PathingPartyConfig? partyConfig = null, Dictionary<string, object?>? settings = null, AutoFightConfig? autoFightConfig = null)
    {
        _logger = App.GetLogger<AutoFriendshipTask>();
        _config = config;
        _partyConfig = partyConfig;
        _settingsOverride = settings;
        _autoFightConfig = autoFightConfig;
    }

    public async Task Start(CancellationToken ct)
    {
        _ct = ct;

        // 如果有配置组传入的覆盖配置，在全局配置的深拷贝上应用，避免污染全局状态
        if (_settingsOverride != null && _settingsOverride.Count > 0)
        {
            var json = System.Text.Json.JsonSerializer.Serialize(_config);
            _config = System.Text.Json.JsonSerializer.Deserialize<AutoFriendshipConfig>(json) ?? _config;

            ApplySettingsOverride();
        }

        _logger.LogInformation("好感任务开始，敌人类型: {EnemyType}", _config.EnemyType);

        try
        {
            // 加载资源
            LoadResources();

            // 切换队伍
            await SwitchPartyIfNeededAsync();

            // 启用/禁用自动拾取（通过 dispatcher.addTimer 控制）
            // C# 中由任务调度器统一管理，此处记录日志
            if (_config.DisablePickup)
            {
                _logger.LogInformation("已 禁用 自动拾取任务");
            }
            else
            {
                _logger.LogInformation("已 启用 自动拾取任务");
            }
            
            // 初始准备阶段（对应 JS 入口函数的准备部分）
            await InitialPreparationAsync();
            
            // 主循环
            await AutoFriendshipDevAsync();

            var elapsed = DateTime.Now - _startTime;
            _logger.LogInformation("{EnemyType}好感运行总时长：{Minutes} 分 {Seconds:D2} 秒",
                _config.EnemyType, (int)elapsed.TotalMinutes, elapsed.Seconds);
        }
        catch (OperationCanceledException)
        {
            _logger.LogInformation("好感任务已取消");
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "主循环中出现错误 {Message}", ex.Message);
        }
        finally
        {
            Cleanup();
        }
    }

    private DateTime _startTime = DateTime.Now;

    /// <summary>
    /// 加载 JS 脚本资源（模板、关键词等）
    /// </summary>
    private void LoadResources()
    {
        // 加载 OCR 关键词
        _ocrKeywords = LoadOcrKeywords();
        _logger.LogInformation("加载 OCR 关键词: {Count} 个", _ocrKeywords.Count);

        // 加载经验/摩拉模板
        _expTemplate = AutoFriendshipResourceLoader.LoadExpTemplate();
        _moraTemplate = AutoFriendshipResourceLoader.LoadMoraTemplate();
    }

    /// <summary>
    /// 加载指定敌人类型的 OCR 关键词（完全对应 JS ENEMY_CONFIG.ocrKeywords）
    /// </summary>
    private List<string> LoadOcrKeywords()
    {
        return _config.EnemyType switch
        {
            EnemyType.Fatui => new List<string> { "买卖", "不成", "正义存", "愚人众", "禁止", "危险", "运输", "打倒", "盗宝团", "丘丘人", "今晚", "伙食", "所有人" },
            EnemyType.HilichurlBrigade => new List<string> { "岛上", "无贼", "消灭", "鬼鬼祟祟", "盗宝团" },
            EnemyType.Crocodile => new List<string> { "张牙", "舞爪", "恶党", "鳄鱼", "打倒", "所有", "鳄鱼" },
            EnemyType.Fungus => new List<string> { "实验家", "变成", "实验品", "击败", "所有", "魔物" },
            EnemyType.ElectroMage => new List<string> { "雷萤", "术士", "圆滚滚", "不可食用", "威撼", "攀岩", "消灭", "准备", "打倒", "所有", "魔物", "盗宝团", "击败", "成员", "盗亦无道" },
            _ => new List<string> { "突发", "任务", "打倒", "消灭", "敌人", "所有" }
        };
    }

    /// <summary>
    /// 切换队伍（对应 JS switchPartyIfNeeded）
    /// </summary>
    private async Task SwitchPartyIfNeededAsync()
    {
        if (string.IsNullOrEmpty(_config.PartyName))
        {
            // 为空则回到主界面
            await GenshinReturnMainUiAsync();
            return;
        }

        try
        {
            _logger.LogInformation("正在尝试切换至 {PartyName}", _config.PartyName);
            var switchTask = new SwitchPartyTask();
            if (!await switchTask.Start(_config.PartyName, _ct))
            {
                _logger.LogInformation("切换队伍失败，前往七天神像重试");
                await GenshinTpToStatueOfTheSevenAsync();
                await switchTask.Start(_config.PartyName, _ct);
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "队伍切换失败，可能处于联机模式或其他不可切换状态");
            await GenshinReturnMainUiAsync();
        }
    }

    /// <summary>
    /// 初始准备阶段（对应 JS 入口函数的准备部分，只执行一次）
    /// </summary>
    private async Task InitialPreparationAsync()
    {
        // 盗宝团需要先清理丘丘人
        if (_config.EnemyType == EnemyType.HilichurlBrigade && _config.QiuQiuRenTimeoutSeconds > 0)
        {
            _logger.LogInformation("清理原住民...");
            var prepareNavigated = await AutoPathAsync("盗宝团-准备");

            if (AutoFriendshipTaskDecisions.ShouldSkipClearBattle(
                    _config.EnemyType, _config.QiuQiuRenTimeoutSeconds, prepareNavigated))
            {
                _logger.LogWarning("盗宝团-准备 路径未实际执行（资源缺失或加载失败），跳过清理战斗以避免原地空挥");
            }
            else
            {
                _logger.LogInformation("开始清理战斗，超时时间: {0}秒...", _config.QiuQiuRenTimeoutSeconds);
                await ExecuteClearBattleAsync(_config.QiuQiuRenTimeoutSeconds);
            }
        }

        // 执行 preparePath（愚人众、鳄鱼、蕈兽、雷萤术士有 preparePath）
        var preparePath = GetPreparePathName();
        if (!string.IsNullOrEmpty(preparePath))
        {
            _logger.LogInformation("导航到{EnemyType}触发点...", _config.EnemyType);
            await AutoPathAsync(preparePath);
        }
    }

    /// <summary>
    /// 获取 preparePath 名称（对应 JS ENEMY_CONFIG.preparePath）
    /// </summary>
    private string? GetPreparePathName()
    {
        return _config.EnemyType switch
        {
            EnemyType.Fatui => "愚人众-准备",
            EnemyType.HilichurlBrigade => null,
            EnemyType.Crocodile => "鳄鱼-准备",
            EnemyType.Fungus => "蕈兽-准备",
            EnemyType.ElectroMage => "雷萤术士-准备",
            _ => null
        };
    }

    /// <summary>
    /// 执行清理战斗（对应 JS 清理丘丘人的战斗逻辑）
    /// </summary>
    private async Task ExecuteClearBattleAsync(int timeoutSeconds)
    {
        using var cts = new CancellationTokenSource();
        try
        {
            _fighting = true;

            if (_config.DisableAsyncFight)
            {
                // 同步模式
                AutoFightParam fightParam;
                if (_partyConfig != null)
                {
                    // 配置组启动：使用配置组的战斗参数
                    var strategyName = _partyConfig.AutoFightConfig.StrategyName;
                    string strategyPath = "根据队伍自动选择".Equals(strategyName)
                        ? Global.Absolute(@"User\AutoFight\")
                        : Global.Absolute(@"User\AutoFight\" + strategyName + ".txt");
                    fightParam = new AutoFightParam(strategyPath, _partyConfig.AutoFightConfig);
                }
                else
                {
                    // 独立任务启动：优先使用页面配置，其次使用全局配置
                    if (_autoFightConfig != null)
                    {
                        var strategyName = _autoFightConfig.StrategyName;
                        string strategyPath = "根据队伍自动选择".Equals(strategyName)
                            ? Global.Absolute(@"User\AutoFight\")
                            : Global.Absolute(@"User\AutoFight\" + strategyName + ".txt");
                        fightParam = new AutoFightParam(strategyPath, _autoFightConfig);
                    }
                    else
                    {
                        fightParam = new AutoFightParam();
                    }
                }
                var fightTask = CreateFriendshipFightTask(fightParam);
                if(!cts.IsCancellationRequested)
                {
                    try
                    {
                        await fightTask.Start(cts.Token);
                    }
                    catch (OperationCanceledException)
                    {
                        throw;
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "清理战斗异常: {Message}", ex.Message);
                        AutoFightTask.FightEndTotoly = true;
                    }
                }
                _fighting = false;
            }
            else
            {
                if (!cts.IsCancellationRequested)
                {
                    Dispatcher.IsCustomCts = true;
                    AutoFightParam clearFightParam;
                    if (_autoFightConfig != null)
                    {
                        var strategyName = _autoFightConfig.StrategyName;
                        string strategyPath = "根据队伍自动选择".Equals(strategyName)
                            ? Global.Absolute(@"User\AutoFight\")
                            : Global.Absolute(@"User\AutoFight\" + strategyName + ".txt");
                        clearFightParam = new AutoFightParam(strategyPath, _autoFightConfig);
                    }
                    else
                    {
                        clearFightParam = new AutoFightParam();
                    }
                    var fightTask = CreateFriendshipFightTask(clearFightParam);
                    // 使用 Task.Run 确保战斗任务在后台线程运行，不阻塞当前逻辑
                    var fightHandle = Task.Run(() => fightTask.Start(cts.Token), cts.Token);

                    var timeoutTask = Task.Run(async () =>
                    {
                        var maxLoops = (int)Math.Ceiling(4d * timeoutSeconds);
                        for (int i = 0; i < maxLoops; i++)
                        {
                            if (!_fighting) break;
                            try { await Task.Delay(250, cts.Token); } catch { break; }
                        }
                        if (_fighting)
                        {
                            _logger.LogWarning("清理战斗超时，取消战斗");
                            AutoFightTask.FightEndTotoly = true;
                        }
                    }, cts.Token);

                    // 等待任意一个任务完成
                    await Task.WhenAny(fightHandle, timeoutTask);
                    
                    // 检查战斗任务是否抛出异常
                    if (fightHandle.IsFaulted && fightHandle.Exception != null)
                    {
                        var innerEx = fightHandle.Exception.InnerException ?? fightHandle.Exception;
                        if (innerEx is not OperationCanceledException)
                        {
                            _logger.LogError(innerEx, "清理战斗异常: {Message}", innerEx.Message);
                            AutoFightTask.FightEndTotoly = true;
                        }
                    }
                    
                    _fighting = false;
                
                    // 取消所有任务
                    try { AutoFightTask.FightEndTotoly = true; } catch { } 
                }
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "清理战斗异常: {Message}", ex.Message);
        }
        finally
        {
            _fighting = false;
            try {
                AutoFightTask.FightEndTotoly = true;
                await Delay(2500, _ct);
                cts.Cancel();
            } catch { }
        }
    }

    /// <summary>
    /// 主循环（对应 JS AutoFriendshipDev）
    /// </summary>
    private async Task AutoFriendshipDevAsync()
    {
        _startTime = DateTime.Now;
        int successCount = 0;
        int failureCount = 0;

        // 启动掉落检测后台任务
        var detectExpOrMoraTask = _config.LoopTillNoExpOrMora
            ? Task.Run(() => DetectExpOrMoraLoopAsync(), _ct)
            : null;

        try
        {
            var times = _config.RunTimes;
            for (var i = 0; i < times && _running; i++)
            {
                try
                {
                    var success = await ExecuteSingleFriendshipRoundAsync(i);
                    if (!success)
                        break;

                    successCount++;
                    LogProgress(_startTime, i, _config.RunTimes);
                }
                catch (Exception ex)
                {
                    if (i != 0)
                    {
                        _logger.LogError(ex, "第 {Round} 轮执行异常，继续下一轮", i + 1);
                    }

                    if (i == 0)
                    {
                        times = times + 1;
                    }
                    if(_ct.IsCancellationRequested)break;
                    continue;
                }
                if(_ct.IsCancellationRequested)break;
            }
        }
        finally
        {
            _running = false;
            _logger.LogInformation("本次运行统计：成功 {Success} 次，失败 {Failure} 次", successCount, failureCount);

            if (detectExpOrMoraTask != null)
            {
                try { await detectExpOrMoraTask; } catch { }
            }
        }
    }

    /// <summary>
    /// 打印进度信息（对应 JS logProgress）
    /// </summary>
    private void LogProgress(DateTime startTime, int currentRound, int totalRounds)
    {
        var elapsed = DateTime.Now - startTime;
        var timePerTask = elapsed.TotalMilliseconds / (currentRound + 1);
        var remainingTasks = totalRounds - currentRound - 1;
        var remainingTimeMs = timePerTask * Math.Max(0, remainingTasks);
        var estimatedCompletion = DateTime.Now + TimeSpan.FromMilliseconds(remainingTimeMs);

        var currentTimeStr = $"{(int)elapsed.TotalMinutes} 分 {elapsed.Seconds:D2} 秒";

        _logger.LogInformation("当前进度：{Current}/{Total} ({Percent:F1}%)", currentRound + 1, totalRounds, (currentRound + 1) * 100.0 / totalRounds);
        _logger.LogInformation("当前运行总时长：{Time}", currentTimeStr);
        _logger.LogInformation("预计完成时间：{Time} (约 {Minutes} 分钟)", estimatedCompletion.ToString("HH:mm:ss"), (int)(remainingTimeMs / 60000));
    }

    /// <summary>
    /// 执行单轮好感流程（精确对应 JS executeSingleFriendshipRound）
    /// 流程：导航触发点 -> 掉落检测 -> 首轮检测 -> relogin -> 导航战斗点 -> 战斗(重试) -> 战后/恢复
    /// </summary>
    private async Task<bool> ExecuteSingleFriendshipRoundAsync(int roundIndex)
    {
        // 步骤1: 导航到触发点
        await NavigateToTriggerPointAsync();

        // 步骤2: 掉落检测
        if (!_detectedExpOrMora && _config.LoopTillNoExpOrMora)
        {
            _noExpOrMoraCount++;
            if(roundIndex != 1)_logger.LogWarning("上次运行未检测到经验或摩拉");
            if (_noExpOrMoraCount >= 2)
            {
                _logger.LogWarning("连续两次循环没有经验或摩拉掉落，提前终止");
                return false;
            }
        }
        else
        {
            _noExpOrMoraCount = 0;
            _detectedExpOrMora = false;
        }

        // 步骤3: 首轮额外延迟（仅鳄鱼有 initialDelayMs = 5000）
        var initialDelayMs = GetInitialDelayMs();
        if (roundIndex == 0 && initialDelayMs > 0)
        {
            await Task.Delay(initialDelayMs, _ct);
        }

        // 步骤4: 首轮检测任务是否触发
        bool? ocrStatus = null;
        if (roundIndex == 0)
        {
            ocrStatus = await DetectTaskTriggerAsync();
        }

        // 步骤5: 未检测到则 relogin/wonderlandCycle 后再检测
        if (ocrStatus != true)
        {
            if (_config.Use1000Stars)
            {
                await GenshinWonderlandCycleAsync();
            }
            else
            {
                await GenshinReloginAsync();
            }
            ocrStatus = await DetectTaskTriggerAsync();
        }

        // 步骤6: 仍未检测到则终止
        if (ocrStatus != true)
        {
            _logger.LogInformation("未识别到突发任务，{EnemyType}好感结束", _config.EnemyType);
            return false;
        }

        // 步骤7: 导航到战斗点
        await NavigateToBattlePointAsync();

        // 步骤8: 执行战斗（最多重试2次）
        const int MaxRetryCount = 2;
        int retryCount = 0;

        while (true)
        {
            if (!_running) return false;

            var battleResult = await ExecuteBattleTasksAsync();

            if (battleResult.Status == "success")
            {
                _consecutiveMaxRetryCount = 0;
                await RunPostBattleAsync();
                return true;
            }

            // 战斗失败
            _logger.LogWarning("战斗失败，状态: {Status}，错误信息: {Error}", battleResult.Status, battleResult.ErrorMessage ?? "无");

            if (retryCount >= MaxRetryCount)
            {
                _consecutiveMaxRetryCount++;
                _logger.LogWarning("已尝试恢复 {Max} 次，第 {Consecutive} 次触发最大重试", MaxRetryCount, _consecutiveMaxRetryCount);

                if (_consecutiveMaxRetryCount >= 2)
                {
                    _logger.LogError("连续两次达到最大重试次数，终止任务");
                    return false;
                }

                // 容错处理：传送至七天神像并切换队伍
                _logger.LogInformation("尝试容错处理：传送至七天神像并切换队伍");
                await GenshinTeleportToStatueAsync();
                await SwitchPartyIfNeededAsync();
                _logger.LogInformation("容错处理完成，进入下一轮");
                return true;
            }

            await RecoverAfterFailureAsync();
            retryCount++;
            _logger.LogInformation("第 {Retry} 次恢复后，重新导航至战斗点...", retryCount);
            await NavigateToBattlePointAsync();
            _logger.LogInformation("第 {Retry} 次恢复后，重新执行战斗...", retryCount);
        }
    }

    /// <summary>
    /// 获取初始延迟（对应 JS ENEMY_CONFIG.initialDelayMs）
    /// </summary>
    private int GetInitialDelayMs()
    {
        return _config.EnemyType switch
        {
            EnemyType.Crocodile => 5000,
            _ => 0
        };
    }

    /// <summary>
    /// 导航到触发点（对应 JS navigateToTriggerPoint）
    /// 读取 {enemyType}-触发点.json 末位坐标，距离 ≤8m 才算到达
    /// </summary>
    private async Task NavigateToTriggerPointAsync()
    {
        var locationName = $"{GetEnemyDisplayName()}-触发点";
        var (targetX, targetY) = LoadTargetPosition(locationName);
        await NavigateWithRetryLoopAsync(locationName, targetX, targetY, distanceThreshold: 8);
    }

    /// <summary>
    /// 导航到战斗点（对应 JS navigateToBattlePoint）
    /// 同时保存 battlePointCoords 用于后续离开区域判断
    /// </summary>
    private async Task NavigateToBattlePointAsync()
    {
        var locationName = $"{GetEnemyDisplayName()}-战斗点";
        var (targetX, targetY) = LoadTargetPosition(locationName);

        // 保存战斗点坐标（对应 JS 的 battlePointCoords 全局变量）
        if (targetX.HasValue && targetY.HasValue)
        {
            _battlePointCoords = (targetX.Value, targetY.Value);
        }

        await NavigateWithRetryLoopAsync(locationName, targetX, targetY, distanceThreshold: 8);
    }

    /// <summary>
    /// 读取路径文件末位坐标（对应 JS navigateToTriggerPoint/navigateToBattlePoint 的坐标读取逻辑）
    /// </summary>
    private (double? X, double? Y) LoadTargetPosition(string locationName)
    {
        try
        {
            var filePath = Path.Combine(AutoFriendshipResourceLoader.GetAutoPathFolder(), $"{locationName}.json");
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("读取触发点配置失败: 路径文件不存在 {Path}", filePath);
                return (null, null);
            }

            var task = PathingTask.BuildFromFilePath(filePath);
            if (task?.Positions != null && task.Positions.Count > 0)
            {
                var lastPos = task.Positions[^1];
                _logger.LogDebug("从 {Location} 读取目标坐标: ({X}, {Y})", locationName, lastPos.X, lastPos.Y);
                return (lastPos.X, lastPos.Y);
            }
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "读取触发点配置失败: {Message}", ex.Message);
        }

        return (null, null);
    }

    /// <summary>
    /// 带循环重试的导航（精确对应 JS navigateToTriggerPoint/navigateToBattlePoint 的 while 重试逻辑）
    /// </summary>
    private async Task NavigateWithRetryLoopAsync(string locationName, double? targetX, double? targetY, double distanceThreshold)
    {
        if (!targetX.HasValue || !targetY.HasValue)
        {
            _logger.LogWarning("未配置 {Location} 的坐标，跳过距离校验", locationName);
            await AutoPathAsync(locationName);
            return;
        }

        const int maxRetries = 3;

        for (int retryCount = 0; retryCount < maxRetries; retryCount++)
        {
            if (!_running) break;

            var pos = SafeGetPositionFromMap();
            if (pos.HasValue)
            {
                var distance = Math.Sqrt(Math.Pow(pos.Value.X - targetX.Value, 2) + Math.Pow(pos.Value.Y - targetY.Value, 2));
                if (distance <= distanceThreshold)
                {
                    _logger.LogInformation("已到达{Location}附近，距离: {Distance:F2}米", locationName, distance);
                    return;
                }
                _logger.LogInformation("未到达{Location}，当前距离: {Distance:F2}米，正在导航...", locationName, distance);
            }

            await AutoPathAsync(locationName);
        }
    }

    /// <summary>
    /// 安全获取地图坐标（对应 JS safeGetPositionFromMap）
    /// </summary>
    private (double X, double Y)? SafeGetPositionFromMap()
    {
        try
        {
            var capture = CaptureToRectArea();
            var mapMatchMethod = TaskContext.Instance().Config.PathingConditionConfig.MapMatchingMethod;
            var imagePosition = Navigation.GetPositionStable(capture, MapTypes.Teyvat.ToString(), mapMatchMethod);
            // 将图像像素坐标转换为原神世界坐标
            var genshinPosition = MapManager.GetMap(MapTypes.Teyvat.ToString(), mapMatchMethod)
                .ConvertImageCoordinatesToGenshinMapCoordinates(imagePosition);
            if (genshinPosition == null)
            {
                return null;
            }
            return (genshinPosition.Value.X, genshinPosition.Value.Y);
        }
        catch
        {
            return null;
        }
    }

    /// <summary>
    /// 执行路径文件（对应 JS AutoPath）
    /// </summary>
    /// <returns>true = PathExecutor.Pathing 成功返回；false = 路径文件缺失 / 加载失败 / 异常</returns>
    private async Task<bool> AutoPathAsync(string locationName)
    {
        try
        {
            var filePath = Path.Combine(AutoFriendshipResourceLoader.GetAutoPathFolder(), $"{locationName}.json");
            if (!File.Exists(filePath))
            {
                _logger.LogWarning("路径文件不存在: {Path}", filePath);
                return false;
            }

            var task = PathingTask.BuildFromFilePath(filePath);
            if (task == null)
            {
                _logger.LogWarning("路径加载失败: {Location}", locationName);
                return false;
            }

            var pathExecutor = new PathExecutor(_ct);
            if (_partyConfig != null)
            {
                pathExecutor.PartyConfig = _partyConfig;
            }

            await pathExecutor.Pathing(task);
            return true;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "执行 {Location} 路径时发生错误: {Message}", locationName, ex.Message);
            return false;
        }
    }

    /// <summary>
    /// OCR 检测是否触发突发任务（对应 JS detectTaskTrigger）
    /// 在 (0, 200, 300, 300) 区域检测 _ocrKeywords
    /// </summary>
    private async Task<bool> DetectTaskTriggerAsync()
    {
        _logger.LogInformation("开始检测任务触发，OCR 超时 {Timeout} 秒", _config.OcrTimeoutSeconds);

        using var timeoutCts = new CancellationTokenSource(TimeSpan.FromSeconds(_config.OcrTimeoutSeconds));
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(_ct, timeoutCts.Token);

        while (!linkedCts.Token.IsCancellationRequested)
        {
            if (!_running) break;

            try
            {
                var region = await GetGameRegionAsync();
                var results = region.FindMulti(RecognitionObject.Ocr(0, 200, 300, 300));

                for (int o = 0; o < results.Count; o++)
                {
                    var res = results[o];
                    if (res == null || string.IsNullOrEmpty(res.Text)) continue;

                    foreach (var keyword in _ocrKeywords)
                    {
                        if (res.Text.Contains(keyword))
                        {
                            _logger.LogInformation("检测到突发任务触发: {Keyword}", keyword);
                            return true;
                        }
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR检测突发任务过程中出错: {Message}", ex.Message);
            }

            await Task.Delay(1000, linkedCts.Token);
        }

        _logger.LogInformation("OCR 检测超时，未检测到任务触发");
        return false;
    }

    /// <summary>
    /// 执行战斗任务（精确对应 JS executeBattleTasks）
    /// </summary>
    private async Task<BattleResult> ExecuteBattleTasksAsync()
    {
        _logger.LogInformation("开始战斗!");

        var cts = new CancellationTokenSource();
        try
        {
            if (_config.DisableAsyncFight)
            {
                // 同步模式
                AutoFightParam fightParam;
                if (_partyConfig != null)
                {
                    // 配置组启动：使用配置组的战斗参数
                    var strategyName = _partyConfig.AutoFightConfig.StrategyName;
                    string strategyPath = "根据队伍自动选择".Equals(strategyName)
                        ? Global.Absolute(@"User\AutoFight\")
                        : Global.Absolute(@"User\AutoFight\" + strategyName + ".txt");
                    fightParam = new AutoFightParam(strategyPath, _partyConfig.AutoFightConfig);
                }
                else
                {
                    // 独立任务启动：优先使用页面配置，其次使用全局配置
                    if (_autoFightConfig != null)
                    {
                        var strategyName = _autoFightConfig.StrategyName;
                        string strategyPath = "根据队伍自动选择".Equals(strategyName)
                            ? Global.Absolute(@"User\AutoFight\")
                            : Global.Absolute(@"User\AutoFight\" + strategyName + ".txt");
                        fightParam = new AutoFightParam(strategyPath, _autoFightConfig);
                    }
                    else
                    {
                        fightParam = new AutoFightParam();
                    }
                }
                var fightTask = CreateFriendshipFightTask(fightParam);
                if(!cts.IsCancellationRequested)await fightTask.Start(cts.Token);
                return new BattleResult { Status = "success" };
            }
            else
            {
                // 异步模式：并发启动战斗 + OCR 检测结果
                Dispatcher.IsCustomCts = true;
                AutoFightParam fightParam;
                if (_partyConfig != null)
                {
                    // 配置组启动：使用配置组的战斗参数
                    var strategyName = _partyConfig.AutoFightConfig.StrategyName;
                    string strategyPath = "根据队伍自动选择".Equals(strategyName)
                        ? Global.Absolute(@"User\AutoFight\")
                        : Global.Absolute(@"User\AutoFight\" + strategyName + ".txt");
                    fightParam = new AutoFightParam(strategyPath, _partyConfig.AutoFightConfig);
                }
                else
                {
                    // 独立任务启动：优先使用页面配置，其次使用全局配置
                    if (_autoFightConfig != null)
                    {
                        var strategyName = _autoFightConfig.StrategyName;
                        string strategyPath = "根据队伍自动选择".Equals(strategyName)
                            ? Global.Absolute(@"User\AutoFight\")
                            : Global.Absolute(@"User\AutoFight\" + strategyName + ".txt");
                        fightParam = new AutoFightParam(strategyPath, _autoFightConfig);
                    }
                    else
                    {
                        fightParam = new AutoFightParam();
                    }
                }
                var fightTask = CreateFriendshipFightTask(fightParam);
                // 使用 Task.Run 确保战斗任务在后台线程运行，不阻塞当前逻辑
                
                Task? fightHandle = null;
                if (!cts.IsCancellationRequested)
                {
                    fightHandle = fightTask.Start(cts.Token);
                }
                else
                {
                    _logger.LogWarning("战斗任务启动时已取消");
                    return new BattleResult { Status = "error", ErrorMessage = "战斗任务启动时已取消" };
                }
                
                _fighting = true;

                var statusPromise = WaitForBattleResultAsync(cts);

                // 等待任意一个任务完成，提高响应速度并避免不必要的等待
                var completedTask = await Task.WhenAny(fightHandle, statusPromise);

                _fighting = false;

                if (completedTask == statusPromise)
                {
                    // OCR 检测先出结果，立即取消战斗任务
                    var status = await statusPromise;
                    try { AutoFightTask.FightEndTotoly = true;  } catch { }
                    return new BattleResult { Status = status };
                }
                else
                {
                    // 战斗任务先结束（AutoFightTask 自行完成或异常）
                    try { AutoFightTask.FightEndTotoly = true; } catch { }
                    
                    // 检查战斗任务是否异常
                    if (fightHandle.IsFaulted)
                    {
                        var ex = fightHandle.Exception?.InnerException;
                        var msg = ex?.Message ?? "";
                        if (msg.Contains("取消自动任务"))
                        {
                            return new BattleResult { Status = "success" };
                        }
                        _logger.LogError(ex, "战斗执行过程中出错: {Message}", msg);
                        return new BattleResult { Status = "error", ErrorMessage = msg };
                    }
                    
                    // 战斗脚本执行完毕，视为成功
                    return new BattleResult { Status = "success" };
                }
            }
        }
        catch (Exception ex)
        {
            var msg = ex.Message ?? "";
            if (msg.Contains("取消自动任务"))
            {
                return new BattleResult { Status = "success" };
            }
            _logger.LogError(ex, "战斗执行过程中出错: {Message}", msg);
            return new BattleResult { Status = "error", ErrorMessage = msg };
        }
        finally
        {
            try { AutoFightTask.FightEndTotoly = true;
                await Delay(2500, _ct);
                cts.Cancel();}
            catch { }
            Simulation.SendInput.Mouse.LeftButtonUp();
        }
    }

    /// <summary>
    /// OCR 轮询战斗结果（精确对应 JS waitForBattleResult）
    /// 区域1 (850, 150, 200, 80): 检测"事件"/"完成"/"失败"
    /// 区域2 (0, 200, 300, 300): 检测事件关键词
    /// </summary>
    private async Task<string> WaitForBattleResultAsync(CancellationTokenSource cts)
    {
        var timeoutMs = _config.FightTimeoutSeconds * 1000;
        var fightStartTime = DateTime.Now;
        var successKeywords = new List<string> { "事件", "完成" };
        var failureKeywords = new List<string> { "失败" };
        var pollIntervalMs = 500;
        int notFindCount = 0;

        while ((DateTime.Now - fightStartTime).TotalMilliseconds < timeoutMs)
        {
            if (!_running) break;
            if (!_fighting) break;

            try
            {
                var region = await GetGameRegionAsync();

                // 区域1: 事件/完成/失败检测
                var result1 = region.Find(RecognitionObject.Ocr(850, 150, 200, 80));
                var text1 = (result1?.Text ?? "").Replace(" ", "").Replace("\r", "").Replace("\n", "");

                // 区域2: 任务触发关键词检测
                var result2 = region.Find(RecognitionObject.Ocr(0, 200, 300, 300));
                var text2 = (result2?.Text ?? "").Replace(" ", "").Replace("\r", "").Replace("\n", "");

                // 蕈兽特殊：检测"维沙瓦"
                if (_config.EnemyType == EnemyType.Fungus && text2.Contains("维沙瓦"))
                {
                    _logger.LogInformation("战斗结果：成功");
                    try { AutoFightTask.FightEndTotoly = true;} catch { }
                    return "success";
                }

                // 检查成功关键词（开战后2秒以上）
                if ((DateTime.Now - fightStartTime).TotalMilliseconds >= 2000)
                {
                    foreach (var keyword in successKeywords)
                    {
                        if (text1.Contains(keyword))
                        {
                            _logger.LogInformation("检测到战斗成功关键词: {Keyword}", keyword);
                            _logger.LogInformation("战斗结果：成功");
                            try { AutoFightTask.FightEndTotoly = true; } catch { }
                            return "success";
                        }
                    }
                }

                // 检查失败关键词
                foreach (var keyword in failureKeywords)
                {
                    if (text1.Contains(keyword))
                    {
                        _logger.LogWarning("检测到战斗失败关键词: {Keyword}", keyword);
                        try { AutoFightTask.FightEndTotoly = true; } catch { }
                        return "failure";
                    }
                }

                // 非蕈兽：检测事件关键词
                if (_config.EnemyType != EnemyType.Fungus)
                {
                    int findCount = 0;
                    foreach (var keyword in _ocrKeywords)
                    {
                        if (text2.Contains(keyword)) findCount++;
                    }

                    if (findCount == 0)
                    {
                        notFindCount++;
                        _logger.LogDebug("未检测到任务触发关键词：{0} 次", notFindCount);
                    }
                    else
                    {
                        notFindCount = 0;
                    }

                    if (notFindCount > 10)
                    {
                        // 检查是否在战斗点附近（距离 ≤25m）
                        var nearBattlePoint = false;
                        if (_battlePointCoords.HasValue)
                        {
                            var pos = SafeGetPositionFromMap();
                            if (pos.HasValue)
                            {
                                var dx = pos.Value.X - _battlePointCoords.Value.X;
                                var dy = pos.Value.Y - _battlePointCoords.Value.Y;
                                var dist = Math.Sqrt(dx * dx + dy * dy);
                                nearBattlePoint = dist <= 25;
                            }
                        }

                        if (nearBattlePoint)
                        {
                            _logger.LogInformation("触发关键词消失但仍在战斗点附近，视为本轮结束");
                            try { AutoFightTask.FightEndTotoly = true; } catch { }
                            return "success";
                        }

                        _logger.LogWarning("不在任务触发区域，战斗失败");
                        try { AutoFightTask.FightEndTotoly = true; } catch { }
                        return "out_of_area";
                    }
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "OCR过程中出错");
            }

            await Task.Delay(pollIntervalMs, _ct);
        }

        _logger.LogWarning("在超时时间内未检测到战斗结果");
        try { AutoFightTask.FightEndTotoly = true; } catch { }
        return "failure";
    }

    /// <summary>
    /// 战后处理（精确对应 JS runPostBattle）
    /// </summary>
    private async Task RunPostBattleAsync()
    {
        // 执行 postBattlePath（鳄鱼有"鳄鱼-拾取"，蕈兽有"蕈兽-对话"）
        var postBattlePath = GetPostBattlePathName();
        if (!string.IsNullOrEmpty(postBattlePath))
        {
            await AutoPathAsync(postBattlePath);
        }

        // 蕈兽特殊对话流程
        if (_config.EnemyType == EnemyType.Fungus)
        {
            await Task.Delay(50, _ct);
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_F);
            await Task.Delay(50, _ct);
            Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_F);
            await Task.Delay(500, _ct);
            await GenshinChooseTalkOptionAsync("下次");
            await Task.Delay(500, _ct);
        }
    }

    /// <summary>
    /// 获取 postBattlePath（对应 JS ENEMY_CONFIG.postBattlePath）
    /// </summary>
    private string? GetPostBattlePathName()
    {
        return _config.EnemyType switch
        {
            EnemyType.Crocodile => "鳄鱼-拾取",
            EnemyType.Fungus => "蕈兽-对话",
            _ => null
        };
    }

    /// <summary>
    /// 失败恢复（精确对应 JS recoverAfterFailure）
    /// 传送到七天神像 -> 执行 failReturnPath -> 额外等待
    /// </summary>
    private async Task RecoverAfterFailureAsync()
    {
        // 失败统一返回七天神像
        await GenshinTpToStatueOfTheSevenAsync();

        // 执行 failReturnPath（优先于 preparePath）
        var failReturnPath = GetFailReturnPathName();
        if (!string.IsNullOrEmpty(failReturnPath))
        {
            await AutoPathAsync(failReturnPath);
        }
        else
        {
            // fallback 到 preparePath
            var preparePath = GetPreparePathName();
            if (!string.IsNullOrEmpty(preparePath))
            {
                await AutoPathAsync(preparePath);
            }
            else
            {
                await AutoPathAsync($"{GetEnemyDisplayName()}-触发点");
            }
        }

        // 额外等待（仅鳄鱼有 failReturnSleepMs = 5000）
        var failReturnSleepMs = GetFailReturnSleepMs();
        if (failReturnSleepMs > 0)
        {
            _logger.LogInformation("等待 {Ms} 毫秒", failReturnSleepMs);
            await Task.Delay(failReturnSleepMs, _ct);
        }
    }

    /// <summary>
    /// 获取 failReturnPath（对应 JS ENEMY_CONFIG.failReturnPath）
    /// </summary>
    private string? GetFailReturnPathName()
    {
        return _config.EnemyType switch
        {
            EnemyType.Fatui => "愚人众-准备",
            EnemyType.HilichurlBrigade => "盗宝团-准备",
            EnemyType.Crocodile => "鳄鱼-准备",
            EnemyType.Fungus => "蕈兽-准备",
            EnemyType.ElectroMage => "雷萤术士-准备",
            _ => null
        };
    }

    /// <summary>
    /// 获取失败恢复额外等待时间（对应 JS ENEMY_CONFIG.failReturnSleepMs）
    /// </summary>
    private int GetFailReturnSleepMs()
    {
        return _config.EnemyType switch
        {
            EnemyType.Crocodile => 5000,
            _ => 0
        };
    }

    /// <summary>
    /// 掉落检测后台循环（精确对应 JS detectExpOrMora）
    /// 模板匹配检测经验/摩拉图标，设置 detectedExpOrMora = true
    /// </summary>
    private async Task DetectExpOrMoraLoopAsync()
    {
        _logger.LogInformation("启动掉落检测循环");

        while (_running)
        {
            try
            {
                if (!_detectedExpOrMora)
                {
                    var region = await GetGameRegionAsync();

                    // 检测经验（模板区域: 74, 341, 133, 462）
                    if (_expTemplate != null && !_expTemplate.Empty())
                    {
                        using var res = new Mat();
                        Cv2.MatchTemplate(region.SrcMat, _expTemplate, res, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out _);
                        if (maxVal >= 0.85)
                        {
                            _logger.LogInformation("识别到经验");
                            _detectedExpOrMora = true;
                            await Task.Delay(200, _ct);
                            continue;
                        }
                    }

                    // 检测摩拉
                    if (_moraTemplate != null && !_moraTemplate.Empty())
                    {
                        using var res = new Mat();
                        Cv2.MatchTemplate(region.SrcMat, _moraTemplate, res, TemplateMatchModes.CCoeffNormed);
                        Cv2.MinMaxLoc(res, out _, out double maxVal, out _, out _);
                        if (maxVal >= 0.85)
                        {
                            _logger.LogInformation("识别到摩拉");
                            _detectedExpOrMora = true;
                            await Task.Delay(200, _ct);
                            continue;
                        }
                    }
                }
                else
                {
                    await Task.Delay(200, _ct);
                }

                await Task.Delay(200, _ct);
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "检测经验和摩拉掉落过程中出现错误: {Message}", ex.Message);
            }
        }
    }

    /// <summary>
    /// 获取游戏区域截图（精确对应 JS getGameRegion + gameRegionManager 逻辑）
    /// 缓存近 GameRegionCacheSize 张截图，避免频繁截图
    /// </summary>
    private async Task<ImageRegion> GetGameRegionAsync(int minIntervalMs = 17)
    {
        // 等待其他任务完成截图
        while (_isCapturing)
        {
            await Task.Delay(1, _ct);
        }

        _isCapturing = true;
        try
        {
            var now = DateTime.Now;
            if ((now - _lastCaptureTime).TotalMilliseconds >= minIntervalMs || _gameRegionCache.Count == 0)
            {
                while (_isDisposing) await Task.Delay(1, _ct);

                _lastCaptureTime = now;
                var newRegion = CaptureToRectArea();
                _gameRegionCache.Add(newRegion);

                // 释放超量缓存
                while (_gameRegionCache.Count > GameRegionCacheSize)
                {
                    var oldest = _gameRegionCache[0];
                    oldest.Dispose();
                    _gameRegionCache.RemoveAt(0);
                }
            }
        }
        finally
        {
            _isCapturing = false;
        }

        return _gameRegionCache[^1];
    }

    // ===== 以下为 Game 基操方法的委托实现 =====

    private async Task GenshinReloginAsync()
    {
        _logger.LogInformation("执行重新登录...");
        await new ExitAndReloginJob().Start(_ct);
    }

    private async Task GenshinWonderlandCycleAsync()
    {
        _logger.LogInformation("执行千番胜循环...");
        await new EnterAndExitWonderlandJob().Start(_ct);
    }

    private async Task GenshinTeleportToStatueAsync()
    {
        _logger.LogInformation("传送至七天神像...");
        await new TpTask(_ct).TpToStatueOfTheSeven();
    }

    private async Task GenshinTpToStatueOfTheSevenAsync()
    {
        _logger.LogInformation("传送至七天神像...");
        await new TpTask(_ct).TpToStatueOfTheSeven();
    }

    private async Task GenshinReturnMainUiAsync()
    {
        _logger.LogInformation("返回主界面...");
        await new ReturnMainUiTask().Start(_ct);
    }

    /// <summary>
    /// 选择对话选项（OCR 识别并点击）
    /// </summary>
    private async Task GenshinChooseTalkOptionAsync(string optionText)
    {
        _logger.LogInformation("选择对话选项: {Option}", optionText);

        try
        {
            var region = await GetGameRegionAsync();
            var results = region.FindMulti(RecognitionObject.Ocr(
                (int)(region.Width * 0.25),
                (int)(region.Height * 0.4),
                (int)(region.Width * 0.5),
                (int)(region.Height * 0.35)));

            for (int i = 0; i < results.Count; i++)
            {
                var res = results[i];
                if (res != null && !string.IsNullOrEmpty(res.Text) && res.Text.Contains(optionText))
                {
                    res.Click();
                    _logger.LogInformation("已点击对话选项: {Text}", res.Text);
                    return;
                }
            }

            _logger.LogWarning("未找到对话选项: {Option}", optionText);
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "选择对话选项失败: {Message}", ex.Message);
        }

        await Task.CompletedTask;
    }

    /// <summary>
    /// 清理资源
    /// </summary>
    private void Cleanup()
    {
        _logger.LogInformation("清理资源...");
        _running = false;
        _fighting = false;

        // 释放截图缓存
        foreach (var region in _gameRegionCache)
        {
            try { region.Dispose(); } catch { }
        }
        _gameRegionCache.Clear();

        // 释放模板
        if (_expTemplate != null) { _expTemplate.Dispose(); _expTemplate = null; }
        if (_moraTemplate != null) { _moraTemplate.Dispose(); _moraTemplate = null; }
    }

    /// <summary>
    /// 释放资源
    /// </summary>
    public void Dispose()
    {
        if (_isDisposing) return;
        _isDisposing = true;
        Cleanup();
        GC.SuppressFinalize(this);
    }

    /// <summary>
    /// 获取敌人类型显示名称
    /// </summary>
    private string GetEnemyDisplayName()
    {
        return _config.EnemyType switch
        {
            EnemyType.Fatui => "愚人众",
            EnemyType.HilichurlBrigade => "盗宝团",
            EnemyType.Crocodile => "鳄鱼",
            EnemyType.Fungus => "蕈兽",
            EnemyType.ElectroMage => "雷萤术士",
            _ => "未知"
        };
    }

    // ===== 内部数据结构 =====

    private class BattleResult
    {
        public string Status { get; set; } = "";
        public string? ErrorMessage { get; set; }
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

        // enemyType: 下拉框值是中文显示名，需要转换为 EnemyType 枚举
        var enemyTypeStr = Get("enemyType", "");
        if (!string.IsNullOrEmpty(enemyTypeStr))
        {
            var enemyMap = new Dictionary<string, EnemyType>
            {
                ["愚人众"] = EnemyType.Fatui,
                ["盗宝团"] = EnemyType.HilichurlBrigade,
                ["鳄鱼"] = EnemyType.Crocodile,
                ["蕈兽"] = EnemyType.Fungus,
                ["雷萤术士"] = EnemyType.ElectroMage
            };
            if (enemyMap.TryGetValue(enemyTypeStr, out var type))
                _config.EnemyType = type;
        }

        _config.RunTimes = Get("runTimes", _config.RunTimes);
        _config.OcrTimeoutSeconds = Get("ocrTimeout", _config.OcrTimeoutSeconds);
        _config.FightTimeoutSeconds = Get("fightTimeout", _config.FightTimeoutSeconds);
        _config.QiuQiuRenTimeoutSeconds = Get("qiuQiuRen", _config.QiuQiuRenTimeoutSeconds);
        _config.PartyName = Get("partyName", _config.PartyName);
        _config.LoopTillNoExpOrMora = Get("loopTillNoExpOrMora", _config.LoopTillNoExpOrMora);
        _config.DisablePickup = Get("disablePickup", _config.DisablePickup);
        _config.DisableAsyncFight = Get("disableAsyncFight", _config.DisableAsyncFight);
        _config.Use1000Stars = Get("use1000Stars", _config.Use1000Stars);
    }

    /// <summary>
    /// 获取可配置参数定义（供UI编辑使用），顺序和说明与JS settings.json一致
    /// </summary>
    public static List<SoloTaskSettingItem> GetSettingDefinitions()
    {
        var config = TaskContext.Instance().Config.AutoFriendshipConfig;

        return new List<SoloTaskSettingItem>
        {
            new() { Name = "enemyType", Label = "敌人类型", Type = "select", DefaultValue = GetEnemyDisplayNameFromEnum(config.EnemyType),
                Options = new() { "愚人众", "盗宝团", "鳄鱼", "蕈兽", "雷萤术士" } },
            new() { Name = "runTimes", Label = "执行次数", Type = "number", DefaultValue = config.RunTimes },
            new() { Name = "ocrTimeout", Label = "OCR检测突发任务超时时间（秒）", Type = "number", DefaultValue = config.OcrTimeoutSeconds },
            new() { Name = "fightTimeout", Label = "战斗超时时间（秒）", Type = "number", DefaultValue = config.FightTimeoutSeconds },
            new() { Name = "qiuQiuRen", Label = "清理丘丘人超时时间（秒）\n设为0则不清理，仅盗宝团生效", Type = "number", DefaultValue = config.QiuQiuRenTimeoutSeconds },
            new() { Name = "partyName", Label = "切换队伍名称\n留空则不切换队伍", Type = "text", DefaultValue = config.PartyName },
            new() { Name = "loopTillNoExpOrMora", Label = "循环至无经验/摩拉掉落\n勾选后连续2轮无掉落则自动终止", Type = "bool", DefaultValue = config.LoopTillNoExpOrMora },
            new() { Name = "disablePickup", Label = "禁用自动拾取", Type = "bool", DefaultValue = config.DisablePickup },
            new() { Name = "disableAsyncFight", Label = "禁用异步战斗\n勾选后使用同步战斗模式，依赖配置组的战斗结束检测自行退出", Type = "bool", DefaultValue = config.DisableAsyncFight },
            new() { Name = "use1000Stars", Label = "使用幻想真境剧诗重置\n勾选后未检测到触发时使用wonderlandCycle而非relogin", Type = "bool", DefaultValue = config.Use1000Stars },
        };
    }

    private static string GetEnemyDisplayNameFromEnum(EnemyType type)
    {
        return type switch
        {
            EnemyType.Fatui => "愚人众",
            EnemyType.HilichurlBrigade => "盗宝团",
            EnemyType.Crocodile => "鳄鱼",
            EnemyType.Fungus => "蕈兽",
            EnemyType.ElectroMage => "雷萤术士",
            _ => "盗宝团"
        };
    }
}
