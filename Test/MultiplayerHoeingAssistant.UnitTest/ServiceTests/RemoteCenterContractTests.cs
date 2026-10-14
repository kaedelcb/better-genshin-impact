using System.IO;
using System.Diagnostics;
using System.Text.Json;
using System.Reflection;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RemoteCenterContractTests
{
    [Fact]
    public void ExecutorOperationJournal_RestartKeepsTerminal_AndTreatsAcceptedAsUnknown()
    {
        var root = Path.Combine(Path.GetTempPath(), "bettergi-center-operation-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "operation.json");
            var store = new RemoteCenterOperationStore(file);
            var accepted = new RemoteCenterReply { Status = "accepted", OperationId = "request-a" };
            const string fingerprint = "request-content-a";
            store.Begin("request-a", accepted, fingerprint);
            Assert.Equal("unknown", new RemoteCenterOperationStore(file).Get("request-a")?.Status);
            Assert.Equal("unknown", new RemoteCenterOperationStore(file)
                .GetForRequest("request-a", fingerprint)?.Status);
            Assert.Throws<RemoteCenterOperationConflictException>(() =>
                store.GetForRequest("request-a", "different-content"));
            var done = RemoteCenterReply.Ok(message: "原操作已完成");
            done.OperationId = "request-a";
            store.Complete("request-a", done);
            var recovered = new RemoteCenterOperationStore(file).Get("request-a");
            Assert.Equal("ok", recovered?.Status);
            Assert.Equal("request-a", recovered?.OperationId);
            Assert.Equal("ok", store.GetForRequest("request-a", fingerprint)?.Status);
            Assert.Throws<InvalidOperationException>(() => store.Begin("request-a", accepted, fingerprint));
            Assert.True(File.Exists(file + ".backup"));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public void PendingOperationJournal_SurvivesReopen_AndDoesNotClearAnotherIdentity()
    {
        var root = Path.Combine(Path.GetTempPath(), "bettergi-center-pending-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "pending.json");
            var first = new RemoteCenterPendingOperationStore(file);
            first.Remember("uid:instance", new RemoteCenterPendingOperation("request-a",
                "task.run.start", DateTimeOffset.UtcNow));
            var reopened = new RemoteCenterPendingOperationStore(file);
            Assert.Equal("request-a", reopened.Get("uid:instance")?.Id);
            Assert.Throws<InvalidOperationException>(() => reopened.Remember("uid:instance",
                new RemoteCenterPendingOperation("request-b", "task.run.start", DateTimeOffset.UtcNow)));
            reopened.Clear("uid:instance", "request-b");
            Assert.Equal("request-a", reopened.Get("uid:instance")?.Id);
            reopened.Clear("uid:instance", "request-a");
            Assert.Null(new RemoteCenterPendingOperationStore(file).Get("uid:instance"));
            Assert.True(File.Exists(file + ".backup"));
            File.WriteAllText(file, "{corrupt");
            Assert.Throws<InvalidDataException>(() => reopened.Get("uid:instance"));
            Assert.Equal("{corrupt", File.ReadAllText(file));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task IsolatedExecutorProcess_ServesStartupAndTaskCenterWithoutLiveUserData()
    {
        var root = Path.Combine(Path.GetTempPath(), "bettergi-center-process-smoke-" + Guid.NewGuid().ToString("N"));
        var uid = "center-smoke-" + Guid.NewGuid().ToString("N");
        var secret = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        var bgiPath = Path.Combine(root, "BetterGI.exe");
        File.WriteAllBytes(bgiPath, []);
        File.WriteAllText(Path.Combine(root, "mistletoe-package.json"),
            "{\"schema\":\"mistletoe.local-package\",\"schemaVersion\":1,\"ipcIsolation\":true}");
        var config = new AssistConfig
        {
            PlayerUid = uid, PlayerName = "隔离执行端", TeamUids = [uid],
            ControlRoomPassword = secret, StandaloneMode = true, ObserverMode = false,
            BgiPath = bgiPath,
            DisclaimerAccepted = true, ClientInstanceId = "center-smoke-" + Guid.NewGuid().ToString("N")
        };
        File.WriteAllText(Path.Combine(root, "assistant-config.json"), JsonSerializer.Serialize(config));
        var exe = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory,
            "../../../../../MultiplayerHoeingAssistant/bin/Debug/net8.0-windows/MultiplayerHoeingAssistant.exe"));
        Assert.True(File.Exists(exe), "先执行无部署助手构建再运行隔离进程测试");
        var start = new ProcessStartInfo(exe, "--no-auto-launch")
        {
            UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = Path.GetDirectoryName(exe)!
        };
        start.Environment["NEXUSBGI_DATA_ROOT"] = root;
        using var child = Process.Start(start) ?? throw new InvalidOperationException("隔离执行端未启动");
        Process? restarted = null;
        var passed = false;
        try
        {
            var room = AssistConfigManager.GenerateControlRoomCode([uid]);
            RemoteCenterReply? probeReply = null;
            LocalConfigEditChannel.Endpoint? endpoint = null;
            for (var attempt = 0; attempt < 12 && !child.HasExited; attempt++)
            {
                var probe = RemoteCenterProtocol.Request(room, uid, "隔离监控端", uid,
                    new RemoteCenterRequest { Operation = "probe", TargetInstanceId = config.ClientInstanceId });
                try
                {
                    endpoint = await LocalConfigEditChannel.FindAsync(probe, secret,
                        RemoteCenterProtocol.ReplyCommand, "center");
                    if (endpoint is null) { await Task.Delay(250); continue; }
                    var wire = await LocalConfigEditChannel.SendAsync(endpoint, probe, secret,
                        TimeSpan.FromSeconds(2));
                    probeReply = JsonSerializer.Deserialize<RemoteCenterReply>(
                        RemoteCenterProtocol.PayloadOf(wire!)!);
                    if (probeReply?.Status == "ok") break;
                }
                catch (Exception ex) when (ex is IOException or TimeoutException
                    or OperationCanceledException or InvalidOperationException) { }
                await Task.Delay(250);
            }
            Assert.Equal("ok", probeReply?.Status);
            Assert.Equal(child.Id, endpoint?.ProcessId);
            var wrongInstance = RemoteCenterProtocol.Request(room, uid, "隔离监控端", uid,
                new RemoteCenterRequest { Operation = "probe", TargetInstanceId = "missing-instance" });
            Assert.Null(await LocalConfigEditChannel.FindAsync(wrongInstance, secret,
                RemoteCenterProtocol.ReplyCommand, "center"));
            var instance = probeReply!.TargetInstanceId;
            Assert.False(string.IsNullOrWhiteSpace(instance));
            var monitor = new MainViewModel { RoomCode = room };
            typeof(MainViewModel).GetField("_config", BindingFlags.Instance | BindingFlags.NonPublic)!
                .SetValue(monitor, new AssistConfig
                {
                    PlayerUid = uid, PlayerName = "隔离监控端", TeamUids = [uid],
                    ControlRoomPassword = secret, ObserverMode = true, StandaloneMode = true
                });
            var target = Assert.Single(await monitor.FindLocalCenterExecutorsAsync());
            Assert.Equal(instance, target.ClientInstanceId);
            Assert.True(target.ForceLocal);
            var startupBefore = await monitor.RequestRemoteCenterAsync(target, "startup.get");
            Assert.Equal("ok", startupBefore.Status);
            var startupSnapshot = JsonSerializer.Deserialize<RemoteStartupSnapshot>(startupBefore.PayloadJson!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            var startupConfig = StartupFlowSchemeStore.Clone(startupSnapshot.Config);
            startupConfig.Enabled = false;
            startupConfig.DelaySeconds = 12;
            startupConfig.Steps = [new StartupStep { Id = "isolated-startup-sync", Kind = StartupStepKinds.Wait, WaitSeconds = 0 }];
            var startupSaved = await monitor.RequestRemoteCenterAsync(target, "startup.save", startupConfig, startupSnapshot.Revision);
            Assert.Equal("ok", startupSaved.Status);
            var startupAfterReply = await monitor.RequestRemoteCenterAsync(target, "startup.get");
            var startupAfter = JsonSerializer.Deserialize<RemoteStartupSnapshot>(startupAfterReply.PayloadJson!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.Equal(12, startupAfter.Config.DelaySeconds);
            Assert.Equal(startupSaved.Revision, startupAfter.Revision);
            Assert.Equal("isolated-startup-sync", Assert.Single(startupAfter.Config.Steps).Id);
            var rejectedStartup = await monitor.RequestRemoteCenterAsync(target, "startup.save", startupConfig, startupSnapshot.Revision);
            Assert.Equal("rejected", rejectedStartup.Status);
            Assert.Equal("rejected", (await monitor.RequestRemoteCenterAsync(target, "operation.get", rejectedStartup.OperationId)).Status);
            var savedSchemes = await monitor.RequestRemoteCenterAsync(target, "startup.schemes.save",
                new[] { new StartupFlowScheme { Name = "真实隔离执行端方案", Config = startupConfig } }, startupAfter.SchemesRevision);
            Assert.Equal("ok", savedSchemes.Status);
            var schemesAfterReply = await monitor.RequestRemoteCenterAsync(target, "startup.get");
            var schemesAfter = JsonSerializer.Deserialize<RemoteStartupSnapshot>(schemesAfterReply.PayloadJson!,
                new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!;
            Assert.Equal("真实隔离执行端方案", Assert.Single(schemesAfter.Schemes).Name);
            Assert.Equal(savedSchemes.Revision, schemesAfter.SchemesRevision);
            var rejectedSchemes = await monitor.RequestRemoteCenterAsync(target, "startup.schemes.save",
                new[] { new StartupFlowScheme { Name = "旧草稿" } }, startupAfter.SchemesRevision);
            Assert.Equal("rejected", rejectedSchemes.Status);
            Assert.Equal("rejected", (await monitor.RequestRemoteCenterAsync(target, "operation.get", rejectedSchemes.OperationId)).Status);
            var startupStatus = await monitor.RequestRemoteCenterAsync(target, "startup.status");
            Assert.Equal("ok", startupStatus.Status);
            using (var statusJson = JsonDocument.Parse(startupStatus.PayloadJson!))
                Assert.Equal(JsonValueKind.Array,
                    statusJson.RootElement.GetProperty("nodeStates").ValueKind);
            Assert.Equal("ok", (await monitor.RequestRemoteCenterAsync(target,
                "startup.runStates.clear")).Status);
            var startupRun = await monitor.RequestRemoteCenterAsync(target, "startup.run");
            Assert.Equal("accepted", startupRun.Status);
            for (var attempt = 0; attempt < 20 && startupRun.Status == "accepted"; attempt++)
            {
                await Task.Delay(100);
                startupRun = await monitor.RequestRemoteCenterAsync(target,
                    "operation.get", startupRun.OperationId);
            }
            Assert.Equal("ok", startupRun.Status);
            Assert.Equal("ok", (await monitor.RequestRemoteCenterAsync(target, "task.list")).Status);
            var bgiBefore = await monitor.RequestRemoteCenterAsync(target, "task.bgiPath.get");
            Assert.Equal("ok", bgiBefore.Status);
            var bgiSaved = await monitor.RequestRemoteCenterAsync(target,
                "task.bgiPath.save", bgiPath, bgiBefore.Revision);
            Assert.Equal("ok", bgiSaved.Status);
            Assert.True(File.Exists(Path.Combine(root,
                "assistant-config.json." + bgiBefore.Revision![..8] + ".backup.json")));
            var bgiAfter = await monitor.RequestRemoteCenterAsync(target, "task.bgiPath.get");
            using (var bgiJson = JsonDocument.Parse(bgiAfter.PayloadJson!))
                Assert.Equal(bgiPath, bgiJson.RootElement.GetProperty("path").GetString());
            Assert.Equal("rejected", (await monitor.RequestRemoteCenterAsync(target,
                "task.bgiPath.save", bgiPath, bgiBefore.Revision)).Status);
            var doc = new WorkflowDocument
            {
                WorkflowId = "wf-" + Guid.NewGuid().ToString("N"), Name = "隔离进程远程计划",
                Nodes = [new WorkflowNode { NodeId = "n-end", Kind = "control.end" }]
            };
            var saved = await monitor.RequestRemoteCenterAsync(target, "task.flow.save", doc);
            Assert.Equal("ok", saved.Status);
            Assert.False(string.IsNullOrWhiteSpace(saved.Revision));
            Assert.False(string.IsNullOrWhiteSpace(saved.OperationId));
            var saveReceipt = await monitor.RequestRemoteCenterAsync(target, "operation.get", saved.OperationId);
            Assert.Equal("ok", saveReceipt.Status);
            Assert.Equal(saved.Revision, saveReceipt.Revision);
            var loaded = await monitor.RequestRemoteCenterAsync(target, "task.flow.get", doc.WorkflowId);
            Assert.Equal("ok", loaded.Status);
            var conflictingCopy = JsonSerializer.Deserialize<WorkflowDocument>(JsonSerializer.Serialize(doc))!;
            conflictingCopy.Name = "过期修订不能覆盖";
            var refused = await monitor.RequestRemoteCenterAsync(target, "task.flow.save", conflictingCopy, "outdated-revision");
            Assert.Equal("rejected", refused.Status);
            Assert.Contains("修订冲突", refused.Message);
            Assert.Equal(doc.Name, JsonSerializer.Deserialize<WorkflowDocument>(
                (await monitor.RequestRemoteCenterAsync(target, "task.flow.get", doc.WorkflowId)).PayloadJson!)!.Name);
            // A different resource with identical bytes must not receive the old row's edit.
            var resourceFlow = new WorkflowDocument
            {
                WorkflowId = "wf-" + Guid.NewGuid().ToString("N"), Name = "配置身份守卫",
                Nodes = [new WorkflowNode { NodeId = "resource-node", Kind = "resource.configGroup",
                    Ref = new WorkflowResourceRef { Config = "原配置", Revision = "same-bytes" } }]
            };
            var firstResourceSave = await monitor.RequestRemoteCenterAsync(target, "task.flow.save", resourceFlow);
            Assert.Equal("ok", firstResourceSave.Status);
            resourceFlow.Nodes[0].Ref!.Config = "已切换配置";
            var secondResourceSave = await monitor.RequestRemoteCenterAsync(target,
                "task.flow.save", resourceFlow, firstResourceSave.Revision);
            Assert.Equal("ok", secondResourceSave.Status);
            var staleResourceSave = await monitor.RequestRemoteCenterAsync(target, "task.resource.layout.save", new
            {
                workflowId = resourceFlow.WorkflowId, nodeId = "resource-node",
                resourceKind = "resource.configGroup", resourceConfig = "原配置", resourceTaskId = (string?)null,
                current = new WorkflowResourceEditor.ResourceTaskLayout("same-bytes", null, []),
                tasks = Array.Empty<WorkflowResourceEditor.ResourceTask>(), startTaskId = (string?)null
            });
            Assert.Equal("rejected", staleResourceSave.Status);
            Assert.Contains("引用的配置已经改变", staleResourceSave.Message);
            Assert.Equal("ok", (await monitor.RequestRemoteCenterAsync(target, "task.flow.delete",
                new { workflowId = resourceFlow.WorkflowId, acknowledgeUnknownRuns = false },
                secondResourceSave.Revision)).Status);
            var listed = await monitor.RequestRemoteCenterAsync(target, "task.list");
            Assert.Equal("ok", listed.Status);
            Assert.Contains(doc.WorkflowId, listed.PayloadJson);
            var flowExport = await monitor.RequestRemoteCenterAsync(target,
                "task.flow.export", doc.WorkflowId);
            Assert.Equal("ok", flowExport.Status);
            var flowJson = JsonSerializer.Deserialize<string>(flowExport.PayloadJson!)!;
            var flowForImport = System.Text.Json.Nodes.JsonNode.Parse(flowJson)!.AsObject();
            flowForImport["workflowId"] = "wf-" + Guid.NewGuid().ToString("N");
            var flowImport = await monitor.RequestRemoteCenterAsync(target,
                "task.flow.import", flowForImport.ToJsonString());
            Assert.Equal("accepted", flowImport.Status);
            for (var attempt = 0; attempt < 20 && flowImport.Status == "accepted"; attempt++)
            {
                await Task.Delay(100);
                flowImport = await monitor.RequestRemoteCenterAsync(target,
                    "operation.get", flowImport.OperationId);
            }
            Assert.Equal("ok", flowImport.Status);
            var exported = await monitor.RequestRemoteCenterAsync(target, "task.schedule.export");
            Assert.Equal("ok", exported.Status);
            var package = JsonSerializer.Deserialize<string>(exported.PayloadJson!)!;
            Assert.Contains("mistletoe.scheduleList", package);
            var imported = await monitor.RequestRemoteCenterAsync(target, "task.schedule.import", package);
            Assert.Equal("accepted", imported.Status);
            for (var attempt = 0; attempt < 20 && imported.Status == "accepted"; attempt++)
            {
                await Task.Delay(100);
                imported = await monitor.RequestRemoteCenterAsync(target, "operation.get", imported.OperationId);
            }
            Assert.Equal("ok", imported.Status);
            var deleteId = Guid.NewGuid().ToString("N");
            var deleted = await monitor.RequestRemoteCenterAsync(target, "task.flow.delete",
                new { workflowId = doc.WorkflowId, acknowledgeUnknownRuns = false },
                saved.Revision, deleteId);
            Assert.Equal("ok", deleted.Status);
            Assert.Equal(deleteId, deleted.OperationId);
            var duplicate = await monitor.RequestRemoteCenterAsync(target, "task.flow.delete",
                new { workflowId = doc.WorkflowId, acknowledgeUnknownRuns = false },
                saved.Revision, deleteId);
            Assert.Equal("ok", duplicate.Status);
            Assert.Equal(deleted.Message, duplicate.Message);
            var conflict = await monitor.RequestRemoteCenterAsync(target, "task.flow.delete",
                new { workflowId = "different-flow", acknowledgeUnknownRuns = false },
                saved.Revision, deleteId);
            Assert.Equal("unknown", conflict.Status);
            Assert.Equal(deleteId, conflict.OperationId);
            Assert.Equal("ok", (await monitor.RequestRemoteCenterAsync(target,
                "operation.get", deleteId)).Status);
            child.Kill();
            child.WaitForExit(10000);
            restarted = Process.Start(start) ?? throw new InvalidOperationException("隔离执行端未能重启");
            RemoteCenterReply? afterRestart = null;
            for (var attempt = 0; attempt < 12 && !restarted.HasExited; attempt++)
            {
                afterRestart = await monitor.RequestRemoteCenterAsync(target, "startup.get");
                if (afterRestart.Status == "ok") break;
                await Task.Delay(250);
            }
            Assert.Equal("ok", afterRestart?.Status);
            var recoveredDelete = await monitor.RequestRemoteCenterAsync(target, "task.flow.delete",
                new { workflowId = doc.WorkflowId, acknowledgeUnknownRuns = false },
                saved.Revision, deleteId);
            Assert.Equal("ok", recoveredDelete.Status);
            Assert.Equal(deleted.OperationId, recoveredDelete.OperationId);
            Assert.Equal(deleted.Message, recoveredDelete.Message);
            var recoveredStartup = await monitor.RequestRemoteCenterAsync(target,
                "startup.run", requestId: startupRun.OperationId);
            Assert.Equal("ok", recoveredStartup.Status);
            Assert.Equal(startupRun.OperationId, recoveredStartup.OperationId);
            var recoveredSave = await monitor.RequestRemoteCenterAsync(target,
                "task.flow.save", doc, requestId: saved.OperationId);
            Assert.Equal("ok", recoveredSave.Status);
            Assert.Equal(saved.Revision, recoveredSave.Revision);
            Assert.Equal("rejected", (await monitor.RequestRemoteCenterAsync(target,
                "task.flow.get", doc.WorkflowId)).Status);
            var receiptFile = Path.Combine(root, "remote-center-operation-results.json");
            var originalReceipts = File.ReadAllText(receiptFile);
            try
            {
                File.WriteAllText(receiptFile, "{corrupt");
                var stop = await monitor.RequestRemoteCenterAsync(target, "task.run.action",
                    new { runId = "isolated-missing-run", action = "Stop" });
                Assert.Equal("rejected", stop.Status);
                Assert.Contains("运行记录不存在：isolated-missing-run", stop.Message);
                Assert.Equal("accepted", (await monitor.RequestRemoteCenterAsync(target, "startup.cancel")).Status);
                var blocked = await monitor.RequestRemoteCenterAsync(target, "task.flow.save",
                    new WorkflowDocument { WorkflowId = "wf-blocked", Name = "不应写入" });
                Assert.Equal("rejected", blocked.Status);
                Assert.Contains("台账无法读取", blocked.Message);
                Assert.False(File.Exists(Path.Combine(root, "flows", "wf-blocked.flow.json")));
                Assert.Equal("{corrupt", File.ReadAllText(receiptFile));
            }
            finally { File.WriteAllText(receiptFile, originalReceipts); }
            passed = true;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit(10000);
            }
            if (restarted is not null)
            {
                if (!restarted.HasExited) { restarted.Kill(); restarted.WaitForExit(10000); }
                restarted.Dispose();
            }
            if (passed) DeleteTestRoot(root);
        }
    }

    [Fact]
    public void StartupRemoteSave_RejectsStaleRevision_PreservesCurrentFileAndBackup()
    {
        var root = Path.Combine(Path.GetTempPath(), "bettergi-center-store-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var file = Path.Combine(root, "startup-flow.json");
            var store = new StartupFlowStore(file);
            var first = store.SaveChecked(new StartupFlowConfig { DelaySeconds = 3 }, null);
            var second = store.SaveChecked(new StartupFlowConfig { DelaySeconds = 8 }, first);
            Assert.NotEqual(first, second);
            Assert.Throws<StartupRevisionConflictException>(() =>
                store.SaveChecked(new StartupFlowConfig { DelaySeconds = 99 }, first));
            var current = store.LoadChecked();
            Assert.Equal(8, current.Config.DelaySeconds);
            Assert.Equal(second, current.Revision);
            Assert.True(File.Exists(Path.Combine(root, $"startup-flow.{first[..8]}.backup.json")));
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public void SchemesRemoteSave_RejectsConcurrentOverwrite()
    {
        var root = Path.Combine(Path.GetTempPath(), "bettergi-center-schemes-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(root);
        try
        {
            var store = new StartupFlowSchemeStore(Path.Combine(root, "schemes.json"));
            var first = store.SaveAllChecked([new StartupFlowScheme { Name = "第一版" }], null);
            store.SaveAllChecked([new StartupFlowScheme { Name = "第二版" }], first);
            Assert.Throws<StartupRevisionConflictException>(() =>
                store.SaveAllChecked([new StartupFlowScheme { Name = "旧草稿" }], first));
            Assert.Equal("第二版", Assert.Single(store.LoadChecked().Schemes).Name);
        }
        finally { DeleteTestRoot(root); }
    }

    [Fact]
    public async Task LocalCenterPipe_AuthenticatesAndReturnsMatchingInstance()
    {
        using var channel = new LocalConfigEditChannel(() => "secret", command =>
            Task.FromResult<RemoteCommand?>(RemoteCenterProtocol.Reply(command, "target", "执行端",
                new RemoteCenterReply { Status = "ok", TargetInstanceId = "instance-a" })),
            _ => { }, pipeName: "bettergi-center-test-" + Guid.NewGuid().ToString("N"), channel: "center");
        var command = RemoteCenterProtocol.Request("room", "target", "监控端", "target",
            new RemoteCenterRequest { Operation = "probe", TargetInstanceId = "instance-a" });
        await Assert.ThrowsAnyAsync<IOException>(() =>
            LocalConfigEditChannel.SendAsync(channel.Address, command, "wrong-secret"));
        var reply = await LocalConfigEditChannel.SendAsync(channel.Address, command, "secret");
        Assert.NotNull(reply);
        Assert.Equal(RemoteCenterProtocol.ReplyCommand, reply.Cmd);
        Assert.Equal(command.CommandId, reply.CommandId);
    }

    private static void DeleteTestRoot(string root)
    {
        var full = Path.GetFullPath(root);
        var temp = Path.GetFullPath(Path.GetTempPath());
        if (!full.StartsWith(temp, StringComparison.OrdinalIgnoreCase)
            || !Path.GetFileName(full).StartsWith("bettergi-center-", StringComparison.Ordinal))
            throw new InvalidOperationException("拒绝删除非本测试临时目录");
        Directory.Delete(full, recursive: true);
    }
}
