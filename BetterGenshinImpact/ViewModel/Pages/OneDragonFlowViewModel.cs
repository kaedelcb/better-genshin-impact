using System;
using System.Collections.Generic;
using BetterGenshinImpact.Model;
using CommunityToolkit.Mvvm.ComponentModel;
using Microsoft.Extensions.Logging;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Data;
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
using System.Windows.Controls;
using ABI.Windows.UI.UIAutomation;
using Wpf.Ui;
using StackPanel = Wpf.Ui.Controls.StackPanel;
using BetterGenshinImpact.Core.Config;
using BetterGenshinImpact.Core.Script.Project;
using BetterGenshinImpact.Service.Interface;
using TextBlock = Wpf.Ui.Controls.TextBlock;
using WinForms = System.Windows.Forms;
using BetterGenshinImpact.Core.Simulator;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static Vanara.PInvoke.User32;
using BetterGenshinImpact.GameTask.AutoSkip.Assets;
using BetterGenshinImpact.GameTask.AutoWood.Assets;
using System;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoWood.Assets;
using BetterGenshinImpact.GameTask.AutoWood.Utils;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common;
using Rect = OpenCvSharp.Rect;
using System.Collections.Generic;
using System.Windows.Data;
using CommunityToolkit.Mvvm.Input;
using Grid = System.Windows.Controls.Grid;
using System.Windows.Media;
using Button = Wpf.Ui.Controls.Button;
using System.Windows.Media.Animation;
using System.Windows.Controls.Primitives;
using System.Windows.Threading;
using System.Windows.Input;
using System.Collections.Specialized;

using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.Linq;
using GongSolutions.Wpf.DragDrop;
using GongSolutions.Wpf.DragDrop;

namespace BetterGenshinImpact.ViewModel.Pages;

public partial class OneDragonFlowViewModel : ViewModel
{
    private readonly ILogger<OneDragonFlowViewModel> _logger = App.GetLogger<OneDragonFlowViewModel>();

    public static readonly string OneDragonFlowConfigFolder = Global.Absolute(@"User\OneDragon");

    private readonly ScriptService _scriptService;

    [ObservableProperty] private ObservableCollection<OneDragonTaskItem> _taskList =
    [
        new("领取邮件"),
        new("合成树脂"),
        // new ("每日委托"),
        new("自动秘境"),
        // new ("自动锻造"),
        // new ("自动刷地脉花"),
        new("领取每日奖励"),
        new ("领取尘歌壶奖励"),
        // new ("自动七圣召唤"),
    ];
    //更新右上角的任务列表
    public ICollectionView FilteredConfigList { get; }
    

    private bool FilterLogic(object item)
    {
        if (item is OneDragonFlowConfig config)
        {
            SelectedConfig = config;
            OnConfigDropDownChanged();
            return config.ScheduleName == Config.SelectedOneDragonFlowPlanName;
        }
        return false;
    }
    
    public void RefreshFilteredConfigList()
    {
        FilteredConfigList.Filter = FilterLogic;
        FilteredConfigList.Refresh();
    }
    
    [ObservableProperty] private OneDragonTaskItem _selectedTask;

    partial void OnSelectedTaskChanged(OneDragonTaskItem value)
    {
        if (value != null)
        {
            InputScriptGroupName = value.Name;
        }
    }

    // 其他属性和方法...
    [ObservableProperty] private string _inputScriptGroupName = string.Empty;

    [ObservableProperty]
    private ObservableCollection<OneDragonTaskItem> _playTaskList = new ObservableCollection<OneDragonTaskItem>();

    [ObservableProperty]
    private ObservableCollection<ScriptGroup> _scriptGroups = new ObservableCollection<ScriptGroup>();

    [ObservableProperty] private ObservableCollection<ScriptGroup> _scriptGroupsdefault =
        new ObservableCollection<ScriptGroup>()
        {
            new() { Name = "领取邮件" },
            new() { Name = "合成树脂" },
            new() { Name = "自动秘境" },
            new() { Name = "领取每日奖励" },
            new() {Name = "领取尘歌壶奖励" },
        };

    private readonly string _scriptGroupPath = Global.Absolute(@"User\ScriptGroup");
    private readonly string _configPath = Global.Absolute(@"User\OneDragon");
    private readonly string _basePath = AppDomain.CurrentDomain.BaseDirectory;
    
    private void ReadScriptGroup()
    {
        try
        {
            if (!Directory.Exists(_scriptGroupPath))
            {
                Directory.CreateDirectory(_scriptGroupPath);
            }

            ScriptGroups.Clear();
            foreach (var group in _scriptGroupsdefault)
            {
                ScriptGroups.Add(group);
            }

            var files = Directory.GetFiles(_scriptGroupPath, "*.json");
            foreach (var file in files)
            {
                try
                {
                    var json = File.ReadAllText(file);
                    var group = ScriptGroup.FromJson(json);

                    var nst = TaskContext.Instance().Config.NextScheduledTask.Find(item => item.Item1 == group.Name);
                    foreach (var item in group.Projects)
                    {
                        item.NextFlag = false;
                        if (nst != default)
                        {
                            if (nst.Item2 == item.Index && nst.Item3 == item.FolderName && nst.Item4 == item.Name)
                            {
                                item.NextFlag = true;
                            }
                        }
                    }

                    ScriptGroups.Add(group);
                }
                catch (Exception e)
                {
                    _logger.LogInformation(e, "读取配置组配置时失败");
                }
            }

            ScriptGroups = new ObservableCollection<ScriptGroup>(ScriptGroups.OrderBy(g => g.Index));
        }
        catch (Exception e)
        {
            _logger.LogInformation(e, "读取配置组配置时失败");
        }
    }

    private async void AddNewTaskGroup()
    {
        ReadScriptGroup();
        var selectedGroupNamePick = await OnStartMultiScriptGroupAsync();
        if (selectedGroupNamePick == null)
        {
            return;
        }
        int pickTaskCount = selectedGroupNamePick.Split(',').Count();
        foreach (var selectedGroupName in selectedGroupNamePick.Split(','))
        {
            var taskItem = new OneDragonTaskItem(selectedGroupName)
            {
                IsEnabled = true
            };
            if (TaskList.All(task => task.Name != taskItem.Name))
            {
                var names = selectedGroupName.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                    .Select(name => name.Trim())
                    .ToList();
                bool containsAnyDefaultGroup =
                    names.Any(name => ScriptGroupsdefault.Any(defaultSg => defaultSg.Name == name));
                if (containsAnyDefaultGroup)
                {
                    int lastDefaultGroupIndex = -1;
                    for (int i = TaskList.Count - 1; i >= 0; i--)
                    {
                        if (ScriptGroupsdefault.Any(defaultSg => defaultSg.Name == TaskList[i].Name))
                        {
                            lastDefaultGroupIndex = i;
                            break;
                        }
                    }
                    if (lastDefaultGroupIndex >= 0)
                    {
                        TaskList.Insert(lastDefaultGroupIndex + 1, taskItem);
                    }
                    else
                    {
                        TaskList.Insert(0, taskItem);
                    }
                    if (pickTaskCount == 1)
                    {
                        Toast.Success("一条龙任务添加成功");
                    }
                }
                else
                {
                    TaskList.Add(taskItem);
                    if (pickTaskCount == 1)
                    {
                        Toast.Success("配置组添加成功");
                    }
                }
            }
            else
            {
                if (pickTaskCount == 1)
                {
                    Toast.Warning("任务或配置组已存在");
                }
            } 
        }
        if (pickTaskCount > 1)
        {
                Toast.Success(pickTaskCount + " 个任务添加成功");  
        }
    }

    public async Task<string?> OnStartMultiScriptGroupAsync()
    {
        var stackPanel = new StackPanel();
        var checkBoxes = new Dictionary<ScriptGroup, CheckBox>();
        CheckBox selectedCheckBox = null; // 用于保存当前选中的 CheckBox
        foreach (var scriptGroup in ScriptGroups)
        {
            if (TaskList.Any(taskName => scriptGroup.Name.Contains(taskName.Name)))
            {
                continue; // 不显示已经存在的配置组
            }
            var checkBox = new CheckBox
            {
                Content = scriptGroup.Name,
                Tag = scriptGroup,
                IsChecked = false // 初始状态下都未选中
            };
            checkBoxes[scriptGroup] = checkBox;
            stackPanel.Children.Add(checkBox);
        }
        var uiMessageBox = new Wpf.Ui.Controls.MessageBox
        {
        Title = "选择增加的配置组（可多选）",
        Content = new ScrollViewer
        {
            Content = stackPanel,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
        },
        CloseButtonText = "关闭",
        PrimaryButtonText = "确认",
        Owner = Application.Current.ShutdownMode == ShutdownMode.OnMainWindowClose ? null : Application.Current.MainWindow,
        WindowStartupLocation = WindowStartupLocation.CenterOwner,
        SizeToContent = SizeToContent.Width , // 确保弹窗根据内容自动调整大小
        MaxHeight = 600,
        };
        var result = await uiMessageBox.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            List<string> selectedItems = new List<string>(); // 用于存储所有选中的项
            foreach (var checkBox in checkBoxes.Values)
            {
                if (checkBox.IsChecked == true)
                {
                    // 确保 Tag 是 ScriptGroup 类型，并返回其 Name 属性
                    var scriptGroup = checkBox.Tag as ScriptGroup;
                    if (scriptGroup != null)
                    { 
                        selectedItems.Add(scriptGroup.Name);
                    }
                    else
                    {
                        Toast.Error("配置组加载失败");
                    }
                }
            }
            return string.Join(",", selectedItems); // 返回所有选中的项
        }
        return null;
    }

    public async Task<string?> OnPotBuyItemAsync()
    {
        var stackPanel = new StackPanel
        {
            Orientation = Orientation.Vertical,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center
        };
        var checkBoxes = new Dictionary<string, CheckBox>(); 
        CheckBox selectedCheckBox = null;
        
        if (SelectedConfig.SecretTreasureObjects == null || SelectedConfig.SecretTreasureObjects.Count == 0)
        {
            Toast.Warning("未配置洞天百宝购买配置，请先设置");
            SelectedConfig.SecretTreasureObjects.Add("每天重复");
        }
        var infoTextBlock = new TextBlock
        {
            Text = "日期不影响领取好感和钱币",
            HorizontalAlignment = HorizontalAlignment.Center,
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };

        stackPanel.Children.Add(infoTextBlock);
        // 添加下拉选择框
        var dayComboBox = new ComboBox
        {
            ItemsSource = new List<string> { "星期一", "星期二", "星期三", "星期四", "星期五", "星期六", "星期日", "每天重复" },
            SelectedItem = SelectedConfig.SecretTreasureObjects.First(),
            FontSize = 12,
            Margin = new Thickness(0, 0, 0, 10)
        };
        stackPanel.Children.Add(dayComboBox);
        
        foreach (var potBuyItem in SecretTreasureObjectList)
        {
            var checkBox = new CheckBox
            {
                Content = potBuyItem,
                Tag = potBuyItem,
                MinWidth = 180,
                IsChecked = SelectedConfig.SecretTreasureObjects.Contains(potBuyItem) 
            };
            checkBoxes[potBuyItem] = checkBox; 
            stackPanel.Children.Add(checkBox);
        }
        
        var uiMessageBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "洞天百宝购买选择",
            Content = new ScrollViewer
            {
                Content = stackPanel,
                VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            },
            CloseButtonText = "关闭",
            PrimaryButtonText = "确认",
            Owner = Application.Current.ShutdownMode == ShutdownMode.OnMainWindowClose ? null : Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Width, // 确保弹窗根据内容自动调整大小
            MinWidth = 200,
            MaxHeight = 500,
        };

        var result = await uiMessageBox.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            SelectedConfig.SecretTreasureObjects.Clear();
            SelectedConfig.SecretTreasureObjects.Add(dayComboBox.SelectedItem.ToString());
            List<string> selectedItems = new List<string>(); // 用于存储所有选中的项
            foreach (var checkBox in checkBoxes.Values)
            {
                if (checkBox.IsChecked == true)
                {
                    var potBuyItem = checkBox.Tag as string;
                    if (potBuyItem != null)
                    {
                        selectedItems.Add(potBuyItem);
                        SelectedConfig.SecretTreasureObjects.Add(potBuyItem);
                    }
                    else
                    {
                        Toast.Error("加载失败");
                    }
                }
            }
            if (selectedItems.Count > 0)
            {
                return string.Join(",", selectedItems); // 返回所有选中的项
            }
            else
            {
                Toast.Warning("选择为空，请选择购买的宝物");
            }
        }
        return null;
    }
    
    [ObservableProperty] private ObservableCollection<OneDragonFlowConfig> _configList = [];
    /// <summary>
    /// 当前生效配置
    /// </summary>
  
    [ObservableProperty] private OneDragonFlowConfig? _selectedConfig;
    
    [ObservableProperty] 
    private OneDragonFlowConfig _selectedConfigCache;
    partial void OnSelectedConfigChanged(OneDragonFlowConfig? value)
    {
        if (!string.IsNullOrEmpty(value.Name))
        {
            _selectedConfigCache = value; 
        }
    }

    [ObservableProperty] private List<string> _craftingBenchCountry = ["枫丹", "稻妻", "璃月", "蒙德"];

    [ObservableProperty] private List<string> _adventurersGuildCountry = ["枫丹", "稻妻", "璃月", "蒙德"];

    [ObservableProperty] private List<string> _domainNameList = ["", ..MapLazyAssets.Instance.DomainNameList];

    [ObservableProperty] private List<string> _completionActionList = ["无", "关闭游戏", "关闭游戏和软件", "关机"];

    [ObservableProperty] private List<string> _sundayEverySelectedValueList = ["1", "2", "3"];
    
    [ObservableProperty] private List<string> _sundaySelectedValueList = ["1", "2", "3"];

    [ObservableProperty] private List<string> _secretTreasureObjectList = ["布匹","须臾树脂","大英雄的经验","流浪者的经验","精锻用魔矿","摩拉","祝圣精华","祝圣油膏"];
    
    public AllConfig Config { get; set; } = TaskContext.Instance().Config;
    
    public  OneDragonFlowViewModel()
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
            
            if (e.NewItems != null && e.NewItems.Count == 1) 
            {
                var movedConfig = e.NewItems[0] as OneDragonFlowConfig;
                if (movedConfig != null)
                {
                    if (Config.SelectedOneDragonFlowPlanName != movedConfig.ScheduleName)//防止错误移动
                    {
                        return;
                    }
                    var currentScheduleName = Config.SelectedOneDragonFlowPlanName;
                    var currentScheduleConfigs = ConfigList
                        .Where(c => c.ScheduleName == currentScheduleName)
                        .ToList();
                    for (int i = 0; i < currentScheduleConfigs.Count; i++)// 重新排序
                    {
                        currentScheduleConfigs[i].IndexId = i + 1;
                        WriteConfig(currentScheduleConfigs[i]);
                    }
                }
            }
        };
        
        TaskList.CollectionChanged += (sender, e) =>
        {
            if (e.NewItems != null)
            {
                foreach (OneDragonTaskItem newItem in e.NewItems)
                {
                    newItem.PropertyChanged += TaskPropertyChanged;
                }
            }
            if (e.OldItems != null)
            {
                foreach (OneDragonTaskItem oldItem in e.OldItems)
                {
                    oldItem.PropertyChanged -= TaskPropertyChanged;
                }
            }
            if (e.Action == NotifyCollectionChangedAction.Move)
            {
                SaveConfig();
            }
        };
        FilteredConfigList = CollectionViewSource.GetDefaultView(ConfigList);
        FilteredConfigList.Filter = FilterLogic;
    }

    [RelayCommand]
    private async Task ShowAndSwitchPlanAsync()
    {
        var listBox = new ListBox
        {
            ItemsSource = Config.ScheduleList,
            SelectedItem = Config.SelectedOneDragonFlowPlanName,
            MinWidth = 180
        };

        var newButton = new Button { Content = "新建计划表", Margin = new Thickness(0, 0, 10, 0) };
        var deleteButton = new Button { Content = "删除计划表" };

        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { newButton, deleteButton }
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 标题
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 按钮

        grid.Children.Add(new TextBlock { Text = "请选择计划表：", Margin = new Thickness(0, 0, 0, 5), FontSize = 14 });
        Grid.SetRow(grid.Children[^1], 0);

        var scrollViewer = new ScrollViewer
        {
            Content = listBox,
            VerticalScrollBarVisibility = ScrollBarVisibility.Auto,
            MaxHeight = 250
        };
        grid.Children.Add(scrollViewer);
        Grid.SetRow(scrollViewer, 1);

        grid.Children.Add(buttonPanel);
        Grid.SetRow(buttonPanel, 2);

        var messageBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "切换计划表",
            Content = grid,
            CloseButtonText = "取消",
            PrimaryButtonText = "确认",
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Width,
            MinWidth = 220,
            MaxHeight = 400
        };
  
        newButton.Click += async (s, e) =>
        {
            messageBox.Hide();
            var newName = PromptDialog.Prompt("请输入新的计划表名称", "新建计划表");
            if (!string.IsNullOrWhiteSpace(newName))
            {
                if (!Config.ScheduleList.Contains(newName))
                {
                    Config.ScheduleList.Add(newName);
                    Config.ScheduleList = new ObservableCollection<string>(Config.ScheduleList.OrderBy(x => x));
                    Toast.Success($"计划表 \"{newName}\" 添加成功！");
                }
                else
                {
                    Toast.Warning($"计划表 \"{newName}\" 已存在！");
                }
            }
            listBox.ItemsSource = Config.ScheduleList;
            await messageBox.ShowDialogAsync();
        };
  
      deleteButton.Click += (s, e) =>
      {
          if (listBox.SelectedItem is string selected && selected != "默认计划表")
          {
              var selectedcopy = selected;// 保存选择的计划表名称
              if (ConfigList.Any(c => c.ScheduleName == selected))
                {
                    if (MessageBox.Show($"计划表 \"{selected}\" 下有配置单，配置单将移至 <默认计划表> 中，确定要删除计划表 \"{selected}\" ？", "删除计划表", System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes)
                    {
                        foreach (var config in ConfigList.Where(c => c.ScheduleName == selected))
                        {
                            config.ScheduleName = "默认计划表";
                        }
                        Config.ScheduleList = new ObservableCollection<string>(Config.ScheduleList.Where(x => x != selected));
                        Toast.Success($"计划表 \"{selected}\" 删除成功！");
                        listBox.ItemsSource = Config.ScheduleList;
                        listBox.SelectedItem = "默认计划表";
                    }
                }
                else
                {
                    if (MessageBox.Show($"计划表 \"{selected}\" 下没有配置单，可以直接删除。", "删除计划表", System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes)
                    {
                        Config.ScheduleList = new ObservableCollection<string>(Config.ScheduleList.Where(x => x != selected));
                        Toast.Success($"计划表 \"{selected}\" 删除成功！");
                        listBox.ItemsSource = Config.ScheduleList;
                        listBox.SelectedItem = "默认计划表";
                    }
                }
                if (selectedcopy == Config.SelectedOneDragonFlowPlanName)
                {
                    Config.SelectedOneDragonFlowPlanName = "默认计划表";
                }
                RefreshFilteredConfigList();
          }
          else
          {
              Toast.Warning("不能删除默认计划表！");
          }
      };
  
      var result = await messageBox.ShowDialogAsync();
      if (result == Wpf.Ui.Controls.MessageBoxResult.Primary && listBox.SelectedItem is string selectedPlan)
      {
          Config.SelectedOneDragonFlowPlanName = selectedPlan;
          InitConfigList();
          RefreshFilteredConfigList();
          var lastConfig = ConfigList.LastOrDefault(c => c.ScheduleName == selectedPlan);
          if (lastConfig != null)
          {
              Toast.Warning($"计划表 \"{selectedPlan}\" 下有配置单，将自动选择最后一条配置单");
              SelectedConfig = lastConfig;
              OnConfigDropDownChanged();
          }
      }
    }
    
    [RelayCommand]
    private void AddScheduleItem()
    {
        var newScheduleName = PromptDialog.Prompt("请输入新的计划表名称", "新增计划表");
    
        // 检查输入是否为空或重复
        if (!string.IsNullOrEmpty(newScheduleName))
        {
            if (!Config.ScheduleList.Contains(newScheduleName))
            {
                Config.ScheduleList.Add(newScheduleName);
                Config.ScheduleList = new ObservableCollection<string>(Config.ScheduleList.OrderBy(schedule => schedule));
                Toast.Success($"计划表 \"{newScheduleName}\" 添加成功！");
            }
            else
            {
                Toast.Warning($"计划表 \"{newScheduleName}\" 已存在！");
            }
        }
        else
        {
            Toast.Warning("计划表名称不能为空！");
        }
    }

    [RelayCommand]
    private void DeleteConfig()
    {
        if (SelectedConfig == null || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        string configName = SelectedConfig.Name;
        if (!string.IsNullOrEmpty(configName))
        {
            var configToDelete = ConfigList.FirstOrDefault(c => c.Name == configName);
            if (configToDelete != null)
            {
                var result = MessageBox.Show($"确定要删除 {configName} 配置单吗？删除后无法恢复！", "确认删除", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning);
                if (result == System.Windows.MessageBoxResult.Yes)
                {
                    string filePath = Path.Combine(_basePath, _configPath, $"{configName}.json");
                    try
                    {
                        if (File.Exists(filePath))
                        {
                            File.Delete(filePath);
                            ConfigList.Remove(configToDelete);
    
                            // 删除后处理 SelectedConfig
                            var nextConfig = ConfigList.LastOrDefault(c => c.ScheduleName == Config.SelectedOneDragonFlowPlanName);
                            if (nextConfig != null)
                            {
                                SelectedConfig = nextConfig;
                                OnConfigDropDownChanged();
                            }
                            else
                            {
                                // 检查是否已存在“默认配置”
                                var defaultConfig = ConfigList.FirstOrDefault(c =>
                                    c.Name == "默认配置");
                                
                                if (defaultConfig == null && ConfigList.Count <= 0)
                                {
                                    defaultConfig = new OneDragonFlowConfig
                                    {
                                        Name = "默认配置",
                                        ScheduleName = Config.SelectedOneDragonFlowPlanName,
                                        IndexId = 1
                                    };
                                    ConfigList.Add(defaultConfig);
                                }
                                SelectedConfig = defaultConfig;
                                OnConfigDropDownChanged();
                            }
                            RefreshFilteredConfigList();
                            var configs = ConfigList.Where(c => c.ScheduleName == Config.SelectedOneDragonFlowPlanName).ToList();
                            for (int i = 0; i < configs.Count; i++)
                            {
                                configs[i].IndexId = i + 1;
                            }
                            var lastConfig = ConfigList.LastOrDefault(c => c.ScheduleName == Config.SelectedOneDragonFlowPlanName);
                            if (lastConfig != null)
                            {
                                SelectedConfig = lastConfig;
                                OnConfigDropDownChanged();
                            }
                            Toast.Success($"配置 {configName} 已删除");
                        }
                        else
                        {
                            Toast.Warning($"配置文件 {configName}.json 未找到");
                        }
                    }
                    catch (Exception ex)
                    {
                        Toast.Error($"删除配置文件 {configName}.json 时发生错误: {ex.Message}");
                    }
                }
            }
            else
            {
                Toast.Warning($"配置 {configName} 未找到");
            }
        }
        else
        {
            Toast.Warning("请输入配置名称");
        }
    }
    
    [RelayCommand]
    private void CopyConfig()
    {
        if (SelectedConfig == null || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        string oldName = SelectedConfig.Name;
        if (string.IsNullOrEmpty(oldName))
        {
            Toast.Warning("请输入需要复制的配置名称");
            return;
        }
        var configToCopy = ConfigList.FirstOrDefault(c => c.Name == oldName);
        if (configToCopy == null)
        {
            Toast.Warning($"未找到名称为 {oldName} 的配置");
            return;
        }
        // 弹窗输入新名称
        var newName = PromptDialog.Prompt("请输入新的一条龙配置单名称", "复制配置单", oldName);
        if (string.IsNullOrEmpty(newName))
        {
            Toast.Warning("新名称不能为空");
            return;
        }
        // 检查新名称是否已存在
        if (ConfigList.Any(c => c.Name == newName))
        {
            Toast.Warning($"名称为 {newName} 的配置已存在，请输入其他名称");
            return;
        }
        // 复制配置文件并修改名称
        string oldFilePath = Path.Combine(_basePath, _configPath, $"{oldName}.json");
        string newFilePath = Path.Combine(_basePath, _configPath, $"{newName}.json");
        try
        {
            if (File.Exists(oldFilePath))
            {
                File.Copy(oldFilePath, newFilePath); // 复制文件
                var newConfig = JsonConvert.DeserializeObject<OneDragonFlowConfig>(File.ReadAllText(newFilePath));
                if (newConfig != null)
                {
                    newConfig.Name = newName; // 修改配置的 Name 属性
                    newConfig.ScheduleName = Config.SelectedOneDragonFlowPlanName; // 更新计划表名称
                    WriteConfig(newConfig); // 保存修改后的配置
                    ConfigList.Add(newConfig); // 添加到配置列表
                    Toast.Success($"配置 {oldName} 已复制为 {newName}");
                }
            }
            else
            {
                Toast.Warning($"原配置文件 {oldName}.json 不存在");
            }
        }
        catch (Exception ex)
        {
            Toast.Error($"复制配置时发生错误: {ex.Message}");
        }
    }

    [RelayCommand]
    private  void EditConfig()
    {
        if (SelectedConfig == null || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        string oldName = SelectedConfig.Name;
        if (string.IsNullOrEmpty(oldName))
        {
            Toast.Warning("请输入需要修改的配置名称");
            return;
        }
        var configToRename = ConfigList.FirstOrDefault(c => c.Name == oldName);
        if (configToRename == null)
        {
            Toast.Warning($"未找到名称为 {oldName} 的配置");
            return;
        }
        // 弹窗输入新名称
        var newName = PromptDialog.Prompt("请输入新的一条龙配置单名称", "修改配置单名称", SelectedConfig.Name);
        if (string.IsNullOrEmpty(newName))
        {
            Toast.Warning("新名称不能为空");
            return;
        }
        // 检查新名称是否已存在
        if (ConfigList.Any(c => c.Name == newName))
        {
            Toast.Warning($"名称为 {newName} 的配置已存在，请输入其他名称");
            return;
        }
        // 修改配置名称和文件名
        string oldFilePath = Path.Combine(_basePath, _configPath, $"{oldName}.json");
        string newFilePath = Path.Combine(_basePath, _configPath, $"{newName}.json");
        try
        {
            if (File.Exists(oldFilePath))
            {
                File.Move(oldFilePath, newFilePath); // 修改文件名
            }
            configToRename.Name = newName; // 修改配置的 Name 属性
            WriteConfig(configToRename); // 保存修改后的配置
            Toast.Success($"配置名 {oldName} 称已修改为 {newName}");
        }
        catch (Exception ex)
        {
            Toast.Error($"修改配置名称时发生错误: {ex.Message}");
        }
    }

    [RelayCommand]
    private async void DeleteScheduleItem()
    {
        // 检查是否有计划表
        if (Config.ScheduleList.Count == 0)
        {
            Toast.Warning("没有可删除的计划表！");
            return;
        }

        // 创建一个 ComboBox 作为选择控件
        var comboBox = new ComboBox
        {
            ItemsSource = Config.ScheduleList,
            SelectedIndex = 0 // 默认选择第一个
        };

        // 弹出对话框让用户选择要删除的计划表
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "删除计划表",
            Content = new StackPanel
            {
                Children =
                {
                    new TextBlock { Text = "请选择要删除的计划表：", Margin = new Thickness(0, 0, 0, 10) },
                    comboBox
                }
            },
            PrimaryButtonText = "删除",
            CloseButtonText = "取消",
            Height = 200,
            Owner = Application.Current.MainWindow, // 设置 Owner 确保弹窗与主窗口关联
            WindowStartupLocation = WindowStartupLocation.CenterOwner, // 确保弹窗居中显示
            SizeToContent = SizeToContent.Width // 根据内容自动调整大小
        };

        var result = await dialog.ShowDialogAsync();

        // 检查用户是否确认删除
        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary && comboBox.SelectedItem is string selectedSchedule)
        {
            if (selectedSchedule == "默认计划表")
            {
                Toast.Warning("默认计划表不能删除！");
                return;
            }
            // 删除计划表
            
            // 将原来属于该计划表的配置单改为“默认计划表”
            foreach (var config in ConfigList.Where(config => config.ScheduleName == selectedSchedule))
            {
                config.ScheduleName = "默认计划表";
            }
            
            Config.ScheduleList = new ObservableCollection<string>(Config.ScheduleList.Where(schedule => schedule != selectedSchedule));

            Toast.Success($"计划表 \"{selectedSchedule}\" 删除成功！");
        }
        else
        {
            Toast.Warning("未选择计划表或操作已取消！");
        }
    }
    
    public override void OnNavigatedTo()
    {
        InitConfigList();
    }

    public void InitConfigList()
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
        configs = configs.OrderBy(config => config.IndexId).ToList();
        ConfigList.Clear();
        foreach (var config in configs)
        {
            ConfigList.Add(config);
        }
        SelectedConfig = selected;
        LoadDisplayTaskListFromConfig();
        OnConfigDropDownChanged();
    }

    // 新增方法：从配置文件加载 DisplayTaskList
    public void LoadDisplayTaskListFromConfig()
    {
        if (string.IsNullOrEmpty(SelectedConfig.Name) || string.IsNullOrEmpty(SelectedConfig.TaskEnabledList.ToString())) 
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

    [RelayCommand]
    private void DeleteConfigDisplayTaskListFromConfig()
    {
        if (string.IsNullOrEmpty(SelectedConfig.Name) || string.IsNullOrEmpty(SelectedConfig.TaskEnabledList.ToString()))
        {
            Toast.Warning("请先选择配置组和任务");
            return;
        }

        TaskList.Clear();
        foreach (var kvp in SelectedConfig.TaskEnabledList)
        {
            var taskItem = new OneDragonTaskItem(kvp.Key)
            {
                IsEnabled = kvp.Value
            };
            if (taskItem.Name != InputScriptGroupName)
            {
                TaskList.Add(taskItem);
                taskItem = null;
                Toast.Information("已经删除");
            }
        }
    }

    [RelayCommand]
    private void OnConfigDropDownChanged()
    {
        _isLoading = true;
        try
        {
            SetSomeSelectedConfig(SelectedConfig);
        }
        finally
        {
            _isLoading = false;
        }
    }

    public void SaveConfig()
    {
        if (string.IsNullOrEmpty(SelectedConfig.Name) || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            return;
        }

        SelectedConfig.TaskEnabledList.Clear();
        foreach (var task in TaskList)
        {
            SelectedConfig.TaskEnabledList[task.Name] = task.IsEnabled;
        }
        
        WriteConfig(SelectedConfig);
    }

    [RelayCommand]
    private async void AddPotBuyItem()
    {
        await OnPotBuyItemAsync();
        SaveConfig();
        SelectedTask = null;
    }
    
    [RelayCommand]
    private void AddTaskGroup()
    {
        if(SelectedConfig == null || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        AddNewTaskGroup();
        SaveConfig();
        SelectedTask = null;
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
    
    private bool _isLoading = false;
    private async void TaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoading) return;

        _isLoading = true;
        try
        {
            if (SelectedConfig.ScheduleName == Config.SelectedOneDragonFlowPlanName)
            {
                SaveConfig();
            }
            else
            {
                Toast.Warning("错误触发！");
            }
        }
        finally
        {
            _isLoading = false;
        }
    }

    private async void ConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (_isLoading) return;

        _isLoading = true;
        try
        {
            
            if (SelectedConfig.ScheduleName == Config.SelectedOneDragonFlowPlanName)
            {
                SaveConfig();
            } 
            else
            {
                Toast.Warning("错误触发！！");
            }
        }
        finally
        {
            if (e.PropertyName == nameof(OneDragonFlowConfig.ScheduleName))
            {
                // 获取与 SelectedConfig.ScheduleName 相同的配置项并按 IndexId 排序
                var configs = ConfigList
                    .Where(c => c.ScheduleName == SelectedConfig.ScheduleName)
                    .OrderBy(c => c.IndexId) 
                    .ToList();
                int newIndexCount = configs.Count;
                SelectedConfig.IndexId = newIndexCount;
                WriteConfig(SelectedConfig);
                RefreshFilteredConfigList();
            }
            _isLoading = false;
        }

    }
   
    
    public void WriteConfig(OneDragonFlowConfig? config)
    {
        if (config == null || string.IsNullOrEmpty(config.Name))
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

    private bool _autoRun = true;
    
    [RelayCommand]
    private void OnLoaded()
    {
        // 组件首次加载时运行一次。
        if (!_autoRun)
        {
            return;
        }
        _autoRun = false;
        
        var distinctScheduleNames = ConfigList.Select(x => x.ScheduleName).Distinct().ToList();
        foreach (var scheduleName in distinctScheduleNames)
        {
            if (!Config.ScheduleList.Contains(scheduleName))
            {
                Config.ScheduleList.Add(scheduleName);
            }
        }
        foreach (var config in ConfigList)
        {
            if (string.IsNullOrWhiteSpace(config.ScheduleName))
            {
                try
                {
                    config.ScheduleName = "默认计划表";
                    _isLoading = true;
                    WriteConfig(config);
                }
                finally
                {
                    _isLoading = false;
                }
            }
        }
        
        var args = Environment.GetCommandLineArgs();
        if (args.Length > 1 && args[1].Contains("startOneDragon"))
        {
            // 通过命令行参数启动一条龙。
            if (args.Length > 2)
            {
                // 从命令行参数中提取一条龙配置名称。
                _logger.LogInformation($"参数指定的一条龙配置：{args[2]}");
                var argsOneDragonConfig = ConfigList.FirstOrDefault(x => x.Name == args[2], null);
                if (argsOneDragonConfig != null)
                {
                    // 设定配置，配置下拉框会选定。
                    SelectedConfig = argsOneDragonConfig;
                    // 调用选定更新函数。
                    OnConfigDropDownChanged();
                }
                else
                {
                    _logger.LogWarning("未找到，请检查。");
                }
            }
            // 异步执行一条龙
            Toast.Information($"命令行一条龙「{SelectedConfig.Name}」。");
            OnOneKeyExecute();
        }
        if (args.Length > 1 && args[1].Contains("startContinuousOneDragon"))
        {
            // 通过命令行参数启动一条龙。
            if (args.Length > 2)
            {
                // 从命令行参数中提取一条龙配置名称。
                //_logger.LogInformation($"参数指定的连续一条龙配置：{args[2]}");
                
                var argsOneDragonPlan = !string.IsNullOrEmpty(args[2]) ? args[2] : Config.SelectedOneDragonFlowPlanName;

                if (!string.IsNullOrEmpty(argsOneDragonPlan))
                { 
                    // 设定配置，配置下拉框会选定。
                    Config.SelectedOneDragonFlowPlanName = argsOneDragonPlan;
                    RefreshFilteredConfigList();
                    string todayNow = DateTime.Now.DayOfWeek switch
                    {
                        DayOfWeek.Monday => "星期一",
                        DayOfWeek.Tuesday => "星期二",
                        DayOfWeek.Wednesday => "星期三",
                        DayOfWeek.Thursday => "星期四",
                        DayOfWeek.Friday => "星期五",
                        DayOfWeek.Saturday => "星期六",
                        DayOfWeek.Sunday => "星期日",
                        _ => "未知"
                    };
                    //确认配置单是否存在
                    var boundConfigs = ConfigList.Where(config => config.AccountBinding == true && (config.Period == "每日" 
                            || config.Period == todayNow) && config.ScheduleName == argsOneDragonPlan)
                        .OrderBy(config => config.IndexId)
                        .ToList();
                    if (boundConfigs.Count == 0)
                    {
                        Toast.Warning("没有可执行的配置单，请先设定配置单");
                        return;
                    }
                }
                else
                {
                    _logger.LogWarning("未找到，请检查。");
                }
            }
            // 异步执行一条龙
            Toast.Information($"命令行连续一条龙「{Config.SelectedOneDragonFlowPlanName}」。");
            OnOneKeyContinuousExecutionOneKey();
        }
    }
    
    //连续执行一条龙配置单
    private bool _continuousExecutionMark = false;
    private int _executionSuccessCount = 0; 
    [RelayCommand]
    private async Task OnOneKeyContinuousExecutionOneKey()
    {
        await ScriptService.StartGameTask();
        _continuousExecutionMark = true;
        _executionSuccessCount = 0; 
        string todayNow = DateTime.Now.DayOfWeek switch
        {
            DayOfWeek.Monday => "星期一",
            DayOfWeek.Tuesday => "星期二",
            DayOfWeek.Wednesday => "星期三",
            DayOfWeek.Thursday => "星期四",
            DayOfWeek.Friday => "星期五",
            DayOfWeek.Saturday => "星期六",
            DayOfWeek.Sunday => "星期日",
            _ => "未知"
        };
        if (ConfigList.Count == 0)//命令行启动时，没有初始化，或者没有配置单，再次确认
        { 
            Toast.Warning("配置单空，尝试初始化！");
            InitConfigList();
        }
        var boundConfigs = ConfigList.Where(config => config.AccountBinding == true && (config.Period == todayNow || config.Period == "每日") && config.ScheduleName == Config.SelectedOneDragonFlowPlanName)
            .OrderBy(config => config.IndexId)
            .ToList();
        _logger.LogInformation("连续一条龙：今天 {todayNow} ，执行 {ScheduleName} 计划，生效配置单数量 {BoundConfigCount}",todayNow,Config.SelectedOneDragonFlowPlanName,boundConfigs.Count);
        //Toast.Success(boundConfigs.Count+"  "+ConfigList.Count);
        if (ConfigList.Count == 0 || boundConfigs.Count == 0) 
        {
            Toast.Warning("连续一条龙需绑定UID,请先设定配置单");
            return;
        }
        int configIndex = 0;
        foreach (var config in boundConfigs)
        {
            configIndex++;
            SelectedConfig = config;
            OnConfigDropDownChanged();
            _logger.LogInformation("正在执行 {ScheduleName} 计划的第 {ConfigIndex} / {boundConfigs.Count} 个配置单：{Config.Name}，绑定UID {Config.GenshinUid}", 
                Config.SelectedOneDragonFlowPlanName,configIndex,boundConfigs.Count,config.Name, config.GenshinUid);
            await Task.Delay(1000);
            await OnOneKeyExecute();
            await new ReturnMainUiTask().Start(CancellationToken.None);
            // 如果任务已经被取消，中断所有任务
            if (CancellationContext.Instance.Cts.IsCancellationRequested)
            {
                _continuousExecutionMark = false;// 标记连续执行结束
                _executionSuccessCount = 0;// 重置连续执行成功次数
                _logger.LogInformation("连续一条龙：任务结束");
                Notify.Event(NotificationEvent.DragonEnd).Success("连续一条龙：任务结束");
                return; // 后续的检查任务也不执行
            }
        }
        // 连续执行完毕后，检查和最终结束的任务
        await new TaskRunner().RunThreadAsync(async () =>
        {
            await Task.Delay(500);
            Notify.Event(NotificationEvent.DragonEnd).Success($"连续一条龙：{Config.SelectedOneDragonFlowPlanName} 共完成 {_executionSuccessCount} / {boundConfigs.Count} 个配置单一条龙任务");
            _logger.LogInformation("连续一条龙：{_selectedOneDragonFlowPlanName} 共完成 {_executionSuccessCount} / {boundConfigs.Count} 个配置单一条龙任务",Config.SelectedOneDragonFlowPlanName,_executionSuccessCount,boundConfigs.Count);
            _continuousExecutionMark = false;// 标记连续执行结束
            _executionSuccessCount = 0;// 重置连续执行成功次数
            if (SelectedConfig != null && !string.IsNullOrEmpty(Config.ContinuousCompletionAction))
            {
                switch (Config.ContinuousCompletionAction)
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
        });
    }

    [RelayCommand]
    public async Task OnOneKeyExecute()
    {
        if (!_continuousExecutionMark)
        {
            InitConfigList();//初始化配置，保证当前选择的配置是最新的 
        }
        if (string.IsNullOrEmpty(SelectedConfig.Name) || string.IsNullOrEmpty(Config.SelectedOneDragonFlowConfigName))
        {
            Toast.Warning("请先选择配置");
            return;
        }
        
        var taskListCopy = new List<OneDragonTaskItem>(TaskList);//避免执行过程中修改TaskList
        foreach (var task in taskListCopy)
        {
            task.InitAction(SelectedConfig);
        }

        int finishOneTaskcount = 1;
        int finishTaskcount = 1;
        int enabledTaskCountall = SelectedConfig.TaskEnabledList.Count(t => t.Value);
        _logger.LogInformation($"启用任务总数量: {enabledTaskCountall}");

        await ScriptService.StartGameTask(); 
        
        // 验证UID
        bool uidCheckResult = false;
        bool switchAccountResult = false;
        int reTrySwitchTimes = _exitPhoneCount; // 切换账号的最大次数
        int reTrySwitchCount = 0; // 当前切换账号的次数
        int retrySingleTimes = 3; // 当前账号的UID验证最大次数
        int retrySingleCount = 0; // 当前账号的UID验证次数
        
        for (int i = 0; i < retrySingleTimes*reTrySwitchTimes; i++){
            await new TaskRunner().RunCurrentAsync(async () =>
            {
                //月卡检测或主页检测
                await new BlessingOfTheWelkinMoonTask().Start(CancellationContext.Instance.Cts.Token);
                //===============
                
                retrySingleCount++;
                uidCheckResult = await VerifyUid(cts: new CancellationContext()); // 验证当前登录账号的UID
            });
            // 如果任务已经被取消，中断所有任务
            if (CancellationContext.Instance.Cts.IsCancellationRequested)
            {
                _continuousExecutionMark = false;// 标记连续执行结束
                _executionSuccessCount = 0;// 重置连续执行成功次数
                _logger.LogInformation("连续一条龙：任务结束");
                Notify.Event(NotificationEvent.DragonEnd).Success("连续一条龙：任务结束");
                return; // 后续的检查任务也不执行
            }
            if (!uidCheckResult)
            {   
                if (retrySingleCount >= retrySingleTimes)
                {
                    reTrySwitchCount ++;
                    if (reTrySwitchCount >= reTrySwitchTimes)
                    {
                        _logger.LogError("UID验证:  {SelectedConfig.Name} / {SelectedConfig.GenshinUid} 配置单任务,切换账号 {reTrySwitchTimes} 次,验证UID仍然失败,退出执行",
                            SelectedConfig.Name,SelectedConfig.GenshinUid,reTrySwitchTimes-1);
                        return;
                    }
                    _logger.LogWarning("UID验证:失败 {retrySingleTimes} 次,第 {reTrySwitchCount} 次尝试切换账号",retrySingleTimes,reTrySwitchCount);
                    await new TaskRunner().RunCurrentAsync(async () =>
                    { 
                        retrySingleCount = 0; // 重置UID验证次数
                        switchAccountResult = await SwitchAccount(cts: new CancellationContext(), reTrySwitchCount); //失败后，切换账号
                    });
                    // 如果任务已经被取消，中断所有任务
                    if (CancellationContext.Instance.Cts.IsCancellationRequested)
                    {
                        _continuousExecutionMark = false;// 标记连续执行结束
                        _executionSuccessCount = 0;// 重置连续执行成功次数
                        _logger.LogInformation("连续一条龙：任务结束");
                        Notify.Event(NotificationEvent.DragonEnd).Success("连续一条龙：任务结束");
                        return; // 后续的检查任务也不执行
                    }
                    _logger.LogInformation($"切换账号: {(switchAccountResult ? "成功" : "失败")} ,继续UID验证");
                }
                else
                {
                    _logger.LogWarning("UID验证:失败,第 {retrySingleCount} 次尝重试验证",retrySingleCount);
                }
            }
            else
            {
                _logger.LogInformation($"UID验证 {SelectedConfig.GenshinUid} ，继续执行");
                break;
            }
        }
        // 验证UID结束

        ReadScriptGroup();
        foreach (var task in ScriptGroupsdefault)
        {
            ScriptGroups.Remove(task);
        }

        foreach (var scriptGroup in ScriptGroups)
        {
            SelectedConfig.TaskEnabledList.Remove(scriptGroup.Name);
        }

        if (string.IsNullOrEmpty(SelectedConfig.Name) || taskListCopy.Count(t => t.IsEnabled) == 0)
        {
            Toast.Warning("请先选择任务");
            _logger.LogInformation("没有配置,退出执行!");
            return;
        }

        int enabledoneTaskCount = SelectedConfig.TaskEnabledList.Count(t => t.Value);
        _logger.LogInformation($"启用一条龙任务的数量: {enabledoneTaskCount}");
        
        await ScriptService.StartGameTask();
        SaveConfig();
        
        int enabledTaskCount = SelectedConfig.TaskEnabledList.Count(t =>
            t.Value && ScriptGroupsdefault.All(defaultTask => defaultTask.Name != t.Key));
        _logger.LogInformation($"启用配置组任务的数量: {enabledTaskCount}");

        if (enabledoneTaskCount <= 0)
        {
            _logger.LogInformation("没有一条龙任务!");
        }
        
        Notify.Event(NotificationEvent.DragonStart).Success("一条龙启动");
        foreach (var task in taskListCopy)
        {
            if (task is { IsEnabled: true, Action: not null })
            {
                if (ScriptGroupsdefault.Any(defaultSg => defaultSg.Name == task.Name))
                {
                    _logger.LogInformation($"一条龙任务执行: {finishOneTaskcount++}/{enabledoneTaskCount}");
                    await new TaskRunner().RunThreadAsync(async () =>
                    {
                        await task.Action();
                        await Task.Delay(1000);
                    });
                }
                else
                {
                    try
                    {
                        if (enabledTaskCount <= 0)
                        {
                            _logger.LogInformation("没有配置组任务,退出执行!");
                            return;
                        }

                        Notify.Event(NotificationEvent.DragonStart).Success("配置组任务启动");

                        if (SelectedConfig.TaskEnabledList[task.Name])
                        {
                            _logger.LogInformation($"配置组任务执行: {finishTaskcount++}/{enabledTaskCount}");
                            await Task.Delay(500);
                            string filePath = Path.Combine(_basePath, _scriptGroupPath, $"{task.Name}.json");
                            var group = ScriptGroup.FromJson(await File.ReadAllTextAsync(filePath));
                            IScriptService? scriptService = App.GetService<IScriptService>();
                            await scriptService!.RunMulti(ScriptControlViewModel.GetNextProjects(group), group.Name);
                            await Task.Delay(1000);
                        }
                    }
                    catch (Exception e)
                    {
                        _logger.LogDebug(e, "执行配置组任务时失败");
                        Toast.Error("执行配置组任务时失败");
                    }
                }
                // 如果任务已经被取消，中断所有任务
                if (CancellationContext.Instance.Cts.IsCancellationRequested)
                {
                    _logger.LogInformation("任务被取消，退出执行");
                    Notify.Event(NotificationEvent.DragonEnd).Success("一条龙和配置组任务结束");
                    return; // 后续的检查任务也不执行
                }
            }
        }
        
        // 当次执行配置单完成后，检查和最终结束的任务
        await new TaskRunner().RunThreadAsync(async () =>
        {
            _executionSuccessCount++;
            await new CheckRewardsTask().Start(CancellationContext.Instance.Cts.Token);
            await Task.Delay(500);
            Notify.Event(NotificationEvent.DragonEnd).Success($"配置单 {SelectedConfig.Name} 绑定 {SelectedConfig.GenshinUid}，一条龙和配置组任务结束");
            _logger.LogInformation("配置单 {SelectedConfig.Name} 绑定UID {GenshinUid} 一条龙和配置组任务结束",
                SelectedConfig.Name,string.IsNullOrEmpty(SelectedConfig.GenshinUid) ? "未绑定" : SelectedConfig.GenshinUid);
            // 单次执行完成后，不执行后续的完成任务
            if (!_continuousExecutionMark)
            {
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
            }
        });
    }
    
    // 新增方法：读取粘贴板内容
    private string GetClipboardText()
    {
        try
        {
            if (Clipboard.ContainsText())
            {
                return Clipboard.GetText();
            }
            else
            {
                _logger.LogWarning("读取不到游戏UID");
                return string.Empty;
            }
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "读取游戏UID时发生错误。");
            return string.Empty;
        }
    }
    
    [RelayCommand]
    private void DeleteTaskGroup()
    {
        if (SelectedConfig == null || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        DeleteConfigDisplayTaskListFromConfig();
        SaveConfig();
        InputScriptGroupName = null;
    }

    [RelayCommand]
    private void OnAddConfig()
    {
        var str = PromptDialog.Prompt("请输入一条龙配置单名称", "新增一条龙配置单");
        if (!string.IsNullOrEmpty(str))
        {
            // 检查是否已存在
            if (ConfigList.Any(x => x.Name == str))
            {
                Toast.Warning($"一条龙配置单 {str} 已经存在，请勿重复添加");
            }
            else
            {
                int index = ConfigList.Count(c => c.ScheduleName == Config.SelectedOneDragonFlowPlanName);
                var nc = new OneDragonFlowConfig
                {
                    ScheduleName = Config.SelectedOneDragonFlowPlanName, // 设置为当前选定的计划表
                    IndexId = index + 1,
                    Name = str
                };
                // 如果 index 超出范围则直接 Add
                if (index >= ConfigList.Count)
                    ConfigList.Add(nc);
                else
                    ConfigList.Insert(index, nc);
                SelectedConfig = nc;
                OnConfigDropDownChanged();
            }
            SaveConfig();
        }
    }
    
    //UID验证
    private async Task<bool> VerifyUid(CancellationContext cts)  
    {
        if (string.IsNullOrEmpty(SelectedConfig.Name))
        {
            return false;
        }
        
        if (SelectedConfig.AccountBinding == true)
        {
            await new ReturnMainUiTask().Start(cts.Cts.Token);
            Clipboard.Clear();
            Simulation.SendInput.Keyboard.KeyPress(VK.VK_ESCAPE);
            
            for (int i = 0; i < 10; i++)
            {
                using var closeRa = CaptureToRectArea().Find(AutoSkipAssets.Instance.PageCloseMainRo); 
                if (!closeRa.IsEmpty())
                {
                    closeRa.ClickTo(closeRa.X + closeRa.Width*3, closeRa.X + closeRa.Height*4);
                    await new ReturnMainUiTask().Start(cts.Cts.Token);
                    break;
                }
                await Task.Delay(500);
            }
            
            string clipboardContent = GetClipboardText();
            if (string.IsNullOrEmpty(clipboardContent))
            {
                _logger.LogError("UID读取失败，退出执行");
                return false;
            }else
            {
                if (clipboardContent.Contains(SelectedConfig.GenshinUid))
                {
                    _logger.LogInformation("UID验证: {text} 绑定 {text}，完成",SelectedConfig.Name,SelectedConfig.GenshinUid);
                    return true;
                }
                else
                {
                    _logger.LogWarning(clipboardContent.Length == 9 && clipboardContent.All(char.IsNumber) ? 
                        $"UID验证: 失败 {SelectedConfig.Name} ,绑定 {SelectedConfig.GenshinUid}，验证 {clipboardContent}" : "UID验证:失败");
                    return false;
                }
            }
        }
        else
        {
            _logger.LogInformation("未绑定UID，不执行UID验证");
            return true;
        }
    }
  
    //切换账号
    private AutoWoodAssets _assets;
    private readonly Login3rdParty _login3RdParty = new();
    private int _exitPhoneCount = 3; //,账号数为图标数量-1，默认记录2个账号
    private async Task<bool> SwitchAccount(CancellationContext cts,int switchTime = 1) //基于重新登录函数ExitAndReloginJob改造
    {

        //============== 退出游戏流程 ==============
        Logger.LogInformation("退出至登录页面");
        _assets = AutoWoodAssets.Instance;
        SystemControl.FocusWindow(TaskContext.Instance().GameHandle);
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
        await Delay(800, cts.Cts.Token);
        
        // 菜单界面验证（带重试机制）
        try
        {
            NewRetry.Do(() => 
            {
                using var contentRegion = CaptureToRectArea();
                using var ra = contentRegion.Find(_assets.MenuBagRo);
                if (ra.IsEmpty())
                {
                    // 未检测到菜单时再次发送ESC
                    Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
                    throw new RetryException("菜单界面验证失败");
                }
            }, TimeSpan.FromSeconds(1.2), 5);  // 1.2秒内重试5次
        }
        catch
        {
            // 即使失败也继续退出流程
        }

        // 点击退出按钮
        GameCaptureRegion.GameRegionClick((size, scale) => (50 * scale, size.Height - 50 * scale));
        await Delay(500, cts.Cts.Token);

        // 确认退出
        using var cr = CaptureToRectArea();
        cr.Find(_assets.ConfirmRo, ra =>
        {
            ra.Click();
            ra.Dispose();
        });
            
        await Delay(1000, cts.Cts.Token);  // 等待退出完成
        
        //============== 重新登录流程 ==============
        // 0第三方登录（如果启用）
     
        _login3RdParty.RefreshAvailabled();
        if (_login3RdParty is { Type: Login3rdParty.The3rdPartyType.Bilibili, IsAvailabled: true })
        {
            await Delay(1, cts.Cts.Token);
            _login3RdParty.Login(cts.Cts.Token);
            Logger.LogInformation("退出重登启用 B 服模式");
        }
        
        // 1点击账号切换按钮
        Logger.LogInformation("点击 {text} 按钮", "账号切换");
        var exitSwitchClickCnt = 0;
        for (var i = 0; i < 50; i++)
        {
            await Delay(1, cts.Cts.Token);
            using var contentRegion = CaptureToRectArea();
            using var ra = contentRegion.Find(_assets.ExitSwitchRo);
            if (!ra.IsEmpty())
            {
                await Delay(500, cts.Cts.Token);
                ra.Click();
                await Delay(500, cts.Cts.Token);//两次确认，防止卡顿
                ra.Click();
                await Delay(1000, cts.Cts.Token);  
                break;
            }
            else
            {
                exitSwitchClickCnt++;   
                if (exitSwitchClickCnt > 2)
                {
                    await Delay(1000, cts.Cts.Token);
                }
            }
            await Delay(2000, cts.Cts.Token);  
        }
        
        // 2点击“退出”按钮
        Logger.LogInformation("点击 {text} 按钮", "退出");
        var exitClickCnt = 0;
        for (var i = 0; i < 50; i++)
        {
            await Delay(1, cts.Cts.Token);
            var ra = CaptureToRectArea();
            var list = ra.FindMulti(new RecognitionObject
            {
                RecognitionType = RecognitionTypes.Ocr,
                RegionOfInterest = new Rect(ra.Width/2, ra.Height *11/20, ra.Width/5, ra.Height/8)
            });
            Region? exitClickCntIcon = list.FirstOrDefault(r => r.Text.Contains("退出"));
            if (exitClickCntIcon != null)
            {
                await Delay(500, cts.Cts.Token);
                exitClickCntIcon.Click();
                await Delay(1000, cts.Cts.Token);  
                break;
            }
            else
            {
                exitClickCnt++;
                if (exitClickCnt > 2)
                {
                    await Delay(1000, cts.Cts.Token); 
                }
            }
            await Delay(1000, cts.Cts.Token);  
        }
        
        // 3点击账号选择按钮
        var exitPhoneClickCnt = 0;
        for (var i = 0; i < 50; i++)
        {
            await Delay(1, cts.Cts.Token);
            using var contentRegion = CaptureToRectArea();
            using var ra = contentRegion.Find(_assets.ExitPhoneRo);
            if (!ra.IsEmpty())
            {
                Logger.LogInformation("执行 {text} 动作","选择账号");
                await Delay(500, cts.Cts.Token);
                ra.Click();
                
                await Delay(300, cts.Cts.Token);
                ra.MoveTo(ra.Width*2,ra.Height/2);//移开鼠标
                await Delay(1000, cts.Cts.Token);
                
                // 确认记录账号数量==========================================================
                var capturedArea = CaptureToRectArea();
                var exitPhone = capturedArea.FindMulti(_assets.ExitPhoneRo);
                _exitPhoneCount = exitPhone.Count; // 获取账号数量
                Logger.LogInformation("当前记录账号数量: {count}", _exitPhoneCount-1);
                // 确认记录账号数量==========================================================
                
                await Delay(500, cts.Cts.Token);
                if (_exitPhoneCount == 3)
                {
                    ra.ClickTo(ra.Width*0.5,ra.Height*2+ra.Height*1.3);//如果只有两账号，固定选另一个
                }
                else if (_exitPhoneCount == 4 && switchTime >= 2)
                {
                    ra.ClickTo(ra.Width*0.5,ra.Height*2+ra.Height*switchTime*1.3);//如果有三个账号，切换到第3个
                }
                await Delay(1000, cts.Cts.Token);     
                ra.ClickTo(0,ra.Height+ra.Height*1.5);//进入游戏
                await Delay(1000, cts.Cts.Token);  
                break;
            }
            else
            {
                exitPhoneClickCnt++; 
                if (exitPhoneClickCnt > 2)
                {
                    await Delay(1000, cts.Cts.Token);
                    break;
                }
            }
            await Delay(1000, cts.Cts.Token);  
        }

        // 4进入游戏检测
        var clickCnt = 0;
        for (var i = 0; i < 50; i++)
        {
            await Delay(1, cts.Cts.Token);

            using var contentRegion = CaptureToRectArea();
            using var ra = contentRegion.Find(_assets.EnterGameRo);
            if (!ra.IsEmpty())
            {
                clickCnt++;
                GameCaptureRegion.GameRegion1080PPosClick(955, 656);
            }
            else
            {
                if (clickCnt > 2)
                {
                    await Delay(5000, cts.Cts.Token);
                    break;
                }
            }
            await Delay(1000, cts.Cts.Token);  
        }
        
        if (clickCnt == 0)
        {
            throw new RetryException("未检测进入游戏界面");
        }

        for (var i = 0; i < 50; i++)
        {
            if (Bv.IsInMainUi(CaptureToRectArea()))
            {
                Logger.LogInformation("执行 {text} 操作结束","更换账号");
                break;
            }
            else
            {
                await new BlessingOfTheWelkinMoonTask().Start(CancellationContext.Instance.Cts.Token);
            }
            await Delay(1000, cts.Cts.Token);
            
        }
        await Delay(500, cts.Cts.Token);
        return true;
    }
}