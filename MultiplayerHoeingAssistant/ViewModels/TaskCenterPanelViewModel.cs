using System.Collections.ObjectModel;
using System.Text.Json;
using System.Windows.Threading;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

/// <summary>
/// R4.8 Batch D 任务中心面板 ViewModel（设计稿 §4.7/4.8；用户已确认草图 task-center-mockup）。
/// 纪律：
/// - 保留式编辑（一轮 I4）：编辑 LoadSnapshot 文档的序列化往返深拷贝，只改触碰字段——未知字段/多触发器/
///   多策略/loop 参数/activation 元数据原样保留；missPolicy=nextDay 仅新建触发器默认值；
/// - 候选 candidate-ready 只读预览（禁启动禁编辑，激活归 R5；一轮 I1）；隔离文件禁一切写路径；
/// - uid/bindingCode 三分（一轮 I6）：原值驻 draft 模型 / 显示掩码（≤5 全遮盖，否则前 3+***+后 2）/
///   编辑框空=不修改；掩码绝不落盘、不进协议；
/// - 追加节点来源=资源目录快照（SingleTask 禁追加）；新节点 NodeId="n-"+8hex 冲突检查；
/// - 2s 定时器只刷展示快照（一轮 S1），不重建编辑草稿；测试可注入停用；
/// - 动作走宿主结构化结果（HostActionResult），反馈文案入 StatusMessage。
/// </summary>
public sealed class TaskCenterPanelViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions CloneOptions = new();

    private readonly TaskCenterHost _host;
    private readonly Action<string> _log;
    private readonly DispatcherTimer? _timer; // null=测试不自动刷新
    private bool _refreshing;

    public TaskCenterPanelViewModel(TaskCenterHost host, Action<string>? log = null, bool autoRefresh = true)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _log = log ?? (_ => { });
        KickCatalogRefresh(); // 二轮（重要3）：生产目录刷新入口（异步，不占展示定时器；BGI 离线自动缓存降级留痕）
        Refresh();
        if (autoRefresh)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
        }
    }

    /// <summary>页面释放时停表（S1：定时器不承担调度/恢复）。</summary>
    public void StopAutoRefresh() => _timer?.Stop();

    /// <summary>页面重新 Loaded 时恢复刷新（幂等；与 StopAutoRefresh 配对）。</summary>
    public void StartAutoRefresh() => _timer?.Start();

    // ================= 流程列表 =================

    public ObservableCollection<WorkflowListItemVm> Flows { get; } = [];

    private string? _statusMessage;
    /// <summary>动作反馈（结构化结果文案；金色=受理/生效，红色=不可用）。</summary>
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }

    public RelayCommand RefreshCommand => new(_ => { KickCatalogRefresh(); Refresh(); });

    private bool _catalogRefreshInFlight;

    /// <summary>资源目录异步拉取（防重入；结果经 Current 快照被 2s 展示拍读取，S1 定时器本身不触发 IO）。</summary>
    private void KickCatalogRefresh()
    {
        if (_catalogRefreshInFlight) return;
        _catalogRefreshInFlight = true;
        _ = Task.Run(async () =>
        {
            try { await _host.Catalog.RefreshAsync().ConfigureAwait(false); }
            catch { /* 降级原因留在快照 DegradedReason，展示拍可读 */ }
            finally { _catalogRefreshInFlight = false; }
        });
    }

    public RelayCommand NewFlowCommand => new(_ =>
    {
        if (!GuardNoOpenDraft()) return;
        try
        {
            var doc = new WorkflowDocument { Name = "新流程 " + DateTime.Now.ToString("MM-dd HH:mm") };
            _host.SaveFlow(doc, null); // 新建合同：expectedRevision 必须为 null
            _log($"[任务中心] 新建流程「{doc.Name}」（{doc.WorkflowId}）");
            Refresh();
            BeginEdit(doc.WorkflowId!);
        }
        catch (Exception ex)
        {
            SetStatus("新建流程失败：" + ex.Message, isError: true);
        }
    });

    /// <summary>二轮（重要5）：已有未保存草稿（含修订冲突保留的草稿）时拒绝切换/新建，防静默丢稿。</summary>
    private bool GuardNoOpenDraft()
    {
        if (Editing is null) return true;
        SetStatus("已有未保存的编辑草稿，请先保存或放弃后再操作。", isError: true);
        return false;
    }

    private bool _startResumeInFlight;

    public RelayCommand StartFlowCommand => new(async p =>
    {
        // 二轮（阻断2/阻断4）：CanStart 命令层守卫 + 真异步等待——UI 不阻塞，日志回调的 Dispatcher.Invoke 可正常完成，无互等死锁
        if (p is not WorkflowListItemVm { CanStart: true } item || _startResumeInFlight) return;
        _startResumeInFlight = true;
        try
        {
            ApplyActionResult(await _host.StartWorkflowAsync(item.WorkflowId));
        }
        catch (Exception ex)
        {
            SetStatus("启动失败：" + ex.Message, isError: true);
        }
        finally
        {
            _startResumeInFlight = false;
        }
        Refresh();
    });

    public RelayCommand PreviewFlowCommand => new(p =>
    {
        if (p is not WorkflowListItemVm item) return;
        BeginPreview(item.WorkflowId);
    });

    public RelayCommand EditFlowCommand => new(p =>
    {
        if (p is not WorkflowListItemVm { CanEdit: true } item) return;
        if (!GuardNoOpenDraft()) return;
        BeginEdit(item.WorkflowId);
    });

    // ================= 编辑 =================

    private WorkflowEditVm? _editing;
    public WorkflowEditVm? Editing { get => _editing; private set { SetProperty(ref _editing, value); OnPropertyChanged(nameof(IsEditing)); } }
    public bool IsEditing => Editing is not null;

    private void BeginEdit(string workflowId)
    {
        try
        {
            var snapshot = _host.LoadFlowSnapshot(workflowId);
            // 二轮（阻断2）：编辑前复核实际快照——候选只读（列表条目可能过期；激活归 R5），宿主保存层另有独立禁写
            if (string.Equals(snapshot.Document.Activation?.Status, "candidate-ready", StringComparison.Ordinal))
            {
                SetStatus("candidate-ready 候选流程为只读预览，不能编辑（激活归 R5）。", isError: true);
                BeginPreview(workflowId);
                return;
            }
            // 二轮（建议2）：缺稳定身份的文件保存会产生身份-文件名错位（下次判型即隔离）——拒绝编辑，仅可预览
            if (string.IsNullOrWhiteSpace(snapshot.Document.WorkflowId))
            {
                SetStatus("该流程文件缺少稳定身份（workflowId），只能预览；请经迁移导入或新建获得身份后再编辑。", isError: true);
                BeginPreview(workflowId);
                return;
            }
            // 保留式编辑：序列化往返深拷贝（ExtensionData/未知字段全保留），只改触碰字段
            var draft = JsonSerializer.Deserialize<WorkflowDocument>(
                JsonSerializer.Serialize(snapshot.Document, CloneOptions), CloneOptions)
                ?? throw new InvalidOperationException("流程文档深拷贝失败");
            Editing = new WorkflowEditVm(draft, snapshot.Revision, _host.Catalog);
            Previewing = null;
        }
        catch (Exception ex)
        {
            SetStatus($"加载流程失败：{ex.Message}", isError: true);
        }
    }

    public RelayCommand SaveFlowCommand => new(_ =>
    {
        if (Editing is null) return;
        try
        {
            var submission = Editing.BuildSubmissionCopy(); // 二轮（阻断1/重要4）：基线+编辑集生成提交副本，保存失败不污染草稿
            var newRevision = _host.SaveFlow(submission, Editing.BaseRevision);
            _log($"[任务中心] 流程「{submission.Name}」已保存为新修订 {newRevision[..8]}（运行中实例不受影响，重载修订下一节点边界生效）");
            SetStatus($"已保存为新修订 {newRevision[..8]}…；正在运行的实例不受影响。", isError: false);
            Editing = null;
            Refresh();
        }
        catch (WorkflowRevisionConflictException ex)
        {
            // 一轮 I4：冲突保留草稿，不覆盖未保存内容
            SetStatus(ex.Message + "（草稿保留，可放弃后重新编辑）", isError: true);
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, isError: true);
        }
    });

    public RelayCommand DiscardEditCommand => new(_ => Editing = null);

    // ================= 预览（只读） =================

    private WorkflowPreviewVm? _previewing;
    public WorkflowPreviewVm? Previewing { get => _previewing; private set { SetProperty(ref _previewing, value); OnPropertyChanged(nameof(IsPreviewing)); } }
    public bool IsPreviewing => Previewing is not null;

    private void BeginPreview(string workflowId)
    {
        try
        {
            var snapshot = _host.LoadFlowSnapshot(workflowId);
            Previewing = WorkflowPreviewVm.Build(snapshot.Document, snapshot.Revision);
        }
        catch (Exception ex)
        {
            // 隔离文件也可从目录条目拿到原因（List 已判型）；此处兜底展示
            SetStatus($"预览加载失败：{ex.Message}", isError: true);
        }
    }

    public RelayCommand ClosePreviewCommand => new(_ => Previewing = null);

    // ================= 运行状态 =================

    public ObservableCollection<ActiveRunVm> ActiveRuns { get; } = [];
    public ObservableCollection<HistoryRunVm> HistoryRuns { get; } = [];

    public bool HasActiveRuns => ActiveRuns.Count > 0;

    public RelayCommand StopRunCommand => new(p => ApplyRunAction(p, WorkflowRunAction.Stop));
    public RelayCommand SkipNodeCommand => new(p => ApplyRunAction(p, WorkflowRunAction.SkipCurrent));
    public RelayCommand PauseRunCommand => new(p => ApplyRunAction(p, WorkflowRunAction.Pause));
    public RelayCommand ReloadRunCommand => new(p => ApplyRunAction(p, WorkflowRunAction.ReloadDefinition));

    public RelayCommand ResumeRunCommand => new(async p =>
    {
        if (p is not ActiveRunVm vm || _startResumeInFlight) return;
        _startResumeInFlight = true;
        try
        {
            ApplyActionResult(await _host.ResumeRunAsync(vm.RunId));
        }
        catch (Exception ex)
        {
            SetStatus("恢复失败：" + ex.Message, isError: true);
        }
        finally
        {
            _startResumeInFlight = false;
        }
        Refresh();
    });

    private void ApplyRunAction(object? p, WorkflowRunAction action)
    {
        if (p is not ActiveRunVm vm) return;
        try
        {
            ApplyActionResult(_host.RequestRunAction(vm.RunId, action));
        }
        catch (Exception ex)
        {
            SetStatus("动作失败：" + ex.Message, isError: true); // 二轮（重要5）：异常路径也走结构化反馈
        }
        Refresh();
    }

    private void ApplyActionResult(HostActionResult result)
    {
        SetStatus(result.Message, isError: result.Status == HostActionStatus.Unavailable);
        _log($"[任务中心] 动作{result.Status}：{result.Message}");
    }

    private void SetStatus(string message, bool isError)
    {
        // 二轮（重要4）：展示前脱敏——草稿涉及的 uid/bindingCode 原值与编辑值一律掩码化（掩码不落盘、不进协议、不上屏）
        if (Editing is not null)
        {
            foreach (var secret in Editing.CollectSensitiveValues().Distinct())
            {
                if (secret.Length > 0)
                    message = message.Replace(secret, NodeEditVm.MaskSensitive(secret), StringComparison.Ordinal);
            }
        }
        StatusMessage = message;
        StatusIsError = isError;
    }

    // ================= 刷新（展示快照；一轮 S1：不重建编辑草稿、不承担调度） =================

    public void Refresh()
    {
        if (_refreshing) return; // 防 Tick 重叠
        _refreshing = true;
        try
        {
            var catalog = _host.Catalog.Current;
            var catalogStatus = catalog.IsDegraded
                ? $"资源目录降级展示（{catalog.DegradedReason}）"
                : null;

            // 流程列表
            var entries = _host.ListFlows();
            SyncCollection(Flows, entries, e => e.WorkflowId,
                e => new WorkflowListItemVm(e),
                (vm, e) => vm.Update(e));

            // 运行卡片
            SyncCollection(ActiveRuns, _host.ListActiveRuns(), r => r.RunId,
                r => ActiveRunVm.Build(r, _host),
                (vm, r) => vm.Update(r, _host));
            OnPropertyChanged(nameof(HasActiveRuns));
            SyncCollection(HistoryRuns, _host.ListHistoryRuns(20), r => r.RunId,
                r => new HistoryRunVm(r),
                (vm, r) => vm.Update(r));

            CatalogStatusText = catalogStatus;
            // 编辑中的追加来源目录同步刷新（不打断草稿）
            Editing?.RefreshCatalog(_host.Catalog);
        }
        catch (Exception ex)
        {
            // 一轮 I7：单个坏文件/瞬时读失败不炸面板——留痕并保旧展示
            _log($"[任务中心] 面板刷新失败（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            _refreshing = false;
        }
    }

    private string? _catalogStatusText;
    public string? CatalogStatusText { get => _catalogStatusText; private set => SetProperty(ref _catalogStatusText, value); }

    private static void SyncCollection<TVm, TModel>(ObservableCollection<TVm> target, IReadOnlyList<TModel> source,
        Func<TModel, string> keyOf, Func<TModel, TVm> create, Action<TVm, TModel> update)
        where TVm : class
    {
        // 按键对齐增量更新（避免闪烁；新增尾部追加，缺失移除）
        var sourceKeys = source.Select(keyOf).ToHashSet(StringComparer.Ordinal);
        for (var i = target.Count - 1; i >= 0; i--)
        {
            // TVm 需暴露 Key
            if (target[i] is not IKeyedVm keyed || !sourceKeys.Contains(keyed.Key))
                target.RemoveAt(i);
        }
        foreach (var model in source)
        {
            var key = keyOf(model);
            var existing = target.OfType<IKeyedVm>().FirstOrDefault(v => v.Key == key);
            if (existing is TVm vm) update(vm, model);
            else target.Add(create(model));
        }
        // 二轮（重要6）：按源顺序重排（新入项默认尾部追加，对齐源序——历史 UpdatedAt 倒序持续成立）
        for (var i = 0; i < source.Count; i++)
        {
            var key = keyOf(source[i]);
            var cur = -1;
            for (var j = 0; j < target.Count; j++)
            {
                if (target[j] is IKeyedVm k && k.Key == key) { cur = j; break; }
            }
            if (cur >= 0 && cur != i) target.Move(cur, i);
        }
    }

    internal interface IKeyedVm { string Key { get; } }
}

/// <summary>流程列表条目（展示快照；candidate-ready 禁启动禁编辑，一轮 I1；隔离禁写路径）。</summary>
public sealed class WorkflowListItemVm : ViewModelBase, TaskCenterPanelViewModel.IKeyedVm
{
    private WorkflowCatalogEntry _entry;

    internal WorkflowListItemVm(WorkflowCatalogEntry entry) => _entry = entry;

    public string Key => _entry.WorkflowId;
    public string WorkflowId => _entry.WorkflowId;
    public string Name => _entry.Name;
    public string RevisionShort => _entry.Revision.Length > 8 ? _entry.Revision[..8] : _entry.Revision;
    // 二轮（阻断2）：候选身份只依据 activation——与类型支持能力解耦（候选+未知类型不得显示为可编辑 active）
    public bool IsCandidate => string.Equals(_entry.ActivationStatus, "candidate-ready", StringComparison.Ordinal);
    public bool IsQuarantined => _entry.Status == WorkflowFileStatus.Quarantined;
    public bool IsActive => !IsCandidate && !IsQuarantined;
    public bool CanStart => IsActive && _entry.UnsupportedKinds.Count == 0;
    public bool CanEdit => IsActive;

    public string StateBadge => IsQuarantined ? "已隔离"
        : IsCandidate ? "candidate-ready · 只读候选"
        : "active";

    public string? DetailNote => IsQuarantined ? $"隔离原因：{_entry.QuarantineReason}"
        : _entry.UnsupportedKinds.Count > 0 ? "未支持类型：" + string.Join("、", _entry.UnsupportedKinds)
        : null;

    internal void Update(WorkflowCatalogEntry entry)
    {
        _entry = entry;
        OnPropertyChanged(string.Empty);
    }
}

/// <summary>活动运行卡片条目（当前节点/动作可用性/结构化反馈入口）。</summary>
public sealed class ActiveRunVm : ViewModelBase, TaskCenterPanelViewModel.IKeyedVm
{
    private WorkflowRunRecord _run = null!;

    internal static ActiveRunVm Build(WorkflowRunRecord run, TaskCenterHost host)
    {
        var vm = new ActiveRunVm();
        vm.Update(run, host);
        return vm;
    }

    public string Key => RunId;
    public string RunId { get; private set; } = "";
    public string RunIdShort => RunId.Length > 8 ? RunId[..8] : RunId;
    public string WorkflowId { get; private set; } = "";
    public string StateText { get; private set; } = "";
    public string CurrentNodeText { get; private set; } = "";
    public string ProgressChainText { get; private set; } = "";
    public string? NoteText { get; private set; }

    public bool CanStop { get; private set; }
    public bool CanSkip { get; private set; }
    public bool CanPause { get; private set; }
    public bool CanReload { get; private set; }
    public bool CanResume { get; private set; }

    internal void Update(WorkflowRunRecord run, TaskCenterHost host)
    {
        _run = run;
        RunId = run.RunId;
        WorkflowId = run.WorkflowId;
        StateText = run.State switch
        {
            WorkflowRunState.Planned => "已计划",
            WorkflowRunState.Running => "运行中",
            WorkflowRunState.Waiting => $"等待触发（{run.Wait?.NextTriggerAt:MM-dd HH:mm}）",
            WorkflowRunState.Completing => "收尾中",
            WorkflowRunState.Paused => "已暂停（≠停止；节点边界保留等待记录）",
            WorkflowRunState.Interrupted => "已中断（可显式恢复）",
            WorkflowRunState.Unknown => "Unknown · 结果不可考（需对账）",
            _ => run.State.ToString(),
        };
        var (current, chain) = DescribeProgress(run, host);
        CurrentNodeText = current;
        ProgressChainText = chain;
        NoteText = run.Note;

        var driving = host.IsDriving(run.WorkflowId);
        CanStop = run.State is WorkflowRunState.Running or WorkflowRunState.Waiting or WorkflowRunState.Paused;
        CanSkip = driving && run.State == WorkflowRunState.Running;
        CanPause = driving && run.State == WorkflowRunState.Running;
        CanReload = driving && run.State is WorkflowRunState.Running or WorkflowRunState.Waiting or WorkflowRunState.Paused;
        CanResume = run.State is WorkflowRunState.Interrupted or WorkflowRunState.Paused;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>进度链文本（一轮 S1：NodeOutcomes 可视呈现限长 20；游标节点标「运行中」）。</summary>
    private static (string Current, string Chain) DescribeProgress(WorkflowRunRecord run, TaskCenterHost host)
    {
        try
        {
            var snapshot = host.LoadFlowSnapshot(run.WorkflowId);
            var nodes = snapshot.Document.Nodes;
            var currentLoop = run.Cursor?.LoopIteration ?? 0;
            // 二轮（重要6）：outcome 按当前轮次匹配（旧轮结果不得冒充本轮）；游标节点「运行中」优先于陈旧 outcome
            var segments = new List<string>();
            foreach (var node in nodes.Take(20))
            {
                var outcome = run.NodeOutcomes.LastOrDefault(o => o.NodeId == node.NodeId && o.LoopIteration == currentLoop);
                var isCurrent = run.Cursor?.NodeId == node.NodeId
                    && run.State is WorkflowRunState.Running or WorkflowRunState.Paused;
                var label = isCurrent ? "运行中" : outcome?.Result switch
                {
                    "succeeded" => "succeeded",
                    "skippedUser" => "显式跳过",
                    "skippedFilter" => "过滤跳过",
                    "failed" => "failed",
                    "rejected" => "rejected",
                    "cancelled" => "cancelled",
                    "cancelUnconfirmed" => "cancelUnconfirmed",
                    "unknown" => "unknown",
                    _ => "待执行",
                };
                segments.Add($"{node.Ref?.Config ?? node.NodeId} · {label}");
            }
            var current = run.Cursor is { } c
                ? nodes.FirstOrDefault(n => n.NodeId == c.NodeId)?.Ref?.Config ?? c.NodeId
                : "—";
            // 二轮（重要6）：运行绑定旧修订而定义已修改时显式标注（展示按当前定义映射，仅为近似）
            var revisionNote = !string.Equals(snapshot.Revision, run.WorkflowRevision, StringComparison.OrdinalIgnoreCase)
                ? "（流程已出新修订，进度按当前定义近似映射）" : "";
            return ($"当前节点：{current}（第 {currentLoop + 1} 轮）{revisionNote}", string.Join("  →  ", segments));
        }
        catch
        {
            return ("当前节点：—（流程定义不可读）", "");
        }
    }
}

/// <summary>历史运行条目（只读）。</summary>
public sealed class HistoryRunVm : ViewModelBase, TaskCenterPanelViewModel.IKeyedVm
{
    private WorkflowRunRecord _run;

    internal HistoryRunVm(WorkflowRunRecord run) => _run = run;

    public string Key => _run.RunId;
    public string RunIdShort => _run.RunId.Length > 8 ? _run.RunId[..8] : _run.RunId;
    public string StateBadge => _run.State.ToString();
    public string Summary
    {
        get
        {
            var ok = _run.NodeOutcomes.Count(o => o.Result is "succeeded" or "skippedUser" or "skippedFilter");
            var total = _run.NodeOutcomes.Count;
            var fail = _run.NodeOutcomes.FirstOrDefault(o => o.Result is "failed" or "rejected");
            return fail is not null
                ? $"{ok}/{total} 节点成功 · {fail.NodeId} {fail.Result}：{fail.Reason} · {_run.UpdatedAt:MM-dd HH:mm}"
                : $"{ok}/{total} 节点成功 · {_run.UpdatedAt:MM-dd HH:mm}";
        }
    }

    internal void Update(WorkflowRunRecord run)
    {
        _run = run;
        OnPropertyChanged(string.Empty);
    }
}