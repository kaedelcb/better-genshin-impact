using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.ViewModels;
using Xunit;

namespace MultiplayerHoeingAssistant.UnitTest.ServiceTests;

public class RemoteConfigEditingTests
{
    [Theory]
    [InlineData(false, true, true, false)]
    [InlineData(false, false, true, true)]
    [InlineData(false, false, false, false)]
    [InlineData(true, true, true, true)]
    [InlineData(true, true, false, true)]
    [InlineData(true, false, true, true)]
    public void EditButton_RespectsRoleAndTarget(bool observer, bool self, bool online, bool expected)
    {
        var member = new MemberViewModel { PlayerUid = "123", IsObserverMode = observer, IsSelf = self, Online = online };
        Assert.Equal(expected, member.CanRemoteEdit);
        member.PlayerUid = "";
        Assert.False(member.CanRemoteEdit);
    }

    [Fact]
    public void SwitchingMode_RefreshesSelfEditButton()
    {
        var member = new MemberViewModel { PlayerUid = "123", IsSelf = true, Online = true };
        var notifications = new List<string?>();
        member.PropertyChanged += (_, e) => notifications.Add(e.PropertyName);
        member.IsObserverMode = true;
        Assert.True(member.CanRemoteEdit);
        member.IsObserverMode = false;
        Assert.False(member.CanRemoteEdit);
        Assert.Equal(2, notifications.Count(n => n == nameof(MemberViewModel.CanRemoteEdit)));
    }

    [Fact]
    public void LocalRequests_RequireExecutorRoomTargetAndKnownSender()
    {
        var config = new AssistConfig { PlayerUid = "target", TeamUids = ["peer"] };
        var command = Request("remote_config.pull");
        Assert.True(MainViewModel.CanHandleLocalConfigRequest(config, "room", command));
        command.SenderUid = "target"; // 监控端与执行端使用同 UID。
        Assert.True(MainViewModel.CanHandleLocalConfigRequest(config, "room", command));
        config.ObserverMode = true;
        Assert.False(MainViewModel.CanHandleLocalConfigRequest(config, "room", command));
        config.ObserverMode = false;
        Assert.False(MainViewModel.CanHandleLocalConfigRequest(config, "other-room", command));
        command.Target = ["other-user"];
        Assert.False(MainViewModel.CanHandleLocalConfigRequest(config, "room", command));
        command.Target = ["target"];
        command.SenderUid = "stranger";
        Assert.False(MainViewModel.CanHandleLocalConfigRequest(config, "room", command));
        command.SenderUid = "peer";
        command.Cmd = "start_group";
        Assert.False(MainViewModel.CanHandleLocalConfigRequest(config, "room", command));
    }

    [Fact]
    public async Task LocalPipe_RejectsWrongPassword_ThenTransfersBothSettings()
    {
        var calls = 0;
        using var channel = new LocalConfigEditChannel(() => "room-password", cmd =>
        {
            calls++;
            return Task.FromResult<RemoteCommand?>(Reply(cmd, cmd.Params!));
        }, _ => { }, "bgi-config-test-" + Guid.NewGuid().ToString("N"));
        var command = Request("remote_config.push");
        command.Params = new()
        {
            ["scriptGroupConfigJson"] = "{\"策略\":\"" + new string('测', 40000) + "\"}",
            ["soloTaskName"] = "锄地一条龙（联机）",
            ["soloTaskSettingsJson"] = "{\"enabled\":false,\"roles\":[1,2]}"
        };
        await Assert.ThrowsAnyAsync<IOException>(() => LocalConfigEditChannel.SendAsync(channel.Address, command, "wrong-password"));
        Assert.Equal(0, calls);
        var reply = await LocalConfigEditChannel.SendAsync(channel.Address, command, "room-password");
        Assert.NotNull(reply);
        foreach (var entry in command.Params)
            Assert.Equal(entry.Value, ((JsonElement)reply!.Params![entry.Key]).GetString());
        Assert.Equal(1, calls);
    }

    [Fact]
    public async Task LocalPipe_RejectsReplacedInstanceAndMismatchedReply()
    {
        using var channel = new LocalConfigEditChannel(() => "secret", cmd =>
            Task.FromResult<RemoteCommand?>(new RemoteCommand { SenderUid = "wrong", CommandId = cmd.CommandId }),
            _ => { }, "bgi-config-test-" + Guid.NewGuid().ToString("N"));
        await Assert.ThrowsAsync<InvalidOperationException>(() => LocalConfigEditChannel.SendAsync(
            channel.Address with { StartTicks = 0 }, Request("remote_config.pull"), "secret"));
        await Assert.ThrowsAsync<InvalidDataException>(() => LocalConfigEditChannel.SendAsync(
            channel.Address, Request("remote_config.pull"), "secret"));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task EditSession_PreservesBothSettings_AndNeverResendsFailedLocalSaveViaServer(bool local, bool loseSaveReply)
    {
        var serverCalls = 0;
        var localCalls = 0;
        var reports = new List<string>();
        RemoteCommand? saved = null;
        RemoteConfigEditService service = null!;
        Task<bool> Transport(RemoteCommand cmd)
        {
            Dictionary<string, object> data;
            if (cmd.Cmd == "remote_config.pull")
                data = new() { ["ok"] = "true", ["packageJson"] = "{\"fileMd5\":\"original-md5\"}" };
            else
            {
                saved = cmd;
                if (loseSaveReply) throw new IOException("应用后回执连接中断");
                data = new() { ["ok"] = "true", ["message"] = "已落盘" };
            }
            Assert.True(service.TryComplete(cmd.CommandId, Reply(cmd, data)));
            return Task.FromResult(true);
        }
        service = new RemoteConfigEditService(cmd => { serverCalls++; return Transport(cmd); },
            () => "peer", () => "监控端", reports.Add, sendIpcAsync: (op, _) => Task.FromResult<IpcResponse?>(new()
            {
                Success = true,
                Data = op == "config.open_remote_editor" ? "{\"state\":\"editing\"}" :
                    "{\"state\":\"saved\",\"scriptGroupConfigJson\":\"{\\\"enabled\\\":true}\",\"soloTaskName\":\"锄地一条龙（联机）\",\"soloTaskSettingsJson\":\"{\\\"enabled\\\":false}\"}"
            }));
        await service.RunAsync("target", "执行端", "配置组", local ? cmd => { localCalls++; return Transport(cmd); } : null);
        Assert.True(saved != null, string.Join("\n", reports));
        Assert.Equal("original-md5", saved!.Params!["baseMd5"]);
        Assert.Equal("{\"enabled\":true}", saved.Params["scriptGroupConfigJson"]);
        Assert.Equal("锄地一条龙（联机）", saved.Params["soloTaskName"]);
        Assert.Equal("{\"enabled\":false}", saved.Params["soloTaskSettingsJson"]);
        Assert.Equal(local ? 0 : 2, serverCalls);
        Assert.Equal(local ? 2 : 0, localCalls);
        Assert.Equal(!loseSaveReply, reports.Any(r => r.StartsWith("远程配置已应用")));
    }

    private static RemoteCommand Request(string cmd) => new()
    {
        Cmd = cmd, RoomCode = "room", SenderUid = "peer", Target = ["target"], CommandId = Guid.NewGuid().ToString("N")
    };

    private static RemoteCommand Reply(RemoteCommand request, Dictionary<string, object> data) => new()
    {
        Cmd = request.Cmd == "remote_config.pull" ? "remote_config.data" : "remote_config.push_result",
        SenderUid = "target", CommandId = request.CommandId, Params = data
    };
}
