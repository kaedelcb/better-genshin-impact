using System.Collections.Concurrent;
using System.Collections.ObjectModel;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Runtime.CompilerServices;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    public ObservableCollection<RemoteCenterTarget> RemoteCenterTargets { get; } = [];
    private readonly ConcurrentDictionary<string, LocalConfigEditChannel.Endpoint> _localCenterEndpoints = new();
    private sealed record CenterTargetScope(string ServerUrl, string RoomCode);
    private readonly ConditionalWeakTable<RemoteCenterTarget, CenterTargetScope> _centerTargetScopes = new();
    private readonly RemoteCenterPendingOperationStore _pendingCenterOperations = new();

    private CenterTargetScope BindCenterTargetScope(RemoteCenterTarget target, string server, string room) =>
        _centerTargetScopes.GetValue(target, _ => new CenterTargetScope(server, room));

    private static bool MatchesCenterTargetScope(CenterTargetScope scope, string server, string room) =>
        scope.RoomCode == room && RoomServerAddress.Same(scope.ServerUrl, server);

    private bool TryGetCachedCenterEndpoint(RemoteCenterTarget target, string key, string server,
        [NotNullWhen(true)] out LocalConfigEditChannel.Endpoint? endpoint)
    {
        if (_localCenterEndpoints.TryGetValue(key, out endpoint))
            return RoomServerAddress.Same(endpoint.ServerUrl, server);

        // Existing in-process fixtures install direct default-server endpoints by UID:instance.
        // Production discovery never populates this unscoped key, nor accepts it for another server.
        return RoomServerAddress.Same(server, LocalConfigEditChannel.DefaultServerUrl)
            && _localCenterEndpoints.TryGetValue(target.PlayerUid + ":" + target.ClientInstanceId, out endpoint)
            && endpoint is { ServerUrl: null, StartTicks: > 0 } && endpoint.ProcessId == Environment.ProcessId;
    }

    internal string GetRemoteCenterScopeKey(RemoteCenterTarget target)
    {
        var scope = BindCenterTargetScope(target, _config?.ServerUrl ?? "", RoomCode);
        // A legacy UID:instance receipt has no server or room provenance; leave it untouched.
        var server = RoomServerAddress.TryNormalize(scope.ServerUrl, out var normalized) ? normalized : scope.ServerUrl.Trim();
        return JsonSerializer.Serialize(new[] { server, scope.RoomCode, target.PlayerUid, target.ClientInstanceId });
    }

    private void RemoveCenterTargetsForOtherScopes(string server, string room)
    {
        for (var i = RemoteCenterTargets.Count - 1; i >= 0; i--)
            if (_centerTargetScopes.TryGetValue(RemoteCenterTargets[i], out var found)
                && !MatchesCenterTargetScope(found, server, room)) RemoteCenterTargets.RemoveAt(i);
    }

    internal RemoteCenterPendingOperation? PendingCenterOperation(RemoteCenterTarget target)
        => _pendingCenterOperations.Get(GetRemoteCenterScopeKey(target));

    internal void RememberCenterOperation(RemoteCenterTarget target, string id, string operation)
        => _pendingCenterOperations.Remember(GetRemoteCenterScopeKey(target),
            new RemoteCenterPendingOperation(id, operation, DateTimeOffset.UtcNow));

    internal void ClearCenterOperation(RemoteCenterTarget target, string id)
        => _pendingCenterOperations.Clear(GetRemoteCenterScopeKey(target), id);

    private void UpdateRemoteCenterTargets(ControlRoomPlayersUpdate update)
    {
        // 切房预览的成员来自候选连接；命令仍须等活动房间提交后才能发送。
        var server = _roomPreviewClient?.CurrentServerUrl ?? _config?.ServerUrl ?? "";
        var room = _roomPreviewClient?.CurrentRoomCode ?? RoomCode;
        if (!RoomServerAddress.TryNormalize(server, out _)) return;
        RemoveCenterTargetsForOtherScopes(server, room);
        if (update.Full)
        {
            var live = (update.Players ?? []).Where(p => p.Online && p.PlayerUid == _config?.PlayerUid
                && !string.IsNullOrEmpty(p.ClientInstanceId) && !string.IsNullOrEmpty(p.MachineId)).ToArray();
            for (var i = RemoteCenterTargets.Count - 1; i >= 0; i--)
            {
                var target = RemoteCenterTargets[i];
                if (target.ForceLocal) continue;
                if (!live.Any(p => p.PlayerUid == target.PlayerUid
                    && p.ClientInstanceId == target.ClientInstanceId)) RemoteCenterTargets.RemoveAt(i);
            }
            foreach (var player in live) UpsertRemoteCenterTarget(player, server, room);
        }
        else
        {
            foreach (var player in update.Changed ?? [])
            {
                if (player.Online) UpsertRemoteCenterTarget(player, server, room);
                else RemoveRemoteCenterTargets(player.PlayerUid, player.ClientInstanceId);
            }
            foreach (var uid in update.Removed ?? []) RemoveRemoteCenterTargets(uid, null);
        }
    }

    private void UpsertRemoteCenterTarget(ControlRoomPlayer player, string server, string room)
    {
        if (player.PlayerUid != _config?.PlayerUid) return;
        if (string.IsNullOrEmpty(player.ClientInstanceId) || string.IsNullOrEmpty(player.MachineId)) return;
        var current = RemoteCenterTargets.FirstOrDefault(t => t.PlayerUid == player.PlayerUid
            && t.ClientInstanceId == player.ClientInstanceId);
        if (current is not null)
        {
            BindCenterTargetScope(current, server, room);
            current.PlayerName = player.PlayerName;
            current.MachineId = player.MachineId;
            current.Online = player.Online;
            return;
        }
        var target = new RemoteCenterTarget
        {
            PlayerUid = player.PlayerUid, PlayerName = player.PlayerName,
            ClientInstanceId = player.ClientInstanceId, MachineId = player.MachineId,
            Online = player.Online
        };
        BindCenterTargetScope(target, server, room);
        RemoteCenterTargets.Add(target);
    }

    private void RemoveRemoteCenterTargets(string uid, string? instanceId)
    {
        for (var i = RemoteCenterTargets.Count - 1; i >= 0; i--)
            if (!RemoteCenterTargets[i].ForceLocal
                && RemoteCenterTargets[i].PlayerUid == uid
                && (instanceId is null || RemoteCenterTargets[i].ClientInstanceId == instanceId))
                RemoteCenterTargets.RemoveAt(i);
    }

    private bool IsCurrentCenterCaller(string room, string uid, LocalConfigEditChannel.RoomAuthentication authentication) =>
        IsObserverMode && RoomCode == room && _config?.PlayerUid == uid
        && IsCurrentLocalRoomAuthentication(authentication);

    /// <summary>监控端同机发现；不依赖网络房间成员列表。</summary>
    public async Task<IReadOnlyList<RemoteCenterTarget>> FindLocalCenterExecutorsAsync()
    {
        using var roomEffect = TryEnterAssistantRoomEffect();
        if (roomEffect is null || !IsObserverMode || _config is not { } config
            || string.IsNullOrEmpty(config.ControlRoomPassword) || string.IsNullOrEmpty(config.PlayerUid)) return [];
        var server = config.ServerUrl;
        if (!RoomServerAddress.TryNormalize(server, out _)) return [];
        var room = RoomCode;
        var uid = config.PlayerUid;
        var secret = config.ControlRoomPassword;
        var authentication = new LocalConfigEditChannel.RoomAuthentication(server, secret);
        RemoveCenterTargetsForOtherScopes(server, room);
        var probe = RemoteCenterProtocol.Request(room, uid, config.PlayerName,
            uid, new RemoteCenterRequest { Operation = "probe" });
        var endpoints = await LocalConfigEditChannel.FindAllAsync(probe, secret,
            RemoteCenterProtocol.ReplyCommand, "center", server);
        var found = new List<RemoteCenterTarget>();
        foreach (var endpoint in endpoints)
        {
            try
            {
                if (!IsCurrentCenterCaller(room, uid, authentication)) return [];
                var replyCommand = await LocalConfigEditChannel.SendAsync(endpoint, probe, secret,
                    TimeSpan.FromSeconds(2), server);
                if (!IsCurrentCenterCaller(room, uid, authentication)) return [];
                var reply = ParseCenterReply(replyCommand);
                if (reply.Status != "ok" || string.IsNullOrEmpty(reply.PayloadJson)) continue;
                using var document = JsonDocument.Parse(reply.PayloadJson);
                var root = document.RootElement;
                var instanceId = root.GetProperty("instanceId").GetString();
                if (string.IsNullOrEmpty(instanceId) || instanceId != reply.TargetInstanceId) continue;
                var target = new RemoteCenterTarget
                {
                    PlayerUid = uid,
                    PlayerName = root.GetProperty("executorName").GetString() ?? uid,
                    ClientInstanceId = instanceId,
                    MachineId = root.GetProperty("machineId").GetString() ?? "",
                    Online = true, ForceLocal = true
                };
                BindCenterTargetScope(target, server, room);
                _localCenterEndpoints[GetRemoteCenterScopeKey(target)] = endpoint;
                found.Add(target);
            }
            catch (Exception ex) when (ex is IOException or TimeoutException or OperationCanceledException
                or InvalidOperationException or InvalidDataException or JsonException or System.ComponentModel.Win32Exception)
            {
                // A pipe may disappear between discovery and read; keep other verified targets.
            }
        }
        return IsCurrentCenterCaller(room, uid, authentication) ? found : [];
    }

    public async Task<RemoteCenterTarget?> FindLocalCenterExecutorAsync()
    {
        var found = await FindLocalCenterExecutorsAsync();
        return found.Count switch
        {
            0 => null,
            1 => found[0],
            _ => throw new InvalidOperationException("本机有多个执行端，请在目标列表中选择")
        };
    }

    public async Task<RemoteCenterReply> RequestRemoteCenterAsync(RemoteCenterTarget target,
        string operation, object? payload = null, string? expectedRevision = null,
        string? requestId = null, int? flowEditVersion = null)
    {
        if (!IsObserverMode || _config is not { } config)
            return RemoteCenterReply.Rejected("远程中心仅供监控端使用");
        if (string.IsNullOrWhiteSpace(target.PlayerUid) || string.IsNullOrWhiteSpace(target.ClientInstanceId))
            return RemoteCenterReply.Rejected("执行端身份未知，请重新选择目标");
        using var roomEffect = TryEnterAssistantRoomEffect();
        if (roomEffect is null)
            return RemoteCenterReply.Rejected("房间正在切换或等待核对，请稍后重试");
        var server = config.ServerUrl;
        var room = RoomCode;
        if (!RoomServerAddress.TryNormalize(server, out _)
            || !MatchesCenterTargetScope(BindCenterTargetScope(target, server, room), server, room))
            return RemoteCenterReply.Rejected("执行端所属服务器或房间已变化，请重新选择目标");
        var senderUid = config.PlayerUid;
        var request = new RemoteCenterRequest
        {
            Operation = operation,
            TargetInstanceId = target.ClientInstanceId,
            ExpectedRevision = expectedRevision,
            FlowEditVersion = flowEditVersion,
            PayloadJson = payload is null ? null : JsonSerializer.Serialize(payload)
        };
        var command = RemoteCenterProtocol.Request(room, senderUid, config.PlayerName,
            target.PlayerUid, request, requestId);
        var sameMachine = target.ForceLocal
            || (!string.IsNullOrEmpty(HostMachineIdentity.Current)
                && target.MachineId == HostMachineIdentity.Current);
        try
        {
            RemoteCenterReply reply;
            if (sameMachine)
            {
                var secret = config.ControlRoomPassword;
                if (string.IsNullOrEmpty(secret))
                    return RemoteCenterReply.Rejected("同机远程中心需要配置控制房间密码");
                var authentication = new LocalConfigEditChannel.RoomAuthentication(server, secret);
                var key = GetRemoteCenterScopeKey(target);
                if (!TryGetCachedCenterEndpoint(target, key, server, out var endpoint))
                {
                    var probe = RemoteCenterProtocol.Request(room, senderUid, config.PlayerName,
                        target.PlayerUid, new RemoteCenterRequest
                        { Operation = "probe", TargetInstanceId = target.ClientInstanceId });
                    endpoint = await LocalConfigEditChannel.FindAsync(probe, secret,
                        RemoteCenterProtocol.ReplyCommand, "center", server);
                    if (endpoint is null)
                        return RemoteCenterReply.Rejected("同机执行端本地通道不可用；未改走网络重发");
                    _localCenterEndpoints[key] = endpoint;
                }
                if (!IsCurrentCenterCaller(room, senderUid, authentication))
                    return RemoteCenterReply.Rejected("监控端房间或服务器已变化，请重新选择目标");
                try
                {
                    reply = ParseCenterReply(await LocalConfigEditChannel.SendAsync(endpoint, command, secret, serverUrl: server));
                }
                catch { _localCenterEndpoints.TryRemove(key, out _); throw; }
            }
            else
            {
                if (string.IsNullOrEmpty(target.MachineId))
                    return RemoteCenterReply.Rejected("目标未上报机器身份，无法确定本地或网络通道");
                var client = _signalRClient;
                if (client is not { IsConnected: true } || client.CurrentRoomCode != room
                    || !RoomServerAddress.Same(client.CurrentServerUrl, server))
                    return RemoteCenterReply.Rejected("当前服务器的控制房间网络未连接");
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
