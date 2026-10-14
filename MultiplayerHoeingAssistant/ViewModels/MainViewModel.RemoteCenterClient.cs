using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.IO;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<RemoteCenterTarget> RemoteCenterTargets { get; } = [];
    private readonly ConcurrentDictionary<string, LocalConfigEditChannel.Endpoint> _localCenterEndpoints = new();
    private static string LocalEndpointKey(RemoteCenterTarget target)
        => target.PlayerUid + ":" + target.ClientInstanceId;

    private void UpdateRemoteCenterTargets(ControlRoomPlayersUpdate update)
    {
        if (update.Full)
        {
            var live = (update.Players ?? []).Where(p => p.Online && p.PlayerUid == _config?.PlayerUid
                && !string.IsNullOrEmpty(p.ClientInstanceId) && !string.IsNullOrEmpty(p.MachineId)).ToArray();
            for (var i = RemoteCenterTargets.Count - 1; i >= 0; i--)
            {
                var target = RemoteCenterTargets[i];
                if (!live.Any(p => p.PlayerUid == target.PlayerUid
                    && p.ClientInstanceId == target.ClientInstanceId)) RemoteCenterTargets.RemoveAt(i);
            }
            foreach (var player in live) UpsertRemoteCenterTarget(player);
        }
        else
        {
            foreach (var player in update.Changed ?? [])
            {
                if (player.Online) UpsertRemoteCenterTarget(player);
                else RemoveRemoteCenterTargets(player.PlayerUid, player.ClientInstanceId);
            }
            foreach (var uid in update.Removed ?? []) RemoveRemoteCenterTargets(uid, null);
        }
    }

    private void UpsertRemoteCenterTarget(ControlRoomPlayer player)
    {
        if (player.PlayerUid != _config?.PlayerUid) return;
        if (string.IsNullOrEmpty(player.ClientInstanceId) || string.IsNullOrEmpty(player.MachineId)) return;
        var current = RemoteCenterTargets.FirstOrDefault(t => t.PlayerUid == player.PlayerUid
            && t.ClientInstanceId == player.ClientInstanceId);
        if (current is not null)
        {
            current.PlayerName = player.PlayerName;
            current.MachineId = player.MachineId;
            current.Online = player.Online;
            return;
        }
        RemoteCenterTargets.Add(new RemoteCenterTarget
        {
            PlayerUid = player.PlayerUid, PlayerName = player.PlayerName,
            ClientInstanceId = player.ClientInstanceId, MachineId = player.MachineId,
            Online = player.Online
        });
    }

    private void RemoveRemoteCenterTargets(string uid, string? instanceId)
    {
        for (var i = RemoteCenterTargets.Count - 1; i >= 0; i--)
            if (RemoteCenterTargets[i].PlayerUid == uid
                && (instanceId is null || RemoteCenterTargets[i].ClientInstanceId == instanceId))
                RemoteCenterTargets.RemoveAt(i);
    }

    /// <summary>监控端同机发现；不依赖网络房间成员列表。</summary>
    public async Task<RemoteCenterTarget?> FindLocalCenterExecutorAsync()
    {
        if (!IsObserverMode || _config is null || string.IsNullOrEmpty(_config.ControlRoomPassword)
            || string.IsNullOrEmpty(_config.PlayerUid)) return null;
        var probe = RemoteCenterProtocol.Request(RoomCode, _config.PlayerUid, _config.PlayerName,
            _config.PlayerUid, new RemoteCenterRequest { Operation = "probe" });
        var endpoint = await LocalConfigEditChannel.FindAsync(probe, _config.ControlRoomPassword,
            RemoteCenterProtocol.ReplyCommand, "center");
        if (endpoint is null) return null;
        var replyCommand = await LocalConfigEditChannel.SendAsync(endpoint, probe, _config.ControlRoomPassword);
        var reply = ParseCenterReply(replyCommand);
        if (reply.Status != "ok" || string.IsNullOrEmpty(reply.PayloadJson)) return null;
        using var document = JsonDocument.Parse(reply.PayloadJson);
        var root = document.RootElement;
        var instanceId = root.GetProperty("instanceId").GetString();
        if (string.IsNullOrEmpty(instanceId)) return null;
        var found = new RemoteCenterTarget
        {
            PlayerUid = _config.PlayerUid,
            PlayerName = root.GetProperty("executorName").GetString() ?? _config.PlayerUid,
            ClientInstanceId = instanceId,
            MachineId = root.GetProperty("machineId").GetString() ?? "",
            Online = true, ForceLocal = true
        };
        _localCenterEndpoints[LocalEndpointKey(found)] = endpoint;
        return found;
    }

    public async Task<RemoteCenterReply> RequestRemoteCenterAsync(RemoteCenterTarget target,
        string operation, object? payload = null, string? expectedRevision = null)
    {
        if (!IsObserverMode || _config is null)
            return RemoteCenterReply.Rejected("远程中心仅供监控端使用");
        if (string.IsNullOrWhiteSpace(target.PlayerUid) || string.IsNullOrWhiteSpace(target.ClientInstanceId))
            return RemoteCenterReply.Rejected("执行端身份未知，请重新选择目标");
        var request = new RemoteCenterRequest
        {
            Operation = operation,
            TargetInstanceId = target.ClientInstanceId,
            ExpectedRevision = expectedRevision,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload)
        };
        var command = RemoteCenterProtocol.Request(RoomCode, _config.PlayerUid, _config.PlayerName,
            target.PlayerUid, request);
        var sameMachine = target.ForceLocal
            || (!string.IsNullOrEmpty(HostMachineIdentity.Current)
                && target.MachineId == HostMachineIdentity.Current);
        try
        {
            RemoteCenterReply reply;
            if (sameMachine)
            {
                var secret = _config.ControlRoomPassword;
                if (string.IsNullOrEmpty(secret))
                    return RemoteCenterReply.Rejected("同机远程中心需要配置控制房间密码");
                var key = LocalEndpointKey(target);
                if (!_localCenterEndpoints.TryGetValue(key, out var endpoint))
                {
                    var probe = RemoteCenterProtocol.Request(RoomCode, _config.PlayerUid, _config.PlayerName,
                        target.PlayerUid, new RemoteCenterRequest
                        { Operation = "probe", TargetInstanceId = target.ClientInstanceId });
                    endpoint = await LocalConfigEditChannel.FindAsync(probe, secret,
                        RemoteCenterProtocol.ReplyCommand, "center");
                    if (endpoint is null)
                        return RemoteCenterReply.Rejected("同机执行端本地通道不可用；未改走网络重发");
                    _localCenterEndpoints[key] = endpoint;
                }
                try { reply = ParseCenterReply(await LocalConfigEditChannel.SendAsync(endpoint, command, secret)); }
                catch { _localCenterEndpoints.TryRemove(key, out _); throw; }
            }
            else
            {
                if (string.IsNullOrEmpty(target.MachineId))
                    return RemoteCenterReply.Rejected("目标未上报机器身份，无法确定本地或网络通道");
                var client = _signalRClient;
                if (client is not { IsConnected: true })
                    return RemoteCenterReply.Rejected("控制房间网络未连接");
                var completion = new TaskCompletionSource<RemoteCenterReply>(TaskCreationOptions.RunContinuationsAsynchronously);
                var pending = new PendingCenterReply(target.PlayerUid, target.ClientInstanceId, completion);
                if (!_pendingCenterReplies.TryAdd(command.CommandId, pending))
                    return RemoteCenterReply.Rejected("远程中心请求身份冲突");
                try
                {
                    await client.SendRemoteCenterCommandAsync(command);
                    reply = await completion.Task.WaitAsync(TimeSpan.FromSeconds(20));
                }
                finally { _pendingCenterReplies.TryRemove(command.CommandId, out _); }
            }
            if (reply.TargetInstanceId != target.ClientInstanceId)
                return RemoteCenterReply.Rejected("执行端实例已变化，回复未采用");
            return reply;
        }
        catch (TimeoutException)
        {
            return RemoteCenterReply.Unknown(command.CommandId,
                "执行端未在期限内回复；本次操作结果未确认，请先刷新状态核对");
        }
        catch (Exception ex)
        {
            return RemoteCenterReply.Unknown(command.CommandId,
                "远程中心通信中断，结果未确认：" + ex.Message);
        }
    }

    private void CompleteRemoteCenterReply(RemoteCommand command)
    {
        if (!_pendingCenterReplies.TryGetValue(command.CommandId, out var pending)
            || command.SenderUid != pending.TargetUid) return;
        try
        {
            var reply = ParseCenterReply(command);
            if (reply.TargetInstanceId == pending.TargetInstanceId)
                pending.Completion.TrySetResult(reply);
        }
        catch (Exception ex) { pending.Completion.TrySetException(ex); }
    }

    private static RemoteCenterReply ParseCenterReply(RemoteCommand? command)
    {
        if (command?.Cmd != RemoteCenterProtocol.ReplyCommand)
            throw new InvalidDataException("执行端未返回中心回复");
        var json = RemoteCenterProtocol.PayloadOf(command);
        return string.IsNullOrEmpty(json)
            ? throw new InvalidDataException("执行端中心回复为空")
            : JsonSerializer.Deserialize<RemoteCenterReply>(json)
                ?? throw new InvalidDataException("执行端中心回复格式无效");
    }
}
