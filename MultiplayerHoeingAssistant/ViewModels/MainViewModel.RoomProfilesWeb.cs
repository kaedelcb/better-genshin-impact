using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private const string WebProfilesReceiptPrefix = "room-profiles-web-";
    private const string WebSwitchReceiptPrefix = "room-switch-web-";
    private const string WebOrchestrationReceiptPrefix = "room-orchestration-web-";

    private sealed record WebRoomPrefixes(string Profiles, string Switch, string Orchestration)
    {
        public string? ForOperation(string operation) => operation.StartsWith("room.profiles.", StringComparison.Ordinal) ? Profiles
            : operation.StartsWith("room.switch.", StringComparison.Ordinal) ? Switch
            : operation.StartsWith("room.orchestration.", StringComparison.Ordinal) ? Orchestration : null;

        public string? ForReceipt(string? id) => Owns(id, Profiles) ? Profiles
            : Owns(id, Switch) ? Switch : Owns(id, Orchestration) ? Orchestration : null;

        public static bool Owns(string? id, string prefix) => id is not null
            && id.StartsWith(prefix, StringComparison.Ordinal) && ValidWebRoomRequestId(id[prefix.Length..]);
    }

    private sealed record WebRoomRequestContext(WebRoomPrefixes Prefixes, Func<bool> StillAuthorized);

    private static bool ValidWebRoomRequestId(string? id)
        => id is { Length: > 0 and <= 64 } && id.All(char.IsAsciiLetterOrDigit);

    // Receipts must follow the same executor when it changes rooms, but not a different
    // browser authorization, UID, instance or machine. Local pairing is an executor-local owner.
    private static WebRoomPrefixes CreateWebRoomPrefixes(AssistConfig config, bool local, string? browserId)
    {
        var owner = local ? "local" : "browser:" + browserId;
        var scope = Convert.ToHexString(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            config.PlayerUid, config.ClientInstanceId, MachineId = HostMachineIdentity.Current, Owner = owner
        })))[..32].ToLowerInvariant() + "-";
        return new(WebProfilesReceiptPrefix + scope, WebSwitchReceiptPrefix + scope, WebOrchestrationReceiptPrefix + scope);
    }

    private static bool IsWebRoomReceipt(string? id) => id is not null &&
        (id.StartsWith(WebProfilesReceiptPrefix, StringComparison.Ordinal)
            || id.StartsWith(WebSwitchReceiptPrefix, StringComparison.Ordinal)
            || id.StartsWith(WebOrchestrationReceiptPrefix, StringComparison.Ordinal));

    private static bool IsRoomProfilesOrSwitchOperation(string operation)
        => operation.StartsWith("room.profiles.", StringComparison.Ordinal) || operation.StartsWith("room.switch.", StringComparison.Ordinal);

    private static bool IsOwnerRoomOperation(string operation) => IsRoomProfilesOrSwitchOperation(operation)
        || operation.StartsWith("room.orchestration.", StringComparison.Ordinal);

    private static bool IsSupportedOwnerRoomOperation(string operation) => operation is
        "room.profiles.get" or "room.profiles.legacyCode" or "room.profiles.save" or "room.profiles.create" or "room.profiles.remove"
        or "room.switch.start" or "room.switch.status" or "room.switch.cancel" or "room.switch.recheck"
        or "room.orchestration.get" or "room.orchestration.edit" or "room.orchestration.names" or "room.orchestration.catalog"
        or "room.orchestration.layout" or "room.orchestration.save" or "room.orchestration.cancel";

    // Strictly the read-only owner snapshots and the original switch's cancellation/recovery
    // can be dispatched without a room-effect lease. They are STILL checked by the owner,
    // exact-instance/machine and receipt-scope gates. No ordinary room/task action uses this path.
    private static bool HasRoomWebIdentity(AssistConfig config) => !string.IsNullOrWhiteSpace(config.PlayerUid)
        && !string.IsNullOrWhiteSpace(config.ClientInstanceId) && !string.IsNullOrWhiteSpace(HostMachineIdentity.Current);

    private static string? WebString(JsonNode? value)
        => value is JsonValue text && text.TryGetValue<string>(out var result) ? result : null;

    private static bool IsRoomWebReceiptReference(string operation) => operation is "operation.get" or "room.orchestration.cancel"
        or "room.switch.status" or "room.switch.cancel" or "room.switch.recheck";

    private static bool IsRoomWebRequest(string operation, string? receiptId) => IsOwnerRoomOperation(operation)
        || operation == "operation.get" && (IsWebRoomReceipt(receiptId)
            || receiptId?.StartsWith("room-orchestration-", StringComparison.Ordinal) == true);

    private static string? ValidateRoomWebBoundary(AssistConfig config, string operation, string? receiptId,
        string? expectedMachineId, WebRoomPrefixes? prefixes)
    {
        if (!IsRoomWebRequest(operation, receiptId)) return null;
        if (config.ObserverMode || !HasRoomWebIdentity(config) || expectedMachineId != HostMachineIdentity.Current || prefixes is null)
            return "仅当前本人已确认机器和实例的执行端可执行网页房间操作";
        if (operation == "operation.get")
            return prefixes.ForReceipt(receiptId) is null ? "该操作回执不属于原网页授权及执行端" : null;
        if (!IsSupportedOwnerRoomOperation(operation)) return "不支持的网页房间操作";
        if (IsRoomWebReceiptReference(operation) && !WebRoomPrefixes.Owns(receiptId, prefixes.ForOperation(operation)!))
            return "该操作编号不属于原网页授权及执行端";
        return null;
    }

    internal static bool WebRoomRequestCanReadOrRecoverDuringSwitch(JsonObject request)
    {
        if (request["operation"] is not JsonValue value || !value.TryGetValue<string>(out var operation)) return false;
        return operation is "web.identity" or "room.profiles.get" or "room.switch.status" or "room.switch.cancel" or "room.switch.recheck"
            || operation == "operation.get" && request["payload"] is JsonValue receipt
                && receipt.TryGetValue<string>(out var id) && IsWebRoomReceipt(id);
    }

    private static readonly byte[] RoomProfilesRevisionKey = RandomNumberGenerator.GetBytes(32);

    // The revision covers the editable room fields and hidden credentials (but not unrelated
    // runtime/schedule/task fields). A process-local keyed digest must not be an offline password
    // verifier. It is stable until a relevant setting changes; a restart invalidates old drafts.
    private static string RoomProfilesWebRevision(AssistConfig config)
        => Convert.ToHexString(HMACSHA256.HashData(RoomProfilesRevisionKey, JsonSerializer.SerializeToUtf8Bytes(new
        {
            config.ClientInstanceId, config.PlayerUid, config.ObserverMode, config.StandaloneMode,
            config.ActiveRoomProfileId, LocalRoomProfileId = EffectiveLocalRoomProfileId(config),
            config.ServerUrl, config.ControlRoomPassword, config.TeamUids, config.ExpectedHoeingPlayers,
            Profiles = config.RoomProfiles.Select(p => p is null ? null : new
            {
                p.Id, p.Name, p.Note, ServerUrl = config.GetRoomServerUrl(p), p.RoomCode, p.Password, p.TeamUids, p.ExpectedHoeingPlayers
            }).ToArray()
        })));

    private static string SafeRoomServerUrl(string? address) => RoomServerAddress.TryNormalize(address, out var server) ? server : "";

    private static bool WebRoomProfileIdsValid(AssistConfig config)
        => config.RoomProfiles.All(p => p is not null && !string.IsNullOrWhiteSpace(p.Id))
            && config.RoomProfiles.Select(p => p.Id).Distinct(StringComparer.Ordinal).Count() == config.RoomProfiles.Count;

    private bool CanEditWebRoomProfiles(AssistConfig config) => !_disposing && IsExecutorMode && ReferenceEquals(config, _config)
        && _configManager is not null && CanEditConfiguration && !RoomSwitchHasPendingRequest && !RoomSwitchBlocksRoomOperations
        && _roomProfileEditGate.CurrentCount != 0 && WebRoomProfileIdsValid(config);

    private bool CanStartWebRoomSwitch(AssistConfig config) => CanEditWebRoomProfiles(config) && !IsStandaloneMode
        && (ActiveRoomProfile is not null && GetAssistantRoomIdentity() is not null
            || ActiveRoomProfile is null && _signalRClient is null && string.IsNullOrEmpty(RoomCode));

    private static object ProjectWebRoomProfile(AssistConfig config, AssistantRoomProfile profile)
    {
        var active = config.ActiveRoomProfileId == profile.Id;
        var localId = EffectiveLocalRoomProfileId(config);
        var localKnown = !string.IsNullOrEmpty(localId) && config.RoomProfiles.Count(p => p is not null && p.Id == localId) == 1;
        var local = localId == profile.Id;
        return new
        {
            id = profile.Id, name = profile.Name, note = profile.Note,
            serverUrl = SafeRoomServerUrl(active ? config.ServerUrl : config.GetRoomServerUrl(profile)), roomCode = profile.RoomCode,
            memberUids = (active ? config.TeamUids : profile.TeamUids)?.ToArray() ?? [],
            expectedPlayers = active ? config.ExpectedHoeingPlayers : profile.ExpectedHoeingPlayers,
            hasPassword = !string.IsNullOrEmpty(active ? config.ControlRoomPassword : profile.Password),
            isLocalRoom = local, isActiveRoom = active, canRemove = localKnown && !local && !active,
            canEditCredentials = !local && !active
        };
    }

    private sealed record WebRoomDraft(string? ProfileId, string? Name, string? Note, string? ServerUrl, string? RoomCode,
        string? Password, List<string>? MemberUids, int ExpectedPlayers);

    private static List<string> NormalizeWebRoomUids(IEnumerable<string>? members)
    {
        if (members is null || members.Any(uid => uid is null)) throw new RoomProfileEditRejectedException("成员 UID 参数无效");
        return members.Select(uid => uid.Trim()).Where(uid => uid.Length > 0).Distinct(StringComparer.Ordinal).ToList();
    }

    private static AssistantRoomProfile ResolveWebRoomProfile(AssistConfig config, string? id)
    {
        if (string.IsNullOrWhiteSpace(id)) throw new RoomProfileEditRejectedException("请选择有效的房间档案");
        var matches = config.RoomProfiles.Where(p => p is not null && p.Id == id).ToArray();
        if (matches.Length != 1) throw new RoomProfileEditRejectedException("该房间档案已变化，请重新读取");
        return matches[0];
    }

    private static void RequireWebRoomProfileRevision(AssistConfig config, string? revision)
    {
        if (string.IsNullOrWhiteSpace(revision) || revision != RoomProfilesWebRevision(config))
            throw new RoomProfileEditRejectedException("房间档案修订已变化，请重新读取后操作");
        if (!WebRoomProfileIdsValid(config)) throw new RoomProfileEditRejectedException("房间档案身份不唯一，请先在执行端核对");
    }

    private async Task<RemoteCenterReply> ExecuteRoomProfilesWebOperationAsync(string requestId, string fingerprint,
        RemoteCenterRequest request, WebRoomRequestContext context)
    {
        if (!context.StillAuthorized() || _config is not { } config)
            return RemoteCenterReply.Rejected("原网页授权或执行端已变化，未执行");
        try
        {
            switch (request.Operation)
            {
                case "room.profiles.get":
                    return RemoteCenterReply.Ok(new
                    {
                        profiles = config.RoomProfiles.Where(p => p is not null).Select(p => ProjectWebRoomProfile(config, p)).ToArray(),
                        activeProfileId = config.ActiveRoomProfileId, localProfileId = EffectiveLocalRoomProfileId(config),
                        defaultServerUrl = SafeRoomServerUrl(config.ServerUrl), playerUid = config.PlayerUid, maxProfiles = 5,
                        canEdit = CanEditWebRoomProfiles(config), canCreate = CanEditWebRoomProfiles(config) && !IsStandaloneMode && config.RoomProfiles.Count < 5,
                        canSwitch = CanStartWebRoomSwitch(config), standaloneMode = IsStandaloneMode,
                        activeRoomConfirmed = ActiveRoomProfile is not null && GetAssistantRoomIdentity() is not null,
                        switchPending = RoomSwitchHasPendingRequest, switchBlocked = RoomSwitchBlocksRoomOperations,
                        switchOperationId = VisibleWebSwitchId(context.Prefixes)
                    }, RoomProfilesWebRevision(config));
                case "room.profiles.legacyCode":
                {
                    var uids = NormalizeWebRoomUids(Decode<List<string>>(request.PayloadJson));
                    return AssistantRoomDraft.HasValidMemberUids(uids, config.PlayerUid)
                        ? RemoteCenterReply.Ok(new { roomCode = AssistConfigManager.GenerateControlRoomCode(uids) })
                        : RemoteCenterReply.Rejected("请填写有效的成员 UID，并将本机玩家的 UID 加入名单");
                }
                case "room.profiles.save":
                case "room.profiles.create":
                {
                    RequireWebRoomProfileRevision(config, request.ExpectedRevision);
                    if (!CanEditWebRoomProfiles(config)) return RemoteCenterReply.Rejected("房间或本机设置暂不可编辑，请重新读取");
                    var create = request.Operation == "room.profiles.create";
                    if (create && IsStandaloneMode) return RemoteCenterReply.Rejected("请在联网模式建房");
                    var edit = Decode<WebRoomDraft>(request.PayloadJson);
                    var selected = edit.ProfileId is null or "" ? null : ResolveWebRoomProfile(config, edit.ProfileId);
                    if (edit.Name is null || edit.ServerUrl is null || edit.RoomCode is null || edit.MemberUids is null)
                        return RemoteCenterReply.Rejected("缺少房间档案必填字段");
                    var password = edit.Password;
                    if (string.IsNullOrEmpty(password) && selected is not null)
                        password = selected.Id == config.ActiveRoomProfileId ? config.ControlRoomPassword : selected.Password;
                    var draft = new AssistantRoomDraft(edit.Name, edit.Note ?? "", edit.ServerUrl, edit.RoomCode, password ?? "",
                        NormalizeWebRoomUids(edit.MemberUids), edit.ExpectedPlayers);
                    var saved = await SaveAssistantRoomProfileAsync(config, selected, draft, create, request.ExpectedRevision, context.StillAuthorized);
                    return RemoteCenterReply.Ok(ProjectWebRoomProfile(config, saved), RoomProfilesWebRevision(config), "房间档案已保存");
                }
                case "room.profiles.remove":
                {
                    RequireWebRoomProfileRevision(config, request.ExpectedRevision);
                    if (!CanEditWebRoomProfiles(config)) return RemoteCenterReply.Rejected("房间或本机设置暂不可编辑，请重新读取");
                    var selected = ResolveWebRoomProfile(config, Decode<string>(request.PayloadJson));
                    RemoveAssistantRoomProfile(config, selected, request.ExpectedRevision);
                    return RemoteCenterReply.Ok(new { id = selected.Id }, RoomProfilesWebRevision(config), "已移除本机档案；未删除服务器房间及历史");
                }
                case "room.switch.start":
                {
                    RequireWebRoomProfileRevision(config, request.ExpectedRevision);
                    if (!CanStartWebRoomSwitch(config) || _webRoomSwitch is not null)
                        return RemoteCenterReply.Rejected("当前连接、配置或切房状态不允许新的手动切房，请核对后操作");
                    var target = ResolveWebRoomProfile(config, Decode<string>(request.PayloadJson));
                    return BeginWebRoomSwitch(requestId, fingerprint, target.Id, context);
                }
                case "room.switch.status":
                case "room.switch.cancel":
                case "room.switch.recheck":
                {
                    var id = Decode<string>(request.PayloadJson);
                    if (!WebRoomPrefixes.Owns(id, context.Prefixes.Switch))
                        return RemoteCenterReply.Rejected("切房请求不属于原网页授权及执行端");
                    if (request.Operation == "room.switch.status") return ReadWebRoomSwitch(config, id);
                    if (request.Operation == "room.switch.cancel")
                    {
                        if (_webRoomSwitch is not { } current || current.Id != id || _changingAssistantRoom || RoomSwitchBlocksRoomOperations)
                            return RemoteCenterReply.Rejected("原请求不在可取消的等待阶段，请核对切房状态");
                        current.Cancellation.Cancel();
                        return new RemoteCenterReply { Status = "accepted", Message = "已请求取消原切房等待，终态仍需核对" };
                    }
                    if (RoomSwitchHasPendingRequest || _unconfirmedRoomSwitch?.RequestId != id)
                        return RemoteCenterReply.Rejected("原切房请求没有可核对的待查记录，或正在处理；不会重发");
                    return BeginWebRoomSwitchRecheck(requestId, fingerprint, id, context);
                }
                default:
                    return RemoteCenterReply.Rejected("不支持的网页房间操作");
            }
        }
        catch (RoomProfileEditRejectedException ex) { return RemoteCenterReply.Rejected(ex.WebMessage); }
        catch (JsonException) { return RemoteCenterReply.Rejected("房间操作参数格式无效"); }
        catch (RoomProfileCreateIncompleteException ex)
        {
            return new RemoteCenterReply { Status = "unknown", OperationId = requestId, Message = ex.Message,
                PayloadJson = JsonSerializer.Serialize(new { createdRoomCode = ex.RoomCode, profileSaved = false }) };
        }
        catch (Exception)
        {
            // A server or transport error may contain credentials. Keep desktop diagnostics on
            // the executor; never put raw network exceptions or saved passwords in the Web receipt.
            return RemoteCenterReply.Unknown(requestId, "执行端未能确认房间操作结果，请重新读取或在本机核对；不会自动重发");
        }
    }

    private sealed record WebRoomSwitchWork(string Id, CancellationTokenSource Cancellation);
    private WebRoomSwitchWork? _webRoomSwitch;
    private string? _lastWebRoomSwitchOperationId;

    private string? VisibleWebSwitchId(WebRoomPrefixes prefixes)
        => new[] { _webRoomSwitch?.Id, _unconfirmedRoomSwitch?.RequestId, _lastWebRoomSwitchOperationId }
            .FirstOrDefault(id => WebRoomPrefixes.Owns(id, prefixes.Switch));

    private RemoteCenterReply BeginWebRoomSwitch(string id, string fingerprint, string targetProfileId, WebRoomRequestContext context)
    {
        var accepted = BeginOperationReceipt(id, fingerprint);
        var job = new WebRoomSwitchWork(id, new CancellationTokenSource());
        _webRoomSwitch = job;
        _lastWebRoomSwitchOperationId = id;
        _ = CompleteCenterOperationAsync(id, async () =>
        {
            // The original transport still owns its short admission/reply room effect. The
            // transaction waits for that effect to be released just like any other room effect;
            // nothing in the Web handler retains it for the duration of the switch.
            try
            {
                var result = await SwitchAssistantRoomCoreAsync(targetProfileId, null, null, job.Cancellation.Token,
                    requestIdentity: id, stillAuthorized: context.StillAuthorized);
                if (result.Success) return RemoteCenterReply.Ok(new { activeProfileId = targetProfileId }, message: "执行端已确认目标房间");
                return result.Unknown ? RemoteCenterReply.Unknown(id, "切房结果待核对，未自动重试；请在执行端核对原请求")
                    : RemoteCenterReply.Rejected(job.Cancellation.IsCancellationRequested ? "原切房等待已取消，未确认目标房间"
                        : "切房未完成，请核对执行端连接、批次和档案状态");
            }
            catch (Exception) { return RemoteCenterReply.Unknown(id, "原切房操作结果未确认，请在执行端核对"); }
            finally { if (ReferenceEquals(_webRoomSwitch, job)) _webRoomSwitch = null; job.Cancellation.Dispose(); }
        });
        return accepted;
    }

    private bool WebSwitchRecoveryCleared(RemoteCenterReply? receipt)
        => receipt?.PayloadJson is { } json && JsonNode.Parse(json)?["recoveryCleared"]?.GetValue<bool>() == true;

    private RemoteCenterReply ReadWebRoomSwitch(AssistConfig config, string id)
    {
        var receipt = ReadCenterOperation(id);
        if (receipt is null && _unconfirmedRoomSwitch?.RequestId != id)
            return RemoteCenterReply.Rejected("执行端找不到原切房记录，结果未确认");
        var canCancel = _webRoomSwitch?.Id == id && !_changingAssistantRoom && !RoomSwitchBlocksRoomOperations;
        return RemoteCenterReply.Ok(new
        {
            operationId = id, operationStatus = receipt?.Status ?? "unknown", canCancel,
            canRecheck = !RoomSwitchHasPendingRequest && _unconfirmedRoomSwitch?.RequestId == id,
            switchPending = RoomSwitchHasPendingRequest, switchBlocked = RoomSwitchBlocksRoomOperations,
            activeProfileId = config.ActiveRoomProfileId, activeRoomConfirmed = ActiveRoomProfile is not null && GetAssistantRoomIdentity() is not null,
            recoveryCleared = WebSwitchRecoveryCleared(receipt)
        }, message: canCancel ? "原切房请求已受理，正在等待安全切换" : receipt?.Message ?? "切房结果待核对");
    }

    private RemoteCenterReply BeginWebRoomSwitchRecheck(string id, string fingerprint, string originalId, WebRoomRequestContext context)
    {
        var accepted = BeginOperationReceipt(id, fingerprint);
        _ = CompleteCenterOperationAsync(id, async () =>
        {
            if (!TryEnterConfigTransferOperation("room_switch", out var transfer))
                return RemoteCenterReply.Rejected(ConfigTransferBlockedMessage);
            using var operation = transfer;
            try
            {
                if (!await RecheckAssistantRoomSwitchAsync(originalId, context.StillAuthorized))
                    return RemoteCenterReply.Unknown(id, "原切房仍待核对，没有重发切房");
                // Recheck establishes that the old pending transaction can be released, NOT
                // whether a lost original cutover reply had been successful. Preserve that fact.
                if (ReadCenterOperation(originalId)?.Status == "unknown")
                {
                    var original = RemoteCenterReply.Unknown(originalId, "原切房历史结果仍未知；已核对并解除待查记录，未恢复自动流程");
                    original.PayloadJson = JsonSerializer.Serialize(new { recoveryCleared = true });
                    _centerOperationStore.Complete(originalId, original);
                    _centerOperations.TryRemove(originalId, out _);
                }
                _lastWebRoomSwitchOperationId = originalId;
                return RemoteCenterReply.Ok(new { originalOperationId = originalId, recoveryCleared = true },
                    message: "原待查记录已解除；未自动重发切房或恢复流程");
            }
            catch (Exception) { return RemoteCenterReply.Unknown(id, "原切房核对结果未确认，请在执行端核对实际状态"); }
        });
        return accepted;
    }
}
