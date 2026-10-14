using System.IO;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public sealed class RemoteCenterContractTests
{
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
