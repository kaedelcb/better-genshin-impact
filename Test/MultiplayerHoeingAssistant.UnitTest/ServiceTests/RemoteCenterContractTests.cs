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
    public async Task IsolatedExecutorProcess_ServesStartupAndTaskCenterWithoutLiveUserData()
    {
        var root = Path.Combine(Path.GetTempPath(), "bettergi-center-process-smoke-" + Guid.NewGuid().ToString("N"));
        var uid = "center-smoke-" + Guid.NewGuid().ToString("N");
        var secret = Guid.NewGuid().ToString("N");
        Directory.CreateDirectory(root);
        var config = new AssistConfig
        {
            PlayerUid = uid, PlayerName = "隔离执行端", TeamUids = [uid],
            ControlRoomPassword = secret, StandaloneMode = true, ObserverMode = false,
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
            Assert.Equal("ok", (await monitor.RequestRemoteCenterAsync(target, "startup.get")).Status);
            Assert.Equal("ok", (await monitor.RequestRemoteCenterAsync(target, "task.list")).Status);
            var bgiPath = Path.Combine(root, "BetterGI.exe");
            File.WriteAllBytes(bgiPath, []);
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
            var listed = await monitor.RequestRemoteCenterAsync(target, "task.list");
            Assert.Equal("ok", listed.Status);
            Assert.Contains(doc.WorkflowId, listed.PayloadJson);
            var exported = await monitor.RequestRemoteCenterAsync(target, "task.schedule.export");
            Assert.Equal("ok", exported.Status);
            var package = JsonSerializer.Deserialize<string>(exported.PayloadJson!)!;
            Assert.Contains("mistletoe.scheduleList", package);
            var imported = await monitor.RequestRemoteCenterAsync(target, "task.schedule.import", package);
            Assert.Equal("ok", imported.Status);
            passed = true;
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
                child.WaitForExit(10000);
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
            Assert.Throws<InvalidOperationException>(() =>
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
            Assert.Throws<InvalidOperationException>(() =>
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
