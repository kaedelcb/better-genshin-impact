using System;
using System.Threading.Tasks;
using BetterGenshinImpact.ViewModel.Pages.OneDragon;
using CommunityToolkit.Mvvm.ComponentModel;
using System.Windows.Media;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.AutoDomain;
using BetterGenshinImpact.GameTask.Common;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.ViewModel.Pages;
using Microsoft.Extensions.Logging;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.Service.Interface;
using System.Collections.Generic;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Script.Project;
using System.Windows;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.ViewModel.Pages;
using Wpf.Ui;
using Wpf.Ui.Violeta.Controls;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Core.Script.Group;
using System.IO;
using BetterGenshinImpact.View.Windows;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;

using BetterGenshinImpact.View;
using BetterGenshinImpact.View.Drawable;
using Microsoft.Extensions.Logging;
using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Helpers;
using Wpf.Ui.Violeta.Controls;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using System.Collections.ObjectModel;
using BetterGenshinImpact.Service;
using CommunityToolkit.Mvvm.ComponentModel;
using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Text.Json;
using BetterGenshinImpact.View.Windows.Editable;
using WinRT;

namespace BetterGenshinImpact.Model;

public partial class OneDragonTaskItem : ObservableObject
{
    [ObservableProperty]
    private string _name;

    [ObservableProperty]
    private Brush _statusColor = Brushes.Gray;

    [ObservableProperty]
    private bool _isEnabled = true;

    [ObservableProperty]
    private OneDragonBaseViewModel? _viewModel;

    public Func<Task>? Action { get; private set; }
    public Func<Task>? Action2 { get; private set; }
    
    public OneDragonTaskItem(string name)
    {
        Name = name;
    }
    private readonly ScriptControlViewModel _scriptControlViewModel;
    
    public void InitAction(OneDragonFlowConfig config)
    {
        if (config.TaskEnabledList.TryGetValue(Name, out _))
        {
            config.TaskEnabledList[Name] = IsEnabled;
        }
        else
        {
            config.TaskEnabledList.Add(Name, IsEnabled);
        }

        switch (Name)
        {
            case "领取邮件":
                Action = async () => { await new ClaimMailRewardsTask().Start(CancellationContext.Instance.Cts.Token); };
                break;
            case "合成树脂":
                Action = async () =>
                {
                    try
                    {
                        await new GoToCraftingBenchTask().Start(config.CraftingBenchCountry, CancellationContext.Instance.Cts.Token);
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

                    var (partyName, domainName) = config.GetDomainConfig();

                    //LCB=========未配置秘境名称============
                    if (string.IsNullOrEmpty(domainName) || partyName == "地脉花")
                    {
                        TaskControl.Logger.LogInformation("自动秘境任务：未配置秘境名称或设置了地脉花，跳过执行");
                        TaskContext.Instance().Config.LCBauto.Enabled = false; //触发
                        return;
                    }
                    else
                    {
                        TaskControl.Logger.LogInformation("自动秘境任务：执行");
                        TaskContext.Instance().Config.LCBauto.Enabled = true; //触发
                    }
                    //LCB===========未配置秘境名称==========

                    var autoDomainParam = new AutoDomainParam(0, path)
                    {
                        PartyName = partyName,
                        DomainName = domainName
                    };
                    await new AutoDomainTask(autoDomainParam).Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            case "领取每日奖励":
                Action = async () =>
                {
                    await new GoToAdventurersGuildTask().Start(config.AdventurersGuildCountry, CancellationContext.Instance.Cts.Token, config.DailyRewardPartyName);
                    await new ClaimBattlePassRewardsTask().Start(CancellationContext.Instance.Cts.Token);
                };
                break;
            default:
                Action = () => Task.CompletedTask;
                break;
        }
    }

    public void InitActionPEI(OneDragonFlowConfig config)
    {
        if (config.TaskEnabledList.TryGetValue(Name, out _))
        {
            config.TaskEnabledList[Name] = IsEnabled;
        }
        else
        {
            config.TaskEnabledList.Add(Name, IsEnabled);
        }   
        
        switch (Name)
        {
            case "默认配置组":
                Action2 = async () => 
                {
                    
                    TaskRunner.LockManager.ReleaseLock();
                    await Task.Delay(2000);
                   var snackbarService = new SnackbarService();
                    var scriptService = new ScriptService();
                    
                    // 确保 ScriptControlViewModel 已正确初始化
                    var viewModel = new ScriptControlViewModel(snackbarService, scriptService);
                    string basePath = AppDomain.CurrentDomain.BaseDirectory;
                    string relativePath = @"User\\ScriptGroup\\杂项和狗粮组========================.json";
                    string filePath = Path.Combine(basePath, relativePath);
                    viewModel.SelectedScriptGroup = new ScriptGroup { Name = "杂项和狗粮组========================" };
                    viewModel.SelectedScriptGroup = ScriptGroup.FromJson(File.ReadAllText(filePath));
                    
                    if (TaskContext.Instance().Config.LCBauto.Enabled == true )
                    { 
                        viewModel.SelectedScriptGroup.Projects.RemoveAt(1); 
                        //var addProject = new ScriptGroupProject(new ScriptProject("开始标志")); // JS脚本项目
                        //viewModel.SelectedScriptGroup.Projects.Add(addProject); //增加
                    }
                    await viewModel.StartScriptGroupCommand.ExecuteAsync(viewModel.SelectedScriptGroup);
                    await Task.Delay(1000);
                    TaskRunner.LockManager.ReleaseLock();
                    
                    //单个脚本项目
                    // var projectList = new List<ScriptGroupProject>
                    // {
                    //     new ScriptGroupProject(new ScriptProject("Auto全自动“枫丹”地脉花")), // JS脚本项目
                    //     ScriptGroupProject.BuildKeyMouseProject("AutoClick"), // 键鼠脚本项目
                    // };
                    // var scriptService = App.GetService<IScriptService>(); // 假设我们使用依赖注入获取服务实例
                    // await scriptService.RunMulti(projectList, "默认配置组");
                };
                break;
            
            case "临时组":
                Action2 = async () => 
                {
                    TaskRunner.LockManager.ReleaseLock();
                    await Task.Delay(2000);
                    var snackbarService = new SnackbarService();
                    var scriptService = new ScriptService();
                    
                    // 确保 ScriptControlViewModel 已正确初始化
                    var viewModel = new ScriptControlViewModel(snackbarService, scriptService);
                    string basePath = AppDomain.CurrentDomain.BaseDirectory;
                    string relativePath = @"User\ScriptGroup\临时组.json";     
                    string filePath = Path.Combine(basePath, relativePath);
                    viewModel.SelectedScriptGroup = new ScriptGroup { Name = "临时组" };
                    //var json = File.ReadAllText(file);
                    viewModel.SelectedScriptGroup = ScriptGroup.FromJson(File.ReadAllText(filePath));
                    await viewModel.StartScriptGroupCommand.ExecuteAsync(viewModel.SelectedScriptGroup);
                    await Task.Delay(1000);
                    TaskRunner.LockManager.ReleaseLock();
                };
                break;
            
            case "测试":
                Action2 = async () => 
                {
                    TaskRunner.LockManager.ReleaseLock();
                    await Task.Delay(2000);
                    var snackbarService = new SnackbarService();
                    var scriptService = new ScriptService();
                    
                    // 确保 ScriptControlViewModel 已正确初始化
                    var viewModel = new ScriptControlViewModel(snackbarService, scriptService);
                    string basePath = AppDomain.CurrentDomain.BaseDirectory;
                    string relativePath = @"User\ScriptGroup\测试.json";     
                    string filePath = Path.Combine(basePath, relativePath);
                    viewModel.SelectedScriptGroup = new ScriptGroup { Name = "测试" };
                    //var json = File.ReadAllText(file);
                    viewModel.SelectedScriptGroup = ScriptGroup.FromJson(File.ReadAllText(filePath));
                    await viewModel.StartScriptGroupCommand.ExecuteAsync(viewModel.SelectedScriptGroup);
                    await Task.Delay(1000);
                    TaskRunner.LockManager.ReleaseLock();
                };
                break;
            
            case "连续执行配置组":
                Action2 = async () =>
                {
                    TaskRunner.LockManager.ReleaseLock();
                    await Task.Delay(1000);
                    var snackbarService = new SnackbarService();
                    var scriptService = new ScriptService();
                    var viewModel = new ScriptControlViewModel(snackbarService, scriptService);
                    string basePath = AppDomain.CurrentDomain.BaseDirectory;
                    string relativePath1 = @"User\ScriptGroup\打怪材料组1.json";
                    string relativePath2 = @"User\ScriptGroup\资源采集组1.json";
                    
                    string filePath1 = Path.Combine(basePath, relativePath1);
                    string filePath2 = Path.Combine(basePath, relativePath2);
                    
                    var selectedGroups = new List<ScriptGroup>();
                    selectedGroups.Add(ScriptGroup.FromJson(File.ReadAllText(filePath1)));
                    selectedGroups.Add(ScriptGroup.FromJson(File.ReadAllText(filePath2)));
                    
                    await viewModel.StartGroups( selectedGroups);  
                    await Task.Delay(1000);
                    TaskRunner.LockManager.ReleaseLock();
                };
                break;  
            
            default:
                Action2 = () => Task.CompletedTask;
                break;
        }
        
        
    }
    
    
    
}