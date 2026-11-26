using System.IO;
using System.Windows;
using System.Windows.Input;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private sealed record RoomFollowOwner(string Uid, string Instance, string Machine)
    {
        internal bool Owns(RoomFollowGrant grant) => grant.Invitation.Uid == Uid
            && grant.Invitation.InstanceId == Instance && grant.Invitation.MachineId == Machine;
    }

    private sealed class RoomFollowing(AssistConfig original, string originalRoom, AppPage originalPage, RoomFollowInvitation invitation)
    {
        internal AssistConfig Original { get; } = original;
        internal string OriginalRoom { get; } = originalRoom;
        internal AppPage OriginalPage { get; } = originalPage;
        internal RoomFollowInvitation Invitation { get; set; } = invitation;
        internal RoomFollowSubscriber? Subscriber;
        internal RoomFollowSnapshot? LastSnapshot;
        internal RoomFollowProjection Projection = RoomFollowProjection.Create(original, null, false);
        internal HashSet<RoomFollowRoomCredential> RejectedCredentials { get; } = [];
    }

    // The runtime and its projection are owned by the UI dispatcher. Background callbacks only
    // enqueue work, checking the original subscription/session again before applying anything.
    private volatile RoomFollowing? _roomFollowing;
    private readonly Dictionary<string, RoomFollowObserverSession> _followRooms = new(StringComparer.Ordinal);
    // The primary is a genuine normally authenticated observer, but it is intentionally NOT
    // installed in the ordinary command/desktop-control client slot while following is read-only.
    private SignalRClient? _roomFollowPrimaryClient;
    private readonly Dictionary<string, RoomFollowPublisher> _followPublishers = new(StringComparer.Ordinal);
    private RoomFollowPreferences _roomFollowPreferences = new();
    private RoomFollowInvitation? _roomFollowInvitation;
    private RoomFollowOwner? _roomFollowOwner;
    private RoomFollowGrant[] _roomFollowGrants = [];
    private bool _roomFollowInitialized;
    private volatile bool _roomFollowBusy, _roomFollowRestoring;
    private bool _roomFollowRestoreRequested;
    private string _roomFollowStatus = "房间跟随未启用";
    private string _roomFollowError = "";

    public bool IsRoomFollowing => _roomFollowing is not null;
    public bool RoomFollowSharingEnabled => _roomFollowPreferences.ShareEnabled && IsExecutorMode;
    public bool RoomFollowBusy => _roomFollowBusy;
    public bool CanOpenRoomFollow => _roomFollowInitialized && !_disposing;
    private bool CanStartRoomFollowSharing => CanOpenRoomFollow && !_roomFollowBusy && !_configTransferInProgress
        && !IsStandaloneMode && CurrentRoomFollowOwner() is not null;
    public bool CanShareRoomFollow => CanStartRoomFollowSharing || CanOpenRoomFollow && !_roomFollowBusy && RoomFollowSharingEnabled;
    public bool CanGenerateRoomFollowGrant => CanStartRoomFollowSharing && RoomFollowSharingEnabled;
    public bool CanChangeRoomFollowInvitation => CanOpenRoomFollow && !_roomFollowBusy && IsObserverMode && !_configTransferInProgress;
    public bool CanEnableRoomFollow => CanChangeRoomFollowInvitation && (IsRoomFollowing || _roomFollowInvitation is not null
        && _config is { } config && RoomFollowProjection.HasObserverIdentity(config));
    public bool CanChooseRoomView => CanEditConfiguration;
    public string RoomFollowStatus => _roomFollowStatus;
    public string RoomFollowError => _roomFollowError;
    public string RoomFollowInvitationSummary => _roomFollowInvitation is { } invitation
        ? $"目标 UID {invitation.Uid} · 实例 {ShortRoomFollowId(invitation.InstanceId)} · 机器 {ShortRoomFollowId(invitation.MachineId)}\n"
            + (invitation.Local ? "本机同 Windows 用户邀请" : "HTTPS 中继：" + invitation.ServerUrl)
        : "尚未保存邀请";
    public string RoomFollowNoticeText => IsRoomFollowing ? string.Join("\n", new[] { RoomFollowStatus, RoomFollowError }
        .Where(text => !string.IsNullOrWhiteSpace(text))) : "";
    public string RoomFollowEntryText => IsRoomFollowing ? "房间跟随 · 已开启" : RoomFollowSharingEnabled ? "房间跟随 · 共享已开启" : "房间跟随";
    public string RoomFollowDefaultRelay => SafeRoomServerUrl((_roomFollowing?.Original ?? _config)?.ServerUrl);

    public sealed record RoomFollowGrantChoice(string Id, string Label);
    public IReadOnlyList<RoomFollowGrantChoice> RoomFollowGrantChoices => _roomFollowGrants.Select(grant => new RoomFollowGrantChoice(
        grant.Invitation.ChannelId, (grant.Invitation.Local ? "本机" : grant.Invitation.ServerUrl)
        + " · " + ShortRoomFollowId(grant.Invitation.ChannelId))).ToArray();

    private static string ShortRoomFollowId(string id) => id[..Math.Min(8, id.Length)];
    private RoomFollowOwner? CurrentRoomFollowOwner() => _config is { ObserverMode: false } config ? RoomFollowIdentity(config) : null;

    private static RoomFollowOwner? RoomFollowIdentity(AssistConfig? config)
        => config is not null && RoomFollowFormat.IsIdentity(config.PlayerUid)
            && RoomFollowFormat.IsIdentity(config.ClientInstanceId) && RoomFollowFormat.IsIdentity(HostMachineIdentity.Current)
            ? new(config.PlayerUid, config.ClientInstanceId, HostMachineIdentity.Current) : null;

    private void NotifyRoomFollowBindings()
    {
        OnPropertyChanged(nameof(IsRoomFollowing));
        OnPropertyChanged(nameof(RoomFollowSharingEnabled));
        OnPropertyChanged(nameof(RoomFollowBusy));
        OnPropertyChanged(nameof(CanOpenRoomFollow));
        OnPropertyChanged(nameof(CanShareRoomFollow));
        OnPropertyChanged(nameof(CanGenerateRoomFollowGrant));
        OnPropertyChanged(nameof(CanChangeRoomFollowInvitation));
        OnPropertyChanged(nameof(CanEnableRoomFollow));
        OnPropertyChanged(nameof(RoomFollowInvitationSummary));
        OnPropertyChanged(nameof(RoomFollowGrantChoices));
        OnPropertyChanged(nameof(RoomFollowEntryText));
        OnPropertyChanged(nameof(RoomFollowStatus));
        OnPropertyChanged(nameof(RoomFollowError));
        OnPropertyChanged(nameof(RoomFollowNoticeText));
        OnPropertyChanged(nameof(RoomViewNoticeText));
        OnPropertyChanged(nameof(CanChooseRoomView));
        OnPropertyChanged(nameof(CanEditConfiguration));
        OnPropertyChanged(nameof(CanUseAssistantRoomMemberActions));
        NotifyRoomOrchestrationModeProperties();
        CommandManager.InvalidateRequerySuggested();
    }

    private void SetRoomFollowError(string message)
    {
        _roomFollowError = message;
        NotifyRoomFollowBindings();
    }

    private bool SaveRoomFollowPreferences(RoomFollowPreferences preferences)
    {
        try
        {
            new RoomFollowPreferencesStore().Save(preferences);
            _roomFollowPreferences = preferences;
            return true;
        }
        catch
        {
            SetRoomFollowError("房间跟随设置未能保存，请稍后重试。原房间设置未改变。");
            return false;
        }
    }

    private async Task InitializeRoomFollowAsync()
    {
        _roomFollowInitialized = true;
        try
        {
            _roomFollowPreferences = new RoomFollowPreferencesStore().Load();
            _roomFollowInvitation = _roomFollowPreferences.Invitation.Length == 0 ? null
                : RoomFollowInvitation.Parse(_roomFollowPreferences.Invitation);
            if (CurrentRoomFollowOwner() is { } owner)
            {
                _roomFollowOwner = owner;
                ReloadRoomFollowGrants(owner);
                if (_roomFollowPreferences.FollowEnabled)
                {
                    var off = _roomFollowPreferences with { FollowEnabled = false };
                    _roomFollowPreferences = off;
                    SaveRoomFollowPreferences(off);
                }
                if (_roomFollowPreferences.ShareEnabled && !IsStandaloneMode) StartRoomFollowPublishers();
                else if (_roomFollowPreferences.ShareEnabled) await StopRoomFollowSharingAsync(revoke: false);
            }
            else
            {
                if (_roomFollowPreferences.ShareEnabled)
                {
                    // Role may have changed since the previous process. Old grants never get
                    // an implicit publisher again just because the stored checkbox was on.
                    _roomFollowOwner = RoomFollowIdentity(_config);
                    await StopRoomFollowSharingAsync(revoke: true);
                    _roomFollowOwner = null;
                    _roomFollowGrants = [];
                }
                if (_roomFollowPreferences.FollowEnabled) await SetRoomFollowingEnabledAsync(true);
            }
        }
        catch
        {
            _roomFollowPreferences = new();
            _roomFollowInvitation = null;
            await StopRoomFollowPublishersAsync();
            _roomFollowError = "房间跟随设置或授权无法读取。本次保持关闭，原文件未被覆盖。";
        }
        RefreshRoomFollowStatus();
        NotifyRoomFollowBindings();
    }

    private void ReloadRoomFollowGrants(RoomFollowOwner owner) => _roomFollowGrants = new RoomFollowGrantStore().Load()
        .Where(owner.Owns).ToArray();

    private RoomFollowGrant[] EditRoomFollowGrants(RoomFollowOwner owner, Func<IEnumerable<RoomFollowGrant>, IEnumerable<RoomFollowGrant>> edit)
    {
        using var writing = ConfigFileWriteGate.Acquire(Path.Combine(AssistantDataDirectory.Root, "room-follow-grants.protected"));
        var store = new RoomFollowGrantStore();
        var all = store.Load();
        var changed = all.Where(g => !owner.Owns(g)).Concat(edit(all.Where(owner.Owns))).ToArray();
        store.Save(changed);
        return changed.Where(owner.Owns).ToArray();
    }

    internal async Task SetRoomFollowSharingEnabledAsync(bool enabled)
    {
        if (_roomFollowBusy || _disposing || !IsExecutorMode) return;
        if (enabled && !CanStartRoomFollowSharing)
        {
            SetRoomFollowError("共享需要联网设置和已配置的执行端身份。");
            return;
        }
        _roomFollowBusy = true;
        _roomFollowError = "";
        NotifyRoomFollowBindings();
        try
        {
            if (!enabled) { await StopRoomFollowSharingAsync(revoke: false); return; }
            if (CurrentRoomFollowOwner() is not { } owner) return;
            _roomFollowOwner = owner;
            ReloadRoomFollowGrants(owner);
            if (SaveRoomFollowPreferences(_roomFollowPreferences with { ShareEnabled = true, FollowEnabled = false }))
                StartRoomFollowPublishers();
        }
        catch { SetRoomFollowError("无法读取或启用跟随授权，未确认共享。"); }
        finally { _roomFollowBusy = false; RefreshRoomFollowStatus(); NotifyRoomFollowBindings(); }
    }

    internal async Task<string?> GenerateRoomFollowInvitationAsync(bool local, string relay)
    {
        if (!CanGenerateRoomFollowGrant || CurrentRoomFollowOwner() is not { } owner) return null;
        _roomFollowError = "";
        try
        {
            var grant = RoomFollowGrant.Create(relay, local, owner.Uid, owner.Instance, owner.Machine);
            var encoded = grant.Invitation.Encode();
            _roomFollowGrants = EditRoomFollowGrants(owner, grants => grants.Append(grant));
            StartRoomFollowPublishers();
            RefreshRoomFollowStatus();
            NotifyRoomFollowBindings();
            return encoded;
        }
        catch (FormatException) { SetRoomFollowError("无法生成邀请：远程中继必须是有效且不含凭据的 HTTPS 地址。"); }
        catch { SetRoomFollowError("无法保存跟随授权，未生成新的可用邀请。"); }
        await Task.CompletedTask;
        return null;
    }

    internal string? CopyRoomFollowInvitation(string id)
        => CurrentRoomFollowOwner() is { } owner && _roomFollowGrants.SingleOrDefault(g => owner.Owns(g) && g.Invitation.ChannelId == id) is { } grant
            ? grant.Invitation.Encode() : null;

    internal async Task RevokeRoomFollowGrantAsync(string id)
    {
        if (_roomFollowBusy || _disposing || CurrentRoomFollowOwner() is not { } owner) return;
        _roomFollowBusy = true;
        _roomFollowError = "";
        NotifyRoomFollowBindings();
        try
        {
            if (_followPublishers.Remove(id, out var publisher)) await DisposeRoomFollowPublisherAsync(publisher);
            _roomFollowGrants = EditRoomFollowGrants(owner, grants => grants.Where(g => g.Invitation.ChannelId != id));
        }
        catch
        {
            // A failed durable revoke must not silently restart the old channel.
            await StopRoomFollowSharingAsync(revoke: false);
            SetRoomFollowError("已停止本次共享，但邀请撤销未能保存；请重试撤销，勿重新开启共享。");
        }
        finally { _roomFollowBusy = false; RefreshRoomFollowStatus(); NotifyRoomFollowBindings(); }
    }

    private void StartRoomFollowPublishers()
    {
        if (_disposing || !_roomFollowPreferences.ShareEnabled || IsStandaloneMode || _config is not { ObserverMode: false } config
            || CurrentRoomFollowOwner() != _roomFollowOwner) return;
        foreach (var grant in _roomFollowGrants)
        {
            if (_followPublishers.ContainsKey(grant.Invitation.ChannelId)) continue;
            var publisher = new RoomFollowPublisher(grant, config.BypassSystemProxy);
            _followPublishers.Add(grant.Invitation.ChannelId, publisher);
            _ = publisher.StartAsync(); // Start schedules the transport, not a relay acknowledgement.
        }
        PublishRoomFollowSnapshot();
    }

    // Called on view/profile changes and actual room connection transitions, never by status polling.
    private void PublishRoomFollowSnapshot()
    {
        if (!_roomFollowInitialized || _disposing) return;
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(PublishRoomFollowSnapshot));
            return;
        }
        if (_roomFollowRestoreRequested && !IsRoomFollowing && IsObserverMode)
        {
            RefreshRoomFollowStatus();
            OnPropertyChanged(nameof(RoomFollowStatus));
        }
        if (!_roomFollowPreferences.ShareEnabled) return;
        if (_configTransferInProgress)
        {
            foreach (var publisher in _followPublishers.Values) publisher.Update(null);
            return;
        }
        if (CurrentRoomFollowOwner() != _roomFollowOwner || IsStandaloneMode || !IsExecutorMode)
        {
            // A config/role replacement (including the normal settings path) cannot publish
            // under the previous identity. Stop the workers before accepting any new snapshot.
            _ = StopRoomFollowSharingAsync(revoke: CurrentRoomFollowOwner() != _roomFollowOwner);
            return;
        }
        if (_config is not { } config || _followPublishers.Count == 0) return;
        try
        {
            var confirmed = GetAssistantRoomIdentity() is not null && config.ActiveRoomProfile is { } active
                && active.RoomCode == RoomCode && RoomServerAddress.Same(config.GetRoomServerUrl(active), config.ServerUrl);
            var snapshot = RoomFollowProjection.Capture(config, HostMachineIdentity.Current, confirmed);
            foreach (var publisher in _followPublishers.Values) publisher.Update(snapshot);
            if (_roomFollowError == "当前房间档案格式不适合共享，本次状态未发布。") SetRoomFollowError("");
        }
        catch
        {
            foreach (var publisher in _followPublishers.Values)
                try { publisher.Update(null); } catch { }
            SetRoomFollowError("当前房间档案格式不适合共享，本次状态未发布。");
        }
    }

    private static async Task DisposeRoomFollowPublisherAsync(RoomFollowPublisher publisher)
    {
        try { publisher.Update(null); } catch { }
        try { await publisher.DisposeAsync().ConfigureAwait(false); }
        catch { /* No capability or transport exception is written to the log. */ }
    }

    private Task StopRoomFollowPublishersAsync()
    {
        var publishers = _followPublishers.Values.ToArray();
        _followPublishers.Clear();
        return Task.WhenAll(publishers.Select(DisposeRoomFollowPublisherAsync));
    }

    private async Task StopRoomFollowSharingAsync(bool revoke)
    {
        // The in-memory opt-in is cleared even if durable storage fails. No new worker can
        // start while that failure is presented to the user.
        var off = _roomFollowPreferences with { ShareEnabled = false };
        _roomFollowPreferences = off;
        var stopping = StopRoomFollowPublishersAsync();
        SaveRoomFollowPreferences(off);
        if (revoke && _roomFollowOwner is { } owner)
        {
            try { _roomFollowGrants = EditRoomFollowGrants(owner, _ => []); }
            catch { SetRoomFollowError("共享已停止，但旧身份的跟随授权未能永久撤销，请核对本机授权。 "); }
        }
        await stopping;
        RefreshRoomFollowStatus();
        NotifyRoomFollowBindings();
    }

    internal async Task ReplaceRoomFollowInvitationAsync(string text)
    {
        if (!CanChangeRoomFollowInvitation) return;
        RoomFollowInvitation? invitation;
        try { invitation = string.IsNullOrWhiteSpace(text) ? null : RoomFollowInvitation.Parse(text); }
        catch (FormatException) { SetRoomFollowError("邀请格式无效，未替换原邀请。"); return; }
        if (invitation is { Local: true } && invitation.MachineId != HostMachineIdentity.Current)
        { SetRoomFollowError("本机邀请只支持同一台机器、同一个 Windows 用户；跨机请使用 HTTPS 邀请。"); return; }
        _roomFollowBusy = true;
        _roomFollowError = "";
        NotifyRoomFollowBindings();
        try
        {
            var next = _roomFollowPreferences with { Invitation = invitation?.Encode() ?? "", FollowEnabled = invitation is not null && IsRoomFollowing };
            if (!SaveRoomFollowPreferences(next)) return;
            _roomFollowInvitation = invitation;
            if (_roomFollowing is { } runtime)
            {
                if (invitation is null) await StopRoomFollowingCoreAsync();
                else
                {
                    runtime.Invitation = invitation;
                    runtime.LastSnapshot = null;
                    runtime.RejectedCredentials.Clear();
                    await RestartRoomFollowSubscriberAsync(runtime);
                }
            }
        }
        catch { SetRoomFollowError("邀请已更改，但新的跟随连接尚未确认。可关闭后重试。"); }
        finally { _roomFollowBusy = false; RefreshRoomFollowStatus(); NotifyRoomFollowBindings(); }
    }

    internal async Task SetRoomFollowingEnabledAsync(bool enabled)
    {
        if (_roomFollowBusy || _disposing || !IsObserverMode) return;
        _roomFollowBusy = true;
        _roomFollowError = "";
        NotifyRoomFollowBindings();
        try
        {
            if (!enabled)
            {
                var off = _roomFollowPreferences with { FollowEnabled = false };
                _roomFollowPreferences = off;
                SaveRoomFollowPreferences(off);
                await StopRoomFollowingCoreAsync();
                return;
            }
            if (_roomFollowing is not null) return;
            if (_roomFollowInvitation is not { } invitation || _config is not { } original || _configManager is null
                || !RoomFollowProjection.HasObserverIdentity(original))
            { SetRoomFollowError("请先保存跟随邀请，并在联网监控模式配置本机身份。"); return; }
            if (invitation.Local && invitation.MachineId != HostMachineIdentity.Current)
            { SetRoomFollowError("此本机邀请不属于当前机器，请替换邀请。"); return; }
            if (_commandExecutor is not null || _processMonitor is not null || AssistantRoomHasBatch())
            { SetRoomFollowError("本机执行运行时尚未退出，请先确认已处于监控模式再开启跟随。"); return; }

            RoomFollowing? runtime = null;
            lock (_assistantRoomEffectsGate)
            lock (_configTransferGate)
            {
                if (ReferenceEquals(_config, original) && _assistantRoomEffects == 0 && _configTransferOperations == 0 && !_configTransferInProgress
                    && !RoomSwitchHasPendingRequest && !RoomSwitchBlocksRoomOperations && _roomProfileEditGate.CurrentCount != 0
                    && Volatile.Read(ref _refreshRunning) == 0 && Volatile.Read(ref _retryRunning) == 0
                    && Volatile.Read(ref _settingsReconnectPending) == 0 && _pendingControlClient is null
                    && _connectionAttemptGate.CurrentCount != 0 && _pendingCommandResults.Count == 0 && _pendingCenterReplies.IsEmpty
                    && _resetRequestGate.CurrentCount != 0 && _localResetGate.CurrentCount != 0 && Volatile.Read(ref _ordinaryStopInProgress) == 0)
                {
                    runtime = new RoomFollowing(RoomFollowProjection.CopyOriginal(original), RoomCode, CurrentPage, invitation);
                    _roomFollowing = runtime;
                }
            }
            if (runtime is null) { SetRoomFollowError("原房间仍有连接或操作正在处理，请等其结束后开启跟随。"); return; }
            _roomFollowRestoreRequested = false;
            if (!SaveRoomFollowPreferences(_roomFollowPreferences with { FollowEnabled = true, ShareEnabled = false }))
            { _roomFollowing = null; return; }

            // The guarded runtime projection is never published through the configuration writer.
            // Retire the former config object too: a callback still holding it after follow-off
            // cannot overwrite the frozen original settings restored below.
            _configManager.SetRoomFollowProjectionActive(true);
            original.IsRoomFollowProjection = true;
            StopSignalRRecovery();
            _retryTimer?.Dispose(); _retryTimer = null;
            StopAllRoomSpectators();
            StopRoomOrchestrationMonitor();
            var old = _signalRClient;
            _roomFollowPrimaryClient = null;
            _signalRClient = null;
            _controlAuthority.Revoke();
            old?.RetireForReplacement();
            try
            {
                ApplyRoomFollowSnapshot(runtime, null);
                CurrentPage = AppPage.Home;
            }
            finally { if (old is not null) await DisposeFollowSignalRAsync(old); }
            await RestartRoomFollowSubscriberAsync(runtime);
        }
        catch
        {
            SetRoomFollowError("房间跟随未能启动，未确认连接；已恢复本机原设置。");
            var off = _roomFollowPreferences with { FollowEnabled = false };
            _roomFollowPreferences = off;
            SaveRoomFollowPreferences(off);
            await StopRoomFollowingCoreAsync();
        }
        finally { _roomFollowBusy = false; RefreshRoomFollowStatus(); NotifyRoomFollowBindings(); }
    }

    private async Task RestartRoomFollowSubscriberAsync(RoomFollowing runtime)
    {
        var previous = runtime.Subscriber;
        runtime.Subscriber = null;
        ApplyRoomFollowSnapshot(runtime, null);
        if (previous is not null) await DisposeRoomFollowSubscriberAsync(previous);
        if (_disposing || !ReferenceEquals(_roomFollowing, runtime)) return;
        var subscriber = new RoomFollowSubscriber(runtime.Invitation, runtime.Original.BypassSystemProxy);
        runtime.Subscriber = subscriber;
        subscriber.Changed += snapshot => QueueRoomFollowUi(() =>
        {
            if (!ReferenceEquals(_roomFollowing, runtime) || !ReferenceEquals(runtime.Subscriber, subscriber)) return;
            ApplyRoomFollowSnapshot(runtime, snapshot);
        });
        await subscriber.StartAsync();
    }

    private void QueueRoomFollowUi(Action apply)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (_disposing || dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.BeginInvoke(new Action(() =>
        {
            if (_disposing) return;
            try { apply(); }
            catch
            {
                // A view callback failing after a new snapshot cannot leave the previous login
                // marked current. Fence all observer clients until a new snapshot/refresh.
                if (_roomFollowing is { } runtime)
                {
                    runtime.Projection.Invalidate();
                    _ = StopFollowRoomsAsync();
                    _roomFollowPrimaryClient = null;
                    _roomCode = "";
                    _isConnected = false;
                    Members.Clear();
                    RemoteCenterTargets.Clear();
                    OnPropertyChanged(nameof(RoomCode));
                    OnPropertyChanged(nameof(IsConnected));
                    NotifyRoomProfiles();
                    RefreshRoomFollowStatus();
                }
                SetRoomFollowError("房间跟随视图更新失败，登录未确认；请刷新或关闭跟随。");
            }
        }));
    }

    private void ApplyRoomFollowSnapshot(RoomFollowing runtime, RoomFollowSnapshot? snapshot)
    {
        if (_disposing || !ReferenceEquals(_roomFollowing, runtime)) return;
        if (snapshot is not null) runtime.LastSnapshot = snapshot;
        runtime.Projection = RoomFollowProjection.Create(runtime.Original, runtime.LastSnapshot, snapshot is not null);
        _config = runtime.Projection.Runtime;
        SyncFollowRoomSessions();
        UpdateRoomFollowDisplay(profilesChanged: true);
        if (snapshot is not null) CurrentPage = AppPage.Home;
    }

    private void SyncFollowRoomSessions()
    {
        if (_roomFollowing is not { } runtime || _disposing) return;
        var projection = runtime.Projection;
        var desired = projection.DesiredConnections().ToHashSet(StringComparer.Ordinal);
        foreach (var id in _followRooms.Keys.ToArray())
        {
            var session = _followRooms[id];
            if (session.Capture().AuthenticationRequired) runtime.RejectedCredentials.Add(session.Credential);
            if (desired.Contains(id) && projection.Credentials[id] == session.Credential) continue;
            _followRooms.Remove(id);
            RemoveSpectatorMemberCacheForProfile(id);
            _ = DisposeFollowRoomAsync(session);
        }
        foreach (var id in desired)
        {
            if (_followRooms.ContainsKey(id) || runtime.RejectedCredentials.Contains(projection.Credentials[id])) continue;
            var session = new RoomFollowObserverSession(projection.Credentials[id]);
            _followRooms.Add(id, session);
            session.Changed += () => QueueRoomFollowUi(() =>
            {
                if (!ReferenceEquals(_roomFollowing, runtime) || !_followRooms.TryGetValue(id, out var current) || !ReferenceEquals(current, session)) return;
                if (session.Capture().AuthenticationRequired) runtime.RejectedCredentials.Add(session.Credential);
                UpdateRoomFollowDisplay(profilesChanged: false);
            });
            session.Start();
        }
    }

    private IReadOnlyList<ControlRoomPlayer> GetFollowRoomPlayers(string profileId) => _followRooms.TryGetValue(profileId, out var room)
        ? room.Capture().Players : [];

    private void UpdateRoomFollowDisplay(bool profilesChanged)
    {
        if (_roomFollowing is not { } runtime || _disposing) return;
        var projection = runtime.Projection;
        var target = projection.ConfirmedTargetId;
        var primary = _followRooms.TryGetValue(target, out var session) ? session.Capture() : null;
        var client = primary?.Client is { IsControlRoomReady: true } ready ? ready : null;
        projection.SetActualLogin(client is null ? null : target);
        var newRoom = projection.Runtime.EffectiveRoomCode;
        var changed = !ReferenceEquals(client, _roomFollowPrimaryClient) || RoomCode != newRoom;
        if (changed)
        {
            Interlocked.Increment(ref _controlContextVersion);
            _controlAuthority.Revoke();
            _roomFollowPrimaryClient = client;
            _roomCode = newRoom;
            Members.Clear();
            RemoteCenterTargets.Clear();
            ResetRoomMemberRevision();
            _lastLoggedProgress = "";
            OnPropertyChanged(nameof(RoomCode));
        }
        if (client is not null && projection.Runtime.ActiveRoomProfile is { } active)
        {
            var players = primary!.Players;
            var keep = players.Select(p => p.PlayerUid).ToHashSet(StringComparer.Ordinal);
            foreach (var stale in Members.Where(m => !keep.Contains(m.PlayerUid)).ToArray()) Members.Remove(stale);
            foreach (var player in players)
            {
                var member = Members.FirstOrDefault(m => m.PlayerUid == player.PlayerUid);
                if (member is null)
                {
                    var (file, ring) = AvatarPool[Members.Count % AvatarPool.Length];
                    member = new MemberViewModel { PlayerUid = player.PlayerUid, AvatarPath = $"pack://application:,,,/Assets/Images/{file}.png", AvatarRing = ring };
                    ApplyControlRoomPlayerToMember(member, player, true, active.Id, active.Name, IsViewingAllRooms);
                    Members.Add(member);
                }
                else ApplyControlRoomPlayerToMember(member, player, true, active.Id, active.Name, IsViewingAllRooms);
            }
        }
        else if (Members.Count > 0) Members.Clear();
        if (IsConnected != (client is not null)) IsConnected = client is not null;
        if (profilesChanged || changed) NotifyRoomProfiles();
        else RebuildDisplayedMembers();
        if (changed) NotifyFollowRoomChanged();
        MemberOnlineChanged?.Invoke();
        RefreshRoomFollowStatus();
        NotifyRoomFollowBindings();
    }

    private void NotifyFollowRoomChanged()
    {
        try { AssistantRoomChanged?.Invoke(); RefreshCompleted?.Invoke(); }
        catch { /* The follow status still reflects the actual authenticated room. */ }
    }

    private void RefreshRoomFollowStatus()
    {
        if (_roomFollowing is not { } runtime)
        {
            _roomFollowStatus = RoomFollowSharingEnabled
                ? $"共享已开启（{_roomFollowGrants.Length} 份邀请）；仅发布房间元数据，中继/订阅端在线状态未确认。"
                : IsObserverMode
                    ? _roomFollowRestoreRequested
                        ? _roomFollowRestoring ? "跟随已关闭，正在恢复本机房间连接…"
                            : "跟随已关闭；本机设置和页面已恢复。" + (RoomCode.Length == 0 ? "本机原先未选房间。"
                                : GetAssistantRoomIdentity() is not null ? "当前登录已确认。" : "当前登录未确认（仍按普通连接流程恢复）。")
                        : "跟随未开启；本机房间和页面保持独立。"
                    : "房间元数据共享未开启。";
            return;
        }
        var projection = runtime.Projection;
        if (!projection.Fresh || projection.Snapshot is null)
        {
            _roomFollowStatus = "等待执行端共享状态；跟随登录未确认。" + (runtime.LastSnapshot is null ? "" : " 档案为上次收到的元数据。")
                + " 可关闭跟随恢复本机设置。";
            return;
        }
        var target = projection.ConfirmedTargetId;
        if (target.Length == 0)
        {
            _roomFollowStatus = "已收到档案，但执行端登录尚未确认（离线或切房中），正在等待。";
            return;
        }
        var missing = projection.AuthenticationNeededIds().Concat(projection.DesiredConnections().Where(id =>
            runtime.RejectedCredentials.Contains(projection.Credentials[id]))).ToHashSet(StringComparer.Ordinal);
        var active = projection.Runtime.RoomProfiles.First(p => p.Id == target);
        var observation = _followRooms.TryGetValue(target, out var room) ? room.Capture() : null;
        _roomFollowStatus = missing.Contains(target) ? "登录待授权：" + active.Name
            : projection.DecideLogin(projection.Runtime.ActiveRoomProfileId) == RoomFollowLoginDecision.Current && observation?.Client is not null
                ? "跟随中：已用本机身份登录「" + active.Name + "」。"
                : observation?.Error is { Length: > 0 } error ? "跟随登录未确认：" + error
                : "正在使用本机已保存的凭据进入「" + active.Name + "」…";
        if (missing.Count > 0)
        {
            var names = projection.Runtime.RoomProfiles.Where(p => missing.Contains(p.Id)).Select(p =>
                p.Name + "（" + (string.IsNullOrEmpty(p.ServerUrl) ? "服务器未确认" : p.ServerUrl) + " / " + p.RoomCode + "）");
            _roomFollowStatus += "\n需普通入房授权：" + string.Join("、", names)
                + "。请关闭跟随，在「管理房间」中添加或核对本人凭据并手动确认入房，再重新开启跟随。";
        }
        var pending = projection.DesiredConnections().Where(id => id != target && !missing.Contains(id)
            && (!_followRooms.TryGetValue(id, out var other) || other.Capture().Client is null)).ToHashSet(StringComparer.Ordinal);
        if (pending.Count > 0) _roomFollowStatus += "\n其他视图房间尚未确认连接：" + string.Join("、",
            projection.Runtime.RoomProfiles.Where(p => pending.Contains(p.Id)).Select(p => p.Name)) + "。";
    }

    private async Task<bool> RefreshRoomFollowingAsync()
    {
        if (_roomFollowing is not { } runtime || _roomFollowBusy || _disposing) return false;
        _roomFollowBusy = true;
        _roomFollowError = "";
        try
        {
            if (!runtime.Projection.Fresh) await RestartRoomFollowSubscriberAsync(runtime);
            else
            {
                foreach (var id in _followRooms.Keys.ToArray())
                {
                    var session = _followRooms[id];
                    if (session.Capture().AuthenticationRequired) { runtime.RejectedCredentials.Add(session.Credential); continue; }
                    _followRooms.Remove(id);
                    RemoveSpectatorMemberCacheForProfile(id);
                    _ = DisposeFollowRoomAsync(session);
                }
                SyncFollowRoomSessions();
                UpdateRoomFollowDisplay(profilesChanged: false);
            }
        }
        catch { SetRoomFollowError("跟随连接刷新未确认，请关闭后重试。"); }
        finally { _roomFollowBusy = false; RefreshRoomFollowStatus(); NotifyRoomFollowBindings(); }
        return IsConnected && _roomFollowPrimaryClient is { IsControlRoomReady: true };
    }

    private async Task StopRoomFollowingCoreAsync(bool restoreConnection = true)
    {
        if (_roomFollowing is not { } runtime) return;
        _roomFollowRestoring = true;
        _roomFollowRestoreRequested = restoreConnection;
        var subscriber = runtime.Subscriber;
        runtime.Subscriber = null;
        var ordinary = _signalRClient;
        ordinary?.RetireForReplacement();
        var stopping = StopFollowRoomsAsync();
        try
        {
            _roomFollowPrimaryClient = null;
            _signalRClient = null;
            _controlAuthority.Revoke();
            Interlocked.Increment(ref _controlContextVersion);
            _config = runtime.Original;
            _roomCode = runtime.OriginalRoom;
            _roomFollowing = null;
            Members.Clear();
            RemoteCenterTargets.Clear();
            ResetRoomMemberRevision();
            IsConnected = false;
            CurrentPage = runtime.OriginalPage;
            OnPropertyChanged(nameof(RoomCode));
            NotifyRoomProfiles();
            NotifyModeBindings();
            NotifyFollowRoomChanged();
            if (subscriber is not null) await DisposeRoomFollowSubscriberAsync(subscriber);
            if (ordinary is not null) await DisposeFollowSignalRAsync(ordinary);
            await stopping;
            if (!_disposing && restoreConnection && !IsStandaloneMode && RoomCode.Length > 0)
            {
                await ConnectSignalRAsync(); // The original saved observer settings, NOT the executor's profile ids.
                NotifyFollowRoomChanged();
            }
        }
        finally
        {
            _configManager?.SetRoomFollowProjectionActive(false);
            _roomFollowRestoring = false;
            NotifyRoomFollowBindings();
        }
    }

    private Task StopFollowRoomsAsync()
    {
        var rooms = _followRooms.Values.ToArray();
        _followRooms.Clear();
        _spectatorMemberCache.Clear();
        return Task.WhenAll(rooms.Select(DisposeFollowRoomAsync));
    }

    private static async Task DisposeFollowRoomAsync(RoomFollowObserverSession room)
    { try { await room.DisposeAsync().ConfigureAwait(false); } catch { } }
    private static async Task DisposeRoomFollowSubscriberAsync(RoomFollowSubscriber subscriber)
    { try { await subscriber.DisposeAsync().ConfigureAwait(false); } catch { } }
    private static async Task DisposeFollowSignalRAsync(SignalRClient client)
    { try { await client.DisposeAsync().ConfigureAwait(false); } catch { } }

    private async Task StopRoomFollowForRoleChangeAsync(bool revokeSharing = true)
    {
        if (!IsRoomFollowing && !_roomFollowPreferences.FollowEnabled && !RoomFollowSharingEnabled
            && (!revokeSharing || _roomFollowGrants.Length == 0)) return;
        _roomFollowBusy = true;
        NotifyRoomFollowBindings();
        try
        {
            if (IsRoomFollowing || _roomFollowPreferences.FollowEnabled)
            {
                var off = _roomFollowPreferences with { FollowEnabled = false };
                _roomFollowPreferences = off;
                SaveRoomFollowPreferences(off);
                await StopRoomFollowingCoreAsync(restoreConnection: false);
            }
            if (RoomFollowSharingEnabled || revokeSharing && _roomFollowGrants.Length > 0)
                await StopRoomFollowSharingAsync(revoke: revokeSharing);
        }
        finally { _roomFollowBusy = false; RefreshRoomFollowStatus(); NotifyRoomFollowBindings(); }
    }

    private void RoomFollowModeChanged()
    {
        if (!_roomFollowInitialized || _disposing) return;
        if (Application.Current?.Dispatcher is { } dispatcher && !dispatcher.CheckAccess())
        {
            if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(new Action(RoomFollowModeChanged));
            return;
        }
        PublishRoomFollowSnapshot();
        if (_configTransferInProgress) { NotifyRoomFollowBindings(); return; }
        if (!RoomFollowSharingEnabled && CurrentRoomFollowOwner() != _roomFollowOwner)
        {
            if (_roomFollowOwner is not null && _roomFollowGrants.Length > 0)
                _ = StopRoomFollowSharingAsync(revoke: true);
            _roomFollowOwner = CurrentRoomFollowOwner();
            _roomFollowGrants = [];
            if (_roomFollowOwner is { } owner)
            {
                try { ReloadRoomFollowGrants(owner); }
                catch { _roomFollowError = "本机房间跟随授权无法读取。"; }
            }
        }
        RefreshRoomFollowStatus();
        NotifyRoomFollowBindings();
    }

    private void StopRoomFollowOnShutdown()
    {
        // Detach synchronously; neither a late subscriber callback nor a reconnect can adopt
        // a room after shutdown. Keep the user's explicit opt-in preference for the next launch.
        var runtime = _roomFollowing;
        var subscriber = runtime?.Subscriber;
        _roomFollowing = null;
        if (runtime is not null)
        {
            // The follow session owns this client. Do not send it through the normal second
            // shutdown/disposal path, and leave the projection write fence closed on exit.
            _signalRClient = null;
            _config = runtime.Original;
            _roomCode = runtime.OriginalRoom;
        }
        var rooms = StopFollowRoomsAsync();
        var publishing = StopRoomFollowPublishersAsync();
        var subscribing = subscriber is null ? Task.CompletedTask : DisposeRoomFollowSubscriberAsync(subscriber);
        // These workers never synchronously invoke the dispatcher. A short bounded wait permits
        // the relay/pipe to observe disconnect without prolonging the existing shutdown protocol.
        try { Task.WhenAll(rooms, publishing, subscribing).Wait(TimeSpan.FromSeconds(3)); } catch { }
    }

    private RoomOrchestrationPresentation GetFollowRoomOrchestrationPresentation()
    {
        // The follow capability shares ONLY layout metadata. Do not fetch another user's task
        // settings/commands or display missing bindings as a known empty task configuration.
        var status = new RoomOrchestrationStatus("unknown", "待核对");
        var projection = _roomFollowing!.Projection;
        var cards = projection.Runtime.RoomProfiles.Select(p => new RoomOrchestrationCard(p.Id, p.Name, p.RoomCode,
            "", p.Id == projection.Runtime.ActiveRoomProfileId, false, status, "",
            "仅跟随房间元数据；任务明细、进度和完成动作未共享。", [], [], "任务状态未确认",
            $"{p.ExpectedHoeingPlayers} 人", "未共享", "未共享", null, null, ProgressUnconfirmed: true)).ToArray();
        var active = projection.Runtime.ActiveRoomProfile;
        return new(active is null ? "监控端登录：尚未确认" : $"监控端登录：{active.Name} {active.RoomCode}",
            RoomFollowNoticeText, status, [], cards);
    }
}
