using System.Collections.ObjectModel;
using System.Windows;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// 槲寄生 · 调度器主 ViewModel（当前落地「启动中心」与任务中心的 BGI 任务状态判断，其余子页为规划中占位）。
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
    private readonly StartupFlowSchemeStore _schemeStore;
    private readonly StartupFlowRunner _runner;
    private StartupFlowConfig _config;
    private CancellationTokenSource? _runCts;
    /// <summary>字段编辑的防抖保存（避免每敲一个字符写一次盘）。</summary>
    private readonly DispatcherTimer _saveDebounce;

    public MistletoeViewModel(MainViewModel mainVm)
    {
        _mainVm = mainVm;
        _store = new StartupFlowStore();
        _schemeStore = new StartupFlowSchemeStore();
        _config = _store.Load();
        _runner = new StartupFlowRunner(mainVm.ExecuteLocalBgiCommandAsync, EnterTaskCenterAsync, ArmTimer, ConfirmHandlerAsync, mainVm.AddLog);
        _runner.NodeStateSink = OnNodeStateReported;
        RootChain = new StepChainViewModel(_config.Steps, this, parentCondition: null, branchName: "主流程");
        foreach (var s in _schemeStore.Load()) Schemes.Add(new SchemeItemViewModel(s));
        ArmedTimers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasArmedTimers));

        _saveDebounce = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(500) };
        _saveDebounce.Tick += (_, _) =>
        {
            _saveDebounce.Stop();
            SaveNow();
        };

        // 任务中心「BGI 任务状态」卡片：2s 一拍读 MainViewModel 的 10s 状态快照缓存刷新绑定。
        // 快照每轮换新实例且无 PropertyChanged 通知（LatestLocalStatus 为普通自动属性），故用定时器拉取，不新起 IPC。
        _bgiStatusRefresh = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
        _bgiStatusRefresh.Tick += (_, _) => RefreshBgiTaskStatus();
        _bgiStatusRefresh.Start();
        RefreshBgiTaskStatus();

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

    // ================= 任务中心：BGI 任务状态判断（只读 LatestLocalStatus 快照，2s 定时拉取） =================

    /// <summary>状态卡片刷新器（快照每轮为新实例且无变更通知，只能拉不能订）。</summary>
    private readonly DispatcherTimer _bgiStatusRefresh;

    private bool _bgiTaskRunning;
    /// <summary>BGI 当前是否有任务在跑。判断口径用快照 TaskRunning，不用任务名非空——任务停止后任务名有残留窗口。</summary>
    public bool BgiTaskRunning
    {
        get => _bgiTaskRunning;
        private set => SetProperty(ref _bgiTaskRunning, value);
    }

    private string _bgiTaskStatusText = "暂无状态（等待 BGI 首次状态上报）";
    /// <summary>任务中心状态行文字。</summary>
    public string BgiTaskStatusText
    {
        get => _bgiTaskStatusText;
        private set => SetProperty(ref _bgiTaskStatusText, value);
    }

    private string _bgiCurrentTaskDisplay = "—";
    /// <summary>当前任务显示（配置组 · 任务名/线路，与主页 TaskDisplayText 同拼接口径）。</summary>
    public string BgiCurrentTaskDisplay
    {
        get => _bgiCurrentTaskDisplay;
        private set => SetProperty(ref _bgiCurrentTaskDisplay, value);
    }

    /// <summary>读 MainViewModel 最近一次状态快照刷新绑定属性（快照为只读缓存，不新起 IPC 轮询）。</summary>
    private void RefreshBgiTaskStatus()
    {
        var s = _mainVm.LatestLocalStatus;
        if (s == null)
        {
            BgiTaskRunning = false;
            BgiTaskStatusText = "暂无状态（等待 BGI 首次状态上报）";
            BgiCurrentTaskDisplay = "—";
            return;
        }
        BgiTaskRunning = s.TaskRunning;
        BgiTaskStatusText = s.TaskRunning ? "BGI 任务运行中" : "BGI 空闲（无任务运行）";
        BgiCurrentTaskDisplay = s.TaskRunning ? ComposeTaskDisplay(s) : "—";
    }

    /// <summary>拼接任务显示文本：配置组 · 任务名 · 线路（空段跳过；联机锄地时线路优先于任务名）。与 ControlRoom 玩家卡片 TaskDisplayText 同口径。</summary>
    private static string ComposeTaskDisplay(ControlStatus s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(s.CurrentTaskGroupName)) parts.Add(s.CurrentTaskGroupName);
        if (!string.IsNullOrEmpty(s.CurrentTaskName) && string.IsNullOrEmpty(s.CurrentRouteDisplay)) parts.Add(s.CurrentTaskName);
        if (!string.IsNullOrEmpty(s.CurrentRouteDisplay)) parts.Add(s.CurrentRouteDisplay);
        return parts.Count > 0 ? string.Join(" · ", parts) : s.CurrentTaskName ?? "任务执行中";
    }

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

    /// <summary>执行时自动展开实际走到的分支（持久化在启动流程配置里）。</summary>
    public bool AutoExpandTakenBranch
    {
        get => _config.AutoExpandBranch;
        set
        {
            if (_config.AutoExpandBranch == value) return;
            _config.AutoExpandBranch = value;
            OnPropertyChanged();
            SaveNow();
        }
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

    // ---- 分支一键展开/收起（递归整棵树；运行态不持久化） ----

    private bool _allBranchesExpanded;
    public string AllBranchesToggleText => _allBranchesExpanded ? "⤡ 收起全部分支" : "⤢ 展开全部分支";

    public RelayCommand ToggleAllBranchesCommand => new(_ =>
    {
        _allBranchesExpanded = !_allBranchesExpanded;
        SetBranchesExpandedRecursive(RootChain, _allBranchesExpanded);
        OnPropertyChanged(nameof(AllBranchesToggleText));
    });

    private static void SetBranchesExpandedRecursive(StepChainViewModel chain, bool expanded)
    {
        foreach (var step in chain.Steps)
        {
            step.BranchesExpanded = expanded;
            SetBranchesExpandedRecursive(step.TrueChain, expanded);
            SetBranchesExpandedRecursive(step.FalseChain, expanded);
            SetBranchesExpandedRecursive(step.FireChain, expanded);
        }
    }

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
        // 「结束流程」必须保持在链尾：它自己不能上移，其他节点也不能下移越过它
        if (vm.Kind == StartupStepKinds.EndFlow || chain.Steps[target].Kind == StartupStepKinds.EndFlow)
        {
            _mainVm.AddLog("[槲寄生] 「结束流程」必须是链的最后一个节点，不能这样移动");
            return;
        }
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

        // 先从原链移除，再按「结束流程必须在链尾」规则换算目标位置
        sourceChain.Steps.RemoveAt(oldIndex);
        sourceChain.ModelList.RemoveAt(oldIndex);

        if (dragged.Kind == StartupStepKinds.EndFlow)
        {
            // 结束流程只能落在目标链尾部
            insertIndex = targetChain.Steps.Count;
        }
        else
        {
            // 同链内移动且原位置在插入点之前：删除后插入点前移一位
            if (ReferenceEquals(sourceChain, targetChain) && oldIndex < insertIndex) insertIndex--;
            insertIndex = Math.Clamp(insertIndex, 0, targetChain.Steps.Count);
            // 目标链已有结束流程：普通节点不能插到它后面（它后面的节点永远不会执行）
            var endIdx = -1;
            for (var i = 0; i < targetChain.Steps.Count; i++)
            {
                if (targetChain.Steps[i].Kind == StartupStepKinds.EndFlow) { endIdx = i; break; }
            }
            if (endIdx >= 0 && insertIndex > endIdx)
            {
                insertIndex = endIdx;
                _mainVm.AddLog($"[槲寄生] 「{targetChain.BranchName}」以「结束流程」收尾，节点已自动放到它前面");
            }
        }

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
            ResetRunStates();
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

    // ================= 执行路径可视化（节点运行态） =================

    private bool _hasAnyRunState;
    /// <summary>当前有任何节点带运行态标记（控制「清除路径显示」按钮显隐）。</summary>
    public bool HasAnyRunState
    {
        get => _hasAnyRunState;
        private set => SetProperty(ref _hasAnyRunState, value);
    }

    /// <summary>手动清除本次执行留下的路径显示（节点状态徽标、分支灰显全部复位）。</summary>
    public RelayCommand ClearRunStateDisplayCommand => new(_ =>
    {
        ResetRunStates();
        _mainVm.AddLog("[槲寄生] 已清除执行路径显示");
    });

    /// <summary>
    /// Runner 的节点状态回报入口（可能在线程池线程上）：定位对应节点 VM 并更新运行态。
    /// 条件节点出结果时联动：走过的分支自动展开（可在总控关掉）、没走的分支灰显。
    /// </summary>
    private void OnNodeStateReported(StartupStep step, NodeRunState state, string? note)
    {
        RunOnUi(() =>
        {
            var vm = FindStepVm(RootChain, step);
            if (vm == null) return; // 节点在 VM 树里找不到（理论上不该发生），只丢可视化不影响执行
            vm.RunState = state;
            vm.RunStateNote = note ?? "";
            HasAnyRunState = true;
            if (state == NodeRunState.CondTrue || state == NodeRunState.CondFalse)
            {
                var tookTrue = state == NodeRunState.CondTrue;
                vm.TrueChain.BranchDimmed = !tookTrue;
                vm.FalseChain.BranchDimmed = tookTrue;
                if (AutoExpandTakenBranch) vm.BranchesExpanded = true;
            }
        });
    }

    /// <summary>按模型引用在 VM 树里递归定位节点（树很小，直接走查）。</summary>
    private static StartupStepViewModel? FindStepVm(StepChainViewModel chain, StartupStep model)
    {
        foreach (var vm in chain.Steps)
        {
            if (ReferenceEquals(vm.Model, model)) return vm;
            var hit = FindStepVm(vm.TrueChain, model)
                      ?? FindStepVm(vm.FalseChain, model)
                      ?? FindStepVm(vm.FireChain, model);
            if (hit != null) return hit;
        }
        return null;
    }

    /// <summary>每次执行前重置整棵树的运行态（节点状态 + 分支灰显），让本次路径从零画起。</summary>
    private void ResetRunStates()
    {
        ResetRunStatesRecursive(RootChain);
        HasAnyRunState = false;
    }

    private static void ResetRunStatesRecursive(StepChainViewModel chain)
    {
        chain.BranchDimmed = false;
        foreach (var step in chain.Steps)
        {
            step.RunState = NodeRunState.None;
            step.RunStateNote = "";
            ResetRunStatesRecursive(step.TrueChain);
            ResetRunStatesRecursive(step.FalseChain);
            ResetRunStatesRecursive(step.FireChain);
        }
    }

    /// <summary>「进入任务中心执行」节点的交接实现。任务中心（总计划 §3）落地前为占位：
    /// 先判断并记录 BGI 当前任务状态与任务名（读 LatestLocalStatus 快照，同任务中心状态卡片口径），
    /// 流程继续后续节点。落地后在此驱动任务序列。
    /// </summary>
    private Task EnterTaskCenterAsync()
    {
        var s = _mainVm.LatestLocalStatus;
        var judgment = s == null
            ? "BGI 状态未知（尚无状态快照）"
            : s.TaskRunning
                ? $"BGI 正在运行任务「{ComposeTaskDisplay(s)}」"
                : "BGI 当前空闲";
        _mainVm.AddLog($"[槲寄生] 任务中心交接判断：{judgment}。任务中心尚未落地（规划中），本次交接为空转——后续节点照常继续");
        return Task.CompletedTask;
    }

    /// <summary>
    /// 「人工确认」节点的弹窗实现（Runner 注入的委托）。回 UI 线程弹模态窗，
    /// 返回 (走向, 判断依据)。弹窗期间流程挂起等待；用户取消整个流程需先关掉弹窗。
    /// </summary>
    private async Task<(bool passed, string desc)> ConfirmHandlerAsync(StartupStep step, CancellationToken ct)
    {
        var result = await Application.Current.Dispatcher.InvokeAsync(
            () => ConfirmStepWindow.ShowConfirm(step, Application.Current.MainWindow));
        ct.ThrowIfCancellationRequested();
        return result;
    }

    // ================= 定时触发器（武装中的定时器列表） =================

    /// <summary>当前定时中的触发器（运行态，不持久化；助手重启后需流程重跑才会重新挂载）。</summary>
    public ObservableCollection<ArmedTimerViewModel> ArmedTimers { get; } = [];

    public bool HasArmedTimers => ArmedTimers.Count > 0;

    /// <summary>定时触发器节点执行到此：校验参数后挂载定时器（Runner 注入的委托）。</summary>
    private void ArmTimer(StartupStep step)
    {
        if (!TimeOnly.TryParse(step.TriggerTime, out var t))
        {
            _mainVm.AddLog($"[槲寄生] 定时触发器时间格式无效（{step.TriggerTime}），未挂载");
            return;
        }
        var fireAt = NextOccurrence(t);
        var timer = new ArmedTimerViewModel(step, fireAt, this);
        RunOnUi(() => ArmedTimers.Add(timer));
        _mainVm.AddLog($"[槲寄生] 定时触发器「{StartupFlowRunner.DisplayName(step, 0)}」已挂载：{fireAt:MM-dd HH:mm} 触发「到点执行」链（{step.FireSteps.Count} 个节点{(step.RepeatDaily ? "，每天重复" : "" )}）");
        _ = RunTimerAsync(timer);
    }

    /// <summary>今天的该时刻未到则今天，否则明天。</summary>
    private static DateTime NextOccurrence(TimeOnly t)
    {
        var now = DateTime.Now;
        var at = now.Date + t.ToTimeSpan();
        return at > now ? at : at.AddDays(1);
    }

    private async Task RunTimerAsync(ArmedTimerViewModel timer)
    {
        try
        {
            var delay = timer.NextFireAt - DateTime.Now;
            if (delay > TimeSpan.Zero)
                await Task.Delay(delay, timer.Cts.Token);

            RunOnUi(() => ArmedTimers.Remove(timer));
            var step = timer.Step;
            _mainVm.AddLog($"[槲寄生] 定时触发器「{StartupFlowRunner.DisplayName(step, 0)}」到点（{DateTime.Now:HH:mm}），开始执行「到点执行」链");
            OnNodeStateReported(step, NodeRunState.Running, null);
            await _runner.RunAsync(step.FireSteps.ToList(), timer.Cts.Token);
            OnNodeStateReported(step, NodeRunState.Success, $"已于 {DateTime.Now:HH:mm} 触发");

            // 每天重复：本轮跑完后重新挂载到明天的同一时刻（取消语义不走到这里）
            if (step.RepeatDaily && TimeOnly.TryParse(step.TriggerTime, out var t))
            {
                var fireAt = NextOccurrence(t);
                timer.Reset(fireAt);
                RunOnUi(() => ArmedTimers.Add(timer));
                _mainVm.AddLog($"[槲寄生] 定时触发器「{StartupFlowRunner.DisplayName(step, 0)}」已按「每天重复」重新挂载：{fireAt:MM-dd HH:mm}");
                _ = RunTimerAsync(timer);
            }
        }
        catch (OperationCanceledException)
        {
            _mainVm.AddLog($"[槲寄生] 定时触发器「{timer.Title}」已取消");
        }
        catch (Exception ex)
        {
            _mainVm.AddLog($"[槲寄生] 定时触发器「{timer.Title}」执行异常：{ex.Message}");
        }
    }

    /// <summary>取消一个定时中的触发器（页面「取消」按钮）。</summary>
    internal void CancelTimer(ArmedTimerViewModel timer)
    {
        RunOnUi(() => ArmedTimers.Remove(timer));
        timer.Cts.Cancel();
    }

    /// <summary>ObservableCollection 的增删必须回 UI 线程（定时器回调可能在线程池线程上）。</summary>
    private static void RunOnUi(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) action();
        else dispatcher.Invoke(action);
    }

    // ================= 方案管理（命名保存 / 恢复整份启动设置） =================

    /// <summary>恢复前自动备份的固定方案名（每次恢复前覆盖刷新，防手滑）。</summary>
    private const string AutoBackupSchemeName = "恢复前自动备份";

    /// <summary>已保存的方案列表（按保存时间倒序展示）。</summary>
    public ObservableCollection<SchemeItemViewModel> Schemes { get; } = [];

    private string _schemeNameText = "";
    /// <summary>「保存方案」的名称输入。</summary>
    public string SchemeNameText
    {
        get => _schemeNameText;
        set => SetProperty(ref _schemeNameText, value);
    }

    private SchemeItemViewModel? _selectedScheme;
    /// <summary>方案下拉框当前选中的方案。</summary>
    public SchemeItemViewModel? SelectedScheme
    {
        get => _selectedScheme;
        set
        {
            if (SetProperty(ref _selectedScheme, value))
                OnPropertyChanged(nameof(HasSelectedScheme));
        }
    }

    public bool HasSelectedScheme => SelectedScheme != null;

    public RelayCommand SaveSchemeCommand => new(_ =>
    {
        var name = SchemeNameText.Trim();
        if (string.IsNullOrEmpty(name))
        {
            _mainVm.AddLog("[槲寄生] 请先给方案起个名字再保存");
            return;
        }
        var existing = Schemes.FirstOrDefault(s => s.Scheme.Name == name);
        if (existing != null)
        {
            // 同名覆盖更新
            existing.Scheme.SavedAt = DateTime.Now;
            existing.Scheme.Config = StartupFlowSchemeStore.Clone(_config);
            existing.RefreshDisplay();
            _mainVm.AddLog($"[槲寄生] 已覆盖更新方案「{name}」（{existing.Scheme.Config.Steps.Count} 个主流程节点）");
        }
        else
        {
            var scheme = new StartupFlowScheme
            {
                Name = name,
                SavedAt = DateTime.Now,
                Config = StartupFlowSchemeStore.Clone(_config),
            };
            Schemes.Insert(0, new SchemeItemViewModel(scheme));
            _mainVm.AddLog($"[槲寄生] 已保存方案「{name}」（{scheme.Config.Steps.Count} 个主流程节点）");
        }
        PersistSchemes();
        SchemeNameText = "";
    });

    public RelayCommand RestoreSchemeCommand => new(_ =>
    {
        var item = SelectedScheme;
        if (item == null) return;
        if (IsRunning)
        {
            _mainVm.AddLog("[槲寄生] 流程正在执行中，请先取消或等执行完再恢复方案");
            return;
        }

        // 恢复前自动备份当前配置（覆盖同名，始终只留最新一份）
        var backup = Schemes.FirstOrDefault(s => s.Scheme.Name == AutoBackupSchemeName);
        if (backup != null)
        {
            backup.Scheme.SavedAt = DateTime.Now;
            backup.Scheme.Config = StartupFlowSchemeStore.Clone(_config);
            backup.RefreshDisplay();
        }
        else
        {
            backup = new SchemeItemViewModel(new StartupFlowScheme
            {
                Name = AutoBackupSchemeName,
                SavedAt = DateTime.Now,
                Config = StartupFlowSchemeStore.Clone(_config),
            });
            Schemes.Add(backup);
        }

        ApplyConfig(StartupFlowSchemeStore.Clone(item.Scheme.Config));
        PersistSchemes();
        _mainVm.AddLog($"[槲寄生] 已恢复方案「{item.Scheme.Name}」（保存于 {item.Scheme.SavedAt:MM-dd HH:mm}）；恢复前的配置已自动备份为「{AutoBackupSchemeName}」");
    });

    public RelayCommand DeleteSchemeCommand => new(_ =>
    {
        var item = SelectedScheme;
        if (item == null) return;
        Schemes.Remove(item);
        SelectedScheme = null;
        PersistSchemes();
        _mainVm.AddLog($"[槲寄生] 已删除方案「{item.Scheme.Name}」");
    });

    private void PersistSchemes()
    {
        _schemeStore.SaveAll(Schemes.Select(s => s.Scheme).ToList());
    }

    /// <summary>
    /// 用一份配置原位替换当前配置（清空再填充 _config.Steps，不换引用——
    /// RootChain.ModelList 持有的正是这个列表），并重建 VM 树、立即落盘。
    /// </summary>
    private void ApplyConfig(StartupFlowConfig config)
    {
        SelectedStep = null;
        _config.Enabled = config.Enabled;
        _config.DelaySeconds = config.DelaySeconds;
        _config.AutoExpandBranch = config.AutoExpandBranch;
        _config.Steps.Clear();
        _config.Steps.AddRange(config.Steps);

        RootChain.Steps.Clear();
        foreach (var model in _config.Steps)
            RootChain.Steps.Add(new StartupStepViewModel(model, this, RootChain));

        OnPropertyChanged(nameof(FlowEnabled));
        OnPropertyChanged(nameof(DelaySecondsText));
        OnPropertyChanged(nameof(AutoExpandTakenBranch));
        OnPropertyChanged(nameof(HasSteps));
        SaveNow();
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
        // 增删移动后联动刷新：空链提示（HasSteps）、结束流程链尾约束（HasEndFlow/HasNoEndFlow）
        Steps.CollectionChanged += (_, _) =>
        {
            OnPropertyChanged(nameof(HasSteps));
            OnPropertyChanged(nameof(HasEndFlow));
            OnPropertyChanged(nameof(HasNoEndFlow));
        };
    }

    /// <summary>对应的模型列表（与 Steps 一一同步）。</summary>
    public List<StartupStep> ModelList { get; }

    public ObservableCollection<StartupStepViewModel> Steps { get; }

    /// <summary>所属条件节点（null = 主流程链）。环检测沿此链向上走。</summary>
    public StartupStepViewModel? ParentCondition { get; }

    /// <summary>链显示名（"主流程" / "「条件名」的是分支" 等，用于日志）。</summary>
    public string BranchName { get; }

    public bool HasSteps => Steps.Count > 0;

    /// <summary>链中已有「结束流程」节点（它必须是链尾终点：之后不能再加任何节点，也不能拖节点到它后面）。</summary>
    public bool HasEndFlow => Steps.Any(s => s.Kind == StartupStepKinds.EndFlow);

    /// <summary>「＋ 添加节点」按钮可用性绑定用（HasEndFlow 的反相，WPF 无内置反转转换器）。</summary>
    public bool HasNoEndFlow => !HasEndFlow;

    // ---- 执行路径可视化：未走到的分支整链灰显（运行态不持久化） ----

    private bool _branchDimmed;
    /// <summary>本链所属的条件判断走了另一条分支 → 整链灰显（一眼看出"这条路没走"）。</summary>
    public bool BranchDimmed
    {
        get => _branchDimmed;
        set => SetProperty(ref _branchDimmed, value);
    }

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
        // 「结束流程」是链尾终点：链里已有它时不能再添加任何节点（新节点永远追加在尾，必然落在它后面成为死节点）
        if (HasEndFlow)
        {
            _owner.Log($"[槲寄生] 「{BranchName}」已有「结束流程」节点，它是终点，之后不能再添加节点");
            return;
        }
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
        FireChain = new StepChainViewModel(model.FireSteps, owner, this, $"「{DisplayName}」的到点执行链");
    }

    public StartupStep Model { get; }

    /// <summary>所属链（拖拽跨链移动时更新）。</summary>
    public StepChainViewModel OwnerChain { get; internal set; }

    /// <summary>条件成立（是）子链。</summary>
    public StepChainViewModel TrueChain { get; }

    /// <summary>条件不成立（否）子链。</summary>
    public StepChainViewModel FalseChain { get; }

    /// <summary>定时触发器的「到点执行」子链（仅 timerTrigger 使用；环检测与跨链拖拽与分支链同机制）。</summary>
    public StepChainViewModel FireChain { get; }

    public string Kind => Model.Kind;
    public bool IsCondition => Model.NodeType == "condition";
    public bool IsTimerTrigger => Model.Kind == StartupStepKinds.TimerTrigger;
    public string Icon => StartupStepKinds.Find(Model.Kind)?.Icon ?? "▶";
    public string TypeName => StartupStepKinds.Find(Model.Kind)?.DisplayName ?? Model.Kind;

    private bool _isSelected;
    /// <summary>是否展开参数编辑器（由宿主的 SelectedStep 单向驱动）。</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set => SetProperty(ref _isSelected, value);
    }

    // ---- 分支区折叠（仅条件节点有意义；运行态不持久化，默认折叠） ----

    private bool _branchesExpanded;
    /// <summary>是/否分支区域是否展开（默认折叠，保持主流程一眼看到底）。</summary>
    public bool BranchesExpanded
    {
        get => _branchesExpanded;
        set
        {
            if (SetProperty(ref _branchesExpanded, value))
            {
                OnPropertyChanged(nameof(BranchAreaVisible));
                OnPropertyChanged(nameof(BranchToggleText));
            }
        }
    }

    /// <summary>分支区域可见性绑定用（条件节点 + 已展开才显示）。</summary>
    public bool BranchAreaVisible => IsCondition && BranchesExpanded;

    public string BranchToggleText => BranchesExpanded ? "▾ 分支" : "▸ 分支";

    public RelayCommand ToggleBranchesCommand => new(_ => BranchesExpanded = !BranchesExpanded);

    // ---- 执行路径可视化：节点运行态（运行态不持久化，每次执行前由宿主重置） ----

    private NodeRunState _runState = NodeRunState.None;
    /// <summary>本次执行中该节点的运行态（驱动卡片边框色与状态徽标）。</summary>
    public NodeRunState RunState
    {
        get => _runState;
        set
        {
            if (SetProperty(ref _runState, value))
            {
                OnPropertyChanged(nameof(HasRunState));
                OnPropertyChanged(nameof(RunStateBadgeText));
            }
        }
    }

    private string _runStateNote = "";
    /// <summary>运行态附注（条件节点的判断依据，如「当前 10:32，区间 08:00~12:00」）。</summary>
    public string RunStateNote
    {
        get => _runStateNote;
        set
        {
            if (SetProperty(ref _runStateNote, value))
                OnPropertyChanged(nameof(HasRunStateNote));
        }
    }

    public bool HasRunState => RunState != NodeRunState.None;

    public bool HasRunStateNote => !string.IsNullOrWhiteSpace(RunStateNote);

    /// <summary>卡片上的状态徽标文字。</summary>
    public string RunStateBadgeText => RunState switch
    {
        NodeRunState.Running => "执行中…",
        NodeRunState.Success => "✓ 完成",
        NodeRunState.Failed => "✗ 失败",
        NodeRunState.Skipped => "已跳过",
        NodeRunState.CondTrue => "→ 是",
        NodeRunState.CondFalse => "→ 否",
        _ => "",
    };

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

    /// <summary>触发时间 HH:mm（timerTrigger 用）。</summary>
    public string TriggerTime
    {
        get => Model.TriggerTime;
        set { Model.TriggerTime = value; Changed(); }
    }

    /// <summary>每天重复触发（timerTrigger 用）。</summary>
    public bool RepeatDaily
    {
        get => Model.RepeatDaily;
        set { Model.RepeatDaily = value; Changed(); }
    }

    /// <summary>启动前先关闭 BGI（startBgi 用，让参数生效）。</summary>
    public bool KillBeforeStart
    {
        get => Model.KillBeforeStart;
        set { Model.KillBeforeStart = value; Changed(); }
    }

    /// <summary>弹窗提示内容（manualConfirm 用）。</summary>
    public string ConfirmMessage
    {
        get => Model.ConfirmMessage;
        set { Model.ConfirmMessage = value; Changed(); }
    }

    /// <summary>超时秒数文本（manualConfirm 用；0=不限时）。</summary>
    public string ConfirmTimeoutSecondsText
    {
        get => Model.ConfirmTimeoutSeconds.ToString();
        set
        {
            Model.ConfirmTimeoutSeconds = int.TryParse(value, out var n) ? Math.Clamp(n, 0, 86400) : 60;
            Changed();
        }
    }

    /// <summary>超时走向下标（manualConfirm 用）：0=超时走「是」，1=超时走「否」。</summary>
    public int TimeoutGoTrueIndex
    {
        get => Model.ConfirmTimeoutGoTrue ? 0 : 1;
        set { Model.ConfirmTimeoutGoTrue = value == 0; Changed(); }
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
        StartupStepKinds.ManualConfirm =>
            $"{(string.IsNullOrWhiteSpace(Model.ConfirmMessage) ? "（未填提示内容）" : Model.ConfirmMessage)}" +
            $"{(Model.ConfirmTimeoutSeconds > 0 ? $"；{Model.ConfirmTimeoutSeconds} 秒超时走「{(Model.ConfirmTimeoutGoTrue ? "是" : "否")}」" : "；不限时")}",
        StartupStepKinds.StartBgi => (string.IsNullOrWhiteSpace(Model.Arguments) ? "启动本机 BGI" : $"启动本机 BGI（参数：{Model.Arguments}）")
            + (Model.KillBeforeStart ? "，先关闭再启动" : ""),
        StartupStepKinds.StopBgi => "强制结束本会话 BGI 进程",
        StartupStepKinds.StartGame or StartupStepKinds.StartProgram =>
            string.IsNullOrWhiteSpace(Model.Path) ? "（未填写程序路径）" : Model.Path,
        StartupStepKinds.RunCmd => string.IsNullOrWhiteSpace(Model.Arguments) ? "（未填写命令）" : Model.Arguments,
        StartupStepKinds.KillProgram => string.IsNullOrWhiteSpace(Model.ProcessName) ? "（未填写进程名）" : $"结束进程 {Model.ProcessName}",
        StartupStepKinds.Wait => $"等待 {Model.WaitSeconds} 秒",
        StartupStepKinds.TimerTrigger =>
            $"{Model.TriggerTime} 触发「到点执行」链（{Model.FireSteps.Count} 个节点{(Model.RepeatDaily ? "，每天重复" : "")}）",
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

/// <summary>
/// 一个已保存方案的列表项视图模型：包一层为了下拉框展示行（名称+保存时间）。
/// 重写 ToString 是因为鎏金下拉框的选中框直接显示 SelectionBoxItem 的文本。
/// </summary>
public sealed class SchemeItemViewModel : ViewModelBase
{
    public SchemeItemViewModel(StartupFlowScheme scheme)
    {
        Scheme = scheme;
    }

    /// <summary>对应的方案模型。</summary>
    public StartupFlowScheme Scheme { get; }

    private string _displayLine = "";
    /// <summary>下拉行文本：方案名（MM-dd HH:mm）。</summary>
    public string DisplayLine
    {
        get => string.IsNullOrEmpty(_displayLine) ? BuildDisplayLine() : _displayLine;
        private set => SetProperty(ref _displayLine, value);
    }

    /// <summary>覆盖更新方案后刷新展示行。</summary>
    public void RefreshDisplay() => DisplayLine = BuildDisplayLine();

    private string BuildDisplayLine() => $"{Scheme.Name}（{Scheme.SavedAt:MM-dd HH:mm}）";

    public override string ToString() => DisplayLine;
}

/// <summary>
/// 一个已挂载（定时中）的定时触发器的视图模型：页面「定时中的触发器」卡片的行项。
/// 运行态对象，不持久化——助手重启后定时器全部消失，需启动流程重跑才会重新挂载（在冒险日志有说明）。
/// </summary>
public sealed class ArmedTimerViewModel : ViewModelBase
{
    private readonly MistletoeViewModel _owner;

    public ArmedTimerViewModel(StartupStep step, DateTime nextFireAt, MistletoeViewModel owner)
    {
        Step = step;
        _owner = owner;
        NextFireAt = nextFireAt;
    }

    /// <summary>对应的定时触发器节点模型（到点时执行其 FireSteps）。</summary>
    public StartupStep Step { get; }

    /// <summary>取消令牌（取消按钮 / 流程无关，独立取消这个定时器）。</summary>
    public CancellationTokenSource Cts { get; private set; } = new();

    private DateTime _nextFireAt;
    public DateTime NextFireAt
    {
        get => _nextFireAt;
        private set
        {
            if (SetProperty(ref _nextFireAt, value))
                OnPropertyChanged(nameof(StatusLine));
        }
    }

    /// <summary>节点显示名（日志与列表用）。</summary>
    public string Title => StartupFlowRunner.DisplayName(Step, 0);

    /// <summary>状态行：⏰ 节点名 — MM-dd HH:mm 触发（每天重复）。</summary>
    public string StatusLine =>
        $"{Title} — {NextFireAt:MM-dd HH:mm} 触发「到点执行」链（{Step.FireSteps.Count} 个节点{(Step.RepeatDaily ? "，每天重复" : "")}）";

    public RelayCommand CancelCommand => new(_ => _owner.CancelTimer(this));

    /// <summary>每天重复时复用同一行项重新挂载（换发新 CTS，更新下次触发时间）。</summary>
    public void Reset(DateTime nextFireAt)
    {
        Cts.Dispose();
        Cts = new CancellationTokenSource();
        NextFireAt = nextFireAt;
    }
}
