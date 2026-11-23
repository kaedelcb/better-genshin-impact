using System.IO;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Services.Gateway;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private sealed record RoomSwitchRecord(string RequestId, string ServerUrl, string Uid,
        string OriginalProfileId, string TargetProfileId, string OriginalRoom, string TargetRoom, bool ReadyAttempted = false);

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
        var connectionGate = false;
        var restartSource = false;
        try
        {
            if (_disposing || IsStandaloneMode || _config is not { } config || _configManager is null)
                return new(false, false, "单机模式或配置未就绪，未切换房间");
            if (RoomSwitchBlocksRoomOperations) return new(false, true, "切换结果待核对");
            if (!TryEnterConfigTransferOperation("room_switch", out var transfer))
                return new(false, false, ConfigTransferBlockedMessage);
            using var transferOperation = transfer;
            var source = config.ActiveRoomProfile;
            if (config.RoomProfiles.SingleOrDefault(p => p.Id == profileId) is not { } target)
                return new(false, false, "找不到目标房间档案");
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
                source?.Id ?? "", target.Id, source?.RoomCode ?? "", target.RoomCode);
            SetRoomSwitchStatus("等待切换到「" + target.Name + "」");
            while (true)
            {
                wait.Token.ThrowIfCancellationRequested();
                if (verifySource is not null) await verifySource(wait.Token);
                if (identity != GetAssistantRoomIdentity() || !ReferenceEquals(config, _config) || IsStandaloneMode
                    || !config.RoomProfiles.Contains(target) || comparedTarget != (target.RoomCode, target.Password, string.Join('\n', target.TeamUids)))
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
            await candidate.ConnectAsync(config.ServerUrl, target.RoomCode, target.Password, config.PlayerUid,
                config.PlayerName, [.. target.TeamUids], config.ObserverMode, config.ClientInstanceId,
                config.BypassSystemProxy, joinBudget.Token);
            _roomPreviewClient = candidate;
            candidate.ApplyRoomSnapshot(candidate.PreparedRoomSnapshot!);
            if (verifySource is not null) await verifySource(joinBudget.Token);
            RecordAssistantRoomSwitch(record with { ReadyAttempted = true });
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
                if (!ReferenceEquals(_config, config) || IsStandaloneMode || _disposing)
                    throw new OperationCanceledException("设置或运行模式已变化");
                var previous = source ?? new AssistantRoomProfile();
                previous.Capture(config);
                var now = DateTime.Now;
                if (source is not null && TimeSpan.TryParse(target.ScheduledOnlineTime, out var schedule)
                    && now >= now.Date.Add(schedule) && target.ScheduledOnlineLastFireDate?.Date != now.Date)
                    target.ScheduledOnlineLastFireDate = now.Date; // A room that was inactive at its scheduled time does not catch up on switch.
                target.ApplyTo(config);
                config.ActiveRoomProfileId = target.Id;
                if (!_configManager.TrySave(config))
                {
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
                _signalRClient = candidate;
                candidate.ApplyRoomSnapshot(ready);
                candidate.CompleteRoomSwitch();
                _controlAuthority.Publish(candidate, candidate.ControlContext);
                ClearAssistantRoomSwitchRecord();
                IsConnected = true;
                OnPropertyChanged(nameof(RoomCode));
                NotifyRoomProfiles();
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
        catch (SignalRClient.RoomJoinRejectedException ex) when (confirmedLeave)
        {
            // A business rejection before preparation registered no target execution rights.
            try { ClearAssistantRoomSwitchRecord(); restartSource = true; }
            catch (Exception storageError)
            {
                SetRoomSwitchStatus("切换结果待核对：" + storageError.Message);
                return new(false, true, RoomSwitchStatus);
            }
            SetRoomSwitchStatus("入房失败，尝试恢复原房间：" + ex.Message);
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
            if (_unconfirmedRoomSwitch is not { } record || _config is not { } config || IsStandaloneMode) return;
            if (record.ServerUrl != config.ServerUrl || record.Uid != config.PlayerUid || AssistantRoomHasBatch())
            { SetRoomSwitchStatus("切换结果待核对：原执行身份已变化或联机尚未收尾"); return; }
            using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            var retainedSource = _signalRClient;
            ControlRoomPlayersUpdate? retainedSourceSnapshot = null;
            await using var probe = new MhaGatewayClient();
            await probe.ConnectAsync(MhaGatewayClient.NormalizeBaseUrl(config.ServerUrl, out _), config.BypassSystemProxy, budget.Token);
            foreach (var (id, code) in new[] { (record.OriginalProfileId, record.OriginalRoom), (record.TargetProfileId, record.TargetRoom) })
            {
                if (string.IsNullOrEmpty(code) || code == record.TargetRoom && !record.ReadyAttempted) continue;
                var profile = config.RoomProfiles.SingleOrDefault(p => p.Id == id && p.RoomCode == code);
                if (profile is null) throw new InvalidOperationException("原房间档案已变化");
                var reply = await probe.InvokeCommandAsync("control.roomAccess", new { roomCode = code, password = profile.Password, playerUid = config.PlayerUid }, ct: budget.Token);
                if (reply.Payload?["success"]?.GetValue<bool>() != true || reply.Payload?["busy"]?.GetValue<bool>() != false
                    || reply.Payload?["joined"] is null)
                    throw new InvalidOperationException("房间 " + code + " 未确认释放批次责任");
                if (reply.Payload["joined"]!.GetValue<bool>())
                {
                    if (code != record.OriginalRoom || retainedSource?.CurrentRoomCode != code
                        || await retainedSource.ReadRoomSnapshotAsync(budget.Token) is not { Full: true } snapshot)
                        throw new InvalidOperationException("房间 " + code + " 未确认释放成员资格");
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
        if (_disposing || client.IsRetired || client.CurrentRoomCode != RoomCode && !ReferenceEquals(client, _roomPreviewClient)) return false;
        var preview = ReferenceEquals(client, _roomPreviewClient);
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

    private async Task EditAssistantRoomsAsync()
    {
        using var roomEffect = TryEnterAssistantRoomEffect();
        if (roomEffect is null || RoomSwitchHasPendingRequest || RoomSwitchBlocksRoomOperations || !CanEditConfiguration
            || _config is not { } config || _configManager is null) return;
        var window = new Window { Title = "助手房间档案", Width = 440, Height = 410,
            Owner = Application.Current.MainWindow, WindowStartupLocation = WindowStartupLocation.CenterOwner,
            ResizeMode = ResizeMode.NoResize };
        var layout = new StackPanel { Margin = new Thickness(16) };
        var selector = new ComboBox { ItemsSource = RoomSwitchChoices.ToList(), DisplayMemberPath = nameof(AssistantRoomProfile.Name),
            SelectedItem = config.ActiveRoomProfile, Margin = new Thickness(0, 0, 0, 8) };
        var name = new TextBox();
        var code = new TextBox();
        var password = new PasswordBox();
        var members = new TextBox();
        var result = new TextBlock { TextWrapping = TextWrapping.Wrap, Margin = new Thickness(0, 8, 0, 0) };
        void Row(string caption, Control control) { layout.Children.Add(new TextBlock { Text = caption }); layout.Children.Add(control); }
        void Fill(AssistantRoomProfile? entry)
        {
            name.Text = entry?.Name ?? ""; code.Text = entry?.RoomCode ?? "";
            password.Password = entry?.Password ?? ""; members.Text = entry is null ? "" : string.Join(",", entry.TeamUids);
        }
        selector.SelectionChanged += (_, _) => Fill(selector.SelectedItem as AssistantRoomProfile);
        layout.Children.Add(selector);
        Row("名称（供 BGI 配置组引用）", name);
        Row("房间号（创建时留空）", code);
        Row("房间密码", password);
        Row("成员 UID（逗号分隔）", members);
        var panel = new WrapPanel { Margin = new Thickness(0, 10, 0, 0) };
        void Button(string caption, RoutedEventHandler handler)
        {
            var button = new Button { Content = caption, Margin = new Thickness(0, 0, 8, 4), Padding = new Thickness(6, 3, 6, 3) };
            button.Click += handler; panel.Children.Add(button);
        }
        async Task Save(string action)
        {
            if (RoomSwitchHasPendingRequest || RoomSwitchBlocksRoomOperations || !ReferenceEquals(config, _config)
                || !TryEnterConfigTransferOperation("room_profile_edit", out var lease)) return;
            using var transfer = lease;
            panel.IsEnabled = false;
            try
            {
                var selected = selector.SelectedItem as AssistantRoomProfile;
                var label = name.Text.Trim();
                if (label.Length is 0 or > 64 || config.RoomProfiles.Any(p => (action != "rename" || p != selected)
                    && string.Equals(p.Name, label, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidOperationException("房间名称须非空且在本机唯一");
                if (action == "rename")
                {
                    if (selected is null) throw new InvalidOperationException("请选择要改名的档案");
                    var previous = selected.Name;
                    selected.Name = label;
                    if (!_configManager.TrySave(config)) { selected.Name = previous; throw new IOException("档案未保存"); }
                }
                else
                {
                    if (config.RoomProfiles.Count >= 5) throw new InvalidOperationException("本机最多保存 5 个房间档案");
                    var roomCode = code.Text.Trim().ToUpperInvariant();
                    var secret = password.Password;
                    var uids = members.Text.Split([',', '，', ' ', '\n', '\r'], StringSplitOptions.TrimEntries | StringSplitOptions.RemoveEmptyEntries)
                        .Distinct(StringComparer.Ordinal).ToList();
                    if (uids.Count == 0 || uids.Any(uid => uid.Length is < 1 or > 16 || !uid.All(char.IsAsciiDigit))
                        || !uids.Contains(config.PlayerUid, StringComparer.Ordinal) || string.IsNullOrWhiteSpace(secret))
                        throw new InvalidOperationException("请填写房间密码和包含本机的有效成员 UID");
                    if (action == "create")
                    {
                        if (IsStandaloneMode) throw new InvalidOperationException("请先设置在线模式及服务器地址");
                        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(12));
                        await using var gateway = new MhaGatewayClient();
                        await gateway.ConnectAsync(MhaGatewayClient.NormalizeBaseUrl(config.ServerUrl, out _), config.BypassSystemProxy, timeout.Token);
                        var reply = await gateway.InvokeCommandAsync("control.createRoom", new
                        { password = secret, playerUid = config.PlayerUid, allowedUids = uids }, ct: timeout.Token);
                        if (reply.Payload?["success"]?.GetValue<bool>() != true)
                            throw new InvalidOperationException(reply.GetString("reason", "服务端未确认建房"));
                        roomCode = reply.GetString("roomCode");
                    }
                    if (roomCode.Length != 6 || roomCode.Any(c => !char.IsAsciiLetterOrDigit(c))
                        || config.RoomProfiles.Any(p => p.RoomCode == roomCode))
                        throw new InvalidOperationException("请输入不同的 6 位房间号");
                    var profile = new AssistantRoomProfile { Id = Guid.NewGuid().ToString("N"), Name = label,
                        RoomCode = roomCode, Password = secret, TeamUids = uids, ExpectedHoeingPlayers = Math.Clamp(uids.Count, 1, 4) };
                    config.RoomProfiles.Add(profile);
                    if (!_configManager.TrySave(config)) { config.RoomProfiles.Remove(profile); throw new IOException("档案未保存"); }
                }
                NotifyRoomProfiles();
                window.Close();
            }
            catch (Exception ex) { result.Text = ex.Message; }
            finally { panel.IsEnabled = true; }
        }
        Button("新建档案", (_, _) => { selector.SelectedItem = null; Fill(null); });
        Button("建房并保存", async (_, _) => await Save("create"));
        Button("保存已有房间", async (_, _) => await Save("existing"));
        Button("保存新名称", async (_, _) => await Save("rename"));
        Button("移除本机档案", (_, _) =>
        {
            if (RoomSwitchHasPendingRequest || RoomSwitchBlocksRoomOperations || !CanEditConfiguration
                || selector.SelectedItem is not AssistantRoomProfile selected || selected.Id == config.ActiveRoomProfileId) return;
            if (MessageBox.Show(window, "移除此本机档案？服务器和历史记录不变。", "移除房间档案", MessageBoxButton.YesNo) != MessageBoxResult.Yes) return;
            var index = config.RoomProfiles.IndexOf(selected);
            if (index < 0) return;
            config.RoomProfiles.RemoveAt(index);
            if (!_configManager.TrySave(config)) { config.RoomProfiles.Insert(index, selected); result.Text = "档案未保存"; return; }
            NotifyRoomProfiles(); window.Close();
        });
        layout.Children.Add(panel); layout.Children.Add(result); window.Content = layout;
        Fill(selector.SelectedItem as AssistantRoomProfile);
        window.ShowDialog();
        await Task.CompletedTask;
    }
}
