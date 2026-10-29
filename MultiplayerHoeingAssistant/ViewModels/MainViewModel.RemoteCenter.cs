using System.Collections.Concurrent;
using System.IO;
using System.Text.Json;
using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private LocalConfigEditChannel? _localCenterChannel;
    private sealed record CenterRequestClaim(string Fingerprint,
        TaskCompletionSource<RemoteCenterReply> Completion);
    private readonly ConcurrentDictionary<string, CenterRequestClaim> _centerRequestReplies = new();
    private readonly ConcurrentDictionary<string, RemoteCenterReply> _centerOperations = new();
    private readonly RemoteCenterOperationStore _centerOperationStore = new();
    private sealed record PendingCenterReply(string TargetUid, string TargetInstanceId,
        TaskCompletionSource<RemoteCenterReply> Completion);
    private readonly ConcurrentDictionary<string, PendingCenterReply> _pendingCenterReplies = new();

    private void StartLocalCenterChannel()
    {
        try
        {
            _localCenterChannel = new LocalConfigEditChannel(
                () => _config?.ControlRoomPassword,
                cmd => Application.Current.Dispatcher.InvokeAsync(
                    () => HandleRemoteCenterRequestAsync(cmd)).Task.Unwrap(),
                AddLog, channel: "center");
        }
        catch (Exception ex) { AddLog("本地中心通道不可用：" + ex.Message); }
    }

    private async Task<RemoteCommand?> HandleRemoteCenterRequestAsync(RemoteCommand command, bool allowLocalObserver = false)
    {
        if (_config is null || (!IsExecutorMode && !allowLocalObserver) || command.Cmd != RemoteCenterProtocol.RequestCommand
            || command.RoomCode != RoomCode || command.Target is not { Count: 1 }
            || command.Target[0] != _config.PlayerUid || string.IsNullOrWhiteSpace(command.CommandId)
            || string.IsNullOrWhiteSpace(command.SenderUid)
            || command.SenderUid != _config.PlayerUid
            || command.ExpiresAtUtc is null || command.ExpiresAtUtc <= DateTimeOffset.UtcNow)
            return null;

        RemoteCenterReply reply;
        try
        {
            var payload = RemoteCenterProtocol.PayloadOf(command);
            if (string.IsNullOrEmpty(payload) || payload.Length > 2_000_000)
                throw new InvalidDataException("中心请求为空或超过大小限制");
            var request = JsonSerializer.Deserialize<RemoteCenterRequest>(payload)
                ?? throw new InvalidDataException("中心请求无效");
            if (request.Version != RemoteCenterRequest.CurrentVersion)
                throw new InvalidOperationException("执行端与监控端的远程中心协议版本不兼容");
            if (request.Operation != "probe"
                && (string.IsNullOrWhiteSpace(request.TargetInstanceId)
                    || request.TargetInstanceId != _config.ClientInstanceId))
                throw new InvalidOperationException("执行端实例已变化，请重新连接");
            if (command.TargetInstanceId is { Length: > 0 } targetInstance
                && targetInstance != _config.ClientInstanceId)
                throw new InvalidOperationException("请求的执行端实例不匹配");
            var fingerprint = RemoteCenterProtocol.RequestFingerprint(command, request);
            var claim = new CenterRequestClaim(fingerprint,
                new TaskCompletionSource<RemoteCenterReply>(TaskCreationOptions.RunContinuationsAsynchronously));
            var owner = _centerRequestReplies.GetOrAdd(command.CommandId, claim);
            if (owner.Fingerprint != fingerprint)
                reply = RemoteCenterReply.Unknown(command.CommandId,
                    "相同请求身份的内容不一致；本次未执行，原请求责任保持");
            else if (!ReferenceEquals(owner, claim))
                reply = await owner.Completion.Task;
            else
            {
                try
                {
                    var previous = UsesDurableCenterReceipt(request)
                        ? _centerOperationStore.GetForRequest(command.CommandId, fingerprint) : null;
                    reply = previous is not null
                        ? (_centerOperations.TryGetValue(command.CommandId, out var inFlight) ? inFlight : previous)
                        : UsesImmediateCenterReceipt(request.Operation)
                            ? await ExecuteImmediateCenterMutationAsync(command.CommandId, fingerprint,
                                () => ExecuteRemoteCenterOperationAsync(command.CommandId, request, fingerprint))
                            : await ExecuteRemoteCenterOperationAsync(command.CommandId, request, fingerprint);
                }
                catch (RemoteCenterOperationConflictException ex)
                { reply = RemoteCenterReply.Unknown(command.CommandId, ex.Message); }
                catch (Exception ex) { reply = RemoteCenterReply.Rejected(ex.Message); }
                reply.TargetInstanceId = _config.ClientInstanceId;
                owner.Completion.TrySetResult(reply);
                if (_centerRequestReplies.Count > 512)
                    foreach (var cached in _centerRequestReplies.Where(item => item.Value.Completion.Task.IsCompleted)
                        .Take(128).ToArray()) _centerRequestReplies.TryRemove(cached.Key, out _);
            }
            reply.TargetInstanceId = _config.ClientInstanceId;
        }
        catch (Exception ex) { reply = RemoteCenterReply.Rejected(ex.Message); reply.TargetInstanceId = _config.ClientInstanceId; }
        return RemoteCenterProtocol.Reply(command, _config.PlayerUid, _config.PlayerName, reply);
    }

    private sealed record FlowStartArgs(string WorkflowId, string? EntryNodeId = null,
        string? ExpectedRevision = null, string? StopAfterNodeId = null);
    private sealed record RunActionArgs(string RunId, string Action);
    private sealed record FlowDeleteArgs(string WorkflowId, bool AcknowledgeUnknownRuns = false);
    private sealed record ResourceNodeArgs(string WorkflowId, string NodeId);
    private sealed record ResourceLayoutSaveArgs(string WorkflowId, string NodeId,
        WorkflowResourceEditor.ResourceTaskLayout Current,
        List<WorkflowResourceEditor.ResourceTask> Tasks, string? StartTaskId,
        string? ResourceKind = null, string? ResourceConfig = null, string? ResourceTaskId = null);

    private static bool UsesImmediateCenterReceipt(string operation) => operation is
        "startup.save" or "startup.schemes.save" or "startup.autoLaunch.save"
        or "task.resource.layout.save" or "task.resource.editor.open" or "task.bgiPath.save"
        or "task.flow.save" or "task.flow.delete" or "task.flow.restore";

    private static bool UsesDurableCenterReceipt(RemoteCenterRequest request)
        => UsesImmediateCenterReceipt(request.Operation) || request.Operation is
            "startup.run" or "task.catalog.connect" or "task.flow.import" or "task.schedule.import"
            or "task.migration.prepare" or "task.migration.activate" or "task.migration.rollback"
            or "task.migration.rehearsal" or "task.run.start" or "task.run.resume"
            or "task.flow.resetExecutionState"
        || request.Operation == "task.run.action"
            && Decode<RunActionArgs>(request.PayloadJson).Action != nameof(WorkflowRunAction.Stop);

    private async Task<RemoteCenterReply> ExecuteRemoteCenterOperationAsync(string requestId,
        RemoteCenterRequest request, string fingerprint)
    {
        var isStop = request.Operation is "startup.cancel" or "startup.trigger.cancel"
            || request.Operation == "task.run.action"
                && Decode<RunActionArgs>(request.PayloadJson).Action == nameof(WorkflowRunAction.Stop);
        if (!TryEnterConfigTransferOperation(isStop ? "stop" : request.Operation, out var transferOperation))
            return RemoteCenterReply.Rejected(ConfigTransferBlockedMessage);
        using var configTransferOperation = transferOperation;
        var mistletoe = (Application.Current.MainWindow as MainWindow)?.Mistletoe
            ?? throw new InvalidOperationException("执行端启动中心尚未就绪");
        var host = TaskCenterHost;
        switch (request.Operation)
        {
            case "probe":
                return RemoteCenterReply.Ok(new
                {
                    instanceId = _config!.ClientInstanceId,
                    machineId = HostMachineIdentity.Current,
                    executorName = _config.PlayerName,
                    capabilities = new[] { "startup", "taskCenter" }
                });
            case "startup.get":
            {
                var (config, revision) = mistletoe.ReadRemoteStartupConfig();
                var (schemes, schemesRevision) = mistletoe.ReadRemoteSchemes();
                return RemoteCenterReply.Ok(new
                {
                    config, revision, schemes, schemesRevision,
                    mistletoe.IsRunning, mistletoe.StatusText,
                    autoLaunchOnBoot = mistletoe.AutoLaunchOnBoot,
                    autoLaunchOnBootModeIndex = mistletoe.AutoLaunchOnBootModeIndex,
                    autoLaunchRevision = AutoLaunchRevision(mistletoe),
                    timers = mistletoe.ArmedTimers.Select(t => t.StatusLine).ToArray(),
                    watchdogs = mistletoe.ArmedWatchdogs.Select(w => w.StatusLine).ToArray(),
                    logTriggers = mistletoe.ArmedLogTriggers.Select(l => l.StatusLine).ToArray(),
                    triggers = CaptureRemoteTriggers(mistletoe),
                    prompts = ConfirmStepWindow.RemotePrompts(),
                    nodeStates = mistletoe.CaptureNodeRunStates()
                }, revision);
            }
            case "startup.status":
                return RemoteCenterReply.Ok(new
                {
                    isRunning = mistletoe.IsRunning, statusText = mistletoe.StatusText,
                    triggers = CaptureRemoteTriggers(mistletoe),
                    prompts = ConfirmStepWindow.RemotePrompts(),
                    nodeStates = mistletoe.CaptureNodeRunStates()
                });
            case "startup.prompt.answer":
            {
                using var json = JsonDocument.Parse(request.PayloadJson ?? "{}");
                var id = json.RootElement.GetProperty("runtimeId").GetString() ?? "";
                var yes = json.RootElement.GetProperty("yes").GetBoolean();
                return ConfirmStepWindow.TryAnswerRemote(id, yes)
                    ? RemoteCenterReply.Ok(message: "执行端已接收人工确认")
                    : RemoteCenterReply.Rejected("人工确认已结束或身份已变化");
            }
            case "startup.trigger.cancel":
            {
                var id = Decode<string>(request.PayloadJson);
                if (mistletoe.ArmedTimers.FirstOrDefault(t => t.RuntimeId == id) is { } timer)
                    mistletoe.CancelTimer(timer);
                else if (mistletoe.ArmedWatchdogs.FirstOrDefault(w => w.RuntimeId == id) is { } watchdog)
                    mistletoe.CancelWatchdog(watchdog);
                else if (mistletoe.ArmedLogTriggers.FirstOrDefault(l => l.RuntimeId == id) is { } log)
                    mistletoe.CancelLogTrigger(log);
                else return RemoteCenterReply.Rejected("执行端已找不到这条触发器；请刷新状态");
                return RemoteCenterReply.Ok(message: "执行端已取消触发器");
            }
            case "startup.save":
            {
                var config = Decode<StartupFlowConfig>(request.PayloadJson);
                string revision;
                try { revision = mistletoe.SaveRemoteStartupConfig(config, request.ExpectedRevision); }
                catch (InvalidOperationException ex) when (ex is StartupRevisionConflictException or StartupEditBusyException)
                { return RemoteCenterReply.Rejected(ex.Message); }
                return RemoteCenterReply.Ok(revision: revision, message: "执行端启动中心已保存");
            }
            case "startup.schemes.save":
            {
                var schemes = Decode<List<StartupFlowScheme>>(request.PayloadJson);
                string revision;
                try { revision = mistletoe.SaveRemoteSchemes(schemes, request.ExpectedRevision); }
                catch (InvalidOperationException ex) when (ex is StartupRevisionConflictException or StartupEditBusyException)
                { return RemoteCenterReply.Rejected(ex.Message); }
                return RemoteCenterReply.Ok(revision: revision, message: "执行端方案库已保存");
            }
            case "startup.autoLaunch.save":
            {
                if (string.IsNullOrWhiteSpace(request.ExpectedRevision)
                    || request.ExpectedRevision != AutoLaunchRevision(mistletoe))
                    return RemoteCenterReply.Rejected("执行端开机自启动设置已变化，请重新读取后再修改");
                using var json = JsonDocument.Parse(request.PayloadJson ?? "{}");
                var enabled = json.RootElement.GetProperty("enabled").GetBoolean();
                var mode = json.RootElement.GetProperty("modeIndex").GetInt32();
                // 远程请求不能代替目标账户确认 UAC；无权限时明确返回失败。
                var registration = ConfigureAutoStartup(enabled, mode, allowElevation: false);
                if (!registration.Success)
                    return RemoteCenterReply.Rejected(registration.Message);
                return RemoteCenterReply.Ok(revision: AutoLaunchRevision(mistletoe),
                    message: "执行端开机自启动设置已应用");
            }
            case "startup.programs.list":
            {
                var requested = request.PayloadJson is null ? "" : Decode<string>(request.PayloadJson);
                if (string.IsNullOrWhiteSpace(requested))
                    return RemoteCenterReply.Ok(new
                    {
                        path = "", directories = DriveInfo.GetDrives().Where(d => d.IsReady)
                            .Select(d => d.RootDirectory.FullName).ToArray(), programs = Array.Empty<string>()
                    });
                if (!Path.IsPathFullyQualified(requested))
                    return RemoteCenterReply.Rejected("请选择执行端上的绝对路径");
                var directory = Path.GetFullPath(requested);
                if (!Directory.Exists(directory)) return RemoteCenterReply.Rejected("执行端目录不存在");
                return RemoteCenterReply.Ok(new
                {
                    path = directory,
                    directories = Directory.EnumerateDirectories(directory).Take(500).ToArray(),
                    programs = Directory.EnumerateFiles(directory, "*.exe").Take(500).ToArray()
                });
            }
            case "startup.run":
                if (mistletoe.IsRunning) return RemoteCenterReply.Rejected("启动流程已在运行");
                return BeginTrackedOperation(requestId, fingerprint, mistletoe.RunForRemoteAsync);
            case "startup.cancel":
                mistletoe.CancelRunCommand.Execute(null);
                ConfirmStepWindow.CloseForRemoteStop();
                return new RemoteCenterReply { Status = "accepted", Message = "执行端已受理取消请求" };
            case "startup.runStates.clear":
                mistletoe.ClearRunStateDisplayCommand.Execute(null);
                return RemoteCenterReply.Ok(message: "执行端执行路径显示已清除");
            case "task.list":
            {
                var overview = host.ListRunOverview();
                var snapshots = new Dictionary<string, WorkflowSnapshot>(StringComparer.Ordinal);
                foreach (var workflowId in overview.Active.Select(run => run.WorkflowId).Distinct(StringComparer.Ordinal))
                {
                    try { snapshots[workflowId] = host.LoadFlowSnapshot(workflowId); }
                    catch { /* Keep the run visible when its definition is missing or quarantined. */ }
                }
                return RemoteCenterReply.Ok(new
                {
                    serverTime = DateTimeOffset.Now,
                    flows = host.ListFlows(), active = overview.Active, history = overview.History,
                    catalog = host.Catalog.Current,
                    storedResourceEntries = host.Catalog.Current.Entries.Count == 0
                        ? host.ListStoredResourceEntries() : [],
                    deleted = host.ListDeletedFlows(), activeFlowSnapshots = snapshots,
                    drivingWorkflowIds = overview.Active.Where(run => host.IsDriving(run.WorkflowId))
                        .Select(run => run.WorkflowId).Distinct(StringComparer.Ordinal).ToArray()
                });
            }
            case "task.catalog.connect":
                return BeginTrackedOperation(requestId, fingerprint, host.ConnectResourceCatalogAsync);
            case "task.resource.layout.get":
            {
                var args = Decode<ResourceNodeArgs>(request.PayloadJson);
                var node = host.LoadFlowSnapshot(args.WorkflowId).Document.Nodes
                    .SingleOrDefault(n => n.NodeId == args.NodeId)
                    ?? throw new InvalidOperationException("执行端找不到该配置单");
                var layout = await host.ReadResourceTaskLayoutAsync(node);
                return RemoteCenterReply.Ok(layout, layout.Revision);
            }
            case "task.resource.layout.save":
            {
                var args = Decode<ResourceLayoutSaveArgs>(request.PayloadJson);
                var snapshot = host.LoadFlowSnapshot(args.WorkflowId);
                var node = snapshot.Document.Nodes.SingleOrDefault(n => n.NodeId == args.NodeId)
                    ?? throw new InvalidOperationException("执行端找不到该配置单");
                if (node.Kind != args.ResourceKind || node.Ref?.Config != args.ResourceConfig
                    || node.Ref?.TaskId != args.ResourceTaskId)
                    return RemoteCenterReply.Rejected("该计划行引用的配置已经改变，请重新读取后再修改");
                var revision = await host.ApplyResourceTaskLayoutAsync(node, args.Current,
                    args.Tasks, args.StartTaskId);
                return RemoteCenterReply.Ok(revision: revision,
                    message: "BGI 配置已保存；请同步当前计划引用修订");
            }
            case "task.resource.revision.get":
            {
                var args = Decode<ResourceNodeArgs>(request.PayloadJson);
                var node = host.LoadFlowSnapshot(args.WorkflowId).Document.Nodes
                    .SingleOrDefault(n => n.NodeId == args.NodeId)
                    ?? throw new InvalidOperationException("执行端找不到该配置单");
                var revision = await host.ReadResourceRevisionAsync(node);
                return RemoteCenterReply.Ok(revision: revision);
            }
            case "task.resource.editor.open":
            {
                var args = Decode<ResourceNodeArgs>(request.PayloadJson);
                var node = host.LoadFlowSnapshot(args.WorkflowId).Document.Nodes
                    .SingleOrDefault(item => item.NodeId == args.NodeId)
                    ?? throw new InvalidOperationException("执行端找不到该资源节点");
                await host.OpenResourceEditorAsync(node);
                return RemoteCenterReply.Ok(message: "已在执行端桌面打开 BGI 资源编辑器");
            }
            case "task.bgiPath.get":
            {
                var settings = _configManager ?? throw new InvalidOperationException("执行端助手设置尚未就绪");
                var (path, revision) = settings.ReadBgiPathChecked();
                return RemoteCenterReply.Ok(new { path }, revision);
            }
            case "task.bgiPath.save":
            {
                var settings = _configManager ?? throw new InvalidOperationException("执行端助手设置尚未就绪");
                var requestedPath = Decode<string>(request.PayloadJson);
                if (!Path.IsPathFullyQualified(requestedPath)
                    || !string.Equals(Path.GetFileName(requestedPath), "BetterGI.exe",
                        StringComparison.OrdinalIgnoreCase)
                    || !File.Exists(requestedPath))
                    return RemoteCenterReply.Rejected("请选择执行端上存在的 BetterGI.exe");
                var path = Path.GetFullPath(requestedPath);
                var result = settings.SaveBgiPathChecked(path, request.ExpectedRevision);
                if (!result.Saved) return RemoteCenterReply.Rejected(result.Message);
                _config!.BgiPath = path;
                return RemoteCenterReply.Ok(new { path }, result.Revision, result.Message);
            }
            case "task.flow.get":
            {
                var workflowId = Decode<string>(request.PayloadJson);
                var snapshot = host.LoadFlowSnapshot(workflowId);
                return RemoteCenterReply.Ok(snapshot.Document, snapshot.Revision);
            }
            case "task.flow.save":
            {
                var document = Decode<WorkflowDocument>(request.PayloadJson);
                string revision;
                try { revision = host.SaveFlow(document, request.ExpectedRevision); }
                catch (WorkflowRevisionConflictException ex) { return RemoteCenterReply.Rejected(ex.Message); }
                mistletoe.TaskCenter.RefreshCommand.Execute(null);
                return RemoteCenterReply.Ok(new { document.WorkflowId }, revision, "执行端任务计划已保存");
            }
            case "task.flow.import":
            {
                var json = Decode<string>(request.PayloadJson);
                if (json.Length > 2_000_000) return RemoteCenterReply.Rejected("导入计划超过大小限制");
                return BeginTrackedOperation(requestId, fingerprint, async () =>
                {
                    var path = Path.Combine(Path.GetTempPath(),
                        "bettergi-center-import-" + Guid.NewGuid().ToString("N") + ".json");
                    try
                    {
                        await File.WriteAllTextAsync(path, json);
                        var id = await Task.Run(() => host.Workflows.Import(path));
                        try
                        {
                            await Application.Current.Dispatcher.InvokeAsync(
                                () => mistletoe.TaskCenter.RefreshCommand.Execute(null));
                        }
                        catch (Exception ex) { AddLog("[远程中心] 计划已导入，但界面刷新失败：" + ex.Message); }
                        return HostActionResult.Effective("已在执行端导入计划 " + id);
                    }
                    finally
                    {
                        try { if (File.Exists(path)) File.Delete(path); }
                        catch (Exception ex) { AddLog("[远程中心] 导入临时文件清理失败：" + ex.Message); }
                    }
                });
            }
            case "task.schedule.import":
            {
                var json = Decode<string>(request.PayloadJson);
                if (json.Length > 2_000_000) return RemoteCenterReply.Rejected("调度列表超过大小限制");
                return BeginTrackedOperation(requestId, fingerprint, async () =>
                {
                    var path = Path.Combine(Path.GetTempPath(),
                        "bettergi-center-schedule-import-" + Guid.NewGuid().ToString("N") + ".json");
                    try
                    {
                        await File.WriteAllTextAsync(path, json);
                        var ids = await Task.Run(() => WorkflowScheduleTransfer.Import(host.Workflows, path));
                        try
                        {
                            await Application.Current.Dispatcher.InvokeAsync(
                                () => mistletoe.TaskCenter.RefreshCommand.Execute(null));
                        }
                        catch (Exception ex) { AddLog("[远程中心] 调度列表已导入，但界面刷新失败：" + ex.Message); }
                        return HostActionResult.Effective($"已在执行端导入 {ids.Count} 个只读候选");
                    }
                    finally
                    {
                        try { if (File.Exists(path)) File.Delete(path); }
                        catch (Exception ex) { AddLog("[远程中心] 导入临时文件清理失败：" + ex.Message); }
                    }
                });
            }
            case "task.schedule.export":
            {
                var path = Path.Combine(Path.GetTempPath(),
                    "bettergi-center-schedule-export-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    WorkflowScheduleTransfer.Export(host.Workflows, path);
                    return RemoteCenterReply.Ok(File.ReadAllText(path),
                        message: "已读取执行端整份调度列表导出副本");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
            case "task.flow.export":
            {
                var workflowId = Decode<string>(request.PayloadJson);
                var path = Path.Combine(Path.GetTempPath(), "bettergi-center-export-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    mistletoe.TaskCenter.ExportFlowToFile(workflowId, path);
                    return RemoteCenterReply.Ok(File.ReadAllText(path), message: "已读取执行端计划导出副本");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
            case "task.flow.delete":
            {
                var args = Decode<FlowDeleteArgs>(request.PayloadJson);
                var result = host.DeleteFlow(args.WorkflowId, request.ExpectedRevision!, args.AcknowledgeUnknownRuns);
                mistletoe.TaskCenter.RefreshCommand.Execute(null);
                return result.Status == HostActionStatus.Unavailable
                    ? RemoteCenterReply.Rejected(result.Message)
                    : RemoteCenterReply.Ok(message: result.Message);
            }
            case "task.flow.restore":
            {
                var recoveryId = Decode<string>(request.PayloadJson);
                var result = host.RestoreFlow(recoveryId);
                mistletoe.TaskCenter.RefreshCommand.Execute(null);
                return result.Status == HostActionStatus.Unavailable
                    ? RemoteCenterReply.Rejected(result.Message)
                    : RemoteCenterReply.Ok(message: result.Message);
            }
            case "task.flow.resetExecutionState":
            {
                var workflowId = Decode<string>(request.PayloadJson);
                return BeginTrackedOperation(requestId, fingerprint,
                    () => host.ResetWorkflowExecutionStateAsync(workflowId));
            }
            case "task.migration.prepare":
            {
                var sourceRoot = Decode<string>(request.PayloadJson);
                if (!Path.IsPathFullyQualified(sourceRoot))
                    return RemoteCenterReply.Rejected("迁移来源必须是执行端上的绝对目录");
                return BeginTrackedOperation(requestId, fingerprint, () => host.PrepareLegacyMigrationAsync(sourceRoot));
            }
            case "task.migration.activate":
            {
                var workflowId = Decode<string>(request.PayloadJson);
                return BeginTrackedOperation(requestId, fingerprint, () => host.ActivateMigrationCandidateAsync(workflowId));
            }
            case "task.migration.rollback":
            {
                var workflowId = Decode<string>(request.PayloadJson);
                return BeginTrackedOperation(requestId, fingerprint, () => host.RollbackMigrationAsync(workflowId));
            }
            case "task.migration.rehearsal":
                return BeginTrackedOperation(requestId, fingerprint, () => Task.Run(() =>
                {
                    var report = host.RunMigrationRehearsal();
                    return report.Success ? HostActionResult.Effective(report.Summary)
                        : HostActionResult.Unavailable(report.Summary);
                }));
            case "task.run.start":
            {
                var args = Decode<FlowStartArgs>(request.PayloadJson);
                return BeginTrackedOperation(requestId, fingerprint, async () =>
                    await host.StartWorkflowAsync(args.WorkflowId, args.EntryNodeId,
                        args.ExpectedRevision, args.StopAfterNodeId));
            }
            case "task.run.resume":
            {
                var runId = Decode<string>(request.PayloadJson);
                return BeginTrackedOperation(requestId, fingerprint, () => host.ResumeRunAsync(runId));
            }
            case "task.run.action":
            {
                var args = Decode<RunActionArgs>(request.PayloadJson);
                if (!Enum.TryParse<WorkflowRunAction>(args.Action, out var action))
                    return RemoteCenterReply.Rejected("未知运行操作");
                if (action == WorkflowRunAction.Stop)
                {
                    var result = await host.RequestRunActionAsync(args.RunId, action);
                    return result.Status == HostActionStatus.Unavailable
                        ? RemoteCenterReply.Rejected(result.Message) : RemoteCenterReply.Ok(message: result.Message);
                }
                return BeginTrackedOperation(requestId, fingerprint, () => host.RequestRunActionAsync(args.RunId, action));
            }
            case "operation.get":
            {
                var operationId = Decode<string>(request.PayloadJson);
                return _centerOperations.TryGetValue(operationId, out var state)
                    ? state : _centerOperationStore.Get(operationId)
                        ?? RemoteCenterReply.Rejected("执行端找不到这次操作，请刷新运行状态核对");
            }
            default:
                return RemoteCenterReply.Rejected("当前执行端不支持操作：" + request.Operation);
        }
    }

    private RemoteCenterReply BeginOperationReceipt(string id, string fingerprint)
    {
        var accepted = new RemoteCenterReply { Status = "accepted", OperationId = id,
            Message = "执行端已受理，正在核对运行条件" };
        if (!_centerOperations.TryAdd(id, accepted))
            throw new RemoteCenterOperationConflictException("执行端已有同身份在途操作，本次未再次执行");
        try { _centerOperationStore.Begin(id, accepted, fingerprint); }
        catch
        {
            _centerOperations.TryRemove(id, out _);
            throw;
        }
        return accepted;
    }

    private RemoteCenterReply BeginTrackedOperation(string id, string fingerprint,
        Func<Task<HostActionResult>> work)
    {
        var accepted = BeginOperationReceipt(id, fingerprint);
        _ = CompleteCenterOperationAsync(id, async () =>
        {
            var result = await work();
            return result.Status == HostActionStatus.Unavailable
                ? RemoteCenterReply.Rejected(result.Message) : RemoteCenterReply.Ok(message: result.Message);
        });
        return accepted;
    }

    private async Task<RemoteCenterReply> ExecuteImmediateCenterMutationAsync(string id,
        string fingerprint, Func<Task<RemoteCenterReply>> work)
    {
        BeginOperationReceipt(id, fingerprint);
        return await CompleteCenterOperationAsync(id, work);
    }

    private async Task<RemoteCenterReply> CompleteCenterOperationAsync(string id,
        Func<Task<RemoteCenterReply>> work)
    {
        RemoteCenterReply reply;
        try
        {
            reply = await work();
        }
        catch (Exception ex)
        {
            reply = RemoteCenterReply.Unknown(id, "执行端操作发生异常，结果未确认：" + ex.Message);
        }
        reply.OperationId = id;
        var durable = false;
        try
        {
            _centerOperationStore.Complete(id, reply);
            durable = true;
        }
        catch (Exception ex)
        {
            AddLog("[远程中心] 操作终态回执持久化失败，保留未知责任：" + ex.Message);
            reply = RemoteCenterReply.Unknown(id,
                "执行端操作已返回，但回执持久化失败；请核对实际运行状态");
        }
        if (durable) _centerOperations.TryRemove(id, out _);
        else _centerOperations[id] = reply;
        return reply;
    }

    private static T Decode<T>(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? throw new InvalidDataException("远程中心参数为空")
            : JsonSerializer.Deserialize<T>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })
                ?? throw new InvalidDataException("远程中心参数格式不正确");

    private static string AutoLaunchRevision(MistletoeViewModel vm)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            (vm.AutoLaunchOnBoot ? "1" : "0") + ":" + vm.AutoLaunchOnBootModeIndex);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static RemoteTriggerInfo[] CaptureRemoteTriggers(MistletoeViewModel vm)
        => vm.ArmedTimers.Select(t => new RemoteTriggerInfo(t.RuntimeId, "timer", t.StatusLine, t.Step))
            .Concat(vm.ArmedWatchdogs.Select(w => new RemoteTriggerInfo(w.RuntimeId, "watchdog",
                w.StatusLine, w.Step, w.LastCheckNote)))
            .Concat(vm.ArmedLogTriggers.Select(l => new RemoteTriggerInfo(l.RuntimeId, "log",
                l.StatusLine, l.Step, l.StatusNote)))
            .ToArray();
}
