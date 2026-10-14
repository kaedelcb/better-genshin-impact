using System.Windows;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class TaskCenterPanelViewModel
{
    private bool _hasPlanSyncConflict;
    public bool HasPlanSyncConflict { get => _hasPlanSyncConflict; private set => SetProperty(ref _hasPlanSyncConflict, value); }
    private string? _planSyncMessage;
    public string? PlanSyncMessage { get => _planSyncMessage; private set => SetProperty(ref _planSyncMessage, value); }
    internal Func<bool>? ConfirmReloadLatestPlanForTest { get; set; }

    private bool DraftHasPendingEdits(WorkflowEditVm draft)
        => draft.HasUnsavedChanges || _autoSavePending || _autoSaveInProgress || _remoteSaving;

    private void ClearPlanSyncConflict()
    {
        HasPlanSyncConflict = false;
        PlanSyncMessage = null;
    }

    private void MarkPlanSyncConflict(string message)
    {
        HasPlanSyncConflict = true;
        PlanSyncMessage = message;
        _autoSaveTimer.Stop();
        SetStatus(message, true);
    }

    private void ReconcileLocalEditingRevision(IReadOnlyList<WorkflowCatalogEntry> entries)
    {
        if (Editing is not { } draft || _autoSaveInProgress) return;
        var entry = entries.FirstOrDefault(item => item.WorkflowId == draft.Draft.WorkflowId);
        if (entry?.Status != WorkflowFileStatus.Ready)
        {
            MarkPlanSyncConflict("当前计划已在另一端删除、隔离或变为不可编辑。本端草稿保留，请确认后读取最新计划。");
            return;
        }
        if (entry.Revision == draft.BaseRevision) return;
        if (DraftHasPendingEdits(draft))
        {
            MarkPlanSyncConflict("另一端已经修改了这张计划。本端有未保存的修改，已保留草稿并暂停自动保存；请读取最新计划后重新修改。");
            return;
        }
        ReloadLocalEditingPlan(draft);
        SetStatus("已同步另一端保存的最新计划，可继续编辑。", false);
    }

    private void ReloadLocalEditingPlan(WorkflowEditVm draft)
    {
        var selectedNodeId = draft.SelectedNode?.NodeId;
        var previousSuppression = _suppressAutoSave;
        _suppressAutoSave = true;
        _autoSaveTimer.Stop();
        _autoSavePending = false;
        try
        {
            BeginEdit(draft.Draft.WorkflowId!);
            if (Editing is { } refreshed && selectedNodeId is not null)
                refreshed.SelectedNode = refreshed.Nodes.FirstOrDefault(node => node.NodeId == selectedNodeId);
        }
        finally { _suppressAutoSave = previousSuppression; }
    }

    private async Task ReconcileRemoteEditingRevisionAsync(IReadOnlyList<WorkflowCatalogEntry> entries)
    {
        if (Editing is not { } draft || _remoteSaving) return;
        var entry = entries.FirstOrDefault(item => item.WorkflowId == draft.Draft.WorkflowId);
        if (entry?.Status != WorkflowFileStatus.Ready)
        {
            MarkPlanSyncConflict("执行端当前计划已删除、隔离或变为不可编辑。本端草稿保留，请确认后读取最新计划。");
            return;
        }
        if (entry.Revision == draft.BaseRevision) return;
        if (DraftHasPendingEdits(draft))
        {
            MarkPlanSyncConflict("执行端已经修改了这张计划。本端有未保存的修改，已保留草稿并暂停自动保存；请读取最新计划后重新修改。");
            return;
        }
        var generation = _remoteLoadGeneration;
        var reply = await RemoteRequestAsync("task.flow.get", draft.Draft.WorkflowId);
        if (_remoteAbandoned || generation != _remoteLoadGeneration || !ReferenceEquals(Editing, draft)) return;
        if (DraftHasPendingEdits(draft))
        {
            MarkPlanSyncConflict("读取期间本端又有新的修改。草稿已保留，请确认后读取最新计划。");
            return;
        }
        if (reply.Status != "ok" || string.IsNullOrWhiteSpace(reply.Revision) || string.IsNullOrWhiteSpace(reply.PayloadJson))
        { _remoteStale = true; SetStatus("最新计划读取失败，当前显示保留：" + reply.Message, true); return; }
        var document = JsonSerializer.Deserialize<WorkflowDocument>(reply.PayloadJson, RemoteReadOptions)
            ?? throw new InvalidOperationException("最新计划为空");
        if (document.WorkflowId != draft.Draft.WorkflowId || document.Activation?.Status == "candidate-ready")
        { MarkPlanSyncConflict("执行端计划身份或可编辑状态已变化，当前草稿保留。"); return; }
        var selectedNodeId = draft.SelectedNode?.NodeId;
        var previousSuppression = _suppressAutoSave;
        _suppressAutoSave = true;
        _autoSaveTimer.Stop();
        _autoSavePending = false;
        try
        {
            Editing = new WorkflowEditVm(document, reply.Revision, _host.Catalog);
            if (_host.Catalog.Current.Entries.Count == 0)
                Editing.MergeFallbackSources(_remoteSnapshot?.StoredResourceEntries ?? []);
            Editing.SelectedNode = Editing.Nodes.FirstOrDefault(node => node.NodeId == selectedNodeId);
            SetStatus("已同步执行端保存的最新计划，可继续编辑。", false);
        }
        finally { _suppressAutoSave = previousSuppression; }
    }

    public RelayCommand ReloadLatestPlanCommand => new(async _ =>
    {
        if (Editing is not { } draft || _remoteSaving || _autoSaveInProgress) return;
        if (DraftHasPendingEdits(draft))
        {
            var confirmed = ConfirmReloadLatestPlanForTest?.Invoke() ?? MessageBox.Show(
                "本端有未保存的修改。读取最新计划会放弃这份草稿，不会覆盖另一端已保存的内容。是否继续？",
                "读取最新计划", MessageBoxButton.YesNo, MessageBoxImage.Warning, MessageBoxResult.No) == MessageBoxResult.Yes;
            if (!confirmed) return;
        }
        _autoSaveTimer.Stop();
        _autoSavePending = false;
        if (_remoteTarget is null) ReloadLocalEditingPlan(draft);
        else
        {
            _remoteStale = false;
            await LoadRemoteFlowAsync(draft.Draft.WorkflowId!, draft, draft.EditStateIdentity);
        }
    });
}
