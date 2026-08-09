using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Dependence.Model;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoBoss;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoEat;
using BetterGenshinImpact.GameTask.AutoFishing;
using BetterGenshinImpact.GameTask.AutoCook;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation;
using BetterGenshinImpact.GameTask.AutoPathing.Handler;
using BetterGenshinImpact.GameTask.AutoWood;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.Model.GameUI;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.ClearScript;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Dynamic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.GameTask.AutoFight;
using OfficialAutoFightRouter = BetterGenshinImpact.GameTask.AutoFightOfficial.OfficialAutoFightRouter;
using OfficialParamAdapter = BetterGenshinImpact.GameTask.AutoFightOfficial.OfficialParamAdapter;
using OfficialFightTask = BetterGenshinImpact.GameTask.AutoFightOfficial.AutoFightTask;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoWood.Assets;
using BetterGenshinImpact.GameTask.AutoWood.Utils;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.View.Drawable;
using Microsoft.Extensions.Logging;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator.Extensions;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static Vanara.PInvoke.User32;
using GC = System.GC;
using OpenCvSharp;
using BetterGenshinImpact.Core.Recognition.OpenCv;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.GameTask.AutoFight.Script;
using BetterGenshinImpact.GameTask.AutoLeyLineOutcrop;
using BetterGenshinImpact.GameTask.AutoStygianOnslaught;
using BetterGenshinImpact.GameTask.Common;

namespace BetterGenshinImpact.Core.Script.Dependence;

public class Dispatcher
{
    private readonly ILogger<Dispatcher> _logger = App.GetLogger<Dispatcher>();

    private readonly object _config;
    
    public static bool IsCustomCts { get; set; } = false;

    private AllConfig AllConfig { get; set; } = TaskContext.Instance().Config;
    
    public Dispatcher(object config)
    {
        _config = config;
    }

    public void RunTask()
    {
    }

    /// <summary>
    /// 添加实时任务,会清理之前的所有任务
    /// </summary>
    /// <param name="timer">实时任务触发器</param>
    /// <exception cref="ArgumentNullException"></exception>
    public void AddTimer(RealtimeTimer timer)
    {
        ClearAllTriggers();
        try
        {
            AddTrigger(timer);
        }
        catch (ArgumentException e)
        {
            if (e is ArgumentNullException)
            {
                throw;
            }
        }
    }

    /// <summary>
    /// 清理所有实时任务
    /// </summary>
    public void ClearAllTriggers()
    {
        TaskTriggerDispatcher.Instance().ClearTriggers();
    }

    /// <summary>
    /// 添加实时任务,不会清理之前的任务
    /// </summary>
    /// <param name="timer"></param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public void AddTrigger(RealtimeTimer timer)
    {
        var realtimeTimer = timer;
        if (realtimeTimer == null)
        {
            throw new ArgumentNullException(nameof(realtimeTimer), "实时任务对象不能为空");
        }

        if (string.IsNullOrEmpty(realtimeTimer.Name))
        {
            throw new ArgumentNullException(nameof(realtimeTimer.Name), "实时任务名称不能为空");
        }

        if (!TaskTriggerDispatcher.Instance().AddTrigger(realtimeTimer.Name, realtimeTimer.Config))
        {
            throw new ArgumentException($"添加实时任务失败: {realtimeTimer.Name}", nameof(realtimeTimer.Name));
        }
    }

    public async Task RunTask(SoloTask soloTask, CancellationTokenSource customCts)
    {
        // 创建链接的取消令牌源，任何一个取消都会触发
        CancellationTokenSource linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            customCts.Token,
            CancellationContext.Instance.Cts.Token);
        await RunTask(soloTask, linkedCts.Token);
    }

    public async Task<List<string>> RunTask(SoloTask soloTask,bool isWoodExecute)
    {
        if (soloTask == null || soloTask.Name != "AutoWood")
        {
            throw new ArgumentNullException(nameof(soloTask), "独立任务对象不能为空或非自动伐木任务");
        }

        var taskSettingsPageViewModel = App.GetService<TaskSettingsPageViewModel>();
        if (taskSettingsPageViewModel == null)
        {
            throw new ArgumentNullException(nameof(taskSettingsPageViewModel), "内部视图模型对象为空");
        }

        CancellationToken cancellationToken = CancellationContext.Instance.Cts.Token;
        
        var autoWoodConfig = TaskContext.Instance().Config.AutoWoodConfig;
        // 是否开启识别木材数量
        autoWoodConfig.WoodCountOcrEnabled = soloTask.Config == null ? false : ScriptObjectConverter.GetValue<bool>((ScriptObject)soloTask.Config, "WoodCountOcrEnabled", false);
        // 设置自动伐木次数
        taskSettingsPageViewModel.AutoWoodRoundNum = soloTask.Config == null ? 0 : ScriptObjectConverter.GetValue<int>((ScriptObject)soloTask.Config, "AutoWoodRoundNum", 0);
        // 识别木材相关参数
        if (autoWoodConfig.WoodCountOcrEnabled)
        {
            // 木材上限类型
            autoWoodConfig.MaxWoodType = soloTask.Config == null
                ? "总数上限"
                : ScriptObjectConverter.GetValue<string>((ScriptObject)soloTask.Config, "MaxWoodType", "总数上限");

            if (autoWoodConfig.MaxWoodType == "指定木材上限")
            {
                if (string.IsNullOrEmpty(ScriptObjectConverter.GetValue<string>((ScriptObject)soloTask.Config,
                        "SingleWoodLimit", "")))
                {
                    _logger.LogError("缺少 {Text} 配置 {text2}，跳过指定木材上限, 请检查JS脚本配置", "单个木材上限", "SingleWoodLimit");
                    return string.Empty.Split(',').ToList();
                }

                // 单个木材上限的名称
                autoWoodConfig.SingleWoodLimit = soloTask.Config == null
                    ? string.Empty
                    : ScriptObjectConverter.GetValue<string>((ScriptObject)soloTask.Config, "SingleWoodLimit", "");
                // 校验设置的单个木材上限是否在指定木材种类范围内
                if (!autoWoodConfig.ExistWoods.Contains(autoWoodConfig.SingleWoodLimit))
                {
                    _logger.LogError("配置 {Text} 参数 {text2} ： {text3} 不在指定木材种类范围内，请检查JS脚本编写的参数", "单个木材上限",
                        "SingleWoodLimit", autoWoodConfig.SingleWoodLimit);
                    return string.Empty.Split(',').ToList();
                }
            }

            // 木材上限的数量
            taskSettingsPageViewModel.AutoWoodDailyMaxCount = soloTask.Config == null
                ? 2000
                : ScriptObjectConverter.GetValue<int>((ScriptObject)soloTask.Config, "DailyMaxCount", 2000);
            // 使用小道具后的检测时间
            autoWoodConfig.AfterZSleepDelay = soloTask.Config == null
                ? 3000
                : ScriptObjectConverter.GetValue<int>((ScriptObject)soloTask.Config, "AfterZSleepDelay", 3000);
        }

        // 仅更新伐木参数，不启动任务
        if (!isWoodExecute)
        {
            _logger.LogWarning("仅更新伐木参数，不启动任务。");
            _logger.LogInformation("自动伐木次数 AutoWoodRoundNum ：{AutoWoodRoundNum}",
                taskSettingsPageViewModel.AutoWoodRoundNum);
            _logger.LogInformation("是否开启识别木材数量 WoodCountOcrEnabled ：{WoodCountOcrEnabled}",
                autoWoodConfig.WoodCountOcrEnabled);

            if (autoWoodConfig.WoodCountOcrEnabled)
            {
                _logger.LogInformation("木材上限类型 MaxWoodType ：{MaxWoodType}", autoWoodConfig.MaxWoodType);
                if (autoWoodConfig.MaxWoodType == "指定木材上限")
                    _logger.LogInformation("指定木材上限 SingleWoodLimit ：{SingleWoodLimit}", autoWoodConfig.SingleWoodLimit);
                _logger.LogInformation("木材上限数量 DailyMaxCount ：{DailyMaxCount}",
                    taskSettingsPageViewModel.AutoWoodDailyMaxCount);
                _logger.LogInformation("使用小道具后的检测时间 AfterZSleepDelay ：{AfterZSleepDelay}",
                    autoWoodConfig.AfterZSleepDelay);
            }

            return string.Empty.Split(',').ToList();
        }

        await new AutoWoodTask(new WoodTaskParam(taskSettingsPageViewModel.AutoWoodRoundNum,
            taskSettingsPageViewModel.AutoWoodDailyMaxCount)).Start(cancellationToken);
        
        var combinedResults = new List<string>();
        foreach (var kvp in AutoWoodTask.GlobalResultDict)
        {
            combinedResults.Add(kvp.Key); 
            combinedResults.Add(kvp.Value.ToString());  
        }
        
        return combinedResults.ToList();
    }


    /// <summary>
    /// 运行独立任务
    /// </summary>
    /// <param name="soloTask">
    /// 支持的任务名称:
    /// - AutoGeniusInvokation: 启动自动七圣召唤任务
    /// - AutoWood: 启动自动伐木任务
    /// - AutoFight: 启动自动战斗任务
    /// - AutoDomain: 启动自动秘境任务
    /// </param>
    /// <param name="customCt">自定义取消令牌，允许从JS控制任务取消</param>
    /// <exception cref="ArgumentNullException"></exception>
    /// <exception cref="ArgumentException"></exception>
    public async Task<object?> RunTask(SoloTask soloTask, CancellationToken? customCt = null)
    {
        if (soloTask == null)
        {
            throw new ArgumentNullException(nameof(soloTask), "独立任务对象不能为空");
        }

        var taskSettingsPageViewModel = App.GetService<TaskSettingsPageViewModel>();
        if (taskSettingsPageViewModel == null)
        {
            throw new ArgumentNullException(nameof(taskSettingsPageViewModel), "内部视图模型对象为空");
        }


        CancellationToken cancellationToken;

        if (customCt != null)
        {
            // Logger.LogError("使用自定义取消令牌");
            IsCustomCts = true;
            cancellationToken = customCt.Value;
        }
        else
        {
            // 如果没有自定义令牌，就使用全局令牌
            // Logger.LogError("使用全局取消令牌");
            IsCustomCts = false;
            cancellationToken = CancellationContext.Instance.Cts.Token;
        }

        // 根据名称执行任务
        switch (soloTask.Name)
        {
            case "AutoGeniusInvokation":
                string content;
                // 检查是否有自定义策略内容  
                if (soloTask.Config != null)
                {
                    var jsObject = (ScriptObject)soloTask.Config;
                    content = ScriptObjectConverter.GetValue(jsObject, "strategy", "");
                    if (string.IsNullOrEmpty(content))
                    {
                        // 回退到原有逻辑  
                        if (taskSettingsPageViewModel.GetTcgStrategy(out content))
                        {
                            return null;
                        }
                    }
                }
                else
                {
                    // 回退到原有逻辑  
                    if (taskSettingsPageViewModel.GetTcgStrategy(out content))
                    {
                        return null;
                    }
                }

                await new AutoGeniusInvokationTask(new GeniusInvokationTaskParam(content)).Start(cancellationToken);
                return null;

            case "AutoWood":
                await new AutoWoodTask(new WoodTaskParam(taskSettingsPageViewModel.AutoWoodRoundNum,
                    taskSettingsPageViewModel.AutoWoodDailyMaxCount)).Start(cancellationToken);
                return null;

            case "AutoFight":
                await new AutoFightHandler().RunAsyncByScript(cancellationToken, null, _config);
                return null;

            case "AutoDomain":
                if (taskSettingsPageViewModel.GetFightStrategy(out var path))
                {
                    return null;
                }
                AllConfig.AutoDomainEnable = true;
                var a = await new AutoDomainTask(new AutoDomainParam(0, path)).Start(cancellationToken);
                AllConfig.AutoDomainEnable = false;
                return a;

            case "AutoBoss":
                var autoBossConfig = TaskContext.Instance().Config.AutoBossConfig;
                if (taskSettingsPageViewModel.GetFightStrategy(autoBossConfig.StrategyName, out var autoBossPath))
                {
                    return null;
                }

                return await new AutoBossTask(new AutoBossParam(autoBossPath)).Start(cancellationToken);

            case "AutoFishing":
                await new AutoFishingTask(AutoFishingTaskParam.BuildFromSoloTaskConfig(soloTask.Config)).Start(
                    cancellationToken);
                return null;
            case "AutoCook":
                await new AutoCookTask().Start(cancellationToken);
                return null;
            case "AutoEat":
                {
                    string? foodName = soloTask.Config == null ? null : ScriptObjectConverter.GetValue((ScriptObject)soloTask.Config, "foodName", (string?)null);
                    FoodEffectType? foodEffectType = soloTask.Config == null ? null : (FoodEffectType?)ScriptObjectConverter.GetValue((ScriptObject)soloTask.Config, "foodEffectType", (int?)null);

                    if (foodName != null && foodEffectType != null)
                    {
                        throw new NotSupportedException("不能同时指定foodName和foodEffectType");
                    }

                    if (foodName == null)
                    {
                        if (foodEffectType != null)
                        {
                            PathingPartyConfig? pathingPartyConfig = _config as PathingPartyConfig;
                            if (pathingPartyConfig == null)
                            {
                                throw new NotSupportedException("foodEffectType参数需要调度器配置，请在调度器下使用");
                            }
                            else
                            {
                                switch (foodEffectType)
                                {
                                    case FoodEffectType.ATKBoostingDish:
                                        foodName = pathingPartyConfig.AutoEatConfig.DefaultAtkBoostingDishName;
                                        if (foodName == null)
                                        {
                                            _logger.LogInformation("缺少{Text}配置，跳过吃Buff", "默认的攻击类料理");
                                            return null;
                                        }
                                        break;
                                    case FoodEffectType.AdventurersDish:
                                        foodName = pathingPartyConfig.AutoEatConfig.DefaultAdventurersDishName;
                                        if (foodName == null)
                                        {
                                            _logger.LogInformation("缺少{Text}配置，跳过吃Buff", "默认的冒险类料理");
                                            return null;
                                        }
                                        break;
                                    case FoodEffectType.DEFBoostingDish:
                                        foodName = pathingPartyConfig.AutoEatConfig.DefaultDefBoostingDishName;
                                        if (foodName == null)
                                        {
                                            _logger.LogInformation("缺少{Text}配置，跳过吃Buff", "默认的防御类料理");
                                            return null;
                                        }
                                        break;
                                    default:
                                        throw new NotSupportedException("JS脚本入参错误：错误的foodEffectType");
                                }
                            }
                        }
                    }

                    var autoEatConfig = TaskContext.Instance().Config.AutoEatConfig;
                    return await new AutoEatTask(new AutoEatParam()
                    {
                        CheckInterval = autoEatConfig.CheckInterval,
                        EatInterval = autoEatConfig.EatInterval,
                        ShowNotification = autoEatConfig.ShowNotification,
                        FoodName = foodName
                    }).Start(cancellationToken);
                }
            case "CountInventoryItem":
                {
                    if (soloTask.Config == null)
                    {
                        throw new NullReferenceException($"{nameof(soloTask.Config)}为空");
                    }
                    GridScreenName gridScreenName = ScriptObjectConverter.GetValue((ScriptObject)soloTask.Config, "gridScreenName", (GridScreenName?)null) ?? throw new Exception("gridScreenName为空或错误");
                    string? itemName = ScriptObjectConverter.GetValue((ScriptObject)soloTask.Config, "itemName", (string?)null);
                    IEnumerable<string>? itemNames = ScriptObjectConverter.GetValue<string>((ScriptObject)soloTask.Config, "itemNames");
                    CountInventoryItemParam param = new()
                    {
                        GridScreenName = gridScreenName,
                        ItemName = itemName,
                        ItemNames = itemNames?.ToList() ?? []
                    };

                    var result = await new CountInventoryItem(param).Start(cancellationToken);
                    if (param.ItemName != null)
                    {
                        return result;
                    }
                    else
                    {
                        dynamic expando = new ExpandoObject();
                        var expandoDict = (IDictionary<string, object>)expando;
                        foreach (var kvp in (Dictionary<string, int>)result)
                        {
                            expandoDict[kvp.Key] = kvp.Value;
                        }
                        return expandoDict;
                    }
                }
            default:
                throw new ArgumentException($"未知的任务名称: {soloTask.Name}", nameof(soloTask.Name));
        }
    }

    public CancellationTokenSource GetLinkedCancellationTokenSource()
    {
        // 创建一个新的链接令牌源，链接到全局令牌
        return CancellationTokenSource.CreateLinkedTokenSource(CancellationContext.Instance.Cts.Token);
    }


    public CancellationToken GetLinkedCancellationToken()
    {
        return GetLinkedCancellationTokenSource().Token;
    }
    
    /// <summary>  
    /// 运行自动秘境任务
    /// </summary>  
    /// <param name="param">秘境任务参数</param>  
    /// <param name="customCt">自定义取消令牌</param>  
    /// <returns></returns>  
    public async Task<Dictionary<string, int>> RunAutoDomainTask(AutoDomainParam param, CancellationToken? customCt = null)
    {  
        if (param == null)  
        {  
            throw new ArgumentNullException(nameof(param), "秘境任务参数不能为空");  
        }  
  
        CancellationToken cancellationToken = customCt ?? CancellationContext.Instance.Cts.Token;  
        return await new AutoDomainTask(param).Start(cancellationToken);
    }  

    /// <summary>
    /// 运行自动首领讨伐任务
    /// </summary>
    /// <param name="param">自动首领讨伐任务参数</param>
    /// <param name="customCt">自定义取消令牌</param>
    /// <returns></returns>
    public async Task<Dictionary<string, int>> RunAutoBossTask(AutoBossParam param, CancellationToken? customCt = null)
    {
        if (param == null)
        {
            throw new ArgumentNullException(nameof(param), "自动首领讨伐任务参数不能为空");
        }

        CancellationToken cancellationToken = customCt ?? CancellationContext.Instance.Cts.Token;
        return await new AutoBossTask(param).Start(cancellationToken);
    }

    /// <summary>  
    /// 运行自动战斗任务
    /// </summary>  
    /// <param name="param">战斗任务参数</param>  
    /// <param name="customCt">自定义取消令牌</param>  
    /// <returns></returns>  
    public async Task RunAutoFightTask(AutoFightParam param, CancellationToken? customCt = null)  
    {  
        if (param == null)  
        {  
            throw new ArgumentNullException(nameof(param), "战斗任务参数不能为空");  
        }  
  
        CancellationToken cancellationToken = customCt ?? CancellationContext.Instance.Cts.Token;
        // official-autofight-parallel-engine spec §4.3(E2)：JS 脚本战斗按全局开关路由（非联机）。
        // param 由 JS 层预构建为茶包类型，走公版时用适配器映射公版认识的字段。
        if (OfficialAutoFightRouter.UseOfficial(TaskContext.Instance().Config.AutoFightConfig, false))
        {
            var officialParam = OfficialParamAdapter.FromTeapot(param, TaskContext.Instance().Config.AutoFightOfficialConfig);
            await new OfficialFightTask(officialParam).Start(cancellationToken);
            return;
        }
        await new AutoFightTask(param).Start(cancellationToken);  
    }
    
    /// <summary>
    /// 运行简易战斗策略脚本。
    /// 使用策略语言直接控制角色执行动作（如 e、q、attack 等），适合快速操作。
    /// </summary>
    /// <param name="script">策略字符串，支持逗号/换行/分号分隔指令，可选角色名前缀</param>
    /// <param name="avatarName">指定操作的角色名（可选，不指定则操作当前角色）</param>
    /// <param name="customCt">自定义取消令牌</param>
    public async Task RunCombatScript(string script, string? avatarName = null, CancellationToken? customCt = null)
    {
        if (string.IsNullOrWhiteSpace(script))
        {
            throw new ArgumentException("策略字符串不能为空", nameof(script));
        }

        CancellationToken cancellationToken = customCt ?? CancellationContext.Instance.Cts.Token;

        // 1. 解析策略字符串（ParseContext 已处理全角符号、注释、分号/逗号分隔）
        var combatScript = CombatScriptParser.ParseContext(script, validate: false, defaultAvatarName: avatarName);
        if (combatScript.CombatCommands.Count == 0) return;

        _logger.LogInformation("执行 {Text}", "简易策略脚本");

        await CombatScriptExecutor.ExecuteAsync(combatScript, cancellationToken, _logger);
    }
    
    /// <summary>  
    /// 运行自动地脉花任务
    /// </summary>  
    /// <param name="param">自动地脉花任务参数</param>  
    /// <param name="customCt">自定义取消令牌</param>  
    /// <returns></returns>  
    public async Task RunAutoLeyLineOutcropTask(AutoLeyLineOutcropParam param, CancellationToken? customCt = null)  
    {  
        if (param == null)  
        {  
            throw new ArgumentNullException(nameof(param), "自动地脉花任务参数不能为空");  
        }  
  
        CancellationToken cancellationToken = customCt ?? CancellationContext.Instance.Cts.Token;  
        await new AutoLeyLineOutcropTask(param).Start(cancellationToken);  
    }


    /// <summary>  
    /// 运行自动幽境危战任务
    /// </summary>  
    /// <param name="param">自动幽境危战任务参数</param>  
    /// <param name="customCt">自定义取消令牌</param>  
    /// <returns></returns>  
    public async Task RunAutoStygianOnslaughtTask(AutoStygianOnslaughtParam param, CancellationToken? customCt = null)
    {
        if (param == null)
        {
            throw new ArgumentNullException(nameof(param), "自动幽境危战任务参数不能为空");
        }

        CancellationToken cancellationToken = customCt ?? CancellationContext.Instance.Cts.Token;
        await new AutoStygianOnslaughtTask(param).Start(cancellationToken);
    }
    
    /// <summary>
    /// 运行背包物品计数任务。
    /// </summary>
    /// <param name="param">背包物品计数参数。</param>
    /// <param name="customCt">自定义取消令牌。</param>
    /// <returns>单物品返回数量；多物品返回名称到数量的脚本对象。</returns>
    public async Task<object?> RunCountInventoryItemTask(CountInventoryItemParam param, CancellationToken? customCt = null)
    {
        if (param == null)
        {
            throw new ArgumentNullException(nameof(param), "背包物品计数参数不能为空");
        }

        CancellationToken cancellationToken = customCt ?? CancellationContext.Instance.Cts.Token;
        object result = await new CountInventoryItem(param).Start(cancellationToken);

        if (param.ItemName != null)
        {
            return result;
        }
        else
        {
            dynamic expando = new ExpandoObject();
            var expandoDict = (IDictionary<string, object>)expando;
            foreach (var kvp in (Dictionary<string, int>)result)
            {
                expandoDict[kvp.Key] = kvp.Value;
            }

            return expandoDict;
        }
    }
}
