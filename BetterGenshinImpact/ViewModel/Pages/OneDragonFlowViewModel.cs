using System;
using System.Collections.Generic;
using BetterGenshinImpact.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Job;

using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using BetterGenshinImpact.View.Windows;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using Wpf.Ui.Controls;
using Wpf.Ui.Violeta.Controls;

namespace BetterGenshinImpact.ViewModel.Pages;

public partial class OneDragonFlowViewModel : ViewModel
{
    private readonly ILogger<OneDragonFlowViewModel> _logger = App.GetLogger<OneDragonFlowViewModel>();

    public static readonly string OneDragonFlowConfigFolder = Global.Absolute(@"User\OneDragon");

    private readonly ScriptService _scriptService;
    
    [ObservableProperty]
    private ObservableCollection<OneDragonTaskItem> _taskList =
    [
        new("领取邮件"),
        new("合成树脂"),
        // new ("每日委托"),
        new("自动秘境"),
        // new ("自动锻造"),
        // new ("自动刷地脉花"),
        new("领取每日奖励"), 
        new ("默认配置组"),
        // new ("领取尘歌壶奖励"),
        // new ("自动七圣召唤"),
    ];
    
    public string InputScriptGroupName { get; set; } = string.Empty;

    [ObservableProperty]
    private ObservableCollection<OneDragonTaskItem> _playTaskList = new ObservableCollection<OneDragonTaskItem>();
 
    public void SaveNewTask()
    {
        if (!string.IsNullOrWhiteSpace(InputScriptGroupName))
        {
            var taskItem = new OneDragonTaskItem(InputScriptGroupName)
            {
                IsEnabled = true
            };
            if (!PlayTaskList.Any(task => task.Name == taskItem.Name))
            {    
                TaskList.Add(taskItem);
                SelectedTask = taskItem;
                SaveConfig();
            }else
            {
                Toast.Information("任务已存在，请勿重复添加");
            }
        }
    }
    
    [ObservableProperty]
    private OneDragonTaskItem? _selectedTask;

    [ObservableProperty]
    private ObservableCollection<OneDragonFlowConfig> _configList = [];
    /// <summary>
    /// 当前生效配置
    /// </summary>
    [ObservableProperty]
    private OneDragonFlowConfig? _selectedConfig;

    [ObservableProperty]
    private List<string> _craftingBenchCountry = ["枫丹", "稻妻", "璃月", "蒙德"];

    [ObservableProperty]
    private List<string> _adventurersGuildCountry = ["枫丹", "稻妻", "璃月", "蒙德"];

    [ObservableProperty]
    private List<string> _domainNameList = ["", ..MapLazyAssets.Instance.DomainNameList];

    [ObservableProperty]
    private List<string> _completionActionList = ["无", "关闭游戏", "关闭游戏和软件", "关机"];

    public AllConfig Config { get; set; } = TaskContext.Instance().Config;
    
    public OneDragonFlowViewModel(ScriptService scriptService)
    {
        _scriptService = scriptService;
    }
    
    
    public OneDragonFlowViewModel()
    {
        ConfigList.CollectionChanged += (sender, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (OneDragonFlowConfig newItem in e.NewItems)
                {
                    newItem.PropertyChanged += ConfigPropertyChanged;
                }
            }

            if (e.OldItems != null)
            {
                foreach (OneDragonFlowConfig oldItem in e.OldItems)
                {
                    oldItem.PropertyChanged -= ConfigPropertyChanged;
                }
            }
        };
        SaveConfig();
        InitConfigList();
    }
    public override void OnNavigatedTo()
    {
        InitConfigList();
    }

    private void InitConfigList()
    {
        Directory.CreateDirectory(OneDragonFlowConfigFolder);
        // 读取文件夹内所有json配置，按创建时间正序
        var configFiles = Directory.GetFiles(OneDragonFlowConfigFolder, "*.json");
        var configs = new List<OneDragonFlowConfig>();

        OneDragonFlowConfig? selected = null;
        foreach (var configFile in configFiles)
        {
            var json = File.ReadAllText(configFile);
            var config = JsonConvert.DeserializeObject<OneDragonFlowConfig>(json);
            if (config != null)
            {
                configs.Add(config);
                if (config.Name == TaskContext.Instance().Config.SelectedOneDragonFlowConfigName)
                {
                    selected = config;
                }
            }
        }

        if (selected == null)
        {
            if (configs.Count > 0)
            {
                selected = configs[0];
            }
            else
            {
                selected = new OneDragonFlowConfig
                {
                    Name = "默认配置"
                };
                configs.Add(selected);
            }
        }

        ConfigList.Clear();
        foreach (var config in configs)
        {
            ConfigList.Add(config);
        }

        SelectedConfig = selected;
        LoadDisplayTaskListFromConfig(); // 加载 DisplayTaskList 从配置文件
        SetSomeSelectedConfig(SelectedConfig);
    }

    // 新增方法：从配置文件加载 DisplayTaskList
    private void LoadDisplayTaskListFromConfig()
    {
        if (SelectedConfig == null || SelectedConfig.TaskEnabledList == null)
        {
            return;
        }
        TaskList.Clear();
        foreach (var kvp in SelectedConfig.TaskEnabledList)
        {
            var taskItem = new OneDragonTaskItem(kvp.Key)
            {
                IsEnabled = kvp.Value
            };
            TaskList.Add(taskItem);
        }
    }
    
    private void DeleteConfigDisplayTaskListFromConfig()
    {
        if (SelectedConfig == null || SelectedConfig.TaskEnabledList == null)
        {
            return;
        }

        TaskList.Clear();
        foreach (var kvp in SelectedConfig.TaskEnabledList)
        {
            var taskItem = new OneDragonTaskItem(kvp.Key)
            {
                IsEnabled = kvp.Value
            };
            if (taskItem.Name != InputScriptGroupName )
            {
                TaskList.Add(taskItem);  
            }
        }
    }
    
    [RelayCommand]
    private void OnConfigDropDownChanged()
    {
        SetSomeSelectedConfig(SelectedConfig);
    }

    // OneDragonFlowViewModel.cs LCB 
    public void SaveConfig()
    {
        if (SelectedConfig == null)
        {
            return;
        }

        // 清空现有的 TaskEnabledList
        SelectedConfig.TaskEnabledList.Clear();
        // 遍历 TaskList，将每个任务项的 IsEnabled 值保存到 TaskEnabledList 中
        foreach (var task in TaskList)
        {
            SelectedConfig.TaskEnabledList[task.Name] = task.IsEnabled;
        }

        // 调用 WriteConfig 方法将配置写入文件
        WriteConfig(SelectedConfig);
    }
    
    // OneDragonFlowViewModel.cs
    [RelayCommand]
    private void SaveConfigCommandExecute()
    {
        SaveNewTask();
        SaveConfig();
        Toast.Information("已经保存");
    }
    
    private void SetSomeSelectedConfig(OneDragonFlowConfig? selected)
    {
        if (SelectedConfig != null)
        {
            TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = SelectedConfig.Name;
            foreach (var task in TaskList)
            {
                if (SelectedConfig.TaskEnabledList.TryGetValue(task.Name, out var value))
                {
                    task.IsEnabled = value;
                }
            }
            LoadDisplayTaskListFromConfig();
        }
    }

    private void ConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        saveConfigCommandExecuteCommand.Execute(null);
        WriteConfig(SelectedConfig);
    }
    private void WriteConfig(OneDragonFlowConfig? config)
    {
        if (config == null)
        {
            return;
        }

        try
        {
            Directory.CreateDirectory(OneDragonFlowConfigFolder);
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            var filePath = Path.Combine(OneDragonFlowConfigFolder, $"{config.Name}.json");
            File.WriteAllText(filePath, json);
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "保存配置时失败");
            Toast.Error("保存配置时失败");
        }
    }

    public void OnNavigatedFrom()
    {
    }

    [RelayCommand]
    private async Task OnOneKeyExecute()
    {
        //===========一条龙开始关闭自动钓鱼和值制标记=======LCB========
        TaskContext.Instance().Config.AutoFishingConfig.Enabled = false; //钓鱼触发
        TaskContext.Instance().Config.LCBauto.Enabled = false; //触发
        //===========一条龙开始关闭自动钓鱼和值制标记=======LCB========
        // 根据配置初始化任务
        foreach (var task in TaskList)
        {
            task.InitAction(SelectedConfig);
            task.InitActionPEI(SelectedConfig);
        }

        // 没启动的时候先启动
        await ScriptService.StartGameTask();

        await new TaskRunner()
            .RunThreadAsync(async () =>
            {
                Notify.Event(NotificationEvent.DragonStart).Success("一条龙启动");
                foreach (var task in TaskList)
                {
                    if (task is { IsEnabled: true, Action: not null })
                    {
                        await task.Action();
                        await Task.Delay(1000);
                    }
                }

                await new CheckRewardsTask().Start(CancellationContext.Instance.Cts.Token);
                Notify.Event(NotificationEvent.DragonEnd).Success("一条龙结束");
            });
   
        try{ 
            Notify.Event(NotificationEvent.DragonStart).Success("配置组任务启动");
            foreach (var task in TaskList)
            {
                if (task is { IsEnabled: true, Action: not null })
                {
                    await task.Action2();
                    await Task.Delay(1000);
                }
            }
            await new CheckRewardsTask().Start(CancellationContext.Instance.Cts.Token);
            Notify.Event(NotificationEvent.DragonEnd).Success("配置组任务结束");
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "配置组任务失败");
        }
        
        // 执行完成后操作
        if (SelectedConfig != null && !string.IsNullOrEmpty(SelectedConfig.CompletionAction))
        {
            switch (SelectedConfig.CompletionAction)
            {
                case "关闭游戏":
                    SystemControl.CloseGame();
                    break;
                case "关闭游戏和软件":
                    SystemControl.CloseGame();
                    Application.Current.Dispatcher.Invoke(() => { Application.Current.Shutdown(); });
                    break;
                case "关机":
                    SystemControl.CloseGame();
                    SystemControl.Shutdown();
                    break;
            }
        }
        
        TaskContext.Instance().Config.LCBauto.Enabled = false; //初始关闭
        TaskContext.Instance().Config.AutoFishingConfig.Enabled = true; //钓鱼触发
    }

   [RelayCommand]
private void OnAddTask()
{
    DeleteConfigDisplayTaskListFromConfig(); 
    SaveConfig();
    Toast.Information("已经删除");
}

[RelayCommand]
private void OffAddTask()
{ 
}

    [RelayCommand]
    private void OnAddConfig()
    {
        // 添加配置
        var str = PromptDialog.Prompt("请输入一条龙配置名称", "新增一条龙配置");
        if (!string.IsNullOrEmpty(str))
        {
            // 检查是否已存在
            if (ConfigList.Any(x => x.Name == str))
            {
                Toast.Warning($"一条龙配置 {str} 已经存在，请勿重复添加");
            }
            else
            {
                var nc = new OneDragonFlowConfig { Name = str };
                ConfigList.Insert(0, nc);
                SelectedConfig = nc;
            }
        }
    }
}