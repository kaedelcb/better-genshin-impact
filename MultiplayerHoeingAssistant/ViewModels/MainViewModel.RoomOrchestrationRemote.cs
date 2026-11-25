using System.Security.Cryptography;
using System.Text.Json;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

internal sealed record RemoteOrchestrationSnapshot(string PlayerUid, string InstanceId, string MachineId,
    string ExecutorName, DateTimeOffset CapturedAtUtc, RoomOrchestrationPresentation Presentation);
internal sealed record RemoteOrchestrationEdit(RoomOrchestrationEditSnapshot Snapshot, RoomSwitchCompletion[] Choices,
    bool HasPeerSnapshot, string Revision);
internal sealed record RemoteOrchestrationCatalogRequest(string ProfileId, string Type, string Name, string WorkflowId, bool ExplicitRefresh = false);
internal sealed record RemoteOrchestrationLayoutRequest(string ProfileId, RoomOrchestrationBinding[]? Bindings);
internal sealed record RemoteOrchestrationSaveRequest(string ProfileId, RoomOrchestrationEditChanges Changes, string? AcceptedLayoutConflict);
internal sealed class RoomOrchestrationSaveRejectedException(string message) : InvalidOperationException(message);

public partial class MainViewModel
{
    private static string RoomOrchestrationEditRevision(RoomOrchestrationEditSnapshot snapshot)
        => Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(snapshot)));

    // Executed only by the existing authenticated same-UID/instance center handler. No room
    // passwords, local BGI paths or mutable AssistConfig/room profile objects leave the executor.
    private RemoteCenterReply BeginRoomOrchestrationCatalogRefresh(string id, string fingerprint, RemoteCenterRequest request,
        Func<bool>? stillAuthorized = null)
    {
        var accepted = BeginOperationReceipt(id, fingerprint);
        _ = CompleteCenterOperationAsync(id, async () =>
        {
            using var effect = TryEnterAssistantRoomEffect();
            if (effect is null) return RemoteCenterReply.Rejected("房间正在切换，未刷新目录");
            using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(180));
            var uid = _config?.PlayerUid ?? "";
            if (!TryRegisterCompletionCatalogRefresh((uid, id), cancellation))
                return RemoteCenterReply.Rejected("同一目录刷新正在执行");
            try { return await ExecuteRoomOrchestrationRemoteOperationAsync(request, cancellation.Token, stillAuthorized); }
            finally { _completionCatalogRefreshes.TryRemove((uid, id), out _); }
        });
        return accepted;
    }

    internal async Task<RemoteCenterReply> ExecuteRoomOrchestrationRemoteOperationAsync(RemoteCenterRequest request,
        CancellationToken? refreshToken = null, Func<bool>? stillAuthorized = null)
    {
        if (stillAuthorized?.Invoke() == false) return RemoteCenterReply.Rejected("原网页授权已变化，未执行任务编排请求");
        if (!IsExecutorMode || _config is not { } config || request.TargetInstanceId != config.ClientInstanceId)
            return RemoteCenterReply.Rejected("仅当前已确认身份的执行端可以响应任务编排请求");
        using var budget = refreshToken is { } longRefresh ? CancellationTokenSource.CreateLinkedTokenSource(longRefresh)
            : new CancellationTokenSource(TimeSpan.FromSeconds(18));
        var ct = budget.Token;
        T Read<T>() => request.PayloadJson is { Length: > 0 } payload
            ? JsonSerializer.Deserialize<T>(payload) ?? throw new InvalidOperationException("任务编排请求参数无效")
            : throw new InvalidOperationException("缺少任务编排请求参数");
        switch (request.Operation)
        {
            case "room.orchestration.get":
                return RemoteCenterReply.Ok(new RemoteOrchestrationSnapshot(config.PlayerUid, config.ClientInstanceId,
                    HostMachineIdentity.Current, config.PlayerName, DateTimeOffset.UtcNow, GetRoomOrchestrationPresentation()));
            case "room.orchestration.edit":
                {
                    var profileId = Read<string>();
                    var snapshot = ReadRoomOrchestrationEdit(profileId);
                    return RemoteCenterReply.Ok(new RemoteOrchestrationEdit(snapshot with { BgiPath = "" },
                        RoomSwitchCompletion.Choices(config, profileId).ToArray(), HasRoomOrchestrationPeerSnapshot(profileId),
                        RoomOrchestrationEditRevision(snapshot)));
                }
            case "room.orchestration.names":
                {
                    var args = Read<RemoteOrchestrationCatalogRequest>();
                    _ = ReadRoomOrchestrationEdit(args.ProfileId);
                    return RemoteCenterReply.Ok(await LoadRoomOrchestrationNamesAsync(args.ExplicitRefresh, ct));
                }
            case "room.orchestration.catalog":
                {
                    var args = Read<RemoteOrchestrationCatalogRequest>();
                    _ = ReadRoomOrchestrationEdit(args.ProfileId);
                    if (args.Type is not ("group" or "onedragon" or "workflow"))
                        return RemoteCenterReply.Rejected("未知任务目录类型");
                    return RemoteCenterReply.Ok(await LoadOrchestrationCompletionCatalogAsync(args.Type, args.Name,
                        args.WorkflowId, args.ExplicitRefresh, ct));
                }
            case "room.orchestration.cancel":
                {
                    var id = Read<string>();
                    if (!id.StartsWith("room-orchestration-", StringComparison.Ordinal))
                        return RemoteCenterReply.Rejected("无效的编排目录请求编号");
                    CancelCompletionCatalogRefresh(config.PlayerUid, id);
                    return RemoteCenterReply.Ok(message: "已请求取消目录刷新");
                }
            case "room.orchestration.layout":
                {
                    var args = Read<RemoteOrchestrationLayoutRequest>();
                    _ = ReadRoomOrchestrationEdit(args.ProfileId);
                    return RemoteCenterReply.Ok(GetRoomOrchestrationLayoutConflict(args.ProfileId, args.Bindings));
                }
            case "room.orchestration.save":
                {
                    var committing = false;
                    try
                    {
                        var args = Read<RemoteOrchestrationSaveRequest>();
                        if (args.Changes is null || string.IsNullOrWhiteSpace(request.ExpectedRevision))
                            return RemoteCenterReply.Rejected("缺少原编辑版本，请重新读取执行端配置");
                        var opened = ReadRoomOrchestrationEdit(args.ProfileId);
                        if (RoomOrchestrationEditRevision(opened) != request.ExpectedRevision)
                            return RemoteCenterReply.Rejected("执行端该房间配置已变化，请重新打开编辑器");
                        if (args.Changes.Completion is { } completion
                            && await ValidateRoomOrchestrationCompletionAsync(completion, ct) is { } error)
                            return RemoteCenterReply.Rejected(error);
                        // Never trust a catalog supplied by the monitor. The actual executor checks
                        // every changed task reference and the same current room edit revision.
                        RoomOrchestrationNameCatalog? names = null;
                        if (args.Changes.Bindings is not null || args.Changes.QuickCommands?.Values.Any(value => value.Length > 0) == true)
                            names = await LoadRoomOrchestrationNamesAsync(false, ct);
                        ct.ThrowIfCancellationRequested();
                        if (stillAuthorized?.Invoke() == false || !IsExecutorMode || !ReferenceEquals(config, _config) || config.ClientInstanceId != request.TargetInstanceId
                            || RoomOrchestrationEditRevision(ReadRoomOrchestrationEdit(args.ProfileId)) != request.ExpectedRevision)
                            return RemoteCenterReply.Rejected("执行端房间配置或实例已变化，本次未保存");
                        committing = true;
                        try { CommitRoomOrchestrationEdit(opened, args.Changes, names, args.AcceptedLayoutConflict, remoteCenterOwnsRoomEffect: true); }
                        catch (RoomOrchestrationSaveRejectedException ex) { return RemoteCenterReply.Rejected(ex.Message); }
                        return RemoteCenterReply.Ok(new RemoteOrchestrationSnapshot(config.PlayerUid, config.ClientInstanceId,
                            HostMachineIdentity.Current, config.PlayerName, DateTimeOffset.UtcNow, GetRoomOrchestrationPresentation()),
                            message: "执行端房间配置已确认保存");
                    }
                    catch (Exception ex) when (!committing) { return RemoteCenterReply.Rejected(ex.Message); }
                }
            default:
                return RemoteCenterReply.Rejected("不支持的任务编排操作");
        }
    }
}
