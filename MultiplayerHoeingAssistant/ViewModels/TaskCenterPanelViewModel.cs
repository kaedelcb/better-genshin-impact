using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using System.Collections.Specialized;
using System.ComponentModel;
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
public sealed partial class TaskCenterPanelViewModel : ViewModelBase
{
    private static readonly JsonSerializerOptions CloneOptions = new();

    private readonly TaskCenterHost _host;
    private readonly MainViewModel? _remoteMain;
    private readonly RemoteCenterTarget? _remoteTarget;
    internal bool IsRemoteCenter => _remoteTarget is not null;
    private readonly DispatcherTimer? _remoteTimer;
    private RemoteTaskListSnapshot? _remoteSnapshot;
    private bool _remoteRefreshing;
    private bool _remoteSaving;
    private bool _remoteStale;
    private bool _remoteAbandoned;
    private readonly Action<string> _log;
    private readonly DispatcherTimer? _timer; // null=测试不自动刷新
    private readonly DispatcherTimer _autoSaveTimer;
    private bool _refreshing;
    private bool _autoSavePending;
    private bool _autoSaveInProgress;
    private bool _suppressAutoSave;
    /// <summary>由槲寄生外壳注入的设置入口；基础页不直接依赖 MainViewModel。</summary>
    public Action? OpenSettingsAction { get; set; }
    public Action? FlowChoicesChangedAction { get; set; }
    public RelayCommand OpenSettingsCommand => new(_ => OpenSettingsAction?.Invoke());
    private string CurrentWorkflowSelectionPath
        => Path.Combine(Path.GetDirectoryName(_host.Workflows.RootDirectory) ?? AssistantDataDirectory.Root,
            "taskcenter-current-workflow.txt");

    public TaskCenterPanelViewModel(TaskCenterHost host, Action<string>? log = null,
        bool autoRefresh = true, bool passive = false)
    {
        _host = host ?? throw new ArgumentNullException(nameof(host));
        _log = log ?? (_ => { });
        _autoSaveTimer = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(350) };
        _autoSaveTimer.Tick += (_, _) => AutoSaveTick();
        if (passive) return; // 远程页的占位对象不可扫描、保存或选择监控端本地计划。
        KickCatalogRefresh(); // 二轮（重要3）：生产目录刷新入口（异步，不占展示定时器；BGI 离线自动缓存降级留痕）
        Refresh();
        if (autoRefresh)
        {
            _timer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(2) };
            _timer.Tick += (_, _) => Refresh();
            _timer.Start();
        }
    }

    public TaskCenterPanelViewModel(MainViewModel main, RemoteCenterTarget target)
        : this(CreateRemoteShadowHost(), main.AddLog, autoRefresh: false, passive: true)
    {
        _remoteMain = main;
        _remoteTarget = target;
        _remoteTimer = new DispatcherTimer { Interval = TimeSpan.FromSeconds(3) };
        _remoteTimer.Tick += async (_, _) => await RefreshRemoteAsync();
        // 切换复用页面只更换 DataContext，不会再次 Loaded；首次连接也必须启动轮询。
        _remoteTimer.Start();
        SetStatus("正在读取执行端任务中心…", false);
        _ = Dispatcher.CurrentDispatcher.InvokeAsync(() => RefreshRemoteAsync()).Task.Unwrap();
    }

    /// <summary>页面释放时停表（S1：定时器不承担调度/恢复）。</summary>
    public void StopAutoRefresh() { _timer?.Stop(); _remoteTimer?.Stop(); }

    /// <summary>页面重新 Loaded 时恢复刷新（幂等；与 StopAutoRefresh 配对）。</summary>
    public void StartAutoRefresh()
    {
        _timer?.Start();
        _remoteTimer?.Start();
        if (_remoteTarget is not null) _ = RefreshRemoteAsync();
    }

    // ================= 流程列表 =================

    public ObservableCollection<WorkflowListItemVm> Flows { get; } = [];
    private string? _selectedWorkflowId;
    public string? SelectedWorkflowId
    {
        get => _selectedWorkflowId;
        private set
        {
            if (!SetProperty(ref _selectedWorkflowId, value)) return;
            PersistCurrentWorkflowSelection(value);
            OnPropertyChanged(nameof(CanStartSelectedPlan));
            OnPropertyChanged(nameof(CurrentPlanRuns));
            OnPropertyChanged(nameof(HasCurrentPlanRuns));
            OnPropertyChanged(nameof(CurrentPlanRunsHeight));
        }
    }

    private string? ReadCurrentWorkflowSelection()
    {
        if (_remoteTarget is not null) return null;
        try
        {
            if (!File.Exists(CurrentWorkflowSelectionPath)) return null;
            var value = File.ReadAllText(CurrentWorkflowSelectionPath).Trim();
            return value.Length == 0 ? null : value;
        }
        catch { return null; }
    }

    private void PersistCurrentWorkflowSelection(string? workflowId)
    {
        if (_remoteTarget is not null) return;
        if (string.IsNullOrWhiteSpace(workflowId)) return;
        try
        {
            var root = Path.GetDirectoryName(_host.Workflows.RootDirectory) ?? AssistantDataDirectory.Root;
            Directory.CreateDirectory(root);
            var temp = CurrentWorkflowSelectionPath + "." + Guid.NewGuid().ToString("N") + ".tmp";
            File.WriteAllText(temp, workflowId, new System.Text.UTF8Encoding(false));
            File.Move(temp, CurrentWorkflowSelectionPath, overwrite: true);
        }
        catch { /* selection persistence is a convenience; it never changes execution facts */ }
    }

    internal void ClearCurrentWorkflowSelection()
    {
        if (_remoteTarget is not null) return;
        try { if (File.Exists(CurrentWorkflowSelectionPath)) File.Delete(CurrentWorkflowSelectionPath); }
        catch { }
    }

    private string? _statusMessage;
    /// <summary>动作反馈（结构化结果文案；金色=受理/生效，红色=不可用）。</summary>
    public string? StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }

    private bool _statusIsError;
    public bool StatusIsError { get => _statusIsError; private set => SetProperty(ref _statusIsError, value); }
    public bool ShowBgiConfigButton => StatusIsError && (_remoteTarget is null
        || StatusMessage?.Contains("BGI", StringComparison.OrdinalIgnoreCase) == true
        || StatusMessage?.Contains("路径", StringComparison.Ordinal) == true);

    public RelayCommand RefreshCommand => new(_ =>
    {
        if (_remoteTarget is not null) { _ = RefreshRemoteAsync(); return; }
        KickCatalogRefresh(); Refresh();
    });

    private bool _connectingResources;
    public RelayCommand ConnectResourcesCommand => new(async _ =>
    {
        if (_remoteTarget is not null)
        {
            await RemoteOperationAsync("task.catalog.connect");
            return;
        }
        if (_connectingResources) return;
        _connectingResources = true;
        SetStatus("正在连接BGI资源目录…", false);
        try { ApplyActionResult(await _host.ConnectResourceCatalogAsync()); }
        finally { _connectingResources = false; Refresh(); }
    });

    public RelayCommand PrepareLegacyMigrationCommand => new(async _ =>
    {
        if (!GuardNoOpenDraft()) return;
        if (_remoteTarget is not null)
        {
            var owner = System.Windows.Application.Current.MainWindow;
            if (owner is null) return;
            var source = MultiplayerHoeingAssistant.Views.RemoteProgramPickerWindow.ShowFolderPicker(
                _remoteMain!, _remoteTarget, owner);
            if (source is not null) await RemoteOperationAsync("task.migration.prepare", source);
            return;
        }
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title="选择旧BGI的User目录（只读取原件，生成迁移候选）" };
        if (dialog.ShowDialog()!=true) return;
        var result=await _host.PrepareLegacyMigrationAsync(dialog.FolderName);
        SetStatus(result.Message,!result.Ok);
        Refresh();
    });

    public RelayCommand ActivateMigrationCandidateCommand => new(async p =>
    {
        if (!GuardNoOpenDraft() || p is not WorkflowListItemVm { IsCandidate: true, IsQuarantined: false } item) return;
        if (_remoteTarget is not null)
        {
            await RemoteOperationAsync("task.migration.activate", item.WorkflowId);
            return;
        }
        var result = await _host.ActivateMigrationCandidateAsync(item.WorkflowId);
        SetStatus(result.Message, !result.Ok);
        Refresh();
    });

    public RelayCommand RollbackMigrationCommand => new(async p =>
    {
        if (!GuardNoOpenDraft() || p is not WorkflowListItemVm { IsQuarantined: false } item) return;
        if (_remoteTarget is not null)
        {
            await RemoteOperationAsync("task.migration.rollback", item.WorkflowId);
            return;
        }
        var result = await _host.RollbackMigrationAsync(item.WorkflowId);
        SetStatus(result.Message, !result.Ok);
        Refresh();
    });
    /// <summary>迁移演练摘要（R5.8 §21.4；绑定到面板上的报告文本）。</summary>
    public string MigrationRehearsalSummary
    {
        get => _migrationRehearsalSummary;
        private set { _migrationRehearsalSummary = value; OnPropertyChanged(nameof(MigrationRehearsalSummary)); }
    }
    private string _migrationRehearsalSummary = "未运行（点击「迁移演练」在独立配置根上生成报告）";

    /// <summary>
    /// **一键「迁移演练」（R5.8 §21.4）**：调用宿主入口，在**助手数据根下的独立目录**跑事务组件模拟演练；
    /// **不接触真实 User 目录**；结果摘要写入 <see cref="MigrationRehearsalSummary"/>（页面展示）。
    /// </summary>
    public RelayCommand MigrationRehearsalCommand => new(async _ =>
    {
        if (_remoteTarget is not null)
        {
            var remote = await RemoteOperationAsync("task.migration.rehearsal");
            MigrationRehearsalSummary = (remote.Status == "ok" ? "✅ " : "❌ ") + remote.Message;
            return;
        }
        try
        {
            var report = _host.RunMigrationRehearsal();
            MigrationRehearsalSummary = (report.Success ? "✅ " : "❌ ") + report.Summary + "｜根：" + report.RehearsalRoot;
        }
        catch (Exception ex)
        {
            MigrationRehearsalSummary = "❌ 演练异常：" + ex.GetType().Name + ": " + ex.Message;
            try { _log?.Invoke("[任务中心] 迁移演练异常：" + ex.Message); } catch { }   // 日志异常不得穿透 UI 命令
        }
    });

    private bool _catalogRefreshInFlight;

    /// <summary>资源目录异步拉取（防重入；结果经 Current 快照被 2s 展示拍读取，S1 定时器本身不触发 IO）。</summary>
    private void KickCatalogRefresh()
    {
        if (_remoteTarget is not null) return;
        if (_catalogRefreshInFlight) return;
        _catalogRefreshInFlight = true;
        _ = Task.Run(async () =>
        {
            try { await _host.Catalog.RefreshAsync().ConfigureAwait(false); }
            catch { /* 降级原因留在快照 DegradedReason，展示拍可读 */ }
            finally { _catalogRefreshInFlight = false; }
        });
    }

    public RelayCommand OpenResourceEditorCommand => new(async p =>
    {
        if (p is not NodeEditVm node || Editing?.Nodes.Contains(node) != true) return;
        if (_remoteTarget is not null)
        {
            var reply = await RemoteRequestAsync("task.resource.editor.open", new
            { workflowId = Editing?.Draft.WorkflowId, nodeId = node.NodeId });
            SetStatus(reply.Message + "；配置内部任务可在当前页面远程修改", reply.Status != "ok");
            return;
        }
        try
        {
            await _host.OpenResourceEditorAsync(node.Model);
            SetStatus("已打开 BGI 中的引用资源。保存资源后，请更新引用修订；任务中心计划会自动保存。", false);
        }
        catch (Exception ex) { SetStatus("打开资源失败：" + ex.Message, true); }
    });

    public RelayCommand RefreshResourceReferenceCommand => new(async p =>
    {
        if (p is not NodeEditVm node || Editing?.Nodes.Contains(node) != true) return;
        var draft = Editing;
        try
        {
            var revision = _remoteTarget is null
                ? await _host.ReadResourceRevisionAsync(node.Model)
                : (await RemoteRequestAsync("task.resource.revision.get", new
                { workflowId = draft!.Draft.WorkflowId, nodeId = node.NodeId })).Revision
                    ?? throw new InvalidOperationException("执行端未返回引用修订");
            if (!ReferenceEquals(Editing, draft) || !draft.Nodes.Contains(node)) return;
            node.Model.Ref!.Revision = revision;
            node.NotifyReferenceChanged();
            SetStatus("已更新草稿中的引用修订，任务中心计划会自动保存；正在执行的资源保持原身份。", false);
        }
        catch (Exception ex) { SetStatus("更新引用失败：" + ex.Message, true); }
    });

    internal Task<WorkflowResourceEditor.ResourceTaskLayout> ReadResourceTaskLayoutAsync(NodeEditVm node)
        => _remoteTarget is null ? _host.ReadResourceTaskLayoutAsync(node.Model)
            : ReadRemoteResourceLayoutAsync(node);

    internal Task<string> ApplyResourceTaskLayoutAsync(NodeEditVm node,
        WorkflowResourceEditor.ResourceTaskLayout current,
        IReadOnlyList<WorkflowResourceEditor.ResourceTask> tasks, string? startTaskId)
        => _remoteTarget is null ? _host.ApplyResourceTaskLayoutAsync(node.Model, current, tasks, startTaskId)
            : ApplyRemoteResourceLayoutAsync(node, current, tasks, startTaskId);

    /// <summary>BGI 确认写盘后同步更新当前计划引用，避免下次执行仍携带旧修订。</summary>
    internal NodeEditVm? SaveResourceRevisionAfterLayout(NodeEditVm node, string revision)
    {
        if (_remoteTarget is not null)
            throw new InvalidOperationException("远程配置引用必须等待执行端确认保存");
        if (Editing?.Nodes.Contains(node) != true || node.Model.Ref is null) return null;
        var isGroup = node.Model.Kind == "resource.configGroup";
        foreach (var item in Editing.Nodes)
        {
            if (item.Model.Ref?.Config != node.Model.Ref.Config
                || (isGroup ? item.Model.Kind != "resource.configGroup"
                    : item.Model.Kind is not ("resource.oneDragonConfig" or "resource.singleTask"))) continue;
            item.Model.Ref.Revision = revision;
            item.NotifyReferenceChanged();
        }
        SaveFlowNow(isAutoSave: false);
        return StatusIsError ? null : Editing?.Nodes.FirstOrDefault(item => item.NodeId == node.NodeId);
    }

    internal async Task<NodeEditVm?> SaveResourceRevisionAfterLayoutAsync(NodeEditVm node, string revision)
    {
        if (_remoteTarget is null) return SaveResourceRevisionAfterLayout(node, revision);
        if (Editing?.Nodes.Contains(node) != true || node.Model.Ref is null) return null;
        var isGroup = node.Model.Kind == "resource.configGroup";
        foreach (var item in Editing.Nodes)
        {
            if (item.Model.Ref?.Config != node.Model.Ref.Config
                || (isGroup ? item.Model.Kind != "resource.configGroup"
                    : item.Model.Kind is not ("resource.oneDragonConfig" or "resource.singleTask"))) continue;
            item.Model.Ref.Revision = revision;
            item.NotifyReferenceChanged();
        }
        return await SaveRemoteFlowAsync()
            ? Editing?.Nodes.FirstOrDefault(item => item.NodeId == node.NodeId) : null;
    }

    public RelayCommand NewFlowCommand => new(async _ =>
    {
        if (!GuardNoOpenDraft()) return;
        if (_remoteTarget is not null)
        {
            // 先固定身份并写入监控端未决台账；丢回复后再次点击不会生成另一张计划。
            var doc = new WorkflowDocument
            {
                WorkflowId = "wf-" + Guid.NewGuid().ToString("N"),
                Name = "新计划 " + DateTime.Now.ToString("MM-dd HH:mm")
            };
            var reply = await RemoteOperationAsync("task.flow.save", doc);
            if (reply.Status == "ok") await LoadRemoteFlowAsync(doc.WorkflowId);
            return;
        }
        try
        {
            var doc = new WorkflowDocument { Name = "新计划 " + DateTime.Now.ToString("MM-dd HH:mm") };
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
    /// <summary>导入流程（C01 管理：从外部 JSON 文件导入到本店目录）。</summary>
    public RelayCommand ImportFlowCommand => new(async _ =>
    {
        if (!GuardNoOpenDraft()) return;
        if (!GuardNoOpenDraft()) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "流程文件 (*.json)|*.json",
            Title = "导入流程"
        };
        if (dialog.ShowDialog() != true) return;
        if (_remoteTarget is not null)
        {
            await ImportRemoteFlowAsync(dialog.FileName);
            return;
        }
        try
        {
            ImportFlowFromFile(dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = "导入失败：" + ex.Message;
            StatusIsError = true;
        }
    });

    /// <summary>一次选择并导入多张任务中心计划表；每张文件仍按单张计划的身份与修订合同导入。</summary>
    public RelayCommand ImportAllFlowsCommand => new(async _ =>
    {
        if (!GuardNoOpenDraft()) return;
        var dialog = new Microsoft.Win32.OpenFileDialog
        {
            Filter = "任务中心配置 (*.json)|*.json",
            Title = "导入全部任务中心配置",
            Multiselect = true,
        };
        if (dialog.ShowDialog() != true || dialog.FileNames.Length == 0) return;
        if (_remoteTarget is not null)
        {
            foreach (var file in dialog.FileNames) await ImportRemoteFlowAsync(file);
            return;
        }
        var imported = 0;
        try
        {
            foreach (var file in dialog.FileNames)
            {
                ImportFlowFromFile(file);
                imported++;
            }
            SetStatus($"已导入 {imported} 张任务中心计划表。", false);
        }
        catch (Exception ex) { SetStatus($"已导入 {imported} 张；其余导入失败：{ex.Message}", true); }
    });

    /// <summary>导出流程（C01 管理：将选中流程复制到用户指定路径）。</summary>
    public RelayCommand ExportFlowCommand => new(async p =>
    {
        if (p is not WorkflowListItemVm { IsQuarantined: false } entry) return;
        var dialog = new Microsoft.Win32.SaveFileDialog
        {
            Filter = "流程文件 (*.json)|*.json",
            Title = "导出流程",
            FileName = entry.Name + ".json"
        };
        if (dialog.ShowDialog() != true) return;
        if (_remoteTarget is not null)
        {
            await ExportRemoteFlowAsync(entry.WorkflowId, dialog.FileName);
            return;
        }
        try
        {
            ExportFlowToFile(entry.WorkflowId, dialog.FileName);
        }
        catch (Exception ex)
        {
            StatusMessage = "导出失败：" + ex.Message;
            StatusIsError = true;
        }
    });

    /// <summary>把当前任务中心的全部正式计划导出到一个目录；账号值由单张导出合同统一移除。</summary>
    public RelayCommand ExportAllFlowsCommand => new(async _ =>
    {
        if (!GuardNoOpenDraft()) return;
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = "导出全部任务中心配置" };
        if (dialog.ShowDialog() != true) return;
        if (_remoteTarget is not null)
        {
            var remoteUsedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var entry in Flows.Where(flow => flow.CanExport))
            {
                var destination = BuildUniqueExportFilePath(dialog.FolderName, entry.Name,
                    entry.WorkflowId, remoteUsedStems);
                await ExportRemoteFlowAsync(entry.WorkflowId, destination);
            }
            return;
        }
        var exported = 0;
        var usedStems = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        try
        {
            foreach (var entry in _host.ListFlows().Where(flow => flow.Status != WorkflowFileStatus.Quarantined))
            {
                var destination = TaskCenterPanelViewModel.BuildUniqueExportFilePath(
                    dialog.FolderName, entry.Name, entry.WorkflowId, usedStems);
                ExportFlowToFile(entry.WorkflowId, destination);
                exported++;
            }
            SetStatus($"已导出 {exported} 张任务中心计划表。", false);
        }
        catch (Exception ex) { SetStatus($"已导出 {exported} 张；其余导出失败：{ex.Message}", true); }
    });

    /// <summary>与页面同一路径：导入不覆盖既有身份，保留源文件；有草稿时拒绝切换。</summary>
    internal void ImportFlowFromFile(string sourceFile)
    {
        if (!GuardNoOpenDraft()) return;
        if (_remoteTarget is not null) { _ = ImportRemoteFlowAsync(sourceFile); return; }
        var imported = _host.Workflows.Import(sourceFile);
        SetStatus($"已导入流程「{imported}」", isError: false);
        Refresh();
    }

    /// <summary>公开分享默认去除已知账号值，保留资源引用、策略及未知字段；接收者重新绑定后执行。</summary>
    internal void ExportFlowToFile(string workflowId, string destination)
    {
        if (_remoteTarget is not null)
            throw new InvalidOperationException("远程导出请使用异步执行端导出入口");
        var entry = _host.ListFlows().Single(e => e.WorkflowId == workflowId);
        if (entry.Status == WorkflowFileStatus.Quarantined)
            throw new InvalidOperationException("隔离流程不能导出为可用流程");
        var target = System.IO.Path.GetFullPath(destination);
        var storeDir = System.IO.Path.GetDirectoryName(System.IO.Path.GetFullPath(entry.FilePath))!;
        if (target.StartsWith(storeDir + System.IO.Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("导出目标不能位于流程存储目录内，请另选分享路径");
        var source = _host.Workflows.LoadSnapshot(workflowId);
        var root = System.Text.Json.Nodes.JsonNode.Parse(JsonSerializer.Serialize(source.Document, CloneOptions))!;
        // 迁移提交资格属于本机存储，分享副本不得携带对本机事务的依赖。
        root.AsObject().Remove(WorkflowMigrationConsumer.TransactionField);
        RemoveAccountValues(root);
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            System.IO.File.WriteAllText(temporary, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }),
                new System.Text.UTF8Encoding(false));
            System.IO.File.Move(temporary, target, overwrite: true);
        }
        finally { if (System.IO.File.Exists(temporary)) System.IO.File.Delete(temporary); }
        SetStatus($"已导出流程「{entry.Name}」；账号值已移除，导入后请重新绑定。", isError: false);
    }

    private static void RemoveAccountValues(System.Text.Json.Nodes.JsonNode node)
    {
        if (node is System.Text.Json.Nodes.JsonObject obj)
        {
            foreach (var key in obj.Select(p => p.Key).ToArray())
            {
                if (key.Equals("uid", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("bindingCode", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("GenshinUid", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("AccountBindingCode", StringComparison.OrdinalIgnoreCase)
                    || key.Equals("AccountBinding", StringComparison.OrdinalIgnoreCase))
                    obj.Remove(key);
                else if (obj[key] is { } child) RemoveAccountValues(child);
            }
        }
        else if (node is System.Text.Json.Nodes.JsonArray array)
            foreach (var child in array) if (child is not null) RemoveAccountValues(child);
    }

    /// <summary>二轮（重要5）：已有未保存草稿（含修订冲突保留的草稿）时拒绝切换/新建，防静默丢稿。</summary>
    private bool GuardNoOpenDraft()
    {
        if (Editing is null) return true;
        if (_remoteTarget is not null)
        {
            if (!_remoteStale && !_remoteSaving && !_autoSavePending && !Editing.HasUnsavedChanges
                && _remoteSnapshot?.Flows.FirstOrDefault(flow => flow.WorkflowId == Editing.Draft.WorkflowId)?.Revision
                    == Editing.BaseRevision)
            { Editing = null; return true; }
            SetStatus("执行端计划有未保存或未确认草稿，请先保存或处理冲突", true);
            return false;
        }
        try
        {
            // 未改动且磁盘修订仍一致的草稿可以切换；原始无效输入、新建和冲突草稿必须保留。
            if (!Editing.HasUnsavedChanges && _host.LoadFlowSnapshot(Editing.Draft.WorkflowId!).Revision == Editing.BaseRevision)
            { Editing = null; return true; }
        }
        catch (Exception) { } // 无法复核时保留草稿，不把未知视为安全丢弃。
        SetStatus("已有未保存或修订冲突的编辑草稿，请等待自动保存完成或放弃后再操作。", isError: true);
        return false;
    }

    private bool _startResumeInFlight;

    public RelayCommand StartFlowCommand => new(async p =>
    {
        // 二轮（阻断2/阻断4）：CanStart 命令层守卫 + 真异步等待——UI 不阻塞，日志回调的 Dispatcher.Invoke 可正常完成，无互等死锁
        if (p is not WorkflowListItemVm { CanStart: true } item || _startResumeInFlight) return;
        if (_remoteTarget is not null) { await RemoteStartAsync(item.WorkflowId); return; }
        _startResumeInFlight = true; OnPropertyChanged(nameof(CanStartFromNode));
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
            _startResumeInFlight = false; OnPropertyChanged(nameof(CanStartFromNode));
        }
        Refresh();
    });

    public RelayCommand PreviewFlowCommand => new(async p =>
    {
        if (p is not WorkflowListItemVm item || !GuardNoOpenDraft()) return;
        if (_remoteTarget is not null) { await PreviewRemoteFlowAsync(item.WorkflowId); return; }
        BeginPreview(item.WorkflowId);
    });

    public RelayCommand SelectFlowCommand => new(async p =>
    {
        if (p is not WorkflowListItemVm item || Editing?.Draft.WorkflowId == item.WorkflowId || Previewing?.WorkflowId == item.WorkflowId) return;
        if (!GuardNoOpenDraft()) return;
        if (_remoteTarget is not null)
        {
            if (item.CanEdit) await LoadRemoteFlowAsync(item.WorkflowId);
            else await PreviewRemoteFlowAsync(item.WorkflowId);
            return;
        }
        if (item.CanEdit) BeginEdit(item.WorkflowId); else BeginPreview(item.WorkflowId);
    });

    public RelayCommand EditFlowCommand => new(async p =>
    {
        if (p is not WorkflowListItemVm { CanEdit: true } item) return;
        if (!GuardNoOpenDraft()) return;
        if (_remoteTarget is not null) { await LoadRemoteFlowAsync(item.WorkflowId); return; }
        BeginEdit(item.WorkflowId);
    });

    // ================= 编辑 =================

    private WorkflowEditVm? _editing;
    public WorkflowEditVm? Editing
    {
        get => _editing;
        private set
        {
            if (ReferenceEquals(_editing, value)) return;
            DetachEditingObservers(_editing);
            if (!SetProperty(ref _editing, value)) return;
            AttachEditingObservers(value);
            ClearPlanSyncConflict();
            if (value is not null) SelectedWorkflowId = value.Draft.WorkflowId;
            RefreshStartNodeVisuals();
            OnPropertyChanged(nameof(IsEditing));
            NotifyWorkspace();
        }
    }

    private void AttachEditingObservers(WorkflowEditVm? editing)
    {
        if (editing is null) return;
        editing.PropertyChanged += EditingPropertyChanged;
        editing.Nodes.CollectionChanged += EditingNodesChanged;
        foreach (var node in editing.Nodes) node.PropertyChanged += EditingNodePropertyChanged;
    }

    private void DetachEditingObservers(WorkflowEditVm? editing)
    {
        if (editing is null) return;
        editing.PropertyChanged -= EditingPropertyChanged;
        editing.Nodes.CollectionChanged -= EditingNodesChanged;
        foreach (var node in editing.Nodes) node.PropertyChanged -= EditingNodePropertyChanged;
    }

    private void EditingPropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyWorkspace();
        if (e.PropertyName is not (nameof(WorkflowEditVm.SelectedNode) or nameof(WorkflowEditVm.HasSelectedNode)
            or nameof(WorkflowEditVm.AppendSourceIndex) or nameof(WorkflowEditVm.AppendStatusText)))
            QueueAutoSave();
    }

    private void EditingNodesChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (e.OldItems is not null)
            foreach (var node in e.OldItems.OfType<NodeEditVm>()) node.PropertyChanged -= EditingNodePropertyChanged;
        if (e.NewItems is not null)
            foreach (var node in e.NewItems.OfType<NodeEditVm>()) node.PropertyChanged += EditingNodePropertyChanged;
        NotifyWorkspace();
        QueueAutoSave();
    }

    private void EditingNodePropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        NotifyWorkspace();
        if (e.PropertyName is nameof(NodeEditVm.DisplayName)
            or nameof(NodeEditVm.KindName)
            or nameof(NodeEditVm.ConfigName)
            or nameof(NodeEditVm.RevisionShort)
            or nameof(NodeEditVm.ResourceIndex)
            or nameof(NodeEditVm.ScheduleDisplayText)
            or nameof(NodeEditVm.WeekdaysDisplay)
            or nameof(NodeEditVm.AccountDisplay)
            or nameof(NodeEditVm.StrategySummary)
            or nameof(NodeEditVm.SettingsSummary)
            or nameof(NodeEditVm.HasAccount)
            or nameof(NodeEditVm.HasRedeem)
            or nameof(NodeEditVm.HasWeekdays)
            or nameof(NodeEditVm.HasNoStrategies)
            or nameof(NodeEditVm.ParticipationNote)
            or nameof(NodeEditVm.HasFailureOverride)
            or nameof(NodeEditVm.IsStartNode)
            or nameof(NodeEditVm.IsEnabled))
            return;
        QueueAutoSave();
    }
    public bool IsEditing => Editing is not null;

    private void QueueAutoSave()
    {
        if (_suppressAutoSave || Editing is null) return;
        _autoSavePending = true;
        _autoSaveTimer.Stop();
        if (!HasPlanSyncConflict) _autoSaveTimer.Start();
    }

    private async void AutoSaveTick()
    {
        _autoSaveTimer.Stop();
        if (!_autoSavePending || _autoSaveInProgress || Editing is null) return;
        if (_remoteTarget is not null)
        {
            await SaveRemoteFlowAsync(auto: true);
            return;
        }
        _autoSavePending = false;
        _autoSaveInProgress = true;
        try
        {
            SaveFlowNow(isAutoSave: true);
        }
        finally
        {
            _autoSaveInProgress = false;
        }
    }

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
            if (_host.Catalog.Current.Entries.Count == 0)
                Editing.MergeFallbackSources(_host.ListStoredResourceEntries());
            Previewing = null;
        }
        catch (Exception ex)
        {
            SetStatus($"加载流程失败：{ex.Message}", isError: true);
        }
    }

    public RelayCommand SaveFlowCommand => new(async _ =>
    {
        if (_remoteTarget is not null) await SaveRemoteFlowAsync();
        else SaveFlowNow(isAutoSave: false);
    });

    private void SaveFlowNow(bool isAutoSave)
    {
        if (_remoteTarget is not null) { _ = SaveRemoteFlowAsync(isAutoSave); return; }
        if (Editing is null) return;
        if (HasPlanSyncConflict) { SetStatus(PlanSyncMessage, true); return; }
        _autoSavePending = false;
        _suppressAutoSave = true;
        try
        {
            var selectedNodeId = Editing.SelectedNode?.NodeId;
            var submission = BuildSubmissionCopyForSave(Editing); // 二轮（阻断1/重要4）：基线+编辑集生成提交副本，保存失败不污染草稿
            var newRevision = _host.SaveFlow(submission, Editing.BaseRevision);
            _log($"[任务中心] 流程「{submission.Name}」已保存为新修订 {newRevision[..8]}（运行中实例不受影响，重载修订下一节点边界生效）");
            SetStatus(isAutoSave ? $"已自动保存计划「{submission.Name}」。" : $"已保存计划「{submission.Name}」。", isError: false);
            BeginEdit(submission.WorkflowId!);
            if (Editing is { } savedDraft && selectedNodeId is not null)
                savedDraft.SelectedNode = savedDraft.Nodes.FirstOrDefault(node => node.NodeId == selectedNodeId)
                    ?? savedDraft.Nodes.FirstOrDefault(node => node.IsResource);
            Refresh();
        }
        catch (WorkflowRevisionConflictException ex)
        {
            // 一轮 I4：冲突保留草稿，不覆盖未保存内容
            MarkPlanSyncConflict("另一端已经保存了这张计划。本端草稿已保留，请先读取最新计划后重新修改。" + ex.Message);
        }
        catch (Exception ex)
        {
            SetStatus("保存失败：" + ex.Message, isError: true);
        }
        finally
        {
            _suppressAutoSave = false;
        }
    }

    public RelayCommand DiscardEditCommand => new(_ => Editing = null);

    // ================= 预览（只读） =================

    private WorkflowPreviewVm? _previewing;
    public WorkflowPreviewVm? Previewing { get => _previewing; private set { SetProperty(ref _previewing, value); if (value is not null) SelectedWorkflowId = value.WorkflowId; OnPropertyChanged(nameof(IsPreviewing)); OnPropertyChanged(nameof(CanStartFromNode)); NotifyWorkspace(); } }
    public bool IsPreviewing => Previewing is not null;

    public sealed record StartPointVm(string WorkflowId, string Revision, string NodeId, string Label)
    {
        public override string ToString() => Label;
    }
    public ObservableCollection<StartPointVm> StartPoints { get; } = [];
    private StartPointVm? _selectedStartPoint;
    public StartPointVm? SelectedStartPoint { get => _selectedStartPoint; set { SetProperty(ref _selectedStartPoint, value); OnPropertyChanged(nameof(CanStartFromNode)); } }

    public bool CanStartFromNode => !_startResumeInFlight && Previewing is { } preview
        && SelectedStartPoint is { } point && StartPoints.Contains(point)
        && point.WorkflowId == preview.WorkflowId && point.Revision == preview.Revision
        && Flows.Any(flow => flow.WorkflowId == point.WorkflowId && flow.CanStart);

    public RelayCommand StartFromNodeCommand => new(async _ =>
    {
        if (!CanStartFromNode || SelectedStartPoint is not { } point) return;
        if (_remoteTarget is not null) { await RemoteStartAsync(point.WorkflowId, point.NodeId); return; }
        _startResumeInFlight = true; OnPropertyChanged(nameof(CanStartFromNode));
        try { ApplyActionResult(await _host.StartWorkflowAsync(point.WorkflowId, point.NodeId, point.Revision)); }
        catch (Exception ex) { SetStatus("指定起点启动失败：" + ex.Message, true); }
        finally { _startResumeInFlight = false; OnPropertyChanged(nameof(CanStartFromNode)); }
        Refresh();
    });

    private void BeginPreview(string workflowId)
    {
        if (_remoteTarget is not null) { _ = PreviewRemoteFlowAsync(workflowId); return; }
        try
        {
            var snapshot = _host.LoadFlowSnapshot(workflowId);
            Previewing = WorkflowPreviewVm.Build(snapshot.Document, snapshot.Revision);
            StartPoints.Clear();
            SelectedStartPoint = null;
            if (snapshot.Document.Activation?.Status != "candidate-ready")
                foreach (var node in snapshot.Document.Nodes)
                    StartPoints.Add(new StartPointVm(workflowId, snapshot.Revision, node.NodeId,
                        $"{StartPoints.Count + 1}. {node.Ref?.Config ?? node.Kind} [{node.NodeId}]"));
            SelectedStartPoint = StartPoints.FirstOrDefault();
        }
        catch (Exception ex)
        {
            // 隔离文件也可从目录条目拿到原因（List 已判型）；此处兜底展示
            StartPoints.Clear(); SelectedStartPoint = null;
            SetStatus($"预览加载失败：{ex.Message}", isError: true);
        }
    }

    public RelayCommand ClosePreviewCommand => new(_ => Previewing = null);

    // ================= 运行状态 =================

    public ObservableCollection<ActiveRunVm> ActiveRuns { get; } = [];
    public ObservableCollection<HistoryRunVm> HistoryRuns { get; } = [];

    /// <summary>
    /// 运行卡片只展示当前选中的计划表；旧 Unknown 记录保留在历史数据中，
    /// 不再把无法对账的旧责任渲染成会遮住计划编辑器的首屏卡片。
    /// </summary>
    public IReadOnlyList<ActiveRunVm> CurrentPlanRuns => SelectedWorkflowId is null
        ? Array.Empty<ActiveRunVm>()
        : ActiveRuns.Where(run => string.Equals(run.WorkflowId, SelectedWorkflowId, StringComparison.Ordinal)
            && !run.IsUnknown).ToArray();

    public bool HasCurrentPlanRuns => CurrentPlanRuns.Count > 0;
    public double CurrentPlanRunsHeight => HasCurrentPlanRuns ? double.NaN : 0;

    public bool HasActiveRuns => ActiveRuns.Count > 0;
    public bool CanStartSelectedPlan =>
        Flows.FirstOrDefault(f => f.WorkflowId == SelectedWorkflowId)?.CanStart == true
        && Editing?.Draft.WorkflowId == SelectedWorkflowId
        && HasStartableNodes
        && !_startResumeInFlight
        && !ActiveRuns.Any(run => run.WorkflowId == SelectedWorkflowId && run.BlocksNewStart);

    public RelayCommand StopRunCommand => new(async p => await ApplyRunActionAsync(p, WorkflowRunAction.Stop));
    public RelayCommand ReconcileTerminalRunCommand => new(async p =>
    {
        if (p is not HistoryRunVm vm) return;
        if (_remoteTarget is not null)
        {
            await RemoteOperationAsync("task.run.action", new { runId = vm.RunId, action = "Stop" },
                allowWhilePending: true);
            return;
        }
        try { ApplyActionResult(await _host.RequestRunActionAsync(vm.RunId, WorkflowRunAction.Stop)); }
        catch (Exception ex) { SetStatus(UserFacingActionMessage("结束状态核对失败：" + ex.Message), isError: true); }
        Refresh();
    });

    public RelayCommand SkipNodeCommand => new(async p => await ApplyRunActionAsync(p, WorkflowRunAction.SkipCurrent));
    public RelayCommand PauseRunCommand => new(async p => await ApplyRunActionAsync(p, WorkflowRunAction.Pause));
    public RelayCommand ReloadRunCommand => new(async p => await ApplyRunActionAsync(p, WorkflowRunAction.ReloadDefinition));

    public RelayCommand ResumeRunCommand => new(async p =>
    {
        if (p is not ActiveRunVm vm || _startResumeInFlight) return;
        if (_remoteTarget is not null) { await RemoteOperationAsync("task.run.resume", vm.RunId); return; }
        _startResumeInFlight = true; OnPropertyChanged(nameof(CanStartFromNode));
        try
        {
            ApplyActionResult(await _host.ResumeRunAsync(vm.RunId));
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingActionMessage("恢复失败：" + ex.Message), isError: true);
        }
        finally
        {
            _startResumeInFlight = false; OnPropertyChanged(nameof(CanStartFromNode));
        }
        Refresh();
    });

    private async Task ApplyRunActionAsync(object? p, WorkflowRunAction action)
    {
        if (p is not ActiveRunVm vm) return;
        if (_remoteTarget is not null)
        {
            await RemoteOperationAsync("task.run.action", new { runId = vm.RunId, action = action.ToString() },
                allowWhilePending: action == WorkflowRunAction.Stop);
            return;
        }
        SetStatus(
            vm.IsUnknown && action == WorkflowRunAction.Stop
                ? "正在处理这次运行，请稍候…"
                : action == WorkflowRunAction.Stop
                    ? "正在请求停止当前运行，请稍候…"
                    : "正在更新当前运行状态，请稍候…",
            isError: false);
        try
        {
            ApplyActionResult(await _host.RequestRunActionAsync(vm.RunId, action));
        }
        catch (Exception ex)
        {
            SetStatus(UserFacingActionMessage("动作失败：" + ex.Message), isError: true); // 二轮（重要5）：异常路径也走结构化反馈
        }
        Refresh();
    }

    private void ApplyActionResult(HostActionResult result)
    {
        SetStatus(UserFacingActionMessage(result.Message), isError: result.Status == HostActionStatus.Unavailable);
        _log($"[任务中心] 动作{result.Status}：{result.Message}");
    }

    /// <summary>Keep reconciliation vocabulary in diagnostics; show the user's next action in the task center.</summary>
    internal static string UserFacingActionMessage(string message)
    {
        if (message.Contains("结果不确定", StringComparison.OrdinalIgnoreCase)
            || message.Contains("未决发送", StringComparison.OrdinalIgnoreCase)
            || message.Contains("原受理存储不完整", StringComparison.OrdinalIgnoreCase)
            || message.Contains("原身份停止对账", StringComparison.OrdinalIgnoreCase)
            || message.Contains("主体原轮次", StringComparison.OrdinalIgnoreCase)
            || message.Contains("门面结清", StringComparison.OrdinalIgnoreCase))
            {
                var displayMessage = message.Contains("主体原轮次", StringComparison.OrdinalIgnoreCase)
                    || message.Contains("门面结清", StringComparison.OrdinalIgnoreCase)
                    ? "旧作业在当前 BGI 中没有唯一记录"
                    : message.Replace("Unknown", "未确认", StringComparison.OrdinalIgnoreCase)
                        .Replace("未决发送", "未确认的发送责任", StringComparison.OrdinalIgnoreCase);
                return "系统无法确认上一次任务是否已经执行：" + displayMessage
                    + "。本次没有重发，旧运行仍保留为未确认历史。若 BGI 已重启导致旧作业无法命中，"
                    + "请在左侧计划表名称上右键→删除当前计划表，并确认“保留未知历史并解除计划占用”，再新建计划；若启动仍提示执行通道占用，请先完成外部通道对账。";
            }

        if (message.Contains("运行状态已变化", StringComparison.OrdinalIgnoreCase))
            return "任务状态刚刚变化，请刷新任务中心后再操作。";

        if (message.Contains("任务中心宿主正在退出", StringComparison.OrdinalIgnoreCase))
            return "任务中心正在关闭，本次操作没有发送任务；重新打开后再试。";

        return message;
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
        OnPropertyChanged(nameof(ShowBgiConfigButton));
    }

    // ================= 刷新（展示快照；一轮 S1：不重建编辑草稿、不承担调度） =================

    public void Refresh()
    {
        if (_remoteTarget is not null) { _ = RefreshRemoteAsync(); return; }
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
            foreach (var group in Flows.GroupBy(f => f.Name, StringComparer.OrdinalIgnoreCase))
            {
                var siblings = group.ToArray();
                foreach (var item in siblings)
                {
                    var identity = item.WorkflowId.Length > 8 ? item.WorkflowId[^8..] : item.WorkflowId;
                    if (siblings.Count(f => f.WorkflowId.EndsWith(identity, StringComparison.Ordinal)) > 1)
                        identity = item.WorkflowId;
                    item.SetChoiceIdentityLabel(siblings.Length > 1 ? identity : "");
                }
            }

            // 运行卡片
            var runs = _host.ListRunOverview(20);
            var flowRevisions = entries.GroupBy(e => e.WorkflowId, StringComparer.Ordinal)
                .ToDictionary(g => g.Key, g => g.First().Revision, StringComparer.Ordinal);
            string? RevisionOf(WorkflowRunRecord run)
                => flowRevisions.TryGetValue(run.WorkflowId, out var revision) ? revision : null;
            SyncCollection(ActiveRuns, runs.Active, r => r.RunId,
                r => ActiveRunVm.Build(r, _host, RevisionOf(r)),
                (vm, r) => vm.Update(r, _host, RevisionOf(r)));
            OnPropertyChanged(nameof(HasActiveRuns));
            OnPropertyChanged(nameof(CurrentPlanRuns));
            OnPropertyChanged(nameof(HasCurrentPlanRuns));
            OnPropertyChanged(nameof(CurrentPlanRunsHeight));
            OnPropertyChanged(nameof(CanStartSelectedPlan));
            SyncCollection(HistoryRuns, runs.History, r => r.RunId,
                r => new HistoryRunVm(r),
                (vm, r) => vm.Update(r));

            CatalogStatusText = catalogStatus;
            ReconcileLocalEditingRevision(entries);
            // 编辑中的追加来源目录同步刷新（不打断草稿）
            Editing?.RefreshCatalog(_host.Catalog);
            if (_host.Catalog.Current.Entries.Count == 0)
                Editing?.MergeFallbackSources(_host.ListStoredResourceEntries());
            if (Previewing is { } preview)
            {
                var current = _host.LoadFlowSnapshot(preview.WorkflowId);
                if (current.Revision != preview.Revision) BeginPreview(preview.WorkflowId);
            }
            if (SelectedWorkflowId is null && Editing is null && Previewing is null)
            {
                var preferred = ReadCurrentWorkflowSelection();
                var first = preferred is not null
                    ? Flows.FirstOrDefault(f => f.WorkflowId == preferred)
                    : null;
                if (preferred is not null && first is null)
                {
                    ClearCurrentWorkflowSelection();
                    SetStatus("当前基础计划已不存在或已隔离，请手动选择另一个计划；CLI 未指定计划名时不会静默切换。", true);
                }
                else
                {
                    first ??= Flows.FirstOrDefault(f => f.CanStart) ?? Flows.FirstOrDefault(f => f.CanEdit) ?? Flows.FirstOrDefault(f => f.CanPreview);
                }
                if (first is { CanEdit: true }) BeginEdit(first.WorkflowId);
                else if (first is not null) BeginPreview(first.WorkflowId);
            }
            NotifyWorkspace();
            OnPropertyChanged(nameof(SelectedWorkflowId));
        }
        catch (Exception ex)
        {
            // 一轮 I7：单个坏文件/瞬时读失败不炸面板——留痕并保旧展示
            _log($"[任务中心] 面板刷新失败（{ex.GetType().Name}）：{ex.Message}");
        }
        finally
        {
            _refreshing = false;
            OnPropertyChanged(nameof(CanStartFromNode));
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
    private string _choiceIdentityLabel = "";
    public string ChoiceLabel => Name + (_choiceIdentityLabel.Length > 0 ? " · " + _choiceIdentityLabel : "")
        + (IsCandidate ? "（只读候选）" : IsQuarantined ? "（已隔离）" : "");
    internal void SetChoiceIdentityLabel(string value)
    { if (_choiceIdentityLabel == value) return; _choiceIdentityLabel = value; OnPropertyChanged(nameof(ChoiceLabel)); }
    public string ChoiceToolTip => $"{Name}\n流程 ID：{WorkflowId}\n修订：{_entry.Revision}\n{StateBadge}";
    public string RevisionShort => _entry.Revision.Length > 8 ? _entry.Revision[..8] : _entry.Revision;
    // 二轮（阻断2）：候选身份只依据 activation——与类型支持能力解耦（候选+未知类型不得显示为可编辑 active）
    public bool IsCandidate => string.Equals(_entry.ActivationStatus, "candidate-ready", StringComparison.Ordinal);
    public bool IsQuarantined => _entry.Status == WorkflowFileStatus.Quarantined;
    public bool IsActive => !IsCandidate && !IsQuarantined;
    public bool CanStart => IsActive && _entry.UnsupportedKinds.Count == 0;
    public bool CanEdit => IsActive;
    public bool CanPreview => !IsQuarantined;
    public bool CanExport => !IsQuarantined;

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

    private int _renderedRecordRevision = -1;
    private string? _renderedFlowRevision;
    private bool _renderedDriving;

    internal static ActiveRunVm Build(WorkflowRunRecord run, TaskCenterHost host, string? currentFlowRevision = null)
    {
        var vm = new ActiveRunVm();
        vm.Update(run, host, currentFlowRevision);
        return vm;
    }

    internal static ActiveRunVm BuildRemote(WorkflowRunRecord run, WorkflowSnapshot? snapshot,
        bool driving, string? currentFlowRevision = null)
    {
        var vm = new ActiveRunVm();
        vm.UpdateRemote(run, snapshot, driving, currentFlowRevision);
        return vm;
    }

    public string Key => RunId;
    public string RunId { get; private set; } = "";
    public string RunIdShort => RunId.Length > 8 ? RunId[..8] : RunId;
    public string WorkflowId { get; private set; } = "";
    public string WorkflowName { get; private set; } = "";
    public string StateText { get; private set; } = "";
    public string ActionHintText { get; private set; } = "";
    public string StateColor { get; private set; } = "#75B59F";
    public string StopActionText { get; private set; } = "停止";
    public string StopActionToolTip { get; private set; } = "请求停止并确认结果";
    public bool IsUnknown => _run.State == WorkflowRunState.Unknown;
    public bool BlocksNewStart { get; private set; }
    public string CurrentNodeText { get; private set; } = "";
    internal string? CurrentNodeId => _run.Cursor?.NodeId;
    internal IReadOnlyList<string> NextNodeIds { get; private set; } = [];
    public string ProgressChainText { get; private set; } = "";
    public string? NoteText { get; private set; }

    public bool CanStop { get; private set; }
    public bool CanSkip { get; private set; }
    public bool CanPause { get; private set; }
    public bool CanReload { get; private set; }
    public bool CanResume { get; private set; }

    internal void Update(WorkflowRunRecord run, TaskCenterHost host, string? currentFlowRevision = null)
    {
        WorkflowSnapshot? snapshot = null;
        try { snapshot = host.Workflows.LoadSnapshot(run.WorkflowId); }
        catch { }
        UpdateRemote(run, snapshot, host.IsDriving(run.WorkflowId), currentFlowRevision);
    }

    internal void UpdateRemote(WorkflowRunRecord run, WorkflowSnapshot? flowSnapshot,
        bool driving, string? currentFlowRevision = null)
    {
        if (!string.IsNullOrEmpty(currentFlowRevision)
            && RunId == run.RunId && _renderedRecordRevision == run.RecordRevision
            && string.Equals(_renderedFlowRevision, currentFlowRevision, StringComparison.Ordinal)
            && _renderedDriving == driving) return;

        _run = run;
        RunId = run.RunId;
        WorkflowId = run.WorkflowId;
        WorkflowName = flowSnapshot?.Document.Name ?? run.WorkflowId;
        StateText = run.State switch
        {
            WorkflowRunState.Planned => "已计划",
            WorkflowRunState.Running => "运行中",
            WorkflowRunState.Waiting => $"等待触发（{run.Wait?.NextTriggerAt:MM-dd HH:mm}）",
            WorkflowRunState.LocalWaitParking => run.LocalWaitDecision is { Kind: LocalWaitDecisionKind.Wait, Binding: not null }
                ? "等待空闲 · 按优先级接续" : "等待确认 · 可恢复或停止",
            WorkflowRunState.OnlineHoeingParking => "联机锄地中 · 等待原计划接续",
            WorkflowRunState.Completing => "收尾中",
            WorkflowRunState.Paused => "已暂停",
            WorkflowRunState.Interrupted => "已中断（可显式恢复）",
            WorkflowRunState.Unknown => "无法确认上次结果",
            _ => run.State.ToString(),
        };
        ActionHintText = run.State switch
        {
            WorkflowRunState.Unknown => "上次任务结果未知，当前计划已禁止重复启动；请保留未确认历史并删除旧计划后新建计划。",
            WorkflowRunState.Running => "任务正在执行；需要结束时点击「停止」。",
            WorkflowRunState.Paused => "任务已暂停；准备继续时点击「恢复」。",
            WorkflowRunState.Interrupted => "任务已中断；确认没有其他同计划运行后点击「恢复」。",
            WorkflowRunState.LocalWaitParking => "任务正在等待可用执行位置；可继续等待，或点击「停止」结束本次计划。",
            WorkflowRunState.OnlineHoeingParking => "原计划保留在当前节点；锄地完成且恢复作业终态确认后自动继续。",
            WorkflowRunState.Waiting => "任务正在等待设定时间；需要取消时点击「停止」。",
            WorkflowRunState.Completing => "任务正在收尾，请等待状态确认。",
            _ => "",
        };
        StateColor = run.State switch
        {
            WorkflowRunState.Unknown => "#E4B86A",
            WorkflowRunState.Running => "#75B59F",
            WorkflowRunState.Paused or WorkflowRunState.Interrupted or WorkflowRunState.OnlineHoeingParking => "#D2B66F",
            WorkflowRunState.Completing => "#AAB6C5",
            _ => "#75B59F",
        };
        StopActionText = "停止";
        StopActionToolTip = "请求停止当前运行";
        BlocksNewStart = run.State is WorkflowRunState.Running or WorkflowRunState.Waiting
            or WorkflowRunState.Paused or WorkflowRunState.Completing or WorkflowRunState.Unknown
            or WorkflowRunState.LocalWaitParking or WorkflowRunState.OnlineHoeingParking;
        var (current, chain) = DescribeProgress(run, flowSnapshot);
        CurrentNodeText = current;
        NextNodeIds=[];
        try
        {
            if (flowSnapshot is { } snapshot)
            {
                var plan=new WorkflowPlan(snapshot.Document);
                if(snapshot.Revision==run.WorkflowRevision && run.Cursor is {} cursor && plan.TryLocate(cursor.NodeId,cursor.Occurrence,cursor.LoopIteration,out var at))
                {
                    at=at with {PathLane=cursor.PathLane};
                    var sides=plan.NodeAt(at).Kind=="control.condition"?new[]{"branchYes","branchNo"}:new[]{"succeeded"};
                    NextNodeIds=sides.Select(side=>plan.Next(at,side)?.NodeId).Where(id=>id is not null).Cast<string>().Distinct().ToArray();
                }
            }
        }
        catch { /* 新修订或不可读时不猜下一节点。 */ }
        ProgressChainText = chain;
        NoteText = run.Note;

        CanStop = run.State is WorkflowRunState.Running or WorkflowRunState.Waiting or WorkflowRunState.Paused
            or WorkflowRunState.Completing or WorkflowRunState.LocalWaitParking or WorkflowRunState.OnlineHoeingParking;
        CanSkip = driving && run.State == WorkflowRunState.Running;
        CanPause = run.OnlineHoeingHandoff is null
            && driving && (run.State is WorkflowRunState.Running or WorkflowRunState.Waiting);
        CanReload = driving && run.State is WorkflowRunState.Running or WorkflowRunState.Waiting or WorkflowRunState.Paused;
        // 无驱动的旧 Running/Waiting 允许显式恢复；宿主屏障负责 Interrupted/Unknown，界面不改记录。
        CanResume = run.OnlineHoeingHandoff is null
            && (run.State is WorkflowRunState.Interrupted or WorkflowRunState.Paused or WorkflowRunState.LocalWaitParking
                || (!driving && run.State is WorkflowRunState.Running or WorkflowRunState.Waiting));
        _renderedRecordRevision = run.RecordRevision;
        _renderedFlowRevision = currentFlowRevision ?? flowSnapshot?.Revision;
        _renderedDriving = driving;
        OnPropertyChanged(string.Empty);
    }

    /// <summary>进度链文本（一轮 S1：NodeOutcomes 可视呈现限长 20；游标节点标「运行中」）。</summary>
    private static (string Current, string Chain) DescribeProgress(WorkflowRunRecord run, WorkflowSnapshot? snapshot)
    {
        try
        {
            if (snapshot is null) return ("当前节点：—（流程定义不可读）", "");
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
                    "succeeded" => "已完成",
                    "skippedUser" => "显式跳过",
                    "skippedFilter" => "过滤跳过",
                    "failed" => "失败",
                    "rejected" => "已拒绝",
                    "cancelled" => "已取消",
                    "cancelUnconfirmed" => "停止结果待确认",
                    "unknown" => "待确认",
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

    public string RunId => _run.RunId;

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
