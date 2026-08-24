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
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Script;
using BetterGenshinImpact.Core.Script.Group;
using BetterGenshinImpact.GameTask;
using BetterGenshinImpact.GameTask.Common.Element.Assets;
using BetterGenshinImpact.GameTask.Common.Job;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Helpers.Ui;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using BetterGenshinImpact.View.Windows;
using BetterGenshinImpact.ViewModel.Pages.View;
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
using System.Collections.Specialized;
using WinForms = System.Windows.Forms;
using BetterGenshinImpact.Core.Simulator;
using Vanara.PInvoke;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using static Vanara.PInvoke.User32;
using BetterGenshinImpact.GameTask.AutoWood.Assets;
using System.Threading;
using BetterGenshinImpact.GameTask.AutoGeniusInvokation.Exception;
using BetterGenshinImpact.GameTask.AutoWood.Utils;
using BetterGenshinImpact.GameTask.Common.BgiVision;
using BetterGenshinImpact.GameTask.Model.Area;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.GameTask.Common;
using Rect = OpenCvSharp.Rect;
using System.Windows.Data;
using Grid = System.Windows.Controls.Grid;
using Button = Wpf.Ui.Controls.Button;
using BetterGenshinImpact.View.Pages;
using System.Windows.Media;
using System.Diagnostics;
using Newtonsoft.Json.Linq;
using TextBox = Wpf.Ui.Controls.TextBox;
using System;
using System.Globalization;
using System.Linq;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using BetterGenshinImpact.Core.Recognition;
using BetterGenshinImpact.Core.Simulator;
using BetterGenshinImpact.Core.Simulator.Extensions;
using BetterGenshinImpact.Helpers;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using Microsoft.Extensions.Localization;
using Microsoft.Extensions.Logging;
using static BetterGenshinImpact.GameTask.Common.TaskControl;
using BetterGenshinImpact.GameTask.UseRedeemCode;


namespace BetterGenshinImpact.ViewModel.Pages;

public partial class OneDragonFlowViewModel : ViewModel
{
    private readonly ILogger<OneDragonFlowViewModel> _logger = App.GetLogger<OneDragonFlowViewModel>();

    public static readonly string OneDragonFlowConfigFolder = Global.Absolute(@"User\OneDragon");

    private readonly ScriptService _scriptService;
    
    private readonly ISnackbarService _snackbarService = App.GetService<ISnackbarService>()??
                                                         throw new NullReferenceException("未找到Snackbar服务");
    
    private ScriptGroup _selectedProject;
    
    private ScriptControlViewModel _scriptControlViewModel;
    
    private readonly BlessingOfTheWelkinMoonTask _blessingOfTheWelkinMoonTask = new();
    private readonly AutoRedeemCodeChecker _autoRedeemCodeChecker = new();

    [ObservableProperty] private ObservableCollection<OneDragonTaskItem> _taskList =
    [
        new("领取邮件"),
        new("合成树脂"),
        // new ("每日委托"),
        new("自动秘境"),
        new ("自动首领讨伐"),
        new ("自动幽境危战"),
        new ("自动地脉花"),
        new("领取每日奖励"),
        new ("领取尘歌壶奖励"),
        // new ("自动七圣召唤"),
    ];
    
    //更新右上角的任务列表
    public ICollectionView FilteredConfigList { get; }

    private bool FilterLogic(object item)
    {
        if(_isLoading) return false;
        _isLoading = true;
        if (item is OneDragonFlowConfig config)
        {
            SelectedConfig = config;
            OnConfigDropDownChanged();
            _isLoading = false;
            return config.ScheduleName == Config.SelectedOneDragonFlowPlanName;
        }
        _isLoading = false;
        return false;
    }
    
    public void RefreshFilteredConfigList()
    {
        FilteredConfigList.Filter = FilterLogic;
        FilteredConfigList.Refresh();
    }

    [ObservableProperty] private OneDragonTaskItem? _selectedTask;

    partial void OnSelectedTaskChanged(OneDragonTaskItem value)
    {
        if (value != null)
        {
            InputScriptGroupName = value.Index;
        }
    }

    // 其他属性和方法...
    [ObservableProperty] private int _inputScriptGroupName = 1;

    [ObservableProperty]
    private ObservableCollection<OneDragonTaskItem> _playTaskList = new ObservableCollection<OneDragonTaskItem>();

    [ObservableProperty]
    private ObservableCollection<ScriptGroup> _scriptGroups = new ObservableCollection<ScriptGroup>();
    
    [ObservableProperty] private ObservableCollection<ScriptGroup> _scriptGroupsDefault =
        new ObservableCollection<ScriptGroup>()
        {
            new() { Name = "领取邮件" },
            new() { Name = "合成树脂" },
            new() { Name = "自动秘境" },
            new() { Name = "自动首领讨伐" },
            new() { Name = "自动幽境危战" },
            new() { Name = "自动地脉花" },
            new() { Name = "领取每日奖励" },
            new() { Name = "领取尘歌壶奖励" },
        };

    private readonly string _scriptGroupPath = Global.Absolute(@"User\ScriptGroup");
    private readonly string _configPath = Global.Absolute(@"User\OneDragon");
    private readonly string _basePath = AppDomain.CurrentDomain.BaseDirectory;
    
   public void ReadScriptGroup()
    {
        try
        {
            if (!Directory.Exists(_scriptGroupPath))
            {
                Directory.CreateDirectory(_scriptGroupPath);
            }

            ScriptGroups.Clear();
            var files = Directory.GetFiles(_scriptGroupPath, "*.json");
            List<ScriptGroup> groups = [];
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

                    if (group.Name == TaskContext.Instance().Config.NextScriptGroupName)
                    {
                        group.NextFlag = true;
                    }
                    groups.Add(group);
                }
                catch (Exception e)
                {
                    _logger.LogDebug(e, "读取单个配置组配置时失败");
                    _snackbarService.Show(
                        "读取配置组配置失败",
                        "读取配置组配置失败:" + e.Message,
                        ControlAppearance.Danger,
                        null,
                        TimeSpan.FromSeconds(3)
                    );
                }
            }

            // 按index排序
            groups.Sort((a, b) => a.Index.CompareTo(b.Index));
            foreach (var group in groups)
            {
                ScriptGroups.Add(group);
            }
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "读取配置组配置时失败");
            _snackbarService.Show(
                "读取配置组配置失败",
                "读取配置组配置失败！",
                ControlAppearance.Danger,
                null,
                TimeSpan.FromSeconds(3)
            );
        }
    }

    private async void AddNewTaskGroup()
    {
        if (TaskList.Count >= 999)
        {
            Toast.Warning("任务数量已达上限 999 个，请删除部分任务后再添加");
            return;
        }
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
                IsEnabled = true,
                Index = FindNextAvailableIndex(),
            };

            var names = selectedGroupName.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(name => name.Trim())
                .ToList();// 用于存储所有选中的项
            bool containsAnyDefaultGroup =
                names.Any(name => ScriptGroupsDefault.Any(defaultSg => defaultSg.Name == name));// 判断是否包含默认组
            if (containsAnyDefaultGroup)//如果包含默认组，则插入到默认组后面
            {
                int lastDefaultGroupIndex = -1;
                for (int i = TaskList.Count - 1; i >= 0; i--)
                {
                    if (ScriptGroupsDefault.Any(defaultSg => defaultSg.Name == TaskList[i].Name))
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
        if (pickTaskCount > 1)
        {
                Toast.Success(pickTaskCount + " 个任务添加成功");  
        }
    }
    
    //新增办法，生成任务序号
    private int FindNextAvailableIndex()
    {
        var usedIndices = TaskList.Select(task => task.Index).ToHashSet();
        for (int i = 1; i <= 999; i++)
        {
            if (!usedIndices.Contains(i))
            {
                return i;
            }
        }
        return -1;
    }

    //自动秘境树脂使用优先级
    [RelayCommand]
     private async Task<string> OnResinUsageSequenceAsync()
    {
        var resinDialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "自动秘境领奖树脂配置（使用顺序从上往下）",
            Content = "请设置每种树脂的使用数量",
            CloseButtonText = "关闭",
            Owner = Application.Current.ShutdownMode == ShutdownMode.OnMainWindowClose
                ? null
                : Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Width, 
            MinWidth = 350,
            MinHeight = 350,
            MaxWidth =  350,
            MaxHeight = 350,
        };
        Wpf.Ui.Controls.Grid grid = new Wpf.Ui.Controls.Grid();
        {
            grid.HorizontalAlignment = HorizontalAlignment.Stretch;
            grid.VerticalAlignment = VerticalAlignment.Stretch;
            grid.Margin = new Thickness(10, 0, 0, 0);//
        }
        grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto }); // 第一列：树脂类型
        grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto }); // 第二列：按钮
        grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto}); // 第三列：输入框
        grid.ColumnDefinitions.Add(new ColumnDefinition() { Width = GridLength.Auto }); // 第四列：按钮
        
        grid.RowDefinitions.Add(new RowDefinition()); // 第一行：浓缩树脂
        grid.RowDefinitions.Add(new RowDefinition()); // 第二行：原粹树脂
        grid.RowDefinitions.Add(new RowDefinition()); // 第三行：须臾树脂
        grid.RowDefinitions.Add(new RowDefinition()); // 第四行：脆弱树脂
        grid.RowDefinitions.Add(new RowDefinition()); // 使能按键
        
        string[] resinTypes = { "浓缩树脂", "原粹树脂", "须臾树脂", "脆弱树脂" };
        Dictionary<string, Wpf.Ui.Controls.TextBox> resinInputs = new Dictionary<string, Wpf.Ui.Controls.TextBox>();

        for (int i = 0; i < resinTypes.Length; i++)
        {
            // 树脂类型
            TextBlock textBlock = new TextBlock
            {
                Text = resinTypes[i],
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            };
            Wpf.Ui.Controls.Grid.SetRow(textBlock, i);
            Wpf.Ui.Controls.Grid.SetColumn(textBlock, 0);
            grid.Children.Add(textBlock);

            // 添加按钮“+”
            Button increaseButton = new Button
            {
                Content = "+",
                Width = 40,
                IsEnabled = SelectedConfig.SpecifyResinUse,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0),
            };
            Wpf.Ui.Controls.Grid.SetRow(increaseButton, i);
            Wpf.Ui.Controls.Grid.SetColumn(increaseButton, 1);
            grid.Children.Add(increaseButton);
            
            // 使用局部变量捕获当前的 i 值
            int localIndex = i;
            // 添加输入框
            TextBox textBox = new TextBox
            {
                Text = SelectedConfig.ResinCount.Keys.Contains(resinTypes[i]) 
                    ? SelectedConfig.ResinCount[resinTypes[i]].ToString() 
                    : "0",
                MinWidth =  80,
                IsEnabled = SelectedConfig.SpecifyResinUse,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                TextAlignment = TextAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0)
            };
            Wpf.Ui.Controls.Grid.SetRow(textBox, i);
            Wpf.Ui.Controls.Grid.SetColumn(textBox, 2);
            grid.Children.Add(textBox);
            textBox.TextChanged += (sender, e) =>
            {
                if (int.TryParse(textBox.Text, out int value) && value >= 0 && value <= 99)
                {
                    SelectedConfig.ResinCount[resinTypes[localIndex]] = value;
                    WriteConfig(SelectedConfig);
                    Toast.Information($"当前{resinTypes[localIndex]}数量: {value}");
                }
                else
                {
                    Toast.Warning($"{resinTypes[localIndex]} 的输入无效，请输入非负整数且不超过99");
                    textBox.Text =  "" ; 
                }
            };
            increaseButton.Click += (sender, e) =>
            {
                if (SelectedConfig.ResinCount.ContainsKey(resinTypes[localIndex]) && 
                    SelectedConfig.ResinCount[resinTypes[localIndex]] < 99)
                {
                    SelectedConfig.ResinCount[resinTypes[localIndex]] += 1;
                    if (SelectedConfig.ResinCount[resinTypes[localIndex]] > 99)
                    {
                        SelectedConfig.ResinCount[resinTypes[localIndex]] = 99;
                    }
                    WriteConfig(SelectedConfig);
                    textBox.Text = SelectedConfig.ResinCount[resinTypes[localIndex]].ToString();
                    Toast.Information($"当前{resinTypes[localIndex]}数量: {SelectedConfig.ResinCount[resinTypes[localIndex]]}");
                }
                else
                {
                    Toast.Warning($"{resinTypes[localIndex]} 的数量不能大于99");
                }
            };
            
            // 添加按钮“-”
            Button decreaseButton = new Button
            {
                Content = "-",
                Width = 40,
                IsEnabled = SelectedConfig.SpecifyResinUse,
                VerticalAlignment = VerticalAlignment.Center,
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 0, 0, 0),
            };  
            Wpf.Ui.Controls.Grid.SetRow(decreaseButton, i);
            Wpf.Ui.Controls.Grid.SetColumn(decreaseButton, 3);
            grid.Children.Add(decreaseButton);
            // 使用局部变量捕获当前的 i 值
            decreaseButton.Click += (sender, e) =>
            {
                if (SelectedConfig.ResinCount.ContainsKey(resinTypes[localIndex]) && 
                    SelectedConfig.ResinCount[resinTypes[localIndex]] > 0)
                {
                    SelectedConfig.ResinCount[resinTypes[localIndex]] -= 1;
                    if (SelectedConfig.ResinCount[resinTypes[localIndex]] < 0)
                    {
                        SelectedConfig.ResinCount[resinTypes[localIndex]] = 0;
                    }
                    WriteConfig(SelectedConfig);
                    textBox.Text = SelectedConfig.ResinCount[resinTypes[localIndex]].ToString();
                    Toast.Information($"当前{resinTypes[localIndex]}数量: {SelectedConfig.ResinCount[resinTypes[localIndex]]}");
                }
                else
                {
                    Toast.Warning($"{resinTypes[localIndex]} 的数量不能小于0");
                }
               
            };
            
            //添加细线
            var separator = new Wpf.Ui.Controls.Border
            {
                BorderBrush = new SolidColorBrush(Colors.LightGray),
                BorderThickness = new Thickness(0, 1, 0, 0),
                Margin = new Thickness(0, 0, 0, 0),
                Opacity = 0.2
            };
            Wpf.Ui.Controls.Grid.SetRow(separator, i+1);
            Wpf.Ui.Controls.Grid.SetColumn(separator, 0);
            Wpf.Ui.Controls.Grid.SetColumnSpan(separator, 4); 
            if (i == resinTypes.Length - 1)
            {
                separator.Visibility = Visibility.Collapsed;
            }
            
            grid.Children.Add(separator);
            resinInputs[resinTypes[i]] = textBox;
        }
        
        // 使能按键
        var enableButton = new Button
        {
            Content = SelectedConfig.SpecifyResinUse ? "自定义模式：按上述配置使用树脂类型和数量" : "耗尽模式：先用浓缩，再用原粹，其他不使用",
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 0)
        };
        Wpf.Ui.Controls.Grid.SetRow(enableButton, resinTypes.Length);
        Wpf.Ui.Controls.Grid.SetColumn(enableButton, 0);
        Wpf.Ui.Controls.Grid.SetColumnSpan(enableButton, 4);
        enableButton.Click += (sender, e) =>
        {
            SelectedConfig.SpecifyResinUse = !SelectedConfig.SpecifyResinUse;
            enableButton.Content = SelectedConfig.SpecifyResinUse ? "自定义模式：按上述配置使用树脂类型和数量" : "耗尽模式：先用浓缩，再用原粹，其他不使用";
            Toast.Information(SelectedConfig.SpecifyResinUse ? "自定义模式：按上述配置使用树脂类型和数量" : "耗尽模式：先用浓缩，再用原粹，其他不使用");
            foreach (var input in resinInputs.Values)
            {
                input.IsEnabled = SelectedConfig.SpecifyResinUse;// 根据使能状态启用或禁用输入框
                var increaseButton = grid.Children.OfType<Button>().FirstOrDefault(b => b.Content.ToString() == 
                    "+" && Wpf.Ui.Controls.Grid.GetRow(b) == Wpf.Ui.Controls.Grid.GetRow(input));
                if (increaseButton != null)
                {
                    increaseButton.IsEnabled = SelectedConfig.SpecifyResinUse;
                }
                var decreaseButton = grid.Children.OfType<Button>().FirstOrDefault(b => b.Content.ToString() == 
                    "-" && Wpf.Ui.Controls.Grid.GetRow(b) == Wpf.Ui.Controls.Grid.GetRow(input));
                if (decreaseButton != null)
                {
                    decreaseButton.IsEnabled = SelectedConfig.SpecifyResinUse;
                }
            }
        };
        grid.Children.Add(enableButton);
        resinDialog.Content = grid;
        
        var result = await resinDialog.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.None)
        {
            foreach (var resinType in resinInputs.Keys)// 遍历每种树脂类型
            {
                if (string.IsNullOrEmpty(resinInputs[resinType].Text)) // 如果输入框为空，则设置为0
                {
                    resinInputs[resinType].Text = "0";
                    SelectedConfig.ResinCount[resinType] = 0; // 确保字典中有该键
                }
                if (!int.TryParse(resinInputs[resinType].Text, out int value) || value < 0 || value > 99) // 如果输入框不是整数或超出范围，则弹窗提示并返回
                {
                    Toast.Warning($"{resinType} 的输入无效，请输入非负整数且不超过99");
                    return await OnResinUsageSequenceAsync();
                }
            }
            
            if ((SelectedConfig.ResinCount.Values.All(count => count == 0) || SelectedConfig.ResinCount.Count == 0) && SelectedConfig.SpecifyResinUse)
            {
                Toast.Warning("请至少要有一种树脂的数量大于0");
                return await OnResinUsageSequenceAsync();
            }
            
        }
        
        string resinUsageSequence = string.Join(", ",
            resinInputs.Select(kvp => $"{kvp.Key}: {kvp.Value.Text}"));
        Toast.Information(SelectedConfig.SpecifyResinUse
                                                    ? $"树脂使用配置: {resinUsageSequence}"
                                                    : "树脂使用配置: 耗尽模式，先用浓缩，再用原粹，其他不使用");
        return resinUsageSequence;
    }

    public async Task<string?> OnStartMultiScriptGroupAsync()
    {
        var stackPanel = new StackPanel();
        var checkBoxes = new Dictionary<ScriptGroup, CheckBox>();
        CheckBox selectedCheckBox = null; // 用于保存当前选中的 CheckBox
        foreach (var scriptGroup in ScriptGroupsDefault)
        {
            var checkBox = new CheckBox
            {
                Content = scriptGroup.Name,
                Tag = scriptGroup,
                IsChecked = false
            };
            checkBoxes[scriptGroup] = checkBox;
            stackPanel.Children.Add(checkBox);
        }
        foreach (var scriptGroup in ScriptGroups)
        {
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
        uiMessageBox.SourceInitialized += (s, e) => WindowHelper.TryApplySystemBackdrop(uiMessageBox);
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
    
    [ObservableProperty] private ObservableCollection<OneDragonFlowConfig> _configList = [];
    /// <summary>
    /// 当前生效配置
    /// </summary>
  
    [ObservableProperty] private OneDragonFlowConfig? _selectedConfig;
    
    [ObservableProperty] 
    private OneDragonFlowConfig _selectedConfigCache;
    partial void OnSelectedConfigChanged(OneDragonFlowConfig? value)
    {
        if (_isLoading) return;
        if (!string.IsNullOrEmpty(value.Name))
        {
            _selectedConfigCache = value;
            InitializeDomainNameList();
        }
        _isLoading = false;
    }

    [ObservableProperty] private List<string> _craftingBenchCountry = ["枫丹", "稻妻", "璃月", "蒙德"];

    [ObservableProperty] private List<string> _adventurersGuildCountry = ["挪德卡莱", "枫丹", "稻妻", "璃月", "蒙德"];

    [ObservableProperty] private List<string> _domainNameList = ["", ..MapLazyAssets.Get().DomainNameList];

    [ObservableProperty] private List<string> _completionActionList = ["无", "关闭游戏", "关闭软件", "关闭游戏和软件", "关机"];

    [ObservableProperty] private List<string> _sundayEverySelectedValueList = ["","1", "2", "3"];
    
    [ObservableProperty] private List<string> _sundaySelectedValueList = ["","1", "2", "3"];

    [ObservableProperty] private List<string> _secretTreasureObjectList = ["布匹","须臾树脂","大英雄的经验","流浪者的经验","精锻用魔矿","摩拉","祝圣精华","祝圣油膏"];

    [ObservableProperty] private List<string> _sereniteaPotTpTypes = ["地图传送", "尘歌壶道具"];

    [ObservableProperty] private AutoFightViewModel? _autoFightViewModel;
    
    private string _lastUid = ""; // 上一次切换的UID
    
    [ObservableProperty] private List<string> _customDomainList;
    
    public AllConfig Config { get; set; } = TaskContext.Instance().Config;
    
     public void InitializeDomainNameList()
    {
        if (_isLoading) return;
        if (SelectedConfig != null)
        {
            DomainNameList.Clear();
            
            DomainNameList.AddRange(
                new List<string> { ""}
            );
            
            DomainNameList.AddRange(
                SelectedConfig.CustomDomainList
                    .Where(domain => !DomainNameList.Contains(domain)));
            
            DomainNameList.AddRange(
                    MapLazyAssets.Get().DomainNameList
                    .Where(domain => !DomainNameList.Contains(domain))
            );
        }
    }
    
    public  OneDragonFlowViewModel()
    {
        AutoFightViewModel = new AutoFightViewModel(Config);

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
        if(_autoRun) AdaptVersions();//自动适配版本，一两个大版本后可以注释掉，后续有改动再用
        _customDomainList = new List<string>(); // 或者根据需要进行初始化
        InitializeDomainNameList();
    }
    
    [RelayCommand]
    public async Task ScriptControlPageAsync(ScriptGroup? selectedProject = null)
    {
        ReadScriptGroup();

        if (selectedProject == null)
        {
            _selectedProject = ScriptGroups.FirstOrDefault(sg => sg.Name == SelectedTask.Name) ??
                               ScriptGroups.FirstOrDefault() ?? null;
        }
        else
        {
            _selectedProject = selectedProject;
        }

        _scriptControlViewModel = new ScriptControlViewModel( _snackbarService, 
            _scriptService,ScriptGroups,_selectedProject,true);
        
        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "配置组管理",
            Content = new ScrollViewer
            {
                Content = new ScriptControlPage(_scriptControlViewModel),
                VerticalScrollBarVisibility = ScrollBarVisibility.Disabled,
            },
            CloseButtonText = "关闭",
            Owner = Application.Current.ShutdownMode == ShutdownMode.OnMainWindowClose ? null : Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Height, 
            MinWidth = 700,
            MinHeight = 500,
            MaxHeight = 650,
            Topmost = false,
        };
        var result =await dialog.ShowDialogAsync();
        if (result == Wpf.Ui.Controls.MessageBoxResult.None)
        {
            _scriptControlViewModel.ScriptGroupsCollectionChanged(ScriptGroups,
                new NotifyCollectionChangedEventArgs(NotifyCollectionChangedAction.Reset)); //关闭窗口后保存配置信息
            InitConfigList();
        }
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
        var restoreButton = new Button { Content = "生成USER文件夹到公版BGI", Margin = new Thickness(0, 0, 0, 0) }; // 新增按钮
        var backupButton = new Button { Content = "备份User", Margin = new Thickness(10, 0, 0, 0) };
        var openUserFolderButton = new Button { Content = "打开User", Margin = new Thickness(10, 0, 0, 0) };
        
        var buttonPanel = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Children = { newButton, deleteButton }
        };
        
        var buttonPanel2 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Children = {restoreButton }
        };
        
        var buttonPanel3 = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Stretch,
            VerticalAlignment = VerticalAlignment.Center,
            Margin = new Thickness(0, 10, 0, 0),
            Children = {backupButton ,openUserFolderButton}
        };
        
        openUserFolderButton.Click += (s, e) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "User",
                UseShellExecute = true
            });
        };

        var grid = new Grid();
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 标题
        grid.RowDefinitions.Add(new RowDefinition { Height = new GridLength(1, GridUnitType.Star) }); // 列表
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 按钮
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 按钮
        grid.RowDefinitions.Add(new RowDefinition { Height = GridLength.Auto }); // 按钮
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
        grid.Children.Add(buttonPanel2);
        Grid.SetRow(buttonPanel2, 3);
        grid.Children.Add(buttonPanel3);
        Grid.SetRow(buttonPanel3, 4);

        var messageBox = new Wpf.Ui.Controls.MessageBox
        {
            Title = "配置单编辑器",
            Content = grid,
            CloseButtonText = "取消",
            PrimaryButtonText = "确认",
            Owner = Application.Current.MainWindow,
            WindowStartupLocation = WindowStartupLocation.CenterOwner,
            SizeToContent = SizeToContent.Width,
            MinWidth = 220,
            MaxWidth = 220,
            Height = 700,
            MaxHeight = 700
        };
        
        openUserFolderButton.Click += (s, e) =>
        {
            Process.Start(new ProcessStartInfo
            {
                FileName = "User",
                UseShellExecute = true
            });
        };
        
        restoreButton.Click += (sender, args) =>
        {
            var result = MessageBox.Show("生成公版BGI配置文件到 NewToOldUser 文件夹！确定？", "确认生成", System.Windows.MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result == System.Windows.MessageBoxResult .OK)
            {
                RestoreOldVersions();
            }
        };
        backupButton.Click += (sender, args) =>
        {
            var result = MessageBox.Show("备份整个User文件夹到 Backups 文件夹！您确定要备份User文件夹吗？", "确认备份", System.Windows.MessageBoxButton.OKCancel, MessageBoxImage.Question);
            if (result == System.Windows.MessageBoxResult.OK)
            {
                BackupUser();
            }
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
              var selectedCopy = selected;// 保存选择的计划表名称
              if (ConfigList.Any(c => c.ScheduleName == selected))
                {
                    if (MessageBox.Show($"计划表 \"{selected}\" " +
                                        $"下有配置单，配置单将移至 <默认计划表> 中，确定要删除计划表 \"{selected}\" ？", 
                            "删除计划表", System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes)
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
                    if (MessageBox.Show($"计划表 \"{selected}\" 下没有配置单，可以直接删除。", "删除计划表",
                            System.Windows.MessageBoxButton.YesNo) == System.Windows.MessageBoxResult.Yes)
                    {
                        Config.ScheduleList = new ObservableCollection<string>(Config.ScheduleList.Where(x => x != selected));
                        Toast.Success($"计划表 \"{selected}\" 删除成功！");
                        listBox.ItemsSource = Config.ScheduleList;
                        listBox.SelectedItem = "默认计划表";
                    }
                }
                if (selectedCopy == Config.SelectedOneDragonFlowPlanName)
                {
                    Config.SelectedOneDragonFlowPlanName = "默认计划表";
                }
                RefreshFilteredConfigList();
                var lastConfig = ConfigList.LastOrDefault(c => c.ScheduleName 
                                                               == Config.SelectedOneDragonFlowPlanName);
                if (lastConfig != null)
                {
                    Toast.Warning($"计划表 \"{Config.SelectedOneDragonFlowPlanName}\" 下有配置单，将自动选择最后一条配置单");
                    SelectedConfig = lastConfig;
                    OnConfigDropDownChanged();
                }
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
          var firstConfig = ConfigList.FirstOrDefault(c => c.ScheduleName == selectedPlan);
          if (firstConfig != null)
          {
              SelectedConfig = firstConfig;
              OnConfigDropDownChanged();
          }
      }
    }
    
    [RelayCommand]
    private void AddScheduleItem()
    {
        var newScheduleName = PromptDialog.Prompt("请输入新的计划表名称", "新增计划表");
        
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
                var result = MessageBox.Show($"确定要删除 {configName} 配置单吗？删除后无法恢复！", 
                    "确认删除", System.Windows.MessageBoxButton.YesNo, MessageBoxImage.Warning);
                
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
                            var nextConfig = ConfigList.LastOrDefault(c => c.ScheduleName 
                                                                           == Config.SelectedOneDragonFlowPlanName);
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
                            var configs = ConfigList.Where(c => c.ScheduleName 
                                                                == Config.SelectedOneDragonFlowPlanName).ToList();
                            for (int i = 0; i < configs.Count; i++)
                            {
                                configs[i].IndexId = i + 1;
                            }
                            var lastConfig = ConfigList.LastOrDefault(c => c.ScheduleName 
                                                                           == Config.SelectedOneDragonFlowPlanName);
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
                    newConfig.NextConfiguration = false; // 复制的配置单不作为下一配置单
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
            
            Config.ScheduleList = new ObservableCollection<string>(Config.ScheduleList.Where
                (schedule => schedule != selectedSchedule));

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
            var json = File.ReadAllText(configFile);// 读取配置文件内容
            var config = JsonConvert.DeserializeObject<OneDragonFlowConfig>(json);// 反序列化配置
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
                    Name = "默认配置",
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
    
    private void LoadDisplayTaskListFromConfig()
    {
        if (string.IsNullOrEmpty(SelectedConfig.Name) || string.IsNullOrEmpty(SelectedConfig.TaskEnabledList.ToString())) 
        {
            return;
        }
        TaskList.Clear();
        
        foreach (var kvp in SelectedConfig.TaskEnabledList)
        {
            var taskItem = new OneDragonTaskItem(kvp.Key, kvp.Value.Item1, kvp.Value.Item2)
            {
                IsEnabled = kvp.Value.Item1,
                IsNextTask = kvp.Key == SelectedConfig.NextTaskIndex,
            };
            TaskList.Add(taskItem);
        }
        // SaveConfig();
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
            var taskItem = new OneDragonTaskItem(kvp.Key, kvp.Value.Item1, kvp.Value.Item2)
            {
                Name = kvp.Value.Item2,
                Index = kvp.Key,
                IsEnabled = kvp.Value.Item1
            };
            if (kvp.Key != InputScriptGroupName)
            {
                TaskList.Add(taskItem);
            }
        }
    }
    

    [RelayCommand]
    private void OnConfigDropDownChanged()
    {
        if (_isLoading) return;
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
        if (_autoRun)return;
        
        if (string.IsNullOrEmpty(SelectedConfig.Name) || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            return;
        }

        SelectedConfig.TaskEnabledList.Clear();
        foreach (var task in TaskList)
        { 
            SelectedConfig.TaskEnabledList.Add(task.Index, (task.IsEnabled, task.Name));
        }
        WriteConfig(SelectedConfig);
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
        SelectedTask = TaskList.LastOrDefault();
    }

    [RelayCommand]
    private void OnStrategyDropDownOpened(string type)
    {
        AutoFightViewModel?.OnStrategyDropDownOpened(type);
    }
    
    public void SetSomeSelectedConfig(OneDragonFlowConfig? selected)
    {
        if (SelectedConfig != null)
        {
            TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = SelectedConfig.Name;
            foreach (var task in TaskList)
            {
                if (SelectedConfig.TaskEnabledList.TryGetValue(task.Index, out var taskStatus))
                {
                    task.IsEnabled = taskStatus.Item1;
                }
            }
            LoadDisplayTaskListFromConfig();
        }
    }
    
    public bool _isLoading = false;
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
                Toast.Warning("计划切换中！");
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
            if (SelectedConfig != null && SelectedConfig.ScheduleName == Config.SelectedOneDragonFlowPlanName)
            {
                SaveConfig();
            } 
            else
            {
                Toast.Warning("计划切换中！！");
            }
        }
        finally
        {
            if (SelectedConfig != null &&e.PropertyName == nameof(OneDragonFlowConfig.ScheduleName))
            {
                // 获取与 SelectedConfig.ScheduleName 相同的配置项并按 IndexId 排序
                var configs = ConfigList
                    .Where(c => c.ScheduleName == SelectedConfig?.ScheduleName)
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
            if (!string.IsNullOrEmpty(scheduleName) && !Config.ScheduleList.Contains(scheduleName))
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
                        DayOfWeek.Monday => "周一",
                        DayOfWeek.Tuesday => "周二",
                        DayOfWeek.Wednesday => "周三",
                        DayOfWeek.Thursday => "周四",
                        DayOfWeek.Friday => "周五",
                        DayOfWeek.Saturday => "周六",
                        DayOfWeek.Sunday => "周日",
                        _ => "未知"
                    };
                    //确认配置单是否存在
                    var boundConfigs = ConfigList.Where(config => config.AccountBinding == true 
                                                                && ((config.PeriodList.ContainsKey("每日") && config.PeriodList["每日"]) 
                                                                    || (config.PeriodList.ContainsKey(todayNow) && config.PeriodList[todayNow])) 
                                                                && config.ScheduleName == Config.SelectedOneDragonFlowPlanName).ToList();
                    
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

    [RelayCommand]
    private void SetNextConfiguration()
    {
        if (SelectedConfig == null || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        InitConfigList();
        //把全部配置单的 NextConfiguration 属性设置为 false
        foreach (var config in ConfigList)
        {
            if (config.NextConfiguration)
            {
                config.NextConfiguration = false;
                WriteConfig(config);
            }
        }
        SelectedConfig.NextConfiguration = true;
        Toast.Success( $"配置单 {SelectedConfig.Name} 已设置为下一条配置单");
    }
    
    //清除所有配置单的下一条配置单标记
    [RelayCommand]
    private void ClearNextConfiguration()
    {
        if (SelectedConfig == null || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        InitConfigList();
        //把全部配置单的 NextConfiguration 属性设置为 false
        foreach (var config in ConfigList)
        {
            if (config.NextConfiguration)
            {
                config.NextConfiguration = false;
                WriteConfig(config);
            }
        }
        Toast.Success("已清除所有配置单的下一条配置单标记");
    }
    
    [RelayCommand]
    private void SetAccountBindingCodeSwitchButton()
    {
        Toast.Success("11111...");
        if (SelectedConfig == null)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        if (!SelectedConfig.AccountBinding)
        {
            Toast.Success("账号绑定码已关闭");
            return;
        }
        Toast.Success("正在设置账号绑定码...");
        if (string.IsNullOrEmpty(SelectedConfig.AccountBindingCode) && !string.IsNullOrEmpty(SelectedConfig.GenshinUid))
        {
            //弹窗设置账号绑定码SelectedConfig.AccountBindingCode
            var bindingCode = PromptDialog.Prompt("请输入账号绑定码", "账号绑定码设置");
            if (string.IsNullOrEmpty(bindingCode))
            {
                return;
            }
            SelectedConfig.AccountBindingCode = bindingCode;
            Toast.Success($"UID: {SelectedConfig.GenshinUid} 已经绑定 {SelectedConfig.AccountBindingCode}");
        }
        else
        {
            Toast.Success($"UID {SelectedConfig.GenshinUid} 绑定码为 {SelectedConfig.AccountBindingCode}");
        }
    }

    [RelayCommand]
    private void SetCycleTimeButton()
    {
        SetCycleTime(true);
    }
    
    [RelayCommand]
    private  void SetCycleTimeSwitchButton()
    {
        SetCycleTime(false);
    }

    private void SetCycleTime(bool isChecked = false)
    {

        if (!Config.ScheduleLoop && !isChecked)
        {
            return;
        } 
        
        var line1 = new Separator
        {
            Margin = new Thickness(0, 0, 0, 5)
        };
        var line2 = new Separator
        {
            Margin = new Thickness(0, 0, 0, 5)
        };
        var line3 = new Separator
        {
            Margin = new Thickness(0, 0, 0, 5)
        };
        var startTimeHourTextBox = new TextBox
        {
            Name = "StartTimeHourTextBox",
            Text = Config.ScheduleStartTime.Split(':')[0],
            Width = 100,
            IsEnabled = Config.ScheduleStartOnTime,
            Margin = new Thickness(0, 0, 0, 5) // 添加一些间距
        };
        startTimeHourTextBox.SetBinding(TextBox.IsEnabledProperty, new Binding("ScheduleStartOnTime") { Source = Config });

        var startTimeMinuteTextBox = new TextBox
        {
            Name = "StartTimeMinuteTextBox",
            Text = Config.ScheduleStartTime.Split(':')[1],
            IsEnabled = Config.ScheduleStartOnTime,
            Width = 100,
            Margin = new Thickness(5, 0, 0, 5) // 添加一些间距
        };
        startTimeMinuteTextBox.SetBinding(TextBox.IsEnabledProperty, new Binding("ScheduleStartOnTime") { Source = Config });

        var checkBox0 = new CheckBox
        {
            Name = "ScheduleLoopSkipCheckBox",
            Content = "执行计划表一天后跳出当前循环",
            IsChecked = Config.ScheduleLoopSkip,
            IsEnabled = Config.ScheduleLoop,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 10) // 添加一些间距
        };
        checkBox0.Checked += (s, e) => Config.ScheduleLoopSkip = true;
        checkBox0.Unchecked += (s, e) => Config.ScheduleLoopSkip = false;

        var checkBox = new CheckBox
        {
            Name = "ScheduleLoopCheckBox",
            Content = "指定等待到以下的时间循环执行",
            IsChecked = Config.CycleMode,
            IsEnabled = Config.ScheduleLoop,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 5) // 添加一些间距
        };

        checkBox.Checked += (s, e) => Config.CycleMode = true;
        checkBox.Unchecked += (s, e) => Config.CycleMode = false;

        // 弹窗设定执行完后等待到什么时候再继续循环执行
        var hourTextBox = new TextBox
        {
            Name = "HourTextBox",
            Text = Config.CycleTime.Split(':')[0],
            Width = 100,
            IsEnabled = Config.CycleMode,
            Margin = new Thickness(0, 0, 0, 5) // 添加一些间距
        };
        hourTextBox.SetBinding(TextBox.IsEnabledProperty, new Binding("CycleMode") { Source = Config });

        var minuteTextBox = new TextBox
        {
            Name = "MinuteTextBox",
            Text = Config.CycleTime.Split(':')[1],
            IsEnabled = Config.CycleMode,
            Margin = new Thickness(0, 0, 0, 5),
            Width = 100
        };
        minuteTextBox.SetBinding(TextBox.IsEnabledProperty, new Binding("CycleMode") { Source = Config });

        var startTimeCheckBox = new CheckBox
        {
            Name = "StartTimeCheckBox",
            Content = "设定下方的时间定时启动计划表",
            IsChecked = Config.ScheduleStartOnTime,
            IsEnabled = true,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(0, 0, 0, 5) // 添加一些间距
        };
        startTimeCheckBox.Checked += (s, e) =>
        {
            Config.ScheduleStartOnTime = true;
            startTimeHourTextBox.IsEnabled = true;
            startTimeMinuteTextBox.IsEnabled = true;
        };
        startTimeCheckBox.Unchecked += (s, e) =>
        {
            Config.ScheduleStartOnTime = false;
            startTimeHourTextBox.IsEnabled = false;
            startTimeMinuteTextBox.IsEnabled = false;
        };

        var dialog = new Wpf.Ui.Controls.MessageBox
        {
            Title = "循环配置",
            Content = new StackPanel
            {
                Children =
                {
                    
                    startTimeCheckBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        HorizontalAlignment = HorizontalAlignment.Center, // 确保子 StackPanel 内容居中
                        Margin = new Thickness(0, 0, 0, 10),
                        Children =
                        {
                            startTimeHourTextBox,
                            new TextBlock { Text = " ：", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) },
                            startTimeMinuteTextBox,
                        }
                    },
                    line1,
                    checkBox0,
                    line2,
                    checkBox,
                    new StackPanel
                    {
                        Orientation = Orientation.Horizontal,
                        Margin = new Thickness(0, 0, 0, 5),
                        HorizontalAlignment = HorizontalAlignment.Center, // 确保子 StackPanel 内容居中
                        Children =
                        {
                            hourTextBox,
                            new TextBlock { Text = " ：", VerticalAlignment = VerticalAlignment.Center, HorizontalAlignment = HorizontalAlignment.Center, Margin = new Thickness(5, 0, 0, 0) },
                            minuteTextBox,
                        }
                    },
                }
            },
            PrimaryButtonText = "确定",
            CloseButtonText = "取消",
            Height = 360,
            Owner = Application.Current.MainWindow, // 设置 Owner 确保弹窗与主窗口关联
            WindowStartupLocation = WindowStartupLocation.CenterOwner, // 确保弹窗居中显示
            SizeToContent = SizeToContent.Width // 根据内容自动调整大小
        };

        var result = dialog.ShowDialogAsync().Result;

        if (result == Wpf.Ui.Controls.MessageBoxResult.Primary)
        {
            if (int.TryParse(hourTextBox.Text, out int hour) && hour >= 0 && hour <= 23 &&
                int.TryParse(minuteTextBox.Text, out int minute) && minute >= 0 && minute <= 59)
            {
                Config.CycleTime = $"{hour:D2}:{minute:D2}";
                Toast.Success(Config.CycleMode ? $"已设定<循环>时间为 {Config.CycleTime}" : "<直接>循环模式", ToastLocation.TopCenter, new Thickness(0, 20, 0, 0), 8000);
            }
            else
            {
                if (Config.CycleMode)
                {
                    Toast.Warning("请输入正确的时间（例如04:00）");
                }
            }

            if (Config.ScheduleStartOnTime)
            {
                if (int.TryParse(startTimeHourTextBox.Text, out int startTimeHour) && startTimeHour >= 0 && startTimeHour <= 23 &&
                    int.TryParse(startTimeMinuteTextBox.Text, out int startTimeMinute) && startTimeMinute >= 0 && startTimeMinute <= 59)
                {
                    Config.ScheduleStartTime = $"{startTimeHour:D2}:{startTimeMinute:D2}";
                    Toast.Success($"已设定<启动>时间为 {Config.ScheduleStartTime}", ToastLocation.TopCenter, new Thickness(0, 50, 0, 0), 8000);
                }
                else
                {
                    Toast.Warning("请输入正确的启动时间（例如04:00）");
                }
            }
        }
    }

    //根据
    

    //连续执行一条龙配置单
    private bool _continuousExecutionMark = false;
    private int _executionSuccessCount = 0; 
    private bool _finishMark = false;
    private bool _nextModel =false;
    [RelayCommand]
    private async Task OnOneKeyContinuousExecutionOneKey()
    {
        await ScriptService.StartGameTask();
        
        _logger.LogInformation(
            Config.ScheduleLoop 
                ? (Config.CycleMode ? $"连续一条龙：执行结束后，等待到 {Config.CycleTime} 循环执行计划..." : "连续一条龙：执行结束后，直接循环执行计划...") 
                : "连续一条龙：执行计划表一次...");
        Notify.Event(NotificationEvent.DragonStart).Success(
            Config.ScheduleLoop 
                ? (Config.CycleMode ? $"连续一条龙：执行结束后，等待到 {Config.CycleTime} 循环执行计划..." : "连续一条龙：执行结束后，直接循环执行计划...") 
                : "连续一条龙：执行计划表一次...");
        var boundConfigs = new List<OneDragonFlowConfig>();
        //记录任务开始时间
        var startTime = DateTime.Now;
        
        if (Config.ScheduleStartOnTime)
        {
            var startTimeHour = Config.ScheduleStartTime.Split(':')[0];
            var startTimeMinute = Config.ScheduleStartTime.Split(':')[1];
            var now = DateTime.Now;
            var start = new DateTime(now.Year, now.Month, now.Day, int.Parse(startTimeHour), int.Parse(startTimeMinute), 0);
            if (start < now)
            {
                start = start.AddDays(1); // 如果计划表启动时间已过，设置为明天的同一时间
            }
            var delay = start - now;
            await new TaskRunner().RunThreadAsync(async () =>
            {
                Notify.Event(NotificationEvent.DragonStart).Success(
                    $"计划表启动时间：{start.ToString("yyyy-MM-dd HH:mm:ss")}");                
                _logger.LogInformation("连续一条龙：等待到计划表启动时间 {start}，等待时间 {delay}", start, delay.ToString(@"hh\:mm\:ss"));
                await Task.Delay(delay, CancellationContext.Instance.Cts.Token);
            });
            // 如果任务已经被取消，中断所有任务
            if (CancellationContext.Instance.Cts.IsCancellationRequested)
            {
                _continuousExecutionMark = false;// 标记连续执行结束
                _executionSuccessCount = 0;// 重置连续执行成功次数
                _finishMark = false;
                _logger.LogInformation("连续一条龙：任务结束");
                Notify.Event(NotificationEvent.DragonEnd).Success("连续一条龙：任务结束");
                return; // 后续的检查任务也不执行  
            }
        }
        
        _lastUid = "";
        while (Config.ScheduleLoop || _lastUid == "")
        {
            _finishMark = false;
            _continuousExecutionMark = true;
            _executionSuccessCount = 0;
            _lastUid = "";
            _nextModel =false;
            string todayNow = DateTime.Now.DayOfWeek switch
            {
                DayOfWeek.Monday => "周一",
                DayOfWeek.Tuesday => "周二",
                DayOfWeek.Wednesday => "周三",
                DayOfWeek.Thursday => "周四",
                DayOfWeek.Friday => "周五",
                DayOfWeek.Saturday => "周六",
                DayOfWeek.Sunday => "周日",
                _ => "未知"
            };

            InitConfigList();

            boundConfigs = ConfigList.Where(config => config.AccountBinding == true 
                                                      && ((config.PeriodList.ContainsKey("每日") && config.PeriodList["每日"]) 
                                                          || (config.PeriodList.ContainsKey(todayNow) && config.PeriodList[todayNow])) 
                                                      && config.ScheduleName == Config.SelectedOneDragonFlowPlanName).ToList();
            _logger.LogInformation("连续一条龙：今天 {todayNow} ，执行 {ScheduleName} 计划，生效配置单数量 {BoundConfigCount}",
                todayNow,Config.SelectedOneDragonFlowPlanName,boundConfigs.Count);
            
            if (ConfigList.Count == 0 || boundConfigs.Count == 0) 
            {
                Toast.Warning("连续一条龙需绑定UID,请先设定配置单");
                return;
            }
            
            int configIndex = 0;
            foreach (var config in boundConfigs)
            {
                if (config.NextConfiguration)
                {
                    _nextModel = true;
                    break;
                }
            }
            foreach (var config in boundConfigs)
            {
                if (_nextModel){
                    if (config.NextConfiguration == false){
                         _logger.LogInformation("连续一条龙：配置单 {Config.Name} 跳过", config.Name);
                         configIndex++;
                        continue;
                    }
                    config.NextConfiguration = false;
                    WriteConfig(config);
                }
                _nextModel = false;
                
                await Task.Delay(500);
                for (int i = 0; i < 20; i++)
                {
                    if (_finishMark || _executionSuccessCount == 0)
                    {
                        configIndex++;
                        SelectedConfig = config;
                        OnConfigDropDownChanged();
                        break;
                    }

                    if (i == 19)
                    {
                        //报错退出
                        _logger.LogWarning("连续一条龙：执行错误，退出执行...");
                        Notify.Event(NotificationEvent.DragonEnd).Error("连续一条龙：执行错误，退出执行...");
                        throw new Exception("连续一条龙：执行错误，退出执行...");
                    }
                    await Task.Delay(500);
                }
                _finishMark = false;
                
                _logger.LogInformation("正在执行 {ScheduleName} 计划的第 {ConfigIndex} / {boundConfigs.Count} 个配置单：{Config.Name}，绑定UID {Config.GenshinUid}", 
                    Config.SelectedOneDragonFlowPlanName,configIndex,boundConfigs.Count,config.Name, config.GenshinUid);
                Notify.Event(NotificationEvent.DragonEnd).Success(
                    $"正在执行 {Config.SelectedOneDragonFlowPlanName} 计划的第 {configIndex} / {boundConfigs.Count} 个配置单：{config.Name}，绑定UID {config.GenshinUid}");
                
                await Task.Delay(500);
                await OnOneKeyExecute();
                await Task.Delay(500);
                await new ReturnMainUiTask().Start(CancellationToken.None);
                // 如果任务已经被取消，中断所有任务
                if (CancellationContext.Instance.Cts.IsCancellationRequested)
                {
                    _continuousExecutionMark = false;// 标记连续执行结束
                    _executionSuccessCount = 0;// 重置连续执行成功次数
                    _finishMark = false;
                    _logger.LogInformation("连续一条龙：任务结束");
                    Notify.Event(NotificationEvent.DragonEnd).Success("连续一条龙：任务结束");
                    return; // 后续的检查任务也不执行
                }
                //每次完成一个配置单后，检测执行时间是否超过一天，如果超过一天，直接进入下一个循环
                if (DateTime.Now.Subtract(startTime).TotalDays >= 1 && Config.ScheduleLoopSkip)
                {
                    //月卡检测
                    await _blessingOfTheWelkinMoonTask.Start(CancellationContext.Instance.Cts.Token);
                    _logger.LogInformation("计划表执行时间超过一天，直接进入下一个循环");
                    break;
                }
            }
            if (Config.ScheduleLoop)
            {
                _logger.LogInformation(Config.CycleMode ? "连续一条龙：循环执行计划，等待到下一个循环..." : "连续一条龙：直接循环模式，继续执行...");
                Notify.Event(NotificationEvent.DragonEnd).Success(Config.CycleMode ? "连续一条龙：循环执行计划，等待到下一个循环..." : "连续一条龙：直接循环模式，继续执行...");
            }
            // 任务完成后，判断是否为循环执行
            if (Config.ScheduleLoop && Config.CycleMode)
            {
                // 如果是循环执行，等待到指定时间
                var cycleTime = Config.CycleTime.Split(':');
                if (cycleTime.Length == 2 && int.TryParse(cycleTime[0], out int hour) &&
                    int.TryParse(cycleTime[1], out int minute))
                {
                    var now = DateTime.Now;
                    var nextCycleTime = new DateTime(now.Year, now.Month, now.Day, hour, minute, 0);
                    if (nextCycleTime < now)
                    {
                        nextCycleTime = nextCycleTime.AddDays(1); // 如果指定时间已过，设置为明天的同一时间
                    }

                    // 计算任务总执行时间
                    var taskExecutionTime = now - startTime;

                    // 如果任务执行时间超过一天，直接进入下一个循环
                    if (taskExecutionTime.TotalDays >= 1)
                    {
                        _logger.LogInformation("任务执行时间超过一天，直接进入下一个循环");
                        Notify.Event(NotificationEvent.DragonEnd).Success("任务执行时间超过一天，直接进入下一个循环");
                        // 任务执行的代码
                    }
                    else
                    {
                        var delay = nextCycleTime - now;
                        //如果delay大于24小时，说明任务执行时间超过一天，直接进入下一个循环
                        if (!(delay.TotalHours > 24))
                        {
                            await new TaskRunner().RunThreadAsync(async () =>
                            {
                                Notify.Event(NotificationEvent.DragonEnd).Success(
                                    $"计划表下次循环时间：{nextCycleTime.ToString("yyyy-MM-dd HH:mm:ss")}");
                                _logger.LogInformation("连续一条龙：等待到下一个循环时间 {nextCycleTime}，等待时间 {delay}", nextCycleTime, delay.ToString(@"hh\:mm\:ss"));
                                await Task.Delay(delay, CancellationContext.Instance.Cts.Token);
                            }); 
                        }
                        else
                        {
                            _logger.LogInformation("任务执行时间超过一天，直接进入下一个循环-2");
                            Notify.Event(NotificationEvent.DragonEnd).Success("任务执行时间超过一天，直接进入下一个循环");
                        }
                        
                        // 如果任务已经被取消，中断所有任务
                        if (CancellationContext.Instance.Cts.IsCancellationRequested)
                        {
                            _continuousExecutionMark = false;// 标记连续执行结束
                            _executionSuccessCount = 0;// 重置连续执行成功次数
                            _finishMark = false;
                            _logger.LogInformation("连续一条龙：任务结束");
                            Notify.Event(NotificationEvent.DragonEnd).Success("连续一条龙：任务结束");
                            return; // 后续的检查任务也不执行
                        }
                    }
                }
            }

            _logger.LogInformation(Config.ScheduleLoop ? "连续一条龙：循环执行计划，继续执行..." : "连续一条龙：执行计划单次完成...");
            Notify.Event(NotificationEvent.DragonEnd).Success(Config.ScheduleLoop ? "连续一条龙：循环执行计划，继续执行..." : "连续一条龙：执行计划单次完成...");
        }
        
        // 连续执行完毕后，检查和最终结束的任务
        await new TaskRunner().RunThreadAsync(async () =>
        {
            await Task.Delay(500);
            
            Notify.Event(NotificationEvent.DragonEnd).Success($"连续一条龙：{Config.SelectedOneDragonFlowPlanName} " +
                                                              $"共完成 {_executionSuccessCount} / {boundConfigs.Count} 个配置单一条龙任务");
            _logger.LogInformation("连续一条龙：{_selectedOneDragonFlowPlanName} 共完成 {_executionSuccessCount} / " +
                                   "{boundConfigs.Count} 个配置单一条龙任务",Config.SelectedOneDragonFlowPlanName,_executionSuccessCount,boundConfigs.Count);
           
            _continuousExecutionMark = false;// 标记连续执行结束
            _executionSuccessCount = 0;// 重置连续执行成功次数
            _finishMark = false;
            
            if (SelectedConfig != null && !string.IsNullOrEmpty(Config.ContinuousCompletionAction))
            {
                switch (Config.ContinuousCompletionAction)
                {
                    case "关闭游戏":
                        SystemControl.CloseGame();
                        break;
                    case "关闭软件":
                        Application.Current.Dispatcher.Invoke(() => { Application.Current.Shutdown(); });
                        break;
                    case "关闭游戏和软件":
                        SystemControl.CloseGame();
                        Application.Current.Dispatcher.Invoke(() => { Application.Current.Shutdown(); });
                        break;
                    case "关机":
                        SystemControl.CloseGame();
                        SystemControl.Shutdown();
                        break;
                    default:
                        Logger.LogWarning("未知的完成任务类型: {t}",SelectedConfig.CompletionAction);
                        break;
                }
            }
        });
    }

    private bool _nextTaskModel = false; // 退出手机的最大次数
    
    [RelayCommand]
    public async Task OnOneKeyExecute()
    {
        CancellationContext.Instance.Set();

        if (!_continuousExecutionMark)
        {
            _lastUid = "";
            InitConfigList();//初始化配置，保证当前选择的配置是最新的
        }
        if (string.IsNullOrEmpty(SelectedConfig.Name) || string.IsNullOrEmpty(Config.SelectedOneDragonFlowConfigName))
        {
            Toast.Warning("请先选择配置");
            return;
        }
        
        ReadScriptGroup();

        var taskListCopy = new List<OneDragonTaskItem>(TaskList);//避免执行过程中修改TaskList
        
        if (SelectedConfig.NextTaskIndex > 0)
        {
            // 通过NextTaskIndex找到执行的任务名称
            var taskName = TaskList.FirstOrDefault(t => t.Index == SelectedConfig.NextTaskIndex)?.Name;

            if (!string.IsNullOrEmpty(taskName))
            {
                _logger.LogInformation("连续一条龙：任务将从 {taskName} 开始执行", taskName);
                // 找到该任务在taskListCopy中的位置
                int taskIndex = taskListCopy.FindIndex(t => t.Index == SelectedConfig.NextTaskIndex);

                if (taskIndex >= 0)
                {
                    // 通过taskIndex，去除taskIndex之前的任务
                    taskListCopy = taskListCopy.Skip(taskIndex).ToList();
                }
                else
                {
                    // 如果没有找到该任务，保持原样或处理错误
                    _logger.LogWarning("连续一条龙：未找到指定的任务序号或被删除，将从头开始执行");
                }
            }else
            {
                _logger.LogWarning("连续一条龙：未找到指定的任务序号或被删除，将从头开始执行");
            }
            SelectedConfig.NextTaskIndex = 0;
            LoadDisplayTaskListFromConfig();
        }
        
        foreach (var task in taskListCopy)
        {
            task.InitAction(SelectedConfig);
        }
        
        int finishOneTaskcount = 1;
        int finishTaskcount = 1;
        int enabledTaskCount = 0;
        int enabledoneTaskCount = 0;
        int enabledTaskCountall = taskListCopy.Count(t => t.IsEnabled);
        _logger.LogInformation($"启用任务总数量: {enabledTaskCountall}");

        await ScriptService.StartGameTask();
        _logger.LogInformation($"上一个执行UID：{(string.IsNullOrEmpty(_lastUid) ? "无" : _lastUid)}");

        if (_lastUid != SelectedConfig.GenshinUid)
        {
            var returnMainUiTask = new ReturnMainUiTask();
            // 验证UID
            bool uidCheckResult = false;
            bool switchAccountResult = false;
            int reTrySwitchTimes = _exitPhoneCount; // 切换账号的最大次数
            int reTrySwitchCount = 0; // 当前切换账号的次数
            int retrySingleTimes = 3; // 当前账号的UID验证最大次数
            int retrySingleCount = 0; // 当前账号的UID验证次数

            try
            {
                if (TaskContext.Instance().Config.MapMaskConfig.Enabled)
                {
                    //返回主页
                    using var cancellationTokenSource = new CancellationTokenSource();
                    await _blessingOfTheWelkinMoonTask.Start(cancellationTokenSource.Token);
                    await returnMainUiTask.Start(cancellationTokenSource.Token);
                    await Task.Delay(2000, cancellationTokenSource.Token);
                }

                if (TaskContext.Instance().Config.SkillCdConfig.Enabled)
                {
                    using var ra = CaptureToRectArea();
                    if (Bv.IsInMainUi(ra))
                    {
                        using var cancellationTokenSource2 = new CancellationTokenSource();
                        await _blessingOfTheWelkinMoonTask.Start(cancellationTokenSource2.Token);
                        Simulation.SendInput.SimulateAction(GIActions.OpenAdventurerHandbook);
                        await returnMainUiTask.Start(cancellationTokenSource2.Token);
                        await Task.Delay(2000, cancellationTokenSource2.Token);
                    }
                }
            }
            //ctrl+c取消任务c处理
            catch (TaskCanceledException ex)
            {
                _logger.LogInformation("UID验证过程中任务被取消");
                return;
            }
            catch (OperationCanceledException ex)   
            {
                _logger.LogError(ex, "UID验证:  {SelectedConfig.Name} / {SelectedConfig.GenshinUid} 配置单任务," +
                                     "验证UID时发生错误,退出执行",
                    SelectedConfig.Name, SelectedConfig.GenshinUid);

            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "UID验证:  {SelectedConfig.Name} / {SelectedConfig.GenshinUid} 配置单任务," +
                                     "验证UID时发生未知错误,退出执行",
                    SelectedConfig.Name, SelectedConfig.GenshinUid);
            }
            
            
            for (int i = 0; i < retrySingleTimes * reTrySwitchTimes; i++){

                try
                {
                    using var cancellationTokenSource = new CancellationTokenSource();
                    await _blessingOfTheWelkinMoonTask.Start(cancellationTokenSource.Token);
                    await new TaskRunner().RunCurrentAsync(async () =>
                    {
                        await _blessingOfTheWelkinMoonTask.Start(cancellationTokenSource.Token);
                        //获取原神窗口焦点
                        SystemControl.FocusWindow(TaskContext.Instance().GameHandle);
                        retrySingleCount++;
                        await returnMainUiTask.Start(cancellationTokenSource.Token);
                        uidCheckResult = await VerifyUid(cancellationTokenSource.Token); // 验证当前登录账号的UID
                    });
                }
                catch (TaskCanceledException ex)
                {
                    _logger.LogInformation("UID验证过程中任务被取消1");
                    return;
                }
                catch (OperationCanceledException)
                {
                    _logger.LogInformation("UID验证过程中任务被取消2");
                    return;
                }
                catch (Exception ex)
                {
                    _logger.LogError(ex, "UID验证:  {SelectedConfig.Name} / {SelectedConfig.GenshinUid} 配置单任务," +
                                         "验证UID时发生未知错误,退出执行",
                        SelectedConfig.Name, SelectedConfig.GenshinUid);
                }

                // 如果任务已经被取消，中断所有任务
             
                if (CancellationContext.Instance.Cts.IsCancellationRequested)
                {
                    _continuousExecutionMark = false;// 标记连续执行结束
                    _executionSuccessCount = 0;// 重置连续执行成功次数
                    _finishMark = false;
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
                            _logger.LogError("UID验证:  {SelectedConfig.Name} / {SelectedConfig.GenshinUid} 配置单任务," +
                                             "切换账号 {reTrySwitchTimes} 次,验证UID仍然失败,退出执行",
                                SelectedConfig.Name,SelectedConfig.GenshinUid,reTrySwitchTimes-1);
                            return;
                        }
                        _logger.LogWarning("UID验证:失败 {retrySingleTimes} 次,第 {reTrySwitchCount} 次尝试切换账号",retrySingleTimes,reTrySwitchCount);
                        await new TaskRunner().RunCurrentAsync(async () =>
                        { 
                            retrySingleCount = 0; // 重置UID验证次数
                            switchAccountResult = await SwitchAccount(CancellationContext.Instance.Cts.Token, reTrySwitchCount); // 失败后，切换账号
                        });
                        // 如果任务已经被取消，中断所有任务
                        if (CancellationContext.Instance.Cts.IsCancellationRequested)
                        {
                            _continuousExecutionMark = false;// 标记连续执行结束
                            _executionSuccessCount = 0;// 重置连续执行成功次数
                            _finishMark = false;
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
        }
        else
        {
            _logger.LogWarning("连续一条龙：绑定UID {GenshinUid} 一致，继续执行",SelectedConfig.GenshinUid);
        }
       
        _lastUid = SelectedConfig.GenshinUid;//记录上一次切换的UID
        
        using var cancellationTokenSource33 = new CancellationTokenSource();
        await new TaskRunner().RunCurrentAsync(async () =>
        {
            // 在一条龙启动前检查是否需要自动兑换兑换码（无论是一条龙任务还是配置组任务都需要检查）
            await CheckAndRedeemCodeIfEnabledAsync(cancellationTokenSource33.Token);
        });
        // 如果任务已经被取消，中断所有任务
        if (CancellationContext.Instance.Cts.IsCancellationRequested)
        {
            _continuousExecutionMark = false;// 标记连续执行结束
            _executionSuccessCount = 0;// 重置连续执行成功次数
            _finishMark = false;
            _logger.LogInformation("连续一条龙：任务结束");
            Notify.Event(NotificationEvent.DragonEnd).Success("连续一条龙：任务结束");
            return; // 后续的检查任务也不执行
        }
        
        if (taskListCopy.Count(t => t.IsEnabled) == 0)
        {
            Toast.Warning("请先选择任务");
            _logger.LogInformation("没有配置,退出执行!");
            Notify.Event(NotificationEvent.DragonEnd).Success("没有配置,退出执行!");
            return;
        }
       
        // 筛选出配置组任务
        var scriptGroupsDefaultNames = ScriptGroupsDefault.Select(sgd => sgd.Name).ToHashSet();
        enabledTaskCount = taskListCopy.Count(t => t.IsEnabled && !scriptGroupsDefaultNames.Contains(taskListCopy.FirstOrDefault(tl => tl.Index == t.Index)?.Name));
        enabledoneTaskCount = enabledTaskCountall - enabledTaskCount;
        
        _logger.LogInformation($"启用一条龙任务的数量: {enabledoneTaskCount}");
         _logger.LogInformation($"启用配置组任务的数量: {enabledTaskCount}");
        
        await ScriptService.StartGameTask();
        if (CancellationContext.Instance.IsCancellationRequested)
        {
            _logger.LogInformation("一条龙在启动阶段被取消");
            return;
        }

        SaveConfig();
        
        if (enabledoneTaskCount <= 0)
        {
            _logger.LogInformation("没有一条龙任务!");
        }

        //获取今天天设置的秘境名称
        var domainConfig = SelectedConfig.GetDomainConfig();
        var custoModel = ScriptGroups.Any(scriptGroup => scriptGroup.Name == domainConfig.domainName) && taskListCopy.Any(t => t.Name == "自动秘境" && t.IsEnabled == true);
        // Toast.Success($"当前秘境名称: {domainConfig.domainName}, 是否自定义秘境: {custoModel}");
        
        Notify.Event(NotificationEvent.DragonStart).Success("一条龙启动");
        foreach (var task in taskListCopy)
        {
            if (task is { IsEnabled: true, Action: not null }) {
                
                if (ScriptGroupsDefault.Any(defaultSg => defaultSg.Name == task.Name && defaultSg.Name != "自动秘境") || (!custoModel && task.Name == "自动秘境"))
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
                        if (!(custoModel && task.Name == "自动秘境") && enabledTaskCount <= 0)
                        {
                            _logger.LogInformation("没有配置组任务,退出执行!");
                            return;
                        }
                        Notify.Event(NotificationEvent.DragonStart).Success("配置组任务启动");

                        if ((SelectedConfig.TaskEnabledList.ContainsKey(task.Index) && SelectedConfig.TaskEnabledList[task.Index].Item1) || custoModel && task.Name == "自动秘境")
                        {
                            _logger.LogInformation(custoModel && task.Name == "自动秘境" ? 
                                $"一条龙任务执行：执行自动秘境自定义任务 {finishOneTaskcount++}/{enabledoneTaskCount}" 
                                : $"配置组任务执行: {finishTaskcount++}/{enabledTaskCount}");
                            
                            await Task.Delay(500);
                            
                            var filePath = custoModel && task.Name == "自动秘境" ? 
                                Path.Combine(_basePath, _scriptGroupPath, $"{domainConfig.domainName}.json") 
                                : Path.Combine(_basePath, _scriptGroupPath, $"{task.Name}.json");
                            
                            var group = ScriptGroup.FromJson(await File.ReadAllTextAsync(filePath));

                            // 创建 taskProgress 并设置 CurrentScriptGroupName，供 HandleTaskSuspend 读取
                            // 也让 RunMulti 内部写入 CurrentScriptGroupProjectInfo（子任务进度）
                            var taskProgress = new BetterGenshinImpact.GameTask.TaskProgress.TaskProgress();
                            taskProgress.CurrentScriptGroupName = group.Name;
                            RunnerContext.Instance.taskProgress = taskProgress;

                            IScriptService? scriptService = App.GetService<IScriptService>();
                            await scriptService!.RunMulti(ScriptControlViewModel.GetNextProjects(group), group.Name, taskProgress);
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
            // await new CheckRewardsTask().Start(CancellationContext.Instance.Cts.Token);
            await Task.Delay(500);
            Notify.Event(NotificationEvent.DragonEnd).Success($"配置单 {SelectedConfig.Name} 绑定 {SelectedConfig.GenshinUid}，一条龙和配置组任务结束");
            _logger.LogInformation("配置单 {SelectedConfig.Name} 绑定UID {GenshinUid} 一条龙和配置组任务结束",
                SelectedConfig.Name,string.IsNullOrEmpty(SelectedConfig.GenshinUid) ? "未绑定" : SelectedConfig.GenshinUid);
            
            // Logger.LogInformation("Debug-Log：{t1}.{t2}.{t3}",_continuousExecutionMark,SelectedConfig.Name,SelectedConfig.CompletionAction);
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
                        case "关闭软件":
                            Application.Current.Dispatcher.Invoke(() => { Application.Current.Shutdown(); });
                            break;
                        case "关闭游戏和软件":
                            SystemControl.CloseGame();
                            Application.Current.Dispatcher.Invoke(() => { Application.Current.Shutdown(); });
                            break;
                        case "关机":
                            SystemControl.CloseGame();
                            SystemControl.Shutdown();
                            break;
                        default:
                            Logger.LogWarning("未知的完成任务类型: {t}",SelectedConfig.CompletionAction);
                            break;
                    }
                }
            }
            _executionSuccessCount++;
            await Task.Delay(2000);
            _finishMark = true;
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
        InputScriptGroupName = 999;
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
                Toast.Success($"一条龙配置单 {str} 已添加，位置 {index+1}");
                var nc = new OneDragonFlowConfig
                {
                    ScheduleName = Config.SelectedOneDragonFlowPlanName, // 设置为当前选定的计划表
                    IndexId = index + 1,
                    Name = str
                };
                ConfigList.Add(nc);
                SelectedConfig = nc;
                OnConfigDropDownChanged();
            }
            SaveConfig();
        }
    }

    [RelayCommand]
    private void NextTaskGroup()
    {
        //设置当前任务InputScriptGroupName的_nextTask标志为true其他所有任务的_nextTask标志为false
        if (SelectedConfig == null || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        var taskList = TaskList.Where(t => t.IsEnabled).ToList();
        if (taskList.Count == 0)
        {
            Toast.Warning("请先选择一条龙任务");
            return;
        }
        //确认当前任务是否为IsEnabled=true
        var currentTask = taskList.FirstOrDefault(t => t.Index == InputScriptGroupName);
        if (currentTask == null)
        {
            // 显示当前任务名称，提示其是禁用状态,通过InputScriptGroupName找到任务名称
            var taskName = TaskList.FirstOrDefault(t => t.Index == InputScriptGroupName)?.Name ?? "未知任务";
            Toast.Warning($"当前任务 <{taskName}> 已禁用，请先启用后再从此开始执行");
            return;
        }
        // 设置当前任务的InputScriptGroupName为_nextTaskIndex
        SelectedConfig.NextTaskIndex= InputScriptGroupName;
        //设定OneDragonTaskItem中对应的任务的_nextTask标志为true
        foreach (var task in TaskList)
        {
            if (task.Index == InputScriptGroupName)
            {
                task.IsNextTask = true;
            }
            else
            {
                task.IsNextTask = false;
            }
        }
        //通过InputScriptGroupName找到任务名称
        var taskName2 = TaskList.FirstOrDefault(t => t.Index == InputScriptGroupName)?.Name ?? "未知任务";
        Toast.Success($"设置从 <{taskName2}> 开始执行任务列表");
        SaveConfig();
    }
    
    [RelayCommand]
    private void ClearNextTaskGroup()
    {
        //设置SelectedConfig.NextTaskIndex为空
        if (SelectedConfig == null || SelectedConfig.ScheduleName != Config.SelectedOneDragonFlowPlanName)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        SelectedConfig.NextTaskIndex = 0;
        //设定OneDragonTaskItem中所有任务的_nextTask标志为false
        foreach (var task in TaskList)
        {
            task.IsNextTask = false;
        }
        Toast.Success("清除从此执行标记完成");
        SaveConfig();
    }

    /// <summary>
    /// 检查并自动兑换兑换码（如果启用）
    /// </summary>
    private async Task CheckAndRedeemCodeIfEnabledAsync(CancellationToken cts = default)
    {
        try
        {
            // 使用配置的UID，如果没有配置则使用默认值 "default"
            var uid = string.IsNullOrEmpty(SelectedConfig?.GenshinUid) ? "default" : SelectedConfig!.GenshinUid;
            await _autoRedeemCodeChecker.CheckAndRedeemIfNeeded(uid, cts);
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "自动兑换码检查失败，不阻塞一条龙执行");
        }
    }

    //UID验证
    private async Task<bool> VerifyUid(CancellationToken cts)  
    {
        if (string.IsNullOrEmpty(SelectedConfig?.Name))
        {
            return false;
        }
        
        if (SelectedConfig.AccountBinding == true)
        {
            await new ReturnMainUiTask().Start(cts);
            Clipboard.Clear();
            Simulation.SendInput.Keyboard.KeyPress(VK.VK_ESCAPE);
            
            for (int i = 0; i < 10; i++)
            {
                using var closeRa = CaptureToRectArea().Find(RecognitionAssets.Get("AutoSkip", "PageCloseMain"));
                if (!closeRa.IsEmpty())
                {
                    closeRa.ClickTo(closeRa.X + closeRa.Width * 3, closeRa.X + closeRa.Height * 4);
                    await new ReturnMainUiTask().Start(cts);
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
  
    private static RecognitionObject GetConfirmRa(params string[] targetText)
    {
        var screenArea = CaptureToRectArea();
        return RecognitionObject.OcrMatch(
            (int)(screenArea.Width * 0.2),
            (int)(screenArea.Height * 0.5),
            (int)(screenArea.Width * 0.5),
            (int)(screenArea.Height * 0.5),
            targetText
        );
    }
    //切换账号
    private AutoWoodAssets _assets;
    private readonly Login3rdParty _login3RdParty = new();
    private int _exitPhoneCount = 3; //,账号数为图标数量-1，默认记录2个账号
    private async Task<bool> SwitchAccount(CancellationToken cts,int switchTime = 1) //基于重新登录函数ExitAndReloginJob改造
    {
        //回到主页
        await new ReturnMainUiTask().Start(cts);
        
        //月卡检测
        await _blessingOfTheWelkinMoonTask.Start(cts);
        
        //============== 退出游戏流程 ==============
        Logger.LogInformation("退出至登录页面");
        _assets = AutoWoodAssets.Instance;
        SystemControl.FocusWindow(TaskContext.Instance().GameHandle);
        Simulation.SendInput.Keyboard.KeyPress(User32.VK.VK_ESCAPE);
        await Delay(800, cts);
        
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
        await Delay(500, cts);

        // 确认退出
        using var cr = CaptureToRectArea();
        cr.Find(_assets.ConfirmRo, ra =>
        {
            ra.Click();
            ra.Dispose();
        });
            
        await Delay(1000, cts);  // 等待退出完成
        
        //============== 重新登录流程 ==============
        // 0第三方登录（如果启用）
     
        _login3RdParty.RefreshAvailabled();
        if (_login3RdParty is { Type: Login3rdParty.The3rdPartyType.Bilibili, IsAvailabled: true })
        {
            await Delay(1, cts);
            _login3RdParty.Login(cts);
            Logger.LogInformation("退出重登启用 B 服模式");
        }
        
        // 1点击账号切换按钮
        Logger.LogInformation("点击 {text} 按钮", "账号切换");
        var exitSwitchClickCnt = 0;
        for (var i = 0; i < 20; i++)
        {
            await Delay(1, cts);
            using var contentRegion = CaptureToRectArea();
            using var ra = contentRegion.Find(_assets.ExitSwitchRo);
            if (!ra.IsEmpty())
            {
                await Delay(500, cts);
                ra.Click();
                await Delay(500, cts);//两次确认，防止卡顿
                ra.Click();
                await Delay(1000, cts);  
                break;
            }
            else
            {
                exitSwitchClickCnt++;   
                if (exitSwitchClickCnt > 2)
                {
                    await Delay(1000, cts);
                }
            }
            await Delay(2000, cts);  
        }
        
        // 2点击“退出”按钮
        Logger.LogInformation("点击 {text} 按钮", "退出");
        var exitClickCnt = 0;
        for (var i = 0; i < 20; i++)
        {
            await Delay(1, cts);
            var ra = CaptureToRectArea();
            var list = ra.FindMulti(new RecognitionObject
            {
                RecognitionType = RecognitionTypes.Ocr,
                RegionOfInterest = new Rect(ra.Width/2, ra.Height *11/20, ra.Width/5, ra.Height/8)
            });
            Region? exitClickCntIcon = list.FirstOrDefault(r => r.Text.Contains("退出"));
            if (exitClickCntIcon != null)
            {
                await Delay(500, cts);
                exitClickCntIcon.Click();
                await Delay(1000, cts);  
                break;
            }
            else
            {
                exitClickCnt++;
                if (exitClickCnt > 2)
                {
                    await Delay(1000, cts); 
                }
            }
            await Delay(1000, cts);  
        }
        
        // 3点击账号选择按钮
        var exitPhoneClickCnt = 0;
        for (var i = 0; i < 20; i++)
        {
            await Delay(1, cts);
            
            var mainRegion= await NewRetry.WaitForElementAppear(
                GetConfirmRa("进入游戏"),
                () => {},
                cts,
                20,
                500
            );
            if (mainRegion)
            {
                Logger.LogInformation("执行 {text} 动作","选择账号");
                await NewRetry.WaitForElementDisappear(
                    GetConfirmRa("进入游戏"),
                    () => {GameCaptureRegion.GameRegion1080PPosClick(1100,494);},
                    cts,
                    5,
                    1500
                );
                
                await Delay(300, cts);
                
                var capturedArea = CaptureToRectArea();
                bool isAccountBinding = false;
                var phoneList = capturedArea.FindMulti(RecognitionObject.Ocr(new Rect(760 , 455 , 330, 390)));
                if (phoneList.Count > 0 && SelectedConfig != null && !string.IsNullOrEmpty(SelectedConfig.AccountBindingCode))
                {
                    _exitPhoneCount = phoneList.Count(p => p.Text.Any(c => c == '*'));
                    Logger.LogInformation("当前记录账号数量: {count}", _exitPhoneCount-1);
                    
                    if (_exitPhoneCount < 3 || _exitPhoneCount > 4)
                    {
                        Logger.LogWarning("请检查账号数量是否正确，数量应为2或3");
                    }

                    foreach (var phone in phoneList)
                    {
                        if (phone.Text.Any( c => c == '*'))
                        {
                            string text = phone.Text;
                            if (text.Length < 2)
                            {
                                Logger.LogWarning("字符长度不足2位");
                            }

                            int index = text.Length - 1;
                            string comfirmWord = "";

                            while (index >= 0 && comfirmWord.Length < 2)
                            {
                                char currentChar = text[index];
                                if (char.IsDigit(currentChar))
                                {
                                    comfirmWord = currentChar + comfirmWord;
                                }
                                index--;
                            }

                            if (comfirmWord.Length != 2)
                            {
                                index = 0;
                                string firstTwoChars = "";

                                while (index < text.Length && firstTwoChars.Length < 2)
                                {
                                    char currentChar = text[index];
                                    if (char.IsLetterOrDigit(currentChar))
                                    {
                                        firstTwoChars += currentChar;
                                    }
                                    index++;
                                }

                                if (firstTwoChars.Length == 2)
                                {
                                    comfirmWord = firstTwoChars;
                                }
                            }
                            
                            if (comfirmWord == SelectedConfig.AccountBindingCode)
                            {
                               // 如果账号绑定成功，点击该账号
                                Logger.LogInformation("UID: {0} 已绑定 {1}", SelectedConfig.GenshinUid, SelectedConfig.AccountBindingCode);
                                phone.Click();
                                isAccountBinding = true;
                                await Delay(500, cts);
                                break;
                            }
                        }
                    }
                }
                else
                {
                    Logger.LogWarning(string.IsNullOrEmpty(SelectedConfig?.AccountBindingCode) ? "UID为绑定码为空，重新绑定UID可设置绑定码" : "未检测到账号列表");
                }
                
                //识别识别后用旧办法
                if (isAccountBinding == false)
                {
                    Logger.LogWarning("未检测到账号列表匹配的绑定码，重新绑定UID可设置绑定码");
                    Logger.LogWarning("尝试使用轮切方式切换账号...");
                    Notify.Event("未检测到账号列表匹配的绑定码，重新绑定UID可设置绑定码");
                    
                    await Delay(500, cts);
                    if (_exitPhoneCount == 3 || (_exitPhoneCount == 4 && switchTime == 1))
                    {
                        GameCaptureRegion.GameRegion1080PPosClick(732,670);;//如果只有两账号，固定选另一个
                    }
                    else if (_exitPhoneCount == 4 && switchTime >= 2)
                    {
                        GameCaptureRegion.GameRegion1080PPosClick(735,742);;//如果有三个账号，切换到第3个
                    }
                    
                }
                
                await Delay(1000, cts);     
                GameCaptureRegion.GameRegion1080PPosClick(1158,626);;//进入游戏
                await Delay(1000, cts);  
                GameCaptureRegion.GameRegion1080PPosClick(1158,626);;//进入游戏
                await Delay(1000, cts);   
                break;
            }
            else
            {
                exitPhoneClickCnt++; 
                if (exitPhoneClickCnt > 2)
                {
                    await Delay(1000, cts);
                    break;
                }
            }
            await Delay(1000, cts);  
        }

        // 4进入游戏检测
        var clickCnt = 0;
        for (var i = 0; i < 50; i++)
        {
            await Delay(1, cts);

            using var contentRegion = CaptureToRectArea();
            using var ra = contentRegion.Find(_assets.EnterGameRo);
            if (!ra.IsEmpty())
            {
                clickCnt++;
                GameCaptureRegion.GameRegion1080PPosClick(955, 656);
                GameCaptureRegion.GameRegion1080PPosClick(1660, 282);//非凌晨4点，点击屏幕
            }
            else
            {
                if (clickCnt > 2)
                {
                    await Delay(5000, cts);
                    break;
                }
            }
            await Delay(1000, cts);  
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
                GameCaptureRegion.GameRegion1080PPosClick(955, 656);//非凌晨4点，点击屏幕
                GameCaptureRegion.GameRegion1080PPosClick(1660, 282);//非凌晨4点，点击屏幕
            }
            
            await Delay(1000, cts);
            
           if (i == 49)
           {
               Logger.LogWarning("更换账号失败");
               await Delay(500, cts);
               return false;
           }
           
        }
        await Delay(500, cts);
        return true;
    }
    
     // 旧版本的 OneDragonFlowConfigV0
    #region 
    [Serializable]
    public partial class OneDragonFlowConfigV0 : OneDragonFlowConfig
    {
        // 旧版本的 TaskEnabledList
        [ObservableProperty]
        private Dictionary<string,bool> _taskEnabledList = new();
      
        // 旧版本的 Version
        [ObservableProperty]
        private int _version = 0;
    }
   
    
    private void AdaptVersions()
    {
        Directory.CreateDirectory(OneDragonFlowConfigFolder);
        var configFiles = Directory.GetFiles(OneDragonFlowConfigFolder, "*.json");
        foreach (var configFile in configFiles)
        {
            var json = File.ReadAllText(configFile);
            var config = UpgradeConfig(json);
            if (config != null)
            {
                WriteConfig(config);
            }else
            {
                return;//失败一次退出
            }
        }
    }
    
    private static bool _hasConfigBackup = false; // 备份标志
    private  OneDragonFlowConfig? UpgradeConfig(string json)
    {
        try
        {
            var oldConfig = JsonConvert.DeserializeObject<OneDragonFlowConfigV0>(json);
            
            if (oldConfig != null && oldConfig.Version <= 0)
            {
               if (!_hasConfigBackup)
               {
                   var backupPath = Path.Combine(
                       AppContext.BaseDirectory, 
                       "Backups",
                       $"ConfigBackup_{DateTime.Now:yyyyMMdd_HHmmss}"
                   );
                
                   Directory.CreateDirectory(backupPath);
                   // 再备份整个USER文件夹到restoredFolder
                   BackupDirectory("User", backupPath);

                   Toast.Warning("备份User文件夹到 Backups 文件夹，配置升级中...",ToastLocation.TopCenter,default,6000);
                   _hasConfigBackup = true;
               }
            }
            else
            {
                return null;
            }
            
            var newConfigFromOld = new OneDragonFlowConfig();

            newConfigFromOld.TaskEnabledList = AdaptTaskEnabledList(oldConfig.TaskEnabledList);
            newConfigFromOld.Version = 1;
            
            // 再批量复制其它属性（排除已处理的）
            foreach (var property in typeof(OneDragonFlowConfigV0).GetProperties())
            {
                if (property.Name == "TaskEnabledList" || property.Name == "Version") continue;
                var newProperty = typeof(OneDragonFlowConfig).GetProperty(property.Name);
                if (newProperty != null && newProperty.CanWrite)
                {
                    newProperty.SetValue(newConfigFromOld, property.GetValue(oldConfig));
                }
            }
            
            return newConfigFromOld;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"反序列化错误: {ex.Message}");
        }
        return null;
    }
    
    // 适配方法
    private Dictionary<int, (bool, string)> AdaptTaskEnabledList(Dictionary<string, bool> oldTaskEnabledList)
    {
        var newTaskEnabledList = new Dictionary<int, (bool, string)>();
        int index = oldTaskEnabledList.Count; // 从大到小开始索引

        foreach (var kvp in oldTaskEnabledList)
        {
            newTaskEnabledList[index] = (kvp.Value, kvp.Key);
            index--;
        }

        return newTaskEnabledList;
    }
    
    //备份USER目录及其子目录和文件到以时间命名的文件夹
    private void BackupUser()
    {
        string backupFolder = "Backups";
        string timestampedBackupFolder = Path.Combine(backupFolder, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        BackupDirectory("User", timestampedBackupFolder);
        Toast.Warning($"备份 User 文件夹到 {timestampedBackupFolder} 文件夹", ToastLocation.TopCenter, default, 5000);
    }
    
    
     private void RestoreOldVersions()
    {
        string oldConfigFolder = "User";
        string oldConfigOneDragonFolder = Path.Combine(oldConfigFolder, "OneDragon");
        string backupFolder = "NewUserBackups";
        string restoredFolder = "NewToOldUser";
        string restoredUserFolder = Path.Combine(restoredFolder, "OneDragon");

        Directory.CreateDirectory(backupFolder);
        Directory.CreateDirectory(restoredFolder);


        // 备份整个配置目录及其子目录和文件到以时间命名的文件夹
        string timestampedBackupFolder = Path.Combine(backupFolder, DateTime.Now.ToString("yyyyMMdd_HHmmss"));
        BackupDirectory(oldConfigFolder, timestampedBackupFolder);
        Toast.Warning($"备份现在的配置文件到 {timestampedBackupFolder} 文件夹，开始还原...", ToastLocation.TopCenter, default, 3000);

        // 再备份整个USER文件夹到restoredFolder
        BackupDirectory(oldConfigFolder, restoredFolder);
        
        // 还原配置文件
        foreach (var configFile in Directory.GetFiles(oldConfigOneDragonFolder, "*.json", SearchOption.AllDirectories))
        {
            var json = File.ReadAllText(configFile);
            var newConfig = JsonConvert.DeserializeObject<OneDragonFlowConfig>(json);
            if (newConfig != null)
            {
                var oldConfig = DowngradeConfig(newConfig);
                // 将 oldConfig 对象转换成 JSON 字符串
                string json2 = JsonConvert.SerializeObject(oldConfig);

                // 使用 JObject 解析 JSON 字符串
                JObject jObject = JObject.Parse(json2);

                // 删除特定元素，例如 "SomeProperty"
                jObject.Remove("AccountBinding");
                jObject.Remove("Config");
                jObject.Remove("IndexId");
                jObject.Remove("Period");
                jObject.Remove("SelectedPeriodList");
                jObject.Remove("ScheduleName");
                jObject.Remove("ResinOrder");
                jObject.Remove("GenshinUid");
                jObject.Remove("AccountBinding");
                jObject.Remove("Version");
                // 将修改后的 JObject 转换回 JSON 字符串
                string json3 = jObject.ToString();
                // 把json3写入NewToOldUser\OneDragon\文件夹下的配置文件
                string restoredConfigFile = Path.Combine(restoredUserFolder, Path.GetFileName(configFile));
                File.WriteAllText(restoredConfigFile, json3);
  
               
                // File.WriteAllText(configFile.Replace(oldConfigFolder, restoredUserFolder), json3);
            }
        }
        Toast.Success("还原成功，文件在 NewToOldUser 文件夹下，请重启BGI！", ToastLocation.TopCenter, default, 10000);
    }

    // 备份目录及其子目录和文件
    private static void BackupDirectory(string sourceDir, string targetDir)
    {
        Directory.CreateDirectory(targetDir);

        foreach (var file in Directory.GetFiles(sourceDir))
        {
            string targetFilePath = Path.Combine(targetDir, Path.GetFileName(file));
            File.Copy(file, targetFilePath, overwrite: true);
        }

        foreach (var directory in Directory.GetDirectories(sourceDir))
        {
            string targetSubDirPath = Path.Combine(targetDir, Path.GetFileName(directory));
            BackupDirectory(directory, targetSubDirPath);
        }
    }

    private OneDragonFlowConfigV0? DowngradeConfig(OneDragonFlowConfig newConfig)
    {
        try
        {
            //删除旧版本中不存在的属性
            var oldConfig = new OneDragonFlowConfigV0();
            foreach (var property in typeof(OneDragonFlowConfig).GetProperties())
            {
                var oldProperty = typeof(OneDragonFlowConfig).GetProperty(property.Name);// 旧版本的属性
                if (oldProperty != null && oldProperty.CanWrite)
                {
                    if (property.Name == "TaskEnabledList")
                    {
                        Dictionary<string, bool> oldTaskEnabledList = ReverseAdaptTaskEnabledList(newConfig.TaskEnabledList);
                        oldConfig.TaskEnabledList = oldTaskEnabledList;
                        continue;
                    }
                    
                    if (property.Name == "Config"  || property.Name == "IndexId"
                         || property.Name == "Period" || property.Name == "SelectedPeriodList" || property.Name == "ScheduleName"
                         || property.Name == "ResinOrder" || property.Name == "GenshinUid" || property.Name == "AccountBinding")
                    {
                        // 这些属性在旧版本中不存在
                        oldProperty.SetValue(oldConfig, null);
                        continue;
                    }

                    oldProperty.SetValue(oldConfig, property.GetValue(newConfig));// 其他属性直接复制

                }
            }
            return oldConfig;
        }
        catch (Exception ex)
        {
            Console.WriteLine($"序列化错误: {ex.Message}");
        }
        return null;
    }

    // 适配方法，将新的 TaskEnabledList 转换为旧的格式
    private static Dictionary<string, bool> ReverseAdaptTaskEnabledList(Dictionary<int, (bool, string)> newTaskEnabledList)
    {
        var oldTaskEnabledList = new Dictionary<string, bool>();

        foreach (var kvp in newTaskEnabledList)
        {
            oldTaskEnabledList[kvp.Value.Item2] = kvp.Value.Item1;
        }
        
        return oldTaskEnabledList;
    }

    // 写入配置文件 (旧版本)
    private void WriteConfig(OneDragonFlowConfig config, string filePath)
    {
        string json = JsonConvert.SerializeObject(config, Formatting.Indented);
        File.WriteAllText(filePath, json);
    }
    
    #endregion
}
