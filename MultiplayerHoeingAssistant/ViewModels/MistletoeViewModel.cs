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
        _runner = new StartupFlowRunner(mainVm.ExecuteLocalBgiCommandAsync, EnterTaskCenterAsync, ArmTimer, ConfirmHandlerAsync, mainVm.AddLog,
            () => mainVm.LatestLocalStatus, ArmWatchdog, (step, ct) => mainVm.QueryReadOnlyTaskStatusAsync(step, ct));
        _runner.NodeStateSink = OnNodeStateReported;
        RootChain = new StepChainViewModel(_config.Steps, this, parentCondition: null, branchName: "主流程");
        foreach (var s in _schemeStore.Load()) Schemes.Add(new SchemeItemViewModel(s));
        ArmedTimers.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasArmedTimers));
        ArmedWatchdogs.CollectionChanged += (_, _) => OnPropertyChanged(nameof(HasArmedWatchdogs));

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

    /// <summary>拼接任务显示文本：配置组 · 任务名 · 线路（空段跳过；联机锄地时线路优先于任务名，
    /// 脚本任务（JS/地图追踪）则三者全上）。与 ControlRoom 玩家卡片 TaskDisplayText 同口径。</summary>
    private static string ComposeTaskDisplay(ControlStatus s)
    {
        var parts = new List<string>();
        if (!string.IsNullOrEmpty(s.CurrentTaskGroupName)) parts.Add(s.CurrentTaskGroupName);
        if (!string.IsNullOrEmpty(s.CurrentScriptRouteName))
        {
            if (!string.IsNullOrEmpty(s.CurrentTaskName)) parts.Add(s.CurrentTaskName);
            parts.Add(s.CurrentScriptRouteName);
        }
        else
        {
            if (!string.IsNullOrEmpty(s.CurrentTaskName) && string.IsNullOrEmpty(s.CurrentRouteDisplay)) parts.Add(s.CurrentTaskName);
            if (!string.IsNullOrEmpty(s.CurrentRouteDisplay)) parts.Add(s.CurrentRouteDisplay);
        }
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

    /// <summary>整棵树里查找第一个指定类型的节点（「进入任务中心执行」全树唯一约束等用）。</summary>
    internal StartupStepViewModel? FindFirstByKind(string kind) => FindFirstByKind(RootChain, kind);

    private static StartupStepViewModel? FindFirstByKind(StepChainViewModel chain, string kind)
    {
        foreach (var vm in chain.Steps)
        {
            if (vm.Kind == kind) return vm;
            var hit = FindFirstByKind(vm.TrueChain, kind)
                      ?? FindFirstByKind(vm.FalseChain, kind)
                      ?? FindFirstByKind(vm.FireChain, kind);
            if (hit != null) return hit;
        }
        return null;
    }

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

    // ================= 流程图弹窗（只读） =================

    /// <summary>已打开的流程图窗口（重复点击时提到前台，不开第二个）。</summary>
    private FlowChartWindow? _flowChartWindow;

    /// <summary>流程图请求在编辑器中定位节点时触发（页面代码后置订阅，负责滚动到卡片）。</summary>
    internal event Action<StartupStepViewModel>? StepRevealRequested;

    public RelayCommand ShowFlowChartCommand => new(_ =>
    {
        if (RootChain.Steps.Count == 0)
        {
            _mainVm.AddLog("[槲寄生] 启动流程还没有节点，先在下方添加节点再看流程图");
            return;
        }
        if (_flowChartWindow is { IsLoaded: true })
        {
            _flowChartWindow.Activate();
            return;
        }
        _flowChartWindow = FlowChartWindow.Show(RootChain, RevealStepInEditor, ArmedTimers, ArmedWatchdogs, Application.Current.MainWindow);
        _flowChartWindow.Closed += (_, _) => _flowChartWindow = null;
    });

    /// <summary>流程图节点点击回调：切回启动中心 Tab、展开沿途分支、选中该节点，并通知页面滚动定位。</summary>
    internal void RevealStepInEditor(StartupStepViewModel vm)
    {
        SelectedTabIndex = 0;
        // 展开从根到该节点沿途所有条件节点的分支区域，确保目标卡片在树上可见
        for (var c = vm.OwnerChain; c.ParentCondition != null; c = c.ParentCondition.OwnerChain)
            c.ParentCondition.BranchesExpanded = true;
        SelectedStep = vm;
        StepRevealRequested?.Invoke(vm);

        // 把助手主窗口提到前台。必须延时到本次鼠标事件路由完之后：点击发生在流程图窗口上，
        // 若在事件处理途中激活主窗口，鼠标弹起路由完系统会把焦点还给流程图，刚置顶又被压回去。
        // 流程图与主窗口是相互独立的窗口（刻意不设 Owner，否则子窗口永远压在主窗口上面）。
        Application.Current.Dispatcher.BeginInvoke(DispatcherPriority.Input, new Action(() =>
        {
            var win = Application.Current.MainWindow;
            if (win == null) return;
            if (!win.IsVisible) win.Show();
            if (win.WindowState == WindowState.Minimized) win.WindowState = WindowState.Normal;
            win.Activate();
        }));
    }

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

    /// <summary>按模型在编辑器树中定位节点（武装中的定时器/电子狗行的「定位」按钮用）：找不到只不跳转，不影响其他逻辑。</summary>
    internal void LocateStep(StartupStep step)
    {
        var vm = FindStepVm(RootChain, step);
        if (vm != null) RevealStepInEditor(vm);
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

    // ================= 电子狗（盯梢中的循环检测列表） =================

    /// <summary>当前盯梢中的电子狗（运行态，不持久化；助手重启后需流程重跑才会重新挂载）。</summary>
    public ObservableCollection<ArmedWatchdogViewModel> ArmedWatchdogs { get; } = [];

    public bool HasArmedWatchdogs => ArmedWatchdogs.Count > 0;

    /// <summary>电子狗节点执行到此：校验参数后挂载循环检测（Runner 注入的委托）。</summary>
    private void ArmWatchdog(StartupStep step)
    {
        var interval = Math.Max(1, step.WatchIntervalSeconds);
        var dog = new ArmedWatchdogViewModel(step, interval, this);
        RunOnUi(() => ArmedWatchdogs.Add(dog));
        _mainVm.AddLog($"[槲寄生] 电子狗「{StartupFlowRunner.DisplayName(step, 0)}」已挂载：每 {interval} 秒盯「{WatchKindDesc(step)}」，成立时执行「触发执行」链（{step.FireSteps.Count} 个节点，{(step.WatchRepeat ? "重复触发" : "触发一次后撤下")}，防抖复核 {Math.Clamp(step.WatchConfirmSeconds, 1, 60)}s×{Math.Clamp(step.WatchConfirmTimes, 1, 10)}）");
        _ = RunWatchdogAsync(dog);
    }

    /// <summary>被盯条件的人类可读描述（挂载日志、卡片摘要与列表状态行共用口径）。</summary>
    internal static string WatchKindDesc(StartupStep step) => step.WatchKind switch
    {
        StartupStepKinds.BgiRunning => $"BGI 进程{(step.ExpectRunning ? "在跑" : "不在")}",
        StartupStepKinds.GameRunning => $"游戏进程{(step.ExpectRunning ? "在跑" : "不在")}",
        StartupStepKinds.ProcessRunning => $"进程 {step.ProcessName} {(step.ExpectRunning ? "存在" : "不存在")}",
        StartupStepKinds.BgiTaskRunning => step.ExpectRunning ? "BGI 有任务在跑" : "BGI 空闲",
        StartupStepKinds.BgiTaskName => $"当前任务名包含「{step.TaskName}」",
        _ => step.WatchKind,
    };

    /// <summary>把电子狗节点的扁平参数拼成一个临时条件节点，复用 Runner 的条件求值（与流程内条件节点同一判断口径）。</summary>
    private static StartupStep BuildWatchCondition(StartupStep step) => new()
    {
        Kind = step.WatchKind,
        ExpectRunning = step.ExpectRunning,
        ProcessName = step.ProcessName,
        TaskName = step.TaskName,
        StatusSource = step.StatusSource,
        StatusTargetOrder = step.StatusTargetOrder,
        StatusTargetUser = step.StatusTargetUser,
        TimeStart = step.TimeStart,
        TimeEnd = step.TimeEnd,
        Weekdays = [.. step.Weekdays],
    };

    /// <summary>
    /// 电子狗循环：每 interval 秒求值一次被盯条件，确认成立的边沿（不成立→成立）触发执行 FireSteps。
    /// 防抖（节点可配，WatchConfirmSeconds×WatchConfirmTimes，默认 1s×1）：发现与已确认状态不同的读数时，
    /// 按配置间隔复核 N 轮，全部一致为新状态才认定翻转（两个方向都防抖）；任一轮回到原状态或快照缺失
    /// 都放弃本次翻转、保持原状态——确认窗口不随检测间隔放大，
    /// 防 BGI 任务切换间隙的中间态造成假触发/假复位。
    /// 容错：快照缺失（BGI 未连接/未上报）的轮次按「未知」处理——不翻转、保持原状态，
    /// 避免断连-重连被误判成一次边沿；触发链执行异常只记日志，狗继续盯。
    /// 基线按「不成立」起算：挂载时条件已成立，复核确认后触发一次（发现即触发）。
    /// </summary>
    private async Task RunWatchdogAsync(ArmedWatchdogViewModel dog)
    {
        var step = dog.Step;
        var confirmSeconds = Math.Clamp(step.WatchConfirmSeconds, 1, 60);
        var confirmTimes = Math.Clamp(step.WatchConfirmTimes, 1, 10);
        var confirmed = false; // 已确认状态
        try
        {
            while (!dog.Cts.Token.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromSeconds(dog.IntervalSeconds), dog.Cts.Token);

                var (reading, desc) = await ReadWatchdogAsync(step, dog.Cts.Token);
                if (reading == null)
                {
                    dog.NoteLastCheck($"{desc}，本轮不计");
                    continue;
                }
                if (reading == confirmed)
                {
                    dog.NoteLastCheck($"{desc}（状态未变）");
                    continue;
                }

                // 出现新状态：按节点配置复核 N 轮（不等下一轮间隔），全部一致才认定翻转
                dog.NoteLastCheck($"{desc}（新状态，{confirmSeconds}s×{confirmTimes} 复核）");
                var flipApproved = true;
                var flipDesc = desc;
                for (var i = 1; i <= confirmTimes; i++)
                {
                    await Task.Delay(TimeSpan.FromSeconds(confirmSeconds), dog.Cts.Token);
                    var (recheck, recheckDesc) = await ReadWatchdogAsync(step, dog.Cts.Token);
                    flipDesc = recheckDesc;
                    if (recheck == null)
                    {
                        dog.NoteLastCheck($"{recheckDesc}，复核第 {i}/{confirmTimes} 轮无效，保持原状态");
                        flipApproved = false;
                        break;
                    }
                    if (recheck == confirmed)
                    {
                        dog.NoteLastCheck($"中间态抖动未确认（{desc} → 复核第 {i}/{confirmTimes} 轮：{recheckDesc}），保持原状态");
                        flipApproved = false;
                        break;
                    }
                }
                if (!flipApproved) continue;

                var rising = reading.Value && !confirmed; // 翻转方向：false→true 才是触发边沿
                confirmed = reading.Value;
                if (!rising)
                {
                    dog.NoteLastCheck($"{flipDesc}（复核一致，已确认翻转为不成立）");
                    continue;
                }

                dog.NoteLastCheck($"{flipDesc}（复核一致，触发）");
                _mainVm.AddLog($"[槲寄生] 电子狗「{dog.Title}」盯到了：{flipDesc}，开始执行「触发执行」链");
                OnNodeStateReported(step, NodeRunState.Running, $"电子狗触发：{flipDesc}");
                try
                {
                    await _runner.RunAsync(step.FireSteps.ToList(), dog.Cts.Token);
                    OnNodeStateReported(step, NodeRunState.Success, $"电子狗于 {DateTime.Now:HH:mm:ss} 触发");
                }
                catch (OperationCanceledException)
                {
                    throw; // 取消语义原样上传（用户撤下狗时中断触发链）
                }
                catch (Exception ex)
                {
                    // 容错：触发链异常只记日志，狗继续盯（RunAsync 内部已逐节点容错，这里是兜底）
                    _mainVm.AddLog($"[槲寄生] 电子狗「{dog.Title}」的「触发执行」链执行异常：{ex.Message}（继续盯梢）");
                }

                if (!step.WatchRepeat)
                {
                    _mainVm.AddLog($"[槲寄生] 电子狗「{dog.Title}」设置为触发一次，已自动撤下");
                    RunOnUi(() => ArmedWatchdogs.Remove(dog));
                    return;
                }
            }
        }
        catch (OperationCanceledException)
        {
            _mainVm.AddLog($"[槲寄生] 电子狗「{dog.Title}」已取消");
        }
        catch (Exception ex)
        {
            // 循环本身出意外（理论上只剩此处兜底）：留痕并撤下，避免无声残留
            _mainVm.AddLog($"[槲寄生] 电子狗「{dog.Title}」检测循环异常：{ex.Message}，已撤下");
            RunOnUi(() => ArmedWatchdogs.Remove(dog));
        }
    }

    /// <summary>读取一轮被盯条件：reading=null 表示「未知」（依赖 BGI 快照的条件在快照缺失时）。
    /// 未知≠不成立——这是断连-重连不被误判成边沿的关键。</summary>
    private async Task<(bool? reading, string desc)> ReadWatchdogAsync(StartupStep step, CancellationToken ct)
    {
        var cond = BuildWatchCondition(step);
        if (step.StatusSource == StartupStatusSource.CurrentSession && _mainVm.LatestLocalStatus == null
            && cond.Kind is StartupStepKinds.BgiTaskRunning or StartupStepKinds.BgiTaskName)
            return (null, "BGI 状态快照缺失（未连接/未上报）");
        return await _runner.ReadConditionAsync(cond, ct);
    }

    /// <summary>取消一个盯梢中的电子狗（页面「取消」按钮）。</summary>
    internal void CancelWatchdog(ArmedWatchdogViewModel dog)
    {
        RunOnUi(() => ArmedWatchdogs.Remove(dog));
        dog.Cts.Cancel();
    }

    /// <summary>弹窗只读展示一条子链的流程内容（定时器/电子狗行的「流程」按钮）。</summary>
    internal void ViewFlow(IReadOnlyList<StartupStep> steps, string title)
    {
        RunOnUi(() => FlowPreviewWindow.Show(title, steps, Application.Current.MainWindow));
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
        // 「进入任务中心执行」全树只能有一个：多个交接点会把任务序列的推进权搞冲突
        if (kind == StartupStepKinds.EnterTaskCenter && _owner.FindFirstByKind(StartupStepKinds.EnterTaskCenter) is { } existing)
        {
            _owner.Log($"[槲寄生] 启动流程中只能有一个「进入任务中心执行」节点（已有：「{existing.DisplayName}」），多个交接会冲突；如需调整位置，请删除后重新添加");
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
    public bool IsWatchdog => Model.Kind == StartupStepKinds.Watchdog;

    /// <summary>触发子链编辑区的标题（定时触发器/电子狗共用 FireChain 编辑器，标题各表）。</summary>
    public string FireChainHeader => IsTimerTrigger
        ? "⏰ 到点执行（时间在节点参数里设置）"
        : IsWatchdog
            ? "🐕 触发执行（被盯条件在节点参数里设置；条件由不成立变成立时执行此链）"
            : "";
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

    /// <summary>任务名文本：bgiTaskName 条件的匹配文本；[旧版遗留] startGroup/startOneClick 节点的任务名（目录已移除，旧配置仍可编辑执行）。</summary>
    public string TaskName
    {
        get => Model.TaskName;
        set { Model.TaskName = value; Changed(); }
    }

    /// <summary>状态来源下拉框：本会话、按实际 BGI 启动顺序、按 Windows 用户名。</summary>
    public int StatusSourceIndex
    {
        get
        {
            var i = Array.IndexOf(StartupStatusSource.All, Model.StatusSource);
            return i >= 0 ? i : 0;
        }
        set
        {
            if (value < 0 || value >= StartupStatusSource.All.Length) return;
            Model.StatusSource = StartupStatusSource.All[value];
            Changed(nameof(StatusSourceIndex));
            Changed(nameof(StatusSourceSummary));
        }
    }

    public string StatusTargetOrderText
    {
        get => Math.Max(1, Model.StatusTargetOrder).ToString();
        set
        {
            Model.StatusTargetOrder = int.TryParse(value, out var n) ? Math.Max(1, n) : 1;
            Changed();
        }
    }

    public string StatusTargetUser
    {
        get => Model.StatusTargetUser;
        set { Model.StatusTargetUser = value ?? ""; Changed(); Changed(nameof(StatusSourceSummary)); }
    }

    public bool HasStatusSource => (Model.Kind == StartupStepKinds.Watchdog ? Model.WatchKind : Model.Kind)
        is StartupStepKinds.BgiRunning or StartupStepKinds.GameRunning or StartupStepKinds.BgiTaskRunning or StartupStepKinds.BgiTaskName;
    public bool IsOrderSource => Model.StatusSource == StartupStatusSource.StartupOrder;
    public bool IsUserSource => Model.StatusSource == StartupStatusSource.UserName;

    public string StatusSourceSummary => Model.StatusSource switch
    {
        StartupStatusSource.StartupOrder => $"首次按 BGI 实际启动时间给会话编号，选择第 {Math.Max(1, Model.StatusTargetOrder)} 个（包含本会话）。助手运行期间序号不前移；同会话 BGI 重启可重连。重启助手重新编号，请先按固定顺序启动全部 BGI；登录顺序不等于 BGI 启动顺序。",
        StartupStatusSource.UserName => string.IsNullOrWhiteSpace(Model.StatusTargetUser)
            ? "按 Windows 用户名匹配（支持 DOMAIN\\user）；同名多会话无法唯一识别时按未知处理，不执行条件。"
            : $"按 Windows 用户名「{Model.StatusTargetUser}」匹配；同名多会话无法唯一识别时按未知处理，不执行条件。",
        _ => "本会话（默认）：只读取当前助手所在 Windows 会话的状态。"
    };


    // ---- 电子狗参数（watchdog 用；被盯条件为进程类/任务状态时复用 ProcessName/ExpectRunningIndex/TaskName） ----

    /// <summary>被盯条件类型键（watchdog 用；XAML 子参数区的 MultiDataTrigger 直接绑它）。</summary>
    public string WatchKind
    {
        get => Model.WatchKind;
        set { Model.WatchKind = value; Changed(); }
    }

    /// <summary>被盯条件下标（watchdog 编辑器下拉框用），顺序同 <see cref="StartupStepKinds.WatchableKinds"/>。</summary>
    public int WatchKindIndex
    {
        get
        {
            var i = Array.IndexOf(StartupStepKinds.WatchableKinds, Model.WatchKind);
            return i >= 0 ? i : 0;
        }
        set
        {
            if (value < 0 || value >= StartupStepKinds.WatchableKinds.Length) return;
            Model.WatchKind = StartupStepKinds.WatchableKinds[value];
            Changed(nameof(WatchKind)); // 联动刷新子参数区的显隐
        }
    }

    /// <summary>检测间隔秒数文本（watchdog 用；最低 1 秒，非法输入回退 30）。</summary>
    public string WatchIntervalSecondsText
    {
        get => Model.WatchIntervalSeconds.ToString();
        set
        {
            Model.WatchIntervalSeconds = int.TryParse(value, out var n) ? Math.Clamp(n, 1, 86400) : 30;
            Changed();
        }
    }

    /// <summary>是否重复触发（watchdog 用）：勾选=每次边沿都触发；不勾=触发一次后自动撤下。</summary>
    public bool WatchRepeat
    {
        get => Model.WatchRepeat;
        set { Model.WatchRepeat = value; Changed(); }
    }

    /// <summary>防抖复核间隔秒数文本（watchdog 用；1~60，非法输入回退 1）。</summary>
    public string WatchConfirmSecondsText
    {
        get => Model.WatchConfirmSeconds.ToString();
        set
        {
            Model.WatchConfirmSeconds = int.TryParse(value, out var n) ? Math.Clamp(n, 1, 60) : 1;
            Changed();
        }
    }

    /// <summary>防抖复核次数文本（watchdog 用；1~10，非法输入回退 1）。</summary>
    public string WatchConfirmTimesText
    {
        get => Model.WatchConfirmTimes.ToString();
        set
        {
            Model.WatchConfirmTimes = int.TryParse(value, out var n) ? Math.Clamp(n, 1, 10) : 1;
            Changed();
        }
    }

    // ---- 卡片摘要行 ----

    public string Summary => BuildSummary(Model);

    /// <summary>卡片摘要行文本（抽成静态方法：流程预览弹窗 FlowPreviewWindow 复用同一口径，避免两处拼接逻辑漂移）。</summary>
    internal static string BuildSummary(StartupStep model) => model.Kind switch
    {
        StartupStepKinds.TimeRange => $"{model.TimeStart} ~ {model.TimeEnd}",
        StartupStepKinds.Weekday => model.Weekdays.Count == 7
            ? "每天"
            : model.Weekdays.Count == 0
                ? "（未勾选任何星期）"
                : "周" + string.Join("、", model.Weekdays.Order().Select(d => "一二三四五六日"[d - 1])),
        StartupStepKinds.BgiRunning => model.ExpectRunning ? "BGI 正在运行 → 是" : "BGI 未运行 → 是",
        StartupStepKinds.GameRunning => model.ExpectRunning ? "游戏正在运行 → 是" : "游戏未运行 → 是",
        StartupStepKinds.ProcessRunning => $"{model.ProcessName} {(model.ExpectRunning ? "存在" : "不存在")} → 是",
        StartupStepKinds.BgiTaskRunning => model.ExpectRunning ? "BGI 有任务在跑 → 是" : "BGI 空闲 → 是",
        StartupStepKinds.BgiTaskName => string.IsNullOrWhiteSpace(model.TaskName) ? "（未填写任务名）" : $"当前任务名包含「{model.TaskName}」→ 是",
        StartupStepKinds.ManualConfirm =>
            $"{(string.IsNullOrWhiteSpace(model.ConfirmMessage) ? "（未填提示内容）" : model.ConfirmMessage)}" +
            $"{(model.ConfirmTimeoutSeconds > 0 ? $"；{model.ConfirmTimeoutSeconds} 秒超时走「{(model.ConfirmTimeoutGoTrue ? "是" : "否")}」" : "；不限时")}",
        StartupStepKinds.StartBgi => (string.IsNullOrWhiteSpace(model.Arguments) ? "启动本机 BGI" : $"启动本机 BGI（参数：{model.Arguments}）")
            + (model.KillBeforeStart ? "，先关闭再启动" : ""),
        StartupStepKinds.StopBgi => "强制结束本会话 BGI 进程",
        StartupStepKinds.StartGame or StartupStepKinds.StartProgram =>
            string.IsNullOrWhiteSpace(model.Path) ? "（未填写程序路径）" : model.Path,
        StartupStepKinds.RunCmd => string.IsNullOrWhiteSpace(model.Arguments) ? "（未填写命令）" : model.Arguments,
        StartupStepKinds.KillProgram => string.IsNullOrWhiteSpace(model.ProcessName) ? "（未填写进程名）" : $"结束进程 {model.ProcessName}",
        StartupStepKinds.Wait => $"等待 {model.WaitSeconds} 秒",
        StartupStepKinds.TimerTrigger =>
            $"{model.TriggerTime} 触发「到点执行」链（{model.FireSteps.Count} 个节点{(model.RepeatDaily ? "，每天重复" : "")}）",
        StartupStepKinds.Watchdog =>
            $"每 {Math.Max(1, model.WatchIntervalSeconds)}s 盯「{MistletoeViewModel.WatchKindDesc(model)}」，成立执行 {model.FireSteps.Count} 个节点（{(model.WatchRepeat ? "重复触发" : "触发一次")}，复核 {Math.Clamp(model.WatchConfirmSeconds, 1, 60)}s×{Math.Clamp(model.WatchConfirmTimes, 1, 10)}）",
        StartupStepKinds.EnterTaskCenter => "交接给任务中心执行任务序列",
        StartupStepKinds.EndFlow => "立即终止整条启动流程",
        StartupStepKinds.StartGroup => string.IsNullOrWhiteSpace(model.TaskName) ? "（旧版节点 · 未填写配置组名）" : $"（旧版节点）配置组「{model.TaskName}」",
        StartupStepKinds.StartOneClick => string.IsNullOrWhiteSpace(model.TaskName) ? "（旧版节点 · 未填写一条龙名）" : $"（旧版节点）一条龙「{model.TaskName}」",
        _ => "",
    };

    private void Changed(string? extraProperty = null)
    {
        if (extraProperty != null) OnPropertyChanged(extraProperty);
        OnPropertyChanged(nameof(Summary));
        OnPropertyChanged(nameof(HasStatusSource));
        OnPropertyChanged(nameof(IsOrderSource));
        OnPropertyChanged(nameof(IsUserSource));
        OnPropertyChanged(nameof(StatusSourceSummary));
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

    public RelayCommand ViewFlowCommand => new(_ => _owner.ViewFlow(Step.FireSteps, $"定时触发器「{Title}」的到点执行流程"));

    /// <summary>在启动中心编辑器树中定位到挂载这个定时器的节点。</summary>
    public RelayCommand LocateCommand => new(_ => _owner.LocateStep(Step));

    public RelayCommand CancelCommand => new(_ => _owner.CancelTimer(this));

    /// <summary>每天重复时复用同一行项重新挂载（换发新 CTS，更新下次触发时间）。</summary>
    public void Reset(DateTime nextFireAt)
    {
        Cts.Dispose();
        Cts = new CancellationTokenSource();
        NextFireAt = nextFireAt;
    }
}

/// <summary>
/// 一个盯梢中的电子狗的视图模型：页面「盯梢中的电子狗」卡片的行项。
/// 运行态对象，不持久化——与定时触发器同口径（助手重启后需流程重跑才会重新挂载）。
/// </summary>
public sealed class ArmedWatchdogViewModel : ViewModelBase
{
    private readonly MistletoeViewModel _owner;

    public ArmedWatchdogViewModel(StartupStep step, int intervalSeconds, MistletoeViewModel owner)
    {
        Step = step;
        IntervalSeconds = intervalSeconds;
        _owner = owner;
    }

    /// <summary>对应的电子狗节点模型（被盯条件参数与「触发执行」链都在上面）。</summary>
    public StartupStep Step { get; }

    /// <summary>检测间隔秒数（挂载时已按下限 5 秒收紧）。</summary>
    public int IntervalSeconds { get; }

    /// <summary>取消令牌（取消按钮 / 触发一次后自动撤下，独立取消这只狗）。</summary>
    public CancellationTokenSource Cts { get; } = new();

    /// <summary>节点显示名（日志与列表用）。</summary>
    public string Title => StartupFlowRunner.DisplayName(Step, 0);

    /// <summary>状态行：节点名 — 每 Ns 盯「条件」，成立执行 X 节点（重复/一次）。</summary>
    public string StatusLine =>
        $"{Title} — 每 {IntervalSeconds}s 盯「{MistletoeViewModel.WatchKindDesc(Step)}」，成立执行 {Step.FireSteps.Count} 个节点（{(Step.WatchRepeat ? "重复触发" : "触发一次")}）";

    private string _lastCheckNote = "等待第一轮检测…";
    /// <summary>最近一轮检测的判断依据（每轮刷新，盯梢过程可见）。</summary>
    public string LastCheckNote
    {
        get => _lastCheckNote;
        private set => SetProperty(ref _lastCheckNote, value);
    }

    /// <summary>检测循环（线程池线程）回报最近一轮依据：回 UI 线程写绑定属性。</summary>
    public void NoteLastCheck(string desc)
    {
        var text = $"最近检测 {DateTime.Now:HH:mm:ss}：{desc}";
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher == null || dispatcher.CheckAccess()) LastCheckNote = text;
        else dispatcher.Invoke(() => LastCheckNote = text);
    }

    public RelayCommand ViewFlowCommand => new(_ => _owner.ViewFlow(Step.FireSteps, $"电子狗「{Title}」的触发执行流程"));

    /// <summary>在启动中心编辑器树中定位到挂载这只电子狗的节点。</summary>
    public RelayCommand LocateCommand => new(_ => _owner.LocateStep(Step));

    public RelayCommand CancelCommand => new(_ => _owner.CancelWatchdog(this));
}
