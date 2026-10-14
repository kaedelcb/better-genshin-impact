using System.IO;
using System.Diagnostics;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
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
            var endpoint = new LocalConfigEditChannel.Endpoint(
                "BetterGI.Assistant.center-p" + child.Id,
                child.Id, child.StartTime.ToUniversalTime().Ticks);
            for (var attempt = 0; attempt < 12 && !child.HasExited; attempt++)
            {
                var probe = RemoteCenterProtocol.Request(room, uid, "隔离监控端", uid,
                    new RemoteCenterRequest { Operation = "probe" });
                try
                {
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
            var instance = probeReply!.TargetInstanceId;
            Assert.False(string.IsNullOrWhiteSpace(instance));
            async Task<RemoteCenterReply> Send(string operation, object? payload = null)
            {
                var command = RemoteCenterProtocol.Request(room, uid, "隔离监控端", uid,
                    new RemoteCenterRequest
                    {
                        Operation = operation, TargetInstanceId = instance,
                        PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload)
                    });
                var wire = await LocalConfigEditChannel.SendAsync(endpoint!, command, secret);
                return JsonSerializer.Deserialize<RemoteCenterReply>(RemoteCenterProtocol.PayloadOf(wire!)!)!;
            }
            Assert.Equal("ok", (await Send("startup.get")).Status);
            Assert.Equal("ok", (await Send("task.list")).Status);
            var doc = new WorkflowDocument
            {
                WorkflowId = "wf-" + Guid.NewGuid().ToString("N"), Name = "隔离进程远程计划",
                Nodes = [new WorkflowNode { NodeId = "n-end", Kind = "control.end" }]
            };
            var saved = await Send("task.flow.save", doc);
            Assert.Equal("ok", saved.Status);
            Assert.False(string.IsNullOrWhiteSpace(saved.Revision));
            var listed = await Send("task.list");
            Assert.Equal("ok", listed.Status);
            Assert.Contains(doc.WorkflowId, listed.PayloadJson);
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
