using System.Collections.ObjectModel;
using System.Globalization;
using System.Windows;
using MultiplayerHoeingAssistant.Models;
using MultiplayerHoeingAssistant.Services;
using MultiplayerHoeingAssistant.Views;

namespace MultiplayerHoeingAssistant.ViewModels;

public partial class MainViewModel
{
    private readonly Dictionary<string, IRoomSpectatorSession> _roomSpectators = new(StringComparer.Ordinal);
    private readonly Dictionary<(string ProfileId, string PlayerUid), MemberViewModel> _spectatorMemberCache = new();
    private readonly Dictionary<string, (string File, string Ring)> _spectatorAvatars = new(StringComparer.Ordinal);
    private readonly Dictionary<string, (string ProfileId, string TargetUid, string Cmd, string TaskName)> _otherRoomPendingCommandResults = new(StringComparer.Ordinal);
    private readonly object _otherRoomResetAckGate = new();
    private readonly Dictionary<string, TaskCompletionSource<CommandResult>> _otherRoomPendingResetAcks = new(StringComparer.Ordinal);
    private readonly object _otherRoomPolicySyncGate = new();
    private readonly List<TaskPolicySyncService> _otherRoomPolicySyncs = [];
    private int _spectatorAvatarCursor;

    /// <summary>测试注入用工厂：未设置时使用真实 SignalR 旁观连接。</summary>
    internal Func<AssistantRoomProfile, string, bool, IRoomSpectatorSession>? RoomSpectatorFactory { get; set; }

    /// <summary>耕地机主页面当前实际展示的成员集合（可能是登录房间、某个只读房间、或「所有房间」去重汇总）。</summary>
    public ObservableCollection<MemberViewModel> DisplayedMembers { get; } = new();

    /// <summary>当前查看的房间视图 ID：AssistConfig.AllRoomsViewId 表示「所有房间」，否则为具体房间档案 ID。</summary>
    public string ViewedRoomProfileId
    {
        get
        {
            var config = RoomViewConfig;
            var raw = config?.ViewedRoomProfileId;
            if (string.IsNullOrWhiteSpace(raw) || string.Equals(raw, AssistConfig.AllRoomsViewId, StringComparison.Ordinal))
                return AssistConfig.AllRoomsViewId;
            if (config?.RoomProfiles.Any(p => string.Equals(p.Id, raw, StringComparison.Ordinal)) == true)
                return raw;
            return AssistConfig.AllRoomsViewId;
        }
    }

    public bool IsViewingAllRooms => string.Equals(ViewedRoomProfileId, AssistConfig.AllRoomsViewId, StringComparison.Ordinal);

    public AssistantRoomProfile? ViewedRoomProfile => IsViewingAllRooms
        ? null
        : RoomViewConfig?.RoomProfiles.FirstOrDefault(p => string.Equals(p.Id, ViewedRoomProfileId, StringComparison.Ordinal));

    public bool IsViewingActiveRoom => !IsViewingAllRooms
        && ViewedRoomProfile is not null
        && string.Equals(ViewedRoomProfile.Id, RoomViewConfig?.ActiveRoomProfileId, StringComparison.Ordinal);

    public bool IsViewingReadOnlyRoom => !IsViewingAllRooms
        && ViewedRoomProfile is not null
        && !IsViewingActiveRoom;

    /// <summary>底部快捷指令是否允许对当前视图执行（其他房间或编排页不执行批量快捷指令）。</summary>
    public bool CanOperateDisplayedRoom => !IsShowingRoomOrchestration && !IsViewingReadOnlyRoom;
    public bool IsDisplayingAllRoomsView => !IsShowingRoomOrchestration && IsViewingAllRooms;
    public bool IsHeaderViewingReadOnlyRoom => !IsShowingRoomOrchestration && IsViewingReadOnlyRoom;

    public string HeaderViewingRoomName => IsShowingRoomOrchestration ? "任务编排" : IsViewingAllRooms
        ? "所有房间"
        : TruncateRoomLabel(ViewedRoomProfile?.Name, 4, "未选择");

    public string HeaderViewingRoomFullName => IsShowingRoomOrchestration ? "任务编排" : IsViewingAllRooms
        ? "所有房间"
        : string.IsNullOrWhiteSpace(ViewedRoomProfile?.Name) ? "未选择" : ViewedRoomProfile!.Name;

    public string HeaderViewingRoomToolTip => IsShowingRoomOrchestration
        ? "任务编排：本机各房间的联机绑定、完成动作和最近确认的接续状态。打开房间面板可返回成员列表。"
        : IsViewingAllRooms
            ? "当前耕地机页面正在查看：所有房间（其他房间成员支持单卡片远控；底部快捷指令作用于当前登录房间）"
            : IsViewingActiveRoom
                ? $"当前耕地机页面正在查看：{HeaderViewingRoomFullName}（当前登录房间）"
                : $"当前耕地机页面正在查看：{HeaderViewingRoomFullName}（支持单卡片远控；底部快捷指令仅作用于当前登录房间）";

    public string AllRoomsCountText => RoomViewConfig?.RoomProfiles is { Count: > 0 } rooms
        ? $"{rooms.Count} 个房间"
        : "暂无房间";

    public string RoomViewNoticeText => string.Join("\n", new[] { RoomViewPreferenceMessage, RoomConnectionsNoticeText }
        .Where(text => !string.IsNullOrWhiteSpace(text)));

    private string RoomConnectionsNoticeText
    {
        get
        {
            if (_config?.RoomProfiles is not { Count: > 0 }) return "";
            if (IsViewingReadOnlyRoom && ViewedRoomProfile is { } target)
            {
                if (IsStandaloneMode) return $"当前为单机模式，无法实时查看或远控「{target.Name}」。";
                if (_roomSpectators.TryGetValue(target.Id, out var session))
                {
                    var hasPlayers = session.GetPlayersSnapshot().Count > 0;
                    if (session.IsConnecting && !hasPlayers) return $"正在连接「{target.Name}」实时视图…";
                    if (!string.IsNullOrWhiteSpace(session.Error) && !hasPlayers)
                        return $"「{target.Name}」连接暂未就绪：{session.Error}";
                }
                return "";
            }
            if (IsViewingAllRooms && _config.RoomProfiles.Count > 1)
            {
                if (IsStandaloneMode) return "当前为单机模式，无法实时查看或远控其他房间。";
                var failed = new List<string>();
                foreach (var profile in _config.RoomProfiles)
                {
                    if (string.Equals(profile.Id, ActiveRoomProfile?.Id, StringComparison.Ordinal)) continue;
                    if (_roomSpectators.TryGetValue(profile.Id, out var session)
                        && !string.IsNullOrWhiteSpace(session.Error)
                        && !session.IsConnecting
                        && session.GetPlayersSnapshot().Count == 0)
                    {
                        failed.Add($"{profile.Name}（{session.Error}）");
                    }
                }
                if (failed.Count > 0) return "部分房间连接未就绪：" + string.Join("；", failed);
            }
            return "";
        }
    }

    public RelayCommand SelectAllRoomsViewCommand => new(_ => SelectViewedRoom(AssistConfig.AllRoomsViewId));
    public RelayCommand SelectViewedRoomCommand => new(value =>
    {
        if (value is AssistantRoomProfile profile) SelectViewedRoom(profile.Id);
        else if (value is string id) SelectViewedRoom(id);
    });

    internal static string TruncateRoomLabel(string? name, int maxElements, string fallback)
    {
        if (string.IsNullOrWhiteSpace(name)) return fallback;
        var characters = StringInfo.ParseCombiningCharacters(name);
        return characters.Length <= maxElements ? name : name[..characters[maxElements]];
    }

    /// <summary>切换耕地机当前查看的房间视图（不改变当前登录房间），并持久化记忆。</summary>
    internal void SelectViewedRoom(string? targetId, bool persist = true)
    {
        var normalized = string.IsNullOrWhiteSpace(targetId)
            || string.Equals(targetId, AssistConfig.AllRoomsViewId, StringComparison.Ordinal)
            || _config?.RoomProfiles.Any(p => string.Equals(p.Id, targetId, StringComparison.Ordinal)) != true
            ? AssistConfig.AllRoomsViewId
            : targetId;

        var returningFromOrchestration = IsShowingRoomOrchestration;
        if (_config is not null)
        {
            SetRoomViewPreference(false, normalized, persist);
        }
        if (returningFromOrchestration) CurrentPage = AppPage.Home;

        NotifyRoomViewProperties();
        SyncRoomViewSessions();
        RebuildDisplayedMembers();
    }

    private void NotifyRoomViewProperties()
    {
        OnPropertyChanged(nameof(ViewedRoomProfileId));
        OnPropertyChanged(nameof(IsViewingAllRooms));
        OnPropertyChanged(nameof(ViewedRoomProfile));
        OnPropertyChanged(nameof(IsViewingActiveRoom));
        OnPropertyChanged(nameof(IsViewingReadOnlyRoom));
        NotifyRoomOrchestrationModeProperties();
        OnPropertyChanged(nameof(AllRoomsCountText));
        OnPropertyChanged(nameof(RoomViewNoticeText));
    }

    /// <summary>按当前视图模式与房间档案列表协调只读旁观连接：仅连接需要查看的非登录房间。</summary>
    internal void SyncRoomViewSessions()
    {
        var desired = new Dictionary<string, (AssistantRoomProfile Profile, string ServerUrl)>(StringComparer.Ordinal);
        if (!_disposing && !IsStandaloneMode && _config is { RoomProfiles.Count: > 0 } config
            && (_configManager is not null || RoomSpectatorFactory is not null))
        {
            var activeId = config.ActiveRoomProfileId;
            if (IsViewingAllRooms)
            {
                foreach (var profile in config.RoomProfiles)
                {
                    if (profile is null || string.Equals(profile.Id, activeId, StringComparison.Ordinal)) continue;
                    var serverUrl = config.GetRoomServerUrl(profile);
                    if (!string.IsNullOrWhiteSpace(serverUrl) && !string.IsNullOrWhiteSpace(profile.RoomCode)
                        && !string.IsNullOrEmpty(profile.Password))
                    {
                        desired[profile.Id] = (profile, serverUrl);
                    }
                }
            }
            else if (ViewedRoomProfile is { } viewed && !string.Equals(viewed.Id, activeId, StringComparison.Ordinal))
            {
                var serverUrl = config.GetRoomServerUrl(viewed);
                if (!string.IsNullOrWhiteSpace(serverUrl) && !string.IsNullOrWhiteSpace(viewed.RoomCode)
                    && !string.IsNullOrEmpty(viewed.Password))
                {
                    desired[viewed.Id] = (viewed, serverUrl);
                }
            }
        }

        var bypassProxy = _config?.BypassSystemProxy == true;
        var toDispose = new List<IRoomSpectatorSession>();
        foreach (var existingId in _roomSpectators.Keys.ToList())
        {
            var session = _roomSpectators[existingId];
            if (!desired.TryGetValue(existingId, out var want)
                || !RoomServerAddress.Same(session.ServerUrl, want.ServerUrl)
                || !string.Equals(session.RoomCode, want.Profile.RoomCode, StringComparison.Ordinal)
                || !string.Equals(session.Password, want.Profile.Password, StringComparison.Ordinal)
                || session.BypassSystemProxy != bypassProxy)
            {
                _roomSpectators.Remove(existingId);
                toDispose.Add(session);
                RemoveSpectatorMemberCacheForProfile(existingId);
            }
        }

        foreach (var (profileId, entry) in desired)
        {
            if (_roomSpectators.ContainsKey(profileId)) continue;
            var session = RoomSpectatorFactory?.Invoke(entry.Profile, entry.ServerUrl, bypassProxy)
                ?? new RoomSpectatorSession(profileId, entry.ServerUrl, entry.Profile.RoomCode, entry.Profile.Password, bypassProxy);
            session.Changed += () => OnSpectatorSessionChanged(profileId, session);
            session.ReplyReceived += cmd => OnSpectatorReplyReceived(profileId, session, cmd);
            session.CommandResultReceived += result => OnSpectatorCommandResultReceived(profileId, session, result);
            _roomSpectators[profileId] = session;
            session.Start();
        }

        foreach (var stale in toDispose)
        {
            _ = DisposeSpectatorSessionAsync(stale);
        }

        OnPropertyChanged(nameof(RoomViewNoticeText));
    }

    internal void RefreshRoomSpectators()
    {
        if (_disposing || IsStandaloneMode) return;
        SyncRoomViewSessions();
        foreach (var session in _roomSpectators.Values.ToList())
        {
            session.ForceReconnect();
        }
    }

    internal void StopAllRoomSpectators()
    {
        var sessions = _roomSpectators.Values.ToList();
        _roomSpectators.Clear();
        _spectatorMemberCache.Clear();
        foreach (var session in sessions)
        {
            _ = DisposeSpectatorSessionAsync(session);
        }
        OnPropertyChanged(nameof(RoomViewNoticeText));
    }

    private static async Task DisposeSpectatorSessionAsync(IRoomSpectatorSession session)
    {
        try { await session.DisposeAsync().ConfigureAwait(false); }
        catch { /* 旁观连接释放异常不影响主程序 */ }
    }

    private void RemoveSpectatorMemberCacheForProfile(string profileId)
    {
        foreach (var key in _spectatorMemberCache.Keys.Where(k => string.Equals(k.ProfileId, profileId, StringComparison.Ordinal)).ToList())
        {
            _spectatorMemberCache.Remove(key);
        }
    }

    private void OnSpectatorSessionChanged(string profileId, IRoomSpectatorSession session)
    {
        if (_disposing) return;
        void Apply()
        {
            if (_disposing) return;
            if (!_roomSpectators.TryGetValue(profileId, out var current) || !ReferenceEquals(current, session)) return;
            RebuildDisplayedMembers();
        }

        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) Apply();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(Apply);
    }

    /// <summary>按当前视图模式重建耕地机展示列表（保持 Members 始终只存登录房间成员，不污染联机协调逻辑）。</summary>
    internal void RebuildDisplayedMembers()
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is not null && !dispatcher.HasShutdownStarted && !dispatcher.CheckAccess())
        {
            dispatcher.BeginInvoke(RebuildDisplayedMembers);
            return;
        }

        var profiles = _config?.RoomProfiles;
        var activeProfile = ActiveRoomProfile;
        var activeId = activeProfile?.Id ?? "";
        var activeName = activeProfile?.Name ?? "";
        List<MemberViewModel> desired;

        if (profiles is not { Count: > 0 } || IsViewingActiveRoom)
        {
            foreach (var member in Members)
            {
                member.SourceRoomProfileId = activeId;
                member.RoomLabel = activeName;
                member.IsActiveRoomMember = true;
                member.IsReadOnlyView = false;
                member.ShowRoomLabel = false;
            }
            desired = [.. Members];
        }
        else if (IsViewingReadOnlyRoom && ViewedRoomProfile is { } target)
        {
            desired = BuildSpectatorMembersForProfile(target, showRoomLabel: false);
        }
        else
        {
            // 「所有房间」：所有卡片都显示房间标签；按 UID 去重（在线优先，同态时登录房间优先，其次档案顺序）。
            var orderedProfiles = new List<AssistantRoomProfile>(profiles.Count);
            if (activeProfile is not null) orderedProfiles.Add(activeProfile);
            foreach (var profile in profiles)
            {
                if (profile is null || ReferenceEquals(profile, activeProfile)) continue;
                orderedProfiles.Add(profile);
            }

            var uidOrder = new List<string>();
            var winnerByUid = new Dictionary<string, MemberViewModel>(StringComparer.Ordinal);

            foreach (var profile in orderedProfiles)
            {
                IEnumerable<MemberViewModel> roomMembers;
                if (string.Equals(profile.Id, activeId, StringComparison.Ordinal))
                {
                    foreach (var member in Members)
                    {
                        member.SourceRoomProfileId = profile.Id;
                        member.RoomLabel = profile.Name;
                        member.IsActiveRoomMember = true;
                        member.IsReadOnlyView = false;
                        member.ShowRoomLabel = true;
                    }
                    roomMembers = Members;
                }
                else
                {
                    roomMembers = BuildSpectatorMembersForProfile(profile, showRoomLabel: true);
                }

                foreach (var candidate in roomMembers)
                {
                    if (string.IsNullOrWhiteSpace(candidate.PlayerUid)) continue;
                    if (!winnerByUid.TryGetValue(candidate.PlayerUid, out var existing))
                    {
                        uidOrder.Add(candidate.PlayerUid);
                        winnerByUid[candidate.PlayerUid] = candidate;
                    }
                    else if (!existing.Online && candidate.Online)
                    {
                        winnerByUid[candidate.PlayerUid] = candidate;
                    }
                }
            }

            desired = new List<MemberViewModel>(uidOrder.Count);
            foreach (var uid in uidOrder)
            {
                desired.Add(winnerByUid[uid]);
            }
        }

        ReconcileDisplayedMembers(desired);
        OnPropertyChanged(nameof(RoomViewNoticeText));
    }

    private List<MemberViewModel> BuildSpectatorMembersForProfile(AssistantRoomProfile profile, bool showRoomLabel)
    {
        if (!_roomSpectators.TryGetValue(profile.Id, out var session)) return [];
        var snapshot = session.GetPlayersSnapshot();
        var result = new List<MemberViewModel>(snapshot.Count);
        var aliveUids = new HashSet<string>(StringComparer.Ordinal);

        foreach (var np in snapshot)
        {
            if (string.IsNullOrWhiteSpace(np.PlayerUid) || np.PlayerUid.StartsWith("web_", StringComparison.Ordinal)) continue;
            aliveUids.Add(np.PlayerUid);
            var key = (profile.Id, np.PlayerUid);
            if (!_spectatorMemberCache.TryGetValue(key, out var vm))
            {
                var (file, ring) = ResolveSpectatorAvatar(np.PlayerUid);
                vm = new MemberViewModel
                {
                    PlayerUid = np.PlayerUid,
                    AvatarPath = $"pack://application:,,,/Assets/Images/{file}.png",
                    AvatarRing = ring,
                    IsSelected = false,
                    IsReadOnlyView = true,
                    IsActiveRoomMember = false
                };
                _spectatorMemberCache[key] = vm;
            }
            ApplyControlRoomPlayerToMember(vm, np, isReadOnly: true, profile.Id, profile.Name, showRoomLabel);
            result.Add(vm);
        }

        foreach (var staleKey in _spectatorMemberCache.Keys
            .Where(k => string.Equals(k.ProfileId, profile.Id, StringComparison.Ordinal) && !aliveUids.Contains(k.PlayerUid))
            .ToList())
        {
            _spectatorMemberCache.Remove(staleKey);
        }

        return result;
    }

    private (string File, string Ring) ResolveSpectatorAvatar(string playerUid)
    {
        var active = Members.FirstOrDefault(m => string.Equals(m.PlayerUid, playerUid, StringComparison.Ordinal));
        if (active is not null && !string.IsNullOrEmpty(active.AvatarPath))
        {
            var matched = AvatarPool.FirstOrDefault(a => active.AvatarPath.EndsWith($"/{a.file}.png", StringComparison.OrdinalIgnoreCase));
            if (!string.IsNullOrEmpty(matched.file)) return matched;
        }
        if (!_spectatorAvatars.TryGetValue(playerUid, out var assigned))
        {
            assigned = AvatarPool[_spectatorAvatarCursor++ % AvatarPool.Length];
            _spectatorAvatars[playerUid] = assigned;
        }
        return assigned;
    }

    private void ApplyControlRoomPlayerToMember(MemberViewModel target, ControlRoomPlayer np,
        bool isReadOnly, string sourceRoomProfileId, string roomLabel, bool showRoomLabel)
    {
        target.PlayerName = np.PlayerName;
        target.ClientInstanceId = np.ClientInstanceId;
        target.MachineId = np.MachineId;
        target.Online = np.Online;
        target.IsSelf = np.PlayerUid == _config?.PlayerUid;
        target.IsObserverMode = IsObserverMode;
        target.BgiStatus = np.BgiStatus;
        target.BgiVersion = np.BgiVersion;
        target.ConfigGroups = np.ConfigGroups;
        target.OneClickConfigs = np.OneClickConfigs;
        target.AutoHoeingRunning = np.AutoHoeingRunning;
        target.AutoHoeingProgress = np.AutoHoeingProgress;
        target.ScriptTaskProgress = np.ScriptTaskProgress;
        target.FriendshipProgress = np.FriendshipProgress;
        target.TaskRunning = np.TaskRunning;
        target.TaskStatusAvailable = np.TaskStatusAvailable;
        target.TaskPaused = np.TaskPaused;
        target.ApplyHoeingStatus(np.HoeingBatchId, np.HoeingStartPhase,
            np.HoeingStartMessage, np.HoeingCompletionWaitReleased);
        target.HoeingCompletionAction = np.HoeingCompletionAction;
        target.CurrentTaskName = np.CurrentTaskName;
        target.CurrentTaskGroupName = np.CurrentTaskGroupName;
        target.CurrentRouteDisplay = np.CurrentRouteDisplay;
        target.CurrentScriptRouteName = np.CurrentScriptRouteName;
        target.Hotkeys = np.Hotkeys;
        target.ConfigGroupTasksWithStatus = np.ConfigGroupTasksWithStatus;
        target.OneClickTasksWithStatus = np.OneClickTasksWithStatus;
        target.OnlineReady = np.OnlineReady;
        target.OnlineMode = np.OnlineMode;
        target.ScheduledOnlineTime = np.ScheduledOnlineTime;
        target.ExpectedHoeingPlayers = np.ExpectedHoeingPlayers;
        target.OnlineHoeingGroupNames = np.OnlineHoeingGroupNames ?? [];
        target.OnlineHoeingGroupTypes = np.OnlineHoeingGroupTypes ?? [];
        target.QuickCommands = np.QuickCommands ?? new();
        target.OnlineHistory = np.OnlineHistory;
        target.SourceRoomProfileId = sourceRoomProfileId;
        target.RoomLabel = roomLabel;
        target.IsActiveRoomMember = !isReadOnly;
        target.IsReadOnlyView = isReadOnly;
        target.ShowRoomLabel = showRoomLabel;
        if (isReadOnly) target.IsSelected = false;
    }

    private void ReconcileDisplayedMembers(List<MemberViewModel> desired)
    {
        var desiredSet = new HashSet<MemberViewModel>(desired, ReferenceEqualityComparer.Instance);
        for (int i = DisplayedMembers.Count - 1; i >= 0; i--)
        {
            if (!desiredSet.Contains(DisplayedMembers[i]))
            {
                DisplayedMembers.RemoveAt(i);
            }
        }

        for (int targetIndex = 0; targetIndex < desired.Count; targetIndex++)
        {
            var item = desired[targetIndex];
            if (targetIndex < DisplayedMembers.Count && ReferenceEquals(DisplayedMembers[targetIndex], item))
                continue;
            var currentIndex = DisplayedMembers.IndexOf(item);
            if (currentIndex >= 0)
            {
                DisplayedMembers.Move(currentIndex, targetIndex);
            }
            else
            {
                DisplayedMembers.Insert(targetIndex, item);
            }
        }
    }

    // ===== 非登录房间单卡片远控隔离路由 =====

    private static bool IsOtherRoomCard(MemberViewModel? member)
        => member is { IsActiveRoomMember: false };

    private string ResolveOtherRoomLabel(string profileId, string? fallback = null)
    {
        if (!string.IsNullOrWhiteSpace(fallback)) return fallback;
        var profile = _config?.RoomProfiles.FirstOrDefault(p => string.Equals(p.Id, profileId, StringComparison.Ordinal));
        return string.IsNullOrWhiteSpace(profile?.Name) ? profileId : profile.Name;
    }

    private bool TryGetOtherRoomSession(MemberViewModel member, out IRoomSpectatorSession session, out string roomLabel)
    {
        roomLabel = ResolveOtherRoomLabel(member.SourceRoomProfileId, member.RoomLabel);
        session = null!;
        if (IsStandaloneMode)
        {
            AddLog($"当前为单机模式，无法远控「{roomLabel}」中的 {member.PlayerName}");
            return false;
        }
        if (string.IsNullOrWhiteSpace(member.SourceRoomProfileId)
            || !_roomSpectators.TryGetValue(member.SourceRoomProfileId, out var found)
            || !found.IsConnected)
        {
            AddLog($"房间「{roomLabel}」连接尚未就绪，未向 {member.PlayerName} 发送命令");
            return false;
        }
        session = found;
        return true;
    }

    internal IReadOnlyList<ControlRoomPlayer> GetOtherRoomPeersSnapshot(MemberViewModel member)
    {
        if (!string.IsNullOrWhiteSpace(member.SourceRoomProfileId)
            && _roomSpectators.TryGetValue(member.SourceRoomProfileId, out var session))
        {
            return session.GetPlayersSnapshot();
        }
        return [];
    }

    internal async Task<bool> SendOtherRoomCardCommandAsync(
        MemberViewModel member,
        string cmd,
        Dictionary<string, object>? parameters = null,
        bool trackStartReceipt = false,
        DateTimeOffset? expiresAtUtc = null,
        string? customLogMessage = null)
    {
        if (!TryEnterConfigTransferOperation(cmd, out var transferOperation))
        {
            AddLog(ConfigTransferBlockedMessage);
            return false;
        }
        using var _ = transferOperation;
        if (!TryGetOtherRoomSession(member, out var session, out var roomLabel)) return false;

        var remoteCmd = new RemoteCommand
        {
            Cmd = cmd,
            RoomCode = session.RoomCode,
            Sender = _config?.PlayerName ?? "",
            SenderUid = session.SpectatorUid,
            Target = [member.PlayerUid],
            CommandId = "otherroom_" + Guid.NewGuid().ToString("N"),
            ExpiresAtUtc = expiresAtUtc,
            Params = parameters
        };

        if (trackStartReceipt && cmd is "start_group" or "start_oneclick")
        {
            TrackOtherRoomPendingCommandResult(remoteCmd, member);
        }

        bool sent;
        try
        {
            sent = await session.SendRemoteCommandAsync(remoteCmd);
        }
        catch (Exception ex)
        {
            sent = false;
            AddLog($"向「{roomLabel}」的 {member.PlayerName} 发送 {cmd} 失败：{ex.Message}");
        }

        if (!sent)
        {
            if (trackStartReceipt && _otherRoomPendingCommandResults.Remove(remoteCmd.CommandId))
            {
                if (member.RemoteStartResultState == "pending")
                {
                    member.RemoteStartResult = "发送失败（房间未连接）";
                    member.RemoteStartResultState = "failed";
                }
            }
            AddLog($"房间「{roomLabel}」连接已断开，未向 {member.PlayerName} 下发 {cmd}");
            return false;
        }

        AddLog(customLogMessage ?? $"已向「{roomLabel}」的 {member.PlayerName} 远程下发 {cmd} 命令");
        return true;
    }

    private void TrackOtherRoomPendingCommandResult(RemoteCommand cmd, MemberViewModel member)
    {
        var taskName = cmd.Params?.GetValueOrDefault(
            cmd.Cmd == "start_group" ? "groupName" : "configName")?.ToString() ?? "";
        _otherRoomPendingCommandResults[cmd.CommandId] = (member.SourceRoomProfileId, member.PlayerUid, cmd.Cmd, taskName);
        var kindText = cmd.Cmd == "start_group" ? "配置组" : "一条龙";
        member.RemoteStartResult = $"{kindText}「{taskName}」已下发，等待回执…";
        member.RemoteStartResultState = "pending";
        _ = ExpireOtherRoomPendingCommandResultAsync(cmd.CommandId);
    }

    private async Task ExpireOtherRoomPendingCommandResultAsync(string commandId)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(15));
            void Expire()
            {
                if (!_otherRoomPendingCommandResults.Remove(commandId, out var pending)) return;
                if (_spectatorMemberCache.TryGetValue((pending.ProfileId, pending.TargetUid), out var member)
                    && member.RemoteStartResultState == "pending")
                {
                    member.RemoteStartResult = "已发送（无回执）";
                    member.RemoteStartResultState = "noreceipt";
                }
            }
            var dispatcher = Application.Current?.Dispatcher;
            if (dispatcher is null || dispatcher.CheckAccess()) Expire();
            else if (!dispatcher.HasShutdownStarted) _ = dispatcher.BeginInvoke(Expire);
        }
        catch { }
    }

    private void OnSpectatorReplyReceived(string profileId, IRoomSpectatorSession session, RemoteCommand cmd)
    {
        if (_disposing) return;
        if (!_roomSpectators.TryGetValue(profileId, out var current) || !ReferenceEquals(current, session)) return;

        if (string.Equals(cmd.Cmd, "ack", StringComparison.Ordinal))
        {
            var status = cmd.Params?.GetValueOrDefault("status")?.ToString() ?? "failed";
            var message = cmd.Params?.GetValueOrDefault("message")?.ToString() ?? "";
            lock (_otherRoomResetAckGate)
            {
                if (_otherRoomPendingResetAcks.TryGetValue($"{profileId}\0{cmd.CommandId}\0{cmd.SenderUid}", out var waiter))
                {
                    waiter.TrySetResult(new CommandResult { Status = status, Message = message });
                }
            }
            var roomLabel = ResolveOtherRoomLabel(profileId);
            InvokeOnUiThread(() => AddLog($"确认 [{roomLabel}]: {cmd.Sender} - {message}"));
            return;
        }

        if (cmd.Cmd is "remote_config.data" or "remote_config.push_result")
        {
            _remoteConfigEditService?.TryComplete(cmd.CommandId, cmd);
            return;
        }

        if (cmd.Cmd is "task_policy.data" or "task_policy.push_result")
        {
            List<TaskPolicySyncService> snapshot;
            lock (_otherRoomPolicySyncGate) snapshot = [.. _otherRoomPolicySyncs];
            foreach (var sync in snapshot)
            {
                if (sync.TryComplete(cmd.CommandId, cmd)) return;
            }
        }
    }

    private void OnSpectatorCommandResultReceived(string profileId, IRoomSpectatorSession session, RemoteCommandResult result)
    {
        if (_disposing) return;
        InvokeOnUiThread(() =>
        {
            if (_disposing) return;
            if (!_roomSpectators.TryGetValue(profileId, out var current) || !ReferenceEquals(current, session)) return;
            var roomLabel = ResolveOtherRoomLabel(profileId);
            var displayName = string.IsNullOrEmpty(result.TargetName) ? result.TargetUid : result.TargetName;
            var statusText = result.Status switch
            {
                "success" => $"启动成功：{result.TaskName}",
                "cancelled" => $"执行被取消：{result.TaskName}",
                _ => $"启动失败：{(string.IsNullOrEmpty(result.Message) ? result.Status : result.Message)}"
            };
            AddLog($"[回执][{roomLabel}] {displayName} {result.Cmd}「{result.TaskName}」→ {statusText}");
            _otherRoomPendingCommandResults.Remove(result.CommandId);

            if (_spectatorMemberCache.TryGetValue((profileId, result.TargetUid), out var member))
            {
                member.RemoteStartResult = statusText;
                member.RemoteStartResultState = result.Status == "success" ? "success" : "failed";
            }
        });
    }

    private void InvokeOnUiThread(Action action)
    {
        var dispatcher = Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action();
        else if (!dispatcher.HasShutdownStarted) dispatcher.BeginInvoke(action);
    }

    private (TaskPolicySyncService Service, IDisposable Registration) EnterOtherRoomTaskPolicySync(
        IRoomSpectatorSession session)
    {
        var sync = new TaskPolicySyncService(
            async command =>
            {
                if (!session.IsConnected) return false;
                command.RoomCode = session.RoomCode;
                command.SenderUid = session.SpectatorUid;
                return await session.SendRemoteCommandAsync(command);
            },
            () => session.SpectatorUid,
            () => _config?.PlayerName ?? "");
        lock (_otherRoomPolicySyncGate) _otherRoomPolicySyncs.Add(sync);
        return (sync, new ActionDisposable(() =>
        {
            lock (_otherRoomPolicySyncGate) _otherRoomPolicySyncs.Remove(sync);
        }));
    }

    internal async Task<bool> ClearOtherRoomOnlineHistoryAsync(MemberViewModel member)
    {
        if (!TryGetOtherRoomSession(member, out var session, out var roomLabel)) return false;
        try
        {
            var ok = await session.ClearOnlineHistoryAsync(member.PlayerUid);
            if (ok) AddLog($"已清除「{roomLabel}」中 {member.PlayerName} 的联机记录");
            else AddLog($"清除「{roomLabel}」中 {member.PlayerName} 的联机记录失败：房间未连接");
            return ok;
        }
        catch (Exception ex)
        {
            AddLog($"清除「{roomLabel}」中 {member.PlayerName} 的联机记录失败：{ex.Message}");
            return false;
        }
    }

    internal async Task<bool> ResetOtherRoomCardStateAsync(MemberViewModel member)
    {
        if (!TryGetOtherRoomSession(member, out var session, out var roomLabel)) return false;

        var resetId = "reset_" + Guid.NewGuid().ToString("N");
        var expectedGeneration = 0;
        var expectedBatchId = member.HoeingBatchId ?? "";
        var serverResetConfirmed = false;

        try
        {
            var current = await session.GetCurrentCoordinatedBatchAsync();
            if (current != null) expectedBatchId = current.BatchId;
            var reset = await session.ResetCoordinatedBatchAsync(
                resetId, [member.PlayerUid],
                expectedGeneration: current?.Generation,
                expectedBatchId: current?.BatchId);
            if (reset != null)
            {
                if (reset.Reason == "stale_reset")
                {
                    AddLog($"[状态重置][{roomLabel}] 目标房间批次已改变，旧重置请求未执行；目标任务保持运行");
                    return false;
                }
                serverResetConfirmed = true;
                expectedGeneration = reset.Generation;
                expectedBatchId = reset.BatchId;
                AddLog($"[状态重置][{roomLabel}] 服务端已处理单人重置 resetId={resetId}，批次={reset.BatchId}");
            }
        }
        catch (Exception ex)
        {
            AddLog($"[状态重置][{roomLabel}] 服务端批次失效未确认，将继续发送单人复位命令：{ex.Message}");
        }

        var ackKey = $"{member.SourceRoomProfileId}\0{resetId}\0{member.PlayerUid}";
        var waiter = new TaskCompletionSource<CommandResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_otherRoomResetAckGate) _otherRoomPendingResetAcks[ackKey] = waiter;
        try
        {
            var parameters = new Dictionary<string, object> { ["resetId"] = resetId };
            if (expectedGeneration > 0) parameters["generation"] = expectedGeneration;
            if (!string.IsNullOrWhiteSpace(expectedBatchId)) parameters["batchId"] = expectedBatchId;

            var sent = await session.SendRemoteCommandAsync(new RemoteCommand
            {
                Cmd = "reset_state",
                RoomCode = session.RoomCode,
                Sender = _config?.PlayerName ?? "",
                SenderUid = session.SpectatorUid,
                Target = [member.PlayerUid],
                CommandId = resetId,
                Params = parameters
            });
            if (!sent)
            {
                AddLog($"[状态重置][{roomLabel}] 房间连接已断开，未发送单人复位命令");
                return false;
            }

            var completed = await Task.WhenAny(waiter.Task, Task.Delay(TimeSpan.FromSeconds(18)));
            if (completed == waiter.Task && waiter.Task.IsCompletedSuccessfully && waiter.Task.Result.Status == "success")
            {
                AddLog($"[状态重置][{roomLabel}] {member.PlayerName} 已完成单人复位");
                return true;
            }
            var detail = completed == waiter.Task && waiter.Task.IsCompletedSuccessfully
                ? waiter.Task.Result.Message
                : "等待恢复结果超时，未冒认恢复成功";
            AddLog($"[状态重置][{roomLabel}] {member.PlayerName} 未确认复位（服务端={(serverResetConfirmed ? "已处理" : "未确认")}）：{detail}");
            return false;
        }
        finally
        {
            lock (_otherRoomResetAckGate) _otherRoomPendingResetAcks.Remove(ackKey);
        }
    }

    internal async Task RemoteConfigEditOtherRoomCardAsync(MemberViewModel member)
    {
        if (!TryGetOtherRoomSession(member, out var session, out var roomLabel)) return;
        if (!member.Online)
        {
            AddLog($"配置编辑：「{roomLabel}」中的 {member.PlayerName} 当前离线，无法远程编辑配置");
            return;
        }
        var groups = member.ConfigGroups ?? [];
        if (groups.Count == 0)
        {
            AddLog($"配置编辑：「{roomLabel}」中的 {member.PlayerName} 没有可用的配置组（可能状态尚未同步）");
            return;
        }

        try
        {
            _remoteConfigEditService ??= new RemoteConfigEditService(
                SendConfigViaServerAsync, () => _config?.PlayerUid ?? "", () => _config?.PlayerName ?? "",
                AddLog, () => _externalClient, EnsureLocalBgiReadyAsync);

            AddLog($"配置编辑：正在通过「{roomLabel}」远控通道编辑 {member.PlayerName} 的配置组");
            var groupName = RemoteConfigGroupSelectWindow.ShowSelectDialog(groups, member.PlayerName, Application.Current.MainWindow);
            if (string.IsNullOrEmpty(groupName)) return;

            var profileId = member.SourceRoomProfileId;
            await _remoteConfigEditService.RunAsync(member.PlayerUid, member.PlayerName, groupName, async command =>
            {
                if (!_roomSpectators.TryGetValue(profileId, out var current) || !ReferenceEquals(current, session) || !session.IsConnected)
                {
                    AddLog($"配置编辑：「{roomLabel}」连接已断开，请重新发起编辑");
                    return false;
                }
                command.RoomCode = session.RoomCode;
                command.SenderUid = session.SpectatorUid;
                return await session.SendRemoteCommandAsync(command);
            });
        }
        catch (Exception ex)
        {
            AddLog($"配置编辑失败（{roomLabel} / {member.PlayerName}）：{ex.Message}");
        }
    }

    private sealed class ActionDisposable(Action dispose) : IDisposable
    {
        private Action? _dispose = dispose;
        public void Dispose() => Interlocked.Exchange(ref _dispose, null)?.Invoke();
    }
}
