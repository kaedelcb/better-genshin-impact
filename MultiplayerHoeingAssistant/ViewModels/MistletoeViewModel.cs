using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 槲寄生 · 调度器主 ViewModel（当前落地「启动中心」，其余三个子页为规划中占位）。
/// 手写 INPC（继承 ViewModelBase），构造注入 MainViewModel 引用（同 DodocoViewModel 模式）。
///
/// 启动中心定位：进入任务中心前的环境准备编排（树形分支流程：条件节点分出「是/否」两条子链，
/// 子链可嵌套条件；动作节点不含具体 BGI 任务——任务序列由「进入任务中心执行」节点交接）。
///
/// 双端差异（见《槲寄生调度器总计划》§1）：
/// - 监控端只显示「启动中心」Tab（任务中心/执行策略/三方接入整排隐藏，由 XAML 绑 IsExecutorMode 控制）；
/// - 启动中心两端都有，编排的是「本机」启动动作；监控端的 BGI 类节点由执行入口返回失败并记日志；
/// - 流程配置存本机 %APPDATA%/NexusBGI/startup-flow.json，不走 SignalR 同步，两端天然隔离。
/// </summary>
public sealed class MistletoeViewModel : ViewModelBase
{
    private readonly MainViewModel _mainVm;
    private readonly StartupFlowStore _store;
    private readonly StartupFlowRunner _runner;
    private StartupFlowConfig _config;
    private CancellationTokenSource? _runCts;
    /// <summary>字段编辑的防抖保存（避免每敲一个字符写一次盘）。</summary>
    private readonly DispatcherTimer _saveDebounce;

    public MistletoeViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;
        _store = new StartupFlowStore();
        _config = _store.Load();
        _runner = new StartupFlowRunner(mainVm.ExecuteLocalBgiCommandAsync, EnterTaskCenterAsync, mainVm.AddLog);
        RootChain = new StepChainViewModel(_config.Steps, this, parentCondition: null, branchName: "主流程");

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SaveNow();
        };

        // 开机自启动两个参数直接代理 MainViewModel（与设置页同一份），
        // 设置页改完后经 PropertyChanged 转发刷新本页绑定（2026-09-08 修复跨页面显示陈旧）。
        _mainVm.PropertyChanged += (_, e) =>
        {
            if (e.PropertyName is nameof(MainViewModel.AutoLaunchOnBoot))
            {
                OnPropertyChanged(nameof(AutoLaunchOnBoot));
                // 启动方式下拉框的 IsEnabled 依赖开关状态，一并刷新
                OnPropertyChanged(nameof(AutoLaunchOnBootModeIndex));
            }
            else if (e.PropertyName is nameof(MainViewModel.AutoLaunchOnBootModeIndex))
            {
                OnPropertyChanged(nameof(AutoLaunchOnBootModeIndex));
            }
        };
    }

    // ================= Tab 导航（0=启动中心 1=任务中心 2=执行策略 3=三方接入） =================

    private int _selectedTabIndex;
    public int SelectedTabIndex
    {
        get => _selectedTabIndex;
        set => SetProperty(ref _selectedTabIndex, value);
    }

    public RelayCommand SelectTabCommand => new(p =>
    {
        if (p != null && int.TryParse(p.ToString(), out var idx)) SelectedTabIndex = idx;
    });

    /// <summary>返回耕地机主页（同 DodocoViewModel.BackCommand）。</summary>
    public RelayCommand BackCommand => new(_ => _mainVm.CurrentPage = AppPage.Home);

    // ================= 流程总开关与参数 =================

    /// <summary>助手启动后自动执行启动流程。</summary>
    public bool FlowEnabled
    {
        get => _config.Enabled;
        set
        {
            if (_config.Enabled == value) return;
            _config.Enabled = value;
            OnPropertyChanged();
            SaveNow();
            _mainVm.AddLog($"[槲寄生] 启动流程自动执行已{(value ? "开启" : "关闭")}");
        }
    }

    /// <summary>自动执行延迟秒数（文本框绑定，非法输入回退 5）。</summary>
    public string DelaySecondsText
    {
        get => _config.DelaySeconds.ToString();
        set
        {
            var v = int.TryParse(value, out var n) ? Math.Clamp(n, 0, 600) : 5;
            if (_config.DelaySeconds == v) return;
            _config.DelaySeconds = v;
            OnPropertyChanged();
            RequestSave();
        }
    }

    /// <summary>开机自启动（直接复用设置页同款参数 AssistConfig.AutoLaunchOnBoot，双向同步）。</summary>
    public bool AutoLaunchOnBoot
    {
        get => _mainVm.AutoLaunchOnBoot;
        set => _mainVm.AutoLaunchOnBoot = value;
    }

    /// <summary>开机自启动方式下标（0=弹窗启动 1=静默托盘），复用 MainViewModel 同名属性。</summary>
    public int AutoLaunchOnBootModeIndex
    {
        get => _mainVm.AutoLaunchOnBootModeIndex;
        set => _mainVm.AutoLaunchOnBootModeIndex = value;
    }

    // ================= 节点链（树形分支） =================

    /// <summary>主流程链。</summary>
    public StepChainViewModel RootChain { get; }

    /// <summary>节点目录（条件类/动作类），各链的「添加节点」弹层共用。</summary>
    public IReadOnlyList<StartupStepKinds.KindInfo> ConditionKinds { get; } =
        StartupStepKinds.All.Where(k => k.NodeType == "condition").ToList();
    public IReadOnlyList<StartupStepKinds.KindInfo> ActionKinds { get; } =
        StartupStepKinds.All.Where(k => k.NodeType == "action").ToList();

    public bool HasSteps => RootChain.Steps.Count > 0;

    private StartupStepViewModel? _selectedStep;
    /// <summary>当前展开参数编辑器的节点（再点一次收起）。</summary>
    public StartupStepViewModel? SelectedStep
    {
        get => _selectedStep;
        private set
        {
            if (_selectedStep == value) return;
            if (_selectedStep != null) _selectedStep.IsSelected = false;
            _selectedStep = value;
            if (_selectedStep != null) _selectedStep.IsSelected = true;
            OnPropertyChanged();
        }
    }

    public RelayCommand ToggleSelectCommand => new(p =>
    {
        if (p is not StartupStepViewModel vm) return;
        SelectedStep = SelectedStep == vm ? null : vm;
    });

    public RelayCommand RemoveStepCommand => new(p =>
    {
        if (p is not StartupStepViewModel vm) return;
        if (SelectedStep == vm || IsInSubtree(SelectedStep, vm)) SelectedStep = null;
        var chain = vm.OwnerChain;
        var idx = chain.Steps.IndexOf(vm);
        if (idx < 0) return;
        chain.Steps.RemoveAt(idx);
        chain.ModelList.RemoveAt(idx);
        OnPropertyChanged(nameof(HasSteps));
        SaveNow();
        _mainVm.AddLog($"[槲寄生] 已删除节点「{vm.DisplayName}」（{chain.BranchName}）");
    });

    public RelayCommand MoveUpCommand => new(p => MoveStep(p as StartupStepViewModel, -1));
    public RelayCommand MoveDownCommand => new(p => MoveStep(p as StartupStepViewModel, +1));

    private void MoveStep(StartupStepViewModel? vm, int delta)
    {
        if (vm == null) return;
        var chain = vm.OwnerChain;
        var idx = chain.Steps.IndexOf(vm);
        var target = idx + delta;
        if (idx < 0 || target < 0 || target >= chain.Steps.Count) return;
        chain.Steps.Move(idx, target);
        chain.ModelList.RemoveAt(idx);
        chain.ModelList.Insert(target, vm.Model);
        SaveNow();
    }

    /// <summary>
    /// 拖拽重排（支持跨链）：把 dragged 插入 targetChain 的 insertIndex 位置。
    /// 含环检测——目标链是拖拽节点自身的子链（或更深层后代链）时拒绝，
    /// 否则会把条件节点拖进自己的分支里形成环。
    /// </summary>
    public void MoveStepTo(StartupStepViewModel dragged, StepChainViewModel targetChain, int insertIndex)
    {
        for (var c = targetChain; c.ParentCondition != null; c = c.ParentCondition.OwnerChain)
        {
            if (ReferenceEquals(c.ParentCondition, dragged))
            {
                _mainVm.AddLog($"[槲寄生] 不能把「{dragged.DisplayName}」拖进它自己的分支里");
                return;
            }
        }

        var sourceChain = dragged.OwnerChain;
        var oldIndex = sourceChain.Steps.IndexOf(dragged);
        if (oldIndex < 0) return;

        // 同链内移动且原位置在插入点之前：删除后插入点前移一位
        if (ReferenceEquals(sourceChain, targetChain) && oldIndex < insertIndex) insertIndex--;
        insertIndex = Math.Clamp(insertIndex, 0, targetChain.Steps.Count);

        sourceChain.Steps.RemoveAt(oldIndex);
        sourceChain.ModelList.RemoveAt(oldIndex);
        targetChain.Steps.Insert(insertIndex, dragged);
        targetChain.ModelList.Insert(insertIndex, dragged.Model);
        dragged.OwnerChain = targetChain;
        OnPropertyChanged(nameof(HasSteps));
        SaveNow();
        _mainVm.AddLog($"[槲寄生] 节点「{dragged.DisplayName}」已移动到「{targetChain.BranchName}」第 {insertIndex + 1} 位");
    }

    /// <summary>判断 maybeChild 是否在 ancestor 的子树内（删除节点时联动收起编辑器用）。</summary>
    private static bool IsInSubtree(StartupStepViewModel? maybeChild, StartupStepViewModel ancestor)
    {
        if (maybeChild == null) return false;
        for (var c = maybeChild.OwnerChain; c.ParentCondition != null; c = c.ParentCondition.OwnerChain)
        {
            if (ReferenceEquals(c.ParentCondition, ancestor)) return true;
        }
        return false;
    }

    // ================= 执行 =================

    private bool _isRunning;
    public bool IsRunning
    {
        get => _isRunning;
        private set
        {
            if (SetProperty(ref _isRunning, value))
                OnPropertyChanged(nameof(IsNotRunning));
        }
    }

    public bool IsNotRunning => !IsRunning;

    private string _statusText = "尚未执行过";
    /// <summary>最近一次执行的状态描述（页头状态行）。</summary>
    public string StatusText
    {
        get => _statusText;
        private set => SetProperty(ref _statusText, value);
    }

    public RelayCommand RunNowCommand => new(_ => _ = RunFlowAsync(0, "手动"));
    public RelayCommand CancelRunCommand => new(_ => _runCts?.Cancel());

    /// <summary>执行启动流程（delaySeconds&gt;0 时先延时，可被取消）。重入守卫：执行中直接忽略。</summary>
    private async Task RunFlowAsync(int delaySeconds, string reason)
    {
        if (IsRunning) return;
        IsRunning = true;
        _runCts?.Dispose();
        _runCts = new CancellationTokenSource();
        var ct = _runCts.Token;
        try
        {
            if (delaySeconds > 0)
            {
                StatusText = $"{reason}触发：{delaySeconds} 秒后开始执行…";
                _mainVm.AddLog($"[槲寄生] {reason}触发启动流程，{delaySeconds} 秒后开始执行");
                await Task.Delay(TimeSpan.FromSeconds(delaySeconds), ct);
            }

            StatusText = $"正在执行（{reason}触发）…";
            await _runner.RunAsync(_config.Steps.ToList(), ct);
            StatusText = $"上次执行完成（{DateTime.Now:HH:mm:ss}，{reason}触发）";
        }
        catch (OperationCanceledException)
        {
            StatusText = "已取消";
            _mainVm.AddLog("[槲寄生] 启动流程已取消");
        }
        catch (Exception ex)
        {
            StatusText = $"执行异常：{ex.Message}";
            _mainVm.AddLog($"[槲寄生] 启动流程执行异常：{ex.Message}");
        }
        finally
        {
            IsRunning = false;
        }
    }

    /// <summary>MainViewModel.InitializeAsync 完成后由 MainWindow 回调：按配置决定是否自动执行启动流程。</summary>
    internal void OnAppInitialized()
    {
        if (!_config.Enabled) return;
        if (_config.Steps.Count == 0)
        {
            _mainVm.AddLog("[槲寄生] 启动流程已启用但没有节点，自动执行跳过");
            return;
        }
        _ = RunFlowAsync(Math.Max(0, _config.DelaySeconds), "自动");
    }

    /// <summary>
    /// 「进入任务中心执行」节点的交接实现。任务中心（总计划 §3）落地前为占位：
    /// 记日志并返回，流程继续后续节点。落地后在此驱动任务序列。
    /// </summary>
    private Task EnterTaskCenterAsync()
    {
        _mainVm.AddLog("[槲寄生] 任务中心尚未落地（规划中），本次交接为空转——后续节点照常继续");
        return Task.CompletedTask;
    }

    // ================= 保存 =================

    /// <summary>防抖保存（节点参数逐键编辑时走这里）。</summary>
    internal void RequestSave()
    {
        _saveDebounce.Stop();
        _saveDebounce.Start();
    }

    internal void SaveNow()
    {
        _saveDebounce.Stop();
        _store.Save(_config);
    }

    internal void Log(string message) => _mainVm.AddLog(message);
}

/// <summary>
/// 一条节点链（主流程或某个条件节点的是/否分支）的视图模型。
/// 持有模型列表与 VM 集合的双写同步；「＋ 添加节点」弹层状态与命令挂在链上，
/// 使主链与任意分支子链共用同一套交互。
/// </summary>
public sealed class StepChainViewModel : ViewModelBase
{
    private readonly MistletoeViewModel _owner;

    public StepChainViewModel(List<StartupStep> modelList, MistletoeViewModel owner,
        StartupStepViewModel? parentCondition, string branchName)
    {
        ModelList = modelList;
        _owner = owner;
        ParentCondition = parentCondition;
        BranchName = branchName;
        Steps = new ObservableCollection<StartupStepViewModel>(
            modelList.Select(s => new StartupStepViewModel(s, owner, this)));
    }

    /// <summary>对应的模型列表（与 Steps 一一同步）。</summary>
    public List<StartupStep> ModelList { get; }

    public ObservableCollection<StartupStepViewModel> Steps { get; }

    /// <summary>所属条件节点（null = 主流程链）。环检测沿此链向上走。</summary>
    public StartupStepViewModel? ParentCondition { get; }

    /// <summary>链显示名（"主流程" / "「条件名」的是分支" 等，用于日志）。</summary>
    public string BranchName { get; }

    public bool HasSteps => Steps.Count > 0;

    // ---- 「＋ 添加节点」弹层（每条链各一份状态；目录转发宿主的，主链/分支链写法统一） ----

    public IReadOnlyList<StartupStepKinds.KindInfo> ConditionKinds => _owner.ConditionKinds;
    public IReadOnlyList<StartupStepKinds.KindInfo> ActionKinds => _owner.ActionKinds;

    private bool _isAddPopupOpen;
    public bool IsAddPopupOpen
    {
        get => _isAddPopupOpen;
        set => SetProperty(ref _isAddPopupOpen, value);
    }

    public RelayCommand OpenAddPopupCommand => new(_ => IsAddPopupOpen = true);

    public RelayCommand AddStepCommand => new(p =>
    {
        if (p is not string kind) return;
        IsAddPopupOpen = false;
        var model = StartupStepKinds.Create(kind);
        var vm = new StartupStepViewModel(model, _owner, this);
        ModelList.Add(model);
        Steps.Add(vm);
        _owner.ToggleSelectCommand.Execute(vm); // 新节点直接展开编辑器，引导填参数
        _owner.SaveNow();
        _owner.Log($"[槲寄生] 已在「{BranchName}」添加节点「{vm.DisplayName}」");
    });
}

/// <summary>
/// 启动流程节点的视图模型：包装 <see cref="StartupStep"/> 模型，
/// 所有属性写穿到模型并通知宿主防抖保存；Summary 为卡片上的参数摘要行。
/// 条件节点带 TrueChain/FalseChain 两条子链（递归树形结构）。
/// </summary>
public sealed class StartupStepViewModel : ViewModelBase
{
    private readonly MistletoeViewModel _owner;

    public StartupStepViewModel(StartupStep model, MistletoeViewModel owner, StepChainViewModel ownerChain)
    {
        Model = model;
        _owner = owner;
        OwnerChain = ownerChain;
        TrueChain = new StepChainViewModel(model.TrueSteps, owner, this, $"「{DisplayName}」的是分支");
        FalseChain = new StepChainViewModel(model.FalseSteps, owner, this, $"「{DisplayName}」的否分支");
    }

    public StartupStep Model { get; }

    /// <summary>所属链（拖拽跨链移动时更新）。</summary>
    public StepChainViewModel OwnerChain { get; internal set; }

    /// <summary>条件成立（是）子链。</summary>
    public StepChainViewModel TrueChain { get; }

    /// <summary>条件不成立（否）子链。</summary>
    public StepChainViewModel FalseChain { get; }

    public string Kind => Model.Kind;
    public bool IsCondition => Model.NodeType == "condition";
    public string Icon => StartupStepKinds.Find(Model.Kind)?.Icon ?? "▶";
    public string TypeName => StartupStepKinds.Find(Model.Kind)?.DisplayName ?? Model.Kind;

    private bool _isSelected;
    /// <summary>是否展开参数编辑器（由宿主的 SelectedStep 单向驱动）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // ---- 通用字段 ----

    public string DisplayName => string.IsNullOrWhiteSpace(Model.Name) ? TypeName : Model.Name;

    public string Name
    {
        get => Model.Name;
        set { Model.Name = value; Changed(nameof(DisplayName)); }
    }

    public bool Enabled
    {
        get => Model.Enabled;
        set { Model.Enabled = value; Changed(); }
    }

    // ---- 条件参数 ----

    public string TimeStart
    {
        get => Model.TimeStart;
        set { Model.TimeStart = value; Changed(); }
    }

    public string TimeEnd
    {
        get => Model.TimeEnd;
        set { Model.TimeEnd = value; Changed(); }
    }

    public string ProcessName
    {
        get => Model.ProcessName;
        set { Model.ProcessName = value; Changed(); }
    }

    /// <summary>期望运行状态下标：0=正在运行 1=未运行。</summary>
    public int ExpectRunningIndex
    {
        get => Model.ExpectRunning ? 0 : 1;
        set { Model.ExpectRunning = value == 0; Changed(); }
    }

    /// <summary>星期勾选（index 0=周一 … 6=周日）。</summary>
    public bool GetWeekday(int index)
    {
        var day = index + 1;
        return Model.Weekdays.Contains(day);
    }

    public void SetWeekday(int index, bool on)
    {
        var day = index + 1;
        if (on && !Model.Weekdays.Contains(day)) Model.Weekdays.Add(day);
        if (!on) Model.Weekdays.Remove(day);
        Changed();
    }

    // 星期绑定属性（WPF 不能绑方法，W1=周一 … W7=周日）
    public bool W1 { get => GetWeekday(0); set => SetWeekday(0, value); }
    public bool W2 { get => GetWeekday(1); set => SetWeekday(1, value); }
    public bool W3 { get => GetWeekday(2); set => SetWeekday(2, value); }
    public bool W4 { get => GetWeekday(3); set => SetWeekday(3, value); }
    public bool W5 { get => GetWeekday(4); set => SetWeekday(4, value); }
    public bool W6 { get => GetWeekday(5); set => SetWeekday(5, value); }
    public bool W7 { get => GetWeekday(6); set => SetWeekday(6, value); }

    // ---- 动作参数 ----

    public string Path
    {
        get => Model.Path;
        set { Model.Path = value; Changed(); }
    }

    public string Arguments
    {
        get => Model.Arguments;
        set { Model.Arguments = value; Changed(); }
    }

    public string WaitSecondsText
    {
        get => Model.WaitSeconds.ToString();
        set
        {
            Model.WaitSeconds = int.TryParse(value, out var n) ? Math.Clamp(n, 0, 3600) : 10;
            Changed();
        }
    }

    /// <summary>[旧版遗留] startGroup/startOneClick 节点的任务名（目录已移除，旧配置仍可编辑执行）。</summary>
    public string TaskName
    {
        get => Model.TaskName;
        set { Model.TaskName = value; Changed(); }
    }

    // ---- 卡片摘要行 ----

    public string Summary => Model.Kind switch
    {
        StartupStepKinds.TimeRange => $"{Model.TimeStart} ~ {Model.TimeEnd}",
        StartupStepKinds.Weekday => Model.Weekdays.Count == 7
            ? "每天"
            : Model.Weekdays.Count == 0
                ? "（未勾选任何星期）"
                : "周" + string.Join("、", Model.Weekdays.Order().Select(d => "一二三四五六日"[d - 1])),
        StartupStepKinds.BgiRunning => Model.ExpectRunning ? "BGI 正在运行 → 是" : "BGI 未运行 → 是",
        StartupStepKinds.GameRunning => Model.ExpectRunning ? "游戏正在运行 → 是" : "游戏未运行 → 是",
        StartupStepKinds.ProcessRunning => $"{Model.ProcessName} {(Model.ExpectRunning ? "存在" : "不存在")} → 是",
        StartupStepKinds.StartBgi => "启动本机 BGI",
        StartupStepKinds.StopBgi => "强制结束本会话 BGI 进程",
        StartupStepKinds.StartGame or StartupStepKinds.StartProgram =>
            string.IsNullOrWhiteSpace(Model.Path) ? "（未填写程序路径）" : Model.Path,
        StartupStepKinds.RunCmd => string.IsNullOrWhiteSpace(Model.Arguments) ? "（未填写命令）" : Model.Arguments,
        StartupStepKinds.Wait => $"等待 {Model.WaitSeconds} 秒",
        StartupStepKinds.EnterTaskCenter => "交接给任务中心执行任务序列",
        StartupStepKinds.EndFlow => "立即终止整条启动流程",
        StartupStepKinds.StartGroup => string.IsNullOrWhiteSpace(Model.TaskName) ? "（旧版节点 · 未填写配置组名）" : $"（旧版节点）配置组「{Model.TaskName}」",
        StartupStepKinds.StartOneClick => string.IsNullOrWhiteSpace(Model.TaskName) ? "（旧版节点 · 未填写一条龙名）" : $"（旧版节点）一条龙「{Model.TaskName}」",
        _ => "",
    };

    private void Changed(string? extraProperty = null)
    {
        if (extraProperty != null) OnPropertyChanged(extraProperty);
        OnPropertyChanged(nameof(Summary));
        _owner.RequestSave();
    }
}
