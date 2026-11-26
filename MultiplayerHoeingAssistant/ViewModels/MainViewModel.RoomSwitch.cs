using System.IO;
using System.Text.Json;
using System.Windows;
using MultiplayerHoeingAssistant.Views;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private sealed record RoomSwitchRecord(string RequestId, string ServerUrl, string Uid,
        string OriginalProfileId, string TargetProfileId, string OriginalRoom, string TargetRoom,
        bool ReadyAttempted = false, string? TargetServerUrl = null);

    private readonly SemaphoreSlim _assistantRoomSwitchGate = new(1, 1);
    private CancellationTokenSource? _assistantRoomSwitchCancellation;
    private RoomSwitchRecord? _unconfirmedRoomSwitch;
    private bool _roomSwitchRecordUnreadable;
    private volatile bool _changingAssistantRoom;
    private readonly object _assistantRoomEffectsGate = new();
    private int _assistantRoomEffects;

    private sealed class AssistantRoomEffect(MainViewModel owner) : IDisposable
    {
        public void Dispose() { lock (owner._assistantRoomEffectsGate) owner._assistantRoomEffects--; }
    }

    private IDisposable? TryEnterAssistantRoomEffect()
    {
        lock (_assistantRoomEffectsGate)
        {
            if (RoomSwitchBlocksRoomOperations) return null;
            _assistantRoomEffects++;
            return new AssistantRoomEffect(this);
        }
    }

    private bool TryBlockAssistantRoomEffects()
    {
        lock (_assistantRoomEffectsGate)
        {
            if (_assistantRoomEffects != 0) return false;
            _changingAssistantRoom = true;
            return true;
        }
    }
    private sealed record RoomChoicesSnapshot(List<AssistantRoomProfile>? Source, long Version, AssistantRoomProfile[] Items);
    private RoomChoicesSnapshot _roomSwitchChoices = new(null, -1, []);
    private long _roomSwitchChoicesVersion;
    private SignalRClient? _roomPreviewClient;
    private long _roomSwitchCommittedAtUtc;
    private string _roomSwitchStatus = "";
    private const string MissingRoomPasswordMessage = "目标房间需填写密码，请在「管理房间」中填写房间密码和本机成员 UID 后再切换。";
    private static string RoomSwitchRecordPath => Path.Combine(AssistantDataDirectory.Root, "room-switch-pending.json");

    // 每次房间列表变化都发布新的不可变快照数组，避免 WPF ListBox 绑定到原地增删的 List<T> 后发生 ItemContainerGenerator 计数错位。
    public IReadOnlyList<AssistantRoomProfile> RoomSwitchChoices
    {
        get
        {
            var source = _config?.RoomProfiles;
            var version = Volatile.Read(ref _roomSwitchChoicesVersion);
            var current = Volatile.Read(ref _roomSwitchChoices);
            if (ReferenceEquals(current.Source, source) && current.Version == version)
                return current.Items;
            AssistantRoomProfile[] items = source is { Count: > 0 } ? [.. source] : [];
            var activeId = _config?.ActiveRoomProfileId;
            foreach (var item in items)
            {
                if (item is not null)
                    item.IsActiveRoom = !string.IsNullOrEmpty(activeId) && string.Equals(item.Id, activeId, StringComparison.Ordinal);
            }
            Volatile.Write(ref _roomSwitchChoices, new(source, version, items));
            return items;
        }
    }
    public AssistantRoomProfile? ActiveRoomProfile => _config?.ActiveRoomProfile;
    public string HeaderRoomName
    {
        get
        {
            var name = ActiveRoomProfile?.Name;
            if (string.IsNullOrWhiteSpace(name)) return "未选择";
            var characters = System.Globalization.StringInfo.ParseCombiningCharacters(name);
            return characters.Length <= 4 ? name : name[..characters[4]];
        }
    }
    public string RoomSwitchStatus => _roomSwitchStatus;
    internal event Action? AssistantRoomChanged;
    public bool CanUseAssistantRoomMemberActions => !RoomSwitchBlocksRoomOperations;
    private bool RoomSwitchBlocksRoomOperations => _changingAssistantRoom || _unconfirmedRoomSwitch is not null || _roomSwitchRecordUnreadable;
    private bool RoomSwitchHasPendingRequest => _assistantRoomSwitchGate.CurrentCount == 0;

    public RelayCommand SwitchAssistantRoomCommand => new(async value =>
    {
        if (value is not AssistantRoomProfile profile) return;
        var result = await SwitchAssistantRoomForFlowAsync(profile.Id, null, CancellationToken.None);
        if (!result.Success && !result.Unknown) SetRoomSwitchStatus(result.Message);
    });
    public RelayCommand CancelAssistantRoomSwitchCommand => new(_ => CancelWaitingAssistantRoomSwitch(),
        _ => RoomSwitchHasPendingRequest && !_changingAssistantRoom && !RoomSwitchBlocksRoomOperations);
    public RelayCommand RecheckAssistantRoomSwitchCommand => new(async _ => await RecheckAssistantRoomSwitchAsync(),
        _ => !RoomSwitchHasPendingRequest && _unconfirmedRoomSwitch is not null);
    public RelayCommand OpenAssistantRoomsCommand => new(async _ => await EditAssistantRoomsAsync());
    public RelayCommand StopLocalDuringAssistantRoomSwitchCommand => new(async _ =>
    {
        if (!IsExecutorMode || _config is not { PlayerUid.Length: > 0 } config) return;
        if (MessageBox.Show("停止本机当前任务及待执行流程？不会向其他成员发送停止命令。", "停止本机",
            MessageBoxButton.YesNo, MessageBoxImage.Question) != MessageBoxResult.Yes) return;
        await ExecuteUserStopAsync([config.PlayerUid]);
    }, _ => IsExecutorMode && RoomSwitchBlocksRoomOperations);

    internal void CancelWaitingAssistantRoomSwitch()
    {
        CancelRoomOrchestrationPending("用户取消待切换房间", revokeOnline: false);
        try { Volatile.Read(ref _assistantRoomSwitchCancellation)?.Cancel(); }
        catch (ObjectDisposedException) { }
    }

    private void SetRoomSwitchStatus(string value)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.HasShutdownStarted) return;
        dispatcher.Invoke(() =>
        {
            _roomSwitchStatus = value;
            OnPropertyChanged(nameof(RoomSwitchStatus));
            OnPropertyChanged(nameof(CanUseAssistantRoomMemberActions));
            System.Windows.Input.CommandManager.InvalidateRequerySuggested();
        });
    }

    private void NotifyRoomProfiles() => NotifyRoomProfilesCore(reconcileSessions: true);

    private void NotifyRoomProfilesCore(bool reconcileSessions)
    {
        if (_config?.RoomProfiles is { Count: > 0 } rooms)
        {
            var activeId = _config.ActiveRoomProfileId;
            foreach (var room in rooms)
            {
                if (room is not null)
                    room.IsActiveRoom = !string.IsNullOrEmpty(activeId) && string.Equals(room.Id, activeId, StringComparison.Ordinal);
            }
        }
        Interlocked.Increment(ref _roomSwitchChoicesVersion);
        OnPropertyChanged(nameof(RoomSwitchChoices));
        OnPropertyChanged(nameof(ActiveRoomProfile));
        OnPropertyChanged(nameof(HeaderRoomName));
        OnPropertyChanged(nameof(Config));
        NotifyRoomViewProperties();
        if (reconcileSessions) SyncRoomViewSessions();
        RebuildDisplayedMembers();
    }

    private void LoadAssistantRoomSwitchRecord()
    {
        try
        {
            if (!File.Exists(RoomSwitchRecordPath)) return;
            _unconfirmedRoomSwitch = JsonSerializer.Deserialize<RoomSwitchRecord>(File.ReadAllText(RoomSwitchRecordPath))
                ?? throw new InvalidDataException("切房记录为空");
            SetRoomSwitchStatus($"切换结果待核对：{_unconfirmedRoomSwitch.OriginalRoom} → {_unconfirmedRoomSwitch.TargetRoom}");
        }
        catch (Exception ex)
        {
            _roomSwitchRecordUnreadable = true;
            SetRoomSwitchStatus("切房记录读取失败，已暂停自动房间操作：" + ex.Message);
        }
    }

    private void RecordAssistantRoomSwitch(RoomSwitchRecord record)
    {
        if (!LocalPeerSyncService.WriteFileAtomic(RoomSwitchRecordPath, JsonSerializer.Serialize(record)))
            throw new IOException("无法保存切房记录，未提交离房");
        _unconfirmedRoomSwitch = record;
    }

    private void ClearAssistantRoomSwitchRecord()
    {
        File.Delete(RoomSwitchRecordPath);
        _unconfirmedRoomSwitch = null;
    }

    private string? GetAssistantRoomIdentity()
    {
        var current = _signalRClient;
        return !RoomSwitchBlocksRoomOperations && current is { IsControlRoomReady: true }
            && current.CurrentRoomCode == RoomCode && RoomServerAddress.Same(current.CurrentServerUrl, _config?.ServerUrl)
            ? current.DesktopControlContext : null;
    }

    internal bool IsAssistantRoomIdentityCurrent(string identity) => identity == GetAssistantRoomIdentity();

    private bool AssistantRoomHasBatch()
    {
        lock (_batchGate)
            if (_activeBatch is { IsAlive: true } || _pendingCoordinatedBegin is not null) return true;
        if (_commandExecutor?.TakeoverTicketSnapshot is not null) return true;
        TaskCenterHost? host;
        lock (_taskCenterHostGate) host = _taskCenterHost;
        return host?.HasUnsettledOnlineHoeingHandoff == true;
    }

    internal Task<FlowRoomSwitchResult> SwitchAssistantRoomForFlowAsync(string profileId,
        Func<CancellationToken, Task>? verifySource, CancellationToken ct)
        => SwitchAssistantRoomCoreAsync(profileId, verifySource, null, ct);

    private static string? GetRoomSwitchCredentialError(AssistConfig config, AssistantRoomProfile target)
    {
        if (target.NeedsRoomPassword) return MissingRoomPasswordMessage;
        if (target.PreserveRoomCode && (string.IsNullOrWhiteSpace(config.PlayerName)
            || !AssistantRoomDraft.HasValidMemberUids(target.TeamUids ?? [], config.PlayerUid)))
            return "目标房间的本机玩家名称或成员 UID 未配置，请在本机设置和「管理房间」中核对后再切换。";
        return null;
    }

    private async Task<FlowRoomSwitchResult> SwitchAssistantRoomCoreAsync(string profileId,
        Func<CancellationToken, Task>? verifySource, string? expectedSourceRoom, CancellationToken ct,
        bool waitForOnlineRound = false, RoomOrchestrationIntent? orchestration = null,
        string? requestIdentity = null, Func<bool>? stillAuthorized = null)
    {
        if (ct.IsCancellationRequested || stillAuthorized?.Invoke() == false)
            return new(false, false, "切房已取消或原授权已变化，未提交");
        // A metadata-only room must not supersede a pending valid switch or revoke the
        // current completion action before its own credentials have been configured.
        if (!RoomSwitchBlocksRoomOperations && _config is { StandaloneMode: false } current
            && current.ActiveRoomProfileId != profileId
            && current.RoomProfiles.Find(p => p is not null && p.Id == profileId) is { } requested
            && GetRoomSwitchCredentialError(current, requested) is { } preflightError)
            return new(false, false, preflightError);
        var replaced = orchestration is null && CancelRoomOrchestrationPending("有更新的手动或流程切房请求", revokeOnline: false);
        // A manual/flow request superseding an automatic waiter may briefly wait for that
        // waiter's cancellation to release the existing gate. Other callers retain immediate busy.
        if (replaced ? !await _assistantRoomSwitchGate.WaitAsync(TimeSpan.FromSeconds(5), ct)
            : !_assistantRoomSwitchGate.Wait(0)) return new(false, false, "切换忙");
        using var cancel = CancellationTokenSource.CreateLinkedTokenSource(ct);
        _assistantRoomSwitchCancellation = cancel;
        var token = cancel.Token;
        SignalRClient? candidate = null;
        var submittedLeave = false;
        var confirmedLeave = false;
        var readyAttempted = false;
        var connectionGate = false;
        var restartSource = false;
        try
        {
            if (_disposing || _config is not { StandaloneMode: false } config || _configManager is null)
                return new(false, false, "单机模式或配置未就绪，未切换房间");
            if (RoomSwitchBlocksRoomOperations) return new(false, true, "切换结果待核对");
            if (!TryEnterConfigTransferOperation("room_switch", out var transfer))
                return new(false, false, ConfigTransferBlockedMessage);
            using var transferOperation = transfer;
            config.EnsureLocalRoomProfile();
            var source = config.ActiveRoomProfile;
            var sourceKey = GetRoomOrchestrationKey(source);
            if (config.RoomProfiles.SingleOrDefault(p => p.Id == profileId) is not { } target)
                return new(false, false, "找不到目标房间档案");
            if (source?.Id != target.Id && GetRoomSwitchCredentialError(config, target) is { } credentialError)
                return new(false, false, credentialError);
            if (!RoomServerAddress.TryNormalize(config.GetRoomServerUrl(target), out var targetServer))
                return new(false, false, "目标档案的服务器地址无效，请在管理房间中核对");
            var old = _signalRClient;
            var identity = GetAssistantRoomIdentity();
            var firstRoom = source is null && old is null && string.IsNullOrEmpty(RoomCode);
            if ((!firstRoom && (old is null || identity is null || source is null))
                || expectedSourceRoom is not null && expectedSourceRoom != identity)
                return new(false, false, "原房间连接已变化，未切换");
            if (source?.Id != target.Id) verifySource ??= await CaptureManualRoomSwitchStopGuardAsync(token);
            if (stillAuthorized is not null)
            {
                var originalGuard = verifySource;
                verifySource = async guardToken =>
                {
                    guardToken.ThrowIfCancellationRequested();
                    if (!stillAuthorized()) throw new OperationCanceledException("原网页授权或执行端已变化", guardToken);
                    if (originalGuard is not null) await originalGuard(guardToken);
                    if (!stillAuthorized()) throw new OperationCanceledException("原网页授权或执行端已变化", guardToken);
                };
            }
            if (verifySource is not null) await verifySource(token);
            token.ThrowIfCancellationRequested();
            if (source?.Id == target.Id) return new(true, false, "已在目标房间", identity);

            // An explicit return after BGI online can wait through the current batch. Independent
            // nodes have a shorter admission budget and never cancel the work they're waiting on.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(waitForOnlineRound ? TimeSpan.FromHours(6) : TimeSpan.FromSeconds(45));
            var comparedTarget = (target.RoomCode, target.Password, Uids: string.Join('\n', target.TeamUids));
            var record = new RoomSwitchRecord(requestIdentity ?? Guid.NewGuid().ToString("N"), config.ServerUrl, config.PlayerUid,
                source?.Id ?? "", target.Id, source?.RoomCode ?? "", target.RoomCode, TargetServerUrl: targetServer);
            SetRoomSwitchStatus("等待切换到「" + target.Name + "」");
            while (true)
            {
                wait.Token.ThrowIfCancellationRequested();
                if (verifySource is not null) await verifySource(wait.Token);
                if (identity != GetAssistantRoomIdentity() || !ReferenceEquals(config, _config) || config.StandaloneMode
                    || !config.RoomProfiles.Contains(target) || !RoomServerAddress.Same(targetServer, config.GetRoomServerUrl(target))
                    || comparedTarget != (target.RoomCode, target.Password, string.Join('\n', target.TeamUids)))
                    return new(false, false, "房间连接或档案已变化，未切换");
                if (GetRoomSwitchCredentialError(config, target) is { } changedCredentialError)
                    return new(false, false, changedCredentialError);
                if (AssistantRoomHasBatch() || Volatile.Read(ref _ordinaryStopInProgress) != 0
                    || _localResetGate.CurrentCount == 0 || Volatile.Read(ref _refreshRunning) != 0
                    || !_pendingCenterReplies.IsEmpty || _pendingCommandResults.Count != 0)
                {
                    await Task.Delay(750, wait.Token);
                    continue;
                }
                await _connectionAttemptGate.WaitAsync(wait.Token);
                connectionGate = true;
                if (identity != GetAssistantRoomIdentity() || AssistantRoomHasBatch())
                {
                    _connectionAttemptGate.Release();
                    connectionGate = false;
                    if (identity != GetAssistantRoomIdentity()) return new(false, false, "原房间连接已变化");
                    continue;
                }
                if (verifySource is not null) await verifySource(wait.Token);
                wait.Token.ThrowIfCancellationRequested();
                if (!TryBlockAssistantRoomEffects())
                {
                    _connectionAttemptGate.Release();
                    connectionGate = false;
                    await Task.Delay(250, wait.Token);
                    continue;
                }
                if (orchestration is not null && !UpdateRoomOrchestrationExecution(orchestration.Id, old => old with
                    { SwitchState = "switching", Message = "正在确认原房间允许安全离开" }))
                    throw new IOException("编排接续记录未保存，未提交离房");
                RecordAssistantRoomSwitch(record);
                // No outbound command or automatic reconnect can reassign the source during the leave.
                submittedLeave = true;
                using var leaveBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
                leaveBudget.CancelAfter(TimeSpan.FromSeconds(12));
                var leave = old is null ? (Success: true, Busy: false, Reason: "")
                    : await old.LeaveControlRoomForSwitchAsync(leaveBudget.Token);
                if (leave.Success)
                {
                    confirmedLeave = true;
                    break;
                }
                if (!leave.Busy) throw new ControlChannelUnavailableException(leave.Reason);
                ClearAssistantRoomSwitchRecord();
                submittedLeave = false;
                _changingAssistantRoom = false;
                _connectionAttemptGate.Release();
                connectionGate = false;
                SetRoomSwitchStatus("等待切换到「" + target.Name + "」：当前房间仍在等待或收尾");
                if (orchestration is not null)
                    UpdateRoomOrchestrationExecution(orchestration.Id, old => old with
                    { SwitchState = "waiting", Message = "本机已清理，等待原房间队友结束或收尾" });
                await Task.Delay(750, wait.Token);
            }

            StopSignalRRecovery();
            _retryTimer?.Dispose();
            _retryTimer = null;
            if (old is not null)
            {
                _controlAuthority.Revoke(old);
                old.RetireForReplacement();
                _signalRClient = null;
                await old.DisposeAsync();
            }
            Application.Current.Dispatcher.Invoke(() =>
            {
                IsConnected = false;
                Members.Clear();
                ResetRoomMemberRevision();
                MemberOnlineChanged?.Invoke();
            });
            SetRoomSwitchStatus("正在进入「" + target.Name + "」");
            token.ThrowIfCancellationRequested();
            using var joinBudget = CancellationTokenSource.CreateLinkedTokenSource(token);
            joinBudget.CancelAfter(TimeSpan.FromSeconds(20));
            candidate = CreateWiredSignalRClient();
            candidate.PrepareRoomSwitch();
            // Candidate has no batch authority before acceptance. Its join is serialized by the attempt gate.
            candidate.CoordinateRoomJoin = null;
            await candidate.ConnectAsync(targetServer, target.RoomCode, target.Password, config.PlayerUid,
                config.PlayerName, [.. target.TeamUids], config.ObserverMode, config.ClientInstanceId,
                config.BypassSystemProxy, joinBudget.Token);
            _roomPreviewClient = candidate;
            candidate.ApplyRoomSnapshot(candidate.PreparedRoomSnapshot!);
            if (verifySource is not null) await verifySource(joinBudget.Token);
            RecordAssistantRoomSwitch(record with { ReadyAttempted = true });
            readyAttempted = true;
            var ready = await candidate.ConfirmRoomSwitchAsync(BuildTargetRoomStatus(config, target), joinBudget.Token);
            if (verifySource is not null) await verifySource(joinBudget.Token);
            joinBudget.Token.ThrowIfCancellationRequested();
            var originalId = config.ActiveRoomProfileId;
            var oldSuppression = _intentLifecycle.ManuallyClearedOnline;
            var newSuppression = _roomScheduleSuppressed.TryGetValue(target.Id, out var suppressed)
                ? suppressed : !App.LaunchIntent.AllowsAutomaticStartup || string.IsNullOrEmpty(target.ScheduledOnlineTime);
            Application.Current.Dispatcher.Invoke(() =>
            {
                joinBudget.Token.ThrowIfCancellationRequested();
                if (!ReferenceEquals(_config, config) || config.StandaloneMode || _disposing)
                    throw new OperationCanceledException("设置或运行模式已变化");
                if (orchestration is not null)
                    PrepareRoomOrchestrationCutover(orchestration, candidate.DesktopControlContext);
                var previous = source ?? new AssistantRoomProfile();
                previous.Capture(config);
                var previousTargetFireDate = target.ScheduledOnlineLastFireDate;
                var now = DateTime.Now;
                if (source is not null && TimeSpan.TryParse(target.ScheduledOnlineTime, out var schedule)
                    && now >= now.Date.Add(schedule) && target.ScheduledOnlineLastFireDate?.Date != now.Date)
                    target.ScheduledOnlineLastFireDate = now.Date; // A room that was inactive at its scheduled time does not catch up on switch.
                target.ApplyTo(config);
                config.ServerUrl = targetServer;
                var originalLocalId = config.LocalRoomProfileId;
                config.ActiveRoomProfileId = target.Id;
                config.EnsureLocalRoomProfile();
                if (!_configManager.TrySave(config))
                {
                    target.ScheduledOnlineLastFireDate = previousTargetFireDate;
                    previous.ApplyTo(config);
                    config.ActiveRoomProfileId = originalId;
                    config.LocalRoomProfileId = originalLocalId;
                    throw new IOException("目标档案未写入，切换结果待核对");
                }
                if (source is not null) _roomScheduleSuppressed[source.Id] = oldSuppression;
                _roomCode = target.RoomCode;
                Interlocked.Increment(ref _controlContextVersion);
                candidate.ControlContext = CurrentControlContext();
                candidate.CoordinateRoomJoin = CoordinateControlRoomJoinAsync;
                PublishCoordinatedResetFenceRoom(candidate);
                if (sourceKey is not null) RevokeRoomOrchestrationOnline("已离开原房间", departed: sourceKey);
                _intentLifecycle.ChangeAssistantRoom(newSuppression);
                Volatile.Write(ref _roomSwitchCommittedAtUtc, DateTime.UtcNow.Ticks);
                _lastScheduledFireDate = config.ScheduledOnlineLastFireDate?.Date ?? DateTime.MinValue;
                lock (_batchGate)
                {
                    _lastHoeingBatchId = null;
                    _lastHoeingStartPhase = null;
                    _lastHoeingStartMessage = null;
                }
                _lastLoggedProgress = "";
                _signalRClient = candidate;
                candidate.ApplyRoomSnapshot(ready);
                candidate.CompleteRoomSwitch();
                _controlAuthority.Publish(candidate, candidate.ControlContext);
                ClearAssistantRoomSwitchRecord();
                if (orchestration is not null) ConfirmRoomOrchestrationCutover(orchestration);
                IsConnected = true;
                OnPropertyChanged(nameof(RoomCode));
                NotifyRoomProfiles();
                OnPropertyChanged(nameof(IsStandaloneMode));
                OnPropertyChanged(nameof(ConfigurationTransferStatusMessage));
                try { AssistantRoomChanged?.Invoke(); }
                catch (Exception observerError) { AddLog("跨房间刷新失败：" + observerError.Message); }
                RefreshCompleted?.Invoke();
            });
            _roomPreviewClient = null;
            _changingAssistantRoom = false;
            var committed = candidate;
            candidate = null;
            SetRoomSwitchStatus("已切换到「" + target.Name + "」");
            committed.DispatchPreparedRoomCommands();
            // The automatic second stage must not race the target's batch-current restore.
            // Existing manual and independent BGI switches retain their asynchronous behavior.
            if (orchestration is not null)
            {
                try { await committed.RestoreCoordinatedAuthorityAsync(token); }
                catch (Exception ex)
                {
                    // The target room is already confirmed and the local profile committed. A
                    // cancelled/failed successor eligibility read cannot turn a real switch into
                    // an unknown leave/join transaction or authorize a rollback to the old room.
                    AddLog("已切换房间，目标批次待核对，未推进自动后继：" + ex.Message);
                    CancelRoomOrchestrationPending("目标房间批次核对未确认，未推进自动后继", revokeOnline: false, revokeBatch: false);
                    UpdateRoomOrchestrationExecution(orchestration.Id, old => old with
                    {
                        SwitchState = "switched", PostState = old.Target.RunAfterSwitch ? "cancelled" : "none",
                        Message = "房间已确认切换；目标批次尚未核对，未自动启动后继"
                    });
                }
            }
            else ObserveOnlineTask(committed.RestoreCoordinatedAuthorityAsync(), "核对目标房间联机批次");
            ObserveOnlineTask(ReportStatusAsync(), "切房后状态上报");
            return new(true, false, RoomSwitchStatus, committed.DesktopControlContext);
        }
        catch (Exception ex) when (confirmedLeave && !readyAttempted)
        {
            // The target is still read-only, even if its preparation reply was lost.
            try
            {
                if (candidate is not null)
                {
                    await candidate.DisposeAsync();
                    candidate = null;
                }
                ClearAssistantRoomSwitchRecord();
                restartSource = !string.IsNullOrEmpty(RoomCode);
            }
            catch (Exception cleanupError)
            {
                SetRoomSwitchStatus("切换结果待核对：" + cleanupError.Message);
                return new(false, true, RoomSwitchStatus);
            }
            var prefix = restartSource ? "目标入房未完成，尝试恢复原服务器的房间：" : "目标入房未完成：";
            SetRoomSwitchStatus(prefix + ex.Message);
            return new(false, false, RoomSwitchStatus);
        }
        catch (Exception ex)
        {
            var unknown = submittedLeave || _unconfirmedRoomSwitch is not null;
            var message = unknown ? "切换结果待核对：" + ex.Message
                : ex is OperationCanceledException ? "切房已取消或等待超时" : "切房失败：" + ex.Message;
            // A lost leave reply is not authority to disconnect a possibly still active source.
            // Keep its transport available for the existing stop and batch cleanup paths.
            SetRoomSwitchStatus(message);
            return new(false, unknown, message);
        }
        finally
        {
            _roomPreviewClient = null;
            if (candidate is not null)
            {
                if (ReferenceEquals(candidate, _signalRClient))
                {
                    _controlAuthority.Revoke(candidate);
                    _signalRClient = null;
                }
                try { await candidate.DisposeAsync(); }
                catch (Exception ex) { AddLog("目标房间连接清理失败：" + ex.Message); }
            }
            if (_unconfirmedRoomSwitch is not null && _signalRClient is null)
                Application.Current?.Dispatcher.Invoke(() => { IsConnected = false; Members.Clear(); ResetRoomMemberRevision(); });
            _changingAssistantRoom = false;
            if (connectionGate) _connectionAttemptGate.Release();
            try { if (restartSource && !IsStandaloneMode && !_disposing) await ConnectSignalRAsync(); }
            finally
            {
                _assistantRoomSwitchCancellation = null;
                _assistantRoomSwitchGate.Release();
                if (!_disposing && Application.Current?.Dispatcher is { HasShutdownStarted: false } ui)
                    ui.BeginInvoke(new Action(() =>
                    {
                        OnPropertyChanged(nameof(CanUseAssistantRoomMemberActions));
                        System.Windows.Input.CommandManager.InvalidateRequerySuggested();
                    }));
            }
        }
    }

    private ControlStatus BuildTargetRoomStatus(AssistConfig config, AssistantRoomProfile profile)
    {
        var local = LatestLocalStatus;
        var completionConfig = new AssistConfig
        {
            RoomProfiles = config.RoomProfiles.ToList(), ActiveRoomProfileId = profile.Id,
            ServerUrl = config.GetRoomServerUrl(profile), PlayerUid = config.PlayerUid
        };
        profile.ApplyTo(completionConfig);
        return new ControlStatus
        {
            PlayerUid = config.PlayerUid, PlayerName = config.PlayerName, RoomCode = profile.RoomCode,
            BgiStatus = local?.BgiStatus ?? "unknown",
            BgiVersion = BgiVersionResolver.Resolve(_externalClient?.BgiVersion, config.BgiPath) ?? "",
            ConfigGroups = local?.ConfigGroups.ToList() ?? [], OneClickConfigs = local?.OneClickConfigs.ToList() ?? [],
            ScheduledOnlineTime = profile.ScheduledOnlineTime, ExpectedHoeingPlayers = profile.ExpectedHoeingPlayers,
            OnlineHoeingGroupNames = [.. profile.OnlineHoeingGroupNames], OnlineHoeingGroupTypes = [.. profile.OnlineHoeingGroupTypes],
            QuickCommands = new(profile.QuickCommands),
            HoeingCompletionAction = CoordinatedCompletionAction.FromSettings(CompletionPolicyFields.Snapshot(completionConfig), completionConfig),
            TaskStatusAvailable = local?.HasFreshTaskStatus(DateTimeOffset.UtcNow) == true,
            TaskRunning = local?.TaskRunning == true, CurrentTaskName = local?.CurrentTaskName,
        };
    }

    private readonly Dictionary<string, bool> _roomScheduleSuppressed = new(StringComparer.Ordinal);

    private async Task<bool> RecheckAssistantRoomSwitchAsync(string? expectedRequestId = null, Func<bool>? stillAuthorized = null)
    {
        if (!await _assistantRoomSwitchGate.WaitAsync(0)) return false;
        var confirmed = false;
        try
        {
            if (_unconfirmedRoomSwitch is not { } record || _config is not { StandaloneMode: false } config
                || expectedRequestId is not null && record.RequestId != expectedRequestId
                || stillAuthorized?.Invoke() == false) return false;
            // In-flight records from the old shared-address version have no TargetServerUrl.
            var targetServer = record.TargetServerUrl ?? record.ServerUrl;
            if (record.Uid != config.PlayerUid || AssistantRoomHasBatch()
                || !((config.ActiveRoomProfileId == record.OriginalProfileId && RoomServerAddress.Same(config.ServerUrl, record.ServerUrl))
                    || (config.ActiveRoomProfileId == record.TargetProfileId && RoomServerAddress.Same(config.ServerUrl, targetServer))))
            { SetRoomSwitchStatus("切换结果待核对：原执行身份已变化或联机尚未收尾"); return false; }
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var retainedSource = _signalRClient;
            ControlRoomPlayersUpdate? retainedSourceSnapshot = null;
            foreach (var (id, code, address, isTarget) in new[]
                { (record.OriginalProfileId, record.OriginalRoom, record.ServerUrl, false),
                  (record.TargetProfileId, record.TargetRoom, targetServer, true) })
            {
                if (string.IsNullOrEmpty(code) || isTarget && !record.ReadyAttempted) continue;
                var profile = config.RoomProfiles.SingleOrDefault(p => p.Id == id && p.RoomCode == code
                    && RoomServerAddress.Same(config.GetRoomServerUrl(p), address));
                if (profile is null || !RoomServerAddress.TryNormalize(address, out var server))
                    throw new InvalidOperationException("待核对的服务器或档案已变化");
                if (stillAuthorized?.Invoke() == false) throw new OperationCanceledException("原网页授权或执行端已变化");
                await using var probe = new MhaGatewayClient();
                await probe.ConnectAsync(server, config.BypassSystemProxy, budget.Token);
                if (stillAuthorized?.Invoke() == false) throw new OperationCanceledException("原网页授权或执行端已变化");
                var reply = await probe.InvokeCommandAsync("control.roomAccess", new { roomCode = code, password = profile.Password, playerUid = config.PlayerUid }, ct: budget.Token);
                if (reply.Payload?["success"]?.GetValue<bool>() != true || reply.Payload?["busy"]?.GetValue<bool>() != false
                    || reply.Payload?["joined"] is null)
                    throw new InvalidOperationException("房间 " + profile.Name + "（" + server + "）未确认释放批次责任");
                if (reply.Payload["joined"]!.GetValue<bool>())
                {
                    if (isTarget || retainedSource?.CurrentRoomCode != code || !RoomServerAddress.Same(retainedSource.CurrentServerUrl, server)
                        || await retainedSource.ReadRoomSnapshotAsync(budget.Token) is not { Full: true } snapshot)
                        throw new InvalidOperationException("房间 " + profile.Name + "（" + server + "）未确认释放成员资格");
                    retainedSourceSnapshot = snapshot;
                }
            }
            if (IsBgiRunning && (LatestLocalStatus?.HasFreshTaskStatus(DateTimeOffset.UtcNow) != true
                || LatestLocalStatus is { TaskRunning: true } or { AutoHoeingRunning: true }))
                throw new InvalidOperationException("本机 BGI 尚未确认空闲；可使用原有停止入口后重新检查");
            if (retainedSourceSnapshot is not null && retainedSource is not { IsControlRoomReady: true })
                throw new InvalidOperationException("原房间连接在核对中改变");
            if (stillAuthorized?.Invoke() == false || !ReferenceEquals(config, _config) || _unconfirmedRoomSwitch != record)
                throw new InvalidOperationException("原切房记录、网页授权或执行端已变化");
            ClearAssistantRoomSwitchRecord();
            confirmed = true;
            SetRoomSwitchStatus("已确认原请求终止，未恢复自动流程");
            if (retainedSourceSnapshot is { } full)
                retainedSource!.ApplyRoomSnapshot(full);
            else
            {
                if (retainedSource is not null)
                {
                    retainedSource.RetireForReplacement();
                    _controlAuthority.Revoke(retainedSource);
                    _signalRClient = null;
                    await retainedSource.DisposeAsync();
                }
                await ConnectSignalRAsync();
            }
        }
        catch (Exception ex) { SetRoomSwitchStatus("切换结果待核对：" + ex.Message); }
        finally { _assistantRoomSwitchGate.Release(); }
        return confirmed;
    }

    private long _appliedRoomMemberRevision;
    private string? _roomMemberEpoch;
    private int _requestingFullRoomSnapshot;
    private void ResetRoomMemberRevision()
    {
        _appliedRoomMemberRevision = 0;
        _roomMemberEpoch = null;
    }

    private bool AcceptAssistantRoomMembers(SignalRClient client, ControlRoomPlayersUpdate update)
    {
        if (_disposing || client.IsRetired) return false;
        var preview = ReferenceEquals(client, _roomPreviewClient);
        if (!preview && (client.ControlContext != CurrentControlContext() || client.CurrentRoomCode != RoomCode
            || !RoomServerAddress.Same(client.CurrentServerUrl, _config?.ServerUrl))) return false;
        if (!preview && !ReferenceEquals(client, _signalRClient) && !ReferenceEquals(client, _pendingControlClient)) return false;
        if (!preview && RoomSwitchBlocksRoomOperations) return false;
        var epoch = client.DesktopControlContext;
        if (_roomMemberEpoch != epoch)
        {
            _roomMemberEpoch = epoch;
            _appliedRoomMemberRevision = 0;
        }
        if (update.Revision <= 0) return true;
        if (update.Revision <= _appliedRoomMemberRevision) return false;
        if (!update.Full && update.BaseRevision is { } baseline && baseline != _appliedRoomMemberRevision)
        {
            if (Interlocked.CompareExchange(ref _requestingFullRoomSnapshot, 1, 0) == 0)
                ObserveOnlineTask(RefreshAssistantRoomSnapshotAsync(client), "补取房间成员全量");
            return false;
        }
        _appliedRoomMemberRevision = update.Revision;
        return true;
    }

    private async Task RefreshAssistantRoomSnapshotAsync(SignalRClient client)
    {
        try
        {
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(8));
            var full = await client.ReadRoomSnapshotAsync(budget.Token);
            if (full is { Full: true }) client.ApplyRoomSnapshot(full);
        }
        finally { Interlocked.Exchange(ref _requestingFullRoomSnapshot, 0); }
    }

    private readonly SemaphoreSlim _roomProfileEditGate = new(1, 1);

    // Both the desktop editor and the owner Web editor use these validation, persistence and
    // rollback paths. The Web editor supplies an optimistic revision, the desktop editor an
    // actual selected profile object. Neither can overwrite a removed/replaced profile.
    private sealed class RoomProfileEditRejectedException(string message, string? webMessage = null) : InvalidOperationException(message)
    {
        public string WebMessage { get; } = webMessage ?? message;
    }

    private sealed class RoomProfileCreateIncompleteException(string roomCode, Exception inner)
        : IOException("房间已创建（" + roomCode + "），但档案未能保存。请用房间号添加已有房间。", inner)
    {
        public string RoomCode { get; } = roomCode;
    }

    private static string EffectiveLocalRoomProfileId(AssistConfig config)
        => string.IsNullOrEmpty(config.LocalRoomProfileId) ? config.ActiveRoomProfile?.Id ?? "" : config.LocalRoomProfileId;

    private void VerifyAssistantRoomProfileEditable(AssistConfig config, string? expectedRevision = null)
    {
        if (_disposing || RoomSwitchHasPendingRequest || RoomSwitchBlocksRoomOperations || !CanEditConfiguration
            || !ReferenceEquals(config, _config) || _configManager is null)
            throw new RoomProfileEditRejectedException("房间或本机设置已变化，请重新打开房间管理");
        if (expectedRevision is not null && expectedRevision != RoomProfilesWebRevision(config))
            throw new RoomProfileEditRejectedException("房间档案已变化，请重新读取后修改");
    }

    private Task EditAssistantRoomsAsync()
    {
        using var roomEffect = TryEnterAssistantRoomEffect();
        if (roomEffect is null || RoomSwitchHasPendingRequest || !CanEditConfiguration
            || _config is not { } config || _configManager is null) return Task.CompletedTask;
        config.FillLegacyRoomServerUrls();
        config.EnsureLocalRoomProfile();
        var window = new AssistantRoomProfilesWindow(config.RoomProfiles.ToList(), config.ActiveRoomProfileId, () => config.LocalRoomProfileId,
            config.ServerUrl, config.PlayerUid,
            (selected, draft, create) => SaveAssistantRoomProfileAsync(config, selected, draft, create),
            selected => RemoveAssistantRoomProfile(config, selected),
            () => ExportAssistantRoomBook(config), text => MergeAssistantRoomBook(config, text))
        { Owner = Application.Current.MainWindow, DataContext = this };
        window.ShowDialog();
        return Task.CompletedTask;
    }

    private string ExportAssistantRoomBook(AssistConfig config)
    {
        if (!_roomProfileEditGate.Wait(0)) throw new RoomProfileEditRejectedException("另一个房间档案正在保存，请稍后重新读取");
        try
        {
            VerifyAssistantRoomProfileEditable(config);
            if (!TryEnterConfigTransferOperation("room_profile_edit", out var operation))
                throw new RoomProfileEditRejectedException(ConfigTransferBlockedMessage);
            using var transfer = operation;
            using var writing = _configManager!.AcquireWriteGate();
            return RoomBookTransfer.Export(config);
        }
        finally { _roomProfileEditGate.Release(); }
    }

    private RoomBookMergeResult MergeAssistantRoomBook(AssistConfig config, string text)
    {
        if (!_roomProfileEditGate.Wait(0)) throw new RoomProfileEditRejectedException("另一个房间档案正在保存，请稍后重新读取");
        try
        {
            VerifyAssistantRoomProfileEditable(config);
            if (!TryEnterConfigTransferOperation("room_profile_edit", out var lease))
                throw new RoomProfileEditRejectedException(ConfigTransferBlockedMessage);
            using var transfer = lease;
            RoomBookMergeResult result;
            lock (_batchGate)
            {
                VerifyAssistantRoomProfileEditable(config);
                result = _configManager!.MergeRoomBook(config, text);
            }
            if (result.ImportedCount > 0)
            {
                // Persistence is already confirmed, so a view observer's failure cannot turn
                // the merge into a misleading failure receipt or cause a duplicate import.
                try { NotifyRoomProfilesCore(reconcileSessions: false); }
                catch (Exception ex) { AddLog("房间簿已保存，界面刷新失败：" + ex.Message); }
            }
            return result;
        }
        finally { _roomProfileEditGate.Release(); }
    }

    private (string Label, string Server, string Code, bool Active, int Index) ValidateAssistantRoomDraft(
        AssistConfig config, AssistantRoomProfile? selected, AssistantRoomDraft draft, bool create)
    {
        var label = draft.Name.Trim();
        if (label.Length is 0 or > 64 || config.RoomProfiles.Any(p => p != selected
            && string.Equals(p.Name, label, StringComparison.OrdinalIgnoreCase)))
            throw new RoomProfileEditRejectedException("档案名称必须填写，并且不能与本机其他房间重名");
        if (!RoomServerAddress.TryNormalize(draft.ServerUrl, out var server))
            throw new RoomProfileEditRejectedException("服务器地址必须是有效的 http 或 https 基地址");
        if (draft.ExpectedPlayers is < 1 or > 4)
            throw new RoomProfileEditRejectedException("预期开锄人数必须是 1–4 人");
        var exactCode = RoomBookTransfer.UsesExactRoomCode(selected);
        var code = exactCode ? draft.RoomCode.Trim() : draft.RoomCode.Trim().ToUpperInvariant();
        var uids = draft.MemberUids;
        var active = selected is not null && selected.Id == config.ActiveRoomProfileId;
        var index = selected is null ? -1 : config.RoomProfiles.IndexOf(selected);
        if (selected is not null && index < 0)
            throw new RoomProfileEditRejectedException("该档案已被移除或更新");
        if (selected is not null && selected.Id == EffectiveLocalRoomProfileId(config)
            && (create || selected.RoomCode != code || !RoomServerAddress.Same(config.GetRoomServerUrl(selected), server)
                || draft.Password != selected.Password || !uids.SequenceEqual(selected.TeamUids, StringComparer.Ordinal)))
            throw new RoomProfileEditRejectedException("本机房间凭据和成员请在本机设置中修改");
        if (active)
        {
            if (create || selected!.RoomCode != code || !RoomServerAddress.Same(config.ServerUrl, server)
                || draft.Password != config.ControlRoomPassword || !uids.SequenceEqual(config.TeamUids, StringComparer.Ordinal))
                throw new RoomProfileEditRejectedException("不能在这里改变正在使用的服务器或入房凭据，可添加另一个房间档案后切换");
            if (draft.ExpectedPlayers != config.ExpectedHoeingPlayers && (AssistantRoomHasBatch() || _intentLifecycle.IsOnlineReady))
                throw new RoomProfileEditRejectedException("当前房间正在等待开锄或执行任务，请等本轮结束再修改开锄人数");
        }
        else
        {
            if (selected is null && config.RoomProfiles.Count >= RoomBookTransfer.MaxProfiles)
                throw new RoomProfileEditRejectedException($"本机最多保存 {RoomBookTransfer.MaxProfiles} 个房间档案");
            if (!AssistantRoomDraft.HasValidMemberUids(uids, config.PlayerUid))
                throw new RoomProfileEditRejectedException("请填写有效的成员 UID，并将本机玩家的 UID 加入名单");
            if (string.IsNullOrWhiteSpace(draft.Password) || draft.Password.Length > 128)
                throw new RoomProfileEditRejectedException("请填写房间密码，最多 128 个字符");
            if (create)
            {
                if (selected is not null || code.Length != 0 || config.StandaloneMode)
                    throw new RoomProfileEditRejectedException("请在联网模式添加新档案，并将房间号留空后建房");
            }
            else
            {
                if (exactCode ? !RoomBookTransfer.IsValidRoomCode(code) : code.Length != 6 || code.Any(c => !char.IsAsciiLetterOrDigit(c)))
                    throw new RoomProfileEditRejectedException(exactCode ? "房间号须为 1–128 位字母、数字、下划线或短横线" : "请输入服务器上的 6 位房间号");
                if (config.RoomProfiles.Any(p => p != selected && p.RoomCode == code && RoomServerAddress.Same(config.GetRoomServerUrl(p), server)))
                    throw new RoomProfileEditRejectedException("此服务器上的这个房间已保存，请编辑已有档案");
            }
        }
        return (label, server, code, active, index);
    }

    private async Task<AssistantRoomProfile> SaveAssistantRoomProfileAsync(AssistConfig config, AssistantRoomProfile? selected,
        AssistantRoomDraft draft, bool create, string? expectedRevision = null, Func<bool>? stillAuthorized = null)
    {
        void VerifyEditable()
        {
            if (stillAuthorized?.Invoke() == false)
                throw new RoomProfileEditRejectedException("原网页授权或执行端已变化，本次未保存");
            VerifyAssistantRoomProfileEditable(config, expectedRevision);
        }
        if (!_roomProfileEditGate.Wait(0)) throw new RoomProfileEditRejectedException("另一个房间档案正在保存，请稍后重新读取");
        string? createdRoomCode = null;
        try
        {
            VerifyEditable();
            if (!TryEnterConfigTransferOperation("room_profile_edit", out var lease))
                throw new RoomProfileEditRejectedException(ConfigTransferBlockedMessage);
            using var transfer = lease;
            var validated = ValidateAssistantRoomDraft(config, selected, draft, create);
            if (create)
            {
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                await using var gateway = new MhaGatewayClient();
                await gateway.ConnectAsync(validated.Server, config.BypassSystemProxy, timeout.Token);
                VerifyEditable();
                // The connection may have suspended the UI; check ownership, limits and the edited
                // revision again before sending the server-side mutation, not only at final save.
                ValidateAssistantRoomDraft(config, selected, draft, create: true);
                var reply = await gateway.InvokeCommandAsync("control.createRoom", new
                { password = draft.Password, playerUid = config.PlayerUid, allowedUids = draft.MemberUids }, ct: timeout.Token);
                if (reply.Payload?["success"]?.GetValue<bool>() == false)
                    throw new RoomProfileEditRejectedException(reply.GetString("reason", "服务器拒绝建房"), "服务器拒绝建房，请核对服务器及建房条件");
                if (reply.Payload?["success"]?.GetValue<bool>() != true)
                    throw new IOException("服务器未确认建房结果，请核对后再操作");
                var code = reply.GetString("roomCode");
                if (code.Length != 6 || code.Any(c => !char.IsAsciiLetterOrDigit(c)))
                    throw new IOException("服务器确认建房，但未返回有效房间号，请核对后再操作");
                createdRoomCode = code;
                draft = draft with { RoomCode = code };
            }

            AssistantRoomProfile profile;
            lock (_batchGate)
            {
                // Revalidate after async creation and commit active expected-player changes under
                // the same gate used for batch admission. Clone the latest profile, not an old draft.
                VerifyEditable();
                var (label, server, code, active, index) = ValidateAssistantRoomDraft(config, selected, draft, create: false);
                profile = selected is null ? new AssistantRoomProfile { Id = Guid.NewGuid().ToString("N") }
                    : JsonSerializer.Deserialize<AssistantRoomProfile>(JsonSerializer.Serialize(selected))!;
                if (selected is not null && (!RoomServerAddress.Same(config.GetRoomServerUrl(selected), server) || selected.RoomCode != code))
                {
                    profile.ScheduledOnlineLastFireDate = null;
                    profile.ScheduledOnlineRequiresConfirmation = !string.IsNullOrEmpty(profile.ScheduledOnlineTime);
                }
                profile.Name = label;
                profile.Note = draft.Note;
                profile.ServerUrl = active ? config.ServerUrl : server;
                profile.RoomCode = code;
                profile.Password = draft.Password;
                profile.TeamUids = [.. draft.MemberUids];
                profile.ExpectedHoeingPlayers = draft.ExpectedPlayers;
                var previousCount = config.ExpectedHoeingPlayers;
                var previousLocalId = config.LocalRoomProfileId;
                var firstRoom = selected is null && config.RoomProfiles.Count == 0 && string.IsNullOrEmpty(previousLocalId);
                if (index >= 0) config.RoomProfiles[index] = profile;
                else config.RoomProfiles.Add(profile);
                if (firstRoom) config.LocalRoomProfileId = profile.Id;
                if (active) config.ExpectedHoeingPlayers = draft.ExpectedPlayers;
                if (!_configManager!.TrySave(config))
                {
                    if (selected is not null) config.RoomProfiles[index] = selected;
                    else config.RoomProfiles.Remove(profile);
                    config.LocalRoomProfileId = previousLocalId;
                    config.ExpectedHoeingPlayers = previousCount;
                    throw new RoomProfileEditRejectedException("档案未能保存，原设置保留");
                }
            }
            // A UI notification failure after persistence must not be reported as an unsaved
            // server room. The operation receipt is unknown if its final snapshot cannot be read.
            createdRoomCode = null;
            NotifyRoomProfiles();
            if (profile.Id == config.ActiveRoomProfileId) ObserveOnlineTask(ReportStatusAsync(), "更新当前房间人数");
            return profile;
        }
        catch (Exception ex) when (createdRoomCode is not null)
        { throw new RoomProfileCreateIncompleteException(createdRoomCode, ex); }
        finally { _roomProfileEditGate.Release(); }
    }

    private void RemoveAssistantRoomProfile(AssistConfig config, AssistantRoomProfile selected, string? expectedRevision = null)
    {
        if (!_roomProfileEditGate.Wait(0)) throw new RoomProfileEditRejectedException("另一个房间档案正在保存，请稍后重新读取");
        try
        {
            VerifyAssistantRoomProfileEditable(config, expectedRevision);
            var localId = EffectiveLocalRoomProfileId(config);
            if (string.IsNullOrEmpty(localId) || config.RoomProfiles.Count(p => p.Id == localId) != 1)
                throw new RoomProfileEditRejectedException("本机设置的房间无法确认，暂不能移除档案");
            if (selected.Id == localId) throw new RoomProfileEditRejectedException("本机房间不能删除");
            if (selected.Id == config.ActiveRoomProfileId) throw new RoomProfileEditRejectedException("请先切到另一个房间，再移除此档案");
            if (!TryEnterConfigTransferOperation("room_profile_edit", out var lease))
                throw new RoomProfileEditRejectedException(ConfigTransferBlockedMessage);
            using var transfer = lease;
            var index = config.RoomProfiles.IndexOf(selected);
            if (index < 0) throw new RoomProfileEditRejectedException("该档案已被移除或更新");
            var previousViewedId = config.ViewedRoomProfileId;
            if (string.Equals(config.ViewedRoomProfileId, selected.Id, StringComparison.Ordinal))
                config.ViewedRoomProfileId = AssistConfig.AllRoomsViewId;
            config.RoomProfiles.RemoveAt(index);
            if (!_configManager!.TrySave(config))
            {
                config.RoomProfiles.Insert(index, selected);
                config.ViewedRoomProfileId = previousViewedId;
                throw new RoomProfileEditRejectedException("档案未能移除，原设置保留");
            }
            NotifyRoomProfiles();
        }
        finally { _roomProfileEditGate.Release(); }
    }
}
