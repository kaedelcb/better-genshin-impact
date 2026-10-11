using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class TaskCenterPanelViewModel
{
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

    public bool HasFlows => Flows.Count > 0;
    public bool CanAddTasks => Editing is not null;
    public bool IsEmptyPlan => Editing is { Nodes.Count: 0 };
    public bool HasStartableNodes => Editing?.Nodes.Any(node => node.IsEnabled && node.Model.Kind != "control.end") == true;
    public string CurrentPlanTitle => Editing?.NameText
        ?? Flows.FirstOrDefault(f => f.WorkflowId == SelectedWorkflowId)?.Name
        ?? "未选择计划";
    public int EnabledNodeCount => Editing?.Nodes.Count(node => node.IsEnabled) ?? 0;
    public string CurrentPlanSummary => Editing is { } draft
        ? draft.Nodes.Count == 0
            ? "空计划 · 先新增配置单"
            : $"{draft.Nodes.Count} 个配置单 · {EnabledNodeCount} 个已启用"
        : "选择一张计划表开始编辑";
    public string WorkspaceHint => !HasFlows ? "先新建一个计划，再添加任务和安排时间。"
        : IsPreviewing ? "正在查看计划。选择可编辑计划后即可安排任务。"
        : IsEmptyPlan ? "当前计划还没有配置单：点击「新增配置单」（添加任务），或从左侧选择已有计划。"
        : "操作顺序：①选择计划 ②添加任务 ③设置时间 ④保存 ⑤开始。若上方有运行卡片，先处理当前状态，再开始新的运行。";

    private void NotifyWorkspace()
    {
        OnPropertyChanged(nameof(HasFlows));
        OnPropertyChanged(nameof(CanAddTasks));
        OnPropertyChanged(nameof(IsEmptyPlan));
        OnPropertyChanged(nameof(HasStartableNodes));
        OnPropertyChanged(nameof(WorkspaceHint));
        OnPropertyChanged(nameof(CurrentPlanTitle));
        OnPropertyChanged(nameof(CurrentPlanSummary));
        OnPropertyChanged(nameof(EnabledNodeCount));
        OnPropertyChanged(nameof(CanStartSelectedPlan));
    }

    // The start button runs the plan the person is currently looking at, including their edits.
    public RelayCommand StartSelectedPlanCommand => new(async p =>
    {
        if (p is not WorkflowListItemVm { CanStart: true } flow || _startResumeInFlight) return;
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
            ApplyActionResult(await _host.StartWorkflowAsync(flow.WorkflowId));
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

    /// <summary>基础模式复刻 BGI「从此处开始执行」：使用同一修订和宿主显式起点入口。</summary>
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

    public RelayCommand DeleteFlowCommand => new(p =>
    {
        if (p is not WorkflowListItemVm { IsQuarantined: false } flow) return;
        if (!GuardNoOpenDraft()) return;
        if (MessageBox.Show($"删除计划「{flow.Name}」？\n任务资源和运行历史会保留，计划可从管理菜单恢复。",
                "删除计划", MessageBoxButton.OKCancel, MessageBoxImage.Question) != MessageBoxResult.OK) return;
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
