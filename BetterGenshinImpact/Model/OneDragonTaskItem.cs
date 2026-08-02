using System;
using System.Threading.Tasks;
using BetterGenshinImpact.ViewModel.Pages.OneDragon;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.AutoBoss;
using BetterGenshinImpact.GameTask.AutoLeyLineOutcrop;
using BetterGenshinImpact.GameTask.AutoStygianOnslaught;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;

namespace BetterGenshinImpact.Model;

public partial class OneDragonTaskItem : ObservableObject
{
    [ObservableProperty] private int _index;
    
    [ObservableProperty] private string _name;

    [ObservableProperty] private Brush _statusColor = Brushes.Gray;

    [ObservableProperty] private bool _isEnabled = true;
    
    [ObservableProperty] private bool _isNextTask = false;
    
    [ObservableProperty] private OneDragonBaseViewModel? _viewModel;

    public Func<Task>? Action { get; private set; }

    public OneDragonTaskItem(string name)
    {
        Name = name;
    }
    
    public OneDragonTaskItem(int index,bool isEnabled,string name,bool isNextTask = false)
    {
        Index = index;
        IsEnabled = isEnabled;
        Name = name;
        IsNextTask = isNextTask;
    }
    
    // public OneDragonTaskItem(Type viewModelType, Func<Task> action)
    // {
    //     ViewModel = App.GetService(viewModelType) as OneDragonBaseViewModel;
    //     if (ViewModel == null)
    //     {
    //         throw new ArgumentException("Invalid view model type", nameof(viewModelType));
    //     }
    //     Name = ViewModel.Title;
    //     Action = action;
    // }

    public void InitAction(OneDragonFlowConfig config)
    {
        if (config.TaskEnabledList.TryGetValue(Index, out var taskStatus))
        {
            config.TaskEnabledList[Index] =  (IsEnabled, taskStatus.Item2);
        }
        else
        {
            config.TaskEnabledList.Add(Index, (IsEnabled, taskStatus.Item2));
        }

        switch (Name)
        {
            case "领取邮件":
                Action = async () =>
                {
                    await new ClaimMailRewardsTask().Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "合成树脂":
                Action = async () =>
                {
                    try
                    {
                        await new GoToCraftingBenchTask().GoCraftResin(config.CraftingBenchCountry,
                            CancellationContext.Instance.Cts.Token);
                    }
                    catch (Exception e)
                    {
                        TaskControl.Logger.LogError("合成树脂执行异常：" + e.Message);
                    }
                };
                break;
            case "自动秘境":
                Action = async () =>
                {
                    // 先取出当天配置的秘境名。若命中事件任务标记，则改为委派对应独立任务，
                    // 所有参数沿用独立任务自身配置（不走秘境流程）。
                    var (partyName, domainName, sundaySelectedValue, resinCount, specifyResinUse) = config.GetDomainConfig();

                    if (domainName == OneDragonFlowConfig.BossEventMarker)
                    {
                        TaskControl.Logger.LogInformation("自动秘境任务：当天配置为{Text}，改为执行", "首领讨伐");
                        await RunAutoBossFromIndependentConfigAsync();
                        return;
                    }

                    if (domainName == OneDragonFlowConfig.StygianEventMarker)
                    {
                        TaskControl.Logger.LogInformation("自动秘境任务：当天配置为{Text}，改为执行", "幽境危战");
                        await RunAutoStygianAsync();
                        return;
                    }

                    if (string.IsNullOrEmpty(TaskContext.Instance().Config.AutoFightConfig.StrategyName))
                    {
                        TaskContext.Instance().Config.AutoFightConfig.StrategyName = "根据队伍自动选择";
                    }

                    var taskSettingsPageViewModel = App.GetService<TaskSettingsPageViewModel>();
                    if (taskSettingsPageViewModel!.GetFightStrategy(out var path))
                    {
                        TaskControl.Logger.LogError("自动秘境战斗策略{Msg}，跳过", "未配置");
                        return;
                    }

                    if (string.IsNullOrEmpty(domainName))
                    {
                        TaskControl.Logger.LogError("一条龙配置内{Msg}需要刷的秘境，跳过", "未选择");
                        return;
                    }
                    else
                    {
                        TaskControl.Logger.LogInformation("自动秘境任务：执行");
                    }

                    var autoDomainParam = new AutoDomainParam(0, path)
                    {
                        PartyName = partyName,
                        DomainName = domainName,
                        SundaySelectedValue = sundaySelectedValue,
                        ResinCount = resinCount,
                        SpecifyResinUse = specifyResinUse
                    };
                    await new AutoDomainTask(autoDomainParam).Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "自动首领讨伐":
                Action = async () => await RunAutoBossAsync(config);
                break;
            case "自动幽境危战":
                Action = async () => await RunAutoStygianAsync();
                break;
            case "领取每日奖励":
                Action = async () =>
                {
                    await new GoToAdventurersGuildTask().Start(config.AdventurersGuildCountry,
                        CancellationContext.Instance.Cts.Token, config.DailyRewardPartyName);
                    await new ClaimBattlePassRewardsTask().Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "领取尘歌壶奖励":
                Action = async () =>
                {
                    await new GoToSereniteaPotTask().Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "锄地一条龙":
                Action = async () =>
                {
                    await new GameTask.AutoHoeing.AutoHoeingTask().Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "自动地脉花":
                Action = async () =>
                {
                    if (!config.ShouldRunLeyLineToday())
                    {
                        TaskControl.Logger.LogInformation("自动地脉花未在运行日期内，跳过");
                        return;
                    }

                    var taskConfig = TaskContext.Instance().Config.AutoLeyLineOutcropConfig;
                    var originalType = taskConfig.LeyLineOutcropType;
                    var originalCountry = taskConfig.Country;
                    var originalCount = taskConfig.Count;
                    var originalExhaustionMode = taskConfig.IsResinExhaustionMode;
                    var originalOpenModeCountMin = taskConfig.OpenModeCountMin;
                    var (type, country) = config.GetLeyLineConfigForToday(taskConfig);

                    try
                    {
                        taskConfig.LeyLineOutcropType = type;
                        taskConfig.Country = country;
                        taskConfig.IsResinExhaustionMode = config.LeyLineResinExhaustionMode;
                        taskConfig.OpenModeCountMin = config.LeyLineOpenModeCountMin;
                        if (config.LeyLineRunCount > 0)
                        {
                            taskConfig.Count = config.LeyLineRunCount;
                        }

                        AutoLeyLineOutcropParam param = new AutoLeyLineOutcropParam();
                        param.SetAutoLeyLineOutcropConfig(taskConfig);
                        await new AutoLeyLineOutcropTask(param, config.LeyLineOneDragonMode)
                            .Start(CancellationContext.Instance.Cts.Token);
                    }
                    finally
                    {
                        taskConfig.LeyLineOutcropType = originalType;
                        taskConfig.Country = originalCountry;
                        taskConfig.Count = originalCount;
                        taskConfig.IsResinExhaustionMode = originalExhaustionMode;
                        taskConfig.OpenModeCountMin = originalOpenModeCountMin;
                    }
                };
                break;
            default:
                Action = () => Task.CompletedTask;
                break;
        }
    }

    /// <summary>
    /// 执行"自动首领讨伐"独立任务。参数全部来自一条龙配置的 AutoBoss* 字段，
    /// 与任务列表中的"自动首领讨伐"条目行为完全一致。
    /// 供"自动首领讨伐"条目与"自动秘境"命中首领标记时共用。
    /// </summary>
    private static async Task RunAutoBossAsync(OneDragonFlowConfig config)
    {
        if (string.IsNullOrEmpty(config.AutoBossStrategyName))
        {
            config.AutoBossStrategyName = "根据队伍自动选择";
        }

        var taskSettingsPageViewModel = App.GetService<TaskSettingsPageViewModel>();
        if (taskSettingsPageViewModel!.GetFightStrategy(config.AutoBossStrategyName, out var path))
        {
            TaskControl.Logger.LogError("自动首领讨伐战斗策略{Msg}，跳过", "未配置");
            return;
        }

        if (string.IsNullOrWhiteSpace(config.AutoBossName))
        {
            TaskControl.Logger.LogError("一条龙配置内{Msg}需要讨伐的首领，跳过", "未选择");
            return;
        }

        AutoBossParam param = AutoBossParam.CreateWithoutDefaultConfig(path);
        param.BossName = config.AutoBossName;
        param.StrategyName = config.AutoBossStrategyName;
        param.TeamName = config.AutoBossTeamName;
        param.SpecifyRunCount = config.AutoBossSpecifyRunCount;
        param.RunCount = config.AutoBossRunCount;
        param.UseTransientResin = config.AutoBossUseTransientResin;
        param.UseFragileResin = config.AutoBossUseFragileResin;
        param.ReviveRetryCount = config.AutoBossReviveRetryCount;
        param.ReturnToStatueAfterEachRound = config.AutoBossReturnToStatueAfterEachRound;
        param.RewardRecognitionEnabled = config.AutoBossRewardRecognitionEnabled;
        param.Timeout = config.AutoBossTimeout;
        await new AutoBossTask(param).Start(CancellationContext.Instance.Cts.Token);
    }

    /// <summary>
    /// 执行"自动首领讨伐"独立任务，参数全部来自独立任务自身配置 AutoBossConfig
    /// （即 TaskSettingsPage"自动首领讨伐"面板里选的首领 / 策略 / 队伍等）。
    /// 逻辑与独立任务启动按钮 OnSwitchAutoBoss 完全一致。
    /// 供"自动秘境"命中"首领讨伐"标记时使用。
    /// </summary>
    private static async Task RunAutoBossFromIndependentConfigAsync()
    {
        var bossConfig = TaskContext.Instance().Config.AutoBossConfig;

        var taskSettingsPageViewModel = App.GetService<TaskSettingsPageViewModel>();
        if (taskSettingsPageViewModel!.GetFightStrategy(bossConfig.StrategyName, out var path))
        {
            TaskControl.Logger.LogError("自动首领讨伐战斗策略{Msg}，跳过", "未配置");
            return;
        }

        if (string.IsNullOrWhiteSpace(bossConfig.BossName))
        {
            TaskControl.Logger.LogError("独立任务内{Msg}需要讨伐的首领，跳过", "未选择");
            return;
        }

        AutoBossParam param = new AutoBossParam(path);
        param.SetAutoBossConfig(bossConfig);
        await new AutoBossTask(param).Start(CancellationContext.Instance.Cts.Token);
    }

    /// <summary>
    /// 执行"自动幽境危战"独立任务。参数全部来自 AutoStygianOnslaughtConfig，
    /// 与任务列表中的"自动幽境危战"条目行为完全一致。
    /// 供"自动幽境危战"条目与"自动秘境"命中幽境标记时共用。
    /// </summary>
    private static async Task RunAutoStygianAsync()
    {
        if (string.IsNullOrEmpty(TaskContext.Instance().Config.AutoStygianOnslaughtConfig.StrategyName))
        {
            TaskContext.Instance().Config.AutoStygianOnslaughtConfig.StrategyName = "根据队伍自动选择";
        }

        var taskSettingsPageViewModel = App.GetService<TaskSettingsPageViewModel>();
        if (taskSettingsPageViewModel!.GetFightStrategy(TaskContext.Instance().Config.AutoStygianOnslaughtConfig.StrategyName, out var path))
        {
            TaskControl.Logger.LogError("自动幽境危战战斗策略{Msg}，跳过", "未配置");
            return;
        }

        AutoStygianOnslaughtParam param = new AutoStygianOnslaughtParam();
        param.SetAutoStygianOnslaughtConfig(TaskContext.Instance().Config.AutoStygianOnslaughtConfig);
        await new AutoStygianOnslaughtTask(param, path).Start(CancellationContext.Instance.Cts.Token);
    }
}
