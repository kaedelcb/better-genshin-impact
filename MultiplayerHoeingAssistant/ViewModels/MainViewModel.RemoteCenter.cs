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
    private readonly ConcurrentDictionary<string, RemoteCenterReply> _centerRequestReplies = new();
    private readonly ConcurrentDictionary<string, RemoteCenterReply> _centerOperations = new();
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

    private async Task<RemoteCommand?> HandleRemoteCenterRequestAsync(RemoteCommand command)
    {
        if (_config is null || !IsExecutorMode || command.Cmd != RemoteCenterProtocol.RequestCommand
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
            if (_centerRequestReplies.TryGetValue(command.CommandId, out var previous))
                reply = previous;
            else
            {
                reply = await ExecuteRemoteCenterOperationAsync(command.CommandId, request);
                reply.TargetInstanceId = _config.ClientInstanceId;
                _centerRequestReplies.TryAdd(command.CommandId, reply);
                if (_centerRequestReplies.Count > 512)
                    foreach (var key in _centerRequestReplies.Keys.Take(128)) _centerRequestReplies.TryRemove(key, out _);
            }
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
        List<WorkflowResourceEditor.ResourceTask> Tasks, string? StartTaskId);

    private async Task<RemoteCenterReply> ExecuteRemoteCenterOperationAsync(string requestId,
        RemoteCenterRequest request)
    {
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
                    prompts = ConfirmStepWindow.RemotePrompts()
                }, revision);
            }
            case "startup.status":
                return RemoteCenterReply.Ok(new
                {
                    isRunning = mistletoe.IsRunning, statusText = mistletoe.StatusText,
                    triggers = CaptureRemoteTriggers(mistletoe),
                    prompts = ConfirmStepWindow.RemotePrompts()
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
                var revision = mistletoe.SaveRemoteStartupConfig(config, request.ExpectedRevision);
                return RemoteCenterReply.Ok(revision: revision, message: "执行端启动中心已保存");
            }
            case "startup.schemes.save":
            {
                var schemes = Decode<List<StartupFlowScheme>>(request.PayloadJson);
                var revision = mistletoe.SaveRemoteSchemes(schemes, request.ExpectedRevision);
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
                mistletoe.AutoLaunchOnBootModeIndex = mode;
                mistletoe.AutoLaunchOnBoot = enabled;
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
                mistletoe.RunNowCommand.Execute(null);
                return new RemoteCenterReply { Status = "accepted", Message = "执行端已受理启动流程" };
            case "startup.cancel":
                mistletoe.CancelRunCommand.Execute(null);
                ConfirmStepWindow.CloseForRemoteStop();
                return new RemoteCenterReply { Status = "accepted", Message = "执行端已受理取消请求" };
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
                return BeginTrackedOperation(requestId, host.ConnectResourceCatalogAsync);
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
            case "task.settings.open":
                OpenSettingsCommand.Execute(null);
                return RemoteCenterReply.Ok(message: "已在执行端桌面打开助手设置");
            case "task.flow.get":
            {
                var workflowId = Decode<string>(request.PayloadJson);
                var snapshot = host.LoadFlowSnapshot(workflowId);
                return RemoteCenterReply.Ok(snapshot.Document, snapshot.Revision);
            }
            case "task.flow.save":
            {
                var document = Decode<WorkflowDocument>(request.PayloadJson);
                var revision = host.SaveFlow(document, request.ExpectedRevision);
                mistletoe.TaskCenter.RefreshCommand.Execute(null);
                return RemoteCenterReply.Ok(new { document.WorkflowId }, revision, "执行端任务计划已保存");
            }
            case "task.flow.import":
            {
                var json = Decode<string>(request.PayloadJson);
                if (json.Length > 2_000_000) return RemoteCenterReply.Rejected("导入计划超过大小限制");
                var path = Path.Combine(Path.GetTempPath(), "bettergi-center-import-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(path, json);
                    var id = host.Workflows.Import(path);
                    mistletoe.TaskCenter.RefreshCommand.Execute(null);
                    return RemoteCenterReply.Ok(new { workflowId = id }, message: "已导入执行端计划");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
            }
            case "task.schedule.import":
            {
                var json = Decode<string>(request.PayloadJson);
                if (json.Length > 2_000_000) return RemoteCenterReply.Rejected("调度列表超过大小限制");
                var path = Path.Combine(Path.GetTempPath(),
                    "bettergi-center-schedule-import-" + Guid.NewGuid().ToString("N") + ".json");
                try
                {
                    File.WriteAllText(path, json);
                    var ids = WorkflowScheduleTransfer.Import(host.Workflows, path);
                    mistletoe.TaskCenter.RefreshCommand.Execute(null);
                    return RemoteCenterReply.Ok(new { count = ids.Count },
                        message: $"已在执行端导入 {ids.Count} 个只读候选");
                }
                finally { if (File.Exists(path)) File.Delete(path); }
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
            case "task.migration.prepare":
            {
                var sourceRoot = Decode<string>(request.PayloadJson);
                if (!Path.IsPathFullyQualified(sourceRoot))
                    return RemoteCenterReply.Rejected("迁移来源必须是执行端上的绝对目录");
                return BeginTrackedOperation(requestId, () => host.PrepareLegacyMigrationAsync(sourceRoot));
            }
            case "task.migration.activate":
            {
                var workflowId = Decode<string>(request.PayloadJson);
                return BeginTrackedOperation(requestId, () => host.ActivateMigrationCandidateAsync(workflowId));
            }
            case "task.migration.rollback":
            {
                var workflowId = Decode<string>(request.PayloadJson);
                return BeginTrackedOperation(requestId, () => host.RollbackMigrationAsync(workflowId));
            }
            case "task.migration.rehearsal":
                return BeginTrackedOperation(requestId, () => Task.Run(() =>
                {
                    var report = host.RunMigrationRehearsal();
                    return report.Success ? HostActionResult.Effective(report.Summary)
                        : HostActionResult.Unavailable(report.Summary);
                }));
            case "task.run.start":
            {
                var args = Decode<FlowStartArgs>(request.PayloadJson);
                return BeginTrackedOperation(requestId, async () =>
                    await host.StartWorkflowAsync(args.WorkflowId, args.EntryNodeId,
                        args.ExpectedRevision, args.StopAfterNodeId));
            }
            case "task.run.resume":
            {
                var runId = Decode<string>(request.PayloadJson);
                return BeginTrackedOperation(requestId, () => host.ResumeRunAsync(runId));
            }
            case "task.run.action":
            {
                var args = Decode<RunActionArgs>(request.PayloadJson);
                if (!Enum.TryParse<WorkflowRunAction>(args.Action, out var action))
                    return RemoteCenterReply.Rejected("未知运行操作");
                return BeginTrackedOperation(requestId, () => host.RequestRunActionAsync(args.RunId, action));
            }
            case "operation.get":
            {
                var operationId = Decode<string>(request.PayloadJson);
                return _centerOperations.TryGetValue(operationId, out var state)
                    ? state : RemoteCenterReply.Rejected("执行端找不到这次操作，请刷新运行状态核对");
            }
            default:
                return RemoteCenterReply.Rejected("当前执行端不支持操作：" + request.Operation);
        }
    }

    private RemoteCenterReply BeginTrackedOperation(string id, Func<Task<HostActionResult>> work)
    {
        var accepted = new RemoteCenterReply { Status = "accepted", OperationId = id,
            Message = "执行端已受理，正在核对运行条件" };
        if (!_centerOperations.TryAdd(id, accepted)) return _centerOperations[id];
        _ = CompleteTrackedOperationAsync(id, work);
        return accepted;
    }

    private async Task CompleteTrackedOperationAsync(string id, Func<Task<HostActionResult>> work)
    {
        try
        {
            var result = await work();
            _centerOperations[id] = result.Status == HostActionStatus.Unavailable
                ? RemoteCenterReply.Rejected(result.Message)
                : RemoteCenterReply.Ok(message: result.Message);
            _centerOperations[id].OperationId = id;
        }
        catch (Exception ex)
        {
            _centerOperations[id] = RemoteCenterReply.Rejected(ex.Message);
            _centerOperations[id].OperationId = id;
        }
    }

    private static T Decode<T>(string? json)
        => string.IsNullOrWhiteSpace(json)
            ? throw new InvalidDataException("远程中心参数为空")
            : JsonSerializer.Deserialize<T>(json)
                ?? throw new InvalidDataException("远程中心参数格式不正确");

    private static string AutoLaunchRevision(MistletoeViewModel vm)
    {
        var bytes = System.Text.Encoding.UTF8.GetBytes(
            (vm.AutoLaunchOnBoot ? "1" : "0") + ":" + vm.AutoLaunchOnBootModeIndex);
        return Convert.ToHexString(System.Security.Cryptography.SHA256.HashData(bytes));
    }

    private static RemoteTriggerInfo[] CaptureRemoteTriggers(MistletoeViewModel vm)
        => vm.ArmedTimers.Select(t => new RemoteTriggerInfo(t.RuntimeId, "timer", t.StatusLine))
            .Concat(vm.ArmedWatchdogs.Select(w => new RemoteTriggerInfo(w.RuntimeId, "watchdog", w.StatusLine)))
            .Concat(vm.ArmedLogTriggers.Select(l => new RemoteTriggerInfo(l.RuntimeId, "log", l.StatusLine)))
            .ToArray();
}
