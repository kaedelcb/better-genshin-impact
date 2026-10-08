using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class TaskCenterPanelViewModel
{
    public bool HasFlows => Flows.Count > 0;
    public bool CanAddTasks => Editing is not null;
    public bool IsEmptyPlan => Editing is { Nodes.Count: 0 };
    public string WorkspaceHint => !HasFlows ? "先新建一个计划，再添加任务和安排时间。"
        : IsPreviewing ? "正在查看计划。选择可编辑计划后即可安排任务。"
        : IsEmptyPlan ? "点击「添加任务」，安排你的第一个任务。"
        : "点击任务查看设置，拖动任务调整时间；保存后开始运行。";

    private void NotifyWorkspace()
    {
        OnPropertyChanged(nameof(HasFlows));
        OnPropertyChanged(nameof(CanAddTasks));
        OnPropertyChanged(nameof(IsEmptyPlan));
        OnPropertyChanged(nameof(WorkspaceHint));
    }

    // The start button runs the plan the person is currently looking at, including their edits.
    public RelayCommand StartSelectedPlanCommand => new(async p =>
    {
        if (p is not WorkflowListItemVm { CanStart: true } flow || _startResumeInFlight) return;
        _startResumeInFlight = true;
        OnPropertyChanged(nameof(CanStartFromNode));
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
        finally { _startResumeInFlight = false; Refresh(); OnPropertyChanged(nameof(CanStartFromNode)); }
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
            { Editing = null; Previewing = null; SelectedWorkflowId = null; }
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
