using System.IO;
using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class TaskCenterPanelViewModel
{
    /// <summary>
    /// 由基础页在打开“新增配置单”时调用。资源目录是异步快照，不能假设构造时已经完成；
    /// 这里同步刷新当前快照并合并已保存计划中的离线资源，避免点击后得到空菜单却没有解释。
    /// </summary>
    internal void RefreshEditingResourceSources()
    {
        if (Editing is not { } editing) return;
        editing.RefreshCatalog(_host.Catalog);
        editing.MergeFallbackSources(_host.ListStoredResourceEntries());
        NotifyWorkspace();
    }

    internal void ShowInlineActionFeedback(string message, bool isError)
        => SetStatus(message, isError);

    /// <summary>导出整批计划时为同名计划生成不冲突的文件名，避免“全部导出”静默覆盖。</summary>
    internal static string BuildUniqueExportFilePath(
        string folder,
        string planName,
        string workflowId,
        ISet<string> usedStems)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(folder);
        ArgumentNullException.ThrowIfNull(usedStems);
        var stem = string.Concat((planName ?? string.Empty)
            .Select(ch => Path.GetInvalidFileNameChars().Contains(ch) ? '_' : ch));
        if (string.IsNullOrWhiteSpace(stem)) stem = "计划表";

        var candidate = stem;
        var shortId = string.IsNullOrWhiteSpace(workflowId)
            ? "copy"
            : workflowId.Length > 8 ? workflowId[^8..] : workflowId;
        var suffix = 0;
        while (!usedStems.Add(candidate)
            || File.Exists(Path.Combine(folder, candidate + ".json")))
        {
            suffix++;
            candidate = $"{stem}_{shortId}{(suffix == 1 ? string.Empty : "_" + suffix)}";
        }
        return Path.Combine(folder, candidate + ".json");
    }

    internal static bool TryResolveContinuousPlan(
        IEnumerable<WorkflowListItemVm> flows,
        string? planName,
        string? currentWorkflowId,
        out WorkflowListItemVm? item,
        out string detail)
    {
        var candidates = string.IsNullOrWhiteSpace(planName)
            ? flows.Where(f => f.WorkflowId == currentWorkflowId).ToArray()
            : flows.Where(f => string.Equals(f.Name, planName, StringComparison.Ordinal)).ToArray();
        if (candidates.Length == 1 && candidates[0].CanStart)
        {
            item = candidates[0];
            detail = string.Empty;
            return true;
        }

        item = null;
        detail = string.IsNullOrWhiteSpace(planName)
            ? "没有可解析的当前基础计划"
            : candidates.Length == 0 ? $"找不到计划「{planName}」"
            : candidates.Length > 1 ? $"计划「{planName}」不唯一"
            : $"计划「{candidates[0].Name}」当前不可启动（{candidates[0].StateBadge}）";
        return false;
    }

    /// <summary>基础模式只列出可编辑的正式计划；候选/隔离文件留给独立管理与高级入口。</summary>
    public IReadOnlyList<WorkflowListItemVm> BasicFlows => Flows.Where(flow => flow.IsActive).ToArray();
    public bool HasFlows => BasicFlows.Count > 0;
    public bool CanAddTasks => Editing is not null;
    /// <summary>基础模式只投影真正的 resource.* 配置单；控制节点仍留在内部执行模型中。</summary>
    public IReadOnlyList<NodeEditVm> ResourceNodes => Editing?.Nodes.Where(node => node.IsResource).ToArray()
        ?? Array.Empty<NodeEditVm>();
    public bool HasSelectedResource => Editing?.SelectedNode?.IsResource == true
        && ResourceNodes.Contains(Editing.SelectedNode);
    public bool IsEmptyPlan => ResourceNodes.Count == 0;
    public bool HasStartableNodes => ResourceNodes.Any(node => node.IsEnabled);
    public string CurrentPlanTitle => Editing?.NameText
        ?? Flows.FirstOrDefault(f => f.WorkflowId == SelectedWorkflowId)?.Name
        ?? "未选择计划";
    public int EnabledNodeCount => ResourceNodes.Count(node => node.IsEnabled);
    public string CurrentPlanSummary => Editing is { } draft
        ? ResourceNodes.Count == 0
            ? "空计划 · 先新增配置单"
            : $"{ResourceNodes.Count} 个配置单 · {EnabledNodeCount} 个已启用"
        : "选择一张计划表开始编辑";
    public string WorkspaceHint => !HasFlows ? "先新建一个计划，再添加任务和安排时间。"
        : IsPreviewing ? "正在查看计划。选择可编辑计划后即可安排任务。"
        : IsEmptyPlan ? "当前计划还没有配置单：点击「新增配置单」（添加任务），或从左侧选择已有计划。"
        : "操作顺序：①选择计划 ②添加任务 ③设置时间 ④保存 ⑤开始。若上方有运行卡片，先处理当前状态，再开始新的运行。";

    private void NotifyWorkspace()
    {
        if (Editing is { } draft)
        {
            var resourceNodes = draft.Nodes.Where(node => node.IsResource).ToArray();
            for (var i = 0; i < resourceNodes.Length; i++) resourceNodes[i].ResourceIndex = i + 1;
            if (draft.SelectedNode is not { IsResource: true } selected || !resourceNodes.Contains(selected))
                draft.SelectedNode = resourceNodes.FirstOrDefault();
        }
        OnPropertyChanged(nameof(HasFlows));
        OnPropertyChanged(nameof(BasicFlows));
        OnPropertyChanged(nameof(CanAddTasks));
        OnPropertyChanged(nameof(ResourceNodes));
        OnPropertyChanged(nameof(HasSelectedResource));
        OnPropertyChanged(nameof(IsEmptyPlan));
        OnPropertyChanged(nameof(HasStartableNodes));
        OnPropertyChanged(nameof(WorkspaceHint));
        OnPropertyChanged(nameof(CurrentPlanTitle));
        OnPropertyChanged(nameof(CurrentPlanSummary));
        OnPropertyChanged(nameof(EnabledNodeCount));
        OnPropertyChanged(nameof(CanStartSelectedPlan));
    }

    /// <summary>基础模式“启动配置单”：以当前选中的 resource.* 节点作为入口，仍经同一宿主安全门。</summary>
    public RelayCommand StartSelectedResourceCommand => new(async p =>
    {
        var node = p as NodeEditVm ?? Editing?.SelectedNode;
        if (node is not { IsResource: true } || Editing is not { } draft
            || !draft.Nodes.Contains(node) || _startResumeInFlight)
            return;

        _startResumeInFlight = true;
        SetStatus("正在准备 BGI 执行环境…", false);
        OnPropertyChanged(nameof(CanStartSelectedPlan));
        try
        {
            if (draft.HasUnsavedChanges)
            {
                _host.SaveFlow(draft.BuildSubmissionCopy(), draft.BaseRevision);
                BeginEdit(draft.Draft.WorkflowId!);
                node = Editing?.Nodes.FirstOrDefault(n => n.NodeId == node.NodeId) ?? node;
            }

            var revision = _host.LoadFlowSnapshot(draft.Draft.WorkflowId!).Revision;
            ApplyActionResult(await _host.StartWorkflowAsync(draft.Draft.WorkflowId!, node.NodeId, revision, node.NodeId));
        }
        catch (Exception ex) { SetStatus("启动配置单失败：" + ex.Message + "；编辑内容已保留。", true); }
        finally
        {
            _startResumeInFlight = false;
            Refresh();
            OnPropertyChanged(nameof(CanStartSelectedPlan));
        }
    });

    /// <summary>基础模式顶部“删除”：与行右键共用同一条资源删除路径并给出即时反馈。</summary>
    public RelayCommand RemoveSelectedResourceCommand => new(p =>
    {
        var node = p as NodeEditVm ?? Editing?.SelectedNode;
        if (Editing is not { } editing || node is not { IsResource: true } || !editing.Nodes.Contains(node))
        {
            SetStatus("请先选择一行配置单。", isError: true);
            return;
        }

        var name = node.DisplayName;
        editing.RemoveNodeCommand.Execute(node);
        Refresh();
        SetStatus($"已移除配置单「{name}」，请点击“保存”使计划生效。", isError: false);
    });

    // The start button runs the plan the person is currently looking at, including their edits.
    public RelayCommand StartSelectedPlanCommand => new(async p =>
    {
        if (p is not WorkflowListItemVm flow || _startResumeInFlight) return;
        if (!flow.CanStart)
        {
            SetStatus($"计划「{flow.Name}」当前不可启动：{flow.StateBadge}。", isError: true);
            Refresh();
            return;
        }
        _startResumeInFlight = true;
        SetStatus("正在准备 BGI 执行环境…", false);
        OnPropertyChanged(nameof(CanStartFromNode));
        OnPropertyChanged(nameof(CanStartSelectedPlan));
        try
        {
            if (Editing is { } draft)
            {
                if (draft.Draft.WorkflowId != flow.WorkflowId)
                { SetStatus("请先保存当前计划，再开始另一个计划。", true); return; }
                if (draft.HasUnsavedChanges)
                {
                    _host.SaveFlow(draft.BuildSubmissionCopy(), draft.BaseRevision);
                    BeginEdit(flow.WorkflowId);
                }
            }
            var result = await _host.StartWorkflowAsync(flow.WorkflowId);
            if (!HasStartableNodes && result.Status == HostActionStatus.Unavailable)
                SetStatus("当前计划没有启用的配置单，请先勾选一行并保存。" +
                    (result.Message.Contains("BGI", StringComparison.OrdinalIgnoreCase) ? "；BGI未就绪。" : ""), isError: true);
            else
                ApplyActionResult(result);
        }
        catch (Exception ex) { SetStatus("开始失败：" + ex.Message + "；编辑内容已保留。", true); }
        finally
        {
            _startResumeInFlight = false;
            Refresh();
            OnPropertyChanged(nameof(CanStartFromNode));
            OnPropertyChanged(nameof(CanStartSelectedPlan));
        }
    });

    /// <summary>基础模式从当前配置单入口启动，使用同一修订和宿主安全门。</summary>
    public RelayCommand StartFromSelectedNodeCommand => new(async p =>
    {
        if (p is not NodeEditVm node || Editing is not { } draft || _startResumeInFlight) return;
        _startResumeInFlight = true;
        SetStatus("正在准备 BGI 执行环境…", false);
        OnPropertyChanged(nameof(CanStartSelectedPlan));
        try
        {
            if (draft.HasUnsavedChanges)
            {
                _host.SaveFlow(draft.BuildSubmissionCopy(), draft.BaseRevision);
                BeginEdit(draft.Draft.WorkflowId!);
                node = Editing?.Nodes.FirstOrDefault(n => n.NodeId == node.NodeId) ?? node;
            }
            var revision = _host.LoadFlowSnapshot(draft.Draft.WorkflowId!).Revision;
            ApplyActionResult(await _host.StartWorkflowAsync(draft.Draft.WorkflowId!, node.NodeId, revision));
        }
        catch (Exception ex) { SetStatus("从此处开始失败：" + ex.Message, true); }
        finally
        {
            _startResumeInFlight = false;
            Refresh();
            OnPropertyChanged(nameof(CanStartSelectedPlan));
        }
    });

    /// <summary>测试接缝只替代确认结果，不替代删除写入路径。</summary>
    internal Func<WorkflowListItemVm, bool>? ConfirmDeleteOverride { get; set; }

    public RelayCommand DeleteFlowCommand => new(p =>
    {
        if (p is not WorkflowListItemVm { IsQuarantined: false } flow) return;
        // 删除是用户明确点下的管理动作：不能被当前草稿的切换守卫静默挡住。
        // 计划删除可从“计划表”菜单恢复，右键操作直接生效，避免确认框被宿主窗口遮挡时看起来像“点击无反应”。
        var confirmed = ConfirmDeleteOverride?.Invoke(flow) ?? true;
        if (!confirmed) return;
        try
        {
            var result = _host.DeleteFlow(flow.WorkflowId, _host.LoadFlowSnapshot(flow.WorkflowId).Revision);
            ApplyActionResult(result);
            if (result.Status != HostActionStatus.Unavailable)
            {
                Editing = null; Previewing = null; SelectedWorkflowId = null;
                ClearCurrentWorkflowSelection();
            }
            Refresh();
        }
        catch (Exception ex) { SetStatus("删除失败：" + ex.Message, true); }
    });

    public IReadOnlyList<WorkflowDeletionReceipt> DeletedFlows => _host.ListDeletedFlows();
    public void RestoreDeletedFlow(string recoveryId)
    {
        if (!GuardNoOpenDraft()) return;
        try { ApplyActionResult(_host.RestoreFlow(recoveryId)); Refresh(); }
        catch (Exception ex) { SetStatus("恢复失败：" + ex.Message, true); }
    }
}
