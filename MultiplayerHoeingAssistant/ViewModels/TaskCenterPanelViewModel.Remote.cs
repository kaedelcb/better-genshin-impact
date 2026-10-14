using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public sealed partial class TaskCenterPanelViewModel
{
    private static readonly JsonSerializerOptions RemoteReadOptions = new() { PropertyNameCaseInsensitive = true };
    private int _remoteLoadGeneration;

    public bool HasPendingRemoteOperation
    {
        get
        {
            if (_remoteTarget is null) return false;
            try { return _remoteMain?.PendingCenterOperation(_remoteTarget) is not null; }
            catch { return true; }
        }
    }

    public RelayCommand ReviewPendingRemoteOperationCommand => new(async _ =>
    {
        if (_remoteTarget is null || _remoteMain is null) return;
        try
        {
            var pending = _remoteMain.PendingCenterOperation(_remoteTarget);
            if (pending is null) { OnPropertyChanged(nameof(HasPendingRemoteOperation)); return; }
            var state = await RemoteRequestAsync("operation.get", pending.Id);
            if (state.OperationId == pending.Id && state.Status is "ok" or "rejected")
            {
                _remoteMain.ClearCenterOperation(_remoteTarget, pending.Id);
                OnPropertyChanged(nameof(HasPendingRemoteOperation));
                SetStatus($"原操作已核对：{state.Message}；本次没有重发。", state.Status != "ok");
                await RefreshRemoteAsync();
                return;
            }
            if (state.Status == "accepted")
            {
                SetStatus("执行端仍在处理原操作；当前不能解除阻断，也没有重发。", true);
                return;
            }
            var owner = System.Windows.Application.Current?.MainWindow;
            if (System.Windows.MessageBox.Show(owner,
                    "执行端无法确认原操作的终态。只有你已通过执行端运行记录或实际状态核对，"
                    + "并接受旧结果仍为未知，才能解除本机重发阻断。解除不会把旧操作判成功或失败，也不会立即重发。是否继续？",
                    "解除未确认操作阻断", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning,
                    System.Windows.MessageBoxResult.No) != System.Windows.MessageBoxResult.Yes) return;
            _remoteMain.ClearCenterOperation(_remoteTarget, pending.Id);
            OnPropertyChanged(nameof(HasPendingRemoteOperation));
            SetStatus("已按人工核对解除本机阻断；原操作结果仍未知，本次没有重发。", true);
        }
        catch (Exception ex) { SetStatus("未确认操作核对失败，阻断保持：" + ex.Message, true); }
    });

    // A detached store is a fail-safe for any legacy view command that has not been
    // redirected yet. It must never point at the observer's real TaskCenter files.
    private static TaskCenterHost CreateRemoteShadowHost()
    {
        var root = Path.Combine(Path.GetTempPath(), "bettergi-remote-center-shadow-" + Guid.NewGuid().ToString("N"));
        return new TaskCenterHost(Path.Combine(root, "flows"), Path.Combine(root, "runs"),
            Path.Combine(root, "catalog.json"), () => null, () => false, () => null);
    }

    private Task<RemoteCenterReply> RemoteRequestAsync(string operation, object? payload = null,
        string? expectedRevision = null, string? requestId = null)
        => _remoteMain!.RequestRemoteCenterAsync(_remoteTarget!, operation, payload, expectedRevision, requestId);

    private async Task RefreshRemoteAsync()
    {
        if (_remoteRefreshing || _remoteAbandoned) return;
        _remoteRefreshing = true;
        try
        {
            var reply = await RemoteRequestAsync("task.list");
            if (reply.Status != "ok" || string.IsNullOrWhiteSpace(reply.PayloadJson))
            {
                _remoteStale = true;
                SetStatus("执行端任务中心不可用：" + reply.Message, true);
                return;
            }
            var data = JsonSerializer.Deserialize<RemoteTaskListSnapshot>(reply.PayloadJson, RemoteReadOptions)
                ?? throw new InvalidDataException("执行端任务中心列表为空");
            _remoteSnapshot = data;
            _remoteStale = false;
            _host.Catalog.SetRemoteSnapshot(data.Catalog);
            CatalogStatusText = data.Catalog.IsDegraded
                ? "资源目录降级展示（" + data.Catalog.DegradedReason + "）" : null;
            var entries = data.Flows;
            SyncCollection(Flows, entries, entry => entry.WorkflowId,
                entry => new WorkflowListItemVm(entry), (vm, entry) => vm.Update(entry));
            FlowChoicesChangedAction?.Invoke();
            foreach (var group in Flows.GroupBy(flow => flow.Name, StringComparer.OrdinalIgnoreCase))
            {
                var siblings = group.ToArray();
                foreach (var item in siblings)
                    item.SetChoiceIdentityLabel(siblings.Length > 1
                        ? item.WorkflowId.Length > 8 ? item.WorkflowId[^8..] : item.WorkflowId : "");
            }
            var revisions = entries.ToDictionary(entry => entry.WorkflowId, entry => entry.Revision,
                StringComparer.Ordinal);
            WorkflowSnapshot? SnapshotOf(WorkflowRunRecord run)
                => data.ActiveFlowSnapshots.GetValueOrDefault(run.WorkflowId);
            bool Driving(WorkflowRunRecord run) => data.DrivingWorkflowIds.Contains(run.WorkflowId);
            string? RevisionOf(WorkflowRunRecord run) => revisions.GetValueOrDefault(run.WorkflowId);
            SyncCollection(ActiveRuns, data.Active, run => run.RunId,
                run => ActiveRunVm.BuildRemote(run, SnapshotOf(run), Driving(run), RevisionOf(run)),
                (vm, run) => vm.UpdateRemote(run, SnapshotOf(run), Driving(run), RevisionOf(run)));
            SyncCollection(HistoryRuns, data.History, run => run.RunId,
                run => new HistoryRunVm(run), (vm, run) => vm.Update(run));
            OnPropertyChanged(nameof(DeletedFlows));
            OnPropertyChanged(nameof(HasActiveRuns));
            OnPropertyChanged(nameof(CurrentPlanRuns));
            OnPropertyChanged(nameof(HasCurrentPlanRuns));
            OnPropertyChanged(nameof(CurrentPlanRunsHeight));
            OnPropertyChanged(nameof(CanStartSelectedPlan));
            await ReconcileRemoteEditingRevisionAsync(entries);
            Editing?.RefreshCatalog(_host.Catalog);
            if (data.Catalog.Entries.Count == 0)
                Editing?.MergeFallbackSources(data.StoredResourceEntries);
            if (SelectedWorkflowId is null && Editing is null && Previewing is null)
            {
                var first = Flows.FirstOrDefault(flow => flow.CanStart)
                    ?? Flows.FirstOrDefault(flow => flow.CanEdit)
                    ?? Flows.FirstOrDefault(flow => flow.CanPreview);
                if (first is { CanEdit: true }) await LoadRemoteFlowAsync(first.WorkflowId);
                else if (first is not null) await PreviewRemoteFlowAsync(first.WorkflowId);
            }
            NotifyWorkspace();
            OnPropertyChanged(nameof(SelectedWorkflowId));
            if (StatusMessage?.StartsWith("执行端任务中心不可用", StringComparison.Ordinal) == true)
                SetStatus("已重新连接执行端任务中心", false);
            if (_remoteMain!.PendingCenterOperation(_remoteTarget!) is { } pending)
            {
                var state = await RemoteRequestAsync("operation.get", pending.Id);
                if (state.OperationId == pending.Id && state.Status is "ok" or "rejected")
                {
                    _remoteMain.ClearCenterOperation(_remoteTarget!, pending.Id);
                    OnPropertyChanged(nameof(HasPendingRemoteOperation));
                    SetStatus($"此前{pending.Operation}已确认：{state.Message}", state.Status != "ok");
                }
                else SetStatus($"此前{pending.Operation}仍未确认；不会自动重发。" + state.Message, true);
            }
        }
        catch (Exception ex)
        {
            _remoteStale = true;
            SetStatus("执行端任务中心读取失败：" + ex.Message, true);
        }
        finally { _remoteRefreshing = false; }
    }

    private async Task LoadRemoteFlowAsync(string workflowId, WorkflowEditVm? expectedDraft = null, string? expectedEditState = null)
    {
        var generation = ++_remoteLoadGeneration;
        var reply = await RemoteRequestAsync("task.flow.get", workflowId);
        if (generation != _remoteLoadGeneration || _remoteAbandoned) return;
        if (expectedDraft is not null)
        {
            if (!ReferenceEquals(Editing, expectedDraft)) return;
            if (expectedDraft.EditStateIdentity != expectedEditState)
            {
                MarkPlanSyncConflict("读取期间又有新的修改，草稿已保留；请重新确认是否读取最新计划。");
                return;
            }
        }
        if (reply.Status != "ok" || string.IsNullOrEmpty(reply.PayloadJson)
            || string.IsNullOrEmpty(reply.Revision))
        {
            SetStatus("打开执行端计划失败：" + reply.Message, true);
            return;
        }
        try
        {
            var document = JsonSerializer.Deserialize<WorkflowDocument>(reply.PayloadJson, RemoteReadOptions)
                ?? throw new InvalidDataException("执行端计划为空");
            if (document.Activation?.Status == "candidate-ready")
            {
                await PreviewRemoteFlowAsync(workflowId);
                return;
            }
            if (string.IsNullOrWhiteSpace(document.WorkflowId))
            {
                SetStatus("执行端计划缺少稳定身份，只能预览", true);
                await PreviewRemoteFlowAsync(workflowId);
                return;
            }
            var selectedNodeId = Editing?.Draft.WorkflowId == workflowId ? Editing.SelectedNode?.NodeId : null;
            _suppressAutoSave = true;
            Editing = new WorkflowEditVm(document, reply.Revision, _host.Catalog);
            if (_host.Catalog.Current.Entries.Count == 0)
                Editing.MergeFallbackSources(_remoteSnapshot?.StoredResourceEntries ?? []);
            if (selectedNodeId is not null)
                Editing.SelectedNode = Editing.Nodes.FirstOrDefault(node => node.NodeId == selectedNodeId);
            Previewing = null;
            SetStatus("已打开执行端计划「" + document.Name + "」", false);
        }
        catch (Exception ex) { SetStatus("执行端计划无法编辑：" + ex.Message, true); }
        finally { _suppressAutoSave = false; }
    }

    private async Task PreviewRemoteFlowAsync(string workflowId)
    {
        var reply = await RemoteRequestAsync("task.flow.get", workflowId);
        if (reply.Status != "ok" || string.IsNullOrEmpty(reply.PayloadJson)
            || string.IsNullOrEmpty(reply.Revision))
        {
            SetStatus("预览执行端计划失败：" + reply.Message, true);
            return;
        }
        var document = JsonSerializer.Deserialize<WorkflowDocument>(reply.PayloadJson, RemoteReadOptions)
            ?? throw new InvalidDataException("执行端计划为空");
        Previewing = WorkflowPreviewVm.Build(document, reply.Revision);
        StartPoints.Clear();
        SelectedStartPoint = null;
        if (document.Activation?.Status != "candidate-ready")
            foreach (var node in document.Nodes)
                StartPoints.Add(new StartPointVm(workflowId, reply.Revision, node.NodeId,
                    $"{StartPoints.Count + 1}. {node.Ref?.Config ?? node.Kind} [{node.NodeId}]"));
        SelectedStartPoint = StartPoints.FirstOrDefault();
    }

    private async Task<bool> SaveRemoteFlowAsync(bool auto = false)
    {
        if (HasPlanSyncConflict) { SetStatus(PlanSyncMessage, true); return false; }
        if (_remoteAbandoned || _remoteSaving || Editing is not { } draft || _remoteStale)
            return false;
        if (!draft.HasUnsavedChanges && !_autoSavePending) return true;
        _autoSaveTimer.Stop();
        _autoSavePending = false;
        _remoteSaving = true;
        try
        {
            var selectedNodeId = draft.SelectedNode?.NodeId;
            var document = BuildSubmissionCopyForSave(draft);
            var submittedJson = JsonSerializer.Serialize(document);
            var reply = await RemoteRequestAsync("task.flow.save", document, draft.BaseRevision);
            if (reply.Status != "ok" || string.IsNullOrEmpty(reply.Revision))
            {
                _remoteStale = true;
                if (reply.Status == "rejected" && reply.Message.Contains("修订冲突", StringComparison.Ordinal))
                    MarkPlanSyncConflict("执行端已保存了另一份修改。本端草稿已保留，请读取最新计划后重新修改。" + reply.Message);
                else SetStatus("执行端计划保存结果未确认，草稿已保留：" + reply.Message, true);
                return false;
            }
            var changedDuringSend = ReferenceEquals(Editing, draft)
                && JsonSerializer.Serialize(BuildSubmissionCopyForSave(draft)) != submittedJson;
            var nextDocument = changedDuringSend ? BuildSubmissionCopyForSave(draft) : document;
            _suppressAutoSave = true;
            Editing = new WorkflowEditVm(nextDocument, reply.Revision, _host.Catalog);
            if (_host.Catalog.Current.Entries.Count == 0)
                Editing.MergeFallbackSources(_remoteSnapshot?.StoredResourceEntries ?? []);
            if (selectedNodeId is not null)
                Editing.SelectedNode = Editing.Nodes.FirstOrDefault(node => node.NodeId == selectedNodeId);
            _suppressAutoSave = false;
            if (changedDuringSend)
            {
                _autoSavePending = true;
                _autoSaveTimer.Start();
                SetStatus("执行端已保存上一版，正在继续保存编辑期间的新改动", false);
            }
            else SetStatus(auto ? "计划已自动保存到执行端" : "计划已保存到执行端", false);
            await RefreshRemoteAsync();
            return true;
        }
        catch (Exception ex)
        {
            _remoteStale = true;
            SetStatus("执行端计划保存结果未知，草稿已保留：" + ex.Message, true);
            return false;
        }
        finally { _suppressAutoSave = false; _remoteSaving = false; }
    }

    internal async Task<bool> FlushRemoteAsync()
    {
        if (_remoteTarget is null) return true;
        _autoSaveTimer.Stop();
        for (var i = 0; _remoteSaving && i < 200; i++) await Task.Delay(50);
        if (_remoteSaving || _remoteStale || HasPlanSyncConflict) return false;
        if (Editing is { HasUnsavedChanges: true } || _autoSavePending)
            return await SaveRemoteFlowAsync();
        return true;
    }

    internal async Task AbandonRemoteAsync()
    {
        _remoteAbandoned = true;
        _autoSaveTimer.Stop();
        _remoteTimer?.Stop();
        for (var i = 0; _remoteSaving && i < 400; i++) await Task.Delay(50);
    }

    private async Task<RemoteCenterReply> RemoteOperationAsync(string operation, object? payload = null,
        string? expectedRevision = null, bool allowWhilePending = false)
    {
        try
        {
            if (!allowWhilePending && _remoteMain!.PendingCenterOperation(_remoteTarget!) is { } pending)
            {
                var previous = await RemoteRequestAsync("operation.get", pending.Id);
                if (previous.OperationId == pending.Id
                    && previous.Status is "ok" or "rejected")
                {
                    _remoteMain.ClearCenterOperation(_remoteTarget!, pending.Id);
                    OnPropertyChanged(nameof(HasPendingRemoteOperation));
                    await RefreshRemoteAsync();
                    SetStatus($"上次{pending.Operation}已核对：{previous.Message}；本次没有重发，请确认后再点。",
                        previous.Status != "ok");
                    return RemoteCenterReply.Rejected("上次操作已核对；本次没有重发");
                }
                SetStatus($"上次{pending.Operation}结果仍未确认；本次没有重发。" + previous.Message, true);
                return RemoteCenterReply.Unknown(pending.Id, "上次操作结果未确认，本次没有重发");
            }
            var requestId = Guid.NewGuid().ToString("N");
            if (!allowWhilePending)
            {
                _remoteMain!.RememberCenterOperation(_remoteTarget!, requestId, operation);
                OnPropertyChanged(nameof(HasPendingRemoteOperation));
            }
            var reply = await RemoteRequestAsync(operation, payload, expectedRevision, requestId);
            if (!allowWhilePending && reply.Status is "ok" or "rejected"
                && string.IsNullOrEmpty(reply.OperationId))
            {
                _remoteMain!.ClearCenterOperation(_remoteTarget!, requestId);
                OnPropertyChanged(nameof(HasPendingRemoteOperation));
            }
            if (reply.Status == "accepted" && !string.IsNullOrWhiteSpace(reply.OperationId))
            {
                if (reply.OperationId != requestId)
                    return RemoteCenterReply.Unknown(requestId, "执行端受理身份不匹配，本次结果未确认");
                for (var i = 0; i < 20; i++)
                {
                    await Task.Delay(500);
                    reply = await RemoteRequestAsync("operation.get", requestId);
                    if (reply.Status != "accepted") break;
                }
                if (reply.Status == "accepted")
                    reply = RemoteCenterReply.Unknown(requestId,
                        "执行端已受理但仍未返回终态；请刷新任务中心核对后再操作");
            }
            if (!allowWhilePending && reply.OperationId == requestId
                && reply.Status is "ok" or "rejected")
            {
                _remoteMain!.ClearCenterOperation(_remoteTarget!, requestId);
                OnPropertyChanged(nameof(HasPendingRemoteOperation));
            }
            SetStatus(reply.Message, reply.Status is not "ok" and not "accepted");
            await RefreshRemoteAsync();
            return reply;
        }
        catch (Exception ex)
        {
            SetStatus("远程操作或本地未确认台账失败；本次没有自动重发：" + ex.Message, true);
            return RemoteCenterReply.Unknown("", "远程操作结果未确认：" + ex.Message);
        }
    }

    private async Task<bool> RemoteStartAsync(string workflowId, string? nodeId = null,
        string? stopAfterNodeId = null)
    {
        if (_startResumeInFlight || _remoteAbandoned) return false;
        _startResumeInFlight = true;
        OnPropertyChanged(nameof(CanStartSelectedPlan));
        try
        {
            if (_remoteStale || !await FlushRemoteAsync())
            {
                SetStatus("执行端计划尚未确认保存，已取消启动", true);
                return false;
            }
            if (Editing?.Draft.WorkflowId != workflowId)
            {
                SetStatus("请先打开执行端当前计划，再启动", true);
                return false;
            }
            var reply = await RemoteOperationAsync("task.run.start", new
            {
                workflowId, entryNodeId = nodeId, expectedRevision = Editing.BaseRevision,
                stopAfterNodeId
            });
            return reply.Status == "ok";
        }
        finally
        {
            _startResumeInFlight = false;
            OnPropertyChanged(nameof(CanStartSelectedPlan));
        }
    }

    private async Task ImportRemoteFlowAsync(string sourceFile)
    {
        try
        {
            if (_remoteStale) throw new InvalidOperationException("执行端状态未确认，请先刷新");
            var json = await File.ReadAllTextAsync(sourceFile);
            await RemoteOperationAsync("task.flow.import", json);
        }
        catch (Exception ex) { SetStatus("导入执行端计划失败：" + ex.Message, true); }
    }

    private async Task ImportRemoteScheduleAsync(string sourceFile)
    {
        try
        {
            if (_remoteStale || !await FlushRemoteAsync())
                throw new InvalidOperationException("执行端状态未确认，请先处理当前草稿");
            var json = await File.ReadAllTextAsync(sourceFile);
            await RemoteOperationAsync("task.schedule.import", json);
        }
        catch (Exception ex) { SetStatus("导入执行端调度列表失败：" + ex.Message, true); }
    }

    private async Task ExportRemoteScheduleAsync(string destination)
    {
        try
        {
            var target = Path.GetFullPath(destination);
            var ownRoot = Path.TrimEndingDirectorySeparator(Path.GetFullPath(AssistantDataDirectory.Root));
            if (target.StartsWith(ownRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("远程调度列表导出不能写入监控端本机数据目录");
            var reply = await RemoteRequestAsync("task.schedule.export");
            if (reply.Status != "ok" || reply.PayloadJson is null)
                throw new InvalidOperationException(reply.Message);
            var json = JsonSerializer.Deserialize<string>(reply.PayloadJson)
                ?? throw new InvalidDataException("执行端调度列表导出结果为空");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, json);
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            SetStatus("执行端整份调度列表已导出到监控端所选位置；账号值已移除", false);
        }
        catch (Exception ex) { SetStatus("导出执行端调度列表失败：" + ex.Message, true); }
    }

    private async Task ExportRemoteFlowAsync(string workflowId, string destination)
    {
        try
        {
            var target = Path.GetFullPath(destination);
            var ownStore = Path.TrimEndingDirectorySeparator(Path.GetFullPath(WorkflowStore.DefaultFlowsDir()));
            if (target.StartsWith(ownStore + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                throw new InvalidOperationException("导出不能写入监控端本机任务中心目录");
            var reply = await RemoteRequestAsync("task.flow.export", workflowId);
            if (reply.Status != "ok" || reply.PayloadJson is null)
                throw new InvalidOperationException(reply.Message);
            var json = JsonSerializer.Deserialize<string>(reply.PayloadJson)
                ?? throw new InvalidDataException("执行端导出结果为空");
            var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                await File.WriteAllTextAsync(temporary, json);
                File.Move(temporary, target, overwrite: true);
            }
            finally { if (File.Exists(temporary)) File.Delete(temporary); }
            SetStatus("执行端计划已导出到监控端所选位置；账号值已移除", false);
        }
        catch (Exception ex) { SetStatus("导出执行端计划失败：" + ex.Message, true); }
    }

    private async Task DeleteRemoteFlowAsync(WorkflowListItemVm flow)
    {
        if (_remoteStale) { SetStatus("执行端状态未确认，请先刷新再删除", true); return; }
        if (ConfirmDeleteOverride?.Invoke(flow) == false) return;
        if (ConfirmDeleteOverride is null
            && System.Windows.MessageBox.Show($"从执行端删除计划「{flow.Name}」？原计划会保留在可恢复记录中。",
                "删除执行端计划", System.Windows.MessageBoxButton.YesNo,
                System.Windows.MessageBoxImage.Warning) != System.Windows.MessageBoxResult.Yes) return;
        var revision = _remoteSnapshot?.Flows.FirstOrDefault(item => item.WorkflowId == flow.WorkflowId)?.Revision;
        if (string.IsNullOrWhiteSpace(revision))
        { SetStatus("执行端计划修订未知，请刷新后重试", true); return; }
        var reply = await RemoteRequestAsync("task.flow.delete",
            new { workflowId = flow.WorkflowId, acknowledgeUnknownRuns = false }, revision);
        if (reply.Status == "rejected" && _remoteSnapshot?.Active.Any(run =>
                run.WorkflowId == flow.WorkflowId && run.State == WorkflowRunState.Unknown) == true)
        {
            var confirmed = ConfirmDeleteUnknownOverride?.Invoke(flow)
                ?? System.Windows.MessageBox.Show(
                    "该计划有结果未知的旧运行。继续只删除计划定义并保留 Unknown 运行历史；不会把未知判成成功。是否解除占用？",
                    "保留未知历史", System.Windows.MessageBoxButton.YesNo,
                    System.Windows.MessageBoxImage.Warning, System.Windows.MessageBoxResult.No)
                    == System.Windows.MessageBoxResult.Yes;
            if (confirmed)
                reply = await RemoteRequestAsync("task.flow.delete",
                    new { workflowId = flow.WorkflowId, acknowledgeUnknownRuns = true }, revision);
        }
        SetStatus(reply.Message, reply.Status != "ok");
        if (reply.Status == "ok")
        {
            Editing = null;
            Previewing = null;
            SelectedWorkflowId = null;
            await RefreshRemoteAsync();
        }
    }

    private async Task<WorkflowResourceEditor.ResourceTaskLayout> ReadRemoteResourceLayoutAsync(NodeEditVm node)
    {
        if (_remoteStale || _remoteAbandoned || !await FlushRemoteAsync())
            throw new InvalidOperationException("执行端计划未确认保存，请先处理计划状态再读取配置");
        var reply = await RemoteRequestAsync("task.resource.layout.get", new
        { workflowId = Editing?.Draft.WorkflowId, nodeId = node.NodeId });
        if (reply.Status != "ok" || string.IsNullOrEmpty(reply.PayloadJson))
            throw new InvalidOperationException(reply.Message);
        return JsonSerializer.Deserialize<WorkflowResourceEditor.ResourceTaskLayout>(
            reply.PayloadJson, RemoteReadOptions)
            ?? throw new InvalidDataException("执行端内部任务布局为空");
    }

    private async Task<string> ApplyRemoteResourceLayoutAsync(NodeEditVm node,
        WorkflowResourceEditor.ResourceTaskLayout current,
        IReadOnlyList<WorkflowResourceEditor.ResourceTask> tasks, string? startTaskId)
    {
        if (_remoteStale || _remoteAbandoned || !await FlushRemoteAsync())
            throw new InvalidOperationException("执行端状态未确认，配置未发送");
        var reply = await RemoteOperationAsync("task.resource.layout.save", new
        {
            workflowId = Editing?.Draft.WorkflowId, nodeId = node.NodeId,
            resourceKind = node.Model.Kind, resourceConfig = node.Model.Ref?.Config,
            resourceTaskId = node.Model.Ref?.TaskId, current, tasks, startTaskId
        });
        if (reply.Status is "unknown" or "accepted")
            throw new RemoteResourceSaveUnconfirmedException(reply.Message);
        if (reply.Status != "ok" || string.IsNullOrWhiteSpace(reply.Revision))
            throw new InvalidOperationException(reply.Message);
        return reply.Revision;
    }
}
