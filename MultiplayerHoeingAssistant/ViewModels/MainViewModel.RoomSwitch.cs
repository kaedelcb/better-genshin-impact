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
    private SignalRClient? _roomPreviewClient;
    private long _roomSwitchCommittedAtUtc;
    private string _roomSwitchStatus = "";
    private static string RoomSwitchRecordPath => Path.Combine(AssistantDataDirectory.Root, "room-switch-pending.json");

    public IReadOnlyList<AssistantRoomProfile> RoomSwitchChoices => _config?.RoomProfiles ?? [];
    public AssistantRoomProfile? ActiveRoomProfile => _config?.ActiveRoomProfile;
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

    private void NotifyRoomProfiles()
    {
        OnPropertyChanged(nameof(RoomSwitchChoices));
        OnPropertyChanged(nameof(ActiveRoomProfile));
        OnPropertyChanged(nameof(Config));
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

    private async Task<FlowRoomSwitchResult> SwitchAssistantRoomCoreAsync(string profileId,
        Func<CancellationToken, Task>? verifySource, string? expectedSourceRoom, CancellationToken ct,
        bool waitForOnlineRound = false)
    {
        if (ct.IsCancellationRequested) return new(false, false, "切房已取消，未提交");
        if (!_assistantRoomSwitchGate.Wait(0)) return new(false, false, "切换忙");
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
            var source = config.ActiveRoomProfile;
            if (config.RoomProfiles.SingleOrDefault(p => p.Id == profileId) is not { } target)
                return new(false, false, "找不到目标房间档案");
            if (!RoomServerAddress.TryNormalize(config.GetRoomServerUrl(target), out var targetServer))
                return new(false, false, "目标档案的服务器地址无效，请在管理房间中核对");
            var old = _signalRClient;
            var identity = GetAssistantRoomIdentity();
            var firstRoom = source is null && old is null && string.IsNullOrEmpty(RoomCode);
            if ((!firstRoom && (old is null || identity is null || source is null))
                || expectedSourceRoom is not null && expectedSourceRoom != identity)
                return new(false, false, "原房间连接已变化，未切换");
            if (source?.Id != target.Id) verifySource ??= await CaptureManualRoomSwitchStopGuardAsync(token);
            if (verifySource is not null) await verifySource(token);
            token.ThrowIfCancellationRequested();
            if (source?.Id == target.Id) return new(true, false, "已在目标房间", identity);

            // An explicit return after BGI online can wait through the current batch. Independent
            // nodes have a shorter admission budget and never cancel the work they're waiting on.
            using var wait = CancellationTokenSource.CreateLinkedTokenSource(token);
            wait.CancelAfter(waitForOnlineRound ? TimeSpan.FromHours(6) : TimeSpan.FromSeconds(45));
            var comparedTarget = (target.RoomCode, target.Password, Uids: string.Join('\n', target.TeamUids));
            var record = new RoomSwitchRecord(Guid.NewGuid().ToString("N"), config.ServerUrl, config.PlayerUid,
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
                var previous = source ?? new AssistantRoomProfile();
                previous.Capture(config);
                var previousTargetFireDate = target.ScheduledOnlineLastFireDate;
                var now = DateTime.Now;
                if (source is not null && TimeSpan.TryParse(target.ScheduledOnlineTime, out var schedule)
                    && now >= now.Date.Add(schedule) && target.ScheduledOnlineLastFireDate?.Date != now.Date)
                    target.ScheduledOnlineLastFireDate = now.Date; // A room that was inactive at its scheduled time does not catch up on switch.
                target.ApplyTo(config);
                config.ServerUrl = targetServer;
                config.ActiveRoomProfileId = target.Id;
                if (!_configManager.TrySave(config))
                {
                    target.ScheduledOnlineLastFireDate = previousTargetFireDate;
                    previous.ApplyTo(config);
                    config.ActiveRoomProfileId = originalId;
                    throw new IOException("目标档案未写入，切换结果待核对");
                }
                if (source is not null) _roomScheduleSuppressed[source.Id] = oldSuppression;
                _roomCode = target.RoomCode;
                Interlocked.Increment(ref _controlContextVersion);
                candidate.ControlContext = CurrentControlContext();
                candidate.CoordinateRoomJoin = CoordinateControlRoomJoinAsync;
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
            ObserveOnlineTask(committed.RestoreCoordinatedAuthorityAsync(), "核对目标房间联机批次");
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
        var completionConfig = new AssistConfig();
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
            HoeingCompletionAction = CoordinatedCompletionAction.FromSettings(CompletionPolicyFields.Snapshot(completionConfig)),
            TaskStatusAvailable = local?.HasFreshTaskStatus(DateTimeOffset.UtcNow) == true,
            TaskRunning = local?.TaskRunning == true, CurrentTaskName = local?.CurrentTaskName,
        };
    }

    private readonly Dictionary<string, bool> _roomScheduleSuppressed = new(StringComparer.Ordinal);

    private async Task RecheckAssistantRoomSwitchAsync()
    {
        if (!await _assistantRoomSwitchGate.WaitAsync(0)) return;
        try
        {
            if (_unconfirmedRoomSwitch is not { } record || _config is not { StandaloneMode: false } config) return;
            // In-flight records from the old shared-address version have no TargetServerUrl.
            var targetServer = record.TargetServerUrl ?? record.ServerUrl;
            if (record.Uid != config.PlayerUid || AssistantRoomHasBatch()
                || !((config.ActiveRoomProfileId == record.OriginalProfileId && RoomServerAddress.Same(config.ServerUrl, record.ServerUrl))
                    || (config.ActiveRoomProfileId == record.TargetProfileId && RoomServerAddress.Same(config.ServerUrl, targetServer))))
            { SetRoomSwitchStatus("切换结果待核对：原执行身份已变化或联机尚未收尾"); return; }
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
                await using var probe = new MhaGatewayClient();
                await probe.ConnectAsync(server, config.BypassSystemProxy, budget.Token);
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
            ClearAssistantRoomSwitchRecord();
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
        if (!preview && (client.CurrentRoomCode != RoomCode || !RoomServerAddress.Same(client.CurrentServerUrl, _config?.ServerUrl))) return false;
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

    private Task EditAssistantRoomsAsync()
    {
        using var roomEffect = TryEnterAssistantRoomEffect();
        if (roomEffect is null || RoomSwitchHasPendingRequest || !CanEditConfiguration
            || _config is not { } config || _configManager is null) return Task.CompletedTask;
        config.FillLegacyRoomServerUrls();
        var window = new AssistantRoomProfilesWindow(config.RoomProfiles.ToList(), config.ActiveRoomProfileId,
            config.ServerUrl, config.PlayerUid, SaveProfileAsync, RemoveProfile)
        { Owner = Application.Current.MainWindow };
        window.ShowDialog();
        return Task.CompletedTask;

        void VerifyEditable()
        {
            if (_disposing || RoomSwitchHasPendingRequest || RoomSwitchBlocksRoomOperations || !CanEditConfiguration
                || !ReferenceEquals(config, _config))
                throw new InvalidOperationException("房间或本机设置已变化，请关闭后重新打开房间管理");
        }

        async Task SaveProfileAsync(AssistantRoomProfile? selected, AssistantRoomDraft draft, bool create)
        {
            VerifyEditable();
            if (!TryEnterConfigTransferOperation("room_profile_edit", out var lease))
                throw new InvalidOperationException(ConfigTransferBlockedMessage);
            using var transfer = lease;
            var label = draft.Name.Trim();
            if (label.Length is 0 or > 64 || config.RoomProfiles.Any(p => p != selected
                && string.Equals(p.Name, label, StringComparison.OrdinalIgnoreCase)))
                throw new InvalidOperationException("档案名称必须填写，并且不能与本机其他房间重名");
            if (!RoomServerAddress.TryNormalize(draft.ServerUrl, out var server))
                throw new InvalidOperationException("服务器地址必须是有效的 http 或 https 基地址");
            if (draft.ExpectedPlayers is < 1 or > 4)
                throw new InvalidOperationException("预期开锄人数必须是 1–4 人");
            var code = draft.RoomCode.Trim().ToUpperInvariant();
            var uids = draft.MemberUids;
            var active = selected is not null && selected.Id == config.ActiveRoomProfileId;
            var index = selected is null ? -1 : config.RoomProfiles.IndexOf(selected);
            if (selected is not null && index < 0)
                throw new InvalidOperationException("该档案已被移除");
            if (active)
            {
                if (create || selected!.RoomCode != code || !RoomServerAddress.Same(config.ServerUrl, server)
                    || draft.Password != config.ControlRoomPassword || !uids.SequenceEqual(config.TeamUids, StringComparer.Ordinal))
                    throw new InvalidOperationException("不能在这里改变正在使用的服务器或入房凭据，可添加另一个房间档案后切换");
                if (draft.ExpectedPlayers != config.ExpectedHoeingPlayers && (AssistantRoomHasBatch() || _intentLifecycle.IsOnlineReady))
                    throw new InvalidOperationException("当前房间正在等待开锄或执行任务，请等本轮结束再修改开锄人数");
            }
            else
            {
                if (selected is null && config.RoomProfiles.Count >= 5)
                    throw new InvalidOperationException("本机最多保存 5 个房间档案");
                if (!AssistantRoomDraft.HasValidMemberUids(uids, config.PlayerUid))
                    throw new InvalidOperationException("请填写有效的成员 UID，并将本机玩家的 UID 加入名单");
                if (string.IsNullOrWhiteSpace(draft.Password) || draft.Password.Length > 128)
                    throw new InvalidOperationException("请填写房间密码，最多 128 个字符");
                if (create)
                {
                    if (selected is not null || code.Length != 0 || config.StandaloneMode)
                        throw new InvalidOperationException("请在联网模式添加新档案，并将房间号留空后建房");
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                    await using var gateway = new MhaGatewayClient();
                    await gateway.ConnectAsync(server, config.BypassSystemProxy, timeout.Token);
                    var reply = await gateway.InvokeCommandAsync("control.createRoom", new
                    { password = draft.Password, playerUid = config.PlayerUid, allowedUids = uids }, ct: timeout.Token);
                    if (reply.Payload?["success"]?.GetValue<bool>() != true)
                        throw new InvalidOperationException(reply.GetString("reason", "服务器未确认建房"));
                    code = reply.GetString("roomCode");
                    VerifyEditable();
                }
                if (code.Length != 6 || code.Any(c => !char.IsAsciiLetterOrDigit(c)))
                    throw new InvalidOperationException("请输入服务器上的 6 位房间号");
                if (config.RoomProfiles.Any(p => p != selected && p.RoomCode == code && RoomServerAddress.Same(config.GetRoomServerUrl(p), server)))
                    throw new InvalidOperationException("此服务器上的这个房间已保存，请编辑已有档案");
            }

            var profile = selected is null ? new AssistantRoomProfile { Id = Guid.NewGuid().ToString("N") }
                : JsonSerializer.Deserialize<AssistantRoomProfile>(JsonSerializer.Serialize(selected))!;
            if (selected is not null && (!RoomServerAddress.Same(config.GetRoomServerUrl(selected), server) || selected.RoomCode != code))
            {
                profile.ScheduledOnlineLastFireDate = null;
                profile.ScheduledOnlineRequiresConfirmation = !string.IsNullOrEmpty(profile.ScheduledOnlineTime);
            }
            profile.Name = label;
            profile.ServerUrl = active ? config.ServerUrl : server;
            profile.RoomCode = code;
            profile.Password = draft.Password;
            profile.TeamUids = uids;
            profile.ExpectedHoeingPlayers = draft.ExpectedPlayers;
            var previousCount = config.ExpectedHoeingPlayers;
            if (index >= 0) config.RoomProfiles[index] = profile;
            else config.RoomProfiles.Add(profile);
            if (active) config.ExpectedHoeingPlayers = draft.ExpectedPlayers;
            if (!_configManager.TrySave(config))
            {
                if (selected is not null) config.RoomProfiles[index] = selected;
                else config.RoomProfiles.Remove(profile);
                config.ExpectedHoeingPlayers = previousCount;
                throw new IOException(create ? "房间已创建（" + code + "），但档案未能保存。请用房间号添加已有房间。" : "档案未能保存，原设置保留");
            }
            NotifyRoomProfiles();
            if (active) ObserveOnlineTask(ReportStatusAsync(), "更新当前房间人数");
        }

        void RemoveProfile(AssistantRoomProfile selected)
        {
            VerifyEditable();
            if (selected.Id == config.ActiveRoomProfileId) throw new InvalidOperationException("请先切到另一个房间，再移除此档案");
            if (!TryEnterConfigTransferOperation("room_profile_edit", out var lease))
                throw new InvalidOperationException(ConfigTransferBlockedMessage);
            using var transfer = lease;
            var index = config.RoomProfiles.IndexOf(selected);
            if (index < 0) return;
            config.RoomProfiles.RemoveAt(index);
            if (!_configManager.TrySave(config))
            {
                config.RoomProfiles.Insert(index, selected);
                throw new IOException("档案未能移除，原设置保留");
            }
            NotifyRoomProfiles();
        }
    }
}
