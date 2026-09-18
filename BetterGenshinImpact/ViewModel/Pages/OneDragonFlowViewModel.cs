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
using BetterGenshinImpact.Helpers.Ui;
using BetterGenshinImpact.Service;
using BetterGenshinImpact.Service.Notification;
using BetterGenshinImpact.Service.Notification.Model.Enum;
using BetterGenshinImpact.View.Windows;
using BetterGenshinImpact.ViewModel.Pages.View;
using CommunityToolkit.Mvvm.Input;
using Newtonsoft.Json;
using BetterGenshinImpact.Core.Script.Project;
using BetterGenshinImpact.Service.Interface;
using System.Collections.Specialized;
using Wpf.Ui.Violeta.Controls;

namespace BetterGenshinImpact.ViewModel.Pages;

public partial class OneDragonFlowViewModel : ViewModel
{
    private readonly ILogger<OneDragonFlowViewModel> _logger = App.GetLogger<OneDragonFlowViewModel>();

    public static readonly string OneDragonFlowConfigFolder = Global.Absolute(@"User\OneDragon");

    private readonly ScriptService _scriptService;
    // ===== R2/R3 执行桥状态（茶版移植，公版基底之上保留）=====
    /// <summary>[A5-2/A5-3] 当前龙父作业 Id（观察面发布与子作业挂接用；未登记时跳过发布）。</summary>
    private Guid? _currentDragonJobId;

    /// <summary>本次执行是否跑完尾部检查（ExecuteOneDragonAsync 结果判定用）。</summary>
    private bool _finishMark;

    // ===== R3.0 过渡窗口加载保护（待迁移文件只读识别与提示，见 R0 基线 §8 / R3 开工定案）=====
    /// <summary>受保护（旧格式/损坏）的一条龙配置文件清单，仅展示与提示，不进入加载/执行/写回。</summary>
    [ObservableProperty] private ObservableCollection<string> _pendingMigrationFiles = [];

    [ObservableProperty] private bool _hasPendingMigrationFiles;

    [ObservableProperty] private string _pendingMigrationHint = string.Empty;

    private static string DescribeConfigShape(OneDragonConfigShape shape) => shape switch
    {
        OneDragonConfigShape.LegacyNameBool => "旧版名称键格式",
        OneDragonConfigShape.TeabagTuple => "旧版调度格式",
        OneDragonConfigShape.StructuralBad => "结构损坏",
        _ => "无法识别的格式",
    };

    /// <summary>R3.0 写保护判定：目标路径现存文件为受保护形状（旧格式/损坏）时，任何入口不得写回。</summary>
    private bool IsProtectedConfigFile(string filePath)
    {
        if (!File.Exists(filePath))
        {
            return false;
        }
        try
        {
            return OneDragonConfigShapePreflight.InspectBytes(File.ReadAllBytes(filePath)).IsProtected;
        }
        catch (Exception e)
        {
            _logger.LogWarning(e, "一条龙配置文件 {Path} 预检失败，按受保护处理", filePath);
            return true;
        }
    }


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
            new() { Name = "自动首领讨伐" },
            new() { Name = "自动幽境危战" },
            new() { Name = "自动地脉花" },
            new() { Name = "领取每日奖励" },
            new() {Name = "领取尘歌壶奖励" },
        };

    private readonly string _scriptGroupPath = Global.Absolute(@"User\ScriptGroup");
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
        // 这个方法现在由XAML中的Popup处理，保留为空或者可以删除
        // 实际逻辑已经移到ProcessSelectedGroups方法中
    }

    public void ProcessSelectedGroups(List<string> selectedGroupNames)
    {
        if (selectedGroupNames == null || !selectedGroupNames.Any())
        {
            return;
        }

        int pickTaskCount = selectedGroupNames.Count;
        
        foreach (var selectedGroupName in selectedGroupNames)
        {
            var taskItem = new OneDragonTaskItem(selectedGroupName)
            {
                IsEnabled = true
            };
            taskItem.Id = GenerateUniqueTaskId();
            
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
        if (pickTaskCount > 1)
        {
            Toast.Success(pickTaskCount + " 个任务添加成功");  
        }
    }

    // 原来的OnStartMultiScriptGroupAsync方法已被移除，功能已迁移到XAML Popup中
    
    [ObservableProperty] private ObservableCollection<OneDragonFlowConfig> _configList = [];
    /// <summary>
    /// 当前生效配置
    /// </summary>
    [ObservableProperty] private OneDragonFlowConfig? _selectedConfig;

    [ObservableProperty] private List<string> _craftingBenchCountry = ["枫丹", "稻妻", "璃月", "蒙德"];

    [ObservableProperty] private List<string> _adventurersGuildCountry = ["挪德卡莱", "枫丹", "稻妻", "璃月", "蒙德"];

    [ObservableProperty] private List<string> _domainNameList = ["", ..MapLazyAssets.Get().DomainNameList];

    [ObservableProperty] private List<string> _completionActionList = ["无", "关闭游戏", "关闭软件", "关闭游戏和软件", "关机"];

    [ObservableProperty] private List<string> _sundayEverySelectedValueList = ["","1", "2", "3"];
    
    [ObservableProperty] private List<string> _sundaySelectedValueList = ["","1", "2", "3"];

    [ObservableProperty] private List<string> _secretTreasureObjectList = ["布匹","须臾树脂","大英雄的经验","流浪者的经验","精锻用魔矿","摩拉","祝圣精华","祝圣油膏"];
    
    [ObservableProperty] private List<string> _sereniteaPotTpTypes = ["地图传送", "尘歌壶道具"];

    [ObservableProperty] private AutoFightViewModel? _autoFightViewModel;
    
    public AllConfig Config { get; set; } = TaskContext.Instance().Config;

    public OneDragonFlowViewModel()
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
    }

    public override void OnNavigatedTo()
    {
        InitConfigList();
    }

    public void InitConfigList() // R3：IPC 配置应用后刷新入口（HandleSetTaskEnabled）需要 public
    {
        Directory.CreateDirectory(OneDragonFlowConfigFolder);
        // 读取文件夹内所有json配置，按创建时间正序
        var configFiles = Directory.GetFiles(OneDragonFlowConfigFolder, "*.json");
        var configs = new List<OneDragonFlowConfig>();
        var pending = new List<string>();

        OneDragonFlowConfig? selected = null;
        foreach (var configFile in configFiles)
        {
            // R3.0 过渡窗口加载保护：旧格式/损坏文件只读识别与报告，不进入加载与执行（R0 基线 §8 硬门槛）
            var verdict = OneDragonConfigShapePreflight.InspectBytes(File.ReadAllBytes(configFile));
            if (verdict.IsProtected)
            {
                pending.Add($"{Path.GetFileName(configFile)}（{DescribeConfigShape(verdict.Shape)}）");
                _logger.LogWarning("一条龙配置 {File} 形状 {Shape} 受保护（待迁移），已跳过加载", Path.GetFileName(configFile), verdict.Shape);
                continue;
            }

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

        PendingMigrationFiles = new ObservableCollection<string>(pending);
        HasPendingMigrationFiles = pending.Count > 0;
        PendingMigrationHint = pending.Count == 0
            ? string.Empty
            : $"检测到 {pending.Count} 个旧版一条龙配置文件，已保护性跳过（不会被修改或执行）：{string.Join("、", pending)}。迁移能力将在后续版本提供。";

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

    public void LoadDisplayTaskListFromConfig()
    {
        if (SelectedConfig == null || SelectedConfig.TaskEnabledList == null)
        {
            return;
        }

        TaskList.Clear();

        // 旧格式兼容：TaskDefinitions 为空时，TaskEnabledList 键为任务名
        bool isOldFormat = SelectedConfig.TaskDefinitions == null || SelectedConfig.TaskDefinitions.Count == 0;

        // 使用 TaskOrder 恢复顺序；若无则回退到 TaskEnabledList 的键顺序
        var orderedKeys = SelectedConfig.TaskOrder?.Count > 0
            ? SelectedConfig.TaskOrder
            : SelectedConfig.TaskEnabledList.Keys.ToList();

        foreach (var key in orderedKeys)
        {
            if (!SelectedConfig.TaskEnabledList.TryGetValue(key, out var enabled))
            {
                continue;
            }

            OneDragonTaskItem taskItem;
            if (isOldFormat)
            {
                taskItem = new OneDragonTaskItem(key) { IsEnabled = enabled };
            }
            else
            {
                if (!SelectedConfig.TaskDefinitions.TryGetValue(key, out var name))
                {
                    continue;
                }
                taskItem = new OneDragonTaskItem(name, key) { IsEnabled = enabled };
            }
            taskItem.IsNextTask = key == SelectedConfig.NextTaskId;
            TaskList.Add(taskItem);
        }
    }

    [RelayCommand]
    private void DeleteConfigDisplayTaskListFromConfig()
    {
        if (SelectedConfig == null || SelectedTask == null)
        {
            Toast.Warning("请先选择配置组和任务");
            return;
        }

        var itemToDelete = TaskList.FirstOrDefault(t => t.Id == SelectedTask.Id);
        if (itemToDelete != null)
        {
            TaskList.Remove(itemToDelete);
            Toast.Information("已经删除");
        }
    }

    [RelayCommand]
    private void OnConfigDropDownChanged()
    {
        SetSomeSelectedConfig(SelectedConfig);
        SelectedTask = null;
    }

    public void SaveConfig()
    {
        if (SelectedConfig == null)
        {
            return;
        }

        SelectedConfig.TaskDefinitions.Clear();
        SelectedConfig.TaskEnabledList.Clear();
        SelectedConfig.TaskOrder.Clear();
        foreach (var task in TaskList)
        {
            SelectedConfig.TaskDefinitions[task.Id] = task.Name;
            SelectedConfig.TaskEnabledList[task.Id] = task.IsEnabled;
            SelectedConfig.TaskOrder.Add(task.Id);
        }

        WriteConfig(SelectedConfig);
    }
    
    [RelayCommand]
    private void AddTaskGroup()
    {
        // 触发弹窗显示的事件，让View层处理
        // 我们可以通过一个属性来通知View显示弹窗
        ShouldShowAddTaskGroupPopup = true;
    }
    
    [ObservableProperty]
    private bool _shouldShowAddTaskGroupPopup = false;

    [RelayCommand]
    private void SaveActionConfig()
    {
        SaveConfig();
        Toast.Information("排序已保存");
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
                if (SelectedConfig.TaskEnabledList.TryGetValue(task.Id, out var value))
                {
                    task.IsEnabled = value;
                }
            }

            LoadDisplayTaskListFromConfig();
        }
    }

    private async void TaskPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        await Task.Delay(100); //等会加载完再保存
        SaveConfig();
    }

    private void ConfigPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        SaveConfig();
        WriteConfig(SelectedConfig);
    }

    /// <returns>文件已确认提交返回 true；保护拒写或 I/O 失败返回 false（ASTRA 会诊 P0：调用方据此决定是否允许后续破坏性动作）</returns>
    public bool WriteConfig(OneDragonFlowConfig? config)
    {
        if (config == null)
        {
            return false;
        }

        var filePath = Path.Combine(OneDragonFlowConfigFolder, $"{config.Name}.json");
        // R3.0 写保护：同名占用防护——受保护的旧格式/损坏文件（待迁移）不被覆盖写回
        if (IsProtectedConfigFile(filePath))
        {
            _logger.LogWarning("拒绝写入：{Path} 为受保护的旧格式文件（待迁移）", filePath);
            Toast.Error($"配置「{config.Name}」与待迁移的旧版文件同名，已拒绝写入");
            return false;
        }

        try
        {
            Directory.CreateDirectory(OneDragonFlowConfigFolder);
            var json = JsonConvert.SerializeObject(config, Formatting.Indented);
            File.WriteAllText(filePath, json);
            return true;
        }
        catch (Exception e)
        {
            _logger.LogDebug(e, "保存配置时失败");
            Toast.Error("保存配置时失败");
            return false;
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
        //
        var cmdOptions = CommandLineOptions.Instance;
        if (cmdOptions.Action == CommandLineAction.StartOneDragon)
        {
            // 通过命令行参数启动一条龙。
            if (cmdOptions.OneDragonConfigName != null)
            {
                // 从命令行参数中提取一条龙配置名称。
                _logger.LogInformation($"参数指定的一条龙配置：{cmdOptions.OneDragonConfigName}");
                var argsOneDragonConfig = ConfigList.FirstOrDefault(x =>
                    string.Equals(x.Name, cmdOptions.OneDragonConfigName, StringComparison.Ordinal));
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
    }

    [RelayCommand]
    public async Task OnOneKeyExecute() => await ExecuteOneDragonAsync();

    /// <summary>
    /// [R2 桥准入壳，R3 保留] 一条龙统一执行入口：UI 命令与 IPC/助手路径都经此进入执行漏斗。
    /// </summary>
    public async Task<TaskRunResult> ExecuteOneDragonAsync(BetterGenshinImpact.Service.Execution.JobDescriptor? request = null)
    {
        var source = RunnerContext.Instance.OneDragonJobSourceHint ?? BetterGenshinImpact.Service.Execution.JobSource.Ui;
        RunnerContext.Instance.OneDragonJobSourceHint = null;
        RunnerContext.Instance.OneDragonParentJobId = null;
        if (string.IsNullOrEmpty(SelectedConfig?.Name)) return TaskRunResult.Failed;
        var descriptor = request ?? new BetterGenshinImpact.Service.Execution.JobDescriptor(
            BetterGenshinImpact.Service.Execution.JobKind.OneDragon, SelectedConfig.Name, source);
        descriptor = descriptor with { JobId = descriptor.JobId ?? Guid.NewGuid() };
        // R3.0 硬门槛：受保护（旧格式/损坏，待迁移）的配置文件不进入执行，起步即拒绝并留痕。
        if (IsProtectedConfigFile(Path.Combine(OneDragonFlowConfigFolder, descriptor.Name + ".json")))
        {
            _logger.LogWarning("一条龙配置 {Name} 为受保护的旧格式文件（待迁移），本次执行被拒绝", descriptor.Name);
            Toast.Warning($"一条龙配置「{descriptor.Name}」为旧版格式，待迁移后方可执行");
            return TaskRunResult.Failed;
        }
        BetterGenshinImpact.Service.Execution.ExecutionScope scope;
        try { scope = BetterGenshinImpact.Service.Execution.ExecutionScope.Start(descriptor); }
        catch (InvalidOperationException ex)
        {
            _finishMark = false;
            _logger.LogWarning(ex, "一条龙未获执行权");
            return TaskRunResult.RejectedSlotBusy;
        }
        using var rootLifetime = scope;
        var registry = BetterGenshinImpact.Service.Execution.JobRegistry.Instance;
        var parent = descriptor.JobId is { } id ? registry.Query(id) : null;
        parent ??= registry.Submit(descriptor.Kind, descriptor.Name, descriptor.Source,
            descriptor.Generation, descriptor.IdempotencyKey, jobId: descriptor.JobId, identity: descriptor.ExecutionIdentity).Job;
        _currentDragonJobId = parent.JobId;
        registry.TryMarkRunning(parent.JobId);
        scope.SetDragonNode(string.Empty);
        _finishMark = false;
        var result = TaskRunResult.Failed;
        var failureCode = BetterGenshinImpact.Service.Execution.JobErrorCodes.TaskStartFailed; // R4.6：受控失败码（account_mismatch 等）
        try
        {
            // R4.6 E2-9：expectedUid 复验——执行权已取得（ExecutionScope.Start 成功）、首个资源副作用前；
            // 不符抛 AccountMismatchException（不释放执行权再排队）。手动入口 descriptor 无 ExpectedUid，零变化。
            await Service.OneDragon.ExpectedUidGate.VerifyOrThrowAsync(descriptor.ExpectedUid, scope.Token);
            await OnOneKeyExecuteCore();
            // E9：Skipped（仅正常跳过）与 Ran 同样进入完成判定——尾部跑完（_finishMark）即根作业成功
            // （Succeeded-with-skips，子作业各自携带 Skipped）；其他结果原样透传。
            result = Service.OneDragon.OneDragonRootResultRule.DecideRootResult(scope.Result, _finishMark);
        }
        catch (BetterGenshinImpact.Service.Execution.AccountMismatchException ex)
        {
            // R4.6 E2-9：账号不符——受保护执行阶段内拒绝，未产生资源副作用
            _logger.LogWarning("一条龙执行被拒绝（账号不符）：{Message}", ex.Message);
            failureCode = BetterGenshinImpact.Service.Execution.JobErrorCodes.AccountMismatch;
            result = TaskRunResult.Failed;
        }
        catch (OperationCanceledException)
        {
            result = scope.Result == TaskRunResult.Preempted ? TaskRunResult.Preempted : TaskRunResult.Cancelled;
        }
        catch (Exception ex)
        {
            _logger.LogError(ex, "一条龙执行失败");
            result = TaskRunResult.Failed;
        }
        finally
        {
            var cancelled = result is TaskRunResult.Cancelled or TaskRunResult.Preempted;
            registry.TryMarkTerminal(parent.JobId,
                result == TaskRunResult.Ran ? BetterGenshinImpact.Service.Execution.JobState.Succeeded
                    : cancelled ? BetterGenshinImpact.Service.Execution.JobState.Cancelled
                    : BetterGenshinImpact.Service.Execution.JobState.Failed,
                result == TaskRunResult.Ran ? null : result == TaskRunResult.Preempted ? "preempted"
                    : cancelled ? scope.StopReason : failureCode, null, cancelled);
            _currentDragonJobId = null;
            _runningConfig = null;
            if (result != TaskRunResult.Ran) _finishMark = false;
        }
        return result;
    }

    private OneDragonFlowConfig? _runningConfig;

    /// <summary>[R2 桥] 前置步骤结果纪律：未成功的前置必须显式抛错，不得静默继续。</summary>
    private static void RequireDragonStep(TaskRunResult result)
    {
        if (result == TaskRunResult.Ran) return;
        var scope = BetterGenshinImpact.Service.Execution.ExecutionScope.Current!;
        scope.Observe(result);
        scope.ThrowIfStopped();
        throw new InvalidOperationException("一条龙前置动作未成功: " + result);
    }

    private async Task OnOneKeyExecuteCore()
    {
        var scope = BetterGenshinImpact.Service.Execution.ExecutionScope.Current!;
        scope.ThrowIfStopped();
        _logger.LogInformation($"启用一条龙配置：{SelectedConfig?.Name}");

        // 启动等待之前先进行取消操作的初始化，便于在任务开始前终止任务.
        CancellationContext.Instance.Set();

        // [批次名单 2026-09-13] 捕获本次执行的批次绑定名单（助手批次下发时由调用方预置），
        // 随后立即清空 RunnerContext——生命周期=本次执行，杜绝跨执行残留
        // （旧 IsMultiplayerAssistantActivated 因无退出路径清理而残留误吞后续组的教训）。
        var batchGroupNames = RunnerContext.Instance.BatchGroupNames;
        RunnerContext.Instance.BatchGroupNames = null;

        InitConfigList(); // 初始化配置，保证当前选择的配置是最新的
        var executionConfig = JsonConvert.DeserializeObject<OneDragonFlowConfig>(JsonConvert.SerializeObject(SelectedConfig))!;
        if (scope.Descriptor.ConfigRevision is { } requiredRevision)
        {
            // 修订守卫：执行以排期时的配置快照为准；期间被改动则拒绝（configuration_changed）
            var snapshot = await BetterGenshinImpact.Service.Execution.TaskConfigurationContract.Default.ReadAsync(scope.Descriptor.Name, true);
            if (snapshot.Revision != requiredRevision) throw new InvalidOperationException("configuration_changed");
            executionConfig = snapshot.Document.ToObject<OneDragonFlowConfig>() ?? throw new InvalidOperationException("invalid_configuration");
            executionConfig.Name = scope.Descriptor.Name;
        }
        if (executionConfig.Name != scope.Descriptor.Name)
            throw new InvalidOperationException("执行配置在起步前已改变");
        _runningConfig = executionConfig;
        // R3.2 参数快照下发：间接读配置的辅助任务（合成/尘歌壶）经作用域取本次冻结副本
        scope.SetDragonConfigSnapshot(JsonConvert.SerializeObject(executionConfig));
        scope.TrackConfigurationFile(Path.Combine(OneDragonFlowConfigFolder, executionConfig.Name + ".json"));
        // R3 原生身份：桥恢复起点为任务项 GUID
        if (scope.Descriptor.ResumeTaskId is { } resumeTaskId) executionConfig.NextTaskId = resumeTaskId;
        if (string.IsNullOrEmpty(executionConfig.Name) || string.IsNullOrEmpty(Config.SelectedOneDragonFlowConfigName))
        {
            Toast.Warning("请先选择配置");
            return;
        }

        ReadScriptGroup();

        var taskListCopy = TaskList.Select(t => new OneDragonTaskItem(t.Name, t.Id) { IsEnabled = t.IsEnabled }).ToList(); // 避免执行过程中修改 TaskList
        if (scope.Descriptor.ConfigRevision != null)
        {
            // 修订快照模式：以快照文档为准重建条目（原生投影：TaskOrder 顺序 + TaskDefinitions 名称 + TaskEnabledList 开关）
            taskListCopy = executionConfig.TaskOrder
                .Where(taskId => executionConfig.TaskEnabledList.ContainsKey(taskId))
                .Select(taskId => new OneDragonTaskItem(
                    executionConfig.TaskDefinitions.TryGetValue(taskId, out var taskName) ? taskName : taskId, taskId)
                { IsEnabled = executionConfig.TaskEnabledList[taskId] })
                .ToList();
        }
        if (scope.Descriptor.TaskId is { } singleId)
        {
            // R3 单项过滤：原生 GUID 身份（legacy:<index> 过渡身份已退出）
            taskListCopy = taskListCopy.Where(t => t.Id == singleId).ToList();
            if (taskListCopy.Count != 1 || !taskListCopy[0].IsEnabled) throw new InvalidOperationException("task_not_found_or_disabled");
            executionConfig.NextTaskId = string.Empty;
            executionConfig.CompletionAction = ""; // A leaf must never execute whole-plan shutdown/close actions.
        }
        if (!taskListCopy.Any(t => t.IsEnabled))
            throw new InvalidOperationException("no_work: 一条龙没有启用的任务");

        // 如果设置了 NextTaskId，从指定任务开始执行
        if (!string.IsNullOrEmpty(executionConfig.NextTaskId))
        {
            var taskIndex = taskListCopy.FindIndex(t => t.Id == executionConfig.NextTaskId);
            if (taskIndex >= 0)
            {
                _logger.LogInformation("一条龙：任务将从 {Name} 开始执行", taskListCopy[taskIndex].Name);
                taskListCopy = taskListCopy.Skip(taskIndex).ToList();
            }
            else if (scope.Descriptor.ResumeTaskId != null)
            {
                // resume 严格合同：恢复位置丢失必须拒绝，绝不能静默从头重跑
                throw new InvalidOperationException("恢复位置已不存在，不能从头重跑");
            }
            else
            {
                // 公版 B04：UI「从此执行」标记丢失 → 警告并从头开始执行
                _logger.LogWarning("一条龙：未找到标记的任务，将从头开始执行");
            }
            executionConfig.NextTaskId = string.Empty;
            if (scope.Descriptor.ResumeTaskId == null && SelectedConfig != null) SelectedConfig.NextTaskId = string.Empty;
            LoadDisplayTaskListFromConfig();
        }

        foreach (var task in taskListCopy)
        {
            task.InitAction(executionConfig);
        }

        int finishOneTaskcount = 1;
        int finishTaskcount = 1;
        int enabledTaskCountall = taskListCopy.Count(t => t.IsEnabled);
        _logger.LogInformation($"启用任务总数量: {enabledTaskCountall}");

        await ScriptService.StartGameTask();
        if (CancellationContext.Instance.IsCancellationRequested)
        {
            _logger.LogInformation("一条龙在启动阶段被取消");
            return;
        }

        SaveConfig();

        var scriptGroupsDefaultNames = ScriptGroupsdefault.Select(sgd => sgd.Name).ToHashSet();
        int enabledTaskCount = taskListCopy.Count(t => t.IsEnabled && !scriptGroupsDefaultNames.Contains(t.Name));
        int enabledoneTaskCount = enabledTaskCountall - enabledTaskCount;
        _logger.LogInformation($"启用一条龙任务的数量: {enabledoneTaskCount}");
        _logger.LogInformation($"启用配置组任务的数量: {enabledTaskCount}");

        if (enabledoneTaskCount <= 0)
        {
            _logger.LogInformation("没有一条龙任务!");
        }

        Notify.Event(NotificationEvent.DragonStart).Success("一条龙启动");
        var enabledOrdinal = 0; // [A5-3] 当前条目在本次执行启用序列中的序号（job.progress 的 currentIndex）
        var delegatedGroups = new HashSet<string>(StringComparer.Ordinal);
        foreach (var task in taskListCopy)
        {
            scope.ThrowIfStopped();
            if (task is { IsEnabled: true, Action: not null })
            {
                // [A5-1] 回写龙级水位线（R3 原生 GUID 身份）：suspend 时据此保存"被中断的条目"，
                // resume 经 NextTaskId 回灌后从该条目重跑（被中断条目未完成，重跑是既定语义）。
                // 批次跳过的配置组（由助手批次外部驱动）也会推进水位线——它们已逻辑启动。
                scope.SetDragonNode(task.Id);
                // ASTRA 会诊二轮：单项显式执行不适用批次委派——外部驱动者正是经单项执行来跑委派组，
                // 跳过会造成「未执行却报成功」；整龙语义不变（判定入守卫，有夹具承载）
                if (OneDragonFlowExecutionGuards.ShouldSkipForBatchDelegation(
                        scope.Descriptor.TaskId != null, batchGroupNames, task.Name, scriptGroupsDefaultNames.Contains(task.Name))
                    && delegatedGroups.Add(task.Name))
                {
                    // [F03 修正版] 批次委派判断在执行前进行，绝不先跑后跳
                    _logger.LogInformation("批次外部负责配置组 {Name}，本龙执行前跳过", task.Name);
                    continue;
                }

                // [A5-3] job.progress（父作业视角）：外部观察者凭事件族 + ext.job.list 还原执行轨迹。
                // 父作业未登记（观察性故障容错为空）时跳过发布，绝不影响执行。
                enabledOrdinal++;
                if (_currentDragonJobId is { } progressParentId)
                {
                    BetterGenshinImpact.Service.ExternalInterface.ExternalInterfaceEventHub.Instance
                        .PublishJobProgress(progressParentId, enabledOrdinal, enabledTaskCountall, task.Name);
                }

                if (scriptGroupsDefaultNames.Contains(task.Name))
                {
                    _logger.LogInformation($"一条龙任务执行: {finishOneTaskcount++}/{enabledoneTaskCount}");
                    // 默认条目（自动秘境/首领讨伐等）直接 new Task().Start()，不经 ScriptService；
                    // 传入条目名作为 soloTaskName，让 task.status/包络日志携带任务身份（联机助手可识别）
                    // [A2.6] 龙内子项登记进统一注册表（Solo/OneDragonInternal），漏斗终态可靠跟踪；
                    // [A5-2] 子项挂到龙父作业（ParentJobId）；观察槽位抢占留痕不静默。
                    // R4.7 结果传播显式通道（R3 终审挂账）：壶/幽境等吞异常子项的真实结果经通道上报；
                    // 上报失败时以显式异常抛出——子作业终态与根作业聚合拿到真实结果，失败不被后续成功覆盖。
                    // Start 调用外观与公版容错语义不变（原生链继续执行、页面展示语义不变）。
                    using var itemResultChannel = BetterGenshinImpact.GameTask.Common.OneDragonItemResultChannel.OpenScoped();
                    var itemRunResult = await new TaskRunner().RunThreadAsync(async () =>
                    {
                        await task.Action();
                        await Task.Delay(1000);
                        // ASTRA 二轮 B6：取消/正常跳过与失败分别表达——取消显式抛出（子作业终态 Cancelled）；
                        // 正常跳过不抛出，由 TaskRunner 终态前消费通道（注册表 JobState.Skipped，公版原生链继续执行不变）。
                        if (itemResultChannel.Outcome == BetterGenshinImpact.GameTask.Common.OneDragonItemOutcome.Failed)
                            throw new BetterGenshinImpact.GameTask.Common.OneDragonItemFailedException(
                                itemResultChannel.Reason ?? "子项显式上报失败");
                        if (itemResultChannel.Outcome == BetterGenshinImpact.GameTask.Common.OneDragonItemOutcome.Cancelled)
                            throw new OperationCanceledException(
                                itemResultChannel.Reason ?? "子项显式上报取消");
                    }, soloTaskName: task.Name,
                        job: new BetterGenshinImpact.Service.Execution.JobDescriptor(
                            BetterGenshinImpact.Service.Execution.JobKind.Solo, task.Name,
                            BetterGenshinImpact.Service.Execution.JobSource.OneDragonInternal,
                            ParentJobId: _currentDragonJobId));
                    scope.Observe(itemRunResult);
                    if (itemRunResult is TaskRunResult.Preempted or TaskRunResult.Cancelled or TaskRunResult.RejectedSlotBusy) return;
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

                        if (executionConfig.TaskEnabledList.TryGetValue(task.Id, out var enabledFlag) && enabledFlag)
                        {
                            _logger.LogInformation($"配置组任务执行: {finishTaskcount++}/{enabledTaskCount}");
                            await Task.Delay(500);
                            string filePath = Path.Combine(_basePath, _scriptGroupPath, $"{task.Name}.json");
                            var group = ScriptGroup.FromJson(await File.ReadAllTextAsync(filePath));

                            // 创建 taskProgress 并设置 CurrentScriptGroupName，供 HandleTaskSuspend 读取
                            // 也让 RunMulti 内部写入 CurrentScriptGroupProjectInfo（子任务进度）
                            var taskProgress = new BetterGenshinImpact.GameTask.TaskProgress.TaskProgress();
                            taskProgress.CurrentScriptGroupName = group.Name;
                            RunnerContext.Instance.taskProgress = taskProgress;

                            IScriptService? scriptService = App.GetService<IScriptService>();
                            // [A5-2] 子项挂到龙父作业（ParentJobId）；槽位抢占由漏斗显式登记
                            // Rejected(task_busy)（观察面走注册表，同 A2 纪律）。
                            var groupResult = await scriptService!.RunMulti(ScriptControlViewModel.GetNextProjects(group), group.Name, taskProgress,
                                new BetterGenshinImpact.Service.Execution.JobDescriptor(
                                    BetterGenshinImpact.Service.Execution.JobKind.Group, group.Name,
                                    BetterGenshinImpact.Service.Execution.JobSource.OneDragonInternal,
                                    ParentJobId: _currentDragonJobId));
                            scope.Observe(groupResult);
                            if (groupResult is TaskRunResult.Preempted or TaskRunResult.Cancelled or TaskRunResult.RejectedSlotBusy) return;
                            await Task.Delay(1000, scope.Token);
                        }
                    }
                    catch (OperationCanceledException) { throw; }
                    catch (Exception e)
                    {
                        scope.Observe(TaskRunResult.Failed);
                        _logger.LogDebug(e, "执行配置组任务时失败");
                        Toast.Error("执行配置组任务时失败");
                    }
                }
                // 如果任务已经被取消，中断所有任务
                if (scope.Token.IsCancellationRequested)
                {
                    _logger.LogInformation("任务被取消，退出执行");
                    if (CancellationContext.Instance.IsManualStop is false)
                    {
                        Notify.Event(NotificationEvent.DragonEnd).Success("一条龙和配置组任务结束");
                    }
                    return; // 后续的检查任务也不执行
                }
            }
        }

        scope.ThrowIfStopped();
        // E9 根/叶结果合同（R4.6 ASTRA 三轮 B7）：正常跳过（Skipped）不算坏结果——整龙跑完含跳过
        // 仍进入尾部（CheckRewards/通知/CompletionAction 照常，D15）；失败/取消/抢占/拒绝仍提前返回。
        if (!Service.OneDragon.OneDragonRootResultRule.ShouldRunTail(scope.Result)) return;
        if (scope.Descriptor.TaskId != null)
        {
            // R3 单项边界（定案 §8 + ASTRA 会诊）：单项执行不附带整龙收尾（CheckRewardsTask/完成后动作/整龙结束通知）
            _finishMark = true;
            _logger.LogInformation("一条龙单项任务执行完成");
            return;
        }
        // 当次执行配置单完成后，检查和最终结束的任务
        RequireDragonStep(await new TaskRunner().RunThreadAsync(async () =>
        {
            // [D06/F11] 恢复公版尾部检查：领取额外奖励（茶版曾注释掉）
            await new CheckRewardsTask().Start(CancellationContext.Instance.Cts.Token);
            await Task.Delay(500);
            if (CancellationContext.Instance.IsManualStop is false)
            {
                Notify.Event(NotificationEvent.DragonEnd).Success("一条龙和配置组任务结束");
            }
            _logger.LogInformation("一条龙和配置组任务结束");

            // 执行完成后操作（公版语义：单次配置级 CompletionAction）
            // R4.6 D10/E4'：任务中心调用固定 suppress=true——原生完成动作被抑制（流程 terminal.completionAction
            // 在成功边界统一触发一次）；CheckRewardsTask/通知保留（D10 分别归属）；手动入口缺省 false 行为不变。
            if (!scope.SuppressConfigCompletionAction && !string.IsNullOrEmpty(executionConfig.CompletionAction))
            {
                switch (executionConfig.CompletionAction)
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
                        _logger.LogWarning("未知的完成任务类型: {t}", executionConfig.CompletionAction);
                        break;
                }
            }
            _finishMark = true;
        }));
    }
    /// <summary>
    /// 生成与 TaskList 中现有 ID 不重复的唯一 ID。
    /// </summary>
    private string GenerateUniqueTaskId()
    {
        var existingIds = new HashSet<string>(TaskList.Select(t => t.Id));
        string newId;
        do
        {
            newId = Guid.NewGuid().ToString();
        } while (existingIds.Contains(newId));
        return newId;
    }

    [RelayCommand]
    private void CopyTask(OneDragonTaskItem? taskItem)
    {
        if (taskItem == null) return;

        var copy = new OneDragonTaskItem(taskItem.Name) { IsEnabled = taskItem.IsEnabled };
        copy.Id = GenerateUniqueTaskId();

        var index = TaskList.IndexOf(taskItem);
        if (index >= 0)
        {
            TaskList.Insert(index + 1, copy);
        }
        else
        {
            TaskList.Add(copy);
        }

        SaveConfig();
        Toast.Success($"已复制任务: {taskItem.Name}");
    }

    [RelayCommand]
    private void DeleteTask(OneDragonTaskItem? taskItem)
    {
        if (taskItem == null) return;

        TaskList.Remove(taskItem);
        SaveConfig();
        Toast.Success($"已删除任务: {taskItem.Name}");
    }

    [RelayCommand]
    private void SetTaskAsNext(OneDragonTaskItem? taskItem)
    {
        if (taskItem == null) return;
        if (SelectedConfig == null)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }
        if (!taskItem.IsEnabled)
        {
            Toast.Warning($"当前任务 <{taskItem.Name}> 已禁用，请先启用后再从此开始执行");
            return;
        }

        SelectedConfig.NextTaskId = taskItem.Id;
        foreach (var task in TaskList)
        {
            task.IsNextTask = task.Id == taskItem.Id;
        }
        Toast.Success($"设置从 <{taskItem.Name}> 开始执行任务列表");
        SaveConfig();
    }

    [RelayCommand]
    private void DeleteTaskGroup()
    {
        DeleteConfigDisplayTaskListFromConfig();
        SaveConfig();
        InputScriptGroupName = null;
    }

    [RelayCommand]
    private void NextTaskGroup()
    {
        if (SelectedConfig == null)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }

        var currentTask = SelectedTask;
        if (currentTask == null)
        {
            Toast.Warning("请先选择要从此开始执行的任务");
            return;
        }
        if (!currentTask.IsEnabled)
        {
            Toast.Warning($"当前任务 <{currentTask.Name}> 已禁用，请先启用后再从此开始执行");
            return;
        }

        SelectedConfig.NextTaskId = currentTask.Id;
        foreach (var task in TaskList)
        {
            task.IsNextTask = task.Id == currentTask.Id;
        }
        Toast.Success($"设置从 <{currentTask.Name}> 开始执行任务列表");
        SaveConfig();
    }

    [RelayCommand]
    private void ClearNextTaskGroup()
    {
        if (SelectedConfig == null)
        {
            Toast.Warning("请先选择一条龙配置单");
            return;
        }

        SelectedConfig.NextTaskId = string.Empty;
        foreach (var task in TaskList)
        {
            task.IsNextTask = false;
        }
        Toast.Success("清除从此执行标记完成");
        SaveConfig();
    }

    [RelayCommand]
    private void OnAddConfig()
    {
        // 添加配置
        var str = PromptDialog.Prompt("请输入一条龙配置名称", "新增一条龙配置");
        if (!string.IsNullOrEmpty(str))
        {
            // R3.0 同名占用防护：不与待迁移的受保护文件抢名字
            if (IsProtectedConfigFile(Path.Combine(OneDragonFlowConfigFolder, $"{str}.json")))
            {
                Toast.Warning($"名称「{str}」被待迁移的旧版文件占用，请换一个名称");
                return;
            }
            // ASTRA 三轮：新增与重命名同一威胁模型——大小写差异/列表外孤立文件都不得覆盖已有文件
            var addDecision = OneDragonFlowExecutionGuards.EvaluateNewConfigName(
                str,
                ConfigList.Select(x => x.Name),
                File.Exists(Path.Combine(OneDragonFlowConfigFolder, $"{str}.json")));
            if (addDecision == OneDragonRenameDecision.NameConflict)
            {
                Toast.Warning($"一条龙配置 {str} 已经存在，请勿重复添加");
                return;
            }
            if (addDecision == OneDragonRenameDecision.DiskFileConflict)
            {
                Toast.Warning("目标路径已存在配置列表外的文件，为避免误覆盖已拒绝新增");
                return;
            }

            var nc = new OneDragonFlowConfig { Name = str };
            ConfigList.Insert(0, nc);
            SelectedConfig = nc;
        }

        SaveConfig();
    }

    [RelayCommand]
    private async Task DeleteConfig()
    {
        if (SelectedConfig == null)
        {
            Toast.Warning("请先选择要删除的配置");
            return;
        }

        var displayName = SelectedConfig.Name.Length > 14 
            ? $"{SelectedConfig.Name[..4]}...{SelectedConfig.Name[^4..]}" 
            : SelectedConfig.Name;
        var result = await ThemedMessageBox.ShowAsync(
            $"确定要删除配置「{displayName}」吗？", 
            "删除配置", 
            System.Windows.MessageBoxButton.YesNo, 
            ThemedMessageBox.MessageBoxIcon.Question);
        if (result != System.Windows.MessageBoxResult.Yes)
        {
            return;
        }

        try
        {
            // 删除对应的JSON文件
            var configFile = Path.Combine(OneDragonFlowConfigFolder, $"{SelectedConfig.Name}.json");
            // ASTRA 会诊 P0：删除前复检磁盘形状——加载后被外部替换为旧格式/损坏文件（待迁移）时拒绝删除，保留迁移输入
            if (IsProtectedConfigFile(configFile))
            {
                _logger.LogWarning("拒绝删除：{Path} 当前为受保护的旧格式文件（待迁移）", configFile);
                Toast.Error($"配置「{SelectedConfig.Name}」的文件已变为待迁移的旧版格式，已拒绝删除");
                return;
            }
            if (File.Exists(configFile))
            {
                File.Delete(configFile);
            }

            // 从列表中移除
            ConfigList.Remove(SelectedConfig);

            // 如果列表为空，创建默认配置
            if (ConfigList.Count == 0)
            {
                var defaultConfig = new OneDragonFlowConfig
                {
                    Name = "默认配置"
                };
                ConfigList.Add(defaultConfig);
                SelectedConfig = defaultConfig;
                WriteConfig(defaultConfig);
            }
            else
            {
                // 如果还有其他配置，选中第一个
                SelectedConfig = ConfigList[0];
            }

            // 更新全局配置名称
            TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = SelectedConfig.Name;
            
            // 刷新任务列表
            LoadDisplayTaskListFromConfig();
            SelectedTask = null!;
            InputScriptGroupName = string.Empty;
            
            // 保存配置
            SaveConfig();

            Toast.Success("配置删除成功");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "删除配置时失败");
            Toast.Error("删除配置时失败");
        }
    }

    [RelayCommand]
    private void RenameConfig()
    {
        if (SelectedConfig == null)
        {
            Toast.Warning("请先选择要重命名的配置");
            return;
        }

        var newName = PromptDialog.Prompt("请输入新的配置名称", "重命名配置", SelectedConfig.Name);
        if (string.IsNullOrEmpty(newName))
        {
            return;
        }

        if (newName == SelectedConfig.Name)
        {
            return;
        }

        var newFilePath = Path.Combine(OneDragonFlowConfigFolder, $"{newName}.json");
        // R3.0 同名占用防护：不覆盖待迁移的受保护文件
        if (IsProtectedConfigFile(newFilePath))
        {
            Toast.Warning($"名称「{newName}」被待迁移的旧版文件占用，请使用其他名称");
            return;
        }
        // ASTRA 会诊二轮：重命名判定入守卫（仅大小写改名=同一物理文件，写后删旧即删新；磁盘孤立文件不静默覆盖）
        var renameDecision = OneDragonFlowExecutionGuards.EvaluateRename(
            Path.Combine(OneDragonFlowConfigFolder, $"{SelectedConfig.Name}.json"),
            newFilePath,
            newName,
            ConfigList.Where(x => x != SelectedConfig).Select(x => x.Name),
            File.Exists(newFilePath));
        switch (renameDecision)
        {
            case OneDragonRenameDecision.NameConflict:
                Toast.Warning($"配置名称「{newName}」已存在，请使用其他名称");
                return;
            case OneDragonRenameDecision.CaseOnlyRenameRejected:
                Toast.Warning($"名称「{newName}」与当前名称仅大小写不同，二者在磁盘上是同一文件，请先改为其他名称");
                return;
            case OneDragonRenameDecision.DiskFileConflict:
                Toast.Warning("目标路径已存在配置列表外的文件，为避免误覆盖已拒绝重命名");
                return;
        }


        try
        {
            // 保存旧名称
            var oldName = SelectedConfig.Name;
            
            // 更新配置名称
            SelectedConfig.Name = newName;

            // 先写入新文件；写入被保护拒绝或 I/O 失败时中止，绝不删除旧文件（ASTRA 会诊 P0）
            if (!WriteConfig(SelectedConfig))
            {
                SelectedConfig.Name = oldName;
                return;
            }

            // 写入确认提交后再删除旧文件；旧路径同样复检受保护状态
            var oldConfigFile = Path.Combine(OneDragonFlowConfigFolder, $"{oldName}.json");
            if (IsProtectedConfigFile(oldConfigFile))
            {
                _logger.LogWarning("重命名已写入新文件，但旧文件当前为受保护的旧格式（待迁移），保留不删：{Path}", oldConfigFile);
                Toast.Warning($"新配置已保存，旧文件「{oldName}」已变为待迁移的旧版格式，已保留未删除");
            }
            else if (File.Exists(oldConfigFile)
                     && !string.Equals(oldConfigFile, newFilePath, StringComparison.OrdinalIgnoreCase))
            {
                // 路径等价（仅大小写差异）已在守卫阶段拒绝，此处为纵深防御：绝不删除与目标同物理文件的旧路径
                File.Delete(oldConfigFile);
            }

            // 更新全局配置名称
            TaskContext.Instance().Config.SelectedOneDragonFlowConfigName = newName;

            Toast.Success("配置重命名成功");
        }
        catch (Exception e)
        {
            _logger.LogError(e, "重命名配置时失败");
            Toast.Error("重命名配置时失败");
        }
    }
}

