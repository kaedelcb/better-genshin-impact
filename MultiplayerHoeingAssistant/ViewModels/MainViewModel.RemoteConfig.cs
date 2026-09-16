using System.Text.Json;
using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private LocalConfigEditChannel? _localConfigChannel;

    private void StartLocalConfigChannel()
    {
        try
        {
            _localConfigChannel = new LocalConfigEditChannel(
                () => _config?.ControlRoomPassword,
                cmd => Application.Current.Dispatcher.InvokeAsync(() => HandleLocalConfigRequestAsync(cmd)).Task.Unwrap(),
                AddLog);
        }
        catch (Exception ex) { AddLog($"本地配置通道不可用，将使用服务器：{ex.Message}"); }
    }

    private async Task<RemoteCommand?> HandleLocalConfigRequestAsync(RemoteCommand cmd)
    {
        // 认证通过仍须确认角色、房间、目标；监控端永远不作为配置落盘目标。
        if (!CanHandleLocalConfigRequest(_config, RoomCode, cmd)) return null;

        if (cmd.Cmd == "remote_config.pull") return await HandleRemoteConfigPullAsync(cmd);
        if (cmd.Cmd == "remote_config.push") return await HandleRemoteConfigPushAsync(cmd);
        if (cmd.Cmd is not ("remote_config.probe" or "remote_config.list")) return null;

        var reply = new RemoteCommand
        {
            Cmd = cmd.Cmd + "_result", SenderUid = _config!.PlayerUid, Sender = _config.PlayerName,
            Target = [cmd.SenderUid], CommandId = cmd.CommandId,
            Params = new Dictionary<string, object>()
        };
        if (cmd.Cmd == "remote_config.list")
        {
            var response = await SendConfigBgiIpcAsync("config.list", null);
            if (response is not { Success: true } || string.IsNullOrEmpty(response.Data))
                throw new InvalidOperationException(response?.ErrorMessage ?? "执行端 BGI 未返回配置列表");
            reply.Params["configGroups"] = JsonSerializer.Serialize(ParseConfigListData(
                JsonSerializer.Deserialize<JsonElement>(response.Data)).ConfigGroups);
        }
        return reply;
    }

    internal static bool CanHandleLocalConfigRequest(AssistConfig? config, string room, RemoteCommand cmd) =>
        config is { ObserverMode: false } && !string.IsNullOrEmpty(room) && cmd.RoomCode == room
        && !string.IsNullOrEmpty(config.PlayerUid) && cmd.Target is { Count: 1 } && cmd.Target[0] == config.PlayerUid
        && !string.IsNullOrEmpty(cmd.SenderUid)
        && (cmd.SenderUid == config.PlayerUid || config.TeamUids.Contains(cmd.SenderUid))
        && cmd.Cmd is "remote_config.probe" or "remote_config.list" or "remote_config.pull" or "remote_config.push";

    // 配置请求只能落到目标助手所属会话。写入发出后遇到异常不改走另一条 BGI 通道重试。
    private async Task<IpcResponse?> SendConfigBgiIpcAsync(string operation, string? payload)
    {
        if (_externalClient is { State: BgiExternalLinkState.Ready } ext
            && BgiExternalClient.TryMapToExtOperation(operation, out var extOperation))
        {
            var result = await ext.SendCommandAsync(extOperation,
                payload == null ? null : JsonSerializer.Deserialize<JsonElement>(payload), TimeSpan.FromSeconds(8));
            return new IpcResponse { Success = result.Success, Data = result.Data, ErrorMessage = result.ErrorMessage };
        }
        using var ipc = new IpcClient();
        await ipc.ConnectAsync(3000);
        if (!ipc.IsSessionTrusted) throw new InvalidOperationException("配置通道未能确认目标 BGI 属于本会话，已拒绝读写");
        return await ipc.SendCommandAsync(new IpcRequest { OpCode = operation, Payload = payload });
    }

    private async Task<bool> SendConfigViaServerAsync(RemoteCommand command)
    {
        var client = _signalRClient;
        if (client is not { IsConnected: true }) return false;
        await client.SendRemoteCommandAsync(command);
        return true;
    }

    /// <summary>拉取和保存固定使用本次选中的目标与通道；写入结果不明时不跨通道重发。</summary>
    private async void OnRemoteConfigEdit(object? parameter)
    {
        if (parameter is not MemberViewModel member || string.IsNullOrEmpty(member.PlayerUid)) return;
        if (member.PlayerUid == _config?.PlayerUid && !IsObserverMode)
        {
            AddLog("执行端不能远程编辑自己的配置组（请在本机直接修改）");
            return;
        }

        try
        {
            var room = RoomCode;
            var password = _config?.ControlRoomPassword ?? "";
            var senderUid = _config?.PlayerUid ?? "";
            var probe = new RemoteCommand
            {
                Cmd = "remote_config.probe", RoomCode = room, SenderUid = senderUid,
                Sender = _config?.PlayerName ?? "", Target = [member.PlayerUid],
                CommandId = Guid.NewGuid().ToString("N")
            };
            var endpoint = string.IsNullOrEmpty(password) ? null : await LocalConfigEditChannel.FindAsync(probe, password);
            var groups = member.ConfigGroups ?? [];
            var serverClient = _signalRClient;
            Func<RemoteCommand, Task<bool>> send = async command =>
            {
                if (RoomCode != room || _config?.PlayerUid != senderUid || !ReferenceEquals(serverClient, _signalRClient))
                    throw new InvalidOperationException("编辑期间房间、身份或连接已变化，请重新发起编辑");
                if (serverClient is not { IsConnected: true }) return false;
                command.SenderUid = senderUid;
                await serverClient.SendRemoteCommandAsync(command);
                return true;
            };

            _remoteConfigEditService ??= new RemoteConfigEditService(
                SendConfigViaServerAsync, () => _config?.PlayerUid ?? "", () => _config?.PlayerName ?? "",
                AddLog, () => _externalClient, EnsureLocalBgiReadyAsync);
            if (endpoint != null)
            {
                AddLog($"配置编辑：已匹配 {member.PlayerName} 的本地执行端，拉取和保存将使用本地通道");
                probe.Cmd = "remote_config.list";
                var list = await LocalConfigEditChannel.SendAsync(endpoint, probe, password);
                groups = JsonSerializer.Deserialize<List<string>>(GetRemoteParam(list?.Params, "configGroups") ?? "[]") ?? [];
                send = async command =>
                {
                    command.RoomCode = room;
                    command.SenderUid = senderUid;
                    var reply = await LocalConfigEditChannel.SendAsync(endpoint, command, password);
                    if (reply == null) throw new InvalidOperationException("本地执行端已不可用或身份/模式已变化，请重新发起编辑");
                    var expectedReply = command.Cmd == "remote_config.pull" ? "remote_config.data" : "remote_config.push_result";
                    if (reply.Cmd != expectedReply) throw new InvalidOperationException("本地执行端返回了不匹配的配置回复");
                    _remoteConfigEditService.TryComplete(command.CommandId, reply);
                    return true;
                };
            }
            else if (!member.Online || _signalRClient is not { IsConnected: true })
            {
                AddLog($"未发现 {member.PlayerName} 的本地执行端，且成员离线或服务器未连接，无法编辑配置");
                return;
            }
            else
            {
                AddLog($"配置编辑：{member.PlayerName} 未匹配本地执行端，拉取和保存将使用服务器");
            }

            if (groups.Count == 0)
            {
                AddLog($"成员 {member.PlayerName} 没有可用的配置组（可能状态尚未同步）");
                return;
            }
            var groupName = RemoteConfigGroupSelectWindow.ShowSelectDialog(groups, member.PlayerName, Application.Current.MainWindow);
            if (!string.IsNullOrEmpty(groupName))
                await _remoteConfigEditService.RunAsync(member.PlayerUid, member.PlayerName, groupName, send);
        }
        catch (Exception ex) { AddLog($"配置编辑失败：{ex.Message}"); }
    }
}
